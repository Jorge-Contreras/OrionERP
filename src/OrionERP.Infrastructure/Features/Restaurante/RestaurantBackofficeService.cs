using System.Data;
using System.Data.Common;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using OrionERP.Application.Common;
using OrionERP.Application.Features.Logistica.Shared;
using OrionERP.Application.Features.Restaurante;

namespace OrionERP.Infrastructure.Features.Restaurante;

public sealed class RestaurantBackofficeService : IRestaurantBackofficeService
{
  private readonly IDbConnectionFactory _connectionFactory;
  public RestaurantBackofficeService(IDbConnectionFactory connectionFactory)=>_connectionFactory=connectionFactory??throw new ArgumentNullException(nameof(connectionFactory));

  public async Task<RestaurantReportDto> GetReportAsync(string rfc,int siteId,DateTime from,DateTime to,CancellationToken ct=default)
  {
    var normalizedRfc=LogisticsRfc.Require(rfc);var start=from.Date;var end=to.Date.AddDays(1);
    if(end<=start)throw new InvalidOperationException("El rango del reporte no es válido.");
    const string sql=
      """
      SELECT COUNT(*) AS OrderCount,
        CAST(ISNULL(SUM(CASE WHEN PaymentStatus='Paid' THEN Total ELSE 0 END),0) AS decimal(18,2)) AS NetSales,
        CAST(ISNULL(SUM(CASE WHEN PaymentStatus='Paid' THEN TaxTotal ELSE 0 END),0) AS decimal(18,2)) AS TaxTotal,
        CAST(ISNULL(SUM(CASE WHEN PaymentStatus='Paid' THEN DiscountTotal ELSE 0 END),0) AS decimal(18,2)) AS DiscountTotal,
        CAST(ISNULL(SUM(CASE WHEN PaymentStatus='Paid' THEN TipTotal ELSE 0 END),0) AS decimal(18,2)) AS TipTotal,
        CAST(ISNULL(SUM(CASE WHEN PaymentStatus='Paid' THEN TheoreticalCost ELSE 0 END),0) AS decimal(18,2)) AS TheoreticalCost,
        CAST(ISNULL(SUM(CASE WHEN PaymentStatus='PendingSettlement' THEN BalanceDue ELSE 0 END),0) AS decimal(18,2)) AS PendingSettlement,
        CAST(ISNULL(AVG(CASE WHEN PaymentStatus='Paid' THEN Total END),0) AS decimal(18,2)) AS AverageTicket
      FROM restaurante.[Order] WHERE Rfc=@Rfc AND SiteId=@SiteId AND OperationalDate>=@Start AND OperationalDate<@End AND [Status]<>'Cancelled';
      SELECT PaymentMethod AS Label,CAST(SUM(Amount-RefundedAmount) AS decimal(18,2)) AS Amount
      FROM restaurante.Payment paymentInfo JOIN restaurante.[Order] orderInfo ON orderInfo.Rfc=paymentInfo.Rfc AND orderInfo.Id=paymentInfo.OrderId
      WHERE paymentInfo.Rfc=@Rfc AND orderInfo.SiteId=@SiteId AND orderInfo.OperationalDate>=@Start AND orderInfo.OperationalDate<@End
        AND paymentInfo.[Status] IN ('Paid','PartiallyRefunded','Refunded')
      GROUP BY PaymentMethod ORDER BY Amount DESC;
      SELECT TOP(12) lineInfo.ProductNameSnapshot AS ProductName,CAST(SUM(lineInfo.Quantity) AS decimal(18,2)) AS Quantity,
        CAST(SUM(lineInfo.LineTotal) AS decimal(18,2)) AS Sales
      FROM restaurante.OrderLine lineInfo JOIN restaurante.[Order] orderInfo ON orderInfo.Rfc=lineInfo.Rfc AND orderInfo.Id=lineInfo.OrderId
      WHERE lineInfo.Rfc=@Rfc AND orderInfo.SiteId=@SiteId AND orderInfo.OperationalDate>=@Start AND orderInfo.OperationalDate<@End
        AND orderInfo.PaymentStatus='Paid' AND lineInfo.[Status]<>'Cancelled'
        AND lineInfo.LineKind<>'ComboComponent'
      GROUP BY lineInfo.ProductNameSnapshot ORDER BY Sales DESC,Quantity DESC;
      SELECT OperationalDate,COUNT(*) AS OrderCount,CAST(SUM(Total) AS decimal(18,2)) AS Sales,CAST(SUM(TheoreticalCost) AS decimal(18,2)) AS Cost
      FROM restaurante.[Order] WHERE Rfc=@Rfc AND SiteId=@SiteId AND OperationalDate>=@Start AND OperationalDate<@End AND PaymentStatus='Paid' AND [Status]<>'Cancelled'
      GROUP BY OperationalDate ORDER BY OperationalDate;
      """;
    using var conn=CreateConnection();using var multi=await conn.QueryMultipleAsync(new CommandDefinition(sql,new{Rfc=normalizedRfc,SiteId=siteId,Start=start,End=end},cancellationToken:ct));
    var report=await multi.ReadSingleAsync<RestaurantReportDto>();report.PaymentMethods=(await multi.ReadAsync<RestaurantReportBreakdownDto>()).AsList();report.TopProducts=(await multi.ReadAsync<RestaurantTopProductDto>()).AsList();report.DailySales=(await multi.ReadAsync<RestaurantDailySalesDto>()).AsList();return report;
  }

  public async Task<IReadOnlyList<RestaurantSettlementCandidateDto>> GetSettlementCandidatesAsync(string rfc,int siteId,CancellationToken ct=default)
  {
    const string sql=
      """
      SELECT orderInfo.Id AS OrderId,orderInfo.Folio,orderInfo.OperationalDate,delivery.ExternalProviderId,provider.[Name] AS ProviderName,
        delivery.ExternalReference,orderInfo.Total AS GrossAmount,delivery.CommissionAmount,
        orderInfo.Total-delivery.CommissionAmount AS NetAmount,delivery.DeliveredAt
      FROM restaurante.[Order] orderInfo JOIN restaurante.Delivery delivery ON delivery.Rfc=orderInfo.Rfc AND delivery.OrderId=orderInfo.Id
      JOIN restaurante.ExternalProvider provider ON provider.Rfc=delivery.Rfc AND provider.Id=delivery.ExternalProviderId
      WHERE orderInfo.Rfc=@Rfc AND orderInfo.SiteId=@SiteId AND orderInfo.PaymentStatus='PendingSettlement'
        AND orderInfo.[Status]='Delivered' AND delivery.SettledAt IS NULL
      ORDER BY provider.[Name],delivery.DeliveredAt,orderInfo.Folio;
      """;
    using var conn=CreateConnection();return(await conn.QueryAsync<RestaurantSettlementCandidateDto>(new CommandDefinition(sql,new{Rfc=LogisticsRfc.Require(rfc),SiteId=siteId},cancellationToken:ct))).AsList();
  }

  public async Task<IReadOnlyList<RestaurantProviderSettlementDto>> GetSettlementsAsync(string rfc,int siteId,CancellationToken ct=default)
  {
    const string sql=
      """
      SELECT settlement.Id,settlement.SettlementCode,provider.[Name] AS ProviderName,settlement.[Status],settlement.GrossAmount,
        settlement.CommissionAmount,settlement.NetAmount,settlement.SettledAt,settlement.CreatedAt,COUNT(lines.OrderId) AS OrderCount
      FROM restaurante.ProviderSettlement settlement JOIN restaurante.ExternalProvider provider ON provider.Rfc=settlement.Rfc AND provider.Id=settlement.ExternalProviderId
      LEFT JOIN restaurante.ProviderSettlementOrder lines ON lines.Rfc=settlement.Rfc AND lines.SettlementId=settlement.Id
      WHERE settlement.Rfc=@Rfc AND settlement.SiteId=@SiteId
      GROUP BY settlement.Id,settlement.SettlementCode,provider.[Name],settlement.[Status],settlement.GrossAmount,settlement.CommissionAmount,settlement.NetAmount,settlement.SettledAt,settlement.CreatedAt
      ORDER BY settlement.CreatedAt DESC;
      """;
    using var conn=CreateConnection();return(await conn.QueryAsync<RestaurantProviderSettlementDto>(new CommandDefinition(sql,new{Rfc=LogisticsRfc.Require(rfc),SiteId=siteId},cancellationToken:ct))).AsList();
  }

  public async Task<RestaurantCommandResult> CreateSettlementAsync(RestaurantSettlementCreateRequest request,string userName,CancellationToken ct=default)
  {
    ArgumentNullException.ThrowIfNull(request);var rfc=LogisticsRfc.Require(request.Rfc);var ids=request.OrderIds.Distinct().ToArray();
    if(ids.Length==0||string.IsNullOrWhiteSpace(request.SettlementCode))return RestaurantCommandResult.Fail("Selecciona órdenes e indica un código de liquidación.");
    using var conn=CreateConnection();await conn.OpenAsync(ct);await using var tx=await conn.BeginTransactionAsync(IsolationLevel.Serializable,ct);
    try
    {
      var rows=(await conn.QueryAsync<SettlementOrderRow>(new CommandDefinition(
        """
        SELECT orderInfo.Id,orderInfo.Total AS GrossAmount,delivery.CommissionAmount,delivery.ExternalProviderId
        FROM restaurante.[Order] orderInfo WITH (UPDLOCK,HOLDLOCK)
        JOIN restaurante.Delivery delivery ON delivery.Rfc=orderInfo.Rfc AND delivery.OrderId=orderInfo.Id
        WHERE orderInfo.Rfc=@Rfc AND orderInfo.SiteId=@SiteId AND orderInfo.Id IN @Ids
          AND orderInfo.PaymentStatus='PendingSettlement' AND orderInfo.[Status]='Delivered' AND delivery.SettledAt IS NULL;
        """,new{Rfc=rfc,request.SiteId,Ids=ids},tx,cancellationToken:ct))).AsList();
      if(rows.Count!=ids.Length||rows.Select(x=>x.ExternalProviderId).Distinct().Count()!=1){await tx.RollbackAsync(ct);return RestaurantCommandResult.Fail("Todas las órdenes deben estar entregadas, pendientes y pertenecer al mismo proveedor/RFC.");}
      var settlementId=Guid.NewGuid();var gross=rows.Sum(x=>x.GrossAmount);var commission=rows.Sum(x=>x.CommissionAmount);var net=gross-commission;
      await conn.ExecuteAsync(new CommandDefinition(
        """
        INSERT INTO restaurante.ProviderSettlement (Id,Rfc,SiteId,ExternalProviderId,SettlementCode,[Status],GrossAmount,CommissionAmount,NetAmount,SettledAt,CreatedBy)
        VALUES (@Id,@Rfc,@SiteId,@ProviderId,@Code,'Settled',@Gross,@Commission,@Net,SYSUTCDATETIME(),@UserName);
        """,new{Id=settlementId,Rfc=rfc,request.SiteId,ProviderId=rows[0].ExternalProviderId,Code=request.SettlementCode.Trim().ToUpperInvariant(),Gross=gross,Commission=commission,Net=net,UserName=userName},tx,cancellationToken:ct));
      foreach(var row in rows)
      {
        await conn.ExecuteAsync(new CommandDefinition(
          """
          INSERT INTO restaurante.ProviderSettlementOrder (Rfc,SettlementId,OrderId,GrossAmount,CommissionAmount,NetAmount)
          VALUES (@Rfc,@SettlementId,@OrderId,@Gross,@Commission,@Net);
          INSERT INTO restaurante.Payment (Id,Rfc,OrderId,PaymentMethod,Amount,TipAmount,[Status],ExternalReference,IdempotencyKey,ReceivedBy)
          VALUES (NEWID(),@Rfc,@OrderId,'Platform',@Gross,0,'Paid',@Code,CONCAT('SETTLEMENT:',CONVERT(varchar(36),@SettlementId),':',CONVERT(varchar(36),@OrderId)),@UserName);
          UPDATE restaurante.[Order] SET PaymentStatus='Paid',BalanceDue=0,PaidAt=SYSUTCDATETIME(),[Status]='Completed',CompletedAt=SYSUTCDATETIME() WHERE Rfc=@Rfc AND Id=@OrderId;
          UPDATE restaurante.Delivery SET [Status]='Settled',SettledAt=SYSUTCDATETIME() WHERE Rfc=@Rfc AND OrderId=@OrderId;
          INSERT INTO restaurante.EventOutbox (Rfc,SiteId,EventType,AggregateId,Payload)
          VALUES (@Rfc,@SiteId,'OrderSettled',CONVERT(varchar(36),@OrderId),@Payload);
          """,new{Rfc=rfc,SettlementId=settlementId,OrderId=row.Id,Gross=row.GrossAmount,Commission=row.CommissionAmount,Net=row.GrossAmount-row.CommissionAmount,Code=request.SettlementCode.Trim(),UserName=userName,request.SiteId,Payload=JsonSerializer.Serialize(new{orderId=row.Id,settlementId})},tx,cancellationToken:ct));
        await RestaurantOrderEventWriter.AddAsync(
          conn,tx,rfc,request.SiteId,row.Id,
          "PaymentReceived","Payment","Pago de plataforma recibido",
          $"{request.SettlementCode.Trim()} · {row.GrossAmount:C}",userName,ct,
          $"settlement:{settlementId}:{row.Id}:payment");
        await RestaurantOrderEventWriter.AddAsync(
          conn,tx,rfc,request.SiteId,row.Id,
          "OrderSettled","Delivery","Liquidación de plataforma completada",
          $"{request.SettlementCode.Trim()} · Comisión {row.CommissionAmount:C} · Neto {row.GrossAmount-row.CommissionAmount:C}",
          userName,ct,$"settlement:{settlementId}:{row.Id}:completed");
      }
      await tx.CommitAsync(ct);return RestaurantCommandResult.Ok($"Liquidación registrada: {rows.Count} orden(es), neto {net:C}.");
    }
    catch(SqlException ex)when(ex.Number is 2601 or 2627){await tx.RollbackAsync(ct);return RestaurantCommandResult.Fail("El código de liquidación ya existe o una orden ya fue liquidada.");}
    catch{await tx.RollbackAsync(ct);throw;}
  }

  private DbConnection CreateConnection()=>_connectionFactory.Create() as DbConnection??throw new InvalidOperationException("La fábrica no devolvió una DbConnection.");
  private sealed class SettlementOrderRow{public Guid Id{get;set;}public decimal GrossAmount{get;set;}public decimal CommissionAmount{get;set;}public int ExternalProviderId{get;set;}}
}
