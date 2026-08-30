using Microsoft.Data.SqlClient;

namespace OrionERP.Web.Features.TrainingSafety;

public static class TrainingEnvironmentServiceCollectionExtensions
{
  public static IServiceCollection AddTrainingEnvironment(
    this IServiceCollection services,
    string environmentName,
    string connectionString,
    PlatformIsolationOptions isolation,
    string? windowsServiceUrl,
    bool isMarkedTrainingService,
    string? allowedHosts)
  {
    var isTraining = string.Equals(
      environmentName,
      TrainingEnvironment.Name,
      StringComparison.OrdinalIgnoreCase);

    if (isTraining)
    {
      TrainingEnvironmentValidator.ValidateStartup(
        environmentName,
        connectionString,
        isolation,
        windowsServiceUrl,
        isMarkedTrainingService,
        allowedHosts);
    }

    var databaseCatalog = TryGetCatalog(connectionString);
    services.AddSingleton<ITrainingEnvironmentState>(
      new TrainingEnvironmentState(isTraining, environmentName, databaseCatalog));
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
