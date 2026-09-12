using Dapper;
using OrionERP.Application.Features.Logistica.Stock;
using OrionERP.Infrastructure.Features.Logistica.Shared;

namespace OrionERP.Infrastructure.Features.Logistica.Stock;

/// <summary>
/// Consulta única de las opciones de inventario visibles (ubicaciones, saldos y lotes) que
/// comparten la pantalla de movimientos y la de merma. Vive aquí para que las dos lean
/// exactamente los mismos saldos: si el filtro de visibilidad cambia, cambia para ambas.
/// </summary>
internal static class InventoryOptionsQuery
{
  /// <summary>Tres conjuntos de resultados en este orden: ubicaciones, saldos, lotes.</summary>
  internal static string Sql { get; } =
    $$"""
    SELECT locationInfo.Id,locationInfo.LocationCode AS Code,locationInfo.LocationName AS [Name]
    FROM logistica.Location locationInfo
    WHERE locationInfo.Rfc=@Rfc AND locationInfo.IsActive=1 AND locationInfo.IsInventoryEnabled=1
      AND {{LogisticsLocationScope.VisibilitySql("locationInfo")}}
    ORDER BY locationInfo.LocationName,locationInfo.LocationCode;

    SELECT balanceInfo.MaterialId,balanceInfo.LocationId,material.MaterialCode,
           material.[Description] AS MaterialName,unitInfo.Abbreviation AS UnitCode,
           balanceInfo.Quantity,balanceInfo.ReservedQuantity,balanceInfo.AverageUnitCost,material.TrackLots
    FROM logistica.StockBalance balanceInfo
    JOIN logistica.Material material ON material.Rfc=balanceInfo.Rfc AND material.Id=balanceInfo.MaterialId
    JOIN logistica.Location locationInfo ON locationInfo.Rfc=balanceInfo.Rfc AND locationInfo.Id=balanceInfo.LocationId
    LEFT JOIN logistica.UnitOfMeasure unitInfo ON unitInfo.Id=material.BaseUnitId
    WHERE balanceInfo.Rfc=@Rfc AND ISNULL(balanceInfo.IsRemoved,0)=0
      AND material.IsActive=1 AND locationInfo.IsActive=1 AND locationInfo.IsInventoryEnabled=1
      AND {{LogisticsLocationScope.VisibilitySql("locationInfo")}}
    ORDER BY material.[Description],material.MaterialCode,locationInfo.LocationName;

    SELECT lotInfo.Id,lotInfo.MaterialId,lotBalance.LocationId,lotInfo.LotCode,lotInfo.ExpiresAt AS ExpirationDate,
           lotBalance.Quantity,lotBalance.ReservedQuantity
    FROM logistica.MaterialLot lotInfo
    JOIN logistica.LotBalance lotBalance ON lotBalance.Rfc=lotInfo.Rfc AND lotBalance.MaterialLotId=lotInfo.Id
    WHERE lotInfo.Rfc=@Rfc AND lotInfo.IsBlocked=0
      AND lotBalance.Quantity-lotBalance.ReservedQuantity>0
      AND {{LogisticsLocationScope.ForLocationIdSql("lotBalance.LocationId","lotBalance.Rfc")}}
    ORDER BY COALESCE(lotInfo.ExpiresAt,'9999-12-31'),lotInfo.LotCode;
    """;

  /// <summary>Lee los tres conjuntos en el orden que emite <see cref="Sql"/>.</summary>
  internal static async Task<InventoryMovementWorkspaceDto> ReadAsync(SqlMapper.GridReader multi)
    => new()
    {
      Locations = (await multi.ReadAsync<InventoryLocationOptionDto>()).AsList(),
      Balances = (await multi.ReadAsync<InventoryBalanceOptionDto>()).AsList(),
      Lots = (await multi.ReadAsync<InventoryLotOptionDto>()).AsList()
    };
}
