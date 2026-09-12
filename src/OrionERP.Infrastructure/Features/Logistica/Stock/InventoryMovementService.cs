using System.Data;
using System.Data.Common;
using Dapper;
using OrionERP.Application.Common;
using OrionERP.Application.Features.Logistica.Shared;
using OrionERP.Application.Features.Logistica.Stock;
using OrionERP.Application.Features.Reservaciones;
using OrionERP.Infrastructure.Features.Logistica.Shared;

namespace OrionERP.Infrastructure.Features.Logistica.Stock;

public sealed class InventoryMovementService : IInventoryMovementService
{
  private const int MaxEvidenceBytes = 10 * 1024 * 1024;
  private readonly IDbConnectionFactory _connectionFactory;
  private readonly IHospitalityScopeAccessor? _hospitalityScope;

  public InventoryMovementService(IDbConnectionFactory connectionFactory, IHospitalityScopeAccessor? hospitalityScope = null)
  {
    _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    _hospitalityScope = hospitalityScope;
  }

  public async Task<InventoryMovementWorkspaceDto> GetWorkspaceAsync(string rfc, CancellationToken ct = default)
  {
    var normalizedRfc = LogisticsRfc.Require(rfc);
    var sql = InventoryOptionsQuery.Sql;
    await using var conn = await LogisticsLocationScope.OpenAsync(_connectionFactory,_hospitalityScope,ct);
    await LogisticsLocationScope.EnsureRfcAsync(conn,null,normalizedRfc,ct);
    using var multi = await conn.QueryMultipleAsync(new CommandDefinition(sql, new { Rfc = normalizedRfc }, cancellationToken: ct));
    return await InventoryOptionsQuery.ReadAsync(multi);
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

    await using var conn = await LogisticsLocationScope.OpenAsync(_connectionFactory,_hospitalityScope,ct);
    await LogisticsLocationScope.EnsureRfcAsync(conn,null,rfc,ct);
    await using var tx = await conn.BeginTransactionAsync(IsolationLevel.Serializable, ct);
    try
    {
      await LogisticsLocationScope.EnsureLocationAsync(conn,tx,request.FromLocationId,ct);
      await LogisticsLocationScope.EnsureLocationAsync(conn,tx,request.ToLocationId,ct);
      await conn.ExecuteAsync(new CommandDefinition($$"""
        IF EXISTS (SELECT 1 FROM logistica.InventoryTransfer existing WITH (UPDLOCK,HOLDLOCK)
          WHERE existing.Rfc=@Rfc AND existing.TransferCode=@Code
            AND (NOT {{LogisticsLocationScope.ForLocationIdSql("existing.FromLocationId","existing.Rfc")}}
              OR NOT {{LogisticsLocationScope.ForLocationIdSql("existing.ToLocationId","existing.Rfc")}}))
          THROW 51932,'El traspaso existente pertenece a otra sede.',1;
        """,new { Rfc=rfc,Code=request.TransferCode.Trim().ToUpperInvariant() },tx,cancellationToken:ct));
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
        var material = await InventoryAdjustmentWriter.LoadMaterialAsync(conn, tx, rfc, line.MaterialId, ct);
        if (material is null) throw new InvalidOperationException("Un material no pertenece al RFC o está inactivo.");
        if (material.TrackLots && !line.MaterialLotId.HasValue)
          throw new InvalidOperationException($"El material {material.MaterialCode} requiere seleccionar lote.");

        var source = await InventoryAdjustmentWriter.LoadBalanceAsync(conn, tx, rfc, request.FromLocationId, line.MaterialId, ct)
          ?? throw new InvalidOperationException($"No existe saldo de {material.MaterialCode} en el origen.");
        if (source.Quantity - source.ReservedQuantity < line.Quantity)
          throw new InvalidOperationException($"El disponible de {material.MaterialCode} no alcanza para el traspaso.");

        MovementLotRow? sourceLot = null;
        if (line.MaterialLotId.HasValue)
        {
          sourceLot = await InventoryAdjustmentWriter.LoadLotAsync(conn, tx, rfc, request.FromLocationId, line.MaterialId, line.MaterialLotId.Value, ct)
            ?? throw new InvalidOperationException($"El lote de {material.MaterialCode} no existe en el origen.");
          if (sourceLot.Quantity - sourceLot.ReservedQuantity < line.Quantity)
            throw new InvalidOperationException($"El disponible del lote {sourceLot.LotCode} no alcanza.");
        }

        var destination = await InventoryAdjustmentWriter.LoadBalanceAsync(conn, tx, rfc, request.ToLocationId, line.MaterialId, ct);
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

        await InventoryAdjustmentWriter.InsertTransactionAsync(conn, tx, rfc, source.Id, request.FromLocationId, line.MaterialId,
          "TransferOut", -line.Quantity, source.Quantity - line.Quantity, "InventoryTransfer", transferId, request.Reason, userName, ct);
        await InventoryAdjustmentWriter.InsertTransactionAsync(conn, tx, rfc, destinationBalanceId, request.ToLocationId, line.MaterialId,
          "TransferIn", line.Quantity, destinationAfter, "InventoryTransfer", transferId, request.Reason, userName, ct);
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
    if (lines.Count==0) return LogisticsCommandResult.Fail("El ajuste requiere una diferencia neta distinta de cero.");

    await using var conn = await LogisticsLocationScope.OpenAsync(_connectionFactory,_hospitalityScope,ct);
    await LogisticsLocationScope.EnsureRfcAsync(conn,null,rfc,ct);
    await using var tx = await conn.BeginTransactionAsync(IsolationLevel.Serializable, ct);
    try
    {
      foreach(var locationId in lines.Select(line=>line.LocationId).Distinct())
        await LogisticsLocationScope.EnsureLocationAsync(conn,tx,locationId,ct);
      await conn.ExecuteAsync(new CommandDefinition($$"""
        IF EXISTS (SELECT 1 FROM logistica.InventoryAdjustment existing WITH (UPDLOCK,HOLDLOCK)
          JOIN logistica.InventoryAdjustmentLine line WITH (HOLDLOCK) ON line.Rfc=existing.Rfc AND line.AdjustmentId=existing.Id
          WHERE existing.Rfc=@Rfc AND existing.AdjustmentCode=@Code
            AND NOT {{LogisticsLocationScope.ForLocationIdSql("line.LocationId","line.Rfc")}})
          THROW 51932,'El ajuste existente contiene ubicaciones de otra sede.',1;
        """,new { Rfc=rfc,Code=request.AdjustmentCode.Trim().ToUpperInvariant() },tx,cancellationToken:ct));
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
        await InventoryAdjustmentWriter.ApplyLineAsync(conn, tx, rfc, adjustmentId, adjustmentType, "El ajuste",
          line.MaterialId, line.LocationId, line.MaterialLotId, line.QuantityDelta,
          frozenUnitCost: null, request.Reason, userName, ct);

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
}
