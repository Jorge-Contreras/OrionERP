using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using OrionERP.Application.Common;
using OrionERP.Application.Features.Ajustes;
using OrionERP.Application.Features.Contabilidad.Transacciones;
using OrionERP.Application.Features.Logistica.Purchasing;
using OrionERP.Application.Features.Logistica.Shared;
using OrionERP.Application.Features.Reservaciones;
using OrionERP.Infrastructure.Features.Contabilidad.Transacciones;
using OrionERP.Infrastructure.Features.Logistica.Shared;

namespace OrionERP.Infrastructure.Features.Logistica.Purchasing;

/// <summary>
/// Liga compras recibidas con pólizas. La generación pasa por la bandeja contable durable
/// porque DELETE sobre dbo.Transacciones no funciona: una póliza creada no se puede deshacer,
/// así que un intento que falla a medias se retoma sobre esa misma póliza.
/// </summary>
public sealed class PurchaseAccountingService : IPurchaseAccountingService
{
  private const string PolicyType = "EGRESO";
  private const string DefaultPaymentMethod = "99";
  private const int CandidateLimit = 25;
  private const int CandidateWindowDays = 90;
  private const int StatusBatchLimit = 1000;
  private const int ConceptMaxLength = 500;
  private const int MovementTextMaxLength = 200;
  private const decimal CentTolerance = 0.005m;

  // El SQL de vínculo lanza estos números con un mensaje ya escrito para el usuario.
  private const int FirstBusinessError = 53601;
  private const int LastBusinessError = 53605;

  private static readonly CultureInfo MexicanCulture = CultureInfo.GetCultureInfo("es-MX");
  private static readonly JsonSerializerOptions OutboxJsonOptions = new(JsonSerializerDefaults.Web);
  private static readonly string[] RequiredAccountRoles =
  [
    CfdiPolizaCuentaDefaultRoles.SubtotalGasto,
    CfdiPolizaCuentaDefaultRoles.IvaAcreditable,
    CfdiPolizaCuentaDefaultRoles.TotalGasto
  ];
  private static readonly string[] ReceivedStatuses =
  [
    PurchaseOrderStatuses.PartiallyReceived,
    PurchaseOrderStatuses.Completed
  ];

  private readonly IDbConnectionFactory _connectionFactory;
  private readonly ICurrentCompanyContext _company;
  private readonly ITransaccionService _transacciones;
  private readonly IAjustesService _ajustes;
  private readonly IAccountingOutbox _outbox;
  private readonly ILogger<PurchaseAccountingService> _logger;
  private readonly IHospitalityScopeAccessor? _hospitalityScope;

  public PurchaseAccountingService(
    IDbConnectionFactory connectionFactory,
    ICurrentCompanyContext company,
    ITransaccionService transacciones,
    IAjustesService ajustes,
    IAccountingOutbox outbox,
    ILogger<PurchaseAccountingService> logger,
    IHospitalityScopeAccessor? hospitalityScope = null)
  {
    _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    _company = company ?? throw new ArgumentNullException(nameof(company));
    _transacciones = transacciones ?? throw new ArgumentNullException(nameof(transacciones));
    _ajustes = ajustes ?? throw new ArgumentNullException(nameof(ajustes));
    _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
    _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    _hospitalityScope = hospitalityScope;
  }

  // Recepciones anteriores a que se capturaran totales sólo tienen costo unitario y cantidad;
  // contarlas en cero escondería lo que falta contabilizar.
  private const string OrderTotalsApply = """
      OUTER APPLY (
          SELECT
              CAST(ISNULL(SUM(amounts.LineSubtotal), 0) AS decimal(18,2)) AS RecibidoSubtotal,
              CAST(ISNULL(SUM(amounts.LineTotal), 0) AS decimal(18,2)) AS Recibido,
              MAX(receipt.ReceiptDate) AS UltimaRecepcion
          FROM logistica.PurchaseReceipt receipt
          JOIN logistica.PurchaseReceiptLine receiptLine
            ON receiptLine.PurchaseReceiptId = receipt.Id
           AND receiptLine.Rfc = receipt.Rfc
          CROSS APPLY (
              SELECT
                  COALESCE(receiptLine.TotalAmount,
                           receiptLine.SubtotalAmount + ISNULL(receiptLine.IvaAmount, 0),
                           receiptLine.UnitCost * receiptLine.Quantity,
                           0) AS LineTotal,
                  COALESCE(receiptLine.SubtotalAmount,
                           receiptLine.TotalAmount - ISNULL(receiptLine.IvaAmount, 0),
                           receiptLine.UnitCost * receiptLine.Quantity,
                           0) AS LineSubtotal
          ) amounts
          WHERE receipt.PurchaseOrderId = po.Id
            AND receipt.Rfc = po.Rfc
      ) receiptTotals
      OUTER APPLY (
          SELECT CAST(ISNULL(SUM(link.MontoAsignado), 0) AS decimal(18,2)) AS Contabilizado
          FROM logistica.PurchaseAccountingLink link
          WHERE link.PurchaseOrderId = po.Id
            AND link.Rfc = po.Rfc
      ) linkTotals
      """;

  private static readonly string OrderSql = $"""
      SELECT
          po.Id AS PurchaseOrderId,
          po.PurchaseOrderCode,
          po.[Status] AS [Status],
          po.OrderDate,
          bp.PartnerName AS VendorName,
          bp.Rfc AS VendorRfc,
          receiptTotals.Recibido,
          receiptTotals.RecibidoSubtotal,
          receiptTotals.UltimaRecepcion,
          linkTotals.Contabilizado
      FROM logistica.PurchaseOrder po
      JOIN dbo.BusinessPartner bp
        ON bp.Id = po.BusinessPartnerId
      {OrderTotalsApply}
      WHERE po.Id = @PurchaseOrderId
        AND po.Rfc = @Rfc
        AND {PurchaseOrderService.OrderVisibilitySql};
      """;

  private const string LinkedPolizasSql = """
      SELECT
          link.Id AS LinkId,
          link.TransaccionId,
          link.Origin,
          link.MontoAsignado,
          link.CreatedAt,
          link.CreatedBy,
          poliza.Fecha,
          poliza.Concepto,
          CAST(ABS(poliza.Monto) AS decimal(18,2)) AS MontoPoliza,
          poliza.Tipo_Poliza AS TipoPoliza,
          ISNULL(movimientos.Renglones, 0) AS Renglones,
          CAST(ISNULL(movimientos.Cargos, 0) AS decimal(19,4)) AS Cargos,
          CAST(ISNULL(movimientos.Abonos, 0) AS decimal(19,4)) AS Abonos
      FROM logistica.PurchaseAccountingLink link
      LEFT JOIN dbo.Transacciones poliza
        ON poliza.ID = link.TransaccionId
       AND poliza.RFC = link.Rfc
      OUTER APPLY (
          SELECT
              COUNT(*) AS Renglones,
              SUM(ISNULL(registro.Debe, 0)) AS Cargos,
              SUM(ISNULL(registro.Haber, 0)) AS Abonos
          FROM dbo.Registro_Contable registro
          WHERE registro.TransaccionID = poliza.ID
      ) movimientos
      WHERE link.PurchaseOrderId = @PurchaseOrderId
        AND link.Rfc = @Rfc
      ORDER BY link.CreatedAt DESC, link.Id DESC;
      """;

  // Bajo SERIALIZABLE. Los UPDLOCK van primero y siempre en el orden compra → póliza, para que
  // dos vínculos concurrentes esperen en vez de interbloquearse o rebasar un saldo.
  private static readonly string InsertLinkSql = $"""
      DECLARE @Recibido decimal(18,2), @Contabilizado decimal(18,2),
              @MontoPoliza decimal(18,2), @AsignadoPoliza decimal(18,2), @Message nvarchar(400);

      IF NOT EXISTS
      (
          SELECT 1 FROM logistica.PurchaseOrder po WITH (UPDLOCK, HOLDLOCK)
          WHERE po.Id = @PurchaseOrderId
            AND po.Rfc = @Rfc
            AND {PurchaseOrderService.OrderVisibilitySql}
      )
          THROW 53601, N'No encontramos esta compra o no tienes acceso a ella.', 1;

      SELECT @MontoPoliza = CAST(ABS(poliza.Monto) AS decimal(18,2))
      FROM dbo.Transacciones poliza WITH (UPDLOCK, HOLDLOCK)
      WHERE poliza.ID = @TransaccionId
        AND poliza.RFC = @Rfc;
      IF @MontoPoliza IS NULL
          THROW 53602, N'No encontramos esa póliza en esta empresa.', 1;

      IF EXISTS
      (
          SELECT 1 FROM logistica.PurchaseAccountingLink WITH (UPDLOCK, HOLDLOCK)
          WHERE PurchaseOrderId = @PurchaseOrderId AND TransaccionId = @TransaccionId
      )
      BEGIN
          IF @AllowExisting = 1 RETURN;
          THROW 53603, N'Esa póliza ya está ligada a esta compra.', 1;
      END;

      SELECT @Recibido = receiptTotals.Recibido, @Contabilizado = linkTotals.Contabilizado
      FROM logistica.PurchaseOrder po
      {OrderTotalsApply}
      WHERE po.Id = @PurchaseOrderId;

      SELECT @AsignadoPoliza = CAST(ISNULL(SUM(MontoAsignado), 0) AS decimal(18,2))
      FROM logistica.PurchaseAccountingLink WITH (UPDLOCK, HOLDLOCK)
      WHERE TransaccionId = @TransaccionId;

      IF @Monto > @Recibido - @Contabilizado + 0.005
      BEGIN
          SET @Message = CONCAT(N'El monto rebasa lo que falta registrar de esta compra (',
                                FORMAT(@Recibido - @Contabilizado, 'C', 'es-MX'), N').');
          THROW 53604, @Message, 1;
      END;

      IF @Monto > @MontoPoliza - @AsignadoPoliza + 0.005
      BEGIN
          SET @Message = CONCAT(N'El monto rebasa lo que la póliza tiene disponible (',
                                FORMAT(@MontoPoliza - @AsignadoPoliza, 'C', 'es-MX'), N').');
          THROW 53605, @Message, 1;
      END;

      INSERT INTO logistica.PurchaseAccountingLink (Rfc, PurchaseOrderId, TransaccionId, MontoAsignado, Origin, CreatedBy)
      VALUES (@Rfc, @PurchaseOrderId, @TransaccionId, @Monto, @Origin, @CreatedBy);
      """;

  private const string OutboxOperationsSql = """
      IF OBJECT_ID(N'contabilidad.AccountingOutbox', N'U') IS NOT NULL
          SELECT
              operation.Id,
              operation.[Status] AS [Status],
              operation.TransaccionId,
              CONVERT(bit, CASE WHEN operation.TransaccionId IS NOT NULL AND EXISTS
              (
                  SELECT 1 FROM logistica.PurchaseAccountingLink link
                  WHERE link.PurchaseOrderId = @PurchaseOrderId
                    AND link.TransaccionId = operation.TransaccionId
              ) THEN 1 ELSE 0 END) AS IsLinked
          FROM contabilidad.AccountingOutbox operation
          WHERE operation.CompanyId = @CompanyId
            AND operation.SourceModule = @SourceModule
            AND operation.OperationKey LIKE @KeyPattern;
      ELSE
          SELECT CAST(NULL AS bigint) AS Id, CAST(NULL AS varchar(20)) AS [Status],
                 CAST(NULL AS int) AS TransaccionId, CONVERT(bit, 0) AS IsLinked
          WHERE 1 = 0;
      """;

  public async Task<PurchaseAccountingWorkspaceDto?> GetPurchaseOrderWorkspaceAsync(int purchaseOrderId, CancellationToken ct = default)
  {
    var rfc = CurrentRfc();
    await using var conn = await OpenAsync(rfc, ct);
    var order = await LoadOrderAsync(conn, rfc, purchaseOrderId, ct);
    if (order is null) return null;

    var polizas = await conn.QueryAsync<PurchaseAccountingLinkedPolizaDto>(new CommandDefinition(
      LinkedPolizasSql, new { Rfc = rfc, PurchaseOrderId = purchaseOrderId }, cancellationToken: ct));
    var operations = await LoadOperationsAsync(conn, await _company.RequireCompanyIdAsync(ct), purchaseOrderId, ct);
    var defaults = await _ajustes.GetCfdiPolizaCuentaDefaultsAsync(rfc, ct);
    var formasPago = await _transacciones.GetFormasPagoAsync(ct);

    var workspace = new PurchaseAccountingWorkspaceDto
    {
      Summary = new PurchaseAccountingSummaryDto
      {
        PurchaseOrderId = order.PurchaseOrderId,
        PurchaseOrderCode = order.PurchaseOrderCode,
        Status = order.Status,
        VendorName = order.VendorName,
        VendorRfc = order.VendorRfc,
        Recibido = order.Recibido,
        RecibidoSubtotal = order.RecibidoSubtotal,
        RecibidoIva = order.Recibido - order.RecibidoSubtotal,
        Contabilizado = order.Contabilizado,
        PorContabilizar = order.PorContabilizar,
        UltimaRecepcion = order.UltimaRecepcion,
        CuentasFaltantes = MissingAccounts(defaults),
        PolizaSinTerminarId = operations
          .Where(operation => operation.Status != AccountingOutboxStates.Completed && !operation.IsLinked)
          .Select(operation => operation.TransaccionId)
          .FirstOrDefault(id => id is > 0)
      },
      FormaPagoSugerida = formasPago.Any(forma => forma.Clave == DefaultPaymentMethod) ? DefaultPaymentMethod : null
    };
    workspace.Polizas.AddRange(polizas);
    workspace.FormasPago.AddRange(formasPago);
    return workspace;
  }

  public async Task<IReadOnlyList<PurchaseAccountingCandidateDto>> SearchPolicyCandidatesAsync(
    int purchaseOrderId, string? search, CancellationToken ct = default)
  {
    var rfc = CurrentRfc();
    await using var conn = await OpenAsync(rfc, ct);
    var order = await LoadOrderAsync(conn, rfc, purchaseOrderId, ct);
    if (order is null) return [];

    var (searchId, searchLike) = ParsePolicySearch(search);
    var rows = await conn.QueryAsync<PurchaseAccountingCandidateDto>(new CommandDefinition(
      """
      SELECT TOP (@Limit)
          poliza.ID AS TransaccionId,
          poliza.Fecha,
          poliza.Concepto,
          poliza.Tipo_Poliza AS TipoPoliza,
          CAST(ABS(poliza.Monto) AS decimal(18,2)) AS Monto,
          assigned.Asignado,
          CAST(ABS(poliza.Monto) - assigned.Asignado AS decimal(18,2)) AS Disponible
      FROM dbo.Transacciones poliza
      OUTER APPLY (
          SELECT CAST(ISNULL(SUM(link.MontoAsignado), 0) AS decimal(18,2)) AS Asignado
          FROM logistica.PurchaseAccountingLink link
          WHERE link.TransaccionId = poliza.ID
      ) assigned
      WHERE poliza.RFC = @Rfc
        AND ISNULL(poliza.Tipo_Poliza, '') <> 'INGRESO'
        AND ABS(poliza.Monto) - assigned.Asignado > 0.005
        AND NOT EXISTS
        (
            SELECT 1 FROM logistica.PurchaseAccountingLink existing
            WHERE existing.PurchaseOrderId = @PurchaseOrderId
              AND existing.TransaccionId = poliza.ID
        )
        AND (@SearchId IS NULL OR poliza.ID = @SearchId)
        AND (@SearchLike IS NULL OR poliza.Concepto LIKE @SearchLike OR poliza.Memo LIKE @SearchLike)
        AND (
            @SearchId IS NOT NULL
            OR @SearchLike IS NOT NULL
            OR (poliza.Fecha >= DATEADD(DAY, -@WindowDays, @Anchor) AND poliza.Fecha < DATEADD(DAY, @WindowDays + 1, @Anchor))
        )
      ORDER BY
          ABS(ABS(poliza.Monto) - assigned.Asignado - @Objetivo),
          ABS(DATEDIFF(DAY, poliza.Fecha, @Anchor)),
          poliza.ID DESC;
      """,
      new
      {
        Limit = CandidateLimit,
        Rfc = rfc,
        PurchaseOrderId = purchaseOrderId,
        SearchId = searchId,
        SearchLike = searchLike,
        WindowDays = CandidateWindowDays,
        Anchor = (order.UltimaRecepcion ?? order.OrderDate).Date,
        Objetivo = order.PorContabilizar
      },
      cancellationToken: ct));
    return rows.AsList();
  }

  public async Task<IReadOnlyList<PurchaseOrderPostingStatusDto>> GetPostingStatusesAsync(
    IReadOnlyCollection<int> purchaseOrderIds, CancellationToken ct = default)
  {
    var ids = (purchaseOrderIds ?? []).Where(id => id > 0).Distinct().Take(StatusBatchLimit).ToArray();
    if (ids.Length == 0) return [];

    var rfc = CurrentRfc();
    await using var conn = await OpenAsync(rfc, ct);
    var rows = await conn.QueryAsync<PurchaseOrderPostingStatusDto>(new CommandDefinition(
      $"""
      SELECT po.Id AS PurchaseOrderId, receiptTotals.Recibido, linkTotals.Contabilizado
      FROM logistica.PurchaseOrder po
      {OrderTotalsApply}
      WHERE po.Rfc = @Rfc
        AND po.Id IN @Ids
        AND po.[Status] IN @Statuses
        AND {PurchaseOrderService.OrderVisibilitySql};
      """,
      new { Rfc = rfc, Ids = ids, Statuses = ReceivedStatuses },
      cancellationToken: ct));
    return rows.AsList();
  }

  public async Task<LogisticsCommandResult> GeneratePolicyAsync(
    PurchaseAccountingGenerateRequest request, string? userName, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(request);
    var rfc = CurrentRfc();
    var companyId = await _company.RequireCompanyIdAsync(ct);
    var fecha = request.Fecha.Date;
    if (fecha == default) return LogisticsCommandResult.Fail("Indica la fecha de la póliza.");

    await using var conn = await OpenAsync(rfc, ct);
    var order = await LoadOrderAsync(conn, rfc, request.PurchaseOrderId, ct);
    if (order is null) return LogisticsCommandResult.Fail("No encontramos esta compra o no tienes acceso a ella.");
    if (order.PorContabilizar <= 0m)
      return LogisticsCommandResult.Fail("Esta compra ya está registrada completa en contabilidad.");

    // Todo lo que puede fallar por configuración se revisa antes de crear la póliza: después
    // ya no se puede borrar.
    var defaults = await _ajustes.GetCfdiPolizaCuentaDefaultsAsync(rfc, ct);
    var missing = MissingAccounts(defaults);
    if (missing.Count > 0)
      return LogisticsCommandResult.Fail(
        $"Contabilidad todavía no configura las cuentas para pólizas automáticas ({string.Join(", ", missing)}). " +
        "Pídele que las capture en Ajustes, o liga una póliza que ya exista.");

    var formaPago = request.FormaPago?.Trim();
    if (string.IsNullOrEmpty(formaPago) || !await PaymentMethodExistsAsync(conn, rfc, formaPago, ct))
      return LogisticsCommandResult.Fail("Elige cómo se pagó la compra.");

    if (await IsPeriodClosedAsync(conn, companyId, fecha, ct))
      return LogisticsCommandResult.Fail(
        $"{Capitalize(fecha.ToString("MMMM yyyy", MexicanCulture))} ya está cerrado en contabilidad. " +
        "Elige una fecha de un mes abierto o pide a Contabilidad que lo reabra.");

    var operations = await LoadOperationsAsync(conn, companyId, request.PurchaseOrderId, ct);
    // Una póliza ligada cuya operación no se marcó completa ya cumplió su propósito; se cierra
    // para que no la retome un intento posterior con otro monto.
    foreach (var linked in operations.Where(operation => operation.IsLinked && operation.Status != AccountingOutboxStates.Completed))
      await _outbox.CompleteAsync(linked.Id, ct);
    var sequence = 1 + operations.Count(operation => operation.IsLinked || operation.Status == AccountingOutboxStates.Completed);

    var operation = await _outbox.ClaimAsync(
      AccountingOutboxModules.Purchasing,
      OperationKey(request.PurchaseOrderId, sequence),
      JsonSerializer.Serialize(new { module = "purchasing", request.PurchaseOrderId, sequence }, OutboxJsonOptions),
      ct);
    if (operation.InProgress)
      return LogisticsCommandResult.Fail("Alguien más está creando la póliza de esta compra en este momento. Espera unos segundos y vuelve a abrirla.");
    if (operation.AlreadyCompleted)
      return LogisticsCommandResult.Ok($"La póliza {operation.TransaccionId} de esta compra ya estaba creada.", operation.TransaccionId);

    int? transaccionId = operation.TransaccionId;
    try
    {
      // Reclamada la operación, nadie más genera para esta compra; se relee el saldo por si
      // alguien ligó otra póliza mientras tanto.
      order = await LoadOrderAsync(conn, rfc, request.PurchaseOrderId, ct)
        ?? throw new PurchaseAccountingException("No encontramos esta compra o no tienes acceso a ella.");
      var amounts = PurchasePolicyAmounts.ForPending(order.RecibidoSubtotal, order.Recibido, order.PorContabilizar);
      if (amounts.Total <= 0m)
        throw new PurchaseAccountingException("Esta compra ya está registrada completa en contabilidad.");

      var subtotalAccount = RequireAccount(defaults, CfdiPolizaCuentaDefaultRoles.SubtotalGasto);
      var ivaAccount = RequireAccount(defaults, CfdiPolizaCuentaDefaultRoles.IvaAcreditable);
      var totalAccount = RequireAccount(defaults, CfdiPolizaCuentaDefaultRoles.TotalGasto);
      var concept = Truncate($"COMPRA {order.PurchaseOrderCode} - {order.VendorName}", ConceptMaxLength);
      var memo = $"Generada desde Compras para la orden {order.PurchaseOrderCode}. Usuario: {userName ?? "OrionERP"}.";

      var resumed = transaccionId is > 0 && await PolicyExistsAsync(conn, rfc, transaccionId.Value, ct);
      if (!resumed)
      {
        if (transaccionId is > 0) await _outbox.ForgetPolicyAsync(operation.Id, ct);
        var created = await _transacciones.CreateTransaccionAsync(new TransaccionCreateRequest
        {
          Rfc = rfc,
          Fecha = fecha,
          Concepto = concept,
          Monto = amounts.Total,
          TipoPoliza = PolicyType,
          FormaPago = formaPago,
          Facturado = false,
          Memo = memo,
          Cuenta = AccountCode(totalAccount)
        }, ct);
        if (!created.Success || created.NewTransaccionId <= 0)
          throw new PurchaseAccountingException("No se pudo crear la póliza. Intenta de nuevo.");
        transaccionId = created.NewTransaccionId;
        // El rastro se graba antes de seguir: es lo que permite retomar en vez de duplicar.
        await _outbox.RecordPolicyAsync(operation.Id, transaccionId.Value, ct);
      }

      var saved = await _transacciones.GuardarMovimientosAsync(new TransaccionMovimientosUpdateRequest
      {
        TransaccionId = transaccionId!.Value,
        Movimientos = BuildMovements(amounts, concept, subtotalAccount, ivaAccount, totalAccount)
      }, ct);
      if (!saved.Success) throw new PurchaseAccountingException(saved.Message);

      var closed = await _transacciones.GuardarYCerrarAsync(new TransaccionGuardarCerrarRequest
      {
        TransaccionId = transaccionId.Value,
        Concepto = concept,
        Fecha = fecha,
        Cuenta = AccountCode(totalAccount),
        Monto = amounts.Total,
        Facturado = false,
        Memo = memo,
        TipoPoliza = PolicyType,
        FormaPago = formaPago
      }, ct);
      if (!closed.Success) throw new PurchaseAccountingException(closed.Message ?? "No se pudo guardar la póliza.");

      await InsertLinkAsync(conn, rfc, request.PurchaseOrderId, transaccionId.Value, amounts.Total,
        PurchaseAccountingLinkOrigins.Generated, userName, allowExisting: true, ct);
      await _outbox.CompleteAsync(operation.Id, ct);

      return LogisticsCommandResult.Ok(
        $"Listo: se creó la póliza {transaccionId} por {Money(amounts.Total)} y quedó ligada a la compra {order.PurchaseOrderCode}.",
        transaccionId);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
      await FailOperationQuietlyAsync(operation.Id, ex.Message);
      var reason = ex switch
      {
        PurchaseAccountingException business => business.Message,
        SqlException sql when IsBusinessError(sql) => sql.Message,
        _ => null
      };
      if (reason is null)
      {
        _logger.LogError(ex, "Falló la póliza de la compra {PurchaseOrderId}", request.PurchaseOrderId);
        reason = "No se pudo terminar de registrar la póliza.";
      }

      var retryNote = transaccionId is > 0
        ? $" La póliza {transaccionId} quedó creada sin terminar; al volver a intentarlo se completa esa misma, no se crea otra."
        : string.Empty;
      return LogisticsCommandResult.Fail(reason + retryNote, transaccionId);
    }
  }

  public async Task<LogisticsCommandResult> LinkAsync(PurchaseAccountingLinkRequest request, string? userName, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(request);
    var monto = decimal.Round(request.Monto, 2, MidpointRounding.AwayFromZero);
    if (monto <= 0m) return LogisticsCommandResult.Fail("Escribe cuánto de la compra cubre esta póliza.");

    var rfc = CurrentRfc();
    await using var conn = await OpenAsync(rfc, ct);
    try
    {
      await InsertLinkAsync(conn, rfc, request.PurchaseOrderId, request.TransaccionId, monto,
        PurchaseAccountingLinkOrigins.Manual, userName, allowExisting: false, ct);
    }
    catch (SqlException ex) when (IsBusinessError(ex))
    {
      return LogisticsCommandResult.Fail(ex.Message);
    }

    return LogisticsCommandResult.Ok($"Listo: la póliza {request.TransaccionId} quedó ligada a la compra por {Money(monto)}.");
  }

  public async Task<LogisticsCommandResult> UnlinkAsync(int linkId, CancellationToken ct = default)
  {
    var rfc = CurrentRfc();
    await using var conn = await OpenAsync(rfc, ct);
    var removed = await conn.QuerySingleOrDefaultAsync<RemovedLinkRow>(new CommandDefinition(
      $"""
      DELETE link
      OUTPUT deleted.TransaccionId, deleted.Origin
      FROM logistica.PurchaseAccountingLink link
      JOIN logistica.PurchaseOrder po
        ON po.Id = link.PurchaseOrderId
       AND po.Rfc = link.Rfc
      WHERE link.Id = @LinkId
        AND link.Rfc = @Rfc
        AND {PurchaseOrderService.OrderVisibilitySql};
      """,
      new { LinkId = linkId, Rfc = rfc },
      cancellationToken: ct));

    if (removed is null) return LogisticsCommandResult.Fail("Ese vínculo ya no existe. Actualiza la pantalla.");
    return removed.Origin == PurchaseAccountingLinkOrigins.Generated
      ? LogisticsCommandResult.Ok(
        $"Se quitó el vínculo, pero la póliza {removed.TransaccionId} sigue existiendo. Si ya no corresponde, " +
        "Contabilidad debe cancelarla con una reversa para no registrar el gasto dos veces.")
      : LogisticsCommandResult.Ok($"Se quitó el vínculo con la póliza {removed.TransaccionId}.");
  }

  public async Task<PurchasePolizaLinksDto?> GetTransaccionLinksAsync(int transaccionId, CancellationToken ct = default)
  {
    var rfc = CurrentRfc();
    await using var conn = await OpenAsync(rfc, ct);
    var summary = await LoadPolizaAsync(conn, rfc, transaccionId, ct);
    if (summary is null) return null;

    using var multi = await conn.QueryMultipleAsync(new CommandDefinition(
      $"""
      SELECT
          link.Id AS LinkId,
          po.Id AS PurchaseOrderId,
          po.PurchaseOrderCode,
          bp.PartnerName AS VendorName,
          po.OrderDate,
          link.MontoAsignado,
          link.Origin
      FROM logistica.PurchaseAccountingLink link
      JOIN logistica.PurchaseOrder po
        ON po.Id = link.PurchaseOrderId
       AND po.Rfc = link.Rfc
      JOIN dbo.BusinessPartner bp
        ON bp.Id = po.BusinessPartnerId
      WHERE link.TransaccionId = @TransaccionId
        AND link.Rfc = @Rfc
        AND {PurchaseOrderService.OrderVisibilitySql}
      ORDER BY po.OrderDate DESC, link.Id DESC;

      SELECT COUNT(*)
      FROM logistica.PurchaseAccountingLink
      WHERE TransaccionId = @TransaccionId
        AND Rfc = @Rfc;
      """,
      new { TransaccionId = transaccionId, Rfc = rfc },
      cancellationToken: ct));

    var links = new PurchasePolizaLinksDto { Summary = summary };
    links.PurchaseOrders.AddRange(await multi.ReadAsync<PurchaseAccountingLinkedOrderDto>());
    links.ComprasNoVisibles = Math.Max(await multi.ReadSingleAsync<int>() - links.PurchaseOrders.Count, 0);
    return links;
  }

  public async Task<IReadOnlyList<PurchaseOrderAccountingCandidateDto>> SearchPurchaseOrderCandidatesAsync(
    int transaccionId, string? search, CancellationToken ct = default)
  {
    var rfc = CurrentRfc();
    await using var conn = await OpenAsync(rfc, ct);
    var poliza = await LoadPolizaAsync(conn, rfc, transaccionId, ct);
    if (poliza is null) return [];

    var text = search?.Trim();
    var rows = await conn.QueryAsync<PurchaseOrderAccountingCandidateDto>(new CommandDefinition(
      $"""
      SELECT TOP (@Limit)
          po.Id AS PurchaseOrderId,
          po.PurchaseOrderCode,
          bp.PartnerName AS VendorName,
          po.OrderDate,
          receiptTotals.UltimaRecepcion,
          receiptTotals.Recibido,
          linkTotals.Contabilizado,
          CAST(receiptTotals.Recibido - linkTotals.Contabilizado AS decimal(18,2)) AS PorContabilizar
      FROM logistica.PurchaseOrder po
      JOIN dbo.BusinessPartner bp
        ON bp.Id = po.BusinessPartnerId
      {OrderTotalsApply}
      WHERE po.Rfc = @Rfc
        AND po.[Status] IN @Statuses
        AND {PurchaseOrderService.OrderVisibilitySql}
        AND receiptTotals.Recibido - linkTotals.Contabilizado > 0.005
        AND NOT EXISTS
        (
            SELECT 1 FROM logistica.PurchaseAccountingLink existing
            WHERE existing.PurchaseOrderId = po.Id
              AND existing.TransaccionId = @TransaccionId
        )
        AND (@SearchLike IS NULL OR po.PurchaseOrderCode LIKE @SearchLike OR bp.PartnerName LIKE @SearchLike)
      ORDER BY
          ABS(receiptTotals.Recibido - linkTotals.Contabilizado - @Objetivo),
          ABS(DATEDIFF(DAY, ISNULL(receiptTotals.UltimaRecepcion, po.OrderDate), @Anchor)),
          po.Id DESC;
      """,
      new
      {
        Limit = CandidateLimit,
        Rfc = rfc,
        Statuses = ReceivedStatuses,
        TransaccionId = transaccionId,
        SearchLike = string.IsNullOrEmpty(text) ? null : $"%{EscapeLike(text)}%",
        Objetivo = Math.Max(poliza.Disponible, 0m),
        Anchor = poliza.Fecha.Date
      },
      cancellationToken: ct));
    return rows.AsList();
  }

  internal static string OperationKey(int purchaseOrderId, int sequence)
    => string.Create(CultureInfo.InvariantCulture, $"PO:{purchaseOrderId}:{sequence}");

  /// <summary>Un número (con o sin #) es un folio de póliza; cualquier otro texto busca en concepto y memo.</summary>
  internal static (int? Id, string? Like) ParsePolicySearch(string? search)
  {
    var text = search?.Trim();
    if (string.IsNullOrEmpty(text)) return (null, null);
    if (int.TryParse(text.TrimStart('#'), NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0)
      return (id, null);
    return (null, $"%{EscapeLike(text)}%");
  }

  private static string EscapeLike(string value)
    => value.Replace("[", "[[]", StringComparison.Ordinal)
      .Replace("%", "[%]", StringComparison.Ordinal)
      .Replace("_", "[_]", StringComparison.Ordinal);

  private string CurrentRfc() => LogisticsRfc.Require(_company.RequireRfc());

  private async Task<DbConnection> OpenAsync(string rfc, CancellationToken ct)
  {
    var conn = await LogisticsLocationScope.OpenAsync(_connectionFactory, _hospitalityScope, ct);
    try
    {
      await LogisticsLocationScope.EnsureRfcAsync(conn, null, rfc, ct);
      return conn;
    }
    catch
    {
      await conn.DisposeAsync();
      throw;
    }
  }

  private static Task<OrderRow?> LoadOrderAsync(DbConnection conn, string rfc, int purchaseOrderId, CancellationToken ct)
    => conn.QuerySingleOrDefaultAsync<OrderRow>(new CommandDefinition(
      OrderSql, new { Rfc = rfc, PurchaseOrderId = purchaseOrderId }, cancellationToken: ct));

  private static Task<PurchasePolizaSummaryDto?> LoadPolizaAsync(DbConnection conn, string rfc, int transaccionId, CancellationToken ct)
    => conn.QuerySingleOrDefaultAsync<PurchasePolizaSummaryDto>(new CommandDefinition(
      """
      SELECT
          poliza.ID AS TransaccionId,
          poliza.Fecha,
          poliza.Concepto,
          CAST(ABS(poliza.Monto) AS decimal(18,2)) AS Monto,
          assigned.Asignado
      FROM dbo.Transacciones poliza
      OUTER APPLY (
          SELECT CAST(ISNULL(SUM(link.MontoAsignado), 0) AS decimal(18,2)) AS Asignado
          FROM logistica.PurchaseAccountingLink link
          WHERE link.TransaccionId = poliza.ID
      ) assigned
      WHERE poliza.ID = @TransaccionId
        AND poliza.RFC = @Rfc;
      """,
      new { TransaccionId = transaccionId, Rfc = rfc },
      cancellationToken: ct));

  private static async Task<IReadOnlyList<OperationRow>> LoadOperationsAsync(
    DbConnection conn, long companyId, int purchaseOrderId, CancellationToken ct)
    => (await conn.QueryAsync<OperationRow>(new CommandDefinition(
      OutboxOperationsSql,
      new
      {
        CompanyId = companyId,
        SourceModule = AccountingOutboxModules.Purchasing,
        PurchaseOrderId = purchaseOrderId,
        KeyPattern = string.Create(CultureInfo.InvariantCulture, $"PO:{purchaseOrderId}:%")
      },
      cancellationToken: ct))).AsList();

  private static async Task InsertLinkAsync(
    DbConnection conn,
    string rfc,
    int purchaseOrderId,
    int transaccionId,
    decimal monto,
    string origin,
    string? userName,
    bool allowExisting,
    CancellationToken ct)
  {
    await using var tx = await conn.BeginTransactionAsync(IsolationLevel.Serializable, ct);
    try
    {
      await LogisticsLocationScope.RefreshAsync(conn, tx, ct);
      await conn.ExecuteAsync(new CommandDefinition(
        InsertLinkSql,
        new
        {
          Rfc = rfc,
          PurchaseOrderId = purchaseOrderId,
          TransaccionId = transaccionId,
          Monto = monto,
          Origin = origin,
          CreatedBy = userName,
          AllowExisting = allowExisting
        },
        tx,
        cancellationToken: ct));
      await tx.CommitAsync(ct);
    }
    catch
    {
      try { await tx.RollbackAsync(CancellationToken.None); } catch { /* la transacción ya se cerró */ }
      throw;
    }
  }

  private static Task<bool> PaymentMethodExistsAsync(DbConnection conn, string rfc, string clave, CancellationToken ct)
    => conn.ExecuteScalarAsync<bool>(new CommandDefinition(
      "SELECT CONVERT(bit, CASE WHEN EXISTS (SELECT 1 FROM dbo.Formas_Pago WHERE Rfc = @Rfc AND Clave = @Clave) THEN 1 ELSE 0 END);",
      new { Rfc = rfc, Clave = clave },
      cancellationToken: ct));

  private static Task<bool> IsPeriodClosedAsync(DbConnection conn, long companyId, DateTime fecha, CancellationToken ct)
    => conn.ExecuteScalarAsync<bool>(new CommandDefinition(
      AccountingCycleSql.PeriodClosedForDateSql,
      new { CompanyId = companyId, Fecha = fecha },
      cancellationToken: ct));

  private static Task<bool> PolicyExistsAsync(DbConnection conn, string rfc, int transaccionId, CancellationToken ct)
    => conn.ExecuteScalarAsync<bool>(new CommandDefinition(
      "SELECT CONVERT(bit, CASE WHEN EXISTS (SELECT 1 FROM dbo.Transacciones WHERE ID = @Id AND RFC = @Rfc) THEN 1 ELSE 0 END);",
      new { Id = transaccionId, Rfc = rfc },
      cancellationToken: ct));

  private static IReadOnlyList<string> MissingAccounts(CfdiPolizaCuentaDefaultsDto defaults)
    => RequiredAccountRoles
      .Where(role => FindAccount(defaults, role) is null)
      .Select(role => CfdiPolizaCuentaDefaultRoles.Required.First(required => required.CuentaClave == role).Nombre)
      .ToList();

  /// <summary>Mismo criterio que el procedimiento de pólizas CFDI: los tres niveles deben existir.</summary>
  private static CfdiPolizaCuentaDefaultAccountDto? FindAccount(CfdiPolizaCuentaDefaultsDto defaults, string role)
    => defaults.Cuentas.FirstOrDefault(account =>
      string.Equals(account.CuentaClave, role, StringComparison.OrdinalIgnoreCase)
      && account.CuentaContableId is > 0
      && !string.IsNullOrWhiteSpace(account.Nivel1)
      && !string.IsNullOrWhiteSpace(account.Nivel2)
      && !string.IsNullOrWhiteSpace(account.Nivel3));

  private static CfdiPolizaCuentaDefaultAccountDto RequireAccount(CfdiPolizaCuentaDefaultsDto defaults, string role)
    => FindAccount(defaults, role)
      ?? throw new PurchaseAccountingException("Contabilidad cambió las cuentas de Ajustes mientras se creaba la póliza. Intenta de nuevo.");

  private static List<TransaccionMovimientoUpdateItem> BuildMovements(
    PurchasePolicyAmounts amounts,
    string concept,
    CfdiPolizaCuentaDefaultAccountDto subtotalAccount,
    CfdiPolizaCuentaDefaultAccountDto ivaAccount,
    CfdiPolizaCuentaDefaultAccountDto totalAccount)
  {
    var movements = new List<TransaccionMovimientoUpdateItem>();
    if (amounts.Subtotal > 0m) movements.Add(Movement(subtotalAccount, concept, amounts.Subtotal, 0m));
    if (amounts.Iva > 0m) movements.Add(Movement(ivaAccount, concept, amounts.Iva, 0m));
    movements.Add(Movement(totalAccount, concept, 0m, amounts.Total));
    return movements;
  }

  private static TransaccionMovimientoUpdateItem Movement(
    CfdiPolizaCuentaDefaultAccountDto account, string concept, decimal debe, decimal haber)
    => new()
    {
      CuentaId = account.CuentaContableId,
      Nivel1 = account.Nivel1,
      Nivel2 = account.Nivel2,
      Nivel3 = account.Nivel3,
      NombreCuenta = Truncate(string.IsNullOrWhiteSpace(account.CuentaDescripcion) ? AccountCode(account) : account.CuentaDescripcion, MovementTextMaxLength),
      Concepto = Truncate(concept, MovementTextMaxLength),
      Debe = debe,
      Haber = haber
    };

  private static string AccountCode(CfdiPolizaCuentaDefaultAccountDto account)
    => $"{account.Nivel1}.{account.Nivel2}.{account.Nivel3}";

  private static bool IsBusinessError(SqlException ex)
    => ex.Number is >= FirstBusinessError and <= LastBusinessError;

  private async Task FailOperationQuietlyAsync(long operationId, string reason)
  {
    try
    {
      await _outbox.FailAsync(operationId, reason, CancellationToken.None);
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "No se pudo registrar el fallo de la operación contable de compras {OperationId}", operationId);
    }
  }

  private static string Truncate(string value, int maxLength)
    => value.Length <= maxLength ? value : value[..maxLength];

  private static string Money(decimal amount) => amount.ToString("C2", MexicanCulture);

  private static string Capitalize(string value)
    => string.IsNullOrEmpty(value) ? value : char.ToUpper(value[0], MexicanCulture) + value[1..];

  private sealed class PurchaseAccountingException(string message) : Exception(message);

  private sealed class OrderRow
  {
    public int PurchaseOrderId { get; set; }
    public string PurchaseOrderCode { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime OrderDate { get; set; }
    public string VendorName { get; set; } = string.Empty;
    public string? VendorRfc { get; set; }
    public decimal Recibido { get; set; }
    public decimal RecibidoSubtotal { get; set; }
    public DateTime? UltimaRecepcion { get; set; }
    public decimal Contabilizado { get; set; }

    public decimal PorContabilizar => Recibido - Contabilizado > CentTolerance ? Recibido - Contabilizado : 0m;
  }

  private sealed class OperationRow
  {
    public long Id { get; set; }
    public string Status { get; set; } = string.Empty;
    public int? TransaccionId { get; set; }
    public bool IsLinked { get; set; }
  }

  private sealed class RemovedLinkRow
  {
    public int TransaccionId { get; set; }
    public string Origin { get; set; } = string.Empty;
  }
}
