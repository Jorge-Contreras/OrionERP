using System.Data;
using System.Data.Common;
using System.Text.Json;
using Dapper;
using OrionERP.Application.Common;
using OrionERP.Application.Features.Logistica.Shared;
using OrionERP.Application.Features.Restaurante;

namespace OrionERP.Infrastructure.Features.Restaurante;

public sealed class BrunoPublicCatalogService : IBrunoPublicCatalogService
{
  private readonly IDbConnectionFactory _connectionFactory;
  private readonly IRestaurantCatalogService _catalogService;
  private readonly IRestaurantPromotionService _promotionService;

  public BrunoPublicCatalogService(
    IDbConnectionFactory connectionFactory,
    IRestaurantCatalogService catalogService,
    IRestaurantPromotionService promotionService)
  {
    _connectionFactory = connectionFactory;
    _catalogService = catalogService;
    _promotionService = promotionService;
  }

  public async Task<BrunoPublicCatalogDto?> GetCatalogAsync(
    string rfc,
    string siteCode,
    DateTimeOffset at,
    CancellationToken ct = default)
  {
    var normalizedRfc = LogisticsRfc.Require(rfc);
    var sites = await _catalogService.GetSitesAsync(normalizedRfc, ct);
    var site = sites.FirstOrDefault(item =>
      item.IsEnabled &&
      string.Equals(item.SiteCode, siteCode, StringComparison.OrdinalIgnoreCase));
    if (site is null)
    {
      return null;
    }

    var settings = await GetSettingsAsync(normalizedRfc, site.Id, ct);
    if (settings is null)
    {
      return null;
    }

    var menu = await _catalogService.GetPosCatalogAsync(normalizedRfc, site.Id, at, ct);
    IReadOnlyList<BrunoPublicPromotionDto> promotions = Array.Empty<BrunoPublicPromotionDto>();
    if (settings.IsPromotionsEnabled)
    {
      var allPromotions = await _promotionService.GetPromotionsAsync(
        normalizedRfc,
        site.Id,
        includeInactive: false,
        ct);
      promotions = allPromotions
        .Where(item =>
          item.IsPublic &&
          item.WebEnabled &&
          item.Status is RestaurantPromotionStatuses.Active or RestaurantPromotionStatuses.Scheduled)
        .OrderByDescending(item => item.Priority)
        .ThenBy(item => item.Id)
        .Select(item => new BrunoPublicPromotionDto
        {
          Id = item.Id,
          Name = item.Name,
          Description = item.PublicDescription,
          Terms = item.PublicTerms,
          ValidFromLocal = item.ValidFromLocal,
          ValidToLocal = item.ValidToLocal,
          Schedules = item.Schedules
        })
        .ToList();
    }

    return new BrunoPublicCatalogDto
    {
      Settings = settings,
      Menu = menu,
      Promotions = promotions
    };
  }

  public async Task<BrunoPublicSiteSettingsDto?> GetSettingsAsync(
    string rfc,
    int? siteId = null,
    CancellationToken ct = default)
  {
    var normalizedRfc = LogisticsRfc.Require(rfc);
    using var conn = CreateConnection();
    return await conn.QuerySingleOrDefaultAsync<BrunoPublicSiteSettingsDto>(new CommandDefinition(
      """
      SELECT TOP(1)
        Rfc,SiteId,LegalName,PublicName,HeroEyebrow,HeroTitle,HeroDescription,
        AddressLine,Neighborhood,PostalCode,City,StateName,CountryName,
        WhatsAppPhone,WhatsAppDisplay,MapsUrl,FacebookUrl,InstagramUrl,TikTokUrl,
        OpeningHoursJson,SeoDescription,IsWebsiteEnabled,IsMembershipEnabled,
        IsLoyaltyAccrualEnabled,IsPromotionsEnabled,UpdatedAt
      FROM restaurante.PublicSiteSettings
      WHERE Rfc=@Rfc AND (@SiteId IS NULL OR SiteId=@SiteId)
      ORDER BY SiteId;
      """,
      new { Rfc = normalizedRfc, SiteId = siteId },
      cancellationToken: ct));
  }

  public async Task<RestaurantCommandResult> SaveSettingsAsync(
    BrunoPublicSiteSettingsSaveRequest request,
    string userName,
    CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(request);
    var normalizedRfc = LogisticsRfc.Require(request.Rfc);
    try
    {
      using var json = JsonDocument.Parse(request.OpeningHoursJson);
      if (json.RootElement.ValueKind != JsonValueKind.Object)
      {
        return RestaurantCommandResult.Fail("El horario debe ser un objeto JSON por día.");
      }
    }
    catch (JsonException)
    {
      return RestaurantCommandResult.Fail("El horario no contiene JSON válido.");
    }

    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    await using var tx = await conn.BeginTransactionAsync(IsolationLevel.Serializable, ct);
    try
    {
      var affected = await conn.ExecuteAsync(new CommandDefinition(
        """
        UPDATE restaurante.PublicSiteSettings
        SET LegalName=@LegalName,PublicName=@PublicName,HeroEyebrow=@HeroEyebrow,
            HeroTitle=@HeroTitle,HeroDescription=@HeroDescription,
            AddressLine=@AddressLine,Neighborhood=@Neighborhood,PostalCode=@PostalCode,
            City=@City,StateName=@StateName,CountryName=@CountryName,
            WhatsAppPhone=@WhatsAppPhone,WhatsAppDisplay=@WhatsAppDisplay,MapsUrl=@MapsUrl,
            FacebookUrl=@FacebookUrl,InstagramUrl=@InstagramUrl,TikTokUrl=@TikTokUrl,
            OpeningHoursJson=@OpeningHoursJson,SeoDescription=@SeoDescription,
            IsWebsiteEnabled=@IsWebsiteEnabled,IsMembershipEnabled=@IsMembershipEnabled,
            IsLoyaltyAccrualEnabled=@IsLoyaltyAccrualEnabled,
            IsPromotionsEnabled=@IsPromotionsEnabled,
            UpdatedAt=SYSUTCDATETIME(),UpdatedBy=@UpdatedBy
        WHERE Rfc=@Rfc AND SiteId=@SiteId;
        """,
        new
        {
          Rfc = normalizedRfc,
          request.SiteId,
          request.LegalName,
          request.PublicName,
          request.HeroEyebrow,
          request.HeroTitle,
          request.HeroDescription,
          request.AddressLine,
          request.Neighborhood,
          request.PostalCode,
          request.City,
          request.StateName,
          request.CountryName,
          request.WhatsAppPhone,
          request.WhatsAppDisplay,
          request.MapsUrl,
          FacebookUrl = NullIfWhiteSpace(request.FacebookUrl),
          InstagramUrl = NullIfWhiteSpace(request.InstagramUrl),
          TikTokUrl = NullIfWhiteSpace(request.TikTokUrl),
          request.OpeningHoursJson,
          request.SeoDescription,
          request.IsWebsiteEnabled,
          request.IsMembershipEnabled,
          request.IsLoyaltyAccrualEnabled,
          request.IsPromotionsEnabled,
          UpdatedBy = string.IsNullOrWhiteSpace(userName) ? "orionerp" : userName.Trim()
        },
        tx,
        cancellationToken: ct));
      if (affected != 1)
      {
        await tx.RollbackAsync(ct);
        return RestaurantCommandResult.Fail("La configuración pública no existe; aplica primero la migración de Bruno.");
      }

      await conn.ExecuteAsync(new CommandDefinition(
        """
        UPDATE fidelidad.ProgramSettings
        SET IsAccrualEnabled=@Enabled,UpdatedAt=SYSUTCDATETIME(),UpdatedBy=@UpdatedBy
        WHERE Rfc=@Rfc;
        """,
        new
        {
          Rfc = normalizedRfc,
          Enabled = request.IsLoyaltyAccrualEnabled,
          UpdatedBy = string.IsNullOrWhiteSpace(userName) ? "orionerp" : userName.Trim()
        },
        tx,
        cancellationToken: ct));
      await tx.CommitAsync(ct);
      return RestaurantCommandResult.Ok("La configuración del sitio quedó guardada.");
    }
    catch
    {
      await tx.RollbackAsync(ct);
      throw;
    }
  }

  private DbConnection CreateConnection() =>
    _connectionFactory.Create() as DbConnection
      ?? throw new InvalidOperationException("La fábrica no devolvió una DbConnection.");
  private static string? NullIfWhiteSpace(string? value) =>
    string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
