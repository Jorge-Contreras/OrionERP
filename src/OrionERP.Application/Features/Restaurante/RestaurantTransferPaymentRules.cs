namespace OrionERP.Application.Features.Restaurante;

/// <summary>
/// Normaliza, valida y da formato a los datos bancarios que el punto de venta
/// entrega al cliente cuando paga con transferencia electrónica de fondos (SPEI).
/// Los dígitos se guardan sin separadores y sólo se agrupan al imprimirlos.
/// </summary>
public static class RestaurantTransferPaymentRules
{
  public const int ClabeLength = 18;
  public const int MinimumAccountLength = 6;
  public const int MaximumAccountLength = 20;
  public const int MinimumCardLength = 15;
  public const int MaximumCardLength = 19;

  // Ponderadores oficiales de la CLABE: se repiten 3-7-1 sobre los primeros 17 dígitos.
  private static readonly int[] ClabeWeights = [3, 7, 1];

  public static string? NormalizeDigits(string? value)
  {
    if (string.IsNullOrWhiteSpace(value))
    {
      return null;
    }

    var digits = new string(value.Where(char.IsAsciiDigit).ToArray());
    return digits.Length == 0 ? null : digits;
  }

  public static string? NormalizeText(string? value)
  {
    var trimmed = value?.Trim();
    return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
  }

  /// <summary>
  /// Verifica los 18 dígitos y el dígito verificador de la CLABE. Un error de
  /// captura aquí se traduce en una transferencia rechazada por el banco.
  /// </summary>
  public static bool IsValidClabe(string? value)
  {
    var digits = NormalizeDigits(value);
    if (digits is null || digits.Length != ClabeLength)
    {
      return false;
    }

    var sum = 0;
    for (var index = 0; index < ClabeLength - 1; index++)
    {
      sum += (digits[index] - '0') * ClabeWeights[index % ClabeWeights.Length] % 10;
    }

    var expected = (10 - (sum % 10)) % 10;
    return expected == digits[^1] - '0';
  }

  /// <summary>Valida el número de tarjeta con el algoritmo de Luhn.</summary>
  public static bool IsValidCardNumber(string? value)
  {
    var digits = NormalizeDigits(value);
    if (digits is null || digits.Length is < MinimumCardLength or > MaximumCardLength)
    {
      return false;
    }

    var sum = 0;
    var doubling = false;
    for (var index = digits.Length - 1; index >= 0; index--)
    {
      var digit = digits[index] - '0';
      if (doubling)
      {
        digit *= 2;
        if (digit > 9)
        {
          digit -= 9;
        }
      }

      sum += digit;
      doubling = !doubling;
    }

    return sum % 10 == 0;
  }

  public static bool IsValidAccountNumber(string? value)
  {
    var digits = NormalizeDigits(value);
    return digits is not null && digits.Length is >= MinimumAccountLength and <= MaximumAccountLength;
  }

  /// <summary>Agrupa la CLABE como banco, plaza, cuenta y dígito verificador.</summary>
  public static string FormatClabe(string? value)
  {
    var digits = NormalizeDigits(value);
    if (digits is null)
    {
      return string.Empty;
    }

    return digits.Length == ClabeLength
      ? $"{digits[..3]} {digits[3..6]} {digits[6..17]} {digits[17]}"
      : GroupDigits(digits, 4);
  }

  public static string FormatCardNumber(string? value) => GroupDigits(NormalizeDigits(value), 4);

  public static string FormatAccountNumber(string? value) => GroupDigits(NormalizeDigits(value), 4);

  private static string GroupDigits(string? digits, int size)
  {
    if (string.IsNullOrEmpty(digits))
    {
      return string.Empty;
    }

    var groups = new List<string>((digits.Length / size) + 1);
    for (var index = 0; index < digits.Length; index += size)
    {
      groups.Add(digits.Substring(index, Math.Min(size, digits.Length - index)));
    }

    return string.Join(' ', groups);
  }
}
