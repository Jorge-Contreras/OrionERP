using System.Security.Cryptography;
using OrionERP.Application.Features.Restaurante;
using SkiaSharp;

namespace OrionERP.Infrastructure.Features.Restaurante;

public static class RestaurantDeliveryImageNormalizer
{
  public const int MaximumSourceBytes = 12 * 1024 * 1024;
  public const int MaximumStoredBytes = 2 * 1024 * 1024;
  public const int MaximumEdge = 1600;
  public const int ThumbnailEdge = 320;

  public static RestaurantDeliveryNormalizedImage? TryNormalize(byte[] source)
  {
    if (source.Length is 0 or > MaximumSourceBytes
        || RestaurantSignageDefaults.SniffContentType(source) is null)
      return null;
    try
    {
      using var decoded = SKBitmap.Decode(source);
      if (decoded is null || decoded.Width <= 0 || decoded.Height <= 0) return null;
      using var encodedData = SKData.CreateCopy(source);
      using var codec = SKCodec.Create(encodedData);
      using var oriented = ApplyOrientation(decoded, codec?.EncodedOrigin ?? SKEncodedOrigin.TopLeft);
      using var normalized = Resize(oriented, MaximumEdge);
      byte[]? content = null;
      for (var quality = 86; quality >= 60; quality -= 6)
      {
        using var image = SKImage.FromBitmap(normalized);
        using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, quality);
        content = encoded?.ToArray();
        if (content is { Length: > 0 and <= MaximumStoredBytes }) break;
      }
      if (content is not { Length: > 0 and <= MaximumStoredBytes }) return null;
      using var thumbnailBitmap = Resize(normalized, ThumbnailEdge);
      using var thumbnailImage = SKImage.FromBitmap(thumbnailBitmap);
      using var thumbnailData = thumbnailImage.Encode(SKEncodedImageFormat.Jpeg, 78);
      var thumbnail = thumbnailData?.ToArray();
      if (thumbnail is not { Length: > 0 }) return null;
      return new RestaurantDeliveryNormalizedImage(
        content,
        thumbnail,
        normalized.Width,
        normalized.Height,
        SHA256.HashData(content));
    }
    catch
    {
      return null;
    }
  }

  private static SKBitmap Resize(SKBitmap source, int maximumEdge)
  {
    var scale = Math.Min(1d, (double)maximumEdge / Math.Max(source.Width, source.Height));
    var width = Math.Max(1, (int)Math.Round(source.Width * scale));
    var height = Math.Max(1, (int)Math.Round(source.Height * scale));
    var resized = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgb888x, SKAlphaType.Opaque));
    using var canvas = new SKCanvas(resized);
    canvas.Clear(SKColors.White);
    canvas.DrawBitmap(source, new SKRect(0, 0, width, height), new SKSamplingOptions(SKCubicResampler.Mitchell));
    return resized;
  }

  private static SKBitmap ApplyOrientation(SKBitmap source, SKEncodedOrigin origin)
  {
    var swapsAxes = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
      or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
    var result = new SKBitmap(
      new SKImageInfo(swapsAxes ? source.Height : source.Width, swapsAxes ? source.Width : source.Height,
        SKColorType.Rgb888x, SKAlphaType.Opaque));
    using var canvas = new SKCanvas(result);
    canvas.Clear(SKColors.White);
    switch (origin)
    {
      case SKEncodedOrigin.TopRight: canvas.Translate(source.Width, 0); canvas.Scale(-1, 1); break;
      case SKEncodedOrigin.BottomRight: canvas.Translate(source.Width, source.Height); canvas.RotateDegrees(180); break;
      case SKEncodedOrigin.BottomLeft: canvas.Translate(0, source.Height); canvas.Scale(1, -1); break;
      case SKEncodedOrigin.LeftTop: canvas.RotateDegrees(90); canvas.Scale(1, -1); break;
      case SKEncodedOrigin.RightTop: canvas.Translate(source.Height, 0); canvas.RotateDegrees(90); break;
      case SKEncodedOrigin.RightBottom: canvas.Translate(source.Height, source.Width); canvas.RotateDegrees(90); canvas.Scale(-1, 1); break;
      case SKEncodedOrigin.LeftBottom: canvas.Translate(0, source.Width); canvas.RotateDegrees(-90); break;
    }
    canvas.DrawBitmap(source, 0, 0, new SKSamplingOptions(SKCubicResampler.Mitchell));
    return result;
  }
}

public sealed record RestaurantDeliveryNormalizedImage(
  byte[] Content,
  byte[] Thumbnail,
  int Width,
  int Height,
  byte[] Hash);
