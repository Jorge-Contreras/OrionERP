using System.Text.Json;
using OrionERP.Bruno.Web;

namespace OrionERP.UnitTests.Restaurante;

public sealed class RestaurantOnlineOrderingUxTests
{
  [Theory]
  [InlineData("/menu")]
  [InlineData("/ordenar")]
  [InlineData("/checkout")]
  [InlineData("/pedido/valid-token")]
  [InlineData("/pedido/valid-token/")]
  public void Bruno_host_recognizes_online_ordering_routes(string path)
    => Assert.True(BrunoSiteConstants.IsPublicRoute(path));

  [Theory]
  [InlineData("/pedido")]
  [InlineData("/pedido/")]
  [InlineData("/checkout/extra")]
  [InlineData("/unknown")]
  public void Bruno_host_rejects_unknown_or_incomplete_routes(string path)
    => Assert.False(BrunoSiteConstants.IsPublicRoute(path));

  [Fact]
  public void Menu_has_explicit_online_eligibility_customization_notes_and_a_persisted_mobile_cart()
  {
    var page = Read("src/OrionERP.Bruno.Web/Features/BrunoMenuPage.razor");
    var styles = Read("src/OrionERP.Bruno.Web/Features/BrunoMenuPage.razor.css");
    var script = Read("src/OrionERP.Bruno.Web/wwwroot/js/brunos-ordering.js");

    Assert.Contains("@page \"/menu\"", page, StringComparison.Ordinal);
    Assert.Contains("@page \"/ordenar\"", page, StringComparison.Ordinal);
    Assert.Contains("product.CanOrderOnline", page, StringComparison.Ordinal);
    Assert.Contains("maxlength=\"500\"", page, StringComparison.Ordinal);
    Assert.Contains("ModifierOptionIds", page, StringComparison.Ordinal);
    Assert.Contains("ComboSelections", page, StringComparison.Ordinal);
    Assert.Contains("role=\"dialog\"", page, StringComparison.Ordinal);
    Assert.Contains("aria-live=\"polite\"", page, StringComparison.Ordinal);
    Assert.Contains(".cart-drawer", styles, StringComparison.Ordinal);
    Assert.Contains("@media (max-width: 640px)", styles, StringComparison.Ordinal);
    Assert.Contains("orion.restaurant.cart.v", script, StringComparison.Ordinal);
    Assert.Contains("cleanCart", script, StringComparison.Ordinal);
  }

  [Fact]
  public void Checkout_requotes_server_side_supports_guest_and_member_and_never_offers_redemption()
  {
    var page = Read("src/OrionERP.Bruno.Web/Features/Ordering/BrunoCheckoutPage.razor");
    var script = Read("src/OrionERP.Bruno.Web/wwwroot/js/brunos-ordering.js");

    Assert.Contains("checkout.QuoteAsync", Read("src/OrionERP.Bruno.Web/Features/Ordering/RestaurantCheckoutApi.cs"), StringComparison.Ordinal);
    Assert.Contains("brunoOrdering.quoteCart", page, StringComparison.Ordinal);
    Assert.Contains("GetMemberProfileByIdentityAsync", page, StringComparison.Ordinal);
    Assert.Contains("Puedes comprar como invitado", page, StringComparison.Ordinal);
    Assert.Contains("El canje de puntos sigue disponible solamente en el restaurante", page, StringComparison.Ordinal);
    Assert.DoesNotContain("PointsToRedeem", page, StringComparison.Ordinal);
    Assert.Contains("PayPal captura el pago de inmediato", page, StringComparison.Ordinal);
    Assert.Contains("no mostramos un tiempo estimado", page, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("X-CSRF-TOKEN", script, StringComparison.Ordinal);
    Assert.Contains("credentials: 'same-origin'", script, StringComparison.Ordinal);
  }

  [Fact]
  public void Checkout_reports_why_the_quote_failed_instead_of_blaming_the_cart()
  {
    var page = Read("src/OrionERP.Bruno.Web/Features/Ordering/BrunoCheckoutPage.razor");
    var script = Read("src/OrionERP.Bruno.Web/wwwroot/js/brunos-ordering.js");
    var quoteStart = script.IndexOf("async quoteCart(request)", StringComparison.Ordinal);
    var quoteEnd = script.IndexOf("async getStatus(", quoteStart, StringComparison.Ordinal);

    Assert.True(quoteStart >= 0 && quoteEnd > quoteStart, "The cart quote helper could not be isolated.");
    // Blazor Server replaces the text of a thrown JS error, so the endpoint's
    // reason only reaches the page when it comes back as data.
    var quoteCart = script[quoteStart..quoteEnd];
    Assert.Contains("succeeded: false", quoteCart, StringComparison.Ordinal);
    Assert.Contains("message: error.message", quoteCart, StringComparison.Ordinal);

    // A guest on a members-only site must be sent to registration, not told a price moved.
    Assert.Contains("configuration?.AllowGuestCheckout != true && !isMemberConnected", page, StringComparison.Ordinal);
    Assert.Contains("&& !MembershipRequired) await QuoteAsync();", page, StringComparison.Ordinal);
    Assert.Contains("else if (MembershipRequired)", page, StringComparison.Ordinal);
    Assert.Contains("/cuenta/registro?returnUrl=%2Fcheckout", page, StringComparison.Ordinal);
    Assert.Contains("Confirma el correo que te enviamos para activarla.", page, StringComparison.Ordinal);
  }

  [Fact]
  public void Payment_recovery_routes_every_created_attempt_to_status_before_another_payment()
  {
    var checkout = Read("src/OrionERP.Bruno.Web/Features/Ordering/BrunoCheckoutPage.razor");
    var tracking = Read("src/OrionERP.Bruno.Web/Features/Ordering/BrunoOrderStatusPage.razor");
    var script = Read("src/OrionERP.Bruno.Web/wwwroot/js/brunos-ordering.js");

    Assert.Contains("sessionStorage.setItem", script, StringComparison.Ordinal);
    Assert.Contains("trackingToken", script, StringComparison.Ordinal);
    Assert.Contains("/pedido/", checkout, StringComparison.Ordinal);
    Assert.Contains("No vuelvas a pagar", tracking, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("Task.Delay(TimeSpan.FromSeconds(7)", tracking, StringComparison.Ordinal);
    Assert.Contains("Te enviaremos un correo cuando esté listo", tracking, StringComparison.Ordinal);
  }

  [Fact]
  public void PayPal_cancel_discards_only_the_unapproved_attempt_and_unlocks_checkout_recovery()
  {
    var checkout = Read("src/OrionERP.Bruno.Web/Features/Ordering/BrunoCheckoutPage.razor");
    var script = Read("src/OrionERP.Bruno.Web/wwwroot/js/brunos-ordering.js");
    var cancelStart = script.IndexOf("async onCancel()", StringComparison.Ordinal);
    var errorStart = script.IndexOf("async onError(error)", cancelStart, StringComparison.Ordinal);

    Assert.True(cancelStart >= 0 && errorStart > cancelStart, "PayPal cancellation handler could not be isolated.");
    var cancel = script[cancelStart..errorStart];
    Assert.Contains("clearPendingCheckoutAttempt(options.clientAttemptId)", cancel, StringComparison.Ordinal);
    Assert.Contains("OnPaymentCancelled", cancel, StringComparison.Ordinal);
    Assert.Contains("options.clientAttemptId", cancel, StringComparison.Ordinal);
    Assert.Contains("String(current.clientAttemptId) === String(attemptId)", script, StringComparison.Ordinal);

    var callbackStart = checkout.IndexOf("public Task OnPaymentCancelled(string cancelledAttemptId)", StringComparison.Ordinal);
    var callbackEnd = checkout.IndexOf("[JSInvokable]", callbackStart + 1, StringComparison.Ordinal);
    Assert.True(callbackStart >= 0 && callbackEnd > callbackStart, "Checkout cancellation callback could not be isolated.");
    Assert.Contains("cancelledAttempt != clientAttemptId", checkout[callbackStart..callbackEnd], StringComparison.Ordinal);
    Assert.Contains("recoveryTrackingToken = null", checkout[callbackStart..callbackEnd], StringComparison.Ordinal);
  }

  [Fact]
  public void Bruno_keeps_an_independent_blank_secret_section_and_safe_defaults()
  {
    using var document = JsonDocument.Parse(Read("src/OrionERP.Bruno.Web/appsettings.json"));
    var checkout = document.RootElement.GetProperty("RestaurantCheckout");

    Assert.Equal("Sandbox", checkout.GetProperty("Environment").GetString());
    Assert.Equal("MXN", checkout.GetProperty("Currency").GetString());
    Assert.Equal("es_MX", checkout.GetProperty("PayPalLocale").GetString());
    Assert.Equal("shared-paypal-v1", checkout.GetProperty("MerchantProfileKey").GetString());
    Assert.Equal(string.Empty, checkout.GetProperty("PayPalClientId").GetString());
    Assert.Equal(string.Empty, checkout.GetProperty("PayPalClientSecret").GetString());
    Assert.Equal(string.Empty, checkout.GetProperty("PayPalWebhookId").GetString());
    Assert.Equal(10, checkout.GetProperty("QuoteTokenLifetimeMinutes").GetInt32());
  }

  [Fact]
  public void Checkout_api_separates_browser_antiforgery_from_verified_webhook()
  {
    var api = Read("src/OrionERP.Bruno.Web/Features/Ordering/RestaurantCheckoutApi.cs");
    var program = Read("src/OrionERP.Bruno.Web/Program.cs");

    Assert.Contains("antiforgery.ValidateRequestAsync(context)", api, StringComparison.Ordinal);
    Assert.Contains("DisableAntiforgery()", api, StringComparison.Ordinal);
    Assert.Contains("PAYPAL-TRANSMISSION-SIG", api, StringComparison.Ordinal);
    Assert.Contains("RequireRateLimiting(\"checkout\")", api, StringComparison.Ordinal);
    Assert.Contains("RequireRateLimiting(\"webhook\")", api, StringComparison.Ordinal);
    Assert.Contains("PayPalOrdersClient<RestaurantCheckoutOptions>", program, StringComparison.Ordinal);
    Assert.Contains("https://*.paypal.com", program, StringComparison.Ordinal);
  }

  [Fact]
  public void Confirmation_and_ready_email_worker_uses_the_scoped_durable_queue()
  {
    var notifications = Read("src/OrionERP.Bruno.Web/Services/OnlineRestaurantOrderNotifications.cs");
    var migration = Read("src/OrionERP.Infrastructure/Features/Restaurante/Sql/20260914_restaurant_online_ordering_runtime_corrections.sql");
    var program = Read("src/OrionERP.Bruno.Web/Program.cs");

    Assert.Contains("IOnlineRestaurantOrderEmailSender", notifications, StringComparison.Ordinal);
    Assert.Contains("RestaurantOnlineTrackingTokenPolicy.Create", notifications, StringComparison.Ordinal);
    Assert.Contains("OnlineOrderNotificationClaim", notifications, StringComparison.Ordinal);
    Assert.Contains("OnlineOrderNotificationComplete", notifications, StringComparison.Ordinal);
    Assert.Contains("OnlineOrderNotificationFail", notifications, StringComparison.Ordinal);
    Assert.Contains("exception.GetType().Name", notifications, StringComparison.Ordinal);
    Assert.DoesNotContain("FailureMessage = exception.Message", notifications, StringComparison.Ordinal);
    Assert.Contains("Te mandaremos otro correo cuando esté listo", notifications, StringComparison.Ordinal);
    Assert.Contains("AddHostedService<OnlineRestaurantOrderNotificationWorker>", program, StringComparison.Ordinal);
    Assert.Contains("AddHostedService<OnlineRestaurantPaymentRecoveryWorker>", program, StringComparison.Ordinal);
    Assert.Contains("RestaurantPayPalClientResolver", program, StringComparison.Ordinal);
    Assert.Contains("RestaurantPayPalHistorical", program, StringComparison.Ordinal);
    Assert.Contains("IPayPalRecoveryProcessor", notifications, StringComparison.Ordinal);
    Assert.Contains("OnlineOrderNotificationProviderMessageSet", notifications, StringComparison.Ordinal);
    Assert.Contains("MicrosoftGraphMailMessageState.Missing", notifications, StringComparison.Ordinal);
    Assert.Contains("MicrosoftGraphMailMessageState.Draft", notifications, StringComparison.Ordinal);
    var providerPersist = notifications.IndexOf("OnlineOrderNotificationProviderMessageSet", StringComparison.Ordinal);
    var providerSend = notifications.IndexOf("await _sender.SendDraftAsync(providerMessageId", StringComparison.Ordinal);
    Assert.True(providerPersist >= 0 && providerSend > providerPersist, "The immutable draft id must be persisted before Graph sends it.");

    Assert.Contains("ProviderMessageId nvarchar(512)", migration, StringComparison.Ordinal);
    Assert.Contains("OnlineOrderNotificationProviderMessageSet", migration, StringComparison.Ordinal);
    Assert.Contains("notification.[Status]=''Processing''", migration, StringComparison.Ordinal);
    Assert.Contains("notification.LeaseExpiresAtUtc<SYSUTCDATETIME()", migration, StringComparison.Ordinal);
    Assert.Contains("AND ProviderMessageId IS NOT NULL", migration, StringComparison.Ordinal);
  }

  [Fact]
  public void Capture_retry_returns_durable_pending_before_quote_expiry_or_repricing()
  {
    var service = Read("src/OrionERP.Infrastructure/Features/Restaurante/RestaurantOnlineCheckoutService.cs");
    var captureStart = service.IndexOf(
      "public async Task<RestaurantOnlinePayPalCaptureResult> CapturePayPalOrderAsync(",
      StringComparison.Ordinal);
    var captureEnd = service.IndexOf(
      "public async Task<RestaurantOnlineCheckoutStatusDto?> GetStatusAsync(",
      captureStart + 1,
      StringComparison.Ordinal);
    Assert.True(captureStart >= 0 && captureEnd > captureStart, "CapturePayPalOrderAsync could not be isolated.");

    var capture = service[captureStart..captureEnd];
    var attemptLoad = capture.IndexOf("attempt = await GetAttemptAsync", StringComparison.Ordinal);
    var durableResult = capture.IndexOf("var terminal = ExistingCaptureResult", StringComparison.Ordinal);
    var quoteRead = capture.IndexOf("if (!TryReadQuote", StringComparison.Ordinal);
    var reprice = capture.IndexOf("var current = await QuoteAsync", StringComparison.Ordinal);

    Assert.True(attemptLoad >= 0, "Capture must load the durable checkout attempt first.");
    Assert.True(durableResult > attemptLoad, "Capture must derive retries from the durable attempt.");
    Assert.True(quoteRead > durableResult, "Durable capture states must be returned before quote expiry is evaluated.");
    Assert.True(reprice > quoteRead, "A new capture may reprice only after validating its quote.");

    var resultStart = service.IndexOf(
      "private static RestaurantOnlinePayPalCaptureResult? ExistingCaptureResult(",
      StringComparison.Ordinal);
    var resultEnd = service.IndexOf("private static void ValidateProviderOrder(", resultStart + 1, StringComparison.Ordinal);
    Assert.True(resultStart >= 0 && resultEnd > resultStart, "ExistingCaptureResult could not be isolated.");

    var existingResult = service[resultStart..resultEnd];
    Assert.Contains("attempt.State == RestaurantOnlineCheckoutStatuses.CapturePending", existingResult, StringComparison.Ordinal);
    Assert.Contains("Code = \"capture_pending\"", existingResult, StringComparison.Ordinal);
    Assert.Contains("TrackingToken = trackingToken", existingResult, StringComparison.Ordinal);
    Assert.Contains("PaymentCaptured = false", existingResult, StringComparison.Ordinal);
    Assert.Contains("IsPending = true", existingResult, StringComparison.Ordinal);
  }

  [Fact]
  public void Attempt_create_maps_only_locked_revalidation_failures_to_a_safe_public_result()
  {
    var service = Read("src/OrionERP.Infrastructure/Features/Restaurante/RestaurantOnlineCheckoutService.cs");
    var createStart = service.IndexOf(
      "public async Task<RestaurantOnlinePayPalOrderResult> CreatePayPalOrderAsync(",
      StringComparison.Ordinal);
    var createEnd = service.IndexOf(
      "public async Task<RestaurantOnlinePayPalCaptureResult> CapturePayPalOrderAsync(",
      createStart + 1,
      StringComparison.Ordinal);
    Assert.True(createStart >= 0 && createEnd > createStart, "CreatePayPalOrderAsync could not be isolated.");

    var create = service[createStart..createEnd];
    Assert.Contains("when (IsAttemptCreateRevalidationFailure(exception))", create, StringComparison.Ordinal);
    Assert.Contains("var refreshedBootstrap = await LoadBootstrapAsync", create, StringComparison.Ordinal);
    Assert.Contains("EvaluateAvailability(binding, refreshedBootstrap)", create, StringComparison.Ordinal);
    Assert.Contains("\"requote_required\"", create, StringComparison.Ordinal);
    Assert.Contains("exception.Number is 53624 or 53628", service, StringComparison.Ordinal);
    Assert.DoesNotContain("exception.Number is 53624 or 53628 or", service, StringComparison.Ordinal);
  }

  [Fact]
  public void PayPal_recovery_reloads_a_concurrent_state_winner_and_still_finishes_its_lease()
  {
    var recovery = Read("src/OrionERP.Infrastructure/Features/Restaurante/RestaurantPayPalRecoveryProcessor.cs");
    var stateStart = recovery.IndexOf("private async Task SetAttemptStateAsync(", StringComparison.Ordinal);
    var stateEnd = recovery.IndexOf("private async Task FinishRefundAsync(", stateStart + 1, StringComparison.Ordinal);
    Assert.True(stateStart >= 0 && stateEnd > stateStart, "SetAttemptStateAsync could not be isolated.");

    var stateUpdate = recovery[stateStart..stateEnd];
    Assert.Contains("exception.Number == 53652", stateUpdate, StringComparison.Ordinal);
    Assert.Contains("restaurante.OnlineCheckoutAttemptGet", stateUpdate, StringComparison.Ordinal);
    Assert.Contains("string.Equals(current.State, row.State", stateUpdate, StringComparison.Ordinal);

    var voidedUpdate = recovery.IndexOf("PAYPAL_ORDER_VOIDED", StringComparison.Ordinal);
    var voidedFinish = recovery.IndexOf("await FinishRecoveryAsync", voidedUpdate, StringComparison.Ordinal);
    Assert.True(voidedUpdate >= 0 && voidedFinish > voidedUpdate,
      "A superseded VOIDED transition must still release its recovery lease.");
  }

  [Fact]
  public void Refund_recovery_requires_the_expected_capture_and_preserves_provider_id_on_mismatch()
  {
    var recovery = Read("src/OrionERP.Infrastructure/Features/Restaurante/RestaurantPayPalRecoveryProcessor.cs");
    var refundStart = recovery.IndexOf("private async Task ProcessRefundAsync(", StringComparison.Ordinal);
    var refundEnd = recovery.IndexOf("private async Task FinishRecoveryAsync(", refundStart + 1, StringComparison.Ordinal);
    Assert.True(refundStart >= 0 && refundEnd > refundStart, "ProcessRefundAsync could not be isolated.");

    var refund = recovery[refundStart..refundEnd];
    Assert.Contains("string.Equals(refund.CaptureId, row.ProviderCaptureId", recovery, StringComparison.Ordinal);
    Assert.Contains("!string.IsNullOrWhiteSpace(refund?.RefundId)", refund, StringComparison.Ordinal);
    Assert.Contains("providerRefundId: acceptedRefundId", refund, StringComparison.Ordinal);
    Assert.Contains("PAYPAL_REFUND_MISMATCH", refund, StringComparison.Ordinal);
  }

  [Fact]
  public void Refund_webhooks_get_provider_truth_then_bind_atomically_or_remain_alertable()
  {
    var recovery = Read("src/OrionERP.Infrastructure/Features/Restaurante/RestaurantPayPalRecoveryProcessor.cs");
    var eventStart = recovery.IndexOf("private async Task ProcessRefundEventAsync(", StringComparison.Ordinal);
    var eventEnd = recovery.IndexOf("private async Task ProcessRefundAsync(", eventStart + 1, StringComparison.Ordinal);
    Assert.True(eventStart >= 0 && eventEnd > eventStart, "ProcessRefundEventAsync could not be isolated.");

    var refundEvent = recovery[eventStart..eventEnd];
    var providerGet = refundEvent.IndexOf("GetRefundAsync", StringComparison.Ordinal);
    var atomicBind = refundEvent.IndexOf("restaurante.PaymentGatewayRefundEventBind", StringComparison.Ordinal);
    Assert.True(providerGet >= 0 && atomicBind > providerGet,
      "A refund webhook must retrieve provider truth before binding a local refund.");
    Assert.Contains("ProviderCaptureId = refund.CaptureId", refundEvent, StringComparison.Ordinal);
    Assert.Contains("Amount = refund.Amount.Value", refundEvent, StringComparison.Ordinal);
    Assert.Contains("CurrencyCode = refund.Amount.Currency", refundEvent, StringComparison.Ordinal);
    Assert.Contains("\"Failed\"", refundEvent, StringComparison.Ordinal);
    Assert.Contains("PAYPAL_REFUND_EVENT_", refundEvent, StringComparison.Ordinal);

    Assert.Contains("IsCaptureRefundOrReversalEvent", recovery, StringComparison.Ordinal);
    Assert.Contains("PAYPAL_CAPTURE_REFUND_REQUIRES_RECONCILIATION", recovery, StringComparison.Ordinal);
    Assert.DoesNotContain("refundStatus ? \"Processed\"", recovery, StringComparison.Ordinal);
  }

  private static string Read(string relativePath)
    => File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../", relativePath)));
}
