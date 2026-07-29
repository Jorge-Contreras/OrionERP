using System.Data.Common;
using Dapper;
using OrionERP.Application.Common;
using OrionERP.Application.Features.Contabilidad.Transacciones;
using OrionERP.Application.Features.Logistica.Shared;
using OrionERP.Application.Features.Restaurante;

namespace OrionERP.Infrastructure.Features.Restaurante;

public sealed class RestaurantAccountingService : IRestaurantAccountingService
{
  private readonly IDbConnectionFactory _connectionFactory;
  private readonly ITransaccionService _transactionService;

  public RestaurantAccountingService(IDbConnectionFactory connectionFactory, ITransaccionService transactionService)
  {
    _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    _transactionService = transactionService ?? throw new ArgumentNullException(nameof(transactionService));
  }

  public async Task<RestaurantAccountingPreviewDto> GetDailyPreviewAsync(
    string rfc,
    int siteId,
    DateTime operationalDate,
    CancellationToken ct = default)
  {
    var normalizedRfc = LogisticsRfc.Require(rfc);
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
    var date = operationalDate.Date;
    var preview = await GetDailyPreviewAsync(normalizedRfc, siteId, date, ct);
    if (preview.ExistingTransactionId.HasValue)
      return RestaurantCommandResult.Ok($"La póliza diaria ya existe: {preview.ExistingTransactionId}.");
    if (preview.EligibleOrderCount == 0)
      return RestaurantCommandResult.Fail("No hay ventas liquidadas elegibles para esta fecha.");
    if (!preview.ConfigurationComplete)
      return RestaurantCommandResult.Fail("Completa las cuentas contables requeridas en Configuración operativa.");

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
      return RestaurantCommandResult.Fail($"Los cobros ({receiptTotal:C}) no coinciden con las ventas netas e IVA ({totals.Sales + totals.Tax:C}); concilia pagos antes de generar la póliza.");

    var concept = $"VENTAS RESTAURANTE {date:yyyy-MM-dd} SEDE {siteId}";
    var transactionId = await CreateClosedTransactionAsync(
      normalizedRfc, date, concept, receiptTotal, config, totals, false, false,
      $"Consolidado diario Restaurante; excluye CFDI individuales, propinas, cancelaciones, reembolsos y saldos pendientes. Usuario: {userName}", ct);
    try
    {
      var linkedOrders = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
        """
        SET XACT_ABORT ON;
        BEGIN TRANSACTION;
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
        """, new { Rfc = normalizedRfc, SiteId = siteId, Date = date, TransactionId = transactionId, UserName = userName }, cancellationToken: ct));
      if (linkedOrders == 0) throw new InvalidOperationException("Las ventas fueron vinculadas por otro proceso.");
      return RestaurantCommandResult.Ok($"Póliza diaria {transactionId} generada y balanceada para {linkedOrders} venta(s).");
    }
    catch (Exception ex)
    {
      await _transactionService.DeleteTransaccionAsync(transactionId, ct);
      return RestaurantCommandResult.Fail($"No se generó la póliza: {ex.Message}");
    }
  }

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
    if (order.LinkType == "IndividualCfdi")
      return RestaurantCommandResult.Ok($"La orden ya tiene póliza individual {order.LinkedTransactionId}.");

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
      return RestaurantCommandResult.Fail("Los cobros de la orden no coinciden con su total; concilia antes de ligar el CFDI.");
    var config = await LoadConfigurationAsync(conn, normalizedRfc, order.SiteId, ct);
    if (!IsComplete(config, totals.Tax, totals.Discounts, totals.Cost, totals.Receipts))
      return RestaurantCommandResult.Fail("Completa las cuentas contables requeridas para esta sede.");

    int? reversalTransactionId = null;
    try
    {
      if (order.LinkType == "DailyConsolidated")
      {
        var reversalConcept = $"AJUSTE CFDI TARDÍO RESTAURANTE FOLIO {order.Folio:000}";
        reversalTransactionId = await CreateClosedTransactionAsync(
          normalizedRfc, order.OperationalDate, reversalConcept, receiptTotal, config, totals, false, true,
          $"Reversión supervisada de la porción incluida en la póliza diaria {order.LinkedTransactionId}. Usuario: {userName}", ct);
      }

      var concept = $"VENTA RESTAURANTE FOLIO {order.Folio:000} CFDI {comprobanteId}";
      var individualTransactionId = await CreateClosedTransactionAsync(
        normalizedRfc, order.OperationalDate, concept, receiptTotal, config, totals, true, false,
        $"Póliza individual de venta Restaurante con CFDI. Usuario: {userName}", ct);
      var cfdiLink = await _transactionService.InsertTransaccionComprobanteAsync(individualTransactionId, comprobanteId, order.Total, ct);
      if (!cfdiLink.Success)
      {
        await _transactionService.DeleteTransaccionAsync(individualTransactionId, ct);
        if (reversalTransactionId.HasValue) await _transactionService.DeleteTransaccionAsync(reversalTransactionId.Value, ct);
        return RestaurantCommandResult.Fail(cfdiLink.Message ?? "No se pudo ligar el CFDI a la póliza individual.");
      }

      try
      {
        await conn.ExecuteAsync(new CommandDefinition(
          """
          SET XACT_ABORT ON;
          BEGIN TRANSACTION;
          IF @ReversalTransactionId IS NOT NULL
          BEGIN
            INSERT INTO restaurante.AccountingLink (Rfc,SiteId,OrderId,OperationalDate,LinkType,TransactionId)
            VALUES (@Rfc,@SiteId,@OrderId,@Date,'LateCfdiReversal',@ReversalTransactionId);
          END;
          INSERT INTO restaurante.AccountingLink (Rfc,SiteId,OrderId,OperationalDate,LinkType,TransactionId,CfdiId)
          VALUES (@Rfc,@SiteId,@OrderId,@Date,'IndividualCfdi',@IndividualTransactionId,@CfdiId);

          IF EXISTS (SELECT 1 FROM restaurante.AccountingOrderLink WITH (UPDLOCK,HOLDLOCK) WHERE Rfc=@Rfc AND OrderId=@OrderId)
            UPDATE restaurante.AccountingOrderLink
            SET LinkType='IndividualCfdi',TransactionId=@IndividualTransactionId,CfdiId=@CfdiId,CreatedAt=SYSUTCDATETIME()
            WHERE Rfc=@Rfc AND OrderId=@OrderId;
          ELSE
            INSERT INTO restaurante.AccountingOrderLink (Rfc,OrderId,SiteId,OperationalDate,LinkType,TransactionId,CfdiId)
            VALUES (@Rfc,@OrderId,@SiteId,@Date,'IndividualCfdi',@IndividualTransactionId,@CfdiId);

          INSERT INTO restaurante.OrderEvent
            (Rfc,SiteId,OrderId,EventType,Category,Title,[Description],Actor,SourceKey)
          VALUES
            (@Rfc,@SiteId,@OrderId,'CfdiLinked','Accounting',N'CFDI ligado a póliza individual',
             CONCAT(N'Póliza ',@IndividualTransactionId,N' · CFDI ',@CfdiId),@UserName,
             CONCAT('accounting:',CONVERT(varchar(36),@OrderId),':IndividualCfdi:',@IndividualTransactionId));
          COMMIT TRANSACTION;
          """, new
          {
            Rfc = normalizedRfc,
            order.SiteId,
            OrderId = orderId,
            Date = order.OperationalDate,
            ReversalTransactionId = reversalTransactionId,
            IndividualTransactionId = individualTransactionId,
            CfdiId = comprobanteId,
            UserName = userName
          }, cancellationToken: ct));
        return reversalTransactionId.HasValue
          ? RestaurantCommandResult.Ok($"Se generó reversión {reversalTransactionId} y póliza individual {individualTransactionId} ligada al CFDI.")
          : RestaurantCommandResult.Ok($"Póliza individual {individualTransactionId} ligada al CFDI.");
      }
      catch
      {
        await _transactionService.DeleteTransaccionAsync(individualTransactionId, ct);
        if (reversalTransactionId.HasValue) await _transactionService.DeleteTransaccionAsync(reversalTransactionId.Value, ct);
        throw;
      }
    }
    catch (Exception ex)
    {
      return RestaurantCommandResult.Fail($"No se generó la póliza individual: {ex.Message}");
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
             linkInfo.LinkType,linkInfo.TransactionId AS LinkedTransactionId
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
  }
}
