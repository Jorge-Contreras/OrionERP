using System.Data;
using System.Data.Common;
using System.Text.Json;
using Dapper;
using OrionERP.Application.Common;
using OrionERP.Application.Features.Logistica.Shared;
using OrionERP.Application.Features.Platform;
using OrionERP.Application.Features.Restaurante;

namespace OrionERP.Infrastructure.Features.Restaurante;

public sealed class RestaurantPublicCatalogService : IRestaurantPublicCatalogService
{
  private readonly IDbConnectionFactory _connectionFactory;
  private readonly IRestaurantCatalogService _catalogService;
  private readonly IRestaurantPromotionService _promotionService;
  private readonly IOrionSqlSessionFactory _sessionFactory;

  public RestaurantPublicCatalogService(
    IDbConnectionFactory connectionFactory,
    IRestaurantCatalogService catalogService,
    IRestaurantPromotionService promotionService,
    IOrionSqlSessionFactory sessionFactory)
  {
    _connectionFactory = connectionFactory;
    _catalogService = catalogService;
    _promotionService = promotionService;
    _sessionFactory = sessionFactory;
  }

  public async Task<RestaurantPublicCatalogDto?> GetCatalogAsync(
    PublicSiteBinding binding,
    DateTimeOffset at,
    CancellationToken ct = default)
  {
    EnsureRestaurantBinding(binding);
    await using var connection = await _sessionFactory.OpenAsync(
      PlatformExecutionScope.FromPublicSite(binding), ct);
    var siteId = await connection.QuerySingleOrDefaultAsync<int?>(new CommandDefinition(
      """
      SELECT Id
      FROM restaurante.Site
      WHERE OrionCompanyId=@CompanyId AND OrionSiteId=@SiteId AND IsEnabled=1;
      """,
      new { binding.CompanyId, binding.SiteId },
      cancellationToken: ct));
    if (!siteId.HasValue)
      return null;

    var settings = await GetSettingsAsync(binding, ct);
    if (settings is null)
      return null;

    var menu = await _catalogService.GetPublicCatalogAsync(binding.CompanyRfc, siteId.Value, at, ct);
    IReadOnlyList<RestaurantPublicPromotionDto> promotions = Array.Empty<RestaurantPublicPromotionDto>();
    if (settings.IsPromotionsEnabled)
    {
      var allPromotions = await _promotionService.GetPromotionsAsync(
        binding.CompanyRfc, siteId.Value, includeInactive: false, ct);
      promotions = allPromotions
        .Where(item => item.IsPublic && item.WebEnabled &&
          item.Status is RestaurantPromotionStatuses.Active or RestaurantPromotionStatuses.Scheduled)
        .OrderByDescending(item => item.Priority)
        .ThenBy(item => item.Id)
        .Select(item => new RestaurantPublicPromotionDto
        {
          Id = item.Id,
          Name = item.Name,
          Description = item.PublicDescription,
          Terms = item.PublicTerms,
          ValidFromLocal = item.ValidFromLocal,
          ValidToLocal = item.ValidToLocal,
          Schedules = item.Schedules
        })
        .ToArray();
    }

    return new RestaurantPublicCatalogDto
    {
      Settings = settings,
      Menu = menu,
      Promotions = promotions
    };
  }

  public async Task<RestaurantPublicSiteSettingsDto?> GetSettingsAsync(
    PublicSiteBinding binding,
    CancellationToken ct = default)
  {
    EnsureRestaurantBinding(binding);
    await using var connection = await _sessionFactory.OpenAsync(
      PlatformExecutionScope.FromPublicSite(binding), ct);
    return await connection.QuerySingleOrDefaultAsync<RestaurantPublicSiteSettingsDto>(new CommandDefinition(
      """
      SELECT
        PublicSiteId,Rfc,SiteId,LegalName,PublicName,HeroEyebrow,HeroTitle,HeroDescription,
        AddressLine,Neighborhood,PostalCode,City,StateName,CountryName,
        WhatsAppPhone,WhatsAppDisplay,MapsUrl,FacebookUrl,InstagramUrl,TikTokUrl,
        OpeningHoursJson,SeoDescription,IsWebsiteEnabled,IsMembershipEnabled,
        IsLoyaltyAccrualEnabled,IsPromotionsEnabled,UpdatedAt
      FROM restaurante.PublicSiteSettings
      WHERE PublicSiteId=@PublicSiteId;
      """,
      new { binding.PublicSiteId },
      cancellationToken: ct));
  }

  public async Task<(byte[] Bytes, string ContentType)?> GetProductImageAsync(
    PublicSiteBinding binding,
    long productId,
    bool thumbnail,
    CancellationToken ct = default)
  {
    EnsureRestaurantBinding(binding);
    if (productId <= 0)
      throw new ArgumentOutOfRangeException(nameof(productId));

    await using var connection = await _sessionFactory.OpenAsync(
      PlatformExecutionScope.FromPublicSite(binding), ct);
    var row = await connection.QueryFirstOrDefaultAsync<PublicImageRow>(new CommandDefinition(
      """
      SELECT
        CASE WHEN @Thumbnail=1
          THEN COALESCE(product.VariantImageThumbnail,product.VariantImage,card.FamilyImageThumbnail,
            material.PrimaryImageThumbnail,card.FamilyImage,material.PrimaryImage)
          ELSE COALESCE(product.VariantImage,card.FamilyImage,material.PrimaryImage) END Bytes,
        COALESCE(product.VariantImageContentType,card.ImageContentType,material.PrimaryImageContentType,'image/jpeg') ContentType
      FROM restaurante.Site siteInfo
      JOIN restaurante.Product product ON product.Rfc=siteInfo.Rfc
      JOIN restaurante.ProductCard card ON card.Rfc=product.Rfc AND card.Id=product.ProductCardId
      LEFT JOIN logistica.Material material ON material.Rfc=product.Rfc AND material.Id=product.MaterialId
      LEFT JOIN restaurante.KitchenStation station ON station.Rfc=product.Rfc AND station.Id=product.KitchenStationId
      WHERE siteInfo.OrionCompanyId=@CompanyId AND siteInfo.OrionSiteId=@SiteId AND siteInfo.IsEnabled=1
        AND product.Id=@ProductId AND product.IsActive=1
        AND (product.KitchenStationId IS NULL OR station.SiteId=siteInfo.Id);
      """,
      new
      {
        binding.CompanyId,
        binding.SiteId,
        ProductId = productId,
        Thumbnail = thumbnail
      },
      cancellationToken: ct));
    return row?.Bytes is { Length: > 0 }
      ? (row.Bytes, row.ContentType ?? "image/jpeg")
      : null;
  }

  public async Task<RestaurantPublicSiteSettingsDto?> GetSettingsAsync(
    string rfc,
    int siteId,
    CancellationToken ct = default)
  {
    var normalizedRfc = LogisticsRfc.Require(rfc);
    if (siteId <= 0)
    {
      throw new ArgumentOutOfRangeException(nameof(siteId));
    }

    using var conn = CreateConnection();
    return await conn.QuerySingleOrDefaultAsync<RestaurantPublicSiteSettingsDto>(new CommandDefinition(
      """
      SELECT
        Rfc,SiteId,LegalName,PublicName,HeroEyebrow,HeroTitle,HeroDescription,
        AddressLine,Neighborhood,PostalCode,City,StateName,CountryName,
        WhatsAppPhone,WhatsAppDisplay,MapsUrl,FacebookUrl,InstagramUrl,TikTokUrl,
        OpeningHoursJson,SeoDescription,IsWebsiteEnabled,IsMembershipEnabled,
        IsLoyaltyAccrualEnabled,IsPromotionsEnabled,UpdatedAt
      FROM restaurante.PublicSiteSettings
      WHERE Rfc=@Rfc AND SiteId=@SiteId;
      """,
      new { Rfc = normalizedRfc, SiteId = siteId },
      cancellationToken: ct));
  }

  public async Task<RestaurantCommandResult> SaveSettingsAsync(
    RestaurantPublicSiteSettingsSaveRequest request,
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
        return RestaurantCommandResult.Fail("La configuración pública no existe; aplica primero la migración de Restaurant.");
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

  private static void EnsureRestaurantBinding(PublicSiteBinding binding)
  {
    ArgumentNullException.ThrowIfNull(binding);
    if (!string.Equals(binding.ModuleCode, PlatformModuleCodes.Restaurant, StringComparison.Ordinal))
      throw new UnauthorizedAccessException("El sitio público no pertenece al módulo Restaurant.");
  }

  private sealed class PublicImageRow
  {
    public byte[]? Bytes { get; set; }
    public string? ContentType { get; set; }
  }
}
