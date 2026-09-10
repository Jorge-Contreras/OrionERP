using System.Data.Common;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging;
using OrionERP.Application.Common;
using OrionERP.Application.Features.Contabilidad.Transacciones;
using OrionERP.Application.Features.Logistica.Shared;
using OrionERP.Application.Features.Restaurante;

namespace OrionERP.Infrastructure.Features.Restaurante;

public sealed class RestaurantAccountingService : IRestaurantAccountingService
{
  private static readonly JsonSerializerOptions OutboxJsonOptions = new(JsonSerializerDefaults.Web);
  private readonly IDbConnectionFactory _connectionFactory;
  private readonly ITransaccionService _transactionService;
  private readonly IRestaurantScopeAccessor _scopeAccessor;
  private readonly IAccountingOutbox _outbox;
  private readonly ILogger<RestaurantAccountingService> _logger;

  public RestaurantAccountingService(
    IDbConnectionFactory connectionFactory,
    ITransaccionService transactionService,
    IRestaurantScopeAccessor scopeAccessor,
    IAccountingOutbox outbox,
    ILogger<RestaurantAccountingService> logger)
  {
    _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    _transactionService = transactionService ?? throw new ArgumentNullException(nameof(transactionService));
    _scopeAccessor = scopeAccessor ?? throw new ArgumentNullException(nameof(scopeAccessor));
    _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
    _logger = logger ?? throw new ArgumentNullException(nameof(logger));
  }

  /// <summary>
  /// Identidad única de la contabilización diaria. Determinista a propósito: dos
  /// reintentos de la misma fecha y sede son la misma operación, no dos.
  /// </summary>
  internal static string DailyOperationKey(int siteId, DateTime operationalDate)
    => $"DAILY:{siteId}:{operationalDate:yyyy-MM-dd}";

  public async Task<RestaurantAccountingPreviewDto> GetDailyPreviewAsync(
    string rfc,
    int siteId,
    DateTime operationalDate,
    CancellationToken ct = default)
  {
    var normalizedRfc = LogisticsRfc.Require(rfc);
    await _scopeAccessor.ResolveRequiredAsync(normalizedRfc, siteId, ct);
    var date = operationalDate.Date;
    const string sql =
      """
      SELECT CashAccount,CardBankAccount,TransferBankAccount,PlatformReceivableAccount,SalesAccount,VatAccount,DiscountAccount,
             InventoryAccount,CostOfSalesAccount,DailyPolicyEnabled
      FROM restaurante.AccountingConfiguration WHERE Rfc=@Rfc AND SiteId=@SiteId;

      SELECT TOP(1) TransactionId
      FROM restaurante.AccountingLink
      WHERE Rfc=@Rfc AND SiteId=@SiteId AND LinkType='DailyConsolidated' AND OperationalDate=@Date;

      SELECT COUNT(*) AS EligibleOrderCount,
             CAST(ISNULL(SUM(orderInfo.Total-orderInfo.TaxTotal),0) AS decimal(18,2)) AS Sales,
             CAST(ISNULL(SUM(orderInfo.TaxTotal),0) AS decimal(18,2)) AS Tax,
             CAST(ISNULL(SUM(orderInfo.DiscountTotal),0) AS decimal(18,2)) AS Discounts,
             CAST(ISNULL(SUM(orderInfo.TheoreticalCost),0) AS decimal(18,2)) AS Cost
      FROM restaurante.[Order] orderInfo
      WHERE orderInfo.Rfc=@Rfc AND orderInfo.SiteId=@SiteId AND orderInfo.OperationalDate=@Date
        AND orderInfo.PaymentStatus='Paid' AND orderInfo.[Status]<>'Cancelled'
        AND NOT EXISTS
        (
          SELECT 1 FROM restaurante.AccountingOrderLink linkInfo
          WHERE linkInfo.Rfc=orderInfo.Rfc AND linkInfo.OrderId=orderInfo.Id
        );

      SELECT paymentInfo.PaymentMethod AS Label,
             CAST(SUM(paymentInfo.Amount-paymentInfo.RefundedAmount) AS decimal(18,2)) AS Amount
      FROM restaurante.Payment paymentInfo
      JOIN restaurante.[Order] orderInfo ON orderInfo.Rfc=paymentInfo.Rfc AND orderInfo.Id=paymentInfo.OrderId
      WHERE orderInfo.Rfc=@Rfc AND orderInfo.SiteId=@SiteId AND orderInfo.OperationalDate=@Date
        AND orderInfo.PaymentStatus='Paid' AND orderInfo.[Status]<>'Cancelled'
        AND paymentInfo.Amount-paymentInfo.RefundedAmount>0
        AND NOT EXISTS
        (
          SELECT 1 FROM restaurante.AccountingOrderLink linkInfo
          WHERE linkInfo.Rfc=orderInfo.Rfc AND linkInfo.OrderId=orderInfo.Id
        )
      GROUP BY paymentInfo.PaymentMethod ORDER BY paymentInfo.PaymentMethod;
      """;
    using var conn = CreateConnection();
    using var multi = await conn.QueryMultipleAsync(new CommandDefinition(
      sql, new { Rfc = normalizedRfc, SiteId = siteId, Date = date }, cancellationToken: ct));
    var config = await multi.ReadSingleOrDefaultAsync<AccountingConfigurationRow>();
    var existing = await multi.ReadSingleOrDefaultAsync<int?>();
    var preview = await multi.ReadSingleAsync<RestaurantAccountingPreviewDto>();
    preview.OperationalDate = date;
    preview.ExistingTransactionId = existing;
    preview.Receipts = (await multi.ReadAsync<RestaurantReportBreakdownDto>()).AsList();
    preview.ConfigurationComplete = IsComplete(config, preview.Tax, preview.Discounts, preview.Cost, preview.Receipts);
    return preview;
  }

  public async Task<RestaurantCommandResult> GenerateDailyPolicyAsync(
    string rfc,
    int siteId,
    DateTime operationalDate,
    string userName,
    CancellationToken ct = default)
  {
    var normalizedRfc = LogisticsRfc.Require(rfc);
    var scope = await _scopeAccessor.ResolveRequiredAsync(normalizedRfc, siteId, ct);
    var date = operationalDate.Date;
    var preview = await GetDailyPreviewAsync(normalizedRfc, siteId, date, ct);
    if (preview.ExistingTransactionId.HasValue)
      return RestaurantCommandResult.Ok($"La póliza diaria ya existe: {preview.ExistingTransactionId}.");

    // Reclamar antes de trabajar: la identidad es única en la base, así que un
    // reintento concurrente no puede producir una segunda contabilización.
    var operation = await _outbox.ClaimAsync(
      AccountingOutboxModules.Restaurant,
      DailyOperationKey(siteId, date),
      $"{{\"module\":\"restaurant\",\"kind\":\"dailyConsolidated\",\"siteId\":{siteId},\"operationalDate\":\"{date:yyyy-MM-dd}\"}}",
      ct);
    if (operation.AlreadyCompleted)
      return RestaurantCommandResult.Ok($"La póliza diaria ya existe: {operation.TransaccionId}.");
    if (operation.InProgress)
      return RestaurantCommandResult.Fail("La póliza diaria ya se está generando. Espera un momento antes de reintentar.");

    if (preview.EligibleOrderCount == 0)
    {
      await _outbox.FailAsync(operation.Id, "No hay ventas liquidadas elegibles para esta fecha.", ct);
      return RestaurantCommandResult.Fail("No hay ventas liquidadas elegibles para esta fecha.");
    }
    if (!preview.ConfigurationComplete)
    {
      await _outbox.FailAsync(operation.Id, "Faltan cuentas contables de la sede.", ct);
      return RestaurantCommandResult.Fail("Completa las cuentas contables requeridas en Configuración operativa.");
    }

    using var conn = CreateConnection();
    var config = await LoadConfigurationAsync(conn, normalizedRfc, siteId, ct);
    var totals = new AccountingTotals
    {
      Sales = preview.Sales,
      Tax = preview.Tax,
      Discounts = preview.Discounts,
      Cost = preview.Cost,
      Receipts = preview.Receipts
    };
    var receiptTotal = totals.Receipts.Sum(receipt => receipt.Amount);
    if (Math.Abs(receiptTotal - (totals.Sales + totals.Tax)) > 0.02m)
    {
      // Un descuadre no publica, y la operación queda pendiente con su motivo.
      var imbalance = $"Los cobros ({receiptTotal:C}) no coinciden con las ventas netas e IVA ({totals.Sales + totals.Tax:C}); concilia pagos antes de generar la póliza.";
      await _outbox.FailAsync(operation.Id, imbalance, ct);
      return RestaurantCommandResult.Fail(imbalance);
    }

    // Recuperación entre pasos: si un intento anterior alcanzó a crear la póliza y no
    // a vincularla, se retoma esa misma póliza en vez de crear otra.
    var resumed = operation.ResumesFromExistingPolicy
      && await PolicyExistsAsync(conn, operation.TransaccionId!.Value, ct);
    var transactionId = resumed ? operation.TransaccionId!.Value : 0;
    if (!resumed)
    {
      if (operation.ResumesFromExistingPolicy) await _outbox.ForgetPolicyAsync(operation.Id, ct);
      var concept = $"VENTAS RESTAURANTE {date:yyyy-MM-dd} SEDE {siteId}";
      transactionId = await CreateClosedTransactionAsync(
        normalizedRfc, date, concept, receiptTotal, config, totals, false, false,
        $"Consolidado diario Restaurante; excluye CFDI individuales, propinas, cancelaciones, reembolsos y saldos pendientes. Usuario: {userName}", ct);
      // El rastro se graba antes de intentar el vínculo: eso es lo que hace
      // recuperable un fallo entre ambos pasos.
      await _outbox.RecordPolicyAsync(operation.Id, transactionId, ct);
    }
    try
    {
      var linkedOrders = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
        $"""
        SET XACT_ABORT ON;
        BEGIN TRANSACTION;
        {RestaurantScopeAccessor.EnsureEnabledSql}
        IF NOT EXISTS
        (
          SELECT 1 FROM restaurante.AccountingLink WITH (UPDLOCK,HOLDLOCK)
          WHERE Rfc=@Rfc AND SiteId=@SiteId AND OperationalDate=@Date AND LinkType='DailyConsolidated'
        )
          INSERT INTO restaurante.AccountingLink (Rfc,SiteId,OperationalDate,LinkType,TransactionId)
          VALUES (@Rfc,@SiteId,@Date,'DailyConsolidated',@TransactionId);

        INSERT INTO restaurante.AccountingOrderLink
          (Rfc,OrderId,SiteId,OperationalDate,LinkType,TransactionId)
        SELECT orderInfo.Rfc,orderInfo.Id,orderInfo.SiteId,orderInfo.OperationalDate,'DailyConsolidated',@TransactionId
        FROM restaurante.[Order] orderInfo WITH (UPDLOCK,HOLDLOCK)
        WHERE orderInfo.Rfc=@Rfc AND orderInfo.SiteId=@SiteId AND orderInfo.OperationalDate=@Date
          AND orderInfo.PaymentStatus='Paid' AND orderInfo.[Status]<>'Cancelled'
          AND NOT EXISTS
          (
            SELECT 1 FROM restaurante.AccountingOrderLink existing
            WHERE existing.Rfc=orderInfo.Rfc AND existing.OrderId=orderInfo.Id
          );
        DECLARE @Linked int=@@ROWCOUNT;

        INSERT INTO restaurante.OrderEvent
          (Rfc,SiteId,OrderId,EventType,Category,Title,[Description],Actor,SourceKey)
        SELECT linkInfo.Rfc,linkInfo.SiteId,linkInfo.OrderId,'AccountingLinked','Accounting',
               N'Orden incluida en póliza diaria',CONCAT(N'Póliza ',@TransactionId),@UserName,
               CONCAT('accounting:',CONVERT(varchar(36),linkInfo.OrderId),':DailyConsolidated:',@TransactionId)
        FROM restaurante.AccountingOrderLink linkInfo
        WHERE linkInfo.Rfc=@Rfc AND linkInfo.TransactionId=@TransactionId
          AND linkInfo.LinkType='DailyConsolidated';
        COMMIT TRANSACTION;
        SELECT @Linked;
        """, ScopedParameters(scope, new { Rfc = normalizedRfc, SiteId = siteId, Date = date, TransactionId = transactionId, UserName = userName }), cancellationToken: ct));
      if (linkedOrders == 0)
      {
        // Nadie más pudo estar en esta misma operación —la identidad es única—, así que
        // las ventas se fueron por otra vía y esta póliza sí quedaría huérfana.
        await _transactionService.DeleteTransaccionAsync(transactionId, ct);
        await _outbox.ForgetPolicyAsync(operation.Id, ct);
        await _outbox.FailAsync(operation.Id, "Las ventas fueron vinculadas por otra vía contable.", ct);
        return RestaurantCommandResult.Fail("No se generó la póliza: las ventas fueron vinculadas por otra vía contable.");
      }

      await _outbox.CompleteAsync(operation.Id, ct);
      return RestaurantCommandResult.Ok($"Póliza diaria {transactionId} generada y balanceada para {linkedOrders} venta(s).");
    }
    catch (Exception ex)
    {
      // La póliza NO se borra: ya está registrada en la bandeja y el reintento retoma
      // desde el vínculo. Borrarla aquí es justo lo que hacía este flujo no durable.
      await _outbox.FailAsync(operation.Id, ex.Message, ct);
      return RestaurantCommandResult.Fail(
        $"La póliza {transactionId} quedó creada pero sin vincular: {ex.Message} El reintento la retoma sin duplicarla.");
    }
  }

  private static Task<bool> PolicyExistsAsync(DbConnection conn, int transaccionId, CancellationToken ct)
    => conn.ExecuteScalarAsync<bool>(new CommandDefinition(
      "SELECT CONVERT(bit, CASE WHEN EXISTS (SELECT 1 FROM dbo.Transacciones WHERE ID=@Id) THEN 1 ELSE 0 END);",
      new { Id = transaccionId }, cancellationToken: ct));

  public async Task<RestaurantCommandResult> GenerateIndividualCfdiPolicyAsync(
    string rfc,
    Guid orderId,
    int comprobanteId,
    string userName,
    CancellationToken ct = default)
  {
    var normalizedRfc = LogisticsRfc.Require(rfc);
    if (comprobanteId <= 0) return RestaurantCommandResult.Fail("Selecciona un comprobante CFDI válido.");
    using var conn = CreateConnection();
    var order = await LoadOrderAsync(conn, normalizedRfc, orderId, ct);
    if (order is null) return RestaurantCommandResult.Fail("La orden no pertenece al RFC seleccionado.");
    if (order.Status == "Cancelled" || order.PaymentStatus != "Paid")
      return RestaurantCommandResult.Fail("Sólo se puede ligar CFDI a una venta pagada y no cancelada.");
    var scope = await _scopeAccessor.ResolveRequiredAsync(normalizedRfc, order.SiteId, ct);

    // Una orden admite una sola conversión a póliza individual. El CFDI solicitado
    // forma parte del payload inmutable: cambiarlo después de iniciar exige revisar
    // el intento, no crear otra póliza.
    var individualPayload = new IndividualCfdiPayload(
      scope.LegacySiteId,
      order.Id,
      comprobanteId,
      decimal.Round(order.Total, 2, MidpointRounding.ToEven));
    var individualOperation = await _outbox.ClaimAsync(
      AccountingOutboxModules.Restaurant,
      IndividualCfdiOperationKey(scope.LegacySiteId, order.Id),
      JsonSerializer.Serialize(individualPayload, OutboxJsonOptions),
      ct);
    if (individualOperation.InProgress)
      return RestaurantCommandResult.Fail("La póliza individual ya se está generando. Espera un momento antes de reintentar.");

    int? durableIndividualTransactionId = individualOperation.TransaccionId;
    try
    {
      EnsureIndividualPayload(individualOperation, normalizedRfc, individualPayload);
      if (individualOperation.AlreadyCompleted)
      {
        if (individualOperation.TransaccionId is not > 0)
          throw new InvalidOperationException("El rastro durable completado no identifica su póliza.");
        return RestaurantCommandResult.Ok($"La orden ya tiene póliza individual {individualOperation.TransaccionId}.");
      }

      // Adopta una póliza individual creada por la versión anterior sólo cuando el
      // usuario solicita exactamente el mismo CFDI de la misma orden.
      if (order.LinkType == "IndividualCfdi")
      {
        if (order.LinkedTransactionId is not > 0 || order.LinkedCfdiId != comprobanteId)
          throw new InvalidOperationException("La orden ya tiene otra póliza individual o un CFDI distinto.");
        if (individualOperation.TransaccionId is > 0
          && individualOperation.TransaccionId != order.LinkedTransactionId)
          throw new InvalidOperationException("El rastro durable apunta a una póliza distinta de la orden.");
        if (individualOperation.TransaccionId is null)
        {
          await _outbox.RecordPolicyAsync(individualOperation.Id, order.LinkedTransactionId.Value, ct);
          durableIndividualTransactionId = order.LinkedTransactionId.Value;
        }
        await _outbox.CompleteAsync(individualOperation.Id, ct);
        return RestaurantCommandResult.Ok($"La orden ya tiene póliza individual {order.LinkedTransactionId}.");
      }

      var receipts = (await conn.QueryAsync<RestaurantReportBreakdownDto>(new CommandDefinition(
        """
        SELECT PaymentMethod AS Label,CAST(SUM(Amount-RefundedAmount) AS decimal(18,2)) AS Amount
        FROM restaurante.Payment
        WHERE Rfc=@Rfc AND OrderId=@OrderId AND Amount-RefundedAmount>0
        GROUP BY PaymentMethod;
        """, new { Rfc = normalizedRfc, OrderId = orderId }, cancellationToken: ct))).AsList();
      var totals = new AccountingTotals
      {
        Sales = order.Total - order.TaxTotal,
        Tax = order.TaxTotal,
        Discounts = order.DiscountTotal,
        Cost = order.TheoreticalCost,
        Receipts = receipts
      };
      var receiptTotal = receipts.Sum(receipt => receipt.Amount);
      if (Math.Abs(receiptTotal - order.Total) > 0.02m)
        throw new InvalidOperationException("Los cobros de la orden no coinciden con su total; concilia antes de ligar el CFDI.");
      var config = await LoadConfigurationAsync(conn, normalizedRfc, order.SiteId, ct);
      if (!IsComplete(config, totals.Tax, totals.Discounts, totals.Cost, totals.Receipts))
        throw new InvalidOperationException("Completa las cuentas contables requeridas para esta sede.");

      int? reversalTransactionId = null;
      if (order.LinkType == "DailyConsolidated")
      {
        if (order.LinkedTransactionId is not > 0)
          throw new InvalidOperationException("La venta diaria no identifica la póliza que debe revertirse.");
        reversalTransactionId = await EnsureLateCfdiReversalAsync(
          conn,
          scope,
          normalizedRfc,
          order,
          receiptTotal,
          config,
          totals,
          userName,
          ct);
      }

      var resumed = individualOperation.ResumesFromExistingPolicy
        && await PolicyExistsAsync(conn, individualOperation.TransaccionId!.Value, ct);
      var individualTransactionId = resumed ? individualOperation.TransaccionId!.Value : 0;
      if (!resumed)
      {
        if (individualOperation.ResumesFromExistingPolicy)
          await _outbox.ForgetPolicyAsync(individualOperation.Id, ct);
        var concept = $"VENTA RESTAURANTE FOLIO {order.Folio:000} CFDI {comprobanteId}";
        individualTransactionId = await CreateClosedTransactionAsync(
          normalizedRfc, order.OperationalDate, concept, receiptTotal, config, totals, true, false,
          $"Póliza individual de venta Restaurante con CFDI. Usuario: {userName}", ct);
        try
        {
          await _outbox.RecordPolicyAsync(individualOperation.Id, individualTransactionId, ct);
          durableIndividualTransactionId = individualTransactionId;
        }
        catch
        {
          await _transactionService.DeleteTransaccionAsync(individualTransactionId, ct);
          throw;
        }
      }

      if (!await _transactionService.IsComprobanteLinkedToTransaccionAsync(
        individualTransactionId, comprobanteId, ct))
      {
        var cfdiLink = await _transactionService.InsertTransaccionComprobanteAsync(
          individualTransactionId, comprobanteId, order.Total, ct);
        if (!cfdiLink.Success)
          throw new InvalidOperationException(cfdiLink.Message ?? "No se pudo ligar el CFDI a la póliza individual.");
      }

      await LinkIndividualPolicyAsync(
        conn, scope, normalizedRfc, order, individualTransactionId, comprobanteId, userName, ct);
      await _outbox.CompleteAsync(individualOperation.Id, ct);
      return reversalTransactionId.HasValue
        ? RestaurantCommandResult.Ok($"Se generó reversión {reversalTransactionId} y póliza individual {individualTransactionId} ligada al CFDI.")
        : RestaurantCommandResult.Ok($"Póliza individual {individualTransactionId} ligada al CFDI.");
    }
    catch (Exception ex)
    {
      await FailOutboxQuietlyAsync(individualOperation.Id, ex, ct);
      var existing = durableIndividualTransactionId is > 0
        ? $" La póliza {durableIndividualTransactionId} se conserva y el reintento la retomará."
        : string.Empty;
      return RestaurantCommandResult.Fail($"No se completó la póliza individual: {ex.Message}{existing}");
    }
  }

  internal static string IndividualCfdiOperationKey(int siteId, Guid orderId)
    => $"INDIVIDUAL_CFDI:{siteId}:{orderId:D}";

  internal static string LateCfdiReversalOperationKey(int siteId, Guid orderId)
    => $"LATE_CFDI_REVERSAL:{siteId}:{orderId:D}";

  private async Task<int> EnsureLateCfdiReversalAsync(
    DbConnection conn,
    RestaurantScope scope,
    string rfc,
    AccountingOrderRow order,
    decimal receiptTotal,
    AccountingConfigurationRow config,
    AccountingTotals totals,
    string userName,
    CancellationToken ct)
  {
    var originalTransactionId = order.LinkedTransactionId!.Value;
    var payload = new LateCfdiReversalPayload(
      scope.LegacySiteId,
      order.Id,
      originalTransactionId,
      decimal.Round(order.Total, 2, MidpointRounding.ToEven));
    var operation = await _outbox.ClaimAsync(
      AccountingOutboxModules.Restaurant,
      LateCfdiReversalOperationKey(scope.LegacySiteId, order.Id),
      JsonSerializer.Serialize(payload, OutboxJsonOptions),
      ct);
    if (operation.InProgress)
      throw new InvalidOperationException("La reversión de CFDI tardío ya se está generando.");
    EnsureReversalPayload(operation, rfc, payload);
    if (operation.AlreadyCompleted)
      return operation.TransaccionId is > 0
        ? operation.TransaccionId.Value
        : throw new InvalidOperationException("El rastro durable completado no identifica su póliza de reversión.");

    var resumed = operation.ResumesFromExistingPolicy
      && await PolicyExistsAsync(conn, operation.TransaccionId!.Value, ct);
    var transactionId = resumed ? operation.TransaccionId!.Value : 0;
    if (!resumed)
    {
      if (operation.ResumesFromExistingPolicy)
        await _outbox.ForgetPolicyAsync(operation.Id, ct);
      var concept = $"AJUSTE CFDI TARDÍO RESTAURANTE FOLIO {order.Folio:000}";
      transactionId = await CreateClosedTransactionAsync(
        rfc, order.OperationalDate, concept, receiptTotal, config, totals, false, true,
        $"Reversión supervisada de la porción incluida en la póliza diaria {originalTransactionId}. Usuario: {userName}", ct);
      try
      {
        await _outbox.RecordPolicyAsync(operation.Id, transactionId, ct);
      }
      catch
      {
        await _transactionService.DeleteTransaccionAsync(transactionId, ct);
        throw;
      }
    }

    try
    {
      var linkedTransactionId = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
        $"""
        SET XACT_ABORT ON;
        BEGIN TRANSACTION;
        {RestaurantScopeAccessor.EnsureEnabledSql}
        IF NOT EXISTS
        (
          SELECT 1 FROM restaurante.AccountingLink WITH (UPDLOCK,HOLDLOCK)
          WHERE Rfc=@Rfc AND OrderId=@OrderId AND LinkType='LateCfdiReversal'
        )
          INSERT INTO restaurante.AccountingLink
            (Rfc,SiteId,OrderId,OperationalDate,LinkType,TransactionId)
          VALUES (@Rfc,@SiteId,@OrderId,@Date,'LateCfdiReversal',@TransactionId);
        SELECT TransactionId FROM restaurante.AccountingLink
        WHERE Rfc=@Rfc AND OrderId=@OrderId AND LinkType='LateCfdiReversal';
        COMMIT TRANSACTION;
        """, ScopedParameters(scope, new
        {
          Rfc = rfc,
          SiteId = order.SiteId,
          OrderId = order.Id,
          Date = order.OperationalDate,
          TransactionId = transactionId
        }), cancellationToken: ct));
      if (linkedTransactionId != transactionId)
        throw new InvalidOperationException("La orden ya tiene otra reversión de CFDI tardío.");
      await _outbox.CompleteAsync(operation.Id, ct);
      return transactionId;
    }
    catch (Exception ex)
    {
      await FailOutboxQuietlyAsync(operation.Id, ex, ct);
      throw;
    }
  }

  private async Task FailOutboxQuietlyAsync(long operationId, Exception original, CancellationToken ct)
  {
    try
    {
      await _outbox.FailAsync(operationId, original.Message, ct);
    }
    catch (Exception outboxException)
    {
      _logger.LogError(
        outboxException,
        "No se pudo conservar el fallo de la operación contable de restaurante {OperationId}",
        operationId);
    }
  }

  private static async Task LinkIndividualPolicyAsync(
    DbConnection conn,
    RestaurantScope scope,
    string rfc,
    AccountingOrderRow order,
    int transactionId,
    int comprobanteId,
    string userName,
    CancellationToken ct)
  {
    await conn.ExecuteAsync(new CommandDefinition(
      $"""
      SET XACT_ABORT ON;
      BEGIN TRANSACTION;
      {RestaurantScopeAccessor.EnsureEnabledSql}
      IF EXISTS
      (
        SELECT 1 FROM restaurante.AccountingLink WITH (UPDLOCK,HOLDLOCK)
        WHERE Rfc=@Rfc AND OrderId=@OrderId AND LinkType='IndividualCfdi'
          AND (TransactionId<>@TransactionId OR ISNULL(CfdiId,0)<>@CfdiId)
      ) THROW 52300,'La orden ya tiene otra póliza individual o un CFDI distinto.',1;

      IF NOT EXISTS
      (
        SELECT 1 FROM restaurante.AccountingLink WITH (UPDLOCK,HOLDLOCK)
        WHERE Rfc=@Rfc AND OrderId=@OrderId AND LinkType='IndividualCfdi'
      )
        INSERT INTO restaurante.AccountingLink
          (Rfc,SiteId,OrderId,OperationalDate,LinkType,TransactionId,CfdiId)
        VALUES (@Rfc,@SiteId,@OrderId,@Date,'IndividualCfdi',@TransactionId,@CfdiId);

      IF EXISTS
      (
        SELECT 1 FROM restaurante.AccountingOrderLink WITH (UPDLOCK,HOLDLOCK)
        WHERE Rfc=@Rfc AND OrderId=@OrderId AND LinkType='IndividualCfdi'
          AND (TransactionId<>@TransactionId OR ISNULL(CfdiId,0)<>@CfdiId)
      ) THROW 52301,'La orden ya apunta a otra póliza individual o a otro CFDI.',1;
      IF EXISTS
      (
        SELECT 1 FROM restaurante.AccountingOrderLink WITH (UPDLOCK,HOLDLOCK)
        WHERE Rfc=@Rfc AND OrderId=@OrderId
          AND LinkType NOT IN ('DailyConsolidated','IndividualCfdi')
      ) THROW 52302,'La orden tiene un vínculo contable incompatible con la póliza individual.',1;

      IF EXISTS (SELECT 1 FROM restaurante.AccountingOrderLink WITH (UPDLOCK,HOLDLOCK) WHERE Rfc=@Rfc AND OrderId=@OrderId)
        UPDATE restaurante.AccountingOrderLink
        SET LinkType='IndividualCfdi',TransactionId=@TransactionId,CfdiId=@CfdiId,CreatedAt=SYSUTCDATETIME()
        WHERE Rfc=@Rfc AND OrderId=@OrderId;
      ELSE
        INSERT INTO restaurante.AccountingOrderLink
          (Rfc,OrderId,SiteId,OperationalDate,LinkType,TransactionId,CfdiId)
        VALUES (@Rfc,@OrderId,@SiteId,@Date,'IndividualCfdi',@TransactionId,@CfdiId);

      IF NOT EXISTS
      (
        SELECT 1 FROM restaurante.OrderEvent WITH (UPDLOCK,HOLDLOCK)
        WHERE Rfc=@Rfc AND OrderId=@OrderId
          AND SourceKey=CONCAT('accounting:',CONVERT(varchar(36),@OrderId),':IndividualCfdi:',@TransactionId)
      )
        INSERT INTO restaurante.OrderEvent
          (Rfc,SiteId,OrderId,EventType,Category,Title,[Description],Actor,SourceKey)
        VALUES
          (@Rfc,@SiteId,@OrderId,'CfdiLinked','Accounting',N'CFDI ligado a póliza individual',
           CONCAT(N'Póliza ',@TransactionId,N' · CFDI ',@CfdiId),@UserName,
           CONCAT('accounting:',CONVERT(varchar(36),@OrderId),':IndividualCfdi:',@TransactionId));
      COMMIT TRANSACTION;
      """, ScopedParameters(scope, new
      {
        Rfc = rfc,
        SiteId = order.SiteId,
        OrderId = order.Id,
        Date = order.OperationalDate,
        TransactionId = transactionId,
        CfdiId = comprobanteId,
        UserName = userName
      }), cancellationToken: ct));
  }

  private static void EnsureIndividualPayload(
    AccountingOperation operation,
    string rfc,
    IndividualCfdiPayload current)
  {
    var original = DeserializePayload<IndividualCfdiPayload>(operation, rfc);
    if (original != current)
      throw new InvalidOperationException("La orden o el CFDI cambiaron después de iniciar la póliza individual.");
  }

  private static void EnsureReversalPayload(
    AccountingOperation operation,
    string rfc,
    LateCfdiReversalPayload current)
  {
    var original = DeserializePayload<LateCfdiReversalPayload>(operation, rfc);
    if (original != current)
      throw new InvalidOperationException("La póliza diaria cambió después de iniciar su reversión.");
  }

  private static T DeserializePayload<T>(AccountingOperation operation, string rfc) where T : class
  {
    if (!string.Equals(operation.Rfc, rfc, StringComparison.OrdinalIgnoreCase))
      throw new InvalidOperationException("La operación durable pertenece a otro RFC.");
    try
    {
      return JsonSerializer.Deserialize<T>(operation.Payload, OutboxJsonOptions)
        ?? throw new InvalidOperationException("El rastro durable no tiene contenido.");
    }
    catch (JsonException ex)
    {
      throw new InvalidOperationException("El rastro durable no es válido.", ex);
    }
  }

  private async Task<int> CreateClosedTransactionAsync(
    string rfc,
    DateTime date,
    string concept,
    decimal receiptTotal,
    AccountingConfigurationRow config,
    AccountingTotals totals,
    bool invoiced,
    bool reverse,
    string memo,
    CancellationToken ct)
  {
    var created = await _transactionService.CreateTransaccionAsync(new TransaccionCreateRequest
    {
      Rfc = rfc,
      Fecha = date,
      Concepto = concept,
      Monto = receiptTotal,
      TipoPoliza = "INGRESO",
      FormaPago = "VARIAS",
      Facturado = invoiced,
      Memo = memo,
      Cuenta = config.SalesAccount
    }, ct);
    if (!created.Success) throw new InvalidOperationException(created.Message ?? "No se pudo crear la transacción contable.");

    var transactionId = created.NewTransaccionId;
    try
    {
      var saved = await _transactionService.GuardarMovimientosAsync(new TransaccionMovimientosUpdateRequest
      {
        TransaccionId = transactionId,
        Movimientos = BuildMovements(config, totals, reverse)
      }, ct);
      if (!saved.Success) throw new InvalidOperationException(saved.Message);
      var closed = await _transactionService.GuardarYCerrarAsync(new TransaccionGuardarCerrarRequest
      {
        TransaccionId = transactionId,
        Concepto = concept,
        Fecha = date,
        Cuenta = config.SalesAccount,
        Monto = receiptTotal,
        Facturado = invoiced,
        Memo = memo,
        TipoPoliza = "INGRESO",
        FormaPago = "VARIAS"
      }, ct);
      if (!closed.Success) throw new InvalidOperationException(closed.Message);
      return transactionId;
    }
    catch
    {
      await _transactionService.DeleteTransaccionAsync(transactionId, ct);
      throw;
    }
  }

  private static DynamicParameters ScopedParameters(RestaurantScope scope, object parameters)
  {
    var merged = new DynamicParameters(parameters);
    merged.AddDynamicParams(RestaurantScopeAccessor.EnsureEnabledParameters(scope));
    return merged;
  }

  private static List<TransaccionMovimientoUpdateItem> BuildMovements(
    AccountingConfigurationRow config,
    AccountingTotals totals,
    bool reverse)
  {
    var movements = new List<TransaccionMovimientoUpdateItem>();
    void Add(string account, string name, decimal debit, decimal credit)
      => movements.Add(reverse ? Movement(account, name, credit, debit) : Movement(account, name, debit, credit));
    foreach (var receipt in totals.Receipts)
    {
      var account = receipt.Label switch
      {
        "Cash" => config.CashAccount,
        "ExternalCard" => config.CardBankAccount,
        "Transfer" => config.TransferBankAccount,
        "Platform" => config.PlatformReceivableAccount,
        _ => null
      };
      if (string.IsNullOrWhiteSpace(account)) throw new InvalidOperationException($"Falta la cuenta para {receipt.Label}.");
      Add(account, $"Cobros {receipt.Label}", receipt.Amount, 0);
    }
    if (totals.Discounts > 0) Add(config.DiscountAccount!, "Descuentos Restaurante", totals.Discounts, 0);
    if (totals.Cost > 0) Add(config.CostOfSalesAccount!, "Costo teórico de ventas", totals.Cost, 0);
    Add(config.SalesAccount!, "Ventas Restaurante", 0, totals.Sales + totals.Discounts);
    if (totals.Tax > 0) Add(config.VatAccount!, "IVA trasladado", 0, totals.Tax);
    if (totals.Cost > 0) Add(config.InventoryAccount!, "Salida de inventario por venta", 0, totals.Cost);
    return movements;
  }

  private static TransaccionMovimientoUpdateItem Movement(string account, string name, decimal debit, decimal credit)
  {
    var parts = account.Split(['.', '-', '/', '>'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    return new()
    {
      Nivel1 = parts.ElementAtOrDefault(0) ?? account,
      Nivel2 = parts.ElementAtOrDefault(1),
      Nivel3 = parts.ElementAtOrDefault(2),
      NombreCuenta = name,
      Concepto = name,
      Debe = debit,
      Haber = credit
    };
  }

  private static bool IsComplete(
    AccountingConfigurationRow? config,
    decimal tax,
    decimal discounts,
    decimal cost,
    IReadOnlyList<RestaurantReportBreakdownDto> receipts)
  {
    if (config is null || !config.DailyPolicyEnabled || string.IsNullOrWhiteSpace(config.SalesAccount)) return false;
    if (tax > 0 && string.IsNullOrWhiteSpace(config.VatAccount)) return false;
    if (discounts > 0 && string.IsNullOrWhiteSpace(config.DiscountAccount)) return false;
    if (cost > 0 && (string.IsNullOrWhiteSpace(config.InventoryAccount) || string.IsNullOrWhiteSpace(config.CostOfSalesAccount))) return false;
    return receipts.All(receipt => !string.IsNullOrWhiteSpace(receipt.Label switch
    {
      "Cash" => config.CashAccount,
      "ExternalCard" => config.CardBankAccount,
      "Transfer" => config.TransferBankAccount,
      "Platform" => config.PlatformReceivableAccount,
      _ => null
    }));
  }

  private static Task<AccountingConfigurationRow> LoadConfigurationAsync(
    DbConnection conn,
    string rfc,
    int siteId,
    CancellationToken ct)
    => conn.QuerySingleAsync<AccountingConfigurationRow>(new CommandDefinition(
      "SELECT * FROM restaurante.AccountingConfiguration WHERE Rfc=@Rfc AND SiteId=@SiteId;",
      new { Rfc = rfc, SiteId = siteId }, cancellationToken: ct));

  private static Task<AccountingOrderRow?> LoadOrderAsync(DbConnection conn, string rfc, Guid orderId, CancellationToken ct)
    => conn.QuerySingleOrDefaultAsync<AccountingOrderRow>(new CommandDefinition(
      """
      SELECT orderInfo.Id,orderInfo.SiteId,orderInfo.Folio,orderInfo.OperationalDate,orderInfo.[Status],orderInfo.PaymentStatus,
             orderInfo.Total,orderInfo.TaxTotal,orderInfo.DiscountTotal,orderInfo.TheoreticalCost,
             linkInfo.LinkType,linkInfo.TransactionId AS LinkedTransactionId,linkInfo.CfdiId AS LinkedCfdiId
      FROM restaurante.[Order] orderInfo
      LEFT JOIN restaurante.AccountingOrderLink linkInfo ON linkInfo.Rfc=orderInfo.Rfc AND linkInfo.OrderId=orderInfo.Id
      WHERE orderInfo.Rfc=@Rfc AND orderInfo.Id=@OrderId;
      """, new { Rfc = rfc, OrderId = orderId }, cancellationToken: ct));

  private DbConnection CreateConnection()
    => _connectionFactory.Create() as DbConnection
      ?? throw new InvalidOperationException("La fábrica no devolvió una DbConnection.");

  private sealed class AccountingConfigurationRow
  {
    public string? CashAccount { get; set; }
    public string? CardBankAccount { get; set; }
    public string? TransferBankAccount { get; set; }
    public string? PlatformReceivableAccount { get; set; }
    public string? SalesAccount { get; set; }
    public string? VatAccount { get; set; }
    public string? DiscountAccount { get; set; }
    public string? InventoryAccount { get; set; }
    public string? CostOfSalesAccount { get; set; }
    public bool DailyPolicyEnabled { get; set; }
  }

  private sealed class AccountingTotals
  {
    public decimal Sales { get; set; }
    public decimal Tax { get; set; }
    public decimal Discounts { get; set; }
    public decimal Cost { get; set; }
    public IReadOnlyList<RestaurantReportBreakdownDto> Receipts { get; set; } = [];
  }

  private sealed class AccountingOrderRow
  {
    public Guid Id { get; set; }
    public int SiteId { get; set; }
    public int Folio { get; set; }
    public DateTime OperationalDate { get; set; }
    public string Status { get; set; } = string.Empty;
    public string PaymentStatus { get; set; } = string.Empty;
    public decimal Total { get; set; }
    public decimal TaxTotal { get; set; }
    public decimal DiscountTotal { get; set; }
    public decimal TheoreticalCost { get; set; }
    public string? LinkType { get; set; }
    public int? LinkedTransactionId { get; set; }
    public int? LinkedCfdiId { get; set; }
  }

  private sealed record IndividualCfdiPayload(int SiteId, Guid OrderId, int CfdiId, decimal Amount);
  private sealed record LateCfdiReversalPayload(int SiteId, Guid OrderId, int OriginalTransactionId, decimal Amount);
}
