using OrionERP.Application.Features.Restaurante;
using OrionERP.Web.Features.Restaurante;
using System.Text;

namespace OrionERP.UnitTests.Restaurante;

public sealed class RestaurantReceiptReprintMapperTests
{
  [Fact]
  public void Create_PreservesComboHierarchySnapshotsAndDoesNotDoubleSubtotal()
  {
    var receipt = new RestaurantReceiptDto
    {
      SiteName = "Bruno's",
      SiteTimeZoneId = "UTC",
      CreatedAt = DateTime.UtcNow,
      Total = 145m,
      Lines =
      [
        new()
        {
          Id = 10,
          ProductId = 500,
          ProductName = "Combo chilaquiles",
          LineKind = RestaurantOrderLineKinds.Combo,
          Quantity = 1,
          UnitPrice = 145m,
          BaseUnitPrice = 95m,
          ChoicePriceDelta = 50m
        },
        new()
        {
          Id = 11,
          ProductId = 501,
          ProductName = "Chilaquiles",
          LineKind = RestaurantOrderLineKinds.ComboComponent,
          ParentOrderLineId = 10,
          ParentProductName = "Combo chilaquiles",
          ComboSlotName = "Platillo",
          Quantity = 1,
          ChoicePriceDelta = 30m,
          MenuSectionName = "Comida",
          MenuSectionSortOrder = 1,
          StructuredModifiers =
          [
            new()
            {
              ModifierOptionId = 99,
              GroupName = "Cambios",
              Name = "Cebolla",
              EffectKind = RestaurantModifierEffectKinds.RemoveIngredient
            },
            new()
            {
              ModifierOptionId = 99,
              GroupName = "Cambios",
              Name = "Carne",
              EffectKind = RestaurantModifierEffectKinds.AddQuantity
            }
          ]
        }
      ]
    };

    var model = RestaurantReceiptReprintMapper.Create(receipt, new RestaurantPosCatalogDto());

    Assert.Equal(145m, model.Subtotal);
    Assert.Equal(1, model.SectionTicketCount);
    Assert.Equal(RestaurantOrderLineKinds.Combo, model.Lines[0].LineKind);
    Assert.Equal(RestaurantOrderLineKinds.ComboComponent, model.Lines[1].LineKind);
    Assert.Equal("Combo chilaquiles", model.Lines[1].ParentProductName);
    Assert.Equal("Platillo", model.Lines[1].ComboSlotName);
    Assert.Collection(model.Lines[1].StructuredModifiers,
      removed => Assert.Equal(RestaurantModifierEffectKinds.RemoveIngredient, removed.EffectKind),
      added => Assert.Equal(RestaurantModifierEffectKinds.AddQuantity, added.EffectKind));
  }

  [Fact]
  public void Create_PrefersSaleTimeSectionSnapshotOverCurrentCatalog()
  {
    var receipt = new RestaurantReceiptDto
    {
      SiteName = "Bruno's",
      SiteTimeZoneId = "UTC",
      CreatedAt = DateTime.UtcNow,
      Lines =
      [
        new()
        {
          ProductId = 100,
          ProductName = "Hamburguesa",
          Quantity = 1,
          UnitPrice = 100,
          MenuSectionId = 10,
          MenuSectionName = "Cena",
          MenuSectionSortOrder = 4
        }
      ]
    };
    var catalog = new RestaurantPosCatalogDto
    {
      Sections =
      [
        new()
        {
          Id = 11,
          Name = "Menú actualizado",
          Products = [new() { Id = 100 }]
        }
      ]
    };

    var model = RestaurantReceiptReprintMapper.Create(receipt, catalog);

    var line = Assert.Single(model.Lines);
    Assert.Equal("Cena", line.SectionName);
    Assert.Equal(4, line.SectionSortOrder);
  }

  [Fact]
  public void Create_UsesPersistedReceiptValuesAndMarksEveryCopyAsAReprint()
  {
    var createdAt = new DateTime(2026, 8, 5, 18, 30, 0, DateTimeKind.Unspecified);
    var receipt = new RestaurantReceiptDto
    {
      OrderId = Guid.NewGuid(),
      SiteId = 7,
      SiteName = "Bruno's",
      SiteTimeZoneId = "UTC",
      Folio = 42,
      OrderType = "Pickup",
      CustomerName = "María López",
      CreatedAt = createdAt,
      DiscountTotal = 20m,
      TaxTotal = 31.72m,
      TipTotal = 15m,
      Total = 230m,
      TaxRate = 0.16m,
      PricesIncludeTax = true,
      MembershipNumber = "BR-100",
      PointsEarned = 23,
      PointsRedeemed = 100,
      RedemptionValue = 100m,
      PointsBalance = 173,
      Lines =
      [
        new()
        {
          Id = 1,
          ProductId = 100,
          ProductName = "Hamburguesa",
          Quantity = 2,
          UnitPrice = 100m,
          DiscountAmount = 20m,
          Modifiers = ["Queso extra"]
        },
        new()
        {
          Id = 2,
          IsCustom = true,
          ProductName = "Cargo personalizado",
          Quantity = 1,
          UnitPrice = 50m
        }
      ],
      Payments =
      [
        new() { PaymentMethod = "Cash", Amount = 200m, RefundedAmount = 20m },
        new() { PaymentMethod = "ExternalCard", Amount = 50m }
      ],
      Promotions =
      [
        new() { PromotionId = 5, PromotionName = "Promo verano", DiscountAmount = 20m }
      ]
    };
    var catalog = new RestaurantPosCatalogDto
    {
      Site = new() { Id = 7, Name = "Bruno's" },
      Sections =
      [
        new()
        {
          Id = 10,
          Name = "Comida",
          SortOrder = 1,
          Products = [new() { Id = 100, Name = "Hamburguesa" }]
        }
      ]
    };

    var model = RestaurantReceiptReprintMapper.Create(receipt, catalog);

    Assert.True(model.IsReprint);
    Assert.Equal(250m, model.Subtotal);
    Assert.Equal(198.28m, model.SubtotalBeforeTax);
    Assert.Equal(180m, model.CashReceived);
    Assert.Equal(50m, model.CardAmount);
    Assert.Equal(2, model.SectionTicketCount);
    Assert.Equal("Comida", model.Lines[0].SectionName);
    Assert.True(model.Lines[1].IsCustom);
    Assert.Equal("Promo verano", Assert.Single(model.Promotions).PromotionName);
    Assert.Equal(100, model.PointsRedeemed);
    Assert.Equal(100m, model.RedemptionValue);
    Assert.Equal(173, model.PointsBalance);
    Assert.Equal(new DateTimeOffset(2026, 8, 5, 18, 30, 0, TimeSpan.Zero), model.CreatedAt);

    var pdf = new RestaurantReceiptPdfService().Generate(model);
    Assert.True(pdf.Length > 1_000);
    Assert.Equal("%PDF-", Encoding.ASCII.GetString(pdf, 0, 5));
  }
}
