using System;
using System.Collections.Generic;
using System.Linq;

namespace OrionERP.Application.Features.Contabilidad.Transacciones;

/// <summary>
/// Valida que una póliza cuadre (suma de Cargos = suma de Abonos) antes de persistirla.
/// El frontend ya lo verifica, pero una póliza descuadrada corrompe la contabilidad y la
/// e-contabilidad, así que el servidor debe rechazarla como última línea de defensa.
/// </summary>
public static class MovimientosCuadreValidator
{
  /// <summary>Tolerancia por centavos de redondeo acumulados.</summary>
  public const decimal Tolerance = 0.005m;

  /// <returns><c>null</c> si la póliza es válida; el mensaje de negocio si no lo es.</returns>
  public static string? Validate(IReadOnlyCollection<TransaccionMovimientoUpdateItem> movimientos)
  {
    if (movimientos is null || movimientos.Count == 0)
    {
      // Sin renglones no hay nada que descuadre (p. ej. se están borrando todos).
      return null;
    }

    if (movimientos.Any(m => m.Debe < 0m || m.Haber < 0m))
    {
      return "Hay renglones con Cargo o Abono negativo. Corrige los importes: usa el otro lado del asiento en lugar de un número negativo.";
    }

    if (movimientos.Any(m => m.Debe != 0m && m.Haber != 0m))
    {
      return "Hay renglones con Cargo y Abono al mismo tiempo. Cada renglón del asiento debe tener importe en un solo lado.";
    }

    var totalDebe = movimientos.Sum(m => m.Debe);
    var totalHaber = movimientos.Sum(m => m.Haber);
    var diferencia = totalDebe - totalHaber;

    if (Math.Abs(diferencia) > Tolerance)
    {
      return $"El asiento no cuadra: los Cargos suman {totalDebe:N2} y los Abonos {totalHaber:N2} (diferencia {diferencia:N2}). Ajusta los movimientos para que ambos lados coincidan antes de guardar.";
    }

    return null;
  }
}
