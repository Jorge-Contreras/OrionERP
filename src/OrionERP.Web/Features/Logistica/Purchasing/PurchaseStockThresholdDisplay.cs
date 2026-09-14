using System.Globalization;
using OrionERP.Application.Features.Logistica.Purchasing;

namespace OrionERP.Web.Features.Logistica.Purchasing;

/// <summary>
/// Una línea discreta con la existencia, el mínimo y el máximo de un material. Explica por qué
/// Auto PO lo metió a la compra sin competir con las cantidades de la orden, y le recuerda a quien
/// revisa que esos límites se pueden reconsiderar.
/// </summary>
public static class PurchaseStockThresholdDisplay
{
  public const string AdjustHint = "El mínimo y el máximo se ajustan en Logística › Ubicaciones.";

  /// <param name="balances">Saldos de las ubicaciones consideradas; las que no tienen saldo simplemente no vienen.</param>
  /// <param name="locationCount">Cuántas ubicaciones abarca el texto: una fila del reparto o la suma de la línea.</param>
  public static PurchaseStockHint Describe(
    IReadOnlyCollection<PurchaseStockThresholdDto> balances,
    int locationCount,
    string? baseUnitName,
    CultureInfo culture)
  {
    ArgumentNullException.ThrowIfNull(balances);
    ArgumentNullException.ThrowIfNull(culture);

    var unit = string.IsNullOrWhiteSpace(baseUnitName) ? null : baseUnitName.Trim();
    var unitSuffix = unit is null ? string.Empty : $" {unit}";
    var unitClause = unit is null ? string.Empty : $", en {unit}";
    var title = locationCount > 1
      ? $"Existencia de hoy y mínimo/máximo sumados de las {locationCount} ubicaciones del reparto{unitClause}. {AdjustHint}"
      : $"Existencia de hoy y mínimo/máximo configurados en esta ubicación{unitClause}. {AdjustHint}";
    var stock = Format(balances.Sum(balance => balance.Quantity), culture);

    var minimums = balances.Where(balance => balance.MinQuantity.HasValue).ToList();
    var maximums = balances.Where(balance => balance.MaxQuantity.HasValue).ToList();
    if (minimums.Count == 0 && maximums.Count == 0)
    {
      return new PurchaseStockHint($"Existencia {stock}{unitSuffix} · sin mínimo ni máximo", title, IsBelowMinimum: false);
    }

    var minimum = minimums.Count == 0 ? "—" : Format(minimums.Sum(balance => balance.MinQuantity!.Value), culture);
    var maximum = maximums.Count == 0 ? "—" : Format(maximums.Sum(balance => balance.MaxQuantity!.Value), culture);

    // Mismo criterio que Auto PO y Ubicaciones: llegar al mínimo ya cuenta como bajo mínimo.
    var isBelowMinimum = minimums.Any(balance => balance.Quantity <= balance.MinQuantity!.Value);

    return new PurchaseStockHint($"Existencia {stock} · Mín {minimum} · Máx {maximum}{unitSuffix}", title, isBelowMinimum);
  }

  private static string Format(decimal value, CultureInfo culture)
    => value.ToString("#,0.##", culture);
}

public sealed record PurchaseStockHint(string Text, string Title, bool IsBelowMinimum);
