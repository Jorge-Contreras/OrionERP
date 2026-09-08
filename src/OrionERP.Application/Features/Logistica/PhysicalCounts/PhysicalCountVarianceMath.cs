namespace OrionERP.Application.Features.Logistica.PhysicalCounts;

/// <summary>
/// Aritmética de contabilización de un conteo físico.
/// El conteo fotografía la existencia esperada al abrirse; para cuando se contabiliza pudo haber
/// movimientos (compras, consumos, traspasos) que cambiaron la existencia real. El ajuste al
/// kardex debe medirse contra la existencia <b>real</b> al momento de contabilizar, no contra la
/// fotografía, o el saldo acumulado del kardex deja de cuadrar.
/// </summary>
public static class PhysicalCountVarianceMath
{
  /// <summary>Tolerancia para considerar que dos cantidades son iguales.</summary>
  public const decimal Epsilon = 0.0001m;

  /// <summary>Delta que se registra en el kardex: lo contado menos la existencia real actual.</summary>
  public static decimal PostingDelta(decimal countedQuantity, decimal systemQuantityNow)
    => countedQuantity - systemQuantityNow;

  /// <summary>
  /// <c>true</c> si la existencia del sistema ya no coincide con la fotografiada al abrir el
  /// conteo: hubo movimientos durante la captura y hay que avisar al usuario.
  /// </summary>
  public static bool MovedDuringCount(decimal expectedAtOpen, decimal systemQuantityNow)
    => decimal.Abs(systemQuantityNow - expectedAtOpen) > Epsilon;
}
