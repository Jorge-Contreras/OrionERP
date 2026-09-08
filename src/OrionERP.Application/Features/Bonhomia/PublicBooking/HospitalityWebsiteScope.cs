using OrionERP.Application.Features.Platform;

namespace OrionERP.Application.Features.Bonhomia.PublicBooking;

/// <summary>
/// Database scope for one verified hospitality website instance. This value is
/// deliberately created from <see cref="PublicSiteBinding"/> and is never
/// accepted from an HTTP request, route, header or quote payload.
/// </summary>
public sealed record HospitalityWebsiteScope(
  long CompanyId,
  long SiteId,
  string CompanyRfc,
  string PublicSiteKey)
{
  public bool Owns(long companyId, long siteId)
    => CompanyId == companyId && SiteId == siteId;
}

public static class HospitalityWebsiteScopePolicy
{
  public static HospitalityWebsiteScope FromBinding(PublicSiteBinding binding)
  {
    ArgumentNullException.ThrowIfNull(binding);

    if (!string.Equals(binding.ModuleCode, PlatformModuleCodes.Hospitality, StringComparison.Ordinal))
    {
      throw new InvalidOperationException(
        $"PublicSite '{binding.PublicSiteKey}' is not bound to the HOSPITALITY module.");
    }

    if (binding.CompanyId <= 0 || binding.SiteId <= 0)
    {
      throw new InvalidOperationException("The hospitality website binding has an invalid company/site scope.");
    }

    if (string.IsNullOrWhiteSpace(binding.CompanyRfc)
        || string.IsNullOrWhiteSpace(binding.PublicSiteKey))
    {
      throw new InvalidOperationException("The hospitality website binding is incomplete.");
    }

    return new HospitalityWebsiteScope(
      binding.CompanyId,
      binding.SiteId,
      binding.CompanyRfc,
      binding.PublicSiteKey);
  }

  public static void EnsureQuoteBelongsToScope(
    BonhomiaQuoteDto quote,
    HospitalityWebsiteScope scope)
  {
    ArgumentNullException.ThrowIfNull(quote);
    ArgumentNullException.ThrowIfNull(scope);

    if (string.IsNullOrWhiteSpace(quote.PublicSiteKey)
        || !string.Equals(quote.PublicSiteKey, scope.PublicSiteKey, StringComparison.Ordinal))
    {
      throw new BonhomiaPublicBookingException(
        "quote_scope_mismatch",
        "La cotizacion no pertenece a este sitio de hospedaje.");
    }
  }
}

public interface IHospitalityWebsiteScopeAccessor
{
  Task<HospitalityWebsiteScope> ResolveRequiredAsync(CancellationToken ct = default);
}
