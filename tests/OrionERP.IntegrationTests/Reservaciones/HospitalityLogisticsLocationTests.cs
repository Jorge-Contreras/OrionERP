using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using OrionERP.Application.Common;
using OrionERP.Application.Features.Logistica.Locations;
using OrionERP.Infrastructure.Features.Logistica.PhysicalCounts;
using OrionERP.Application.Features.Reservaciones;
using OrionERP.Infrastructure.Features.Logistica.Locations;
using OrionERP.Infrastructure.Features.Logistica.Shared;
using OrionERP.Infrastructure.Features.Reservaciones;
using OrionERP.Infrastructure.Features.Restaurante;
using CompanyConnectionFactory = OrionERP.Infrastructure.Features.Cfdi.DescargaMasiva.Dapper.SqlConnectionFactory;

namespace OrionERP.IntegrationTests.Reservaciones;

public sealed class HospitalityLogisticsLocationTests
{
  [Fact, Trait("Category", "SqlIntegration")]
  public async Task LocationHierarchy_HidesCopiedNamesAndRejectsForeignOrDetachedReferences()
  {
    if (Environment.GetEnvironmentVariable("ORION_RUN_SQL_INTEGRATION")!="1") return;
    var source=Environment.GetEnvironmentVariable("ASPNETCORE_ConnectionStrings__OrionDb")??throw new InvalidOperationException("Missing Sandbox connection.");
    var cs=new SqlConnectionStringBuilder(source) { InitialCatalog="Orion_Sandbox" }.ConnectionString;
    var cfg=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["ConnectionStrings:OrionDb"]=cs }).Build();
    await using var bootstrap=new SqlConnection(cs);
    await bootstrap.OpenAsync();
    Assert.Equal("Orion_Sandbox",await bootstrap.ExecuteScalarAsync<string>("SELECT DB_NAME()"),ignoreCase:true);
    var rows=(await bootstrap.QueryAsync<ScopeRow>("SELECT p.PublicSiteKey,p.CompanyId,p.SiteId,c.Rfc AS CompanyRfc FROM orion.PublicSite p JOIN orion.Company c ON c.CompanyId=p.CompanyId WHERE p.PublicSiteKey IN ('synthetic-hospitality-main','bonhomia-main')")).ToList();
    var a=rows.Single(x=>x.PublicSiteKey=="synthetic-hospitality-main").Scope;
    var b=rows.Single(x=>x.PublicSiteKey=="bonhomia-main").Scope;
    var factoryA=new CompanyConnectionFactory(cfg,new CompanyRfc(a.CompanyRfc));
    var factoryB=new CompanyConnectionFactory(cfg,new CompanyRfc(b.CompanyRfc));
    var serviceA=new LocationService(factoryA,new FixedScope(a));
    var serviceB=new LocationService(factoryB,new FixedScope(b));
    var generalService=new LocationService(factoryA);
    var marker="LRLS-"+Guid.NewGuid().ToString("N");
    var idsA=new List<int>(); var idsB=new List<int>(); var countIds=new List<int>(); var balanceIds=new List<int>(); int roomId=0; int materialId=0; int restaurantSiteId=0; long restaurantPlatformSiteId=0; var orderIds=new List<Guid>(); var reservationIds=new List<long>();
    try
    {
      await HospitalityConnectionFactory.InitializeAsync(bootstrap,a);
      roomId=await bootstrap.ExecuteScalarAsync<int>("INSERT dbo.ROOM(ROOM_NAME,ROOM_TYPE) VALUES(@Name,'SUITE'); SELECT CONVERT(int,SCOPE_IDENTITY());",new { Name=marker });
      var rootRequest=new LocationUpsertRequest { LocationName=marker+"-private",RoomId=roomId };
      var root=await serviceA.SaveLocationAsync(rootRequest);
      Assert.True(root.Success,root.Message); idsA.Add(root.EntityId!.Value);
      var child=await serviceA.SaveLocationAsync(new() { LocationName=marker+"-child-copy",ParentLocationId=root.EntityId });
      Assert.True(child.Success,child.Message); idsA.Add(child.EntityId!.Value);
      var grandchild=await serviceA.SaveLocationAsync(new() { LocationName=marker+"-grandchild-copy",ParentLocationId=child.EntityId });
      Assert.True(grandchild.Success,grandchild.Message); idsA.Add(grandchild.EntityId!.Value);
      var general=await generalService.SaveLocationAsync(new() { LocationName=marker+"-general" });
      Assert.True(general.Success,general.Message); idsA.Add(general.EntityId!.Value);
      var foreign=await serviceB.SaveLocationAsync(new() { LocationName=marker+"-foreign-general" });
      Assert.True(foreign.Success,foreign.Message); idsB.Add(foreign.EntityId!.Value);
      var crudLocationRequest=new LocationUpsertRequest { LocationName=marker+"-crud",IsInventoryEnabled=true,IsActive=true };
      var crudLocation=await generalService.SaveLocationAsync(crudLocationRequest);
      Assert.True(crudLocation.Success,crudLocation.Message); idsA.Add(crudLocation.EntityId!.Value);
      crudLocationRequest.Id=crudLocation.EntityId;
      crudLocationRequest.LocationName=marker+"-crud-edited";
      Assert.True((await generalService.SaveLocationAsync(crudLocationRequest)).Success);
      crudLocationRequest.IsActive=false;
      Assert.True((await generalService.SaveLocationAsync(crudLocationRequest)).Success);
      Assert.False((await generalService.GetLocationAsync(crudLocation.EntityId.Value))!.IsActive);

      Assert.NotNull(await serviceA.GetLocationAsync(root.EntityId.Value));
      Assert.NotNull(await serviceA.GetLocationAsync(grandchild.EntityId.Value));
      Assert.Null(await serviceB.GetLocationAsync(root.EntityId.Value));
      Assert.Null(await generalService.GetLocationAsync(root.EntityId.Value));
      Assert.Null(await generalService.GetLocationAsync(child.EntityId.Value));
      Assert.Null(await generalService.GetLocationAsync(grandchild.EntityId.Value));
      Assert.Null(await serviceA.GetLocationAsync(foreign.EntityId.Value));
      Assert.NotNull(await generalService.GetLocationAsync(general.EntityId.Value));
      var visibleGeneral=await generalService.GetLocationsAsync(new() { SearchText=marker });
      Assert.Single(visibleGeneral); Assert.Equal(general.EntityId,visibleGeneral[0].Id);
      Assert.DoesNotContain(await generalService.GetLocationLookupAsync(),x=>x.Id==root.EntityId || x.Id==child.EntityId || x.Id==grandchild.EntityId);
      Assert.DoesNotContain(Flatten(await generalService.GetLocationTreeAsync()),x=>x==root.EntityId || x==child.EntityId || x==grandchild.EntityId);
      Assert.Contains(Flatten(await serviceA.GetLocationTreeAsync()),x=>x==grandchild.EntityId);

      rootRequest.Id=root.EntityId;
      rootRequest.LocationName=marker+"-forbidden";
      Assert.False((await serviceB.SaveLocationAsync(rootRequest)).Success);
      Assert.False((await generalService.SaveLocationAsync(rootRequest)).Success);
      Assert.False((await serviceB.SaveLocationAsync(new() { LocationName=marker+"-cross-parent",ParentLocationId=root.EntityId })).Success);
      Assert.False((await serviceB.SaveLocationAsync(new() { LocationName=marker+"-cross-room",RoomId=roomId })).Success);
      rootRequest.RoomId=null;
      Assert.False((await serviceA.SaveLocationAsync(rootRequest)).Success);
      Assert.False((await serviceA.SaveLocationAsync(new() { Id=child.EntityId,LocationName=marker+"-detach",ParentLocationId=null })).Success);
      Assert.Equal(marker+"-private",(await serviceA.GetLocationAsync(root.EntityId.Value))!.LocationName);

      // LegacyRoomId alone remains sensitive even when modern RoomId is NULL.
      await bootstrap.ExecuteAsync("EXEC sys.sp_set_session_context @key=N'OrionRfc',@value=@Rfc",new { Rfc=a.CompanyRfc });
      var legacy=await bootstrap.ExecuteScalarAsync<int>("INSERT logistica.Location(LocationCode,LocationName,LocationType,LegacyRoomId) VALUES(@Name,@Name,'Storage',@RoomId); SELECT CONVERT(int,SCOPE_IDENTITY());",new { Name=marker+"-legacy",RoomId=roomId });
      idsA.Add(legacy);
      Assert.NotNull(await serviceA.GetLocationAsync(legacy));
      Assert.Null(await generalService.GetLocationAsync(legacy));

      // Physical-count snapshots inherit privacy from every location/ancestor in every line.
      var unitId=await bootstrap.ExecuteScalarAsync<int>("SELECT TOP (1) Id FROM logistica.UnitOfMeasure WHERE IsActive=1 ORDER BY Id;");
      materialId=await bootstrap.ExecuteScalarAsync<int>("INSERT logistica.Material(Rfc,MaterialCode,[Description],BaseUnitId) VALUES(@Rfc,@Code,@Description,@UnitId); SELECT CONVERT(int,SCOPE_IDENTITY());",new { Rfc=a.CompanyRfc,Code=marker[..20],Description=marker,UnitId=unitId });
      Assert.True(materialId>0);
      foreach(var locationId in new[] { grandchild.EntityId!.Value,general.EntityId!.Value })
        balanceIds.Add(await bootstrap.ExecuteScalarAsync<int>("INSERT logistica.StockBalance(LocationId,MaterialId,Quantity) VALUES(@LocationId,@MaterialId,1); SELECT CONVERT(int,SCOPE_IDENTITY());",new { LocationId=locationId,MaterialId=materialId }));
      var countsA=new PhysicalCountService(factoryA,new FixedScope(a));
      var countsB=new PhysicalCountService(factoryB,new FixedScope(b));
      var countsGeneral=new PhysicalCountService(factoryA);
      var preview=await countsA.PreviewScopeAsync(new() { LocationId=grandchild.EntityId });
      Assert.Equal(1,preview.LineCount);
      var countA=await countsA.CreateSessionAsync(new() { LocationId=grandchild.EntityId,Notes=marker,CreatedBy="scope-test" });
      Assert.True(countA.Success,countA.Message); countIds.Add(countA.EntityId!.Value);
      var countGeneral=await countsGeneral.CreateSessionAsync(new() { LocationId=general.EntityId,Notes=marker,CreatedBy="scope-test" });
      Assert.True(countGeneral.Success,countGeneral.Message); countIds.Add(countGeneral.EntityId!.Value);
      Assert.NotNull(await countsA.GetSessionAsync(countA.EntityId.Value));
      Assert.Null(await countsB.GetSessionAsync(countA.EntityId.Value));
      Assert.Null(await countsGeneral.GetSessionAsync(countA.EntityId.Value));
      Assert.NotNull(await countsGeneral.GetSessionAsync(countGeneral.EntityId.Value));
      Assert.DoesNotContain(await countsGeneral.GetSessionsAsync(),row=>row.Id==countA.EntityId.Value);
      Assert.Contains(await countsGeneral.GetSessionsAsync(),row=>row.Id==countGeneral.EntityId.Value);
      var generalCountLine=Assert.Single((await countsGeneral.GetSessionAsync(countGeneral.EntityId.Value))!.Lines);
      var editedGeneralCount=await countsGeneral.CaptureLineAsync(new()
      {
        SessionId=countGeneral.EntityId.Value,LineId=generalCountLine.Id,CountedQuantity=1m,
        Notes=marker+"-edited",CapturedBy="scope-test"
      });
      Assert.True(editedGeneralCount.Success,editedGeneralCount.Message);
      var deletedGeneralCount=await countsGeneral.DeleteDraftSessionAsync(countGeneral.EntityId.Value);
      Assert.True(deletedGeneralCount.Success,deletedGeneralCount.Message);
      Assert.Null(await countsGeneral.GetSessionAsync(countGeneral.EntityId.Value));
      Assert.NotEmpty((await countsA.PreviewScopeAsync(new() { LocationId=grandchild.EntityId })).Conflicts);
      await Assert.ThrowsAsync<SqlException>(()=>countsB.CreateSessionAsync(new() { LocationId=grandchild.EntityId,Notes=marker }));
      await Assert.ThrowsAsync<SqlException>(()=>countsGeneral.CreateSessionAsync(new() { LocationId=grandchild.EntityId,Notes=marker }));
      var countLine=Assert.Single((await countsA.GetSessionAsync(countA.EntityId.Value))!.Lines);
      var bytes=System.Text.Encoding.UTF8.GetBytes(marker);
      var captured=await countsA.CaptureLineAsync(new() { SessionId=countA.EntityId.Value,LineId=countLine.Id,CountedQuantity=2m,Notes=marker,CapturedBy="scope-test",AttachmentBytes=bytes,AttachmentFileName="scope.txt",AttachmentContentType="text/plain" });
      Assert.True(captured.Success,captured.Message);
      var capturedLine=Assert.Single((await countsA.GetSessionAsync(countA.EntityId.Value))!.Lines);
      var attachment=Assert.Single(capturedLine.Attachments);
      Assert.Equal(bytes,(await countsA.GetAttachmentContentAsync(attachment.Id))!.Bytes);
      Assert.Null(await countsB.GetAttachmentContentAsync(attachment.Id));
      Assert.Null(await countsGeneral.GetAttachmentContentAsync(attachment.Id));
      await Assert.ThrowsAsync<SqlException>(()=>countsB.CaptureLineAsync(new() { SessionId=countA.EntityId.Value,LineId=countLine.Id,CountedQuantity=9m }));
      await Assert.ThrowsAsync<SqlException>(()=>countsGeneral.DeleteDraftSessionAsync(countA.EntityId.Value));
      await Assert.ThrowsAsync<SqlException>(()=>countsB.ApproveSessionAsync(countA.EntityId.Value,"scope-test"));
      Assert.True((await countsA.SubmitSessionAsync(countA.EntityId.Value,"scope-test")).Success);
      Assert.True((await countsA.ApproveSessionAsync(countA.EntityId.Value,"scope-test")).Success);
      var posted=await countsA.PostSessionAsync(countA.EntityId.Value,"scope-test");
      Assert.True(posted.Success,posted.Message);
      Assert.Equal(2m,await bootstrap.ExecuteScalarAsync<decimal>("SELECT Quantity FROM logistica.StockBalance WHERE Id=@Id",new { Id=balanceIds[0] }));

      // POS cannot reveal or mutate an order if any reserved/consumed location is hidden.
      restaurantPlatformSiteId=await bootstrap.ExecuteScalarAsync<long>("INSERT orion.Site(CompanyId,SiteKey,DisplayName,TimeZoneId) VALUES(@CompanyId,@Code,@Name,'America/Mexico_City'); SELECT CONVERT(bigint,SCOPE_IDENTITY());",new { a.CompanyId,Code=marker[..25].ToLowerInvariant(),Name=marker });
      await bootstrap.ExecuteAsync("INSERT orion.SiteCapability(CompanyId,SiteId,ModuleCode,IsEnabled,UpdatedBy) VALUES(@CompanyId,@SiteId,'HOSPITALITY',1,N'SqlIntegration'),(@CompanyId,@SiteId,'RESTAURANT',1,N'SqlIntegration');",new { a.CompanyId,SiteId=restaurantPlatformSiteId });
      restaurantSiteId=await bootstrap.ExecuteScalarAsync<int>("INSERT restaurante.Site(Rfc,SiteCode,[Name]) VALUES(@Rfc,@Code,@Name); SELECT CONVERT(int,SCOPE_IDENTITY());",new { Rfc=a.CompanyRfc,Code=marker[..25].ToLowerInvariant(),Name=marker });
      foreach(var privateInventory in new[] { true,false })
      {
        var orderId=Guid.NewGuid(); orderIds.Add(orderId);
        var reservationId=await bootstrap.ExecuteScalarAsync<long>("INSERT logistica.InventoryReservation(Rfc,SiteId,ReferenceType,ReferenceId,IdempotencyKey,[Status]) VALUES(@Rfc,@SiteId,'RestaurantOrder',@OrderId,@Key,'Reserved'); SELECT CONVERT(bigint,SCOPE_IDENTITY());",new { Rfc=a.CompanyRfc,SiteId=restaurantSiteId,OrderId=orderId,Key=orderId.ToString() });
        reservationIds.Add(reservationId);
        await bootstrap.ExecuteAsync("INSERT logistica.InventoryReservationLine(Rfc,ReservationId,MaterialId,LocationId,RequiredQuantity,ReservedQuantity) VALUES(@Rfc,@ReservationId,@MaterialId,@LocationId,1,0);",new { Rfc=a.CompanyRfc,ReservationId=reservationId,MaterialId=materialId,LocationId=general.EntityId });
        if(privateInventory)
          await bootstrap.ExecuteAsync("INSERT logistica.InventoryReservationLine(Rfc,ReservationId,MaterialId,LocationId,RequiredQuantity,ReservedQuantity) VALUES(@Rfc,@ReservationId,@MaterialId,@LocationId,1,0);",new { Rfc=a.CompanyRfc,ReservationId=reservationId,MaterialId=materialId,LocationId=grandchild.EntityId });
        await bootstrap.ExecuteAsync("""
          INSERT restaurante.[Order](Id,Rfc,SiteId,Folio,OperationalDate,OrderType,[Status],PaymentStatus,CustomerName,
            Subtotal,DiscountTotal,TaxTotal,TipTotal,Total,BalanceDue,TaxRateSnapshot,PricesIncludeTaxSnapshot,InventoryReservationId,IdempotencyKey)
          VALUES(@Id,@Rfc,@SiteId,@Folio,CONVERT(date,SYSUTCDATETIME()),'TakeAway','Sent','Pending',@Name,0,0,0,0,0,0,0,1,@ReservationId,@Key);
          """,new { Id=orderId,Rfc=a.CompanyRfc,SiteId=restaurantSiteId,Folio=orderIds.Count,Name=marker,ReservationId=reservationId,Key=orderId.ToString() });
      }
      var ordersA=new RestaurantOrderService(factoryA,new FixedScope(a));
      var ordersGeneral=new RestaurantOrderService(factoryA);
      var ordersB=new RestaurantOrderService(factoryB,new FixedScope(b));
      Assert.NotNull(await ordersA.GetOrderAsync(a.CompanyRfc,orderIds[0]));
      Assert.Null(await ordersGeneral.GetOrderAsync(a.CompanyRfc,orderIds[0]));
      Assert.Null(await ordersGeneral.GetReceiptAsync(a.CompanyRfc,orderIds[0]));
      Assert.NotNull(await ordersGeneral.GetOrderAsync(a.CompanyRfc,orderIds[1]));
      Assert.Null(await ordersB.GetOrderAsync(b.CompanyRfc,orderIds[0]));
      Assert.Equal(orderIds[1],Assert.Single(await ordersGeneral.GetPublicBoardAsync(a.CompanyRfc,restaurantSiteId)).Id);
      var orderDenied=await Assert.ThrowsAsync<SqlException>(()=>ordersGeneral.CancelOrderAsync(a.CompanyRfc,orderIds[0],"scope-test","scope-test"));
      Assert.Equal(51936,orderDenied.Number);
      await Assert.ThrowsAsync<SqlException>(()=>ordersGeneral.SetOrderPriorityAsync(a.CompanyRfc,orderIds[0],1,"scope-test","scope-test"));
      var rfcDenied=await Assert.ThrowsAsync<SqlException>(()=>ordersB.GetOrderAsync(a.CompanyRfc,orderIds[0]));
      Assert.Equal(51935,rfcDenied.Number);
      Assert.Equal("Sent",await bootstrap.ExecuteScalarAsync<string>("SELECT [Status] FROM restaurante.[Order] WHERE Id=@Id",new { Id=orderIds[0] }));
      var prioritized=await ordersGeneral.SetOrderPriorityAsync(a.CompanyRfc,orderIds[1],1,marker,"scope-test");
      Assert.True(prioritized.Success,prioritized.Message);
      var cancelled=await ordersGeneral.CancelOrderAsync(a.CompanyRfc,orderIds[1],marker,"scope-test");
      Assert.True(cancelled.Success,cancelled.Message);
      Assert.Equal("Cancelled",await bootstrap.ExecuteScalarAsync<string>(
        "SELECT [Status] FROM restaurante.[Order] WHERE Id=@Id",new { Id=orderIds[1] }));

      // Explicit RFC is mandatory even though the historical policy allowed NULL.
      var missingFactory=new CompanyConnectionFactory(cfg,new CompanyRfc(null));
      var missing=await Assert.ThrowsAsync<SqlException>(()=>LogisticsLocationScope.OpenAsync(missingFactory,null));
      Assert.Equal(51930,missing.Number);
      var mismatched=await Assert.ThrowsAsync<SqlException>(()=>LogisticsLocationScope.OpenAsync(factoryB,new FixedScope(a)));
      Assert.Equal(51931,mismatched.Number);
    }
    finally
    {
      await bootstrap.ExecuteAsync("EXEC sys.sp_set_session_context @key=N'OrionRfc',@value=@Rfc",new { Rfc=a.CompanyRfc });
      foreach(var orderId in orderIds)
        await bootstrap.ExecuteAsync("""
DELETE restaurante.EventOutbox WHERE AggregateId=CONVERT(varchar(36),@Id);
DELETE restaurante.SupervisorAuthorization WHERE AggregateId=CONVERT(varchar(36),@Id);
DELETE restaurante.OrderEvent WHERE OrderId=@Id;
DELETE restaurante.[Order] WHERE Id=@Id AND CustomerName=@Marker;
""",new { Id=orderId,Marker=marker });
      foreach(var reservationId in reservationIds)
        await bootstrap.ExecuteAsync("DELETE logistica.InventoryReservationLine WHERE ReservationId=@Id; DELETE logistica.InventoryReservation WHERE Id=@Id;",new { Id=reservationId });
      if(restaurantSiteId>0)
        await bootstrap.ExecuteAsync("DELETE restaurante.Site WHERE Id=@Id AND [Name]=@Marker",new { Id=restaurantSiteId,Marker=marker });
      if(restaurantPlatformSiteId>0)
        await bootstrap.ExecuteAsync("DELETE orion.SiteCapability WHERE CompanyId=@CompanyId AND SiteId=@SiteId; DELETE orion.Site WHERE CompanyId=@CompanyId AND SiteId=@SiteId AND SiteKey=@SiteKey;",new { a.CompanyId,SiteId=restaurantPlatformSiteId,SiteKey=marker[..25].ToLowerInvariant() });
      foreach(var countId in countIds)
        await bootstrap.ExecuteAsync("""
DELETE attachment FROM logistica.PhysicalCountAttachment attachment JOIN logistica.PhysicalCountLine line ON line.Id=attachment.PhysicalCountLineId WHERE line.SessionId=@Id;
DELETE FROM logistica.PhysicalCountLine WHERE SessionId=@Id;
DELETE FROM logistica.PhysicalCountSessionMaterial WHERE SessionId=@Id;
DELETE FROM logistica.PhysicalCountSession WHERE Id=@Id AND Notes=@Marker;
""",new { Id=countId,Marker=marker });
      foreach(var balanceId in balanceIds)
        await bootstrap.ExecuteAsync("DELETE logistica.StockTransaction WHERE StockBalanceId=@Id; DELETE logistica.StockBalance WHERE Id=@Id;",new { Id=balanceId });
      if(materialId>0)
        await bootstrap.ExecuteAsync("DELETE logistica.Material WHERE Id=@Id AND MaterialCode=@Code;",new { Id=materialId,Code=marker[..20] });
      foreach(var id in idsA.AsEnumerable().Reverse())
        await bootstrap.ExecuteAsync("DELETE logistica.Location WHERE Id=@Id AND LocationName LIKE @Marker",new { Id=id,Marker=marker+"%" });
      await bootstrap.ExecuteAsync("EXEC sys.sp_set_session_context @key=N'OrionRfc',@value=@Rfc",new { Rfc=b.CompanyRfc });
      foreach(var id in idsB.AsEnumerable().Reverse())
        await bootstrap.ExecuteAsync("DELETE logistica.Location WHERE Id=@Id AND LocationName LIKE @Marker",new { Id=id,Marker=marker+"%" });
      await HospitalityConnectionFactory.InitializeAsync(bootstrap,a);
      await bootstrap.ExecuteAsync("DELETE dbo.ROOM WHERE ID=@Id AND ROOM_NAME=@Marker",new { Id=roomId,Marker=marker });
    }
  }

  private static IEnumerable<int> Flatten(IEnumerable<LocationTreeNodeDto> nodes)
    => nodes.SelectMany(node=>new[] { node.Id }.Concat(Flatten(node.Children)));
  private sealed class CompanyRfc(string? rfc):ICurrentRfcAccessor { public string? CurrentRfc=>rfc; }
  private sealed class FixedScope(HospitalityScope scope):IHospitalityScopeAccessor
  { public Task<HospitalityScope> ResolveRequiredAsync(CancellationToken ct=default)=>Task.FromResult(scope); }
  private sealed class ScopeRow
  {
    public string PublicSiteKey {get;set;}="";
    public long CompanyId {get;set;}
    public long SiteId {get;set;}
    public string CompanyRfc {get;set;}="";
    public HospitalityScope Scope=>new(CompanyId,SiteId,CompanyRfc);
  }
}
