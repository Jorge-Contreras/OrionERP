using System.ComponentModel.DataAnnotations;

namespace OrionERP.Application.Features.Restaurante;

public sealed class BrunoPublicSiteSettingsDto
{
  public string Rfc { get; set; } = string.Empty;
  public int SiteId { get; set; }
  public string LegalName { get; set; } = string.Empty;
  public string PublicName { get; set; } = string.Empty;
  public string HeroEyebrow { get; set; } = string.Empty;
  public string HeroTitle { get; set; } = string.Empty;
  public string HeroDescription { get; set; } = string.Empty;
  public string AddressLine { get; set; } = string.Empty;
  public string Neighborhood { get; set; } = string.Empty;
  public string PostalCode { get; set; } = string.Empty;
  public string City { get; set; } = string.Empty;
  public string StateName { get; set; } = string.Empty;
  public string CountryName { get; set; } = string.Empty;
  public string WhatsAppPhone { get; set; } = string.Empty;
  public string WhatsAppDisplay { get; set; } = string.Empty;
  public string MapsUrl { get; set; } = string.Empty;
  public string? FacebookUrl { get; set; }
  public string? InstagramUrl { get; set; }
  public string? TikTokUrl { get; set; }
  public string OpeningHoursJson { get; set; } = "{}";
  public string SeoDescription { get; set; } = string.Empty;
  public bool IsWebsiteEnabled { get; set; }
  public bool IsMembershipEnabled { get; set; }
  public bool IsLoyaltyAccrualEnabled { get; set; }
  public bool IsPromotionsEnabled { get; set; }
  public DateTime UpdatedAt { get; set; }
}

public sealed class BrunoPublicSiteSettingsSaveRequest
{
  [Required] public string Rfc { get; set; } = string.Empty;
  public int SiteId { get; set; }
  [Required, StringLength(200)] public string LegalName { get; set; } = string.Empty;
  [Required, StringLength(160)] public string PublicName { get; set; } = string.Empty;
  [Required, StringLength(160)] public string HeroEyebrow { get; set; } = string.Empty;
  [Required, StringLength(240)] public string HeroTitle { get; set; } = string.Empty;
  [Required, StringLength(800)] public string HeroDescription { get; set; } = string.Empty;
  [Required, StringLength(300)] public string AddressLine { get; set; } = string.Empty;
  [Required, StringLength(160)] public string Neighborhood { get; set; } = string.Empty;
  [Required, StringLength(10)] public string PostalCode { get; set; } = string.Empty;
  [Required, StringLength(120)] public string City { get; set; } = string.Empty;
  [Required, StringLength(120)] public string StateName { get; set; } = string.Empty;
  [Required, StringLength(120)] public string CountryName { get; set; } = string.Empty;
  [Required, StringLength(30)] public string WhatsAppPhone { get; set; } = string.Empty;
  [Required, StringLength(40)] public string WhatsAppDisplay { get; set; } = string.Empty;
  [Required, Url, StringLength(1000)] public string MapsUrl { get; set; } = string.Empty;
  [Url, StringLength(1000)] public string? FacebookUrl { get; set; }
  [Url, StringLength(1000)] public string? InstagramUrl { get; set; }
  [Url, StringLength(1000)] public string? TikTokUrl { get; set; }
  [Required, StringLength(2000)] public string OpeningHoursJson { get; set; } = "{}";
  [Required, StringLength(500)] public string SeoDescription { get; set; } = string.Empty;
  public bool IsWebsiteEnabled { get; set; }
  public bool IsMembershipEnabled { get; set; }
  public bool IsLoyaltyAccrualEnabled { get; set; }
  public bool IsPromotionsEnabled { get; set; }
}

public sealed class BrunoPublicCatalogDto
{
  public BrunoPublicSiteSettingsDto Settings { get; set; } = new();
  public RestaurantPosCatalogDto Menu { get; set; } = new();
  public IReadOnlyList<BrunoPublicPromotionDto> Promotions { get; set; } = Array.Empty<BrunoPublicPromotionDto>();
}

public sealed class BrunoPublicPromotionDto
{
  public long Id { get; set; }
  public string Name { get; set; } = string.Empty;
  public string Description { get; set; } = string.Empty;
  public string Terms { get; set; } = string.Empty;
  public DateTime? ValidFromLocal { get; set; }
  public DateTime? ValidToLocal { get; set; }
  public IReadOnlyList<RestaurantPromotionScheduleDto> Schedules { get; set; } = Array.Empty<RestaurantPromotionScheduleDto>();
}
