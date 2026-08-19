namespace OrionERP.Web.Features.TrainingSafety;

public static class TrainingSafetyEndpoints
{
  public static IEndpointRouteBuilder MapTrainingReadiness(this IEndpointRouteBuilder endpoints)
  {
    endpoints.MapGet("/readyz", async (
      ITrainingEnvironmentState state,
      IConfiguration configuration,
      HttpContext httpContext,
      CancellationToken cancellationToken) =>
    {
      var remoteAddress = httpContext.Connection.RemoteIpAddress;
      if (remoteAddress is null || !System.Net.IPAddress.IsLoopback(remoteAddress))
        return Results.NotFound();

      var schemaVersion = state.DatabaseSchemaVersion;
      var sanitized = state.DataSanitized;
      var syntheticOnly = state.SyntheticDataOnly;
      var loginIsolated = state.RuntimeLoginIsolated;
      var databaseSafetyVerified = state.DatabaseSafetyVerified;

      if (state.IsTraining)
      {
        try
        {
          var current = await TrainingDatabaseSafetyVerifier.VerifyOrThrowAsync(
            configuration.GetConnectionString("OrionDb") ?? string.Empty,
            cancellationToken);
          schemaVersion = current.SchemaVersion;
          sanitized = current.DataSanitized;
          syntheticOnly = current.SyntheticDataOnly;
          loginIsolated = current.RuntimeLoginIsolated;
          databaseSafetyVerified = current.Verified;
        }
        catch
        {
          return Results.Json(new
          {
            status = "not_ready",
            environment = state.EnvironmentName,
            reason = "training_database_safety_check_failed"
          }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
      }

      return Results.Ok(new
      {
        status = "ready",
        environment = state.EnvironmentName,
        database = new
        {
          catalog = state.DatabaseCatalog,
          trainingCatalogAllowed = !state.IsTraining
            || string.Equals(
              state.DatabaseCatalog,
              TrainingEnvironment.RequiredDatabaseCatalog,
              StringComparison.OrdinalIgnoreCase),
          safetyVerified = databaseSafetyVerified,
          schemaVersion,
          sanitized,
          syntheticDataOnly = syntheticOnly,
          runtimeLoginIsolated = loginIsolated
        },
        trainingSafety = new
        {
          active = state.IsTraining,
          externalEffectsBlocked = state.ExternalEffectsBlocked,
          outboundHttpBlocked = state.IsTraining,
          serverOutboundHttpBlocked = state.IsTraining,
          browserOutboundBlocked = state.IsTraining,
          productionCookiesAndKeysIsolated = state.IsTraining
        }
      });
    })
    .AllowAnonymous()
    .WithName("OrionReadiness");

    return endpoints;
  }

  public static IEndpointRouteBuilder MapTrainingBlockedExternalEffectEndpoints(
    this IEndpointRouteBuilder endpoints)
  {
    static IResult Block(string effect) => Results.Problem(
      title: "Acción externa bloqueada",
      detail: TrainingExternalEffectsPolicy.BlockedMessage(effect),
      statusCode: StatusCodes.Status409Conflict);

    endpoints.MapPost("/api/openclaw/reservations", () => Block("la creación de reservaciones mediante OpenClaw"));
    endpoints.MapGet("/api/openclaw/reservations/{reservationId:int}/pdf", () => Block("el acceso externo de OpenClaw"));
    endpoints.MapGet("/api/restaurant/qz/certificate", () => Block("la conexión con QZ Tray y la impresión real"));
    endpoints.MapPost("/api/restaurant/qz/sign", () => Block("la conexión con QZ Tray y la impresión real"));

    return endpoints;
  }
}
