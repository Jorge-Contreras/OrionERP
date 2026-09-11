namespace OrionERP.Application.Features.Hospitality.PublicBooking;

public static class HospitalityCheckoutOptionsValidator
{
  public static IReadOnlyList<string> ValidateForEnvironment(
    HospitalityCheckoutOptions options,
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
      errors.Add("Production hospitality checkout must set HospitalityCheckout:Environment to Live or Production.");
    }

    if (!options.IsPayPalConfigured)
    {
      errors.Add("Production hospitality checkout requires HospitalityCheckout:PayPalClientId and HospitalityCheckout:PayPalClientSecret.");
    }

    if (!IsAbsoluteHttpsUrl(options.PublicBaseUrl))
    {
      errors.Add("Production hospitality checkout requires HospitalityCheckout:PublicBaseUrl to be an absolute HTTPS URL.");
    }

    if (string.IsNullOrWhiteSpace(options.AccountingAccount))
    {
      errors.Add("Production hospitality checkout requires a bound accounting account name.");
    }

    if (string.IsNullOrWhiteSpace(options.PublicName)
        || string.IsNullOrWhiteSpace(options.ReservationSourceLabel))
    {
      errors.Add("Production hospitality checkout requires its public name and reservation source label.");
    }

    return errors;
  }

  private static bool IsAbsoluteHttpsUrl(string? value)
    => Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri)
      && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
}
