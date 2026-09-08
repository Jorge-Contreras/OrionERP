using System.Globalization;

namespace OrionERP.Application.Features.Platform;

public static class PlatformAdministrationPolicy
{
  public static string? NormalizeTaxRfc(string? value)
  {
    var normalized = NullIfWhiteSpace(value)?.ToUpperInvariant();
    if (normalized is null)
      return null;
    if (normalized.Length is < 12 or > 13
        || normalized.Any(character => !char.IsAsciiLetterUpper(character)
          && !char.IsAsciiDigit(character)
          && character is not '&' and not 'Ñ'))
      throw Invalid("El RFC fiscal debe tener 12 o 13 caracteres y usar únicamente letras, números, & o Ñ.");
    return normalized;
  }

  public static string? NormalizeLegacyTenantKey(string? value)
  {
    var normalized = NullIfWhiteSpace(value);
    if (normalized is not null && (normalized.Length > 50 || normalized.Any(char.IsControl)))
      throw Invalid("La clave heredada no puede exceder 50 caracteres ni contener caracteres de control.");
    return normalized;
  }

  public static string NormalizeSiteKey(string? value)
    => NormalizeSlug(value, "La clave de sede", 2, 100);

  public static string NormalizePublicSiteKey(string? value)
    => NormalizeSlug(value, "La clave del website", 3, 100);

  public static string NormalizeDisplayName(string? value)
  {
    var normalized = value?.Trim() ?? string.Empty;
    if (normalized.Length is 0 or > 200 || normalized.Any(char.IsControl))
      throw Invalid("El nombre debe tener entre 1 y 200 caracteres.");
    return normalized;
  }

  public static string NormalizeTimeZoneId(string? value)
  {
    var normalized = value?.Trim() ?? string.Empty;
    if (normalized.Length is 0 or > 100 || normalized.Any(char.IsControl))
      throw Invalid("La zona horaria debe tener entre 1 y 100 caracteres.");
    return normalized;
  }

  public static string NormalizeModuleCode(string? value)
  {
    var normalized = value?.Trim().ToUpperInvariant() ?? string.Empty;
    if (!PlatformModuleCodes.Known.Contains(normalized))
      throw Invalid("El módulo indicado no pertenece al catálogo autorizado.");
    return normalized;
  }

  public static string NormalizeCompanyModuleStatus(string? value)
  {
    var normalized = value?.Trim() ?? string.Empty;
    if (!PlatformCompanyModuleStatuses.Known.Contains(normalized))
      throw Invalid("El estado del módulo no es válido.");
    return normalized;
  }

  public static (DateTime? FromUtc, DateTime? ToUtc) NormalizeEffectiveRange(
    DateTime? effectiveFromUtc,
    DateTime? effectiveToUtc)
  {
    var from = NormalizeUtc(effectiveFromUtc);
    var to = NormalizeUtc(effectiveToUtc);
    if (from.HasValue && to.HasValue && to.Value <= from.Value)
      throw Invalid("El fin de vigencia debe ser posterior al inicio.");
    return (from, to);
  }

  public static string NormalizeCanonicalHost(string? value)
  {
    var host = value?.Trim().TrimEnd('.') ?? string.Empty;
    if (host.Length is 0 or > 253
        || host.Contains('/') || host.Contains(':') || host.Contains('@')
        || host.Contains('?') || host.Contains('#') || host.Contains('*'))
      throw Invalid("El dominio debe incluir únicamente el host DNS, sin protocolo, puerto, ruta ni comodines.");

    string asciiHost;
    try
    {
      asciiHost = new IdnMapping().GetAscii(host).ToLowerInvariant();
    }
    catch (ArgumentException)
    {
      throw Invalid("El dominio no es un host DNS válido.");
    }

    if (Uri.CheckHostName(asciiHost) != UriHostNameType.Dns || !asciiHost.Contains('.'))
      throw Invalid("El dominio debe ser un host DNS completo, por ejemplo reservaciones.ejemplo.com.");
    return asciiHost;
  }

  public static long NormalizePublicSiteVersion(long value, string label)
  {
    if (value <= 0)
      throw Invalid($"La versión de {label} debe ser mayor a cero.");
    return value;
  }

  public static bool IsCompletePublicActivationChain(
    bool companyIsActive,
    bool siteIsActive,
    bool moduleIsActive,
    bool moduleRequiresSite,
    string moduleCode,
    string? companyModuleStatus,
    DateTime? effectiveFromUtc,
    DateTime? effectiveToUtc,
    bool capabilityIsEnabled,
    DateTime utcNow)
    => companyIsActive
      && siteIsActive
      && moduleIsActive
      && moduleRequiresSite
      && PlatformModuleCodes.PublicWebsiteModules.Contains(moduleCode)
      && PublicSiteResolutionPolicy.IsEffective(
        companyModuleStatus,
        effectiveFromUtc,
        effectiveToUtc,
        utcNow)
      && capabilityIsEnabled;

  public static byte[] DecodeConcurrencyToken(string? value)
  {
    try
    {
      var bytes = Convert.FromBase64String(value?.Trim() ?? string.Empty);
      if (bytes.Length != 8)
        throw new FormatException();
      return bytes;
    }
    catch (FormatException)
    {
      throw new PlatformAdministrationConcurrencyException();
    }
  }

  private static string NormalizeSlug(string? value, string label, int minimum, int maximum)
  {
    var normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
    if (normalized.Length < minimum || normalized.Length > maximum
        || normalized[0] == '-' || normalized[^1] == '-'
        || normalized.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
      throw Invalid($"{label} debe usar entre {minimum} y {maximum} caracteres: letras minúsculas, números o guiones.");
    return normalized;
  }

  private static DateTime? NormalizeUtc(DateTime? value)
  {
    if (!value.HasValue)
      return null;
    return value.Value.Kind switch
    {
      DateTimeKind.Utc => value.Value,
      DateTimeKind.Local => value.Value.ToUniversalTime(),
      _ => DateTime.SpecifyKind(value.Value, DateTimeKind.Utc)
    };
  }

  private static PlatformAdministrationValidationException Invalid(string message)
    => new(message);

  private static string? NullIfWhiteSpace(string? value)
    => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
