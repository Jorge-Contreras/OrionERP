using OrionERP.Application.Features.Cfdi.HtmlCFDI;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace OrionERP.Web.Features.Cfdi.HtmlCFDI;

public sealed class CfdiPdfService : ICfdiPdfService
{
  private const string BrandPrimary = "#0B5A68";
  private const string BrandPrimaryDark = "#083F49";
  private const string BrandMuted = "#66757A";
  private const string BrandBorder = "#D8E2E0";
  private const string BrandSurface = "#F7FAF9";
  private const string BrandAccent = "#EAF3F1";

  public CfdiPdfService()
  {
    QuestPDF.Settings.License = LicenseType.Community;
  }

  public byte[] Generate(CfdiReadableDocument document)
  {
    ArgumentNullException.ThrowIfNull(document);

    return Document.Create(container =>
      {
        container.Page(page =>
        {
          page.Size(PageSizes.Letter);
          page.Margin(24);
          page.DefaultTextStyle(text => text.FontSize(8).FontColor(BrandPrimaryDark));

          page.Header().Element(header => ComposeHeader(header, document));
          page.Content().PaddingTop(8).Element(content => ComposeContent(content, document));
          page.Footer().PaddingTop(6).Element(footer => ComposeFooter(footer, document));
        });
      })
      .GeneratePdf();
  }

  private static void ComposeHeader(IContainer container, CfdiReadableDocument document)
  {
    container.Column(column =>
    {
      column.Spacing(8);

      column.Item().Row(row =>
      {
        row.RelativeItem().Column(title =>
        {
          title.Spacing(2);
          title.Item().Text("Comprobante Fiscal Digital por Internet")
            .FontSize(17)
            .SemiBold()
            .FontColor(BrandPrimaryDark);
          title.Item().Text($"{CfdiDisplay.TypeName(document.TipoDeComprobante)} | CFDI v{CfdiDisplay.Safe(document.Version)}")
            .FontSize(9)
            .FontColor(BrandMuted);
        });

        row.ConstantItem(185).AlignRight().Column(meta =>
        {
          meta.Spacing(2);
          meta.Item().AlignRight().Text(CfdiDisplay.Amount(GetPrimaryTotal(document), document.Moneda))
            .FontSize(15)
            .Bold()
            .FontColor(BrandPrimary);
          meta.Item().AlignRight().Text("Total")
            .FontSize(7)
            .FontColor(BrandMuted);
        });
      });

      column.Item().Element(SummaryBand).Row(row =>
      {
        row.Spacing(8);
        row.RelativeItem(1.1f).Element(cell => ComposeInlineValue(cell, "Serie / Folio", CfdiDisplay.SerieFolio(document)));
        row.RelativeItem(2.2f).Element(cell => ComposeInlineValue(cell, "Folio fiscal", document.Timbre?.Uuid));
        row.RelativeItem(1.2f).Element(cell => ComposeInlineValue(cell, "Fecha", document.Fecha));
        row.RelativeItem(1.1f).Element(cell => ComposeInlineValue(cell, "Tipo", CfdiDisplay.TypeLabel(document.TipoDeComprobante)));
      });
    });
  }

  private static void ComposeContent(IContainer container, CfdiReadableDocument document)
  {
    container.Column(column =>
    {
      column.Spacing(7);

      column.Item().Row(row =>
      {
        row.Spacing(8);
        row.RelativeItem().Element(section => ComposePartySection(section, "Emisor", document.Emisor));
        row.RelativeItem().Element(section => ComposePartySection(section, "Receptor", document.Receptor));
      });

      column.Item().Element(section => ComposeFiscalData(section, document));
      column.Item().Element(section => ComposeConcepts(section, document));

      if (string.Equals(document.TipoDeComprobante, "P", StringComparison.OrdinalIgnoreCase))
      {
        column.Item().Element(section => ComposePago20(section, document));
      }
      else
      {
        column.Item().Element(section => ComposeTaxesAndTotals(section, document));
      }

      column.Item().Element(section => ComposeTimbre(section, document));
    });
  }

  private static void ComposePartySection(IContainer container, string title, CfdiParty? party)
  {
    container.Element(SectionCard).Column(section =>
    {
      section.Spacing(4);
      section.Item().Element(SectionTitle).Text(title).SemiBold().FontSize(10).FontColor(BrandPrimaryDark);
      section.Item().Element(fields => ComposeFieldGrid(
        fields,
        [
          new FieldEntry("RFC", party?.Rfc),
          new FieldEntry("Razón social", party?.Nombre),
          new FieldEntry("Régimen fiscal", CfdiDisplay.PartyRegimen(party)),
          new FieldEntry("Uso CFDI", party?.UsoCfdi),
          new FieldEntry("Domicilio fiscal", party?.DomicilioFiscalReceptor)
        ],
        fieldsPerRow: 2));
    });
  }

  private static void ComposeFiscalData(IContainer container, CfdiReadableDocument document)
  {
    container.Element(SectionCard).Column(section =>
    {
      section.Spacing(4);
      section.Item().Element(SectionTitle).Text("Datos fiscales del comprobante")
        .SemiBold()
        .FontSize(10)
        .FontColor(BrandPrimaryDark);

      section.Item().Element(fields => ComposeFieldGrid(fields,
      [
        new FieldEntry("Tipo de comprobante", CfdiDisplay.TypeLabel(document.TipoDeComprobante)),
        new FieldEntry("Método de pago", document.MetodoPago),
        new FieldEntry("Forma de pago", document.FormaPago),
        new FieldEntry("Moneda", document.Moneda),
        new FieldEntry("Tipo de cambio", document.TipoCambio),
        new FieldEntry("Lugar de expedición", document.LugarExpedicion),
        new FieldEntry("Exportación", document.Exportacion),
        new FieldEntry("Certificado emisor", document.NoCertificado),
        new FieldEntry("Condiciones de pago", document.CondicionesDePago)
      ]));
    });
  }

  private static void ComposeConcepts(IContainer container, CfdiReadableDocument document)
  {
    container.Element(SectionCard).Column(section =>
    {
      section.Spacing(7);
      section.Item().Element(SectionTitle).Text("Conceptos")
        .SemiBold()
        .FontSize(10)
        .FontColor(BrandPrimaryDark);

      if (document.Conceptos.Count == 0)
      {
        section.Item().PaddingVertical(5).Text("Sin conceptos.").FontColor(BrandMuted);
        return;
      }

      section.Item().Table(table =>
      {
        table.ColumnsDefinition(columns =>
        {
          columns.RelativeColumn(0.75f);
          columns.RelativeColumn(3.1f);
          columns.RelativeColumn(0.65f);
          columns.RelativeColumn(0.8f);
          columns.RelativeColumn(0.95f);
          columns.RelativeColumn(0.95f);
          columns.RelativeColumn(0.85f);
          columns.RelativeColumn(0.7f);
        });

        AddHeader(table, ["Clave", "Descripción", "Cant.", "Unidad", "Valor unit.", "Importe", "Desc.", "Obj."]);

        foreach (var concepto in document.Conceptos)
        {
          table.Cell().Element(TableBodyCell).Text(CfdiDisplay.Safe(concepto.ClaveProdServ));
          table.Cell().Element(TableBodyCell).Column(cell =>
          {
            cell.Spacing(1);
            cell.Item().Text(CfdiDisplay.Safe(concepto.Descripcion)).SemiBold();
            if (CfdiDisplay.HasRealValue(concepto.NoIdentificacion))
            {
              cell.Item().Text($"No. ident.: {concepto.NoIdentificacion}").FontSize(6).FontColor(BrandMuted);
            }
          });
          table.Cell().Element(TableBodyCell).AlignRight().Text(CfdiDisplay.Safe(concepto.Cantidad));
          table.Cell().Element(TableBodyCell).Text(CfdiDisplay.FirstNonEmpty(concepto.ClaveUnidad, concepto.Unidad));
          table.Cell().Element(TableBodyCell).AlignRight().Text(CfdiDisplay.Amount(concepto.ValorUnitario, document.Moneda));
          table.Cell().Element(TableBodyCell).AlignRight().Text(CfdiDisplay.Amount(concepto.Importe, document.Moneda));
          table.Cell().Element(TableBodyCell).AlignRight().Text(CfdiDisplay.Amount(concepto.Descuento, document.Moneda));
          table.Cell().Element(TableBodyCell).Text(CfdiDisplay.Safe(concepto.ObjetoImp));

          if (concepto.Traslados.Count > 0 || concepto.Retenciones.Count > 0)
          {
            table.Cell()
              .ColumnSpan(8)
              .Element(TableTaxCell)
              .Column(taxes => ComposeConceptTaxes(taxes, concepto, document.Moneda));
          }
        }
      });
    });
  }

  private static void ComposeTaxesAndTotals(IContainer container, CfdiReadableDocument document)
  {
    container.Row(row =>
    {
      row.Spacing(8);
      row.RelativeItem(1.55f).Element(section => ComposeTaxes(section, document));
      row.RelativeItem().Element(section => ComposeTotals(section, document));
    });
  }

  private static void ComposeTaxes(IContainer container, CfdiReadableDocument document)
  {
    container.Element(SectionCard).Column(section =>
    {
      section.Spacing(6);
      section.Item().Element(SectionTitle).Text("Impuestos")
        .SemiBold()
        .FontSize(10)
        .FontColor(BrandPrimaryDark);

      if (document.Impuestos is null)
      {
        section.Item().Text("Sin impuestos registrados.").FontColor(BrandMuted);
        return;
      }

      section.Item().Element(fields => ComposeFieldGrid(fields,
      [
        new FieldEntry("Total trasladados", CfdiDisplay.Amount(document.Impuestos.TotalTrasladados, document.Moneda)),
        new FieldEntry("Total retenidos", CfdiDisplay.Amount(document.Impuestos.TotalRetenidos, document.Moneda))
      ]));

      ComposeTaxList(section, "Traslados", document.Impuestos.Traslados, document.Moneda);
      ComposeTaxList(section, "Retenciones", document.Impuestos.Retenciones, document.Moneda);
    });
  }

  private static void ComposeTotals(IContainer container, CfdiReadableDocument document)
  {
    container.Element(SectionCard).Column(section =>
    {
      section.Spacing(6);
      section.Item().Element(SectionTitle).Text("Totales")
        .SemiBold()
        .FontSize(10)
        .FontColor(BrandPrimaryDark);

      ComposeTotalLine(section, "Subtotal", CfdiDisplay.Amount(document.SubTotal, document.Moneda));
      ComposeTotalLine(section, "Descuento", CfdiDisplay.Amount(document.Descuento, document.Moneda));
      if (document.Impuestos is not null)
      {
        ComposeTotalLine(section, "IVA trasladado", CfdiDisplay.Amount(document.Impuestos.TotalTrasladados, document.Moneda));
        ComposeTotalLine(section, "Retenciones", CfdiDisplay.Amount(document.Impuestos.TotalRetenidos, document.Moneda));
      }

      section.Item().BorderTop(1).BorderColor(BrandBorder).PaddingTop(5).Row(line =>
      {
        line.RelativeItem().Text("Total").SemiBold().FontSize(10);
        line.ConstantItem(96).AlignRight().Text(CfdiDisplay.Amount(document.Total, document.Moneda))
          .Bold()
          .FontSize(12)
          .FontColor(BrandPrimary);
      });
    });
  }

  private static void ComposePago20(IContainer container, CfdiReadableDocument document)
  {
    container.Element(SectionCard).Column(section =>
    {
      section.Spacing(8);
      section.Item().Element(SectionTitle).Text("Complemento de pagos 2.0")
        .SemiBold()
        .FontSize(10)
        .FontColor(BrandPrimaryDark);

      var pago20 = document.Pago20;
      if (pago20 is null)
      {
        section.Item().Text("Sin información de complemento de pago.").FontColor(BrandMuted);
        return;
      }

      section.Item().Element(fields => ComposeFieldGrid(fields,
      [
        new FieldEntry("Versión pagos", pago20.Version),
        new FieldEntry("Monto total pagos", CfdiDisplay.Amount(pago20.Totales?.MontoTotalPagos, document.Moneda)),
        new FieldEntry("Base IVA 16%", CfdiDisplay.Amount(pago20.Totales?.TotalTrasladosBaseIva16, document.Moneda)),
        new FieldEntry("IVA 16%", CfdiDisplay.Amount(pago20.Totales?.TotalTrasladosImpuestoIva16, document.Moneda))
      ]));

      var index = 1;
      foreach (var pago in pago20.Pagos)
      {
        section.Item().Element(payment => ComposePayment(payment, pago, index, document.Moneda));
        index++;
      }
    });
  }

  private static void ComposePayment(IContainer container, CfdiPago20Pago pago, int index, string? currency)
  {
    container.Border(1).BorderColor(BrandBorder).Background(Colors.White).Padding(8).Column(section =>
    {
      section.Spacing(6);
      section.Item().Text($"Pago {index}").SemiBold().FontColor(BrandPrimaryDark);
      section.Item().Element(fields => ComposeFieldGrid(fields,
      [
        new FieldEntry("Fecha pago", pago.FechaPago),
        new FieldEntry("Forma de pago", pago.FormaDePagoP),
        new FieldEntry("Moneda", pago.MonedaP),
        new FieldEntry("Tipo de cambio", pago.TipoCambioP),
        new FieldEntry("Monto", CfdiDisplay.Amount(pago.Monto, pago.MonedaP ?? currency))
      ]));

      if (pago.Documentos.Count > 0)
      {
        section.Item().PaddingTop(3).Text("Documentos relacionados").SemiBold().FontSize(8).FontColor(BrandMuted);
        section.Item().Element(table => ComposePaymentDocumentsTable(table, pago.Documentos, pago.MonedaP ?? currency));
      }

      if (pago.Traslados.Count > 0)
      {
        section.Item().Text("Traslados del pago").SemiBold().FontSize(8).FontColor(BrandMuted);
        section.Item().Text(string.Join(" | ", pago.Traslados.Select(t => FormatPaymentTax(t, pago.MonedaP ?? currency))))
          .FontSize(6.5f);
      }
    });
  }

  private static void ComposePaymentDocumentsTable(IContainer container, IReadOnlyList<CfdiPago20Docto> documents, string? currency)
  {
    container.Table(table =>
    {
      table.ColumnsDefinition(columns =>
      {
        columns.RelativeColumn(2.4f);
        columns.RelativeColumn(1f);
        columns.RelativeColumn(0.7f);
        columns.RelativeColumn(0.9f);
        columns.RelativeColumn(0.9f);
        columns.RelativeColumn(0.9f);
        columns.RelativeColumn(0.9f);
      });

      AddHeader(table, ["UUID", "Serie/Folio", "Mon.", "Parc.", "Saldo ant.", "Pagado", "Saldo"]);

      foreach (var document in documents)
      {
        table.Cell().Element(TableBodyCell).Text(CfdiDisplay.Safe(document.IdDocumento));
        table.Cell().Element(TableBodyCell).Text(CfdiDisplay.FirstNonEmpty($"{document.Serie} {document.Folio}".Trim(), "-"));
        table.Cell().Element(TableBodyCell).Text(CfdiDisplay.Safe(document.MonedaDr));
        table.Cell().Element(TableBodyCell).Text(CfdiDisplay.Safe(document.NumParcialidad));
        table.Cell().Element(TableBodyCell).AlignRight().Text(CfdiDisplay.Amount(document.ImpSaldoAnt, document.MonedaDr ?? currency));
        table.Cell().Element(TableBodyCell).AlignRight().Text(CfdiDisplay.Amount(document.ImpPagado, document.MonedaDr ?? currency));
        table.Cell().Element(TableBodyCell).AlignRight().Text(CfdiDisplay.Amount(document.ImpSaldoInsoluto, document.MonedaDr ?? currency));

        if (document.Traslados.Count > 0)
        {
          table.Cell()
            .ColumnSpan(7)
            .Element(TableTaxCell)
            .Text(string.Join(" | ", document.Traslados.Select(t => FormatPaymentTax(t, document.MonedaDr ?? currency))))
            .FontSize(6.5f);
        }
      }
    });
  }

  private static void ComposeTimbre(IContainer container, CfdiReadableDocument document)
  {
    if (document.Timbre is null)
    {
      return;
    }

    container.Element(SectionCard).Column(section =>
    {
      section.Spacing(7);
      section.Item().Element(SectionTitle).Text("Timbre Fiscal Digital")
        .SemiBold()
        .FontSize(10)
        .FontColor(BrandPrimaryDark);

      section.Item().Element(fields => ComposeFieldGrid(fields,
      [
        new FieldEntry("UUID", document.Timbre.Uuid),
        new FieldEntry("Fecha de timbrado", document.Timbre.FechaTimbrado),
        new FieldEntry("Certificado SAT", document.Timbre.NoCertificadoSat),
        new FieldEntry("RFC proveedor certificación", document.Timbre.RfcProvCertif),
        new FieldEntry("Leyenda", document.Timbre.Leyenda)
      ]));

      ComposeLongText(section, "Sello digital emisor", document.Timbre.SelloCfd);
      ComposeLongText(section, "Sello digital SAT", document.Timbre.SelloSat);
    });
  }

  private static void ComposeFooter(IContainer container, CfdiReadableDocument document)
  {
    container.Column(column =>
    {
      column.Item().LineHorizontal(1).LineColor(BrandBorder);
      column.Item().PaddingTop(4).Row(row =>
      {
        row.RelativeItem().Text($"CFDI | {CfdiDisplay.SerieFolio(document)} | {CfdiDisplay.Safe(document.Timbre?.Uuid)}")
          .FontSize(7)
          .FontColor(BrandMuted);

        row.ConstantItem(90).AlignRight().Text(text =>
        {
          text.DefaultTextStyle(style => style.FontSize(7).FontColor(BrandMuted));
          text.Span("Pagina ");
          text.CurrentPageNumber();
          text.Span(" / ");
          text.TotalPages();
        });
      });
    });
  }

  private static void ComposeFieldGrid(IContainer container, IReadOnlyList<FieldEntry> fields, int fieldsPerRow = 3)
  {
    var visibleFields = fields
      .Where(field => CfdiDisplay.HasRealValue(field.Value))
      .ToList();

    if (visibleFields.Count == 0)
    {
      container.Text("-").FontColor(BrandMuted);
      return;
    }

    container
      .Background(Colors.White)
      .Border(1)
      .BorderColor(BrandBorder)
      .PaddingHorizontal(6)
      .PaddingVertical(4)
      .Column(column =>
      {
        column.Spacing(2);

        foreach (var rowFields in visibleFields.Chunk(Math.Max(1, fieldsPerRow)))
        {
          column.Item().Text(text =>
          {
            for (var index = 0; index < rowFields.Length; index++)
            {
              if (index > 0)
              {
                text.Span("  |  ").FontSize(7).FontColor(BrandMuted);
              }

              text.Span($"{rowFields[index].Label}: ")
                .FontSize(6.6f)
                .SemiBold()
                .FontColor(BrandMuted);
              text.Span(CfdiDisplay.Safe(rowFields[index].Value))
                .FontSize(7.4f)
                .SemiBold()
                .FontColor(BrandPrimaryDark);
            }
          });
        }
      });
  }

  private static void ComposeInlineValue(IContainer container, string label, string? value)
  {
    container.Column(column =>
    {
      column.Spacing(1);
      column.Item().Text(label).FontSize(6.5f).SemiBold().FontColor(BrandMuted);
      column.Item().Text(CfdiDisplay.Safe(value)).FontSize(8).SemiBold().FontColor(BrandPrimaryDark);
    });
  }

  private static void ComposeConceptTaxes(ColumnDescriptor column, CfdiConcepto concepto, string? currency)
  {
    if (concepto.Traslados.Count > 0)
    {
      column.Item().Text($"Traslados: {string.Join(" | ", concepto.Traslados.Select(t => FormatTax(t, currency)))}").FontSize(6.5f);
    }

    if (concepto.Retenciones.Count > 0)
    {
      column.Item().Text($"Retenciones: {string.Join(" | ", concepto.Retenciones.Select(t => FormatTax(t, currency)))}").FontSize(6.5f);
    }
  }

  private static void ComposeTaxList(ColumnDescriptor section, string title, IReadOnlyList<CfdiImpuestoDetalle> taxes, string? currency)
  {
    if (taxes.Count == 0)
    {
      return;
    }

    section.Item().PaddingTop(2).Text(title).SemiBold().FontSize(8).FontColor(BrandMuted);
    foreach (var tax in taxes)
    {
      section.Item().Text(FormatTax(tax, currency)).FontSize(6.8f);
    }
  }

  private static void ComposeTotalLine(ColumnDescriptor section, string label, string value)
  {
    if (!CfdiDisplay.HasRealValue(value))
    {
      return;
    }

    section.Item().Row(line =>
    {
      line.RelativeItem().Text(label).FontColor(BrandMuted);
      line.ConstantItem(96).AlignRight().Text(value).SemiBold();
    });
  }

  private static void ComposeLongText(ColumnDescriptor section, string label, string? value)
  {
    if (!CfdiDisplay.HasRealValue(value))
    {
      return;
    }

    section.Item().Column(column =>
    {
      column.Spacing(2);
      column.Item().Text(label).SemiBold().FontSize(7).FontColor(BrandMuted);
      column.Item().Background(Colors.White).Border(1).BorderColor(BrandBorder).Padding(5)
        .Text(CfdiDisplay.Safe(value))
        .FontSize(5.6f);
    });
  }

  private static void AddHeader(TableDescriptor table, IReadOnlyList<string> headers)
  {
    table.Header(header =>
    {
      foreach (var item in headers)
      {
        header.Cell().Element(TableHeaderCell).Text(item).SemiBold();
      }
    });
  }

  private static string FormatTax(CfdiImpuestoDetalle tax, string? currency)
    => $"{CfdiDisplay.Safe(tax.Impuesto)} {CfdiDisplay.Safe(tax.TipoFactor)} {CfdiDisplay.Safe(tax.TasaOCuota)}; Base {CfdiDisplay.Amount(tax.Base, currency)}; Importe {CfdiDisplay.Amount(tax.Importe, currency)}";

  private static string FormatPaymentTax(CfdiPago20Traslado tax, string? currency)
    => $"{CfdiDisplay.Safe(tax.Impuesto)} {CfdiDisplay.Safe(tax.TipoFactor)} {CfdiDisplay.Safe(tax.TasaOCuota)}; Base {CfdiDisplay.Amount(tax.Base, currency)}; Importe {CfdiDisplay.Amount(tax.Importe, currency)}";

  private static string? GetPrimaryTotal(CfdiReadableDocument document)
    => string.Equals(document.TipoDeComprobante, "P", StringComparison.OrdinalIgnoreCase)
      ? document.Pago20?.Totales?.MontoTotalPagos ?? document.Total
      : document.Total;

  private static IContainer SectionCard(IContainer container)
    => container
      .Border(1)
      .BorderColor(BrandBorder)
      .Background(BrandSurface)
      .Padding(7);

  private static IContainer SectionTitle(IContainer container)
    => container
      .PaddingBottom(4)
      .BorderBottom(1)
      .BorderColor(BrandBorder);

  private static IContainer SummaryBand(IContainer container)
    => container
      .Background(BrandAccent)
      .Border(1)
      .BorderColor(BrandBorder)
      .PaddingHorizontal(8)
      .PaddingVertical(6);

  private static IContainer TableHeaderCell(IContainer container)
    => container
      .Background(BrandPrimary)
      .PaddingHorizontal(4)
      .PaddingVertical(4)
      .DefaultTextStyle(style => style.FontColor(Colors.White).FontSize(6.8f));

  private static IContainer TableBodyCell(IContainer container)
    => container
      .Background(Colors.White)
      .BorderBottom(1)
      .BorderColor(BrandBorder)
      .PaddingHorizontal(4)
      .PaddingVertical(4)
      .DefaultTextStyle(style => style.FontSize(6.8f));

  private static IContainer TableTaxCell(IContainer container)
    => container
      .Background(BrandAccent)
      .BorderBottom(1)
      .BorderColor(BrandBorder)
      .PaddingHorizontal(5)
      .PaddingVertical(4)
      .DefaultTextStyle(style => style.FontSize(6.5f).FontColor(BrandPrimaryDark));

  private sealed record FieldEntry(string Label, string? Value);
}
