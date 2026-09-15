using System.Data;
using System.Globalization;
using System.Net;
using Dapper;
using Microsoft.Extensions.Hosting;
using OrionERP.Application.Common;
using OrionERP.Application.Features.Platform;
using OrionERP.Application.Features.Restaurante;
using OrionERP.Infrastructure.Features.Mail;

namespace OrionERP.Bruno.Web.Services;

public sealed class OnlineRestaurantOrderEmail
{
  public long NotificationId { get; init; }
  public string NotificationType { get; init; } = string.Empty;
  public string RecipientEmail { get; init; } = string.Empty;
  public string IdempotencyKey { get; init; } = string.Empty;
  public Guid ClientAttemptId { get; init; }
  public string CustomerName { get; init; } = string.Empty;
  public int OrderFolio { get; init; }
  public decimal Total { get; init; }
  public string CurrencyCode { get; init; } = "MXN";
}

public interface IOnlineRestaurantOrderEmailSender
{
  Task<string> CreateDraftAsync(
    PublicSiteBinding binding,
    OnlineRestaurantOrderEmail message,
    CancellationToken ct = default);

  Task<MicrosoftGraphMailMessageState> GetMessageStateAsync(
    string providerMessageId,
    CancellationToken ct = default);

  Task SendDraftAsync(
    string providerMessageId,
    CancellationToken ct = default);
}

public sealed class OnlineRestaurantOrderEmailSender : IOnlineRestaurantOrderEmailSender
{
  private readonly IMicrosoftGraphMailClient<RestaurantMailOptions> _mail;
  private readonly PublicWebsitePresentationDefinition _presentation;
  private readonly IPublicWebsiteInstanceContext _website;

  public OnlineRestaurantOrderEmailSender(
    IMicrosoftGraphMailClient<RestaurantMailOptions> mail,
    PublicWebsitePresentationDefinition presentation,
    IPublicWebsiteInstanceContext website)
  {
    _mail = mail;
    _presentation = presentation;
    _website = website;
  }

  public async Task<string> CreateDraftAsync(
    PublicSiteBinding binding,
    OnlineRestaurantOrderEmail message,
    CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(binding);
    ArgumentNullException.ThrowIfNull(message);
    if (binding.PublicSiteId <= 0 || message.ClientAttemptId == Guid.Empty)
      throw new InvalidOperationException("The notification has no public tracking identity.");
    if (string.IsNullOrWhiteSpace(message.RecipientEmail))
      throw new InvalidOperationException("The notification has no recipient.");

    var trackingToken = RestaurantOnlineTrackingTokenPolicy.Create(binding.PublicSiteId, message.ClientAttemptId);
    var trackingUrl = new Uri(
      _website.Instance.CanonicalBaseUri,
      $"pedido/{Uri.EscapeDataString(trackingToken)}").ToString();
    var isReady = string.Equals(message.NotificationType, "Ready", StringComparison.OrdinalIgnoreCase);
    if (!isReady && !string.Equals(message.NotificationType, "Confirmation", StringComparison.OrdinalIgnoreCase))
      throw new InvalidOperationException("The notification type is not public-safe.");

    var subject = isReady
      ? $"Tu pedido #{message.OrderFolio} está listo para recoger"
      : $"Confirmamos tu pedido #{message.OrderFolio}";
    var headline = isReady ? "Tu pedido está listo" : "Pedido confirmado";
    var body = isReady
      ? $"Ya puedes recoger tu pedido en {WebUtility.HtmlEncode(_presentation.ShortName)}. Presenta el folio #{message.OrderFolio}."
      : "Recibimos tu pago y enviamos el pedido a cocina. Te mandaremos otro correo cuando esté listo para recoger; espera ese aviso antes de acudir.";
    var greeting = string.IsNullOrWhiteSpace(message.CustomerName)
      ? string.Empty
      : $"<p>Hola, {WebUtility.HtmlEncode(message.CustomerName.Trim())}.</p>";
    var logoUrl = new Uri(
      _website.Instance.CanonicalBaseUri,
      _presentation.LogoPath.TrimStart('/')).ToString();
    var total = WebUtility.HtmlEncode(
      $"{message.Total.ToString("C", CultureInfo.GetCultureInfo("es-MX"))} {message.CurrencyCode}");

    var html =
      $$"""
      <div style="font-family:Arial,sans-serif;max-width:580px;margin:auto;color:#25211d;line-height:1.55">
        <p><img src="{{WebUtility.HtmlEncode(logoUrl)}}" alt="{{WebUtility.HtmlEncode(_presentation.PublicName)}}" style="display:block;max-width:140px;height:auto"></p>
        <h1 style="color:{{_presentation.PrimaryColor}}">{{WebUtility.HtmlEncode(headline)}}</h1>
        {{greeting}}
        <p>{{body}}</p>
        <div style="margin:22px 0;padding:16px;border:1px solid #ddd5cc;border-radius:10px;background:#faf8f5">
          <strong>Pedido #{{message.OrderFolio}}</strong><br>
          <span>Total: {{total}}</span>
        </div>
        <p><a href="{{WebUtility.HtmlEncode(trackingUrl)}}" style="display:inline-block;background:{{_presentation.PrimaryColor}};color:#fff;padding:12px 18px;border-radius:8px;text-decoration:none">Ver estado del pedido</a></p>
        <p style="font-size:12px;color:#6d6862">Este pedido es para recoger. No respondas con datos de pago. Si necesitas ayuda o solicitar un reembolso, contacta directamente al restaurante.</p>
        <p style="font-size:12px;color:#6d6862">{{WebUtility.HtmlEncode(_presentation.LegalName)}}</p>
      </div>
      """;

    return await _mail.CreateDraftAsync(new MicrosoftGraphMailMessage
    {
      ToRecipients = [message.RecipientEmail.Trim()],
      Subject = subject,
      Message = html
    }, ct);
  }

  public Task<MicrosoftGraphMailMessageState> GetMessageStateAsync(
    string providerMessageId,
    CancellationToken ct = default)
    => _mail.GetMessageStateAsync(providerMessageId, ct);

  public Task SendDraftAsync(
    string providerMessageId,
    CancellationToken ct = default)
    => _mail.SendDraftAsync(providerMessageId, ct);
}

internal interface IOnlineRestaurantOrderNotificationQueue
{
  Task<int> ProcessBatchAsync(CancellationToken ct = default);
}

internal sealed class OnlineRestaurantOrderNotificationQueue : IOnlineRestaurantOrderNotificationQueue
{
  private readonly IDbConnectionFactory _connections;
  private readonly IPublicWebsiteInstanceContext _website;
  private readonly IOnlineRestaurantOrderEmailSender _sender;
  private readonly ILogger<OnlineRestaurantOrderNotificationQueue> _logger;

  public OnlineRestaurantOrderNotificationQueue(
    IDbConnectionFactory connections,
    IPublicWebsiteInstanceContext website,
    IOnlineRestaurantOrderEmailSender sender,
    ILogger<OnlineRestaurantOrderNotificationQueue> logger)
  {
    _connections = connections;
    _website = website;
    _sender = sender;
    _logger = logger;
  }

  public async Task<int> ProcessBatchAsync(CancellationToken ct = default)
  {
    var binding = await _website.ResolveRequiredAsync(ct);
    var leaseId = Guid.NewGuid();
    await using var connection = (System.Data.Common.DbConnection)_connections.Create();
    var claimed = (await connection.QueryAsync<ClaimedNotification>(new CommandDefinition(
      "restaurante.OnlineOrderNotificationClaim",
      new { LeaseId = leaseId, BatchSize = 10, LeaseSeconds = 600 },
      commandType: CommandType.StoredProcedure,
      cancellationToken: ct))).AsList();

    foreach (var item in claimed)
    {
      try
      {
        var email = new OnlineRestaurantOrderEmail
        {
          NotificationId = item.Id,
          NotificationType = item.NotificationType,
          RecipientEmail = item.RecipientEmail,
          IdempotencyKey = item.IdempotencyKey,
          ClientAttemptId = item.ClientAttemptId,
          CustomerName = item.CustomerName,
          OrderFolio = item.OrderFolio,
          Total = item.Total,
          CurrencyCode = item.CurrencyCode
        };
        var providerMessageId = item.ProviderMessageId;
        if (string.IsNullOrWhiteSpace(providerMessageId))
        {
          providerMessageId = await _sender.CreateDraftAsync(binding, email, ct);
          await connection.ExecuteAsync(new CommandDefinition(
            "restaurante.OnlineOrderNotificationProviderMessageSet",
            new { Id = item.Id, LeaseId = leaseId, ProviderMessageId = providerMessageId },
            commandType: CommandType.StoredProcedure,
            cancellationToken: ct));
        }

        var providerState = await _sender.GetMessageStateAsync(providerMessageId, ct);
        if (providerState == MicrosoftGraphMailMessageState.Missing)
          throw new InvalidOperationException("The durable Graph message cannot be located.");
        if (providerState == MicrosoftGraphMailMessageState.Draft)
          await _sender.SendDraftAsync(providerMessageId, ct);

        await connection.ExecuteAsync(new CommandDefinition(
          "restaurante.OnlineOrderNotificationComplete",
          new { Id = item.Id, LeaseId = leaseId },
          commandType: CommandType.StoredProcedure,
          cancellationToken: ct));
      }
      catch (OperationCanceledException) when (ct.IsCancellationRequested)
      {
        throw;
      }
      catch (Exception exception)
      {
        var delayMinutes = Math.Min(30, 1 << Math.Min(Math.Max(item.Attempts, 1), 5));
        try
        {
          await connection.ExecuteAsync(new CommandDefinition(
            "restaurante.OnlineOrderNotificationFail",
            new
            {
              Id = item.Id,
              LeaseId = leaseId,
              FailureCode = "email_send_failed",
              FailureMessage = exception.GetType().Name,
              NextRetryAtUtc = DateTime.UtcNow.AddMinutes(delayMinutes)
            },
            commandType: CommandType.StoredProcedure,
            cancellationToken: ct));
        }
        catch (Exception recordingException) when (recordingException is not OperationCanceledException)
        {
          _logger.LogError(
            recordingException,
            "Could not persist the sanitized restaurant email failure for notification {NotificationId}.",
            item.Id);
        }
        _logger.LogWarning(
          "Restaurant order notification {NotificationId} failed with {FailureType}; a retry was scheduled.",
          item.Id,
          exception.GetType().Name);
      }
    }

    return claimed.Count;
  }

  private sealed class ClaimedNotification
  {
    public long Id { get; init; }
    public string NotificationType { get; init; } = string.Empty;
    public string RecipientEmail { get; init; } = string.Empty;
    public string IdempotencyKey { get; init; } = string.Empty;
    public string? ProviderMessageId { get; init; }
    public int Attempts { get; init; }
    public Guid ClientAttemptId { get; init; }
    public string CustomerName { get; init; } = string.Empty;
    public int OrderFolio { get; init; }
    public decimal Total { get; init; }
    public string CurrencyCode { get; init; } = "MXN";
  }
}

internal sealed class OnlineRestaurantOrderNotificationWorker : BackgroundService
{
  private readonly IServiceScopeFactory _scopeFactory;
  private readonly ILogger<OnlineRestaurantOrderNotificationWorker> _logger;

  public OnlineRestaurantOrderNotificationWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<OnlineRestaurantOrderNotificationWorker> logger)
  {
    _scopeFactory = scopeFactory;
    _logger = logger;
  }

  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    var failureDelay = TimeSpan.FromSeconds(5);
    while (!stoppingToken.IsCancellationRequested)
    {
      try
      {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var queue = scope.ServiceProvider.GetRequiredService<IOnlineRestaurantOrderNotificationQueue>();
        var processed = await queue.ProcessBatchAsync(stoppingToken);
        failureDelay = TimeSpan.FromSeconds(5);
        if (processed == 0) await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
      }
      catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
      {
        break;
      }
      catch (Exception exception)
      {
        _logger.LogError(
          "The restaurant order notification worker paused after {FailureType}.",
          exception.GetType().Name);
        await Task.Delay(failureDelay, stoppingToken);
        failureDelay = TimeSpan.FromSeconds(Math.Min(60, failureDelay.TotalSeconds * 2));
      }
    }
  }
}

/// <summary>
/// Payment/webhook/refund recovery intentionally runs independently of the
/// public ordering switch. Pausing sales must never strand captured money.
/// </summary>
internal sealed class OnlineRestaurantPaymentRecoveryWorker : BackgroundService
{
  private readonly IServiceScopeFactory _scopeFactory;
  private readonly ILogger<OnlineRestaurantPaymentRecoveryWorker> _logger;

  public OnlineRestaurantPaymentRecoveryWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<OnlineRestaurantPaymentRecoveryWorker> logger)
  {
    _scopeFactory = scopeFactory;
    _logger = logger;
  }

  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    var failureDelay = TimeSpan.FromSeconds(5);
    while (!stoppingToken.IsCancellationRequested)
    {
      try
      {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var recovery = scope.ServiceProvider.GetRequiredService<IPayPalRecoveryProcessor>();
        var processed = await recovery.ProcessPendingAsync(10, stoppingToken);
        failureDelay = TimeSpan.FromSeconds(5);
        if (processed == 0) await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
      }
      catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
      {
        break;
      }
      catch (Exception exception)
      {
        _logger.LogError(
          "The restaurant payment recovery worker paused after {FailureType}.",
          exception.GetType().Name);
        await Task.Delay(failureDelay, stoppingToken);
        failureDelay = TimeSpan.FromSeconds(Math.Min(60, failureDelay.TotalSeconds * 2));
      }
    }
  }
}
