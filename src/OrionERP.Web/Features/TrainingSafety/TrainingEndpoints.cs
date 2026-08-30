using Microsoft.Data.SqlClient;

namespace OrionERP.Web.Features.TrainingSafety;

public static class TrainingEndpoints
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

      string activeCatalog;
      try
      {
        await using var connection = new SqlConnection(
          configuration.GetConnectionString("OrionDb") ?? string.Empty);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT DB_NAME();";
        activeCatalog = Convert.ToString(
          await command.ExecuteScalarAsync(cancellationToken)) ?? string.Empty;
      }
      catch
      {
        return Results.Json(new
        {
          status = "not_ready",
          environment = state.EnvironmentName,
          reason = "database_unavailable"
        }, statusCode: StatusCodes.Status503ServiceUnavailable);
      }

      if (!string.Equals(activeCatalog, state.DatabaseCatalog, StringComparison.OrdinalIgnoreCase))
      {
        return Results.Json(new
        {
          status = "not_ready",
          environment = state.EnvironmentName,
          reason = "database_catalog_mismatch"
        }, statusCode: StatusCodes.Status503ServiceUnavailable);
      }

      return Results.Ok(new
      {
        status = "ready",
        environment = state.EnvironmentName,
        database = new
        {
          catalog = activeCatalog,
          reachable = true,
          trainingCatalogAllowed = !state.IsTraining
            || string.Equals(
              activeCatalog,
              TrainingEnvironment.RequiredDatabaseCatalog,
              StringComparison.OrdinalIgnoreCase)
        },
        training = new
        {
          active = state.IsTraining,
          mode = state.IsTraining ? "production_clone" : "standard",
          existingUsersPreserved = state.IsTraining
        }
      });
    })
    .AllowAnonymous()
    .WithName("OrionReadiness");

    return endpoints;
  }
}
