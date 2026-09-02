using System.ComponentModel.DataAnnotations;

namespace OrionERP.Application.Features.Restaurante;

public static class RestaurantSignageDefaults
{
  public const int RotationSeconds = 8;
  public const int RefreshSeconds = 300;
  public const int TransitionMs = 450;
  public const int MaxImageBytes = 25 * 1024 * 1024;
  public const int MaxImagesPerScreen = 24;
  public const int MinKeyLength = 2;
  public const int MaxKeyLength = 40;

  /// <summary>Segmentos hermanos de /menus/{rfc}/{screenKey}; no pueden usarse como clave.</summary>
  public static readonly string[] ReservedKeys = ["media", "manifest.json"];

  public static readonly string[] AllowedContentTypes = ["image/png", "image/jpeg", "image/webp"];

  /// <summary>Convierte un texto libre en una clave apta para URL: minúsculas, sin acentos, separada por guiones.</summary>
  public static string NormalizeKey(string? value)
  {
    if (string.IsNullOrWhiteSpace(value)) return string.Empty;

    var normalized = value.Trim().ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD);
    var builder = new System.Text.StringBuilder(normalized.Length);
    foreach (var character in normalized)
    {
      // Se descartan las marcas diacríticas para que "menú" y "menu" produzcan la misma clave.
      if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(character)
          == System.Globalization.UnicodeCategory.NonSpacingMark)
        continue;

      if (character is >= 'a' and <= 'z' or >= '0' and <= '9')
        builder.Append(character);
      else if (builder.Length > 0 && builder[^1] != '-')
        builder.Append('-');
    }

    var key = builder.ToString().Trim('-');
    return key.Length > MaxKeyLength ? key[..MaxKeyLength].Trim('-') : key;
  }

  public static bool IsValidKey(string? value)
  {
    if (string.IsNullOrEmpty(value)) return false;
    if (value.Length is < MinKeyLength or > MaxKeyLength) return false;
    if (ReservedKeys.Contains(value, StringComparer.OrdinalIgnoreCase)) return false;
    return value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');
  }

  /// <summary>
  /// Determina el tipo real a partir de los bytes mágicos. Nunca se confía en el
  /// tipo declarado por el navegador: estos bytes se sirven de vuelta en una URL
  /// pública y un SVG o HTML disfrazado de imagen sería XSS almacenado.
  /// </summary>
  public static string? SniffContentType(byte[]? bytes)
  {
    if (bytes is null || bytes.Length < 12) return null;

    if (bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
      return "image/png";
    if (bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
      return "image/jpeg";
    if (bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) && bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8))
      return "image/webp";

    return null;
  }
}

public sealed class RestaurantSignageImageDto
{
  public long Id { get; set; }
  public int SortOrder { get; set; }
  public string FileName { get; set; } = string.Empty;
  public string ContentType { get; set; } = "image/png";
  public int ByteLength { get; set; }
  public int? Width { get; set; }
  public int? Height { get; set; }
  public string? AltText { get; set; }
  public bool IsEnabled { get; set; } = true;
  public string ContentHash { get; set; } = string.Empty;

  public string SizeLabel => ByteLength >= 1024 * 1024
    ? $"{ByteLength / (1024d * 1024d):0.0} MB"
    : $"{Math.Max(1, ByteLength / 1024)} KB";

  public string DimensionsLabel => Width.HasValue && Height.HasValue ? $"{Width}×{Height}" : "—";
}

public sealed class RestaurantSignageScreenDto
{
  public int Id { get; set; }
  public string Rfc { get; set; } = string.Empty;
  public int? SiteId { get; set; }
  public string? SiteName { get; set; }
  public string ScreenKey { get; set; } = string.Empty;
  public string Name { get; set; } = string.Empty;
  public int RotationSeconds { get; set; } = RestaurantSignageDefaults.RotationSeconds;
  public int RefreshSeconds { get; set; } = RestaurantSignageDefaults.RefreshSeconds;
  public int TransitionMs { get; set; } = RestaurantSignageDefaults.TransitionMs;
  public int SortOrder { get; set; }
  public bool IsEnabled { get; set; } = true;
  public DateTime UpdatedAt { get; set; }
  public string? UpdatedBy { get; set; }
  public List<RestaurantSignageImageDto> Images { get; set; } = [];

  public bool RotatesImages => Images.Count(image => image.IsEnabled) > 1;
}

public sealed class RestaurantSignageScreenSaveRequest
{
  [Required] public string Rfc { get; set; } = string.Empty;
  public int? Id { get; set; }
  public int? SiteId { get; set; }

  [Required(ErrorMessage = "Captura la clave de la pantalla.")]
  [StringLength(RestaurantSignageDefaults.MaxKeyLength, MinimumLength = RestaurantSignageDefaults.MinKeyLength)]
  public string ScreenKey { get; set; } = string.Empty;

  [Required(ErrorMessage = "Captura el nombre de la pantalla.")]
  [StringLength(120)]
  public string Name { get; set; } = string.Empty;

  [Range(3, 3600, ErrorMessage = "La rotación debe estar entre 3 y 3600 segundos.")]
  public int RotationSeconds { get; set; } = RestaurantSignageDefaults.RotationSeconds;

  [Range(30, 86400, ErrorMessage = "El refresco debe estar entre 30 y 86400 segundos.")]
  public int RefreshSeconds { get; set; } = RestaurantSignageDefaults.RefreshSeconds;

  [Range(0, 5000)] public int TransitionMs { get; set; } = RestaurantSignageDefaults.TransitionMs;
  public int SortOrder { get; set; }
  public bool IsEnabled { get; set; } = true;
  public string? UpdatedBy { get; set; }
}

public sealed class RestaurantSignageImageUploadRequest
{
  [Required] public string Rfc { get; set; } = string.Empty;
  public int ScreenId { get; set; }
  [Required, StringLength(260)] public string FileName { get; set; } = string.Empty;
  public byte[] Content { get; set; } = [];
  [StringLength(300)] public string? AltText { get; set; }
  public string? UpdatedBy { get; set; }
}

public sealed class RestaurantSignageOrderRequest
{
  [Required] public string Rfc { get; set; } = string.Empty;
  public int ScreenId { get; set; }
  public List<long> ImageIdsInOrder { get; set; } = [];
}

public sealed class RestaurantSignagePublicImageDto
{
  public long Id { get; set; }
  public string ContentHash { get; set; } = string.Empty;
  public string? AltText { get; set; }
}

public sealed class RestaurantSignagePublicScreenDto
{
  public string Rfc { get; set; } = string.Empty;
  public string ScreenKey { get; set; } = string.Empty;
  public string Name { get; set; } = string.Empty;
  public int RotationSeconds { get; set; } = RestaurantSignageDefaults.RotationSeconds;
  public int RefreshSeconds { get; set; } = RestaurantSignageDefaults.RefreshSeconds;
  public int TransitionMs { get; set; } = RestaurantSignageDefaults.TransitionMs;
  public List<RestaurantSignagePublicImageDto> Images { get; set; } = [];
}

public readonly record struct RestaurantSignageImagePayload(byte[] Bytes, string ContentType, string ContentHash);
