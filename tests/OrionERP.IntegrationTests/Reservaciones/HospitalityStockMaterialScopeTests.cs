using Dapper;
using System.Reflection;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using OrionERP.Application.Common;
using OrionERP.Application.Features.Reservaciones;
using OrionERP.Application.Features.Restaurante;
using OrionERP.Application.Features.Contabilidad.ContabilidadRegistros;
using OrionERP.Infrastructure.Features.Logistica.Locations;
using OrionERP.Infrastructure.Features.Logistica.Materials;
using OrionERP.Infrastructure.Features.Logistica.Stock;
using OrionERP.Infrastructure.Features.Reservaciones;
using OrionERP.Infrastructure.Features.Restaurante;
using CompanyConnectionFactory = OrionERP.Infrastructure.Features.Cfdi.DescargaMasiva.Dapper.SqlConnectionFactory;

namespace OrionERP.IntegrationTests.Reservaciones;

public sealed class HospitalityStockMaterialScopeTests
{
  [Fact, Trait("Category", "SqlIntegration")]
  public async Task StockAndMaterial_HideQuantitiesHistoryAndBytesAcrossSites_AndPreserveGeneralInventory()
  {
    if (Environment.GetEnvironmentVariable("ORION_RUN_SQL_INTEGRATION") != "1") return;
    var cs = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable("ASPNETCORE_ConnectionStrings__OrionDb")
      ?? throw new InvalidOperationException("Missing Sandbox connection")) { InitialCatalog = "Orion_Sandbox" }.ConnectionString;
    var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:OrionDb"] = cs }).Build();
    await using var setup = new SqlConnection(cs);
    await setup.OpenAsync();
    Assert.Equal("Orion_Sandbox", await setup.ExecuteScalarAsync<string>("SELECT DB_NAME()"), ignoreCase: true);
    var scope = await setup.QuerySingleAsync<HospitalityScope>("SELECT p.CompanyId,p.SiteId,c.Rfc AS CompanyRfc FROM orion.PublicSite p JOIN orion.Company c ON c.CompanyId=p.CompanyId WHERE p.PublicSiteKey='bonhomia-main'");
    var other = await setup.QuerySingleAsync<HospitalityScope>("SELECT p.CompanyId,p.SiteId,c.Rfc AS CompanyRfc FROM orion.PublicSite p JOIN orion.Company c ON c.CompanyId=p.CompanyId WHERE p.PublicSiteKey='synthetic-hospitality-main'");
    await HospitalityConnectionFactory.InitializeAsync(setup, scope);
    await setup.ExecuteAsync("EXEC sys.sp_set_session_context @key=N'OrionRfc',@value=@Rfc", new { Rfc = scope.CompanyRfc });
    var factory = new CompanyConnectionFactory(cfg, new CompanyRfc(scope.CompanyRfc));
    var otherFactory = new CompanyConnectionFactory(cfg, new CompanyRfc(other.CompanyRfc));
    var scoped = new FixedScope(scope);
    var stockA = new StockService(factory, scoped);
    var materialA = new MaterialService(factory, hospitalityScope: scoped);
    var locations = new LocationService(factory, scoped);
    var marker = "smrls" + Guid.NewGuid().ToString("N")[..12];
    var ids = new List<int>();
    long siteB = 0;
    int room = 0, material = 0, restaurantSite = 0;
    try
    {
      siteB = await setup.ExecuteScalarAsync<long>("INSERT orion.Site(CompanyId,SiteKey,DisplayName,TimeZoneId) VALUES(@CompanyId,@Marker,@Marker,'America/Mexico_City'); SELECT CONVERT(bigint,SCOPE_IDENTITY());", new { scope.CompanyId, Marker = marker });
      await setup.ExecuteAsync("INSERT orion.SiteCapability(CompanyId,SiteId,ModuleCode,IsEnabled,UpdatedBy) VALUES(@CompanyId,@SiteId,'HOSPITALITY',1,N'SqlIntegration');",new { scope.CompanyId,SiteId=siteB });
      room = await setup.ExecuteScalarAsync<int>("INSERT dbo.ROOM(ROOM_NAME,ROOM_TYPE) VALUES(@Marker,'SUITE'); SELECT CONVERT(int,SCOPE_IDENTITY());", new { Marker = marker });
      var root = await locations.SaveLocationAsync(new() { LocationName = marker + "-room", RoomId = room });
      Assert.True(root.Success, root.Message); ids.Add(root.EntityId!.Value);
      var child = await locations.SaveLocationAsync(new() { LocationName = marker + "-child", ParentLocationId = root.EntityId });
      Assert.True(child.Success, child.Message); ids.Add(child.EntityId!.Value);
      var general = await locations.SaveLocationAsync(new() { LocationName = marker + "-general" });
      Assert.True(general.Success, general.Message); ids.Add(general.EntityId!.Value);
      material = await setup.ExecuteScalarAsync<int>("INSERT logistica.Material(MaterialCode,[Description],BaseUnitId) SELECT @Marker,@Marker,MIN(Id) FROM logistica.UnitOfMeasure; SELECT CONVERT(int,SCOPE_IDENTITY());", new { Marker = marker });
      var privateBalance = await AddBalance(child.EntityId.Value, 7);
      var generalBalance = await AddBalance(general.EntityId.Value, 3);
      var bytes = System.Text.Encoding.UTF8.GetBytes(marker);
      var attachment = await stockA.SaveLocationMaterialAttachmentAsync(new() { LocationId = child.EntityId.Value, MaterialId = material, FileName = "private.txt", FileExtension = "txt", ContentType = "text/plain", Bytes = bytes });
      Assert.True(attachment.Success, attachment.Message);
      Assert.Equal(7m, Assert.Single(await stockA.GetStockAsync(new() { LocationId = child.EntityId })).Quantity);
      Assert.Single(await stockA.GetStockTransactionsAsync(privateBalance));
      Assert.Single(await stockA.GetLocationMaterialAttachmentsAsync(child.EntityId.Value, material));
      Assert.Equal(bytes, (await stockA.GetLocationMaterialAttachmentContentAsync(attachment.EntityId!.Value))!.Bytes);
      Assert.Equal(10m, (await materialA.GetMaterialInventoryAsync(scope.CompanyRfc, material)).TotalQuantity);
      Assert.Equal(2, (await materialA.GetMaterialMovementsAsync(new() { Rfc = scope.CompanyRfc, MaterialId = material })).Count);

      foreach (var accessor in new IHospitalityScopeAccessor?[] { null, new FixedScope(scope with { SiteId = siteB }) })
      {
        var stock = new StockService(factory, accessor);
        var materials = new MaterialService(factory, hospitalityScope: accessor);
        Assert.Empty(await stock.GetStockAsync(new() { LocationId = child.EntityId }));
        Assert.Empty(await stock.GetStockTransactionsAsync(privateBalance));
        Assert.Empty(await stock.GetLocationMaterialAttachmentsAsync(child.EntityId.Value, material, includeDeleted: true));
        Assert.Null(await stock.GetLocationMaterialAttachmentContentAsync(attachment.EntityId.Value));
        Assert.False((await stock.SaveStockThresholdsAsync(new() { StockBalanceId = privateBalance, MinQuantity = 999 })).Success);
        Assert.False((await stock.AddMaterialToLocationAsync(new() { LocationId = child.EntityId.Value, MaterialId = material })).Success);
        Assert.False((await stock.RemoveLocationMaterialAsync(privateBalance, "scope-test")).Success);
        Assert.False((await stock.ReactivateLocationMaterialAsync(privateBalance, "scope-test")).Success);
        Assert.False((await stock.SaveLocationMaterialAttachmentAsync(new() { LocationId = child.EntityId.Value, MaterialId = material, FileName = "denied.txt", FileExtension = "txt", Bytes = bytes })).Success);
        var snapshot = await materials.GetMaterialInventoryAsync(scope.CompanyRfc, material);
        Assert.Equal(3m, snapshot.TotalQuantity);
        Assert.Equal(general.EntityId, Assert.Single(snapshot.Locations).LocationId);
        Assert.Equal(1, snapshot.TotalMovementCount);
        Assert.Equal(general.EntityId, Assert.Single(await materials.GetMaterialMovementsAsync(new() { Rfc = scope.CompanyRfc, MaterialId = material })).LocationId);
        var listed = Assert.Single(await materials.GetMaterialsAsync(new() { Rfc = scope.CompanyRfc, SearchText = marker }));
        Assert.Equal(3m, listed.TotalQuantity);
        Assert.Equal(51862, (await Assert.ThrowsAsync<SqlException>(() => materials.GetMaterialLifecycleAssessmentAsync(scope.CompanyRfc, material))).Number);
        Assert.Equal(51862, (await Assert.ThrowsAsync<SqlException>(() => materials.DeactivateMaterialAsync(new() { Rfc = scope.CompanyRfc, MaterialId = material, DeactivatedBy = "scope test" }))).Number);
        Assert.True((await stock.SaveStockThresholdsAsync(new() { StockBalanceId = generalBalance, MinQuantity = 1 })).Success);
        var publicAttachment = await stock.SaveLocationMaterialAttachmentAsync(new() { LocationId = general.EntityId.Value, MaterialId = material, FileName = "general.txt", FileExtension = "txt", Bytes = bytes });
        Assert.True(publicAttachment.Success, publicAttachment.Message);
        Assert.Equal(bytes, (await stock.GetLocationMaterialAttachmentContentAsync(publicAttachment.EntityId!.Value))!.Bytes);
      }

      var foreignStock = new StockService(otherFactory, new FixedScope(other));
      var foreignMaterial = new MaterialService(otherFactory, hospitalityScope: new FixedScope(other));
      Assert.Empty(await foreignStock.GetStockAsync(new() { LocationId = general.EntityId }));
      Assert.Empty(await foreignStock.GetStockTransactionsAsync(privateBalance));
      Assert.Null(await foreignStock.GetLocationMaterialAttachmentContentAsync(attachment.EntityId.Value));
      Assert.False((await foreignStock.SaveStockThresholdsAsync(new() { StockBalanceId = generalBalance, MinQuantity = 999 })).Success);
      Assert.Empty((await foreignMaterial.GetMaterialInventoryAsync(other.CompanyRfc, material)).Locations);
      Assert.Equal(51861, (await Assert.ThrowsAsync<SqlException>(() => foreignMaterial.GetMaterialInventoryAsync(scope.CompanyRfc, material))).Number);
      Assert.Equal(7m, await setup.ExecuteScalarAsync<decimal>("SELECT Quantity FROM logistica.StockBalance WHERE Id=@Id", new { Id = privateBalance }));
      Assert.Null(await setup.ExecuteScalarAsync<decimal?>("SELECT MinQuantity FROM logistica.StockBalance WHERE Id=@Id", new { Id = privateBalance }));
      Assert.True(await setup.ExecuteScalarAsync<bool>("SELECT IsActive FROM logistica.Material WHERE Id=@Id", new { Id = material }));

      // The restaurant readiness report must use the same location closure for
      // stock, lots, available quantities and copied location names.
      restaurantSite = await setup.ExecuteScalarAsync<int>("INSERT restaurante.Site(Rfc,SiteCode,[Name],TimeZoneId) VALUES(@Rfc,@Marker,@Marker,'America/Mexico_City'); SELECT CONVERT(int,SCOPE_IDENTITY());", new { Rfc = scope.CompanyRfc, Marker = marker });
      await setup.ExecuteAsync("UPDATE logistica.Material SET TrackLots=1,FulfillmentMode='StockItem' WHERE Id=@Id;", new { Id = material });
      var lot = await setup.ExecuteScalarAsync<long>("INSERT logistica.MaterialLot(Rfc,MaterialId,LotCode,SourceType) VALUES(@Rfc,@MaterialId,@Marker,'Test'); SELECT CONVERT(bigint,SCOPE_IDENTITY());", new { Rfc = scope.CompanyRfc, MaterialId = material, Marker = marker });
      await setup.ExecuteAsync("INSERT logistica.LotBalance(Rfc,MaterialLotId,MaterialId,LocationId,Quantity) VALUES(@Rfc,@Lot,@MaterialId,@Private,7),(@Rfc,@Lot,@MaterialId,@General,3);", new { Rfc = scope.CompanyRfc, Lot = lot, MaterialId = material, Private = child.EntityId, General = general.EntityId });
      var catalog = DispatchProxy.Create<IRestaurantCatalogService, TestProxy>();
      ((TestProxy)(object)catalog).Result = new RestaurantPosCatalogDto
      {
        Site = new() { Id = restaurantSite, Rfc = scope.CompanyRfc, TimeZoneId = "America/Mexico_City" },
        Sections = [new() { Name = marker, Products = [new() { Id = material, MaterialId = material, Name = marker, Sku = marker, IsActive = true, FulfillmentMode = "StockItem" }] }]
      };
      var reportA = await new RestaurantSaleReadinessService(factory, catalog, scoped).AnalyzeAsync(scope.CompanyRfc, restaurantSite, DateTimeOffset.UtcNow);
      Assert.Equal(10m, Assert.Single(reportA.Ingredients).UsableQuantity);
      Assert.Contains(marker + "-child", Assert.Single(reportA.Ingredients).LocationSummary);
      foreach (var accessor in new IHospitalityScopeAccessor?[] { null, new FixedScope(scope with { SiteId = siteB }) })
      {
        var report = await new RestaurantSaleReadinessService(factory, catalog, accessor).AnalyzeAsync(scope.CompanyRfc, restaurantSite, DateTimeOffset.UtcNow);
        var ingredient = Assert.Single(report.Ingredients);
        Assert.Equal(3m, ingredient.StockQuantity);
        Assert.Equal(3m, ingredient.UsableQuantity);
        Assert.Contains(marker + "-general", ingredient.LocationSummary);
        Assert.DoesNotContain(marker + "-child", ingredient.LocationSummary);
      }
      Assert.Equal(51936, (await Assert.ThrowsAsync<SqlException>(() => new RestaurantSaleReadinessService(otherFactory, catalog).AnalyzeAsync(scope.CompanyRfc, restaurantSite, DateTimeOffset.UtcNow))).Number);

      // Persistent diagnostics always measure general inventory, regardless of
      // hospitality access; old inventory snapshots have no trustworthy scope.
      await setup.ExecuteAsync("UPDATE logistica.Material SET ProductType='FinishedGood',FulfillmentMode='MakeToOrder',BaseUnitPrice=1 WHERE Id=@Id;", new { Id = material });
      var diagnostics = new RestaurantDiagnosticsService(factory, DispatchProxy.Create<IRestaurantAccountingService, TestProxy>(), DispatchProxy.Create<ICuentasContablesRepository, TestProxy>());
      var run = await diagnostics.RunAsync(new() { Rfc = scope.CompanyRfc, SiteId = restaurantSite, From = DateTime.Today, To = DateTime.Today }, marker);
      var inventoryFinding = Assert.Single(run.Findings, f => f.ReglaClave == "R17-G");
      Assert.Equal(3m, inventoryFinding.MontoExpuesto);
      Assert.DoesNotContain(run.Findings, f => f.ReglaClave is "R13" or "R16" or "R17");
      var legacyFinding = await setup.ExecuteScalarAsync<long>("INSERT restaurante.DiagnosticoHallazgo(Rfc,CorridaId,ReglaClave,Severidad,Titulo,Detalle,MontoExpuesto) VALUES(@Rfc,@RunId,'R17','Critica','historical private',@Marker,123456); SELECT CONVERT(bigint,SCOPE_IDENTITY());", new { Rfc = scope.CompanyRfc, RunId = run.Id, Marker = marker });
      await setup.ExecuteAsync("UPDATE restaurante.DiagnosticoCorrida SET MontoExpuesto=123456,HallazgosTotal=999,Criticos=999 WHERE Id=@Id;", new { Id = run.Id });
      var history = Assert.Single(await diagnostics.GetHistoryAsync(scope.CompanyRfc, restaurantSite));
      Assert.DoesNotContain(history.Findings, f => f.Id == legacyFinding);
      Assert.Equal(history.Findings.Count, history.HallazgosTotal);
      Assert.Equal(history.Findings.Sum(f => f.MontoExpuesto), history.MontoExpuesto);
      Assert.Equal(history.Findings.Count(f => f.Severidad == "Critica"), history.Criticos);
      Assert.False((await diagnostics.AcceptFindingAsync(scope.CompanyRfc, legacyFinding, "scope test justification", marker)).Success);
      Assert.True((await diagnostics.AcceptFindingAsync(scope.CompanyRfc, inventoryFinding.Id, "scope test justification", marker)).Success);
      Assert.Equal("Abierto", await setup.ExecuteScalarAsync<string>("SELECT Estado FROM restaurante.DiagnosticoHallazgo WHERE Id=@Id", new { Id = legacyFinding }));

      async Task<int> AddBalance(int locationId, decimal quantity)
      {
        var id = await setup.ExecuteScalarAsync<int>("INSERT logistica.StockBalance(LocationId,MaterialId,Quantity) VALUES(@LocationId,@MaterialId,@Quantity); SELECT CONVERT(int,SCOPE_IDENTITY());", new { LocationId = locationId, MaterialId = material, Quantity = quantity });
        await setup.ExecuteAsync("INSERT logistica.StockTransaction(StockBalanceId,LocationId,MaterialId,TransactionType,QuantityDelta,QuantityAfter,Notes) VALUES(@Id,@LocationId,@MaterialId,'INITIAL',@Quantity,@Quantity,@Marker);", new { Id = id, LocationId = locationId, MaterialId = material, Quantity = quantity, Marker = marker });
        return id;
      }
    }
    finally
    {
      await setup.ExecuteAsync("DELETE f FROM restaurante.DiagnosticoHallazgo f JOIN restaurante.DiagnosticoCorrida r ON r.Id=f.CorridaId WHERE r.SiteId=@SiteId AND r.EjecutadoPor=@Marker; DELETE restaurante.DiagnosticoCorrida WHERE SiteId=@SiteId AND EjecutadoPor=@Marker; DELETE restaurante.Site WHERE Id=@SiteId AND SiteCode=@Marker; DELETE logistica.LotBalance WHERE MaterialId=@MaterialId; DELETE logistica.MaterialLot WHERE MaterialId=@MaterialId; DELETE logistica.LocationMaterialAttachment WHERE MaterialId=@MaterialId; DELETE logistica.StockTransaction WHERE MaterialId=@MaterialId; DELETE logistica.StockBalance WHERE MaterialId=@MaterialId; DELETE logistica.Material WHERE Id=@MaterialId AND MaterialCode=@Marker;", new { MaterialId = material, Marker = marker, SiteId = restaurantSite });
      foreach (var id in ids.AsEnumerable().Reverse()) await setup.ExecuteAsync("DELETE logistica.Location WHERE Id=@Id AND LocationName LIKE @Marker", new { Id = id, Marker = marker + "%" });
      await setup.ExecuteAsync("DELETE dbo.ROOM WHERE ID=@Id AND ROOM_NAME=@Marker; DELETE orion.SiteCapability WHERE CompanyId=@CompanyId AND SiteId=@SiteId AND ModuleCode='HOSPITALITY'; DELETE orion.Site WHERE SiteId=@SiteId AND SiteKey=@Marker;", new { Id = room, scope.CompanyId, SiteId = siteB, Marker = marker });
    }
  }

  private sealed class CompanyRfc(string rfc) : ICurrentRfcAccessor { public string CurrentRfc => rfc; }
  private sealed class FixedScope(HospitalityScope scope) : IHospitalityScopeAccessor
  { public Task<HospitalityScope> ResolveRequiredAsync(CancellationToken ct = default) => Task.FromResult(scope); }

  public class TestProxy : DispatchProxy
  {
    public RestaurantPosCatalogDto? Result { get; set; }
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
      => targetMethod?.Name == nameof(IRestaurantCatalogService.GetPosCatalogAsync)
        ? Task.FromResult(Result!) : throw new InvalidOperationException("Unexpected test dependency call.");
  }
}
