using System;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace OrionERP.Web.Identity;

public static class PasswordResetLinkCodec
{
  public static string Encode(string userId, string code)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(userId);
    ArgumentException.ThrowIfNullOrWhiteSpace(code);

    var json = JsonSerializer.Serialize(new PasswordResetLinkPayload(userId, code));
    return WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(json));
  }

  public static bool TryDecode(string? payload, out string userId, out string code)
  {
    userId = string.Empty;
    code = string.Empty;

    if (string.IsNullOrWhiteSpace(payload))
    {
      return false;
    }

    try
    {
      var json = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(payload));
      var decoded = JsonSerializer.Deserialize<PasswordResetLinkPayload>(json);
      if (decoded is null ||
          string.IsNullOrWhiteSpace(decoded.UserId) ||
          string.IsNullOrWhiteSpace(decoded.Code))
      {
        return false;
      }

      userId = decoded.UserId;
      code = decoded.Code;
      return true;
    }
    catch
    {
      return false;
    }
  }

  private sealed record PasswordResetLinkPayload(string UserId, string Code);
}
