using System.Globalization;

namespace OrionERP.Application.Features.Logistica.Materials;

/// <summary>
/// El escalón mínimo de compra de un material, expresado en unidades de compra. Separa dos cosas
/// que antes venían juntas: la conversión —cuántas unidades base trae una presentación— y si el
/// proveedor despacha esa presentación cerrada o partida.
///
/// El papel higiénico se compra por paquetes completos de 24 rollos; el pollo se controla en gramos,
/// se vende por kilo y el proveedor sí despacha 1.5 kg. Los dos tienen presentación de compra y
/// conversión mayor que uno, así que la regla no se puede derivar de los datos: la declara
/// <see cref="MaterialUpsertRequest.PurchaseIncrement"/>.
/// </summary>
public static class MaterialPurchaseIncrement
{
  /// <summary>Solo presentaciones completas. Es el valor por omisión de todo material.</summary>
  public const decimal WholePresentation = 1m;

  /// <summary>Fraccionable: cualquier cantidad es válida.</summary>
  public const decimal Fractional = 0m;

  /// <summary>La escala real de las columnas de cantidad, <c>decimal(18,4)</c>.</summary>
  public const int QuantityScale = 4;

  /// <summary>Un escalón ausente o negativo se lee como presentación completa, nunca como libre.</summary>
  public static decimal Normalize(decimal? increment)
    => increment.HasValue && increment.Value >= 0m
      ? increment.Value
      : WholePresentation;

  public static bool AllowsAnyQuantity(decimal increment)
    => Normalize(increment) <= 0m;

  /// <summary>Cuántas unidades base trae una presentación. Cero o menos se lee como uno.</summary>
  public static decimal NormalizePresentationSize(decimal purchaseQuantity)
    => purchaseQuantity > 0m ? purchaseQuantity : WholePresentation;

  /// <summary>
  /// Un material sin presentación de compra no tiene escalón que respetar: se pide en unidad base y
  /// cualquier cantidad vale. Es la misma compuerta que existía antes de que el escalón fuera
  /// configurable, y la razón de que agregar la columna no cambie ningún material existente.
  /// </summary>
  public static bool UsesPurchasePresentation(decimal purchaseQuantity, string? purchaseUnitName)
    => NormalizePresentationSize(purchaseQuantity) > 1m
      || !string.IsNullOrWhiteSpace(purchaseUnitName);

  /// <summary>
  /// Valida una cantidad expresada en unidad base. Con escalón 1 equivale exactamente a la regla
  /// anterior —el cociente entre cantidad y presentación debe ser entero—; con escalón 0 acepta
  /// cualquier cantidad.
  /// </summary>
  public static bool IsValidQuantity(
    decimal baseQuantity,
    decimal purchaseQuantity,
    string? purchaseUnitName,
    decimal increment)
  {
    var presentationSize = NormalizePresentationSize(purchaseQuantity);
    if (!UsesPurchasePresentation(presentationSize, purchaseUnitName))
    {
      return true;
    }

    var step = Normalize(increment);
    if (step <= 0m)
    {
      return true;
    }

    var quotient = baseQuantity / presentationSize / step;
    return quotient == decimal.Truncate(quotient);
  }

  /// <summary>
  /// Sube la cantidad al siguiente escalón completo. Un material fraccionable se queda con la
  /// necesidad tal cual, que es lo que el reabasto automático debe ordenar.
  /// </summary>
  public static decimal RoundUpToIncrement(decimal baseQuantity, decimal purchaseQuantity, decimal increment)
  {
    if (baseQuantity <= 0m)
    {
      return 0m;
    }

    var presentationSize = NormalizePresentationSize(purchaseQuantity);
    var step = Normalize(increment);
    if (step <= 0m)
    {
      return baseQuantity;
    }

    var stepInBaseUnits = presentationSize * step;
    return decimal.Ceiling(baseQuantity / stepInBaseUnits) * stepInBaseUnits;
  }

  /// <summary>
  /// El requisito en palabras, para el mensaje de error: "1 Paquete", "24.00 Rollo por Paquete" o
  /// "0.50 Kilo" cuando el escalón no es la presentación completa.
  /// </summary>
  public static string DescribeRequirement(
    string? baseUnitName,
    string? purchaseUnitName,
    decimal purchaseQuantity,
    decimal increment,
    CultureInfo? culture = null)
  {
    var effectiveCulture = culture ?? CultureInfo.CurrentCulture;
    var presentationSize = NormalizePresentationSize(purchaseQuantity);
    var step = Normalize(increment);
    var purchaseUnitText = string.IsNullOrWhiteSpace(purchaseUnitName)
      ? "unidad de compra"
      : purchaseUnitName.Trim();
    var baseUnitText = string.IsNullOrWhiteSpace(baseUnitName)
      ? "unidad base"
      : baseUnitName.Trim();

    if (step != WholePresentation)
    {
      return $"{step.ToString("N2", effectiveCulture)} {purchaseUnitText}";
    }

    if (presentationSize == 1m)
    {
      return $"1 {purchaseUnitText}";
    }

    return $"{presentationSize.ToString("N2", effectiveCulture)} {baseUnitText} por {purchaseUnitText}";
  }

  /// <summary>La leyenda que describe el modo en las pantallas de materiales y compras.</summary>
  public static string DescribeMode(decimal increment)
    => AllowsAnyQuantity(increment)
      ? "Se puede comprar fraccionado"
      : "Solo presentaciones completas";
}
