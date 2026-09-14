using System.Globalization;
using OrionERP.Application.Features.Logistica.Purchasing;
using OrionERP.Web.Features.Logistica.Purchasing;

namespace OrionERP.UnitTests.Logistica;

public class PurchaseStockThresholdDisplayTests
{
  private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("es-MX");

  [Fact]
  public void OneLocation_ShowsStockAgainstItsLimitsAndFlagsReachingTheMinimum()
  {
    var hint = PurchaseStockThresholdDisplay.Describe(
      [new PurchaseStockThresholdDto { MaterialId = 7, LocationId = 3, Quantity = 2m, MinQuantity = 2m, MaxQuantity = 6m }],
      locationCount: 1,
      baseUnitName: "Pieza",
      Culture);

    Assert.Equal("Existencia 2 · Mín 2 · Máx 6 Pieza", hint.Text);
    Assert.True(hint.IsBelowMinimum);
    Assert.Contains("esta ubicación", hint.Title, StringComparison.Ordinal);
    Assert.Contains(PurchaseStockThresholdDisplay.AdjustHint, hint.Title, StringComparison.Ordinal);
  }

  [Fact]
  public void SeveralLocations_SumTheirLimitsAndFlagWhenAnyOneIsShort()
  {
    var hint = PurchaseStockThresholdDisplay.Describe(
      [
        new PurchaseStockThresholdDto { MaterialId = 7, LocationId = 3, Quantity = 3m, MinQuantity = 2m, MaxQuantity = 6m },
        new PurchaseStockThresholdDto { MaterialId = 7, LocationId = 4, Quantity = 0.5m, MinQuantity = 2m, MaxQuantity = 6m }
      ],
      locationCount: 2,
      baseUnitName: "Kilo",
      Culture);

    Assert.Equal("Existencia 3.5 · Mín 4 · Máx 12 Kilo", hint.Text);
    Assert.True(hint.IsBelowMinimum);
    Assert.Contains("2 ubicaciones", hint.Title, StringComparison.Ordinal);
  }

  [Fact]
  public void MissingLimits_SayTheyAreNotConfiguredInsteadOfShowingZeros()
  {
    var withoutBalance = PurchaseStockThresholdDisplay.Describe([], locationCount: 1, baseUnitName: null, Culture);
    var onlyMaximum = PurchaseStockThresholdDisplay.Describe(
      [new PurchaseStockThresholdDto { MaterialId = 7, LocationId = 3, Quantity = 1m, MaxQuantity = 4m }],
      locationCount: 1,
      baseUnitName: "Litro",
      Culture);

    Assert.Equal("Existencia 0 · sin mínimo ni máximo", withoutBalance.Text);
    Assert.False(withoutBalance.IsBelowMinimum);
    Assert.Equal("Existencia 1 · Mín — · Máx 4 Litro", onlyMaximum.Text);
    Assert.False(onlyMaximum.IsBelowMinimum);
  }
}
