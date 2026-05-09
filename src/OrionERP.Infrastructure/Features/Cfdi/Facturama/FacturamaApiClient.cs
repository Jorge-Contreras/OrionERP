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
  private const string SandboxUser = "jorgecontreras";
  private const string SandboxPassword = "Orion2020";
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

    request.Headers.Authorization = _authHeader;
    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

    using var response = await _httpClient.SendAsync(request, ct);
    var body = await response.Content.ReadAsStringAsync(ct);

    if (!response.IsSuccessStatusCode)
      throw BuildHttpError(response.StatusCode, body);

    var cfdiId = TryGetObjectStringProperty(body, "Id");
    if (string.IsNullOrWhiteSpace(cfdiId))
      throw new InvalidOperationException("Facturama respondió sin el identificador del CFDI emitido.");

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

    message.Headers.Authorization = _authHeader;
    message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

    using var response = await _httpClient.SendAsync(message, ct);
    var body = await response.Content.ReadAsStringAsync(ct);

    if (!response.IsSuccessStatusCode)
      throw BuildHttpError(
          response.StatusCode,
          body,
          $"Facturama ({_baseUri.Host}) no pudo validar el receptor RFC {request.Rfc}.");

    return JsonSerializer.Deserialize<FacturamaReceiverValidationResult>(body, JsonOptions)
        ?? new FacturamaReceiverValidationResult();
  }

  public async Task<FacturamaTaxEntity> GetTaxEntityAsync(CancellationToken ct = default)
  {
    using var request = new HttpRequestMessage(HttpMethod.Get, BuildUri("api/TaxEntity"));
    request.Headers.Authorization = _authHeader;
    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

    using var response = await _httpClient.SendAsync(request, ct);
    var body = await response.Content.ReadAsStringAsync(ct);

    if (!response.IsSuccessStatusCode)
      throw BuildHttpError(
          response.StatusCode,
          body,
          $"Facturama ({_baseUri.Host}) no pudo consultar la entidad fiscal configurada.");

    return JsonSerializer.Deserialize<FacturamaTaxEntity>(body, JsonOptions)
        ?? throw new InvalidOperationException("Facturama respondió sin datos de la entidad fiscal configurada.");
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
    request.Headers.Authorization = _authHeader;
    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

    using var response = await _httpClient.SendAsync(request, ct);
    var body = await response.Content.ReadAsStringAsync(ct);

    if (!response.IsSuccessStatusCode)
      throw BuildHttpError(
          response.StatusCode,
          body,
          $"Facturama ({_baseUri.Host}) no pudo descargar el archivo {documentType} del CFDI {cfdiId}.");

    var base64 = TryGetObjectStringProperty(body, "Content");
    if (string.IsNullOrWhiteSpace(base64))
      throw new InvalidOperationException($"Facturama respondió sin contenido base64 para el archivo {documentType}.");

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
    request.Headers.Authorization = _authHeader;
    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

    using var response = await _httpClient.SendAsync(request, ct);
    var body = await response.Content.ReadAsStringAsync(ct);

    if (!response.IsSuccessStatusCode)
      throw BuildHttpError(
          response.StatusCode,
          body,
          $"Facturama ({_baseUri.Host}) devolvió {(int)response.StatusCode} al consultar el UUID {uuid}.");

    using var document = JsonDocument.Parse(body);
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
    request.Headers.Authorization = _authHeader;
    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

    using var response = await _httpClient.SendAsync(request, ct);
    var body = await response.Content.ReadAsStringAsync(ct);

    if (!response.IsSuccessStatusCode)
      throw BuildHttpError(
          response.StatusCode,
          body,
          $"Facturama ({_baseUri.Host}) devolvió {(int)response.StatusCode} al cancelar el CFDI {cfdiId}.");
  }

  private Uri BuildUri(string relativePath)
    => new(_baseUri, relativePath);

  private static string? TryGetObjectStringProperty(string json, string propertyName)
  {
    using var document = JsonDocument.Parse(json);
    if (document.RootElement.ValueKind == JsonValueKind.Object &&
        document.RootElement.TryGetProperty(propertyName, out var property))
    {
      return property.GetString();
    }

    return null;
  }

  private static FacturamaSettings ResolveSettings(IConfiguration configuration)
  {
    var configuredBaseUrl = configuration["Facturama:BaseUrl"];
    var configuredUser = configuration["Facturama:User"];
    var configuredPassword = configuration["Facturama:Password"];

    var useSandboxDefaults = ShouldUseSandboxDefaults(configuration, configuredBaseUrl);
    var defaultBaseUrl = useSandboxDefaults ? SandboxBaseUrl : ProductionBaseUrl;
    var user = string.IsNullOrWhiteSpace(configuredUser)
        ? (useSandboxDefaults ? SandboxUser : null)
        : configuredUser;
    var password = string.IsNullOrWhiteSpace(configuredPassword)
        ? (useSandboxDefaults ? SandboxPassword : null)
        : configuredPassword;

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
      System.Net.HttpStatusCode statusCode,
      string body,
      string? prefix = null)
  {
    if (statusCode == System.Net.HttpStatusCode.Unauthorized)
    {
      var baseMessage =
          $"Facturama ({_baseUri.Host}) devolvió 401. " +
          "Verifica las credenciales configuradas en Facturama:User y Facturama:Password.";

      if (string.IsNullOrWhiteSpace(prefix))
      {
        return new InvalidOperationException(baseMessage);
      }

      return new InvalidOperationException($"{prefix} {baseMessage}");
    }

    var detail = FormatFacturamaErrorBody(body);
    var message = string.IsNullOrWhiteSpace(prefix)
        ? $"Facturama ({_baseUri.Host}) devolvió {(int)statusCode}: {detail}"
        : $"{prefix} Estatus {(int)statusCode}: {detail}";

    return new InvalidOperationException(message);
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

      return parts.Count == 0 ? body : string.Join(" | ", parts);
    }
    catch (JsonException)
    {
      return body;
    }
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

  private static string NormalizeFacturamaFieldName(string fieldName)
  {
    const string prefix = "cfdiToCreate.";
    return fieldName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
        ? fieldName[prefix.Length..]
        : fieldName;
  }

  private sealed record FacturamaSettings(string BaseUrl, string User, string Password);
}
