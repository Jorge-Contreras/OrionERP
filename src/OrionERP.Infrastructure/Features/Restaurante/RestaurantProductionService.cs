using System.Data;
using System.Data.Common;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using OrionERP.Application.Common;
using OrionERP.Application.Features.Logistica.Shared;
using OrionERP.Application.Features.Restaurante;

namespace OrionERP.Infrastructure.Features.Restaurante;

public sealed class RestaurantProductionService : IRestaurantProductionService
{
  private readonly IDbConnectionFactory _connectionFactory;

  public RestaurantProductionService(IDbConnectionFactory connectionFactory)
  {
    _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
  }

  public async Task<RestaurantProductionWorkspaceDto> GetWorkspaceAsync(string rfc, int siteId, CancellationToken ct = default)
  {
    var normalizedRfc = LogisticsRfc.Require(rfc);
    const string sql =
      """
      SELECT production.Id, production.ProductionCode, production.SiteId, site.[Name] AS SiteName,
             production.ProductMaterialId, material.[Description] AS ProductName, production.BomVersionId,
             versionInfo.VersionNumber AS BomVersionNumber, production.PlannedQuantity, production.ActualQuantity,
             production.UnitId, unitInfo.UnitName, production.OutputLocationId,
             locationInfo.LocationName AS OutputLocationName, outputLot.LotCode AS OutputLotCode,
             outputLot.ExpiresAt AS OutputExpiresAt, production.[Status], production.FrozenTheoreticalCost,
             production.WasteQuantity, production.PlannedAt, production.StartedAt, production.CompletedAt
      FROM logistica.ProductionOrder production
      JOIN restaurante.Site site ON site.Rfc=production.Rfc AND site.Id=production.SiteId
      JOIN logistica.Material material ON material.Rfc=production.Rfc AND material.Id=production.ProductMaterialId
      JOIN logistica.BomVersion versionInfo ON versionInfo.Rfc=production.Rfc AND versionInfo.Id=production.BomVersionId
      JOIN logistica.UnitOfMeasure unitInfo ON unitInfo.Id=production.UnitId
      JOIN logistica.Location locationInfo ON locationInfo.Rfc=production.Rfc AND locationInfo.Id=production.OutputLocationId
      LEFT JOIN logistica.MaterialLot outputLot ON outputLot.Rfc=production.Rfc AND outputLot.Id=production.OutputLotId
      WHERE production.Rfc=@Rfc AND production.SiteId=@SiteId
      ORDER BY CASE production.[Status] WHEN 'Started' THEN 0 WHEN 'Planned' THEN 1 ELSE 2 END,
               production.PlannedAt DESC;

      SELECT versionInfo.Id,
             CONCAT(material.[Description], ' · BOM v', versionInfo.VersionNumber, ' · ',
                    CONVERT(varchar(30), versionInfo.YieldQuantity), ' ', unitInfo.Abbreviation) AS Label
      FROM logistica.BomVersion versionInfo
      JOIN logistica.BomHeader headerInfo ON headerInfo.Rfc=versionInfo.Rfc AND headerInfo.Id=versionInfo.BomHeaderId
      JOIN logistica.Material material ON material.Rfc=headerInfo.Rfc AND material.Id=headerInfo.ProductMaterialId
      JOIN logistica.UnitOfMeasure unitInfo ON unitInfo.Id=versionInfo.YieldUnitId
      WHERE versionInfo.Rfc=@Rfc AND versionInfo.[Status]='Active'
        AND material.FulfillmentMode='MakeToStock' AND material.IsActive=1
      ORDER BY material.[Description], versionInfo.VersionNumber DESC;

      SELECT CAST(locationInfo.Id AS bigint) AS Id,
             CONCAT(locationInfo.LocationName, ' · ', locationInfo.LocationCode) AS Label
      FROM logistica.Location locationInfo
      WHERE locationInfo.Rfc=@Rfc AND locationInfo.IsActive=1 AND locationInfo.IsInventoryEnabled=1
      ORDER BY locationInfo.LocationName, locationInfo.Id;

      -- Sólo los que están atorados: tienen receta activa pero se clasificaron como comprados,
      -- así que nadie puede producirlos. Un MakeToOrder con receta es lo normal en un platillo
      -- que se prepara al momento y no debe aparecer aquí como problema.
      SELECT CAST(material.Id AS bigint) AS Id,
             CONCAT(material.[Description], ' · clasificado como insumo comprado') AS Label
      FROM logistica.Material material
      JOIN logistica.BomHeader headerInfo ON headerInfo.Rfc=material.Rfc AND headerInfo.ProductMaterialId=material.Id
      JOIN logistica.BomVersion versionInfo ON versionInfo.Rfc=headerInfo.Rfc AND versionInfo.BomHeaderId=headerInfo.Id
       AND versionInfo.[Status]='Active'
      WHERE material.Rfc=@Rfc AND material.IsActive=1 AND material.FulfillmentMode='StockItem'
      ORDER BY material.[Description];
      """;
    using var conn = CreateConnection();
    using var multi = await conn.QueryMultipleAsync(new CommandDefinition(sql, new { Rfc = normalizedRfc, SiteId = siteId }, cancellationToken: ct));
    return new RestaurantProductionWorkspaceDto
    {
      Orders = (await multi.ReadAsync<RestaurantProductionOrderDto>()).AsList(),
      ActiveBoms = (await multi.ReadAsync<RestaurantLookupDto>()).AsList(),
      OutputLocations = (await multi.ReadAsync<RestaurantLookupDto>()).AsList(),
      UnproducibleWithRecipe = (await multi.ReadAsync<RestaurantLookupDto>()).AsList()
    };
  }

  public async Task<RestaurantCommandResult> PlanAsync(RestaurantProductionPlanRequest request, string userName, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(request);
    var rfc = LogisticsRfc.Require(request.Rfc);
    if (request.PlannedQuantity <= 0 || string.IsNullOrWhiteSpace(request.IdempotencyKey))
    {
      return RestaurantCommandResult.Fail("La cantidad y la clave de idempotencia son obligatorias.");
    }

    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    await using var tx = await conn.BeginTransactionAsync(IsolationLevel.Serializable, ct);
    try
    {
      var reservationKey = $"PRODUCTION:{request.IdempotencyKey.Trim()}";
      var existing = await conn.ExecuteScalarAsync<Guid?>(new CommandDefinition(
        "SELECT ReferenceId FROM logistica.InventoryReservation WITH (UPDLOCK,HOLDLOCK) WHERE Rfc=@Rfc AND IdempotencyKey=@Key;",
        new { Rfc = rfc, Key = reservationKey }, tx, cancellationToken: ct));
      if (existing.HasValue)
      {
        await tx.CommitAsync(ct);
        return RestaurantCommandResult.Ok("La orden de producción ya había sido planeada.");
      }

      var definition = await conn.QuerySingleOrDefaultAsync<ProductionDefinitionRow>(new CommandDefinition(
        """
        SELECT versionInfo.Id AS BomVersionId, versionInfo.BomHeaderId, versionInfo.YieldQuantity,
               versionInfo.YieldUnitId, ISNULL(versionInfo.FrozenTheoreticalCost, 0) AS TheoreticalCost,
               headerInfo.ProductMaterialId
        FROM logistica.BomVersion versionInfo WITH (UPDLOCK,HOLDLOCK)
        JOIN logistica.BomHeader headerInfo ON headerInfo.Rfc=versionInfo.Rfc AND headerInfo.Id=versionInfo.BomHeaderId
        JOIN logistica.Material material ON material.Rfc=headerInfo.Rfc AND material.Id=headerInfo.ProductMaterialId
        WHERE versionInfo.Rfc=@Rfc AND versionInfo.Id=@BomVersionId AND versionInfo.[Status]='Active'
          AND material.FulfillmentMode='MakeToStock' AND material.IsActive=1;
        """, new { Rfc = rfc, request.BomVersionId }, tx, cancellationToken: ct));
      if (definition is null)
      {
        await tx.RollbackAsync(ct);
        return RestaurantCommandResult.Fail("El BOM no está activo o el producto no está configurado para producción por lote.");
      }
      var referencesValid = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
        """
        SELECT CAST(CASE WHEN EXISTS (SELECT 1 FROM restaurante.Site WHERE Rfc=@Rfc AND Id=@SiteId AND IsEnabled=1)
          AND EXISTS (SELECT 1 FROM logistica.Location WHERE Rfc=@Rfc AND Id=@LocationId AND IsActive=1 AND IsInventoryEnabled=1)
          THEN 1 ELSE 0 END AS bit);
        """, new { Rfc = rfc, request.SiteId, LocationId = request.OutputLocationId }, tx, cancellationToken: ct));
      if (!referencesValid)
      {
        await tx.RollbackAsync(ct);
        return RestaurantCommandResult.Fail("La sede o ubicación de salida no pertenece al RFC activo.");
      }

      var multiplier = request.PlannedQuantity / definition.YieldQuantity;
      var requirements = new Dictionary<int, decimal>();
      await ExpandVersionAsync(conn, tx, rfc, definition.BomVersionId, multiplier, requirements, new HashSet<int>(), 0, ct);
      var productionId = Guid.NewGuid();
      var reservationId = await ReserveAsync(conn, tx, rfc, request.SiteId, productionId, reservationKey, requirements, userName, ct);
      var code = $"PROD-{DateTime.UtcNow:yyyyMMddHHmmss}-{productionId.ToString("N")[..6].ToUpperInvariant()}";
      await conn.ExecuteAsync(new CommandDefinition(
        """
        INSERT INTO logistica.ProductionOrder
          (Id,Rfc,SiteId,ProductionCode,ProductMaterialId,BomVersionId,PlannedQuantity,UnitId,
           OutputLocationId,ReservationId,[Status],FrozenTheoreticalCost,PlannedAt,CreatedBy)
        VALUES
          (@Id,@Rfc,@SiteId,@Code,@ProductMaterialId,@BomVersionId,@PlannedQuantity,@UnitId,
           @OutputLocationId,@ReservationId,'Planned',@Cost,SYSUTCDATETIME(),@CreatedBy);
        """, new
        {
          Id = productionId, Rfc = rfc, request.SiteId, Code = code, definition.ProductMaterialId,
          definition.BomVersionId, request.PlannedQuantity, UnitId = definition.YieldUnitId,
          request.OutputLocationId, ReservationId = reservationId,
          Cost = decimal.Round(definition.TheoreticalCost * multiplier, 6), CreatedBy = userName
        }, tx, cancellationToken: ct));
      await AddEventAsync(conn, tx, rfc, request.SiteId, "ProductionPlanned", productionId, ct);
      await tx.CommitAsync(ct);
      return RestaurantCommandResult.Ok($"Orden {code} planeada y materiales reservados.");
    }
    catch (SqlException ex) when (ex.Number is 2601 or 2627)
    {
      await tx.RollbackAsync(ct);
      return RestaurantCommandResult.Fail("La orden ya existe o la clave de idempotencia fue reutilizada.");
    }
    catch
    {
      await tx.RollbackAsync(ct);
      throw;
    }
  }

  public async Task<RestaurantCommandResult> StartAsync(string rfc, Guid productionOrderId, string userName, CancellationToken ct = default)
  {
    var normalizedRfc = LogisticsRfc.Require(rfc);
    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    await using var tx = await conn.BeginTransactionAsync(IsolationLevel.Serializable, ct);
    try
    {
      var order = await GetLockedOrderAsync(conn, tx, normalizedRfc, productionOrderId, ct);
      if (order is null) return await RollbackFailAsync(tx, "La orden no existe en el RFC seleccionado.", ct);
      if (order.Status == "Started") return await CommitOkAsync(tx, "La producción ya había iniciado.", ct);
      if (order.Status != "Planned") return await RollbackFailAsync(tx, "Sólo una producción planeada puede iniciar.", ct);
      await ConsumeAsync(conn, tx, normalizedRfc, order.ReservationId, userName, ct);
      await conn.ExecuteAsync(new CommandDefinition(
        "UPDATE logistica.ProductionOrder SET [Status]='Started',StartedAt=SYSUTCDATETIME() WHERE Rfc=@Rfc AND Id=@Id;",
        new { Rfc = normalizedRfc, Id = productionOrderId }, tx, cancellationToken: ct));
      await AddEventAsync(conn, tx, normalizedRfc, order.SiteId, "ProductionStarted", productionOrderId, ct);
      await tx.CommitAsync(ct);
      return RestaurantCommandResult.Ok("La producción inició y la reserva se convirtió en consumo.");
    }
    catch { await tx.RollbackAsync(ct); throw; }
  }

  public async Task<RestaurantCommandResult> CompleteAsync(RestaurantProductionCompleteRequest request, string userName, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(request);
    var rfc = LogisticsRfc.Require(request.Rfc);
    if (request.ActualQuantity <= 0 || string.IsNullOrWhiteSpace(request.OutputLotCode))
      return RestaurantCommandResult.Fail("La cantidad real y el lote de salida son obligatorios.");

    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    await using var tx = await conn.BeginTransactionAsync(IsolationLevel.Serializable, ct);
    try
    {
      var order = await GetLockedOrderAsync(conn, tx, rfc, request.ProductionOrderId, ct);
      if (order is null) return await RollbackFailAsync(tx, "La orden no existe en el RFC seleccionado.", ct);
      if (order.Status == "Completed") return await CommitOkAsync(tx, "La producción ya estaba completada.", ct);
      if (order.Status != "Started") return await RollbackFailAsync(tx, "La producción debe estar iniciada antes de completarse.", ct);

      var lotCode = request.OutputLotCode.Trim().ToUpperInvariant();
      var unitCost = decimal.Round(order.FrozenTheoreticalCost / request.ActualQuantity, 6);
      var lotId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
        """
        INSERT INTO logistica.MaterialLot
          (Rfc,MaterialId,LotCode,ManufacturedAt,ExpiresAt,UnitCost,SourceType,CreatedBy)
        VALUES
          (@Rfc,@MaterialId,@LotCode,CONVERT(date,SYSUTCDATETIME()),@ExpiresAt,@UnitCost,'Production',@CreatedBy);
        SELECT CAST(SCOPE_IDENTITY() AS bigint);
        """, new { Rfc = rfc, MaterialId = order.ProductMaterialId, LotCode = lotCode, request.ExpiresAt, UnitCost = unitCost, CreatedBy = userName }, tx, cancellationToken: ct));

      var current = await conn.QuerySingleOrDefaultAsync<OutputBalanceRow>(new CommandDefinition(
        "SELECT Id,Quantity,AverageUnitCost FROM logistica.StockBalance WITH (UPDLOCK,HOLDLOCK) WHERE Rfc=@Rfc AND MaterialId=@MaterialId AND LocationId=@LocationId AND IsRemoved=0;",
        new { Rfc = rfc, MaterialId = order.ProductMaterialId, LocationId = order.OutputLocationId }, tx, cancellationToken: ct));
      int balanceId;
      decimal quantityAfter;
      if (current is null)
      {
        balanceId = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
          """
          INSERT INTO logistica.StockBalance (Rfc,LocationId,MaterialId,Quantity,ReservedQuantity,AverageUnitCost,UpdatedAt,IsRemoved)
          VALUES (@Rfc,@LocationId,@MaterialId,@Quantity,0,@UnitCost,SYSUTCDATETIME(),0);
          SELECT CAST(SCOPE_IDENTITY() AS int);
          """, new { Rfc = rfc, LocationId = order.OutputLocationId, MaterialId = order.ProductMaterialId, Quantity = request.ActualQuantity, UnitCost = unitCost }, tx, cancellationToken: ct));
        quantityAfter = request.ActualQuantity;
      }
      else
      {
        quantityAfter = current.Quantity + request.ActualQuantity;
        var average = quantityAfter == 0 ? unitCost : ((current.Quantity * current.AverageUnitCost) + order.FrozenTheoreticalCost) / quantityAfter;
        await conn.ExecuteAsync(new CommandDefinition(
          "UPDATE logistica.StockBalance SET Quantity=@Quantity,AverageUnitCost=@Average,UpdatedAt=SYSUTCDATETIME() WHERE Rfc=@Rfc AND Id=@Id;",
          new { Rfc = rfc, Id = current.Id, Quantity = quantityAfter, Average = decimal.Round(average, 6) }, tx, cancellationToken: ct));
        balanceId = current.Id;
      }
      await conn.ExecuteAsync(new CommandDefinition(
        """
        INSERT INTO logistica.LotBalance (Rfc,MaterialLotId,MaterialId,LocationId,Quantity,ReservedQuantity)
        VALUES (@Rfc,@LotId,@MaterialId,@LocationId,@Quantity,0);
        INSERT INTO logistica.StockTransaction
          (Rfc,StockBalanceId,LocationId,MaterialId,TransactionType,QuantityDelta,QuantityAfter,ReferenceType,ReferenceId,Notes,PerformedBy)
        VALUES
          (@Rfc,@BalanceId,@LocationId,@MaterialId,'ProductionOutput',@Quantity,@QuantityAfter,'ProductionReservation',@ReservationId,@Notes,@PerformedBy);
        UPDATE logistica.ProductionOrder
        SET [Status]='Completed',ActualQuantity=@Quantity,WasteQuantity=@Waste,OutputLotId=@LotId,
            CompletedAt=SYSUTCDATETIME(),CompletedBy=@PerformedBy
        WHERE Rfc=@Rfc AND Id=@ProductionId;
        """, new
        {
          Rfc = rfc, LotId = lotId, MaterialId = order.ProductMaterialId, LocationId = order.OutputLocationId,
          Quantity = request.ActualQuantity, BalanceId = balanceId, QuantityAfter = quantityAfter,
          order.ReservationId, Notes = $"Salida de producción {order.ProductionCode}", PerformedBy = userName,
          Waste = request.WasteQuantity, ProductionId = request.ProductionOrderId
        }, tx, cancellationToken: ct));
      await AddEventAsync(conn, tx, rfc, order.SiteId, "ProductionCompleted", request.ProductionOrderId, ct);
      await tx.CommitAsync(ct);
      return RestaurantCommandResult.Ok($"Producción completada en el lote {lotCode}.");
    }
    catch (SqlException ex) when (ex.Number is 2601 or 2627)
    {
      await tx.RollbackAsync(ct);
      return RestaurantCommandResult.Fail("El código de lote ya existe para este producto y RFC.");
    }
    catch { await tx.RollbackAsync(ct); throw; }
  }

  public async Task<RestaurantCommandResult> CancelAsync(string rfc, Guid productionOrderId, string userName, CancellationToken ct = default)
  {
    var normalizedRfc = LogisticsRfc.Require(rfc);
    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    await using var tx = await conn.BeginTransactionAsync(IsolationLevel.Serializable, ct);
    try
    {
      var order = await GetLockedOrderAsync(conn, tx, normalizedRfc, productionOrderId, ct);
      if (order is null) return await RollbackFailAsync(tx, "La orden no existe en el RFC seleccionado.", ct);
      if (order.Status != "Planned") return await RollbackFailAsync(tx, "Sólo una orden planeada puede cancelarse; después de iniciar debe registrarse merma.", ct);
      await ReleaseAsync(conn, tx, normalizedRfc, order.ReservationId, ct);
      await conn.ExecuteAsync(new CommandDefinition(
        "UPDATE logistica.ProductionOrder SET [Status]='Cancelled',CompletedAt=SYSUTCDATETIME(),CompletedBy=@UserName WHERE Rfc=@Rfc AND Id=@Id;",
        new { Rfc = normalizedRfc, Id = productionOrderId, UserName = userName }, tx, cancellationToken: ct));
      await AddEventAsync(conn, tx, normalizedRfc, order.SiteId, "ProductionCancelled", productionOrderId, ct);
      await tx.CommitAsync(ct);
      return RestaurantCommandResult.Ok("La orden fue cancelada y sus reservas se liberaron.");
    }
    catch { await tx.RollbackAsync(ct); throw; }
  }

  private static async Task ExpandVersionAsync(DbConnection conn, DbTransaction tx, string rfc, long versionId, decimal multiplier,
    IDictionary<int, decimal> requirements, ISet<int> path, int depth, CancellationToken ct)
  {
    if (depth >= 32) throw new InvalidOperationException("El BOM excede 32 niveles.");
    var rows = (await conn.QueryAsync<ComponentRow>(new CommandDefinition(
      """
      SELECT component.ComponentMaterialId AS MaterialId,
             component.Quantity*(1+component.ExpectedWastePercent/100.0)
               * COALESCE(materialConversion.Factor,globalConversion.Factor,CASE WHEN component.UnitId=material.BaseUnitId THEN 1 END) AS BaseQuantity,
             childVersion.Id AS ChildVersionId, material.FulfillmentMode
      FROM logistica.BomComponent component
      JOIN logistica.Material material ON material.Rfc=component.Rfc AND material.Id=component.ComponentMaterialId
      OUTER APPLY (SELECT TOP(1) Factor FROM logistica.MaterialUnitConversion conversionInfo
                   WHERE conversionInfo.Rfc=component.Rfc AND conversionInfo.MaterialId=component.ComponentMaterialId
                     AND conversionInfo.FromUnitId=component.UnitId AND conversionInfo.ToUnitId=material.BaseUnitId AND conversionInfo.IsActive=1) materialConversion
      OUTER APPLY (SELECT TOP(1) Factor FROM logistica.UnitConversion conversionInfo
                   WHERE conversionInfo.FromUnitId=component.UnitId AND conversionInfo.ToUnitId=material.BaseUnitId AND conversionInfo.IsActive=1) globalConversion
      OUTER APPLY (SELECT TOP(1) child.Id FROM logistica.BomHeader childHeader
                   JOIN logistica.BomVersion child ON child.Rfc=childHeader.Rfc AND child.BomHeaderId=childHeader.Id AND child.[Status]='Active'
                   WHERE childHeader.Rfc=component.Rfc AND childHeader.ProductMaterialId=component.ComponentMaterialId) childVersion
      WHERE component.Rfc=@Rfc AND component.BomVersionId=@VersionId AND component.IsOptional=0;
      """, new { Rfc = rfc, VersionId = versionId }, tx, cancellationToken: ct))).AsList();
    if (rows.Count == 0) throw new InvalidOperationException("El BOM activo no tiene componentes.");
    foreach (var row in rows)
    {
      if (!row.BaseQuantity.HasValue) throw new InvalidOperationException($"Falta conversión para el material {row.MaterialId}.");
      var needed = row.BaseQuantity.Value * multiplier;
      if (row.FulfillmentMode == "MakeToOrder" && row.ChildVersionId.HasValue)
      {
        if (!path.Add(row.MaterialId)) throw new InvalidOperationException("El BOM contiene un ciclo.");
        var childYield = await conn.ExecuteScalarAsync<decimal>(new CommandDefinition(
          "SELECT YieldQuantity FROM logistica.BomVersion WHERE Rfc=@Rfc AND Id=@Id;",
          new { Rfc = rfc, Id = row.ChildVersionId.Value }, tx, cancellationToken: ct));
        await ExpandVersionAsync(conn, tx, rfc, row.ChildVersionId.Value, needed / childYield, requirements, new HashSet<int>(path), depth + 1, ct);
      }
      else
      {
        requirements.TryGetValue(row.MaterialId, out var current);
        requirements[row.MaterialId] = current + needed;
      }
    }
  }

  private static async Task<long> ReserveAsync(DbConnection conn, DbTransaction tx, string rfc, int siteId, Guid productionId,
    string idempotencyKey, IReadOnlyDictionary<int, decimal> requirements, string userName, CancellationToken ct)
  {
    var reservationId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
      """
      INSERT INTO logistica.InventoryReservation (Rfc,SiteId,ReferenceType,ReferenceId,IdempotencyKey,[Status],CreatedBy)
      VALUES (@Rfc,@SiteId,'ProductionOrder',@ReferenceId,@Key,'Reserved',@UserName);
      SELECT CAST(SCOPE_IDENTITY() AS bigint);
      """, new { Rfc = rfc, SiteId = siteId, ReferenceId = productionId, Key = idempotencyKey, UserName = userName }, tx, cancellationToken: ct));
    foreach (var requirement in requirements.Where(item => item.Value > 0))
    {
      var needed = decimal.Round(requirement.Value, 4, MidpointRounding.AwayFromZero);
      var remaining = needed;
      var trackLots = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
        "SELECT TrackLots FROM logistica.Material WHERE Rfc=@Rfc AND Id=@Id;",
        new { Rfc = rfc, Id = requirement.Key }, tx, cancellationToken: ct));
      if (trackLots)
      {
        var lots = (await conn.QueryAsync<AvailabilityRow>(new CommandDefinition(
          """
          SELECT lotBalance.LocationId,lotBalance.MaterialLotId,lotBalance.Quantity-lotBalance.ReservedQuantity AS AvailableQuantity,lot.UnitCost
          FROM logistica.LotBalance lotBalance WITH (UPDLOCK,HOLDLOCK)
          JOIN logistica.MaterialLot lot ON lot.Rfc=lotBalance.Rfc AND lot.Id=lotBalance.MaterialLotId
          LEFT JOIN restaurante.SiteLocationPriority priorityInfo ON priorityInfo.Rfc=lotBalance.Rfc AND priorityInfo.SiteId=@SiteId AND priorityInfo.LocationId=lotBalance.LocationId
          WHERE lotBalance.Rfc=@Rfc AND lotBalance.MaterialId=@MaterialId AND lotBalance.Quantity>lotBalance.ReservedQuantity
            AND lot.IsBlocked=0 AND (lot.ExpiresAt IS NULL OR lot.ExpiresAt>=CONVERT(date,SYSUTCDATETIME()))
          ORDER BY ISNULL(priorityInfo.Priority,2147483647),CASE WHEN lot.ExpiresAt IS NULL THEN 1 ELSE 0 END,lot.ExpiresAt,lot.Id;
          """, new { Rfc = rfc, SiteId = siteId, MaterialId = requirement.Key }, tx, cancellationToken: ct))).AsList();
        foreach (var lot in lots.Where(_ => remaining > 0))
        {
          var take = Math.Min(remaining, lot.AvailableQuantity);
          if (take <= 0) continue;
          await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE logistica.LotBalance SET ReservedQuantity=ReservedQuantity+@Take,UpdatedAt=SYSUTCDATETIME() WHERE Rfc=@Rfc AND MaterialLotId=@LotId AND LocationId=@LocationId;
            UPDATE logistica.StockBalance SET ReservedQuantity=ReservedQuantity+@Take,UpdatedAt=SYSUTCDATETIME() WHERE Rfc=@Rfc AND MaterialId=@MaterialId AND LocationId=@LocationId;
            INSERT INTO logistica.InventoryReservationLine (Rfc,ReservationId,MaterialId,LocationId,MaterialLotId,RequiredQuantity,ReservedQuantity,FrozenUnitCost)
            VALUES (@Rfc,@ReservationId,@MaterialId,@LocationId,@LotId,@Needed,@Take,@Cost);
            """, new { Rfc = rfc, Take = take, LotId = lot.MaterialLotId, lot.LocationId, MaterialId = requirement.Key, ReservationId = reservationId, Needed = needed, Cost = lot.UnitCost }, tx, cancellationToken: ct));
          remaining -= take;
        }
      }
      else
      {
        var balances = (await conn.QueryAsync<AvailabilityRow>(new CommandDefinition(
          """
          SELECT balance.LocationId,CAST(NULL AS bigint) AS MaterialLotId,balance.Quantity-balance.ReservedQuantity AS AvailableQuantity,balance.AverageUnitCost AS UnitCost
          FROM logistica.StockBalance balance WITH (UPDLOCK,HOLDLOCK)
          LEFT JOIN restaurante.SiteLocationPriority priorityInfo ON priorityInfo.Rfc=balance.Rfc AND priorityInfo.SiteId=@SiteId AND priorityInfo.LocationId=balance.LocationId
          WHERE balance.Rfc=@Rfc AND balance.MaterialId=@MaterialId AND balance.IsRemoved=0 AND balance.Quantity>balance.ReservedQuantity
          ORDER BY ISNULL(priorityInfo.Priority,2147483647),balance.LocationId;
          """, new { Rfc = rfc, SiteId = siteId, MaterialId = requirement.Key }, tx, cancellationToken: ct))).AsList();
        foreach (var balance in balances.Where(_ => remaining > 0))
        {
          var take = Math.Min(remaining, balance.AvailableQuantity);
          if (take <= 0) continue;
          await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE logistica.StockBalance SET ReservedQuantity=ReservedQuantity+@Take,UpdatedAt=SYSUTCDATETIME() WHERE Rfc=@Rfc AND MaterialId=@MaterialId AND LocationId=@LocationId;
            INSERT INTO logistica.InventoryReservationLine (Rfc,ReservationId,MaterialId,LocationId,RequiredQuantity,ReservedQuantity,FrozenUnitCost)
            VALUES (@Rfc,@ReservationId,@MaterialId,@LocationId,@Needed,@Take,@Cost);
            """, new { Rfc = rfc, Take = take, MaterialId = requirement.Key, balance.LocationId, ReservationId = reservationId, Needed = needed, Cost = balance.UnitCost }, tx, cancellationToken: ct));
          remaining -= take;
        }
      }
      if (remaining > 0) throw new InvalidOperationException($"Inventario insuficiente para producir. Material {requirement.Key}, faltan {remaining:N4}.");
    }
    return reservationId;
  }

  private static async Task ConsumeAsync(DbConnection conn, DbTransaction tx, string rfc, long reservationId, string userName, CancellationToken ct)
  {
    var status = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
      "SELECT [Status] FROM logistica.InventoryReservation WITH (UPDLOCK,HOLDLOCK) WHERE Rfc=@Rfc AND Id=@Id;", new { Rfc = rfc, Id = reservationId }, tx, cancellationToken: ct));
    if (status == "Consumed") return;
    if (status != "Reserved") throw new InvalidOperationException("La reserva ya no está disponible.");
    var lines = (await conn.QueryAsync<ReservationLineRow>(new CommandDefinition(
      "SELECT Id,MaterialId,LocationId,MaterialLotId,ReservedQuantity FROM logistica.InventoryReservationLine WHERE Rfc=@Rfc AND ReservationId=@Id;",
      new { Rfc = rfc, Id = reservationId }, tx, cancellationToken: ct))).AsList();
    foreach (var line in lines)
    {
      var balance = await conn.QuerySingleAsync<OutputBalanceRow>(new CommandDefinition(
        "SELECT Id,Quantity,AverageUnitCost FROM logistica.StockBalance WITH (UPDLOCK,HOLDLOCK) WHERE Rfc=@Rfc AND MaterialId=@MaterialId AND LocationId=@LocationId;",
        new { Rfc = rfc, line.MaterialId, line.LocationId }, tx, cancellationToken: ct));
      await conn.ExecuteAsync(new CommandDefinition(
        """
        UPDATE logistica.StockBalance SET Quantity=Quantity-@Quantity,ReservedQuantity=ReservedQuantity-@Quantity,UpdatedAt=SYSUTCDATETIME() WHERE Rfc=@Rfc AND Id=@BalanceId;
        INSERT INTO logistica.StockTransaction (Rfc,StockBalanceId,LocationId,MaterialId,TransactionType,QuantityDelta,QuantityAfter,ReferenceType,ReferenceId,Notes,PerformedBy)
        VALUES (@Rfc,@BalanceId,@LocationId,@MaterialId,'ProductionConsumption',-@Quantity,@After,'ProductionReservation',@ReservationId,'Consumo de producción',@UserName);
        UPDATE logistica.InventoryReservationLine SET ConsumedQuantity=@Quantity WHERE Rfc=@Rfc AND Id=@LineId;
        """, new { Rfc = rfc, Quantity = line.ReservedQuantity, BalanceId = balance.Id, line.LocationId, line.MaterialId, After = balance.Quantity-line.ReservedQuantity, ReservationId = reservationId, UserName = userName, LineId = line.Id }, tx, cancellationToken: ct));
      if (line.MaterialLotId.HasValue)
        await conn.ExecuteAsync(new CommandDefinition(
          "UPDATE logistica.LotBalance SET Quantity=Quantity-@Quantity,ReservedQuantity=ReservedQuantity-@Quantity,UpdatedAt=SYSUTCDATETIME() WHERE Rfc=@Rfc AND MaterialLotId=@LotId AND LocationId=@LocationId;",
          new { Rfc = rfc, Quantity = line.ReservedQuantity, LotId = line.MaterialLotId.Value, line.LocationId }, tx, cancellationToken: ct));
    }
    await conn.ExecuteAsync(new CommandDefinition(
      "UPDATE logistica.InventoryReservation SET [Status]='Consumed',ConsumedAt=SYSUTCDATETIME() WHERE Rfc=@Rfc AND Id=@Id;",
      new { Rfc = rfc, Id = reservationId }, tx, cancellationToken: ct));
  }

  private static async Task ReleaseAsync(DbConnection conn, DbTransaction tx, string rfc, long reservationId, CancellationToken ct)
  {
    var status = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
      "SELECT [Status] FROM logistica.InventoryReservation WITH (UPDLOCK,HOLDLOCK) WHERE Rfc=@Rfc AND Id=@Id;", new { Rfc = rfc, Id = reservationId }, tx, cancellationToken: ct));
    if (status != "Reserved") return;
    var lines = (await conn.QueryAsync<ReservationLineRow>(new CommandDefinition(
      "SELECT Id,MaterialId,LocationId,MaterialLotId,ReservedQuantity FROM logistica.InventoryReservationLine WHERE Rfc=@Rfc AND ReservationId=@Id;",
      new { Rfc = rfc, Id = reservationId }, tx, cancellationToken: ct))).AsList();
    foreach (var line in lines)
    {
      await conn.ExecuteAsync(new CommandDefinition(
        "UPDATE logistica.StockBalance SET ReservedQuantity=ReservedQuantity-@Quantity,UpdatedAt=SYSUTCDATETIME() WHERE Rfc=@Rfc AND MaterialId=@MaterialId AND LocationId=@LocationId;",
        new { Rfc = rfc, Quantity = line.ReservedQuantity, line.MaterialId, line.LocationId }, tx, cancellationToken: ct));
      if (line.MaterialLotId.HasValue)
        await conn.ExecuteAsync(new CommandDefinition(
          "UPDATE logistica.LotBalance SET ReservedQuantity=ReservedQuantity-@Quantity,UpdatedAt=SYSUTCDATETIME() WHERE Rfc=@Rfc AND MaterialLotId=@LotId AND LocationId=@LocationId;",
          new { Rfc = rfc, Quantity = line.ReservedQuantity, LotId = line.MaterialLotId.Value, line.LocationId }, tx, cancellationToken: ct));
    }
    await conn.ExecuteAsync(new CommandDefinition(
      "UPDATE logistica.InventoryReservation SET [Status]='Released',ReleasedAt=SYSUTCDATETIME() WHERE Rfc=@Rfc AND Id=@Id;",
      new { Rfc = rfc, Id = reservationId }, tx, cancellationToken: ct));
  }

  private static Task<ProductionOrderRow?> GetLockedOrderAsync(DbConnection conn, DbTransaction tx, string rfc, Guid id, CancellationToken ct)
    => conn.QuerySingleOrDefaultAsync<ProductionOrderRow>(new CommandDefinition(
      "SELECT Id,SiteId,ProductionCode,ProductMaterialId,OutputLocationId,ReservationId,[Status],FrozenTheoreticalCost FROM logistica.ProductionOrder WITH (UPDLOCK,HOLDLOCK) WHERE Rfc=@Rfc AND Id=@Id;",
      new { Rfc = rfc, Id = id }, tx, cancellationToken: ct));

  private static Task AddEventAsync(DbConnection conn, DbTransaction tx, string rfc, int siteId, string eventType, Guid id, CancellationToken ct)
    => conn.ExecuteAsync(new CommandDefinition(
      "INSERT INTO restaurante.EventOutbox (Rfc,SiteId,EventType,AggregateId,Payload) VALUES (@Rfc,@SiteId,@Type,@Id,@Payload);",
      new { Rfc = rfc, SiteId = siteId, Type = eventType, Id = id.ToString(), Payload = JsonSerializer.Serialize(new { productionOrderId = id }) }, tx, cancellationToken: ct));

  private static async Task<RestaurantCommandResult> RollbackFailAsync(DbTransaction tx, string message, CancellationToken ct)
  { await tx.RollbackAsync(ct); return RestaurantCommandResult.Fail(message); }

  private static async Task<RestaurantCommandResult> CommitOkAsync(DbTransaction tx, string message, CancellationToken ct)
  { await tx.CommitAsync(ct); return RestaurantCommandResult.Ok(message); }

  private DbConnection CreateConnection()
    => _connectionFactory.Create() as DbConnection ?? throw new InvalidOperationException("La fábrica no devolvió una DbConnection.");

  private sealed class ProductionDefinitionRow
  { public long BomVersionId { get; set; } public long BomHeaderId { get; set; } public decimal YieldQuantity { get; set; } public int YieldUnitId { get; set; } public decimal TheoreticalCost { get; set; } public int ProductMaterialId { get; set; } }
  private sealed class ComponentRow
  { public int MaterialId { get; set; } public decimal? BaseQuantity { get; set; } public long? ChildVersionId { get; set; } public string FulfillmentMode { get; set; } = string.Empty; }
  private sealed class AvailabilityRow
  { public int LocationId { get; set; } public long? MaterialLotId { get; set; } public decimal AvailableQuantity { get; set; } public decimal UnitCost { get; set; } }
  private sealed class ReservationLineRow
  { public long Id { get; set; } public int MaterialId { get; set; } public int LocationId { get; set; } public long? MaterialLotId { get; set; } public decimal ReservedQuantity { get; set; } }
  private sealed class ProductionOrderRow
  { public Guid Id { get; set; } public int SiteId { get; set; } public string ProductionCode { get; set; } = string.Empty; public int ProductMaterialId { get; set; } public int OutputLocationId { get; set; } public long ReservationId { get; set; } public string Status { get; set; } = string.Empty; public decimal FrozenTheoreticalCost { get; set; } }
  private sealed class OutputBalanceRow
  { public int Id { get; set; } public decimal Quantity { get; set; } public decimal AverageUnitCost { get; set; } }
}
