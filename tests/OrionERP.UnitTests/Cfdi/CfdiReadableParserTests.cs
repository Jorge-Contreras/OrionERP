using OrionERP.Application.Features.Cfdi.HtmlCFDI;

namespace OrionERP.UnitTests.Cfdi;

public class CfdiReadableParserTests
{
    [Fact]
    public void Parse_IngresoCfdi_ReturnsKeyData()
    {
        var parser = new CfdiReadableParser();

        var doc = parser.Parse(IngresoXml);

        Assert.Equal("I", doc.TipoDeComprobante);
        Assert.Equal("MXN", doc.Moneda);
        Assert.Equal("90205", doc.LugarExpedicion);
        Assert.Equal("NUEVA WAL MART DE MEXICO", doc.Emisor?.Nombre);
        Assert.Equal("OHM191112Q26", doc.Receptor?.Rfc);
        Assert.Equal("32F710B0-C35A-4169-BC4A-9CA3925BBF2E", doc.Timbre?.Uuid);
        Assert.NotNull(doc.Impuestos);
        Assert.Equal("70.48", doc.Impuestos?.TotalTrasladados);
        Assert.Contains(doc.Conceptos, c => c.ClaveProdServ == "50161500");
    }

    [Fact]
    public void Parse_Pagos20Cfdi_ReadsPagoNodes()
    {
        var parser = new CfdiReadableParser();

        var doc = parser.Parse(Pago20Xml);

        Assert.Equal("P", doc.TipoDeComprobante);
        Assert.Equal("TME840315KT6", doc.Emisor?.Rfc);
        Assert.NotNull(doc.Pago20);
        Assert.Equal("778.00", doc.Pago20?.Totales?.MontoTotalPagos);

        var pago = Assert.Single(doc.Pago20!.Pagos);
        Assert.Equal("04", pago.FormaDePagoP);

        var docto = Assert.Single(pago.Documentos);
        Assert.Equal("aad6d134-a51f-431b-9104-736228757bbb", docto.IdDocumento);
        Assert.NotEmpty(docto.Traslados);
        Assert.NotEmpty(pago.Traslados);
    }

    private const string Pago20Xml = """
<?xml version="1.0" encoding="UTF-8"?>
<cfdi:Comprobante xsi:schemaLocation="http://www.sat.gob.mx/cfd/4 http://www.sat.gob.mx/sitio_internet/cfd/4/cfdv40.xsd http://www.sat.gob.mx/Pagos20 http://www.sat.gob.mx/sitio_internet/cfd/Pagos/Pagos20.xsd" Version="4.0" Fecha="2025-09-16T02:17:23" SubTotal="0" Moneda="XXX" Total="0" TipoDeComprobante="P" Exportacion="01" LugarExpedicion="06500" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:pago20="http://www.sat.gob.mx/Pagos20" xmlns:cfdi="http://www.sat.gob.mx/cfd/4">
  <cfdi:Emisor Rfc="TME840315KT6" Nombre="TELEFONOS DE MEXICO" RegimenFiscal="601"/>
  <cfdi:Receptor Rfc="OHM191112Q26" Nombre="ORION HABITAT DE MEXICO" DomicilioFiscalReceptor="90204" RegimenFiscalReceptor="601" UsoCFDI="CP01"/>
  <cfdi:Conceptos>
    <cfdi:Concepto ClaveProdServ="84111506" Cantidad="1" ClaveUnidad="ACT" Descripcion="Pago" ValorUnitario="0" Importe="0" ObjetoImp="01"/>
  </cfdi:Conceptos>
  <cfdi:Complemento>
    <tfd:TimbreFiscalDigital Version="1.1" UUID="8a97fdc0-b9c6-4f08-bbcc-edef6d7a4807" FechaTimbrado="2025-09-16T21:27:03" NoCertificadoSAT="00001000000717752386" xmlns:tfd="http://www.sat.gob.mx/TimbreFiscalDigital"/>
    <pago20:Pagos Version="2.0">
      <pago20:Totales TotalTrasladosImpuestoIVA16="107.31" TotalTrasladosBaseIVA16="670.69" MontoTotalPagos="778.00"/>
      <pago20:Pago Monto="778.00" MonedaP="MXN" FormaDePagoP="04" FechaPago="2025-09-15T00:01:00" TipoCambioP="1">
        <pago20:DoctoRelacionado Serie="CFDI" ImpSaldoInsoluto="308.99" ImpPagado="778.00" ImpSaldoAnt="1086.99" NumParcialidad="1" MonedaDR="MXN" Folio="00" IdDocumento="aad6d134-a51f-431b-9104-736228757bbb" EquivalenciaDR="1" ObjetoImpDR="02">
          <pago20:ImpuestosDR>
            <pago20:TrasladosDR>
              <pago20:TrasladoDR ImporteDR="107.31" TasaOCuotaDR="0.160000" TipoFactorDR="Tasa" ImpuestoDR="002" BaseDR="670.69"/>
            </pago20:TrasladosDR>
          </pago20:ImpuestosDR>
        </pago20:DoctoRelacionado>
        <pago20:ImpuestosP>
          <pago20:TrasladosP>
            <pago20:TrasladoP ImporteP="107.31" TasaOCuotaP="0.160000" TipoFactorP="Tasa" ImpuestoP="002" BaseP="670.69"/>
          </pago20:TrasladosP>
        </pago20:ImpuestosP>
      </pago20:Pago>
    </pago20:Pagos>
  </cfdi:Complemento>
</cfdi:Comprobante>
""";

    private const string IngresoXml = """
<?xml version="1.0" encoding="utf-8"?>
<cfdi:Comprobante xmlns:cfdi="http://www.sat.gob.mx/cfd/4" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xsi:schemaLocation="http://www.sat.gob.mx/cfd/4 http://www.sat.gob.mx/sitio_internet/cfd/4/cfdv40.xsd" Version="4.0" Fecha="2025-09-06T13:17:12" Moneda="MXN" TipoCambio="1" SubTotal="1200.90" Descuento="16.38" Total="1255.00" FormaPago="03" CondicionesDePago="Inmediato" TipoDeComprobante="I" MetodoPago="PUE" LugarExpedicion="90205" NoCertificado="00001000000714274329" Exportacion="01" Serie="IMADY" Folio="51815">
  <cfdi:Emisor Rfc="NWM9709244W4" Nombre="NUEVA WAL MART DE MEXICO" RegimenFiscal="601" />
  <cfdi:Receptor Rfc="OHM191112Q26" Nombre="ORION HABITAT DE MEXICO" UsoCFDI="G03" DomicilioFiscalReceptor="90204" RegimenFiscalReceptor="601" />
  <cfdi:Conceptos>
    <cfdi:Concepto ClaveProdServ="50161500" Cantidad="1" ClaveUnidad="H87" Unidad="PIEZAS" Descripcion="CANDEREL220" ValorUnitario="110.00" Importe="110.00" ObjetoImp="02"></cfdi:Concepto>
  </cfdi:Conceptos>
  <cfdi:Impuestos TotalImpuestosTrasladados="70.48"></cfdi:Impuestos>
  <cfdi:Complemento>
    <tfd:TimbreFiscalDigital xmlns:tfd="http://www.sat.gob.mx/TimbreFiscalDigital" Version="1.1" UUID="32F710B0-C35A-4169-BC4A-9CA3925BBF2E" FechaTimbrado="2025-09-06T14:18:12" RfcProvCertif="SST060807KU0" NoCertificadoSAT="00001000000711914678" />
  </cfdi:Complemento>
</cfdi:Comprobante>
""";
}
