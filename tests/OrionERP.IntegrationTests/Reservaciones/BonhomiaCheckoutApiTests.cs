using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using OrionERP.Application.Features.Hospitality.PublicBooking;
using OrionERP.Application.Features.Platform;
using OrionERP.Application.Features.Reservaciones.Experiencias;
using OrionERP.Application.Features.Reservaciones.ListaReservaciones;
using OrionERP.Bonhomia.Web.Features.Bonhomia.Checkout;
using OrionERP.Infrastructure.Features.Reservaciones.ListaReservaciones.Pdf;

namespace OrionERP.IntegrationTests.Reservaciones;

public class HospitalityCheckoutApiTests
{
  private const string CurrentPrivacyVersion = "2026-09-02";
  private const string CurrentTermsVersion = "2026-09-02";

  [Fact]
  public async Task CreateOrder_RejectsMissingQuoteToken()
  {
    await using var app = await CreateAppAsync();
    var client = app.GetTestClient();

    var response = await client.PostAsJsonAsync("/api/hospitality/checkout/orders", new HospitalityCreatePayPalOrderRequest());

    Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
  }

  [Fact]
  public async Task CheckoutRoutes_AreAnonymous_InPublicHostMapping()
  {
    await using var app = await CreateAppAsync();
    var endpoints = app.Services.GetRequiredService<EndpointDataSource>().Endpoints
      .OfType<RouteEndpoint>()
      .ToArray();

    AssertAnonymousRoute(endpoints, "/api/hospitality/checkout/orders");
    AssertAnonymousRoute(endpoints, "/api/hospitality/checkout/orders/{orderId}");
    AssertAnonymousRoute(endpoints, "/api/hospitality/checkout/reservations/{reservationId:int}/pdf");
  }

  [Fact]
  public async Task CreateOrder_ReturnsConflict_WhenLiveQuoteChanged()
  {
    var original = CreateQuote(1250m);
    var changed = CreateQuote(1300m);
    var booking = new FakeBookingService { Quote = changed };
    await using var app = await CreateAppAsync(bookingService: booking);
    var tokenService = app.Services.GetRequiredService<IHospitalityQuoteTokenService>();
    var client = app.GetTestClient();

    var response = await client.PostAsJsonAsync("/api/hospitality/checkout/orders", new HospitalityCreatePayPalOrderRequest
    {
      QuoteToken = tokenService.CreateToken(original),
      QuoteFingerprint = original.Fingerprint,
      Accepted = true,
      PrivacyVersion = CurrentPrivacyVersion,
      TermsVersion = CurrentTermsVersion
    });

    Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
  }

  [Fact]
  public async Task CreateOrder_UsesPaymentAttemptId_ForPayPalIdempotency()
  {
    var quote = CreateQuote(1250m);
    var payPal = new FakePayPalClient();
    await using var app = await CreateAppAsync(payPalClient: payPal);
    var tokenService = app.Services.GetRequiredService<IHospitalityQuoteTokenService>();
    var client = app.GetTestClient();

    var response = await client.PostAsJsonAsync("/api/hospitality/checkout/orders", new HospitalityCreatePayPalOrderRequest
    {
      QuoteToken = tokenService.CreateToken(quote),
      QuoteFingerprint = quote.Fingerprint,
      PaymentAttemptId = "attempt-abc-123",
      Accepted = true,
      PrivacyVersion = CurrentPrivacyVersion,
      TermsVersion = CurrentTermsVersion
    });

    response.EnsureSuccessStatusCode();
    Assert.Equal(ExpectedPayPalRequestId("ord", "attempt-abc-123", quote), payPal.LastCreateIdempotencyKey);
  }

  [Fact]
  public async Task CreateOrder_RejectsMissingPrivacyVersion_BeforeCreatingPayPalOrder()
  {
    var quote = CreateQuote(1250m);
    var payPal = new FakePayPalClient();
    await using var app = await CreateAppAsync(payPalClient: payPal);
    var tokenService = app.Services.GetRequiredService<IHospitalityQuoteTokenService>();
    var client = app.GetTestClient();

    var response = await client.PostAsJsonAsync("/api/hospitality/checkout/orders", new HospitalityCreatePayPalOrderRequest
    {
      QuoteToken = tokenService.CreateToken(quote),
      QuoteFingerprint = quote.Fingerprint,
      Accepted = true,
      PrivacyVersion = string.Empty,
      TermsVersion = CurrentTermsVersion
    });
    var problem = await response.Content.ReadAsStringAsync();

    Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    Assert.Contains("legal_consent_required", problem, StringComparison.Ordinal);
    Assert.Equal(0, payPal.CreateCount);
  }

  [Fact]
  public async Task ConfirmOrder_RejectsObsoleteTermsVersion_BeforeCapturingPayPalOrder()
  {
    var quote = CreateQuote(1250m);
    var booking = new FakeBookingService { Quote = quote };
    var payPal = new FakePayPalClient();
    await using var app = await CreateAppAsync(booking, payPal);
    var tokenService = app.Services.GetRequiredService<IHospitalityQuoteTokenService>();
    var client = app.GetTestClient();

    var response = await client.PostAsJsonAsync("/api/hospitality/checkout/orders/PAYPAL-STALE", new HospitalityConfirmPayPalOrderRequest
    {
      QuoteToken = tokenService.CreateToken(quote),
      QuoteFingerprint = quote.Fingerprint,
      Accepted = true,
      PrivacyVersion = CurrentPrivacyVersion,
      TermsVersion = "2025-01-01",
      Customer = CreateCustomer()
    });
    var problem = await response.Content.ReadAsStringAsync();

    Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    Assert.Contains("legal_documents_changed", problem, StringComparison.Ordinal);
    Assert.Equal(0, payPal.CaptureCount);
    Assert.Equal(0, booking.PaidReservationCount);
  }

  [Fact]
  public async Task ConfirmOrder_DoesNotCapture_WhenAvailabilityDisappears()
  {
    var quote = CreateQuote(1250m);
    var booking = new FakeBookingService
    {
      CreateQuoteException = new HospitalityPublicBookingException("not_available", "No disponible.")
    };
    var payPal = new FakePayPalClient();
    await using var app = await CreateAppAsync(booking, payPal);
    var tokenService = app.Services.GetRequiredService<IHospitalityQuoteTokenService>();
    var client = app.GetTestClient();

    var response = await client.PostAsJsonAsync("/api/hospitality/checkout/orders/PAYPAL-1", new HospitalityConfirmPayPalOrderRequest
    {
      QuoteToken = tokenService.CreateToken(quote),
      QuoteFingerprint = quote.Fingerprint,
      PaymentAttemptId = "attempt-confirm-123",
      Accepted = true,
      PrivacyVersion = CurrentPrivacyVersion,
      TermsVersion = CurrentTermsVersion,
      Customer = CreateCustomer()
    });

    Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    Assert.Equal(0, payPal.CaptureCount);
  }

  [Fact]
  public async Task ConfirmOrder_CapturesAndCreatesReservation()
  {
    var quote = CreateQuote(1250m);
    var booking = new FakeBookingService
    {
      Quote = quote,
      PaidReservation = new HospitalityPaidReservationResult
      {
        ReservationId = 49210,
        TransaccionId = 8821,
        ClientName = "Comprador PayPal",
        Total = quote.Total,
        CreatedNewReservation = true
      }
    };
    var payPal = new FakePayPalClient
    {
      CaptureResult = new HospitalityPayPalCaptureResult
      {
        OrderId = "PAYPAL-1",
        CustomId = quote.Fingerprint,
        ReferenceId = quote.QuoteId.ToString("N"),
        OrderStatus = "COMPLETED",
        CaptureId = "CAPTURE-1",
        Status = "COMPLETED",
        Amount = quote.Total,
        Currency = "MXN",
        PayerName = "Comprador PayPal",
        PayerEmail = "payer@example.com",
        PayerPhone = "7491234567"
      }
    };
    var emailSender = new FakeConfirmationEmailSender();
    await using var app = await CreateAppAsync(booking, payPal, emailSender);
    var tokenService = app.Services.GetRequiredService<IHospitalityQuoteTokenService>();
    var client = app.GetTestClient();

    var response = await client.PostAsJsonAsync("/api/hospitality/checkout/orders/PAYPAL-1", new HospitalityConfirmPayPalOrderRequest
    {
      QuoteToken = tokenService.CreateToken(quote),
      QuoteFingerprint = quote.Fingerprint,
      PaymentAttemptId = "attempt-confirm-123",
      Accepted = true,
      PrivacyVersion = CurrentPrivacyVersion,
      TermsVersion = CurrentTermsVersion
    });

    response.EnsureSuccessStatusCode();
    var payload = await response.Content.ReadFromJsonAsync<HospitalityConfirmPayPalOrderResponse>();

    Assert.NotNull(payload);
    Assert.Equal(49210, payload!.ReservationId);
    Assert.Equal(8821, payload.TransaccionId);
    Assert.Equal("Comprador PayPal", payload.ClientName);
    Assert.Equal("payer@example.com", payload.CustomerEmail);
    Assert.Equal("7491234567", payload.CustomerPhone);
    Assert.Equal("Suite Paris", payload.RoomName);
    Assert.Equal(new DateOnly(2026, 6, 10), payload.CheckIn);
    Assert.Equal(new DateOnly(2026, 6, 12), payload.CheckOut);
    Assert.Equal(2, payload.Nights);
    Assert.Equal(2, payload.Guests);
    Assert.Equal("PAYPAL-1", payload.PayPalOrderId);
    Assert.Equal("CAPTURE-1", payload.PayPalCaptureId);
    Assert.Equal("COMPLETED", payload.PayPalStatus);
    Assert.Equal("payer@example.com", payload.PayPalPayerEmail);
    Assert.Contains("/api/hospitality/checkout/reservations/49210/pdf?token=", payload.PdfUrl, StringComparison.Ordinal);
    Assert.Equal(1, payPal.CaptureCount);
    Assert.Equal(ExpectedPayPalRequestId("cap", "attempt-confirm-123", quote), payPal.LastCaptureIdempotencyKey);
    Assert.Equal(1, booking.PaidReservationCount);
    Assert.NotNull(booking.LastCustomer);
    Assert.Equal("Comprador PayPal", booking.LastCustomer!.FullName);
    Assert.Equal("payer@example.com", booking.LastCustomer.Email);
    Assert.Equal("7491234567", booking.LastCustomer.Phone);
    Assert.NotNull(booking.LastLegalAcceptance);
    Assert.Equal(CurrentPrivacyVersion, booking.LastLegalAcceptance!.PrivacyVersion);
    Assert.Equal(CurrentTermsVersion, booking.LastLegalAcceptance.TermsVersion);
    Assert.Equal(TimeSpan.Zero, booking.LastLegalAcceptance.AcceptedAtUtc.Offset);
    Assert.True(booking.LastLegalAcceptance.AcceptedAtUtc <= DateTimeOffset.UtcNow);
    var confirmationEmail = Assert.Single(emailSender.Sent);
    Assert.Equal(49210, confirmationEmail.ReservationId);
    Assert.Equal("payer@example.com", confirmationEmail.Customer.Email);
    Assert.Equal("CAPTURE-1", confirmationEmail.Payment.CaptureId);
    Assert.Contains("/api/hospitality/checkout/reservations/49210/pdf?token=", confirmationEmail.PdfUrl, StringComparison.Ordinal);
  }

  [Fact]
  public async Task ConfirmOrder_PreservesProtectedQuoteIdentity_WhenLiveQuoteRegeneratesQuoteId()
  {
    var protectedQuote = CreateQuote(1250m);
    var liveQuote = CreateQuote(1250m);
    var regeneratedLiveQuoteId = liveQuote.QuoteId;
    Assert.NotEqual(protectedQuote.QuoteId, regeneratedLiveQuoteId);
    Assert.Equal(protectedQuote.Fingerprint, liveQuote.Fingerprint);

    var booking = new FakeBookingService { Quote = liveQuote };
    var payPal = new FakePayPalClient
    {
      CaptureResult = new HospitalityPayPalCaptureResult
      {
        OrderId = "PAYPAL-RECALCULATED",
        CustomId = protectedQuote.Fingerprint,
        ReferenceId = protectedQuote.QuoteId.ToString("N"),
        OrderStatus = "COMPLETED",
        CaptureId = "CAPTURE-RECALCULATED",
        Status = "COMPLETED",
        Amount = protectedQuote.Total,
        Currency = protectedQuote.Currency,
        PayerName = "Cliente Web",
        PayerEmail = "cliente@example.com"
      }
    };
    await using var app = await CreateAppAsync(booking, payPal);
    var tokenService = app.Services.GetRequiredService<IHospitalityQuoteTokenService>();
    var client = app.GetTestClient();

    var response = await client.PostAsJsonAsync(
      "/api/hospitality/checkout/orders/PAYPAL-RECALCULATED",
      new HospitalityConfirmPayPalOrderRequest
      {
        QuoteToken = tokenService.CreateToken(protectedQuote),
        QuoteFingerprint = protectedQuote.Fingerprint,
        PaymentAttemptId = "attempt-regenerated-quote-id",
        Accepted = true,
        PrivacyVersion = CurrentPrivacyVersion,
        TermsVersion = CurrentTermsVersion,
        Customer = CreateCustomer()
      });

    response.EnsureSuccessStatusCode();
    Assert.Equal(1, payPal.CaptureCount);
    Assert.Equal(1, booking.PaidReservationCount);
    Assert.Equal(protectedQuote.QuoteId, booking.LastPaidReservationQuote?.QuoteId);
    Assert.NotEqual(regeneratedLiveQuoteId, booking.LastPaidReservationQuote?.QuoteId);
  }

  [Fact]
  public async Task ConfirmOrder_ReturnsSuccess_WhenConfirmationEmailFails()
  {
    var quote = CreateQuote(1250m);
    var booking = new FakeBookingService
    {
      Quote = quote,
      PaidReservation = new HospitalityPaidReservationResult
      {
        ReservationId = 49210,
        TransaccionId = 8821,
        ClientName = "Comprador PayPal",
        Total = quote.Total,
        CreatedNewReservation = true
      }
    };
    var emailSender = new FakeConfirmationEmailSender
    {
      SendException = new InvalidOperationException("Graph unavailable")
    };

    await using var app = await CreateAppAsync(bookingService: booking, confirmationEmailSender: emailSender);
    var tokenService = app.Services.GetRequiredService<IHospitalityQuoteTokenService>();
    var client = app.GetTestClient();

    var response = await client.PostAsJsonAsync("/api/hospitality/checkout/orders/PAYPAL-1", new HospitalityConfirmPayPalOrderRequest
    {
      QuoteToken = tokenService.CreateToken(quote),
      QuoteFingerprint = quote.Fingerprint,
      PaymentAttemptId = "attempt-confirm-123",
      Accepted = true,
      PrivacyVersion = CurrentPrivacyVersion,
      TermsVersion = CurrentTermsVersion,
      Customer = CreateCustomer()
    });

    response.EnsureSuccessStatusCode();
    Assert.Single(emailSender.Sent);
    Assert.Equal(1, booking.PaidReservationCount);
  }

  [Fact]
  public async Task ConfirmOrder_DoesNotSendConfirmationEmail_WhenReservationAlreadyExists()
  {
    var quote = CreateQuote(1250m);
    var booking = new FakeBookingService
    {
      Quote = quote,
      PaidReservation = new HospitalityPaidReservationResult
      {
        ReservationId = 49210,
        TransaccionId = 8821,
        ClientName = "Comprador PayPal",
        Total = quote.Total,
        CreatedNewReservation = false
      }
    };
    var emailSender = new FakeConfirmationEmailSender();

    await using var app = await CreateAppAsync(bookingService: booking, confirmationEmailSender: emailSender);
    var tokenService = app.Services.GetRequiredService<IHospitalityQuoteTokenService>();
    var client = app.GetTestClient();

    var response = await client.PostAsJsonAsync("/api/hospitality/checkout/orders/PAYPAL-1", new HospitalityConfirmPayPalOrderRequest
    {
      QuoteToken = tokenService.CreateToken(quote),
      QuoteFingerprint = quote.Fingerprint,
      PaymentAttemptId = "attempt-confirm-123",
      Accepted = true,
      PrivacyVersion = CurrentPrivacyVersion,
      TermsVersion = CurrentTermsVersion,
      Customer = CreateCustomer()
    });

    response.EnsureSuccessStatusCode();
    Assert.Empty(emailSender.Sent);
  }

  [Fact]
  public async Task ConfirmOrder_UsesConfiguredPublicBaseUrl_ForReservationPdf()
  {
    var quote = CreateQuote(1250m);
    var booking = new FakeBookingService
    {
      Quote = quote,
      PaidReservation = new HospitalityPaidReservationResult
      {
        ReservationId = 49210,
        TransaccionId = 8821,
        ClientName = "Cliente Web",
        Total = quote.Total
      }
    };

    await using var app = await CreateAppAsync(
      bookingService: booking,
      configureOptions: options => options.PublicBaseUrl = "https://Bonhomia.Orion.land");
    var tokenService = app.Services.GetRequiredService<IHospitalityQuoteTokenService>();
    var client = app.GetTestClient();

    var response = await client.PostAsJsonAsync("/api/hospitality/checkout/orders/PAYPAL-1", new HospitalityConfirmPayPalOrderRequest
    {
      QuoteToken = tokenService.CreateToken(quote),
      QuoteFingerprint = quote.Fingerprint,
      PaymentAttemptId = "attempt-confirm-123",
      Accepted = true,
      PrivacyVersion = CurrentPrivacyVersion,
      TermsVersion = CurrentTermsVersion,
      Customer = CreateCustomer()
    });

    response.EnsureSuccessStatusCode();
    var payload = await response.Content.ReadFromJsonAsync<HospitalityConfirmPayPalOrderResponse>();

    Assert.NotNull(payload);
    Assert.StartsWith("https://bonhomia.orion.land/api/hospitality/checkout/reservations/49210/pdf?token=", payload!.PdfUrl, StringComparison.Ordinal);
  }

  [Fact]
  public async Task ConfirmOrder_ReturnsPendingPaymentProblem_WhenPayPalCaptureIsPending()
  {
    var quote = CreateQuote(1250m);
    var booking = new FakeBookingService
    {
      Quote = quote,
      CreatePaidReservationException = new HospitalityPublicBookingException(
        "payment_not_completed",
        "PayPal devolvio el cobro en estado PENDING (RECEIVING_PREFERENCE_MANDATES_MANUAL_ACTION). Orden: COMPLETED. No se creo la reservacion porque PayPal aun no acredita el pago.")
    };
    var payPal = new FakePayPalClient
    {
      CaptureResult = new HospitalityPayPalCaptureResult
      {
        OrderId = "PAYPAL-PENDING",
        CustomId = quote.Fingerprint,
        ReferenceId = quote.QuoteId.ToString("N"),
        CaptureId = "CAPTURE-PENDING",
        Status = "PENDING",
        StatusReason = "RECEIVING_PREFERENCE_MANDATES_MANUAL_ACTION",
        OrderStatus = "COMPLETED",
        Amount = quote.Total,
        Currency = "MXN",
        PayerEmail = "payer@example.com"
      }
    };
    await using var app = await CreateAppAsync(booking, payPal);
    var tokenService = app.Services.GetRequiredService<IHospitalityQuoteTokenService>();
    var client = app.GetTestClient();

    var response = await client.PostAsJsonAsync("/api/hospitality/checkout/orders/PAYPAL-PENDING", new HospitalityConfirmPayPalOrderRequest
    {
      QuoteToken = tokenService.CreateToken(quote),
      QuoteFingerprint = quote.Fingerprint,
      PaymentAttemptId = "attempt-pending-123",
      Accepted = true,
      PrivacyVersion = CurrentPrivacyVersion,
      TermsVersion = CurrentTermsVersion,
      Customer = CreateCustomer()
    });
    var problem = await response.Content.ReadAsStringAsync();

    Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    Assert.Contains("payment_not_completed", problem);
    Assert.Contains("RECEIVING_PREFERENCE_MANDATES_MANUAL_ACTION", problem);
    Assert.Equal(1, payPal.CaptureCount);
    Assert.Equal(0, booking.PaidReservationCount);
  }

  [Fact]
  public async Task ConfirmOrder_RejectsPayPalOrderBoundToAnotherWebsiteQuote()
  {
    var quote = CreateQuote(1250m);
    var booking = new FakeBookingService { Quote = quote };
    var payPal = new FakePayPalClient
    {
      CaptureResult = new HospitalityPayPalCaptureResult
      {
        OrderId = "PAYPAL-FOREIGN",
        CustomId = "another-sites-fingerprint",
        ReferenceId = Guid.NewGuid().ToString("N"),
        CaptureId = "CAPTURE-FOREIGN",
        Status = "COMPLETED",
        Amount = quote.Total,
        Currency = quote.Currency
      }
    };
    await using var app = await CreateAppAsync(booking, payPal);
    var tokenService = app.Services.GetRequiredService<IHospitalityQuoteTokenService>();
    var client = app.GetTestClient();

    var response = await client.PostAsJsonAsync("/api/hospitality/checkout/orders/PAYPAL-FOREIGN", new HospitalityConfirmPayPalOrderRequest
    {
      QuoteToken = tokenService.CreateToken(quote),
      QuoteFingerprint = quote.Fingerprint,
      PaymentAttemptId = "attempt-foreign",
      Accepted = true,
      PrivacyVersion = CurrentPrivacyVersion,
      TermsVersion = CurrentTermsVersion,
      Customer = CreateCustomer()
    });
    var problem = await response.Content.ReadAsStringAsync();

    Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    Assert.Contains("paypal_quote_mismatch", problem, StringComparison.Ordinal);
    Assert.Equal(1, payPal.CaptureCount);
    Assert.Equal(0, booking.PaidReservationCount);
  }

  [Fact]
  public async Task ConfirmOrder_ReturnsProblem_WhenReservationCreationFailsUnexpectedly()
  {
    var quote = CreateQuote(1250m);
    var booking = new FakeBookingService
    {
      Quote = quote,
      CreatePaidReservationUnexpectedException = new InvalidOperationException("Database write failed.")
    };
    var payPal = new FakePayPalClient();
    await using var app = await CreateAppAsync(booking, payPal);
    var tokenService = app.Services.GetRequiredService<IHospitalityQuoteTokenService>();
    var client = app.GetTestClient();

    var response = await client.PostAsJsonAsync("/api/hospitality/checkout/orders/PAYPAL-RECOVERY", new HospitalityConfirmPayPalOrderRequest
    {
      QuoteToken = tokenService.CreateToken(quote),
      QuoteFingerprint = quote.Fingerprint,
      PaymentAttemptId = "attempt-recovery-123",
      Accepted = true,
      PrivacyVersion = CurrentPrivacyVersion,
      TermsVersion = CurrentTermsVersion,
      Customer = CreateCustomer()
    });
    var problem = await response.Content.ReadAsStringAsync();

    Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    Assert.Contains("checkout_confirm_failed", problem);
    Assert.Contains("no intentes pagar de nuevo", problem);
    Assert.Equal(1, payPal.CaptureCount);
    Assert.Equal(0, booking.PaidReservationCount);
  }

  [Fact]
  public async Task GetReservationPdf_ReturnsUnauthorized_WhenTokenIsInvalid()
  {
    await using var app = await CreateAppAsync();
    var client = app.GetTestClient();

    var response = await client.GetAsync("/api/hospitality/checkout/reservations/49210/pdf?token=invalido");

    Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
  }

  [Fact]
  public async Task GetReservationPdf_ReturnsPdf_WhenTokenIsValid()
  {
    var booking = new FakeBookingService
    {
      Quote = CreateQuote(1250m),
      ReservationDetail = new ReservacionDetailDto
      {
        Id = 49210,
        Cliente = "Cliente Web",
        CheckIn = new DateTime(2026, 6, 10),
        CheckOut = new DateTime(2026, 6, 12),
        Status = "PAGADA",
        RequiresCfdi = true,
        TotalSuites = 2500m,
        SubTotal = 2500m,
        Tax = 400m,
        TotalPrice = 2900m,
        Pagado = 2900m,
        PorPagar = 0m
      }
    };
    await using var app = await CreateAppAsync(bookingService: booking);
    var pdfTokenService = app.Services.GetRequiredService<IHospitalityReservationPdfTokenService>();
    var client = app.GetTestClient();

    var response = await client.GetAsync($"/api/hospitality/checkout/reservations/49210/pdf?token={pdfTokenService.CreateToken(49210)}");

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
    var bytes = await response.Content.ReadAsByteArrayAsync();
    Assert.True(bytes.AsSpan().StartsWith("%PDF"u8), "The API should return a PDF payload.");
  }

  private static async Task<WebApplication> CreateAppAsync(
    FakeBookingService? bookingService = null,
    FakePayPalClient? payPalClient = null,
    FakeConfirmationEmailSender? confirmationEmailSender = null,
    Action<HospitalityCheckoutOptions>? configureOptions = null)
  {
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions
    {
      EnvironmentName = Environments.Development
    });

    builder.WebHost.UseTestServer();
    builder.Services.AddRouting();
    builder.Services.AddDataProtection();
    builder.Services.Configure<HospitalityCheckoutOptions>(options =>
    {
      options.PdfTokenLifetimeMinutes = 30;
      configureOptions?.Invoke(options);
    });
    builder.Services.AddSingleton<IHospitalityQuoteTokenService, HospitalityQuoteTokenService>();
    builder.Services.AddSingleton<IHospitalityReservationPdfTokenService, HospitalityReservationPdfTokenService>();
    builder.Services.AddSingleton(CreatePresentation());
    builder.Services.AddSingleton<IHospitalityPublicBookingService>(bookingService ?? new FakeBookingService { Quote = CreateQuote(1250m) });
    builder.Services.AddSingleton<IHospitalityPayPalClient>(payPalClient ?? new FakePayPalClient());
    builder.Services.AddSingleton<IHospitalityReservationConfirmationEmailSender>(confirmationEmailSender ?? new FakeConfirmationEmailSender());
    builder.Services.AddSingleton<IReservacionPdfDocumentFactory, FakeReservacionPdfDocumentFactory>();
    builder.Services.AddSingleton<IReservacionPdfService, FakeReservacionPdfService>();

    var app = builder.Build();
    app.MapHospitalityCheckoutApi();
    await app.StartAsync();
    return app;
  }

  private static void AssertAnonymousRoute(IReadOnlyCollection<RouteEndpoint> endpoints, string routePattern)
  {
    var endpoint = Assert.Single(endpoints, item => item.RoutePattern.RawText == routePattern);
    Assert.NotNull(endpoint.Metadata.GetMetadata<IAllowAnonymous>());
  }

  private static HospitalityQuoteDto CreateQuote(decimal nightlyPrice)
    => HospitalityQuoteCalculator.BuildQuote(
      new HospitalityQuoteRequest
      {
        RoomName = "Suite Paris",
        CheckIn = new DateOnly(2026, 6, 10),
        CheckOut = new DateOnly(2026, 6, 12),
        Guests = 2
      },
      new HospitalityRoomAvailabilityDto
      {
        RoomId = 1,
        RoomName = "Suite Paris",
        Capacity = 2,
        Bedrooms = 1,
        BasePrice = nightlyPrice,
        Image = "/Images/Bonhomia/welcome-detail.png",
        Days =
        [
          new HospitalityDayAvailabilityDto { Date = new DateOnly(2026, 6, 10), IsAvailable = true, StateCode = "available", Price = nightlyPrice },
          new HospitalityDayAvailabilityDto { Date = new DateOnly(2026, 6, 11), IsAvailable = true, StateCode = "available", Price = nightlyPrice }
        ]
      },
      Array.Empty<HospitalityExtraOptionDto>(),
      Array.Empty<ExperienceCatalogItemDto>(),
      DateTimeOffset.UtcNow.AddMinutes(30),
      "MXN",
      60);

  private static string ExpectedPayPalRequestId(
    string prefix,
    string paymentAttemptId,
    HospitalityQuoteDto quote)
  {
    var safe = new string(paymentAttemptId
      .Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_')
      .ToArray());
    var material = Encoding.UTF8.GetBytes($"{prefix}|{quote.Fingerprint}|{safe}");
    var digest = Convert.ToHexString(SHA256.HashData(material)).ToLowerInvariant();
    return $"{prefix}-{digest[..32]}";
  }

  private static HospitalityCustomerInfo CreateCustomer()
    => new()
    {
      FullName = "Cliente Web",
      Email = "cliente@example.com",
      Phone = "7491103026"
    };

  private static PublicWebsitePresentationDefinition CreatePresentation()
  {
    var instance = PublicWebsiteInstancePolicy.Create(
      new PublicWebsiteInstanceOptions
      {
        PublicSiteKey = "hospitality-test",
        ExpectedCompanyRfc = "AAA010101AAA",
        SiteKey = "hospitality-test-site",
        ModuleCode = PlatformModuleCodes.Hospitality,
        CanonicalHost = "hospitality.example.test",
        LoopbackPort = 5010
      },
      PlatformModuleCodes.Hospitality);

    return PublicWebsitePresentationPolicy.Create(
      new PublicWebsitePresentationOptions
      {
        PublicSiteKey = instance.PublicSiteKey,
        BrandingVersion = 1,
        ContentVersion = 1,
        PublicName = "Hospedaje de prueba",
        ShortName = "Hospedaje",
        LegalName = "Hospedaje de Prueba, S.A. de C.V.",
        LocationName = "Ciudad de Prueba",
        Tagline = "Descanso de prueba.",
        FooterSummary = "Portal publico de prueba.",
        SeoDescription = "Portal publico de prueba para checkout.",
        Locale = "es-MX",
        PublicEmail = "reservas@example.test",
        WhatsAppE164 = "+525500000000",
        WhatsAppDisplay = "+52 55 0000 0000",
        OperatingAddress = "Domicilio operativo de prueba",
        FiscalAddress = "Domicilio fiscal de prueba",
        PrivacyEmail = "privacidad@example.test",
        PrivacyVersion = CurrentPrivacyVersion,
        PrivacyUpdatedDisplay = "2 de septiembre de 2026",
        TermsVersion = CurrentTermsVersion,
        TermsUpdatedDisplay = "2 de septiembre de 2026",
        PrimaryColor = "#123456",
        PrimaryDarkColor = "#102030",
        AccentColor = "#ABCDEF",
        Assets = new Dictionary<string, string>
        {
          ["logo"] = "/assets/logo.svg",
          ["favicon"] = "/assets/favicon.png"
        }
      },
      instance);
  }

  private sealed class FakeBookingService : IHospitalityPublicBookingService
  {
    public HospitalityQuoteDto? Quote { get; set; }
    public HospitalityPublicBookingException? CreateQuoteException { get; set; }
    public HospitalityPublicBookingException? CreatePaidReservationException { get; set; }
    public Exception? CreatePaidReservationUnexpectedException { get; set; }
    public HospitalityPaidReservationResult? PaidReservation { get; set; }
    public ReservacionDetailDto? ReservationDetail { get; set; }
    public int PaidReservationCount { get; private set; }
    public HospitalityCustomerInfo? LastCustomer { get; private set; }
    public HospitalityLegalAcceptance? LastLegalAcceptance { get; private set; }
    public HospitalityQuoteDto? LastPaidReservationQuote { get; private set; }

    public Task<HospitalityAvailabilityDto> GetAvailabilityAsync(DateOnly startDate, DateOnly endDateExclusive, CancellationToken ct = default)
      => Task.FromResult(new HospitalityAvailabilityDto());

    public Task<HospitalityQuoteDto> CreateQuoteAsync(HospitalityQuoteRequest request, CancellationToken ct = default)
    {
      if (CreateQuoteException is not null)
      {
        throw CreateQuoteException;
      }

      return Task.FromResult(Quote ?? CreateQuote(1250m));
    }

    public Task ValidateQuoteAvailabilityAsync(HospitalityQuoteDto quote, CancellationToken ct = default)
      => Task.CompletedTask;

    public Task<HospitalityPaidReservationResult> CreatePaidReservationAsync(
      HospitalityQuoteDto quote,
      HospitalityCustomerInfo customer,
      HospitalityPayPalCaptureResult payment,
      HospitalityLegalAcceptance legalAcceptance,
      CancellationToken ct = default)
    {
      if (CreatePaidReservationException is not null)
      {
        throw CreatePaidReservationException;
      }

      if (CreatePaidReservationUnexpectedException is not null)
      {
        throw CreatePaidReservationUnexpectedException;
      }

      HospitalityPayPalOrderPolicy.EnsureCaptureBelongsToQuote(payment, quote);
      PaidReservationCount++;
      LastPaidReservationQuote = quote;
      LastCustomer = customer;
      LastLegalAcceptance = legalAcceptance;
      return Task.FromResult(PaidReservation ?? new HospitalityPaidReservationResult
      {
        ReservationId = 1,
        TransaccionId = 2,
        ClientName = customer.FullName,
        Total = quote.Total,
        CreatedNewReservation = true
      });
    }

    public Task<ReservacionDetailDto?> GetReservationDetailAsync(int reservationId, CancellationToken ct = default)
      => Task.FromResult(ReservationDetail);
  }

  private sealed class FakePayPalClient : IHospitalityPayPalClient
  {
    public int CreateCount { get; private set; }
    public int CaptureCount { get; private set; }
    public string LastCreateIdempotencyKey { get; private set; } = string.Empty;
    public string LastCaptureIdempotencyKey { get; private set; } = string.Empty;
    public HospitalityPayPalCaptureResult? CaptureResult { get; set; }

    public Task<HospitalityPayPalOrderResult> CreateOrderAsync(HospitalityQuoteDto quote, string idempotencyKey, CancellationToken ct = default)
    {
      CreateCount++;
      LastCreateIdempotencyKey = idempotencyKey;
      return Task.FromResult(new HospitalityPayPalOrderResult { OrderId = "PAYPAL-1", Status = "CREATED" });
    }

    public Task<HospitalityPayPalCaptureResult> CaptureOrderAsync(
      string orderId,
      HospitalityQuoteDto quote,
      string idempotencyKey,
      CancellationToken ct = default)
    {
      CaptureCount++;
      LastCaptureIdempotencyKey = idempotencyKey;
      return Task.FromResult(CaptureResult ?? new HospitalityPayPalCaptureResult
      {
        OrderId = orderId,
        CustomId = quote.Fingerprint,
        ReferenceId = quote.QuoteId.ToString("N"),
        OrderStatus = "COMPLETED",
        CaptureId = "CAPTURE-1",
        Status = "COMPLETED",
        Amount = CreateQuote(1250m).Total,
        Currency = "MXN",
        PayerName = "Comprador PayPal",
        PayerEmail = "payer@example.com"
      });
    }
  }

  private sealed class FakeConfirmationEmailSender : IHospitalityReservationConfirmationEmailSender
  {
    public List<HospitalityReservationConfirmationEmail> Sent { get; } = new();
    public Exception? SendException { get; set; }

    public Task SendConfirmationAsync(
      HospitalityReservationConfirmationEmail confirmation,
      CancellationToken ct = default)
    {
      Sent.Add(confirmation);

      if (SendException is not null)
      {
        throw SendException;
      }

      return Task.CompletedTask;
    }
  }

  private sealed class FakeReservacionPdfDocumentFactory : IReservacionPdfDocumentFactory
  {
    public ReservacionPdfDocumentModel CreateFromDetail(ReservacionDetailDto detail)
      => CreateModel(detail.Id, detail.Cliente);

    public ReservacionPdfDocumentModel CreateFromSnapshot(ReservacionPdfSnapshot snapshot)
      => CreateModel(snapshot.ReservationId, snapshot.Cliente);

    private static ReservacionPdfDocumentModel CreateModel(int reservationId, string cliente)
      => new(
        reservationId,
        cliente,
        "PAGADA",
        "10/06/2026",
        "12/06/2026",
        "2",
        "Bonhomia Web",
        "Si",
        string.Empty,
        DateTime.Now.ToString("f"),
        "$2,500.00",
        string.Empty,
        string.Empty,
        "$0.00",
        "$2,500.00",
        "$400.00",
        "$0.00",
        "$2,900.00",
        "$2,900.00",
        "$0.00",
        Array.Empty<ReservacionPdfSuiteRow>(),
        Array.Empty<ReservacionPdfExtraRow>(),
        Array.Empty<ReservacionPdfPagoRow>(),
        Array.Empty<ReservacionPdfAttachmentRow>());
  }

  private sealed class FakeReservacionPdfService : IReservacionPdfService
  {
    private static readonly byte[] PdfBytes = Encoding.ASCII.GetBytes("%PDF-1.4\n%Fake Bonhomia\n");

    public byte[] Generate(ReservacionPdfDocumentModel model)
      => PdfBytes;
  }
}
