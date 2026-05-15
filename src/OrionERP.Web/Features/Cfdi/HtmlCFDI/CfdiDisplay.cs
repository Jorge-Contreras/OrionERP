using System.Globalization;
using OrionERP.Application.Features.Cfdi.HtmlCFDI;

namespace OrionERP.Web.Features.Cfdi.HtmlCFDI;

internal static class CfdiDisplay
{
  private static readonly CultureInfo MoneyCulture = CultureInfo.GetCultureInfo("es-MX");

  public static string Safe(string? value)
    => string.IsNullOrWhiteSpace(value) ? "-" : value.Trim();

  public static string FirstNonEmpty(params string?[] values)
  {
    foreach (var value in values)
    {
      if (HasRealValue(value))
      {
        return value!.Trim();
      }
    }

    return "-";
  }

  public static bool HasRealValue(string? value)
    => !string.IsNullOrWhiteSpace(value) && !string.Equals(value.Trim(), "-", StringComparison.Ordinal);

  public static string Amount(string? value, string? currency = null)
  {
    if (string.IsNullOrWhiteSpace(value))
    {
      return "-";
    }

    var trimmed = value.Trim();
    if (!TryParseDecimal(trimmed, out var amount))
    {
      return trimmed;
    }

    var normalizedCurrency = string.IsNullOrWhiteSpace(currency) ? "MXN" : currency.Trim();
    var prefix = string.Equals(normalizedCurrency, "MXN", StringComparison.OrdinalIgnoreCase)
      ? "$"
      : normalizedCurrency.ToUpperInvariant();

    return $"{prefix} {amount.ToString("N2", MoneyCulture)}";
  }

  public static string TypeLabel(string? value)
    => value?.Trim().ToUpperInvariant() switch
    {
      "I" => "I - Ingreso",
      "E" => "E - Egreso",
      "P" => "P - Pago",
      "N" => "N - Nómina",
      "T" => "T - Traslado",
      _ => Safe(value)
    };

  public static string TypeName(string? value)
    => value?.Trim().ToUpperInvariant() switch
    {
      "I" => "Factura de ingreso",
      "E" => "Nota de crédito",
      "P" => "Complemento de pago",
      "N" => "Nómina",
      "T" => "Traslado",
      _ => "CFDI"
    };

  public static string SerieFolio(CfdiReadableDocument document)
  {
    var serie = string.IsNullOrWhiteSpace(document.Serie) ? null : document.Serie.Trim();
    var folio = string.IsNullOrWhiteSpace(document.Folio) ? null : document.Folio.Trim();

    if (!string.IsNullOrWhiteSpace(serie) && !string.IsNullOrWhiteSpace(folio))
    {
      return $"{serie}-{folio}";
    }

    return FirstNonEmpty(folio, serie);
  }

  public static string PartyRegimen(CfdiParty? party)
    => FirstNonEmpty(party?.RegimenFiscal, party?.RegimenFiscalReceptor);

  private static bool TryParseDecimal(string value, out decimal amount)
    => decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out amount)
       || decimal.TryParse(value, NumberStyles.Number, MoneyCulture, out amount)
       || decimal.TryParse(value, NumberStyles.Number, CultureInfo.CurrentCulture, out amount);
}
