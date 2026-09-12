using System.Data.Common;
using Dapper;

namespace OrionERP.Infrastructure.Features.Logistica.Stock;

/// <summary>
/// Aplica una partida de ajuste o de merma sobre el inventario: valida material, ubicación
/// y disponible, mueve <c>StockBalance</c> y <c>LotBalance</c>, escribe la partida del
/// documento con su costo congelado y agrega la fila del kardex.
/// </summary>
/// <remarks>
/// Existe para que <see cref="InventoryMovementService"/> y <see cref="WasteService"/>
/// compartan una sola implementación de estas invariantes: duplicarlas es cómo se pierde
/// la cuadratura entre el saldo, el lote y el kardex. Debe llamarse dentro de una
/// transacción <c>Serializable</c> ya abierta y con el alcance de ubicación verificado.
/// </remarks>
internal static class InventoryAdjustmentWriter
{
  /// <param name="documentSubject">
  /// Sujeto del mensaje de error, ya con su artículo: <c>"El ajuste"</c>, <c>"La merma"</c>.
  /// </param>
  /// <param name="frozenUnitCost">
  /// Costo a congelar en la partida. <c>null</c> toma el promedio vigente del saldo; una
  /// reversa pasa el costo del documento original para deshacer el valor exacto.
  /// </param>
  internal static async Task ApplyLineAsync(
    DbConnection conn,
    DbTransaction tx,
    string rfc,
    long adjustmentId,
    string transactionType,
    string documentSubject,
    int materialId,
    int locationId,
    long? materialLotId,
    decimal quantityDelta,
    decimal? frozenUnitCost,
    string notes,
    string userName,
    CancellationToken ct)
  {
    var material = await LoadMaterialAsync(conn, tx, rfc, materialId, ct)
      ?? throw new InvalidOperationException("Un material no pertenece al RFC o está inactivo.");
    if (material.TrackLots && !materialLotId.HasValue)
      throw new InvalidOperationException($"El material {material.MaterialCode} requiere seleccionar lote.");
    if (!await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
      "SELECT CAST(CASE WHEN EXISTS(SELECT 1 FROM logistica.Location WITH (UPDLOCK,HOLDLOCK) WHERE Rfc=@Rfc AND Id=@LocationId AND IsActive=1 AND IsInventoryEnabled=1) THEN 1 ELSE 0 END AS bit);",
      new { Rfc = rfc, LocationId = locationId }, tx, cancellationToken: ct)))
      throw new InvalidOperationException("Una ubicación no pertenece al RFC o está inactiva.");

    var balance = await LoadBalanceAsync(conn, tx, rfc, locationId, materialId, ct);
    if (balance is null && quantityDelta < 0)
      throw new InvalidOperationException($"No existe saldo de {material.MaterialCode} para descontar.");
    if (balance is not null && quantityDelta < 0 && balance.Quantity - balance.ReservedQuantity < -quantityDelta)
      throw new InvalidOperationException($"{documentSubject} excede el disponible de {material.MaterialCode}.");
    var balanceId = balance?.Id ?? await EnsureBalanceAsync(conn, tx, rfc, locationId, materialId, ct);
    var quantityAfter = (balance?.Quantity ?? 0) + quantityDelta;

    if (materialLotId.HasValue)
    {
      var lot = await LoadMaterialLotAsync(conn, tx, rfc, materialId, materialLotId.Value, ct)
        ?? throw new InvalidOperationException($"El lote de {material.MaterialCode} no pertenece al RFC/material.");
      var lotBalance = await LoadLotAsync(conn, tx, rfc, locationId, materialId, materialLotId.Value, ct);
      if (lotBalance is null && quantityDelta < 0)
        throw new InvalidOperationException($"El lote {lot.LotCode} no tiene saldo en la ubicación.");
      if (lotBalance is not null && quantityDelta < 0 && lotBalance.Quantity - lotBalance.ReservedQuantity < -quantityDelta)
        throw new InvalidOperationException($"{documentSubject} excede el disponible del lote {lot.LotCode}.");
      await conn.ExecuteAsync(new CommandDefinition(
        lotBalance is null
          ? "INSERT INTO logistica.LotBalance (Rfc,MaterialLotId,MaterialId,LocationId,Quantity,ReservedQuantity) VALUES (@Rfc,@LotId,@MaterialId,@LocationId,@Delta,0);"
          : "UPDATE logistica.LotBalance SET Quantity=Quantity+@Delta,UpdatedAt=SYSUTCDATETIME() WHERE Rfc=@Rfc AND MaterialLotId=@LotId AND LocationId=@LocationId;",
        new { Rfc = rfc, Delta = quantityDelta, LotId = materialLotId.Value, MaterialId = materialId, LocationId = locationId }, tx, cancellationToken: ct));
    }

    await conn.ExecuteAsync(new CommandDefinition(
      """
      UPDATE logistica.StockBalance SET Quantity=Quantity+@Delta,UpdatedAt=SYSUTCDATETIME()
      WHERE Rfc=@Rfc AND Id=@BalanceId;
      INSERT INTO logistica.InventoryAdjustmentLine
        (Rfc,AdjustmentId,MaterialId,LocationId,MaterialLotId,QuantityDelta,FrozenUnitCost)
      VALUES
        (@Rfc,@AdjustmentId,@MaterialId,@LocationId,@MaterialLotId,@Delta,@UnitCost);
      """,
      new
      {
        Rfc = rfc,
        Delta = quantityDelta,
        BalanceId = balanceId,
        AdjustmentId = adjustmentId,
        MaterialId = materialId,
        LocationId = locationId,
        MaterialLotId = materialLotId,
        UnitCost = frozenUnitCost ?? balance?.AverageUnitCost ?? 0
      }, tx, cancellationToken: ct));

    await InsertTransactionAsync(conn, tx, rfc, balanceId, locationId, materialId,
      transactionType, quantityDelta, quantityAfter, "InventoryAdjustment", adjustmentId, notes, userName, ct);
  }

  /// <summary>
  /// Devuelve el saldo utilizable de la ubicación. Si existe uno dado de baja lo reactiva:
  /// <c>UX_StockBalance_LocationMaterial</c> es único, así que insertar otro reventaría.
  /// </summary>
  private static async Task<int> EnsureBalanceAsync(
    DbConnection conn, DbTransaction tx, string rfc, int locationId, int materialId, CancellationToken ct)
  {
    var removedId = await conn.ExecuteScalarAsync<int?>(new CommandDefinition(
      """
      SELECT Id FROM logistica.StockBalance WITH (UPDLOCK,HOLDLOCK)
      WHERE Rfc=@Rfc AND LocationId=@LocationId AND MaterialId=@MaterialId AND ISNULL(IsRemoved,0)=1;
      """, new { Rfc = rfc, LocationId = locationId, MaterialId = materialId }, tx, cancellationToken: ct));
    if (removedId.HasValue)
    {
      await conn.ExecuteAsync(new CommandDefinition(
        """
        UPDATE logistica.StockBalance
        SET IsRemoved=0,RemovedAt=NULL,RemovedBy=NULL,UpdatedAt=SYSUTCDATETIME()
        WHERE Rfc=@Rfc AND Id=@Id;
        """, new { Rfc = rfc, Id = removedId.Value }, tx, cancellationToken: ct));
      return removedId.Value;
    }
    return await conn.ExecuteScalarAsync<int>(new CommandDefinition(
      """
      INSERT INTO logistica.StockBalance (Rfc,LocationId,MaterialId,Quantity,ReservedQuantity,AverageUnitCost)
      VALUES (@Rfc,@LocationId,@MaterialId,0,0,0);
      SELECT CAST(SCOPE_IDENTITY() AS int);
      """, new { Rfc = rfc, LocationId = locationId, MaterialId = materialId }, tx, cancellationToken: ct));
  }

  internal static Task InsertTransactionAsync(
    DbConnection conn, DbTransaction tx, string rfc, int balanceId, int locationId, int materialId,
    string type, decimal delta, decimal quantityAfter, string referenceType, long referenceId,
    string notes, string userName, CancellationToken ct)
    => conn.ExecuteAsync(new CommandDefinition(
      """
      INSERT INTO logistica.StockTransaction
        (Rfc,StockBalanceId,LocationId,MaterialId,TransactionType,QuantityDelta,QuantityAfter,ReferenceType,ReferenceId,Notes,PerformedBy)
      VALUES
        (@Rfc,@BalanceId,@LocationId,@MaterialId,@Type,@Delta,@QuantityAfter,@ReferenceType,@ReferenceId,@Notes,@UserName);
      """,
      new
      {
        Rfc = rfc, BalanceId = balanceId, LocationId = locationId, MaterialId = materialId, Type = type,
        Delta = delta, QuantityAfter = quantityAfter, ReferenceType = referenceType,
        ReferenceId = checked((int)referenceId), Notes = notes.Trim(), UserName = userName
      }, tx, cancellationToken: ct));

  internal static Task<MovementMaterialRow?> LoadMaterialAsync(
    DbConnection conn, DbTransaction tx, string rfc, int materialId, CancellationToken ct)
    => conn.QuerySingleOrDefaultAsync<MovementMaterialRow>(new CommandDefinition(
      "SELECT Id,MaterialCode,TrackLots FROM logistica.Material WITH (UPDLOCK,HOLDLOCK) WHERE Rfc=@Rfc AND Id=@MaterialId AND IsActive=1;",
      new { Rfc = rfc, MaterialId = materialId }, tx, cancellationToken: ct));

  internal static Task<MovementBalanceRow?> LoadBalanceAsync(
    DbConnection conn, DbTransaction tx, string rfc, int locationId, int materialId, CancellationToken ct)
    => conn.QuerySingleOrDefaultAsync<MovementBalanceRow>(new CommandDefinition(
      "SELECT Id,Quantity,ReservedQuantity,AverageUnitCost FROM logistica.StockBalance WITH (UPDLOCK,HOLDLOCK) WHERE Rfc=@Rfc AND LocationId=@LocationId AND MaterialId=@MaterialId AND ISNULL(IsRemoved,0)=0;",
      new { Rfc = rfc, LocationId = locationId, MaterialId = materialId }, tx, cancellationToken: ct));

  internal static Task<MovementLotRow?> LoadLotAsync(
    DbConnection conn, DbTransaction tx, string rfc, int locationId, int materialId, long lotId, CancellationToken ct)
    => conn.QuerySingleOrDefaultAsync<MovementLotRow>(new CommandDefinition(
      """
      SELECT lotInfo.Id,lotInfo.LotCode,lotBalance.Quantity,lotBalance.ReservedQuantity
      FROM logistica.MaterialLot lotInfo WITH (UPDLOCK,HOLDLOCK)
      JOIN logistica.LotBalance lotBalance WITH (UPDLOCK,HOLDLOCK)
        ON lotBalance.Rfc=lotInfo.Rfc AND lotBalance.MaterialLotId=lotInfo.Id
      WHERE lotInfo.Rfc=@Rfc AND lotInfo.Id=@LotId AND lotInfo.MaterialId=@MaterialId
        AND lotBalance.LocationId=@LocationId AND lotInfo.IsBlocked=0;
      """, new { Rfc = rfc, LocationId = locationId, MaterialId = materialId, LotId = lotId }, tx, cancellationToken: ct));

  internal static Task<MovementLotRow?> LoadMaterialLotAsync(
    DbConnection conn, DbTransaction tx, string rfc, int materialId, long lotId, CancellationToken ct)
    => conn.QuerySingleOrDefaultAsync<MovementLotRow>(new CommandDefinition(
      "SELECT Id,LotCode FROM logistica.MaterialLot WITH (UPDLOCK,HOLDLOCK) WHERE Rfc=@Rfc AND Id=@LotId AND MaterialId=@MaterialId AND IsBlocked=0;",
      new { Rfc = rfc, MaterialId = materialId, LotId = lotId }, tx, cancellationToken: ct));
}

internal sealed class MovementMaterialRow { public int Id { get; set; } public string MaterialCode { get; set; } = string.Empty; public bool TrackLots { get; set; } }
internal sealed class MovementBalanceRow { public int Id { get; set; } public decimal Quantity { get; set; } public decimal ReservedQuantity { get; set; } public decimal AverageUnitCost { get; set; } }
internal sealed class MovementLotRow { public long Id { get; set; } public string LotCode { get; set; } = string.Empty; public decimal Quantity { get; set; } public decimal ReservedQuantity { get; set; } }
