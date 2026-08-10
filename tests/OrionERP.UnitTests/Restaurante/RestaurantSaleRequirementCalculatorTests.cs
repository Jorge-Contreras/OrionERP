using OrionERP.Application.Features.Restaurante;

namespace OrionERP.UnitTests.Restaurante;

public sealed class RestaurantSaleRequirementCalculatorTests
{
  [Fact]
  public void Calculate_ExpandsNestedBomWasteAndConversions()
  {
    var graph = Graph(
      materials:
      [
        Material(100, "BURGER", "Hamburguesa", "MakeToOrder", 1),
        Material(200, "PATTY", "Carne preparada", "MakeToOrder", 1),
        Material(300, "MEAT", "Carne de sirloin", "StockItem", 1)
      ],
      boms:
      [
        Bom(100, 10, 1, Component(1, 200, 2, 1, 10)),
        Bom(200, 20, 2, Component(2, 300, 500, 2, 0))
      ],
      conversions:
      [
        new RestaurantSaleUnitConversionNode { MaterialId = 300, FromUnitId = 2, ToUnitId = 1, Factor = 0.001m }
      ]);

    var result = RestaurantSaleRequirementCalculator.Calculate(graph, 100, "BRUN-SIR-01", 1);

    Assert.Empty(result.Issues);
    Assert.Equal(0.55m, result.Requirements[300]);
    Assert.Contains("Hamburguesa", result.RequirementPaths[300], StringComparison.Ordinal);
    Assert.Contains("Carne preparada", result.RequirementPaths[300], StringComparison.Ordinal);
    Assert.Equal(2, result.Trace.Count);
    Assert.Equal(0.55m, result.Trace.Last().RequiredBaseQuantity);
  }

  [Fact]
  public void Calculate_ReportsNestedMakeToOrderMaterialWithoutBom()
  {
    var graph = Graph(
      materials:
      [
        Material(100, "BURGER", "Hamburguesa", "MakeToOrder", 1),
        Material(6938, "SIRLOIN", "Carne de sirloin", "MakeToOrder", 1)
      ],
      boms: [Bom(100, 10, 1, Component(1, 6938, 1, 1, 0))]);

    var result = RestaurantSaleRequirementCalculator.Calculate(graph, 100, "BRUN-SIR-01", 1);

    var issue = Assert.Single(result.Issues);
    Assert.Equal("BOM_MISSING", issue.Code);
    Assert.Contains("BRUN-SIR-01", issue.Message, StringComparison.Ordinal);
    Assert.Contains("material 6938", issue.Message, StringComparison.Ordinal);
    Assert.Contains("fabricación bajo pedido", issue.Message, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public void Calculate_ReportsUnitConversionMissing()
  {
    var graph = Graph(
      materials:
      [
        Material(100, "BURGER", "Hamburguesa", "MakeToOrder", 1),
        Material(300, "MEAT", "Carne", "StockItem", 1)
      ],
      boms: [Bom(100, 10, 1, Component(1, 300, 250, 2, 0))]);

    var result = RestaurantSaleRequirementCalculator.Calculate(graph, 100, "BURGER-01", 1);

    var issue = Assert.Single(result.Issues);
    Assert.Equal("BOM_CONVERSION_MISSING", issue.Code);
    Assert.Equal("Falta una conversión de unidad para el material 300.", issue.Message);
    Assert.Empty(result.Requirements);
  }

  [Fact]
  public void Calculate_ReportsBomCycle()
  {
    var graph = Graph(
      materials:
      [
        Material(100, "A", "Producto A", "MakeToOrder", 1),
        Material(200, "B", "Producto B", "MakeToOrder", 1)
      ],
      boms:
      [
        Bom(100, 10, 1, Component(1, 200, 1, 1, 0)),
        Bom(200, 20, 1, Component(2, 100, 1, 1, 0))
      ]);

    var result = RestaurantSaleRequirementCalculator.Calculate(graph, 100, "CYCLE", 1);

    Assert.Contains(result.Issues, issue => issue.Code == "BOM_CYCLE_OR_DEPTH");
    Assert.Contains(result.Trace, trace => trace.Status == RestaurantSaleReadinessStatuses.ConfigurationBlocked);
  }

  [Fact]
  public void Calculate_AppliesSelectedModifierDeltaOnly()
  {
    var graph = Graph(
      materials: [Material(300, "MEAT", "Carne", "StockItem", 1)],
      deltas:
      [
        new RestaurantSaleModifierDeltaNode { OptionId = 7, MaterialId = 300, QuantityDelta = 50, UnitId = 2, Unit = "g" },
        new RestaurantSaleModifierDeltaNode { OptionId = 8, MaterialId = 300, QuantityDelta = 100, UnitId = 2, Unit = "g" }
      ],
      conversions:
      [
        new RestaurantSaleUnitConversionNode { MaterialId = 300, FromUnitId = 2, ToUnitId = 1, Factor = 0.001m }
      ]);

    var result = RestaurantSaleRequirementCalculator.Calculate(graph, 300, "MEAT-01", 2, [7]);

    Assert.Empty(result.Issues);
    Assert.Equal(2.1m, result.Requirements[300]);
  }

  private static RestaurantSaleRequirementGraph Graph(
    IReadOnlyList<RestaurantSaleMaterialNode> materials,
    IReadOnlyList<RestaurantSaleBomNode>? boms = null,
    IReadOnlyList<RestaurantSaleUnitConversionNode>? conversions = null,
    IReadOnlyList<RestaurantSaleModifierDeltaNode>? deltas = null)
    => new()
    {
      Materials = materials.ToDictionary(material => material.Id),
      ActiveBoms = (boms ?? []).ToDictionary(bom => bom.ProductMaterialId),
      UnitConversions = conversions ?? [],
      ModifierDeltas = deltas ?? []
    };

  private static RestaurantSaleMaterialNode Material(int id, string code, string name, string mode, int baseUnitId)
    => new()
    {
      Id = id,
      Code = code,
      Name = name,
      FulfillmentMode = mode,
      BaseUnitId = baseUnitId,
      BaseUnit = "kg",
      IsActive = true
    };

  private static RestaurantSaleBomNode Bom(int productMaterialId, long versionId, decimal yield, params RestaurantSaleBomComponentNode[] components)
    => new()
    {
      ProductMaterialId = productMaterialId,
      VersionId = versionId,
      VersionNumber = 1,
      YieldQuantity = yield,
      YieldUnitId = 1,
      YieldUnit = "u",
      Components = components
    };

  private static RestaurantSaleBomComponentNode Component(long id, int materialId, decimal quantity, int unitId, decimal waste)
    => new()
    {
      Id = id,
      MaterialId = materialId,
      Quantity = quantity,
      UnitId = unitId,
      Unit = unitId == 1 ? "kg" : "g",
      ExpectedWastePercent = waste,
      SortOrder = (int)id
    };
}

