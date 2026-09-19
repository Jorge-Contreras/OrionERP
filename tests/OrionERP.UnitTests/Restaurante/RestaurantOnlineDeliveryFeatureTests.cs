using OrionERP.Infrastructure.Features.Restaurante;
using SkiaSharp;

namespace OrionERP.UnitTests.Restaurante;

public sealed class RestaurantOnlineDeliveryFeatureTests
{
  [Fact]
  public void EvidenceNormalizer_RejectsUnknownBytes_AndProducesBoundedJpegAndThumbnail()
  {
    Assert.Null(RestaurantDeliveryImageNormalizer.TryNormalize([1, 2, 3, 4]));

    using var bitmap = new SKBitmap(2200, 1200);
    using (var canvas = new SKCanvas(bitmap)) canvas.Clear(SKColors.CornflowerBlue);
    using var image = SKImage.FromBitmap(bitmap);
    using var png = image.Encode(SKEncodedImageFormat.Png, 100);

    var normalized = RestaurantDeliveryImageNormalizer.TryNormalize(png.ToArray());

    Assert.NotNull(normalized);
    Assert.True(Math.Max(normalized!.Width, normalized.Height) <= 1600);
    Assert.InRange(normalized.Content.Length, 1, RestaurantDeliveryImageNormalizer.MaximumStoredBytes);
    Assert.NotEmpty(normalized.Thumbnail);
    Assert.Equal(32, normalized.Hash.Length);
    Assert.Equal(0xFF, normalized.Content[0]);
    Assert.Equal(0xD8, normalized.Content[1]);
  }

  [Fact]
  public void Migration_DefinesIndependentModesRetentionAndAtomicCourierConstraints()
  {
    var sql = File.ReadAllText(Path.Combine(RepositoryRoot(),
      "src", "OrionERP.Infrastructure", "Features", "Restaurante", "Sql", "20260918_restaurant_online_delivery.sql"));
    Assert.Contains("DeliveryEnabled bit NOT NULL", sql, StringComparison.Ordinal);
    Assert.Contains("DeliveryFlatFee", sql, StringComparison.Ordinal);
    Assert.Contains("OnlineCheckoutFacade", sql, StringComparison.Ordinal);
    Assert.Contains("DeliveryEvidence", sql, StringComparison.Ordinal);
    Assert.Contains("DATEADD(DAY,-90", sql, StringComparison.Ordinal);
    Assert.Contains("OnlineCheckoutChargeBeginV2", sql, StringComparison.Ordinal);
    Assert.Contains("OutForDelivery", sql, StringComparison.Ordinal);
    Assert.Contains("ProfileVersion=12", sql, StringComparison.Ordinal);
  }

  [Fact]
  public void RetentionCorrections_UsePlatformScope_AndRemovePublicPurgeExecution()
  {
    var sqlRoot = Path.Combine(RepositoryRoot(),
      "src", "OrionERP.Infrastructure", "Features", "Restaurante", "Sql");
    var permissionFix = File.ReadAllText(Path.Combine(sqlRoot,
      "20260918_restaurant_online_delivery_runtime_fix.sql"));
    var scopeFix = File.ReadAllText(Path.Combine(sqlRoot,
      "20260918_restaurant_online_delivery_scope_fix.sql"));

    Assert.Contains("ProfileVersion=13", permissionFix, StringComparison.Ordinal);
    Assert.Contains("ObjectName='OnlineDeliveryEvidencePurge'", permissionFix, StringComparison.Ordinal);
    Assert.Contains("La purga de evidencias solo admite el worker interno", permissionFix, StringComparison.Ordinal);
    Assert.Contains("CompanyId=@CompanyId AND SiteId=@PlatformSiteId", scopeFix, StringComparison.Ordinal);
    Assert.Contains("PublicExecuteRemoved", scopeFix, StringComparison.Ordinal);
    Assert.DoesNotContain("facade.SiteId=@PlatformSiteId", scopeFix, StringComparison.Ordinal);
  }

  private static string RepositoryRoot()
  {
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "OrionERP.sln"))) directory = directory.Parent;
    return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
  }
}
