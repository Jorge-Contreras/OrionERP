using System.Xml;
using System.Xml.Linq;

namespace OrionERP.Application.Features.Cfdi.HtmlCFDI;

public sealed class CfdiReadableParser
{
  private static readonly XNamespace CfdiNs = "http://www.sat.gob.mx/cfd/4";
  private static readonly XNamespace TimbreNs = "http://www.sat.gob.mx/TimbreFiscalDigital";
  private static readonly XNamespace Pago20Ns = "http://www.sat.gob.mx/Pagos20";

  public CfdiReadableDocument Parse(string xmlText)
  {
    if (string.IsNullOrWhiteSpace(xmlText))
      throw new InvalidOperationException("El XML del CFDI está vacío.");

    XDocument document;
    try
    {
      document = XDocument.Parse(xmlText, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
    }
    catch (XmlException ex)
    {
      throw new InvalidOperationException("El archivo no es un XML válido.", ex);
    }

    var comprobante = document.Root ?? throw new InvalidOperationException("El XML no contiene un nodo raíz.");
    if (comprobante.Name != CfdiNs + "Comprobante")
      throw new InvalidOperationException("El XML no corresponde a un CFDI 4.0.");

    var tipo = GetAttribute(comprobante, "TipoDeComprobante")
               ?? throw new InvalidOperationException("El CFDI no indica el TipoDeComprobante.");
    tipo = tipo.ToUpperInvariant();

    if (tipo is not ("I" or "E" or "N" or "P"))
      throw new InvalidOperationException($"TipoDeComprobante no soportado: {tipo}.");

    var readable = new CfdiReadableDocument
    {
      TipoDeComprobante = tipo,
      Version = GetAttribute(comprobante, "Version"),
      Serie = GetAttribute(comprobante, "Serie"),
      Folio = GetAttribute(comprobante, "Folio"),
      Fecha = GetAttribute(comprobante, "Fecha"),
      LugarExpedicion = GetAttribute(comprobante, "LugarExpedicion"),
      Moneda = GetAttribute(comprobante, "Moneda"),
      TipoCambio = GetAttribute(comprobante, "TipoCambio"),
      MetodoPago = GetAttribute(comprobante, "MetodoPago"),
      FormaPago = GetAttribute(comprobante, "FormaPago"),
      SubTotal = GetAttribute(comprobante, "SubTotal"),
      Descuento = GetAttribute(comprobante, "Descuento"),
      Total = GetAttribute(comprobante, "Total"),
      Exportacion = GetAttribute(comprobante, "Exportacion"),
      CondicionesDePago = GetAttribute(comprobante, "CondicionesDePago")
    };

    readable.Emisor = ParseParty(comprobante.Element(CfdiNs + "Emisor"));
    readable.Receptor = ParseParty(comprobante.Element(CfdiNs + "Receptor"));
    readable.Conceptos.AddRange(ParseConceptos(comprobante.Element(CfdiNs + "Conceptos")));

    var impuestosNode = comprobante.Element(CfdiNs + "Impuestos");
    readable.Impuestos = ParseImpuestos(impuestosNode);

    var complemento = comprobante.Element(CfdiNs + "Complemento");
    readable.Timbre = ParseTimbre(complemento);
    if (tipo == "P")
    {
      readable.Pago20 = ParsePago20(complemento);
    }

    return readable;
  }

  private static CfdiParty? ParseParty(XElement? element)
  {
    if (element is null)
      return null;

    return new CfdiParty
    {
      Rfc = GetAttribute(element, "Rfc"),
      Nombre = GetAttribute(element, "Nombre"),
      RegimenFiscal = GetAttribute(element, "RegimenFiscal"),
      RegimenFiscalReceptor = GetAttribute(element, "RegimenFiscalReceptor"),
      UsoCfdi = GetAttribute(element, "UsoCFDI"),
      DomicilioFiscalReceptor = GetAttribute(element, "DomicilioFiscalReceptor")
    };
  }

  private static IEnumerable<CfdiConcepto> ParseConceptos(XElement? conceptosNode)
  {
    if (conceptosNode is null)
      yield break;

    foreach (var c in conceptosNode.Elements(CfdiNs + "Concepto"))
    {
      var concepto = new CfdiConcepto
      {
        ClaveProdServ = GetAttribute(c, "ClaveProdServ"),
        NoIdentificacion = GetAttribute(c, "NoIdentificacion"),
        Cantidad = GetAttribute(c, "Cantidad"),
        ClaveUnidad = GetAttribute(c, "ClaveUnidad"),
        Unidad = GetAttribute(c, "Unidad"),
        Descripcion = GetAttribute(c, "Descripcion"),
        ValorUnitario = GetAttribute(c, "ValorUnitario"),
        Importe = GetAttribute(c, "Importe"),
        Descuento = GetAttribute(c, "Descuento"),
        ObjetoImp = GetAttribute(c, "ObjetoImp")
      };

      var impuestosNode = c.Element(CfdiNs + "Impuestos");
      if (impuestosNode is not null)
      {
        foreach (var traslado in impuestosNode.Element(CfdiNs + "Traslados")?.Elements(CfdiNs + "Traslado")
                   ?? Enumerable.Empty<XElement>())
        {
          concepto.Traslados.Add(ParseImpuestoDetalle(traslado));
        }

        foreach (var retencion in impuestosNode.Element(CfdiNs + "Retenciones")?.Elements(CfdiNs + "Retencion")
                   ?? Enumerable.Empty<XElement>())
        {
          concepto.Retenciones.Add(ParseImpuestoDetalle(retencion));
        }
      }

      yield return concepto;
    }
  }

  private static CfdiImpuestos? ParseImpuestos(XElement? node)
  {
    if (node is null)
      return null;

    var impuestos = new CfdiImpuestos
    {
      TotalTrasladados = GetAttribute(node, "TotalImpuestosTrasladados"),
      TotalRetenidos = GetAttribute(node, "TotalImpuestosRetenidos")
    };

    foreach (var traslado in node.Element(CfdiNs + "Traslados")?.Elements(CfdiNs + "Traslado")
               ?? Enumerable.Empty<XElement>())
    {
      impuestos.Traslados.Add(ParseImpuestoDetalle(traslado));
    }

    foreach (var retencion in node.Element(CfdiNs + "Retenciones")?.Elements(CfdiNs + "Retencion")
               ?? Enumerable.Empty<XElement>())
    {
      impuestos.Retenciones.Add(ParseImpuestoDetalle(retencion));
    }

    return impuestos;
  }

  private static CfdiImpuestoDetalle ParseImpuestoDetalle(XElement element)
    => new()
    {
      Impuesto = GetAttribute(element, "Impuesto") ?? GetAttribute(element, "ImpuestoP") ?? GetAttribute(element, "ImpuestoDR"),
      TipoFactor = GetAttribute(element, "TipoFactor") ?? GetAttribute(element, "TipoFactorP") ?? GetAttribute(element, "TipoFactorDR"),
      TasaOCuota = GetAttribute(element, "TasaOCuota") ?? GetAttribute(element, "TasaOCuotaP") ?? GetAttribute(element, "TasaOCuotaDR"),
      Base = GetAttribute(element, "Base") ?? GetAttribute(element, "BaseP") ?? GetAttribute(element, "BaseDR"),
      Importe = GetAttribute(element, "Importe") ?? GetAttribute(element, "ImporteP") ?? GetAttribute(element, "ImporteDR")
    };

  private static CfdiTimbre? ParseTimbre(XElement? complemento)
  {
    if (complemento is null)
      return null;

    var timbre = complemento.Elements().FirstOrDefault(e => e.Name == TimbreNs + "TimbreFiscalDigital");
    if (timbre is null)
      return null;

    return new CfdiTimbre
    {
      Uuid = GetAttribute(timbre, "UUID"),
      FechaTimbrado = GetAttribute(timbre, "FechaTimbrado"),
      NoCertificadoSat = GetAttribute(timbre, "NoCertificadoSAT"),
      RfcProvCertif = GetAttribute(timbre, "RfcProvCertif"),
      Leyenda = GetAttribute(timbre, "Leyenda"),
      SelloCfd = GetAttribute(timbre, "SelloCFD"),
      SelloSat = GetAttribute(timbre, "SelloSAT")
    };
  }

  private static CfdiPago20Data? ParsePago20(XElement? complemento)
  {
    if (complemento is null)
      return null;

    var pagosNode = complemento.Elements().FirstOrDefault(e => e.Name == Pago20Ns + "Pagos");
    if (pagosNode is null)
      return null;

    var data = new CfdiPago20Data
    {
      Version = GetAttribute(pagosNode, "Version")
    };

    var totalesNode = pagosNode.Element(Pago20Ns + "Totales");
    if (totalesNode is not null)
    {
      data.Totales = new CfdiPago20Totales
      {
        MontoTotalPagos = GetAttribute(totalesNode, "MontoTotalPagos"),
        TotalTrasladosBaseIva16 = GetAttribute(totalesNode, "TotalTrasladosBaseIVA16"),
        TotalTrasladosImpuestoIva16 = GetAttribute(totalesNode, "TotalTrasladosImpuestoIVA16")
      };
    }

    foreach (var pagoNode in pagosNode.Elements(Pago20Ns + "Pago"))
    {
      var pago = new CfdiPago20Pago
      {
        FechaPago = GetAttribute(pagoNode, "FechaPago"),
        FormaDePagoP = GetAttribute(pagoNode, "FormaDePagoP"),
        MonedaP = GetAttribute(pagoNode, "MonedaP"),
        TipoCambioP = GetAttribute(pagoNode, "TipoCambioP"),
        Monto = GetAttribute(pagoNode, "Monto")
      };

      foreach (var doctoNode in pagoNode.Elements(Pago20Ns + "DoctoRelacionado"))
      {
        var docto = new CfdiPago20Docto
        {
          IdDocumento = GetAttribute(doctoNode, "IdDocumento"),
          Serie = GetAttribute(doctoNode, "Serie"),
          Folio = GetAttribute(doctoNode, "Folio"),
          MonedaDr = GetAttribute(doctoNode, "MonedaDR"),
          NumParcialidad = GetAttribute(doctoNode, "NumParcialidad"),
          ImpSaldoAnt = GetAttribute(doctoNode, "ImpSaldoAnt"),
          ImpPagado = GetAttribute(doctoNode, "ImpPagado"),
          ImpSaldoInsoluto = GetAttribute(doctoNode, "ImpSaldoInsoluto"),
          EquivalenciaDr = GetAttribute(doctoNode, "EquivalenciaDR"),
          ObjetoImpDr = GetAttribute(doctoNode, "ObjetoImpDR")
        };

        var impuestosDr = doctoNode.Element(Pago20Ns + "ImpuestosDR");
        var trasladosDr = impuestosDr?.Element(Pago20Ns + "TrasladosDR");
        if (trasladosDr is not null)
        {
          foreach (var traslado in trasladosDr.Elements(Pago20Ns + "TrasladoDR"))
          {
            docto.Traslados.Add(ParsePagoTraslado(traslado));
          }
        }

        pago.Documentos.Add(docto);
      }

      var impuestosP = pagoNode.Element(Pago20Ns + "ImpuestosP");
      var trasladosP = impuestosP?.Element(Pago20Ns + "TrasladosP");
      if (trasladosP is not null)
      {
        foreach (var traslado in trasladosP.Elements(Pago20Ns + "TrasladoP"))
        {
          pago.Traslados.Add(ParsePagoTraslado(traslado));
        }
      }

      data.Pagos.Add(pago);
    }

    return data;
  }

  private static CfdiPago20Traslado ParsePagoTraslado(XElement element)
    => new()
    {
      Base = GetAttribute(element, "BaseP") ?? GetAttribute(element, "BaseDR") ?? GetAttribute(element, "Base"),
      Impuesto = GetAttribute(element, "ImpuestoP") ?? GetAttribute(element, "ImpuestoDR") ?? GetAttribute(element, "Impuesto"),
      TipoFactor = GetAttribute(element, "TipoFactorP") ?? GetAttribute(element, "TipoFactorDR") ?? GetAttribute(element, "TipoFactor"),
      TasaOCuota = GetAttribute(element, "TasaOCuotaP") ?? GetAttribute(element, "TasaOCuotaDR") ?? GetAttribute(element, "TasaOCuota"),
      Importe = GetAttribute(element, "ImporteP") ?? GetAttribute(element, "ImporteDR") ?? GetAttribute(element, "Importe")
    };

  private static string? GetAttribute(XElement element, string name) => element.Attribute(name)?.Value;
}
