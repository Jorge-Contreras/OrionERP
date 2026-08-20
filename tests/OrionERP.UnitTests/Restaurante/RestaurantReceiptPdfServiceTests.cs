using System.Text;
using System.Text.RegularExpressions;
using OrionERP.Web.Features.Restaurante;

namespace OrionERP.UnitTests.Restaurante;

public sealed class RestaurantReceiptPdfServiceTests
{
  [Fact]
  public void DocumentModel_CountsMenuSectionsAndCustomItemTicket()
  {
    var model = CreateSampleModel();

    Assert.Equal(3, model.SectionTicketCount);
  }

  [Fact]
  public void Generate_CreatesOrderNumberCustomerMenuSectionAndCustomItemTickets()
  {
    var service = new RestaurantReceiptPdfService();

    var pdf = service.Generate(CreateSampleModel());

    Assert.True(pdf.Length > 1_000);
    Assert.Equal("%PDF-", Encoding.ASCII.GetString(pdf, 0, 5));

    var pdfText = Encoding.Latin1.GetString(pdf);
    var pageObjects = Regex.Matches(pdfText, @"/Type\s*/Page(?!s)\b").Count;
    Assert.Equal(5, pageObjects);
  }

  internal static RestaurantReceiptPdfDocumentModel CreateSampleModel()
    => new()
    {
      SiteName = "Bruno's",
      Folio = 42,
      CustomerName = "María López",
      OrderType = "Table",
      TableName = "Mesa 4",
      CreatedAt = new DateTimeOffset(2026, 7, 25, 20, 15, 0, TimeSpan.FromHours(-6)),
      OrderNotes = "Entregar todo junto",
      Subtotal = 325m,
      DiscountTotal = 25m,
      SubtotalBeforeTax = 258.62m,
      Tax = 41.38m,
      TaxRate = 0.16m,
      PricesIncludeTax = true,
      Total = 300m,
      Tip = 30m,
      CashReceived = 350m,
      Change = 20m,
      Lines =
      [
        new()
        {
          ProductName = "Hamburguesa de la casa",
          Quantity = 2,
          UnitPrice = 125m,
          DiscountAmount = 25m,
          Notes = "Una sin cebolla",
          Modifiers = ["Queso extra"],
          SectionName = "Comida",
          SectionSortOrder = 1
        },
        new()
        {
          ProductName = "Limonada mineral",
          Quantity = 2,
          UnitPrice = 37.5m,
          SectionName = "Bebidas",
          SectionSortOrder = 2
        },
        new()
        {
          ProductName = "Cargo por artículo especial",
          Quantity = 1,
          UnitPrice = 0.01m,
          IsCustom = true
        }
      ]
    };
}
