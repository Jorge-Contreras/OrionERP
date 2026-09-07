using System;
using System.Collections.Generic;

namespace OrionERP.Application.Features.ReportesFinancieros.Models;

/// <summary>
/// Encabezado del periodo: quien declara, con que parametros y en que estado.
/// </summary>
public class DeclaracionEncabezadoRow
{
  public string Rfc { get; set; } = string.Empty;
  public int Ejercicio { get; set; }
  public int Periodo { get; set; }
  public string NombreMes { get; set; } = string.Empty;

  /// <summary>'M' persona moral, 'F' persona fisica. v1 solo calcula ISR de 'M'.</summary>
  public string TipoPersona { get; set; } = "M";
  public string? RegimenFiscal { get; set; }
  public decimal TasaIsr { get; set; }
  public decimal? Coeficiente { get; set; }

  /// <summary>
  /// Ejercicio de la declaracion anual de la que sale el coeficiente. Se muestra
  /// porque el coeficiente cambia a media ano y conviene poder decir de donde
  /// viene el que se esta aplicando.
  /// </summary>
  public int? CoeficienteOrigen { get; set; }

  public decimal ProporcionIva { get; set; }
  public bool ObligadoIva { get; set; }

  public DateTime FechaVencimiento { get; set; }
  public int DiasParaVencimiento { get; set; }

  public bool TienePerfil { get; set; }
  public bool TieneCoeficiente { get; set; }
  public bool TieneDeclaracion { get; set; }
  public string? DeclaracionTipo { get; set; }
  public string? NumeroOperacion { get; set; }
  public DateTime? FechaPresentacion { get; set; }

  /// <summary>
  /// Falso cuando el periodo no es enero y no hay declaraciones anteriores
  /// importadas. Sin ese historial la cadena acumulada de ISR no se puede armar.
  /// </summary>
  public bool TieneHistorialAnterior { get; set; }

  public int? TransaccionIdCierre { get; set; }
  public decimal IsrACargo { get; set; }
  public decimal IvaSaldoAFavor { get; set; }
  public decimal IvaACargo { get; set; }

  public bool EsPersonaMoral => string.Equals(TipoPersona, "M", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Un renglon del formato del SAT, con las tres cifras que se comparan:
/// lo que el portal precarga, lo que sostenemos y lo que ya se presento.
/// </summary>
public class DeclaracionRenglonRow
{
  public int Orden { get; set; }
  public string Seccion { get; set; } = string.Empty;
  public string Concepto { get; set; } = string.Empty;

  /// <summary>"money" o "rate"; decide como se formatea y si admite copiado.</summary>
  public string Formato { get; set; } = "money";
  public bool EsTotal { get; set; }

  public decimal? ValorSat { get; set; }
  public decimal? ValorNuestro { get; set; }
  public decimal? ValorDeclarado { get; set; }
  public decimal? DifSatNuestro { get; set; }
  public decimal? DifDeclarado { get; set; }
  public string Estado { get; set; } = "PENDIENTE";

  public bool EsTasa => string.Equals(Formato, "rate", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Conciliacion CFDI contra contabilidad contra declarado.</summary>
public class DeclaracionConciliacionRow
{
  public int Orden { get; set; }
  public string Concepto { get; set; } = string.Empty;
  public string? Cuenta { get; set; }
  public decimal? ValorCfdi { get; set; }
  public decimal? ValorContable { get; set; }
  public decimal? ValorDeclarado { get; set; }
  public bool NoComparable { get; set; }
  public decimal? DifCfdiContable { get; set; }
  public decimal? DifDeclarado { get; set; }
  public string Estado { get; set; } = "NA";
}

/// <summary>Retenciones de ISR e IVA, propias y de plataformas.</summary>
public class DeclaracionRetencionRow
{
  public int Orden { get; set; }
  public string Concepto { get; set; } = string.Empty;
  public string? Cuenta { get; set; }
  public decimal? ValorCfdi { get; set; }
  public decimal? ValorContable { get; set; }
  public decimal? ValorDeclarado { get; set; }
  public string? Nota { get; set; }
  public decimal? DifCfdiContable { get; set; }
  public string Estado { get; set; } = "NA";
}

/// <summary>Un renglon del asiento de cierre que salda 118-01 contra 208-01.</summary>
public class DeclaracionCierreRow
{
  public int Orden { get; set; }
  public string? Cuenta { get; set; }
  public string? NombreCuenta { get; set; }
  public decimal Debe { get; set; }
  public decimal Haber { get; set; }
  public string? Concepto { get; set; }
  public DateTime Fecha { get; set; }
  public decimal Neto { get; set; }
  public bool EsFavor { get; set; }
  public string? Mensaje { get; set; }
}

/// <summary>Un hallazgo de la auditoria del mes.</summary>
public class DeclaracionHallazgoRow
{
  public string Severidad { get; set; } = "Baja";
  public string Tipo { get; set; } = string.Empty;
  public string Descripcion { get; set; } = string.Empty;
  public decimal? Monto { get; set; }
  public int? ComprobanteId { get; set; }
  public string? FolioFiscal { get; set; }
  public string? Contraparte { get; set; }
  public DateTime? Fecha { get; set; }
  public string? RutaDetalle { get; set; }

  public bool EsAlta => string.Equals(Severidad, "Alta", StringComparison.OrdinalIgnoreCase);
}

public class DeclaracionHallazgoResumen
{
  public int Total { get; set; }
  public int Altas { get; set; }
  public int Medias { get; set; }
  public int Bajas { get; set; }
}

/// <summary>Un mes del ejercicio: calculado contra declarado.</summary>
public class DeclaracionEjercicioRow
{
  public int Mes { get; set; }
  public string NombreMes { get; set; } = string.Empty;
  public decimal Ingresos { get; set; }
  public decimal? IngresosDeclarado { get; set; }
  public decimal? DifIngresos { get; set; }
  public decimal IngresosAcum { get; set; }
  public decimal? Coeficiente { get; set; }
  public decimal IsrCausado { get; set; }
  public decimal PagosProvAnteriores { get; set; }
  public decimal IsrACargo { get; set; }
  public decimal? IsrACargoDeclarado { get; set; }
  public decimal IvaACargo { get; set; }
  public decimal? IvaACargoDeclarado { get; set; }
  public decimal IvaAcreditable { get; set; }
  public decimal? IvaAcreditableDeclarado { get; set; }
  public decimal SaldoAFavor { get; set; }
  public decimal? SaldoAFavorDeclarado { get; set; }
  public string? TipoDeclaracion { get; set; }
  public string? NumeroOperacion { get; set; }
  public DateTime? FechaPresentacion { get; set; }
  public bool EsParcial { get; set; }
  public bool TieneDeclaracion { get; set; }
  public bool RequiereComplementaria { get; set; }
}

/// <summary>Todo lo que la pantalla necesita para un periodo.</summary>
public class DeclaracionMensualReport
{
  public DeclaracionEncabezadoRow? Encabezado { get; set; }
  public IReadOnlyList<DeclaracionRenglonRow> Isr { get; set; } = Array.Empty<DeclaracionRenglonRow>();
  public IReadOnlyList<DeclaracionRenglonRow> Iva { get; set; } = Array.Empty<DeclaracionRenglonRow>();
  public IReadOnlyList<DeclaracionConciliacionRow> Conciliacion { get; set; } = Array.Empty<DeclaracionConciliacionRow>();
  public IReadOnlyList<DeclaracionRetencionRow> Retenciones { get; set; } = Array.Empty<DeclaracionRetencionRow>();
  public IReadOnlyList<DeclaracionCierreRow> Cierre { get; set; } = Array.Empty<DeclaracionCierreRow>();
  public IReadOnlyList<DeclaracionHallazgoRow> Hallazgos { get; set; } = Array.Empty<DeclaracionHallazgoRow>();
  public DeclaracionHallazgoResumen? HallazgosResumen { get; set; }
}

/// <summary>
/// Resultado de leer un acuse del SAT. Los renglones "parciales" salen de las
/// tablas de periodos anteriores que trae cualquier acuse: un solo PDF de julio
/// reconstruye enero a junio, aunque solo con ingresos nominales, ISR a cargo y
/// numero de operacion.
/// </summary>
public class DeclaracionImportada
{
  public string ArchivoNombre { get; set; } = string.Empty;
  public string ArchivoSha256 { get; set; } = string.Empty;
  public string? Rfc { get; set; }
  public int Ejercicio { get; set; }
  public int Periodo { get; set; }
  public string TipoDeclaracion { get; set; } = "N";
  public int NumeroComplementaria { get; set; }
  public string? NumeroOperacion { get; set; }
  public DateTime? FechaPresentacion { get; set; }
  public string? Estatus { get; set; }
  public bool EsParcial { get; set; }
  public Dictionary<string, decimal?> Campos { get; set; } = new(StringComparer.Ordinal);

  /// <summary>Meses anteriores reconstruidos desde las tablas del acuse.</summary>
  public List<DeclaracionImportada> Anteriores { get; set; } = new();

  public string? Error { get; set; }
  public bool EsValida => Error is null && Periodo is >= 1 and <= 12 && Ejercicio > 2000;
}

public class DeclaracionImportResultado
{
  public int Insertadas { get; set; }
  public int Actualizadas { get; set; }
  public int Omitidas { get; set; }
  public List<string> Mensajes { get; set; } = new();
}
