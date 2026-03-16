using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OrionERP.Application.Features.Reservaciones.ListaReservaciones;
using OrionERP.Application.Features.Reservaciones.OpenClaw;
using OrionERP.Web.Features.Reservaciones.ListaReservaciones;
using OrionERP.Web.Features.Reservaciones.OpenClaw;

namespace OrionERP.IntegrationTests.Reservaciones;

public class OpenClawReservationsApiTests
{
  [Fact]
  public async Task PostReservation_ReturnsUnauthorized_WhenApiKeyIsMissing()
  {
    await using var app = await CreateAppAsync(new FakeOpenClawReservationsService());
    var client = app.GetTestClient();

    var response = await client.PostAsJsonAsync("/api/openclaw/reservations", CreateSampleRequest());

    Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
  }

  [Fact]
  public async Task PostReservation_ReturnsReservationAndPdfUrl_WhenRequestSucceeds()
  {
    var fakeService = new FakeOpenClawReservationsService
    {
      CreateResult = new OpenClawReservationCreateResult
      {
        ReservationId = 23891,
        ClientName = "Jorge Contreras",
        CheckIn = new DateOnly(2026, 3, 18),
        CheckOut = new DateOnly(2026, 3, 20),
        Status = "NUEVA",
        Taxable = true,
        SuiteNames = new[] { "LONDON", "PARIS", "MOSCU" },
        Extras = new[]
        {
          OpenClawReservationLineFactory.CreateExtra("CAMASTRO", 2, 200m, null),
          OpenClawReservationLineFactory.CreateExtra("CHECK-IN ANTICIPADO", 1, 200m, "Checkin anticipado"),
          OpenClawReservationLineFactory.CreateDiscount("DESCUENTO", 4237.29m, 5m)
        },
        SuiteSubtotal = 4237.29m,
        ExtrasSubtotal = 290.10m,
        TotalPrice = 5251.77m
      }
    };

    await using var app = await CreateAppAsync(fakeService);
    var client = app.GetTestClient();
    client.DefaultRequestHeaders.Add("X-Orion-Api-Key", TestApiKey);

    var response = await client.PostAsJsonAsync("/api/openclaw/reservations", CreateSampleRequest());

    response.EnsureSuccessStatusCode();
    var payload = await response.Content.ReadFromJsonAsync<OpenClawReservationCreateResponse>();

    Assert.NotNull(payload);
    Assert.Equal(23891, payload!.ReservationId);
    Assert.Equal("Jorge Contreras", payload.ClientName);
    Assert.Equal(new DateOnly(2026, 3, 18), payload.CheckIn);
    Assert.Equal(new DateOnly(2026, 3, 20), payload.CheckOut);
    Assert.Equal("NUEVA", payload.Status);
    Assert.True(payload.Taxable);
    Assert.Equal(new[] { "LONDON", "PARIS", "MOSCU" }, payload.SuiteNames);
    Assert.Equal(3, payload.Extras.Count);
    Assert.Contains("/api/openclaw/reservations/23891/pdf?token=", payload.PdfUrl, StringComparison.Ordinal);
  }

  [Fact]
  public async Task PostReservation_UsesConfiguredPublicBaseUrl_WhenProvided()
  {
    var fakeService = new FakeOpenClawReservationsService
    {
      CreateResult = new OpenClawReservationCreateResult
      {
        ReservationId = 23891,
        ClientName = "Jorge Contreras",
        CheckIn = new DateOnly(2026, 3, 18),
        CheckOut = new DateOnly(2026, 3, 20),
        Status = "NUEVA",
        Taxable = true,
        SuiteNames = new[] { "LONDON", "PARIS", "MOSCU" },
        Extras = Array.Empty<OpenClawReservationCreatedExtra>(),
        SuiteSubtotal = 4237.29m,
        ExtrasSubtotal = 290.10m,
        TotalPrice = 5251.77m
      }
    };

    await using var app = await CreateAppAsync(fakeService, options =>
    {
      options.PublicBaseUrl = "https://OrionERP.Orion.land";
    });

    var client = app.GetTestClient();
    client.DefaultRequestHeaders.Add("X-Orion-Api-Key", TestApiKey);

    var response = await client.PostAsJsonAsync("/api/openclaw/reservations", CreateSampleRequest());

    response.EnsureSuccessStatusCode();
    var payload = await response.Content.ReadFromJsonAsync<OpenClawReservationCreateResponse>();

    Assert.NotNull(payload);
    Assert.StartsWith("https://orionerp.orion.land/api/openclaw/reservations/23891/pdf?token=", payload!.PdfUrl, StringComparison.Ordinal);
  }

  [Fact]
  public async Task PostReservation_ReturnsConflict_WhenServiceRejectsAvailability()
  {
    var fakeService = new FakeOpenClawReservationsService
    {
      CreateException = new OpenClawReservationConflictException("Las suites ya no están disponibles.")
    };

    await using var app = await CreateAppAsync(fakeService);
    var client = app.GetTestClient();
    client.DefaultRequestHeaders.Add("X-Orion-Api-Key", TestApiKey);

    var response = await client.PostAsJsonAsync("/api/openclaw/reservations", CreateSampleRequest());

    Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    var payload = await response.Content.ReadAsStringAsync();
    Assert.Contains("Las suites ya no están disponibles.", payload, StringComparison.Ordinal);
  }

  [Fact]
  public async Task GetPdf_ReturnsUnauthorized_WhenTokenIsInvalid()
  {
    await using var app = await CreateAppAsync(new FakeOpenClawReservationsService());
    var client = app.GetTestClient();

    var response = await client.GetAsync("/api/openclaw/reservations/23891/pdf?token=invalido");

    Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
  }

  [Fact]
  public async Task GetPdf_ReturnsPdf_WhenTokenIsValid()
  {
    var fakeService = new FakeOpenClawReservationsService
    {
      ReservationDetail = new ReservacionDetailDto
      {
        Id = 23891,
        Cliente = "Jorge Contreras",
        CheckIn = new DateTime(2026, 3, 18),
        CheckOut = new DateTime(2026, 3, 20),
        Status = "NUEVA",
        Taxable = true,
        TotalSuites = 4237.29m,
        TotalExtras = 290.10m,
        SubTotal = 4527.39m,
        Tax = 724.38m,
        TotalPrice = 5251.77m,
        Suites = new List<ReservacionSuiteDto>(),
        Extras = new List<ReservacionExtraDto>(),
        Pagos = new List<ReservacionPagoDto>(),
        Attachments = new List<ReservacionAttachmentDto>()
      }
    };

    await using var app = await CreateAppAsync(fakeService);
    var tokenService = app.Services.GetRequiredService<IOpenClawReservationPdfTokenService>();
    var client = app.GetTestClient();

    var response = await client.GetAsync($"/api/openclaw/reservations/23891/pdf?token={tokenService.CreateToken(23891)}");

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
    var bytes = await response.Content.ReadAsByteArrayAsync();
    Assert.True(bytes.AsSpan().StartsWith("%PDF"u8), "The API should return a valid PDF payload.");
  }

  private static OpenClawReservationCreateRequest CreateSampleRequest()
    => new()
    {
      ClientName = "Jorge Contreras",
      CheckIn = new DateOnly(2026, 3, 18),
      CheckOut = new DateOnly(2026, 3, 20),
      SuiteNames = new[] { "LONDON", "PARIS", "MOSCU" },
      GeneralDiscountPercent = 5m,
      Extras = new[]
      {
        new OpenClawReservationExtraRequest { CatalogName = "CAMASTRO", Quantity = 2 },
        new OpenClawReservationExtraRequest { CatalogName = "CHECK-IN ANTICIPADO", Quantity = 1, Notes = "Checkin anticipado" }
      }
    };

  private static async Task<WebApplication> CreateAppAsync(
    FakeOpenClawReservationsService fakeService,
    Action<OpenClawApiOptions>? configureOptions = null)
  {
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions
    {
      EnvironmentName = Environments.Development
    });

    builder.WebHost.UseTestServer();
    builder.Services.AddRouting();
    builder.Services.AddDataProtection();
    builder.Services.Configure<OpenClawApiOptions>(options =>
    {
      options.ApiKey = TestApiKey;
      options.PdfTokenLifetimeMinutes = 30;
      configureOptions?.Invoke(options);
    });
    builder.Services.AddSingleton<IOpenClawReservationsService>(fakeService);
    builder.Services.AddSingleton<IOpenClawReservationPdfTokenService, OpenClawReservationPdfTokenService>();
    builder.Services.AddSingleton<IReservacionPdfDocumentFactory, FakeReservacionPdfDocumentFactory>();
    builder.Services.AddSingleton<IReservacionPdfService, FakeReservacionPdfService>();

    var app = builder.Build();
    app.MapOpenClawReservationsApi();
    await app.StartAsync();
    return app;
  }

  private const string TestApiKey = "test-openclaw-api-key";

  private sealed class FakeOpenClawReservationsService : IOpenClawReservationsService
  {
    public OpenClawReservationCreateResult? CreateResult { get; set; }
    public Exception? CreateException { get; set; }
    public ReservacionDetailDto? ReservationDetail { get; set; }

    public Task<OpenClawReservationCreateResult> CreateReservationAsync(OpenClawReservationCreateRequest request, CancellationToken ct = default)
    {
      if (CreateException is not null)
      {
        throw CreateException;
      }

      return Task.FromResult(CreateResult ?? new OpenClawReservationCreateResult());
    }

    public Task<ReservacionDetailDto?> GetReservationDetailAsync(int reservationId, CancellationToken ct = default)
      => Task.FromResult(ReservationDetail);
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
        "NUEVA",
        "18/03/2026",
        "20/03/2026",
        "2",
        string.Empty,
        "Si",
        string.Empty,
        DateTime.Now.ToString("f"),
        "$0.00",
        "$0.00",
        "$0.00",
        "$0.00",
        "$0.00",
        "$0.00",
        "$0.00",
        "$0.00",
        Array.Empty<ReservacionPdfSuiteRow>(),
        Array.Empty<ReservacionPdfExtraRow>(),
        Array.Empty<ReservacionPdfPagoRow>(),
        Array.Empty<ReservacionPdfAttachmentRow>());
  }

  private sealed class FakeReservacionPdfService : IReservacionPdfService
  {
    private static readonly byte[] PdfBytes = Encoding.ASCII.GetBytes("%PDF-1.4\n%Fake\n");

    public byte[] Generate(ReservacionPdfDocumentModel model)
      => PdfBytes;
  }
}
