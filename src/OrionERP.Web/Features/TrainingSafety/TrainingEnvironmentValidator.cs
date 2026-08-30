using Microsoft.Data.SqlClient;

namespace OrionERP.Web.Features.TrainingSafety;

public static class TrainingEnvironmentValidator
{
  public static void ValidateStartup(
    string environmentName,
    string? connectionString,
    PlatformIsolationOptions isolation,
    string? windowsServiceUrl,
    bool isMarkedTrainingService,
    string? allowedHosts = "localhost;127.0.0.1",
    string? publicTrainingOrigin = null)
  {
    var isTraining = string.Equals(
      environmentName,
      TrainingEnvironment.Name,
      StringComparison.OrdinalIgnoreCase);

    if (isTraining != isMarkedTrainingService)
    {
      throw new InvalidOperationException(
        $"Training startup blocked: host environment '{environmentName}' and the " +
        $"{TrainingEnvironment.ServiceMarkerVariable} service marker do not agree.");
    }

    var catalog = TryGetCatalog(connectionString);
    if (!isTraining)
    {
      if (string.Equals(catalog, TrainingEnvironment.RequiredDatabaseCatalog, StringComparison.OrdinalIgnoreCase))
      {
        throw new InvalidOperationException(
          $"Training startup blocked: '{TrainingEnvironment.RequiredDatabaseCatalog}' may only be used by the Training service.");
      }

      return;
    }

    var connectionBuilder = GetConnectionBuilderOrThrow(connectionString);
    if (!string.Equals(
      connectionBuilder.InitialCatalog.Trim(),
      TrainingEnvironment.RequiredDatabaseCatalog,
      StringComparison.OrdinalIgnoreCase))
    {
      throw new InvalidOperationException(
        $"Training startup blocked: ConnectionStrings:OrionDb must target exactly " +
        $"'{TrainingEnvironment.RequiredDatabaseCatalog}', but targets '{connectionBuilder.InitialCatalog}'.");
    }

    if (!string.IsNullOrWhiteSpace(connectionBuilder.AttachDBFilename))
      throw new InvalidOperationException("Training startup blocked: AttachDBFilename is not allowed.");
    if (connectionBuilder.Encrypt == SqlConnectionEncryptOption.Optional)
      throw new InvalidOperationException("Training startup blocked: ConnectionStrings:OrionDb must enable encryption.");

    RequireDistinctPath(
      isolation.ResolveDataProtectionKeyDirectory(AppContext.BaseDirectory),
      Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, PlatformIsolationOptions.ProductionDataProtectionKeyPath)),
      "Data Protection key path");
    RequireDistinctValue(
      isolation.AntiforgeryCookieName,
      PlatformIsolationOptions.ProductionAntiforgeryCookieName,
      "antiforgery cookie name");
    RequireDistinctValue(
      isolation.IdentityCookieName,
      PlatformIsolationOptions.ProductionIdentityCookieName,
      "Identity cookie name");
    RequireDistinctValue(
      isolation.KioskDeviceCookieName,
      PlatformIsolationOptions.ProductionKioskDeviceCookieName,
      "workforce kiosk cookie name");

    var hostEntries = (allowedHosts ?? string.Empty)
      .Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    if (hostEntries.Length == 0 || hostEntries.Any(host => host is "*" or "+"))
      throw new InvalidOperationException("Training startup blocked: AllowedHosts must use explicit hostnames.");
    if (!hostEntries.Contains("127.0.0.1", StringComparer.OrdinalIgnoreCase))
      throw new InvalidOperationException("Training startup blocked: AllowedHosts must include 127.0.0.1 for readiness.");

    if (!string.IsNullOrWhiteSpace(publicTrainingOrigin))
      ValidatePublicOrigin(publicTrainingOrigin);

    if (!Uri.TryCreate(windowsServiceUrl, UriKind.Absolute, out var serviceUri)
        || serviceUri.Scheme != Uri.UriSchemeHttp
        || !serviceUri.IsLoopback
        || !string.IsNullOrEmpty(serviceUri.UserInfo)
        || serviceUri.AbsolutePath != "/"
        || !string.IsNullOrEmpty(serviceUri.Query)
        || !string.IsNullOrEmpty(serviceUri.Fragment)
        || serviceUri.Port <= 0
        || TrainingEnvironment.IsReservedProductionPort(serviceUri.Port))
    {
      throw new InvalidOperationException(
        "Training startup blocked: Hosting:WindowsServiceUrl must be a dedicated loopback HTTP origin.");
    }
  }

  private static SqlConnectionStringBuilder GetConnectionBuilderOrThrow(string? connectionString)
  {
    if (string.IsNullOrWhiteSpace(connectionString))
      throw new InvalidOperationException("Training startup blocked: ConnectionStrings:OrionDb is missing.");

    try
    {
      return new SqlConnectionStringBuilder(connectionString);
    }
    catch (ArgumentException exception)
    {
      throw new InvalidOperationException(
        "Training startup blocked: ConnectionStrings:OrionDb is invalid.",
        exception);
    }
  }

  private static string TryGetCatalog(string? connectionString)
  {
    if (string.IsNullOrWhiteSpace(connectionString)) return string.Empty;
    try
    {
      return new SqlConnectionStringBuilder(connectionString).InitialCatalog?.Trim() ?? string.Empty;
    }
    catch (ArgumentException)
    {
      return string.Empty;
    }
  }

  private static void ValidatePublicOrigin(string value)
  {
    if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
        || string.IsNullOrWhiteSpace(uri.Host)
        || !string.IsNullOrEmpty(uri.UserInfo)
        || uri.AbsolutePath != "/"
        || !string.IsNullOrEmpty(uri.Query)
        || !string.IsNullOrEmpty(uri.Fragment)
        || (uri.Scheme != Uri.UriSchemeHttps && !(uri.IsLoopback && uri.Scheme == Uri.UriSchemeHttp)))
    {
      throw new InvalidOperationException("Training startup blocked: Capacitacion:SandboxBaseUrl must be an HTTPS origin.");
    }

    if (string.Equals(uri.Host, "orionerp.orion.land", StringComparison.OrdinalIgnoreCase))
      throw new InvalidOperationException("Training startup blocked: the public Training origin cannot be the production origin.");
  }

  private static void RequireDistinctPath(string actual, string productionValue, string label)
  {
    if (string.Equals(
      Path.GetFullPath(actual),
      Path.GetFullPath(productionValue),
      OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
    {
      throw new InvalidOperationException($"Training startup blocked: {label} must remain separate from the live service.");
    }
  }

  private static void RequireDistinctValue(string actual, string productionValue, string label)
  {
    if (string.Equals(actual, productionValue, StringComparison.Ordinal))
      throw new InvalidOperationException($"Training startup blocked: {label} must remain separate from the live service.");
  }
}
