using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace OrionERP.Web.Features.Restaurante;

public sealed class RestaurantReceiptPdfService : IRestaurantReceiptPdfService
{
  private static readonly CultureInfo MexicanCulture = CultureInfo.GetCultureInfo("es-MX");
  private const string MutedColor = "#3F3F3F";
  private const string LightColor = "#D5D5D5";

  public RestaurantReceiptPdfService()
  {
    QuestPDF.Settings.License = LicenseType.Community;
  }

  public byte[] Generate(RestaurantReceiptPdfDocumentModel model)
  {
    ArgumentNullException.ThrowIfNull(model);

    var sectionGroups = model.Lines
      .Select(line => new
      {
        Line = line,
        SectionName = RestaurantReceiptPdfDocumentModel.GetTicketSectionName(line)
      })
      .Where(item => item.SectionName is not null)
      .OrderBy(item => item.Line.IsCustom ? int.MaxValue : item.Line.SectionSortOrder)
      .GroupBy(item => item.SectionName!, StringComparer.OrdinalIgnoreCase)
      .ToList();

    return Document.Create(document =>
      {
        document.Page(page =>
        {
          ConfigureThermalPage(page);
          page.Content().Element(container => ComposeCustomerTicket(container, model));
        });

        foreach (var section in sectionGroups)
        {
          document.Page(page =>
          {
            ConfigureThermalPage(page);
            page.Content().Element(container => ComposeSectionTicket(
              container,
              model,
              section.Key,
              section.Select(item => item.Line).ToList()));
          });
        }
      })
      .GeneratePdf();
  }

  private static void ConfigureThermalPage(PageDescriptor page)
  {
    page.ContinuousSize(80, Unit.Millimetre);
    page.Margin(4, Unit.Millimetre);
    page.DefaultTextStyle(style => style
      .FontSize(8)
      .FontColor(Colors.Black)
      .LineHeight(1.15f));
  }

  private static void ComposeCustomerTicket(IContainer container, RestaurantReceiptPdfDocumentModel model)
  {
    container.Column(column =>
    {
      column.Spacing(3);
      column.Item().AlignCenter().Text(model.SiteName).FontSize(12).Bold();
      column.Item().AlignCenter().Text("TICKET DE CLIENTE").FontSize(7).SemiBold().LetterSpacing(0.08f);
      column.Item().AlignCenter().Text($"ORDEN {model.Folio:000}").FontSize(18).Bold();
      column.Item().AlignCenter().Text(model.CustomerName).FontSize(11).Bold();
      column.Item().AlignCenter().Text($"{OrderTypeLabel(model)}  |  {model.CreatedAt:dd/MM/yyyy HH:mm}")
        .FontSize(7)
        .FontColor(MutedColor);

      column.Item().PaddingVertical(2).LineHorizontal(1).LineColor(Colors.Black);

      foreach (var line in model.Lines)
      {
        column.Item().Element(item => ComposeCustomerLine(item, line, model.PricesIncludeTax));
      }

      if (!string.IsNullOrWhiteSpace(model.OrderNotes))
      {
        column.Item().PaddingTop(2).Text(text =>
        {
          text.Span("Nota de la orden: ").SemiBold();
          text.Span(model.OrderNotes.Trim());
        });
      }

      column.Item().PaddingVertical(2).LineHorizontal(1).LineColor(Colors.Black);
      column.Item().Element(item => ComposeMoneyRow(item, "Productos", model.Subtotal));
      if (model.DiscountTotal > 0)
      {
        column.Item().Element(item => ComposeMoneyRow(item, "Descuento", -model.DiscountTotal));
      }
      foreach (var promotion in model.Promotions)
      {
        var code = string.IsNullOrWhiteSpace(promotion.Code) ? string.Empty : $" · {promotion.Code}";
        column.Item().PaddingLeft(4).Text($"Promo: {promotion.PromotionName}{code} (-{promotion.DiscountAmount:C})")
          .FontSize(7)
          .FontColor(MutedColor);
      }
      column.Item().Element(item => ComposeMoneyRow(item, "Subtotal antes de IVA", model.SubtotalBeforeTax));
      column.Item().Element(item => ComposeMoneyRow(item, $"IVA ({model.TaxRate:P0})", model.Tax));
      if (model.Delivery > 0)
      {
        column.Item().Element(item => ComposeMoneyRow(item, "Entrega", model.Delivery));
      }
      column.Item().PaddingTop(2).Element(item => ComposeMoneyRow(item, "TOTAL", model.Total, emphasized: true));

      if (model.Tip > 0 || model.CashReceived > 0 || model.CardAmount > 0 || model.TransferAmount > 0 || model.BalanceDue > 0)
      {
        column.Item().PaddingTop(3).Text("PAGO").FontSize(7).SemiBold();
      }
      if (model.CashReceived > 0)
      {
        column.Item().Element(item => ComposeMoneyRow(item, "Efectivo recibido", model.CashReceived));
      }
      if (model.CardAmount > 0)
      {
        column.Item().Element(item => ComposeMoneyRow(item, "Tarjeta", model.CardAmount));
      }
      if (model.TransferAmount > 0)
      {
        column.Item().Element(item => ComposeMoneyRow(item, "Transferencia", model.TransferAmount));
      }
      if (model.Tip > 0)
      {
        column.Item().Element(item => ComposeMoneyRow(item, "Propina", model.Tip));
      }
      if (model.Change > 0)
      {
        column.Item().Element(item => ComposeMoneyRow(item, "Cambio", model.Change, emphasized: true));
      }
      if (model.BalanceDue > 0)
      {
        column.Item().Element(item => ComposeMoneyRow(item, "Saldo pendiente", model.BalanceDue, emphasized: true));
      }

      if (!string.IsNullOrWhiteSpace(model.MembershipNumber))
      {
        column.Item().PaddingTop(4).LineHorizontal(1).LineColor(LightColor);
        column.Item().PaddingTop(2).Text($"Membresía {model.MembershipNumber}").FontSize(8).SemiBold();
        column.Item().Text($"Puntos de esta compra: {model.PointsEarned}").FontSize(8);
        if (model.PointsBalance.HasValue)
        {
          column.Item().Text($"Saldo de puntos: {model.PointsBalance.Value}").FontSize(8).SemiBold();
        }
      }

      column.Item().PaddingTop(5).LineHorizontal(1).LineColor(LightColor);
      column.Item().PaddingTop(3).AlignCenter().Text("Gracias por su compra").FontSize(8).SemiBold();
      column.Item().Height(3, Unit.Millimetre);
    });
  }

  private static void ComposeCustomerLine(IContainer container, RestaurantReceiptPdfLineModel line, bool pricesIncludeTax)
  {
    container.Column(column =>
    {
      column.Spacing(1);
      column.Item().Row(row =>
      {
        row.ConstantItem(29).Text(FormatQuantity(line.Quantity)).SemiBold();
        row.RelativeItem().Text(line.ProductName).SemiBold();
        row.ConstantItem(52).AlignRight().Text(FormatMoney(LineAmount(line)));
      });

      if (line.Modifiers.Count > 0)
      {
        column.Item().PaddingLeft(29).Text($"+ {string.Join(", ", line.Modifiers)}")
          .FontSize(7)
          .FontColor(MutedColor);
      }
      if (!string.IsNullOrWhiteSpace(line.Notes))
      {
        column.Item().PaddingLeft(29).Text($"Nota: {line.Notes.Trim()}")
          .FontSize(7)
          .FontColor(MutedColor);
      }
      if (line.DiscountAmount > 0)
      {
        column.Item().PaddingLeft(29).Text($"Descuento de partida: {FormatMoney(line.DiscountAmount)}")
          .FontSize(7)
          .FontColor(MutedColor);
      }
      if (!pricesIncludeTax)
      {
        column.Item().PaddingLeft(29).Text("Importe antes de IVA")
          .FontSize(6.5f)
          .FontColor(MutedColor);
      }
    });
  }

  private static void ComposeSectionTicket(
    IContainer container,
    RestaurantReceiptPdfDocumentModel model,
    string sectionName,
    IReadOnlyList<RestaurantReceiptPdfLineModel> lines)
  {
    container.Column(column =>
    {
      column.Spacing(3);
      column.Item().AlignCenter().Text(model.SiteName).FontSize(9).Bold();
      column.Item().AlignCenter().Text($"SECCIÓN: {sectionName.ToUpper(MexicanCulture)}").FontSize(13).Bold();
      column.Item().AlignCenter().Text($"ORDEN {model.Folio:000}").FontSize(20).Bold();
      column.Item().AlignCenter().Text(model.CustomerName).FontSize(12).Bold();
      column.Item().AlignCenter().Text($"{OrderTypeLabel(model)}  |  {model.CreatedAt:dd/MM/yyyy HH:mm}")
        .FontSize(7)
        .FontColor(MutedColor);

      column.Item().PaddingVertical(2).LineHorizontal(1.2f).LineColor(Colors.Black);

      foreach (var line in lines)
      {
        column.Item().Element(item => ComposeSectionLine(item, line));
      }

      if (!string.IsNullOrWhiteSpace(model.OrderNotes))
      {
        column.Item().PaddingTop(3).BorderTop(1).BorderColor(LightColor).PaddingTop(3).Text(text =>
        {
          text.Span("NOTA DE LA ORDEN: ").Bold();
          text.Span(model.OrderNotes.Trim()).Bold();
        });
      }

      column.Item().PaddingTop(5).LineHorizontal(1).LineColor(Colors.Black);
      var lineCountLabel = lines.Count == 1 ? "1 partida" : $"{lines.Count} partidas";
      column.Item().PaddingTop(2).AlignCenter().Text($"{lineCountLabel} para {sectionName}").FontSize(7).SemiBold();
      column.Item().Height(3, Unit.Millimetre);
    });
  }

  private static void ComposeSectionLine(IContainer container, RestaurantReceiptPdfLineModel line)
  {
    container.PaddingVertical(2).Column(column =>
    {
      column.Spacing(1);
      column.Item().Text(text =>
      {
        text.Span($"{FormatQuantity(line.Quantity)} x ").FontSize(12).Bold();
        text.Span(line.ProductName).FontSize(11).Bold();
      });

      if (line.Modifiers.Count > 0)
      {
        column.Item().PaddingLeft(8).Text($"+ {string.Join(", ", line.Modifiers)}").FontSize(9).SemiBold();
      }
      if (!string.IsNullOrWhiteSpace(line.Notes))
      {
        column.Item()
          .Padding(3)
          .Border(1)
          .BorderColor(Colors.Black)
          .Text($"NOTA: {line.Notes.Trim()}")
          .FontSize(9)
          .Bold();
      }
    });
  }

  private static void ComposeMoneyRow(IContainer container, string label, decimal amount, bool emphasized = false)
  {
    container.Row(row =>
    {
      var labelText = row.RelativeItem().Text(label);
      var amountText = row.ConstantItem(62).AlignRight().Text(FormatMoney(amount));

      if (emphasized)
      {
        labelText.FontSize(10).Bold();
        amountText.FontSize(10).Bold();
      }
      else
      {
        labelText.FontSize(8);
        amountText.FontSize(8).SemiBold();
      }
    });
  }

  private static string OrderTypeLabel(RestaurantReceiptPdfDocumentModel model)
    => model.OrderType switch
    {
      "Table" when !string.IsNullOrWhiteSpace(model.TableName) => model.TableName.Trim(),
      "Table" => "Mesa",
      "Delivery" => "Domicilio",
      _ => "Para recoger"
    };

  private static decimal LineAmount(RestaurantReceiptPdfLineModel line)
    => decimal.Round(
      Math.Max(0, line.UnitPrice * line.Quantity - line.DiscountAmount),
      2,
      MidpointRounding.AwayFromZero);

  private static string FormatQuantity(decimal quantity)
    => quantity.ToString("0.##", MexicanCulture);

  private static string FormatMoney(decimal amount)
    => amount.ToString("C2", MexicanCulture);
}
