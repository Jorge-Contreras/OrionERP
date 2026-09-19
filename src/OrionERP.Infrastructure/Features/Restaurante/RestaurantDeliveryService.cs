using System.Data;
using System.Data.Common;
using Dapper;
using OrionERP.Application.Common;
using OrionERP.Application.Features.Logistica.Shared;
using OrionERP.Application.Features.Restaurante;
using OrionERP.Application.Features.Reservaciones;
using OrionERP.Infrastructure.Features.Logistica.Shared;

namespace OrionERP.Infrastructure.Features.Restaurante;

public sealed class RestaurantDeliveryService : IRestaurantDeliveryService
{
  private readonly IDbConnectionFactory _connections;
  private readonly IHospitalityScopeAccessor? _scope;

  public RestaurantDeliveryService(IDbConnectionFactory connections, IHospitalityScopeAccessor? scope = null)
  {
    _connections = connections;
    _scope = scope;
  }

  public async Task<IReadOnlyList<RestaurantDeliveryQueueItemDto>> GetQueueAsync(
    string rfc, int siteId, string userName, bool canSupervise, CancellationToken ct = default)
  {
    var normalizedRfc = LogisticsRfc.Require(rfc);
    var normalizedUser = RequireUser(userName);
    using var connection = await OpenAsync(normalizedRfc, ct);
    const string sql =
      """
      SELECT orderInfo.Id OrderId,orderInfo.Folio,orderInfo.SiteId,orderInfo.[Status],orderInfo.PaymentStatus,
        orderInfo.CreatedAt,orderInfo.ReadyAt,orderInfo.CustomerName,orderInfo.CustomerPhone,
        delivery.AddressLine,delivery.AddressComplement,delivery.AddressReferences,
        delivery.Latitude,delivery.Longitude,delivery.AddressVerificationStatus,delivery.DropoffPreference,
        delivery.AssignedCourierUserName,delivery.AssignedAt,delivery.AddressConfirmedAt,
        facade.Id FacadeEvidenceId,orderInfo.Total
      FROM restaurante.[Order] orderInfo
      JOIN restaurante.Delivery delivery ON delivery.Rfc=orderInfo.Rfc AND delivery.OrderId=orderInfo.Id
      OUTER APPLY
      (
        SELECT TOP(1) evidence.Id
        FROM restaurante.DeliveryEvidence evidence
        WHERE evidence.Rfc=delivery.Rfc AND evidence.OrderId=delivery.OrderId
          AND evidence.EvidenceType='Facade' AND evidence.PurgedAtUtc IS NULL
        ORDER BY evidence.Id DESC
      ) facade
      WHERE orderInfo.Rfc=@Rfc AND orderInfo.SiteId=@SiteId
        AND orderInfo.OrderType='Delivery' AND orderInfo.SalesChannel='Web'
        AND orderInfo.[Status] IN('Ready','Dispatched')
        AND (@CanSupervise=1 OR orderInfo.[Status]='Ready' OR delivery.AssignedCourierUserName=@UserName)
      ORDER BY CASE orderInfo.[Status] WHEN 'Dispatched' THEN 0 ELSE 1 END,
        COALESCE(delivery.AssignedAt,orderInfo.ReadyAt,orderInfo.CreatedAt),orderInfo.Folio;
      """;
    var orders = (await connection.QueryAsync<RestaurantDeliveryQueueItemDto>(new CommandDefinition(
      sql,
      new { Rfc = normalizedRfc, SiteId = siteId, UserName = normalizedUser, CanSupervise = canSupervise },
      cancellationToken: ct))).AsList();
    if (orders.Count == 0) return orders;
    var ids = orders.Select(order => order.OrderId).ToArray();
    var lines = (await connection.QueryAsync<DeliveryLineRow>(new CommandDefinition(
      """
      SELECT OrderId,ProductNameSnapshot ProductName,Quantity,Notes
      FROM restaurante.OrderLine
      WHERE Rfc=@Rfc AND OrderId IN @OrderIds AND ParentOrderLineId IS NULL
      ORDER BY OrderId,Id;
      """,
      new { Rfc = normalizedRfc, OrderIds = ids },
      cancellationToken: ct))).AsList();
    var byOrder = lines.GroupBy(line => line.OrderId).ToDictionary(group => group.Key, group => group.Select(line => new RestaurantDeliveryLineDto
    {
      ProductName = line.ProductName,
      Quantity = line.Quantity,
      Notes = line.Notes
    }).ToArray());
    foreach (var order in orders) order.Lines = byOrder.GetValueOrDefault(order.OrderId) ?? [];
    return orders;
  }

  public async Task<RestaurantCommandResult> ClaimAndDispatchAsync(
    string rfc, Guid orderId, string userName, CancellationToken ct = default)
  {
    var normalizedRfc = LogisticsRfc.Require(rfc);
    var normalizedUser = RequireUser(userName);
    await using var connection = await OpenAsync(normalizedRfc, ct);
    await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
    var row = await LoadForUpdateAsync(connection, transaction, normalizedRfc, orderId, ct);
    if (row is null || row.OrderStatus != RestaurantOrderStatuses.Ready)
      return await RollbackAsync(transaction, "La entrega ya no está lista para asignarse.", ct);
    if (!string.IsNullOrWhiteSpace(row.AssignedCourierUserName)
        && !string.Equals(row.AssignedCourierUserName, normalizedUser, StringComparison.OrdinalIgnoreCase))
      return await RollbackAsync(transaction, "Otro repartidor tomó esta entrega primero.", ct);

    var now = DateTime.UtcNow;
    await connection.ExecuteAsync(new CommandDefinition(
      """
      UPDATE restaurante.Delivery
      SET AssignedCourierUserName=@UserName,AssignedAt=COALESCE(AssignedAt,@Now),
          [Status]='Dispatched',DispatchedAt=COALESCE(DispatchedAt,@Now)
      WHERE Rfc=@Rfc AND OrderId=@OrderId;
      UPDATE restaurante.[Order]
      SET [Status]='Dispatched'
      WHERE Rfc=@Rfc AND Id=@OrderId AND [Status]='Ready';
      """,
      new { Rfc = normalizedRfc, OrderId = orderId, UserName = normalizedUser, Now = now },
      transaction,
      cancellationToken: ct));
    await RestaurantOrderEventWriter.AddAsync(
      connection, transaction, normalizedRfc, row.SiteId, orderId,
      "DeliveryDispatched", "Delivery", "Pedido en camino",
      "El repartidor inició la entrega.", normalizedUser, ct,
      $"delivery:{orderId}:dispatched");
    await EnqueueNotificationAsync(connection, transaction, row, "OutForDelivery", ct);
    await transaction.CommitAsync(ct);
    return RestaurantCommandResult.Ok("La entrega quedó asignada y en camino.");
  }

  public async Task<RestaurantCommandResult> ReleaseOrReassignAsync(
    RestaurantDeliveryAssignmentRequest request, string supervisorUserName, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(request);
    var rfc = LogisticsRfc.Require(request.Rfc);
    var supervisor = RequireUser(supervisorUserName);
    await using var connection = await OpenAsync(rfc, ct);
    await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
    var row = await LoadForUpdateAsync(connection, transaction, rfc, request.OrderId, ct);
    if (row is null || row.OrderStatus is not (RestaurantOrderStatuses.Ready or RestaurantOrderStatuses.Dispatched))
      return await RollbackAsync(transaction, "La entrega ya no admite reasignación.", ct);
    var courier = NullIfWhiteSpace(request.CourierUserName);
    var affected = await connection.ExecuteAsync(new CommandDefinition(
      """
      UPDATE delivery
      SET AssignedCourierUserName=@Courier,AssignedAt=CASE WHEN @Courier IS NULL THEN NULL ELSE SYSUTCDATETIME() END
      FROM restaurante.Delivery delivery
      JOIN restaurante.[Order] orderInfo ON orderInfo.Rfc=delivery.Rfc AND orderInfo.Id=delivery.OrderId
      WHERE delivery.Rfc=@Rfc AND delivery.OrderId=@OrderId
        AND orderInfo.OrderType='Delivery' AND orderInfo.[Status] IN('Ready','Dispatched');
      """,
      new { Rfc = rfc, request.OrderId, Courier = courier },
      transaction,
      cancellationToken: ct));
    if (affected != 1)
      return await RollbackAsync(transaction, "La entrega ya no admite reasignación.", ct);
    await RestaurantOrderEventWriter.AddAsync(
      connection, transaction, rfc, row.SiteId, request.OrderId,
      courier is null ? "DeliveryReleased" : "DeliveryReassigned", "Delivery",
      courier is null ? "Entrega liberada" : "Repartidor reasignado",
      courier is null
        ? $"{supervisor} liberó la asignación de reparto."
        : $"{supervisor} reasignó la entrega a {courier}.",
      supervisor, ct);
    await transaction.CommitAsync(ct);
    return RestaurantCommandResult.Ok(courier is null ? "La entrega quedó liberada." : "El repartidor fue reasignado.");
  }

  public async Task<RestaurantCommandResult> ConfirmAddressAsync(
    string rfc, Guid orderId, string userName, CancellationToken ct = default)
  {
    var normalizedRfc = LogisticsRfc.Require(rfc);
    var normalizedUser = RequireUser(userName);
    await using var connection = await OpenAsync(normalizedRfc, ct);
    var affected = await connection.ExecuteAsync(new CommandDefinition(
      """
      UPDATE delivery
      SET AddressVerificationStatus='StaffConfirmed',AddressConfirmedAt=SYSUTCDATETIME(),AddressConfirmedBy=@UserName
      FROM restaurante.Delivery delivery
      JOIN restaurante.[Order] orderInfo ON orderInfo.Rfc=delivery.Rfc AND orderInfo.Id=delivery.OrderId
      WHERE delivery.Rfc=@Rfc AND delivery.OrderId=@OrderId
        AND orderInfo.[Status] IN('Ready','Dispatched');
      """,
      new { Rfc = normalizedRfc, OrderId = orderId, UserName = normalizedUser },
      cancellationToken: ct));
    return affected == 1
      ? RestaurantCommandResult.Ok("La dirección quedó confirmada.")
      : RestaurantCommandResult.Fail("La entrega ya no admite cambios de dirección.");
  }

  public async Task<RestaurantCommandResult> CompleteAsync(
    RestaurantDeliveryCompleteRequest request, string userName, bool canOverrideProof, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(request);
    var rfc = LogisticsRfc.Require(request.Rfc);
    var normalizedUser = RequireUser(userName);
    var normalizedProof = request.ProofPhoto is { Length: > 0 }
      ? RestaurantDeliveryImageNormalizer.TryNormalize(request.ProofPhoto)
      : null;
    if (request.ProofPhoto is { Length: > 0 } && normalizedProof is null)
      return RestaurantCommandResult.Fail("Usa una foto JPEG, PNG o WebP válida y de hasta 12 MB.");

    await using var connection = await OpenAsync(rfc, ct);
    await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
    var row = await LoadForUpdateAsync(connection, transaction, rfc, request.OrderId, ct);
    if (row is null || row.OrderStatus != RestaurantOrderStatuses.Dispatched)
      return await RollbackAsync(transaction, "La orden ya no está en camino.", ct);
    if (!canOverrideProof && !string.Equals(row.AssignedCourierUserName, normalizedUser, StringComparison.OrdinalIgnoreCase))
      return await RollbackAsync(transaction, "Esta entrega está asignada a otro repartidor.", ct);
    if (!string.Equals(row.PaymentStatus, RestaurantPaymentStatuses.Paid, StringComparison.Ordinal))
      return await RollbackAsync(transaction, "La orden no está completamente pagada.", ct);

    var proofRequired = row.DropoffPreference == RestaurantDeliveryDropoffPreferences.LeaveAtDoor;
    if (proofRequired && normalizedProof is null)
    {
      if (!request.OverrideMissingProof || !canOverrideProof || string.IsNullOrWhiteSpace(request.OverrideReason))
        return await RollbackAsync(transaction, "Toma una foto de la entrega o solicita una excepción de supervisor.", ct);
    }
    if (normalizedProof is not null)
      await InsertEvidenceAsync(connection, transaction, row, RestaurantDeliveryEvidenceTypes.DropoffProof, normalizedProof, normalizedUser, ct);

    var usedProofOverride = proofRequired && normalizedProof is null;
    if (usedProofOverride)
    {
      var reason = request.OverrideReason!.Trim();
      if (reason.Length > 500) reason = reason[..500];
      await connection.ExecuteAsync(new CommandDefinition(
        """
        INSERT INTO restaurante.SupervisorAuthorization
          (Rfc,SiteId,ActionType,AggregateId,Reason,RequestedBy,AuthorizedBy)
        VALUES
          (@Rfc,@SiteId,'DeliveryProofOverride',CONVERT(varchar(36),@OrderId),@Reason,@UserName,@UserName);
        """,
        new { Rfc = rfc, row.SiteId, OrderId = request.OrderId, Reason = reason, UserName = normalizedUser },
        transaction,
        cancellationToken: ct));
    }

    var now = DateTime.UtcNow;
    await connection.ExecuteAsync(new CommandDefinition(
      """
      UPDATE restaurante.Delivery
      SET [Status]='Delivered',DeliveredAt=COALESCE(DeliveredAt,@Now)
      WHERE Rfc=@Rfc AND OrderId=@OrderId;
      UPDATE restaurante.OrderLine
      SET [Status]=CASE WHEN [Status]<>'Cancelled' THEN 'Delivered' ELSE [Status] END,
          DeliveredAt=CASE WHEN [Status]<>'Cancelled' THEN COALESCE(DeliveredAt,@Now) ELSE DeliveredAt END
      WHERE Rfc=@Rfc AND OrderId=@OrderId;
      UPDATE restaurante.[Order]
      SET [Status]='Completed',CompletedAt=COALESCE(CompletedAt,@Now)
      WHERE Rfc=@Rfc AND Id=@OrderId AND [Status]='Dispatched' AND PaymentStatus='Paid';
      """,
      new { Rfc = rfc, OrderId = request.OrderId, Now = now },
      transaction,
      cancellationToken: ct));
    await RestaurantOrderEventWriter.AddAsync(
      connection, transaction, rfc, row.SiteId, request.OrderId,
      "DeliveryCompleted", "Delivery", "Entrega completada",
      normalizedProof is not null ? "La entrega se cerró con comprobante." : "La entrega se cerró con excepción autorizada y auditada.",
      normalizedUser, ct, $"delivery:{request.OrderId}:completed");
    await EnqueueNotificationAsync(connection, transaction, row, "Delivered", ct);
    await transaction.CommitAsync(ct);
    return RestaurantCommandResult.Ok("La entrega quedó completada.");
  }

  public async Task<RestaurantDeliveryEvidencePayload?> GetEvidenceAsync(
    string rfc, long evidenceId, string userName, bool canSupervise, CancellationToken ct = default)
  {
    var normalizedRfc = LogisticsRfc.Require(rfc);
    var normalizedUser = RequireUser(userName);
    using var connection = await OpenAsync(normalizedRfc, ct);
    return await connection.QuerySingleOrDefaultAsync<RestaurantDeliveryEvidencePayload>(new CommandDefinition(
      """
      SELECT evidence.Thumbnail Bytes,evidence.ContentType,
        CONVERT(varchar(64),evidence.ContentHash,2) ContentHash
      FROM restaurante.DeliveryEvidence evidence
      JOIN restaurante.Delivery delivery ON delivery.Rfc=evidence.Rfc AND delivery.OrderId=evidence.OrderId
      WHERE evidence.Rfc=@Rfc AND evidence.Id=@EvidenceId AND evidence.PurgedAtUtc IS NULL
        AND (@CanSupervise=1 OR delivery.AssignedCourierUserName=@UserName OR delivery.AssignedCourierUserName IS NULL);
      """,
      new { Rfc = normalizedRfc, EvidenceId = evidenceId, UserName = normalizedUser, CanSupervise = canSupervise },
      cancellationToken: ct));
  }

  private static async Task<DeliveryRow?> LoadForUpdateAsync(
    DbConnection connection, DbTransaction transaction, string rfc, Guid orderId, CancellationToken ct)
    => await connection.QuerySingleOrDefaultAsync<DeliveryRow>(new CommandDefinition(
      """
      SELECT orderInfo.Rfc,orderInfo.Id OrderId,orderInfo.PublicSiteId,orderInfo.OnlineCheckoutAttemptId,
        orderInfo.SiteId,orderInfo.[Status] OrderStatus,orderInfo.PaymentStatus,
        delivery.DropoffPreference,delivery.AssignedCourierUserName
      FROM restaurante.[Order] orderInfo WITH(UPDLOCK,HOLDLOCK)
      JOIN restaurante.Delivery delivery WITH(UPDLOCK,HOLDLOCK)
        ON delivery.Rfc=orderInfo.Rfc AND delivery.OrderId=orderInfo.Id
      WHERE orderInfo.Rfc=@Rfc AND orderInfo.Id=@OrderId
        AND orderInfo.OrderType='Delivery' AND orderInfo.SalesChannel='Web';
      """,
      new { Rfc = rfc, OrderId = orderId },
      transaction,
      cancellationToken: ct));

  private static async Task InsertEvidenceAsync(
    DbConnection connection, DbTransaction transaction, DeliveryRow row, string type,
    RestaurantDeliveryNormalizedImage image, string actor, CancellationToken ct)
    => await connection.ExecuteAsync(new CommandDefinition(
      """
      INSERT restaurante.DeliveryEvidence
        (PublicSiteId,Rfc,SiteId,OrderId,EvidenceType,ContentType,ByteLength,Width,Height,Content,Thumbnail,ContentHash,Source,CreatedBy)
      VALUES
        (@PublicSiteId,@Rfc,@SiteId,@OrderId,@EvidenceType,'image/jpeg',@ByteLength,@Width,@Height,@Content,@Thumbnail,@ContentHash,'Courier',@CreatedBy);
      """,
      new
      {
        Rfc = row.Rfc,
        row.PublicSiteId,
        row.SiteId,
        row.OrderId,
        EvidenceType = type,
        ByteLength = image.Content.Length,
        image.Width,
        image.Height,
        image.Content,
        image.Thumbnail,
        ContentHash = image.Hash,
        CreatedBy = actor
      },
      transaction,
      cancellationToken: ct));

  private static async Task EnqueueNotificationAsync(
    DbConnection connection, DbTransaction transaction, DeliveryRow row, string type, CancellationToken ct)
  {
    if (!row.PublicSiteId.HasValue || !row.OnlineCheckoutAttemptId.HasValue) return;
    await connection.ExecuteAsync(new CommandDefinition(
      """
      INSERT restaurante.OnlineOrderNotification
        (PublicSiteId,Rfc,SiteId,CheckoutAttemptId,RestaurantOrderId,NotificationType,RecipientEmail,IdempotencyKey)
      SELECT attempt.PublicSiteId,attempt.Rfc,attempt.SiteId,attempt.Id,@OrderId,@Type,
        attempt.CustomerEmail,CONCAT('email-',LOWER(@Type),'-',CONVERT(varchar(36),attempt.Id))
      FROM restaurante.OnlineCheckoutAttempt attempt
      WHERE attempt.PublicSiteId=@PublicSiteId AND attempt.Rfc=@Rfc AND attempt.SiteId=@SiteId
        AND attempt.Id=@AttemptId
        AND NOT EXISTS
        (SELECT 1 FROM restaurante.OnlineOrderNotification existing WITH(UPDLOCK,HOLDLOCK)
         WHERE existing.PublicSiteId=attempt.PublicSiteId AND existing.CheckoutAttemptId=attempt.Id
           AND existing.NotificationType=@Type);
      """,
      new
      {
        row.PublicSiteId,
        Rfc = row.Rfc,
        row.SiteId,
        AttemptId = row.OnlineCheckoutAttemptId,
        row.OrderId,
        Type = type
      },
      transaction,
      cancellationToken: ct));
  }

  private async Task<DbConnection> OpenAsync(string rfc, CancellationToken ct)
  {
    var connection = await LogisticsLocationScope.OpenAsync(_connections, _scope, ct);
    try
    {
      await LogisticsLocationScope.EnsureRfcAsync(connection, null, rfc, ct);
      return connection;
    }
    catch { await connection.DisposeAsync(); throw; }
  }

  private static async Task<RestaurantCommandResult> RollbackAsync(DbTransaction transaction, string message, CancellationToken ct)
  {
    await transaction.RollbackAsync(ct);
    return RestaurantCommandResult.Fail(message);
  }

  private static string RequireUser(string value)
    => string.IsNullOrWhiteSpace(value) ? throw new UnauthorizedAccessException("La sesión no identifica al empleado.") : value.Trim();
  private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

  private sealed class DeliveryLineRow
  {
    public Guid OrderId { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public string? Notes { get; set; }
  }

  private sealed class DeliveryRow
  {
    public string Rfc { get; set; } = string.Empty;
    public Guid OrderId { get; set; }
    public long? PublicSiteId { get; set; }
    public Guid? OnlineCheckoutAttemptId { get; set; }
    public int SiteId { get; set; }
    public string OrderStatus { get; set; } = string.Empty;
    public string PaymentStatus { get; set; } = string.Empty;
    public string DropoffPreference { get; set; } = string.Empty;
    public string? AssignedCourierUserName { get; set; }
  }
}
