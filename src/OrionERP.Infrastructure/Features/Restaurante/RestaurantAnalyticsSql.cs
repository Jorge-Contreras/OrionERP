namespace OrionERP.Infrastructure.Features.Restaurante;

/// <summary>
/// Fragmentos de SQL que comparten los reportes contables y el diagnóstico del
/// Restaurante, para que ambos midan el costo exactamente igual.
/// </summary>
internal static class RestaurantAnalyticsSql
{
  /// <summary>
  /// Costo unitario recalculado desde la receta activa con los precios de hoy.
  /// Reproduce la fórmula de BomRecipeService.CalculateTheoreticalCostAsync y
  /// además cuenta los componentes cuya conversión de unidad no resuelve, que
  /// de otro modo desaparecerían del costo sin aviso. Espera el parámetro @Rfc
  /// y expone la expresión RecetaCosto.
  /// </summary>
  public const string RecipeCostCte =
    """
    WITH VersionActiva AS
    (
      SELECT header.ProductMaterialId,
             versionInfo.Id AS BomVersionId,
             versionInfo.Rfc,
             versionInfo.YieldQuantity,
             versionInfo.YieldUnitId,
             versionInfo.FrozenTheoreticalCost,
             ROW_NUMBER() OVER (PARTITION BY header.ProductMaterialId ORDER BY versionInfo.Id DESC) AS Fila
      FROM logistica.BomHeader header
      JOIN logistica.BomVersion versionInfo
        ON versionInfo.Rfc = header.Rfc AND versionInfo.BomHeaderId = header.Id
      WHERE header.Rfc = @Rfc AND versionInfo.[Status] = 'Active'
    ),
    RecetaCosto AS
    (
      SELECT activa.ProductMaterialId,
             activa.YieldQuantity,
             unidad.Abbreviation AS UnidadRendimiento,
             CAST(activa.FrozenTheoreticalCost AS decimal(18,6)) AS CostoCongelado,
             CAST(ISNULL(SUM(
               component.Quantity
               * (1 + component.ExpectedWastePercent / 100.0)
               * COALESCE(materialConversion.Factor, globalConversion.Factor,
                          CASE WHEN component.UnitId = componentMaterial.BaseUnitId THEN 1 END)
               * COALESCE(componentMaterial.BaseUnitPrice, 0)
             ), 0) / NULLIF(activa.YieldQuantity, 0) AS decimal(18,6)) AS CostoRecalculado,
             SUM(CASE WHEN COALESCE(materialConversion.Factor, globalConversion.Factor,
                        CASE WHEN component.UnitId = componentMaterial.BaseUnitId THEN 1 END) IS NULL
                      THEN 1 ELSE 0 END) AS ComponentesSinConversion
      FROM VersionActiva activa
      JOIN logistica.BomComponent component
        ON component.Rfc = activa.Rfc AND component.BomVersionId = activa.BomVersionId
      JOIN logistica.Material componentMaterial
        ON componentMaterial.Rfc = component.Rfc AND componentMaterial.Id = component.ComponentMaterialId
      LEFT JOIN logistica.UnitOfMeasure unidad ON unidad.Id = activa.YieldUnitId
      OUTER APPLY
      (
        SELECT TOP (1) conversionInfo.Factor
        FROM logistica.MaterialUnitConversion conversionInfo
        WHERE conversionInfo.Rfc = componentMaterial.Rfc
          AND conversionInfo.MaterialId = componentMaterial.Id
          AND conversionInfo.FromUnitId = component.UnitId
          AND conversionInfo.ToUnitId = componentMaterial.BaseUnitId
          AND conversionInfo.IsActive = 1
      ) materialConversion
      OUTER APPLY
      (
        SELECT TOP (1) conversionInfo.Factor
        FROM logistica.UnitConversion conversionInfo
        WHERE conversionInfo.FromUnitId = component.UnitId
          AND conversionInfo.ToUnitId = componentMaterial.BaseUnitId
          AND conversionInfo.IsActive = 1
      ) globalConversion
      WHERE activa.Fila = 1
      GROUP BY activa.ProductMaterialId, activa.YieldQuantity, unidad.Abbreviation, activa.FrozenTheoreticalCost
    )
    """;
}
