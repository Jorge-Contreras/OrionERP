using OrionERP.Application.Features.Contabilidad.Transacciones;

namespace OrionERP.Application.Features.Logistica.Purchasing;

public static class PurchaseAccountingLinkOrigins
{
  /// <summary>La póliza la creó Compras con las cuentas de Ajustes.</summary>
  public const string Generated = "Generated";

  /// <summary>La póliza ya existía y alguien la ligó a la compra.</summary>
  public const string Manual = "Manual";
}

/// <summary>Estado contable de una compra: lo recibido contra lo que ya está en pólizas.</summary>
public sealed class PurchaseAccountingSummaryDto
{
  public int PurchaseOrderId { get; set; }
  public string PurchaseOrderCode { get; set; } = string.Empty;
  public string Status { get; set; } = string.Empty;
  public string VendorName { get; set; } = string.Empty;
  public string? VendorRfc { get; set; }
  public decimal Recibido { get; set; }
  public decimal RecibidoSubtotal { get; set; }
  public decimal RecibidoIva { get; set; }
  public decimal Contabilizado { get; set; }
  public decimal PorContabilizar { get; set; }
  public DateTime? UltimaRecepcion { get; set; }

  /// <summary>Nombres de las cuentas de Ajustes que faltan para generar pólizas automáticas.</summary>
  public IReadOnlyList<string> CuentasFaltantes { get; set; } = [];

  /// <summary>
  /// Póliza que un intento anterior creó y no terminó de ligar. El siguiente intento de
  /// generar la retoma en lugar de crear otra.
  /// </summary>
  public int? PolizaSinTerminarId { get; set; }

  public bool CuentasListas => CuentasFaltantes.Count == 0;
}

/// <summary>Póliza ligada a una compra, vista desde Compras.</summary>
public sealed class PurchaseAccountingLinkedPolizaDto
{
  public int LinkId { get; set; }
  public int TransaccionId { get; set; }
  public string Origin { get; set; } = PurchaseAccountingLinkOrigins.Manual;
  public decimal MontoAsignado { get; set; }
  public DateTime CreatedAt { get; set; }
  public string? CreatedBy { get; set; }
  public DateTime? Fecha { get; set; }
  public string? Concepto { get; set; }
  public decimal? MontoPoliza { get; set; }
  public string? TipoPoliza { get; set; }
  public int Renglones { get; set; }
  public decimal Cargos { get; set; }
  public decimal Abonos { get; set; }

  public bool FueGenerada => string.Equals(Origin, PurchaseAccountingLinkOrigins.Generated, StringComparison.Ordinal);

  /// <summary>Tiene renglones y cargos igual a abonos, con la tolerancia del validador de cuadre.</summary>
  public bool EstaCuadrada => Renglones > 0 && Math.Abs(Cargos - Abonos) <= MovimientosCuadreValidator.Tolerance;
}

/// <summary>Póliza con saldo disponible que podría cubrir una compra.</summary>
public sealed class PurchaseAccountingCandidateDto
{
  public int TransaccionId { get; set; }
  public DateTime Fecha { get; set; }
  public string? Concepto { get; set; }
  public string? TipoPoliza { get; set; }
  public decimal Monto { get; set; }
  public decimal Asignado { get; set; }
  public decimal Disponible { get; set; }
}

public sealed class PurchaseAccountingWorkspaceDto
{
  public PurchaseAccountingSummaryDto Summary { get; set; } = new();
  public List<PurchaseAccountingLinkedPolizaDto> Polizas { get; } = [];
  public List<FormaPagoLookupDto> FormasPago { get; } = [];

  /// <summary>"99 Por definir" cuando existe en el catálogo de la empresa.</summary>
  public string? FormaPagoSugerida { get; set; }
}

public sealed class PurchaseAccountingGenerateRequest
{
  public int PurchaseOrderId { get; set; }
  public DateTime Fecha { get; set; }
  public string? FormaPago { get; set; }
}

public sealed class PurchaseAccountingLinkRequest
{
  public int PurchaseOrderId { get; set; }
  public int TransaccionId { get; set; }
  public decimal Monto { get; set; }
}

/// <summary>Lo recibido y lo contabilizado de una compra, para marcar la lista de Compras.</summary>
public sealed class PurchaseOrderPostingStatusDto
{
  public int PurchaseOrderId { get; set; }
  public decimal Recibido { get; set; }
  public decimal Contabilizado { get; set; }

  public decimal PorContabilizar => Math.Max(Recibido - Contabilizado, 0m);
}

public sealed class PurchasePolizaSummaryDto
{
  public int TransaccionId { get; set; }
  public DateTime Fecha { get; set; }
  public string? Concepto { get; set; }
  public decimal Monto { get; set; }
  public decimal Asignado { get; set; }

  public decimal Disponible => Monto - Asignado;
}

/// <summary>Compra ligada a una póliza, vista desde Transacciones.</summary>
public sealed class PurchaseAccountingLinkedOrderDto
{
  public int LinkId { get; set; }
  public int PurchaseOrderId { get; set; }
  public string PurchaseOrderCode { get; set; } = string.Empty;
  public string VendorName { get; set; } = string.Empty;
  public DateTime OrderDate { get; set; }
  public decimal MontoAsignado { get; set; }
  public string Origin { get; set; } = PurchaseAccountingLinkOrigins.Manual;

  public bool FueGenerada => string.Equals(Origin, PurchaseAccountingLinkOrigins.Generated, StringComparison.Ordinal);
}

public sealed class PurchasePolizaLinksDto
{
  public PurchasePolizaSummaryDto Summary { get; set; } = new();
  public List<PurchaseAccountingLinkedOrderDto> PurchaseOrders { get; } = [];

  /// <summary>Vínculos a compras de una sede que el usuario no ve; sí cuentan en <see cref="PurchasePolizaSummaryDto.Asignado"/>.</summary>
  public int ComprasNoVisibles { get; set; }
}

/// <summary>Compra recibida con saldo por contabilizar, candidata a ligarse a una póliza.</summary>
public sealed class PurchaseOrderAccountingCandidateDto
{
  public int PurchaseOrderId { get; set; }
  public string PurchaseOrderCode { get; set; } = string.Empty;
  public string VendorName { get; set; } = string.Empty;
  public DateTime OrderDate { get; set; }
  public DateTime? UltimaRecepcion { get; set; }
  public decimal Recibido { get; set; }
  public decimal Contabilizado { get; set; }
  public decimal PorContabilizar { get; set; }
}

/// <summary>Reparto del monto por contabilizar entre subtotal e IVA.</summary>
public readonly record struct PurchasePolicyAmounts(decimal Subtotal, decimal Iva, decimal Total)
{
  /// <summary>
  /// Conserva la proporción de IVA de lo recibido: si una póliza parcial ya cubrió parte de la
  /// compra, el resto lleva el mismo peso de impuesto. El IVA absorbe el centavo de redondeo
  /// para que cargos y abonos cuadren exactos.
  /// </summary>
  public static PurchasePolicyAmounts ForPending(decimal recibidoSubtotal, decimal recibido, decimal porContabilizar)
  {
    var total = decimal.Round(porContabilizar, 2, MidpointRounding.AwayFromZero);
    if (total <= 0m || recibido <= 0m) return new(0m, 0m, 0m);

    var subtotal = decimal.Round(recibidoSubtotal * total / recibido, 2, MidpointRounding.AwayFromZero);
    subtotal = Math.Clamp(subtotal, 0m, total);
    return new(subtotal, total - subtotal, total);
  }
}
