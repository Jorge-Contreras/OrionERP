using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using OrionERP.Application.Common;
using OrionERP.Application.Features.Ajustes.Catalogos;
using OrionERP.Application.Features.Logistica.Purchasing;
using OrionERP.Application.Features.Reservaciones;
using OrionERP.Infrastructure.Features.Logistica.Purchasing;
using OrionERP.Infrastructure.Features.Ajustes.Catalogos;
using OrionERP.Infrastructure.Features.Reservaciones;

namespace OrionERP.IntegrationTests.Reservaciones;

public sealed class HospitalityPurchasingScopeTests
{
  [Fact]
  [Trait("Category", "SqlIntegration")]
  public async Task Purchasing_HidesWholeDocumentsAcrossSites_AndPreservesGeneralOrders()
  {
    if (Environment.GetEnvironmentVariable("ORION_RUN_SQL_INTEGRATION") != "1") return;
    var source = Environment.GetEnvironmentVariable("ASPNETCORE_ConnectionStrings__OrionDb")
      ?? throw new InvalidOperationException("Sandbox connection required.");
    var cs = new SqlConnectionStringBuilder(source) { InitialCatalog = "Orion_Sandbox" }.ConnectionString;
    await using var setup = new SqlConnection(cs);
    await setup.OpenAsync();
    Assert.Equal("Orion_Sandbox", await setup.ExecuteScalarAsync<string>("SELECT DB_NAME();"), ignoreCase: true);
    var a = await setup.QuerySingleAsync<ScopeRow>("""
      SELECT ps.CompanyId,ps.SiteId,c.Rfc AS CompanyRfc FROM orion.PublicSite ps
      JOIN orion.Company c ON c.CompanyId=ps.CompanyId WHERE ps.PublicSiteKey='bonhomia-main';
      """);
    var scopeA = new HospitalityScope(a.CompanyId, a.SiteId, a.CompanyRfc);
    await HospitalityConnectionFactory.InitializeAsync(setup, scopeA);
    await setup.ExecuteAsync("EXEC sys.sp_set_session_context @key=N'OrionRfc',@value=@Rfc;", new { Rfc = a.CompanyRfc });
    var roomId = await setup.ExecuteScalarAsync<int>("SELECT TOP(1) ID FROM dbo.ROOM ORDER BY ID;");
    var material = await setup.QueryFirstAsync<MaterialRow>("""
      SELECT m.Id,m.MaterialCode,m.Description,mv.BusinessPartnerId FROM logistica.Material m
      JOIN logistica.MaterialVendor mv ON mv.Rfc=m.Rfc AND mv.MaterialId=m.Id
      WHERE m.Rfc=@CompanyRfc AND m.IsActive=1 AND mv.IsActive=1 ORDER BY m.Id;
      """, a);
    var marker = "scpo" + Guid.NewGuid().ToString("N")[..12];
    long siteB = 0;
    var locationIds = new List<int>();
    var orderIds = new List<int>();
    try
    {
      siteB = await setup.ExecuteScalarAsync<long>("""
        INSERT orion.Site(CompanyId,SiteKey,DisplayName,TimeZoneId)
        VALUES(@CompanyId,@Marker,@Marker,'America/Mexico_City'); SELECT CONVERT(bigint,SCOPE_IDENTITY());
        """, new { a.CompanyId, Marker = marker });
      var roomLocation = await AddLocation(roomId, null, marker + "room");
      var childLocation = await AddLocation(null, roomLocation, marker + "child");
      var generalLocation = await AddLocation(null, null, marker + "general");
      var scopedOrder = await AddOrder(childLocation, marker + "A");
      var generalOrder = await AddOrder(generalLocation, marker + "G");
      var mixedOrder = await AddOrder(generalLocation, marker + "M");
      await setup.ExecuteAsync("""
        INSERT logistica.PurchaseOrderRoomScope(PurchaseOrderId,RoomId)
        VALUES(@PurchaseOrderId,@RoomId);
        """, new { PurchaseOrderId = mixedOrder, RoomId = roomId });
      var factory = new CompanyFactory(cs, a.CompanyRfc);
      var serviceA = new PurchaseOrderService(factory, new FixedScope(scopeA));
      var serviceB = new PurchaseOrderService(factory, new FixedScope(scopeA with { SiteId = siteB }));
      var unscoped = new PurchaseOrderService(factory);
      var configuration = new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?> { ["ConnectionStrings:OrionDb"] = cs }).Build();
      var catalogA = new CatalogoService(configuration, new HospitalityConnectionFactory(configuration, new FixedScope(scopeA)));
      var catalogB = new CatalogoService(configuration, new HospitalityConnectionFactory(configuration, new FixedScope(scopeA with { SiteId = siteB })));
      var expectedOwners = (await setup.QueryAsync<string>("""
        SELECT DISTINCT CONVERT(nvarchar(50),p.id) FROM dbo.Proveedores p
        JOIN dbo.ROOM room ON room.OWNER_ID=p.id;
        """)).OrderBy(id => id).ToArray();
      Assert.Equal(expectedOwners, (await catalogA.GetItemsAsync(CatalogoKey.Arrendadores, a.CompanyRfc, null, false)).Select(row => row.Id).OrderBy(id => id));
      Assert.Empty(await catalogB.GetItemsAsync(CatalogoKey.Arrendadores, a.CompanyRfc, null, false));
      Assert.NotNull(await serviceA.GetPurchaseOrderAsync(scopedOrder));
      Assert.Null(await serviceB.GetPurchaseOrderAsync(scopedOrder));
      Assert.Null(await unscoped.GetPurchaseOrderAsync(scopedOrder));
      Assert.Null(await serviceB.GetPurchaseOrderAsync(mixedOrder));
      Assert.Null(await unscoped.GetPurchaseOrderAsync(mixedOrder));
      Assert.NotNull(await serviceB.GetPurchaseOrderAsync(generalOrder));
      Assert.NotNull(await unscoped.GetPurchaseOrderAsync(generalOrder));
      var listB = await serviceB.GetPurchaseOrdersAsync(new PurchaseOrderFilter { SearchText = marker });
      Assert.Single(listB);
      Assert.Equal(generalOrder, listB[0].Id);
      Assert.False((await serviceB.IssueAsync(scopedOrder, marker)).Success);
      Assert.False((await serviceB.CancelAsync(mixedOrder, marker)).Success);
      Assert.True((await serviceA.IssueAsync(scopedOrder, marker)).Success);
      Assert.True((await unscoped.CancelAsync(generalOrder, marker)).Success);
      var catalog = await serviceB.GetCatalogAsync();
      Assert.DoesNotContain(catalog.Locations, row => row.Id == roomLocation || row.Id == childLocation);
      Assert.Contains(catalog.Locations, row => row.Id == generalLocation);
      var invalidDraft = new PurchaseOrderUpsertRequest
      {
        BusinessPartnerId = material.BusinessPartnerId,
        OrderDate = DateTime.Today,
        Lines = [new() { MaterialId = material.Id, Allocations = [new() { LocationId = childLocation, PlannedQuantity = 1 }] }]
      };
      var rejected = await Assert.ThrowsAsync<SqlException>(() => serviceB.SaveDraftAsync(invalidDraft, marker));
      Assert.Equal(51932, rejected.Number);
      var crudDraft = new PurchaseOrderUpsertRequest
      {
        BusinessPartnerId = material.BusinessPartnerId,
        OrderDate = DateTime.Today,
        Notes = marker + "-created",
        LinkMaterialsToVendor = false,
        Lines = [new()
        {
          MaterialId = material.Id,
          BaseUnitPrice = 1m,
          PurchaseQuantitySnapshot = 1m,
          Allocations = [new() { LocationId = generalLocation, PlannedQuantity = 1m }]
        }]
      };
      var createdDraft = await serviceB.SaveDraftAsync(crudDraft, marker);
      Assert.True(createdDraft.Success, createdDraft.Message);
      var crudDraftId = createdDraft.EntityId!.Value;
      orderIds.Add(crudDraftId);
      crudDraft.Id = crudDraftId;
      crudDraft.Notes = marker + "-edited";
      var editedDraft = await serviceB.SaveDraftAsync(crudDraft, marker);
      Assert.True(editedDraft.Success, editedDraft.Message);
      Assert.Equal(marker + "-edited", await setup.ExecuteScalarAsync<string>(
        "SELECT Notes FROM logistica.PurchaseOrder WHERE Id=@Id;", new { Id = crudDraftId }));
      var cancelledDraft = await serviceB.CancelAsync(crudDraftId, marker);
      Assert.True(cancelledDraft.Success, cancelledDraft.Message);
      Assert.Equal(PurchaseOrderStatuses.Cancelled, await setup.ExecuteScalarAsync<string>(
        "SELECT [Status] FROM logistica.PurchaseOrder WHERE Id=@Id;", new { Id = crudDraftId }));
      var noCompany = new PurchaseOrderService(new CompanyFactory(cs, "__UNSCOPED__"));
      var noCompanyError = await Assert.ThrowsAsync<SqlException>(() => noCompany.GetPurchaseOrdersAsync(new()));
      Assert.Equal(51930, noCompanyError.Number);
    }
    finally
    {
      // Delete only IDs produced by this test, in dependency order.
      foreach (var id in orderIds)
        await setup.ExecuteAsync("""
          DELETE a FROM logistica.PurchaseOrderLineAllocation a
          JOIN logistica.PurchaseOrderLine l ON l.Id=a.PurchaseOrderLineId WHERE l.PurchaseOrderId=@Id;
          DELETE logistica.PurchaseOrderLine WHERE PurchaseOrderId=@Id;
          DELETE logistica.PurchaseOrderRoomScope WHERE PurchaseOrderId=@Id;
          DELETE logistica.PurchaseOrder WHERE Id=@Id;
          """, new { Id = id });
      foreach (var id in locationIds.AsEnumerable().Reverse())
        await setup.ExecuteAsync("DELETE logistica.Location WHERE Id=@Id;", new { Id = id });
      if (siteB > 0) await setup.ExecuteAsync("DELETE orion.Site WHERE SiteId=@SiteId;", new { SiteId = siteB });
    }

    async Task<int> AddLocation(int? room, int? parent, string code)
    {
      var id = await setup.ExecuteScalarAsync<int>("""
        INSERT logistica.Location(LocationCode,LocationName,LocationType,RoomId,ParentLocationId)
        VALUES(@Code,@Code,'Warehouse',@RoomId,@ParentId); SELECT CONVERT(int,SCOPE_IDENTITY());
        """, new { Code = code, RoomId = room, ParentId = parent });
      locationIds.Add(id);
      return id;
    }

    async Task<int> AddOrder(int location, string code)
    {
      var id = await setup.ExecuteScalarAsync<int>("""
        INSERT logistica.PurchaseOrder(PurchaseOrderCode,BusinessPartnerId,OrderDate,Notes)
        VALUES(@Code,@BusinessPartnerId,CONVERT(date,SYSUTCDATETIME()),@Code); SELECT CONVERT(int,SCOPE_IDENTITY());
        """, new { Code = code, material.BusinessPartnerId });
      orderIds.Add(id);
      var line = await setup.ExecuteScalarAsync<int>("""
        INSERT logistica.PurchaseOrderLine(PurchaseOrderId,MaterialId,MaterialCodeSnapshot,MaterialDescriptionSnapshot,OrderedQuantity)
        VALUES(@Id,@MaterialId,@MaterialCode,@Description,1); SELECT CONVERT(int,SCOPE_IDENTITY());
        """, new { Id = id, MaterialId = material.Id, material.MaterialCode, material.Description });
      await setup.ExecuteAsync("""
        INSERT logistica.PurchaseOrderLineAllocation(PurchaseOrderLineId,LocationId,PlannedQuantity)
        VALUES(@Line,@Location,1);
        """, new { Line = line, Location = location });
      return id;
    }
  }

  private sealed record ScopeRow(long CompanyId, long SiteId, string CompanyRfc);
  private sealed record MaterialRow(int Id, string MaterialCode, string Description, int BusinessPartnerId);
  private sealed class FixedScope(HospitalityScope scope) : IHospitalityScopeAccessor
  {
    public Task<HospitalityScope> ResolveRequiredAsync(CancellationToken ct = default) => Task.FromResult(scope);
  }
  private sealed class CompanyFactory(string connectionString, string rfc) : IDbConnectionFactory
  {
    public IDbConnection Create()
    {
      var connection = new SqlConnection(connectionString);
      connection.StateChange += (_, args) =>
      {
        if (args.CurrentState == ConnectionState.Open)
          connection.Execute("EXEC sys.sp_set_session_context @key=N'OrionRfc',@value=@Rfc;", new { Rfc = rfc });
      };
      return connection;
    }
  }
}
