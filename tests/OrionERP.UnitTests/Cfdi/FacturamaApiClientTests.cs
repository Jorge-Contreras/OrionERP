using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Configuration;
using OrionERP.Application.Features.Cfdi.Facturama;
using OrionERP.Infrastructure.Features.Cfdi.Facturama;

namespace OrionERP.UnitTests.Cfdi;

public class FacturamaApiClientTests
{
  [Fact]
  public async Task CreateIssuedCfdiAsync_UsesSandboxDefaults_InDevelopment()
  {
    var handler = new RecordingHttpMessageHandler();
    var client = CreateClient(
        handler,
        new Dictionary<string, string?>
        {
          ["ENVIRONMENT"] = "Development"
        });

    var id = await client.CreateIssuedCfdiAsync("""{"test":true}""");

    Assert.Equal("sandbox-id", id);
    Assert.Equal(new Uri("https://apisandbox.facturama.mx/3/cfdis"), handler.LastRequest?.RequestUri);
    Assert.Equal(CreateBasicAuthHeader("jorgecontreras", "Orion2020"), handler.LastRequest?.Headers.Authorization);
  }

  [Fact]
  public async Task CreateIssuedCfdiAsync_UsesSandboxCredentials_WhenSandboxBaseUrlIsExplicitlyConfigured()
  {
    var handler = new RecordingHttpMessageHandler();
    var client = CreateClient(
        handler,
        new Dictionary<string, string?>
        {
          ["Facturama:BaseUrl"] = "https://apisandbox.facturama.mx"
        });

    var id = await client.CreateIssuedCfdiAsync("""{"test":true}""");

    Assert.Equal("sandbox-id", id);
    Assert.Equal(new Uri("https://apisandbox.facturama.mx/3/cfdis"), handler.LastRequest?.RequestUri);
    Assert.Equal(CreateBasicAuthHeader("jorgecontreras", "Orion2020"), handler.LastRequest?.Headers.Authorization);
  }

  [Fact]
  public async Task CreateIssuedCfdiAsync_HonorsExplicitCredentials_WhenProvided()
  {
    var handler = new RecordingHttpMessageHandler();
    var client = CreateClient(
        handler,
        new Dictionary<string, string?>
        {
          ["Facturama:BaseUrl"] = "https://apisandbox.facturama.mx",
          ["Facturama:User"] = "custom-user",
          ["Facturama:Password"] = "custom-password"
        });

    var id = await client.CreateIssuedCfdiAsync("""{"test":true}""");

    Assert.Equal("sandbox-id", id);
    Assert.Equal(new Uri("https://apisandbox.facturama.mx/3/cfdis"), handler.LastRequest?.RequestUri);
    Assert.Equal(CreateBasicAuthHeader("custom-user", "custom-password"), handler.LastRequest?.Headers.Authorization);
  }

  [Fact]
  public async Task CreateIssuedCfdiAsync_SerializesTypedPayload()
  {
    var handler = new RecordingHttpMessageHandler();
    var client = CreateClient(
        handler,
        new Dictionary<string, string?>
        {
          ["Facturama:BaseUrl"] = "https://apisandbox.facturama.mx"
        });

    await client.CreateIssuedCfdiAsync(new FacturamaIssuedCfdiRequest
    {
      Header = new FacturamaIssuedCfdiHeader
      {
        Folio = "123",
        Date = "2026-04-23T18:00",
        ExpeditionPlace = "90204",
        PaymentForm = "03",
        PaymentMethod = "PUE",
        TaxZipCode = "90204"
      },
      Receiver = new FacturamaReceiver
      {
        Rfc = "XAXX010101000",
        Name = "PUBLICO EN GENERAL",
        CfdiUse = "S01",
        FiscalRegime = "616",
        TaxZipCode = "90204"
      },
      Items = new[]
      {
        new FacturamaIssuedCfdiItem
        {
          ProductCode = "80131501",
          Description = "UNIDAD AL PUBLICO EN GENERAL",
          Unit = "Unidad de servicio",
          UnitCode = "E48",
          UnitPrice = 100m,
          Quantity = 1m,
          Subtotal = 100m,
          Discount = 0m,
          TaxObject = "02",
          Taxes = new[]
          {
            new FacturamaIssuedCfdiTax
            {
              Name = "IVA",
              Rate = 0.16m,
              Total = 16m,
              Base = 100m,
              IsRetention = false
            }
          },
          Total = 116m
        }
      }
    });

    Assert.Contains("\"Folio\":\"123\"", handler.LastRequestBody, StringComparison.Ordinal);
    Assert.DoesNotContain("\"Header\"", handler.LastRequestBody, StringComparison.Ordinal);
    Assert.Contains("\"Receiver\"", handler.LastRequestBody, StringComparison.Ordinal);
    Assert.Contains("\"Taxes\"", handler.LastRequestBody, StringComparison.Ordinal);
  }

  [Fact]
  public async Task CreateIssuedCfdiAsync_UsesProductionDefaults_WhenNoSandboxSignalExists()
  {
    var handler = new RecordingHttpMessageHandler();
    var client = CreateClient(
        handler,
        new Dictionary<string, string?>
        {
          ["ConnectionStrings:OrionDb"] = "Server=127.0.0.1,1433;Database=grupocarpio;User Id=sa;Password=secret;TrustServerCertificate=True;Encrypt=True;",
          ["Facturama:User"] = "prod-user",
          ["Facturama:Password"] = "prod-password"
        });

    var id = await client.CreateIssuedCfdiAsync("""{"test":true}""");

    Assert.Equal("sandbox-id", id);
    Assert.Equal(new Uri("https://api.facturama.mx/3/cfdis"), handler.LastRequest?.RequestUri);
    Assert.Equal(CreateBasicAuthHeader("prod-user", "prod-password"), handler.LastRequest?.Headers.Authorization);
  }

  [Fact]
  public async Task CreateIssuedCfdiAsync_DoesNotInferSandbox_FromSandboxNamedDatabaseAlone()
  {
    var handler = new RecordingHttpMessageHandler();
    var ex = Assert.Throws<InvalidOperationException>(() => CreateClient(
        handler,
        new Dictionary<string, string?>
        {
          ["ConnectionStrings:OrionDb"] = "Server=127.0.0.1,1433;Database=Orion_SandBox;User Id=sa;Password=secret;TrustServerCertificate=True;Encrypt=True;"
        }));

    Assert.Contains("Facturama:User", ex.Message, StringComparison.Ordinal);
    Assert.Contains("Facturama:Password", ex.Message, StringComparison.Ordinal);
  }

  [Fact]
  public async Task CreateIssuedCfdiAsync_ThrowsHelpfulMessage_WhenProductionReturnsUnauthorized()
  {
    var handler = new RecordingHttpMessageHandler(HttpStatusCode.Unauthorized, string.Empty);
    var client = CreateClient(
        handler,
        new Dictionary<string, string?>
        {
          ["Facturama:User"] = "prod-user",
          ["Facturama:Password"] = "prod-password"
        });

    var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.CreateIssuedCfdiAsync("""{"test":true}"""));

    Assert.Contains("devolvió 401", ex.Message, StringComparison.Ordinal);
    Assert.Contains("Facturama:User", ex.Message, StringComparison.Ordinal);
    Assert.Contains("Facturama:Password", ex.Message, StringComparison.Ordinal);
  }

  [Fact]
  public async Task CreateIssuedCfdiAsync_FlattensFacturamaModelStateErrors()
  {
    var handler = new RecordingHttpMessageHandler(
        HttpStatusCode.BadRequest,
        """
        {
          "Message":"La solicitud no es válida.",
          "ModelState":{
            "cfdiToCreate.ExpeditionPlace":["The ExpeditionPlace field is required."],
            "cfdiToCreate.PaymentForm":["PaymentForm debe existir"],
            "cfdiToCreate.Items":["Debe de contener conceptos"]
          }
        }
        """);
    var client = CreateClient(
        handler,
        new Dictionary<string, string?>
        {
          ["Facturama:BaseUrl"] = "https://apisandbox.facturama.mx"
        });

    var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.CreateIssuedCfdiAsync("""{"test":true}"""));

    Assert.Contains("Facturama (apisandbox.facturama.mx) devolvió 400", ex.Message, StringComparison.Ordinal);
    Assert.Contains("Endpoint: POST /3/cfdis", ex.Message, StringComparison.Ordinal);
    Assert.Contains("Detalle interpretado:", ex.Message, StringComparison.Ordinal);
    Assert.Contains("Respuesta cruda:", ex.Message, StringComparison.Ordinal);
    Assert.Contains("La solicitud no es válida.", ex.Message, StringComparison.Ordinal);
    Assert.Contains("ExpeditionPlace: The ExpeditionPlace field is required.", ex.Message, StringComparison.Ordinal);
    Assert.Contains("PaymentForm: PaymentForm debe existir", ex.Message, StringComparison.Ordinal);
    Assert.Contains("Items: Debe de contener conceptos", ex.Message, StringComparison.Ordinal);
  }

  [Fact]
  public async Task CreateIssuedCfdiAsync_IncludesVerboseContext_WhenResponseHasNoCfdiId()
  {
    var handler = new RecordingHttpMessageHandler(
        HttpStatusCode.Created,
        """{"Message":"Timbrado aceptado, pero sin identificador en la respuesta."}""");
    var client = CreateClient(
        handler,
        new Dictionary<string, string?>
        {
          ["Facturama:BaseUrl"] = "https://apisandbox.facturama.mx"
        });

    var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.CreateIssuedCfdiAsync("""{"test":true}"""));

    Assert.Contains("respuesta inesperada", ex.Message, StringComparison.Ordinal);
    Assert.Contains("Endpoint: POST /3/cfdis", ex.Message, StringComparison.Ordinal);
    Assert.Contains("sin el identificador", ex.Message, StringComparison.Ordinal);
    Assert.Contains("Timbrado aceptado", ex.Message, StringComparison.Ordinal);
    Assert.Contains("Respuesta cruda:", ex.Message, StringComparison.Ordinal);
  }

  [Fact]
  public async Task ValidateReceiverAsync_CallsValidationEndpoint()
  {
    var handler = new RecordingHttpMessageHandler(
        HttpStatusCode.OK,
        """{"ExistRfc":true,"MatchName":true,"MatchZipCode":true,"MatchFiscalRegime":true,"IsValid":true}""");
    var client = CreateClient(
        handler,
        new Dictionary<string, string?>
        {
          ["Facturama:BaseUrl"] = "https://apisandbox.facturama.mx"
        });

    var result = await client.ValidateReceiverAsync(new FacturamaReceiverValidationRequest
    {
      Rfc = "AAA010101AAA",
      Name = "CLIENTE DEMO",
      CfdiUse = "G03",
      FiscalRegime = "601",
      TaxZipCode = "90204"
    });

    Assert.True(result.IsValid);
    Assert.Equal(new Uri("https://apisandbox.facturama.mx/customers/validate"), handler.LastRequest?.RequestUri);
    Assert.Equal(HttpMethod.Post, handler.LastRequest?.Method);
    Assert.Contains("\"ZipCode\":\"90204\"", handler.LastRequestBody, StringComparison.Ordinal);
    Assert.DoesNotContain("\"TaxZipCode\"", handler.LastRequestBody, StringComparison.Ordinal);
  }

  [Fact]
  public async Task GetTaxEntityAsync_CallsTaxEntityEndpoint()
  {
    var handler = new RecordingHttpMessageHandler(
        HttpStatusCode.OK,
        """{"Rfc":"OHM191112Q26","TaxAddress":{"ZipCode":"90204"}}""");
    var client = CreateClient(
        handler,
        new Dictionary<string, string?>
        {
          ["Facturama:BaseUrl"] = "https://apisandbox.facturama.mx"
        });

    var result = await client.GetTaxEntityAsync();

    Assert.Equal("OHM191112Q26", result.Rfc);
    Assert.Equal("90204", result.TaxAddress?.ZipCode);
    Assert.Equal(new Uri("https://apisandbox.facturama.mx/api/TaxEntity"), handler.LastRequest?.RequestUri);
    Assert.Equal(HttpMethod.Get, handler.LastRequest?.Method);
  }

  [Fact]
  public async Task CancelIssuedCfdiAsync_CallsIssuedCancelEndpoint()
  {
    var handler = new RecordingHttpMessageHandler(
        HttpStatusCode.OK,
        """{"Status":"canceled"}""");
    var client = CreateClient(
        handler,
        new Dictionary<string, string?>
        {
          ["Facturama:BaseUrl"] = "https://apisandbox.facturama.mx"
        });

    await client.CancelIssuedCfdiAsync("sandbox-cfdi-id");

    Assert.Equal(new Uri("https://apisandbox.facturama.mx/cfdi/sandbox-cfdi-id?type=issued&motive=02"), handler.LastRequest?.RequestUri);
    Assert.Equal(HttpMethod.Delete, handler.LastRequest?.Method);
  }

  [Fact]
  public async Task CancelIssuedCfdiAsync_Throws_WhenFacturamaLeavesCfdiActive()
  {
    var handler = new RecordingHttpMessageHandler(
        HttpStatusCode.OK,
        """{"Status":"active","Message":"El comprobante tiene documentos relacionados."}""");
    var client = CreateClient(
        handler,
        new Dictionary<string, string?>
        {
          ["Facturama:BaseUrl"] = "https://apisandbox.facturama.mx"
        });

    var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.CancelIssuedCfdiAsync("sandbox-cfdi-id"));

    Assert.Contains("no aceptó la cancelación", ex.Message, StringComparison.Ordinal);
    Assert.Contains("Endpoint: DELETE /cfdi/sandbox-cfdi-id?type=issued&motive=02", ex.Message, StringComparison.Ordinal);
    Assert.Contains("Estatus: active", ex.Message, StringComparison.Ordinal);
    Assert.Contains("documentos relacionados", ex.Message, StringComparison.Ordinal);
    Assert.Contains("Respuesta cruda:", ex.Message, StringComparison.Ordinal);
  }

  [Fact]
  public async Task ValidateReceiverAsync_IncludesVerboseContext_WhenTransportFails()
  {
    var handler = new ThrowingHttpMessageHandler(new HttpRequestException("DNS lookup failed"));
    var client = CreateClient(
        handler,
        new Dictionary<string, string?>
        {
          ["Facturama:BaseUrl"] = "https://apisandbox.facturama.mx"
        });

    var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ValidateReceiverAsync(new FacturamaReceiverValidationRequest
    {
      Rfc = "AAA010101AAA",
      Name = "CLIENTE DEMO",
      CfdiUse = "G03",
      FiscalRegime = "601",
      TaxZipCode = "90204"
    }));

    Assert.Contains("No se pudo comunicar con Facturama", ex.Message, StringComparison.Ordinal);
    Assert.Contains("Endpoint: POST /customers/validate", ex.Message, StringComparison.Ordinal);
    Assert.Contains("DNS lookup failed", ex.Message, StringComparison.Ordinal);
    Assert.Contains("conectividad", ex.Message, StringComparison.Ordinal);
  }

  private static FacturamaApiClient CreateClient(HttpMessageHandler handler, IDictionary<string, string?> values)
  {
    var configuration = new ConfigurationBuilder()
        .AddInMemoryCollection(values)
        .Build();

    return new FacturamaApiClient(new HttpClient(handler), configuration);
  }

  private static AuthenticationHeaderValue CreateBasicAuthHeader(string user, string password)
  {
    var token = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{user}:{password}"));
    return new AuthenticationHeaderValue("Basic", token);
  }

  private sealed class RecordingHttpMessageHandler : HttpMessageHandler
  {
    private readonly HttpStatusCode _statusCode;
    private readonly string _body;

    public RecordingHttpMessageHandler(HttpStatusCode statusCode = HttpStatusCode.Created, string body = """{"Id":"sandbox-id"}""")
    {
      _statusCode = statusCode;
      _body = body;
    }

    public HttpRequestMessage? LastRequest { get; private set; }
    public string LastRequestBody { get; private set; } = string.Empty;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
      LastRequest = request;
      LastRequestBody = request.Content is null
        ? string.Empty
        : await request.Content.ReadAsStringAsync(cancellationToken);

      return new HttpResponseMessage(_statusCode)
      {
        Content = new StringContent(_body, Encoding.UTF8, "application/json")
      };
    }
  }

  private sealed class ThrowingHttpMessageHandler : HttpMessageHandler
  {
    private readonly Exception _exception;

    public ThrowingHttpMessageHandler(Exception exception)
    {
      _exception = exception;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
      => Task.FromException<HttpResponseMessage>(_exception);
  }
}
