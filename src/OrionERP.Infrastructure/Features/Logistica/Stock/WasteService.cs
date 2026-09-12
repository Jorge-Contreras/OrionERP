using System.Data;
using System.Data.Common;
using Dapper;
using OrionERP.Application.Common;
using OrionERP.Application.Features.Logistica.Shared;
using OrionERP.Application.Features.Logistica.Stock;
using OrionERP.Application.Features.Reservaciones;
using OrionERP.Infrastructure.Features.Logistica.Shared;
using OrionERP.Infrastructure.Features.Logistica.Support;

namespace OrionERP.Infrastructure.Features.Logistica.Stock;

/// <summary>
/// Baja de producto del inventario. Escribe el mismo documento que la pantalla de
/// movimientos —<c>logistica.InventoryAdjustment</c> con <c>AdjustmentType='Waste'</c>— y le
/// agrega el flujo de revisión y la reversa.
/// </summary>
/// <remarks>
/// El alcance es RFC más visibilidad de ubicación, sin dimensión de sede. No es un descuido:
/// <c>logistica.Location</c> no tiene <c>SiteId</c>, y exigir una sede de Hospedaje dejaría
/// sin poder registrar merma a las empresas que sólo operan Restaurante.
/// </remarks>
public sealed class WasteService : IWasteService
{
  private const int MaxEvidenceBytes = 10 * 1024 * 1024;
  private const string WasteType = "Waste";
  private const string WasteTransaction = "Waste";
  private const string WasteReversalTransaction = "WasteReversal";

  /// <summary>Días hacia atrás que admite la captura: cubre el cierre del turno de anoche.</summary>
  private const int BackdateDayLimit = 7;

  private readonly IDbConnectionFactory _connectionFactory;
  private readonly IHospitalityScopeAccessor? _hospitalityScope;

  public WasteService(IDbConnectionFactory connectionFactory, IHospitalityScopeAccessor? hospitalityScope = null)
  {
    _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    _hospitalityScope = hospitalityScope;
  }

  /// <summary>
  /// Documentos de merma que la sesión puede ver. Se exige que <em>todas</em> las partidas
  /// estén en el alcance: mostrar un documento con partidas de otra sede filtradas dejaría un
  /// costo total que no cuadra con lo que se enseña.
  /// </summary>
  private static string VisibleWasteScopeSql { get; } =
    $$"""
    doc.Rfc=@Rfc AND doc.AdjustmentType='{{WasteType}}'
      AND NOT EXISTS (SELECT 1 FROM logistica.InventoryAdjustmentLine outside
        WHERE outside.Rfc=doc.Rfc AND outside.AdjustmentId=doc.Id
          AND NOT {{LogisticsLocationScope.ForLocationIdSql("outside.LocationId","outside.Rfc")}})
    """;

  /// <summary>
  /// Costo neto: excluye los documentos reversados y sus reversas, que se cancelan entre sí.
  /// Contar ambos inflaría el indicador al doble de lo que de verdad se tiró.
  /// </summary>
  private static string NetWasteScopeSql { get; } =
    $$"""
    {{VisibleWasteScopeSql}}
      AND doc.[Status]<>'{{WasteStatuses.Reversed}}' AND doc.ReversalOfAdjustmentId IS NULL
    """;

  private static string LineCostSql =>
    """
    (SELECT ISNULL(SUM(ABS(line.QuantityDelta)*line.FrozenUnitCost),0)
     FROM logistica.InventoryAdjustmentLine line
     WHERE line.Rfc=doc.Rfc AND line.AdjustmentId=doc.Id)
    """;

  /// <summary>
  /// Dapper de este repositorio sólo entiende <see cref="DateOnly"/> cuando el host registró el
  /// manejador correspondiente, y los hosts públicos no lo hacen. La conversión va aquí para que
  /// el servicio funcione igual en todos.
  /// </summary>
  private static DateTime AsDate(DateOnly value) => value.ToDateTime(TimeOnly.MinValue);

  public async Task<WasteWorkspaceDto> GetWorkspaceAsync(string rfc, CancellationToken ct = default)
  {
    var normalizedRfc = LogisticsRfc.Require(rfc);
    var today = DateOnly.FromDateTime(DateTime.Now);
    var sql =
      $$"""
      {{InventoryOptionsQuery.Sql}}

      SELECT
        TodayCost=ISNULL(SUM(CASE WHEN doc.WasteDate=@Today THEN doc.Cost END),0),
        WeekCost=ISNULL(SUM(CASE WHEN doc.WasteDate>=@WeekStart THEN doc.Cost END),0),
        PendingReviewCount=ISNULL(SUM(CASE WHEN doc.[Status]='{{WasteStatuses.PendingReview}}' THEN 1 END),0)
      FROM (SELECT doc.[Status],COALESCE(doc.OccurredOn,CONVERT(date,doc.CreatedAt)) AS WasteDate,
                   {{LineCostSql}} AS Cost
            FROM logistica.InventoryAdjustment doc
            WHERE {{NetWasteScopeSql}}) doc;

      SELECT TOP(1) doc.ReasonCode
      FROM (SELECT doc.ReasonCode,{{LineCostSql}} AS Cost
            FROM logistica.InventoryAdjustment doc
            WHERE {{NetWasteScopeSql}}
              AND COALESCE(doc.OccurredOn,CONVERT(date,doc.CreatedAt))>=@MonthStart) doc
      GROUP BY doc.ReasonCode
      ORDER BY SUM(doc.Cost) DESC;
      """;
    await using var conn = await LogisticsLocationScope.OpenAsync(_connectionFactory, _hospitalityScope, ct);
    await LogisticsLocationScope.EnsureRfcAsync(conn, null, normalizedRfc, ct);
    using var multi = await conn.QueryMultipleAsync(new CommandDefinition(sql, new
    {
      Rfc = normalizedRfc,
      Today = AsDate(today),
      WeekStart = AsDate(today.AddDays(-6)),
      MonthStart = AsDate(today.AddDays(-29))
    }, cancellationToken: ct));
    var options = await InventoryOptionsQuery.ReadAsync(multi);
    var totals = await multi.ReadSingleAsync<WasteTotalsRow>();
    return new WasteWorkspaceDto
    {
      Locations = options.Locations,
      Balances = options.Balances,
      Lots = options.Lots,
      TodayCost = totals.TodayCost,
      WeekCost = totals.WeekCost,
      PendingReviewCount = totals.PendingReviewCount,
      TopReasonCode = await multi.ReadSingleOrDefaultAsync<string>() ?? string.Empty
    };
  }

  public async Task<WasteHistoryDto> GetHistoryAsync(WasteHistoryQuery query, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(query);
    var rfc = LogisticsRfc.Require(query.Rfc);
    var today = DateOnly.FromDateTime(DateTime.Now);
    var pageSize = Math.Clamp(query.PageSize, 1, 200);
    var page = Math.Max(1, query.Page);
    var sql =
      $$"""
      DROP TABLE IF EXISTS #WasteFiltered;
      DROP TABLE IF EXISTS #WastePage;

      SELECT doc.Id,
             WasteDate=COALESCE(doc.OccurredOn,CONVERT(date,doc.CreatedAt)),
             TotalCost=CONVERT(decimal(18,4),{{LineCostSql}})
      INTO #WasteFiltered
      FROM logistica.InventoryAdjustment doc
      WHERE {{VisibleWasteScopeSql}}
        AND COALESCE(doc.OccurredOn,CONVERT(date,doc.CreatedAt)) BETWEEN @FromDate AND @ToDate
        AND (@ReasonCode IS NULL OR doc.ReasonCode=@ReasonCode)
        AND (@Status IS NULL OR doc.[Status]=@Status)
        AND (@LocationId IS NULL OR EXISTS (SELECT 1 FROM logistica.InventoryAdjustmentLine scoped
              WHERE scoped.Rfc=doc.Rfc AND scoped.AdjustmentId=doc.Id AND scoped.LocationId=@LocationId));

      SELECT TotalCount=COUNT(*),TotalCost=ISNULL(SUM(TotalCost),0) FROM #WasteFiltered;

      SELECT filtered.Id,filtered.TotalCost
      INTO #WastePage
      FROM #WasteFiltered filtered
      ORDER BY filtered.WasteDate DESC,filtered.Id DESC
      OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY;

      SELECT doc.Id,doc.AdjustmentCode AS WasteCode,doc.ReasonCode,doc.Reason,doc.[Status],
             doc.OccurredOn,doc.CreatedAt,doc.CreatedBy,doc.ApprovedBy,doc.ApprovedAt,
             doc.ReversedByAdjustmentId,doc.ReversalOfAdjustmentId,
             HasEvidence=CONVERT(bit,CASE WHEN DATALENGTH(doc.Evidence)>0 THEN 1 ELSE 0 END),
             EvidenceFileName=ISNULL(doc.EvidenceFileName,''),
             paged.TotalCost
      FROM #WastePage paged
      JOIN logistica.InventoryAdjustment doc ON doc.Rfc=@Rfc AND doc.Id=paged.Id
      ORDER BY COALESCE(doc.OccurredOn,CONVERT(date,doc.CreatedAt)) DESC,doc.Id DESC;

      SELECT line.Id,line.AdjustmentId,line.MaterialId,line.LocationId,line.QuantityDelta,line.FrozenUnitCost,
             material.MaterialCode,material.[Description] AS MaterialName,
             UnitCode=ISNULL(unitInfo.Abbreviation,''),
             locationInfo.LocationName,lotInfo.LotCode
      FROM #WastePage paged
      JOIN logistica.InventoryAdjustmentLine line ON line.Rfc=@Rfc AND line.AdjustmentId=paged.Id
      JOIN logistica.Material material ON material.Rfc=line.Rfc AND material.Id=line.MaterialId
      JOIN logistica.Location locationInfo ON locationInfo.Rfc=line.Rfc AND locationInfo.Id=line.LocationId
      LEFT JOIN logistica.UnitOfMeasure unitInfo ON unitInfo.Id=material.BaseUnitId
      LEFT JOIN logistica.MaterialLot lotInfo ON lotInfo.Rfc=line.Rfc AND lotInfo.Id=line.MaterialLotId
      ORDER BY line.AdjustmentId,material.[Description],line.Id;

      DROP TABLE #WastePage;
      DROP TABLE #WasteFiltered;
      """;
    await using var conn = await LogisticsLocationScope.OpenAsync(_connectionFactory, _hospitalityScope, ct);
    await LogisticsLocationScope.EnsureRfcAsync(conn, null, rfc, ct);
    using var multi = await conn.QueryMultipleAsync(new CommandDefinition(sql, new
    {
      Rfc = rfc,
      FromDate = AsDate(query.FromDate ?? today.AddDays(-29)),
      ToDate = AsDate(query.ToDate ?? today),
      ReasonCode = string.IsNullOrWhiteSpace(query.ReasonCode) ? null : WasteReasonCatalog.Normalize(query.ReasonCode),
      Status = string.IsNullOrWhiteSpace(query.Status) ? null : query.Status.Trim(),
      LocationId = query.LocationId > 0 ? query.LocationId : null,
      Skip = (page - 1) * pageSize,
      Take = pageSize
    }, cancellationToken: ct));
    var totals = await multi.ReadSingleAsync<WasteHistoryTotalsRow>();
    var documents = (await multi.ReadAsync<WasteDocumentDto>()).AsList();
    var lines = (await multi.ReadAsync<WasteLineRow>()).AsList();
    foreach (var document in documents)
      document.Lines = lines.Where(line => line.AdjustmentId == document.Id).Select(line => line.ToDto()).ToList();
    return new WasteHistoryDto
    {
      Documents = documents,
      TotalCount = totals.TotalCount,
      TotalCost = totals.TotalCost
    };
  }

  public async Task<LogisticsCommandResult> PostAsync(
    WasteCreateRequest request,
    WasteActor actor,
    CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(request);
    ArgumentNullException.ThrowIfNull(actor);
    var rfc = LogisticsRfc.Require(request.Rfc);
    if (string.IsNullOrWhiteSpace(request.WasteCode))
      return LogisticsCommandResult.Fail("La merma requiere folio.");
    var reason = WasteReasonCatalog.Find(request.ReasonCode);
    if (reason is null)
      return LogisticsCommandResult.Fail("Elige un motivo de merma de la lista.");
    if (reason.RequiresNote && string.IsNullOrWhiteSpace(request.Reason))
      return LogisticsCommandResult.Fail($"El motivo «{reason.Label}» necesita que escribas qué pasó.");
    if (request.Evidence.Length == 0 || string.IsNullOrWhiteSpace(request.EvidenceFileName))
      return LogisticsCommandResult.Fail("Adjunta una foto o un PDF como evidencia de la merma.");
    if (request.Evidence.Length > MaxEvidenceBytes)
      return LogisticsCommandResult.Fail("La evidencia no puede pasar de 10 MB.");
    if (string.IsNullOrWhiteSpace(actor.UserName))
      return LogisticsCommandResult.Fail("No se pudo identificar quién registra la merma.");

    var today = DateOnly.FromDateTime(DateTime.Now);
    var occurredOn = request.OccurredOn ?? today;
    if (occurredOn > today)
      return LogisticsCommandResult.Fail("La merma no puede tener una fecha futura.");
    if (occurredOn < today.AddDays(-BackdateDayLimit))
      return LogisticsCommandResult.Fail($"La merma sólo se puede registrar hasta {BackdateDayLimit} días atrás. Usa un ajuste de inventario para algo más viejo.");

    if (request.Lines.Count == 0 || request.Lines.Any(line =>
          line.MaterialId <= 0 || line.LocationId <= 0 || line.Quantity <= 0))
      return LogisticsCommandResult.Fail("Cada partida necesita ubicación, material y una cantidad mayor que cero.");
    var lines = request.Lines
      .GroupBy(line => (line.MaterialId, line.LocationId, line.MaterialLotId))
      .Select(group => new WasteLineRequest
      {
        MaterialId = group.Key.MaterialId,
        LocationId = group.Key.LocationId,
        MaterialLotId = group.Key.MaterialLotId,
        Quantity = group.Sum(line => line.Quantity)
      })
      .ToList();

    var code = request.WasteCode.Trim().ToUpperInvariant();
    var notes = string.IsNullOrWhiteSpace(request.Reason) ? reason.Label : request.Reason.Trim();
    var status = actor.IsSupervisor ? WasteStatuses.Approved : WasteStatuses.PendingReview;

    await using var conn = await LogisticsLocationScope.OpenAsync(_connectionFactory, _hospitalityScope, ct);
    await LogisticsLocationScope.EnsureRfcAsync(conn, null, rfc, ct);
    await using var tx = await conn.BeginTransactionAsync(IsolationLevel.Serializable, ct);
    try
    {
      foreach (var locationId in lines.Select(line => line.LocationId).Distinct())
        await LogisticsLocationScope.EnsureLocationAsync(conn, tx, locationId, ct);
      await EnsureCodeBelongsToScopeAsync(conn, tx, rfc, code, ct);
      var existing = await conn.ExecuteScalarAsync<long?>(new CommandDefinition(
        "SELECT Id FROM logistica.InventoryAdjustment WITH (UPDLOCK,HOLDLOCK) WHERE Rfc=@Rfc AND AdjustmentCode=@Code;",
        new { Rfc = rfc, Code = code }, tx, cancellationToken: ct));
      if (existing.HasValue)
      {
        await tx.CommitAsync(ct);
        return LogisticsCommandResult.Ok("Esta merma ya había quedado registrada.", checked((int)existing.Value));
      }

      var adjustmentId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
        """
        INSERT INTO logistica.InventoryAdjustment
          (Rfc,AdjustmentCode,AdjustmentType,[Status],ReasonCode,Reason,OccurredOn,Evidence,EvidenceFileName,CreatedBy)
        VALUES
          (@Rfc,@Code,@Type,'Draft',@ReasonCode,@Reason,@OccurredOn,@Evidence,@EvidenceFileName,@UserName);
        SELECT CAST(SCOPE_IDENTITY() AS bigint);
        """, new
        {
          Rfc = rfc,
          Code = code,
          Type = WasteType,
          ReasonCode = reason.Code,
          Reason = notes,
          OccurredOn = AsDate(occurredOn),
          request.Evidence,
          EvidenceFileName = request.EvidenceFileName.Trim(),
          UserName = actor.UserName
        }, tx, cancellationToken: ct));

      foreach (var line in lines)
        await InventoryAdjustmentWriter.ApplyLineAsync(conn, tx, rfc, adjustmentId, WasteTransaction, "La merma",
          line.MaterialId, line.LocationId, line.MaterialLotId, -line.Quantity,
          frozenUnitCost: null, notes, actor.UserName, ct);

      await conn.ExecuteAsync(new CommandDefinition(
        """
        UPDATE logistica.InventoryAdjustment
        SET [Status]=@Status,
            ApprovedAt=CASE WHEN @Status=@ApprovedStatus THEN SYSUTCDATETIME() END,
            ApprovedBy=CASE WHEN @Status=@ApprovedStatus THEN @UserName END
        WHERE Rfc=@Rfc AND Id=@Id;
        """, new
        {
          Rfc = rfc,
          Id = adjustmentId,
          Status = status,
          ApprovedStatus = WasteStatuses.Approved,
          UserName = actor.UserName
        }, tx, cancellationToken: ct));
      await tx.CommitAsync(ct);
      return LogisticsCommandResult.Ok(
        actor.IsSupervisor
          ? "La merma quedó aplicada al inventario."
          : "La merma quedó aplicada al inventario y en espera de revisión del supervisor.",
        checked((int)adjustmentId));
    }
    catch
    {
      await tx.RollbackAsync(ct);
      throw;
    }
  }

  public async Task<LogisticsCommandResult> ApproveAsync(
    string rfc,
    long adjustmentId,
    string userName,
    CancellationToken ct = default)
  {
    var normalizedRfc = LogisticsRfc.Require(rfc);
    if (string.IsNullOrWhiteSpace(userName))
      return LogisticsCommandResult.Fail("No se pudo identificar quién revisa la merma.");

    await using var conn = await LogisticsLocationScope.OpenAsync(_connectionFactory, _hospitalityScope, ct);
    await LogisticsLocationScope.EnsureRfcAsync(conn, null, normalizedRfc, ct);
    await using var tx = await conn.BeginTransactionAsync(IsolationLevel.Serializable, ct);
    try
    {
      var document = await LoadHeaderAsync(conn, tx, normalizedRfc, adjustmentId, ct);
      if (document is null)
      {
        await tx.RollbackAsync(ct);
        return LogisticsCommandResult.Fail("Esa merma ya no existe.");
      }
      if (document.Status != WasteStatuses.PendingReview)
      {
        await tx.RollbackAsync(ct);
        return LogisticsCommandResult.Fail($"Sólo se revisa una merma pendiente; ésta está {WasteStatuses.LabelFor(document.Status).ToLowerInvariant()}.");
      }
      // Quien captura no cierra su propia revisión: es el único control que aporta el flujo.
      if (string.Equals(document.CreatedBy, userName, StringComparison.OrdinalIgnoreCase))
      {
        await tx.RollbackAsync(ct);
        return LogisticsCommandResult.Fail("No puedes aprobar una merma que tú registraste. Pídelo a otro supervisor.");
      }
      await EnsureDocumentInScopeAsync(conn, tx, normalizedRfc, adjustmentId, ct);

      await conn.ExecuteAsync(new CommandDefinition(
        """
        UPDATE logistica.InventoryAdjustment
        SET [Status]=@Status,ApprovedAt=SYSUTCDATETIME(),ApprovedBy=@UserName
        WHERE Rfc=@Rfc AND Id=@Id;
        """, new { Rfc = normalizedRfc, Id = adjustmentId, Status = WasteStatuses.Approved, UserName = userName }, tx, cancellationToken: ct));
      await tx.CommitAsync(ct);
      return LogisticsCommandResult.Ok($"Merma {document.AdjustmentCode} revisada.", checked((int)adjustmentId));
    }
    catch
    {
      await tx.RollbackAsync(ct);
      throw;
    }
  }

  public async Task<LogisticsCommandResult> ReverseAsync(
    WasteReversalRequest request,
    string userName,
    CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(request);
    var rfc = LogisticsRfc.Require(request.Rfc);
    if (string.IsNullOrWhiteSpace(request.Reason))
      return LogisticsCommandResult.Fail("Escribe el motivo de la reversa: sin motivo no es auditable.");
    if (string.IsNullOrWhiteSpace(userName))
      return LogisticsCommandResult.Fail("No se pudo identificar quién reversa la merma.");
    var notes = request.Reason.Trim();

    await using var conn = await LogisticsLocationScope.OpenAsync(_connectionFactory, _hospitalityScope, ct);
    await LogisticsLocationScope.EnsureRfcAsync(conn, null, rfc, ct);
    await using var tx = await conn.BeginTransactionAsync(IsolationLevel.Serializable, ct);
    try
    {
      var document = await LoadHeaderAsync(conn, tx, rfc, request.AdjustmentId, ct);
      if (document is null)
      {
        await tx.RollbackAsync(ct);
        return LogisticsCommandResult.Fail("Esa merma ya no existe.");
      }
      if (document.ReversalOfAdjustmentId.HasValue)
      {
        await tx.RollbackAsync(ct);
        return LogisticsCommandResult.Fail("Una reversa no se reversa. Registra una merma nueva si hace falta.");
      }
      if (document.ReversedByAdjustmentId.HasValue)
      {
        await tx.RollbackAsync(ct);
        return LogisticsCommandResult.Fail("Esta merma ya estaba reversada.");
      }
      if (document.Status is not (WasteStatuses.PendingReview or WasteStatuses.Approved))
      {
        await tx.RollbackAsync(ct);
        return LogisticsCommandResult.Fail($"No se puede reversar una merma {WasteStatuses.LabelFor(document.Status).ToLowerInvariant()}.");
      }
      await EnsureDocumentInScopeAsync(conn, tx, rfc, request.AdjustmentId, ct);

      var originalLines = (await conn.QueryAsync<WasteOriginalLineRow>(new CommandDefinition(
        """
        SELECT line.MaterialId,line.LocationId,line.MaterialLotId,line.QuantityDelta,line.FrozenUnitCost
        FROM logistica.InventoryAdjustmentLine line WITH (UPDLOCK,HOLDLOCK)
        WHERE line.Rfc=@Rfc AND line.AdjustmentId=@Id;
        """, new { Rfc = rfc, Id = request.AdjustmentId }, tx, cancellationToken: ct))).AsList();
      if (originalLines.Count == 0)
      {
        await tx.RollbackAsync(ct);
        return LogisticsCommandResult.Fail("Esa merma no tiene partidas que devolver.");
      }

      var code = BuildReversalCode(document.AdjustmentCode);
      if (await conn.ExecuteScalarAsync<long?>(new CommandDefinition(
        "SELECT Id FROM logistica.InventoryAdjustment WITH (UPDLOCK,HOLDLOCK) WHERE Rfc=@Rfc AND AdjustmentCode=@Code;",
        new { Rfc = rfc, Code = code }, tx, cancellationToken: ct)) is not null)
      {
        await tx.RollbackAsync(ct);
        return LogisticsCommandResult.Fail($"Ya existe un documento con el folio {code}.");
      }

      var reversalId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
        """
        INSERT INTO logistica.InventoryAdjustment
          (Rfc,AdjustmentCode,AdjustmentType,[Status],ReasonCode,Reason,OccurredOn,
           ReversalOfAdjustmentId,CreatedBy,ApprovedAt,ApprovedBy)
        VALUES
          (@Rfc,@Code,@Type,@Status,@ReasonCode,@Reason,@OccurredOn,
           @OriginalId,@UserName,SYSUTCDATETIME(),@UserName);
        SELECT CAST(SCOPE_IDENTITY() AS bigint);
        """, new
        {
          Rfc = rfc,
          Code = code,
          Type = WasteType,
          Status = WasteStatuses.Approved,
          ReasonCode = WasteReasonCatalog.Reversal,
          Reason = notes,
          OccurredOn = AsDate(DateOnly.FromDateTime(DateTime.Now)),
          OriginalId = request.AdjustmentId,
          UserName = userName
        }, tx, cancellationToken: ct));

      // Devuelve el costo congelado del original, no el promedio de hoy: la reversa tiene que
      // deshacer exactamente el valor que se dio de baja, no el que el inventario tenga ahora.
      foreach (var line in originalLines)
        await InventoryAdjustmentWriter.ApplyLineAsync(conn, tx, rfc, reversalId, WasteReversalTransaction, "La reversa",
          line.MaterialId, line.LocationId, line.MaterialLotId, -line.QuantityDelta,
          line.FrozenUnitCost, notes, userName, ct);

      await conn.ExecuteAsync(new CommandDefinition(
        """
        UPDATE logistica.InventoryAdjustment
        SET [Status]=@Status,ReversedByAdjustmentId=@ReversalId
        WHERE Rfc=@Rfc AND Id=@Id;
        """, new { Rfc = rfc, Id = request.AdjustmentId, Status = WasteStatuses.Reversed, ReversalId = reversalId }, tx, cancellationToken: ct));
      await tx.CommitAsync(ct);
      return LogisticsCommandResult.Ok(
        $"Merma {document.AdjustmentCode} reversada con el folio {code}; el inventario quedó como antes.",
        checked((int)reversalId));
    }
    catch
    {
      await tx.RollbackAsync(ct);
      throw;
    }
  }

  public async Task<WasteEvidenceDto?> GetEvidenceAsync(string rfc, long adjustmentId, CancellationToken ct = default)
  {
    var normalizedRfc = LogisticsRfc.Require(rfc);
    await using var conn = await LogisticsLocationScope.OpenAsync(_connectionFactory, _hospitalityScope, ct);
    await LogisticsLocationScope.EnsureRfcAsync(conn, null, normalizedRfc, ct);
    var evidence = await conn.QuerySingleOrDefaultAsync<WasteEvidenceDto>(new CommandDefinition(
      $$"""
      SELECT Content=doc.Evidence,FileName=ISNULL(doc.EvidenceFileName,'evidencia')
      FROM logistica.InventoryAdjustment doc
      WHERE doc.Id=@Id AND DATALENGTH(doc.Evidence)>0 AND {{VisibleWasteScopeSql}};
      """, new { Rfc = normalizedRfc, Id = adjustmentId }, cancellationToken: ct));
    if (evidence is not null)
      evidence.ContentType = LogisticsContentTypes.Normalize(null, evidence.FileName, evidence.Content);
    return evidence;
  }

  /// <summary>
  /// El folio de la reversa cuelga del original para que se lean juntos en el historial.
  /// <c>AdjustmentCode</c> es <c>varchar(30)</c>, así que un folio al límite se recorta.
  /// </summary>
  private static string BuildReversalCode(string originalCode)
  {
    const int maxLength = 30;
    const string suffix = "-R";
    var trimmed = originalCode.Trim().ToUpperInvariant();
    return trimmed.Length + suffix.Length <= maxLength
      ? trimmed + suffix
      : trimmed[..(maxLength - suffix.Length)] + suffix;
  }

  private static Task<WasteHeaderRow?> LoadHeaderAsync(
    DbConnection conn, DbTransaction tx, string rfc, long adjustmentId, CancellationToken ct)
    => conn.QuerySingleOrDefaultAsync<WasteHeaderRow>(new CommandDefinition(
      $"""
      SELECT Id,AdjustmentCode,[Status],ISNULL(CreatedBy,'') AS CreatedBy,
             ReversalOfAdjustmentId,ReversedByAdjustmentId
      FROM logistica.InventoryAdjustment WITH (UPDLOCK,HOLDLOCK)
      WHERE Rfc=@Rfc AND Id=@Id AND AdjustmentType='{WasteType}';
      """, new { Rfc = rfc, Id = adjustmentId }, tx, cancellationToken: ct));

  /// <summary>
  /// Mismo trato que en la pantalla de movimientos: un documento con partidas de otra sede no
  /// se toca desde aquí, ni para revisarlo ni para reversarlo.
  /// </summary>
  private static Task EnsureDocumentInScopeAsync(
    DbConnection conn, DbTransaction tx, string rfc, long adjustmentId, CancellationToken ct)
    => conn.ExecuteAsync(new CommandDefinition(
      $$"""
      IF EXISTS (SELECT 1 FROM logistica.InventoryAdjustmentLine line WITH (HOLDLOCK)
        WHERE line.Rfc=@Rfc AND line.AdjustmentId=@Id
          AND NOT {{LogisticsLocationScope.ForLocationIdSql("line.LocationId","line.Rfc")}})
        THROW 51932,'La merma contiene ubicaciones de otra sede.',1;
      """, new { Rfc = rfc, Id = adjustmentId }, tx, cancellationToken: ct));

  private static Task EnsureCodeBelongsToScopeAsync(
    DbConnection conn, DbTransaction tx, string rfc, string code, CancellationToken ct)
    => conn.ExecuteAsync(new CommandDefinition(
      $$"""
      IF EXISTS (SELECT 1 FROM logistica.InventoryAdjustment existing WITH (UPDLOCK,HOLDLOCK)
        JOIN logistica.InventoryAdjustmentLine line WITH (HOLDLOCK)
          ON line.Rfc=existing.Rfc AND line.AdjustmentId=existing.Id
        WHERE existing.Rfc=@Rfc AND existing.AdjustmentCode=@Code
          AND NOT {{LogisticsLocationScope.ForLocationIdSql("line.LocationId","line.Rfc")}})
        THROW 51932,'El folio ya existe en otra sede.',1;
      """, new { Rfc = rfc, Code = code }, tx, cancellationToken: ct));

  private sealed class WasteTotalsRow
  {
    public decimal TodayCost { get; set; }
    public decimal WeekCost { get; set; }
    public int PendingReviewCount { get; set; }
  }

  private sealed class WasteHistoryTotalsRow
  {
    public int TotalCount { get; set; }
    public decimal TotalCost { get; set; }
  }

  private sealed class WasteHeaderRow
  {
    public long Id { get; set; }
    public string AdjustmentCode { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string CreatedBy { get; set; } = string.Empty;
    public long? ReversalOfAdjustmentId { get; set; }
    public long? ReversedByAdjustmentId { get; set; }
  }

  private sealed class WasteOriginalLineRow
  {
    public int MaterialId { get; set; }
    public int LocationId { get; set; }
    public long? MaterialLotId { get; set; }
    public decimal QuantityDelta { get; set; }
    public decimal FrozenUnitCost { get; set; }
  }

  private sealed class WasteLineRow
  {
    public long Id { get; set; }
    public long AdjustmentId { get; set; }
    public int MaterialId { get; set; }
    public string MaterialCode { get; set; } = string.Empty;
    public string MaterialName { get; set; } = string.Empty;
    public string UnitCode { get; set; } = string.Empty;
    public int LocationId { get; set; }
    public string LocationName { get; set; } = string.Empty;
    public string? LotCode { get; set; }
    public decimal QuantityDelta { get; set; }
    public decimal FrozenUnitCost { get; set; }

    public WasteDocumentLineDto ToDto() => new()
    {
      Id = Id,
      MaterialId = MaterialId,
      MaterialCode = MaterialCode,
      MaterialName = MaterialName,
      UnitCode = UnitCode,
      LocationId = LocationId,
      LocationName = LocationName,
      LotCode = LotCode,
      QuantityDelta = QuantityDelta,
      FrozenUnitCost = FrozenUnitCost
    };
  }
}
