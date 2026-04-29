using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.AspNetCore.Hosting;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace OrionERP.Web.Features.Reservaciones.ListaReservaciones;

public sealed class ReservacionPdfService : IReservacionPdfService
{
  private const string BrandPrimary = "#0B5A68";
  private const string BrandPrimaryDark = "#083F49";
  private const string BrandMuted = "#6B7E83";
  private const string BrandBorder = "#D7E2E0";
  private const string BrandSurface = "#F8FBFA";
  private readonly string _logoSvg;

  public ReservacionPdfService(IWebHostEnvironment environment)
  {
    QuestPDF.Settings.License = LicenseType.Community;

    var logoPath = Path.Combine(environment.WebRootPath, "Images", "BonhomiaSuitesLetterheadLogo.svg");
    _logoSvg = File.Exists(logoPath)
      ? File.ReadAllText(logoPath)
      : FallbackLogoSvg;
  }

  public byte[] Generate(ReservacionPdfDocumentModel model)
  {
    return Document.Create(container =>
      {
        container.Page(page =>
        {
          page.Size(PageSizes.Letter);
          page.Margin(28);
          page.DefaultTextStyle(text => text.FontSize(10).FontColor(BrandPrimaryDark));

          page.Header().Element(header => ComposeHeader(header, model));
          page.Content().PaddingTop(12).Element(content => ComposeContent(content, model));
          page.Footer().PaddingTop(6).Element(ComposeFooter);
        });
      })
      .GeneratePdf();
  }

  private void ComposeHeader(IContainer container, ReservacionPdfDocumentModel model)
  {
    container.Column(column =>
    {
      column.Spacing(8);

      column.Item().Row(row =>
      {
        row.ConstantItem(74).Height(74).Svg(_logoSvg);
        row.RelativeItem().PaddingLeft(12).Column(textColumn =>
        {
          textColumn.Spacing(2);
          textColumn.Item().Text("Bonhomia Suites")
            .FontSize(25).FontFamily("Tahoma")
            .Bold()
            .FontColor(BrandPrimary);

          textColumn.Item().Text("Relajate, Ya estas en casa")
            .FontSize(11).FontFamily("Tahoma")
            .Italic()
            .FontColor(BrandMuted);

          textColumn.Item().PaddingTop(4).Text($"Recibo de Reservacion #{model.ReservationId}")
            .FontSize(15)
            .SemiBold()
            .FontColor(BrandPrimaryDark);
        });

        row.ConstantItem(150).AlignRight().Column(meta =>
        {
          meta.Spacing(2);
          meta.Item().AlignRight().Text($"Generado: {model.GeneratedAt}")
            .FontSize(9)
            .FontColor(BrandMuted);
          meta.Item().AlignRight().Text($"Status: {Safe(model.Status)}")
            .FontSize(9)
            .FontColor(BrandMuted);
        });
      });

      column.Item().LineHorizontal(1).LineColor(BrandBorder);
    });
  }

  private void ComposeContent(IContainer container, ReservacionPdfDocumentModel model)
  {
    container.Column(column =>
    {
      column.Spacing(14);

      column.Item().Element(SectionCard).Column(section =>
      {
        section.Spacing(8);
        section.Item().Element(SectionTitle).Text("Datos de la Reservacion")
          .FontSize(12)
          .SemiBold()
          .FontColor(BrandPrimaryDark);

        section.Item().Element(container => ComposeReservationSummary(container, model));

        section.Item().Element(CompactFieldBlock).Column(field =>
        {
          field.Spacing(1);
          field.Item().Text("Notas").FontSize(8).Bold().FontColor(BrandMuted);
          field.Item().Text(string.IsNullOrWhiteSpace(model.Notes) ? "Sin notas." : model.Notes)
            .FontSize(9);
        });
      });

      column.Item().Element(SectionCard).Column(section =>
      {
        section.Spacing(8);
        section.Item().Element(SectionTitle).Text("Totales")
          .FontSize(12)
          .SemiBold()
          .FontColor(BrandPrimaryDark);

        section.Item().Element(container => ComposeTotalsSummary(container, model));
      });

      column.Item().Element(SectionCard).Column(section =>
      {
        section.Spacing(10);
        section.Item().Element(SectionTitle).Text("Suites")
          .FontSize(12)
          .SemiBold()
          .FontColor(BrandPrimaryDark);

        section.Item().Element(container => ComposeTable(
          container,
          ["Fecha", "Suite", "Precio", "Limpieza"],
          model.Suites,
          row => [row.Fecha, row.Suite, row.Precio, row.Limpieza],
          columns =>
          {
            columns.RelativeColumn(1.2f);
            columns.RelativeColumn(2.2f);
            columns.RelativeColumn(1.2f);
            columns.RelativeColumn(1f);
          },
          "Sin suites registradas.",
          bodyFontSize: 10.5f));
      });

      column.Item().Element(SectionCard).Column(section =>
      {
        section.Spacing(10);
        section.Item().Element(SectionTitle).Text("Extras")
          .FontSize(12)
          .SemiBold()
          .FontColor(BrandPrimaryDark);

        section.Item().Element(container => ComposeTable(
          container,
          ["Suite", "Descripcion", "Precio", "Desc %", "Total", "Notas"],
          model.Extras,
          row => [row.Suite, row.Descripcion, row.Precio, row.Descuento, row.Total, row.Notas],
          columns =>
          {
            columns.RelativeColumn(1.2f);
            columns.RelativeColumn(1.8f);
            columns.RelativeColumn(1f);
            columns.RelativeColumn(0.9f);
            columns.RelativeColumn(1f);
            columns.RelativeColumn(1.8f);
          },
          "Sin extras registrados."));
      });

      column.Item().Element(SectionCard).Column(section =>
      {
        section.Spacing(10);
        section.Item().Element(SectionTitle).Text("Pagos")
          .FontSize(12)
          .SemiBold()
          .FontColor(BrandPrimaryDark);

        section.Item().Element(container => ComposeTable(
          container,
          ["ID", "Fecha", "Monto", "Concepto"],
          model.Pagos,
          row => [row.Id, row.Fecha, row.Monto, row.Concepto],
          columns =>
          {
            columns.RelativeColumn(0.9f);
            columns.RelativeColumn(1.2f);
            columns.RelativeColumn(1f);
            columns.RelativeColumn(2.8f);
          },
          "Sin pagos registrados."));
      });

    });
  }

  private static void ComposeFooter(IContainer container)
  {
    container.Row(row =>
    {
      row.RelativeItem().Text("Bonhomia Suites")
        .FontSize(8)
        .FontColor(BrandMuted);

      row.ConstantItem(80).AlignRight().Text(text =>
      {
        text.DefaultTextStyle(style => style.FontSize(8).FontColor(BrandMuted));
        text.Span("Pagina ");
        text.CurrentPageNumber();
        text.Span(" / ");
        text.TotalPages();
      });
    });
  }

  private static void ComposeFieldPairs(IContainer container, IReadOnlyList<FieldEntry> fields)
  {
    container.Column(column =>
    {
      column.Spacing(8);

      foreach (var pair in fields.Chunk(2))
      {
        column.Item().Row(row =>
        {
          row.Spacing(8);
          row.RelativeItem().Element(cell => ComposeFieldBlock(cell, pair[0]));

          if (pair.Length > 1)
          {
            row.RelativeItem().Element(cell => ComposeFieldBlock(cell, pair[1]));
          }
          else
          {
            row.RelativeItem();
          }
        });
      }
    });
  }

  private static void ComposeTable<TRow>(
    IContainer container,
    IReadOnlyList<string> headers,
    IReadOnlyList<TRow> rows,
    Func<TRow, IReadOnlyList<string>> mapRow,
    Action<TableColumnsDefinitionDescriptor> defineColumns,
    string emptyMessage,
    float bodyFontSize = 9)
  {
    if (rows.Count == 0)
    {
      container.PaddingVertical(6).Text(emptyMessage).FontColor(BrandMuted);
      return;
    }

    container.Table(table =>
    {
      table.ColumnsDefinition(defineColumns);

      table.Header(header =>
      {
        foreach (var item in headers)
        {
          header.Cell().Element(TableHeaderCell).Text(item).SemiBold();
        }
      });

      foreach (var row in rows)
      {
        foreach (var cellValue in mapRow(row))
        {
          table.Cell().Element(cell => TableBodyCell(cell, bodyFontSize)).Text(Safe(cellValue));
        }
      }
    });
  }

  private static void ComposeReservationSummary(IContainer container, ReservacionPdfDocumentModel model)
  {
    container.Element(CompactFieldBlock).Column(column =>
    {
      column.Spacing(6);

      column.Item().Row(row =>
      {
        row.Spacing(10);
        row.RelativeItem(1.7f).Element(cell => ComposeInlineValue(cell, "Cliente", model.Cliente, 11, true));
        row.RelativeItem().Element(cell => ComposeInlineValue(cell, "Recomendacion", model.Recomendacion));
        row.ConstantItem(74).Element(cell => ComposeInlineValue(cell, "Facturable", model.Facturable));
      });

      column.Item().LineHorizontal(1).LineColor(BrandBorder);

      column.Item().Row(row =>
      {
        row.Spacing(10);
        row.RelativeItem().Element(cell => ComposeInlineValue(cell, "Check-in", model.CheckIn));
        row.RelativeItem().Element(cell => ComposeInlineValue(cell, "Check-out", model.CheckOut));
        row.ConstantItem(72).Element(cell => ComposeInlineValue(cell, "Noches", model.NumNoches, 10, true));
      });
    });
  }

  private static void ComposeTotalsSummary(IContainer container, ReservacionPdfDocumentModel model)
  {
    container.Row(row =>
    {
      row.Spacing(10);

      row.RelativeItem(1.25f).Element(CompactFieldBlock).Column(summary =>
      {
        summary.Spacing(5);

        summary.Item().Row(line =>
        {
          line.RelativeItem().Text("Suites").FontSize(9).FontColor(BrandMuted);
          line.ConstantItem(86).AlignRight().Text(model.TotalSuites).FontSize(9.5f).SemiBold();
        });

        summary.Item().Row(line =>
        {
          line.RelativeItem().Text("Extras").FontSize(9).FontColor(BrandMuted);
          line.ConstantItem(86).AlignRight().Text(model.TotalExtras).FontSize(9.5f).SemiBold();
        });

        summary.Item().LineHorizontal(1).LineColor(BrandBorder);

        summary.Item().Row(line =>
        {
          line.RelativeItem().Text("Subtotal").FontSize(9).FontColor(BrandMuted);
          line.ConstantItem(86).AlignRight().Text(model.SubTotal).FontSize(9.5f).SemiBold();
        });

        summary.Item().Row(line =>
        {
          line.RelativeItem().Text("IVA / ISH").FontSize(9).FontColor(BrandMuted);
          line.ConstantItem(86).AlignRight().Text($"{model.Tax} / {model.Ish}").FontSize(9.5f).SemiBold();
        });
      });

      row.RelativeItem().Element(CompactFieldBlock).Column(summary =>
      {
        summary.Spacing(5);

        summary.Item().Row(line =>
        {
          line.RelativeItem().Text("Total Reservacion").FontSize(9).FontColor(BrandMuted);
          line.ConstantItem(92).AlignRight().Text(model.TotalReservacion).FontSize(10).SemiBold();
        });

        summary.Item().Row(line =>
        {
          line.RelativeItem().Text("Pagado").FontSize(9).FontColor(BrandMuted);
          line.ConstantItem(92).AlignRight().Text(model.TotalPagado).FontSize(10).SemiBold();
        });

        summary.Item().Element(container => container
          .Background(Colors.White)
          .Border(1.5f)
          .BorderColor(BrandPrimary)
          .PaddingHorizontal(8)
          .PaddingVertical(6))
          .Row(line =>
          {
            line.RelativeItem().Text("Por Pagar")
              .FontSize(10.5f)
              .Bold()
              .FontColor(BrandPrimaryDark);

            line.ConstantItem(104).AlignRight().Text(model.PorPagar)
              .FontSize(13)
              .Bold()
              .FontColor(BrandPrimary);
          });
      });
    });
  }

  private static IContainer SectionCard(IContainer container)
    => container
      .Border(1)
      .BorderColor(BrandBorder)
      .Background(BrandSurface)
      .Padding(12);

  private static IContainer SectionTitle(IContainer container)
    => container
      .PaddingBottom(6)
      .BorderBottom(1)
      .BorderColor(BrandBorder);

  private static IContainer FieldBlock(IContainer container)
    => container
      .Background(Colors.White)
      .Border(1)
      .BorderColor(BrandBorder)
      .Padding(8);

  private static IContainer CompactFieldBlock(IContainer container)
    => container
      .Background(Colors.White)
      .Border(1)
      .BorderColor(BrandBorder)
      .PaddingHorizontal(8)
      .PaddingVertical(7);

  private static IContainer TableHeaderCell(IContainer container)
    => container
      .Background(BrandPrimary)
      .PaddingHorizontal(6)
      .PaddingVertical(5)
      .DefaultTextStyle(style => style.FontColor(Colors.White).FontSize(9));

  private static IContainer TableBodyCell(IContainer container, float fontSize = 9)
    => container
      .BorderBottom(1)
      .BorderColor(BrandBorder)
      .PaddingHorizontal(6)
      .PaddingVertical(5)
      .DefaultTextStyle(style => style.FontSize(fontSize));

  private static string Safe(string? value)
    => string.IsNullOrWhiteSpace(value) ? "-" : value.Trim();

  private static void ComposeFieldBlock(IContainer container, FieldEntry field)
  {
    container.Element(FieldBlock).Column(column =>
    {
      column.Spacing(2);
      column.Item().Text(field.Label).Bold();
      column.Item().Text(Safe(field.Value))
        .FontSize(field.Emphasize ? 11 : 10)
        .SemiBold();
    });
  }

  private static void ComposeInlineValue(
    IContainer container,
    string label,
    string value,
    float valueFontSize = 10,
    bool emphasize = false)
  {
    container.Column(column =>
    {
      column.Spacing(1);
      column.Item().Text(label).FontSize(8).Bold().FontColor(BrandMuted);
      var valueText = column.Item().Text(Safe(value))
        .FontSize(valueFontSize)
        .FontColor(BrandPrimaryDark);

      if (emphasize)
      {
        valueText.Bold();
      }
      else
      {
        valueText.SemiBold();
      }
    });
  }

  private sealed record FieldEntry(string Label, string Value, bool Emphasize = false);

  private const string FallbackLogoSvg = """
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 512 512">
  <rect x="32" y="32" width="448" height="448" rx="28" fill="#0B5A68"/>
  <rect x="86" y="86" width="340" height="340" fill="#0E6A78"/>
  <rect x="86" y="86" width="340" height="340" fill="none" stroke="#083F49" stroke-width="20"/>
  <g stroke="#F2E9D5" stroke-width="24" stroke-linecap="square" fill="none">
    <path d="M140 170 L190 120 L220 150 L170 200"/>
    <path d="M255 170 L305 120 L335 150 L285 200"/>
    <path d="M370 170 L420 120"/>
    <path d="M140 282 L210 212 L240 242 L170 312"/>
    <path d="M285 282 L355 212 L385 242 L315 312"/>
    <path d="M140 394 L230 304 L260 334 L170 424"/>
    <path d="M285 394 L375 304 L405 334 L315 424"/>
  </g>
  <g fill="#F2E9D5">
    <polygon points="178,154 194,170 178,186 162,170"/>
    <polygon points="293,154 309,170 293,186 277,170"/>
    <polygon points="208,266 228,286 208,306 188,286"/>
    <polygon points="353,266 373,286 353,306 333,286"/>
    <polygon points="238,378 258,398 238,418 218,398"/>
    <polygon points="383,378 403,398 383,418 363,398"/>
  </g>
</svg>
""";
}
