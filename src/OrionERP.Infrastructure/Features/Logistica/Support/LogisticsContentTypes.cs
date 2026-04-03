using System.IO;

namespace OrionERP.Infrastructure.Features.Logistica.Support;

internal static class LogisticsContentTypes
{
  public static string Normalize(string? contentType, string? fileName = null, byte[]? bytes = null)
  {
    if (!string.IsNullOrWhiteSpace(contentType))
    {
      return contentType.Trim();
    }

    var byFileName = FromFileName(fileName);
    if (!string.Equals(byFileName, "application/octet-stream", StringComparison.OrdinalIgnoreCase))
    {
      return byFileName;
    }

    if (bytes is { Length: > 3 })
    {
      if (bytes[0] == 0xFF && bytes[1] == 0xD8)
      {
        return "image/jpeg";
      }

      if (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
      {
        return "image/png";
      }

      if (bytes[0] == 0x25 && bytes[1] == 0x50 && bytes[2] == 0x44 && bytes[3] == 0x46)
      {
        return "application/pdf";
      }
    }

    return "application/octet-stream";
  }

  public static string FromFileName(string? fileName)
  {
    var extension = string.IsNullOrWhiteSpace(fileName)
      ? string.Empty
      : Path.GetExtension(fileName).Trim().ToLowerInvariant();

    return extension switch
    {
      ".jpg" or ".jpeg" => "image/jpeg",
      ".png" => "image/png",
      ".gif" => "image/gif",
      ".bmp" => "image/bmp",
      ".webp" => "image/webp",
      ".pdf" => "application/pdf",
      _ => "application/octet-stream"
    };
  }
}
