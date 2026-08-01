using OrionERP.Application.Common;
using OrionERP.Application.Features.Restaurante;

namespace OrionERP.Bruno.Web;

public static class BrunoSiteConstants
{
  public const string Rfc = BrunoRestaurantConstants.Rfc;
  public const string SiteCode = BrunoRestaurantConstants.SiteCode;
  public const string PrivacyVersion = "2026-07-31";
  public const string TermsVersion = "2026-07-31";
  public const string CanonicalBaseUrl = "https://brunosgarden.com";
}

public sealed class BrunoRfcAccessor : ICurrentRfcAccessor
{
  public string CurrentRfc => BrunoSiteConstants.Rfc;
}
