using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using OrionERP.Application.Features.Cfdi.Facturama;

namespace OrionERP.Infrastructure.Features.Cfdi.Facturama;

public sealed class FacturamaApiClient : IFacturamaApiClient
{
  private const string ProductionBaseUrl = "https://api.facturama.mx";
  private const string SandboxBaseUrl = "https://apisandbox.facturama.mx";
  private static readonly JsonSerializerOptions JsonOptions = new()
  {
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
  };

  private readonly HttpClient _httpClient;
  private readonly Uri _baseUri;
  private readonly AuthenticationHeaderValue _authHeader;

  public FacturamaApiClient(HttpClient httpClient, IConfiguration configuration)
  {
    _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    var settings = ResolveSettings(configuration ?? throw new ArgumentNullException(nameof(configuration)));
    var baseUrl = settings.BaseUrl;

    _baseUri = new Uri(baseUrl.EndsWith("/", StringComparison.Ordinal) ? baseUrl : $"{baseUrl}/", UriKind.Absolute);
    var user = settings.User;
    var password = settings.Password;

    var credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{user}:{password}"));
    _authHeader = new AuthenticationHeaderValue("Basic", credentials);
  }

  public Task<string> CreateIssuedCfdiAsync(FacturamaIssuedCfdiRequest request, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(request);

    var payload = JsonSerializer.Serialize(request, JsonOptions);
    return CreateIssuedCfdiAsync(payload, ct);
  }

  public async Task<string> CreateIssuedCfdiAsync(string jsonPayload, CancellationToken ct = default)
  {
    if (string.IsNullOrWhiteSpace(jsonPayload))
      throw new ArgumentException("El payload JSON de Facturama está vacío.", nameof(jsonPayload));

    using var request = new HttpRequestMessage(HttpMethod.Post, BuildUri("3/cfdis"))
    {
      Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json")
    };

    var body = await SendFacturamaAsync(request, "crear el CFDI emitido", ct);

    var cfdiId = TryGetObjectStringProperty(body, "Id");
    if (string.IsNullOrWhiteSpace(cfdiId))
      throw BuildUnexpectedResponseError(
          request,
          "crear el CFDI emitido",
          "Facturama respondió sin el identificador del CFDI emitido.",
          body);

    return cfdiId;
  }

  public async Task<FacturamaReceiverValidationResult> ValidateReceiverAsync(
      FacturamaReceiverValidationRequest request,
      CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(request);

    using var message = new HttpRequestMessage(HttpMethod.Post, BuildUri("customers/validate"))
    {
      Content = new StringContent(JsonSerializer.Serialize(request, JsonOptions), Encoding.UTF8, "application/json")
    };

    var body = await SendFacturamaAsync(message, $"validar el receptor RFC {request.Rfc}", ct);

    return DeserializeFacturamaResponse<FacturamaReceiverValidationResult>(
        body,
        message,
        $"validar el receptor RFC {request.Rfc}") ?? new FacturamaReceiverValidationResult();
  }

  public async Task<FacturamaTaxEntity> GetTaxEntityAsync(CancellationToken ct = default)
  {
    using var request = new HttpRequestMessage(HttpMethod.Get, BuildUri("api/TaxEntity"));

    var body = await SendFacturamaAsync(request, "consultar la entidad fiscal configurada", ct);

    return DeserializeFacturamaResponse<FacturamaTaxEntity>(
        body,
        request,
        "consultar la entidad fiscal configurada")
        ?? throw BuildUnexpectedResponseError(
            request,
            "consultar la entidad fiscal configurada",
            "Facturama respondió sin datos de la entidad fiscal configurada.",
            body);
  }

  public async Task<FacturamaDocumentContent> DownloadIssuedDocumentAsync(
      string cfdiId,
      FacturamaIssuedDocumentType documentType,
      CancellationToken ct = default)
  {
    if (string.IsNullOrWhiteSpace(cfdiId))
      throw new ArgumentException("El identificador del CFDI de Facturama es obligatorio.", nameof(cfdiId));

    var route = documentType == FacturamaIssuedDocumentType.Xml
        ? $"cfdi/xml/issued/{cfdiId}"
        : $"cfdi/pdf/issued/{cfdiId}";

    using var request = new HttpRequestMessage(HttpMethod.Get, BuildUri(route));

    var body = await SendFacturamaAsync(request, $"descargar el archivo {documentType} del CFDI {cfdiId}", ct);

    var base64 = TryGetObjectStringProperty(body, "Content");
    if (string.IsNullOrWhiteSpace(base64))
      throw BuildUnexpectedResponseError(
          request,
          $"descargar el archivo {documentType} del CFDI {cfdiId}",
          $"Facturama respondió sin contenido base64 para el archivo {documentType}.",
          body);

    return new FacturamaDocumentContent(
        documentType == FacturamaIssuedDocumentType.Xml ? "xml" : "pdf",
        Convert.FromBase64String(base64));
  }

  public async Task<string?> FindIssuedCfdiIdByUuidAsync(string uuid, CancellationToken ct = default)
  {
    if (string.IsNullOrWhiteSpace(uuid))
      throw new ArgumentException("El UUID es obligatorio.", nameof(uuid));

    var route = $"cfdi?type=issued&uuid={Uri.EscapeDataString(uuid)}";

    using var request = new HttpRequestMessage(HttpMethod.Get, BuildUri(route));

    var body = await SendFacturamaAsync(request, $"consultar el UUID {uuid}", ct);

    using var document = ParseFacturamaJsonDocument(body, request, $"consultar el UUID {uuid}");
    if (document.RootElement.ValueKind == JsonValueKind.Array &&
        document.RootElement.GetArrayLength() > 0 &&
        document.RootElement[0].TryGetProperty("Id", out var idElement))
    {
      return idElement.GetString();
    }

    if (document.RootElement.ValueKind == JsonValueKind.Object &&
        document.RootElement.TryGetProperty("Id", out var objectId))
    {
      return objectId.GetString();
    }

    return null;
  }

  public async Task CancelIssuedCfdiAsync(string cfdiId, string motive = "02", CancellationToken ct = default)
  {
    if (string.IsNullOrWhiteSpace(cfdiId))
      throw new ArgumentException("El identificador del CFDI es obligatorio.", nameof(cfdiId));

    var route = $"cfdi/{cfdiId}?type=issued&motive={Uri.EscapeDataString(motive)}";

    using var request = new HttpRequestMessage(HttpMethod.Delete, BuildUri(route));

    var body = await SendFacturamaAsync(request, $"cancelar el CFDI {cfdiId}", ct);

    var cancellation = TryDeserializeCancellationResult(body);
    if (IsRejectedCancellationStatus(cancellation?.Status))
    {
      var detail = BuildCancellationStatusDetail(cancellation, body);
      throw BuildUnexpectedResponseError(
          request,
          $"cancelar el CFDI {cfdiId}",
          $"Facturama no aceptó la cancelación del CFDI {cfdiId}. {detail}",
          body);
    }
  }

  private Uri BuildUri(string relativePath)
    => new(_baseUri, relativePath);

  private async Task<string> SendFacturamaAsync(
      HttpRequestMessage request,
      string operationDescription,
      CancellationToken ct)
  {
    request.Headers.Authorization = _authHeader;
    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

    try
    {
      using var response = await _httpClient.SendAsync(request, ct);
      var body = await response.Content.ReadAsStringAsync(ct);

      if (!response.IsSuccessStatusCode)
      {
        throw BuildHttpError(request, response, body, operationDescription);
      }

      return body;
    }
    catch (HttpRequestException ex)
    {
      throw new InvalidOperationException(BuildTransportErrorMessage(request, operationDescription, ex), ex);
    }
    catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
    {
      throw new InvalidOperationException(BuildTimeoutErrorMessage(request, operationDescription), ex);
    }
  }

  private static string? TryGetObjectStringProperty(string json, string propertyName)
  {
    try
    {
      using var document = JsonDocument.Parse(json);
      if (document.RootElement.ValueKind == JsonValueKind.Object &&
          document.RootElement.TryGetProperty(propertyName, out var property))
      {
        return property.GetString();
      }
    }
    catch (JsonException)
    {
      return null;
    }

    return null;
  }

  private T? DeserializeFacturamaResponse<T>(
      string body,
      HttpRequestMessage request,
      string operationDescription)
  {
    try
    {
      return JsonSerializer.Deserialize<T>(body, JsonOptions);
    }
    catch (JsonException ex)
    {
      throw BuildUnexpectedResponseError(
          request,
          operationDescription,
          $"Facturama devolvió JSON inválido o inesperado para {operationDescription}. {ex.Message}",
          body);
    }
  }

  private JsonDocument ParseFacturamaJsonDocument(
      string body,
      HttpRequestMessage request,
      string operationDescription)
  {
    try
    {
      return JsonDocument.Parse(body);
    }
    catch (JsonException ex)
    {
      throw BuildUnexpectedResponseError(
          request,
          operationDescription,
          $"Facturama devolvió JSON inválido o inesperado para {operationDescription}. {ex.Message}",
          body);
    }
  }

  private static FacturamaCancellationResult? TryDeserializeCancellationResult(string body)
  {
    if (string.IsNullOrWhiteSpace(body))
    {
      return null;
    }

    try
    {
      return JsonSerializer.Deserialize<FacturamaCancellationResult>(body, JsonOptions);
    }
    catch (JsonException)
    {
      return null;
    }
  }

  private static bool IsRejectedCancellationStatus(string? status)
    => string.Equals(status?.Trim(), "active", StringComparison.OrdinalIgnoreCase)
       || string.Equals(status?.Trim(), "rejected", StringComparison.OrdinalIgnoreCase);

  private static string BuildCancellationStatusDetail(FacturamaCancellationResult? cancellation, string body)
  {
    var parts = new List<string>();

    if (!string.IsNullOrWhiteSpace(cancellation?.Status))
    {
      parts.Add($"Estatus: {cancellation.Status.Trim()}");
    }

    if (!string.IsNullOrWhiteSpace(cancellation?.Message))
    {
      parts.Add(cancellation.Message.Trim());
    }

    if (parts.Count == 0)
    {
      parts.Add(FormatFacturamaErrorBody(body));
    }

    return string.Join(". ", parts);
  }

  private static FacturamaSettings ResolveSettings(IConfiguration configuration)
  {
    var configuredBaseUrl = configuration["Facturama:BaseUrl"];
    var configuredUser = configuration["Facturama:User"];
    var configuredPassword = configuration["Facturama:Password"];

    var useSandboxDefaults = ShouldUseSandboxDefaults(configuration, configuredBaseUrl);
    var defaultBaseUrl = useSandboxDefaults ? SandboxBaseUrl : ProductionBaseUrl;
    var user = configuredUser?.Trim();
    var password = configuredPassword;

    if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(password))
    {
      var effectiveHost = IsSandboxBaseUrl(configuredBaseUrl) || useSandboxDefaults
          ? new Uri(SandboxBaseUrl).Host
          : new Uri(defaultBaseUrl).Host;
      throw new InvalidOperationException(
          $"Faltan credenciales de Facturama para {effectiveHost}. " +
          "Configura Facturama:User y Facturama:Password " +
          "(por ejemplo, vía ASPNETCORE_Facturama__User y ASPNETCORE_Facturama__Password).");
    }

    return new FacturamaSettings(
        string.IsNullOrWhiteSpace(configuredBaseUrl) ? defaultBaseUrl : configuredBaseUrl,
        user,
        password);
  }

  private static bool ShouldUseSandboxDefaults(IConfiguration configuration, string? configuredBaseUrl)
  {
    if (IsSandboxBaseUrl(configuredBaseUrl))
    {
      return true;
    }

    var configuredUseSandbox = configuration["Facturama:UseSandboxDefaults"];
    if (bool.TryParse(configuredUseSandbox, out var useSandbox))
    {
      return useSandbox;
    }

    var environmentName = configuration["ENVIRONMENT"] ?? configuration["Environment"];
    return string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase);
  }

  private static bool IsSandboxBaseUrl(string? baseUrl)
  {
    if (string.IsNullOrWhiteSpace(baseUrl))
    {
      return false;
    }

    return Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
        ? uri.Host.Contains("sandbox", StringComparison.OrdinalIgnoreCase)
        : baseUrl.Contains("sandbox", StringComparison.OrdinalIgnoreCase);
  }

  private InvalidOperationException BuildHttpError(
      HttpRequestMessage request,
      HttpResponseMessage response,
      string body,
      string operationDescription)
  {
    var statusCode = response.StatusCode;
    var detail = FormatFacturamaErrorBody(body);
    var responseBody = FormatRawResponseBody(body);
    var reason = string.IsNullOrWhiteSpace(response.ReasonPhrase)
        ? statusCode.ToString()
        : response.ReasonPhrase;
    var endpoint = DescribeRequest(request);
    var message =
        $"Facturama ({_baseUri.Host}) devolvió {(int)statusCode} {reason} al {operationDescription}. " +
        $"Endpoint: {endpoint}. " +
        $"Detalle interpretado: {detail}. " +
        $"Respuesta cruda: {responseBody}.";

    if (statusCode == System.Net.HttpStatusCode.Unauthorized)
    {
      message += " Verifica las credenciales configuradas en Facturama:User y Facturama:Password.";
    }

    return new InvalidOperationException(message);
  }

  private InvalidOperationException BuildUnexpectedResponseError(
      HttpRequestMessage request,
      string operationDescription,
      string reason,
      string body)
  {
    var message =
        $"Facturama ({_baseUri.Host}) devolvió una respuesta inesperada al {operationDescription}. " +
        $"Endpoint: {DescribeRequest(request)}. " +
        $"Problema: {reason}. " +
        $"Detalle interpretado: {FormatFacturamaErrorBody(body)}. " +
        $"Respuesta cruda: {FormatRawResponseBody(body)}.";

    return new InvalidOperationException(message);
  }

  private string BuildTransportErrorMessage(
      HttpRequestMessage request,
      string operationDescription,
      HttpRequestException exception)
  {
    return
        $"No se pudo comunicar con Facturama ({_baseUri.Host}) al {operationDescription}. " +
        $"Endpoint: {DescribeRequest(request)}. " +
        $"Error de red: {exception.Message}. " +
        "Revisa conectividad, DNS, TLS/proxy/firewall y disponibilidad de Facturama.";
  }

  private string BuildTimeoutErrorMessage(
      HttpRequestMessage request,
      string operationDescription)
  {
    return
        $"Facturama ({_baseUri.Host}) no respondió a tiempo al {operationDescription}. " +
        $"Endpoint: {DescribeRequest(request)}. " +
        "Revisa la disponibilidad de Facturama, la conectividad de red y vuelve a intentar.";
  }

  private static string DescribeRequest(HttpRequestMessage request)
  {
    if (request.RequestUri is null)
    {
      return $"{request.Method.Method} <sin URI>";
    }

    var target = request.RequestUri.IsAbsoluteUri
        ? request.RequestUri.PathAndQuery
        : request.RequestUri.ToString();

    return $"{request.Method.Method} {target}";
  }

  private static string FormatFacturamaErrorBody(string body)
  {
    if (string.IsNullOrWhiteSpace(body))
    {
      return "Facturama no devolvió detalle adicional.";
    }

    try
    {
      using var document = JsonDocument.Parse(body);
      var root = document.RootElement;
      if (root.ValueKind != JsonValueKind.Object)
      {
        return body;
      }

      var parts = new List<string>();
      AddStringProperty(parts, root, "Message");
      AddStringProperty(parts, root, "ExceptionMessage");
      AddStringProperty(parts, root, "Error");
      AddStringProperty(parts, root, "error");
      AddStringProperty(parts, root, "Description");
      AddStringProperty(parts, root, "description");
      AddStringProperty(parts, root, "Detail");
      AddStringProperty(parts, root, "detail");
      AddStringProperty(parts, root, "Code");
      AddStringProperty(parts, root, "code");
      AddStringProperty(parts, root, "Status");
      AddStringProperty(parts, root, "status");

      if (root.TryGetProperty("ModelState", out var modelState) &&
          modelState.ValueKind == JsonValueKind.Object)
      {
        foreach (var property in modelState.EnumerateObject())
        {
          var fieldName = NormalizeFacturamaFieldName(property.Name);
          foreach (var error in EnumerateErrorMessages(property.Value))
          {
            parts.Add($"{fieldName}: {error}");
          }
        }
      }

      AddNestedErrors(parts, root, "Errors");
      AddNestedErrors(parts, root, "errors");

      return parts.Count == 0 ? body : string.Join(" | ", parts);
    }
    catch (JsonException)
    {
      return body;
    }
  }

  private static string FormatRawResponseBody(string body)
  {
    var raw = NormalizeWhitespace(body);
    if (string.IsNullOrWhiteSpace(raw))
    {
      return "<sin cuerpo>";
    }

    const int maxLength = 4000;
    return raw.Length <= maxLength ? raw : raw[..maxLength] + "... <truncado>";
  }

  private static string NormalizeWhitespace(string value)
  {
    if (string.IsNullOrWhiteSpace(value))
    {
      return string.Empty;
    }

    return string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
  }

  private static void AddStringProperty(List<string> parts, JsonElement root, string propertyName)
  {
    if (root.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.String)
    {
      var value = property.GetString();
      if (!string.IsNullOrWhiteSpace(value))
      {
        parts.Add(value.Trim());
      }
    }
  }

  private static IEnumerable<string> EnumerateErrorMessages(JsonElement element)
  {
    if (element.ValueKind == JsonValueKind.Array)
    {
      foreach (var item in element.EnumerateArray())
      {
        if (item.ValueKind == JsonValueKind.String)
        {
          var value = item.GetString();
          if (!string.IsNullOrWhiteSpace(value))
          {
            yield return value.Trim();
          }
        }
      }
    }
    else if (element.ValueKind == JsonValueKind.String)
    {
      var value = element.GetString();
      if (!string.IsNullOrWhiteSpace(value))
      {
        yield return value.Trim();
      }
    }
  }

  private static void AddNestedErrors(List<string> parts, JsonElement root, string propertyName)
  {
    if (!root.TryGetProperty(propertyName, out var errors))
    {
      return;
    }

    if (errors.ValueKind == JsonValueKind.Object)
    {
      foreach (var property in errors.EnumerateObject())
      {
        foreach (var error in EnumerateErrorMessages(property.Value))
        {
          parts.Add($"{NormalizeFacturamaFieldName(property.Name)}: {error}");
        }
      }
      return;
    }

    foreach (var error in EnumerateErrorMessages(errors))
    {
      parts.Add(error);
    }
  }

  private static string NormalizeFacturamaFieldName(string fieldName)
  {
    const string prefix = "cfdiToCreate.";
    return fieldName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
        ? fieldName[prefix.Length..]
        : fieldName;
  }

  private sealed record FacturamaSettings(string BaseUrl, string User, string Password);

  private sealed class FacturamaCancellationResult
  {
    [JsonPropertyName("Status")]
    public string? Status { get; set; }

    [JsonPropertyName("Message")]
    public string? Message { get; set; }
  }
}
