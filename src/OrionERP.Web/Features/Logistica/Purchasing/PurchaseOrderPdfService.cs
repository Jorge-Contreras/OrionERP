using System.IO;
using Microsoft.AspNetCore.Hosting;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace OrionERP.Web.Features.Logistica.Purchasing;

public sealed class PurchaseOrderPdfService : IPurchaseOrderPdfService
{
  private const string BrandPrimary = "#0B5A68";
  private const string BrandPrimaryDark = "#083F49";
  private const string BrandMuted = "#6B7E83";
  private const string BrandBorder = "#D7E2E0";
  private const string BrandSurface = "#F8FBFA";
  private readonly string _logoSvg;

  public PurchaseOrderPdfService(IWebHostEnvironment environment)
  {
    QuestPDF.Settings.License = LicenseType.Community;

    var logoPath = Path.Combine(environment.WebRootPath, "Images", "BonhomiaSuitesLetterheadLogo.svg");
    _logoSvg = File.Exists(logoPath)
      ? File.ReadAllText(logoPath)
      : FallbackLogoSvg;
  }

  public byte[] Generate(PurchaseOrderPdfDocumentModel model)
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

  private void ComposeHeader(IContainer container, PurchaseOrderPdfDocumentModel model)
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

          textColumn.Item().Text("Orden de compra")
            .FontSize(15)
            .SemiBold()
            .FontColor(BrandPrimaryDark);

          textColumn.Item().PaddingTop(4).Text(model.PurchaseOrderCode)
            .FontSize(13)
            .SemiBold()
            .FontColor(BrandPrimaryDark);
        });

        row.ConstantItem(170).AlignRight().Column(meta =>
        {
          meta.Spacing(2);
          meta.Item().AlignRight().Text($"Generado: {model.GeneratedAt}")
            .FontSize(9)
            .FontColor(BrandMuted);
          meta.Item().AlignRight().Text($"Status: {model.Status}")
            .FontSize(9)
            .FontColor(BrandMuted);
          meta.Item().AlignRight().Text($"Fecha orden: {model.OrderDate}")
            .FontSize(9)
            .FontColor(BrandMuted);
        });
      });

      column.Item().LineHorizontal(1).LineColor(BrandBorder);
    });
  }

  private void ComposeContent(IContainer container, PurchaseOrderPdfDocumentModel model)
  {
    container.Column(column =>
    {
      column.Spacing(14);

      column.Item().Element(SectionCard).Column(section =>
      {
        section.Spacing(10);
        section.Item().Element(SectionTitle).Text("Proveedor y Totales")
          .FontSize(12)
          .SemiBold()
          .FontColor(BrandPrimaryDark);

        section.Item().Element(container => ComposeFieldPairs(
          container,
          [
            new FieldEntry("Proveedor", model.VendorName),
            new FieldEntry("RFC", model.VendorRfc),
            new FieldEntry("Fecha esperada", model.ExpectedDate),
            new FieldEntry("Capturado por", model.CreatedBy),
            new FieldEntry("Ordenado", model.OrderedQuantity, true),
            new FieldEntry("Recibido", model.ReceivedQuantity),
            new FieldEntry("Pendiente", model.RemainingQuantity, true)
          ]));

        section.Item().Element(FieldBlock).Column(field =>
        {
          field.Spacing(2);
          field.Item().Text("Notas").Bold();
          field.Item().Text(model.Notes);
        });
      });

      column.Item().Element(SectionCard).Column(section =>
      {
        section.Spacing(10);
        section.Item().Element(SectionTitle).Text("Materiales")
          .FontSize(12)
          .SemiBold()
          .FontColor(BrandPrimaryDark);

        section.Item().Element(container => ComposeLineTable(container, model.Lines));
      });

      column.Item().Element(SectionCard).Column(section =>
      {
        section.Spacing(10);
        section.Item().Element(SectionTitle).Text("Asignaciones por Ubicación")
          .FontSize(12)
          .SemiBold()
          .FontColor(BrandPrimaryDark);

        section.Item().Element(container => ComposeAllocationTable(container, model.Allocations));
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

  private static void ComposeLineTable(IContainer container, IReadOnlyList<PurchaseOrderPdfLineRow> rows)
  {
    if (rows.Count == 0)
    {
      container.PaddingVertical(6).Text("Sin materiales registrados.").FontColor(BrandMuted);
      return;
    }

    container.Table(table =>
    {
      table.ColumnsDefinition(columns =>
      {
        columns.ConstantColumn(56);
        columns.RelativeColumn(1.2f);
        columns.RelativeColumn(2.3f);
        columns.RelativeColumn(1.3f);
        columns.RelativeColumn(0.9f);
        columns.RelativeColumn(0.9f);
        columns.RelativeColumn(0.9f);
        columns.RelativeColumn(0.9f);
      });

      table.Header(header =>
      {
        header.Cell().Element(TableHeaderCell).Text("Foto").SemiBold();
        header.Cell().Element(TableHeaderCell).Text("Codigo").SemiBold();
        header.Cell().Element(TableHeaderCell).Text("Material").SemiBold();
        header.Cell().Element(TableHeaderCell).Text("Cod. proveedor").SemiBold();
        header.Cell().Element(TableHeaderCell).Text("Unidad").SemiBold();
        header.Cell().Element(TableHeaderCell).Text("Precio").SemiBold();
        header.Cell().Element(TableHeaderCell).Text("Ordenado").SemiBold();
        header.Cell().Element(TableHeaderCell).Text("Recibido / Pendiente").SemiBold();
      });

      foreach (var row in rows)
      {
        table.Cell().Element(TableBodyCell).Element(cell => ComposeThumbnailCell(cell, row.ThumbnailBytes, row.ThumbnailFallback));
        table.Cell().Element(TableBodyCell).Text(row.MaterialCode);
        table.Cell().Element(TableBodyCell).Column(column =>
        {
          column.Spacing(2);
          column.Item().Text(row.MaterialDescription).SemiBold();
          if (!string.IsNullOrWhiteSpace(row.PurchasePresentation))
          {
            column.Item().Text(row.PurchasePresentation).FontSize(8).FontColor(BrandMuted);
          }
          column.Item().Text($"Pendiente: {row.RemainingQuantity}").FontSize(8).FontColor(BrandMuted);
        });
        table.Cell().Element(TableBodyCell).Text(row.VendorCode);
        table.Cell().Element(TableBodyCell).Text(row.UnitName);
        table.Cell().Element(TableBodyCell).Text(row.UnitPrice);
        table.Cell().Element(TableBodyCell).Text(row.OrderedQuantity);
        table.Cell().Element(TableBodyCell).Column(column =>
        {
          column.Spacing(2);
          column.Item().Text($"Recibido: {row.ReceivedQuantity}");
          column.Item().Text($"Pendiente: {row.RemainingQuantity}").FontSize(8).FontColor(BrandMuted);
        });
      }
    });
  }

  private static void ComposeAllocationTable(IContainer container, IReadOnlyList<PurchaseOrderPdfAllocationRow> rows)
  {
    if (rows.Count == 0)
    {
      container.PaddingVertical(6).Text("Sin asignaciones registradas.").FontColor(BrandMuted);
      return;
    }

    container.Table(table =>
    {
      table.ColumnsDefinition(columns =>
      {
        columns.ConstantColumn(56);
        columns.RelativeColumn(1.1f);
        columns.RelativeColumn(2f);
        columns.RelativeColumn(2f);
        columns.RelativeColumn(0.9f);
        columns.RelativeColumn(0.9f);
        columns.RelativeColumn(0.9f);
      });

      table.Header(header =>
      {
        header.Cell().Element(TableHeaderCell).Text("Foto").SemiBold();
        header.Cell().Element(TableHeaderCell).Text("Codigo").SemiBold();
        header.Cell().Element(TableHeaderCell).Text("Material").SemiBold();
        header.Cell().Element(TableHeaderCell).Text("Ubicacion").SemiBold();
        header.Cell().Element(TableHeaderCell).Text("Planeado").SemiBold();
        header.Cell().Element(TableHeaderCell).Text("Recibido").SemiBold();
        header.Cell().Element(TableHeaderCell).Text("Pendiente").SemiBold();
      });

      foreach (var row in rows)
      {
        table.Cell().Element(TableBodyCell).Element(cell => ComposeThumbnailCell(cell, row.ThumbnailBytes, row.ThumbnailFallback));
        table.Cell().Element(TableBodyCell).Text(row.MaterialCode);
        table.Cell().Element(TableBodyCell).Text(row.MaterialDescription);
        table.Cell().Element(TableBodyCell).Text(row.LocationName);
        table.Cell().Element(TableBodyCell).Text(row.PlannedQuantity);
        table.Cell().Element(TableBodyCell).Text(row.ReceivedQuantity);
        table.Cell().Element(TableBodyCell).Text(row.RemainingQuantity);
      }
    });
  }

  private static void ComposeThumbnailCell(IContainer container, byte[]? thumbnailBytes, string fallback)
  {
    if (thumbnailBytes is { Length: > 0 })
    {
      container
        .Width(44)
        .Height(44)
        .Border(1)
        .BorderColor(BrandBorder)
        .Padding(2)
        .AlignCenter()
        .AlignMiddle()
        .Image(thumbnailBytes)
        .FitArea();
      return;
    }

    container
      .Width(44)
      .Height(44)
      .Background(BrandSurface)
      .Border(1)
      .BorderColor(BrandBorder)
      .Padding(3)
      .AlignCenter()
      .AlignMiddle()
      .Text(fallback)
      .FontSize(7)
      .FontColor(BrandMuted)
      .AlignCenter();
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

  private static IContainer TableHeaderCell(IContainer container)
    => container
      .Background(BrandPrimary)
      .PaddingHorizontal(6)
      .PaddingVertical(5)
      .DefaultTextStyle(style => style.FontColor(Colors.White).FontSize(9));

  private static IContainer TableBodyCell(IContainer container)
    => container
      .BorderBottom(1)
      .BorderColor(BrandBorder)
      .PaddingHorizontal(6)
      .PaddingVertical(5)
      .DefaultTextStyle(style => style.FontSize(9));

  private static void ComposeFieldBlock(IContainer container, FieldEntry field)
  {
    container.Element(FieldBlock).Column(column =>
    {
      column.Spacing(2);
      column.Item().Text(field.Label).Bold();
      column.Item().Text(field.Value)
        .FontSize(field.Emphasize ? 11 : 10)
        .SemiBold();
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
