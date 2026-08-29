namespace OrionERP.Application.Features.Restaurante;

public static class RestaurantDiagnosticSeverities
{
  public const string Critica = "Critica";
  public const string Alta = "Alta";
  public const string Media = "Media";
  public const string Menor = "Menor";
  public const string Informativa = "Informativa";

  /// <summary>Orden de presentación: lo más grave primero.</summary>
  public static int Rank(string severidad) => severidad switch
  {
    Critica => 0,
    Alta => 1,
    Media => 2,
    Menor => 3,
    _ => 4
  };
}

public static class RestaurantDiagnosticStates
{
  public const string Abierto = "Abierto";
  public const string Corregido = "Corregido";
  public const string Aceptado = "Aceptado";
}

/// <summary>Un hallazgo del diagnóstico contable-fiscal.</summary>
public sealed class RestaurantDiagnosticFindingDto
{
  public long Id { get; set; }
  public long CorridaId { get; set; }
  public string ReglaClave { get; set; } = string.Empty;
  public string Severidad { get; set; } = RestaurantDiagnosticSeverities.Media;
  public string Titulo { get; set; } = string.Empty;
  public string Detalle { get; set; } = string.Empty;
  public string? Agrupadores { get; set; }
  public decimal MontoExpuesto { get; set; }
  public int Conteo { get; set; }
  public string? AccionSugerida { get; set; }
  public string Estado { get; set; } = RestaurantDiagnosticStates.Abierto;
  public string? Justificacion { get; set; }
  public DateTime? ResueltoEn { get; set; }
  public string? ResueltoPor { get; set; }

  public IReadOnlyList<string> AgrupadoresLista =>
    string.IsNullOrWhiteSpace(Agrupadores)
      ? []
      : Agrupadores.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>Una corrida del diagnóstico con sus hallazgos.</summary>
public sealed class RestaurantDiagnosticRunDto
{
  public long Id { get; set; }
  public int SiteId { get; set; }
  public DateTime PeriodoInicio { get; set; }
  public DateTime PeriodoFin { get; set; }
  public DateTime EjecutadoEn { get; set; }
  public string EjecutadoPor { get; set; } = string.Empty;
  public int HallazgosTotal { get; set; }
  public int Criticos { get; set; }
  public decimal MontoExpuesto { get; set; }
  public IReadOnlyList<RestaurantDiagnosticFindingDto> Findings { get; set; } = [];

  public int Abiertos => Findings.Count(finding => finding.Estado == RestaurantDiagnosticStates.Abierto);
}

/// <summary>Cuenta de detalle que el restaurante necesita y que falta en el catálogo del RFC.</summary>
public sealed class RestaurantMissingAccountDto
{
  public string Nivel1 { get; set; } = string.Empty;
  public string Nivel2 { get; set; } = string.Empty;
  public string Nivel1Descripcion { get; set; } = string.Empty;
  public string Nivel2Descripcion { get; set; } = string.Empty;
  public string DescripcionSugerida { get; set; } = string.Empty;
  public string Uso { get; set; } = string.Empty;
  public string? CampoConfiguracion { get; set; }
  public bool EncabezadoDisponible { get; set; } = true;
  public bool Seleccionada { get; set; } = true;
}

/// <summary>Resultado de generar las pólizas diarias faltantes de un rango.</summary>
public sealed class RestaurantPolicyBackfillDayDto
{
  public DateTime Fecha { get; set; }
  public bool Generada { get; set; }
  public string Mensaje { get; set; } = string.Empty;
  public int Ordenes { get; set; }
  public decimal Importe { get; set; }
}

public sealed class RestaurantPolicyBackfillResultDto
{
  public bool Success { get; set; }
  public string Message { get; set; } = string.Empty;
  public IReadOnlyList<RestaurantPolicyBackfillDayDto> Days { get; set; } = [];
  public int Generadas => Days.Count(day => day.Generada);
  public int Rechazadas => Days.Count(day => !day.Generada);
}

/// <summary>Catálogo de cuentas de detalle que un restaurante necesita para operar contablemente.</summary>
public static class RestaurantRequiredAccounts
{
  public sealed record Definition(
    string Nivel1,
    string Nivel2,
    string DescripcionSugerida,
    string Uso,
    string? CampoConfiguracion);

  public static readonly IReadOnlyList<Definition> Catalogo =
  [
    new("401", "02", "VENTAS GRAVADAS TASA GENERAL DE CONTADO", "Ingreso por venta de alimentos y bebidas cobrada de contado.", "SalesAccount"),
    new("402", "01", "DESCUENTOS SOBRE VENTAS", "Descuentos y promociones aplicados en el punto de venta.", "DiscountAccount"),
    new("208", "01", "IVA TRASLADADO COBRADO", "IVA que el restaurante cobra al comensal.", "VatAccount"),
    new("213", "01", "IVA POR PAGAR", "IVA por enterar al SAT.", null),
    new("119", "01", "IVA ACREDITABLE PENDIENTE DE PAGO", "IVA de compras a proveedores aún no pagadas.", null),
    new("118", "01", "IVA ACREDITABLE PAGADO", "IVA de compras a proveedores ya pagadas.", null),
    new("115", "02", "INVENTARIO DE INSUMOS", "Existencia de materia prima valuada en almacén.", "InventoryAccount"),
    new("501", "01", "COSTO DE ALIMENTOS Y BEBIDAS", "Costo de lo vendido en el periodo.", "CostOfSalesAccount"),
    new("601", "01", "SUELDOS Y SALARIOS", "Nómina del personal del restaurante.", null),
    new("216", "01", "ISR RETENIDO POR SUELDOS Y SALARIOS", "Retención de ISR a trabajadores.", null),
    new("216", "04", "ISR RETENIDO POR SERVICIOS PROFESIONALES", "Retención a personas físicas con honorarios.", null)
  ];
}
