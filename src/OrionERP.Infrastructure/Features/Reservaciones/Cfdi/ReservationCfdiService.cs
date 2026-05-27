using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OrionERP.Application.Common;
using OrionERP.Application.Features.Cfdi.Facturama;
using OrionERP.Application.Features.Contabilidad.Transacciones;
using OrionERP.Application.Features.Reservaciones.Cfdi;
using OrionERP.Application.Features.Reservaciones.ListaReservaciones;
using OrionERP.Application.Features.Rfcs.Contracts;
using OrionERP.Infrastructure.Features.Cfdi.Facturama;

namespace OrionERP.Infrastructure.Features.Reservaciones.Cfdi;

public sealed class ReservationCfdiService : IReservationCfdiService
{
  private const decimal CurrencyTolerance = 0.05m;
  private const string DefaultPaymentForm = "03";
  private const string DeferredPaymentForm = "99";
  private const string DefaultPaymentMethod = "PUE";
  private const string DeferredPaymentMethod = "PPD";
  private const string DefaultCurrency = "MXN";
  private const int CustomerSuggestionLimit = 12;

  private readonly IDbConnectionFactory _connectionFactory;
  private readonly IConfiguration _configuration;
  private readonly IListaReservacionesService _reservacionesService;
  private readonly ITransaccionService _transaccionService;
  private readonly IFacturamaApiClient _facturamaApiClient;
  private readonly ISatRfcProfileRepository _satRfcProfileRepository;
  private readonly ICfdiStampingService _cfdiStampingService;
  private readonly ILogger<ReservationCfdiService> _logger;

  public ReservationCfdiService(
      IDbConnectionFactory connectionFactory,
      IConfiguration configuration,
      IListaReservacionesService reservacionesService,
      ITransaccionService transaccionService,
      IFacturamaApiClient facturamaApiClient,
      ISatRfcProfileRepository satRfcProfileRepository,
      ICfdiStampingService cfdiStampingService,
      ILogger<ReservationCfdiService> logger)
  {
    _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    _reservacionesService = reservacionesService ?? throw new ArgumentNullException(nameof(reservacionesService));
    _transaccionService = transaccionService ?? throw new ArgumentNullException(nameof(transaccionService));
    _facturamaApiClient = facturamaApiClient ?? throw new ArgumentNullException(nameof(facturamaApiClient));
    _satRfcProfileRepository = satRfcProfileRepository ?? throw new ArgumentNullException(nameof(satRfcProfileRepository));
    _cfdiStampingService = cfdiStampingService ?? throw new ArgumentNullException(nameof(cfdiStampingService));
    _logger = logger ?? throw new ArgumentNullException(nameof(logger));
  }

  public async Task<ReservationCfdiContextDto?> GetContextAsync(
      int reservationId,
      string issuerRfc,
      CancellationToken ct = default)
  {
    if (reservationId <= 0)
      throw new ArgumentOutOfRangeException(nameof(reservationId));

    issuerRfc = NormalizeRfc(issuerRfc);
    if (string.IsNullOrWhiteSpace(issuerRfc))
      throw new InvalidOperationException("Selecciona un RFC antes de preparar el CFDI.");

    var detail = await _reservacionesService.GetReservacionDetailAsync(reservationId, ct)
        ?? throw new InvalidOperationException("No se encontró la reservación seleccionada.");

    var suiteSources = await GetSuiteSourcesAsync(reservationId, ct);
    var extraSources = detail.Extras
        .Select(extra => new ReservationCfdiExtraSource
        {
          Id = extra.Id,
          CatalogName = extra.RoomName,
          Description = extra.RoomDescription,
          Amount = extra.Price,
          Notes = extra.Notes
        })
        .ToArray();

    var items = ReservationCfdiLineFactory.CreateItems(
      suiteSources,
      extraSources,
      detail.SuiteDiscountPercent);
    ValidateReservationTotals(detail, items);

    var searchSeed = string.IsNullOrWhiteSpace(detail.Cliente) ? null : detail.Cliente;
    var suggestedCustomers = await SearchCustomersAsync(searchSeed, ct);
    var polizaOptions = await GetPolizaOptionsAsync(reservationId, issuerRfc, detail.TotalPrice, ct);
    var existingDocuments = await GetExistingDocumentsAsync(reservationId, ct);
    var clienteEmail = await GetReservationCustomerEmailAsync(detail.ClienteId, ct);
    var autoSelectedTransaccionId = GetSingleEligiblePolizaId(polizaOptions);

    return new ReservationCfdiContextDto
    {
      ReservationId = detail.Id,
      ReservationCliente = detail.Cliente,
      RequiresCfdi = detail.RequiresCfdi,
      TotalSuites = detail.TotalSuites,
      SuiteDiscountPercent = detail.SuiteDiscountPercent,
      SuiteDiscountAmount = detail.SuiteDiscountAmount,
      TotalExtras = detail.TotalExtras,
      SubTotal = detail.SubTotal,
      Tax = detail.Tax,
      Ish = detail.Ish,
      TotalReservacion = detail.TotalPrice,
      AutoSelectedTransaccionId = autoSelectedTransaccionId,
      PolizaOptions = polizaOptions,
      Items = items,
      SuggestedCustomers = suggestedCustomers,
      ExistingDocuments = existingDocuments,
      ReceiverDraft = BuildReceiverDraft(detail, clienteEmail, suggestedCustomers)
    };
  }

  public async Task<ReservationFacturacionStatusDto> GetFacturacionStatusAsync(
      int reservationId,
      CancellationToken ct = default)
  {
    if (reservationId <= 0)
      throw new ArgumentOutOfRangeException(nameof(reservationId));

    const string sql = """
SELECT
    rt.TransaccionID AS TransaccionId,
    t.Fecha,
    ISNULL(t.Concepto, '') AS Concepto,
    CAST(ISNULL(rt.Amount, ISNULL(t.Monto, 0)) AS decimal(18,2)) AS Monto
FROM dbo.Reservation_Transacciones rt
LEFT JOIN dbo.Transacciones t
    ON t.ID = rt.TransaccionID
WHERE rt.ReservationID = @ReservationId
ORDER BY t.Fecha DESC, rt.TransaccionID DESC;

WITH ReservationPayments AS
(
    SELECT DISTINCT rt.TransaccionID AS TransaccionId
    FROM dbo.Reservation_Transacciones rt
    WHERE rt.ReservationID = @ReservationId
),
Evidence AS
(
    SELECT
        rp.TransaccionId,
        CAST('CFDI' AS varchar(20)) AS EvidenceType,
        CAST(c.Comprobante_Id AS bigint) AS ComprobanteId,
        CAST(NULL AS int) AS DoctoRelacionadoId,
        CAST(c.Fecha AS datetime) AS Fecha,
        CAST(tfd.UUID AS varchar(100)) AS Uuid,
        CAST(tc.Monto AS decimal(18,2)) AS Amount
    FROM ReservationPayments rp
    INNER JOIN dbo.Transaccion_Comprobante tc
        ON tc.Transaccion_ID = rp.TransaccionId
    INNER JOIN cfdi.Comprobante c
        ON c.Comprobante_Id = tc.Comprobante_ID
    LEFT JOIN cfdi.TimbreFiscalDigital tfd
        ON tfd.Comprobante_ID = c.Comprobante_Id
    WHERE ISNULL(c.TipoDeComprobante, '') <> 'P'
      AND c.FechaCancelacion IS NULL
      AND ISNULL(c.Estatus, '') NOT LIKE 'Cancel%'

    UNION ALL

    SELECT
        rp.TransaccionId,
        CAST('Pago20' AS varchar(20)) AS EvidenceType,
        CAST(c.Comprobante_Id AS bigint) AS ComprobanteId,
        CAST(NULL AS int) AS DoctoRelacionadoId,
        CAST(c.Fecha AS datetime) AS Fecha,
        CAST(tfd.UUID AS varchar(100)) AS Uuid,
        CAST(tc.Monto AS decimal(18,2)) AS Amount
    FROM ReservationPayments rp
    INNER JOIN dbo.Transaccion_Comprobante tc
        ON tc.Transaccion_ID = rp.TransaccionId
    INNER JOIN cfdi.Comprobante c
        ON c.Comprobante_Id = tc.Comprobante_ID
    INNER JOIN cfdi.Pagos20 p20
        ON p20.Comprobante_Id = c.Comprobante_Id
    LEFT JOIN cfdi.TimbreFiscalDigital tfd
        ON tfd.Comprobante_ID = c.Comprobante_Id
    WHERE c.TipoDeComprobante = 'P'
      AND c.FechaCancelacion IS NULL
      AND ISNULL(c.Estatus, '') NOT LIKE 'Cancel%'

    UNION ALL

    SELECT
        rp.TransaccionId,
        CAST('Pago20' AS varchar(20)) AS EvidenceType,
        CAST(c.Comprobante_Id AS bigint) AS ComprobanteId,
        dr.DoctoRelacionado_Id AS DoctoRelacionadoId,
        CAST(COALESCE(p.FechaPago, c.Fecha) AS datetime) AS Fecha,
        CAST(tfd.UUID AS varchar(100)) AS Uuid,
        CAST(ISNULL(td.Monto, dr.ImpPagado) AS decimal(18,2)) AS Amount
    FROM ReservationPayments rp
    INNER JOIN dbo.Transaccion_DoctoRelacionado td
        ON td.Transaccion_ID = rp.TransaccionId
    INNER JOIN cfdi.Pagos20_DoctoRelacionado dr
        ON dr.DoctoRelacionado_Id = td.DoctoRelacionado_Id
    INNER JOIN cfdi.Pagos20_Pago p
        ON p.Pago_Id = dr.Pago_Id
    INNER JOIN cfdi.Pagos20 p20
        ON p20.Pagos20_Id = p.Pagos20_Id
    INNER JOIN cfdi.Comprobante c
        ON c.Comprobante_Id = p20.Comprobante_Id
    LEFT JOIN cfdi.TimbreFiscalDigital tfd
        ON tfd.Comprobante_ID = c.Comprobante_Id
    WHERE c.FechaCancelacion IS NULL
      AND ISNULL(c.Estatus, '') NOT LIKE 'Cancel%'
)
SELECT DISTINCT
    TransaccionId,
    EvidenceType,
    ComprobanteId,
    DoctoRelacionadoId,
    Fecha,
    Uuid,
    Amount
FROM Evidence
ORDER BY TransaccionId, EvidenceType, Fecha DESC, ComprobanteId DESC, DoctoRelacionadoId DESC;
""";

    await using var conn = CreateConnection();
    using var multi = await conn.QueryMultipleAsync(
        new CommandDefinition(sql, new { ReservationId = reservationId }, cancellationToken: ct));

    var payments = (await multi.ReadAsync<ReservationPaymentFacturacionStatusDto>()).AsList();
    var documents = (await multi.ReadAsync<ReservationPaymentFacturacionDocumentDto>()).AsList();

    foreach (var payment in payments)
    {
      var paymentDocuments = documents
          .Where(document => document.TransaccionId == payment.TransaccionId)
          .ToArray();

      payment.RegularCfdiCount = paymentDocuments
          .Where(static document => string.Equals(document.EvidenceType, "CFDI", StringComparison.OrdinalIgnoreCase))
          .Select(static document => document.ComprobanteId)
          .Distinct()
          .Count();

      payment.Pago20Count = paymentDocuments
          .Where(static document => string.Equals(document.EvidenceType, "Pago20", StringComparison.OrdinalIgnoreCase))
          .Select(static document => document.ComprobanteId)
          .Distinct()
          .Count();

      payment.Documents = paymentDocuments;
    }

    return ReservationFacturacionStatusCalculator.Calculate(payments);
  }

  public async Task<IReadOnlyList<ReservationCfdiCustomerSuggestionDto>> SearchCustomersAsync(
      string? searchText,
      CancellationToken ct = default)
  {
    var normalizedSearch = string.IsNullOrWhiteSpace(searchText) ? null : searchText.Trim();

    await using var conn = CreateConnection();
    await conn.OpenAsync(ct);

    var persisted = await GetPersistedCustomersAsync(conn, normalizedSearch, CustomerSuggestionLimit, ct);
    var historical = await GetHistoricalCustomersAsync(conn, normalizedSearch, CustomerSuggestionLimit, ct);

    return MergeSuggestions(normalizedSearch, persisted, historical, CustomerSuggestionLimit);
  }

  public async Task<ReservationCfdiReceiverValidationDto> ValidateReceiverAsync(
      ReservationCfdiCustomerUpsertRequest request,
      CancellationToken ct = default)
  {
    if (request is null)
      throw new ArgumentNullException(nameof(request));

    var normalized = NormalizeReceiver(request);
    return await ValidateReceiverCoreAsync(normalized, ct);
  }

  public async Task<ReservationCfdiCustomerSaveResult> SaveCustomerAsync(
      ReservationCfdiCustomerUpsertRequest request,
      CancellationToken ct = default)
  {
    if (request is null)
      throw new ArgumentNullException(nameof(request));

    try
    {
      var normalized = NormalizeReceiver(request);

      await using var conn = CreateConnection();
      await conn.OpenAsync(ct);

      if (!await HasCfdiProfileTableAsync(conn, ct))
      {
        return ReservationCfdiCustomerSaveResult.Fail(
            "Falta ejecutar el script de base de datos para dbo.BusinessPartnerCfdiProfile antes de guardar clientes fiscales.");
      }

      await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

      try
      {
        var businessPartnerId = await ResolveBusinessPartnerIdAsync(conn, tx, normalized.BusinessPartnerId, normalized.Rfc, ct);
        var partnerName = FirstNonEmpty(normalized.DisplayName, normalized.FiscalName);

        if (businessPartnerId.HasValue)
        {
          const string updateSql = """
UPDATE dbo.BusinessPartner
SET PartnerName = @PartnerName,
    Rfc = @Rfc,
    Email = COALESCE(@Email, Email),
    PostalCode = COALESCE(@PostalCode, PostalCode),
    IsActive = 1,
    UpdatedAt = SYSUTCDATETIME()
WHERE Id = @BusinessPartnerId;
""";

          await conn.ExecuteAsync(
              new CommandDefinition(
                  updateSql,
                  new
                  {
                    BusinessPartnerId = businessPartnerId.Value,
                    PartnerName = partnerName,
                    normalized.Rfc,
                    Email = NullIfWhiteSpace(normalized.Email),
                    PostalCode = normalized.TaxZipCode
                  },
                  tx,
                  cancellationToken: ct));
        }
        else
        {
          const string insertSql = """
INSERT INTO dbo.BusinessPartner
(
    PartnerName,
    Rfc,
    Email,
    PostalCode,
    IsActive
)
VALUES
(
    @PartnerName,
    @Rfc,
    @Email,
    @PostalCode,
    1
);

SELECT CAST(SCOPE_IDENTITY() AS int);
""";

          businessPartnerId = await conn.ExecuteScalarAsync<int>(
              new CommandDefinition(
                  insertSql,
                  new
                  {
                    PartnerName = partnerName,
                    normalized.Rfc,
                    Email = NullIfWhiteSpace(normalized.Email),
                    PostalCode = normalized.TaxZipCode
                  },
                  tx,
                  cancellationToken: ct));
        }

        const string ensureCustomerRoleSql = """
IF NOT EXISTS (
    SELECT 1
    FROM dbo.BusinessPartnerRole
    WHERE BusinessPartnerId = @BusinessPartnerId
      AND RoleCode = 'Customer'
)
BEGIN
    INSERT INTO dbo.BusinessPartnerRole (BusinessPartnerId, RoleCode)
    VALUES (@BusinessPartnerId, 'Customer');
END;
""";

        await conn.ExecuteAsync(
            new CommandDefinition(
                ensureCustomerRoleSql,
                new { BusinessPartnerId = businessPartnerId!.Value },
                tx,
                cancellationToken: ct));

        const string mergeProfileSql = """
MERGE dbo.BusinessPartnerCfdiProfile AS target
USING (SELECT @BusinessPartnerId AS BusinessPartnerId) AS src
ON target.BusinessPartnerId = src.BusinessPartnerId
WHEN MATCHED THEN
    UPDATE SET
        FiscalName = @FiscalName,
        TaxZipCode = @TaxZipCode,
        FiscalRegime = @FiscalRegime,
        DefaultCfdiUse = @DefaultCfdiUse,
        UpdatedAt = SYSUTCDATETIME()
WHEN NOT MATCHED THEN
    INSERT (BusinessPartnerId, FiscalName, TaxZipCode, FiscalRegime, DefaultCfdiUse)
    VALUES (@BusinessPartnerId, @FiscalName, @TaxZipCode, @FiscalRegime, @DefaultCfdiUse);
""";

        await conn.ExecuteAsync(
            new CommandDefinition(
                mergeProfileSql,
                new
                {
                  BusinessPartnerId = businessPartnerId.Value,
                  normalized.FiscalName,
                  normalized.TaxZipCode,
                  normalized.FiscalRegime,
                  DefaultCfdiUse = normalized.CfdiUse
                },
                tx,
                cancellationToken: ct));

        await tx.CommitAsync(ct);

        return ReservationCfdiCustomerSaveResult.Ok(
            $"Cliente fiscal {normalized.FiscalName} guardado correctamente.",
            businessPartnerId.Value);
      }
      catch
      {
        await tx.RollbackAsync(ct);
        throw;
      }
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Failed to save fiscal customer RFC {Rfc}", request.Rfc);
      return ReservationCfdiCustomerSaveResult.Fail(ex.Message, request.BusinessPartnerId);
    }
  }

  public async Task<TransaccionCommandResult> ApplyAirbnbAccountingAsync(
      ReservationAirbnbAccountingRequest request,
      CancellationToken ct = default)
  {
    if (request is null)
      throw new ArgumentNullException(nameof(request));

    if (request.ReservationId <= 0)
      return TransaccionCommandResult.Fail("La reservación seleccionada no es válida.");

    if (request.TransaccionId <= 0)
      return TransaccionCommandResult.Fail("La póliza seleccionada no es válida.");

    var issuerRfc = NormalizeRfc(request.IssuerRfc);
    if (string.IsNullOrWhiteSpace(issuerRfc))
      return TransaccionCommandResult.Fail("Selecciona un RFC emisor antes de generar la póliza Airbnb.");

    try
    {
      var detail = await _reservacionesService.GetReservacionDetailAsync(request.ReservationId, ct);
      if (detail is null)
      {
        return TransaccionCommandResult.Fail("No se encontró la reservación seleccionada.");
      }

      if (detail.AirbnbBreakdown is null)
      {
        return TransaccionCommandResult.Ok("La reservación no tiene desglose Airbnb.");
      }

      return await SaveAirbnbAccountingAsync(request.TransaccionId, issuerRfc, detail, ct);
    }
    catch (Exception ex)
    {
      _logger.LogError(
          ex,
          "Failed to apply Airbnb accounting for reservation {ReservationId} and transaction {TransaccionId}",
          request.ReservationId,
          request.TransaccionId);

      return TransaccionCommandResult.Fail($"No se pudo generar la póliza contable Airbnb: {ex.Message}");
    }
  }

  public async Task<ReservationCfdiCreateResult> CreateCfdiAsync(
      ReservationCfdiCreateRequest request,
      CancellationToken ct = default)
  {
    if (request is null)
      throw new ArgumentNullException(nameof(request));

    if (request.ReservationId <= 0)
      return ReservationCfdiCreateResult.Fail("La reservación seleccionada no es válida.");

    var issuerRfc = NormalizeRfc(request.IssuerRfc);
    if (string.IsNullOrWhiteSpace(issuerRfc))
      return ReservationCfdiCreateResult.Fail("Selecciona un RFC emisor antes de timbrar.");

    try
    {
      var detail = await _reservacionesService.GetReservacionDetailAsync(request.ReservationId, ct);
      if (detail is null)
      {
        return ReservationCfdiCreateResult.Fail("No se encontró la reservación seleccionada.");
      }

      if (!detail.RequiresCfdi)
      {
        return ReservationCfdiCreateResult.Fail("La reservación no está marcada como Requiere CFDI.");
      }

      if (detail.Ish > 0.009m)
      {
        return ReservationCfdiCreateResult.Fail(
            "La reservación incluye ISH y esa composición todavía no está soportada en este flujo de CFDI.");
      }

      var suiteSources = await GetSuiteSourcesAsync(request.ReservationId, ct);
      var extraSources = detail.Extras
          .Select(extra => new ReservationCfdiExtraSource
          {
            Id = extra.Id,
            CatalogName = extra.RoomName,
            Description = extra.RoomDescription,
            Amount = extra.Price,
            Notes = extra.Notes
          })
          .ToArray();

      var items = ReservationCfdiLineFactory.CreateItems(
        suiteSources,
        extraSources,
        detail.SuiteDiscountPercent);
      ValidateReservationTotals(detail, items);

      var facturacionStatus = await GetFacturacionStatusAsync(request.ReservationId, ct);
      if (facturacionStatus.HasAnyFacturacionEvidence)
      {
        return ReservationCfdiCreateResult.Fail(
            "La reservación ya tiene pagos con CFDI o Pago20 ligado. Revisa la facturación existente antes de generar otro.");
      }

      var normalizedReceiver = NormalizeReceiver(request.Receiver);
      var paymentSelection = NormalizePaymentSelection(request.FormaPago, request.MetodoPago);
      await EnsureReceiverIsValidAsync(normalizedReceiver, ct);

      if (request.PersistCustomer)
      {
        var saveResult = await SaveCustomerAsync(normalizedReceiver, ct);
        if (!saveResult.Success || !saveResult.BusinessPartnerId.HasValue)
        {
          return ReservationCfdiCreateResult.Fail(saveResult.Message);
        }

        normalizedReceiver.BusinessPartnerId = saveResult.BusinessPartnerId;
      }

      var target = await ResolveTargetTransaccionAsync(detail, issuerRfc, request, paymentSelection.PaymentForm, ct);
      var linkResult = await _transaccionService.UpsertReservacionLinkAsync(new TransaccionReservacionLinkUpsertRequest
      {
        ReservationId = detail.Id,
        TransaccionId = target.TransaccionId,
        Amount = detail.TotalPrice
      }, ct);

      if (!linkResult.Success)
      {
        return ReservationCfdiCreateResult.Fail(linkResult.Message ?? "No se pudo ligar la póliza a la reservación.", target.TransaccionId);
      }

      if (detail.AirbnbBreakdown is not null)
      {
        var accountingResult = await SaveAirbnbAccountingAsync(target.TransaccionId, issuerRfc, detail, ct);
        if (!accountingResult.Success)
        {
          return ReservationCfdiCreateResult.Fail(
              accountingResult.Message ?? "No se pudo generar la póliza contable Airbnb.",
              target.TransaccionId);
        }
      }

      var expeditionZipCode = await ResolveIssuerTaxZipCodeAsync(issuerRfc, ct);
      var payload = BuildReservationPayload(
          target.TransaccionId,
          target.FormaPago,
          paymentSelection.PaymentMethod,
          expeditionZipCode,
          normalizedReceiver,
          items);

      CfdiStampResult stampResult;
      try
      {
        stampResult = await _cfdiStampingService.StampIssuedCfdiAsync(
            new CfdiStampRequest
            {
              TransaccionId = target.TransaccionId,
              AttachmentLabel = $"RESERVACION {detail.Id}",
              Payload = payload
            },
            ct);
      }
      catch (CfdiStampingException ex)
      {
        if (!string.IsNullOrWhiteSpace(ex.FacturamaCfdiId))
        {
          return ReservationCfdiCreateResult.Fail(
              $"El CFDI se timbró en Facturama ({ex.FacturamaCfdiId}), pero no se pudo completar el registro local: {ex.InnerException?.Message ?? ex.Message}",
              target.TransaccionId);
        }

        return ReservationCfdiCreateResult.Fail(
            $"No se pudo timbrar el CFDI en Facturama: {ex.InnerException?.Message ?? ex.Message}",
            target.TransaccionId);
      }

      var successMessage = stampResult.ComprobanteId.HasValue
          ? $"CFDI creado y ligado a la póliza {target.TransaccionId}. Comprobante local: {stampResult.ComprobanteId.Value}."
          : $"CFDI creado y ligado a la póliza {target.TransaccionId}.";

      return ReservationCfdiCreateResult.Ok(successMessage, target.TransaccionId);
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Failed to create CFDI for reservation {ReservationId}", request.ReservationId);
      return ReservationCfdiCreateResult.Fail($"No se pudo crear el CFDI de la reservación: {ex.Message}");
    }
  }

  private async Task<ResolvedTransaccionTarget> ResolveTargetTransaccionAsync(
      ReservacionDetailDto detail,
      string issuerRfc,
      ReservationCfdiCreateRequest request,
      string requestedPaymentForm,
      CancellationToken ct)
  {
    var polizaOptions = await GetPolizaOptionsAsync(detail.Id, issuerRfc, detail.TotalPrice, ct);

    if (request.CreateNewPoliza)
    {
      var autoSelectedTransaccionId = GetSingleEligiblePolizaId(polizaOptions);
      if (autoSelectedTransaccionId.HasValue)
      {
        return await ResolveExistingTransaccionAsync(
            detail,
            requestedPaymentForm,
            autoSelectedTransaccionId.Value,
            polizaOptions,
            ct);
      }

      return await CreateTransaccionAsync(detail, issuerRfc, requestedPaymentForm, ct);
    }

    var selectedTransaccionId = request.TransaccionId;
    if (!selectedTransaccionId.HasValue)
    {
      selectedTransaccionId = GetSingleEligiblePolizaId(polizaOptions);
    }

    if (!selectedTransaccionId.HasValue)
    {
      throw new InvalidOperationException("Selecciona una póliza elegible o crea una nueva para emitir el CFDI.");
    }

    return await ResolveExistingTransaccionAsync(
        detail,
        requestedPaymentForm,
        selectedTransaccionId.Value,
        polizaOptions,
        ct);
  }

  private async Task<ResolvedTransaccionTarget> ResolveExistingTransaccionAsync(
      ReservacionDetailDto detail,
      string requestedPaymentForm,
      int selectedTransaccionId,
      IReadOnlyList<ReservationCfdiPolizaOptionDto> polizaOptions,
      CancellationToken ct)
  {
    var selected = polizaOptions.FirstOrDefault(option => option.TransaccionId == selectedTransaccionId);
    if (selected is null)
    {
      throw new InvalidOperationException("La póliza seleccionada no está ligada a la reservación actual.");
    }

    if (!selected.IsEligible)
    {
      throw new InvalidOperationException("La póliza seleccionada no es elegible para timbrar esta reservación.");
    }

    var transaccion = await GetTransaccionTargetAsync(selected.TransaccionId, detail.Id, ct)
        ?? throw new InvalidOperationException("No se pudo cargar la póliza seleccionada.");

    var currentPaymentForm = string.IsNullOrWhiteSpace(transaccion.FormaPago)
        ? DefaultPaymentForm
        : transaccion.FormaPago.Trim();

    if (!string.Equals(currentPaymentForm, requestedPaymentForm, StringComparison.OrdinalIgnoreCase))
    {
      await UpdateTransaccionPaymentFormAsync(transaccion.TransaccionId, requestedPaymentForm, ct);
      currentPaymentForm = requestedPaymentForm;
    }

    return new ResolvedTransaccionTarget(
        transaccion.TransaccionId,
        currentPaymentForm,
        false);
  }

  private static int? GetSingleEligiblePolizaId(IReadOnlyList<ReservationCfdiPolizaOptionDto> polizaOptions)
  {
    var eligible = polizaOptions
        .Where(static option => option.IsEligible)
        .Select(static option => option.TransaccionId)
        .Take(2)
        .ToArray();

    return eligible.Length == 1 ? eligible[0] : null;
  }

  private async Task<ResolvedTransaccionTarget> CreateTransaccionAsync(
      ReservacionDetailDto detail,
      string issuerRfc,
      string paymentForm,
      CancellationToken ct)
  {
    var cliente = string.IsNullOrWhiteSpace(detail.Cliente) ? "(Sin cliente)" : detail.Cliente.Trim();

    var createResult = await _transaccionService.CreateTransaccionAsync(new TransaccionCreateRequest
    {
      Rfc = issuerRfc,
      Fecha = DateTime.Now,
      Concepto = $"PAGO POR RESERVACION#{detail.Id} - {cliente}",
      CategoriaId = 19,
      Monto = detail.TotalPrice,
      Cuenta = "ORION HABITAT DE MEXICO",
      TipoPoliza = "INGRESO",
      FormaPago = paymentForm
    }, ct);

    if (!createResult.Success || createResult.NewTransaccionId <= 0)
    {
      throw new InvalidOperationException(createResult.Message ?? "No se pudo crear la póliza para la reservación.");
    }

    return new ResolvedTransaccionTarget(createResult.NewTransaccionId, paymentForm, true);
  }

  private async Task<TransaccionCommandResult> SaveAirbnbAccountingAsync(
      int transaccionId,
      string issuerRfc,
      ReservacionDetailDto detail,
      CancellationToken ct)
  {
    var breakdown = detail.AirbnbBreakdown
        ?? throw new InvalidOperationException("La reservación no tiene desglose Airbnb.");

    var concept = BuildReservationConcept(detail);
    var accounts = await GetAirbnbAccountsAsync(issuerRfc, ct);
    var movimientos = new List<TransaccionMovimientoUpdateItem>();

    AddMovimiento(movimientos, accounts.Bank, concept, breakdown.PayoutAmount, debe: true);
    AddMovimiento(movimientos, accounts.IvaRetained, concept, breakdown.IvaRetainedAmount, debe: true);
    AddMovimiento(movimientos, accounts.IsrRetained, concept, breakdown.IsrRetainedAmount, debe: true);
    AddMovimiento(movimientos, accounts.AirbnbCommission, concept, breakdown.HostServiceFeeTotalAmount, debe: true);
    AddMovimiento(movimientos, accounts.IvaTransferred, concept, breakdown.IvaTransferredAmount, debe: false);
    AddMovimiento(movimientos, accounts.Income, concept, breakdown.TaxableBase, debe: false);

    var debe = RoundCurrency(movimientos.Sum(static item => item.Debe));
    var haber = RoundCurrency(movimientos.Sum(static item => item.Haber));
    if (debe != haber)
    {
      return TransaccionCommandResult.Fail(
          $"El desglose Airbnb no balancea la póliza. Debe {debe:N2}, Haber {haber:N2}.");
    }

    return await _transaccionService.GuardarMovimientosAsync(
        new TransaccionMovimientosUpdateRequest
        {
          TransaccionId = transaccionId,
          Movimientos = movimientos
        },
        ct);
  }

  private async Task<AirbnbAccountingAccounts> GetAirbnbAccountsAsync(string issuerRfc, CancellationToken ct)
  {
    const string sql = """
SELECT
    cc.Nivel1,
    cc.Nivel2,
    cc.Nivel3,
    cc.Descripcion AS NombreCuenta
FROM dbo.CuentasContables cc
WHERE cc.RFC = @IssuerRfc
  AND (
      (cc.Nivel1 = '102' AND cc.Nivel2 = '01' AND cc.Nivel3 = '02')
      OR (cc.Nivel1 = '208' AND cc.Nivel2 = '01' AND cc.Nivel3 = '01')
      OR (cc.Nivel1 = '401' AND cc.Nivel2 = '25' AND cc.Nivel3 = '02')
      OR (cc.Nivel1 = '113' AND cc.Nivel2 = '01' AND cc.Nivel3 = '05')
      OR (cc.Nivel1 = '114' AND cc.Nivel2 = '01' AND cc.Nivel3 = '03')
      OR (cc.Nivel1 = '601' AND cc.Nivel2 = '74' AND cc.Nivel3 = '02')
  );
""";

    await using var conn = CreateConnection();
    var rows = (await conn.QueryAsync<AirbnbAccountingAccount>(
        new CommandDefinition(sql, new { IssuerRfc = issuerRfc }, cancellationToken: ct))).AsList();

    return new AirbnbAccountingAccounts(
        RequireAccount(rows, "102", "01", "02"),
        RequireAccount(rows, "208", "01", "01"),
        RequireAccount(rows, "401", "25", "02"),
        RequireAccount(rows, "113", "01", "05"),
        RequireAccount(rows, "114", "01", "03"),
        RequireAccount(rows, "601", "74", "02"));
  }

  private static AirbnbAccountingAccount RequireAccount(
      IReadOnlyList<AirbnbAccountingAccount> accounts,
      string nivel1,
      string nivel2,
      string nivel3)
  {
    var account = accounts.FirstOrDefault(account =>
        string.Equals(account.Nivel1, nivel1, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(account.Nivel2, nivel2, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(account.Nivel3, nivel3, StringComparison.OrdinalIgnoreCase));

    return account ?? throw new InvalidOperationException(
        $"Falta la cuenta contable {nivel1}-{nivel2}-{nivel3} para generar la póliza Airbnb.");
  }

  private static void AddMovimiento(
      List<TransaccionMovimientoUpdateItem> movimientos,
      AirbnbAccountingAccount account,
      string concept,
      decimal amount,
      bool debe)
  {
    amount = RoundCurrency(amount);
    if (amount <= 0m)
    {
      return;
    }

    movimientos.Add(new TransaccionMovimientoUpdateItem
    {
      Nivel1 = account.Nivel1,
      Nivel2 = account.Nivel2,
      Nivel3 = account.Nivel3,
      NombreCuenta = account.NombreCuenta,
      Concepto = concept,
      Debe = debe ? amount : 0m,
      Haber = debe ? 0m : amount
    });
  }

  private static string BuildReservationConcept(ReservacionDetailDto detail)
  {
    var cliente = string.IsNullOrWhiteSpace(detail.Cliente)
        ? "(Sin cliente)"
        : detail.Cliente.Trim().ToUpperInvariant();

    return $"PAGO DE LA RESERVACION#{detail.Id} ({cliente})";
  }

  private static FacturamaIssuedCfdiRequest BuildReservationPayload(
      int transaccionId,
      string paymentForm,
      string paymentMethod,
      string expeditionZipCode,
      ReservationCfdiCustomerUpsertRequest receiver,
      IReadOnlyList<ReservationCfdiItemPreviewDto> items)
  {
    return new FacturamaIssuedCfdiRequest
    {
      Header = new FacturamaIssuedCfdiHeader
      {
        Folio = transaccionId.ToString(CultureInfo.InvariantCulture),
        Date = DateTime.Now.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture),
        Currency = DefaultCurrency,
        ExpeditionPlace = expeditionZipCode,
        CfdiType = "I",
        PaymentForm = string.IsNullOrWhiteSpace(paymentForm) ? DefaultPaymentForm : paymentForm.Trim(),
        PaymentMethod = string.IsNullOrWhiteSpace(paymentMethod) ? DefaultPaymentMethod : paymentMethod.Trim(),
        TaxZipCode = expeditionZipCode
      },
      Receiver = new FacturamaReceiver
      {
        Rfc = receiver.Rfc,
        Name = receiver.FiscalName,
        CfdiUse = receiver.CfdiUse,
        FiscalRegime = receiver.FiscalRegime,
        TaxZipCode = receiver.TaxZipCode
      },
      Items = items.Select(MapItem).ToArray()
    };
  }

  private static FacturamaIssuedCfdiItem MapItem(ReservationCfdiItemPreviewDto item)
  {
    var taxableBase = RoundCurrency(item.Subtotal - item.Discount);
    var taxes = item.TaxObject == "02"
        ? new[]
        {
          new FacturamaIssuedCfdiTax
          {
            Name = "IVA",
            Rate = 0.16m,
            Base = taxableBase,
            Total = item.Tax,
            IsRetention = false
          }
        }
        : Array.Empty<FacturamaIssuedCfdiTax>();

    return new FacturamaIssuedCfdiItem
    {
      ProductCode = item.ProductCode,
      Description = item.Description,
      Unit = item.Unit,
      UnitCode = item.UnitCode,
      UnitPrice = item.UnitPrice,
      Quantity = item.Quantity,
      Subtotal = item.Subtotal,
      Discount = item.Discount,
      TaxObject = item.TaxObject,
      Taxes = taxes,
      Total = item.Total
    };
  }

  private async Task EnsureReceiverIsValidAsync(
      ReservationCfdiCustomerUpsertRequest receiver,
      CancellationToken ct)
  {
    var isSandbox = IsSandboxEnvironment();
    try
    {
      var result = await ValidateReceiverCoreAsync(receiver, ct);
      if (isSandbox)
      {
        if (!result.IsValid)
        {
          _logger.LogInformation(
              "Sandbox receiver validation returned non-valid result for RFC {Rfc}. Proceeding because sandbox validation is advisory.",
              receiver.Rfc);
        }

        return;
      }

      if (result.IsValid)
      {
        return;
      }

      throw new InvalidOperationException(result.Message);
    }
    catch (Exception ex) when (isSandbox)
    {
      _logger.LogWarning(
          ex,
          "Sandbox receiver validation failed for RFC {Rfc}. Proceeding because sandbox validation is advisory.",
          receiver.Rfc);
    }
  }

  private async Task<ReservationCfdiReceiverValidationDto> ValidateReceiverCoreAsync(
      ReservationCfdiCustomerUpsertRequest receiver,
      CancellationToken ct)
  {
    var result = await _facturamaApiClient.ValidateReceiverAsync(
        BuildReceiverValidationRequest(receiver),
        ct);

    return BuildReceiverValidationResult(result, IsSandboxEnvironment());
  }

  private static FacturamaReceiverValidationRequest BuildReceiverValidationRequest(
      ReservationCfdiCustomerUpsertRequest receiver)
    => new()
    {
      Rfc = receiver.Rfc,
      Name = receiver.FiscalName,
      CfdiUse = receiver.CfdiUse,
      FiscalRegime = receiver.FiscalRegime,
      TaxZipCode = receiver.TaxZipCode
    };

  private static ReservationCfdiReceiverValidationDto BuildReceiverValidationResult(
      FacturamaReceiverValidationResult result,
      bool isSandbox)
  {
    var issues = GetReceiverValidationIssues(result);

    return new ReservationCfdiReceiverValidationDto
    {
      IsValid = result.IsValid,
      ExistRfc = result.ExistRfc,
      MatchName = result.MatchName,
      MatchZipCode = result.MatchZipCode,
      MatchFiscalRegime = result.MatchFiscalRegime,
      IsSandbox = isSandbox,
      Message = BuildReceiverValidationMessage(result, issues),
      ValidatedAtUtc = DateTime.UtcNow
    };
  }

  private static List<string> GetReceiverValidationIssues(FacturamaReceiverValidationResult result)
  {
    var issues = new List<string>();
    if (!result.ExistRfc)
      issues.Add("RFC no localizado");
    if (!result.MatchName)
      issues.Add("razon social no coincide");
    if (!result.MatchZipCode)
      issues.Add("codigo postal no coincide");
    if (!result.MatchFiscalRegime)
      issues.Add("regimen fiscal no coincide");

    return issues;
  }

  private static string BuildReceiverValidationMessage(
      FacturamaReceiverValidationResult result,
      IReadOnlyList<string>? issues = null)
  {
    if (result.IsValid)
    {
      return "Facturama validó al receptor.";
    }

    var effectiveIssues = issues ?? GetReceiverValidationIssues(result);
    return effectiveIssues.Count == 0
        ? "Facturama no validó al receptor."
        : $"Facturama no validó al receptor: {string.Join(", ", effectiveIssues)}.";
  }

  private async Task<string> ResolveIssuerTaxZipCodeAsync(string issuerRfc, CancellationToken ct)
  {
    var profile = await _satRfcProfileRepository.GetAsync(issuerRfc);
    var zipCode = NormalizePostalCode(profile?.CodigoPostal);
    if (!string.IsNullOrWhiteSpace(zipCode))
    {
      return zipCode;
    }

    var taxEntity = await _facturamaApiClient.GetTaxEntityAsync(ct);
    zipCode = NormalizePostalCode(taxEntity.IssuedIn?.ZipCode)
        ?? NormalizePostalCode(taxEntity.TaxAddress?.ZipCode);

    if (string.IsNullOrWhiteSpace(zipCode))
    {
      throw new InvalidOperationException(
          $"No se pudo resolver el codigo postal de expedicion para el RFC {issuerRfc}.");
    }

    return zipCode;
  }

  private async Task<IReadOnlyList<ReservationCfdiPolizaOptionDto>> GetPolizaOptionsAsync(
      int reservationId,
      string issuerRfc,
      decimal reservationTotal,
      CancellationToken ct)
  {
    const string sql = """
SELECT
    t.ID AS TransaccionId,
    t.Fecha,
    ISNULL(t.Concepto, '') AS Concepto,
    CAST(ISNULL(rt.Amount, ISNULL(t.Monto, 0)) AS decimal(18,2)) AS Monto,
    CAST(CASE WHEN EXISTS (
        SELECT 1
        FROM dbo.Transaccion_Comprobante tc
        INNER JOIN cfdi.Comprobante c
            ON c.Comprobante_Id = tc.Comprobante_ID
        WHERE tc.Transaccion_ID = t.ID
          AND c.FechaCancelacion IS NULL
          AND ISNULL(c.Estatus, '') NOT LIKE 'Cancel%'
    ) THEN 1 ELSE 0 END AS bit) AS HasExistingCfdi,
    CAST(CASE WHEN ABS(ISNULL(rt.Amount, ISNULL(t.Monto, 0)) - @ReservationTotal) <= @Tolerance
        THEN 1 ELSE 0 END AS bit) AS MatchesReservationTotal,
    CAST(CASE WHEN t.Tipo_Poliza = 'INGRESO'
                   AND t.RFC = @IssuerRfc
                   AND ABS(ISNULL(rt.Amount, ISNULL(t.Monto, 0)) - @ReservationTotal) <= @Tolerance
                   AND NOT EXISTS (
                       SELECT 1
                       FROM dbo.Transaccion_Comprobante tc
                       INNER JOIN cfdi.Comprobante c
                           ON c.Comprobante_Id = tc.Comprobante_ID
                       WHERE tc.Transaccion_ID = t.ID
                         AND c.FechaCancelacion IS NULL
                         AND ISNULL(c.Estatus, '') NOT LIKE 'Cancel%'
                   )
        THEN 1 ELSE 0 END AS bit) AS IsEligible
FROM dbo.Reservation_Transacciones rt
INNER JOIN dbo.Transacciones t
    ON t.ID = rt.TransaccionID
WHERE rt.ReservationID = @ReservationId
ORDER BY
    CASE WHEN t.Tipo_Poliza = 'INGRESO'
              AND t.RFC = @IssuerRfc
              AND ABS(ISNULL(rt.Amount, ISNULL(t.Monto, 0)) - @ReservationTotal) <= @Tolerance
              AND NOT EXISTS (
                  SELECT 1
                  FROM dbo.Transaccion_Comprobante tc
                  INNER JOIN cfdi.Comprobante c
                      ON c.Comprobante_Id = tc.Comprobante_ID
                  WHERE tc.Transaccion_ID = t.ID
                    AND c.FechaCancelacion IS NULL
                    AND ISNULL(c.Estatus, '') NOT LIKE 'Cancel%'
              )
         THEN 0 ELSE 1 END,
    t.Fecha DESC,
    t.ID DESC;
""";

    await using var conn = CreateConnection();
    var rows = await conn.QueryAsync<ReservationCfdiPolizaOptionDto>(
        new CommandDefinition(
            sql,
            new
            {
              ReservationId = reservationId,
              IssuerRfc = issuerRfc,
              ReservationTotal = reservationTotal,
              Tolerance = CurrencyTolerance
            },
            cancellationToken: ct));

    return rows.AsList();
  }

  private async Task<IReadOnlyList<ReservationCfdiLinkedDocumentDto>> GetExistingDocumentsAsync(
      int reservationId,
      CancellationToken ct)
  {
    const string sql = """
SELECT
    rt.TransaccionID AS TransaccionId,
    CAST(c.Comprobante_Id AS bigint) AS ComprobanteId,
    c.Fecha,
    c.Serie,
    c.Folio,
    tfd.UUID AS Uuid,
    r.Rfc AS ReceptorRfc,
    r.Nombre AS ReceptorNombre,
    CAST(c.Total AS decimal(18,2)) AS Total
FROM dbo.Reservation_Transacciones rt
INNER JOIN dbo.Transaccion_Comprobante tc
    ON tc.Transaccion_ID = rt.TransaccionID
INNER JOIN cfdi.Comprobante c
    ON c.Comprobante_Id = tc.Comprobante_ID
LEFT JOIN cfdi.Receptor r
    ON r.Comprobante_ID = c.Comprobante_Id
LEFT JOIN cfdi.TimbreFiscalDigital tfd
    ON tfd.Comprobante_ID = c.Comprobante_Id
WHERE rt.ReservationID = @ReservationId
  AND c.FechaCancelacion IS NULL
  AND ISNULL(c.Estatus, '') NOT LIKE 'Cancel%'
ORDER BY c.Fecha DESC, c.Comprobante_Id DESC;
""";

    await using var conn = CreateConnection();
    var rows = await conn.QueryAsync<ReservationCfdiLinkedDocumentDto>(
        new CommandDefinition(sql, new { ReservationId = reservationId }, cancellationToken: ct));

    return rows.AsList();
  }

  private async Task<IReadOnlyList<ReservationCfdiSuiteSource>> GetSuiteSourcesAsync(int reservationId, CancellationToken ct)
  {
    const string sql = """
SELECT
    rc.ID AS Id,
    rc.ROOM_DATE AS Fecha,
    ISNULL(rc.ROOM, '') AS RoomName,
    r.ROOM_DESCRIPTION AS RoomDescription,
    CAST(ISNULL(rc.PRECIO, 0) AS decimal(18,2)) AS Price
FROM dbo.ROOM_CALENDAR rc
LEFT JOIN dbo.ROOM r
    ON r.ROOM_NAME = rc.ROOM
WHERE TRY_CAST(rc.LOCK_DESCRIPTION AS int) = @ReservationId
ORDER BY rc.ROOM_DATE, rc.ROOM;
""";

    await using var conn = CreateConnection();
    var rows = await conn.QueryAsync<ReservationCfdiSuiteSource>(
        new CommandDefinition(sql, new { ReservationId = reservationId }, cancellationToken: ct));

    return rows.AsList();
  }

  private async Task<string?> GetReservationCustomerEmailAsync(int? clienteId, CancellationToken ct)
  {
    if (!clienteId.HasValue || clienteId.Value <= 0)
    {
      return null;
    }

    const string sql = """
SELECT TOP (1) c.Email
FROM dbo.Clientes c
WHERE c.ID = @ClienteId;
""";

    await using var conn = CreateConnection();
    return await conn.ExecuteScalarAsync<string?>(
        new CommandDefinition(sql, new { ClienteId = clienteId.Value }, cancellationToken: ct));
  }

  private async Task<IReadOnlyList<ReservationCfdiCustomerSuggestionDto>> GetPersistedCustomersAsync(
      SqlConnection conn,
      string? searchText,
      int top,
      CancellationToken ct)
  {
    if (!await HasCfdiProfileTableAsync(conn, ct))
    {
      return Array.Empty<ReservationCfdiCustomerSuggestionDto>();
    }

    const string sql = """
SELECT TOP (@Top)
    bp.Id AS BusinessPartnerId,
    bp.PartnerName AS DisplayName,
    ISNULL(bp.Rfc, '') AS Rfc,
    ISNULL(NULLIF(profile.FiscalName, ''), bp.PartnerName) AS FiscalName,
    ISNULL(profile.TaxZipCode, ISNULL(bp.PostalCode, '')) AS TaxZipCode,
    ISNULL(profile.FiscalRegime, '') AS FiscalRegime,
    ISNULL(profile.DefaultCfdiUse, '') AS CfdiUse,
    bp.Email,
    CAST(1 AS bit) AS IsPersisted,
    CAST('Cliente fiscal' AS varchar(50)) AS SourceLabel,
    profile.UpdatedAt AS LastUsedAt
FROM dbo.BusinessPartner bp
INNER JOIN dbo.BusinessPartnerRole role
    ON role.BusinessPartnerId = bp.Id
   AND role.RoleCode = 'Customer'
INNER JOIN dbo.BusinessPartnerCfdiProfile profile
    ON profile.BusinessPartnerId = bp.Id
WHERE bp.IsActive = 1
  AND (
      @SearchText IS NULL
      OR bp.PartnerName LIKE @SearchLike
      OR bp.Rfc LIKE @SearchLike
      OR bp.Email LIKE @SearchLike
      OR profile.FiscalName LIKE @SearchLike
  )
ORDER BY
    CASE WHEN @SearchText IS NOT NULL AND bp.Rfc = @SearchText THEN 0 ELSE 1 END,
    profile.UpdatedAt DESC,
    bp.PartnerName,
    bp.Id;
""";

    var rows = await conn.QueryAsync<ReservationCfdiCustomerSuggestionDto>(
        new CommandDefinition(
            sql,
            new
            {
              Top = top,
              SearchText = searchText,
              SearchLike = searchText is null ? null : $"%{searchText}%"
            },
            cancellationToken: ct));

    return rows.AsList();
  }

  private static async Task<IReadOnlyList<ReservationCfdiCustomerSuggestionDto>> GetHistoricalCustomersAsync(
      SqlConnection conn,
      string? searchText,
      int top,
      CancellationToken ct)
  {
    const string sql = """
SELECT TOP (@Top)
    CAST(NULL AS int) AS BusinessPartnerId,
    r.Nombre AS DisplayName,
    r.Rfc,
    r.Nombre AS FiscalName,
    ISNULL(r.DomicilioFiscalReceptor, '') AS TaxZipCode,
    ISNULL(r.RegimenFiscalReceptor, '') AS FiscalRegime,
    ISNULL(r.UsoCFDI, '') AS CfdiUse,
    CAST(NULL AS varchar(100)) AS Email,
    CAST(0 AS bit) AS IsPersisted,
    CAST('Historial CFDI' AS varchar(50)) AS SourceLabel,
    MAX(c.Fecha) AS LastUsedAt
FROM cfdi.Receptor r
INNER JOIN cfdi.Comprobante c
    ON c.Comprobante_Id = r.Comprobante_ID
WHERE r.Rfc <> 'XAXX010101000'
  AND (
      @SearchText IS NULL
      OR r.Rfc LIKE @SearchLike
      OR r.Nombre LIKE @SearchLike
      OR r.DomicilioFiscalReceptor LIKE @SearchLike
  )
GROUP BY
    r.Rfc,
    r.Nombre,
    r.DomicilioFiscalReceptor,
    r.RegimenFiscalReceptor,
    r.UsoCFDI
ORDER BY
    CASE WHEN @SearchText IS NOT NULL AND r.Rfc = @SearchText THEN 0 ELSE 1 END,
    MAX(c.Fecha) DESC,
    r.Nombre,
    r.Rfc;
""";

    var rows = await conn.QueryAsync<ReservationCfdiCustomerSuggestionDto>(
        new CommandDefinition(
            sql,
            new
            {
              Top = top,
              SearchText = searchText,
              SearchLike = searchText is null ? null : $"%{searchText}%"
            },
            cancellationToken: ct));

    return rows.AsList();
  }

  private static IReadOnlyList<ReservationCfdiCustomerSuggestionDto> MergeSuggestions(
      string? searchText,
      IReadOnlyList<ReservationCfdiCustomerSuggestionDto> persisted,
      IReadOnlyList<ReservationCfdiCustomerSuggestionDto> historical,
      int top)
  {
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var merged = new List<ReservationCfdiCustomerSuggestionDto>(top);

    foreach (var source in persisted.Concat(historical))
    {
      var key = $"{source.Rfc}|{source.FiscalName}";
      if (!seen.Add(key))
      {
        continue;
      }

      merged.Add(source);
      if (merged.Count >= top)
      {
        break;
      }
    }

    if (string.IsNullOrWhiteSpace(searchText))
    {
      return merged;
    }

    return merged
        .OrderBy(item => !string.Equals(item.Rfc, searchText, StringComparison.OrdinalIgnoreCase))
        .ThenBy(item => !ContainsIgnoreCase(item.FiscalName, searchText))
        .ThenBy(item => item.IsPersisted ? 0 : 1)
        .ThenByDescending(item => item.LastUsedAt)
        .ToArray();
  }

  private static ReservationCfdiCustomerUpsertRequest BuildReceiverDraft(
      ReservacionDetailDto detail,
      string? clienteEmail,
      IReadOnlyList<ReservationCfdiCustomerSuggestionDto> suggestions)
  {
    var normalizedReservationCustomer = NormalizeDisplayName(detail.Cliente);
    var match = string.IsNullOrWhiteSpace(normalizedReservationCustomer)
        ? null
        : suggestions.FirstOrDefault(item => MatchesCustomerName(item, normalizedReservationCustomer));

    return new ReservationCfdiCustomerUpsertRequest
    {
      BusinessPartnerId = match?.BusinessPartnerId,
      DisplayName = match?.DisplayName ?? normalizedReservationCustomer,
      Rfc = match?.Rfc ?? string.Empty,
      FiscalName = match?.FiscalName ?? normalizedReservationCustomer,
      TaxZipCode = match?.TaxZipCode ?? string.Empty,
      FiscalRegime = match?.FiscalRegime ?? string.Empty,
      CfdiUse = match?.CfdiUse ?? string.Empty,
      Email = match?.Email ?? NullIfWhiteSpace(clienteEmail)
    };
  }

  private static bool MatchesCustomerName(ReservationCfdiCustomerSuggestionDto suggestion, string? reservationCustomerName)
  {
    var left = NormalizeLookupKey(suggestion.DisplayName);
    var right = NormalizeLookupKey(reservationCustomerName);

    return !string.IsNullOrWhiteSpace(left) &&
           !string.IsNullOrWhiteSpace(right) &&
           (left.Contains(right, StringComparison.Ordinal) || right.Contains(left, StringComparison.Ordinal));
  }

  private static void ValidateReservationTotals(
      ReservacionDetailDto detail,
      IReadOnlyList<ReservationCfdiItemPreviewDto> items)
  {
    var itemTotal = RoundCurrency(items.Sum(static item => item.Total));
    var reservationTotal = RoundCurrency(detail.TotalPrice);

    if (itemTotal <= 0m)
    {
      throw new InvalidOperationException("La reservación no tiene conceptos facturables con monto mayor que cero.");
    }

    if (Math.Abs(itemTotal - reservationTotal) > CurrencyTolerance)
    {
      throw new InvalidOperationException(
          $"La composición del CFDI ({itemTotal.ToString("N2", CultureInfo.InvariantCulture)}) no coincide con el total de la reservación ({reservationTotal.ToString("N2", CultureInfo.InvariantCulture)}).");
    }
  }

  private async Task<ReservationTransaccionTargetRow?> GetTransaccionTargetAsync(
      int transaccionId,
      int reservationId,
      CancellationToken ct)
  {
    const string sql = """
SELECT TOP (1)
    t.ID AS TransaccionId,
    t.RFC AS Rfc,
    t.Tipo_Poliza AS TipoPoliza,
    t.Forma_Pago AS FormaPago,
    CAST(ISNULL(rt.Amount, ISNULL(t.Monto, 0)) AS decimal(18,2)) AS Amount,
    CAST(CASE WHEN rt.TransaccionID IS NULL THEN 0 ELSE 1 END AS bit) AS IsLinkedToReservation,
    CAST(CASE WHEN EXISTS (
        SELECT 1
        FROM dbo.Transaccion_Comprobante tc
        INNER JOIN cfdi.Comprobante c
            ON c.Comprobante_Id = tc.Comprobante_ID
        WHERE tc.Transaccion_ID = t.ID
          AND c.FechaCancelacion IS NULL
          AND ISNULL(c.Estatus, '') NOT LIKE 'Cancel%'
    ) THEN 1 ELSE 0 END AS bit) AS HasExistingCfdi
FROM dbo.Transacciones t
LEFT JOIN dbo.Reservation_Transacciones rt
    ON rt.TransaccionID = t.ID
   AND rt.ReservationID = @ReservationId
WHERE t.ID = @TransaccionId;
""";

    await using var conn = CreateConnection();
    return await conn.QueryFirstOrDefaultAsync<ReservationTransaccionTargetRow>(
        new CommandDefinition(
            sql,
            new
            {
              TransaccionId = transaccionId,
              ReservationId = reservationId
            },
            cancellationToken: ct));
  }

  private async Task UpdateTransaccionPaymentFormAsync(int transaccionId, string paymentForm, CancellationToken ct)
  {
    const string sql = """
UPDATE dbo.Transacciones
SET Forma_Pago = @FormaPago
WHERE ID = @TransaccionId;
""";

    await using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    await conn.ExecuteAsync(
        new CommandDefinition(
            sql,
            new
            {
              TransaccionId = transaccionId,
              FormaPago = paymentForm
            },
            cancellationToken: ct));
  }

  private async Task<int?> ResolveBusinessPartnerIdAsync(
      SqlConnection conn,
      SqlTransaction tx,
      int? requestedId,
      string rfc,
      CancellationToken ct)
  {
    if (requestedId.HasValue && requestedId.Value > 0)
    {
      var byId = await conn.ExecuteScalarAsync<int?>(
          new CommandDefinition(
              "SELECT TOP (1) Id FROM dbo.BusinessPartner WHERE Id = @Id;",
              new { Id = requestedId.Value },
              tx,
              cancellationToken: ct));

      if (byId.HasValue)
      {
        return byId.Value;
      }
    }

    return await conn.ExecuteScalarAsync<int?>(
        new CommandDefinition(
            """
SELECT TOP (1) bp.Id
FROM dbo.BusinessPartner bp
WHERE bp.Rfc = @Rfc
ORDER BY bp.IsActive DESC, bp.Id;
""",
            new { Rfc = rfc },
            tx,
            cancellationToken: ct));
  }

  private async Task<bool> HasCfdiProfileTableAsync(SqlConnection conn, CancellationToken ct)
  {
    var result = await conn.ExecuteScalarAsync<int>(
        new CommandDefinition(
            "SELECT CASE WHEN OBJECT_ID('dbo.BusinessPartnerCfdiProfile', 'U') IS NULL THEN 0 ELSE 1 END;",
            cancellationToken: ct));

    return result == 1;
  }

  private bool IsSandboxEnvironment()
  {
    var configuredBaseUrl = _configuration["Facturama:BaseUrl"];
    if (!string.IsNullOrWhiteSpace(configuredBaseUrl))
    {
      return configuredBaseUrl.Contains("sandbox", StringComparison.OrdinalIgnoreCase);
    }

    var environmentName = _configuration["ENVIRONMENT"] ?? _configuration["Environment"];
    return string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase);
  }

  private static PaymentSelection NormalizePaymentSelection(string? paymentForm, string? paymentMethod)
  {
    var normalizedMethod = NormalizeCatalogCode(paymentMethod);
    if (string.IsNullOrWhiteSpace(normalizedMethod))
    {
      normalizedMethod = DefaultPaymentMethod;
    }

    if (!string.Equals(normalizedMethod, DefaultPaymentMethod, StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(normalizedMethod, DeferredPaymentMethod, StringComparison.OrdinalIgnoreCase))
    {
      throw new InvalidOperationException("El metodo de pago seleccionado no es valido.");
    }

    var normalizedForm = NormalizeCatalogCode(paymentForm);
    if (string.Equals(normalizedMethod, DeferredPaymentMethod, StringComparison.OrdinalIgnoreCase))
    {
      normalizedForm = DeferredPaymentForm;
    }
    else if (string.IsNullOrWhiteSpace(normalizedForm) ||
             string.Equals(normalizedForm, DeferredPaymentForm, StringComparison.OrdinalIgnoreCase))
    {
      normalizedForm = DefaultPaymentForm;
    }

    return new PaymentSelection(normalizedForm, normalizedMethod);
  }

  private static ReservationCfdiCustomerUpsertRequest NormalizeReceiver(ReservationCfdiCustomerUpsertRequest request)
  {
    var normalized = new ReservationCfdiCustomerUpsertRequest
    {
      BusinessPartnerId = request.BusinessPartnerId,
      DisplayName = NormalizeDisplayName(request.DisplayName),
      Rfc = NormalizeRfc(request.Rfc),
      FiscalName = NormalizeDisplayName(request.FiscalName),
      TaxZipCode = NormalizePostalCode(request.TaxZipCode) ?? string.Empty,
      FiscalRegime = NormalizeCatalogCode(request.FiscalRegime),
      CfdiUse = NormalizeCatalogCode(request.CfdiUse),
      Email = NullIfWhiteSpace(request.Email)
    };

    if (string.IsNullOrWhiteSpace(normalized.Rfc))
      throw new InvalidOperationException("El RFC del receptor es obligatorio.");

    if (string.IsNullOrWhiteSpace(normalized.FiscalName))
      throw new InvalidOperationException("La razon social del receptor es obligatoria.");

    if (string.IsNullOrWhiteSpace(normalized.TaxZipCode))
      throw new InvalidOperationException("El codigo postal fiscal del receptor es obligatorio.");

    if (string.IsNullOrWhiteSpace(normalized.FiscalRegime))
      throw new InvalidOperationException("El regimen fiscal del receptor es obligatorio.");

    if (string.IsNullOrWhiteSpace(normalized.CfdiUse))
      throw new InvalidOperationException("El uso de CFDI del receptor es obligatorio.");

    if (string.IsNullOrWhiteSpace(normalized.DisplayName))
    {
      normalized.DisplayName = normalized.FiscalName;
    }

    return normalized;
  }

  private static string NormalizeCatalogCode(string? value)
    => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToUpperInvariant();

  private static string NormalizeDisplayName(string? value)
    => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

  private static string NormalizeLookupKey(string? value)
  {
    if (string.IsNullOrWhiteSpace(value))
    {
      return string.Empty;
    }

    return value.Trim().ToUpperInvariant();
  }

  private static string NormalizeRfc(string? value)
    => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToUpperInvariant();

  private static string? NormalizePostalCode(string? value)
  {
    if (string.IsNullOrWhiteSpace(value))
    {
      return null;
    }

    return value.Trim();
  }

  private static string? NullIfWhiteSpace(string? value)
    => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

  private static string FirstNonEmpty(params string?[] values)
  {
    foreach (var value in values)
    {
      if (!string.IsNullOrWhiteSpace(value))
      {
        return value.Trim();
      }
    }

    return string.Empty;
  }

  private static bool ContainsIgnoreCase(string? source, string searchText)
    => !string.IsNullOrWhiteSpace(source) &&
       source.Contains(searchText, StringComparison.OrdinalIgnoreCase);

  private static decimal RoundCurrency(decimal value)
    => decimal.Round(value, 2, MidpointRounding.ToEven);

  private SqlConnection CreateConnection()
    => _connectionFactory.Create() as SqlConnection
      ?? throw new InvalidOperationException("La fabrica de conexiones no devolvio una SqlConnection.");

  private sealed record ResolvedTransaccionTarget(int TransaccionId, string FormaPago, bool CreatedNew);
  private sealed record PaymentSelection(string PaymentForm, string PaymentMethod);
  private sealed record AirbnbAccountingAccounts(
      AirbnbAccountingAccount Bank,
      AirbnbAccountingAccount IvaTransferred,
      AirbnbAccountingAccount Income,
      AirbnbAccountingAccount IvaRetained,
      AirbnbAccountingAccount IsrRetained,
      AirbnbAccountingAccount AirbnbCommission);

  private sealed class AirbnbAccountingAccount
  {
    public string Nivel1 { get; set; } = string.Empty;
    public string Nivel2 { get; set; } = string.Empty;
    public string Nivel3 { get; set; } = string.Empty;
    public string NombreCuenta { get; set; } = string.Empty;
  }

  private sealed class ReservationTransaccionTargetRow
  {
    public int TransaccionId { get; set; }
    public string? Rfc { get; set; }
    public string? TipoPoliza { get; set; }
    public string? FormaPago { get; set; }
    public decimal Amount { get; set; }
    public bool IsLinkedToReservation { get; set; }
    public bool HasExistingCfdi { get; set; }
  }
}
