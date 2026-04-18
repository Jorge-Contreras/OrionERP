using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Data.Common;
using System.Text;
using System.Globalization;
using Dapper;
using OrionERP.Application.Common;
using OrionERP.Application.Features.Logistica.Purchasing;
using OrionERP.Application.Features.Logistica.Shared;

namespace OrionERP.Infrastructure.Features.Logistica.Purchasing;

public sealed class PurchaseOrderService : IPurchaseOrderService
{
  private const string SuiteRoomType = "SUITE";
  private readonly IDbConnectionFactory _connectionFactory;

  public PurchaseOrderService(IDbConnectionFactory connectionFactory)
  {
    _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
  }

  public async Task<IReadOnlyList<PurchaseOrderListItemDto>> GetPurchaseOrdersAsync(PurchaseOrderFilter filter, CancellationToken ct = default)
  {
    filter ??= new PurchaseOrderFilter();
    var skip = Math.Max(filter.Skip, 0);
    var take = Math.Max(filter.Take, 0);

    var sql = new StringBuilder(
      """
      WITH LineTotals AS (
          SELECT
              line.PurchaseOrderId,
              COUNT(*) AS LineCount,
              CAST(ISNULL(SUM(line.OrderedQuantity), 0) AS decimal(18,4)) AS OrderedQuantity,
              CAST(ISNULL(SUM(line.ReceivedQuantity), 0) AS decimal(18,4)) AS ReceivedQuantity
          FROM logistica.PurchaseOrderLine line
          GROUP BY line.PurchaseOrderId
      ),
      AllocationTotals AS (
          SELECT
              line.PurchaseOrderId,
              COUNT(allocation.Id) AS AllocationCount
          FROM logistica.PurchaseOrderLine line
          JOIN logistica.PurchaseOrderLineAllocation allocation
            ON allocation.PurchaseOrderLineId = line.Id
          GROUP BY line.PurchaseOrderId
      )
      SELECT
          po.Id,
          po.PurchaseOrderCode,
          po.BusinessPartnerId,
          bp.PartnerName AS VendorName,
          po.[Status] AS [Status],
          po.OrderDate,
          po.ExpectedDate,
          CAST(ISNULL(lineTotals.OrderedQuantity, 0) AS decimal(18,4)) AS OrderedQuantity,
          CAST(ISNULL(lineTotals.ReceivedQuantity, 0) AS decimal(18,4)) AS ReceivedQuantity,
          CAST(ISNULL(lineTotals.OrderedQuantity, 0) - ISNULL(lineTotals.ReceivedQuantity, 0) AS decimal(18,4)) AS RemainingQuantity,
          ISNULL(lineTotals.LineCount, 0) AS LineCount,
          ISNULL(allocationTotals.AllocationCount, 0) AS AllocationCount,
          po.CreatedAt,
          po.CreatedBy,
          po.UpdatedAt
      FROM logistica.PurchaseOrder po
      JOIN dbo.BusinessPartner bp
        ON bp.Id = po.BusinessPartnerId
      LEFT JOIN LineTotals lineTotals
        ON lineTotals.PurchaseOrderId = po.Id
      LEFT JOIN AllocationTotals allocationTotals
        ON allocationTotals.PurchaseOrderId = po.Id
      WHERE 1 = 1
      """);

    var parameters = new DynamicParameters();

    if (filter.VendorId.HasValue)
    {
      sql.AppendLine(" AND po.BusinessPartnerId = @VendorId");
      parameters.Add("@VendorId", filter.VendorId.Value, DbType.Int32);
    }

    if (!string.IsNullOrWhiteSpace(filter.Status))
    {
      sql.AppendLine(" AND po.[Status] = @Status");
      parameters.Add("@Status", filter.Status.Trim(), DbType.String);
    }

    if (filter.OpenOnly)
    {
      sql.AppendLine(" AND po.[Status] IN @OpenStatuses");
      parameters.Add("@OpenStatuses", PurchaseOrderStatuses.Open);
    }

    if (!string.IsNullOrWhiteSpace(filter.SearchText))
    {
      sql.AppendLine(
        """
         AND (
             po.PurchaseOrderCode LIKE @Search
             OR bp.PartnerName LIKE @Search
             OR po.Notes LIKE @Search
         )
        """);
      parameters.Add("@Search", $"%{filter.SearchText.Trim()}%", DbType.String);
    }

    sql.AppendLine("ORDER BY po.OrderDate DESC, po.Id DESC");

    if (take > 0)
    {
      sql.AppendLine("OFFSET @Skip ROWS");
      sql.AppendLine("FETCH NEXT @Take ROWS ONLY;");
      parameters.Add("@Skip", skip, DbType.Int32);
      parameters.Add("@Take", take, DbType.Int32);
    }
    else
    {
      sql.AppendLine(";");
    }

    using var conn = CreateConnection();
    var rows = await conn.QueryAsync<PurchaseOrderListItemDto>(
      new CommandDefinition(sql.ToString(), parameters, cancellationToken: ct));

    return rows.AsList();
  }

  public async Task<PurchaseOrderDetailDto?> GetPurchaseOrderAsync(int purchaseOrderId, CancellationToken ct = default)
  {
    const string sql =
      """
      SELECT
          po.Id,
          po.PurchaseOrderCode,
          po.BusinessPartnerId,
          bp.PartnerName AS VendorName,
          bp.Rfc AS VendorRfc,
          po.[Status] AS [Status],
          po.OrderDate,
          po.ExpectedDate,
          po.Notes,
          CAST(ISNULL(lineTotals.OrderedQuantity, 0) AS decimal(18,4)) AS OrderedQuantity,
          CAST(ISNULL(lineTotals.ReceivedQuantity, 0) AS decimal(18,4)) AS ReceivedQuantity,
          CAST(ISNULL(lineTotals.OrderedQuantity, 0) - ISNULL(lineTotals.ReceivedQuantity, 0) AS decimal(18,4)) AS RemainingQuantity,
          po.CreatedAt,
          po.CreatedBy,
          po.UpdatedAt,
          po.UpdatedBy,
          po.IssuedAt,
          po.IssuedBy,
          po.CompletedAt,
          po.CompletedBy,
          po.CancelledAt,
          po.CancelledBy
      FROM logistica.PurchaseOrder po
      JOIN dbo.BusinessPartner bp
        ON bp.Id = po.BusinessPartnerId
      OUTER APPLY (
          SELECT
              CAST(ISNULL(SUM(line.OrderedQuantity), 0) AS decimal(18,4)) AS OrderedQuantity,
              CAST(ISNULL(SUM(line.ReceivedQuantity), 0) AS decimal(18,4)) AS ReceivedQuantity
          FROM logistica.PurchaseOrderLine line
          WHERE line.PurchaseOrderId = po.Id
      ) lineTotals
      WHERE po.Id = @PurchaseOrderId;

      SELECT
          line.Id,
          line.MaterialId,
          line.MaterialCodeSnapshot AS MaterialCode,
          line.MaterialDescriptionSnapshot AS MaterialDescription,
          line.VendorCodeSnapshot AS VendorCode,
          line.BaseUnitNameSnapshot AS BaseUnitName,
          CAST(line.PurchaseQuantitySnapshot AS decimal(18,4)) AS PurchaseQuantity,
          line.PurchaseUnitNameSnapshot AS PurchaseUnitName,
          CAST(line.UnitPrice AS decimal(18,4)) AS UnitPrice,
          CAST(line.OrderedQuantity AS decimal(18,4)) AS OrderedQuantity,
          CAST(line.ReceivedQuantity AS decimal(18,4)) AS ReceivedQuantity,
          CAST(line.OrderedQuantity - line.ReceivedQuantity AS decimal(18,4)) AS RemainingQuantity
      FROM logistica.PurchaseOrderLine line
      WHERE line.PurchaseOrderId = @PurchaseOrderId
      ORDER BY line.MaterialCodeSnapshot, line.MaterialDescriptionSnapshot, line.Id;

      SELECT
          allocation.Id,
          allocation.PurchaseOrderLineId,
          allocation.LocationId,
          location.LocationName,
          location.LocationCode,
          CAST(allocation.PlannedQuantity AS decimal(18,4)) AS PlannedQuantity,
          CAST(allocation.ReceivedQuantity AS decimal(18,4)) AS ReceivedQuantity,
          CAST(allocation.PlannedQuantity - allocation.ReceivedQuantity AS decimal(18,4)) AS RemainingQuantity
      FROM logistica.PurchaseOrderLineAllocation allocation
      JOIN logistica.PurchaseOrderLine line
        ON line.Id = allocation.PurchaseOrderLineId
      JOIN logistica.Location location
        ON location.Id = allocation.LocationId
      WHERE line.PurchaseOrderId = @PurchaseOrderId
      ORDER BY line.Id, location.LocationName, allocation.Id;

      SELECT
          receipt.Id AS ReceiptId,
          receipt.ReceiptCode,
          receipt.ReceiptDate,
          line.PurchaseOrderLineId,
          receiptLine.MaterialId,
          poLine.MaterialCodeSnapshot AS MaterialCode,
          poLine.MaterialDescriptionSnapshot AS MaterialDescription,
          poLine.BaseUnitNameSnapshot AS BaseUnitName,
          CAST(poLine.PurchaseQuantitySnapshot AS decimal(18,4)) AS PurchaseQuantity,
          poLine.PurchaseUnitNameSnapshot AS PurchaseUnitName,
          receiptLine.LocationId,
          location.LocationName,
          CAST(receiptLine.Quantity AS decimal(18,4)) AS Quantity,
          receipt.CreatedBy,
          receipt.Notes
      FROM logistica.PurchaseReceiptLine receiptLine
      JOIN logistica.PurchaseReceipt receipt
        ON receipt.Id = receiptLine.PurchaseReceiptId
      JOIN logistica.PurchaseOrderLineAllocation line
        ON line.Id = receiptLine.PurchaseOrderLineAllocationId
      JOIN logistica.PurchaseOrderLine poLine
        ON poLine.Id = receiptLine.PurchaseOrderLineId
      JOIN logistica.Location location
        ON location.Id = receiptLine.LocationId
      WHERE receipt.PurchaseOrderId = @PurchaseOrderId
      ORDER BY receipt.ReceiptDate DESC, receipt.Id DESC, poLine.MaterialCodeSnapshot, location.LocationName, receiptLine.Id DESC;

      SELECT
          room.ID AS Id,
          room.ROOM_NAME AS Name,
          room.ROOM_TYPE AS Code
      FROM logistica.PurchaseOrderRoomScope scope
      JOIN dbo.ROOM room
        ON room.ID = scope.RoomId
      WHERE scope.PurchaseOrderId = @PurchaseOrderId
      ORDER BY room.ROOM_NAME, room.ID;
      """;

    using var conn = CreateConnection();
    using var multi = await conn.QueryMultipleAsync(
      new CommandDefinition(sql, new { PurchaseOrderId = purchaseOrderId }, cancellationToken: ct));

    var detail = await multi.ReadFirstOrDefaultAsync<PurchaseOrderDetailDto>();
    if (detail is null)
    {
      return null;
    }

    var lines = (await multi.ReadAsync<PurchaseOrderLineDto>()).AsList();
    var allocations = (await multi.ReadAsync<PurchaseOrderAllocationDto>()).AsList();
    var history = (await multi.ReadAsync<PurchaseReceiptLineHistoryDto>()).AsList();
    var roomScope = (await multi.ReadAsync<LookupOptionDto>()).AsList();

    var allocationsByLineId = allocations
      .GroupBy(allocation => allocation.PurchaseOrderLineId)
      .ToDictionary(group => group.Key, group => (IReadOnlyList<PurchaseOrderAllocationDto>)group.ToList());

    foreach (var line in lines)
    {
      line.Allocations = allocationsByLineId.TryGetValue(line.Id, out var lineAllocations)
        ? lineAllocations
        : Array.Empty<PurchaseOrderAllocationDto>();
    }

    detail.Lines = lines;
    detail.ReceiptHistory = history;
    detail.RoomScope = roomScope;
    return detail;
  }

  public async Task<PurchaseOrderCatalogDto> GetCatalogAsync(CancellationToken ct = default)
  {
    const string sql =
      """
      SELECT
          bp.Id,
          bp.PartnerName AS Name,
          bp.Rfc AS Code
      FROM dbo.BusinessPartner bp
      WHERE bp.IsActive = 1
        AND (
            EXISTS (SELECT 1 FROM dbo.BusinessPartnerRole r WHERE r.BusinessPartnerId = bp.Id AND r.RoleCode = 'Vendor')
            OR EXISTS (SELECT 1 FROM logistica.VendorProfile vp WHERE vp.BusinessPartnerId = bp.Id)
        )
      ORDER BY bp.PartnerName, bp.Id;

      SELECT
          l.Id,
          l.LocationName AS Name,
          l.LocationCode AS Code
      FROM logistica.Location l
      WHERE l.IsActive = 1
        AND l.IsInventoryEnabled = 1
      ORDER BY l.LocationName, l.Id;

      SELECT
          room.ID AS Id,
          room.ROOM_NAME AS Name,
          room.ROOM_TYPE AS Code
      FROM dbo.ROOM room
      WHERE room.ROOM_TYPE = @RoomType
      ORDER BY room.ROOM_NAME, room.ID;
      """;

    using var conn = CreateConnection();
    using var multi = await conn.QueryMultipleAsync(new CommandDefinition(
      sql,
      new { RoomType = SuiteRoomType },
      cancellationToken: ct));

    return new PurchaseOrderCatalogDto
    {
      Vendors = (await multi.ReadAsync<LookupOptionDto>()).AsList(),
      Locations = (await multi.ReadAsync<LookupOptionDto>()).AsList(),
      Rooms = (await multi.ReadAsync<LookupOptionDto>()).AsList(),
      Statuses = PurchaseOrderStatuses.All
    };
  }

  public async Task<LogisticsCommandResult> CreateAutoDraftAsync(AutoPurchaseOrderCreateRequest request, string? savedBy, CancellationToken ct = default)
  {
    if (request is null)
    {
      throw new ArgumentNullException(nameof(request));
    }

    var validationResults = new List<ValidationResult>();
    if (!Validator.TryValidateObject(request, new ValidationContext(request), validationResults, validateAllProperties: true))
    {
      return LogisticsCommandResult.Fail(validationResults[0].ErrorMessage ?? "La solicitud de Auto PO no es válida.");
    }

    using var conn = CreateConnection();
    await conn.OpenAsync(ct);

    if (!await VendorExistsAsync(conn, tx: null, request.BusinessPartnerId, ct))
    {
      return LogisticsCommandResult.Fail("El proveedor seleccionado no existe o no está activo.");
    }

    var roomScope = await NormalizeAutoPurchaseRoomScopeAsync(conn, request.RoomIds, ct);
    if (!roomScope.Success)
    {
      return LogisticsCommandResult.Fail(roomScope.ErrorMessage ?? "La selección de suites no es válida.");
    }

    var existingDraftId = await GetExistingVendorDraftAsync(conn, request.BusinessPartnerId, roomScope.RoomIds, ct);
    if (existingDraftId.HasValue)
    {
      return LogisticsCommandResult.Ok(
        "El proveedor ya tiene un borrador abierto. Se abrirá ese documento para revisión.",
        existingDraftId.Value);
    }

    var candidateRows = (await LoadAutoReplenishmentRowsAsync(conn, request.BusinessPartnerId, roomScope.RoomIds, ct))
      .Where(row => row.ProjectedQuantity <= row.MinQuantity && row.RawNeedQuantity > 0m)
      .ToList();
    if (candidateRows.Count == 0)
    {
      return LogisticsCommandResult.Fail("No hay materiales por reordenar para el proveedor seleccionado.");
    }

    var defaultLeadTimeDays = await GetVendorDefaultLeadTimeDaysAsync(conn, request.BusinessPartnerId, ct);
    var draftRequest = BuildAutoDraftRequest(request, candidateRows, defaultLeadTimeDays);
    await using var tx = await conn.BeginTransactionAsync(ct);

    try
    {
      var result = await SaveDraftCoreAsync(conn, tx, draftRequest, savedBy, ct, roomScope.RoomIds);
      if (!result.Success)
      {
        await tx.RollbackAsync(ct);
        return result;
      }

      await tx.CommitAsync(ct);

      return LogisticsCommandResult.Ok("Borrador Auto PO generado correctamente.", result.EntityId);
    }
    catch
    {
      await tx.RollbackAsync(ct);
      throw;
    }
  }

  public async Task<LogisticsCommandResult> SaveDraftAsync(PurchaseOrderUpsertRequest request, string? savedBy, CancellationToken ct = default)
  {
    if (request is null)
    {
      throw new ArgumentNullException(nameof(request));
    }

    var validationMessage = ValidateDraftRequest(request);
    if (validationMessage is not null)
    {
      return LogisticsCommandResult.Fail(validationMessage);
    }

    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    await using var tx = await conn.BeginTransactionAsync(ct);

    try
    {
      var result = await SaveDraftCoreAsync(conn, tx, request, savedBy, ct);
      if (!result.Success)
      {
        await tx.RollbackAsync(ct);
        return result;
      }

      await tx.CommitAsync(ct);
      return result;
    }
    catch
    {
      await tx.RollbackAsync(ct);
      throw;
    }
  }

  public async Task<LogisticsCommandResult> IssueAsync(int purchaseOrderId, string? issuedBy, CancellationToken ct = default)
  {
    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    await using var tx = await conn.BeginTransactionAsync(ct);

    try
    {
      var state = await GetPurchaseOrderStateAsync(conn, tx, purchaseOrderId, ct);
      if (state is null)
      {
        await tx.RollbackAsync(ct);
        return LogisticsCommandResult.Fail("La orden de compra ya no existe.");
      }

      if (!string.Equals(state.Status, PurchaseOrderStatuses.Draft, StringComparison.OrdinalIgnoreCase))
      {
        await tx.RollbackAsync(ct);
        return LogisticsCommandResult.Fail("Solo las órdenes en borrador se pueden emitir.");
      }

      if (!await PurchaseOrderHasLinesAsync(conn, tx, purchaseOrderId, ct))
      {
        await tx.RollbackAsync(ct);
        return LogisticsCommandResult.Fail("Agrega al menos un material antes de emitir la orden de compra.");
      }

      var actor = NormalizeActor(issuedBy);

      await conn.ExecuteAsync(
        new CommandDefinition(
          """
          UPDATE logistica.PurchaseOrder
          SET [Status] = @Status,
              IssuedAt = SYSUTCDATETIME(),
              IssuedBy = @IssuedBy,
              UpdatedAt = SYSUTCDATETIME(),
              UpdatedBy = @UpdatedBy
          WHERE Id = @PurchaseOrderId;
          """,
          new
          {
            PurchaseOrderId = purchaseOrderId,
            Status = PurchaseOrderStatuses.Issued,
            IssuedBy = actor,
            UpdatedBy = actor
          },
          tx,
          cancellationToken: ct));

      await tx.CommitAsync(ct);
      return LogisticsCommandResult.Ok("Orden de compra emitida correctamente.", purchaseOrderId);
    }
    catch
    {
      await tx.RollbackAsync(ct);
      throw;
    }
  }

  public async Task<LogisticsCommandResult> ReceiveAsync(PurchaseReceiptCreateRequest request, string? receivedBy, CancellationToken ct = default)
  {
    if (request is null)
    {
      throw new ArgumentNullException(nameof(request));
    }

    var validationMessage = ValidateReceiptRequest(request);
    if (validationMessage is not null)
    {
      return LogisticsCommandResult.Fail(validationMessage);
    }

    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    await using var tx = await conn.BeginTransactionAsync(IsolationLevel.Serializable, ct);

    try
    {
      var order = await GetPurchaseOrderStateAsync(conn, tx, request.PurchaseOrderId, ct);
      if (order is null)
      {
        await tx.RollbackAsync(ct);
        return LogisticsCommandResult.Fail("La orden de compra ya no existe.");
      }

      if (!PurchaseOrderStatuses.Open.Contains(order.Status, StringComparer.OrdinalIgnoreCase))
      {
        await tx.RollbackAsync(ct);
        return LogisticsCommandResult.Fail("Solo las órdenes emitidas o parcialmente recibidas aceptan recepciones.");
      }

      var groupedLines = request.Lines
        .GroupBy(line => line.PurchaseOrderLineAllocationId)
        .ToDictionary(group => group.Key, group => group.Sum(item => item.Quantity));

      var allocationRows = await LoadAllocationRowsAsync(conn, tx, request.PurchaseOrderId, groupedLines.Keys, ct);
      if (allocationRows.Count != groupedLines.Count)
      {
        await tx.RollbackAsync(ct);
        return LogisticsCommandResult.Fail("Todas las líneas recibidas deben pertenecer a la orden seleccionada.");
      }

      foreach (var item in groupedLines)
      {
        var allocation = allocationRows[item.Key];
        var remainingQuantity = allocation.PlannedQuantity - allocation.ReceivedQuantity;
        if (item.Value > remainingQuantity)
        {
          await tx.RollbackAsync(ct);
          return LogisticsCommandResult.Fail($"La recepción excede la cantidad pendiente para {allocation.MaterialDescription} en {allocation.LocationName}.");
        }
      }

      var actor = NormalizeActor(receivedBy);
      var receiptId = await conn.ExecuteScalarAsync<int>(
        new CommandDefinition(
          """
          INSERT INTO logistica.PurchaseReceipt
          (
              PurchaseOrderId,
              ReceiptCode,
              ReceiptDate,
              Notes,
              CreatedAt,
              CreatedBy
          )
          VALUES
          (
              @PurchaseOrderId,
              CONCAT('TMP-', LEFT(REPLACE(CONVERT(varchar(36), NEWID()), '-', ''), 20)),
              @ReceiptDate,
              @Notes,
              SYSUTCDATETIME(),
              @CreatedBy
          );

          SELECT CAST(SCOPE_IDENTITY() AS int);
          """,
          new
          {
            request.PurchaseOrderId,
            ReceiptDate = request.ReceiptDate.Date,
            Notes = NullIfWhiteSpace(request.Notes),
            CreatedBy = actor
          },
          tx,
          cancellationToken: ct));

      await conn.ExecuteAsync(
        new CommandDefinition(
          """
          UPDATE logistica.PurchaseReceipt
          SET ReceiptCode = CONCAT('PR-', RIGHT(REPLICATE('0', 6) + CAST(@ReceiptId AS varchar(20)), 6))
          WHERE Id = @ReceiptId;
          """,
          new { ReceiptId = receiptId },
          tx,
          cancellationToken: ct));

      foreach (var item in groupedLines)
      {
        var allocation = allocationRows[item.Key];
        var quantity = item.Value;

        await conn.ExecuteAsync(
          new CommandDefinition(
            """
            UPDATE logistica.PurchaseOrderLineAllocation
            SET ReceivedQuantity = ReceivedQuantity + @Quantity,
                UpdatedAt = SYSUTCDATETIME()
            WHERE Id = @AllocationId;
            """,
            new
            {
              AllocationId = allocation.AllocationId,
              Quantity = quantity
            },
            tx,
            cancellationToken: ct));

        await conn.ExecuteAsync(
          new CommandDefinition(
            """
            UPDATE logistica.PurchaseOrderLine
            SET ReceivedQuantity = ReceivedQuantity + @Quantity,
                UpdatedAt = SYSUTCDATETIME()
            WHERE Id = @PurchaseOrderLineId;
            """,
            new
            {
              PurchaseOrderLineId = allocation.PurchaseOrderLineId,
              Quantity = quantity
            },
            tx,
            cancellationToken: ct));

        var stockBalance = await GetStockBalanceStateAsync(conn, tx, allocation.LocationId, allocation.MaterialId, ct);
        decimal quantityAfter;
        int stockBalanceId;

        if (stockBalance is null)
        {
          stockBalanceId = await conn.ExecuteScalarAsync<int>(
            new CommandDefinition(
              """
              INSERT INTO logistica.StockBalance
              (
                  LocationId,
                  MaterialId,
                  Quantity,
                  LastPurchaseDate,
                  CreatedAt,
                  UpdatedAt
              )
              VALUES
              (
                  @LocationId,
                  @MaterialId,
                  @Quantity,
                  @LastPurchaseDate,
                  SYSUTCDATETIME(),
                  SYSUTCDATETIME()
              );

              SELECT CAST(SCOPE_IDENTITY() AS int);
              """,
              new
              {
                allocation.LocationId,
                allocation.MaterialId,
                Quantity = quantity,
                LastPurchaseDate = request.ReceiptDate.Date
              },
              tx,
              cancellationToken: ct));

          quantityAfter = quantity;
        }
        else
        {
          stockBalanceId = stockBalance.Id;
          quantityAfter = stockBalance.Quantity + quantity;

          if (stockBalance.IsRemoved)
          {
            await conn.ExecuteAsync(
              new CommandDefinition(
                """
                UPDATE logistica.StockBalance
                SET Quantity = Quantity + @Quantity,
                    LastPurchaseDate = @LastPurchaseDate,
                    IsRemoved = 0,
                    RemovedAt = NULL,
                    RemovedBy = NULL,
                    UpdatedAt = SYSUTCDATETIME()
                WHERE Id = @StockBalanceId;
                """,
                new
                {
                  StockBalanceId = stockBalanceId,
                  Quantity = quantity,
                  LastPurchaseDate = request.ReceiptDate.Date
                },
                tx,
                cancellationToken: ct));
          }
          else
          {
            await conn.ExecuteAsync(
              new CommandDefinition(
                """
                UPDATE logistica.StockBalance
                SET Quantity = Quantity + @Quantity,
                    LastPurchaseDate = @LastPurchaseDate,
                    UpdatedAt = SYSUTCDATETIME()
                WHERE Id = @StockBalanceId;
                """,
                new
                {
                  StockBalanceId = stockBalanceId,
                  Quantity = quantity,
                  LastPurchaseDate = request.ReceiptDate.Date
                },
                tx,
                cancellationToken: ct));
          }
        }

        await conn.ExecuteAsync(
          new CommandDefinition(
            """
            INSERT INTO logistica.PurchaseReceiptLine
            (
                PurchaseReceiptId,
                PurchaseOrderLineAllocationId,
                PurchaseOrderLineId,
                LocationId,
                MaterialId,
                Quantity,
                CreatedAt
            )
            VALUES
            (
                @PurchaseReceiptId,
                @PurchaseOrderLineAllocationId,
                @PurchaseOrderLineId,
                @LocationId,
                @MaterialId,
                @Quantity,
                SYSUTCDATETIME()
            );
            """,
            new
            {
              PurchaseReceiptId = receiptId,
              PurchaseOrderLineAllocationId = allocation.AllocationId,
              allocation.PurchaseOrderLineId,
              allocation.LocationId,
              allocation.MaterialId,
              Quantity = quantity
            },
            tx,
            cancellationToken: ct));

        await conn.ExecuteAsync(
          new CommandDefinition(
            """
            INSERT INTO logistica.StockTransaction
            (
                StockBalanceId,
                LocationId,
                MaterialId,
                TransactionType,
                QuantityDelta,
                QuantityAfter,
                ReferenceType,
                ReferenceId,
                Notes,
                PerformedBy,
                OccurredAt
            )
            VALUES
            (
                @StockBalanceId,
                @LocationId,
                @MaterialId,
                'PurchaseReceipt',
                @QuantityDelta,
                @QuantityAfter,
                'PurchaseReceipt',
                @ReferenceId,
                @Notes,
                @PerformedBy,
                SYSUTCDATETIME()
            );
            """,
            new
            {
              StockBalanceId = stockBalanceId,
              allocation.LocationId,
              allocation.MaterialId,
              QuantityDelta = quantity,
              QuantityAfter = quantityAfter,
              ReferenceId = receiptId,
              Notes = BuildReceiptAuditNote(allocation.MaterialDescription, allocation.LocationName, request.Notes),
              PerformedBy = actor
            },
            tx,
            cancellationToken: ct));
      }

      await UpdatePurchaseOrderStatusAfterReceiptAsync(conn, tx, request.PurchaseOrderId, actor, ct);

      await tx.CommitAsync(ct);
      return LogisticsCommandResult.Ok("Recepción registrada correctamente.", receiptId);
    }
    catch
    {
      await tx.RollbackAsync(ct);
      throw;
    }
  }

  public async Task<LogisticsCommandResult> CompleteAsync(int purchaseOrderId, string? completedBy, CancellationToken ct = default)
  {
    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    await using var tx = await conn.BeginTransactionAsync(ct);

    try
    {
      var state = await GetPurchaseOrderStateAsync(conn, tx, purchaseOrderId, ct);
      if (state is null)
      {
        await tx.RollbackAsync(ct);
        return LogisticsCommandResult.Fail("La orden de compra ya no existe.");
      }

      if (!string.Equals(state.Status, PurchaseOrderStatuses.PartiallyReceived, StringComparison.OrdinalIgnoreCase))
      {
        await tx.RollbackAsync(ct);
        return LogisticsCommandResult.Fail("Solo las órdenes parcialmente recibidas se pueden cerrar manualmente.");
      }

      var remainingQuantity = await GetRemainingQuantityAsync(conn, tx, purchaseOrderId, ct);
      if (remainingQuantity <= 0)
      {
        await tx.RollbackAsync(ct);
        return LogisticsCommandResult.Fail("La orden ya no tiene cantidades pendientes.");
      }

      var actor = NormalizeActor(completedBy);

      await conn.ExecuteAsync(
        new CommandDefinition(
          """
          UPDATE logistica.PurchaseOrder
          SET [Status] = @Status,
              CompletedAt = SYSUTCDATETIME(),
              CompletedBy = @CompletedBy,
              UpdatedAt = SYSUTCDATETIME(),
              UpdatedBy = @UpdatedBy
          WHERE Id = @PurchaseOrderId;
          """,
          new
          {
            PurchaseOrderId = purchaseOrderId,
            Status = PurchaseOrderStatuses.Completed,
            CompletedBy = actor,
            UpdatedBy = actor
          },
          tx,
          cancellationToken: ct));

      await tx.CommitAsync(ct);
      return LogisticsCommandResult.Ok("Orden de compra cerrada correctamente.", purchaseOrderId);
    }
    catch
    {
      await tx.RollbackAsync(ct);
      throw;
    }
  }

  public async Task<LogisticsCommandResult> CancelAsync(int purchaseOrderId, string? cancelledBy, CancellationToken ct = default)
  {
    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    await using var tx = await conn.BeginTransactionAsync(ct);

    try
    {
      var state = await GetPurchaseOrderStateAsync(conn, tx, purchaseOrderId, ct);
      if (state is null)
      {
        await tx.RollbackAsync(ct);
        return LogisticsCommandResult.Fail("La orden de compra ya no existe.");
      }

      if (!string.Equals(state.Status, PurchaseOrderStatuses.Draft, StringComparison.OrdinalIgnoreCase)
          && !string.Equals(state.Status, PurchaseOrderStatuses.Issued, StringComparison.OrdinalIgnoreCase))
      {
        await tx.RollbackAsync(ct);
        return LogisticsCommandResult.Fail("Solo las órdenes sin recepción se pueden cancelar.");
      }

      var hasReceipts = await conn.ExecuteScalarAsync<bool>(
        new CommandDefinition(
          """
          SELECT CAST(CASE WHEN EXISTS (
              SELECT 1
              FROM logistica.PurchaseReceipt receipt
              WHERE receipt.PurchaseOrderId = @PurchaseOrderId
          ) THEN 1 ELSE 0 END AS bit);
          """,
          new { PurchaseOrderId = purchaseOrderId },
          tx,
          cancellationToken: ct));

      if (hasReceipts)
      {
        await tx.RollbackAsync(ct);
        return LogisticsCommandResult.Fail("La orden ya tiene recepciones registradas y no se puede cancelar.");
      }

      var actor = NormalizeActor(cancelledBy);

      await conn.ExecuteAsync(
        new CommandDefinition(
          """
          UPDATE logistica.PurchaseOrder
          SET [Status] = @Status,
              CancelledAt = SYSUTCDATETIME(),
              CancelledBy = @CancelledBy,
              UpdatedAt = SYSUTCDATETIME(),
              UpdatedBy = @UpdatedBy
          WHERE Id = @PurchaseOrderId;
          """,
          new
          {
            PurchaseOrderId = purchaseOrderId,
            Status = PurchaseOrderStatuses.Cancelled,
            CancelledBy = actor,
            UpdatedBy = actor
          },
          tx,
          cancellationToken: ct));

      await tx.CommitAsync(ct);
      return LogisticsCommandResult.Ok("Orden de compra cancelada correctamente.", purchaseOrderId);
    }
    catch
    {
      await tx.RollbackAsync(ct);
      throw;
    }
  }

  private static async Task<LogisticsCommandResult> SaveDraftCoreAsync(
    DbConnection conn,
    DbTransaction tx,
    PurchaseOrderUpsertRequest request,
    string? savedBy,
    CancellationToken ct,
    IReadOnlyCollection<int>? roomScopeIds = null)
  {
    if (!await VendorExistsAsync(conn, tx, request.BusinessPartnerId, ct))
    {
      return LogisticsCommandResult.Fail("El proveedor seleccionado no existe o no está activo.");
    }

    var materialRows = await LoadMaterialRowsAsync(
      conn,
      tx,
      request.BusinessPartnerId,
      request.Lines.Select(line => line.MaterialId),
      ct);

    if (materialRows.Count != request.Lines.Count)
    {
      return LogisticsCommandResult.Fail("Todos los materiales deben existir, estar activos y pertenecer al proveedor seleccionado.");
    }

    var locationIds = request.Lines
      .SelectMany(line => line.Allocations)
      .Select(allocation => allocation.LocationId)
      .Distinct()
      .ToArray();

    var locationRows = await LoadLocationRowsAsync(conn, tx, locationIds, ct);
    if (locationRows.Count != locationIds.Length)
    {
      return LogisticsCommandResult.Fail("Todas las ubicaciones deben existir, estar activas y habilitadas para inventario.");
    }

    foreach (var lineRequest in request.Lines)
    {
      var material = materialRows[lineRequest.MaterialId];
      var totalQuantity = lineRequest.Allocations.Sum(allocation => allocation.PlannedQuantity);
      var purchaseQuantity = NormalizePurchaseQuantity(lineRequest.PurchaseQuantitySnapshot, material.PurchaseQuantity);
      var purchaseUnitName = ResolvePurchaseUnitName(lineRequest.PurchaseUnitNameSnapshot, material.PurchaseUnitName);

      foreach (var allocationRequest in lineRequest.Allocations)
      {
        if (!IsWholePurchaseMultiple(allocationRequest.PlannedQuantity, purchaseQuantity, purchaseUnitName))
        {
          return LogisticsCommandResult.Fail(
            BuildPurchaseAllocationMultipleValidationMessage(
              material,
              locationRows[allocationRequest.LocationId],
              purchaseQuantity));
        }
      }

      if (!IsWholePurchaseMultiple(totalQuantity, purchaseQuantity, purchaseUnitName))
      {
        return LogisticsCommandResult.Fail(BuildPurchaseMultipleValidationMessage(material, purchaseQuantity));
      }
    }

    var actor = NormalizeActor(savedBy);
    var purchaseOrderId = request.Id ?? 0;

    if (request.Id.HasValue && request.Id.Value > 0)
    {
      var existing = await GetPurchaseOrderStateAsync(conn, tx, request.Id.Value, ct);
      if (existing is null)
      {
        return LogisticsCommandResult.Fail("La orden de compra ya no existe.");
      }

      if (!string.Equals(existing.Status, PurchaseOrderStatuses.Draft, StringComparison.OrdinalIgnoreCase))
      {
        return LogisticsCommandResult.Fail("Solo las órdenes en borrador se pueden editar.");
      }

      await conn.ExecuteAsync(
        new CommandDefinition(
          """
          UPDATE logistica.PurchaseOrder
          SET BusinessPartnerId = @BusinessPartnerId,
              OrderDate = @OrderDate,
              ExpectedDate = @ExpectedDate,
              Notes = @Notes,
              UpdatedAt = SYSUTCDATETIME(),
              UpdatedBy = @UpdatedBy
          WHERE Id = @Id;
          """,
          new
          {
            Id = request.Id.Value,
            request.BusinessPartnerId,
            OrderDate = request.OrderDate.Date,
            ExpectedDate = request.ExpectedDate?.Date,
            Notes = NullIfWhiteSpace(request.Notes),
            UpdatedBy = actor
          },
          tx,
          cancellationToken: ct));

      await conn.ExecuteAsync(
        new CommandDefinition(
          """
          DELETE allocation
          FROM logistica.PurchaseOrderLineAllocation allocation
          JOIN logistica.PurchaseOrderLine line
            ON line.Id = allocation.PurchaseOrderLineId
          WHERE line.PurchaseOrderId = @PurchaseOrderId;

          DELETE FROM logistica.PurchaseOrderLine
          WHERE PurchaseOrderId = @PurchaseOrderId;
          """,
          new { PurchaseOrderId = request.Id.Value },
          tx,
          cancellationToken: ct));

      purchaseOrderId = request.Id.Value;
    }
    else
    {
      purchaseOrderId = await conn.ExecuteScalarAsync<int>(
        new CommandDefinition(
          """
          INSERT INTO logistica.PurchaseOrder
          (
              PurchaseOrderCode,
              BusinessPartnerId,
              [Status],
              OrderDate,
              ExpectedDate,
              Notes,
              CreatedAt,
              CreatedBy,
              UpdatedAt,
              UpdatedBy
          )
          VALUES
          (
              CONCAT('TMP-', LEFT(REPLACE(CONVERT(varchar(36), NEWID()), '-', ''), 20)),
              @BusinessPartnerId,
              @Status,
              @OrderDate,
              @ExpectedDate,
              @Notes,
              SYSUTCDATETIME(),
              @CreatedBy,
              SYSUTCDATETIME(),
              @UpdatedBy
          );

          SELECT CAST(SCOPE_IDENTITY() AS int);
          """,
          new
          {
            request.BusinessPartnerId,
            Status = PurchaseOrderStatuses.Draft,
            OrderDate = request.OrderDate.Date,
            ExpectedDate = request.ExpectedDate?.Date,
            Notes = NullIfWhiteSpace(request.Notes),
            CreatedBy = actor,
            UpdatedBy = actor
          },
          tx,
          cancellationToken: ct));

      await conn.ExecuteAsync(
        new CommandDefinition(
          """
          UPDATE logistica.PurchaseOrder
          SET PurchaseOrderCode = CONCAT('PO-', RIGHT(REPLICATE('0', 6) + CAST(@PurchaseOrderId AS varchar(20)), 6))
          WHERE Id = @PurchaseOrderId;
          """,
          new { PurchaseOrderId = purchaseOrderId },
          tx,
          cancellationToken: ct));

      if (roomScopeIds is { Count: > 0 })
      {
        foreach (var roomId in roomScopeIds)
        {
          await conn.ExecuteAsync(
            new CommandDefinition(
              """
              INSERT INTO logistica.PurchaseOrderRoomScope
              (
                  PurchaseOrderId,
                  RoomId,
                  CreatedAt
              )
              VALUES
              (
                  @PurchaseOrderId,
                  @RoomId,
                  SYSUTCDATETIME()
              );
              """,
              new
              {
                PurchaseOrderId = purchaseOrderId,
                RoomId = roomId
              },
              tx,
              cancellationToken: ct));
        }
      }
    }

    foreach (var lineRequest in request.Lines)
    {
      var material = materialRows[lineRequest.MaterialId];
      var orderedQuantity = lineRequest.Allocations.Sum(allocation => allocation.PlannedQuantity);

      var lineId = await conn.ExecuteScalarAsync<int>(
        new CommandDefinition(
          """
          INSERT INTO logistica.PurchaseOrderLine
          (
              PurchaseOrderId,
              MaterialId,
              MaterialCodeSnapshot,
              MaterialDescriptionSnapshot,
              VendorCodeSnapshot,
              BaseUnitNameSnapshot,
              PurchaseQuantitySnapshot,
              PurchaseUnitNameSnapshot,
              UnitPrice,
              OrderedQuantity,
              ReceivedQuantity,
              CreatedAt,
              UpdatedAt
          )
          VALUES
          (
              @PurchaseOrderId,
              @MaterialId,
              @MaterialCodeSnapshot,
              @MaterialDescriptionSnapshot,
              @VendorCodeSnapshot,
              @BaseUnitNameSnapshot,
              @PurchaseQuantitySnapshot,
              @PurchaseUnitNameSnapshot,
              @UnitPrice,
              @OrderedQuantity,
              0,
              SYSUTCDATETIME(),
              SYSUTCDATETIME()
          );

          SELECT CAST(SCOPE_IDENTITY() AS int);
          """,
          new
          {
            PurchaseOrderId = purchaseOrderId,
            lineRequest.MaterialId,
            MaterialCodeSnapshot = material.MaterialCode,
            MaterialDescriptionSnapshot = material.Description,
            VendorCodeSnapshot = NullIfWhiteSpace(material.VendorCode),
            BaseUnitNameSnapshot = NullIfWhiteSpace(material.BaseUnitName),
            PurchaseQuantitySnapshot = NormalizePurchaseQuantity(lineRequest.PurchaseQuantitySnapshot, material.PurchaseQuantity),
            PurchaseUnitNameSnapshot = ResolvePurchaseUnitName(lineRequest.PurchaseUnitNameSnapshot, material.PurchaseUnitName),
            lineRequest.UnitPrice,
            OrderedQuantity = orderedQuantity
          },
          tx,
          cancellationToken: ct));

      foreach (var allocationRequest in lineRequest.Allocations)
      {
        await conn.ExecuteAsync(
          new CommandDefinition(
            """
            INSERT INTO logistica.PurchaseOrderLineAllocation
            (
                PurchaseOrderLineId,
                LocationId,
                PlannedQuantity,
                ReceivedQuantity,
                CreatedAt,
                UpdatedAt
            )
            VALUES
            (
                @PurchaseOrderLineId,
                @LocationId,
                @PlannedQuantity,
                0,
                SYSUTCDATETIME(),
                SYSUTCDATETIME()
            );
            """,
            new
            {
              PurchaseOrderLineId = lineId,
              allocationRequest.LocationId,
              allocationRequest.PlannedQuantity
            },
            tx,
            cancellationToken: ct));
      }
    }

    return LogisticsCommandResult.Ok("Orden de compra guardada correctamente.", purchaseOrderId);
  }

  private DbConnection CreateConnection()
    => _connectionFactory.Create() as DbConnection
      ?? throw new InvalidOperationException("La fábrica de conexiones no devolvió una DbConnection.");

  private static string? ValidateDraftRequest(PurchaseOrderUpsertRequest request)
  {
    var validationResults = new List<ValidationResult>();
    if (!Validator.TryValidateObject(request, new ValidationContext(request), validationResults, validateAllProperties: true))
    {
      return validationResults[0].ErrorMessage ?? "La orden de compra no es válida.";
    }

    if (request.ExpectedDate.HasValue && request.ExpectedDate.Value.Date < request.OrderDate.Date)
    {
      return "La fecha esperada no puede ser anterior a la fecha de la orden.";
    }

    if (request.Lines.Count == 0)
    {
      return "Agrega al menos un material a la orden de compra.";
    }

    var materialIds = new HashSet<int>();
    foreach (var line in request.Lines)
    {
      validationResults.Clear();
      if (!Validator.TryValidateObject(line, new ValidationContext(line), validationResults, validateAllProperties: true))
      {
        return validationResults[0].ErrorMessage ?? "Una línea de la orden no es válida.";
      }

      if (!materialIds.Add(line.MaterialId))
      {
        return "No puedes repetir el mismo material dentro de la orden.";
      }

      if (line.Allocations.Count == 0)
      {
        return "Cada material debe tener al menos una ubicación planeada.";
      }

      var locationIds = new HashSet<int>();
      decimal totalQuantity = 0m;
      foreach (var allocation in line.Allocations)
      {
        validationResults.Clear();
        if (!Validator.TryValidateObject(allocation, new ValidationContext(allocation), validationResults, validateAllProperties: true))
        {
          return validationResults[0].ErrorMessage ?? "Una asignación de la orden no es válida.";
        }

        if (!locationIds.Add(allocation.LocationId))
        {
          return "No puedes repetir la misma ubicación dentro del mismo material.";
        }

        totalQuantity += allocation.PlannedQuantity;
      }

      if (totalQuantity <= 0)
      {
        return "Cada material debe tener una cantidad planeada mayor a 0.";
      }
    }

    return null;
  }

  private static string? ValidateReceiptRequest(PurchaseReceiptCreateRequest request)
  {
    var validationResults = new List<ValidationResult>();
    if (!Validator.TryValidateObject(request, new ValidationContext(request), validationResults, validateAllProperties: true))
    {
      return validationResults[0].ErrorMessage ?? "La recepción no es válida.";
    }

    if (request.Lines.Count == 0)
    {
      return "Captura al menos una cantidad para registrar la recepción.";
    }

    foreach (var line in request.Lines)
    {
      validationResults.Clear();
      if (!Validator.TryValidateObject(line, new ValidationContext(line), validationResults, validateAllProperties: true))
      {
        return validationResults[0].ErrorMessage ?? "Una línea de recepción no es válida.";
      }
    }

    return null;
  }

  private static string NormalizeActor(string? actor)
    => string.IsNullOrWhiteSpace(actor) ? "OrionERP" : actor.Trim();

  private static string? NullIfWhiteSpace(string? value)
    => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

  private static async Task<NormalizedRoomScopeResult> NormalizeAutoPurchaseRoomScopeAsync(
    DbConnection conn,
    IEnumerable<int>? requestedRoomIds,
    CancellationToken ct)
  {
    var suiteRooms = await LoadSuiteRoomsAsync(conn, ct);
    var normalizedRoomIds = NormalizeRoomIds(requestedRoomIds);
    if (normalizedRoomIds.Length == 0)
    {
      return NormalizedRoomScopeResult.Ok([]);
    }

    var suiteRoomIds = suiteRooms.Select(room => room.Id).ToHashSet();
    if (normalizedRoomIds.Any(roomId => !suiteRoomIds.Contains(roomId)))
    {
      return NormalizedRoomScopeResult.Fail("Las suites seleccionadas no existen o ya no están disponibles para Auto PO.");
    }

    if (suiteRooms.Count > 0 && normalizedRoomIds.Length == suiteRooms.Count)
    {
      return NormalizedRoomScopeResult.Ok([]);
    }

    return NormalizedRoomScopeResult.Ok(normalizedRoomIds);
  }

  private static string BuildReceiptAuditNote(string materialDescription, string locationName, string? receiptNotes)
  {
    var note = $"Recepción de compra para {materialDescription} en {locationName}.";
    return string.IsNullOrWhiteSpace(receiptNotes)
      ? note
      : $"{note} {receiptNotes.Trim()}";
  }

  private static async Task<bool> VendorExistsAsync(DbConnection conn, DbTransaction? tx, int businessPartnerId, CancellationToken ct)
  {
    return await conn.ExecuteScalarAsync<bool>(
      new CommandDefinition(
        """
        SELECT CAST(CASE WHEN EXISTS (
            SELECT 1
            FROM dbo.BusinessPartner bp
            WHERE bp.Id = @BusinessPartnerId
              AND bp.IsActive = 1
              AND (
                  EXISTS (SELECT 1 FROM dbo.BusinessPartnerRole r WHERE r.BusinessPartnerId = bp.Id AND r.RoleCode = 'Vendor')
                  OR EXISTS (SELECT 1 FROM logistica.VendorProfile vp WHERE vp.BusinessPartnerId = bp.Id)
              )
        ) THEN 1 ELSE 0 END AS bit);
        """,
        new { BusinessPartnerId = businessPartnerId },
        tx,
        cancellationToken: ct));
  }

  private static async Task<int?> GetExistingVendorDraftAsync(
    DbConnection conn,
    int businessPartnerId,
    IReadOnlyCollection<int> roomIds,
    CancellationToken ct)
  {
    if (roomIds.Count == 0)
    {
      return await conn.QueryFirstOrDefaultAsync<int?>(
        new CommandDefinition(
          """
          SELECT TOP (1) po.Id
          FROM logistica.PurchaseOrder po
          WHERE po.BusinessPartnerId = @BusinessPartnerId
            AND po.[Status] = @Status
            AND NOT EXISTS (
                SELECT 1
                FROM logistica.PurchaseOrderRoomScope scope
                WHERE scope.PurchaseOrderId = po.Id
            )
          ORDER BY ISNULL(po.UpdatedAt, po.CreatedAt) DESC, po.Id DESC;
          """,
          new
          {
            BusinessPartnerId = businessPartnerId,
            Status = PurchaseOrderStatuses.Draft
          },
          cancellationToken: ct));
    }

    return await conn.QueryFirstOrDefaultAsync<int?>(
      new CommandDefinition(
        """
        SELECT TOP (1) po.Id
        FROM logistica.PurchaseOrder po
        WHERE po.BusinessPartnerId = @BusinessPartnerId
          AND po.[Status] = @Status
          AND NOT EXISTS (
              SELECT 1
              FROM logistica.PurchaseOrderRoomScope scope
              WHERE scope.PurchaseOrderId = po.Id
                AND scope.RoomId NOT IN @RoomIds
          )
          AND (
              SELECT COUNT(*)
              FROM logistica.PurchaseOrderRoomScope scope
              WHERE scope.PurchaseOrderId = po.Id
                AND scope.RoomId IN @RoomIds
          ) = @RoomCount
        ORDER BY ISNULL(po.UpdatedAt, po.CreatedAt) DESC, po.Id DESC;
        """,
        new
        {
          BusinessPartnerId = businessPartnerId,
          Status = PurchaseOrderStatuses.Draft,
          RoomIds = roomIds,
          RoomCount = roomIds.Count
        },
        cancellationToken: ct));
  }

  private static async Task<int?> GetVendorDefaultLeadTimeDaysAsync(DbConnection conn, int businessPartnerId, CancellationToken ct)
  {
    return await conn.QueryFirstOrDefaultAsync<int?>(
      new CommandDefinition(
        """
        SELECT vp.DefaultLeadTimeDays
        FROM logistica.VendorProfile vp
        WHERE vp.BusinessPartnerId = @BusinessPartnerId;
        """,
        new { BusinessPartnerId = businessPartnerId },
        cancellationToken: ct));
  }

  private static PurchaseOrderUpsertRequest BuildAutoDraftRequest(
    AutoPurchaseOrderCreateRequest request,
    IReadOnlyList<AutoPurchaseCandidateRow> candidateRows,
    int? defaultLeadTimeDays)
  {
    var lines = candidateRows
      .GroupBy(row => row.MaterialId)
      .OrderBy(group => group.First().MaterialCode, StringComparer.OrdinalIgnoreCase)
      .ThenBy(group => group.First().MaterialDescription, StringComparer.OrdinalIgnoreCase)
      .Select(group =>
      {
        var first = group.First();
        var packSize = NormalizePurchaseQuantity(first.PurchaseQuantity);
        var purchaseUnitName = ResolvePurchaseUnitName(first.PurchaseUnitName, fallbackValue: null);
        var allocations = group
          .OrderBy(item => item.LocationId)
          .Select(item => new PurchaseOrderAllocationUpsertRequest
          {
            LocationId = item.LocationId,
            PlannedQuantity = RequiresWholePurchaseMultiple(packSize, purchaseUnitName)
              ? RoundUpToPurchaseMultiple(item.RawNeedQuantity, packSize)
              : item.RawNeedQuantity
          })
          .ToList();

        return new PurchaseOrderLineUpsertRequest
        {
          MaterialId = first.MaterialId,
          UnitPrice = first.UnitPrice,
          PurchaseQuantitySnapshot = packSize,
          PurchaseUnitNameSnapshot = purchaseUnitName,
          Allocations = allocations
        };
      })
      .ToList();

    return new PurchaseOrderUpsertRequest
    {
      BusinessPartnerId = request.BusinessPartnerId,
      OrderDate = request.OrderDate.Date,
      ExpectedDate = defaultLeadTimeDays.HasValue
        ? request.OrderDate.Date.AddDays(defaultLeadTimeDays.Value)
        : null,
      Lines = lines
    };
  }

  private static async Task<IReadOnlyList<AutoPurchaseCandidateRow>> LoadAutoReplenishmentRowsAsync(
    DbConnection conn,
    int businessPartnerId,
    IReadOnlyCollection<int> roomIds,
    CancellationToken ct)
  {
    var sql = new StringBuilder(
      """
      WITH OpenPurchaseAllocations AS (
          SELECT
              line.MaterialId,
              allocation.LocationId,
              CAST(ISNULL(SUM(allocation.PlannedQuantity - allocation.ReceivedQuantity), 0) AS decimal(18,4)) AS RemainingOpenQuantity
          FROM logistica.PurchaseOrder po
          JOIN logistica.PurchaseOrderLine line
            ON line.PurchaseOrderId = po.Id
          JOIN logistica.PurchaseOrderLineAllocation allocation
            ON allocation.PurchaseOrderLineId = line.Id
          WHERE po.BusinessPartnerId = @BusinessPartnerId
            AND po.[Status] IN @ProjectedStatuses
          GROUP BY line.MaterialId, allocation.LocationId
      )
      SELECT
          sb.MaterialId,
          m.MaterialCode,
          m.[Description] AS MaterialDescription,
          m.VendorCode,
          baseU.UnitName AS BaseUnitName,
          purchaseU.UnitName AS PurchaseUnitName,
          CAST(CASE
              WHEN m.PurchaseQuantity IS NULL OR m.PurchaseQuantity <= 0 THEN 1
              ELSE m.PurchaseQuantity
          END AS decimal(18,4)) AS PurchaseQuantity,
          CAST(m.Price AS decimal(18,4)) AS UnitPrice,
          sb.LocationId,
          location.LocationName,
          location.LocationCode,
          CAST(sb.Quantity AS decimal(18,4)) AS CurrentQuantity,
          CAST(sb.MinQuantity AS decimal(18,4)) AS MinQuantity,
          CAST(sb.MaxQuantity AS decimal(18,4)) AS MaxQuantity,
          CAST(ISNULL(openAlloc.RemainingOpenQuantity, 0) AS decimal(18,4)) AS RemainingOpenQuantity,
          CAST(sb.Quantity + ISNULL(openAlloc.RemainingOpenQuantity, 0) AS decimal(18,4)) AS ProjectedQuantity,
          CAST(sb.MaxQuantity - (sb.Quantity + ISNULL(openAlloc.RemainingOpenQuantity, 0)) AS decimal(18,4)) AS RawNeedQuantity
      FROM logistica.StockBalance sb
      JOIN logistica.Material m
        ON m.Id = sb.MaterialId
      JOIN logistica.Location location
        ON location.Id = sb.LocationId
      LEFT JOIN logistica.UnitOfMeasure baseU
        ON baseU.Id = m.BaseUnitId
      LEFT JOIN logistica.UnitOfMeasure purchaseU
        ON purchaseU.Id = m.PurchaseUnitId
      LEFT JOIN OpenPurchaseAllocations openAlloc
        ON openAlloc.MaterialId = sb.MaterialId
       AND openAlloc.LocationId = sb.LocationId
      WHERE m.BusinessPartnerId = @BusinessPartnerId
        AND m.IsActive = 1
        AND location.IsActive = 1
        AND location.IsInventoryEnabled = 1
        AND ISNULL(sb.IsRemoved, 0) = 0
        AND sb.MinQuantity IS NOT NULL
        AND sb.MaxQuantity IS NOT NULL
      """);

    var parameters = new DynamicParameters();
    parameters.Add("@BusinessPartnerId", businessPartnerId, DbType.Int32);
    parameters.Add("@ProjectedStatuses", PurchaseOrderStatuses.Open);

    if (roomIds.Count > 0)
    {
      sql.AppendLine("  AND location.RoomId IN @RoomIds");
      parameters.Add("@RoomIds", roomIds);
    }

    sql.AppendLine("ORDER BY m.MaterialCode, m.[Description], sb.LocationId;");

    var rows = await conn.QueryAsync<AutoPurchaseCandidateRow>(
      new CommandDefinition(
        sql.ToString(),
        parameters,
        cancellationToken: ct));

    return rows.AsList();
  }

  private static async Task<IReadOnlyList<LookupOptionDto>> LoadSuiteRoomsAsync(DbConnection conn, CancellationToken ct)
  {
    var rooms = await conn.QueryAsync<LookupOptionDto>(
      new CommandDefinition(
        """
        SELECT
            room.ID AS Id,
            room.ROOM_NAME AS Name,
            room.ROOM_TYPE AS Code
        FROM dbo.ROOM room
        WHERE room.ROOM_TYPE = @RoomType
        ORDER BY room.ROOM_NAME, room.ID;
        """,
        new { RoomType = SuiteRoomType },
        cancellationToken: ct));

    return rooms.AsList();
  }

  private static async Task<Dictionary<int, MaterialRow>> LoadMaterialRowsAsync(
    DbConnection conn,
    DbTransaction? tx,
    int businessPartnerId,
    IEnumerable<int> materialIds,
    CancellationToken ct)
  {
    var ids = materialIds.Distinct().ToArray();
    if (ids.Length == 0)
    {
      return [];
    }

    var rows = (await conn.QueryAsync<MaterialRow>(
      new CommandDefinition(
        """
        SELECT
            m.Id,
            m.MaterialCode,
            m.[Description],
            m.VendorCode,
            baseU.UnitName AS BaseUnitName,
            purchaseU.UnitName AS PurchaseUnitName,
            CAST(CASE
                WHEN m.PurchaseQuantity IS NULL OR m.PurchaseQuantity <= 0 THEN 1
                ELSE m.PurchaseQuantity
            END AS decimal(18,4)) AS PurchaseQuantity,
            CAST(m.Price AS decimal(18,4)) AS Price
        FROM logistica.Material m
        LEFT JOIN logistica.UnitOfMeasure baseU
          ON baseU.Id = m.BaseUnitId
        LEFT JOIN logistica.UnitOfMeasure purchaseU
          ON purchaseU.Id = m.PurchaseUnitId
        WHERE m.Id IN @MaterialIds
          AND m.IsActive = 1
          AND m.BusinessPartnerId = @BusinessPartnerId;
        """,
        new
        {
          MaterialIds = ids,
          BusinessPartnerId = businessPartnerId
        },
        tx,
        cancellationToken: ct))).ToList();

    return rows.ToDictionary(row => row.Id);
  }

  private static async Task<Dictionary<int, LocationRow>> LoadLocationRowsAsync(
    DbConnection conn,
    DbTransaction? tx,
    IEnumerable<int> locationIds,
    CancellationToken ct)
  {
    var ids = locationIds.Distinct().ToArray();
    if (ids.Length == 0)
    {
      return [];
    }

    var rows = (await conn.QueryAsync<LocationRow>(
      new CommandDefinition(
        """
        SELECT
            l.Id,
            l.LocationName,
            l.LocationCode
        FROM logistica.Location l
        WHERE l.Id IN @LocationIds
          AND l.IsActive = 1
          AND l.IsInventoryEnabled = 1;
        """,
        new { LocationIds = ids },
        tx,
        cancellationToken: ct))).ToList();

    return rows.ToDictionary(row => row.Id);
  }

  private static async Task<PurchaseOrderStateRow?> GetPurchaseOrderStateAsync(
    DbConnection conn,
    DbTransaction? tx,
    int purchaseOrderId,
    CancellationToken ct)
  {
    return await conn.QueryFirstOrDefaultAsync<PurchaseOrderStateRow>(
      new CommandDefinition(
        """
        SELECT
            po.Id,
            po.[Status] AS [Status]
        FROM logistica.PurchaseOrder po
        WHERE po.Id = @PurchaseOrderId;
        """,
        new { PurchaseOrderId = purchaseOrderId },
        tx,
        cancellationToken: ct));
  }

  private static async Task<bool> PurchaseOrderHasLinesAsync(DbConnection conn, DbTransaction? tx, int purchaseOrderId, CancellationToken ct)
  {
    return await conn.ExecuteScalarAsync<bool>(
      new CommandDefinition(
        """
        SELECT CAST(CASE WHEN EXISTS (
            SELECT 1
            FROM logistica.PurchaseOrderLine line
            WHERE line.PurchaseOrderId = @PurchaseOrderId
        ) THEN 1 ELSE 0 END AS bit);
        """,
        new { PurchaseOrderId = purchaseOrderId },
        tx,
        cancellationToken: ct));
  }

  private static async Task<Dictionary<int, AllocationStateRow>> LoadAllocationRowsAsync(
    DbConnection conn,
    DbTransaction? tx,
    int purchaseOrderId,
    IEnumerable<int> allocationIds,
    CancellationToken ct)
  {
    var ids = allocationIds.Distinct().ToArray();
    if (ids.Length == 0)
    {
      return [];
    }

    var rows = (await conn.QueryAsync<AllocationStateRow>(
      new CommandDefinition(
        """
        SELECT
            allocation.Id AS AllocationId,
            allocation.PurchaseOrderLineId,
            allocation.LocationId,
            location.LocationName,
            line.MaterialId,
            line.MaterialDescriptionSnapshot AS MaterialDescription,
            CAST(allocation.PlannedQuantity AS decimal(18,4)) AS PlannedQuantity,
            CAST(allocation.ReceivedQuantity AS decimal(18,4)) AS ReceivedQuantity
        FROM logistica.PurchaseOrderLineAllocation allocation
        JOIN logistica.PurchaseOrderLine line
          ON line.Id = allocation.PurchaseOrderLineId
        JOIN logistica.Location location
          ON location.Id = allocation.LocationId
        WHERE allocation.Id IN @AllocationIds
          AND line.PurchaseOrderId = @PurchaseOrderId;
        """,
        new
        {
          AllocationIds = ids,
          PurchaseOrderId = purchaseOrderId
        },
        tx,
        cancellationToken: ct))).ToList();

    return rows.ToDictionary(row => row.AllocationId);
  }

  private static async Task<StockBalanceStateRow?> GetStockBalanceStateAsync(
    DbConnection conn,
    DbTransaction? tx,
    int locationId,
    int materialId,
    CancellationToken ct)
  {
    return await conn.QueryFirstOrDefaultAsync<StockBalanceStateRow>(
      new CommandDefinition(
        """
        SELECT
            TOP (1)
            sb.Id,
            CAST(sb.Quantity AS decimal(18,4)) AS Quantity,
            CAST(ISNULL(sb.IsRemoved, 0) AS bit) AS IsRemoved
        FROM logistica.StockBalance sb
        WHERE sb.LocationId = @LocationId
          AND sb.MaterialId = @MaterialId
        ORDER BY sb.Id;
        """,
        new
        {
          LocationId = locationId,
          MaterialId = materialId
        },
        tx,
        cancellationToken: ct));
  }

  private static async Task<decimal> GetRemainingQuantityAsync(DbConnection conn, DbTransaction? tx, int purchaseOrderId, CancellationToken ct)
  {
    return await conn.ExecuteScalarAsync<decimal>(
      new CommandDefinition(
        """
        SELECT CAST(ISNULL(SUM(line.OrderedQuantity - line.ReceivedQuantity), 0) AS decimal(18,4))
        FROM logistica.PurchaseOrderLine line
        WHERE line.PurchaseOrderId = @PurchaseOrderId;
        """,
        new { PurchaseOrderId = purchaseOrderId },
        tx,
        cancellationToken: ct));
  }

  private static async Task UpdatePurchaseOrderStatusAfterReceiptAsync(
    DbConnection conn,
    DbTransaction? tx,
    int purchaseOrderId,
    string actor,
    CancellationToken ct)
  {
    var remainingQuantity = await GetRemainingQuantityAsync(conn, tx, purchaseOrderId, ct);
    var isCompleted = remainingQuantity <= 0;

    await conn.ExecuteAsync(
      new CommandDefinition(
        """
        UPDATE logistica.PurchaseOrder
        SET [Status] = @Status,
            CompletedAt = CASE WHEN @IsCompleted = 1 THEN COALESCE(CompletedAt, SYSUTCDATETIME()) ELSE NULL END,
            CompletedBy = CASE WHEN @IsCompleted = 1 THEN COALESCE(CompletedBy, @CompletedBy) ELSE NULL END,
            UpdatedAt = SYSUTCDATETIME(),
            UpdatedBy = @UpdatedBy
        WHERE Id = @PurchaseOrderId;
        """,
        new
        {
          PurchaseOrderId = purchaseOrderId,
          Status = isCompleted ? PurchaseOrderStatuses.Completed : PurchaseOrderStatuses.PartiallyReceived,
          IsCompleted = isCompleted,
          CompletedBy = actor,
          UpdatedBy = actor
        },
        tx,
        cancellationToken: ct));
  }

  private static decimal NormalizePurchaseQuantity(decimal? preferredValue, decimal? fallbackValue = null)
  {
    if (preferredValue.HasValue && preferredValue.Value > 0m)
    {
      return preferredValue.Value;
    }

    if (fallbackValue.HasValue && fallbackValue.Value > 0m)
    {
      return fallbackValue.Value;
    }

    return 1m;
  }

  private static int[] NormalizeRoomIds(IEnumerable<int>? roomIds)
    => roomIds?
      .Where(roomId => roomId > 0)
      .Distinct()
      .OrderBy(roomId => roomId)
      .ToArray()
      ?? [];

  private static decimal RoundUpToPurchaseMultiple(decimal quantity, decimal purchaseQuantity)
  {
    var normalizedPurchaseQuantity = NormalizePurchaseQuantity(purchaseQuantity);
    if (quantity <= 0m)
    {
      return 0m;
    }

    return decimal.Ceiling(quantity / normalizedPurchaseQuantity) * normalizedPurchaseQuantity;
  }

  private static bool RequiresWholePurchaseMultiple(decimal purchaseQuantity, string? purchaseUnitName)
    => NormalizePurchaseQuantity(purchaseQuantity) > 1m
      || !string.IsNullOrWhiteSpace(purchaseUnitName);

  private static bool IsWholePurchaseMultiple(decimal quantity, decimal purchaseQuantity, string? purchaseUnitName)
  {
    var normalizedPurchaseQuantity = NormalizePurchaseQuantity(purchaseQuantity);
    if (!RequiresWholePurchaseMultiple(normalizedPurchaseQuantity, purchaseUnitName))
    {
      return true;
    }

    var quotient = quantity / normalizedPurchaseQuantity;
    return quotient == decimal.Truncate(quotient);
  }

  private static string? ResolvePurchaseUnitName(string? preferredValue, string? fallbackValue)
  {
    var normalizedPreferred = NullIfWhiteSpace(preferredValue);
    return normalizedPreferred ?? NullIfWhiteSpace(fallbackValue);
  }

  private static string BuildPurchaseMultipleValidationMessage(MaterialRow material, decimal purchaseQuantity)
    => $"La cantidad total para {material.Description} debe ser múltiplo de {BuildPurchaseMultipleRequirementText(material, purchaseQuantity)}.";

  private static string BuildPurchaseAllocationMultipleValidationMessage(MaterialRow material, LocationRow location, decimal purchaseQuantity)
  {
    var locationName = string.IsNullOrWhiteSpace(location.LocationName)
      ? "la ubicación seleccionada"
      : location.LocationName.Trim();
    var locationCode = NullIfWhiteSpace(location.LocationCode);
    var locationLabel = locationCode is null
      ? locationName
      : $"{locationCode} ({locationName})";

    return $"La cantidad planeada para {material.Description} en {locationLabel} debe ser múltiplo de {BuildPurchaseMultipleRequirementText(material, purchaseQuantity)}.";
  }

  private static string BuildPurchaseMultipleRequirementText(MaterialRow material, decimal purchaseQuantity)
  {
    var culture = CultureInfo.CurrentCulture;
    var normalizedPurchaseQuantity = NormalizePurchaseQuantity(purchaseQuantity);
    var purchaseQuantityText = normalizedPurchaseQuantity.ToString("N2", culture);
    var purchaseUnitText = string.IsNullOrWhiteSpace(material.PurchaseUnitName)
      ? "unidad de compra"
      : material.PurchaseUnitName.Trim();
    var baseUnitText = string.IsNullOrWhiteSpace(material.BaseUnitName)
      ? "unidad base"
      : material.BaseUnitName.Trim();

    if (normalizedPurchaseQuantity == 1m && RequiresWholePurchaseMultiple(normalizedPurchaseQuantity, material.PurchaseUnitName))
    {
      return $"1 {purchaseUnitText}";
    }

    return $"{purchaseQuantityText} {baseUnitText} por {purchaseUnitText}";
  }

  private sealed class PurchaseOrderStateRow
  {
    public int Id { get; set; }
    public string Status { get; set; } = string.Empty;
  }

  private sealed class NormalizedRoomScopeResult
  {
    public bool Success { get; private set; }
    public string? ErrorMessage { get; private set; }
    public IReadOnlyList<int> RoomIds { get; private set; } = Array.Empty<int>();

    public static NormalizedRoomScopeResult Ok(IReadOnlyList<int> roomIds)
      => new()
      {
        Success = true,
        RoomIds = roomIds
      };

    public static NormalizedRoomScopeResult Fail(string errorMessage)
      => new()
      {
        Success = false,
        ErrorMessage = errorMessage
      };
  }

  private sealed class MaterialRow
  {
    public int Id { get; set; }
    public string MaterialCode { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? VendorCode { get; set; }
    public string? BaseUnitName { get; set; }
    public string? PurchaseUnitName { get; set; }
    public decimal PurchaseQuantity { get; set; }
    public decimal? Price { get; set; }
  }

  private sealed class LocationRow
  {
    public int Id { get; set; }
    public string LocationName { get; set; } = string.Empty;
    public string LocationCode { get; set; } = string.Empty;
  }

  private sealed class AllocationStateRow
  {
    public int AllocationId { get; set; }
    public int PurchaseOrderLineId { get; set; }
    public int LocationId { get; set; }
    public string LocationName { get; set; } = string.Empty;
    public int MaterialId { get; set; }
    public string MaterialDescription { get; set; } = string.Empty;
    public decimal PlannedQuantity { get; set; }
    public decimal ReceivedQuantity { get; set; }
  }

  private sealed class StockBalanceStateRow
  {
    public int Id { get; set; }
    public decimal Quantity { get; set; }
    public bool IsRemoved { get; set; }
  }

  private sealed class AutoPurchaseCandidateRow
  {
    public int MaterialId { get; set; }
    public string MaterialCode { get; set; } = string.Empty;
    public string MaterialDescription { get; set; } = string.Empty;
    public string? VendorCode { get; set; }
    public string? BaseUnitName { get; set; }
    public string? PurchaseUnitName { get; set; }
    public decimal PurchaseQuantity { get; set; }
    public decimal? UnitPrice { get; set; }
    public int LocationId { get; set; }
    public string LocationName { get; set; } = string.Empty;
    public string? LocationCode { get; set; }
    public decimal CurrentQuantity { get; set; }
    public decimal MinQuantity { get; set; }
    public decimal MaxQuantity { get; set; }
    public decimal RemainingOpenQuantity { get; set; }
    public decimal ProjectedQuantity { get; set; }
    public decimal RawNeedQuantity { get; set; }
  }
}
