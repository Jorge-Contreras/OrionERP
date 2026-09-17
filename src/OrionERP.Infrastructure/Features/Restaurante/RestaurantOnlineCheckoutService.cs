using System.Data;
using System.Globalization;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrionERP.Application.Features.Payments.Clip;
using OrionERP.Application.Features.Platform;
using OrionERP.Application.Features.Restaurante;

namespace OrionERP.Infrastructure.Features.Restaurante;

/// <summary>
/// Public-site adapter for restaurant checkout. The verified PublicSite binding
/// is the only source of tenant identity; browser RFC/site values are never
/// accepted and the database procedures derive the same scope from
/// SESSION_CONTEXT.
/// </summary>
public sealed class RestaurantOnlineCheckoutService : IOnlineRestaurantCheckoutService
{
  private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
  private readonly IOrionSqlSessionFactory _sessions;
  private readonly IRestaurantPublicCatalogService _catalogs;
  private readonly IRestaurantPromotionService _promotions;
  private readonly IOnlineRestaurantQuoteTokenService _quoteTokens;
  private readonly IClipPaymentsClient _clip;
  private readonly RestaurantCheckoutOptions _options;
  private readonly ILogger<RestaurantOnlineCheckoutService> _logger;
  private readonly TimeProvider _clock;

  public RestaurantOnlineCheckoutService(
    IOrionSqlSessionFactory sessions,
    IRestaurantPublicCatalogService catalogs,
    IRestaurantPromotionService promotions,
    IOnlineRestaurantQuoteTokenService quoteTokens,
    IClipPaymentsClient clip,
    IOptions<RestaurantCheckoutOptions> options,
    ILogger<RestaurantOnlineCheckoutService> logger,
    TimeProvider? clock = null)
  {
    _sessions = sessions;
    _catalogs = catalogs;
    _promotions = promotions;
    _quoteTokens = quoteTokens;
    _clip = clip;
    _options = options.Value;
    _logger = logger;
    _clock = clock ?? TimeProvider.System;
  }

  public async Task<RestaurantOnlineOrderingConfigurationDto> GetConfigurationAsync(
    PublicSiteBinding binding,
    CancellationToken ct = default)
  {
    var bootstrap = await LoadBootstrapAsync(binding, ct);
    if (bootstrap is null)
    {
      return new RestaurantOnlineOrderingConfigurationDto
      {
        PublicSiteId = binding.PublicSiteId,
        PublicSiteKey = binding.PublicSiteKey,
        IsEnabled = false,
        CanAcceptOrders = false,
        UnavailableReason = "Los pedidos en línea todavía no están configurados.",
        Currency = NormalizeCurrency(_options.Currency),
        ClipLocale = NormalizeLocale(_options.ClipLocale),
        TermsVersion = _options.TermsVersion,
        PrivacyVersion = _options.PrivacyVersion,
        AllergenDisclaimer = _options.AllergenDisclaimer
      };
    }

    var availability = EvaluateAvailability(binding, bootstrap);
    return new RestaurantOnlineOrderingConfigurationDto
    {
      PublicSiteId = bootstrap.PublicSiteId,
      PublicSiteKey = binding.PublicSiteKey,
      IsEnabled = bootstrap.IsEnabled,
      IsPaused = bootstrap.IsPaused,
      PauseMessage = bootstrap.PauseMessage,
      CanAcceptOrders = availability.Code is null,
      UnavailableReason = availability.Message,
      AllowGuestCheckout = bootstrap.GuestCheckoutEnabled,
      PickupEnabled = bootstrap.PickupEnabled,
      MaximumOrderAmount = bootstrap.MaximumOrderTotal,
      OnlineHoursJson = bootstrap.WeeklyScheduleJson,
      Currency = NormalizeCurrency(_options.Currency),
      // El SDK necesita la clave en el navegador para tokenizar. No es secreta:
      // sola no autoriza ningun cobro, y el cargo siempre sale del backend.
      ClipApiKey = _options.IsClipConfigured ? _options.ClipApiKey.Trim() : string.Empty,
      ClipLocale = NormalizeLocale(_options.ClipLocale),
      TermsVersion = _options.TermsVersion,
      PrivacyVersion = _options.PrivacyVersion,
      AllergenDisclaimer = _options.AllergenDisclaimer,
      ProcessorHeartbeatAtUtc = bootstrap.ProcessorHeartbeatAtUtc
    };
  }

  public async Task<RestaurantOnlineQuoteResult> QuoteAsync(
    PublicSiteBinding binding,
    RestaurantOnlineQuoteRequest request,
    Guid? memberId,
    CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(request);
    EnsureRestaurantBinding(binding);
    var bootstrap = await LoadBootstrapAsync(binding, ct);
    if (bootstrap is null)
      return RestaurantOnlineQuoteResult.Fail("ordering_disabled", "Los pedidos en línea todavía no están configurados.");

    var availability = EvaluateAvailability(binding, bootstrap);
    if (availability.Code is not null)
      return RestaurantOnlineQuoteResult.Fail(availability.Code, availability.Message!);
    if (!memberId.HasValue && !bootstrap.GuestCheckoutEnabled)
      return RestaurantOnlineQuoteResult.Fail("member_required", "Inicia sesión para hacer este pedido.");

    var now = _clock.GetUtcNow();
    var catalog = await _catalogs.GetCatalogAsync(binding, now, ct);
    if (catalog is null)
      return RestaurantOnlineQuoteResult.Fail("not_available", "El menú no está disponible en este momento.");

    try
    {
      var priced = RestaurantOnlineCartCalculator.Calculate(catalog.Menu, request);
      var promotion = await _promotions.QuoteAsync(new RestaurantPromotionQuoteRequest
      {
        Rfc = binding.CompanyRfc,
        SiteId = bootstrap.SiteId,
        At = now,
        Channel = RestaurantSalesChannels.Web,
        OrderType = "Pickup",
        MemberId = memberId,
        Code = request.PromotionCode,
        Lines = priced.Lines.Select(line => new RestaurantPromotionQuoteLineRequest
        {
          LineKey = line.LineKey,
          ProductId = line.Product.Id,
          MaterialCategoryId = line.Product.MaterialCategoryId,
          Quantity = line.QuoteLine.Quantity,
          UnitPrice = line.QuoteLine.UnitPrice,
          ManualDiscountAmount = 0,
          IsCustom = false
        }).ToList()
      }, ct);

      if (!string.IsNullOrWhiteSpace(request.PromotionCode) && !promotion.CodeAccepted)
      {
        return RestaurantOnlineQuoteResult.Fail(
          "promotion_rejected",
          promotion.Message ?? "El código promocional no aplica a este pedido.");
      }

      var promotionDiscount = decimal.Round(
        promotion.PromotionDiscountTotal,
        2,
        MidpointRounding.AwayFromZero);
      var totals = RestaurantPosTotalsCalculator.Calculate(
        priced.MerchandiseTotal,
        promotionDiscount,
        delivery: 0,
        catalog.Menu.Site.TaxRate,
        catalog.Menu.Site.PricesIncludeTax);
      if (totals.Total <= 0)
        return RestaurantOnlineQuoteResult.Fail("invalid_total", "El total del pedido debe ser mayor que cero.");
      if (totals.Total > bootstrap.MaximumOrderTotal)
      {
        return RestaurantOnlineQuoteResult.Fail(
          "maximum_exceeded",
          $"El máximo por pedido en línea es {bootstrap.MaximumOrderTotal.ToString("C", CultureInfo.GetCultureInfo("es-MX"))} MXN.");
      }

      var normalizedRequest = NormalizeRequest(request, promotion.NormalizedCode, priced);
      var quoteLines = ApplyPromotionAndTax(
        priced,
        promotion,
        catalog.Menu.Site.TaxRate,
        catalog.Menu.Site.PricesIncludeTax);
      var snapshot = new RestaurantOnlineQuoteSnapshot
      {
        QuoteId = Guid.NewGuid(),
        PublicSiteId = binding.PublicSiteId,
        PublicSiteKey = binding.PublicSiteKey,
        Rfc = binding.CompanyRfc.Trim().ToUpperInvariant(),
        SiteId = bootstrap.SiteId,
        SettingsConfigurationVersion = bootstrap.ConfigurationVersion,
        MemberId = memberId,
        TermsVersion = bootstrap.TermsVersion,
        PrivacyVersion = bootstrap.PrivacyVersion,
        Request = normalizedRequest,
        Lines = quoteLines,
        Promotions = promotion.Adjustments,
        Subtotal = priced.MerchandiseTotal,
        PromotionDiscount = promotionDiscount,
        Tax = totals.Tax,
        Total = totals.Total,
        Currency = NormalizeCurrency(_options.Currency),
        IssuedAtUtc = now.UtcDateTime,
        ExpiresAtUtc = now.AddMinutes(_options.QuoteTokenLifetimeMinutes).UtcDateTime
      };
      snapshot.Fingerprint = RestaurantOnlineQuotePolicy.CreateFingerprint(snapshot);

      return new RestaurantOnlineQuoteResult
      {
        Succeeded = true,
        Code = "quoted",
        Message = "Revisamos precios y disponibilidad.",
        QuoteToken = _quoteTokens.Protect(snapshot),
        Fingerprint = snapshot.Fingerprint,
        ExpiresAtUtc = snapshot.ExpiresAtUtc,
        Currency = snapshot.Currency,
        Subtotal = snapshot.Subtotal,
        PromotionDiscount = snapshot.PromotionDiscount,
        Tax = snapshot.Tax,
        Total = snapshot.Total,
        Lines = snapshot.Lines,
        Promotions = snapshot.Promotions
      };
    }
    catch (InvalidOperationException exception)
    {
      return RestaurantOnlineQuoteResult.Fail("not_available", exception.Message);
    }
  }

  public async Task<RestaurantOnlineCheckoutBeginResult> BeginCheckoutAsync(
    PublicSiteBinding binding,
    RestaurantOnlineCheckoutBeginRequest request,
    Guid? memberId,
    CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(request);
    EnsureRestaurantBinding(binding);
    var inputError = ValidateBeginRequest(request);
    if (inputError is not null)
      return RestaurantOnlineCheckoutBeginResult.Fail(inputError.Value.Code, inputError.Value.Message);

    var now = _clock.GetUtcNow();
    if (!TryReadQuote(binding, request.QuoteToken, memberId, now, out var suppliedQuote, out var quoteFailure))
      return RestaurantOnlineCheckoutBeginResult.Fail(quoteFailure.Code, quoteFailure.Message);
    if (!string.Equals(request.TermsVersion, _options.TermsVersion, StringComparison.Ordinal)
        || !string.Equals(request.PrivacyVersion, _options.PrivacyVersion, StringComparison.Ordinal))
      return RestaurantOnlineCheckoutBeginResult.Fail("legal_version_changed", "Revisa y acepta la versión vigente de términos y privacidad.");

    var current = await QuoteAsync(binding, suppliedQuote!.Request, memberId, ct);
    if (!current.Succeeded || string.IsNullOrWhiteSpace(current.QuoteToken)
        || !_quoteTokens.TryUnprotect(current.QuoteToken, out var currentQuote)
        || currentQuote is null)
      return RestaurantOnlineCheckoutBeginResult.Fail(current.Code, current.Message);
    if (!RestaurantOnlineQuotePolicy.IsSameQuote(suppliedQuote, currentQuote))
      return RestaurantOnlineCheckoutBeginResult.Fail("requote_required", "El menú o el total cambió. Revisa el pedido antes de pagar.");

    var trackingToken = RestaurantOnlineTrackingTokenPolicy.Create(binding.PublicSiteId, request.ClientAttemptId);
    var trackingHash = RestaurantOnlineTrackingTokenPolicy.Hash(trackingToken);
    var attemptId = Guid.NewGuid();
    CheckoutAttemptRow attempt;
    try
    {
      await using var connection = await OpenAsync(binding, ct);
      attempt = await connection.QuerySingleAsync<CheckoutAttemptRow>(new CommandDefinition(
          "restaurante.OnlineCheckoutAttemptCreate",
          new
          {
            Id = attemptId,
            request.ClientAttemptId,
            MemberId = memberId,
            CustomerName = request.CustomerName.Trim(),
            CustomerEmail = request.CustomerEmail.Trim().ToLowerInvariant(),
            CustomerPhone = request.CustomerPhone.Trim(),
            QuoteFingerprint = currentQuote.Fingerprint,
            QuoteExpiresAtUtc = currentQuote.ExpiresAtUtc,
            currentQuote.SettingsConfigurationVersion,
            CartSnapshotJson = JsonSerializer.Serialize(currentQuote, JsonOptions),
            PromotionCode = NullIfWhiteSpace(currentQuote.Request.PromotionCode),
            currentQuote.Subtotal,
            PromotionDiscountTotal = currentQuote.PromotionDiscount,
            TaxTotal = currentQuote.Tax,
            currentQuote.Total,
            CurrencyCode = currentQuote.Currency,
            PrivacyVersion = currentQuote.PrivacyVersion,
            TermsVersion = currentQuote.TermsVersion,
            LegalAcceptedAtUtc = now.UtcDateTime,
            MerchantProfileKey = _options.MerchantProfileKey.Trim(),
            TrackingTokenHash = trackingHash,
            Provider = RestaurantPaymentGatewayProviders.Clip
          },
          commandType: CommandType.StoredProcedure,
          cancellationToken: ct));
    }
    catch (Microsoft.Data.SqlClient.SqlException exception) when (IsAttemptCreateRevalidationFailure(exception))
    {
      // SQL performs the final locked comparison. If configuration or quote
      // expiry changed after the server re-quote, return a safe customer state
      // without treating unrelated database failures as ordinary validation.
      var refreshedBootstrap = await LoadBootstrapAsync(binding, ct);
      if (refreshedBootstrap is null)
        return RestaurantOnlineCheckoutBeginResult.Fail(
          "ordering_disabled",
          "Los pedidos en línea todavía no están configurados.");
      var refreshedAvailability = EvaluateAvailability(binding, refreshedBootstrap);
      if (refreshedAvailability.Code is not null)
        return RestaurantOnlineCheckoutBeginResult.Fail(
          refreshedAvailability.Code,
          refreshedAvailability.Message!);
      return RestaurantOnlineCheckoutBeginResult.Fail(
        "requote_required",
        "El menú o la configuración cambió. Revisa nuevamente tu pedido antes de pagar.");
    }

    if (!CryptographicOperations.FixedTimeEquals(attempt.TrackingTokenHash, trackingHash)
        || !string.Equals(attempt.QuoteFingerprint, currentQuote.Fingerprint, StringComparison.Ordinal)
        || attempt.MemberId != memberId)
      return RestaurantOnlineCheckoutBeginResult.Fail("attempt_conflict", "Este intento de pago ya pertenece a otro pedido.");

    return new RestaurantOnlineCheckoutBeginResult
    {
      Succeeded = true,
      Code = attempt.WasCreated ? "checkout_ready" : "checkout_exists",
      Message = "Captura los datos de tu tarjeta para pagar.",
      TrackingToken = trackingToken,
      CheckoutStatus = attempt.State,
      WasExisting = !attempt.WasCreated
    };
  }

  public async Task<RestaurantOnlineChargeResult> ChargeAsync(
    PublicSiteBinding binding,
    RestaurantOnlineChargeRequest request,
    Guid? memberId,
    CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(request);
    EnsureRestaurantBinding(binding);
    if (request.ClientAttemptId == Guid.Empty
        || string.IsNullOrWhiteSpace(request.TrackingToken)
        || string.IsNullOrWhiteSpace(request.CardTokenId))
      return RestaurantOnlineChargeResult.Fail("invalid_request", "No fue posible identificar el pago.");

    var expectedTrackingToken = RestaurantOnlineTrackingTokenPolicy.Create(binding.PublicSiteId, request.ClientAttemptId);
    if (!FixedTimeEquals(expectedTrackingToken, request.TrackingToken.Trim()))
      return RestaurantOnlineChargeResult.Fail("not_found", "No encontramos este pedido.");

    CheckoutAttemptRow? attempt;
    await using (var connection = await OpenAsync(binding, ct))
    {
      attempt = await GetAttemptAsync(connection, clientAttemptId: request.ClientAttemptId, ct: ct);
    }
    if (attempt is null || attempt.MemberId != memberId
        || !string.Equals(attempt.Provider, RestaurantPaymentGatewayProviders.Clip, StringComparison.Ordinal))
      return RestaurantOnlineChargeResult.Fail("not_found", "No encontramos este pedido.");

    var terminal = ExistingChargeResult(attempt, expectedTrackingToken);
    if (terminal is not null)
      return terminal;

    var now = _clock.GetUtcNow();
    if (!TryReadQuote(binding, request.QuoteToken, memberId, now, out var suppliedQuote, out var quoteFailure))
    {
      var terminalState = quoteFailure.Code == "quote_expired"
        ? RestaurantOnlineCheckoutStatuses.Expired
        : RestaurantOnlineCheckoutStatuses.RequoteRequired;
      attempt = await SetStateAsync(
        binding,
        attempt.Id,
        attempt.State,
        terminalState,
        quoteFailure.Code.ToUpperInvariant(),
        quoteFailure.Message,
        ct);
      if (!string.Equals(attempt.State, terminalState, StringComparison.Ordinal))
      {
        var concurrentResult = ExistingChargeResult(attempt, expectedTrackingToken);
        if (concurrentResult is not null)
          return concurrentResult;
      }
      return new RestaurantOnlineChargeResult
      {
        Code = quoteFailure.Code,
        Message = quoteFailure.Message,
        CheckoutStatus = terminalState,
        TrackingToken = expectedTrackingToken,
        PaymentCaptured = false,
        IsPending = false
      };
    }
    if (!string.Equals(suppliedQuote!.Fingerprint, attempt.QuoteFingerprint, StringComparison.Ordinal))
      return RestaurantOnlineChargeResult.Fail("attempt_conflict", "La cotización no corresponde a este pago.");

    var bootstrap = await LoadBootstrapAsync(binding, ct);
    if (bootstrap is null)
      return RestaurantOnlineChargeResult.Fail("ordering_disabled", "Los pedidos en línea están deshabilitados.");
    var availability = EvaluateAvailability(binding, bootstrap);
    if (availability.Code is not null)
      return RestaurantOnlineChargeResult.Fail(availability.Code, availability.Message!);

    var current = await QuoteAsync(binding, suppliedQuote.Request, memberId, ct);
    if (!current.Succeeded || string.IsNullOrWhiteSpace(current.QuoteToken)
        || !_quoteTokens.TryUnprotect(current.QuoteToken, out var currentQuote)
        || currentQuote is null
        || !RestaurantOnlineQuotePolicy.IsSameQuote(suppliedQuote, currentQuote))
    {
      attempt = await SetStateAsync(binding, attempt.Id, attempt.State, RestaurantOnlineCheckoutStatuses.RequoteRequired,
        "QUOTE_CHANGED", "El precio o la disponibilidad cambió antes del cobro.", ct);
      if (!string.Equals(attempt.State, RestaurantOnlineCheckoutStatuses.RequoteRequired, StringComparison.Ordinal))
      {
        var concurrentResult = ExistingChargeResult(attempt, expectedTrackingToken);
        if (concurrentResult is not null)
          return concurrentResult;
      }
      return RestaurantOnlineChargeResult.Fail("requote_required", "El menú o el total cambió. No se hizo ningún cobro.");
    }

    // A partir de aqui el cargo es el punto de no retorno. La base concede el
    // derecho exclusivo a cobrar este intento; quien no lo gane no llama a Clip.
    var chargeRequestId = Guid.NewGuid();
    var begin = await BeginChargeAsync(binding, attempt.Id, chargeRequestId, ct);
    if (begin.Blocked)
    {
      await using var connection = await OpenAsync(binding, ct);
      var refreshed = await GetAttemptAsync(connection, id: attempt.Id, ct: ct) ?? attempt;
      return ExistingChargeResult(refreshed, expectedTrackingToken)
        ?? RestaurantOnlineChargeResult.Fail(
          begin.IsStateConflict ? "charge_in_flight" : "ordering_paused",
          begin.IsStateConflict
            ? "Ya estamos procesando un pago de este pedido. No vuelvas a pagar."
            : "Los pedidos se pausaron antes del cobro. No se hizo ningún cargo.");
    }

    ClipPaymentResult payment;
    try
    {
      payment = await _clip.CreatePaymentAsync(new ClipPaymentRequest
      {
        Amount = currentQuote.Total,
        Currency = currentQuote.Currency,
        Description = "Pedido para recoger",
        CardTokenId = request.CardTokenId.Trim(),
        ExternalReference = ClipExternalReference.From(attempt.Id),
        WebhookUrl = _options.WebhookUrl ?? string.Empty,
        CaptureMethod = "automatic",
        Installments = _options.Installments,
        Customer = BuildCustomer(attempt)
      }, ct);
    }
    catch (ClipClientException exception)
    {
      LogProviderFailure(attempt.Id, exception);
      if (exception.IsOutcomeUnknown)
      {
        // No sabemos si Clip cobro. Jamas se reintenta: lo resuelve la
        // reconciliacion por external_reference.
        await RecordChargeAsync(binding, attempt.Id, chargeRequestId, "Unknown",
          failureCode: exception.ProviderErrorCode,
          failureMessage: "El cobro no devolvió respuesta y está en verificación.",
          ct: ct);
        return PendingChargeResult(expectedTrackingToken, RestaurantOnlineCheckoutStatuses.ChargeUnknown);
      }

      var deniedCode = exception.IsNotConfigured ? "CLIP_NOT_CONFIGURED" : exception.ProviderErrorCode;
      var deniedMessage = exception.IsNotConfigured
        ? "El pago en línea no está disponible en este momento. No se hizo ningún cargo."
        : "No pudimos procesar el pago. No se hizo ningún cargo; intenta con otra tarjeta.";
      await RecordChargeAsync(binding, attempt.Id, chargeRequestId, "Denied",
        failureCode: deniedCode, failureMessage: deniedMessage, ct: ct);
      return new RestaurantOnlineChargeResult
      {
        Succeeded = true,
        Code = exception.IsNotConfigured ? "clip_not_configured" : "payment_denied",
        Message = deniedMessage,
        CheckoutStatus = RestaurantOnlineCheckoutStatuses.PaymentDenied,
        TrackingToken = expectedTrackingToken,
        PaymentCaptured = false,
        IsPending = false
      };
    }

    return await ResolveChargeAsync(
      binding,
      attempt.Id,
      chargeRequestId,
      expectedTrackingToken,
      payment,
      currentQuote.Total,
      currentQuote.Currency,
      ct);
  }

  public async Task<RestaurantOnlineChargeResult> ConfirmChargeAsync(
    PublicSiteBinding binding,
    RestaurantOnlineChargeConfirmRequest request,
    Guid? memberId,
    CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(request);
    EnsureRestaurantBinding(binding);
    if (request.ClientAttemptId == Guid.Empty
        || string.IsNullOrWhiteSpace(request.TrackingToken)
        || string.IsNullOrWhiteSpace(request.PaymentId))
      return RestaurantOnlineChargeResult.Fail("invalid_request", "No fue posible identificar el pago.");

    var expectedTrackingToken = RestaurantOnlineTrackingTokenPolicy.Create(binding.PublicSiteId, request.ClientAttemptId);
    if (!FixedTimeEquals(expectedTrackingToken, request.TrackingToken.Trim()))
      return RestaurantOnlineChargeResult.Fail("not_found", "No encontramos este pedido.");

    CheckoutAttemptRow? attempt;
    await using (var connection = await OpenAsync(binding, ct))
    {
      attempt = await GetAttemptAsync(connection, clientAttemptId: request.ClientAttemptId, ct: ct);
    }
    if (attempt is null || attempt.MemberId != memberId
        || !string.Equals(attempt.Provider, RestaurantPaymentGatewayProviders.Clip, StringComparison.Ordinal))
      return RestaurantOnlineChargeResult.Fail("not_found", "No encontramos este pedido.");

    var terminal = ExistingChargeResult(attempt, expectedTrackingToken);
    if (terminal is not null)
      return terminal;

    // El navegador no puede nombrar un pago arbitrario: el identificador tiene
    // que ser el que este intento ya tenia sellado.
    if (attempt.State != RestaurantOnlineCheckoutStatuses.Authenticating3ds
        || string.IsNullOrWhiteSpace(attempt.ProviderOrderId)
        || !FixedTimeEquals(attempt.ProviderOrderId, request.PaymentId.Trim()))
      return RestaurantOnlineChargeResult.Fail("not_found", "No encontramos este pago.");
    if (attempt.ChargeRequestId is null)
      return RestaurantOnlineChargeResult.Fail("invalid_request", "Este pago no tiene un cargo en curso.");

    ClipPaymentResult payment;
    try
    {
      payment = await _clip.GetPaymentAsync(attempt.ProviderOrderId, ct);
    }
    catch (ClipClientException exception)
    {
      LogProviderFailure(attempt.Id, exception);
      // La consulta es segura de repetir, asi que una falla aqui no cambia nada:
      // el estado sigue en 3DS y la recuperacion lo resolvera.
      return PendingChargeResult(expectedTrackingToken, RestaurantOnlineCheckoutStatuses.Authenticating3ds);
    }

    return await ResolveChargeAsync(
      binding,
      attempt.Id,
      attempt.ChargeRequestId.Value,
      expectedTrackingToken,
      payment,
      attempt.Total,
      attempt.CurrencyCode,
      ct);
  }

  public async Task<RestaurantOnlineCheckoutStatusDto?> GetStatusAsync(
    PublicSiteBinding binding,
    string trackingToken,
    CancellationToken ct = default)
  {
    EnsureRestaurantBinding(binding);
    if (string.IsNullOrWhiteSpace(trackingToken) || trackingToken.Length > 100)
      return null;
    await using var connection = await OpenAsync(binding, ct);
    return await connection.QuerySingleOrDefaultAsync<RestaurantOnlineCheckoutStatusDto>(new CommandDefinition(
      "restaurante.OnlineCheckoutStatusGet",
      new { TrackingTokenHash = RestaurantOnlineTrackingTokenPolicy.Hash(trackingToken) },
      commandType: CommandType.StoredProcedure,
      cancellationToken: ct));
  }

  /// <summary>
  /// Clip's notification carries no signature and only the payment id, so
  /// nothing in the body is treated as truth. The event is recorded against the
  /// checkout it belongs to and the recovery worker learns the real state by
  /// calling GET /payments. A forged notification therefore costs one lookup and
  /// can never move money or state on its own.
  /// </summary>
  public async Task<RestaurantClipWebhookResult> ProcessClipWebhookAsync(
    PublicSiteBinding binding,
    RestaurantClipWebhookRequest request,
    CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(request);
    EnsureRestaurantBinding(binding);
    if (!TryParseClipNotification(request.RawBody, out var notification))
      return new RestaurantClipWebhookResult(false, false);

    await using var connection = await OpenAsync(binding, ct);
    var result = await connection.QuerySingleAsync<WebhookRecordResult>(new CommandDefinition(
      "restaurante.PaymentGatewayEventRecord",
      new
      {
        MerchantProfileKey = _options.MerchantProfileKey.Trim(),
        // El aviso del mismo pago llega varias veces con distinto event_type, y
        // el par identifica cada transicion sin colisionar con la anterior.
        ProviderEventId = $"{notification.Id}:{notification.EventType}",
        EventType = notification.EventType,
        ResourceType = "payment",
        ResourceId = notification.Id,
        RelatedOrderId = notification.Id,
        RelatedCaptureId = (string?)null,
        RelatedRefundId = (string?)null,
        PayloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.RawBody))),
        VerificationStatus = "Verified",
        Provider = RestaurantPaymentGatewayProviders.Clip
      },
      commandType: CommandType.StoredProcedure,
      cancellationToken: ct));
    return new RestaurantClipWebhookResult(true, result.WasMatched, notification.Id);
  }

  /// <summary>
  /// Turns a Clip payment into a durable checkout outcome. Used by both the
  /// direct charge and the post-3DS confirmation, so a payment can only ever be
  /// interpreted one way.
  /// </summary>
  private async Task<RestaurantOnlineChargeResult> ResolveChargeAsync(
    PublicSiteBinding binding,
    Guid attemptId,
    Guid chargeRequestId,
    string trackingToken,
    ClipPaymentResult payment,
    decimal expectedAmount,
    string expectedCurrency,
    CancellationToken ct)
  {
    // Un pago que no trae nuestra referencia, o que no coincide en importe, no se
    // puede aceptar como cobro de este pedido; queda en verificacion.
    var belongsToAttempt = ClipExternalReference.TryParse(payment.ExternalReference, out var referencedAttemptId)
      && referencedAttemptId == attemptId;
    var amountMatches = payment.Amount == expectedAmount
      && string.Equals(payment.Currency, expectedCurrency, StringComparison.OrdinalIgnoreCase);
    if (!belongsToAttempt || !amountMatches)
    {
      _logger.LogError(
        "Clip payment {PaymentId} did not match checkout {CheckoutAttemptId}: reference {ExternalReference}, amount {Amount} {Currency} vs {ExpectedAmount} {ExpectedCurrency}.",
        payment.PaymentId,
        attemptId,
        payment.ExternalReference,
        payment.Amount,
        payment.Currency,
        expectedAmount,
        expectedCurrency);
      await RecordChargeAsync(binding, attemptId, chargeRequestId, "Unknown",
        providerPaymentId: NullIfWhiteSpace(payment.PaymentId),
        failureCode: "CLIP_PAYMENT_MISMATCH",
        failureMessage: "El pago no corresponde a este pedido y está en verificación.",
        ct: ct);
      return PendingChargeResult(trackingToken, RestaurantOnlineCheckoutStatuses.ChargeUnknown);
    }

    if (payment.IsApproved)
    {
      var attempt = await RecordChargeAsync(binding, attemptId, chargeRequestId, "Approved",
        providerPaymentId: payment.PaymentId,
        externalReference: payment.ExternalReference,
        grossAmount: payment.Amount,
        currencyCode: payment.Currency,
        capturedAtUtc: payment.ApprovedAtUtc?.UtcDateTime,
        ct: ct);
      return new RestaurantOnlineChargeResult
      {
        Succeeded = true,
        Code = "payment_received",
        Message = "Pago recibido. Estamos confirmando tu pedido.",
        CheckoutStatus = attempt.State,
        TrackingToken = trackingToken,
        OrderFolio = attempt.OrderFolio,
        PaymentCaptured = true,
        IsPending = attempt.State != RestaurantOnlineCheckoutStatuses.PosCreated
      };
    }

    if (payment.RequiresThreeDSecure)
    {
      await RecordChargeAsync(binding, attemptId, chargeRequestId, "Pending3ds",
        providerPaymentId: payment.PaymentId,
        externalReference: payment.ExternalReference,
        ct: ct);
      return new RestaurantOnlineChargeResult
      {
        Succeeded = true,
        Code = "three_ds_required",
        Message = "Tu banco necesita verificar el pago.",
        CheckoutStatus = RestaurantOnlineCheckoutStatuses.Authenticating3ds,
        TrackingToken = trackingToken,
        PaymentCaptured = false,
        IsPending = true,
        ThreeDSecureUrl = payment.PendingAction!.Url
      };
    }

    if (payment.IsTerminalFailure)
    {
      var message = ClipStatusMessages.ForCode(payment.StatusCode);
      await RecordChargeAsync(binding, attemptId, chargeRequestId, "Denied",
        failureCode: NullIfWhiteSpace(payment.StatusCode) ?? "CLIP_REJECTED",
        failureMessage: message,
        ct: ct);
      return new RestaurantOnlineChargeResult
      {
        Succeeded = true,
        Code = "payment_denied",
        Message = ClipStatusMessages.AllowsAnotherCard(payment.StatusCode)
          ? $"{message} No se hizo ningún cargo."
          : message,
        CheckoutStatus = RestaurantOnlineCheckoutStatuses.PaymentDenied,
        TrackingToken = trackingToken,
        PaymentCaptured = false,
        IsPending = false
      };
    }

    // Pendiente sin 3DS accionable, autorizado sin captura, o un estado que no
    // esperabamos: nada que el cliente pueda hacer, lo resuelve la recuperacion.
    _logger.LogWarning(
      "Clip payment {PaymentId} for checkout {CheckoutAttemptId} landed on unhandled status {Status}/{StatusCode}.",
      payment.PaymentId,
      attemptId,
      payment.Status,
      payment.StatusCode);
    await RecordChargeAsync(binding, attemptId, chargeRequestId, "Unknown",
      providerPaymentId: NullIfWhiteSpace(payment.PaymentId),
      externalReference: payment.ExternalReference,
      failureCode: NullIfWhiteSpace(payment.StatusCode) ?? "CLIP_UNRESOLVED",
      failureMessage: "El pago quedó en verificación.",
      ct: ct);
    return PendingChargeResult(trackingToken, RestaurantOnlineCheckoutStatuses.ChargeUnknown);
  }

  private async Task<ChargeBeginOutcome> BeginChargeAsync(
    PublicSiteBinding binding,
    Guid attemptId,
    Guid chargeRequestId,
    CancellationToken ct)
  {
    try
    {
      await using var connection = await OpenAsync(binding, ct);
      var row = await connection.QuerySingleAsync<ChargeBeginRow>(new CommandDefinition(
        "restaurante.OnlineCheckoutChargeBegin",
        new
        {
          Id = attemptId,
          ChargeRequestId = chargeRequestId,
          _options.ProcessorHeartbeatMaxAgeSeconds
        },
        commandType: CommandType.StoredProcedure,
        cancellationToken: ct));
      return row.ChargeRequestId == chargeRequestId
        ? new ChargeBeginOutcome(false, false)
        : new ChargeBeginOutcome(true, true);
    }
    catch (Microsoft.Data.SqlClient.SqlException exception) when (exception.Number is 54030 or 54031)
    {
      _logger.LogInformation(
        "Checkout {CheckoutAttemptId} was not granted the exclusive charge (SQL {Number}).",
        attemptId,
        exception.Number);
      return new ChargeBeginOutcome(true, exception.Number == 54030);
    }
  }

  private async Task<CheckoutAttemptRow> RecordChargeAsync(
    PublicSiteBinding binding,
    Guid attemptId,
    Guid chargeRequestId,
    string outcome,
    string? providerPaymentId = null,
    string? externalReference = null,
    decimal? grossAmount = null,
    string? currencyCode = null,
    string? failureCode = null,
    string? failureMessage = null,
    DateTime? capturedAtUtc = null,
    CancellationToken ct = default)
  {
    await using var connection = await OpenAsync(binding, ct);
    using var multi = await connection.QueryMultipleAsync(new CommandDefinition(
      "restaurante.OnlineCheckoutChargeResult",
      new
      {
        Id = attemptId,
        ChargeRequestId = chargeRequestId,
        Outcome = outcome,
        ProviderPaymentId = providerPaymentId,
        ExternalReference = externalReference,
        GrossAmount = grossAmount,
        CurrencyCode = currencyCode,
        FailureCode = failureCode,
        FailureMessage = failureMessage,
        CapturedAtUtc = capturedAtUtc
      },
      commandType: CommandType.StoredProcedure,
      cancellationToken: ct));
    var attempt = await multi.ReadSingleAsync<CheckoutAttemptRow>();
    _ = await multi.ReadSingleOrDefaultAsync<GatewayTransactionRow>();
    return attempt;
  }

  private static RestaurantOnlineChargeResult PendingChargeResult(string trackingToken, string checkoutStatus)
    => new()
    {
      Succeeded = true,
      Code = checkoutStatus == RestaurantOnlineCheckoutStatuses.Authenticating3ds
        ? "three_ds_pending"
        : "charge_pending",
      Message = "Estamos confirmando el pago. No vuelvas a pagar; actualizaremos este pedido.",
      CheckoutStatus = checkoutStatus,
      TrackingToken = trackingToken,
      PaymentCaptured = false,
      IsPending = true
    };

  private static ClipCustomer BuildCustomer(CheckoutAttemptRow attempt)
  {
    var name = attempt.CustomerName?.Trim() ?? string.Empty;
    var separator = name.IndexOf(' ');
    return new ClipCustomer
    {
      FirstName = separator > 0 ? name[..separator] : name,
      LastName = separator > 0 ? name[(separator + 1)..].Trim() : string.Empty,
      Email = attempt.CustomerEmail?.Trim() ?? string.Empty,
      // Clip rechaza el telefono con separadores; solo acepta los digitos.
      Phone = new string((attempt.CustomerPhone ?? string.Empty).Where(char.IsDigit).ToArray())
    };
  }

  private static bool TryParseClipNotification(string rawBody, out ClipWebhookNotification notification)
  {
    notification = new ClipWebhookNotification();
    if (string.IsNullOrWhiteSpace(rawBody))
      return false;
    try
    {
      using var document = JsonDocument.Parse(rawBody, new JsonDocumentOptions { MaxDepth = 16 });
      var root = document.RootElement;
      if (root.ValueKind != JsonValueKind.Object)
        return false;
      var id = ReadString(root, "id");
      var eventType = ReadString(root, "event_type");
      var origin = ReadString(root, "origin");
      if (string.IsNullOrWhiteSpace(id) || id.Length > 64
          || string.IsNullOrWhiteSpace(eventType) || eventType.Length > 30)
        return false;
      notification = new ClipWebhookNotification { Id = id, EventType = eventType, Origin = origin };
      return true;
    }
    catch (JsonException)
    {
      return false;
    }
  }

  private async Task<BootstrapRow?> LoadBootstrapAsync(PublicSiteBinding binding, CancellationToken ct)
  {
    EnsureRestaurantBinding(binding);
    await using var connection = await OpenAsync(binding, ct);
    await connection.ExecuteAsync(new CommandDefinition(
      "restaurante.OnlineOrderingRuntimeReadinessSet",
      new
      {
        GatewayEnvironment = _options.UseLiveClip ? "Live" : "Sandbox",
        GatewayCredentialsConfigured = _options.IsClipConfigured && _options.IsApiKeyEnvironmentConsistent,
        // Clip no da de alta webhooks ni entrega un identificador que validar: el
        // aviso se manda a la URL publica del sitio, asi que eso es lo que hay
        // que tener listo para poder recibirlo.
        GatewayWebhookConfigured = _options.IsWebhookConfigured,
        MerchantProfileKey = _options.MerchantProfileKey.Trim(),
        TermsVersion = _options.TermsVersion.Trim(),
        PrivacyVersion = _options.PrivacyVersion.Trim()
      },
      commandType: CommandType.StoredProcedure,
      cancellationToken: ct));
    using var multi = await connection.QueryMultipleAsync(new CommandDefinition(
      "restaurante.OnlineOrderingBootstrapGet",
      commandType: CommandType.StoredProcedure,
      cancellationToken: ct));
    var row = await multi.ReadSingleOrDefaultAsync<BootstrapRow>();
    if (row is null)
      return null;
    row.EnabledProductIds = (await multi.ReadAsync<long>()).ToHashSet();
    if (row.PublicSiteId != binding.PublicSiteId
        || !string.Equals(row.Rfc, binding.CompanyRfc, StringComparison.OrdinalIgnoreCase)
        || !string.Equals(row.PublicSiteKey, binding.PublicSiteKey, StringComparison.Ordinal)
        || !string.Equals(row.CanonicalHost, binding.CanonicalHost, StringComparison.OrdinalIgnoreCase))
      throw new UnauthorizedAccessException("La configuración de pedidos no pertenece al sitio público verificado.");
    return row;
  }

  private (string? Code, string? Message) EvaluateAvailability(PublicSiteBinding binding, BootstrapRow row)
  {
    if (!row.IsEnabled)
      return ("ordering_disabled", "Los pedidos en línea no están habilitados en este momento.");
    if (row.IsPaused)
      return ("ordering_paused", NullIfWhiteSpace(row.PauseMessage) ?? "Los pedidos están pausados temporalmente.");
    if (!row.PickupEnabled)
      return ("ordering_disabled", "Los pedidos para recoger no están habilitados.");
    if (row.MaximumOrderTotal <= 0)
      return ("ordering_disabled", "El límite de pedidos no está configurado.");
    if (row.EnabledProductIds.Count == 0)
      return ("ordering_disabled", "Todavía no hay productos habilitados para pedir en línea.");
    if (!RestaurantOnlineQuotePolicy.IsOpen(row.WeeklyScheduleJson, binding.TimeZoneId, _clock.GetUtcNow()))
      return ("ordering_closed", "Los pedidos en línea están cerrados según el horario vigente.");
    if (!_options.IsClipConfigured)
      return ("clip_not_configured", "El pago en línea todavía no está configurado.");
    // Una clave de prueba en produccion no cobraria y una productiva en sandbox
    // cobraria de verdad: en ambos casos el cobro tiene que quedar cerrado.
    if (!_options.IsApiKeyEnvironmentConsistent)
      return ("clip_not_configured", "Las credenciales de cobro no corresponden a este ambiente.");
    if (!_options.IsWebhookConfigured)
      return ("clip_not_configured", "La dirección pública para recibir avisos de pago no está configurada.");
    if (string.IsNullOrWhiteSpace(_options.MerchantProfileKey))
      return ("clip_not_configured", "El perfil de cobro no está configurado.");
    if (!row.GatewayCredentialsConfigured || !row.GatewayWebhookConfigured
        || !string.Equals(row.ActiveMerchantProfileKey, _options.MerchantProfileKey, StringComparison.Ordinal)
        || !string.Equals(row.GatewayEnvironment, _options.UseLiveClip ? "Live" : "Sandbox", StringComparison.OrdinalIgnoreCase)
        || !row.GatewayReadinessAtUtc.HasValue
        || _clock.GetUtcNow() - new DateTimeOffset(DateTime.SpecifyKind(row.GatewayReadinessAtUtc.Value, DateTimeKind.Utc))
          > TimeSpan.FromMinutes(5))
      return ("clip_not_configured", "La configuración segura de cobro no está lista.");
    if (!string.Equals(row.TermsVersion, _options.TermsVersion, StringComparison.Ordinal)
        || !string.Equals(row.PrivacyVersion, _options.PrivacyVersion, StringComparison.Ordinal))
      return ("ordering_disabled", "Las versiones legales del sitio y del checkout no coinciden.");
    if (!row.ProcessorHeartbeatAtUtc.HasValue
        || _clock.GetUtcNow() - new DateTimeOffset(DateTime.SpecifyKind(row.ProcessorHeartbeatAtUtc.Value, DateTimeKind.Utc))
          > TimeSpan.FromSeconds(_options.ProcessorHeartbeatMaxAgeSeconds))
      return ("processor_unavailable", "El procesador de pedidos está temporalmente fuera de línea.");
    if (Uri.TryCreate(_options.PublicBaseUrl, UriKind.Absolute, out var publicBase)
        && !string.Equals(publicBase.Host, binding.CanonicalHost, StringComparison.OrdinalIgnoreCase))
      return ("ordering_disabled", "La dirección pública del checkout no coincide con la configuración del sitio.");
    return (null, null);
  }

  private bool TryReadQuote(
    PublicSiteBinding binding,
    string token,
    Guid? memberId,
    DateTimeOffset now,
    out RestaurantOnlineQuoteSnapshot? quote,
    out (string Code, string Message) failure)
  {
    quote = null;
    if (string.IsNullOrWhiteSpace(token) || !_quoteTokens.TryUnprotect(token, out quote) || quote is null)
    {
      failure = ("invalid_quote", "La cotización no es válida. Vuelve a revisar el carrito.");
      return false;
    }
    if (quote.ExpiresAtUtc <= now.UtcDateTime)
    {
      failure = ("quote_expired", "La cotización venció. Vuelve a revisar precios y disponibilidad.");
      return false;
    }
    if (quote.PublicSiteId != binding.PublicSiteId
        || !string.Equals(quote.PublicSiteKey, binding.PublicSiteKey, StringComparison.Ordinal)
        || !string.Equals(quote.Rfc, binding.CompanyRfc, StringComparison.OrdinalIgnoreCase)
        || quote.MemberId != memberId
        || !string.Equals(quote.TermsVersion, _options.TermsVersion, StringComparison.Ordinal)
        || !string.Equals(quote.PrivacyVersion, _options.PrivacyVersion, StringComparison.Ordinal)
        || !string.Equals(quote.Currency, NormalizeCurrency(_options.Currency), StringComparison.Ordinal)
        || !string.Equals(quote.Fingerprint, RestaurantOnlineQuotePolicy.CreateFingerprint(quote), StringComparison.Ordinal))
    {
      failure = ("invalid_quote", "La cotización no pertenece a este sitio o esta sesión.");
      return false;
    }
    failure = default;
    return true;
  }

  private static RestaurantOnlineQuoteRequest NormalizeRequest(
    RestaurantOnlineQuoteRequest source,
    string? normalizedPromotionCode,
    RestaurantOnlineCartPricingResult priced)
    => new()
    {
      PromotionCode = NullIfWhiteSpace(normalizedPromotionCode),
      Lines = priced.Lines.Select(line => new RestaurantOnlineCartLineRequest
      {
        ProductId = line.OrderLine.ProductId!.Value,
        MenuSectionId = line.OrderLine.MenuSectionId!.Value,
        Quantity = line.OrderLine.Quantity,
        Notes = NullIfWhiteSpace(line.OrderLine.Notes),
        ModifierOptionIds = line.OrderLine.ModifierOptionIds.Distinct().Order().ToList(),
        ComboSelections = line.OrderLine.ComboSelections
          .OrderBy(item => item.ComboSlotId)
          .ThenBy(item => item.ComboSlotOptionId)
          .Select(item => new RestaurantOnlineComboSelectionRequest
          {
            ComboSlotId = item.ComboSlotId,
            ComboSlotOptionId = item.ComboSlotOptionId,
            ModifierOptionIds = item.ModifierOptionIds.Distinct().Order().ToList(),
            Notes = NullIfWhiteSpace(item.Notes)
          }).ToList()
      }).ToList()
    };

  private static IReadOnlyList<RestaurantOnlineQuoteLineDto> ApplyPromotionAndTax(
    RestaurantOnlineCartPricingResult priced,
    RestaurantPromotionQuoteDto promotion,
    decimal taxRate,
    bool pricesIncludeTax)
  {
    var discountByLine = promotion.LineAdjustments
      .GroupBy(item => item.LineKey, StringComparer.Ordinal)
      .ToDictionary(
        group => group.Key,
        group => decimal.Round(group.Sum(item => item.DiscountAmount), 2, MidpointRounding.AwayFromZero),
        StringComparer.Ordinal);
    return priced.Lines.Select(line =>
    {
      var dto = line.QuoteLine;
      var discount = Math.Clamp(discountByLine.GetValueOrDefault(line.LineKey), 0, dto.Total);
      var discounted = decimal.Round(dto.Total - discount, 2, MidpointRounding.AwayFromZero);
      var tax = pricesIncludeTax
        ? taxRate == 0 ? 0 : decimal.Round(discounted - discounted / (1 + taxRate), 2, MidpointRounding.AwayFromZero)
        : decimal.Round(discounted * taxRate, 2, MidpointRounding.AwayFromZero);
      return new RestaurantOnlineQuoteLineDto
      {
        Index = dto.Index,
        ProductId = dto.ProductId,
        MenuSectionId = dto.MenuSectionId,
        ProductName = dto.ProductName,
        Quantity = dto.Quantity,
        UnitPrice = dto.UnitPrice,
        DiscountAmount = discount,
        TaxAmount = tax,
        Total = pricesIncludeTax ? discounted : discounted + tax,
        Notes = dto.Notes,
        Modifiers = dto.Modifiers,
        ComboSelections = dto.ComboSelections
      };
    }).ToArray();
  }

  private static (string Code, string Message)? ValidateBeginRequest(RestaurantOnlineCheckoutBeginRequest request)
  {
    if (request.ClientAttemptId == Guid.Empty)
      return ("invalid_request", "No fue posible identificar este intento de pago.");
    if (!request.TermsAccepted)
      return ("legal_acceptance_required", "Debes aceptar los términos y el aviso de privacidad.");
    if (string.IsNullOrWhiteSpace(request.CustomerName) || request.CustomerName.Trim().Length > 150)
      return ("invalid_contact", "Escribe tu nombre.");
    if (string.IsNullOrWhiteSpace(request.CustomerEmail) || request.CustomerEmail.Trim().Length > 256
        || !MailAddress.TryCreate(request.CustomerEmail.Trim(), out _))
      return ("invalid_contact", "Escribe un correo electrónico válido.");
    var normalizedPhone = request.CustomerPhone?.Trim();
    var phoneDigits = normalizedPhone is null
      ? 0
      : normalizedPhone.Count(char.IsDigit);
    if (string.IsNullOrWhiteSpace(normalizedPhone) || normalizedPhone.Length > 30
        || phoneDigits is < 10 or > 15
        || normalizedPhone.Any(character =>
          !char.IsDigit(character) && character is not '+' and not '-' and not '(' and not ')' and not ' ' and not '.'))
      return ("invalid_contact", "Escribe un teléfono válido.");
    return null;
  }

  private static RestaurantOnlineChargeResult? ExistingChargeResult(CheckoutAttemptRow attempt, string trackingToken)
  {
    if (attempt.State == RestaurantOnlineCheckoutStatuses.PosCreated)
    {
      return new RestaurantOnlineChargeResult
      {
        Succeeded = true,
        Code = "order_confirmed",
        Message = "Tu pedido ya está confirmado.",
        CheckoutStatus = attempt.State,
        TrackingToken = trackingToken,
        OrderFolio = attempt.OrderFolio,
        PaymentCaptured = true,
        IsPending = false
      };
    }
    if (attempt.State is RestaurantOnlineCheckoutStatuses.Captured
        or RestaurantOnlineCheckoutStatuses.CapturedNeedsOrder
        or RestaurantOnlineCheckoutStatuses.RefundRequested
        or RestaurantOnlineCheckoutStatuses.RefundPending
        or RestaurantOnlineCheckoutStatuses.Refunded)
    {
      return new RestaurantOnlineChargeResult
      {
        Succeeded = true,
        Code = attempt.State == RestaurantOnlineCheckoutStatuses.Refunded ? "refunded" : "payment_received",
        Message = attempt.State == RestaurantOnlineCheckoutStatuses.Refunded
          ? "El pago fue reembolsado."
          : "Pago recibido. Estamos confirmando tu pedido.",
        CheckoutStatus = attempt.State,
        TrackingToken = trackingToken,
        OrderFolio = attempt.OrderFolio,
        PaymentCaptured = true,
        IsPending = attempt.State != RestaurantOnlineCheckoutStatuses.Refunded
      };
    }
    // Un cargo en vuelo o en 3DS todavia puede prosperar en Clip, asi que no se
    // ofrece otra tarjeta: cobrar de nuevo cobraria dos veces.
    if (attempt.State is RestaurantOnlineCheckoutStatuses.ChargePending
        or RestaurantOnlineCheckoutStatuses.ChargeUnknown
        or RestaurantOnlineCheckoutStatuses.Authenticating3ds)
    {
      return PendingChargeResult(trackingToken, attempt.State);
    }
    if (attempt.State is RestaurantOnlineCheckoutStatuses.RequoteRequired
        or RestaurantOnlineCheckoutStatuses.Expired
        or RestaurantOnlineCheckoutStatuses.Failed)
    {
      return new RestaurantOnlineChargeResult
      {
        Code = attempt.State == RestaurantOnlineCheckoutStatuses.Expired ? "quote_expired" : "requote_required",
        Message = attempt.State == RestaurantOnlineCheckoutStatuses.Expired
          ? "La cotización venció. Revisa nuevamente tu pedido."
          : "El pedido debe cotizarse nuevamente antes de pagar.",
        CheckoutStatus = attempt.State,
        TrackingToken = trackingToken,
        PaymentCaptured = false,
        IsPending = false
      };
    }
    // Quoted y PaymentDenied admiten un cargo nuevo, asi que no son terminales.
    return null;
  }

  private static bool IsAttemptCreateRevalidationFailure(
    Microsoft.Data.SqlClient.SqlException exception)
    => exception.Number is 53624 or 53628;

  private async Task<CheckoutAttemptRow> SetStateAsync(
    PublicSiteBinding binding,
    Guid id,
    string? expectedState,
    string state,
    string? failureCode,
    string? failureMessage,
    CancellationToken ct)
  {
    try
    {
      await using var connection = await OpenAsync(binding, ct);
      return await connection.QuerySingleAsync<CheckoutAttemptRow>(new CommandDefinition(
        "restaurante.OnlineCheckoutStateSet",
        new
        {
          Id = id,
          ExpectedState = expectedState,
          State = state,
          FailureCode = failureCode,
          FailureMessage = failureMessage,
          NextRetryAtUtc = (DateTime?)null,
          RestaurantOrderId = (Guid?)null
        },
        commandType: CommandType.StoredProcedure,
        cancellationToken: ct));
    }
    catch (Microsoft.Data.SqlClient.SqlException exception) when (exception.Number is 53650 or 53652)
    {
      // A webhook, recovery pass, or duplicate browser request won the state
      // transition. Return that durable outcome instead of surfacing a 500 or
      // overwriting a captured-payment state with stale browser data.
      await using var connection = await OpenAsync(binding, ct);
      return await GetAttemptAsync(connection, id: id, ct: ct)
        ?? throw new InvalidOperationException("The restaurant checkout disappeared after a concurrent state transition.", exception);
    }
  }

  private static Task<CheckoutAttemptRow?> GetAttemptAsync(
    System.Data.Common.DbConnection connection,
    Guid? id = null,
    Guid? clientAttemptId = null,
    byte[]? trackingTokenHash = null,
    string? providerOrderId = null,
    string? providerCaptureId = null,
    string? merchantProfileKey = null,
    CancellationToken ct = default)
    => connection.QuerySingleOrDefaultAsync<CheckoutAttemptRow>(new CommandDefinition(
      "restaurante.OnlineCheckoutAttemptGet",
      new
      {
        Id = id,
        ClientAttemptId = clientAttemptId,
        TrackingTokenHash = trackingTokenHash,
        ProviderOrderId = providerOrderId,
        ProviderCaptureId = providerCaptureId,
        MerchantProfileKey = merchantProfileKey
      },
      commandType: CommandType.StoredProcedure,
      cancellationToken: ct));

  private Task<System.Data.Common.DbConnection> OpenAsync(PublicSiteBinding binding, CancellationToken ct)
    => _sessions.OpenAsync(PlatformExecutionScope.FromPublicSite(binding), ct);

  private void LogProviderFailure(Guid attemptId, ClipClientException exception)
    => _logger.LogWarning(
      exception,
      "Clip operation {Operation} failed for checkout {CheckoutAttemptId}: provider code {ProviderCode}, HTTP {StatusCode}, outcome unknown {OutcomeUnknown}.",
      exception.Operation,
      attemptId,
      exception.ProviderErrorCode,
      exception.StatusCode,
      exception.IsOutcomeUnknown);

  private static void EnsureRestaurantBinding(PublicSiteBinding binding)
  {
    ArgumentNullException.ThrowIfNull(binding);
    if (!string.Equals(binding.ModuleCode, PlatformModuleCodes.Restaurant, StringComparison.Ordinal)
        || binding.PublicSiteId <= 0
        || string.IsNullOrWhiteSpace(binding.PublicSiteKey))
      throw new UnauthorizedAccessException("El sitio público no pertenece al módulo Restaurant.");
  }

  private static string NormalizeCurrency(string? value)
    => string.IsNullOrWhiteSpace(value) ? "MXN" : value.Trim().ToUpperInvariant();
  private static string NormalizeLocale(string? value)
    => string.IsNullOrWhiteSpace(value) ? "es" : value.Trim().ToLowerInvariant();
  private static string? NullIfWhiteSpace(string? value)
    => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

  private static bool FixedTimeEquals(string expected, string supplied)
  {
    var left = Encoding.UTF8.GetBytes(expected);
    var right = Encoding.UTF8.GetBytes(supplied);
    return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
  }

  private static string ReadString(JsonElement parent, string propertyName)
    => parent.ValueKind == JsonValueKind.Object
      && parent.TryGetProperty(propertyName, out var value)
      && value.ValueKind == JsonValueKind.String
      ? value.GetString()?.Trim() ?? string.Empty
      : string.Empty;

  private sealed class BootstrapRow
  {
    public long PublicSiteId { get; set; }
    public string PublicSiteKey { get; set; } = string.Empty;
    public string CanonicalHost { get; set; } = string.Empty;
    public string Rfc { get; set; } = string.Empty;
    public int SiteId { get; set; }
    public long ConfigurationVersion { get; set; }
    public bool IsEnabled { get; set; }
    public bool IsPaused { get; set; }
    public string? PauseMessage { get; set; }
    public string WeeklyScheduleJson { get; set; } = "{}";
    public decimal MaximumOrderTotal { get; set; }
    public bool GuestCheckoutEnabled { get; set; }
    public bool PickupEnabled { get; set; }
    public DateTime? ProcessorHeartbeatAtUtc { get; set; }
    public string TermsVersion { get; set; } = string.Empty;
    public string PrivacyVersion { get; set; } = string.Empty;
    public string ActiveMerchantProfileKey { get; set; } = string.Empty;
    public string GatewayEnvironment { get; set; } = "Unknown";
    public bool GatewayCredentialsConfigured { get; set; }
    public bool GatewayWebhookConfigured { get; set; }
    public DateTime? GatewayReadinessAtUtc { get; set; }
    public HashSet<long> EnabledProductIds { get; set; } = [];
  }

  private sealed class CheckoutAttemptRow
  {
    public bool WasCreated { get; set; }
    public Guid Id { get; set; }
    public long PublicSiteId { get; set; }
    public string Rfc { get; set; } = string.Empty;
    public int SiteId { get; set; }
    public Guid ClientAttemptId { get; set; }
    public long SettingsConfigurationVersion { get; set; }
    public Guid? MemberId { get; set; }
    public string CustomerName { get; set; } = string.Empty;
    public string CustomerEmail { get; set; } = string.Empty;
    public string CustomerPhone { get; set; } = string.Empty;
    public string QuoteFingerprint { get; set; } = string.Empty;
    public byte[] TrackingTokenHash { get; set; } = [];
    public string MerchantProfileKey { get; set; } = string.Empty;
    public decimal Total { get; set; }
    public string CurrencyCode { get; set; } = "MXN";
    public string Provider { get; set; } = string.Empty;
    public string? ProviderRequestId { get; set; }
    public string? ProviderOrderId { get; set; }
    public string? ProviderCaptureId { get; set; }
    public Guid? ChargeRequestId { get; set; }
    public DateTime? ChargeStartedAtUtc { get; set; }
    public string State { get; set; } = string.Empty;
    public Guid? RestaurantOrderId { get; set; }
    public int? OrderFolio { get; set; }
  }

  private sealed class ChargeBeginRow
  {
    public Guid Id { get; set; }
    public Guid? ChargeRequestId { get; set; }
    public string State { get; set; } = string.Empty;
  }

  private sealed record ChargeBeginOutcome(bool Blocked, bool IsStateConflict);

  private sealed class GatewayTransactionRow
  {
    public Guid Id { get; set; }
    public string Status { get; set; } = string.Empty;
  }

  private sealed class WebhookRecordResult
  {
    public bool WasMatched { get; set; }
    public bool WasInserted { get; set; }
    public long? EventId { get; set; }
  }
}
