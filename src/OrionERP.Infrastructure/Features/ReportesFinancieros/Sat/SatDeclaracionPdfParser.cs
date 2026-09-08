using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using OrionERP.Application.Features.ReportesFinancieros.Models;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace OrionERP.Infrastructure.Features.ReportesFinancieros.Sat;

/// <summary>
/// Lee un acuse "Declaracion Provisional o Definitiva de Impuestos Federales"
/// del SAT (version 24.0.0) y devuelve las cifras presentadas.
///
/// El PDF trae capa de texto y cada dato aparece como "ETIQUETA valor" en una
/// sola linea, asi que el parser trabaja por etiqueta exacta y no por posicion.
///
/// Dos complicaciones que dictan el diseno:
///
/// 1. Etiquetas repetidas. "IMPUESTO A CARGO" existe en ISR y en IVA, y
///    "RECARGOS" o "CANTIDAD A PAGAR" aparecen una vez por cada periodo
///    anterior listado. Por eso se lleva el impuesto en curso, tomado del
///    titulo de cada pagina, y se distingue el bloque de detalle del pago.
///
/// 2. El acuse de cualquier mes trae los meses anteriores del ejercicio con su
///    numero de operacion. Un solo PDF reconstruye el ano, aunque solo con
///    ingresos nominales e ISR a cargo: esos renglones se marcan EsParcial.
/// </summary>
public sealed class SatDeclaracionPdfParser
{
  private const string TituloIsr = "ISR personas morales";
  private const string TituloIva = "Impuesto al Valor Agregado. Personas morales";
  private const string TituloAsimilados = "ISR retenciones por asimilados a salarios";

  private static readonly string[] Meses =
  [
    "ENERO", "FEBRERO", "MARZO", "ABRIL", "MAYO", "JUNIO",
    "JULIO", "AGOSTO", "SEPTIEMBRE", "OCTUBRE", "NOVIEMBRE", "DICIEMBRE"
  ];

  // "ENERO 47,821 PRESENTADA- PAGADA 262110036657 17/02/2026"
  private static readonly Regex FilaMes = new(
    @"^(?<mes>[A-ZÁÉÍÓÚÑ]+)\s+(?<monto>-?[\d,]+(?:\.\d+)?)\s+(?<estatus>PRESENTADA[\s-]*PAGADA|PRESENTADA[^\d]*?)\s+(?<op>\d{6,})\s+(?<fecha>\d{2}/\d{2}/\d{4})\s*$",
    RegexOptions.Compiled | RegexOptions.IgnoreCase);

  private static readonly Regex EtiquetaValor = new(
    @"^(?<label>.+?)\s+(?<valor>-?[\d,]+(?:\.\d+)?)\s*$",
    RegexOptions.Compiled);

  public DeclaracionImportada Parse(Stream pdf, string nombreArchivo)
  {
    var resultado = new DeclaracionImportada { ArchivoNombre = nombreArchivo };

    byte[] bytes;
    using (var buffer = new MemoryStream())
    {
      pdf.CopyTo(buffer);
      bytes = buffer.ToArray();
    }

    resultado.ArchivoSha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    List<List<string>> paginas;
    try
    {
      paginas = ExtraerPaginas(bytes);
    }
    catch (Exception ex)
    {
      resultado.Error = $"No se pudo leer el PDF: {ex.Message}";
      return resultado;
    }

    var lineas = paginas.SelectMany(x => x).ToList();

    if (lineas.Count == 0)
    {
      resultado.Error = "El PDF no tiene capa de texto. Si es un escaneo hay que capturarlo a mano.";
      return resultado;
    }

    LeerEncabezado(lineas, resultado);

    if (!resultado.EsValida && resultado.Error is null)
    {
      resultado.Error = "No se reconocio el encabezado del acuse (RFC, ejercicio o periodo).";
      return resultado;
    }

    LeerCampos(paginas, resultado);
    LeerPeriodosAnteriores(lineas, resultado);

    return resultado;
  }

  /// <summary>
  /// Devuelve las lineas agrupadas por pagina. Agrupar importa: PdfPig emite el
  /// encabezado de cada hoja al final de su texto, de modo que un recorrido
  /// plano atribuiria el cuerpo de una hoja al titulo de la anterior -y con eso
  /// el "IMPUESTO A CARGO" de la hoja de asimilados a salarios se leeria como
  /// si fuera el de ISR personas morales.
  /// </summary>
  private static List<List<string>> ExtraerPaginas(byte[] bytes)
  {
    var paginas = new List<List<string>>();
    using var stream = new MemoryStream(bytes, writable: false);
    using var documento = PdfDocument.Open(stream);

    foreach (var pagina in documento.GetPages())
    {
      var texto = ContentOrderTextExtractor.GetText(pagina) ?? string.Empty;
      var lineas = new List<string>();

      foreach (var linea in texto.Split('\n'))
      {
        var limpia = Normalizar(linea);
        if (limpia.Length > 0)
        {
          lineas.Add(limpia);
        }
      }

      paginas.Add(lineas);
    }

    return paginas;
  }

  /// <summary>
  /// Colapsa espacios y quita ligaduras tipograficas. El PDF usa "ﬁ" y "ﬀ" en
  /// palabras como "Deﬁnitiva", que romperian cualquier comparacion literal.
  /// </summary>
  private static string Normalizar(string valor)
  {
    if (string.IsNullOrWhiteSpace(valor))
    {
      return string.Empty;
    }

    var texto = valor
      .Replace('\u00A0', ' ')
      .Replace("\uFB00", "ff").Replace("\uFB01", "fi").Replace("\uFB02", "fl")
      .Replace("\uFB03", "ffi").Replace("\uFB04", "ffl");

    return Regex.Replace(texto, @"\s+", " ").Trim();
  }

  private static void LeerEncabezado(List<string> lineas, DeclaracionImportada destino)
  {
    foreach (var linea in lineas.Take(40))
    {
      var rfc = Buscar(linea, @"RFC:\s*([A-ZÑ&]{3,4}\d{6}[A-Z0-9]{3})");
      if (rfc is not null && destino.Rfc is null)
      {
        destino.Rfc = rfc;
      }

      var ejercicio = Buscar(linea, @"Ejercicio:\s*(\d{4})");
      if (ejercicio is not null && destino.Ejercicio == 0)
      {
        destino.Ejercicio = int.Parse(ejercicio, CultureInfo.InvariantCulture);
      }

      var periodo = Buscar(linea, @"Per[ií]odo de la declaraci[oó]n:\s*([A-Za-zÁÉÍÓÚáéíóú]+)");
      if (periodo is not null && destino.Periodo == 0)
      {
        destino.Periodo = MesANumero(periodo);
      }

      var operacion = Buscar(linea, @"N[uú]mero de operaci[oó]n:\s*(\d+)");
      if (operacion is not null && destino.NumeroOperacion is null)
      {
        destino.NumeroOperacion = operacion;
      }

      // El acuse de una complementaria pone en el mismo renglon el tipo y el
      // motivo ("Complementaria Tipo de complementaria: Modificacion de
      // Obligaciones"). Se corta antes del motivo para que Estatus no crezca
      // sin control, pero se conserva completo en la columna de la tabla.
      var tipo = Buscar(linea, @"Tipo de declaraci[oó]n:\s*(.{1,150}?)\s*$");
      if (tipo is not null && destino.Estatus is null)
      {
        destino.Estatus = tipo.Trim();
        destino.TipoDeclaracion = tipo.StartsWith("Complementaria", StringComparison.OrdinalIgnoreCase) ? "C" : "N";

        // "Complementaria 2" y similares traen el consecutivo en el mismo texto.
        var consecutivo = Buscar(tipo, @"(\d+)");
        if (destino.TipoDeclaracion == "C")
        {
          destino.NumeroComplementaria = consecutivo is null
            ? 1
            : int.Parse(consecutivo, CultureInfo.InvariantCulture);
        }
      }

      var fecha = Buscar(linea, @"Fecha y hora de presentaci[oó]n:\s*(\d{2}/\d{2}/\d{4})");
      if (fecha is not null && destino.FechaPresentacion is null
          && DateTime.TryParseExact(fecha, "dd/MM/yyyy", CultureInfo.InvariantCulture,
               DateTimeStyles.None, out var parsed))
      {
        destino.FechaPresentacion = parsed;
      }
    }
  }

  private static string? Buscar(string linea, string patron)
  {
    var m = Regex.Match(linea, patron, RegexOptions.IgnoreCase);
    return m.Success ? m.Groups[1].Value.Trim() : null;
  }

  private static int MesANumero(string nombre)
  {
    var limpio = QuitarAcentos(nombre).ToUpperInvariant();
    for (var i = 0; i < Meses.Length; i++)
    {
      if (QuitarAcentos(Meses[i]) == limpio)
      {
        return i + 1;
      }
    }

    return 0;
  }

  private static string QuitarAcentos(string valor) => valor
    .Replace("Á", "A").Replace("É", "E").Replace("Í", "I").Replace("Ó", "O").Replace("Ú", "U")
    .Replace("á", "A").Replace("é", "E").Replace("í", "I").Replace("ó", "O").Replace("ú", "U")
    .Replace("Ñ", "N").Replace("ñ", "N")
    .ToUpperInvariant();

  /// <summary>
  /// Etiquetas del formato, con el impuesto al que pertenecen y si viven dentro
  /// del bloque "DETALLE DEL PAGO". La clave es el nombre de columna en
  /// fiscal.DeclaracionPresentada.
  /// </summary>
  private static readonly (string Campo, string Impuesto, bool EnDetalle, string Etiqueta)[] Mapa =
  [
    ("IngresosNominalesPeriodo", TituloIsr, false, "INGRESOS NOMINALES"),
    ("IngresosNominalesAnteriores", TituloIsr, false, "INGRESOS NOMINALES DE PERIODOS ANTERIORES"),
    ("TotalIngresosNominales", TituloIsr, false, "TOTAL DE INGRESOS NOMINALES DEL PERIODO"),
    ("CoeficienteUtilidad", TituloIsr, false, "COEFICIENTE DE UTILIDAD"),
    ("UtilidadFiscal", TituloIsr, false, "UTILIDAD FISCAL PARA PAGO PROVISIONAL"),
    ("DeduccionInmediata", TituloIsr, false, "TOTAL DEDUCCION INMEDIATA DE INVERSIONES DEL PERIODO"),
    ("Ptu", TituloIsr, false, "PTU ACUMULADA A APLICAR EN EL PERIODO"),
    ("BaseGravableIsr", TituloIsr, false, "BASE GRAVABLE DEL PAGO PROVISIONAL"),
    ("ImpuestoCausado", TituloIsr, false, "IMPUESTO CAUSADO"),
    ("PagosProvisionalesAnteriores", TituloIsr, false, "PAGOS PROVISIONALES EFECTUADOS DE PERIODOS ANTERIORES"),
    ("IsrRetenido", TituloIsr, false, "TOTAL DE ISR RETENIDO DEL PERIODO"),
    ("IsrACargo", TituloIsr, false, "IMPUESTO A CARGO"),
    ("IsrRecargos", TituloIsr, true, "RECARGOS"),
    ("IsrTotalAPagar", TituloIsr, true, "CANTIDAD A PAGAR"),

    ("ActosGravados16", TituloIva, false, "VALOR DE LOS ACTOS O ACTIVIDADES GRAVADOS A LA TASA DEL 16%"),
    ("IvaTrasladado16", TituloIva, false, "IVA A CARGO A LA TASA DEL 16%"),
    ("ActosGravados0", TituloIva, false, "VALOR DE LOS ACTOS O ACTIVIDADES GRAVADOS A LA TASA DEL 0% OTROS"),
    ("ActosExentos", TituloIva, false, "VALOR DE LOS ACTOS O ACTIVIDADES POR LOS QUE NO SE DEBA PAGAR EL IMPUESTO (EXENTOS)"),
    ("ActosNoObjeto", TituloIva, false, "VALOR DE LOS ACTOS O ACTIVIDADES NO OBJETO DEL IMPUESTO"),
    ("TotalIvaACargo", TituloIva, false, "TOTAL DE IVA A CARGO"),
    ("ActosPagados16", TituloIva, false, "TOTAL DE LOS ACTOS O ACTIVIDADES PAGADOS A LA TASA DEL 16% DE IVA"),
    ("IvaAcreditable16", TituloIva, false, "IVA DE ACTOS O ACTIVIDADES PAGADOS A LA TASA DEL 16%"),
    ("ActosPagados0", TituloIva, false, "TOTAL DE LOS DEMAS ACTOS O ACTIVIDADES PAGADOS A LA TASA DEL 0% DE IVA"),
    ("ProporcionIva", TituloIva, false, "PROPORCION DE IVA"),
    ("TotalIvaAcreditable", TituloIva, false, "TOTAL DE IVA ACREDITABLE"),
    ("IvaRetenido", TituloIva, false, "IVA RETENIDO"),
    ("IvaSaldoAFavor", TituloIva, false, "SALDO A FAVOR"),
    ("IvaACargo", TituloIva, false, "IMPUESTO A CARGO"),
    ("IvaTotalAPagar", TituloIva, true, "CANTIDAD A PAGAR")
  ];

  private static void LeerCampos(List<List<string>> paginas, DeclaracionImportada destino)
  {
    // enDetalle se arrastra entre hojas porque el bloque "DETALLE DEL PAGO"
    // continua en la siguiente; se reinicia al cambiar de impuesto.
    var impuestoPrevio = string.Empty;
    var enDetalle = false;

    foreach (var pagina in paginas)
    {
      var impuesto = ImpuestoDeLaPagina(pagina);

      if (impuesto != impuestoPrevio)
      {
        enDetalle = false;
        impuestoPrevio = impuesto;
      }

      // La hoja de asimilados a salarios repite etiquetas de ISR con otras
      // cifras. Se salta entera en vez de intentar distinguir renglon a renglon.
      if (impuesto.Length == 0 || impuesto == TituloAsimilados)
      {
        continue;
      }

      string? previa1 = null, previa2 = null;

      foreach (var linea in pagina)
      {
        if (linea.StartsWith("DETALLE DEL PAGO", StringComparison.OrdinalIgnoreCase))
        {
          enDetalle = true;
          previa2 = previa1;
          previa1 = linea;
          continue;
        }

        AsignarSiCoincide(linea, previa1, previa2, impuesto, enDetalle, destino);

        previa2 = previa1;
        previa1 = linea;
      }
    }
  }

  private static string ImpuestoDeLaPagina(List<string> pagina)
  {
    foreach (var linea in pagina)
    {
      if (linea.Equals(TituloIsr, StringComparison.OrdinalIgnoreCase)) return TituloIsr;
      if (linea.Equals(TituloIva, StringComparison.OrdinalIgnoreCase)) return TituloIva;
      if (linea.Equals(TituloAsimilados, StringComparison.OrdinalIgnoreCase)) return TituloAsimilados;
    }

    return string.Empty;
  }

  /// <summary>
  /// Intenta casar la linea contra el mapa de etiquetas. Las etiquetas largas
  /// del formato se parten en dos o tres lineas ("TOTAL DE INGRESOS NOMINALES
  /// DEL" / "PERIODO 413,326"), asi que tambien se prueba uniendola con las
  /// anteriores. El valor siempre sale de la linea actual, de modo que unir de
  /// mas no puede cambiar la cifra, solo el nombre del campo.
  /// </summary>
  private static void AsignarSiCoincide(
    string linea, string? previa1, string? previa2,
    string impuesto, bool enDetalle, DeclaracionImportada destino)
  {
    var m = EtiquetaValor.Match(linea);
    if (!m.Success)
    {
      return;
    }

    var valor = ParseDecimal(m.Groups["valor"].Value);
    if (valor is null)
    {
      return;
    }

    var candidatos = new List<string>(3) { m.Groups["label"].Value };
    if (previa1 is not null)
    {
      candidatos.Add($"{previa1} {m.Groups["label"].Value}".Trim());
      if (previa2 is not null)
      {
        candidatos.Add($"{previa2} {previa1} {m.Groups["label"].Value}".Trim());
      }
    }

    foreach (var candidato in candidatos)
    {
      var etiqueta = QuitarAcentos(candidato);

      foreach (var (campo, imp, detalle, texto) in Mapa)
      {
        if (imp != impuesto || detalle != enDetalle || etiqueta != QuitarAcentos(texto))
        {
          continue;
        }

        // Se queda la ultima aparicion: el formato repite los totales en la
        // seccion de determinacion y esa es la cifra definitiva.
        destino.Campos[campo] = valor;
        return;
      }
    }
  }

  private static decimal? ParseDecimal(string valor)
  {
    var limpio = valor.Replace(",", string.Empty).Trim();
    return decimal.TryParse(limpio, NumberStyles.Number, CultureInfo.InvariantCulture, out var d)
      ? d
      : null;
  }

  /// <summary>
  /// Reconstruye los meses anteriores del ejercicio a partir de las tablas que
  /// el propio acuse imprime. Solo se aprovechan dos: ingresos nominales y
  /// pagos provisionales (el ISR a cargo de cada mes). Las filas resultantes
  /// quedan marcadas EsParcial, para que el acuse propio de cada mes las
  /// complete despues sin que se pierda lo ya conocido.
  /// </summary>
  private static void LeerPeriodosAnteriores(List<string> lineas, DeclaracionImportada destino)
  {
    const string SeccionIngresos = "INGRESOS NOMINALES DE PERIODOS ANTERIORES";
    const string SeccionPagos = "PAGOS PROVISIONALES EFECTUADOS DE PERIODOS ANTERIORES";
    const string SeccionRetenido = "ISR RETENIDO DE PERIODOS ANTERIORES";
    const string SeccionDeduccion = "DEDUCCION INMEDIATA DE INVERSIONES";

    var seccion = string.Empty;
    var porMes = new Dictionary<int, DeclaracionImportada>();

    foreach (var linea in lineas)
    {
      var sinAcentos = QuitarAcentos(linea);

      if (sinAcentos == SeccionIngresos) { seccion = SeccionIngresos; continue; }
      if (sinAcentos == SeccionPagos) { seccion = SeccionPagos; continue; }
      if (sinAcentos == SeccionRetenido) { seccion = SeccionRetenido; continue; }
      if (sinAcentos.StartsWith(SeccionDeduccion, StringComparison.Ordinal)) { seccion = SeccionDeduccion; continue; }

      if (seccion.Length == 0)
      {
        continue;
      }

      var m = FilaMes.Match(linea);
      if (!m.Success)
      {
        continue;
      }

      var mes = MesANumero(m.Groups["mes"].Value);
      if (mes == 0 || mes >= destino.Periodo)
      {
        continue;
      }

      var monto = ParseDecimal(m.Groups["monto"].Value);
      if (monto is null)
      {
        continue;
      }

      if (!porMes.TryGetValue(mes, out var fila))
      {
        fila = new DeclaracionImportada
        {
          ArchivoNombre = destino.ArchivoNombre,
          ArchivoSha256 = destino.ArchivoSha256,
          Rfc = destino.Rfc,
          Ejercicio = destino.Ejercicio,
          Periodo = mes,
          TipoDeclaracion = "N",
          EsParcial = true,
          NumeroOperacion = m.Groups["op"].Value,
          Estatus = Normalizar(m.Groups["estatus"].Value)
        };

        if (DateTime.TryParseExact(m.Groups["fecha"].Value, "dd/MM/yyyy",
              CultureInfo.InvariantCulture, DateTimeStyles.None, out var fecha))
        {
          fila.FechaPresentacion = fecha;
        }

        porMes[mes] = fila;
      }

      if (seccion == SeccionIngresos)
      {
        fila.Campos["IngresosNominalesPeriodo"] = monto;
      }
      else if (seccion == SeccionPagos)
      {
        fila.Campos["IsrACargo"] = monto;
      }
    }

    destino.Anteriores.AddRange(porMes.Values.OrderBy(x => x.Periodo));
  }
}
