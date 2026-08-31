using System.Data;
using System.Data.Common;
using Dapper;
using OrionERP.Application.Common;
using OrionERP.Application.Features.Logistica.Shared;
using OrionERP.Application.Features.Logistica.Stock;

namespace OrionERP.Infrastructure.Features.Logistica.Stock;

public sealed class InventoryMovementService : IInventoryMovementService
{
  private const int MaxEvidenceBytes = 10 * 1024 * 1024;
  private readonly IDbConnectionFactory _connectionFactory;

  public InventoryMovementService(IDbConnectionFactory connectionFactory)
  {
    _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
  }

  public async Task<InventoryMovementWorkspaceDto> GetWorkspaceAsync(string rfc, CancellationToken ct = default)
  {
    var normalizedRfc = LogisticsRfc.Require(rfc);
    const string sql =
      """
      SELECT Id,LocationCode AS Code,LocationName AS [Name]
      FROM logistica.Location
      WHERE Rfc=@Rfc AND IsActive=1 AND IsInventoryEnabled=1
      ORDER BY LocationName,LocationCode;

      SELECT balanceInfo.MaterialId,balanceInfo.LocationId,material.MaterialCode,
             material.[Description] AS MaterialName,unitInfo.UnitCode,
             balanceInfo.Quantity,balanceInfo.ReservedQuantity,balanceInfo.AverageUnitCost,material.TrackLots
      FROM logistica.StockBalance balanceInfo
      JOIN logistica.Material material ON material.Rfc=balanceInfo.Rfc AND material.Id=balanceInfo.MaterialId
      JOIN logistica.Location locationInfo ON locationInfo.Rfc=balanceInfo.Rfc AND locationInfo.Id=balanceInfo.LocationId
      LEFT JOIN logistica.UnitOfMeasure unitInfo ON unitInfo.Id=material.BaseUnitId
      WHERE balanceInfo.Rfc=@Rfc AND ISNULL(balanceInfo.IsRemoved,0)=0
        AND material.IsActive=1 AND locationInfo.IsActive=1 AND locationInfo.IsInventoryEnabled=1
      ORDER BY material.[Description],material.MaterialCode,locationInfo.LocationName;

      SELECT lotInfo.Id,lotInfo.MaterialId,lotBalance.LocationId,lotInfo.LotCode,lotInfo.ExpirationDate,
             lotBalance.Quantity,lotBalance.ReservedQuantity
      FROM logistica.MaterialLot lotInfo
      JOIN logistica.LotBalance lotBalance ON lotBalance.Rfc=lotInfo.Rfc AND lotBalance.MaterialLotId=lotInfo.Id
      WHERE lotInfo.Rfc=@Rfc AND lotInfo.[Status]='Active'
        AND lotBalance.Quantity-lotBalance.ReservedQuantity>0
      ORDER BY COALESCE(lotInfo.ExpirationDate,'9999-12-31'),lotInfo.LotCode;
      """;
    using var conn = CreateConnection();
    using var multi = await conn.QueryMultipleAsync(new CommandDefinition(sql, new { Rfc = normalizedRfc }, cancellationToken: ct));
    return new InventoryMovementWorkspaceDto
    {
      Locations = (await multi.ReadAsync<InventoryLocationOptionDto>()).AsList(),
      Balances = (await multi.ReadAsync<InventoryBalanceOptionDto>()).AsList(),
      Lots = (await multi.ReadAsync<InventoryLotOptionDto>()).AsList()
    };
  }

  public async Task<LogisticsCommandResult> PostTransferAsync(
    InventoryTransferCreateRequest request,
    string userName,
    CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(request);
    var rfc = LogisticsRfc.Require(request.Rfc);
    if (request.FromLocationId <= 0 || request.ToLocationId <= 0 || request.FromLocationId == request.ToLocationId)
      return LogisticsCommandResult.Fail("Selecciona ubicaciones de origen y destino diferentes.");
    if (string.IsNullOrWhiteSpace(request.TransferCode) || string.IsNullOrWhiteSpace(request.Reason) || request.Lines.Count == 0)
      return LogisticsCommandResult.Fail("El traspaso requiere código, motivo y al menos una partida.");

    var lines = request.Lines
      .GroupBy(line => (line.MaterialId, line.MaterialLotId))
      .Select(group => new InventoryTransferLineRequest
      {
        MaterialId = group.Key.MaterialId,
        MaterialLotId = group.Key.MaterialLotId,
        Quantity = group.Sum(line => line.Quantity)
      })
      .ToList();
    if (lines.Any(line => line.MaterialId <= 0 || line.Quantity <= 0))
      return LogisticsCommandResult.Fail("Todas las partidas deben tener material y cantidad positiva.");

    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    await using var tx = await conn.BeginTransactionAsync(IsolationLevel.Serializable, ct);
    try
    {
      var existing = await conn.ExecuteScalarAsync<long?>(new CommandDefinition(
        "SELECT Id FROM logistica.InventoryTransfer WITH (UPDLOCK,HOLDLOCK) WHERE Rfc=@Rfc AND TransferCode=@Code;",
        new { Rfc = rfc, Code = request.TransferCode.Trim().ToUpperInvariant() }, tx, cancellationToken: ct));
      if (existing.HasValue)
      {
        await tx.CommitAsync(ct);
        return LogisticsCommandResult.Ok("El traspaso ya había sido registrado.", checked((int)existing.Value));
      }

      var locationCount = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
        """
        SELECT COUNT(*) FROM logistica.Location WITH (UPDLOCK,HOLDLOCK)
        WHERE Rfc=@Rfc AND Id IN @LocationIds AND IsActive=1 AND IsInventoryEnabled=1;
        """, new { Rfc = rfc, LocationIds = new[] { request.FromLocationId, request.ToLocationId } }, tx, cancellationToken: ct));
      if (locationCount != 2)
      {
        await tx.RollbackAsync(ct);
        return LogisticsCommandResult.Fail("Una ubicación no pertenece al RFC o no está habilitada para inventario.");
      }

      var transferId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
        """
        INSERT INTO logistica.InventoryTransfer
          (Rfc,TransferCode,FromLocationId,ToLocationId,[Status],Reason,CreatedBy)
        VALUES
          (@Rfc,@Code,@FromLocationId,@ToLocationId,'Draft',@Reason,@UserName);
        SELECT CAST(SCOPE_IDENTITY() AS bigint);
        """, new
        {
          Rfc = rfc,
          Code = request.TransferCode.Trim().ToUpperInvariant(),
          request.FromLocationId,
          request.ToLocationId,
          Reason = request.Reason.Trim(),
          UserName = userName
        }, tx, cancellationToken: ct));

      foreach (var line in lines)
      {
        var material = await LoadMaterialAsync(conn, tx, rfc, line.MaterialId, ct);
        if (material is null) throw new InvalidOperationException("Un material no pertenece al RFC o está inactivo.");
        if (material.TrackLots && !line.MaterialLotId.HasValue)
          throw new InvalidOperationException($"El material {material.MaterialCode} requiere seleccionar lote.");

        var source = await LoadBalanceAsync(conn, tx, rfc, request.FromLocationId, line.MaterialId, ct)
          ?? throw new InvalidOperationException($"No existe saldo de {material.MaterialCode} en el origen.");
        if (source.Quantity - source.ReservedQuantity < line.Quantity)
          throw new InvalidOperationException($"El disponible de {material.MaterialCode} no alcanza para el traspaso.");

        MovementLotRow? sourceLot = null;
        if (line.MaterialLotId.HasValue)
        {
          sourceLot = await LoadLotAsync(conn, tx, rfc, request.FromLocationId, line.MaterialId, line.MaterialLotId.Value, ct)
            ?? throw new InvalidOperationException($"El lote de {material.MaterialCode} no existe en el origen.");
          if (sourceLot.Quantity - sourceLot.ReservedQuantity < line.Quantity)
            throw new InvalidOperationException($"El disponible del lote {sourceLot.LotCode} no alcanza.");
        }

        var destination = await LoadBalanceAsync(conn, tx, rfc, request.ToLocationId, line.MaterialId, ct);
        var destinationAfter = (destination?.Quantity ?? 0) + line.Quantity;
        var destinationCost = destination is null || destinationAfter == 0
          ? source.AverageUnitCost
          : ((destination.Quantity * destination.AverageUnitCost) + (line.Quantity * source.AverageUnitCost)) / destinationAfter;
        var destinationBalanceId = destination?.Id ?? await conn.ExecuteScalarAsync<int>(new CommandDefinition(
          """
          INSERT INTO logistica.StockBalance
            (Rfc,LocationId,MaterialId,Quantity,ReservedQuantity,AverageUnitCost)
          VALUES
            (@Rfc,@LocationId,@MaterialId,0,0,@AverageUnitCost);
          SELECT CAST(SCOPE_IDENTITY() AS int);
          """, new { Rfc = rfc, LocationId = request.ToLocationId, line.MaterialId, source.AverageUnitCost }, tx, cancellationToken: ct));

        await conn.ExecuteAsync(new CommandDefinition(
          """
          UPDATE logistica.StockBalance
          SET Quantity=Quantity-@Quantity,UpdatedAt=SYSUTCDATETIME()
          WHERE Rfc=@Rfc AND Id=@SourceBalanceId;
          UPDATE logistica.StockBalance
          SET Quantity=Quantity+@Quantity,AverageUnitCost=@DestinationCost,UpdatedAt=SYSUTCDATETIME()
          WHERE Rfc=@Rfc AND Id=@DestinationBalanceId;
          INSERT INTO logistica.InventoryTransferLine (Rfc,TransferId,MaterialId,MaterialLotId,Quantity)
          VALUES (@Rfc,@TransferId,@MaterialId,@MaterialLotId,@Quantity);
          """, new
          {
            Rfc = rfc,
            Quantity = line.Quantity,
            SourceBalanceId = source.Id,
            DestinationBalanceId = destinationBalanceId,
            DestinationCost = decimal.Round(destinationCost, 6),
            TransferId = transferId,
            line.MaterialId,
            line.MaterialLotId
          }, tx, cancellationToken: ct));

        if (sourceLot is not null)
        {
          await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE logistica.LotBalance SET Quantity=Quantity-@Quantity,UpdatedAt=SYSUTCDATETIME()
            WHERE Rfc=@Rfc AND MaterialLotId=@LotId AND LocationId=@FromLocationId;
            IF EXISTS (SELECT 1 FROM logistica.LotBalance WITH (UPDLOCK,HOLDLOCK) WHERE Rfc=@Rfc AND MaterialLotId=@LotId AND LocationId=@ToLocationId)
              UPDATE logistica.LotBalance SET Quantity=Quantity+@Quantity,UpdatedAt=SYSUTCDATETIME()
              WHERE Rfc=@Rfc AND MaterialLotId=@LotId AND LocationId=@ToLocationId;
            ELSE
              INSERT INTO logistica.LotBalance (Rfc,MaterialLotId,MaterialId,LocationId,Quantity,ReservedQuantity)
              VALUES (@Rfc,@LotId,@MaterialId,@ToLocationId,@Quantity,0);
            """, new
            {
              Rfc = rfc,
              Quantity = line.Quantity,
              LotId = sourceLot.Id,
              FromLocationId = request.FromLocationId,
              ToLocationId = request.ToLocationId,
              line.MaterialId
            }, tx, cancellationToken: ct));
        }

        var referenceId = checked((int)transferId);
        await InsertTransactionAsync(conn, tx, rfc, source.Id, request.FromLocationId, line.MaterialId,
          "TransferOut", -line.Quantity, source.Quantity - line.Quantity, "InventoryTransfer", referenceId, request.Reason, userName, ct);
        await InsertTransactionAsync(conn, tx, rfc, destinationBalanceId, request.ToLocationId, line.MaterialId,
          "TransferIn", line.Quantity, destinationAfter, "InventoryTransfer", referenceId, request.Reason, userName, ct);
      }

      await conn.ExecuteAsync(new CommandDefinition(
        """
        UPDATE logistica.InventoryTransfer
        SET [Status]='Posted',PostedAt=SYSUTCDATETIME(),PostedBy=@UserName
        WHERE Rfc=@Rfc AND Id=@Id;
        """, new { Rfc = rfc, Id = transferId, UserName = userName }, tx, cancellationToken: ct));
      await tx.CommitAsync(ct);
      return LogisticsCommandResult.Ok("El traspaso fue aplicado de forma atómica.", checked((int)transferId));
    }
    catch
    {
      await tx.RollbackAsync(ct);
      throw;
    }
  }

  public async Task<LogisticsCommandResult> PostAdjustmentAsync(
    InventoryAdjustmentCreateRequest request,
    string userName,
    CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(request);
    var rfc = LogisticsRfc.Require(request.Rfc);
    if (string.IsNullOrWhiteSpace(request.AdjustmentCode) || string.IsNullOrWhiteSpace(request.ReasonCode) || string.IsNullOrWhiteSpace(request.Reason))
      return LogisticsCommandResult.Fail("El ajuste requiere código y motivo.");
    if (string.IsNullOrWhiteSpace(request.AuthorizedBy))
      return LogisticsCommandResult.Fail("El ajuste requiere autorización de supervisor.");
    if (request.Evidence.Length == 0 || string.IsNullOrWhiteSpace(request.EvidenceFileName) || request.Evidence.Length > MaxEvidenceBytes)
      return LogisticsCommandResult.Fail("Adjunta evidencia válida de hasta 10 MB.");
    if (request.Lines.Count == 0 || request.Lines.Any(line => line.MaterialId <= 0 || line.LocationId <= 0 || line.QuantityDelta == 0))
      return LogisticsCommandResult.Fail("El ajuste requiere partidas con una diferencia distinta de cero.");
    var adjustmentType = request.AdjustmentType.Trim().ToLowerInvariant() switch
    {
      "waste" or "merma" => "Waste",
      "adjustment" or "ajuste" => "Adjustment",
      _ => throw new InvalidOperationException("Tipo de ajuste no válido.")
    };
    var lines = request.Lines
      .GroupBy(line => (line.MaterialId, line.LocationId, line.MaterialLotId))
      .Select(group => new InventoryAdjustmentLineRequest
      {
        MaterialId = group.Key.MaterialId,
        LocationId = group.Key.LocationId,
        MaterialLotId = group.Key.MaterialLotId,
        QuantityDelta = group.Sum(line => line.QuantityDelta)
      })
      .Where(line => line.QuantityDelta != 0)
      .ToList();

    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    await using var tx = await conn.BeginTransactionAsync(IsolationLevel.Serializable, ct);
    try
    {
      var existing = await conn.ExecuteScalarAsync<long?>(new CommandDefinition(
        "SELECT Id FROM logistica.InventoryAdjustment WITH (UPDLOCK,HOLDLOCK) WHERE Rfc=@Rfc AND AdjustmentCode=@Code;",
        new { Rfc = rfc, Code = request.AdjustmentCode.Trim().ToUpperInvariant() }, tx, cancellationToken: ct));
      if (existing.HasValue)
      {
        await tx.CommitAsync(ct);
        return LogisticsCommandResult.Ok("El ajuste ya había sido registrado.", checked((int)existing.Value));
      }

      var adjustmentId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
        """
        INSERT INTO logistica.InventoryAdjustment
          (Rfc,AdjustmentCode,AdjustmentType,[Status],ReasonCode,Reason,Evidence,EvidenceFileName,CreatedBy)
        VALUES
          (@Rfc,@Code,@Type,'Draft',@ReasonCode,@Reason,@Evidence,@EvidenceFileName,@UserName);
        SELECT CAST(SCOPE_IDENTITY() AS bigint);
        """, new
        {
          Rfc = rfc,
          Code = request.AdjustmentCode.Trim().ToUpperInvariant(),
          Type = adjustmentType,
          ReasonCode = request.ReasonCode.Trim().ToUpperInvariant(),
          Reason = request.Reason.Trim(),
          request.Evidence,
          EvidenceFileName = request.EvidenceFileName.Trim(),
          UserName = userName
        }, tx, cancellationToken: ct));

      foreach (var line in lines)
      {
        var material = await LoadMaterialAsync(conn, tx, rfc, line.MaterialId, ct)
          ?? throw new InvalidOperationException("Un material no pertenece al RFC o está inactivo.");
        if (material.TrackLots && !line.MaterialLotId.HasValue)
          throw new InvalidOperationException($"El material {material.MaterialCode} requiere seleccionar lote.");
        if (!await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
          "SELECT CAST(CASE WHEN EXISTS(SELECT 1 FROM logistica.Location WITH (UPDLOCK,HOLDLOCK) WHERE Rfc=@Rfc AND Id=@LocationId AND IsActive=1 AND IsInventoryEnabled=1) THEN 1 ELSE 0 END AS bit);",
          new { Rfc = rfc, line.LocationId }, tx, cancellationToken: ct)))
          throw new InvalidOperationException("Una ubicación no pertenece al RFC o está inactiva.");

        var balance = await LoadBalanceAsync(conn, tx, rfc, line.LocationId, line.MaterialId, ct);
        if (balance is null && line.QuantityDelta < 0)
          throw new InvalidOperationException($"No existe saldo de {material.MaterialCode} para descontar.");
        if (balance is not null && line.QuantityDelta < 0 && balance.Quantity - balance.ReservedQuantity < -line.QuantityDelta)
          throw new InvalidOperationException($"El ajuste excede el disponible de {material.MaterialCode}.");
        var balanceId = balance?.Id ?? await conn.ExecuteScalarAsync<int>(new CommandDefinition(
          """
          INSERT INTO logistica.StockBalance (Rfc,LocationId,MaterialId,Quantity,ReservedQuantity,AverageUnitCost)
          VALUES (@Rfc,@LocationId,@MaterialId,0,0,0);
          SELECT CAST(SCOPE_IDENTITY() AS int);
          """, new { Rfc = rfc, line.LocationId, line.MaterialId }, tx, cancellationToken: ct));
        var quantityAfter = (balance?.Quantity ?? 0) + line.QuantityDelta;

        if (line.MaterialLotId.HasValue)
        {
          var lot = await LoadMaterialLotAsync(conn, tx, rfc, line.MaterialId, line.MaterialLotId.Value, ct)
            ?? throw new InvalidOperationException($"El lote de {material.MaterialCode} no pertenece al RFC/material.");
          var lotBalance = await LoadLotAsync(conn, tx, rfc, line.LocationId, line.MaterialId, line.MaterialLotId.Value, ct);
          if (lotBalance is null && line.QuantityDelta < 0)
            throw new InvalidOperationException($"El lote {lot.LotCode} no tiene saldo en la ubicación.");
          if (lotBalance is not null && line.QuantityDelta < 0 && lotBalance.Quantity - lotBalance.ReservedQuantity < -line.QuantityDelta)
            throw new InvalidOperationException($"El ajuste excede el disponible del lote {lot.LotCode}.");
          if (lotBalance is null)
          {
            await conn.ExecuteAsync(new CommandDefinition(
              "INSERT INTO logistica.LotBalance (Rfc,MaterialLotId,MaterialId,LocationId,Quantity,ReservedQuantity) VALUES (@Rfc,@LotId,@MaterialId,@LocationId,@Quantity,0);",
              new { Rfc = rfc, LotId = line.MaterialLotId.Value, line.MaterialId, line.LocationId, Quantity = line.QuantityDelta }, tx, cancellationToken: ct));
          }
          else
          {
            await conn.ExecuteAsync(new CommandDefinition(
              "UPDATE logistica.LotBalance SET Quantity=Quantity+@Delta,UpdatedAt=SYSUTCDATETIME() WHERE Rfc=@Rfc AND MaterialLotId=@LotId AND LocationId=@LocationId;",
              new { Rfc = rfc, Delta = line.QuantityDelta, LotId = line.MaterialLotId.Value, line.LocationId }, tx, cancellationToken: ct));
          }
        }

        await conn.ExecuteAsync(new CommandDefinition(
          """
          UPDATE logistica.StockBalance SET Quantity=Quantity+@Delta,UpdatedAt=SYSUTCDATETIME()
          WHERE Rfc=@Rfc AND Id=@BalanceId;
          INSERT INTO logistica.InventoryAdjustmentLine
            (Rfc,AdjustmentId,MaterialId,LocationId,MaterialLotId,QuantityDelta,FrozenUnitCost)
          VALUES
            (@Rfc,@AdjustmentId,@MaterialId,@LocationId,@MaterialLotId,@Delta,@UnitCost);
          """, new
          {
            Rfc = rfc,
            Delta = line.QuantityDelta,
            BalanceId = balanceId,
            AdjustmentId = adjustmentId,
            line.MaterialId,
            line.LocationId,
            line.MaterialLotId,
            UnitCost = balance?.AverageUnitCost ?? 0
          }, tx, cancellationToken: ct));
        await InsertTransactionAsync(conn, tx, rfc, balanceId, line.LocationId, line.MaterialId,
          adjustmentType, line.QuantityDelta, quantityAfter, "InventoryAdjustment", checked((int)adjustmentId), request.Reason, userName, ct);
      }

      await conn.ExecuteAsync(new CommandDefinition(
        """
        UPDATE logistica.InventoryAdjustment
        SET [Status]='Approved',ApprovedAt=SYSUTCDATETIME(),ApprovedBy=@AuthorizedBy
        WHERE Rfc=@Rfc AND Id=@Id;
        """, new { Rfc = rfc, Id = adjustmentId, AuthorizedBy = request.AuthorizedBy.Trim() }, tx, cancellationToken: ct));
      await tx.CommitAsync(ct);
      return LogisticsCommandResult.Ok("El ajuste fue aplicado y quedó respaldado con evidencia.", checked((int)adjustmentId));
    }
    catch
    {
      await tx.RollbackAsync(ct);
      throw;
    }
  }

  private static Task InsertTransactionAsync(
    DbConnection conn,
    DbTransaction tx,
    string rfc,
    int balanceId,
    int locationId,
    int materialId,
    string type,
    decimal delta,
    decimal quantityAfter,
    string referenceType,
    int referenceId,
    string notes,
    string userName,
    CancellationToken ct)
    => conn.ExecuteAsync(new CommandDefinition(
      """
      INSERT INTO logistica.StockTransaction
        (Rfc,StockBalanceId,LocationId,MaterialId,TransactionType,QuantityDelta,QuantityAfter,ReferenceType,ReferenceId,Notes,PerformedBy)
      VALUES
        (@Rfc,@BalanceId,@LocationId,@MaterialId,@Type,@Delta,@QuantityAfter,@ReferenceType,@ReferenceId,@Notes,@UserName);
      """, new { Rfc = rfc, BalanceId = balanceId, LocationId = locationId, MaterialId = materialId, Type = type, Delta = delta, QuantityAfter = quantityAfter, ReferenceType = referenceType, ReferenceId = referenceId, Notes = notes.Trim(), UserName = userName }, tx, cancellationToken: ct));

  private static Task<MovementMaterialRow?> LoadMaterialAsync(DbConnection conn, DbTransaction tx, string rfc, int materialId, CancellationToken ct)
    => conn.QuerySingleOrDefaultAsync<MovementMaterialRow>(new CommandDefinition(
      "SELECT Id,MaterialCode,TrackLots FROM logistica.Material WITH (UPDLOCK,HOLDLOCK) WHERE Rfc=@Rfc AND Id=@MaterialId AND IsActive=1;",
      new { Rfc = rfc, MaterialId = materialId }, tx, cancellationToken: ct));

  private static Task<MovementBalanceRow?> LoadBalanceAsync(DbConnection conn, DbTransaction tx, string rfc, int locationId, int materialId, CancellationToken ct)
    => conn.QuerySingleOrDefaultAsync<MovementBalanceRow>(new CommandDefinition(
      "SELECT Id,Quantity,ReservedQuantity,AverageUnitCost FROM logistica.StockBalance WITH (UPDLOCK,HOLDLOCK) WHERE Rfc=@Rfc AND LocationId=@LocationId AND MaterialId=@MaterialId AND ISNULL(IsRemoved,0)=0;",
      new { Rfc = rfc, LocationId = locationId, MaterialId = materialId }, tx, cancellationToken: ct));

  private static Task<MovementLotRow?> LoadLotAsync(DbConnection conn, DbTransaction tx, string rfc, int locationId, int materialId, long lotId, CancellationToken ct)
    => conn.QuerySingleOrDefaultAsync<MovementLotRow>(new CommandDefinition(
      """
      SELECT lotInfo.Id,lotInfo.LotCode,lotBalance.Quantity,lotBalance.ReservedQuantity
      FROM logistica.MaterialLot lotInfo WITH (UPDLOCK,HOLDLOCK)
      JOIN logistica.LotBalance lotBalance WITH (UPDLOCK,HOLDLOCK)
        ON lotBalance.Rfc=lotInfo.Rfc AND lotBalance.MaterialLotId=lotInfo.Id
      WHERE lotInfo.Rfc=@Rfc AND lotInfo.Id=@LotId AND lotInfo.MaterialId=@MaterialId
        AND lotBalance.LocationId=@LocationId AND lotInfo.[Status]='Active';
      """, new { Rfc = rfc, LocationId = locationId, MaterialId = materialId, LotId = lotId }, tx, cancellationToken: ct));

  private static Task<MovementLotRow?> LoadMaterialLotAsync(DbConnection conn, DbTransaction tx, string rfc, int materialId, long lotId, CancellationToken ct)
    => conn.QuerySingleOrDefaultAsync<MovementLotRow>(new CommandDefinition(
      "SELECT Id,LotCode FROM logistica.MaterialLot WITH (UPDLOCK,HOLDLOCK) WHERE Rfc=@Rfc AND Id=@LotId AND MaterialId=@MaterialId AND [Status]='Active';",
      new { Rfc = rfc, MaterialId = materialId, LotId = lotId }, tx, cancellationToken: ct));

  private DbConnection CreateConnection()
    => _connectionFactory.Create() as DbConnection
      ?? throw new InvalidOperationException("La fábrica de conexiones no devolvió una DbConnection.");

  private sealed class MovementMaterialRow { public int Id { get; set; } public string MaterialCode { get; set; } = string.Empty; public bool TrackLots { get; set; } }
  private sealed class MovementBalanceRow { public int Id { get; set; } public decimal Quantity { get; set; } public decimal ReservedQuantity { get; set; } public decimal AverageUnitCost { get; set; } }
  private sealed class MovementLotRow { public long Id { get; set; } public string LotCode { get; set; } = string.Empty; public decimal Quantity { get; set; } public decimal ReservedQuantity { get; set; } }
}
