using System.Globalization;

namespace OrionERP.Web.Shared;

/// <summary>
/// Parses decimal text entered interactively without treating the other common decimal
/// separator as a thousands separator. This lets a user enter either 12.50 or 12,50
/// regardless of the culture selected by the server-side circuit.
/// </summary>
public static class DecimalTextParser
{
  private const NumberStyles DecimalStyles =
    NumberStyles.AllowLeadingWhite |
    NumberStyles.AllowTrailingWhite |
    NumberStyles.AllowLeadingSign |
    NumberStyles.AllowDecimalPoint;

  public static bool TryParse(string? text, out decimal? value)
    => TryParse(text, CultureInfo.CurrentCulture, out value);

  public static bool TryParse(string? text, CultureInfo culture, out decimal? value)
  {
    ArgumentNullException.ThrowIfNull(culture);

    var trimmed = text?.Trim();
    if (string.IsNullOrEmpty(trimmed))
    {
      value = null;
      return true;
    }

    if (TryParseUsing(trimmed, culture, out value))
    {
      return true;
    }

    if (!Equals(culture, CultureInfo.InvariantCulture)
        && TryParseUsing(trimmed, CultureInfo.InvariantCulture, out value))
    {
      return true;
    }

    var normalized = NormalizeCommonSeparators(trimmed);
    if (!string.Equals(normalized, trimmed, StringComparison.Ordinal)
        && TryParseUsing(normalized, CultureInfo.InvariantCulture, out value))
    {
      return true;
    }

    value = null;
    return false;
  }

  private static bool TryParseUsing(string text, CultureInfo culture, out decimal? value)
  {
    if (decimal.TryParse(text, DecimalStyles, culture, out var parsed))
    {
      value = parsed;
      return true;
    }

    value = null;
    return false;
  }

  private static string NormalizeCommonSeparators(string text)
  {
    var lastDot = text.LastIndexOf('.');
    var lastComma = text.LastIndexOf(',');

    if (lastDot >= 0 && lastComma >= 0)
    {
      var decimalSeparator = lastDot > lastComma ? '.' : ',';
      var groupSeparator = decimalSeparator == '.' ? ',' : '.';
      return text.Replace(groupSeparator.ToString(), string.Empty, StringComparison.Ordinal)
        .Replace(decimalSeparator, '.');
    }

    if (lastComma >= 0)
    {
      return text.Count(character => character == ',') == 1
        ? text.Replace(',', '.')
        : text;
    }

    return text;
  }
}
