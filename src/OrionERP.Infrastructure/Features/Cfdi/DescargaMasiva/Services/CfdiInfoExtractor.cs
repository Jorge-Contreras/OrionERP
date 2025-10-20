using System;
using System.Globalization;
using System.IO;
using System.Xml.Linq;

namespace OrionERP.Infrastructure.Features.Cfdi.DescargaMasiva.Services;

internal static class CfdiInfoExtractor
{
  // Supports CFDI 3.3 and 4.0 (namespaces cfd/3 and cfd/4)
  private static readonly XNamespace Cfdi3 = "http://www.sat.gob.mx/cfd/3";
  private static readonly XNamespace Cfdi4 = "http://www.sat.gob.mx/cfd/4";
  private static readonly XNamespace Tfd = "http://www.sat.gob.mx/TimbreFiscalDigital";

  public static (string? Uuid, string? RfcEmisor, string? RfcReceptor, DateTime? FechaUtc,
                 decimal? SubTotal, decimal? Total, string? Tipo)
      TryExtract(byte[] xml)
  {
    try
    {
      using var ms = new MemoryStream(xml, writable: false);
      var doc = XDocument.Load(ms, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);

      XElement? comp = doc.Root;
      if (comp is null) return default;

      // Handle both namespaces
      if (!IsComprobante(comp))
      {
        comp = doc.Root?.Name.Namespace == Cfdi3 ? doc.Root :
               doc.Root?.Name.Namespace == Cfdi4 ? doc.Root : null;
        if (comp is null || !IsComprobante(comp)) return default;
      }

      // Emisor / Receptor
      var ns = comp.Name.Namespace;
      var emisor = comp.Element(ns + "Emisor");
      var receptor = comp.Element(ns + "Receptor");

      string? rfcEmisor = emisor?.Attribute("Rfc")?.Value ?? emisor?.Attribute("rfc")?.Value;
      string? rfcReceptor = receptor?.Attribute("Rfc")?.Value ?? receptor?.Attribute("rfc")?.Value;

      // UUID in Timbre
      var complemento = comp.Element(ns + "Complemento");
      var tfd = complemento?.Element(Tfd + "TimbreFiscalDigital");
      string? uuid = tfd?.Attribute("UUID")?.Value ?? tfd?.Attribute("Uuid")?.Value;

      // Fechas y totales
      string? fechaTxt = comp.Attribute("Fecha")?.Value ?? comp.Attribute("fecha")?.Value;
      DateTime? fechaUtc = null;
      if (!string.IsNullOrWhiteSpace(fechaTxt))
      {
        // CFDI fecha is local time; we keep it as unspecified -> treat as UTC for consistency in UI
        if (DateTime.TryParse(fechaTxt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt))
          fechaUtc = DateTime.SpecifyKind(dt, DateTimeKind.Utc);
      }

      decimal? sub = TryDec(comp.Attribute("SubTotal")?.Value ?? comp.Attribute("subTotal")?.Value);
      decimal? tot = TryDec(comp.Attribute("Total")?.Value ?? comp.Attribute("total")?.Value);

      string? tipo = comp.Attribute("TipoDeComprobante")?.Value ?? comp.Attribute("tipoDeComprobante")?.Value;

      return (uuid, rfcEmisor, rfcReceptor, fechaUtc, sub, tot, tipo);
    }
    catch
    {
      return default;
    }
  }

  private static bool IsComprobante(XElement e) => e.Name.LocalName.Equals("Comprobante", StringComparison.OrdinalIgnoreCase);

  private static decimal? TryDec(string? s)
      => decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : null;
}
