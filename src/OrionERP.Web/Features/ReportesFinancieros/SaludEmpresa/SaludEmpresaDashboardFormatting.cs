using System.Globalization;

namespace OrionERP.Web.Features.ReportesFinancieros.SaludEmpresa;

public enum SaludEmpresaMetricFormat
{
  Money,
  Number,
  Percent
}

public sealed record SaludEmpresaMetricChange(
  string Text,
  string CssClass,
  int Direction,
  bool HasValue,
  bool IsFavorable);

public static class SaludEmpresaDashboardFormatting
{
  private static readonly CultureInfo MexicanCulture = CultureInfo.GetCultureInfo("es-MX");

  public static string FormatValue(decimal? value, SaludEmpresaMetricFormat format)
  {
    if (!value.HasValue)
    {
      return "-";
    }

    return format switch
    {
      SaludEmpresaMetricFormat.Money => value.Value.ToString("C0", MexicanCulture),
      SaludEmpresaMetricFormat.Percent => $"{value.Value.ToString("N1", MexicanCulture)}%",
      _ => value.Value.ToString("N0", MexicanCulture)
    };
  }

  public static SaludEmpresaMetricChange BuildChange(
    decimal? current,
    decimal? baseline,
    SaludEmpresaMetricFormat format,
    bool lowerIsBetter = false)
  {
    if (!current.HasValue || !baseline.HasValue)
    {
      return new SaludEmpresaMetricChange("Sin base", "health-change--neutral", 0, false, false);
    }

    var comparison = new Application.Features.ReportesFinancieros.Models.SaludEmpresaMetricComparison(current, baseline);
    var direction = comparison.Direction;
    var isFavorable = comparison.IsFavorable(lowerIsBetter);

    var cssClass = direction == 0
      ? "health-change--neutral"
      : isFavorable
        ? "health-change--good"
        : "health-change--bad";

    var text = format == SaludEmpresaMetricFormat.Percent
      ? $"{FormatSigned(comparison.Delta!.Value, "N1")} pp"
      : FormatRelativeOrAbsoluteChange(comparison, format);

    return new SaludEmpresaMetricChange(text, cssClass, direction, true, isFavorable);
  }

  private static string FormatRelativeOrAbsoluteChange(
    Application.Features.ReportesFinancieros.Models.SaludEmpresaMetricComparison comparison,
    SaludEmpresaMetricFormat format)
  {
    if (comparison.DeltaPercent.HasValue)
    {
      return $"{FormatSigned(comparison.DeltaPercent.Value, "N1")}%";
    }

    return format switch
    {
      SaludEmpresaMetricFormat.Money => FormatSignedCurrency(comparison.Delta!.Value),
      _ => FormatSigned(comparison.Delta!.Value, "N0")
    };
  }

  private static string FormatSigned(decimal value, string format)
  {
    var formatted = value.ToString(format, MexicanCulture);
    return value > 0 ? $"+{formatted}" : formatted;
  }

  private static string FormatSignedCurrency(decimal value)
  {
    var formatted = value.ToString("C0", MexicanCulture);
    return value > 0 ? $"+{formatted}" : formatted;
  }
}
