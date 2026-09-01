using System.Text;
using System.Text.RegularExpressions;
using OrionERP.Application.Features.Restaurante;
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

  [Fact]
  public void Generate_ComboHeaderIsFinancialAndOnlyComponentsCreateSectionTickets()
  {
    var model = new RestaurantReceiptPdfDocumentModel
    {
      SiteName = "Bruno's",
      Folio = 7,
      CustomerName = "Cliente Capacitación",
      CreatedAt = DateTimeOffset.UtcNow,
      Subtotal = 110m,
      Total = 110m,
      Lines =
      [
        new()
        {
          ProductName = "Combo capacitación",
          Quantity = 1,
          UnitPrice = 110m,
          LineKind = RestaurantOrderLineKinds.Combo,
          SectionName = "Combos"
        },
        new()
        {
          ProductName = "Chilaquiles",
          Quantity = 1,
          LineKind = RestaurantOrderLineKinds.ComboComponent,
          ParentProductName = "Combo capacitación",
          ComboSlotName = "Platillo",
          ChoicePriceDelta = 15m,
          SectionName = "Comida",
          StructuredModifiers =
          [
            new()
            {
              Name = "Cebolla",
              EffectKind = RestaurantModifierEffectKinds.RemoveIngredient
            },
            new()
            {
              Name = "Carne",
              EffectKind = RestaurantModifierEffectKinds.AddQuantity
            }
          ]
        }
      ]
    };

    var pdf = new RestaurantReceiptPdfService().Generate(model);

    Assert.Equal(1, model.SectionTicketCount);
    Assert.True(pdf.Length > 1_000);
    Assert.Equal("%PDF-", Encoding.ASCII.GetString(pdf, 0, 5));
    Assert.Equal(3, Regex.Matches(Encoding.Latin1.GetString(pdf), @"/Type\s*/Page(?!s)\b").Count);
  }

  [Fact]
  public void Generate_RendersMultipleComboQuantitiesAndComponentDetails()
  {
    var model = new RestaurantReceiptPdfDocumentModel
    {
      SiteName = "BRUNO'S GARDEN & SNACKS",
      Folio = 2,
      CustomerName = "Jorge Contreras",
      OrderType = "Pickup",
      CreatedAt = new DateTimeOffset(2026, 8, 31, 19, 47, 0, TimeSpan.FromHours(-6)),
      Subtotal = 355m,
      SubtotalBeforeTax = 306.03m,
      Tax = 48.97m,
      TaxRate = 0.16m,
      PricesIncludeTax = true,
      Total = 355m,
      Lines =
      [
        new()
        {
          ProductName = "COMBO CHILAQUILES · CAPACITACIÓN",
          Quantity = 1,
          UnitPrice = 125m,
          LineKind = RestaurantOrderLineKinds.Combo
        },
        new()
        {
          ProductName = "CHILAQUILES",
          Quantity = 1,
          ChoicePriceDelta = 30m,
          LineKind = RestaurantOrderLineKinds.ComboComponent,
          ParentProductName = "COMBO CHILAQUILES · CAPACITACIÓN",
          ComboSlotName = "Platillo",
          SectionName = "Comida/Desayuno",
          StructuredModifiers =
          [
            new()
            {
              Name = "PECHUGA DE POLLO DESHUESADA",
              EffectKind = RestaurantModifierEffectKinds.AddQuantity
            }
          ]
        },
        new()
        {
          ProductName = "CAFE AMERICANO",
          Quantity = 1,
          LineKind = RestaurantOrderLineKinds.ComboComponent,
          ParentProductName = "COMBO CHILAQUILES · CAPACITACIÓN",
          ComboSlotName = "Bebida",
          SectionName = "Bebidas S/A"
        },
        new()
        {
          ProductName = "COMBO CHILAQUILES · CAPACITACIÓN",
          Quantity = 2,
          UnitPrice = 115m,
          LineKind = RestaurantOrderLineKinds.Combo
        },
        new()
        {
          ProductName = "CHILAQUILES",
          Quantity = 2,
          ChoicePriceDelta = 20m,
          LineKind = RestaurantOrderLineKinds.ComboComponent,
          ParentProductName = "COMBO CHILAQUILES · CAPACITACIÓN",
          ComboSlotName = "Platillo",
          SectionName = "Comida/Desayuno",
          Notes = "Servir los dos platos al mismo tiempo"
        },
        new()
        {
          ProductName = "CAFE AMERICANO",
          Quantity = 2,
          LineKind = RestaurantOrderLineKinds.ComboComponent,
          ParentProductName = "COMBO CHILAQUILES · CAPACITACIÓN",
          ComboSlotName = "Bebida",
          SectionName = "Bebidas S/A"
        }
      ]
    };

    var pdf = new RestaurantReceiptPdfService().Generate(model);

    Assert.Equal(2, model.SectionTicketCount);
    Assert.True(pdf.Length > 1_000);
    Assert.Equal("%PDF-", Encoding.ASCII.GetString(pdf, 0, 5));
    Assert.Equal(4, Regex.Matches(Encoding.Latin1.GetString(pdf), @"/Type\s*/Page(?!s)\b").Count);
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
