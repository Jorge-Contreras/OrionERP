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
    Assert.Contains("se capturan directamente en un formulario de Clip", page, StringComparison.Ordinal);
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
    Assert.Contains("&& !MembershipRequired && !IsDelivery) await QuoteAsync();", page, StringComparison.Ordinal);
    Assert.Contains("else if (MembershipRequired)", page, StringComparison.Ordinal);
    Assert.Contains("/cuenta/registro?returnUrl=%2Fcheckout", page, StringComparison.Ordinal);
    Assert.Contains("Confirma el correo que te enviamos para activarla.", page, StringComparison.Ordinal);
  }

  [Fact]
  public void Checkout_keeps_feedback_in_view_and_the_cart_visible_before_delivery_is_quoted()
  {
    var page = Read("src/OrionERP.Bruno.Web/Features/Ordering/BrunoCheckoutPage.razor");
    var styles = Read("src/OrionERP.Bruno.Web/Features/Ordering/BrunoCheckoutPage.razor.css");

    Assert.Contains("role=\"alertdialog\"", page, StringComparison.Ordinal);
    Assert.Contains("checkout-feedback__dialog", page, StringComparison.Ordinal);
    Assert.Contains("availabilityWarningDismissed", page, StringComparison.Ordinal);
    Assert.DoesNotContain("class=\"checkout-alert", page, StringComparison.Ordinal);
    Assert.Contains("@foreach (var item in cart)", page, StringComparison.Ordinal);
    Assert.Contains("Subtotal estimado", page, StringComparison.Ordinal);
    Assert.Contains("<dt>Entrega</dt><dd>Por confirmar</dd>", page, StringComparison.Ordinal);
    Assert.Contains("EstimatedCartSubtotal", page, StringComparison.Ordinal);
    Assert.Contains(".checkout-feedback { position:fixed", styles, StringComparison.Ordinal);
    Assert.Contains(".summary-pending", styles, StringComparison.Ordinal);
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
  public void Closing_the_bank_verification_never_offers_another_card()
  {
    var checkout = Read("src/OrionERP.Bruno.Web/Features/Ordering/BrunoCheckoutPage.razor");
    var script = Read("src/OrionERP.Bruno.Web/wwwroot/js/brunos-ordering.js");
    var service = Read("src/OrionERP.Infrastructure/Features/Restaurante/RestaurantOnlineCheckoutService.cs");

    // Cerrar el iframe del 3DS no cancela nada: el cargo sigue vivo en Clip.
    // Decirle al cliente que no se cobro seria mentira y lo llevaria a pagar dos veces.
    var threeDs = script[script.IndexOf("const runThreeDsAsync", StringComparison.Ordinal)..];
    Assert.Contains("event.origin !== expectedOrigin", threeDs, StringComparison.Ordinal);
    Assert.Contains("outcome: 'dismissed'", threeDs, StringComparison.Ordinal);
    Assert.DoesNotContain("No se realizó un nuevo cobro", script, StringComparison.Ordinal);

    var pay = script[script.IndexOf("async payWithCard(options)", StringComparison.Ordinal)..];
    var dismissed = pay.IndexOf("verification.outcome !== 'completed'", StringComparison.Ordinal);
    Assert.True(dismissed >= 0, "The abandoned-verification branch could not be isolated.");
    Assert.Contains("isPending: true", pay[dismissed..], StringComparison.Ordinal);
    Assert.Contains("No vuelvas a pagar", pay[dismissed..], StringComparison.Ordinal);

    // Un cargo en vuelo o en 3DS nunca vuelve a la pantalla de pago.
    var existing = service[service.IndexOf(
      "private static RestaurantOnlineChargeResult? ExistingChargeResult(", StringComparison.Ordinal)..];
    Assert.Contains("RestaurantOnlineCheckoutStatuses.ChargePending", existing, StringComparison.Ordinal);
    Assert.Contains("RestaurantOnlineCheckoutStatuses.Authenticating3ds", existing, StringComparison.Ordinal);
    Assert.Contains("RestaurantOnlineCheckoutStatuses.ChargeUnknown", existing, StringComparison.Ordinal);
    Assert.Contains("/pedido/", checkout, StringComparison.Ordinal);
  }

  [Fact]
  public void Bruno_keeps_an_independent_blank_secret_section_and_safe_defaults()
  {
    using var document = JsonDocument.Parse(Read("src/OrionERP.Bruno.Web/appsettings.json"));
    var checkout = document.RootElement.GetProperty("RestaurantCheckout");

    Assert.Equal("Sandbox", checkout.GetProperty("Environment").GetString());
    Assert.Equal("MXN", checkout.GetProperty("Currency").GetString());
    Assert.Equal("es", checkout.GetProperty("ClipLocale").GetString());
    Assert.Equal("clip-v1", checkout.GetProperty("MerchantProfileKey").GetString());
    Assert.Equal(string.Empty, checkout.GetProperty("ClipApiKey").GetString());
    Assert.Equal(string.Empty, checkout.GetProperty("ClipApiSecret").GetString());
    Assert.Equal(10, checkout.GetProperty("QuoteTokenLifetimeMinutes").GetInt32());
    // Los pagos diferidos no se pueden reembolsar por API (AI1806).
    Assert.Equal(1, checkout.GetProperty("Installments").GetInt32());
    Assert.False(checkout.TryGetProperty("PayPalClientId", out _));
    Assert.False(checkout.TryGetProperty("ClipAuthScheme", out _));
    Assert.False(checkout.TryGetProperty("PayPalWebhookId", out _));
  }

  [Fact]
  public void Checkout_api_separates_browser_antiforgery_from_verified_webhook()
  {
    var api = Read("src/OrionERP.Bruno.Web/Features/Ordering/RestaurantCheckoutApi.cs");
    var program = Read("src/OrionERP.Bruno.Web/Program.cs");

    Assert.Contains("antiforgery.ValidateRequestAsync(context)", api, StringComparison.Ordinal);
    Assert.Contains("DisableAntiforgery()", api, StringComparison.Ordinal);
    Assert.Contains("RequireRateLimiting(\"checkout\")", api, StringComparison.Ordinal);
    Assert.Contains("RequireRateLimiting(\"webhook\")", api, StringComparison.Ordinal);
    // El aviso de Clip no va firmado: no hay encabezado que verificar, y el
    // cuerpo se acota a su forma porque son tres campos cortos.
    Assert.DoesNotContain("TRANSMISSION", api, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("MaximumWebhookBodyBytes = 4_096", api, StringComparison.Ordinal);
    Assert.Contains("ClipPaymentsClient<RestaurantCheckoutOptions>", program, StringComparison.Ordinal);
    Assert.Contains("https://sdk.clip.mx", program, StringComparison.Ordinal);
    Assert.Contains("https://3ds.payclip.com", program, StringComparison.Ordinal);
    Assert.Contains("https://3ds.payclip.io", program, StringComparison.Ordinal);
    // Descubierto probando en sandbox: el SDK sirve el formulario desde
    // elements.clip.mx y carga su script de prevención de fraudes desde
    // tools.clip.mx. Sin estos dos orígenes el formulario no dibuja nada.
    Assert.Contains("https://elements.clip.mx", program, StringComparison.Ordinal);
    Assert.Contains("https://tools.clip.mx", program, StringComparison.Ordinal);
    Assert.Contains("https://places.googleapis.com", program, StringComparison.Ordinal);
    Assert.DoesNotContain("paypal.com", program, StringComparison.OrdinalIgnoreCase);
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
    Assert.Contains("IPaymentRecoveryProcessor, RestaurantPaymentRecoveryProcessor", program, StringComparison.Ordinal);
    Assert.Contains("IPaymentRecoveryProcessor", notifications, StringComparison.Ordinal);
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
  public void Charge_retry_returns_durable_pending_before_quote_expiry_or_repricing()
  {
    var service = Read("src/OrionERP.Infrastructure/Features/Restaurante/RestaurantOnlineCheckoutService.cs");
    var chargeStart = service.IndexOf(
      "public async Task<RestaurantOnlineChargeResult> ChargeAsync(",
      StringComparison.Ordinal);
    var chargeEnd = service.IndexOf(
      "public async Task<RestaurantOnlineChargeResult> ConfirmChargeAsync(",
      chargeStart + 1,
      StringComparison.Ordinal);
    Assert.True(chargeStart >= 0 && chargeEnd > chargeStart, "ChargeAsync could not be isolated.");

    var charge = service[chargeStart..chargeEnd];
    var attemptLoad = charge.IndexOf("attempt = await GetAttemptAsync", StringComparison.Ordinal);
    var durableResult = charge.IndexOf("var terminal = ExistingChargeResult", StringComparison.Ordinal);
    var quoteRead = charge.IndexOf("if (!TryReadQuote", StringComparison.Ordinal);
    var reprice = charge.IndexOf("var current = await QuoteAsync", StringComparison.Ordinal);
    var exclusive = charge.IndexOf("await BeginChargeAsync", StringComparison.Ordinal);
    var providerCall = charge.IndexOf("_clip.CreatePaymentAsync", StringComparison.Ordinal);

    Assert.True(attemptLoad >= 0, "A charge must load the durable checkout attempt first.");
    Assert.True(durableResult > attemptLoad, "A charge must derive retries from the durable attempt.");
    Assert.True(quoteRead > durableResult, "Durable charge states must be returned before quote expiry is evaluated.");
    Assert.True(reprice > quoteRead, "A new charge may reprice only after validating its quote.");
    // El derecho exclusivo lo concede la base y es lo unico que sustituye a la
    // llave de idempotencia que Clip no ofrece en POST /payments.
    Assert.True(exclusive > reprice, "The exclusive charge must be granted after repricing.");
    Assert.True(providerCall > exclusive, "Clip may only be called once the exclusive charge is held.");

    var existingStart = service.IndexOf(
      "private static RestaurantOnlineChargeResult? ExistingChargeResult(",
      StringComparison.Ordinal);
    var existingEnd = service.IndexOf("private static bool IsAttemptCreateRevalidationFailure(", existingStart + 1, StringComparison.Ordinal);
    Assert.True(existingStart >= 0 && existingEnd > existingStart, "ExistingChargeResult could not be isolated.");

    var existingResult = service[existingStart..existingEnd];
    Assert.Contains("PendingChargeResult(trackingToken, attempt.State)", existingResult, StringComparison.Ordinal);
    // Quoted y PaymentDenied son los unicos estados que admiten otra tarjeta.
    Assert.Contains("return null;", existingResult, StringComparison.Ordinal);
  }

  [Fact]
  public void Attempt_create_maps_only_locked_revalidation_failures_to_a_safe_public_result()
  {
    var service = Read("src/OrionERP.Infrastructure/Features/Restaurante/RestaurantOnlineCheckoutService.cs");
    var createStart = service.IndexOf(
      "public async Task<RestaurantOnlineCheckoutBeginResult> BeginCheckoutAsync(",
      StringComparison.Ordinal);
    var createEnd = service.IndexOf(
      "public async Task<RestaurantOnlineChargeResult> ChargeAsync(",
      createStart + 1,
      StringComparison.Ordinal);
    Assert.True(createStart >= 0 && createEnd > createStart, "BeginCheckoutAsync could not be isolated.");

    var create = service[createStart..createEnd];
    Assert.Contains("when (IsAttemptCreateRevalidationFailure(exception))", create, StringComparison.Ordinal);
    Assert.Contains("var refreshedBootstrap = await LoadBootstrapAsync", create, StringComparison.Ordinal);
    Assert.Contains("EvaluateAvailability(binding, refreshedBootstrap)", create, StringComparison.Ordinal);
    Assert.Contains("\"requote_required\"", create, StringComparison.Ordinal);
    // Reservar el intento no toca a Clip: el pago no existe hasta el cargo.
    Assert.DoesNotContain("_clip.", create, StringComparison.Ordinal);
    Assert.Contains("exception.Number is 53624 or 53628", service, StringComparison.Ordinal);
    Assert.DoesNotContain("exception.Number is 53624 or 53628 or", service, StringComparison.Ordinal);
  }

  [Fact]
  public void Charge_recovery_resolves_by_lookup_and_never_repeats_a_charge()
  {
    var recovery = Read("src/OrionERP.Infrastructure/Features/Restaurante/RestaurantPaymentRecoveryProcessor.cs");
    var client = Read("src/OrionERP.Infrastructure/Features/Payments/Clip/ClipPaymentsClient.cs");

    // La invariante central de toda la migracion: Clip no acepta llave de
    // idempotencia en POST /payments, asi que la recuperacion jamas cobra.
    Assert.DoesNotContain("CreatePaymentAsync", recovery, StringComparison.Ordinal);
    Assert.Contains("_clip.GetPaymentAsync", recovery, StringComparison.Ordinal);

    // Un cargo que no dejo identificador solo se encuentra barriendo la ventana:
    // GET /payments no filtra por external_reference.
    var sweep = recovery[recovery.IndexOf("private async Task<ClipPaymentResult?> FindPaymentAsync", StringComparison.Ordinal)..];
    Assert.Contains("_clip.ListPaymentsAsync", sweep, StringComparison.Ordinal);
    Assert.Contains("ClipExternalReference.TryParse", sweep, StringComparison.Ordinal);

    // Un desenlace desconocido en el cobro se marca como tal, nunca como transitorio.
    Assert.Contains("treatFailureAsUnknown: true", client, StringComparison.Ordinal);
    Assert.Contains("isOutcomeUnknown: treatFailureAsUnknown", client, StringComparison.Ordinal);

    // Toda rama libera su concesion, incluida la que declara el cargo perdido.
    var missing = recovery[recovery.IndexOf("private async Task HandleMissingPaymentAsync", StringComparison.Ordinal)..];
    Assert.Contains("CLIP_CHARGE_NOT_FOUND", missing, StringComparison.Ordinal);
    Assert.Contains("ChargeReconciliationWindowMinutes", missing, StringComparison.Ordinal);
    Assert.Contains("await FinishRecoveryAsync", missing, StringComparison.Ordinal);

    // Un pago ajeno o con importe distinto nunca se acepta como cobro del pedido.
    Assert.Contains("private static bool BelongsToAttempt", recovery, StringComparison.Ordinal);
    Assert.Contains("payment.Amount == row.Total", recovery, StringComparison.Ordinal);
    Assert.Contains("CLIP_PAYMENT_MISMATCH", recovery, StringComparison.Ordinal);
  }

  [Fact]
  public void Refund_recovery_checks_what_is_already_refunded_before_asking_again()
  {
    var recovery = Read("src/OrionERP.Infrastructure/Features/Restaurante/RestaurantPaymentRecoveryProcessor.cs");
    var refundStart = recovery.IndexOf("private async Task ProcessRefundAsync(", StringComparison.Ordinal);
    var refundEnd = recovery.IndexOf("private async Task FinishRefundFromProviderAsync(", refundStart + 1, StringComparison.Ordinal);
    Assert.True(refundStart >= 0 && refundEnd > refundStart, "ProcessRefundAsync could not be isolated.");

    var refund = recovery[refundStart..refundEnd];
    // La llave de idempotencia de Clip vive un minuto, asi que no protege entre
    // reintentos; lo que protege es mirar cuanto lleva reembolsado el pago.
    var lookup = refund.IndexOf("_clip.GetPaymentAsync", StringComparison.Ordinal);
    var request = refund.IndexOf("_clip.RefundPaymentAsync", StringComparison.Ordinal);
    Assert.True(lookup >= 0 && request > lookup,
      "The payment's refunded amount must be read before asking Clip for another refund.");
    Assert.Contains("CLIP_REFUND_ALREADY_SETTLED", refund, StringComparison.Ordinal);
    Assert.Contains("CLIP_REFUND_EXCEEDS_PAYMENT", refund, StringComparison.Ordinal);
    Assert.Contains("exception.IsOutcomeUnknown", refund, StringComparison.Ordinal);

    // Clip no entrega desglose de comision, asi que un reembolso completado se
    // liquida a valor nominal: sale el monto integro y no se registra comision.
    // Las columnas son todo-o-nada, asi que solo se escriben al completarse.
    var finish = recovery[recovery.IndexOf("private async Task FinishRefundAsync(", StringComparison.Ordinal)..];
    Assert.Contains("var isSettled = string.Equals(outcome, \"Completed\"", finish, StringComparison.Ordinal);
    Assert.Contains("ProviderGrossAmount = isSettled ? row.Amount : (decimal?)null", finish, StringComparison.Ordinal);
    Assert.Contains("ProviderFeeAmount = isSettled ? 0m : (decimal?)null", finish, StringComparison.Ordinal);
    Assert.Contains("ReconciledAtUtc = isSettled ?", finish, StringComparison.Ordinal);
  }

  [Fact]
  public void Notifications_are_verified_by_lookup_and_external_refunds_stay_alertable()
  {
    var service = Read("src/OrionERP.Infrastructure/Features/Restaurante/RestaurantOnlineCheckoutService.cs");
    var recovery = Read("src/OrionERP.Infrastructure/Features/Restaurante/RestaurantPaymentRecoveryProcessor.cs");

    // El aviso de Clip llega sin firma: nada de su cuerpo se toma como verdad,
    // solo el identificador para ir a consultar el pago.
    var webhook = service[service.IndexOf("public async Task<RestaurantClipWebhookResult> ProcessClipWebhookAsync(", StringComparison.Ordinal)..];
    Assert.Contains("TryParseClipNotification", webhook, StringComparison.Ordinal);
    Assert.Contains("PaymentGatewayEventRecord", webhook, StringComparison.Ordinal);
    Assert.DoesNotContain("GetPaymentAsync", webhook[..webhook.IndexOf("private ", StringComparison.Ordinal)], StringComparison.Ordinal);
    // El mismo pago avisa varias veces con distinto event_type; el par evita que
    // una transicion posterior se descarte como duplicado.
    Assert.Contains("$\"{notification.Id}:{notification.EventType}\"", webhook, StringComparison.Ordinal);

    // Un reembolso hecho fuera del panel, por ejemplo desde la app de Clip, no
    // se puede aplicar solo: se marca para revision en vez de ignorarse.
    Assert.Contains("CLIP_EXTERNAL_REFUND_REQUIRES_RECONCILIATION", recovery, StringComparison.Ordinal);
    Assert.Contains("payment.AmountRefunded > 0 && !IsRefundState(row.State)", recovery, StringComparison.Ordinal);
    Assert.Contains("ManualReviewRetryDelay", recovery, StringComparison.Ordinal);
  }

  [Fact]
  public void No_restaurant_code_still_reads_the_renamed_provider_columns()
  {
    // La migración a Clip renombró PayPal*Id a Provider*Id. Validar sólo los
    // procedimientos de la base no basta: el SQL embebido en C# y las clases que
    // Dapper materializa también nombran columnas, y ahí el fallo es silencioso
    // —la propiedad queda en null— hasta que el pedido ya se cobró. Eso llegó a
    // rechazar y reembolsar un pedido pagado en la prueba de sandbox.
    var raiz = Path.GetFullPath(Path.Combine(
      AppContext.BaseDirectory, "../../../../../", "src"));
    var renombradas = new[] { "PayPalOrderId", "PayPalCaptureId", "PayPalCreateRequestId" };

    var ofensores = Directory
      .EnumerateFiles(raiz, "*.cs", SearchOption.AllDirectories)
      .Concat(Directory.EnumerateFiles(raiz, "*.razor", SearchOption.AllDirectories))
      .Where(ruta => !ruta.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
      // Bonhomía conserva su propio flujo PayPal y sus columnas no se renombraron.
      .Where(ruta => !ruta.Contains("Bonhomia", StringComparison.OrdinalIgnoreCase)
        && !ruta.Contains("Hospitality", StringComparison.OrdinalIgnoreCase)
        && !ruta.Contains("Reservaciones", StringComparison.OrdinalIgnoreCase))
      .Select(ruta => (Ruta: ruta, Texto: File.ReadAllText(ruta)))
      .Where(archivo => renombradas.Any(columna =>
        archivo.Texto.Contains(columna, StringComparison.Ordinal)))
      .Select(archivo => Path.GetFileName(archivo.Ruta))
      .OrderBy(nombre => nombre, StringComparer.Ordinal)
      .ToArray();

    Assert.True(
      ofensores.Length == 0,
      $"Estos archivos siguen nombrando columnas renombradas: {string.Join(", ", ofensores)}");
  }

  private static string Read(string relativePath)
    => File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../", relativePath)));
}
