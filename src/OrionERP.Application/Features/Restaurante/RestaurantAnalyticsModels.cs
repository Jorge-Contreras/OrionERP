namespace OrionERP.Application.Features.Restaurante;

/// <summary>Rango consultado por los reportes contables del Restaurante.</summary>
public sealed class RestaurantAnalyticsQuery
{
  public string Rfc { get; set; } = string.Empty;
  public int SiteId { get; set; }
  public DateTime From { get; set; }
  public DateTime To { get; set; }
}

/// <summary>Un código agrupador nivel 1 con su descripción oficial del Anexo 24.</summary>
public sealed class RestaurantAgrupadorDto
{
  public string Nivel1 { get; set; } = string.Empty;
  public string Descripcion { get; set; } = string.Empty;
  public short Signo { get; set; } = 1;
  public bool Incluido { get; set; } = true;
  public bool EsPersonalizado { get; set; }
  public decimal Cargos { get; set; }
  public decimal Abonos { get; set; }
  public int Movimientos { get; set; }
  public decimal Saldo => Cargos - Abonos;
}

/// <summary>Fila editable del mapeo concepto ↔ agrupador.</summary>
public sealed class RestaurantAgrupadorMapRowDto
{
  public int Id { get; set; }
  public string ConceptoClave { get; set; } = string.Empty;
  public string Nivel1 { get; set; } = string.Empty;
  public string Nivel1Descripcion { get; set; } = string.Empty;
  public short Signo { get; set; } = 1;
  public bool Incluido { get; set; } = true;
  public int Orden { get; set; }
  public bool EsPersonalizado { get; set; }
  public int Movimientos { get; set; }
  public decimal Importe { get; set; }
}

/// <summary>Un concepto de reporte con los agrupadores que lo componen.</summary>
public sealed class RestaurantAgrupadorConceptoDto
{
  public string ConceptoClave { get; set; } = string.Empty;
  public string Etiqueta { get; set; } = string.Empty;
  public string Grupo { get; set; } = string.Empty;
  public short Signo { get; set; } = 1;
  public int Orden { get; set; }
  public IReadOnlyList<RestaurantAgrupadorMapRowDto> Agrupadores { get; set; } = [];

  public IReadOnlyList<string> CodigosIncluidos =>
    Agrupadores.Where(row => row.Incluido).Select(row => row.Nivel1).ToList();
}

/// <summary>Mapeo completo de un RFC más las alertas de cobertura.</summary>
public sealed class RestaurantAgrupadorMapDto
{
  public IReadOnlyList<RestaurantAgrupadorConceptoDto> Conceptos { get; set; } = [];

  /// <summary>Agrupadores con movimientos en el periodo que ningún concepto incluye.</summary>
  public IReadOnlyList<RestaurantAgrupadorDto> FueraDelMapeo { get; set; } = [];

  public decimal ImporteFueraDelMapeo => FueraDelMapeo.Sum(row => row.Cargos + row.Abonos);
}

/// <summary>Renglón del estado de resultados por concepto.</summary>
public sealed class RestaurantPnlRowDto
{
  public string ConceptoClave { get; set; } = string.Empty;
  public string Etiqueta { get; set; } = string.Empty;
  public short Signo { get; set; } = 1;
  public int Orden { get; set; }
  public IReadOnlyList<string> Agrupadores { get; set; } = [];
  public decimal Periodo { get; set; }
  public decimal PeriodoAnterior { get; set; }
  public decimal Acumulado { get; set; }
  public int Movimientos { get; set; }
  public decimal PorcentajeSobreVenta { get; set; }
  public bool EsSubtotal { get; set; }
  public bool SinCuentas { get; set; }
}

/// <summary>Estado de resultados por código agrupador SAT.</summary>
public sealed class RestaurantPnlDto
{
  public DateTime From { get; set; }
  public DateTime To { get; set; }
  public IReadOnlyList<RestaurantPnlRowDto> Rows { get; set; } = [];
  public decimal Ingresos { get; set; }
  public decimal Costo { get; set; }
  public decimal MargenBruto { get; set; }
  public decimal Gastos { get; set; }
  public decimal Resultado { get; set; }
  public decimal CargosTotales { get; set; }
  public decimal AbonosTotales { get; set; }
  public bool Cuadrada => Math.Abs(CargosTotales - AbonosTotales) <= 0.01m;
}

/// <summary>Nodo del desglose nivel 1 → nivel 2 → nivel 3 → póliza.</summary>
public sealed class RestaurantLedgerNodeDto
{
  public string Nivel1 { get; set; } = string.Empty;
  public string? Nivel2 { get; set; }
  public string? Nivel3 { get; set; }
  public string Descripcion { get; set; } = string.Empty;
  public decimal Cargos { get; set; }
  public decimal Abonos { get; set; }
  public int Movimientos { get; set; }
  public decimal Saldo => Cargos - Abonos;

  /// <summary>Verdadero cuando el movimiento se registró contra una cuenta de encabezado.</summary>
  public bool EsEncabezado => Nivel2 == "00" || Nivel2 == "000";
}

/// <summary>Movimiento individual con su póliza, para el último nivel del desglose.</summary>
public sealed class RestaurantLedgerEntryDto
{
  public int TransaccionId { get; set; }
  public DateTime Fecha { get; set; }
  public string TipoPoliza { get; set; } = string.Empty;
  public string Concepto { get; set; } = string.Empty;
  public string Cuenta { get; set; } = string.Empty;
  public string NombreCuenta { get; set; } = string.Empty;
  public string? Referencia { get; set; }
  public decimal Debe { get; set; }
  public decimal Haber { get; set; }
}

/// <summary>Renglón del puente entre la operación del POS y la contabilidad.</summary>
public sealed class RestaurantReconciliationRowDto
{
  public string Concepto { get; set; } = string.Empty;
  public string Detalle { get; set; } = string.Empty;
  public decimal Operacion { get; set; }
  public decimal Contabilidad { get; set; }
  public IReadOnlyList<string> Agrupadores { get; set; } = [];
  public bool AgrupadoresSinMovimiento { get; set; }
  public bool NoComparable { get; set; }
  public decimal Diferencia => Contabilidad - Operacion;
  public bool Conciliado => !NoComparable && Math.Abs(Diferencia) <= 0.01m;
}

/// <summary>Conciliación operación ↔ contabilidad del periodo.</summary>
public sealed class RestaurantReconciliationDto
{
  public IReadOnlyList<RestaurantReconciliationRowDto> Rows { get; set; } = [];
  public int OrdenesPagadas { get; set; }
  public int OrdenesLigadas { get; set; }
  public int DiasConVenta { get; set; }
  public int DiasConPoliza { get; set; }
  public decimal DiferenciaCajaNeta { get; set; }
  public decimal DiferenciaCajaAbsoluta { get; set; }
  public int TurnosConDiferencia { get; set; }
  public int TurnosSinAprobar { get; set; }
  public int OrdenesSinLigar => Math.Max(0, OrdenesPagadas - OrdenesLigadas);
}

/// <summary>Punto de la serie diaria que compara venta operativa contra asiento contable.</summary>
public sealed class RestaurantDailyLedgerPointDto
{
  public DateTime Fecha { get; set; }
  public int Ordenes { get; set; }
  public decimal VentaPos { get; set; }
  public decimal IvaPos { get; set; }
  public decimal CostoRecalculado { get; set; }
  public decimal IngresoContable { get; set; }
  public bool TienePolizaLigada { get; set; }
}

/// <summary>Banda de indicadores del resumen contable.</summary>
public sealed class RestaurantAccountingSummaryDto
{
  public DateTime From { get; set; }
  public DateTime To { get; set; }
  public decimal VentaNetaPos { get; set; }
  public decimal IvaTrasladadoPos { get; set; }
  public decimal DescuentosPos { get; set; }
  public decimal CostoRecalculado { get; set; }
  public decimal IngresoContable { get; set; }
  public decimal GastoContable { get; set; }
  public decimal ResultadoContable { get; set; }
  public int OrdenesPagadas { get; set; }
  public decimal TicketPromedio => OrdenesPagadas == 0 ? 0 : VentaNetaPos / OrdenesPagadas;
  public decimal MargenBruto => VentaNetaPos - CostoRecalculado;
  public decimal FoodCostPorcentaje => VentaNetaPos == 0 ? 0 : CostoRecalculado / VentaNetaPos * 100m;
  public IReadOnlyList<string> AgrupadoresIngreso { get; set; } = [];
  public IReadOnlyList<string> AgrupadoresIva { get; set; } = [];
  public IReadOnlyList<string> AgrupadoresCosto { get; set; } = [];
  public IReadOnlyList<string> AgrupadoresGasto { get; set; } = [];
}

/// <summary>Costo recalculado desde la receta activa, por producto vendido.</summary>
public sealed class RestaurantRecipeCostDto
{
  public int ProductId { get; set; }
  public string Producto { get; set; } = string.Empty;
  public decimal UnidadesVendidas { get; set; }
  public decimal Venta { get; set; }
  public decimal PrecioLista { get; set; }
  public decimal CostoCongelado { get; set; }
  public decimal CostoRecalculado { get; set; }
  public decimal RendimientoReceta { get; set; }
  public string? UnidadRendimiento { get; set; }
  public int ComponentesSinConversion { get; set; }
  public bool TieneReceta { get; set; }

  /// <summary>«Receta» cuando el costo viene del BOM activo, «Compra» cuando es un producto de reventa, «Sin costo» cuando no hay de dónde tomarlo.</summary>
  public string CostoOrigen { get; set; } = "Sin costo";

  public bool SinCosto => CostoRecalculado <= 0.01m;
  public decimal Deriva => CostoCongelado - CostoRecalculado;
  public decimal FoodCostPorcentaje => PrecioLista == 0 ? 0 : CostoRecalculado / PrecioLista * 100m;
  public decimal CostoVendido => CostoRecalculado * UnidadesVendidas;
}

/// <summary>Reporte completo de la pestaña contable.</summary>
public sealed class RestaurantAccountingReportDto
{
  public RestaurantAccountingSummaryDto Summary { get; set; } = new();
  public RestaurantPnlDto Pnl { get; set; } = new();
  public RestaurantReconciliationDto Reconciliation { get; set; } = new();
  public RestaurantAgrupadorMapDto Map { get; set; } = new();
  public IReadOnlyList<RestaurantDailyLedgerPointDto> DailySeries { get; set; } = [];
  public IReadOnlyList<RestaurantAgrupadorDto> Agrupadores { get; set; } = [];
}
