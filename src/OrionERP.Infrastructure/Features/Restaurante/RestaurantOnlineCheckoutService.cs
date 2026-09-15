using System.Data;
using System.Globalization;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrionERP.Application.Features.Payments.PayPal;
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
  private readonly IPayPalOrdersClient _payPal;
  private readonly RestaurantCheckoutOptions _options;
  private readonly ILogger<RestaurantOnlineCheckoutService> _logger;
  private readonly TimeProvider _clock;

  public RestaurantOnlineCheckoutService(
    IOrionSqlSessionFactory sessions,
    IRestaurantPublicCatalogService catalogs,
    IRestaurantPromotionService promotions,
    IOnlineRestaurantQuoteTokenService quoteTokens,
    IPayPalOrdersClient payPal,
    IOptions<RestaurantCheckoutOptions> options,
    ILogger<RestaurantOnlineCheckoutService> logger,
    TimeProvider? clock = null)
  {
    _sessions = sessions;
    _catalogs = catalogs;
    _promotions = promotions;
    _quoteTokens = quoteTokens;
    _payPal = payPal;
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
        PayPalLocale = _options.PayPalLocale,
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
      PayPalClientId = _options.IsPayPalConfigured ? _options.PayPalClientId.Trim() : string.Empty,
      PayPalLocale = string.IsNullOrWhiteSpace(_options.PayPalLocale) ? "es_MX" : _options.PayPalLocale.Trim(),
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

  public async Task<RestaurantOnlinePayPalOrderResult> CreatePayPalOrderAsync(
    PublicSiteBinding binding,
    RestaurantOnlinePayPalOrderCreateRequest request,
    Guid? memberId,
    CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(request);
    EnsureRestaurantBinding(binding);
    var inputError = ValidateCreateRequest(request);
    if (inputError is not null)
      return RestaurantOnlinePayPalOrderResult.Fail(inputError.Value.Code, inputError.Value.Message);

    var now = _clock.GetUtcNow();
    if (!TryReadQuote(binding, request.QuoteToken, memberId, now, out var suppliedQuote, out var quoteFailure))
      return RestaurantOnlinePayPalOrderResult.Fail(quoteFailure.Code, quoteFailure.Message);
    if (!string.Equals(request.TermsVersion, _options.TermsVersion, StringComparison.Ordinal)
        || !string.Equals(request.PrivacyVersion, _options.PrivacyVersion, StringComparison.Ordinal))
      return RestaurantOnlinePayPalOrderResult.Fail("legal_version_changed", "Revisa y acepta la versión vigente de términos y privacidad.");

    var current = await QuoteAsync(binding, suppliedQuote!.Request, memberId, ct);
    if (!current.Succeeded || string.IsNullOrWhiteSpace(current.QuoteToken)
        || !_quoteTokens.TryUnprotect(current.QuoteToken, out var currentQuote)
        || currentQuote is null)
      return RestaurantOnlinePayPalOrderResult.Fail(current.Code, current.Message);
    if (!RestaurantOnlineQuotePolicy.IsSameQuote(suppliedQuote, currentQuote))
      return RestaurantOnlinePayPalOrderResult.Fail("requote_required", "El menú o el total cambió. Revisa el pedido antes de pagar.");

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
            TrackingTokenHash = trackingHash
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
        return RestaurantOnlinePayPalOrderResult.Fail(
          "ordering_disabled",
          "Los pedidos en línea todavía no están configurados.");
      var refreshedAvailability = EvaluateAvailability(binding, refreshedBootstrap);
      if (refreshedAvailability.Code is not null)
        return RestaurantOnlinePayPalOrderResult.Fail(
          refreshedAvailability.Code,
          refreshedAvailability.Message!);
      return RestaurantOnlinePayPalOrderResult.Fail(
        "requote_required",
        "El menú o la configuración cambió. Revisa nuevamente tu pedido antes de pagar.");
    }

    if (!CryptographicOperations.FixedTimeEquals(attempt.TrackingTokenHash, trackingHash)
        || !string.Equals(attempt.QuoteFingerprint, currentQuote.Fingerprint, StringComparison.Ordinal)
        || attempt.MemberId != memberId)
      return RestaurantOnlinePayPalOrderResult.Fail("attempt_conflict", "Este intento de pago ya pertenece a otro pedido.");

    if (!string.IsNullOrWhiteSpace(attempt.PayPalOrderId))
    {
      return new RestaurantOnlinePayPalOrderResult
      {
        Succeeded = true,
        Code = "paypal_order_exists",
        Message = "El pago ya estaba preparado.",
        PayPalOrderId = attempt.PayPalOrderId,
        TrackingToken = trackingToken,
        CheckoutStatus = attempt.State,
        WasExisting = true
      };
    }

    var createKey = RestaurantPayPalMetadataPolicy.RequestId(binding.PublicSiteKey, "create", attempt.Id);
    try
    {
      var order = await _payPal.CreateOrderAsync(new PayPalCreateOrderRequest
      {
        ExperienceContext = new PayPalExperienceContext
        {
          Locale = string.IsNullOrWhiteSpace(_options.PayPalLocale)
            ? "es-MX"
            : _options.PayPalLocale.Trim().Replace('_', '-'),
          ShippingPreference = "NO_SHIPPING",
          UserAction = "PAY_NOW"
        },
        PurchaseUnits =
        [
          new PayPalPurchaseUnitRequest
          {
            ReferenceId = RestaurantPayPalMetadataPolicy.ReferenceId(binding.PublicSiteKey, attempt.Id),
            Description = "Pedido para recoger",
            CustomId = RestaurantPayPalMetadataPolicy.CustomId(binding.PublicSiteKey, currentQuote.Fingerprint),
            InvoiceId = RestaurantPayPalMetadataPolicy.InvoiceId(binding.PublicSiteKey, attempt.Id),
            Amount = new PayPalMoney { Value = currentQuote.Total, Currency = currentQuote.Currency }
          }
        ]
      }, createKey, ct);
      if (string.IsNullOrWhiteSpace(order.OrderId))
        return RestaurantOnlinePayPalOrderResult.Fail("paypal_create_failed", "PayPal no devolvió un identificador de pago.");
      ValidateProviderOrder(order, binding, attempt, currentQuote);

      await using var connection = await OpenAsync(binding, ct);
      attempt = await connection.QuerySingleAsync<CheckoutAttemptRow>(new CommandDefinition(
        "restaurante.OnlineCheckoutPayPalOrderRecord",
        new { attempt.Id, PayPalCreateRequestId = createKey, PayPalOrderId = order.OrderId },
        commandType: CommandType.StoredProcedure,
        cancellationToken: ct));
      return new RestaurantOnlinePayPalOrderResult
      {
        Succeeded = true,
        Code = "paypal_order_created",
        Message = "El pago está listo para autorizarse con PayPal.",
        PayPalOrderId = attempt.PayPalOrderId,
        TrackingToken = trackingToken,
        CheckoutStatus = attempt.State,
        WasExisting = false
      };
    }
    catch (PayPalClientException exception)
    {
      LogProviderFailure(attempt.Id, exception);
      return RestaurantOnlinePayPalOrderResult.Fail(
        exception.ProviderErrorCode == "NOT_CONFIGURED" ? "paypal_not_configured" : "paypal_create_failed",
        "No fue posible preparar el pago con PayPal. Intenta nuevamente.");
    }
    catch (InvalidOperationException exception)
    {
      _logger.LogWarning("PayPal create response did not match restaurant checkout {CheckoutAttemptId}: {Reason}", attempt.Id, exception.Message);
      return RestaurantOnlinePayPalOrderResult.Fail("paypal_order_validation_failed", "PayPal devolvió datos que no corresponden a este pedido.");
    }
  }

  public async Task<RestaurantOnlinePayPalCaptureResult> CapturePayPalOrderAsync(
    PublicSiteBinding binding,
    string payPalOrderId,
    RestaurantOnlinePayPalCaptureRequest request,
    Guid? memberId,
    CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(request);
    EnsureRestaurantBinding(binding);
    if (string.IsNullOrWhiteSpace(payPalOrderId) || request.ClientAttemptId == Guid.Empty
        || string.IsNullOrWhiteSpace(request.TrackingToken))
      return RestaurantOnlinePayPalCaptureResult.Fail("invalid_request", "No fue posible identificar el pago.");

    var expectedTrackingToken = RestaurantOnlineTrackingTokenPolicy.Create(binding.PublicSiteId, request.ClientAttemptId);
    if (!FixedTimeEquals(expectedTrackingToken, request.TrackingToken.Trim()))
      return RestaurantOnlinePayPalCaptureResult.Fail("not_found", "No encontramos este pedido.");

    CheckoutAttemptRow? attempt;
    await using (var connection = await OpenAsync(binding, ct))
    {
      attempt = await GetAttemptAsync(connection, clientAttemptId: request.ClientAttemptId, ct: ct);
    }
    if (attempt is null || !string.Equals(attempt.PayPalOrderId, payPalOrderId.Trim(), StringComparison.Ordinal)
        || attempt.MemberId != memberId)
      return RestaurantOnlinePayPalCaptureResult.Fail("not_found", "No encontramos este pedido.");

    var terminal = ExistingCaptureResult(attempt, expectedTrackingToken);
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
        var concurrentResult = ExistingCaptureResult(attempt, expectedTrackingToken);
        if (concurrentResult is not null)
          return concurrentResult;
      }
      return new RestaurantOnlinePayPalCaptureResult
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
      return RestaurantOnlinePayPalCaptureResult.Fail("attempt_conflict", "La cotización no corresponde a este pago.");

    var bootstrap = await LoadBootstrapAsync(binding, ct);
    if (bootstrap is null)
      return RestaurantOnlinePayPalCaptureResult.Fail("ordering_disabled", "Los pedidos en línea están deshabilitados.");
    var availability = EvaluateAvailability(binding, bootstrap);
    if (availability.Code is not null)
      return RestaurantOnlinePayPalCaptureResult.Fail(availability.Code, availability.Message!);

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
        var concurrentResult = ExistingCaptureResult(attempt, expectedTrackingToken);
        if (concurrentResult is not null)
          return concurrentResult;
      }
      return RestaurantOnlinePayPalCaptureResult.Fail("requote_required", "El menú o el total cambió. No se hizo ningún cobro.");
    }

    var captureWasAuthorized = false;
    try
    {
      var providerOrder = await _payPal.GetOrderAsync(attempt.PayPalOrderId!, ct);
      ValidateProviderOrder(providerOrder, binding, attempt, currentQuote);
      PayPalCaptureResult capture;
      var existingCapture = providerOrder.Captures.FirstOrDefault(item =>
        string.Equals(item.ReferenceId, RestaurantPayPalMetadataPolicy.ReferenceId(binding.PublicSiteKey, attempt.Id), StringComparison.Ordinal));
      if (existingCapture is not null)
      {
        capture = existingCapture;
      }
      else
      {
        if (!string.Equals(providerOrder.Status, "APPROVED", StringComparison.OrdinalIgnoreCase))
          return RestaurantOnlinePayPalCaptureResult.Fail("paypal_order_validation_failed", "Autoriza primero el pago en PayPal.");
        if (!await AuthorizeCaptureAsync(binding, attempt.Id, attempt.PayPalOrderId!, ct))
          return RestaurantOnlinePayPalCaptureResult.Fail("ordering_paused", "Los pedidos se pausaron antes del cobro. No se hizo ningún cargo.");
        captureWasAuthorized = true;
        try
        {
          capture = await _payPal.CaptureOrderAsync(
            attempt.PayPalOrderId!,
            RestaurantPayPalMetadataPolicy.RequestId(binding.PublicSiteKey, "capture", attempt.Id),
            ct);
          capture = CompleteCaptureMetadata(capture, providerOrder);
        }
        catch (PayPalClientException exception) when (exception.IsAlreadyCaptured)
        {
          providerOrder = await _payPal.GetOrderAsync(attempt.PayPalOrderId!, ct);
          ValidateProviderOrder(providerOrder, binding, attempt, currentQuote);
          capture = providerOrder.Captures.SingleOrDefault()
            ?? throw new InvalidOperationException("PayPal reported an existing capture but returned no capture identifier.");
        }
      }

      ValidateCapture(capture, binding, attempt, currentQuote);
      var gatewayStatus = capture.IsCompleted
        ? "Completed"
        : string.Equals(capture.Status, "PENDING", StringComparison.OrdinalIgnoreCase)
          ? "Pending"
          : "Denied";
      await using var connection = await OpenAsync(binding, ct);
      using var multi = await connection.QueryMultipleAsync(new CommandDefinition(
        "restaurante.OnlineCheckoutCaptureRecord",
        new
        {
          attempt.Id,
          PayPalOrderId = attempt.PayPalOrderId,
          PayPalCaptureId = capture.CaptureId,
          Status = gatewayStatus,
          GrossAmount = capture.Amount.Value,
          FeeAmount = capture.SellerReceivableBreakdown?.PayPalFee.Value,
          NetAmount = capture.SellerReceivableBreakdown?.NetAmount.Value,
          CurrencyCode = capture.Amount.Currency,
          IdempotencyKey = RestaurantPayPalMetadataPolicy.RequestId(binding.PublicSiteKey, "capture", attempt.Id),
          CapturedAtUtc = capture.IsCompleted ? now.UtcDateTime : (DateTime?)null
        },
        commandType: CommandType.StoredProcedure,
        cancellationToken: ct));
      attempt = await multi.ReadSingleAsync<CheckoutAttemptRow>();
      _ = await multi.ReadSingleOrDefaultAsync<GatewayTransactionRow>();

      if (!capture.IsCompleted)
      {
        return new RestaurantOnlinePayPalCaptureResult
        {
          Succeeded = true,
          Code = gatewayStatus == "Pending" ? "capture_pending" : "payment_denied",
          Message = gatewayStatus == "Pending"
            ? "PayPal está confirmando el pago. No vuelvas a pagar; actualizaremos este pedido."
            : "PayPal no aprobó el pago.",
          CheckoutStatus = attempt.State,
          TrackingToken = expectedTrackingToken,
          PaymentCaptured = false,
          IsPending = gatewayStatus == "Pending"
        };
      }

      return new RestaurantOnlinePayPalCaptureResult
      {
        Succeeded = true,
        Code = "payment_received",
        Message = "Pago recibido. Estamos confirmando tu pedido.",
        CheckoutStatus = attempt.State,
        TrackingToken = expectedTrackingToken,
        OrderFolio = attempt.OrderFolio,
        PaymentCaptured = true,
        IsPending = attempt.State != RestaurantOnlineCheckoutStatuses.PosCreated
      };
    }
    catch (PayPalClientException exception)
    {
      LogProviderFailure(attempt.Id, exception);
      if (captureWasAuthorized)
      {
        if (IsExplicitCaptureDenial(exception))
        {
          attempt = await SetStateAsync(
            binding,
            attempt.Id,
            RestaurantOnlineCheckoutStatuses.CapturePending,
            RestaurantOnlineCheckoutStatuses.PaymentDenied,
            exception.ProviderErrorCode,
            "PayPal rechazó el cobro.",
            ct);
          if (!string.Equals(attempt.State, RestaurantOnlineCheckoutStatuses.PaymentDenied, StringComparison.Ordinal))
          {
            var concurrentResult = ExistingCaptureResult(attempt, expectedTrackingToken);
            if (concurrentResult is not null)
              return concurrentResult;
          }
          return new RestaurantOnlinePayPalCaptureResult
          {
            Succeeded = true,
            Code = "payment_denied",
            Message = "PayPal no aprobó el pago.",
            CheckoutStatus = RestaurantOnlineCheckoutStatuses.PaymentDenied,
            TrackingToken = expectedTrackingToken,
            PaymentCaptured = false,
            IsPending = false
          };
        }

        return new RestaurantOnlinePayPalCaptureResult
        {
          Succeeded = true,
          Code = "capture_pending",
          Message = "PayPal está confirmando el pago. No vuelvas a pagar; actualizaremos este pedido.",
          CheckoutStatus = RestaurantOnlineCheckoutStatuses.CapturePending,
          TrackingToken = expectedTrackingToken,
          PaymentCaptured = false,
          IsPending = true
        };
      }
      return RestaurantOnlinePayPalCaptureResult.Fail("paypal_capture_failed", "No pudimos confirmar el pago con PayPal. Consulta el estado del pedido antes de intentar nuevamente.");
    }
    catch (InvalidOperationException exception)
    {
      _logger.LogWarning("PayPal capture did not match restaurant checkout {CheckoutAttemptId}: {Reason}", attempt.Id, exception.Message);
      return RestaurantOnlinePayPalCaptureResult.Fail("paypal_order_validation_failed", "PayPal devolvió datos que no corresponden a este pedido.");
    }
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

  public async Task<RestaurantPayPalWebhookResult> ProcessPayPalWebhookAsync(
    PublicSiteBinding binding,
    RestaurantPayPalWebhookRequest request,
    CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(request);
    EnsureRestaurantBinding(binding);
    PayPalWebhookVerificationResult verification;
    try
    {
      verification = await _payPal.VerifyWebhookSignatureAsync(new PayPalWebhookSignature
      {
        TransmissionId = request.TransmissionId,
        TransmissionTime = request.TransmissionTime,
        TransmissionSignature = request.TransmissionSignature,
        CertificateUrl = request.CertificateUrl,
        AuthenticationAlgorithm = request.AuthenticationAlgorithm
      }, request.RawBody, ct);
    }
    catch (PayPalClientException exception)
    {
      _logger.LogWarning(
        "PayPal webhook verification transport failed with provider code {ProviderCode}, HTTP {StatusCode}, debug id {DebugId}.",
        exception.ProviderErrorCode,
        exception.StatusCode,
        exception.DebugId ?? "<none>");
      return new RestaurantPayPalWebhookResult(false, false);
    }
    catch (ArgumentException)
    {
      // Signature headers are untrusted request data. A missing or malformed
      // value is a bad webhook request, not an application failure.
      return new RestaurantPayPalWebhookResult(false, false);
    }
    if (!verification.IsVerified)
      return new RestaurantPayPalWebhookResult(false, false);

    WebhookEnvelope envelope;
    try
    {
      envelope = ParseWebhook(request.RawBody);
    }
    catch (Exception exception) when (exception is JsonException or InvalidOperationException)
    {
      return new RestaurantPayPalWebhookResult(false, false);
    }
    if (string.IsNullOrWhiteSpace(envelope.EventId) || string.IsNullOrWhiteSpace(envelope.EventType))
      return new RestaurantPayPalWebhookResult(false, false);

    await using var connection = await OpenAsync(binding, ct);
    var result = await connection.QuerySingleAsync<WebhookRecordResult>(new CommandDefinition(
      "restaurante.PaymentGatewayEventRecord",
      new
      {
        MerchantProfileKey = _options.MerchantProfileKey.Trim(),
        ProviderEventId = envelope.EventId,
        envelope.EventType,
        envelope.ResourceType,
        envelope.ResourceId,
        envelope.RelatedOrderId,
        envelope.RelatedCaptureId,
        envelope.RelatedRefundId,
        PayloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.RawBody))),
        VerificationStatus = "Verified"
      },
      commandType: CommandType.StoredProcedure,
      cancellationToken: ct));
    return new RestaurantPayPalWebhookResult(true, result.WasMatched, envelope.EventId);
  }

  private async Task<BootstrapRow?> LoadBootstrapAsync(PublicSiteBinding binding, CancellationToken ct)
  {
    EnsureRestaurantBinding(binding);
    await using var connection = await OpenAsync(binding, ct);
    await connection.ExecuteAsync(new CommandDefinition(
      "restaurante.OnlineOrderingRuntimeReadinessSet",
      new
      {
        GatewayEnvironment = _options.UseLivePayPal ? "Live" : "Sandbox",
        GatewayCredentialsConfigured = _options.IsPayPalConfigured,
        GatewayWebhookConfigured = _options.IsWebhookVerificationConfigured,
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
    if (!_options.IsPayPalConfigured)
      return ("paypal_not_configured", "El pago en línea todavía no está configurado.");
    if (!_options.IsWebhookVerificationConfigured)
      return ("paypal_not_configured", "La confirmación segura de PayPal todavía no está configurada.");
    if (string.IsNullOrWhiteSpace(_options.MerchantProfileKey))
      return ("paypal_not_configured", "El perfil de cobro no está configurado.");
    if (!row.GatewayCredentialsConfigured || !row.GatewayWebhookConfigured
        || !string.Equals(row.ActiveMerchantProfileKey, _options.MerchantProfileKey, StringComparison.Ordinal)
        || !string.Equals(row.GatewayEnvironment, _options.UseLivePayPal ? "Live" : "Sandbox", StringComparison.OrdinalIgnoreCase)
        || !row.GatewayReadinessAtUtc.HasValue
        || _clock.GetUtcNow() - new DateTimeOffset(DateTime.SpecifyKind(row.GatewayReadinessAtUtc.Value, DateTimeKind.Utc))
          > TimeSpan.FromMinutes(5))
      return ("paypal_not_configured", "La configuración segura de PayPal no está lista.");
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

  private static (string Code, string Message)? ValidateCreateRequest(RestaurantOnlinePayPalOrderCreateRequest request)
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

  private static RestaurantOnlinePayPalCaptureResult? ExistingCaptureResult(CheckoutAttemptRow attempt, string trackingToken)
  {
    if (attempt.State == RestaurantOnlineCheckoutStatuses.PosCreated)
    {
      return new RestaurantOnlinePayPalCaptureResult
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
      return new RestaurantOnlinePayPalCaptureResult
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
    if (attempt.State == RestaurantOnlineCheckoutStatuses.CapturePending)
    {
      return new RestaurantOnlinePayPalCaptureResult
      {
        Succeeded = true,
        Code = "capture_pending",
        Message = "PayPal está confirmando el pago. No vuelvas a pagar; actualizaremos este pedido.",
        CheckoutStatus = attempt.State,
        TrackingToken = trackingToken,
        PaymentCaptured = false,
        IsPending = true
      };
    }
    if (attempt.State == RestaurantOnlineCheckoutStatuses.PaymentDenied)
    {
      return new RestaurantOnlinePayPalCaptureResult
      {
        Succeeded = true,
        Code = "payment_denied",
        Message = "PayPal no aprobó el pago.",
        CheckoutStatus = attempt.State,
        TrackingToken = trackingToken,
        PaymentCaptured = false,
        IsPending = false
      };
    }
    if (attempt.State is RestaurantOnlineCheckoutStatuses.RequoteRequired
        or RestaurantOnlineCheckoutStatuses.Expired
        or RestaurantOnlineCheckoutStatuses.Failed)
    {
      return new RestaurantOnlinePayPalCaptureResult
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
    return null;
  }

  private static void ValidateProviderOrder(
    PayPalOrderResult order,
    PublicSiteBinding binding,
    CheckoutAttemptRow attempt,
    RestaurantOnlineQuoteSnapshot quote)
  {
    if (!string.IsNullOrWhiteSpace(attempt.PayPalOrderId)
        && !string.Equals(order.OrderId, attempt.PayPalOrderId, StringComparison.Ordinal))
      throw new InvalidOperationException("PayPal order id mismatch.");
    var unit = order.PurchaseUnits.SingleOrDefault()
      ?? throw new InvalidOperationException("PayPal purchase unit missing.");
    if (!string.Equals(unit.ReferenceId, RestaurantPayPalMetadataPolicy.ReferenceId(binding.PublicSiteKey, attempt.Id), StringComparison.Ordinal)
        || !string.Equals(unit.CustomId, RestaurantPayPalMetadataPolicy.CustomId(binding.PublicSiteKey, quote.Fingerprint), StringComparison.Ordinal)
        || !string.Equals(unit.InvoiceId, RestaurantPayPalMetadataPolicy.InvoiceId(binding.PublicSiteKey, attempt.Id), StringComparison.Ordinal)
        || unit.Amount.Value != quote.Total
        || !string.Equals(unit.Amount.Currency, quote.Currency, StringComparison.OrdinalIgnoreCase))
      throw new InvalidOperationException("PayPal purchase unit metadata mismatch.");
  }

  private static void ValidateCapture(
    PayPalCaptureResult capture,
    PublicSiteBinding binding,
    CheckoutAttemptRow attempt,
    RestaurantOnlineQuoteSnapshot quote)
  {
    if (string.IsNullOrWhiteSpace(capture.CaptureId)
        || !string.Equals(capture.OrderId, attempt.PayPalOrderId, StringComparison.Ordinal)
        || !string.Equals(capture.ReferenceId, RestaurantPayPalMetadataPolicy.ReferenceId(binding.PublicSiteKey, attempt.Id), StringComparison.Ordinal)
        || !string.Equals(capture.CustomId, RestaurantPayPalMetadataPolicy.CustomId(binding.PublicSiteKey, quote.Fingerprint), StringComparison.Ordinal)
        || !string.Equals(capture.InvoiceId, RestaurantPayPalMetadataPolicy.InvoiceId(binding.PublicSiteKey, attempt.Id), StringComparison.Ordinal)
        || capture.Amount.Value != quote.Total
        || !string.Equals(capture.Amount.Currency, quote.Currency, StringComparison.OrdinalIgnoreCase))
      throw new InvalidOperationException("PayPal capture metadata mismatch.");
  }

  /// <summary>
  /// PayPal can omit purchase-unit metadata from the immediate capture response.
  /// The order was retrieved and validated immediately before capture, so only
  /// absent linkage fields are completed from that verified response. Conflicting
  /// provider values are retained and rejected by <see cref="ValidateCapture"/>.
  /// </summary>
  private static PayPalCaptureResult CompleteCaptureMetadata(
    PayPalCaptureResult capture,
    PayPalOrderResult verifiedOrder)
  {
    var unit = verifiedOrder.PurchaseUnits.SingleOrDefault()
      ?? throw new InvalidOperationException("PayPal purchase unit missing.");
    return new PayPalCaptureResult
    {
      OrderId = string.IsNullOrWhiteSpace(capture.OrderId) ? verifiedOrder.OrderId : capture.OrderId,
      OrderStatus = string.IsNullOrWhiteSpace(capture.OrderStatus) ? verifiedOrder.Status : capture.OrderStatus,
      CaptureId = capture.CaptureId,
      Status = capture.Status,
      StatusReason = capture.StatusReason,
      ReferenceId = string.IsNullOrWhiteSpace(capture.ReferenceId) ? unit.ReferenceId : capture.ReferenceId,
      CustomId = string.IsNullOrWhiteSpace(capture.CustomId) ? unit.CustomId : capture.CustomId,
      InvoiceId = string.IsNullOrWhiteSpace(capture.InvoiceId) ? unit.InvoiceId : capture.InvoiceId,
      Amount = capture.Amount,
      SellerReceivableBreakdown = capture.SellerReceivableBreakdown,
      Payer = capture.Payer
    };
  }

  private static bool IsExplicitCaptureDenial(PayPalClientException exception)
  {
    if (!string.Equals(exception.Operation, "capture_order", StringComparison.Ordinal))
      return false;
    return exception.ProviderIssueCodes
        .Append(exception.ProviderErrorCode)
        .Any(code => code is not null && code.ToUpperInvariant() is
          "INSTRUMENT_DECLINED" or "PAYER_CANNOT_PAY" or "TRANSACTION_REFUSED"
          or "CARD_DECLINED" or "MAX_NUMBER_OF_PAYMENT_ATTEMPTS_EXCEEDED");
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
    catch (Microsoft.Data.SqlClient.SqlException exception) when (exception.Number == 53652)
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
    string? payPalOrderId = null,
    string? payPalCaptureId = null,
    string? merchantProfileKey = null,
    CancellationToken ct = default)
    => connection.QuerySingleOrDefaultAsync<CheckoutAttemptRow>(new CommandDefinition(
      "restaurante.OnlineCheckoutAttemptGet",
      new
      {
        Id = id,
        ClientAttemptId = clientAttemptId,
        TrackingTokenHash = trackingTokenHash,
        PayPalOrderId = payPalOrderId,
        PayPalCaptureId = payPalCaptureId,
        MerchantProfileKey = merchantProfileKey
      },
      commandType: CommandType.StoredProcedure,
      cancellationToken: ct));

  private async Task<bool> AuthorizeCaptureAsync(
    PublicSiteBinding binding,
    Guid id,
    string payPalOrderId,
    CancellationToken ct)
  {
    try
    {
      await using var connection = await OpenAsync(binding, ct);
      var authorizedAttemptId = await connection.ExecuteScalarAsync<Guid>(new CommandDefinition(
        "restaurante.OnlineCheckoutCaptureAuthorize",
        new
        {
          Id = id,
          PayPalOrderId = payPalOrderId,
          _options.ProcessorHeartbeatMaxAgeSeconds
        },
        commandType: CommandType.StoredProcedure,
        cancellationToken: ct));
      return authorizedAttemptId == id;
    }
    catch (Exception exception) when (exception is InvalidOperationException or Microsoft.Data.SqlClient.SqlException)
    {
      _logger.LogWarning("Checkout capture authorization was rejected for {CheckoutAttemptId}.", id);
      return false;
    }
  }

  private Task<System.Data.Common.DbConnection> OpenAsync(PublicSiteBinding binding, CancellationToken ct)
    => _sessions.OpenAsync(PlatformExecutionScope.FromPublicSite(binding), ct);

  private void LogProviderFailure(Guid attemptId, PayPalClientException exception)
    => _logger.LogWarning(
      "PayPal operation {Operation} failed for checkout {CheckoutAttemptId}: provider code {ProviderCode}, HTTP {StatusCode}, debug id {DebugId}.",
      exception.Operation,
      attemptId,
      exception.ProviderErrorCode,
      exception.StatusCode,
      exception.DebugId ?? "<none>");

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
  private static string? NullIfWhiteSpace(string? value)
    => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

  private static bool FixedTimeEquals(string expected, string supplied)
  {
    var left = Encoding.UTF8.GetBytes(expected);
    var right = Encoding.UTF8.GetBytes(supplied);
    return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
  }

  private static WebhookEnvelope ParseWebhook(string rawBody)
  {
    using var document = JsonDocument.Parse(rawBody);
    var root = document.RootElement;
    var eventId = ReadString(root, "id");
    var eventType = ReadString(root, "event_type");
    var resourceType = ReadString(root, "resource_type");
    var resourceId = string.Empty;
    var resourceStatus = string.Empty;
    var relatedOrderId = string.Empty;
    var relatedCaptureId = string.Empty;
    var relatedRefundId = string.Empty;
    string? amount = null;
    string? currency = null;
    if (root.TryGetProperty("resource", out var resource) && resource.ValueKind == JsonValueKind.Object)
    {
      resourceId = ReadString(resource, "id");
      resourceStatus = ReadString(resource, "status");
      if (resource.TryGetProperty("amount", out var money) && money.ValueKind == JsonValueKind.Object)
      {
        amount = NullIfWhiteSpace(ReadString(money, "value"));
        currency = NullIfWhiteSpace(ReadString(money, "currency_code"));
      }
      if (resource.TryGetProperty("supplementary_data", out var supplementary)
          && supplementary.TryGetProperty("related_ids", out var related))
      {
        relatedOrderId = ReadString(related, "order_id");
        relatedCaptureId = ReadString(related, "capture_id");
        relatedRefundId = ReadString(related, "refund_id");
      }
      if (string.IsNullOrWhiteSpace(relatedCaptureId)
          && string.Equals(resourceType, "refund", StringComparison.OrdinalIgnoreCase)
          && resource.TryGetProperty("links", out var links)
          && links.ValueKind == JsonValueKind.Array)
      {
        relatedCaptureId = ReadCaptureIdFromUpLink(links);
      }
    }
    var sanitized = JsonSerializer.Serialize(new
    {
      id = eventId,
      event_type = eventType,
      resource_type = resourceType,
      resource = new
      {
        id = resourceId,
        status = resourceStatus,
        amount = amount is null ? null : new { value = amount, currency_code = currency },
        related_order_id = NullIfWhiteSpace(relatedOrderId)
      }
    }, JsonOptions);
    return new WebhookEnvelope(
      eventId,
      eventType,
      NullIfWhiteSpace(resourceType),
      NullIfWhiteSpace(resourceId),
      NullIfWhiteSpace(relatedOrderId),
      NullIfWhiteSpace(relatedCaptureId),
      NullIfWhiteSpace(relatedRefundId),
      sanitized);
  }

  // PAYMENT.CAPTURE.REFUNDED carries the refund itself and omits related_ids; its
  // capture is named only by the "up" link (.../v2/payments/captures/{capture_id}).
  private static string ReadCaptureIdFromUpLink(JsonElement links)
  {
    foreach (var link in links.EnumerateArray())
    {
      if (!string.Equals(ReadString(link, "rel"), "up", StringComparison.OrdinalIgnoreCase)
          || !Uri.TryCreate(ReadString(link, "href"), UriKind.Absolute, out var href))
        continue;
      var segments = href.AbsolutePath.TrimEnd('/').Split('/');
      if (segments.Length >= 2
          && string.Equals(segments[^2], "captures", StringComparison.OrdinalIgnoreCase)
          && segments[^1].Length is > 0 and <= 64)
        return segments[^1];
    }
    return string.Empty;
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
    public Guid Id { get; set; }
    public long PublicSiteId { get; set; }
    public string Rfc { get; set; } = string.Empty;
    public int SiteId { get; set; }
    public Guid ClientAttemptId { get; set; }
    public long SettingsConfigurationVersion { get; set; }
    public Guid? MemberId { get; set; }
    public string QuoteFingerprint { get; set; } = string.Empty;
    public byte[] TrackingTokenHash { get; set; } = [];
    public string MerchantProfileKey { get; set; } = string.Empty;
    public string? PayPalOrderId { get; set; }
    public string? PayPalCaptureId { get; set; }
    public string State { get; set; } = string.Empty;
    public Guid? RestaurantOrderId { get; set; }
    public int? OrderFolio { get; set; }
  }

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

  private sealed record WebhookEnvelope(
    string EventId,
    string EventType,
    string? ResourceType,
    string? ResourceId,
    string? RelatedOrderId,
    string? RelatedCaptureId,
    string? RelatedRefundId,
    string SanitizedJson);
}
