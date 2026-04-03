using System.Globalization;
using System.Text;

namespace OrionERP.Application.Features.Reservaciones.OpenClaw;

public static class OpenClawReservationNaming
{
  public static string NormalizeLookupKey(string? value)
  {
    if (string.IsNullOrWhiteSpace(value))
    {
      return string.Empty;
    }

    var normalized = value.Trim().Normalize(NormalizationForm.FormD);
    var builder = new StringBuilder(normalized.Length);
    var lastWasSpace = false;

    foreach (var character in normalized)
    {
      var category = CharUnicodeInfo.GetUnicodeCategory(character);
      if (category == UnicodeCategory.NonSpacingMark)
      {
        continue;
      }

      if (char.IsLetterOrDigit(character))
      {
        builder.Append(char.ToUpperInvariant(character));
        lastWasSpace = false;
        continue;
      }

      if (lastWasSpace)
      {
        continue;
      }

      builder.Append(' ');
      lastWasSpace = true;
    }

    return builder.ToString().Trim();
  }
}
