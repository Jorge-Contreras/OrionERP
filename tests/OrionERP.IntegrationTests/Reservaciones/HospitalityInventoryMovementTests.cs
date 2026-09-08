using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using OrionERP.Application.Common;
using OrionERP.Application.Features.Logistica.Stock;
using OrionERP.Application.Features.Reservaciones;
using OrionERP.Infrastructure.Features.Logistica.Locations;
using OrionERP.Infrastructure.Features.Logistica.Stock;
using OrionERP.Infrastructure.Features.Reservaciones;
using CompanyConnectionFactory=OrionERP.Infrastructure.Features.Cfdi.DescargaMasiva.Dapper.SqlConnectionFactory;

namespace OrionERP.IntegrationTests.Reservaciones;

public sealed class HospitalityInventoryMovementTests
{
  [Fact,Trait("Category","SqlIntegration")]
  public async Task MovementWorkspaceAndWrites_RequireVisibleLocationsIncludingOriginalIdempotentDocument()
  {
    if(Environment.GetEnvironmentVariable("ORION_RUN_SQL_INTEGRATION")!="1") return;
    var cs=new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable("ASPNETCORE_ConnectionStrings__OrionDb")??throw new InvalidOperationException("Missing Sandbox connection")) { InitialCatalog="Orion_Sandbox" }.ConnectionString;
    var cfg=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["ConnectionStrings:OrionDb"]=cs }).Build();
    await using var bootstrap=new SqlConnection(cs); await bootstrap.OpenAsync();
    Assert.Equal("Orion_Sandbox",await bootstrap.ExecuteScalarAsync<string>("SELECT DB_NAME()"),ignoreCase:true);
    var scope=await bootstrap.QuerySingleAsync<HospitalityScope>("SELECT p.CompanyId,p.SiteId,c.Rfc AS CompanyRfc FROM orion.PublicSite p JOIN orion.Company c ON c.CompanyId=p.CompanyId WHERE p.PublicSiteKey='bonhomia-main'");
    var otherRfc=await bootstrap.ExecuteScalarAsync<string>("SELECT c.Rfc FROM orion.PublicSite p JOIN orion.Company c ON c.CompanyId=p.CompanyId WHERE p.PublicSiteKey='brunos-main'");
    var factory=new CompanyConnectionFactory(cfg,new CompanyRfc(scope.CompanyRfc));
    var scoped=new FixedScope(scope);
    var locations=new LocationService(factory,scoped);
    var movements=new InventoryMovementService(factory,scoped);
    var general=new InventoryMovementService(factory);
    var marker="IMRLS"+Guid.NewGuid().ToString("N")[..12];
    var locationIds=new List<int>(); int room=0,material=0;
    try
    {
      await HospitalityConnectionFactory.InitializeAsync(bootstrap,scope);
      await bootstrap.ExecuteAsync("EXEC sys.sp_set_session_context @key=N'OrionRfc',@value=@Rfc",new { Rfc=scope.CompanyRfc });
      room=await bootstrap.ExecuteScalarAsync<int>("INSERT dbo.ROOM(ROOM_NAME,ROOM_TYPE) VALUES(@Marker,'SUITE'); SELECT CONVERT(int,SCOPE_IDENTITY());",new { Marker=marker });
      var roomLocation=await locations.SaveLocationAsync(new() { LocationName=marker+"-private",RoomId=room });
      Assert.True(roomLocation.Success,roomLocation.Message); locationIds.Add(roomLocation.EntityId!.Value);
      var target=await locations.SaveLocationAsync(new() { LocationName=marker+"-general" });
      Assert.True(target.Success,target.Message); locationIds.Add(target.EntityId!.Value);
      var secondTarget=await locations.SaveLocationAsync(new() { LocationName=marker+"-general2" });
      Assert.True(secondTarget.Success,secondTarget.Message); locationIds.Add(secondTarget.EntityId!.Value);
      material=await bootstrap.ExecuteScalarAsync<int>("INSERT logistica.Material(MaterialCode,[Description],BaseUnitId) SELECT @Marker,@Marker,MIN(Id) FROM logistica.UnitOfMeasure; SELECT CONVERT(int,SCOPE_IDENTITY());",new { Marker=marker });
      await bootstrap.ExecuteAsync("INSERT logistica.StockBalance(LocationId,MaterialId,Quantity,ReservedQuantity,AverageUnitCost) VALUES(@LocationId,@MaterialId,10,0,1)",new { LocationId=roomLocation.EntityId,MaterialId=material });
      var workspace=await movements.GetWorkspaceAsync(scope.CompanyRfc);
      Assert.Contains(workspace.Balances,x=>x.LocationId==roomLocation.EntityId && x.MaterialId==material);
      var generalWorkspace=await general.GetWorkspaceAsync(scope.CompanyRfc);
      Assert.DoesNotContain(generalWorkspace.Locations,x=>x.Id==roomLocation.EntityId);
      Assert.DoesNotContain(generalWorkspace.Balances,x=>x.MaterialId==material);
      Assert.Equal(51935,(await Assert.ThrowsAsync<SqlException>(()=>general.GetWorkspaceAsync(otherRfc!))).Number);

      var transfer=new InventoryTransferCreateRequest { Rfc=scope.CompanyRfc,TransferCode=marker+"-T",Reason=marker,FromLocationId=roomLocation.EntityId.Value,ToLocationId=target.EntityId.Value,Lines=[new() { MaterialId=material,Quantity=2 }] };
      Assert.Equal(51932,(await Assert.ThrowsAsync<SqlException>(()=>general.PostTransferAsync(transfer,"Sandbox test"))).Number);
      Assert.True((await movements.PostTransferAsync(transfer,"Sandbox test")).Success);
      Assert.True((await movements.PostTransferAsync(transfer,"Sandbox test")).Success);
      transfer.FromLocationId=target.EntityId.Value; transfer.ToLocationId=secondTarget.EntityId.Value;
      Assert.Equal(51932,(await Assert.ThrowsAsync<SqlException>(()=>general.PostTransferAsync(transfer,"Sandbox test"))).Number);
      transfer.TransferCode=marker+"-G";
      Assert.True((await general.PostTransferAsync(transfer,"Sandbox test")).Success);

      var adjustment=new InventoryAdjustmentCreateRequest { Rfc=scope.CompanyRfc,AdjustmentCode=marker+"-A",ReasonCode="TEST",Reason=marker,AuthorizedBy="Sandbox test",EvidenceFileName="fixture.txt",Evidence=[1,2,3],Lines=[new() { MaterialId=material,LocationId=roomLocation.EntityId.Value,QuantityDelta=1 }] };
      Assert.Equal(51932,(await Assert.ThrowsAsync<SqlException>(()=>general.PostAdjustmentAsync(adjustment,"Sandbox test"))).Number);
      Assert.True((await movements.PostAdjustmentAsync(adjustment,"Sandbox test")).Success);
      adjustment.Lines[0].LocationId=target.EntityId.Value;
      Assert.Equal(51932,(await Assert.ThrowsAsync<SqlException>(()=>general.PostAdjustmentAsync(adjustment,"Sandbox test"))).Number);
      adjustment.AdjustmentCode=marker+"-B";
      Assert.True((await general.PostAdjustmentAsync(adjustment,"Sandbox test")).Success);
    }
    finally
    {
      await bootstrap.ExecuteAsync("""
        DELETE logistica.StockTransaction WHERE MaterialId=@MaterialId;
        DELETE logistica.InventoryTransferLine WHERE MaterialId=@MaterialId;
        DELETE logistica.InventoryAdjustmentLine WHERE MaterialId=@MaterialId;
        DELETE logistica.InventoryTransfer WHERE TransferCode IN (@TransferCode,@GeneralCode);
        DELETE logistica.InventoryAdjustment WHERE AdjustmentCode IN (@AdjustmentCode,@GeneralAdjustmentCode);
        DELETE logistica.StockBalance WHERE MaterialId=@MaterialId;
        DELETE logistica.Material WHERE Id=@MaterialId AND MaterialCode=@Marker;
        """,new { MaterialId=material,Marker=marker,TransferCode=marker+"-T",GeneralCode=marker+"-G",AdjustmentCode=marker+"-A",GeneralAdjustmentCode=marker+"-B" });
      foreach(var id in locationIds.AsEnumerable().Reverse()) await bootstrap.ExecuteAsync("DELETE logistica.Location WHERE Id=@Id AND LocationName LIKE @Marker",new { Id=id,Marker=marker+"%" });
      await bootstrap.ExecuteAsync("DELETE dbo.ROOM WHERE ID=@Id AND ROOM_NAME=@Marker",new { Id=room,Marker=marker });
    }
  }
  private sealed class CompanyRfc(string rfc):ICurrentRfcAccessor { public string CurrentRfc=>rfc; }
  private sealed class FixedScope(HospitalityScope scope):IHospitalityScopeAccessor
  { public Task<HospitalityScope> ResolveRequiredAsync(CancellationToken ct=default)=>Task.FromResult(scope); }
}
