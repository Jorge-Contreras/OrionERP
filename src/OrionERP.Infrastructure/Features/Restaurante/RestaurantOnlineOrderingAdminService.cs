using System.Data;
using System.Data.Common;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using OrionERP.Application.Common;
using OrionERP.Application.Features.Logistica.Shared;
using OrionERP.Application.Features.Platform;
using OrionERP.Application.Features.Restaurante;

namespace OrionERP.Infrastructure.Features.Restaurante;

public sealed class RestaurantOnlineOrderingAdminService : IOnlineRestaurantOrderingAdminService
{
  private readonly IDbConnectionFactory _connections;
  private readonly IOrionSqlSessionFactory _sessions;
  private readonly TimeProvider _clock;

  public RestaurantOnlineOrderingAdminService(
    IDbConnectionFactory connections,
    IOrionSqlSessionFactory sessions,
    TimeProvider? clock = null)
  {
    _connections = connections;
    _sessions = sessions;
    _clock = clock ?? TimeProvider.System;
  }

  public async Task<RestaurantOnlineOrderingAdminDto?> GetAsync(
    string rfc,
    int siteId,
    CancellationToken ct = default)
  {
    var scope = await ResolveScopeAsync(rfc, siteId, ct);
    await using var connection = await _sessions.OpenAsync(scope, ct);
    using var multi = await connection.QueryMultipleAsync(new CommandDefinition(
      "restaurante.OnlineOrderingAdminGet",
      commandType: CommandType.StoredProcedure,
      cancellationToken: ct));
    var row = await multi.ReadSingleOrDefaultAsync<AdminRow>();
    if (row is null) return null;
    var products = (await multi.ReadAsync<RestaurantOnlineProductAdminDto>()).AsList();
    var recovery = (await connection.QueryAsync<RestaurantOnlineRecoveryDto>(new CommandDefinition(
      "restaurante.OnlineOrderingRecoveryList",
      new { Take = 100 },
      commandType: CommandType.StoredProcedure,
      cancellationToken: ct))).AsList();
    var paymentSummaries = (await connection.QueryAsync<RestaurantPaymentGatewaySummaryDto>(new CommandDefinition(
      """
      SELECT transactionInfo.PublicSiteId,transactionInfo.Rfc,transactionInfo.MerchantProfileKey,
        transactionInfo.CurrencyCode Currency,COUNT(*) CaptureCount,
        SUM(transactionInfo.GrossAmount) GrossAmount,
        SUM(COALESCE(transactionInfo.FeeAmount,0)) FeeAmount,
        SUM(COALESCE(transactionInfo.NetAmount,transactionInfo.GrossAmount)) NetAmount,
        SUM(COALESCE(refundInfo.RefundedAmount,0)) RefundedAmount,
        SUM(COALESCE(refundInfo.RefundGrossAmount,0)) RefundGrossAmount,
        SUM(COALESCE(refundInfo.RefundFeeAmount,0)) RefundFeeAmount,
        SUM(COALESCE(refundInfo.RefundNetAmount,0)) RefundNetAmount,
        CASE WHEN SUM(COALESCE(refundInfo.UnreconciledRefundCount,0))>0 THEN NULL
          ELSE SUM(COALESCE(transactionInfo.NetAmount,transactionInfo.GrossAmount))
            -SUM(COALESCE(refundInfo.RefundNetAmount,0)) END NetAfterRefunds,
        SUM(COALESCE(refundInfo.UnreconciledRefundCount,0)) UnreconciledRefundCount
      FROM restaurante.PaymentGatewayTransaction transactionInfo
      OUTER APPLY
      (
        SELECT SUM(refund.Amount) RefundedAmount,
          SUM(COALESCE(refund.ProviderGrossAmount,0)) RefundGrossAmount,
          SUM(COALESCE(refund.ProviderFeeAmount,0)) RefundFeeAmount,
          SUM(COALESCE(refund.ProviderNetAmount,0)) RefundNetAmount,
          SUM(CASE WHEN refund.ReconciledAtUtc IS NULL THEN 1 ELSE 0 END) UnreconciledRefundCount
        FROM restaurante.PaymentGatewayRefund refund
        WHERE refund.PublicSiteId=transactionInfo.PublicSiteId
          AND refund.Rfc=transactionInfo.Rfc
          AND refund.SiteId=transactionInfo.SiteId
          AND refund.GatewayTransactionId=transactionInfo.Id
          AND refund.[Status]='Completed'
      ) refundInfo
      WHERE transactionInfo.PublicSiteId=@PublicSiteId AND transactionInfo.Rfc=@Rfc
        AND transactionInfo.[Status] IN('Completed','PartiallyRefunded','Refunded')
      GROUP BY transactionInfo.PublicSiteId,transactionInfo.Rfc,
        transactionInfo.MerchantProfileKey,transactionInfo.CurrencyCode
      ORDER BY transactionInfo.MerchantProfileKey,transactionInfo.CurrencyCode;
      """,
      new { row.PublicSiteId, row.Rfc },
      cancellationToken: ct))).AsList();
    var blockers = BuildReadinessBlockers(row, products, _clock.GetUtcNow());
    return new RestaurantOnlineOrderingAdminDto
    {
      PublicSiteId = row.PublicSiteId,
      PublicSiteKey = row.PublicSiteKey,
      Rfc = row.Rfc,
      SiteId = row.SiteId,
      IsEnabled = row.IsEnabled,
      IsPaused = row.IsPaused,
      PauseMessage = row.PauseMessage,
      AllowGuestCheckout = row.GuestCheckoutEnabled,
      PickupEnabled = row.PickupEnabled,
      MaximumOrderAmount = row.MaximumOrderTotal,
      OnlineHoursJson = row.WeeklyScheduleJson,
      TermsVersion = row.TermsVersion,
      PrivacyVersion = row.PrivacyVersion,
      ProcessorHeartbeatAtUtc = row.ProcessorHeartbeatAtUtc,
      ConfigurationVersion = row.ConfigurationVersion,
      RowVersion = row.RowVersion,
      IsReady = blockers.Count == 0,
      ReadinessBlockers = blockers,
      Products = products,
      RecoveryItems = recovery,
      PaymentSummaries = paymentSummaries
    };
  }

  public async Task<RestaurantCommandResult> SaveAsync(
    RestaurantOnlineOrderingAdminSaveRequest request,
    string userName,
    CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(request);
    try
    {
      ValidateSchedule(request.OnlineHoursJson);
      var scope = await ResolveScopeAsync(request.Rfc, request.SiteId, ct);
      await using var connection = await _sessions.OpenAsync(scope, ct);
      var current = await connection.QuerySingleAsync<AdminIdentityRow>(new CommandDefinition(
        """
        SELECT settings.PublicSiteId,settings.Rfc,settings.SiteId
        FROM restaurante.OnlineOrderingSettings settings
        WHERE settings.CompanyId=@CompanyId AND settings.OrionSiteId=@OrionSiteId;
        """,
        new { scope.CompanyId, OrionSiteId = scope.SiteId },
        cancellationToken: ct));
      if (current.PublicSiteId != request.PublicSiteId
          || current.SiteId != request.SiteId
          || !string.Equals(current.Rfc, request.Rfc, StringComparison.OrdinalIgnoreCase))
        return RestaurantCommandResult.Fail("El sitio público no coincide con la sede seleccionada.");

      var saved = await connection.QuerySingleAsync<AdminSaveRow>(new CommandDefinition(
        "restaurante.OnlineOrderingAdminSaveV2",
        new
        {
          request.IsEnabled,
          request.IsPaused,
          PauseMessage = NullIfWhiteSpace(request.PauseMessage),
          GuestCheckoutEnabled = request.AllowGuestCheckout,
          request.PickupEnabled,
          MaximumOrderTotal = request.MaximumOrderAmount,
          WeeklyScheduleJson = request.OnlineHoursJson,
          request.TermsVersion,
          request.PrivacyVersion,
          EnabledProductIdsJson = JsonSerializer.Serialize(request.EnabledProductIds.Distinct().Order()),
          ExpectedConfigurationVersion = request.ExpectedConfigurationVersion,
          UpdatedBy = string.IsNullOrWhiteSpace(userName) ? "orionerp" : userName.Trim()
        },
        commandType: CommandType.StoredProcedure,
        cancellationToken: ct));
      return RestaurantCommandResult.Ok(
        saved.IsEnabled
          ? "Los pedidos en línea quedaron habilitados."
          : "La configuración quedó guardada con ventas nuevas deshabilitadas.",
        saved.PublicSiteId);
    }
    catch (SqlException exception) when (exception.Number is >= 53675 and <= 53686)
    {
      return RestaurantCommandResult.Fail(SafeAdminError(exception.Number));
    }
    catch (JsonException)
    {
      return RestaurantCommandResult.Fail("El horario de pedidos no contiene JSON válido.");
    }
    catch (InvalidOperationException exception)
    {
      return RestaurantCommandResult.Fail(exception.Message);
    }
  }

  public async Task<RestaurantCommandResult> RequestRefundAsync(
    RestaurantOnlineRefundRequest request,
    CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(request);
    if (request.OrderId == Guid.Empty || request.Amount <= 0
        || string.IsNullOrWhiteSpace(request.Reason)
        || string.IsNullOrWhiteSpace(request.RequestedByUserName)
        || string.IsNullOrWhiteSpace(request.SupervisorUserName)
        || string.IsNullOrWhiteSpace(request.IdempotencyKey))
      return RestaurantCommandResult.Fail("El reembolso requiere orden, importe, motivo, supervisor y clave de idempotencia.");

    try
    {
      var siteId = await ResolveOrderSiteAsync(request.Rfc, request.OrderId, ct);
      var scope = await ResolveScopeAsync(request.Rfc, siteId, ct);
      await using var connection = await _sessions.OpenAsync(scope, ct);
      var result = await connection.QuerySingleAsync<RefundRequestResult>(new CommandDefinition(
        "restaurante.PaymentGatewayRefundRequest",
        new
        {
          RestaurantOrderId = request.OrderId,
          request.Amount,
          Reason = request.Reason.Trim(),
          IdempotencyKey = request.IdempotencyKey.Trim(),
          RequestedBy = request.RequestedByUserName.Trim(),
          AuthorizedBy = request.SupervisorUserName.Trim()
        },
        commandType: CommandType.StoredProcedure,
        cancellationToken: ct));
      return RestaurantCommandResult.Ok(
        !result.WasCreated
          ? "La solicitud de reembolso ya estaba registrada."
          : "El reembolso quedó solicitado; el pago local cambiará sólo cuando PayPal lo confirme.");
    }
    catch (SqlException exception)
    {
      return RestaurantCommandResult.Fail(exception.Number switch
      {
        53768 => "Ya existe un reembolso pendiente para esta captura con otro importe o motivo.",
        >= 53690 and <= 53720 => "No se pudo solicitar el reembolso: verifica el importe, el pago PayPal y la autorización de supervisor.",
        _ => "No fue posible registrar el reembolso en este momento."
      });
    }
    catch (InvalidOperationException exception)
    {
      return RestaurantCommandResult.Fail(exception.Message);
    }
  }

  private async Task<PlatformExecutionScope> ResolveScopeAsync(string rfc, int siteId, CancellationToken ct)
  {
    var normalizedRfc = LogisticsRfc.Require(rfc);
    if (siteId <= 0) throw new InvalidOperationException("Selecciona una sede válida.");
    await using var connection = _connections.Create() as DbConnection
      ?? throw new InvalidOperationException("La fábrica no devolvió una conexión SQL.");
    await connection.OpenAsync(ct);
    await connection.ExecuteAsync(new CommandDefinition(
      """
      IF CONVERT(varchar(50),SESSION_CONTEXT(N'OrionRfc'))<>@Rfc
        THROW 51935,'El RFC solicitado no coincide con la empresa autorizada.',1;
      """,
      new { Rfc = normalizedRfc },
      cancellationToken: ct));
    var row = await connection.QuerySingleOrDefaultAsync<ScopeRow>(new CommandDefinition(
      """
      SELECT siteInfo.OrionCompanyId CompanyId,siteInfo.OrionSiteId,
             companyInfo.TaxRfc,siteInfo.Id ModuleLocalSiteId
      FROM restaurante.Site siteInfo
      JOIN orion.Company companyInfo
        ON companyInfo.CompanyId=siteInfo.OrionCompanyId AND companyInfo.Rfc=siteInfo.Rfc
      WHERE siteInfo.Rfc=@Rfc AND siteInfo.Id=@SiteId AND siteInfo.IsEnabled=1
        AND siteInfo.OrionCompanyId IS NOT NULL AND siteInfo.OrionSiteId IS NOT NULL;
      """,
      new { Rfc = normalizedRfc, SiteId = siteId },
      cancellationToken: ct))
      ?? throw new InvalidOperationException("La sede no tiene un vínculo activo con OrionERP.");
    return new PlatformExecutionScope(
      row.CompanyId,
      normalizedRfc,
      row.TaxRfc,
      row.OrionSiteId,
      PlatformModuleCodes.Restaurant,
      ModuleLocalSiteId: row.ModuleLocalSiteId).EnsureValid();
  }

  private async Task<int> ResolveOrderSiteAsync(string rfc, Guid orderId, CancellationToken ct)
  {
    var normalizedRfc = LogisticsRfc.Require(rfc);
    await using var connection = _connections.Create() as DbConnection
      ?? throw new InvalidOperationException("La fábrica no devolvió una conexión SQL.");
    await connection.OpenAsync(ct);
    return await connection.QuerySingleOrDefaultAsync<int?>(new CommandDefinition(
      "SELECT SiteId FROM restaurante.[Order] WHERE Rfc=@Rfc AND Id=@OrderId AND SalesChannel='Web';",
      new { Rfc = normalizedRfc, OrderId = orderId },
      cancellationToken: ct))
      ?? throw new InvalidOperationException("La orden Web no existe en el RFC seleccionado.");
  }

  private static List<string> BuildReadinessBlockers(
    AdminRow row,
    IReadOnlyList<RestaurantOnlineProductAdminDto> products,
    DateTimeOffset now)
  {
    var blockers = new List<string>();
    if (!row.PickupEnabled) blockers.Add("Activa pedidos para recoger.");
    if (row.MaximumOrderTotal <= 0) blockers.Add("Configura un máximo de pedido válido.");
    try { ValidateSchedule(row.WeeklyScheduleJson); }
    catch { blockers.Add("Configura al menos un intervalo válido en el horario en línea."); }
    if (!products.Any(product => product.IsOnlineEnabled && product.IsActive))
      blockers.Add("Habilita al menos un producto activo para venta en línea.");
    if (!string.Equals(row.GatewayEnvironment, row.RequiredGatewayEnvironment, StringComparison.OrdinalIgnoreCase))
      blockers.Add($"PayPal debe estar en modo {row.RequiredGatewayEnvironment}.");
    if (!row.GatewayCredentialsConfigured) blockers.Add("Faltan las credenciales privadas de PayPal para este sitio.");
    if (!row.GatewayWebhookConfigured) blockers.Add("Falta registrar el webhook independiente de este sitio.");
    if (!row.GatewayReadinessAtUtc.HasValue
        || now - Utc(row.GatewayReadinessAtUtc.Value) > TimeSpan.FromMinutes(5))
      blockers.Add("La verificación de configuración del sitio no está vigente.");
    if (!row.ProcessorHeartbeatAtUtc.HasValue
        || now - Utc(row.ProcessorHeartbeatAtUtc.Value) > TimeSpan.FromSeconds(120))
      blockers.Add("El procesador de órdenes de OrionERP no está reportando actividad.");
    if (string.IsNullOrWhiteSpace(row.TermsVersion) || string.IsNullOrWhiteSpace(row.PrivacyVersion))
      blockers.Add("Publica las versiones vigentes de términos y privacidad.");
    if (string.IsNullOrWhiteSpace(row.ActiveMerchantProfileKey))
      blockers.Add("Configura el perfil mercantil de PayPal.");
    return blockers;
  }

  private static void ValidateSchedule(string json)
  {
    using var document = JsonDocument.Parse(json);
    if (document.RootElement.ValueKind != JsonValueKind.Object)
      throw new InvalidOperationException("El horario debe ser un objeto JSON por día.");
    var count = 0;
    foreach (var day in document.RootElement.EnumerateObject())
    {
      if (day.Value.ValueKind != JsonValueKind.Array)
        throw new InvalidOperationException("Cada día del horario debe contener una lista de intervalos.");
      foreach (var interval in day.Value.EnumerateArray())
      {
        if (interval.ValueKind != JsonValueKind.Object
            || !TryReadTime(interval, "opens", out var opens)
            || !TryReadTime(interval, "closes", out var closes)
            || opens == closes)
          throw new InvalidOperationException("Un intervalo del horario en línea no es válido.");
        count++;
      }
    }
    if (count == 0) throw new InvalidOperationException("Configura al menos un intervalo de pedidos en línea.");
  }

  private static bool TryReadTime(JsonElement interval, string name, out TimeOnly value)
  {
    value = default;
    return interval.TryGetProperty(name, out var property)
      && property.ValueKind == JsonValueKind.String
      && TimeOnly.TryParseExact(property.GetString(), "HH:mm", out value);
  }

  private static string SafeAdminError(int number) => number switch
  {
    53675 or 53683 => "No se encontró la configuración del sitio público para esta sede.",
    53676 or 53677 or 53681 or 53682 => "El horario o la selección de productos no es válida.",
    53678 => "El máximo por pedido debe ser mayor que cero.",
    53679 => "Las versiones de términos y privacidad son obligatorias.",
    53680 => "Escribe un mensaje para explicar la pausa a los clientes.",
    53684 => "Sólo pueden habilitarse productos activos del RFC seleccionado.",
    53685 => "La configuración cambió. Recarga la pantalla antes de guardar.",
    53686 => "No se puede habilitar todavía: revisa horario, productos, PayPal, webhook y procesador.",
    _ => "No fue posible guardar la configuración de pedidos en línea."
  };

  private static DateTimeOffset Utc(DateTime value)
    => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
  private static string? NullIfWhiteSpace(string? value)
    => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

  private sealed class ScopeRow
  {
    public long CompanyId { get; set; }
    public long OrionSiteId { get; set; }
    public string? TaxRfc { get; set; }
    public int ModuleLocalSiteId { get; set; }
  }
  private sealed class AdminIdentityRow
  {
    public long PublicSiteId { get; set; }
    public string Rfc { get; set; } = string.Empty;
    public int SiteId { get; set; }
  }
  private sealed class AdminRow
  {
    public long PublicSiteId { get; set; }
    public string PublicSiteKey { get; set; } = string.Empty;
    public string Rfc { get; set; } = string.Empty;
    public int SiteId { get; set; }
    public bool IsEnabled { get; set; }
    public bool IsPaused { get; set; }
    public string? PauseMessage { get; set; }
    public bool GuestCheckoutEnabled { get; set; }
    public bool PickupEnabled { get; set; }
    public decimal MaximumOrderTotal { get; set; }
    public string WeeklyScheduleJson { get; set; } = "{}";
    public string TermsVersion { get; set; } = string.Empty;
    public string PrivacyVersion { get; set; } = string.Empty;
    public string ActiveMerchantProfileKey { get; set; } = string.Empty;
    public string GatewayEnvironment { get; set; } = string.Empty;
    public bool GatewayCredentialsConfigured { get; set; }
    public bool GatewayWebhookConfigured { get; set; }
    public DateTime? GatewayReadinessAtUtc { get; set; }
    public DateTime? ProcessorHeartbeatAtUtc { get; set; }
    public string RequiredGatewayEnvironment { get; set; } = string.Empty;
    public long ConfigurationVersion { get; set; }
    public byte[] RowVersion { get; set; } = [];
  }
  private sealed class AdminSaveRow
  {
    public long PublicSiteId { get; set; }
    public bool IsEnabled { get; set; }
  }
  private sealed class RefundRequestResult
  {
    public Guid Id { get; set; }
    public bool WasCreated { get; set; }
  }
}
