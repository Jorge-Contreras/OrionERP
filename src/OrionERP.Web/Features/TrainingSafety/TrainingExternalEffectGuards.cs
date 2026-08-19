using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http;
using OrionERP.Application.Features.Cfdi.DescargaMasiva.Contracts;
using OrionERP.Application.Features.Cfdi.Facturama;
using OrionERP.Application.Features.Reservaciones.CalendarSync;
using OrionERP.Infrastructure.Features.Mail;
using Sat.MassiveDownload.Core;
using Sat.MassiveDownload.Models;

namespace OrionERP.Web.Features.TrainingSafety;

public static class TrainingSafetyServiceCollectionExtensions
{
  public static IServiceCollection AddTrainingSafety(
    this IServiceCollection services,
    string environmentName,
    string connectionString,
    PlatformIsolationOptions isolation,
    string? windowsServiceUrl,
    bool isMarkedTrainingService,
    TrainingDatabaseSafetyAttestation databaseSafety,
    string? allowedHosts)
  {
    var isTraining = string.Equals(environmentName, TrainingEnvironment.Name, StringComparison.OrdinalIgnoreCase);
    if (isTraining)
      TrainingSafetyValidator.ValidateStartup(
        environmentName,
        connectionString,
        isolation,
        windowsServiceUrl,
        isMarkedTrainingService,
        allowedHosts);

    var databaseCatalog = TryGetCatalog(connectionString);
    var state = new TrainingEnvironmentState(isTraining, environmentName, databaseCatalog, databaseSafety);
    services.AddSingleton<ITrainingEnvironmentState>(state);
    services.AddSingleton<ITrainingExternalEffectsPolicy, TrainingExternalEffectsPolicy>();

    if (!isTraining)
      return services;

    // Replace integration boundaries in addition to the global HttpClient
    // circuit breaker. The replacements fail before services can write partial
    // request state to Orion_Training and return an explicit training message.
    services.RemoveAll<IFacturamaApiClient>();
    services.AddSingleton<IFacturamaApiClient, TrainingBlockedFacturamaApiClient>();

    services.RemoveAll<ISatMassiveService>();
    services.AddSingleton<ISatMassiveService, TrainingBlockedSatMassiveService>();
    services.RemoveAll<ISatDownloadCoordinator>();
    services.AddSingleton<ISatDownloadCoordinator, TrainingBlockedSatDownloadCoordinator>();

    services.RemoveAll<IMicrosoftGraphMailClient<GraphMailOptions>>();
    services.AddSingleton<IMicrosoftGraphMailClient<GraphMailOptions>, TrainingBlockedGraphMailClient>();
    services.RemoveAll<IEmailSender>();
    services.AddSingleton<IEmailSender, TrainingBlockedEmailSender>();

    services.RemoveAll<IBonhomiaRoomCalendarSyncService>();
    services.AddSingleton<IBonhomiaRoomCalendarSyncService, TrainingBlockedCalendarSyncService>();

    // Fail closed for future typed/named HttpClients that might otherwise be
    // added without an explicit training replacement.
    services.AddSingleton<IHttpMessageHandlerBuilderFilter, TrainingHttpMessageHandlerBuilderFilter>();
    return services;
  }

  private static string TryGetCatalog(string connectionString)
  {
    try
    {
      return new SqlConnectionStringBuilder(connectionString).InitialCatalog?.Trim() ?? string.Empty;
    }
    catch (ArgumentException)
    {
      return string.Empty;
    }
  }
}

internal sealed class TrainingHttpMessageHandlerBuilderFilter : IHttpMessageHandlerBuilderFilter
{
  private readonly ITrainingExternalEffectsPolicy _policy;

  public TrainingHttpMessageHandlerBuilderFilter(ITrainingExternalEffectsPolicy policy) => _policy = policy;

  public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next)
    => builder =>
    {
      next(builder);
      builder.AdditionalHandlers.Insert(0, new TrainingBlockedHttpMessageHandler(_policy));
    };
}

public sealed class TrainingBlockedHttpMessageHandler : DelegatingHandler
{
  private readonly ITrainingExternalEffectsPolicy _policy;

  public TrainingBlockedHttpMessageHandler(ITrainingExternalEffectsPolicy policy) => _policy = policy;

  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
  {
    var destination = request.RequestUri?.Host ?? "un destino externo";
    throw _policy.CreateException($"la comunicación HTTP con {destination}");
  }
}

internal abstract class TrainingBlockedIntegration
{
  private readonly ITrainingExternalEffectsPolicy _policy;

  protected TrainingBlockedIntegration(ITrainingExternalEffectsPolicy policy) => _policy = policy;

  protected TrainingExternalEffectBlockedException Block(string effect) => _policy.CreateException(effect);
}

internal sealed class TrainingBlockedFacturamaApiClient : TrainingBlockedIntegration, IFacturamaApiClient
{
  public TrainingBlockedFacturamaApiClient(ITrainingExternalEffectsPolicy policy) : base(policy) { }

  public Task<string> CreateIssuedCfdiAsync(FacturamaIssuedCfdiRequest request, CancellationToken ct = default)
    => throw Block("el timbrado real en Facturama");

  public Task<string> CreateIssuedCfdiAsync(string jsonPayload, CancellationToken ct = default)
    => throw Block("el timbrado real en Facturama");

  public Task<FacturamaReceiverValidationResult> ValidateReceiverAsync(
    FacturamaReceiverValidationRequest request,
    CancellationToken ct = default)
    => throw Block("la validación externa en Facturama");

  public Task<FacturamaTaxEntity> GetTaxEntityAsync(CancellationToken ct = default)
    => throw Block("la consulta externa a Facturama");

  public Task<FacturamaDocumentContent> DownloadIssuedDocumentAsync(
    string cfdiId,
    FacturamaIssuedDocumentType documentType,
    CancellationToken ct = default)
    => throw Block("la descarga externa desde Facturama");

  public Task<string?> FindIssuedCfdiIdByUuidAsync(string uuid, CancellationToken ct = default)
    => throw Block("la consulta externa a Facturama");

  public Task CancelIssuedCfdiAsync(string cfdiId, string motive = "02", CancellationToken ct = default)
    => throw Block("la cancelación real de CFDI en Facturama");
}

internal sealed class TrainingBlockedSatMassiveService : TrainingBlockedIntegration, ISatMassiveService
{
  public TrainingBlockedSatMassiveService(ITrainingExternalEffectsPolicy policy) : base(policy) { }

  public Task AuthenticateAsync(X509Certificate2 cert, CancellationToken ct = default)
    => throw Block("la autenticación con el SAT");

  public Task<string> RequestAsync(
    DateTime startUtc,
    DateTime endUtc,
    bool issued,
    string? rfcSolicitante,
    string? rfcFiltro = null,
    string tipoSolicitud = "CFDI",
    string? estado = null,
    CancellationToken ct = default)
    => throw Block("la solicitud real de descarga al SAT");

  public Task<VerifyResult> VerifyAsync(string idSolicitud, string rfcSolicitante, CancellationToken ct = default)
    => throw Block("la verificación real de descarga ante el SAT");

  public Task<byte[]?> DownloadPackageAsync(string idPaquete, string rfcSolicitante, CancellationToken ct = default)
    => throw Block("la descarga real de paquetes desde el SAT");
}

internal sealed class TrainingBlockedSatDownloadCoordinator : TrainingBlockedIntegration, ISatDownloadCoordinator
{
  public TrainingBlockedSatDownloadCoordinator(ITrainingExternalEffectsPolicy policy) : base(policy) { }

  public Task<int> CreateSolicitudAsync(SolicitudParams p, CancellationToken ct = default)
    => throw Block("la creación de solicitudes reales de descarga al SAT");

  public Task<VerifyResultDto> VerifyAsync(int solicitudId, X509Certificate2 cert, CancellationToken ct = default)
    => throw Block("la verificación real de descarga ante el SAT");

  public Task<ProcessSummary> DownloadAndProcessAsync(int solicitudId, X509Certificate2 cert, CancellationToken ct = default)
    => throw Block("la descarga real de paquetes desde el SAT");
}

internal sealed class TrainingBlockedGraphMailClient
  : TrainingBlockedIntegration, IMicrosoftGraphMailClient<GraphMailOptions>
{
  public TrainingBlockedGraphMailClient(ITrainingExternalEffectsPolicy policy) : base(policy) { }

  public Task SendEmailAsync(MicrosoftGraphMailMessage mail, CancellationToken ct = default)
    => throw Block("el envío de correo por Microsoft Graph");

  public Task SendEmailAsync(string email, string subject, string message, CancellationToken ct = default)
    => throw Block("el envío de correo por Microsoft Graph");
}

internal sealed class TrainingBlockedEmailSender : TrainingBlockedIntegration, IEmailSender
{
  public TrainingBlockedEmailSender(ITrainingExternalEffectsPolicy policy) : base(policy) { }

  public Task SendEmailAsync(string email, string subject, string htmlMessage)
    => throw Block("el envío de correo");
}

internal sealed class TrainingBlockedCalendarSyncService
  : TrainingBlockedIntegration, IBonhomiaRoomCalendarSyncService
{
  public TrainingBlockedCalendarSyncService(ITrainingExternalEffectsPolicy policy) : base(policy) { }

  public Task<BonhomiaRoomCalendarSyncResult> SyncAsync(
    DateTime startDate,
    DateTime endDateExclusive,
    CancellationToken ct = default)
    => throw Block("la escritura o sincronización con calendarios de Microsoft Graph");
}
