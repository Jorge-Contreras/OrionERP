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
          page.Margin(20);
          page.DefaultTextStyle(text => text.FontSize(9).FontColor(BrandPrimaryDark));

          page.Content().Element(content => ComposeContent(content, model));
          page.Footer().PaddingTop(6).Element(footer => ComposeFooter(footer, model));
        });
      })
      .GeneratePdf();
  }

  private void ComposeDocumentIntro(IContainer container, PurchaseOrderPdfDocumentModel model)
  {
    container.Column(column =>
    {
      column.Spacing(8);

      column.Item().Row(row =>
      {
        row.ConstantItem(58).Height(58).Svg(_logoSvg);
        row.RelativeItem().PaddingLeft(10).Column(textColumn =>
        {
          textColumn.Spacing(1);
          textColumn.Item().Text("Orden de compra")
            .FontSize(17)
            .SemiBold()
            .FontColor(BrandPrimaryDark);

          textColumn.Item().Text(model.PurchaseOrderCode)
            .FontSize(12)
            .SemiBold()
            .FontColor(BrandPrimary);

          textColumn.Item().Text("Bonhomia Suites")
            .FontSize(9)
            .FontColor(BrandMuted);
        });

        row.ConstantItem(190).AlignRight().Column(meta =>
        {
          meta.Spacing(1);
          meta.Item().AlignRight().Text(model.VendorName)
            .FontSize(11)
            .SemiBold()
            .FontColor(BrandPrimaryDark);
          meta.Item().AlignRight().Text($"Estado: {model.Status}")
            .FontSize(8)
            .FontColor(BrandMuted);
          meta.Item().AlignRight().Text($"Generado: {model.GeneratedAt}")
            .FontSize(8)
            .FontColor(BrandMuted);
        });
      });

      column.Item().Element(SummaryBand).Column(summary =>
      {
        summary.Spacing(3);
        summary.Item().Text(text =>
        {
          text.Span("Proveedor: ").SemiBold();
          text.Span(model.VendorName);
          text.Span("  |  ").FontColor(BrandMuted);
          text.Span("RFC: ").SemiBold();
          text.Span(model.VendorRfc);
          text.Span("  |  ").FontColor(BrandMuted);
          text.Span("Orden: ").SemiBold();
          text.Span(model.OrderDate);
          text.Span("  |  ").FontColor(BrandMuted);
          text.Span("Entrega: ").SemiBold();
          text.Span(model.ExpectedDate);
          text.Span("  |  ").FontColor(BrandMuted);
          text.Span("Capturo: ").SemiBold();
          text.Span(model.CreatedBy);
        });

        summary.Item().DefaultTextStyle(style => style.FontColor(BrandMuted)).Text(text =>
        {
          text.Span("Materiales: ").SemiBold();
          text.Span(model.MaterialCount);
          text.Span("  |  ").FontColor(BrandMuted);
          text.Span("Ubicaciones: ").SemiBold();
          text.Span(model.AllocationCount);
          text.Span("  |  ").FontColor(BrandMuted);
          text.Span("Pendientes: ").SemiBold();
          text.Span(model.PendingAllocationCount);
        });
      });

      if (HasRealValue(model.Notes))
      {
        column.Item().DefaultTextStyle(style => style.FontSize(8)).Text(text =>
        {
          text.Span("Notas: ").SemiBold();
          text.Span(model.Notes);
        });
      }

      column.Item().LineHorizontal(1).LineColor(BrandBorder);
    });
  }

  private void ComposeContent(IContainer container, PurchaseOrderPdfDocumentModel model)
  {
    container.Column(column =>
    {
      column.Spacing(10);

      column.Item().Element(container => ComposeDocumentIntro(container, model));

      column.Item().Element(SectionCard).Column(section =>
      {
        section.Spacing(8);
        section.Item().Element(SectionTitle).Text("Materiales")
          .FontSize(11)
          .SemiBold()
          .FontColor(BrandPrimaryDark);

        section.Item().Element(container => ComposeLineTable(container, model.Lines));
      });

      if (model.Allocations.Count > 0)
      {
        column.Item().PageBreak();
      }

      column.Item().Element(SectionCard).Column(section =>
      {
        section.Spacing(8);
        section.Item().Element(SectionTitle).Text("Asignaciones por Ubicación")
          .FontSize(11)
          .SemiBold()
          .FontColor(BrandPrimaryDark);

        section.Item().Element(container => ComposeAllocationTable(container, model.Allocations));
      });
    });
  }

  private static void ComposeFooter(IContainer container, PurchaseOrderPdfDocumentModel model)
  {
    container.Column(column =>
    {
      column.Item().LineHorizontal(1).LineColor(BrandBorder);
      column.Item().PaddingTop(4).Row(row =>
      {
        row.RelativeItem().Text($"{model.PurchaseOrderCode}  |  {model.VendorName}")
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
        columns.ConstantColumn(40);
        columns.RelativeColumn(3.4f);
        columns.RelativeColumn(1f);
        columns.RelativeColumn(1f);
        columns.RelativeColumn(0.9f);
        columns.RelativeColumn(0.9f);
        columns.RelativeColumn(0.9f);
      });

      table.Header(header =>
      {
        header.Cell().Element(TableHeaderCell).Text("Foto").SemiBold();
        header.Cell().Element(TableHeaderCell).Text("Material").SemiBold();
        header.Cell().Element(TableHeaderCell).Text("Unidad").SemiBold();
        header.Cell().Element(TableHeaderCell).Text("Precio").SemiBold();
        header.Cell().Element(TableHeaderCell).AlignRight().Text("Ordenado").SemiBold();
        header.Cell().Element(TableHeaderCell).AlignRight().Text("Recibido").SemiBold();
        header.Cell().Element(TableHeaderCell).AlignRight().Text("Pendiente").SemiBold();
      });

      foreach (var row in rows)
      {
        var materialMeta = BuildMaterialMeta(row.MaterialCode, row.VendorCode);

        table.Cell().Element(TableBodyCell).Element(cell => ComposeThumbnailCell(cell, row.ThumbnailBytes, row.ThumbnailFallback));
        table.Cell().Element(TableBodyCell).Column(column =>
        {
          column.Spacing(1);
          column.Item().Text(row.MaterialDescription).SemiBold();
          if (HasRealValue(materialMeta))
          {
            column.Item().Text(materialMeta).FontSize(7).FontColor(BrandMuted);
          }
          if (HasRealValue(row.PurchasePresentation))
          {
            column.Item().Text(row.PurchasePresentation).FontSize(7).FontColor(BrandMuted);
          }
        });
        table.Cell().Element(TableBodyCell).Text(row.UnitName);
        table.Cell().Element(TableBodyCell).AlignRight().Text(row.UnitPrice);
        table.Cell().Element(TableBodyCell).AlignRight().Text(row.OrderedQuantity);
        table.Cell().Element(TableBodyCell).AlignRight().Text(row.ReceivedQuantity);
        table.Cell().Element(TableBodyCell).AlignRight().Text(row.RemainingQuantity);
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

    var groupedRows = rows
      .OrderBy(row => row.LocationName, StringComparer.OrdinalIgnoreCase)
      .ThenBy(row => row.MaterialCode, StringComparer.OrdinalIgnoreCase)
      .ThenBy(row => row.MaterialDescription, StringComparer.OrdinalIgnoreCase)
      .GroupBy(row => row.LocationName, StringComparer.OrdinalIgnoreCase)
      .ToList();

    container.Table(table =>
    {
      table.ColumnsDefinition(columns =>
      {
        columns.ConstantColumn(40);
        columns.RelativeColumn(3.2f);
        columns.RelativeColumn(1f);
        columns.RelativeColumn(1f);
        columns.RelativeColumn(1f);
      });

      table.Header(header =>
      {
        header.Cell().Element(TableHeaderCell).Text("Foto").SemiBold();
        header.Cell().Element(TableHeaderCell).Text("Material").SemiBold();
        header.Cell().Element(TableHeaderCell).AlignRight().Text("Planeado").SemiBold();
        header.Cell().Element(TableHeaderCell).AlignRight().Text("Recibido").SemiBold();
        header.Cell().Element(TableHeaderCell).AlignRight().Text("Pendiente").SemiBold();
      });

      foreach (var group in groupedRows)
      {
        table.Cell()
          .ColumnSpan(5)
          .Element(AllocationLocationCell)
          .Text(group.Key)
          .SemiBold()
          .FontColor(BrandPrimaryDark);

        foreach (var row in group)
        {
          table.Cell().Element(TableBodyCell).Element(cell => ComposeThumbnailCell(cell, row.ThumbnailBytes, row.ThumbnailFallback));
          table.Cell().Element(TableBodyCell).Column(column =>
          {
            column.Spacing(1);
            column.Item().Text(row.MaterialDescription).SemiBold();
            if (HasRealValue(row.MaterialCode))
            {
              column.Item().Text(row.MaterialCode).FontSize(7).FontColor(BrandMuted);
            }
          });
          table.Cell().Element(TableBodyCell).AlignRight().Text(row.PlannedQuantity);
          table.Cell().Element(TableBodyCell).AlignRight().Text(row.ReceivedQuantity);
          table.Cell().Element(TableBodyCell).AlignRight().Text(row.RemainingQuantity);
        }
      }
    });
  }

  private static void ComposeThumbnailCell(IContainer container, byte[]? thumbnailBytes, string fallback)
  {
    if (thumbnailBytes is { Length: > 0 })
    {
      container
        .Width(32)
        .Height(32)
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
      .Width(32)
      .Height(32)
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
      .Padding(8);

  private static IContainer SectionTitle(IContainer container)
    => container
      .PaddingBottom(4)
      .BorderBottom(1)
      .BorderColor(BrandBorder);

  private static IContainer SummaryBand(IContainer container)
    => container
      .Border(1)
      .BorderColor(BrandBorder)
      .PaddingHorizontal(8)
      .PaddingVertical(6)
      .DefaultTextStyle(style => style.FontSize(8).FontColor(BrandPrimaryDark));

  private static IContainer TableHeaderCell(IContainer container)
    => container
      .PaddingHorizontal(4)
      .PaddingTop(2)
      .PaddingBottom(3)
      .BorderBottom(1)
      .BorderColor(BrandPrimary)
      .DefaultTextStyle(style => style.FontColor(BrandPrimaryDark).FontSize(8));

  private static IContainer TableBodyCell(IContainer container)
    => container
      .BorderBottom(1)
      .BorderColor(BrandBorder)
      .PaddingHorizontal(4)
      .PaddingVertical(3)
      .DefaultTextStyle(style => style.FontSize(8));

  private static IContainer AllocationLocationCell(IContainer container)
    => container
      .Background("#EEF6F4")
      .BorderTop(1)
      .BorderBottom(1)
      .BorderLeft(3)
      .BorderColor(BrandPrimary)
      .PaddingHorizontal(6)
      .PaddingTop(4)
      .PaddingBottom(3)
      .DefaultTextStyle(style => style.FontSize(9).SemiBold().FontColor(BrandPrimaryDark));

  private static string BuildMaterialMeta(string materialCode, string vendorCode)
  {
    var parts = new List<string>(2);

    if (HasRealValue(materialCode))
    {
      parts.Add(materialCode);
    }

    if (HasRealValue(vendorCode))
    {
      parts.Add($"Prov: {vendorCode}");
    }

    return string.Join("  |  ", parts);
  }

  private static bool HasRealValue(string? value)
    => !string.IsNullOrWhiteSpace(value) && !string.Equals(value.Trim(), "-", StringComparison.Ordinal);

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
