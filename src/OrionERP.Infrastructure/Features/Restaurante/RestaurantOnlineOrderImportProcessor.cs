using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Dapper;
using OrionERP.Application.Common;
using OrionERP.Application.Features.Platform;
using OrionERP.Application.Features.Restaurante;

namespace OrionERP.Infrastructure.Features.Restaurante;

/// <summary>
/// Imports completed public captures into the canonical Restaurant POS workflow.
/// The SQL lease and the POS idempotency key make retries safe across processes.
/// </summary>
public sealed class RestaurantOnlineOrderImportProcessor : IOnlineOrderImportProcessor
{
  private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
  private readonly IEnabledModuleSiteEnumerator _sites;
  private readonly IModuleSiteBindingResolver _bindings;
  private readonly IModuleJobLeaseManager _jobLeases;
  private readonly IOrionSqlSessionFactory _sessions;
  private readonly TimeProvider _clock;

  public RestaurantOnlineOrderImportProcessor(
    IEnabledModuleSiteEnumerator sites,
    IModuleSiteBindingResolver bindings,
    IModuleJobLeaseManager jobLeases,
    IOrionSqlSessionFactory sessions,
    TimeProvider? clock = null)
  {
    _sites = sites;
    _bindings = bindings;
    _jobLeases = jobLeases;
    _sessions = sessions;
    _clock = clock ?? TimeProvider.System;
  }

  public async Task<int> ProcessPendingAsync(int batchSize = 10, CancellationToken ct = default)
  {
    var remaining = Math.Clamp(batchSize, 1, 100);
    var processed = 0;
    foreach (var unboundScope in await _sites.ListAsync(PlatformModuleCodes.Restaurant, ct))
    {
      if (remaining == 0) break;
      var binding = await _bindings.ResolveRequiredAsync(unboundScope, ct);
      var scope = unboundScope with { ModuleLocalSiteId = binding.ModuleLocalSiteId };
      await using var jobLease = await _jobLeases.TryAcquireAsync("RestaurantOnlineOrderImport", scope, ct);
      if (!jobLease.IsAcquired) continue;

      if (!await TryRecordHeartbeatAsync(scope, ct))
        continue;
      while (remaining > 0)
      {
        var leaseId = Guid.NewGuid();
        ImportRow? row;
        await using (var connection = await _sessions.OpenAsync(scope, ct))
        {
          row = await connection.QuerySingleOrDefaultAsync<ImportRow>(new CommandDefinition(
            "restaurante.OnlineOrderImportClaim",
            new { LeaseId = leaseId, LeaseSeconds = 90 },
            commandType: CommandType.StoredProcedure,
            cancellationToken: ct));
        }
        if (row is null) break;

        await ProcessOneAsync(scope, row, leaseId, ct);
        processed++;
        remaining--;
      }
      if (remaining > 0)
      {
        var refunds = await ProcessLocalRefundsAsync(scope, remaining, ct);
        processed += refunds;
        remaining -= refunds;
      }
    }
    return processed;
  }

  public async Task RecordHeartbeatAsync(CancellationToken ct = default)
  {
    foreach (var unboundScope in await _sites.ListAsync(PlatformModuleCodes.Restaurant, ct))
    {
      var binding = await _bindings.ResolveRequiredAsync(unboundScope, ct);
      _ = await TryRecordHeartbeatAsync(
        unboundScope with { ModuleLocalSiteId = binding.ModuleLocalSiteId },
        ct);
    }
  }

  private async Task<bool> TryRecordHeartbeatAsync(PlatformExecutionScope scope, CancellationToken ct)
  {
    await using var connection = await _sessions.OpenAsync(scope, ct);
    var configuredSites = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
      "SELECT COUNT_BIG(*) FROM restaurante.OnlineOrderingSettings;",
      cancellationToken: ct));
    if (configuredSites == 0)
      return false;
    if (configuredSites != 1)
      throw new InvalidOperationException("El contexto del importador resolvió más de un sitio de pedidos en línea.");

    await connection.ExecuteAsync(new CommandDefinition(
      "restaurante.OnlineOrderingProcessorHeartbeatSet",
      commandType: CommandType.StoredProcedure,
      cancellationToken: ct));
    await connection.ExecuteAsync(new CommandDefinition(
      "restaurante.OnlineDeliveryEvidencePurge",
      commandType: CommandType.StoredProcedure,
      cancellationToken: ct));
    return true;
  }

  private async Task ProcessOneAsync(
    PlatformExecutionScope scope,
    ImportRow row,
    Guid leaseId,
    CancellationToken ct)
  {
    try
    {
      var snapshot = JsonSerializer.Deserialize<RestaurantOnlineQuoteSnapshot>(row.CartSnapshotJson, JsonOptions)
        ?? throw new InvalidOperationException("La cotización persistida no se pudo leer.");
      ValidateSnapshot(scope, row, snapshot);
      var fulfillment = snapshot.Request.Fulfillment ?? new RestaurantOnlineFulfillmentRequest();
      var isDelivery = string.Equals(fulfillment.Type, RestaurantOrderTypes.Delivery, StringComparison.Ordinal);

      var service = new RestaurantOrderService(new ScopeConnectionFactory(_sessions, scope));
      var order = await service.CreateOrderAsync(new RestaurantOrderCreateRequest
      {
        Rfc = row.Rfc,
        SiteId = row.SiteId,
        PublicSiteId = row.PublicSiteId,
        OnlineCheckoutAttemptId = row.Id,
        IdempotencyKey = PosIdempotencyKey(row),
        OrderType = isDelivery ? RestaurantOrderTypes.Delivery : RestaurantOrderTypes.Pickup,
        CustomerName = row.CustomerName,
        CustomerEmail = row.CustomerEmail,
        CustomerPhone = row.CustomerPhone,
        DeliveryAddress = isDelivery ? fulfillment.AddressLine : null,
        DeliveryAddressComplement = isDelivery ? fulfillment.AddressComplement : null,
        DeliveryReferences = isDelivery ? fulfillment.Instructions : null,
        DeliveryLatitude = isDelivery ? fulfillment.Latitude : null,
        DeliveryLongitude = isDelivery ? fulfillment.Longitude : null,
        DeliveryGooglePlaceId = isDelivery ? fulfillment.GooglePlaceId : null,
        DeliveryAddressVerificationStatus = isDelivery ? fulfillment.AddressVerificationStatus : null,
        DeliveryDropoffPreference = isDelivery ? fulfillment.DropoffPreference : null,
        DeliveryCost = isDelivery ? snapshot.DeliveryFee : 0,
        ExternalReference = row.ProviderCaptureId,
        MemberId = row.MemberId,
        PointsToRedeem = 0,
        PromotionCode = snapshot.Request.PromotionCode,
        SalesChannel = RestaurantSalesChannels.Web,
        AllowInventoryDeficit = false,
        Lines = snapshot.Request.Lines.Select(ToOrderLine).ToList(),
        Payments =
        [
          new RestaurantPaymentCreateRequest
          {
            PaymentMethod = "Platform",
            Amount = row.Total,
            TipAmount = 0,
            IdempotencyKey = $"gateway-capture:{row.ProviderCaptureId}",
            ExternalReference = row.ProviderCaptureId
          }
        ]
      }, "system:online-order-import", ct);

      await using var connection = await _sessions.OpenAsync(scope, ct);
      var localPaymentId = await connection.QuerySingleAsync<Guid>(new CommandDefinition(
        """
        SELECT paymentInfo.Id
        FROM restaurante.Payment paymentInfo
        WHERE paymentInfo.Rfc=@Rfc AND paymentInfo.OrderId=@OrderId
          AND paymentInfo.PaymentMethod='Platform'
          AND paymentInfo.ExternalReference=@CaptureId;
        """,
        new { row.Rfc, OrderId = order.OrderId, CaptureId = row.ProviderCaptureId },
        cancellationToken: ct));
      await connection.ExecuteAsync(new CommandDefinition(
        "restaurante.OnlineOrderImportCompleteV2",
        new
        {
          Id = row.Id,
          LeaseId = leaseId,
          RestaurantOrderId = order.OrderId,
          LocalPaymentId = localPaymentId
        },
        commandType: CommandType.StoredProcedure,
        cancellationToken: ct));
    }
    catch (Exception exception) when (exception is not OperationCanceledException)
    {
      var deterministic = IsDeterministicFulfillmentFailure(exception);
      await using var connection = await _sessions.OpenAsync(scope, ct);
      await connection.ExecuteAsync(new CommandDefinition(
        "restaurante.OnlineOrderImportFail",
        new
        {
          Id = row.Id,
          LeaseId = leaseId,
          IsDeterministic = deterministic,
          FailureCode = deterministic ? "FULFILLMENT_REJECTED" : "POS_IMPORT_FAILED",
          FailureMessage = SafeFailureMessage(exception, deterministic),
          NextRetryAtUtc = deterministic
            ? (DateTime?)null
            : _clock.GetUtcNow().Add(RetryDelay(row.ImportAttempts)).UtcDateTime
        },
        commandType: CommandType.StoredProcedure,
        cancellationToken: ct));
    }
  }

  private static void ValidateSnapshot(
    PlatformExecutionScope scope,
    ImportRow row,
    RestaurantOnlineQuoteSnapshot snapshot)
  {
    if (!string.Equals(row.Rfc, scope.CompanyRfc, StringComparison.OrdinalIgnoreCase)
        || scope.ModuleLocalSiteId != row.SiteId
        || snapshot.PublicSiteId != row.PublicSiteId
        || !string.Equals(snapshot.Rfc, row.Rfc, StringComparison.OrdinalIgnoreCase)
        || snapshot.SiteId != row.SiteId
        || snapshot.MemberId != row.MemberId
        || !string.Equals(snapshot.Fingerprint, row.QuoteFingerprint, StringComparison.Ordinal)
        || !string.Equals(snapshot.Fingerprint, RestaurantOnlineQuotePolicy.CreateFingerprint(snapshot), StringComparison.Ordinal)
        || snapshot.Total != row.Total
        || !string.Equals(snapshot.Currency, row.CurrencyCode, StringComparison.OrdinalIgnoreCase)
        || !string.Equals(row.CurrencyCode, "MXN", StringComparison.OrdinalIgnoreCase)
        || string.IsNullOrWhiteSpace(row.ProviderCaptureId))
      throw new OnlineOrderSnapshotIntegrityException();
  }

  private async Task<int> ProcessLocalRefundsAsync(
    PlatformExecutionScope scope,
    int limit,
    CancellationToken ct)
  {
    await using var connection = await _sessions.OpenAsync(scope, ct);
    var rows = (await connection.QueryAsync<LocalRefundRow>(new CommandDefinition(
      """
      SELECT TOP (@Limit)
        refundInfo.Id,refundInfo.Amount,refundInfo.Reason,refundInfo.RequestedBy,
        refundInfo.AuthorizedBy,refundInfo.ProviderRefundId,
        transactionInfo.LocalPaymentId,attempt.Rfc,attempt.RestaurantOrderId
      FROM restaurante.PaymentGatewayRefund refundInfo WITH(UPDLOCK,READPAST,ROWLOCK)
      JOIN restaurante.PaymentGatewayTransaction transactionInfo
        ON transactionInfo.PublicSiteId=refundInfo.PublicSiteId
       AND transactionInfo.Rfc=refundInfo.Rfc AND transactionInfo.SiteId=refundInfo.SiteId
       AND transactionInfo.Id=refundInfo.GatewayTransactionId
      JOIN restaurante.OnlineCheckoutAttempt attempt
        ON attempt.PublicSiteId=transactionInfo.PublicSiteId
       AND attempt.Rfc=transactionInfo.Rfc AND attempt.SiteId=transactionInfo.SiteId
       AND attempt.Id=transactionInfo.CheckoutAttemptId
      WHERE refundInfo.[Status]='Completed' AND refundInfo.LocalRefundId IS NULL
        AND transactionInfo.LocalPaymentId IS NOT NULL
        AND attempt.RestaurantOrderId IS NOT NULL
      ORDER BY refundInfo.CompletedAtUtc,refundInfo.Id;
      """,
      new { Limit = Math.Clamp(limit, 1, 100) },
      cancellationToken: ct))).AsList();
    var count = 0;
    foreach (var row in rows)
    {
      try
      {
        var idempotencyKey = $"paypal-refund:{row.ProviderRefundId}";
        var orderService = new RestaurantOrderService(new ScopeConnectionFactory(_sessions, scope));
        var result = await orderService.RefundPaymentAsync(new RestaurantPaymentRefundRequest
        {
          Rfc = row.Rfc,
          PaymentId = row.LocalPaymentId!.Value,
          Amount = row.Amount,
          IdempotencyKey = idempotencyKey,
          Reason = row.Reason,
          SupervisorUserName = row.AuthorizedBy,
          ReopenBalance = false
        }, row.RequestedBy, ct);
        if (!result.Success) throw new InvalidOperationException(result.Message);

        await using (var stateConnection = await _sessions.OpenAsync(scope, ct))
        {
          var state = await stateConnection.QuerySingleAsync<RefundedOrderState>(new CommandDefinition(
            "SELECT PaymentStatus,[Status] OrderStatus FROM restaurante.[Order] WHERE Rfc=@Rfc AND Id=@OrderId;",
            new { row.Rfc, OrderId = row.RestaurantOrderId!.Value },
            cancellationToken: ct));
          if (string.Equals(state.PaymentStatus, RestaurantPaymentStatuses.Refunded, StringComparison.OrdinalIgnoreCase)
              && state.OrderStatus is not (RestaurantOrderStatuses.Completed or RestaurantOrderStatuses.Cancelled))
          {
            var cancellation = await orderService.CancelOrderAsync(
              row.Rfc,
              row.RestaurantOrderId.Value,
              $"Reembolso completo: {row.Reason}",
              row.AuthorizedBy,
              ct);
            if (!cancellation.Success)
            {
              var currentStatus = await stateConnection.ExecuteScalarAsync<string?>(new CommandDefinition(
                "SELECT [Status] FROM restaurante.[Order] WHERE Rfc=@Rfc AND Id=@OrderId;",
                new { row.Rfc, OrderId = row.RestaurantOrderId.Value },
                cancellationToken: ct));
              if (currentStatus is not (RestaurantOrderStatuses.Completed or RestaurantOrderStatuses.Cancelled))
                throw new InvalidOperationException(cancellation.Message);
            }
          }
        }

        await using var completionConnection = await _sessions.OpenAsync(scope, ct);
        var localRefundId = await completionConnection.QuerySingleAsync<Guid>(new CommandDefinition(
          "SELECT Id FROM restaurante.PaymentRefund WHERE Rfc=@Rfc AND IdempotencyKey=@IdempotencyKey;",
          new { row.Rfc, IdempotencyKey = idempotencyKey },
          cancellationToken: ct));
        await completionConnection.ExecuteAsync(new CommandDefinition(
          "restaurante.PaymentGatewayRefundLocalComplete",
          new { row.Id, LocalRefundId = localRefundId },
          commandType: CommandType.StoredProcedure,
          cancellationToken: ct));
      }
      catch (Exception exception) when (exception is not OperationCanceledException)
      {
        await using var failureConnection = await _sessions.OpenAsync(scope, ct);
        await failureConnection.ExecuteAsync(new CommandDefinition(
          """
          UPDATE restaurante.PaymentGatewayRefund
          SET FailureCode='LOCAL_REFUND_FAILED',
              FailureMessage=N'El proveedor confirmó el reembolso, pero OrionERP aún no pudo registrarlo localmente.',
              UpdatedAtUtc=SYSUTCDATETIME()
          WHERE Id=@Id AND [Status]='Completed' AND LocalRefundId IS NULL;
          """,
          new { row.Id },
          cancellationToken: ct));
      }
      count++;
    }
    return count;
  }

  private static RestaurantOrderLineCreateRequest ToOrderLine(RestaurantOnlineCartLineRequest line)
    => new()
    {
      ProductId = line.ProductId,
      MenuSectionId = line.MenuSectionId,
      Quantity = line.Quantity,
      Notes = line.Notes,
      IsCustom = false,
      ModifierOptionIds = line.ModifierOptionIds.ToList(),
      ComboSelections = line.ComboSelections.Select(combo => new RestaurantComboSelectionCreateRequest
      {
        ComboSlotId = combo.ComboSlotId,
        ComboSlotOptionId = combo.ComboSlotOptionId,
        ModifierOptionIds = combo.ModifierOptionIds.ToList(),
        Notes = combo.Notes
      }).ToList()
    };

  private static string PosIdempotencyKey(ImportRow row)
    => $"online:{row.MerchantProfileKey}:{row.ProviderCaptureId}";

  private static bool IsDeterministicFulfillmentFailure(Exception exception)
    => exception is RestaurantOrderBusinessRejectionException or OnlineOrderSnapshotIntegrityException;

  private static string SafeFailureMessage(Exception exception, bool deterministic)
  {
    if (exception is OnlineOrderSnapshotIntegrityException)
      return "Los datos internos del checkout no coincidieron con la captura; se solicitó el reembolso íntegro.";
    if (!deterministic)
      return "El pago está recibido, pero OrionERP aún no pudo crear la orden. El proceso seguirá reintentando.";
    var value = string.IsNullOrWhiteSpace(exception.Message)
      ? "El restaurante no pudo surtir el pedido."
      : exception.Message.Trim();
    return value.Length <= 500 ? value : value[..500];
  }

  private static TimeSpan RetryDelay(int attempts)
    => TimeSpan.FromSeconds(Math.Min(900, Math.Pow(2, Math.Clamp(attempts, 1, 9)) * 5));

  private sealed class ImportRow
  {
    public Guid Id { get; set; }
    public long PublicSiteId { get; set; }
    public string Rfc { get; set; } = string.Empty;
    public int SiteId { get; set; }
    public Guid? MemberId { get; set; }
    public string CustomerName { get; set; } = string.Empty;
    public string CustomerEmail { get; set; } = string.Empty;
    public string CustomerPhone { get; set; } = string.Empty;
    public string QuoteFingerprint { get; set; } = string.Empty;
    public string CartSnapshotJson { get; set; } = string.Empty;
    public decimal Total { get; set; }
    public string CurrencyCode { get; set; } = string.Empty;
    public string MerchantProfileKey { get; set; } = string.Empty;
    public string ProviderCaptureId { get; set; } = string.Empty;
    public int ImportAttempts { get; set; }
  }

  private sealed class RefundedOrderState
  {
    public string PaymentStatus { get; set; } = string.Empty;
    public string OrderStatus { get; set; } = string.Empty;
  }

  private sealed class OnlineOrderSnapshotIntegrityException : InvalidOperationException
  {
    public OnlineOrderSnapshotIntegrityException()
      : base("La cotización persistida no coincide con la captura.") { }
  }

  private sealed class LocalRefundRow
  {
    public Guid Id { get; set; }
    public decimal Amount { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string RequestedBy { get; set; } = string.Empty;
    public string AuthorizedBy { get; set; } = string.Empty;
    public string ProviderRefundId { get; set; } = string.Empty;
    public Guid? LocalPaymentId { get; set; }
    public string Rfc { get; set; } = string.Empty;
    public Guid? RestaurantOrderId { get; set; }
  }

  /// <summary>Adapts an explicit platform scope to legacy POS services.</summary>
  private sealed class ScopeConnectionFactory(
    IOrionSqlSessionFactory sessions,
    PlatformExecutionScope scope) : IDbConnectionFactory
  {
    public IDbConnection Create() => new SessionBackedConnection(sessions, scope);
  }

  private sealed class SessionBackedConnection(
    IOrionSqlSessionFactory sessions,
    PlatformExecutionScope scope) : DbConnection
  {
    private DbConnection? _inner;

    [AllowNull]
    public override string ConnectionString
    {
      get => _inner?.ConnectionString ?? string.Empty;
      set => throw new NotSupportedException();
    }
    public override string Database => RequireInner().Database;
    public override string DataSource => RequireInner().DataSource;
    public override string ServerVersion => RequireInner().ServerVersion;
    public override int ConnectionTimeout => _inner?.ConnectionTimeout ?? 30;
    public override ConnectionState State => _inner?.State ?? ConnectionState.Closed;

    public override void Open() => OpenAsync(CancellationToken.None).GetAwaiter().GetResult();
    public override async Task OpenAsync(CancellationToken cancellationToken)
    {
      if (_inner is not null) return;
      _inner = await sessions.OpenAsync(scope, cancellationToken);
    }
    public override void Close()
    {
      _inner?.Close();
      _inner = null;
    }
    public override Task CloseAsync()
    {
      Close();
      return Task.CompletedTask;
    }
    public override void ChangeDatabase(string databaseName) => RequireInner().ChangeDatabase(databaseName);
    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
      => RequireInner().BeginTransaction(isolationLevel);
    protected override DbCommand CreateDbCommand() => RequireInner().CreateCommand();
    protected override void Dispose(bool disposing)
    {
      if (disposing) _inner?.Dispose();
      _inner = null;
      base.Dispose(disposing);
    }
    public override async ValueTask DisposeAsync()
    {
      if (_inner is not null) await _inner.DisposeAsync();
      _inner = null;
      GC.SuppressFinalize(this);
    }
    private DbConnection RequireInner()
      => _inner ?? throw new InvalidOperationException("La conexión de alcance aún no está abierta.");
  }
}
