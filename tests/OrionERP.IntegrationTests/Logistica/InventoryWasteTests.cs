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

namespace OrionERP.IntegrationTests.Logistica;

public sealed class InventoryWasteTests
{
  [Fact,Trait("Category","SqlIntegration")]
  public async Task WasteMovesStockImmediatelyAndOnlyAReversalPutsItBack()
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
    var waste=new WasteService(factory,scoped);
    var unscoped=new WasteService(factory);
    var cook=new WasteActor("cocinero.sandbox",IsSupervisor:false);
    var supervisor=new WasteActor("supervisor.sandbox",IsSupervisor:true);
    var marker="WASTE"+Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
    var locationIds=new List<int>(); int room=0,material=0;
    try
    {
      await HospitalityConnectionFactory.InitializeAsync(bootstrap,scope);
      await bootstrap.ExecuteAsync("EXEC sys.sp_set_session_context @key=N'OrionRfc',@value=@Rfc",new { Rfc=scope.CompanyRfc });
      room=await bootstrap.ExecuteScalarAsync<int>("INSERT dbo.ROOM(ROOM_NAME,ROOM_TYPE) VALUES(@Marker,'SUITE'); SELECT CONVERT(int,SCOPE_IDENTITY());",new { Marker=marker });
      var roomLocation=await locations.SaveLocationAsync(new() { LocationName=marker+"-private",RoomId=room });
      Assert.True(roomLocation.Success,roomLocation.Message); locationIds.Add(roomLocation.EntityId!.Value);
      material=await bootstrap.ExecuteScalarAsync<int>("INSERT logistica.Material(MaterialCode,[Description],BaseUnitId) SELECT @Marker,@Marker,MIN(Id) FROM logistica.UnitOfMeasure; SELECT CONVERT(int,SCOPE_IDENTITY());",new { Marker=marker });
      await bootstrap.ExecuteAsync("INSERT logistica.StockBalance(LocationId,MaterialId,Quantity,ReservedQuantity,AverageUnitCost) VALUES(@LocationId,@MaterialId,10,2,7)",new { LocationId=roomLocation.EntityId,MaterialId=material });
      var location=roomLocation.EntityId!.Value;
      var balanceArgs=new { LocationId=location,MaterialId=material };
      Task<decimal> OnHandAsync()=>bootstrap.ExecuteScalarAsync<decimal>("SELECT Quantity FROM logistica.StockBalance WHERE LocationId=@LocationId AND MaterialId=@MaterialId",balanceArgs);
      Task<string> StatusAsync(string code)=>bootstrap.ExecuteScalarAsync<string>("SELECT [Status] FROM logistica.InventoryAdjustment WHERE AdjustmentCode=@Code",new { Code=code })!;
      WasteCreateRequest Request(string code,decimal quantity,string reason=WasteReasonCatalog.Expired)=>new()
      {
        Rfc=scope.CompanyRfc,WasteCode=code,ReasonCode=reason,Reason="Prueba de sandbox",
        OccurredOn=DateOnly.FromDateTime(DateTime.Now),Evidence=[1,2,3],EvidenceFileName="evidencia.png",
        Lines=[new() { LocationId=location,MaterialId=material,Quantity=quantity }]
      };

      // El espacio de trabajo sólo muestra lo que la sesión puede ver.
      Assert.Contains((await waste.GetWorkspaceAsync(scope.CompanyRfc)).Balances,item=>item.LocationId==location && item.MaterialId==material);
      Assert.DoesNotContain((await unscoped.GetWorkspaceAsync(scope.CompanyRfc)).Balances,item=>item.MaterialId==material);
      Assert.Equal(51935,(await Assert.ThrowsAsync<SqlException>(()=>unscoped.GetWorkspaceAsync(otherRfc!))).Number);

      // Reglas de captura: motivo del catálogo, nota cuando el motivo no se explica solo,
      // evidencia, fecha no futura, disponible real y alcance de ubicación.
      Assert.False((await waste.PostAsync(Request(marker+"-X",1,"INVENTADO"),cook)).Success);
      var opaque=Request(marker+"-X",1,WasteReasonCatalog.Other); opaque.Reason=string.Empty;
      Assert.False((await waste.PostAsync(opaque,cook)).Success);
      var noEvidence=Request(marker+"-X",1); noEvidence.Evidence=[];
      Assert.False((await waste.PostAsync(noEvidence,cook)).Success);
      var future=Request(marker+"-X",1); future.OccurredOn=DateOnly.FromDateTime(DateTime.Now).AddDays(1);
      Assert.False((await waste.PostAsync(future,cook)).Success);
      var tooOld=Request(marker+"-X",1); tooOld.OccurredOn=DateOnly.FromDateTime(DateTime.Now).AddDays(-30);
      Assert.False((await waste.PostAsync(tooOld,cook)).Success);
      // Disponible es 10 menos 2 reservadas: 9 no alcanza.
      Assert.Contains("disponible",(await Assert.ThrowsAsync<InvalidOperationException>(()=>waste.PostAsync(Request(marker+"-X",9),cook))).Message);
      Assert.Equal(51932,(await Assert.ThrowsAsync<SqlException>(()=>unscoped.PostAsync(Request(marker+"-X",1),cook))).Number);
      Assert.Equal(10m,await OnHandAsync());

      // La baja de quien no es supervisor se aplica de inmediato y queda esperando revisión.
      var posted=await waste.PostAsync(Request(marker+"-A",3),cook);
      Assert.True(posted.Success,posted.Message);
      Assert.Equal(7m,await OnHandAsync());
      Assert.Equal(WasteStatuses.PendingReview,await StatusAsync(marker+"-A"));
      Assert.Equal(-3m,await bootstrap.ExecuteScalarAsync<decimal>("SELECT line.QuantityDelta FROM logistica.InventoryAdjustment doc JOIN logistica.InventoryAdjustmentLine line ON line.AdjustmentId=doc.Id WHERE doc.AdjustmentCode=@Code",new { Code=marker+"-A" }));
      Assert.Equal(1,await bootstrap.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM logistica.StockTransaction WHERE MaterialId=@MaterialId AND TransactionType='Waste' AND QuantityDelta=-3 AND QuantityAfter=7",new { MaterialId=material }));

      // Reenviar el mismo folio no vuelve a descontar.
      Assert.True((await waste.PostAsync(Request(marker+"-A",3),cook)).Success);
      Assert.Equal(7m,await OnHandAsync());

      var pending=await waste.GetHistoryAsync(new() { Rfc=scope.CompanyRfc,Status=WasteStatuses.PendingReview });
      var document=Assert.Single(pending.Documents,item=>item.WasteCode==marker+"-A");
      Assert.True(document.CanReview);
      Assert.True(document.CanReverse);
      Assert.True(document.HasEvidence);
      Assert.Equal(21m,document.TotalCost);
      Assert.Equal(3m,Assert.Single(document.Lines).Quantity);
      Assert.Equal(3,(await waste.GetEvidenceAsync(scope.CompanyRfc,document.Id))!.Content.Length);
      Assert.Equal(1,(await waste.GetWorkspaceAsync(scope.CompanyRfc)).PendingReviewCount);

      // Nadie cierra su propia revisión, y una vez cerrada no se vuelve a cerrar.
      Assert.False((await waste.ApproveAsync(scope.CompanyRfc,document.Id,cook.UserName)).Success);
      Assert.True((await waste.ApproveAsync(scope.CompanyRfc,document.Id,supervisor.UserName)).Success);
      Assert.False((await waste.ApproveAsync(scope.CompanyRfc,document.Id,supervisor.UserName)).Success);

      // La reversa exige motivo, devuelve la cantidad y el valor exactos, y ocurre una sola vez.
      Assert.False((await waste.ReverseAsync(new() { Rfc=scope.CompanyRfc,AdjustmentId=document.Id,Reason=" " },supervisor.UserName)).Success);
      var reversed=await waste.ReverseAsync(new() { Rfc=scope.CompanyRfc,AdjustmentId=document.Id,Reason="Se capturó de más" },supervisor.UserName);
      Assert.True(reversed.Success,reversed.Message);
      Assert.Equal(10m,await OnHandAsync());
      Assert.Equal(7m,await bootstrap.ExecuteScalarAsync<decimal>("SELECT line.FrozenUnitCost FROM logistica.InventoryAdjustment doc JOIN logistica.InventoryAdjustmentLine line ON line.AdjustmentId=doc.Id WHERE doc.AdjustmentCode=@Code",new { Code=marker+"-A-R" }));
      Assert.Equal(1,await bootstrap.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM logistica.StockTransaction WHERE MaterialId=@MaterialId AND TransactionType='WasteReversal' AND QuantityDelta=3 AND QuantityAfter=10",new { MaterialId=material }));
      Assert.Equal(WasteStatuses.Reversed,await StatusAsync(marker+"-A"));
      Assert.False((await waste.ReverseAsync(new() { Rfc=scope.CompanyRfc,AdjustmentId=document.Id,Reason="Otra vez" },supervisor.UserName)).Success);

      // Una reversa no se reversa, y el indicador neto ya no cuenta ninguna de las dos.
      var reversalId=await bootstrap.ExecuteScalarAsync<long>("SELECT Id FROM logistica.InventoryAdjustment WHERE AdjustmentCode=@Code",new { Code=marker+"-A-R" });
      Assert.False((await waste.ReverseAsync(new() { Rfc=scope.CompanyRfc,AdjustmentId=reversalId,Reason="No debe poder" },supervisor.UserName)).Success);
      var afterReversal=await waste.GetWorkspaceAsync(scope.CompanyRfc);
      Assert.Equal(0,afterReversal.PendingReviewCount);
      Assert.Equal(0m,afterReversal.TodayCost);

      // La captura de un supervisor entra aprobada y no pasa por la bandeja.
      var direct=await waste.PostAsync(Request(marker+"-B",1,WasteReasonCatalog.Damage),supervisor);
      Assert.True(direct.Success,direct.Message);
      Assert.Equal(WasteStatuses.Approved,await StatusAsync(marker+"-B"));
      Assert.Equal(9m,await OnHandAsync());
      Assert.Equal(7m,(await waste.GetWorkspaceAsync(scope.CompanyRfc)).TodayCost);

      // Ni el historial ni la evidencia cruzan el alcance de ubicación.
      Assert.Empty((await unscoped.GetHistoryAsync(new() { Rfc=scope.CompanyRfc })).Documents);
      Assert.Null(await unscoped.GetEvidenceAsync(scope.CompanyRfc,document.Id));
    }
    finally
    {
      await bootstrap.ExecuteAsync("""
        DELETE logistica.StockTransaction WHERE MaterialId=@MaterialId;
        DELETE logistica.InventoryAdjustmentLine WHERE MaterialId=@MaterialId;
        UPDATE logistica.InventoryAdjustment SET ReversedByAdjustmentId=NULL,ReversalOfAdjustmentId=NULL WHERE AdjustmentCode LIKE @Codes;
        DELETE logistica.InventoryAdjustment WHERE AdjustmentCode LIKE @Codes;
        DELETE logistica.StockBalance WHERE MaterialId=@MaterialId;
        DELETE logistica.Material WHERE Id=@MaterialId AND MaterialCode=@Marker;
        """,new { MaterialId=material,Marker=marker,Codes=marker+"%" });
      foreach(var id in locationIds.AsEnumerable().Reverse()) await bootstrap.ExecuteAsync("DELETE logistica.Location WHERE Id=@Id AND LocationName LIKE @Marker",new { Id=id,Marker=marker+"%" });
      await bootstrap.ExecuteAsync("DELETE dbo.ROOM WHERE ID=@Id AND ROOM_NAME=@Marker",new { Id=room,Marker=marker });
    }
  }
  private sealed class CompanyRfc(string rfc):ICurrentRfcAccessor { public string CurrentRfc=>rfc; }
  private sealed class FixedScope(HospitalityScope scope):IHospitalityScopeAccessor
  { public Task<HospitalityScope> ResolveRequiredAsync(CancellationToken ct=default)=>Task.FromResult(scope); }
}
