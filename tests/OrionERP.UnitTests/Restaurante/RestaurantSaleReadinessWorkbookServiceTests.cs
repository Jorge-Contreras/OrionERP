using ClosedXML.Excel;
using OrionERP.Application.Features.Restaurante;
using OrionERP.Infrastructure.Features.Restaurante;

namespace OrionERP.UnitTests.Restaurante;

public sealed class RestaurantSaleReadinessWorkbookServiceTests
{
  [Fact]
  public void Create_BuildsDetailedFormattedWorkbook()
  {
    var generatedAt = new DateTimeOffset(2026, 8, 9, 12, 34, 0, TimeSpan.FromHours(-6));
    var report = new RestaurantSaleReadinessReport
    {
      Rfc = "BRUNOS260707L26",
      SiteId = 1,
      SiteCode = "BRUNOS",
      SiteName = "Bruno's",
      SiteTimeZoneId = "Central Standard Time (Mexico)",
      MenuName = "Menú principal",
      GeneratedAtLocal = generatedAt,
      GeneratedAtUtc = generatedAt.ToUniversalTime(),
      Products =
      [
        new RestaurantSaleReadinessProduct
        {
          ProductId = 10,
          Sku = "BRUN-SIR-01",
          ProductName = "Hamburguesa de sirloin",
          Sections = "Hamburguesas",
          Price = 245m,
          MaterialId = 7066,
          MaterialCode = "BRUN-SIR-01",
          MaterialName = "Hamburguesa de sirloin",
          FulfillmentMode = "MakeToOrder",
          IsActive = true,
          Status = RestaurantSaleReadinessStatuses.InventoryBlocked,
          LeafIngredientCount = 1,
          ErrorCount = 1,
          EstimatedSellableUnits = 0,
          BottleneckMaterial = "SIRLOIN · Carne de sirloin",
          PredictedPosMessage = "Inventario insuficiente para el material 6977. Faltan 0.2500.",
          SuggestedAction = "Repón inventario."
        }
      ],
      Ingredients =
      [
        new RestaurantSaleReadinessIngredient
        {
          ProductId = 10,
          ProductSku = "BRUN-SIR-01",
          ProductName = "Hamburguesa de sirloin",
          MaterialId = 6977,
          MaterialCode = "SIRLOIN",
          MaterialName = "Carne de sirloin",
          BaseUnit = "kg",
          BomPath = "Hamburguesa > Carne de sirloin",
          BomDepth = 1,
          RequiredQuantity = 0.25m,
          UsableQuantity = 0,
          ProjectedUsableQuantity = -0.25m,
          ShortageQuantity = 0.25m,
          EstimatedSellableUnits = 0,
          Status = RestaurantSaleReadinessStatuses.InventoryBlocked,
          PredictedPosMessage = "Inventario insuficiente para el material 6977. Faltan 0.2500."
        }
      ],
      BomRows =
      [
        new RestaurantSaleReadinessBomRow
        {
          ProductId = 10,
          ProductSku = "BRUN-SIR-01",
          ProductName = "Hamburguesa de sirloin",
          Path = "Hamburguesa > Carne de sirloin",
          ParentMaterialId = 7066,
          ParentMaterialCode = "BRUN-SIR-01",
          ParentMaterialName = "Hamburguesa de sirloin",
          BomVersionId = 67,
          BomVersionNumber = 1,
          YieldQuantity = 1,
          YieldUnit = "u",
          ComponentMaterialId = 6977,
          ComponentMaterialCode = "SIRLOIN",
          ComponentMaterialName = "Carne de sirloin",
          ComponentQuantity = 250,
          ComponentUnit = "g",
          ConversionFactor = 0.001m,
          RequiredBaseQuantity = 0.25m,
          Status = RestaurantSaleReadinessStatuses.Ready
        }
      ],
      Modifiers =
      [
        new RestaurantSaleReadinessModifierRow
        {
          ProductId = 10,
          ProductSku = "BRUN-SIR-01",
          ProductName = "Hamburguesa de sirloin",
          GroupId = 4,
          GroupName = "Término",
          MinSelections = 1,
          MaxSelections = 1,
          OptionId = 8,
          OptionName = "Medio",
          Status = RestaurantSaleReadinessStatuses.Ready,
          Message = "Sin impacto adicional de inventario."
        }
      ],
      EnvironmentChecks =
      [
        new RestaurantSaleReadinessEnvironmentCheck
        {
          Area = "Sede",
          Check = "Módulo habilitado",
          Status = RestaurantSaleReadinessStatuses.Ready,
          Detail = "La sede está habilitada."
        },
        new RestaurantSaleReadinessEnvironmentCheck
        {
          Area = "Caja",
          Check = "Turno abierto",
          Status = RestaurantSaleReadinessStatuses.Warning,
          Detail = "No hay turno abierto.",
          RecommendedAction = "Abre el turno."
        }
      ],
      Actions =
      [
        new RestaurantSaleReadinessAction
        {
          Severity = RestaurantSaleReadinessSeverities.Error,
          ProductSku = "BRUN-SIR-01",
          ProductName = "Hamburguesa de sirloin",
          MaterialId = 6977,
          Material = "SIRLOIN · Carne de sirloin",
          Issue = "Inventario insuficiente.",
          ShortageQuantity = 0.25m,
          RecommendedAction = "Repón inventario."
        }
      ]
    };

    var export = new RestaurantSaleReadinessWorkbookService().Create(report);

    Assert.Equal("diagnostico-venta-BRUNOS260707L26-BRUNOS-20260809-1234.xlsx", export.FileName);
    Assert.True(export.Bytes.Length > 10_000);
    Assert.Equal((byte)'P', export.Bytes[0]);
    Assert.Equal((byte)'K', export.Bytes[1]);
    using var workbook = new XLWorkbook(new MemoryStream(export.Bytes));
    Assert.Equal(
      ["Resumen", "Productos", "Ingredientes", "BOM y conversiones", "Modificadores", "Entorno POS"],
      workbook.Worksheets.Select(sheet => sheet.Name).ToArray());
    Assert.Equal("BRUN-SIR-01", workbook.Worksheet("Productos").Cell("F5").GetString());
    Assert.Equal(245m, workbook.Worksheet("Productos").Cell("H5").GetValue<decimal>());
    Assert.Equal(0.25m, workbook.Worksheet("Ingredientes").Cell("K5").GetValue<decimal>());
    Assert.Equal("COUNTA('Productos'!$F$5:$F$5)", workbook.Worksheet("Resumen").Cell("A11").FormulaA1);
    Assert.True(workbook.Worksheet("Entorno POS").Cell("E5").IsEmpty());
    Assert.Contains(workbook.Worksheet("Productos").Tables, table => table.Name == "ReadinessProducts");
    Assert.True(workbook.Worksheet("Productos").SheetView.SplitRow >= 4);
    Assert.NotEmpty(workbook.Worksheet("Productos").ConditionalFormats);
  }
}
