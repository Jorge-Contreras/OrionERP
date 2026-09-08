using System.Data.Common;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using OrionERP.Application.Common;
using OrionERP.Application.Features.Reservaciones;
using OrionERP.Application.Features.Restaurante;
using OrionERP.Infrastructure.Features.Logistica.Locations;
using OrionERP.Infrastructure.Features.Reservaciones;
using OrionERP.Infrastructure.Features.Restaurante;
using CompanyConnectionFactory = OrionERP.Infrastructure.Features.Cfdi.DescargaMasiva.Dapper.SqlConnectionFactory;

namespace OrionERP.IntegrationTests.Reservaciones;

public sealed class HospitalityRestaurantProductionScopeTests
{
  [Theory, Trait("Category", "SqlIntegration")]
  [InlineData(false)]
  [InlineData(true)]
  public async Task Production_ProtectsSourcesWholeOrderAndIdempotency_AndCompletesAuthorizedOrder(bool trackLots)
  {
    if (Environment.GetEnvironmentVariable("ORION_RUN_SQL_INTEGRATION") != "1") return;
    await using var f = await Fixture.CreateAsync(trackLots);
    var authorized = f.Production(f.Scope);
    var plan = f.Plan(f.GeneralLocation, "private-input");
    // A higher-priority private source must never supply another hospitality site.
    Assert.True((await f.Catalog(f.Scope).SaveSiteOperationsAsync(f.Operations(f.PrivateLocation, f.GeneralLocation))).Success);
    foreach (var scope in f.DeniedScopes)
    {
      var service = f.Production(scope);
      var workspace = await service.GetWorkspaceAsync(f.Rfc, f.RestaurantSite);
      Assert.DoesNotContain(workspace.OutputLocations, x => x.Id == f.PrivateLocation);
      Assert.Contains(workspace.OutputLocations, x => x.Id == f.GeneralLocation);
      Assert.Equal(51932, (await Assert.ThrowsAsync<SqlException>(() => service.PlanAsync(f.Plan(f.PrivateLocation, "denied-output"), "scope-test"))).Number);
      var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.PlanAsync(plan, "scope-test"));
      Assert.Contains("Inventario insuficiente", error.Message);
      Assert.Equal(0, await f.Sql.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM logistica.InventoryReservation WHERE SiteId=@SiteId", new { SiteId = f.RestaurantSite }));
      Assert.Equal(0m, await f.ReservedAsync());
    }

    // General inventory alone is insufficient: the authorized order has both sources.
    Assert.True((await authorized.PlanAsync(plan, "scope-test")).Success);
    var order = Assert.Single((await authorized.GetWorkspaceAsync(f.Rfc, f.RestaurantSite)).Orders);
    Assert.Equal(new[] { f.PrivateLocation, f.GeneralLocation },
      (await f.Sql.QueryAsync<int>("SELECT LocationId FROM logistica.InventoryReservationLine WHERE ReservationId=(SELECT ReservationId FROM logistica.ProductionOrder WHERE Id=@Id) ORDER BY Id", new { order.Id })).ToArray());
    Assert.Equal(4m, await f.ReservedAsync());
    Assert.True((await authorized.PlanAsync(plan, "scope-test")).Success);
    Assert.Equal(4m, await f.ReservedAsync());

    var completion = new RestaurantProductionCompleteRequest { Rfc = f.Rfc, ProductionOrderId = order.Id, ActualQuantity = 4, OutputLotCode = f.Marker + "-out" };
    foreach (var scope in f.DeniedScopes)
    {
      var service = f.Production(scope);
      Assert.Empty((await service.GetWorkspaceAsync(f.Rfc, f.RestaurantSite)).Orders);
      Assert.False((await service.PlanAsync(plan, "scope-test")).Success);
      Assert.False((await service.StartAsync(f.Rfc, order.Id, "scope-test")).Success);
      Assert.False((await service.CancelAsync(f.Rfc, order.Id, "scope-test")).Success);
      Assert.False((await service.CompleteAsync(completion, "scope-test")).Success);
    }
    Assert.Equal("Planned", await f.StatusAsync(order.Id));
    Assert.Equal(4m, await f.ReservedAsync());
    Assert.True((await authorized.StartAsync(f.Rfc, order.Id, "scope-test")).Success);
    Assert.True((await authorized.StartAsync(f.Rfc, order.Id, "scope-test")).Success);
    Assert.Equal(0m, await f.ReservedAsync());
    Assert.Equal(2m, await f.Sql.ExecuteScalarAsync<decimal>("SELECT SUM(Quantity) FROM logistica.StockBalance WHERE MaterialId=@Id", new { Id = f.InputMaterial }));
    Assert.True((await authorized.CompleteAsync(completion, "scope-test")).Success);
    Assert.True((await authorized.CompleteAsync(completion, "scope-test")).Success);
    Assert.Equal("Completed", await f.StatusAsync(order.Id));
    Assert.Equal(4m, await f.Sql.ExecuteScalarAsync<decimal>("SELECT Quantity FROM logistica.StockBalance WHERE MaterialId=@Id AND LocationId=@Location", new { Id = f.OutputMaterial, Location = f.GeneralLocation }));
    Assert.Equal(3, await f.Sql.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM restaurante.EventOutbox WHERE SiteId=@Id", new { Id = f.RestaurantSite }));
    Assert.Equal(3, await f.Sql.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM logistica.StockTransaction WHERE MaterialId IN @Ids", new { Ids = new[] { f.InputMaterial, f.OutputMaterial } }));
    if (trackLots)
      Assert.Equal(2m, await f.Sql.ExecuteScalarAsync<decimal>("SELECT SUM(Quantity) FROM logistica.LotBalance WHERE MaterialId=@Id", new { Id = f.InputMaterial }));
    foreach (var scope in f.DeniedScopes)
      Assert.Empty((await f.Production(scope).GetWorkspaceAsync(f.Rfc, f.RestaurantSite)).Orders);
  }

  [Theory, Trait("Category", "SqlIntegration")]
  [InlineData(false)]
  [InlineData(true)]
  public async Task Production_GeneralSourcesRemainUsable_AndPrivateDestinationBlocksWholeOrder(bool trackLots)
  {
    if (Environment.GetEnvironmentVariable("ORION_RUN_SQL_INTEGRATION") != "1") return;
    await using var f = await Fixture.CreateAsync(trackLots);
    var authorized = f.Production(f.Scope);
    Assert.True((await f.Catalog(f.Scope).SaveSiteOperationsAsync(f.Operations(f.GeneralLocation, f.PrivateLocation))).Success);
    var plan = f.Plan(f.PrivateLocation, "private-output");
    plan.PlannedQuantity = 1;
    Assert.True((await authorized.PlanAsync(plan, "scope-test")).Success);
    var order = Assert.Single((await authorized.GetWorkspaceAsync(f.Rfc, f.RestaurantSite)).Orders);
    Assert.Equal(f.GeneralLocation, await f.Sql.ExecuteScalarAsync<int>("SELECT LocationId FROM logistica.InventoryReservationLine WHERE ReservationId=(SELECT ReservationId FROM logistica.ProductionOrder WHERE Id=@Id)", new { order.Id }));
    foreach (var scope in f.DeniedScopes)
    {
      var denied = f.Production(scope);
      Assert.Empty((await denied.GetWorkspaceAsync(f.Rfc, f.RestaurantSite)).Orders);
      Assert.False((await denied.StartAsync(f.Rfc, order.Id, "scope-test")).Success);
      Assert.False((await denied.CancelAsync(f.Rfc, order.Id, "scope-test")).Success);
      plan.OutputLocationId = f.GeneralLocation;
      Assert.False((await denied.PlanAsync(plan, "scope-test")).Success);
    }
    Assert.True((await authorized.CancelAsync(f.Rfc, order.Id, "scope-test")).Success);
    Assert.Equal("Cancelled", await f.StatusAsync(order.Id));
    Assert.Equal(0m, await f.ReservedAsync());
    // No hospitality permission is needed to produce exclusively from general stock.
    var general = f.Production(null);
    var generalPlan = f.Plan(f.GeneralLocation, "general");
    generalPlan.PlannedQuantity = 1;
    Assert.True((await general.PlanAsync(generalPlan, "scope-test")).Success);
    var generalOrder = Assert.Single((await general.GetWorkspaceAsync(f.Rfc, f.RestaurantSite)).Orders);
    Assert.True((await general.StartAsync(f.Rfc, generalOrder.Id, "scope-test")).Success);
    Assert.True((await general.CompleteAsync(new() { Rfc = f.Rfc, ProductionOrderId = generalOrder.Id, ActualQuantity = 1, OutputLotCode = f.Marker + "-general" }, "scope-test")).Success);
    Assert.Equal("Completed", await f.StatusAsync(generalOrder.Id));
    Assert.Equal(3m, await f.Sql.ExecuteScalarAsync<decimal>("SELECT Quantity FROM logistica.StockBalance WHERE MaterialId=@Id AND LocationId=@Location", new { Id = f.InputMaterial, Location = f.PrivateLocation }));
  }

  [Fact, Trait("Category", "SqlIntegration")]
  public async Task SiteOperations_RejectsHiddenNewAndOriginalPrioritiesWithoutPartialChanges()
  {
    if (Environment.GetEnvironmentVariable("ORION_RUN_SQL_INTEGRATION") != "1") return;
    await using var f = await Fixture.CreateAsync(false);
    var catalog = f.Catalog(f.Scope);
    Assert.True((await catalog.SaveSiteOperationsAsync(f.Operations(f.GeneralLocation))).Success);
    foreach (var scope in f.DeniedScopes)
    {
      var denied = f.Catalog(scope);
      var visible = await denied.GetSiteOperationsAsync(f.Rfc, f.RestaurantSite);
      Assert.Equal(f.GeneralLocation, Assert.Single(visible.LocationPriorities).LocationId);
      Assert.DoesNotContain(visible.AvailableLocations, x => x.Id == f.PrivateLocation);
      var request = f.Operations(f.PrivateLocation);
      request.OperationalDayCutoff = TimeSpan.FromHours(7);
      Assert.Equal(51932, (await Assert.ThrowsAsync<SqlException>(() => denied.SaveSiteOperationsAsync(request))).Number);
      Assert.Equal(f.GeneralLocation, Assert.Single((await catalog.GetSiteOperationsAsync(f.Rfc, f.RestaurantSite)).LocationPriorities).LocationId);
    }
    Assert.True((await catalog.SaveSiteOperationsAsync(f.Operations(f.PrivateLocation, f.GeneralLocation))).Success);
    foreach (var scope in f.DeniedScopes)
    {
      var denied = f.Catalog(scope);
      Assert.Equal(f.GeneralLocation, Assert.Single((await denied.GetSiteOperationsAsync(f.Rfc, f.RestaurantSite)).LocationPriorities).LocationId);
      // A save of only the visible projection must not erase the hidden original row.
      var request = f.Operations(f.GeneralLocation);
      request.OperationalDayCutoff = TimeSpan.FromHours(7);
      Assert.Equal(51932, (await Assert.ThrowsAsync<SqlException>(() => denied.SaveSiteOperationsAsync(request))).Number);
    }
    Assert.Equal(2, (await catalog.GetSiteOperationsAsync(f.Rfc, f.RestaurantSite)).LocationPriorities.Count);
    Assert.Equal(TimeSpan.FromHours(4), await f.Sql.ExecuteScalarAsync<TimeSpan>("SELECT OperationalDayCutoff FROM restaurante.Site WHERE Id=@Id", new { Id = f.RestaurantSite }));
    Assert.True((await catalog.SaveSiteOperationsAsync(f.Operations(f.GeneralLocation))).Success);
    Assert.True((await f.Catalog(null).SaveSiteOperationsAsync(f.Operations(f.GeneralLocation))).Success);

    var missing = f.Factory(null);
    Assert.Equal(51930, (await Assert.ThrowsAsync<SqlException>(() => new RestaurantProductionService(missing, null, f.ModuleScope).GetWorkspaceAsync(f.Rfc, f.RestaurantSite))).Number);
    Assert.Equal(51930, (await Assert.ThrowsAsync<SqlException>(() => new RestaurantProductionService(missing, null, f.ModuleScope).PlanAsync(f.Plan(f.GeneralLocation, "missing"), "scope-test"))).Number);
    Assert.Equal(51930, (await Assert.ThrowsAsync<SqlException>(() => new RestaurantCatalogService(missing, null, f.ModuleScope).GetSiteOperationsAsync(f.Rfc, f.RestaurantSite))).Number);
    Assert.Equal(51930, (await Assert.ThrowsAsync<SqlException>(() => new RestaurantCatalogService(missing, null, f.ModuleScope).SaveSiteOperationsAsync(f.Operations(f.GeneralLocation)))).Number);
    // Sin accessor de módulo la operación se niega; la ausencia nunca exime.
    await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new RestaurantCatalogService(missing).GetSiteOperationsAsync(f.Rfc, f.RestaurantSite));
    Assert.Equal(51935, (await Assert.ThrowsAsync<SqlException>(() => catalog.GetSiteOperationsAsync("BRUNOS260707L26", f.RestaurantSite))).Number);
    Assert.Equal(51935, (await Assert.ThrowsAsync<SqlException>(() => f.Production(null).GetWorkspaceAsync("BRUNOS260707L26", f.RestaurantSite))).Number);
  }

  private sealed class Fixture : IAsyncDisposable
  {
    public SqlConnection Sql { get; }
    private readonly IConfiguration _configuration;
    public HospitalityScope Scope { get; private set; } = null!;
    public string Rfc => Scope.CompanyRfc;
    public string Marker { get; } = "prls" + Guid.NewGuid().ToString("N")[..12];
    private long _otherSite, _bomHeader, _bomVersion;
    private int _room;
    public int RestaurantSite { get; private set; }
    public int PrivateLocation { get; private set; }
    public int GeneralLocation { get; private set; }
    public int InputMaterial { get; private set; }
    public int OutputMaterial { get; private set; }
    public IEnumerable<HospitalityScope?> DeniedScopes => new HospitalityScope?[] { null, Scope with { SiteId = _otherSite } };

    private Fixture(string cs)
    {
      Sql = new SqlConnection(cs);
      _configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:OrionDb"] = cs }).Build();
    }

    public static async Task<Fixture> CreateAsync(bool trackLots)
    {
      var builder = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable("ASPNETCORE_ConnectionStrings__OrionDb") ?? throw new InvalidOperationException("Missing Sandbox connection")) { InitialCatalog = "Orion_Sandbox" };
      Assert.Equal("Orion_Sandbox", builder.InitialCatalog, ignoreCase: true);
      var f = new Fixture(builder.ConnectionString);
      try
      {
        await f.Sql.OpenAsync();
        Assert.Equal("Orion_Sandbox", await f.Sql.ExecuteScalarAsync<string>("SELECT DB_NAME()"), ignoreCase: true);
        f.Scope = await f.Sql.QuerySingleAsync<HospitalityScope>("SELECT p.CompanyId,p.SiteId,c.Rfc AS CompanyRfc FROM orion.PublicSite p JOIN orion.Company c ON c.CompanyId=p.CompanyId WHERE p.PublicSiteKey='bonhomia-main'");
        await HospitalityConnectionFactory.InitializeAsync(f.Sql, f.Scope);
        await f.Sql.ExecuteAsync("EXEC sys.sp_set_session_context @key=N'OrionRfc',@value=@Rfc", new { f.Rfc });
        f._otherSite = await f.Sql.ExecuteScalarAsync<long>("INSERT orion.Site(CompanyId,SiteKey,DisplayName,TimeZoneId) VALUES(@CompanyId,@Marker,@Marker,'America/Mexico_City'); SELECT CONVERT(bigint,SCOPE_IDENTITY());", new { f.Scope.CompanyId, f.Marker });
        f._room = await f.Sql.ExecuteScalarAsync<int>("INSERT dbo.ROOM(ROOM_NAME,ROOM_TYPE) VALUES(@Marker,'SUITE'); SELECT CONVERT(int,SCOPE_IDENTITY());", new { f.Marker });
        var locations = new LocationService(f.Factory(f.Rfc), new FixedScope(f.Scope));
        var privateLocation = await locations.SaveLocationAsync(new() { LocationName = f.Marker + "-private", RoomId = f._room });
        Assert.True(privateLocation.Success, privateLocation.Message);
        f.PrivateLocation = privateLocation.EntityId!.Value;
        var generalLocation = await locations.SaveLocationAsync(new() { LocationName = f.Marker + "-general" });
        Assert.True(generalLocation.Success, generalLocation.Message);
        f.GeneralLocation = generalLocation.EntityId!.Value;
        f.RestaurantSite = await f.Sql.ExecuteScalarAsync<int>("INSERT restaurante.Site(Rfc,SiteCode,[Name],IsEnabled) VALUES(@Rfc,@Marker,@Marker,1); SELECT CONVERT(int,SCOPE_IDENTITY());", new { f.Rfc, f.Marker });
        var unit = await f.Sql.ExecuteScalarAsync<int>("SELECT MIN(Id) FROM logistica.UnitOfMeasure");
        f.InputMaterial = await f.Sql.ExecuteScalarAsync<int>("INSERT logistica.Material(MaterialCode,[Description],BaseUnitId,TrackLots) VALUES(@Code,@Code,@Unit,@TrackLots); SELECT CONVERT(int,SCOPE_IDENTITY());", new { Code = f.Marker + "-in", Unit = unit, TrackLots = trackLots });
        f.OutputMaterial = await f.Sql.ExecuteScalarAsync<int>("INSERT logistica.Material(MaterialCode,[Description],BaseUnitId,ProductType,FulfillmentMode,TrackLots) VALUES(@Code,@Code,@Unit,'FinishedGood','MakeToStock',1); SELECT CONVERT(int,SCOPE_IDENTITY());", new { Code = f.Marker + "-out", Unit = unit });
        f._bomHeader = await f.Sql.ExecuteScalarAsync<long>("INSERT logistica.BomHeader(Rfc,ProductMaterialId,BomCode,[Name]) VALUES(@Rfc,@Material,@Marker,@Marker); SELECT CONVERT(bigint,SCOPE_IDENTITY());", new { f.Rfc, Material = f.OutputMaterial, f.Marker });
        f._bomVersion = await f.Sql.ExecuteScalarAsync<long>("INSERT logistica.BomVersion(Rfc,BomHeaderId,VersionNumber,[Status],YieldQuantity,YieldUnitId,FrozenTheoreticalCost) VALUES(@Rfc,@Header,1,'Active',1,@Unit,2); SELECT CONVERT(bigint,SCOPE_IDENTITY());", new { f.Rfc, Header = f._bomHeader, Unit = unit });
        await f.Sql.ExecuteAsync("INSERT logistica.BomComponent(Rfc,BomVersionId,ComponentMaterialId,Quantity,UnitId) VALUES(@Rfc,@Version,@Material,1,@Unit)", new { f.Rfc, Version = f._bomVersion, Material = f.InputMaterial, Unit = unit });
        foreach (var location in new[] { f.PrivateLocation, f.GeneralLocation })
        {
          await f.Sql.ExecuteAsync("INSERT logistica.StockBalance(LocationId,MaterialId,Quantity,ReservedQuantity,AverageUnitCost) VALUES(@Location,@Material,3,0,2)", new { Location = location, Material = f.InputMaterial });
          if (trackLots)
            await f.Sql.ExecuteAsync("""
              INSERT logistica.MaterialLot(Rfc,MaterialId,LotCode,UnitCost,SourceType) VALUES(@Rfc,@Material,@Code,2,'Purchase');
              INSERT logistica.LotBalance(Rfc,MaterialLotId,MaterialId,LocationId,Quantity,ReservedQuantity) VALUES(@Rfc,CONVERT(bigint,SCOPE_IDENTITY()),@Material,@Location,3,0);
              """, new { f.Rfc, Material = f.InputMaterial, Code = f.Marker + "-" + location, Location = location });
        }
        return f;
      }
      catch { await f.DisposeAsync(); throw; }
    }

    public CompanyConnectionFactory Factory(string? rfc) => new(_configuration, new CompanyRfc(rfc));
    // La sede sintética de la fixture no tiene contraparte en orion.Site, así que el
    // módulo se da por habilitado: lo que estas pruebas delimitan es Logística.
    public IRestaurantScopeAccessor ModuleScope
      => new FixedRestaurantScope(new RestaurantScope(Scope.CompanyId, Scope.SiteId, RestaurantSite, Rfc));
    public RestaurantProductionService Production(HospitalityScope? scope) => new(Factory(Rfc), scope is null ? null : new FixedScope(scope), ModuleScope);
    public RestaurantCatalogService Catalog(HospitalityScope? scope) => new(Factory(Rfc), scope is null ? null : new FixedScope(scope), ModuleScope);
    public RestaurantProductionPlanRequest Plan(int output, string key) => new() { Rfc = Rfc, SiteId = RestaurantSite, BomVersionId = _bomVersion, PlannedQuantity = 4, OutputLocationId = output, IdempotencyKey = Marker + key };
    public RestaurantSiteOperationsSaveRequest Operations(params int[] locations) => new() { Rfc = Rfc, SiteId = RestaurantSite, LocationPriorities = locations.Select((id, index) => new RestaurantLocationPrioritySaveRequest { LocationId = id, Priority = index + 1 }).ToList() };
    public Task<decimal> ReservedAsync() => Sql.ExecuteScalarAsync<decimal>("SELECT SUM(ReservedQuantity) FROM logistica.StockBalance WHERE MaterialId=@Id", new { Id = InputMaterial });
    public Task<string?> StatusAsync(Guid id) => Sql.ExecuteScalarAsync<string?>("SELECT [Status] FROM logistica.ProductionOrder WHERE Id=@Id", new { Id = id });

    public async ValueTask DisposeAsync()
    {
      try
      {
        if (Scope is null) return;
        // Every deletion is limited to IDs created by this fixture; no historical data is changed.
        await Sql.ExecuteAsync("""
          DELETE restaurante.EventOutbox WHERE SiteId=@RestaurantSite;
          DELETE logistica.StockTransaction WHERE MaterialId IN @Materials;
          DELETE logistica.ProductionOrder WHERE SiteId=@RestaurantSite;
          DELETE line FROM logistica.InventoryReservationLine line JOIN logistica.InventoryReservation reservation ON reservation.Id=line.ReservationId WHERE reservation.SiteId=@RestaurantSite;
          DELETE logistica.InventoryReservation WHERE SiteId=@RestaurantSite;
          DELETE logistica.LotBalance WHERE MaterialId IN @Materials;
          DELETE logistica.MaterialLot WHERE MaterialId IN @Materials;
          DELETE logistica.StockBalance WHERE MaterialId IN @Materials;
          DELETE logistica.BomComponent WHERE BomVersionId=@Version;
          DELETE logistica.BomVersion WHERE Id=@Version;
          DELETE logistica.BomHeader WHERE Id=@Header;
          DELETE logistica.Material WHERE Id IN @Materials;
          DELETE restaurante.SiteLocationPriority WHERE SiteId=@RestaurantSite;
          DELETE restaurante.AccountingConfiguration WHERE SiteId=@RestaurantSite;
          DELETE restaurante.Site WHERE Id=@RestaurantSite AND [Name]=@Marker;
          DELETE logistica.Location WHERE Id IN @Locations;
          DELETE dbo.ROOM WHERE ID=@Room AND ROOM_NAME=@Marker;
          DELETE orion.Site WHERE SiteId=@OtherSite AND SiteKey=@Marker;
          """, new { RestaurantSite, Materials = new[] { InputMaterial, OutputMaterial }, Version = _bomVersion, Header = _bomHeader, Marker, Locations = new[] { PrivateLocation, GeneralLocation }, Room = _room, OtherSite = _otherSite });
      }
      finally { await Sql.DisposeAsync(); }
    }
  }

  private sealed class CompanyRfc(string? rfc) : ICurrentRfcAccessor { public string? CurrentRfc => rfc; }
  private sealed class FixedScope(HospitalityScope scope) : IHospitalityScopeAccessor
  { public Task<HospitalityScope> ResolveRequiredAsync(CancellationToken ct = default) => Task.FromResult(scope); }

  private sealed class FixedRestaurantScope(RestaurantScope granted) : IRestaurantScopeAccessor
  {
    public Task<RestaurantScope> ResolveRequiredAsync(string rfc, int legacySiteId, CancellationToken ct = default)
      => Task.FromResult(granted with { LegacySiteId = legacySiteId });
    public Task EnsureStillEnabledAsync(DbConnection connection, DbTransaction? transaction, RestaurantScope scope, CancellationToken ct = default)
      => Task.CompletedTask;
    public Task<IReadOnlySet<string>> GetEnabledCompanyRfcsAsync(IReadOnlyCollection<string> rfcs, CancellationToken ct = default)
      => Task.FromResult<IReadOnlySet<string>>(rfcs.ToHashSet(StringComparer.OrdinalIgnoreCase));
  }
}
