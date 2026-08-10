using System.Data.Common;
using Dapper;
using OrionERP.Application.Features.Restaurante;

namespace OrionERP.Infrastructure.Features.Restaurante;

internal static class RestaurantRequirementGraphLoader
{
  public static async Task<RestaurantSaleRequirementGraph> LoadAsync(
    DbConnection connection,
    DbTransaction? transaction,
    string rfc,
    IReadOnlyCollection<long>? modifierOptionIds,
    CancellationToken ct)
  {
    var optionIds = modifierOptionIds is { Count: > 0 } ? modifierOptionIds.ToArray() : [-1L];
    const string sql =
      """
      SELECT material.Id, material.MaterialCode AS Code, material.[Description] AS [Name],
             material.FulfillmentMode, material.BaseUnitId,
             COALESCE(NULLIF(baseUnit.Abbreviation, ''), baseUnit.UnitName) AS BaseUnit,
             material.TrackLots, material.IsActive
      FROM logistica.Material material
      LEFT JOIN logistica.UnitOfMeasure baseUnit ON baseUnit.Id = material.BaseUnitId
      WHERE material.Rfc = @Rfc;

      SELECT headerInfo.ProductMaterialId, versionInfo.Id AS VersionId, versionInfo.VersionNumber,
             versionInfo.YieldQuantity, versionInfo.YieldUnitId,
             COALESCE(NULLIF(yieldUnit.Abbreviation, ''), yieldUnit.UnitName) AS YieldUnit,
             component.Id AS ComponentId, component.ComponentMaterialId, component.Quantity,
             component.UnitId,
             COALESCE(NULLIF(componentUnit.Abbreviation, ''), componentUnit.UnitName) AS ComponentUnit,
             component.ExpectedWastePercent, component.SortOrder
      FROM logistica.BomHeader headerInfo
      JOIN logistica.BomVersion versionInfo
        ON versionInfo.Rfc = headerInfo.Rfc AND versionInfo.BomHeaderId = headerInfo.Id
       AND versionInfo.[Status] = 'Active'
      LEFT JOIN logistica.BomComponent component
        ON component.Rfc = versionInfo.Rfc AND component.BomVersionId = versionInfo.Id
      LEFT JOIN logistica.UnitOfMeasure yieldUnit ON yieldUnit.Id = versionInfo.YieldUnitId
      LEFT JOIN logistica.UnitOfMeasure componentUnit ON componentUnit.Id = component.UnitId
      WHERE headerInfo.Rfc = @Rfc
      ORDER BY headerInfo.ProductMaterialId, component.SortOrder, component.Id;

      SELECT MaterialId, FromUnitId, ToUnitId, Factor
      FROM logistica.MaterialUnitConversion
      WHERE Rfc = @Rfc AND IsActive = 1;

      SELECT FromUnitId, ToUnitId, Factor
      FROM logistica.UnitConversion
      WHERE IsActive = 1;

      SELECT delta.ModifierOptionId AS OptionId, delta.MaterialId, delta.QuantityDelta,
             delta.UnitId, COALESCE(NULLIF(unitInfo.Abbreviation, ''), unitInfo.UnitName) AS Unit
      FROM restaurante.ModifierIngredientDelta delta
      LEFT JOIN logistica.UnitOfMeasure unitInfo ON unitInfo.Id = delta.UnitId
      WHERE delta.Rfc = @Rfc AND delta.ModifierOptionId IN @OptionIds;
      """;

    using var multi = await connection.QueryMultipleAsync(new CommandDefinition(
      sql,
      new { Rfc = rfc, OptionIds = optionIds },
      transaction,
      cancellationToken: ct));
    var materials = (await multi.ReadAsync<MaterialRow>()).AsList();
    var bomRows = (await multi.ReadAsync<BomRow>()).AsList();
    var materialConversions = (await multi.ReadAsync<MaterialConversionRow>()).AsList();
    var globalConversions = (await multi.ReadAsync<GlobalConversionRow>()).AsList();
    var modifierDeltas = (await multi.ReadAsync<ModifierDeltaRow>()).AsList();

    return new RestaurantSaleRequirementGraph
    {
      Materials = materials.ToDictionary(
        material => material.Id,
        material => new RestaurantSaleMaterialNode
        {
          Id = material.Id,
          Code = material.Code,
          Name = material.Name,
          FulfillmentMode = material.FulfillmentMode,
          BaseUnitId = material.BaseUnitId,
          BaseUnit = material.BaseUnit,
          TrackLots = material.TrackLots,
          IsActive = material.IsActive
        }),
      ActiveBoms = bomRows.GroupBy(row => row.ProductMaterialId).ToDictionary(
        group => group.Key,
        group =>
        {
          var header = group.First();
          return new RestaurantSaleBomNode
          {
            VersionId = header.VersionId,
            VersionNumber = header.VersionNumber,
            ProductMaterialId = header.ProductMaterialId,
            YieldQuantity = header.YieldQuantity,
            YieldUnitId = header.YieldUnitId,
            YieldUnit = header.YieldUnit,
            Components = group.Where(row => row.ComponentId.HasValue).Select(row => new RestaurantSaleBomComponentNode
            {
              Id = row.ComponentId!.Value,
              MaterialId = row.ComponentMaterialId!.Value,
              Quantity = row.Quantity!.Value,
              UnitId = row.UnitId!.Value,
              Unit = row.ComponentUnit,
              ExpectedWastePercent = row.ExpectedWastePercent!.Value,
              SortOrder = row.SortOrder!.Value
            }).ToList()
          };
        }),
      UnitConversions = materialConversions.Select(row => new RestaurantSaleUnitConversionNode
        {
          MaterialId = row.MaterialId,
          FromUnitId = row.FromUnitId,
          ToUnitId = row.ToUnitId,
          Factor = row.Factor
        })
        .Concat(globalConversions.Select(row => new RestaurantSaleUnitConversionNode
        {
          MaterialId = null,
          FromUnitId = row.FromUnitId,
          ToUnitId = row.ToUnitId,
          Factor = row.Factor
        }))
        .ToList(),
      ModifierDeltas = modifierDeltas.Select(row => new RestaurantSaleModifierDeltaNode
      {
        OptionId = row.OptionId,
        MaterialId = row.MaterialId,
        QuantityDelta = row.QuantityDelta,
        UnitId = row.UnitId,
        Unit = row.Unit
      }).ToList()
    };
  }

  private sealed class MaterialRow
  {
    public int Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string FulfillmentMode { get; set; } = string.Empty;
    public int BaseUnitId { get; set; }
    public string BaseUnit { get; set; } = string.Empty;
    public bool TrackLots { get; set; }
    public bool IsActive { get; set; }
  }

  private sealed class BomRow
  {
    public int ProductMaterialId { get; set; }
    public long VersionId { get; set; }
    public int VersionNumber { get; set; }
    public decimal YieldQuantity { get; set; }
    public int YieldUnitId { get; set; }
    public string YieldUnit { get; set; } = string.Empty;
    public long? ComponentId { get; set; }
    public int? ComponentMaterialId { get; set; }
    public decimal? Quantity { get; set; }
    public int? UnitId { get; set; }
    public string ComponentUnit { get; set; } = string.Empty;
    public decimal? ExpectedWastePercent { get; set; }
    public int? SortOrder { get; set; }
  }

  private sealed class MaterialConversionRow
  {
    public int MaterialId { get; set; }
    public int FromUnitId { get; set; }
    public int ToUnitId { get; set; }
    public decimal Factor { get; set; }
  }

  private sealed class GlobalConversionRow
  {
    public int FromUnitId { get; set; }
    public int ToUnitId { get; set; }
    public decimal Factor { get; set; }
  }

  private sealed class ModifierDeltaRow
  {
    public long OptionId { get; set; }
    public int MaterialId { get; set; }
    public decimal QuantityDelta { get; set; }
    public int UnitId { get; set; }
    public string Unit { get; set; } = string.Empty;
  }
}
