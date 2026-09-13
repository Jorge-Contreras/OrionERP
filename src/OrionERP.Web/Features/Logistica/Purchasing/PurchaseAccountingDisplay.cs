using System.Globalization;

namespace OrionERP.Web.Features.Logistica.Purchasing;

/// <summary>Cómo se nombra el estado contable de una compra en las pantallas de Compras.</summary>
internal static class PurchaseAccountingDisplay
{
  public static string Money(decimal amount) => amount.ToString("C2", CultureInfo.CurrentCulture);

  public static string StatusLabel(decimal recibido, decimal contabilizado, decimal porContabilizar)
    => (recibido, porContabilizar) switch
    {
      (<= 0m, _) => "Sin importes recibidos",
      (_, <= 0m) => "En contabilidad",
      _ when contabilizado > 0m => "Póliza parcial",
      _ => "Falta póliza"
    };

  public static string StatusBadgeClass(decimal recibido, decimal porContabilizar)
    => (recibido, porContabilizar) switch
    {
      (<= 0m, _) => "badge text-bg-secondary",
      (_, <= 0m) => "badge text-bg-success",
      _ => "badge text-bg-warning"
    };
}
