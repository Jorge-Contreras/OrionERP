using Microsoft.AspNetCore.Hosting;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace OrionERP.Web.Features.Arrendadores;

public sealed class ArrendadorEstadoCuentaPdfService : IArrendadorEstadoCuentaPdfService
{
  private const string BrandPrimary = "#0B5A68";
  private const string BrandPrimaryDark = "#083F49";
  private const string BrandMuted = "#6B7E83";
  private const string BrandBorder = "#D7E2E0";
  private const string BrandSurface = "#F8FBFA";
  private readonly string _logoSvg;

  public ArrendadorEstadoCuentaPdfService(IWebHostEnvironment environment)
  {
    QuestPDF.Settings.License = LicenseType.Community;

    var logoPath = Path.Combine(environment.WebRootPath, "Images", "BonhomiaSuitesLetterheadLogo.svg");
    _logoSvg = File.Exists(logoPath)
      ? File.ReadAllText(logoPath)
      : FallbackLogoSvg;
  }

  public byte[] Generate(ArrendadorEstadoCuentaPdfDocumentModel model)
  {
    return Document.Create(container =>
      {
        container.Page(page =>
        {
          page.Size(PageSizes.Letter);
          page.Margin(26);
          page.DefaultTextStyle(text => text.FontSize(9).FontColor(BrandPrimaryDark));

          page.Header().Element(header => ComposeHeader(header, model));
          page.Content().PaddingTop(12).Element(content => ComposeContent(content, model));
          page.Footer().PaddingTop(6).Element(footer => ComposeFooter(footer, model));
        });
      })
      .GeneratePdf();
  }

  private void ComposeHeader(IContainer container, ArrendadorEstadoCuentaPdfDocumentModel model)
  {
    container.Column(column =>
    {
      column.Spacing(8);

      column.Item().Row(row =>
      {
        row.ConstantItem(62).Height(62).Svg(_logoSvg);
        row.RelativeItem().PaddingLeft(12).Column(textColumn =>
        {
          textColumn.Spacing(2);
          textColumn.Item().Text("Estado de cuenta")
            .FontSize(19)
            .SemiBold()
            .FontColor(BrandPrimaryDark);
          textColumn.Item().Text("Bonhomia Suites - Arrendadores")
            .FontSize(11)
            .FontColor(BrandPrimary);
          textColumn.Item().Text(model.Propiedad)
            .FontSize(10)
            .FontColor(BrandMuted);
        });

        row.ConstantItem(190).AlignRight().Column(meta =>
        {
          meta.Spacing(2);
          meta.Item().AlignRight().Text(model.Periodo)
            .FontSize(12)
            .SemiBold()
            .FontColor(BrandPrimaryDark);
          meta.Item().AlignRight().Text($"Generado: {model.GeneratedAt}")
            .FontSize(8)
            .FontColor(BrandMuted);
        });
      });

      column.Item().Element(SummaryBand).Text(text =>
      {
        text.Span("Arrendador: ").SemiBold();
        text.Span(model.Arrendador);
        text.Span("  |  ").FontColor(BrandMuted);
        text.Span("Propiedad: ").SemiBold();
        text.Span(model.Propiedad);
      });

      column.Item().LineHorizontal(1).LineColor(BrandBorder);
    });
  }

  private static void ComposeContent(IContainer container, ArrendadorEstadoCuentaPdfDocumentModel model)
  {
    container.Column(column =>
    {
      column.Spacing(12);

      column.Item().Element(SectionCard).Column(section =>
      {
        section.Spacing(8);
        section.Item().Element(SectionTitle).Text("Resumen del periodo")
          .FontSize(12)
          .SemiBold()
          .FontColor(BrandPrimaryDark);
        section.Item().Element(content => ComposeTotals(content, model));
      });

      column.Item().Element(SectionCard).Column(section =>
      {
        section.Spacing(8);
        section.Item().Element(SectionTitle).Text("Detalle de noches pagadas")
          .FontSize(12)
          .SemiBold()
          .FontColor(BrandPrimaryDark);
        section.Item().Element(content => ComposeDetailTable(content, model.Details));
      });

      column.Item().Element(SectionCard).Column(section =>
      {
        section.Spacing(8);
        section.Item().Element(SectionTitle).Text("Noches excluidas")
          .FontSize(12)
          .SemiBold()
          .FontColor(BrandPrimaryDark);
        section.Item().Element(content => ComposeExclusionsTable(content, model.Exclusions));
      });

      column.Item().DefaultTextStyle(style => style.FontSize(8).FontColor(BrandMuted)).Text(
        "Criterio: se incluyen noches con reservacion ligada, precio mayor a cero y pago contabilizado en Registro_Contable. El ISR se calcula sobre el 30% del arrendador.");
    });
  }

  private static void ComposeTotals(IContainer container, ArrendadorEstadoCuentaPdfDocumentModel model)
  {
    container.Row(row =>
    {
      row.Spacing(8);
      row.RelativeItem().Element(cell => ComposeTotalCard(cell, "Noches", model.NochesOcupadas));
      row.RelativeItem().Element(cell => ComposeTotalCard(cell, "Cobrado", model.Cobrado));
      row.RelativeItem().Element(cell => ComposeTotalCard(cell, "Arrendador 30%", model.Arrendador30));
      row.RelativeItem().Element(cell => ComposeTotalCard(cell, "ISR 10%", model.Isr10));
      row.RelativeItem().Element(cell => ComposeTotalCard(cell, "Pago final", model.PagoFinal, true));
    });
  }

  private static void ComposeTotalCard(IContainer container, string label, string value, bool emphasize = false)
  {
    container
      .Background(emphasize ? Colors.White : BrandSurface)
      .Border(emphasize ? 1.5f : 1)
      .BorderColor(emphasize ? BrandPrimary : BrandBorder)
      .Padding(8)
      .Column(column =>
      {
        column.Spacing(2);
        column.Item().Text(label).FontSize(7.5f).FontColor(BrandMuted);
        column.Item().Text(value)
          .FontSize(emphasize ? 12 : 10)
          .SemiBold()
          .FontColor(emphasize ? BrandPrimary : BrandPrimaryDark);
      });
  }

  private static void ComposeDetailTable(IContainer container, IReadOnlyList<ArrendadorEstadoCuentaPdfDetalleRow> rows)
  {
    if (rows.Count == 0)
    {
      container.PaddingVertical(6).Text("Sin noches pagadas en este periodo.").FontColor(BrandMuted);
      return;
    }

    container.Table(table =>
    {
      table.ColumnsDefinition(columns =>
      {
        columns.RelativeColumn(1f);
        columns.RelativeColumn(2.3f);
        columns.RelativeColumn(0.9f);
        columns.RelativeColumn(1f);
        columns.RelativeColumn(1f);
        columns.RelativeColumn(1f);
        columns.RelativeColumn(1f);
        columns.RelativeColumn(0.9f);
        columns.RelativeColumn(1.1f);
      });

      AddHeader(table, ["Noche", "Huesped", "Res.", "Check-in", "Check-out", "Cobrado", "30%", "ISR", "Pago"]);

      foreach (var row in rows)
      {
        table.Cell().Element(TableBodyCell).Text(row.Noche);
        table.Cell().Element(TableBodyCell).Text(Safe(row.Huesped));
        table.Cell().Element(TableBodyCell).AlignRight().Text(row.ReservationId);
        table.Cell().Element(TableBodyCell).Text(row.CheckIn);
        table.Cell().Element(TableBodyCell).Text(row.CheckOut);
        table.Cell().Element(TableBodyCell).AlignRight().Text(row.Cobrado);
        table.Cell().Element(TableBodyCell).AlignRight().Text(row.Arrendador30);
        table.Cell().Element(TableBodyCell).AlignRight().Text(row.Isr10);
        table.Cell().Element(TableBodyCell).AlignRight().Text(row.PagoFinal);
      }
    });
  }

  private static void ComposeExclusionsTable(IContainer container, IReadOnlyList<ArrendadorEstadoCuentaPdfExclusionRow> rows)
  {
    if (rows.Count == 0)
    {
      container.PaddingVertical(6).Text("Sin noches excluidas en este periodo.").FontColor(BrandMuted);
      return;
    }

    container.Table(table =>
    {
      table.ColumnsDefinition(columns =>
      {
        columns.RelativeColumn(1f);
        columns.RelativeColumn(2.4f);
        columns.RelativeColumn(0.9f);
        columns.RelativeColumn(1f);
        columns.RelativeColumn(1.8f);
      });

      AddHeader(table, ["Noche", "Huesped", "Res.", "Cobrado", "Motivo"]);

      foreach (var row in rows)
      {
        table.Cell().Element(TableBodyCell).Text(row.Noche);
        table.Cell().Element(TableBodyCell).Text(Safe(row.Huesped));
        table.Cell().Element(TableBodyCell).AlignRight().Text(row.ReservationId);
        table.Cell().Element(TableBodyCell).AlignRight().Text(row.Cobrado);
        table.Cell().Element(TableBodyCell).Text(row.Motivo);
      }
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

  private static void ComposeFooter(IContainer container, ArrendadorEstadoCuentaPdfDocumentModel model)
  {
    container.Column(column =>
    {
      column.Item().LineHorizontal(1).LineColor(BrandBorder);
      column.Item().PaddingTop(4).Row(row =>
      {
        row.RelativeItem().Text($"Estado de cuenta | {model.Propiedad} | {model.Periodo}")
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

  private static IContainer SectionCard(IContainer container)
    => container
      .Border(1)
      .BorderColor(BrandBorder)
      .Background(BrandSurface)
      .Padding(10);

  private static IContainer SectionTitle(IContainer container)
    => container
      .PaddingBottom(5)
      .BorderBottom(1)
      .BorderColor(BrandBorder);

  private static IContainer SummaryBand(IContainer container)
    => container
      .Background(BrandSurface)
      .Border(1)
      .BorderColor(BrandBorder)
      .PaddingHorizontal(9)
      .PaddingVertical(6);

  private static IContainer TableHeaderCell(IContainer container)
    => container
      .Background(BrandPrimary)
      .PaddingHorizontal(4)
      .PaddingVertical(4)
      .DefaultTextStyle(style => style.FontColor(Colors.White).FontSize(7.5f));

  private static IContainer TableBodyCell(IContainer container)
    => container
      .Background(Colors.White)
      .BorderBottom(1)
      .BorderColor(BrandBorder)
      .PaddingHorizontal(4)
      .PaddingVertical(4)
      .DefaultTextStyle(style => style.FontSize(7.5f));

  private static string Safe(string? value)
    => string.IsNullOrWhiteSpace(value) ? "-" : value.Trim();

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
</svg>
""";
}
