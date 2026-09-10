using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging;
using OrionERP.Application.Features.Contabilidad.Transacciones;
using OrionERP.Application.Features.Reservaciones;
using OrionERP.Application.Features.Reservaciones.Accounting;
using OrionERP.Application.Features.Reservaciones.Cfdi;
using OrionERP.Application.Features.Reservaciones.ListaReservaciones;

namespace OrionERP.Infrastructure.Features.Reservaciones.Accounting;

/// <summary>
/// Contabilización explícita de una reservación. La identidad durable es
/// empresa/sede/reservación; nunca se recorren calendarios históricos ni se
/// infieren cuentas por el nombre de una habitación.
/// </summary>
public sealed class ReservationAccountingService : IReservationAccountingService
{
  private const string PolicyType = "INGRESO";
  private const string PaymentForm = "03";
  private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

  private readonly IListaReservacionesService _reservations;
  private readonly IHospitalityScopeAccessor _scopeAccessor;
  private readonly HospitalityConnectionFactory _connections;
  private readonly ITransaccionService _transactions;
  private readonly IReservationCfdiService _reservationCfdi;
  private readonly IAccountingOutbox _outbox;
  private readonly ILogger<ReservationAccountingService> _logger;

  public ReservationAccountingService(
    IListaReservacionesService reservations,
    IHospitalityScopeAccessor scopeAccessor,
    HospitalityConnectionFactory connections,
    ITransaccionService transactions,
    IReservationCfdiService reservationCfdi,
    IAccountingOutbox outbox,
    ILogger<ReservationAccountingService> logger)
  {
    _reservations = reservations ?? throw new ArgumentNullException(nameof(reservations));
    _scopeAccessor = scopeAccessor ?? throw new ArgumentNullException(nameof(scopeAccessor));
    _connections = connections ?? throw new ArgumentNullException(nameof(connections));
    _transactions = transactions ?? throw new ArgumentNullException(nameof(transactions));
    _reservationCfdi = reservationCfdi ?? throw new ArgumentNullException(nameof(reservationCfdi));
    _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
    _logger = logger ?? throw new ArgumentNullException(nameof(logger));
  }

  internal static string OperationKey(long siteId, int reservationId)
    => $"RESERVATION:{siteId}:{reservationId}";

  public async Task<ReservationAccountingResult> GeneratePolicyAsync(
    int reservationId,
    CancellationToken ct = default)
  {
    if (reservationId <= 0)
      return ReservationAccountingResult.Fail("La reservación seleccionada no es válida.");

    AccountingOperation? operation = null;
    var transactionId = 0;
    var policyRecorded = false;

    try
    {
      var scope = await _scopeAccessor.ResolveRequiredAsync(ct);
      var detail = await _reservations.GetReservacionDetailAsync(reservationId, ct);
      if (detail is null)
        return ReservationAccountingResult.Fail("No se encontró la reservación en la empresa y sede seleccionadas.");

      var amount = decimal.Round(detail.TotalPrice, 2, MidpointRounding.ToEven);
      if (amount <= 0m)
        return ReservationAccountingResult.Fail("La reservación no tiene un monto mayor que cero para contabilizar.");

      var mapping = await LoadMappingAsync(scope, ct);
      if (mapping is null)
      {
        return ReservationAccountingResult.Fail(
          "La contabilización de Hospedaje está apagada para esta sede. Configura y habilita las cuentas de ingreso y por cobrar.");
      }

      var payload = new ReservationAccountingPayload(
        "hospitality",
        "reservation",
        scope.SiteId,
        detail.Id,
        amount);
      operation = await _outbox.ClaimAsync(
        AccountingOutboxModules.Hospitality,
        OperationKey(scope.SiteId, detail.Id),
        JsonSerializer.Serialize(payload, JsonOptions),
        ct);

      if (operation.AlreadyCompleted)
      {
        return ReservationAccountingResult.Ok(
          $"La reservación ya tiene la póliza durable {operation.TransaccionId}.",
          operation.TransaccionId!.Value);
      }

      if (operation.InProgress)
      {
        return ReservationAccountingResult.Fail(
          "La póliza de esta reservación ya se está generando. Espera un momento antes de reintentar.",
          operation.TransaccionId);
      }

      EnsureSameOperation(operation, scope, payload);

      TransaccionHeaderDto? header = null;
      if (operation.ResumesFromExistingPolicy)
      {
        header = await _transactions.GetHeaderAsync(operation.TransaccionId!.Value, ct);
        if (header is null)
        {
          await _outbox.ForgetPolicyAsync(operation.Id, ct);
        }
      }

      if (header is null)
      {
        var concept = BuildConcept(detail);
        var created = await _transactions.CreateTransaccionAsync(new TransaccionCreateRequest
        {
          Rfc = scope.CompanyRfc,
          Fecha = DateTime.Now,
          Concepto = concept,
          Monto = amount,
          Cuenta = mapping.LeaseIncome.Code,
          TipoPoliza = PolicyType,
          FormaPago = PaymentForm,
          Memo = $"Hospedaje durable · sede {scope.SiteId} · reservación {detail.Id}"
        }, ct);
        if (!created.Success || created.NewTransaccionId <= 0)
          throw new InvalidOperationException(created.Message ?? "No se pudo crear la póliza de Hospedaje.");

        transactionId = created.NewTransaccionId;
        try
        {
          // Se registra antes del vínculo. Desde aquí, cualquier reintento retoma
          // esta misma póliza y nunca crea una segunda.
          await _outbox.RecordPolicyAsync(operation.Id, transactionId, ct);
          policyRecorded = true;
        }
        catch
        {
          // Si ni siquiera se alcanzó a registrar el rastro durable, todavía es
          // seguro compensar la cabecera recién creada.
          await _transactions.DeleteTransaccionAsync(transactionId, ct);
          throw;
        }

        header = await _transactions.GetHeaderAsync(transactionId, ct)
          ?? throw new InvalidOperationException("La póliza creada no se pudo volver a leer.");
      }
      else
      {
        transactionId = header.Id;
        policyRecorded = true;
      }

      var link = await _transactions.UpsertReservacionLinkAsync(new TransaccionReservacionLinkUpsertRequest
      {
        ReservationId = detail.Id,
        TransaccionId = transactionId,
        Amount = amount
      }, ct);
      if (!link.Success)
        throw new InvalidOperationException(link.Message ?? "No se pudo ligar la póliza a la reservación.");

      TransaccionCommandResult accounting;
      if (detail.AirbnbBreakdown is not null)
      {
        accounting = await _reservationCfdi.ApplyAirbnbAccountingAsync(new ReservationAirbnbAccountingRequest
        {
          ReservationId = detail.Id,
          TransaccionId = transactionId,
          IssuerRfc = scope.CompanyRfc
        }, ct);
      }
      else
      {
        accounting = await _transactions.GuardarMovimientosAsync(new TransaccionMovimientosUpdateRequest
        {
          TransaccionId = transactionId,
          Movimientos = BuildLeaseMovements(mapping, detail, amount)
        }, ct);
      }

      if (!accounting.Success)
        throw new InvalidOperationException(accounting.Message);

      var conceptToSave = BuildConcept(detail);
      var saved = await _transactions.GuardarYCerrarAsync(new TransaccionGuardarCerrarRequest
      {
        TransaccionId = transactionId,
        Concepto = conceptToSave,
        Fecha = header.Fecha,
        Cuenta = mapping.LeaseIncome.Code,
        Monto = amount,
        Facturado = header.Facturado ?? false,
        Memo = $"Hospedaje durable · sede {scope.SiteId} · reservación {detail.Id}",
        TipoPoliza = PolicyType,
        FormaPago = string.IsNullOrWhiteSpace(header.FormaPago) ? PaymentForm : header.FormaPago
      }, ct);
      if (!saved.Success)
        throw new InvalidOperationException(saved.Message ?? "No se pudo guardar la póliza de Hospedaje.");

      await _outbox.CompleteAsync(operation.Id, ct);
      return ReservationAccountingResult.Ok(
        detail.AirbnbBreakdown is null
          ? $"Póliza durable {transactionId} creada, balanceada y ligada a la reservación."
          : $"Póliza durable {transactionId} creada, ligada y con registro contable Airbnb.",
        transactionId);
    }
    catch (Exception ex)
    {
      if (operation is not null)
      {
        try { await _outbox.FailAsync(operation.Id, ex.Message, ct); }
        catch (Exception failException)
        {
          _logger.LogError(failException, "No se pudo conservar el fallo de la operación contable {OperationId}", operation.Id);
        }
      }

      _logger.LogError(ex, "No se pudo contabilizar la reservación {ReservationId}", reservationId);
      var retryMessage = policyRecorded && transactionId > 0
        ? $" La póliza {transactionId} quedó registrada para que el siguiente intento la retome sin duplicarla."
        : string.Empty;
      return ReservationAccountingResult.Fail(
        $"No se pudo completar la póliza de la reservación: {ex.Message}{retryMessage}",
        transactionId > 0 ? transactionId : null);
    }
  }

  private async Task<HospitalityMapping?> LoadMappingAsync(HospitalityScope scope, CancellationToken ct)
  {
    await using var conn = await _connections.OpenAsync(ct);
    var row = await conn.QuerySingleOrDefaultAsync<HospitalityMappingRow>(new CommandDefinition(
      """
      SELECT LeaseIncomeAccount, LeaseReceivableAccount
      FROM contabilidad.HospitalityAccountingMapping
      WHERE CompanyId=@CompanyId AND SiteId=@SiteId AND IsEnabled=1;
      """, scope, cancellationToken: ct));
    if (row is null || string.IsNullOrWhiteSpace(row.LeaseIncomeAccount)
      || string.IsNullOrWhiteSpace(row.LeaseReceivableAccount))
      return null;

    var incomeCode = AccountCode.Parse(row.LeaseIncomeAccount);
    var receivableCode = AccountCode.Parse(row.LeaseReceivableAccount);
    var income = await LoadAccountAsync(conn, scope.CompanyRfc, incomeCode, ct);
    var receivable = await LoadAccountAsync(conn, scope.CompanyRfc, receivableCode, ct);
    return new HospitalityMapping(income, receivable);
  }

  private static async Task<Account> LoadAccountAsync(
    System.Data.Common.DbConnection conn,
    string rfc,
    AccountCode code,
    CancellationToken ct)
  {
    var row = await conn.QuerySingleOrDefaultAsync<AccountRow>(new CommandDefinition(
      """
      SELECT TOP (1) Nivel1,Nivel2,Nivel3,Descripcion
      FROM dbo.CuentasContables
      WHERE RFC=@Rfc
        AND LTRIM(RTRIM(Nivel1))=@Nivel1
        AND ISNULL(NULLIF(LTRIM(RTRIM(Nivel2)),''),'')=@Nivel2
        AND ISNULL(NULLIF(LTRIM(RTRIM(Nivel3)),''),'')=@Nivel3;
      """,
      new { Rfc = rfc, code.Nivel1, code.Nivel2, code.Nivel3 },
      cancellationToken: ct));
    if (row is null)
      throw new InvalidOperationException($"La cuenta configurada {code.Code} no existe en el catálogo de la empresa.");
    return new Account(code.Code, row.Nivel1, row.Nivel2, row.Nivel3, row.Descripcion ?? code.Code);
  }

  private static List<TransaccionMovimientoUpdateItem> BuildLeaseMovements(
    HospitalityMapping mapping,
    ReservacionDetailDto detail,
    decimal amount)
  {
    var concept = BuildConcept(detail);
    return
    [
      Movement(mapping.LeaseReceivable, concept, amount, 0m),
      Movement(mapping.LeaseIncome, concept, 0m, amount)
    ];
  }

  private static TransaccionMovimientoUpdateItem Movement(
    Account account,
    string concept,
    decimal debit,
    decimal credit)
    => new()
    {
      Nivel1 = account.Nivel1,
      Nivel2 = account.Nivel2,
      Nivel3 = account.Nivel3,
      NombreCuenta = account.Description,
      Concepto = concept,
      Debe = debit,
      Haber = credit
    };

  private static string BuildConcept(ReservacionDetailDto detail)
  {
    var customer = string.IsNullOrWhiteSpace(detail.Cliente)
      ? "(SIN CLIENTE)"
      : detail.Cliente.Trim().ToUpperInvariant();
    return $"PAGO DE LA RESERVACION#{detail.Id} ({customer})";
  }

  private static void EnsureSameOperation(
    AccountingOperation operation,
    HospitalityScope scope,
    ReservationAccountingPayload current)
  {
    if (operation.CompanyId != scope.CompanyId
      || !string.Equals(operation.Rfc, scope.CompanyRfc, StringComparison.OrdinalIgnoreCase))
      throw new InvalidOperationException("La operación durable no pertenece a la empresa seleccionada.");

    ReservationAccountingPayload? original;
    try { original = JsonSerializer.Deserialize<ReservationAccountingPayload>(operation.Payload, JsonOptions); }
    catch (JsonException ex) { throw new InvalidOperationException("El rastro durable de la reservación no es válido.", ex); }

    if (original is null
      || original.SiteId != current.SiteId
      || original.ReservationId != current.ReservationId
      || decimal.Abs(original.Amount - current.Amount) > 0.01m)
    {
      throw new InvalidOperationException(
        "La reservación cambió después de iniciar su póliza. Revisa el importe antes de continuar; no se creó otra póliza.");
    }
  }

  private sealed record ReservationAccountingPayload(
    string Module,
    string Kind,
    long SiteId,
    int ReservationId,
    decimal Amount);

  private sealed class HospitalityMappingRow
  {
    public string LeaseIncomeAccount { get; set; } = string.Empty;
    public string LeaseReceivableAccount { get; set; } = string.Empty;
  }

  private sealed record HospitalityMapping(Account LeaseIncome, Account LeaseReceivable);

  private sealed class AccountRow
  {
    public string Nivel1 { get; set; } = string.Empty;
    public string? Nivel2 { get; set; }
    public string? Nivel3 { get; set; }
    public string? Descripcion { get; set; }
  }

  private sealed record Account(string Code, string Nivel1, string? Nivel2, string? Nivel3, string Description);

  private sealed record AccountCode(string Code, string Nivel1, string Nivel2, string Nivel3)
  {
    public static AccountCode Parse(string value)
    {
      var normalized = value.Trim();
      var parts = normalized.Split(['.', '-', '/', '>'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
      if (parts.Length is < 1 or > 3)
        throw new InvalidOperationException($"La cuenta configurada {normalized} no tiene un código válido.");
      return new AccountCode(normalized, parts[0], parts.ElementAtOrDefault(1) ?? string.Empty, parts.ElementAtOrDefault(2) ?? string.Empty);
    }
  }
}
