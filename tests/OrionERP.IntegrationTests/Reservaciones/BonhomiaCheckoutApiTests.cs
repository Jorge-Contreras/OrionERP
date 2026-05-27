using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OrionERP.Application.Features.Bonhomia.PublicBooking;
using OrionERP.Application.Features.Reservaciones.ListaReservaciones;
using OrionERP.Web.Features.Bonhomia.Checkout;
using OrionERP.Web.Features.Reservaciones.ListaReservaciones;

namespace OrionERP.IntegrationTests.Reservaciones;

public class BonhomiaCheckoutApiTests
{
  [Fact]
  public async Task CreateOrder_RejectsMissingQuoteToken()
  {
    await using var app = await CreateAppAsync();
    var client = app.GetTestClient();

    var response = await client.PostAsJsonAsync("/api/bonhomia/checkout/orders", new BonhomiaCreatePayPalOrderRequest());

    Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
  }

  [Fact]
  public async Task CreateOrder_ReturnsConflict_WhenLiveQuoteChanged()
  {
    var original = CreateQuote(1250m);
    var changed = CreateQuote(1300m);
    var booking = new FakeBookingService { Quote = changed };
    await using var app = await CreateAppAsync(bookingService: booking);
    var tokenService = app.Services.GetRequiredService<IBonhomiaQuoteTokenService>();
    var client = app.GetTestClient();

    var response = await client.PostAsJsonAsync("/api/bonhomia/checkout/orders", new BonhomiaCreatePayPalOrderRequest
    {
      QuoteToken = tokenService.CreateToken(original),
      QuoteFingerprint = original.Fingerprint
    });

    Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
  }

  [Fact]
  public async Task CreateOrder_UsesPaymentAttemptId_ForPayPalIdempotency()
  {
    var quote = CreateQuote(1250m);
    var payPal = new FakePayPalClient();
    await using var app = await CreateAppAsync(payPalClient: payPal);
    var tokenService = app.Services.GetRequiredService<IBonhomiaQuoteTokenService>();
    var client = app.GetTestClient();

    var response = await client.PostAsJsonAsync("/api/bonhomia/checkout/orders", new BonhomiaCreatePayPalOrderRequest
    {
      QuoteToken = tokenService.CreateToken(quote),
      QuoteFingerprint = quote.Fingerprint,
      PaymentAttemptId = "attempt-abc-123"
    });

    response.EnsureSuccessStatusCode();
    Assert.Equal("ord-attempt-abc-123", payPal.LastCreateIdempotencyKey);
  }

  [Fact]
  public async Task ConfirmOrder_DoesNotCapture_WhenAvailabilityDisappears()
  {
    var quote = CreateQuote(1250m);
    var booking = new FakeBookingService
    {
      CreateQuoteException = new BonhomiaPublicBookingException("not_available", "No disponible.")
    };
    var payPal = new FakePayPalClient();
    await using var app = await CreateAppAsync(booking, payPal);
    var tokenService = app.Services.GetRequiredService<IBonhomiaQuoteTokenService>();
    var client = app.GetTestClient();

    var response = await client.PostAsJsonAsync("/api/bonhomia/checkout/orders/PAYPAL-1", new BonhomiaConfirmPayPalOrderRequest
    {
      QuoteToken = tokenService.CreateToken(quote),
      QuoteFingerprint = quote.Fingerprint,
      PaymentAttemptId = "attempt-confirm-123",
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
      PaidReservation = new BonhomiaPaidReservationResult
      {
        ReservationId = 49210,
        TransaccionId = 8821,
        ClientName = "Cliente Web",
        Total = quote.Total
      }
    };
    var payPal = new FakePayPalClient();
    await using var app = await CreateAppAsync(booking, payPal);
    var tokenService = app.Services.GetRequiredService<IBonhomiaQuoteTokenService>();
    var client = app.GetTestClient();

    var response = await client.PostAsJsonAsync("/api/bonhomia/checkout/orders/PAYPAL-1", new BonhomiaConfirmPayPalOrderRequest
    {
      QuoteToken = tokenService.CreateToken(quote),
      QuoteFingerprint = quote.Fingerprint,
      PaymentAttemptId = "attempt-confirm-123",
      Customer = CreateCustomer()
    });

    response.EnsureSuccessStatusCode();
    var payload = await response.Content.ReadFromJsonAsync<BonhomiaConfirmPayPalOrderResponse>();

    Assert.NotNull(payload);
    Assert.Equal(49210, payload!.ReservationId);
    Assert.Equal(8821, payload.TransaccionId);
    Assert.Equal("Cliente Web", payload.ClientName);
    Assert.Equal("Suite Paris", payload.RoomName);
    Assert.Equal(new DateOnly(2026, 6, 10), payload.CheckIn);
    Assert.Equal(new DateOnly(2026, 6, 12), payload.CheckOut);
    Assert.Equal(2, payload.Nights);
    Assert.Equal(2, payload.Guests);
    Assert.Equal("PAYPAL-1", payload.PayPalOrderId);
    Assert.Equal("CAPTURE-1", payload.PayPalCaptureId);
    Assert.Equal("COMPLETED", payload.PayPalStatus);
    Assert.Equal("payer@example.com", payload.PayPalPayerEmail);
    Assert.Contains("/api/bonhomia/checkout/reservations/49210/pdf?token=", payload.PdfUrl, StringComparison.Ordinal);
    Assert.Equal(1, payPal.CaptureCount);
    Assert.Equal("cap-attempt-confirm-123", payPal.LastCaptureIdempotencyKey);
    Assert.Equal(1, booking.PaidReservationCount);
  }

  [Fact]
  public async Task ConfirmOrder_UsesConfiguredPublicBaseUrl_ForReservationPdf()
  {
    var quote = CreateQuote(1250m);
    var booking = new FakeBookingService
    {
      Quote = quote,
      PaidReservation = new BonhomiaPaidReservationResult
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
    var tokenService = app.Services.GetRequiredService<IBonhomiaQuoteTokenService>();
    var client = app.GetTestClient();

    var response = await client.PostAsJsonAsync("/api/bonhomia/checkout/orders/PAYPAL-1", new BonhomiaConfirmPayPalOrderRequest
    {
      QuoteToken = tokenService.CreateToken(quote),
      QuoteFingerprint = quote.Fingerprint,
      PaymentAttemptId = "attempt-confirm-123",
      Customer = CreateCustomer()
    });

    response.EnsureSuccessStatusCode();
    var payload = await response.Content.ReadFromJsonAsync<BonhomiaConfirmPayPalOrderResponse>();

    Assert.NotNull(payload);
    Assert.StartsWith("https://bonhomia.orion.land/api/bonhomia/checkout/reservations/49210/pdf?token=", payload!.PdfUrl, StringComparison.Ordinal);
  }

  [Fact]
  public async Task ConfirmOrder_ReturnsPendingPaymentProblem_WhenPayPalCaptureIsPending()
  {
    var quote = CreateQuote(1250m);
    var booking = new FakeBookingService
    {
      Quote = quote,
      CreatePaidReservationException = new BonhomiaPublicBookingException(
        "payment_not_completed",
        "PayPal devolvio el cobro en estado PENDING (RECEIVING_PREFERENCE_MANDATES_MANUAL_ACTION). Orden: COMPLETED. No se creo la reservacion porque PayPal aun no acredita el pago.")
    };
    var payPal = new FakePayPalClient
    {
      CaptureResult = new BonhomiaPayPalCaptureResult
      {
        OrderId = "PAYPAL-PENDING",
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
    var tokenService = app.Services.GetRequiredService<IBonhomiaQuoteTokenService>();
    var client = app.GetTestClient();

    var response = await client.PostAsJsonAsync("/api/bonhomia/checkout/orders/PAYPAL-PENDING", new BonhomiaConfirmPayPalOrderRequest
    {
      QuoteToken = tokenService.CreateToken(quote),
      QuoteFingerprint = quote.Fingerprint,
      PaymentAttemptId = "attempt-pending-123",
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
    var tokenService = app.Services.GetRequiredService<IBonhomiaQuoteTokenService>();
    var client = app.GetTestClient();

    var response = await client.PostAsJsonAsync("/api/bonhomia/checkout/orders/PAYPAL-RECOVERY", new BonhomiaConfirmPayPalOrderRequest
    {
      QuoteToken = tokenService.CreateToken(quote),
      QuoteFingerprint = quote.Fingerprint,
      PaymentAttemptId = "attempt-recovery-123",
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

    var response = await client.GetAsync("/api/bonhomia/checkout/reservations/49210/pdf?token=invalido");

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
    var pdfTokenService = app.Services.GetRequiredService<IBonhomiaReservationPdfTokenService>();
    var client = app.GetTestClient();

    var response = await client.GetAsync($"/api/bonhomia/checkout/reservations/49210/pdf?token={pdfTokenService.CreateToken(49210)}");

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
    var bytes = await response.Content.ReadAsByteArrayAsync();
    Assert.True(bytes.AsSpan().StartsWith("%PDF"u8), "The API should return a PDF payload.");
  }

  private static async Task<WebApplication> CreateAppAsync(
    FakeBookingService? bookingService = null,
    FakePayPalClient? payPalClient = null,
    Action<BonhomiaCheckoutOptions>? configureOptions = null)
  {
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions
    {
      EnvironmentName = Environments.Development
    });

    builder.WebHost.UseTestServer();
    builder.Services.AddRouting();
    builder.Services.AddDataProtection();
    builder.Services.Configure<BonhomiaCheckoutOptions>(options =>
    {
      options.PdfTokenLifetimeMinutes = 30;
      configureOptions?.Invoke(options);
    });
    builder.Services.AddSingleton<IBonhomiaQuoteTokenService, BonhomiaQuoteTokenService>();
    builder.Services.AddSingleton<IBonhomiaReservationPdfTokenService, BonhomiaReservationPdfTokenService>();
    builder.Services.AddSingleton<IBonhomiaPublicBookingService>(bookingService ?? new FakeBookingService { Quote = CreateQuote(1250m) });
    builder.Services.AddSingleton<IBonhomiaPayPalClient>(payPalClient ?? new FakePayPalClient());
    builder.Services.AddSingleton<IReservacionPdfDocumentFactory, FakeReservacionPdfDocumentFactory>();
    builder.Services.AddSingleton<IReservacionPdfService, FakeReservacionPdfService>();

    var app = builder.Build();
    app.MapBonhomiaCheckoutApi();
    await app.StartAsync();
    return app;
  }

  private static BonhomiaQuoteDto CreateQuote(decimal nightlyPrice)
    => BonhomiaQuoteCalculator.BuildQuote(
      new BonhomiaQuoteRequest
      {
        RoomName = "Suite Paris",
        CheckIn = new DateOnly(2026, 6, 10),
        CheckOut = new DateOnly(2026, 6, 12),
        Guests = 2
      },
      new BonhomiaRoomAvailabilityDto
      {
        RoomId = 1,
        RoomName = "Suite Paris",
        Capacity = 2,
        Bedrooms = 1,
        BasePrice = nightlyPrice,
        Image = "/Images/Bonhomia/welcome-detail.png",
        Days =
        [
          new BonhomiaDayAvailabilityDto { Date = new DateOnly(2026, 6, 10), IsAvailable = true, StateCode = "available", Price = nightlyPrice },
          new BonhomiaDayAvailabilityDto { Date = new DateOnly(2026, 6, 11), IsAvailable = true, StateCode = "available", Price = nightlyPrice }
        ]
      },
      Array.Empty<BonhomiaExtraOptionDto>(),
      DateTimeOffset.UtcNow.AddMinutes(30),
      "MXN",
      60);

  private static BonhomiaCustomerInfo CreateCustomer()
    => new()
    {
      FullName = "Cliente Web",
      Email = "cliente@example.com",
      Phone = "7491103026"
    };

  private sealed class FakeBookingService : IBonhomiaPublicBookingService
  {
    public BonhomiaQuoteDto? Quote { get; set; }
    public BonhomiaPublicBookingException? CreateQuoteException { get; set; }
    public BonhomiaPublicBookingException? CreatePaidReservationException { get; set; }
    public Exception? CreatePaidReservationUnexpectedException { get; set; }
    public BonhomiaPaidReservationResult? PaidReservation { get; set; }
    public ReservacionDetailDto? ReservationDetail { get; set; }
    public int PaidReservationCount { get; private set; }

    public Task<BonhomiaAvailabilityDto> GetAvailabilityAsync(DateOnly startDate, DateOnly endDateExclusive, CancellationToken ct = default)
      => Task.FromResult(new BonhomiaAvailabilityDto());

    public Task<BonhomiaQuoteDto> CreateQuoteAsync(BonhomiaQuoteRequest request, CancellationToken ct = default)
    {
      if (CreateQuoteException is not null)
      {
        throw CreateQuoteException;
      }

      return Task.FromResult(Quote ?? CreateQuote(1250m));
    }

    public Task ValidateQuoteAvailabilityAsync(BonhomiaQuoteDto quote, CancellationToken ct = default)
      => Task.CompletedTask;

    public Task<BonhomiaPaidReservationResult> CreatePaidReservationAsync(
      BonhomiaQuoteDto quote,
      BonhomiaCustomerInfo customer,
      BonhomiaPayPalCaptureResult payment,
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

      PaidReservationCount++;
      return Task.FromResult(PaidReservation ?? new BonhomiaPaidReservationResult
      {
        ReservationId = 1,
        TransaccionId = 2,
        ClientName = customer.FullName,
        Total = quote.Total
      });
    }

    public Task<ReservacionDetailDto?> GetReservationDetailAsync(int reservationId, CancellationToken ct = default)
      => Task.FromResult(ReservationDetail);
  }

  private sealed class FakePayPalClient : IBonhomiaPayPalClient
  {
    public int CaptureCount { get; private set; }
    public string LastCreateIdempotencyKey { get; private set; } = string.Empty;
    public string LastCaptureIdempotencyKey { get; private set; } = string.Empty;
    public BonhomiaPayPalCaptureResult? CaptureResult { get; set; }

    public Task<BonhomiaPayPalOrderResult> CreateOrderAsync(BonhomiaQuoteDto quote, string idempotencyKey, CancellationToken ct = default)
    {
      LastCreateIdempotencyKey = idempotencyKey;
      return Task.FromResult(new BonhomiaPayPalOrderResult { OrderId = "PAYPAL-1", Status = "CREATED" });
    }

    public Task<BonhomiaPayPalCaptureResult> CaptureOrderAsync(string orderId, string idempotencyKey, CancellationToken ct = default)
    {
      CaptureCount++;
      LastCaptureIdempotencyKey = idempotencyKey;
      return Task.FromResult(CaptureResult ?? new BonhomiaPayPalCaptureResult
      {
        OrderId = orderId,
        OrderStatus = "COMPLETED",
        CaptureId = "CAPTURE-1",
        Status = "COMPLETED",
        Amount = CreateQuote(1250m).Total,
        Currency = "MXN",
        PayerEmail = "payer@example.com"
      });
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
