namespace OrionERP.Application.Features.Restaurante;

/// <summary>
/// Conceptos de reporte del Restaurante y los códigos agrupadores nivel 1 del
/// Anexo 24 que los componen. La tabla restaurante.ReporteAgrupadorMapa manda;
/// esta clase provee la semilla y las etiquetas para la interfaz.
/// </summary>
public static class RestaurantAgrupadorConceptos
{
  // Estado de resultados
  public const string IngresosVenta = "IngresosVenta";
  public const string DevolucionesDescuentos = "DevolucionesDescuentos";
  public const string OtrosIngresos = "OtrosIngresos";
  public const string CostoVenta = "CostoVenta";
  public const string Compras = "Compras";
  public const string DevolucionesCompras = "DevolucionesCompras";
  public const string OtrosCostos = "OtrosCostos";
  public const string GastosGenerales = "GastosGenerales";
  public const string GastosVenta = "GastosVenta";
  public const string GastosAdministracion = "GastosAdministracion";
  public const string GastosFinancieros = "GastosFinancieros";
  public const string ProductosFinancieros = "ProductosFinancieros";
  public const string OtrosGastos = "OtrosGastos";
  public const string OtrosProductos = "OtrosProductos";

  // Posición financiera y conciliación
  public const string Caja = "Caja";
  public const string Bancos = "Bancos";
  public const string Clientes = "Clientes";
  public const string Inventarios = "Inventarios";
  public const string IvaAcreditable = "IvaAcreditable";
  public const string ActivoFijo = "ActivoFijo";
  public const string DepreciacionAcumulada = "DepreciacionAcumulada";
  public const string CargosDiferidos = "CargosDiferidos";
  public const string Proveedores = "Proveedores";
  public const string Acreedores = "Acreedores";
  public const string IvaTrasladado = "IvaTrasladado";
  public const string ImpuestosPorPagar = "ImpuestosPorPagar";
  public const string ImpuestosRetenidos = "ImpuestosRetenidos";
  public const string Capital = "Capital";

  public const string GrupoResultado = "Resultado";
  public const string GrupoPosicion = "Posicion";

  /// <summary>Semilla usada cuando un RFC todavía no tiene mapeo guardado.</summary>
  public static readonly IReadOnlyList<RestaurantAgrupadorSeedRow> Semilla =
  [
    new(IngresosVenta, "401", 1, 10),
    new(DevolucionesDescuentos, "402", -1, 20),
    new(OtrosIngresos, "403", 1, 30),
    new(CostoVenta, "501", -1, 40),
    new(Compras, "502", -1, 50),
    new(DevolucionesCompras, "503", 1, 60),
    new(OtrosCostos, "504", -1, 70),
    new(GastosGenerales, "601", -1, 80),
    new(GastosVenta, "602", -1, 90),
    new(GastosAdministracion, "603", -1, 100),
    new(GastosFinancieros, "701", -1, 110),
    new(ProductosFinancieros, "702", 1, 120),
    new(OtrosGastos, "703", -1, 130),
    new(OtrosProductos, "704", 1, 140),
    new(Caja, "101", 1, 200),
    new(Bancos, "102", 1, 210),
    new(Clientes, "105", 1, 220),
    new(Clientes, "106", 1, 221),
    new(Inventarios, "115", 1, 230),
    new(IvaAcreditable, "118", 1, 240),
    new(IvaAcreditable, "119", 1, 241),
    new(ActivoFijo, "151", 1, 250),
    new(ActivoFijo, "152", 1, 251),
    new(ActivoFijo, "153", 1, 252),
    new(ActivoFijo, "154", 1, 253),
    new(ActivoFijo, "155", 1, 254),
    new(ActivoFijo, "156", 1, 255),
    new(ActivoFijo, "157", 1, 256),
    new(ActivoFijo, "159", 1, 257),
    new(ActivoFijo, "160", 1, 258),
    new(ActivoFijo, "170", 1, 259),
    new(DepreciacionAcumulada, "171", -1, 260),
    new(CargosDiferidos, "173", 1, 270),
    new(CargosDiferidos, "174", 1, 271),
    new(CargosDiferidos, "181", 1, 272),
    new(Proveedores, "201", -1, 280),
    new(Acreedores, "205", -1, 290),
    new(Acreedores, "251", -1, 291),
    new(IvaTrasladado, "208", -1, 300),
    new(IvaTrasladado, "209", -1, 301),
    new(ImpuestosPorPagar, "213", -1, 310),
    new(ImpuestosRetenidos, "216", -1, 320),
    new(Capital, "301", -1, 330),
    new(Capital, "302", -1, 331),
    new(Capital, "303", -1, 332),
    new(Capital, "304", -1, 333),
    new(Capital, "305", -1, 334),
    new(Capital, "306", -1, 335)
  ];

  private static readonly Dictionary<string, string> Etiquetas = new(StringComparer.OrdinalIgnoreCase)
  {
    [IngresosVenta] = "Ingresos por venta",
    [DevolucionesDescuentos] = "Devoluciones y descuentos",
    [OtrosIngresos] = "Otros ingresos",
    [CostoVenta] = "Costo de venta",
    [Compras] = "Compras",
    [DevolucionesCompras] = "Devoluciones sobre compras",
    [OtrosCostos] = "Otros costos",
    [GastosGenerales] = "Gastos generales",
    [GastosVenta] = "Gastos de venta",
    [GastosAdministracion] = "Gastos de administración",
    [GastosFinancieros] = "Gastos financieros",
    [ProductosFinancieros] = "Productos financieros",
    [OtrosGastos] = "Otros gastos",
    [OtrosProductos] = "Otros productos",
    [Caja] = "Caja",
    [Bancos] = "Bancos",
    [Clientes] = "Clientes",
    [Inventarios] = "Inventarios",
    [IvaAcreditable] = "IVA acreditable",
    [ActivoFijo] = "Activo fijo",
    [DepreciacionAcumulada] = "Depreciación acumulada",
    [CargosDiferidos] = "Cargos diferidos",
    [Proveedores] = "Proveedores",
    [Acreedores] = "Acreedores",
    [IvaTrasladado] = "IVA trasladado",
    [ImpuestosPorPagar] = "Impuestos por pagar",
    [ImpuestosRetenidos] = "Impuestos retenidos",
    [Capital] = "Capital contable"
  };

  private static readonly HashSet<string> Resultado = new(StringComparer.OrdinalIgnoreCase)
  {
    IngresosVenta, DevolucionesDescuentos, OtrosIngresos, CostoVenta, Compras,
    DevolucionesCompras, OtrosCostos, GastosGenerales, GastosVenta,
    GastosAdministracion, GastosFinancieros, ProductosFinancieros, OtrosGastos, OtrosProductos
  };

  public static string Etiqueta(string conceptoClave)
    => Etiquetas.TryGetValue(conceptoClave, out var etiqueta) ? etiqueta : conceptoClave;

  public static string Grupo(string conceptoClave)
    => Resultado.Contains(conceptoClave) ? GrupoResultado : GrupoPosicion;

  public static bool EsDeResultado(string conceptoClave) => Resultado.Contains(conceptoClave);

  /// <summary>Conceptos conocidos, en el orden en que deben presentarse.</summary>
  public static IReadOnlyList<string> ConceptosOrdenados =>
    Semilla.OrderBy(row => row.Orden).Select(row => row.ConceptoClave).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}

public sealed record RestaurantAgrupadorSeedRow(string ConceptoClave, string Nivel1, short Signo, int Orden);
