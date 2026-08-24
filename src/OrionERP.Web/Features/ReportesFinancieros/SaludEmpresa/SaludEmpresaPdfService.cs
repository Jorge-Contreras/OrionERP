using System.Globalization;
using Microsoft.AspNetCore.Hosting;
using OrionERP.Application.Features.ReportesFinancieros.Models;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace OrionERP.Web.Features.ReportesFinancieros.SaludEmpresa;

public sealed class SaludEmpresaPdfService : ISaludEmpresaPdfService
{
  private const string BrandPrimary = "#0B5A68";
  private const string BrandPrimaryDark = "#083F49";
  private const string BrandMuted = "#667085";
  private const string BrandBorder = "#D8E2E7";
  private const string BrandSurface = "#F7FAFC";
  private const string BrandSuccess = "#14966F";
  private const string BrandDanger = "#D1495B";
  private const string BrandWarning = "#D98C24";
  private const string BrandInfo = "#2878BD";
  private static readonly CultureInfo MexicanCulture = CultureInfo.GetCultureInfo("es-MX");
  private readonly string _logoSvg;

  public SaludEmpresaPdfService(IWebHostEnvironment environment)
  {
    QuestPDF.Settings.License = LicenseType.Community;

    var logoPath = Path.Combine(environment.WebRootPath, "Images", "BonhomiaSuitesLetterheadLogo.svg");
    _logoSvg = File.Exists(logoPath)
      ? File.ReadAllText(logoPath)
      : FallbackLogoSvg;
  }

  public byte[] Generate(SaludEmpresaPdfDocumentModel model)
  {
    return Document.Create(container =>
      {
        container.Page(page =>
        {
          page.Size(PageSizes.Letter);
          page.Margin(22);
          page.DefaultTextStyle(text => text.FontSize(8.5f).FontColor(BrandPrimaryDark));

          page.Header().Element(header => ComposeHeader(header, model));
          page.Content().PaddingTop(10).Element(content => ComposeContent(content, model));
          page.Footer().PaddingTop(6).Element(footer => ComposeFooter(footer, model));
        });
      })
      .GeneratePdf();
  }

  public byte[] GenerateInvestor(SaludEmpresaPdfDocumentModel model)
  {
    return Document.Create(container =>
      {
        container.Page(page =>
        {
          page.Size(PageSizes.Letter);
          page.Margin(22);
          page.DefaultTextStyle(text => text.FontSize(8.5f).FontColor(BrandPrimaryDark));
          page.Header().Element(header => ComposeHeader(header, model));
          page.Content().PaddingTop(12).Element(content => ComposeInvestorContent(content, model));
          page.Footer().PaddingTop(6).Element(footer => ComposeInvestorFooter(footer, model));
        });
      })
      .GeneratePdf();
  }

  private static void ComposeInvestorContent(IContainer container, SaludEmpresaPdfDocumentModel model)
  {
    var report = model.Report;
    var selected = report.SelectedPeriod;
    container.Column(column =>
    {
      column.Spacing(12);
      column.Item().Text("Informe para inversionistas")
        .FontSize(17).SemiBold().FontColor(BrandPrimaryDark);
      column.Item().Text("Cifras internas, provisionales y no auditadas. El outlook representa exclusivamente negocio on-books; no incorpora demanda hipotética.")
        .FontColor(BrandMuted);

      if (selected is null)
      {
        column.Item().Text("Sin información disponible para el periodo.");
        return;
      }

      column.Item().Element(card => card.Element(SectionCard).Column(section =>
      {
        section.Spacing(7);
        section.Item().Text("Resumen ejecutivo").FontSize(12).SemiBold();
        section.Item().Table(table =>
        {
          table.ColumnsDefinition(columns => { columns.RelativeColumn(); columns.RelativeColumn(); columns.RelativeColumn(); columns.RelativeColumn(); });
          AddHeader(table, ["Ingreso operativo", "Resultado operativo", "Resultado neto", "Caja final"]);
          table.Cell().Element(TableBodyCell).Text(Money(selected.TotalOperatingRevenue));
          table.Cell().Element(TableBodyCell).Text(Money(report.SelectedFinancialBreakdown?.OperatingResult));
          table.Cell().Element(TableBodyCell).Text(Money(selected.NetResult));
          table.Cell().Element(TableBodyCell).Text(Money(report.SelectedCashFlow?.ClosingCashBalance));
        });
        if (report.Metadata.LodgingEnabled)
        {
          section.Item().Table(table =>
          {
            table.ColumnsDefinition(columns => { columns.RelativeColumn(); columns.RelativeColumn(); columns.RelativeColumn(); columns.RelativeColumn(); });
            AddHeader(table, ["Ocupación", "ADR", "RevPAR", "TRevPAR"]);
            table.Cell().Element(TableBodyCell).Text(Percent(selected.OccupancyPct));
            table.Cell().Element(TableBodyCell).Text(Money(selected.ADR));
            table.Cell().Element(TableBodyCell).Text(Money(selected.RevPAR));
            table.Cell().Element(TableBodyCell).Text(Money(selected.TRevPAR));
          });
        }
      }));

      column.Item().Element(card => card.Element(SectionCard).Column(section =>
      {
        section.Spacing(7);
        section.Item().Text("Tendencia de 12 meses").FontSize(12).SemiBold();
        section.Item().Table(table =>
        {
          table.ColumnsDefinition(columns => { columns.RelativeColumn(); columns.RelativeColumn(); columns.RelativeColumn(); columns.RelativeColumn(); columns.RelativeColumn(); });
          AddHeader(table, ["Mes", "Ingreso", "Meta", "Año anterior", "Resultado neto"]);
          foreach (var row in report.Trends.OrderBy(row => row.Month))
          {
            table.Cell().Element(TableBodyCell).Text(row.Month.ToString("MMM yyyy", MexicanCulture));
            table.Cell().Element(TableBodyCell).AlignRight().Text(Money(row.TotalOperatingRevenue));
            table.Cell().Element(TableBodyCell).AlignRight().Text(Money(row.RevenueTarget));
            table.Cell().Element(TableBodyCell).AlignRight().Text(Money(row.PreviousYearRevenue));
            table.Cell().Element(TableBodyCell).AlignRight().Text(Money(row.NetResult));
          }
        });
      }));

      column.Item().Element(card => card.Element(SectionCard).Column(section =>
      {
        section.Spacing(7);
        section.Item().Text("Outlook on-books").FontSize(12).SemiBold();
        section.Item().Table(table =>
        {
          table.ColumnsDefinition(columns => { columns.RelativeColumn(); columns.RelativeColumn(); columns.RelativeColumn(); columns.RelativeColumn(); });
          AddHeader(table, ["Mes", "Ocupación", "Ingreso habitación", "Complementario"]);
          foreach (var row in report.MonthlyOutlook.OrderBy(row => row.Month).Take(12))
          {
            table.Cell().Element(TableBodyCell).Text(row.Month.ToString("MMM yyyy", MexicanCulture));
            table.Cell().Element(TableBodyCell).AlignRight().Text(Percent(row.OccupancyPct));
            table.Cell().Element(TableBodyCell).AlignRight().Text(Money(row.RoomRevenue));
            table.Cell().Element(TableBodyCell).AlignRight().Text(Money(row.ComplementaryRevenue));
          }
        });
      }));

      column.Item().PageBreak();
      column.Item().Element(card => card.Element(SectionCard).Column(section =>
      {
        section.Spacing(7);
        section.Item().Text("Mezcla de ingresos y liquidez").FontSize(12).SemiBold();
        section.Item().Row(row =>
        {
          row.RelativeItem().Table(table =>
          {
            table.ColumnsDefinition(columns => { columns.RelativeColumn(2); columns.RelativeColumn(); columns.RelativeColumn(); });
            AddHeader(table, ["Ingreso", "Monto", "Mezcla"]);
            foreach (var item in report.RevenueMix)
            {
              table.Cell().Element(TableBodyCell).Text(item.RevenueType);
              table.Cell().Element(TableBodyCell).AlignRight().Text(Money(item.Amount));
              table.Cell().Element(TableBodyCell).AlignRight().Text(Percent(item.MixPct));
            }
          });
          row.ConstantItem(12);
          row.RelativeItem().Table(table =>
          {
            table.ColumnsDefinition(columns => { columns.RelativeColumn(2); columns.RelativeColumn(); });
            AddHeader(table, ["Posición", "Saldo"]);
            foreach (var item in report.Liquidity)
            {
              table.Cell().Element(TableBodyCell).Text(item.MetricLabel);
              table.Cell().Element(TableBodyCell).AlignRight().Text(item.IsAvailable ? Money(item.Amount) : "No disponible");
            }
          });
        });
      }));

      var groupedRisks = report.SelectedPeriodIssues
        .GroupBy(issue => issue.Severity)
        .Select(group => new { Severity = group.Key, Count = group.Sum(issue => issue.ItemCount ?? 1), Amount = group.Sum(issue => issue.NetEffect ?? issue.MetricAmount ?? 0m) })
        .OrderBy(group => group.Severity == "Alta" ? 1 : group.Severity == "Media" ? 2 : 3)
        .ToList();
      column.Item().Element(card => card.Element(SectionCard).Column(section =>
      {
        section.Spacing(6);
        section.Item().Text("Riesgos y metodología").FontSize(12).SemiBold();
        foreach (var risk in groupedRisks)
          section.Item().Text($"{risk.Severity}: {risk.Count:N0} observaciones, efecto identificado {Money(risk.Amount)}.");
        section.Item().Text("Ocupación = noches vendidas / noches disponibles; ADR = ingreso neto de habitación / noches vendidas; RevPAR = ingreso neto de habitación / noches disponibles; TRevPAR agrega extras y experiencias netos. Estructura inspirada en USALI y adaptada al catálogo mexicano.")
          .FontColor(BrandMuted);
      }));
    });
  }

  private static void ComposeInvestorFooter(IContainer container, SaludEmpresaPdfDocumentModel model)
  {
    container.BorderTop(1).BorderColor(BrandBorder).PaddingTop(5).Row(row =>
    {
      row.RelativeItem().Text($"Informe para inversionistas | Corte {model.Report.Metadata.CutoffDate:dd/MM/yyyy} | Interno no auditado")
        .FontSize(7).FontColor(BrandMuted);
      row.ConstantItem(75).AlignRight().Text(text => { text.CurrentPageNumber(); text.Span(" / "); text.TotalPages(); });
    });
  }

  private void ComposeHeader(IContainer container, SaludEmpresaPdfDocumentModel model)
  {
    var selected = model.Report.SelectedPeriod;
    var period = selected is null
      ? $"{model.PeriodStart:yyyy-MM-dd HH:mm:ss} - {model.PeriodEnd:yyyy-MM-dd HH:mm:ss}"
      : $"{selected.PeriodLabel} | {selected.PeriodScope}";

    container.Column(column =>
    {
      column.Spacing(8);
      column.Item().Row(row =>
      {
        row.ConstantItem(54).Height(54).Svg(_logoSvg);
        row.RelativeItem().PaddingLeft(10).Column(text =>
        {
          text.Item().Text("Salud financiera")
            .FontSize(18)
            .SemiBold()
            .FontColor(BrandPrimaryDark);
          text.Item().Text("Dashboard ejecutivo de empresa")
            .FontSize(10)
            .FontColor(BrandPrimary);
          text.Item().Text($"RFC: {Safe(model.Rfc)}")
            .FontSize(8)
            .FontColor(BrandMuted);
        });

        row.ConstantItem(190).AlignRight().Column(meta =>
        {
          meta.Item().AlignRight().Text(period)
            .FontSize(11)
            .SemiBold()
            .FontColor(BrandPrimaryDark);
          meta.Item().AlignRight().Text($"Generado: {model.GeneratedAt:yyyy-MM-dd HH:mm}")
            .FontSize(8)
            .FontColor(BrandMuted);
        });
      });

      column.Item().LineHorizontal(1).LineColor(BrandBorder);
    });
  }

  private static void ComposeContent(IContainer container, SaludEmpresaPdfDocumentModel model)
  {
    var report = model.Report;
    var selected = report.SelectedPeriod;

    container.Column(column =>
    {
      column.Spacing(10);

      if (selected is null)
      {
        column.Item().Text("Sin datos para el periodo seleccionado.").FontColor(BrandMuted);
        return;
      }

      column.Item().Element(content => ComposeKpis(content, report));
      column.Item().Element(content => ComposePeriodOverview(content, report));

      column.Item().Row(row =>
      {
        row.Spacing(10);
        row.RelativeItem().Element(content => ComposeFinancialBreakdown(content, report.SelectedFinancialBreakdown));
        row.RelativeItem().Element(content => ComposeCashFlow(content, report.CashFlow));
      });

      column.Item().Element(content => ComposeSuiteTable(content, report.SelectedPeriodSuites));
      column.Item().Element(content => ComposeIssues(content, report.SelectedPeriodIssues));
      if (model.Reconciliation is { Count: > 0 })
        column.Item().Element(content => ComposeReconciliationDetail(content, model.Reconciliation));
    });
  }

  private static void ComposeKpis(IContainer container, SaludEmpresaReport report)
  {
    var selected = report.SelectedPeriod!;
    var previous = report.PreviousPeriod;
    var previousYear = report.SamePeriodPreviousYear;
    var metrics = new[]
    {
      BuildMetric("Ingresos", selected.RoomRevenue, previous?.RoomRevenue, SaludEmpresaMetricFormat.Money),
      BuildMetric("Resultado neto", selected.NetResult, previous?.NetResult, SaludEmpresaMetricFormat.Money),
      BuildMetric("Margen neto", selected.NetMarginPct, previous?.NetMarginPct, SaludEmpresaMetricFormat.Percent),
      BuildMetric("Flujo neto", selected.NetCashflow, previous?.NetCashflow, SaludEmpresaMetricFormat.Money),
      BuildMetric("Ocupación", selected.OccupancyPct, previous?.OccupancyPct, SaludEmpresaMetricFormat.Percent),
      BuildMetric("RevPAR", selected.RevPAR, previous?.RevPAR, SaludEmpresaMetricFormat.Money),
      BuildMetric("Cobranza", selected.CollectionPct, previous?.CollectionPct, SaludEmpresaMetricFormat.Percent),
      BuildMetric("YoY ingresos", selected.RoomRevenue, previousYear?.RoomRevenue, SaludEmpresaMetricFormat.Money)
    };

    container.Column(column =>
    {
      column.Spacing(6);
      for (var i = 0; i < metrics.Length; i += 4)
      {
        var rowMetrics = metrics.Skip(i).Take(4).ToList();
        column.Item().Row(row =>
        {
          row.Spacing(6);
          foreach (var metric in rowMetrics)
          {
            row.RelativeItem().Element(cell => ComposeKpiCell(cell, metric));
          }

          for (var fill = rowMetrics.Count; fill < 4; fill++)
          {
            row.RelativeItem();
          }
        });
      }
    });
  }

  private static void ComposeKpiCell(IContainer container, PdfMetric metric)
  {
    container
      .Border(1)
      .BorderColor(BrandBorder)
      .Background(Colors.White)
      .Padding(8)
      .Column(column =>
      {
        column.Spacing(3);
        column.Item().Text(metric.Label).FontSize(7).FontColor(BrandMuted);
        column.Item().Text(metric.Value).FontSize(12).SemiBold().FontColor(BrandPrimaryDark);
        column.Item().Text(metric.Change.Text).FontSize(7).FontColor(ChangeColor(metric.Change));
      });
  }

  private static void ComposePeriodOverview(IContainer container, SaludEmpresaReport report)
  {
    var maxRevenue = MaxAbs(report.ExecutiveIndicators.Select(row => row.RoomRevenue));
    var maxResult = MaxAbs(report.ExecutiveIndicators.Select(row => row.NetResult));

    container.Element(SectionCard).Column(section =>
    {
      section.Spacing(7);
      section.Item().Element(SectionTitle).Text("Mercado del periodo")
        .FontSize(11)
        .SemiBold()
        .FontColor(BrandPrimaryDark);

      section.Item().Table(table =>
      {
        table.ColumnsDefinition(columns =>
        {
          columns.RelativeColumn(1.2f);
          columns.RelativeColumn(1.3f);
          columns.RelativeColumn(1.3f);
          columns.RelativeColumn(.7f);
          columns.RelativeColumn(.8f);
        });

        AddHeader(table, ["Periodo", "Ingresos", "Resultado", "Ocupación", "RevPAR"]);

        foreach (var row in report.ExecutiveIndicators.OrderBy(row => row.SortOrder))
        {
          table.Cell().Element(TableBodyCell).Column(cell =>
          {
            cell.Item().Text(row.PeriodLabel).SemiBold();
            cell.Item().Text(row.PeriodScope).FontSize(7).FontColor(BrandMuted);
          });
          table.Cell().Element(TableBodyCell).Element(cell => ComposeAmountBar(cell, row.RoomRevenue, maxRevenue, BrandSuccess));
          table.Cell().Element(TableBodyCell).Element(cell => ComposeAmountBar(cell, row.NetResult, maxResult, row.NetResult >= 0 ? BrandSuccess : BrandDanger));
          table.Cell().Element(TableBodyCell).AlignRight().Text(Percent(row.OccupancyPct));
          table.Cell().Element(TableBodyCell).AlignRight().Text(Money(row.RevPAR));
        }
      });
    });
  }

  private static void ComposeFinancialBreakdown(IContainer container, SaludEmpresaFinancialBreakdownRow? row)
  {
    container.Element(SectionCard).Column(section =>
    {
      section.Spacing(7);
      section.Item().Element(SectionTitle).Text("Desglose financiero")
        .FontSize(11)
        .SemiBold()
        .FontColor(BrandPrimaryDark);

      if (row is null)
      {
        section.Item().Text("Sin datos financieros.").FontColor(BrandMuted);
        return;
      }

      var items = new[]
      {
        new PdfBreakdown("Ingreso neto", row.NetAccountingIncome, BrandSuccess),
        new PdfBreakdown("Costo de ventas", row.CostOfSales501504, BrandWarning),
        new PdfBreakdown("Gastos op.", row.OperatingExpenses602605, BrandDanger),
        new PdfBreakdown("Gastos fin.", row.FinancialExpenses701, BrandDanger),
        new PdfBreakdown("Otros netos", row.OtherNet, row.OtherNet >= 0 ? BrandSuccess : BrandDanger),
        new PdfBreakdown("Impuestos", row.Taxes611, BrandWarning),
        new PdfBreakdown("Resultado neto", row.NetResult, row.NetResult >= 0 ? BrandSuccess : BrandDanger)
      };
      var max = MaxAbs(items.Select(item => item.Amount));

      foreach (var item in items)
      {
        section.Item().Row(line =>
        {
          line.RelativeItem(1.1f).Text(item.Label).FontSize(8);
          line.RelativeItem(1.1f).Element(cell => ComposeMiniBar(cell, item.Amount, max, item.Color));
          line.ConstantItem(70).AlignRight().Text(Money(item.Amount)).FontSize(8);
        });
      }
    });
  }

  private static void ComposeCashFlow(IContainer container, IReadOnlyList<SaludEmpresaCashFlowRow> rows)
  {
    container.Element(SectionCard).Column(section =>
    {
      section.Spacing(7);
      section.Item().Element(SectionTitle).Text("Flujo de efectivo")
        .FontSize(11)
        .SemiBold()
        .FontColor(BrandPrimaryDark);

      if (rows.Count == 0)
      {
        section.Item().Text("Sin flujo de efectivo.").FontColor(BrandMuted);
        return;
      }

      var max = MaxAbs(rows.SelectMany(row => new[] { row.CashIn, row.CashOut, row.NetCashflow }));
      foreach (var row in rows.OrderBy(row => row.SortOrder))
      {
        section.Item().Column(item =>
        {
          item.Spacing(2);
          item.Item().Row(line =>
          {
            line.RelativeItem().Text(row.PeriodLabel).SemiBold();
            line.ConstantItem(78).AlignRight().Text(Money(row.NetCashflow)).FontColor(row.NetCashflow >= 0 ? BrandSuccess : BrandDanger);
          });
          item.Item().Row(line =>
          {
            line.RelativeItem().Element(cell => ComposeMiniBar(cell, row.CashIn, max, BrandSuccess));
            line.ConstantItem(5);
            line.RelativeItem().Element(cell => ComposeMiniBar(cell, row.CashOut, max, BrandDanger));
          });
        });
      }
    });
  }

  private static void ComposeSuiteTable(IContainer container, IReadOnlyList<SaludEmpresaSuitePerformanceRow> rows)
  {
    container.Element(SectionCard).Column(section =>
    {
      section.Spacing(7);
      section.Item().Element(SectionTitle).Text("Ranking de suites")
        .FontSize(11)
        .SemiBold()
        .FontColor(BrandPrimaryDark);

      if (rows.Count == 0)
      {
        section.Item().Text("Sin suites con datos del periodo.").FontColor(BrandMuted);
        return;
      }

      var max = MaxAbs(rows.Select(row => row.RoomRevenue));
      section.Item().Table(table =>
      {
        table.ColumnsDefinition(columns =>
        {
          columns.RelativeColumn(1.1f);
          columns.RelativeColumn(1.6f);
          columns.RelativeColumn(.7f);
          columns.RelativeColumn(.8f);
          columns.RelativeColumn(.8f);
        });

        AddHeader(table, ["Suite", "Ingresos", "Ocup.", "ADR", "RevPAR"]);
        foreach (var row in rows)
        {
          table.Cell().Element(TableBodyCell).Text(row.RoomName).SemiBold();
          table.Cell().Element(TableBodyCell).Element(cell => ComposeAmountBar(cell, row.RoomRevenue, max, BrandInfo));
          table.Cell().Element(TableBodyCell).AlignRight().Text(Percent(row.OccupancyPct));
          table.Cell().Element(TableBodyCell).AlignRight().Text(Money(row.ADR));
          table.Cell().Element(TableBodyCell).AlignRight().Text(Money(row.RevPAR));
        }
      });
    });
  }

  private static void ComposeIssues(IContainer container, IReadOnlyList<SaludEmpresaDataQualityRow> rows)
  {
    container.Element(SectionCard).Column(section =>
    {
      section.Spacing(7);
      section.Item().Element(SectionTitle).Text("Conciliación y calidad de datos")
        .FontSize(11)
        .SemiBold()
        .FontColor(BrandPrimaryDark);

      if (rows.Count == 0)
      {
        section.Item().Text("Sin alertas relevantes del periodo.").FontColor(BrandMuted);
        return;
      }

      section.Item().Table(table =>
      {
        table.ColumnsDefinition(columns =>
        {
          columns.RelativeColumn(.6f);
          columns.RelativeColumn(1.1f);
          columns.RelativeColumn(1.8f);
          columns.RelativeColumn(.7f);
          columns.RelativeColumn(.8f);
        });

        AddHeader(table, ["Sev.", "Tipo", "Detalle", "Items", "Importe"]);
        foreach (var row in rows)
        {
          table.Cell().Element(TableBodyCell).Text(row.Severity).FontColor(SeverityColor(row.Severity)).SemiBold();
          table.Cell().Element(TableBodyCell).Text(row.CheckType);
          table.Cell().Element(TableBodyCell).Column(cell =>
          {
            cell.Item().Text(row.Item);
            if (!string.IsNullOrWhiteSpace(row.SampleReference))
            {
              cell.Item().Text(row.SampleReference).FontSize(7).FontColor(BrandMuted);
            }
          });
          table.Cell().Element(TableBodyCell).AlignRight().Text(row.ItemCount?.ToString("N0", MexicanCulture) ?? "-");
          table.Cell().Element(TableBodyCell).AlignRight().Text(Money(row.NetEffect ?? row.MetricAmount));
        }
      });
    });
  }

  private static void ComposeReconciliationDetail(IContainer container, IReadOnlyList<SaludEmpresaReconciliationRow> rows)
  {
    container.Element(SectionCard).Column(section =>
    {
      section.Spacing(7);
      section.Item().Element(SectionTitle).Text("Detalle completo de conciliacion")
        .FontSize(11).SemiBold().FontColor(BrandPrimaryDark);
      section.Item().Table(table =>
      {
        table.ColumnsDefinition(columns =>
        {
          columns.RelativeColumn(.45f); columns.RelativeColumn(.7f); columns.RelativeColumn(1.1f);
          columns.RelativeColumn(1.8f); columns.RelativeColumn(.7f); columns.RelativeColumn(1.1f);
        });
        AddHeader(table, ["Sev.", "Fecha", "Tipo", "Observacion", "Efecto", "Referencia"]);
        foreach (var row in rows)
        {
          table.Cell().Element(TableBodyCell).Text(row.Severity).FontColor(SeverityColor(row.Severity));
          table.Cell().Element(TableBodyCell).Text(row.EventDate?.ToString("dd/MM/yyyy") ?? "-");
          table.Cell().Element(TableBodyCell).Text(row.Type);
          table.Cell().Element(TableBodyCell).Column(cell => { cell.Item().Text(row.Item); if (!string.IsNullOrWhiteSpace(row.Notes)) cell.Item().Text(row.Notes).FontSize(6.5f).FontColor(BrandMuted); });
          table.Cell().Element(TableBodyCell).AlignRight().Text(Money(row.NetEffect ?? row.Amount));
          table.Cell().Element(TableBodyCell).Text(row.Reference ?? "-");
        }
      });
    });
  }

  private static void ComposeAmountBar(IContainer container, decimal? value, decimal max, string color)
  {
    container.Column(column =>
    {
      column.Spacing(2);
      column.Item().AlignRight().Text(Money(value)).FontSize(8);
      column.Item().Element(cell => ComposeMiniBar(cell, value ?? 0m, max, color));
    });
  }

  private static void ComposeMiniBar(IContainer container, decimal value, decimal max, string color)
  {
    var width = max <= 0 ? 0 : Math.Min(72m, Math.Abs(value) / max * 72m);
    container.Height(5).Row(row =>
    {
      if (width > 0)
      {
        row.ConstantItem((float)width).Background(color);
      }

      row.RelativeItem().Background("#EDF1F4");
    });
  }

  private static PdfMetric BuildMetric(string label, decimal? current, decimal? baseline, SaludEmpresaMetricFormat format)
  {
    return new PdfMetric(
      label,
      SaludEmpresaDashboardFormatting.FormatValue(current, format),
      SaludEmpresaDashboardFormatting.BuildChange(current, baseline, format));
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

  private static void ComposeFooter(IContainer container, SaludEmpresaPdfDocumentModel model)
  {
    container.Column(column =>
    {
      column.Item().LineHorizontal(1).LineColor(BrandBorder);
      column.Item().PaddingTop(4).Row(row =>
      {
        row.RelativeItem().Text($"Salud financiera | {Safe(model.Rfc)} | {model.PeriodStart:yyyy-MM-dd} a {model.PeriodEnd:yyyy-MM-dd}")
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
      .Padding(9);

  private static IContainer SectionTitle(IContainer container)
    => container
      .PaddingBottom(4)
      .BorderBottom(1)
      .BorderColor(BrandBorder);

  private static IContainer TableHeaderCell(IContainer container)
    => container
      .Background(BrandPrimary)
      .PaddingHorizontal(4)
      .PaddingVertical(3)
      .DefaultTextStyle(style => style.FontColor(Colors.White).FontSize(7.2f));

  private static IContainer TableBodyCell(IContainer container)
    => container
      .Background(Colors.White)
      .BorderBottom(1)
      .BorderColor(BrandBorder)
      .PaddingHorizontal(4)
      .PaddingVertical(3)
      .DefaultTextStyle(style => style.FontSize(7.2f));

  private static decimal MaxAbs(IEnumerable<decimal?> values)
    => MaxAbs(values.Where(value => value.HasValue).Select(value => value!.Value));

  private static decimal MaxAbs(IEnumerable<decimal> values)
  {
    var max = values.Select(Math.Abs).DefaultIfEmpty(0m).Max();
    return max <= 0 ? 1m : max;
  }

  private static string Money(decimal? value)
    => value.HasValue ? value.Value.ToString("C0", MexicanCulture) : "-";

  private static string Percent(decimal? value)
    => value.HasValue ? $"{value.Value.ToString("N1", MexicanCulture)}%" : "-";

  private static string ChangeColor(SaludEmpresaMetricChange change)
    => change.CssClass.Contains("good", StringComparison.OrdinalIgnoreCase)
      ? BrandSuccess
      : change.CssClass.Contains("bad", StringComparison.OrdinalIgnoreCase)
        ? BrandDanger
        : BrandMuted;

  private static string SeverityColor(string severity)
    => severity.Trim().Equals("Alta", StringComparison.OrdinalIgnoreCase)
      ? BrandDanger
      : severity.Trim().Equals("Media", StringComparison.OrdinalIgnoreCase)
        ? BrandWarning
        : BrandMuted;

  private static string Safe(string? value)
    => string.IsNullOrWhiteSpace(value) ? "-" : value.Trim();

  private sealed record PdfMetric(string Label, string Value, SaludEmpresaMetricChange Change);
  private sealed record PdfBreakdown(string Label, decimal Amount, string Color);

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
