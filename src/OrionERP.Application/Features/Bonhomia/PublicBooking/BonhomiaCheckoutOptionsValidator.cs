namespace OrionERP.Application.Features.Bonhomia.PublicBooking;

public static class BonhomiaCheckoutOptionsValidator
{
  public static IReadOnlyList<string> ValidateForEnvironment(
    BonhomiaCheckoutOptions options,
    string? environmentName)
  {
    ArgumentNullException.ThrowIfNull(options);

    if (!string.Equals(environmentName, "Production", StringComparison.OrdinalIgnoreCase))
    {
      return Array.Empty<string>();
    }

    var errors = new List<string>();

    if (!options.UseLivePayPal)
    {
      errors.Add("Production Bonhomia checkout must set BonhomiaCheckout:Environment to Live or Production.");
    }

    if (!options.IsPayPalConfigured)
    {
      errors.Add("Production Bonhomia checkout requires BonhomiaCheckout:PayPalClientId and BonhomiaCheckout:PayPalClientSecret.");
    }

    if (!IsAbsoluteHttpsUrl(options.PublicBaseUrl))
    {
      errors.Add("Production Bonhomia checkout requires BonhomiaCheckout:PublicBaseUrl to be an absolute HTTPS URL.");
    }

    return errors;
  }

  private static bool IsAbsoluteHttpsUrl(string? value)
    => Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri)
      && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
}
