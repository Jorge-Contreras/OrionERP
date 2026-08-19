using Microsoft.Data.SqlClient;

namespace OrionERP.Web.Features.TrainingSafety;

public static class TrainingSafetyValidator
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
    var isTraining = string.Equals(environmentName, TrainingEnvironment.Name, StringComparison.OrdinalIgnoreCase);
    if (isTraining != isMarkedTrainingService)
    {
      throw new InvalidOperationException(
        $"Training startup blocked: host environment '{environmentName}' and the " +
        $"{TrainingEnvironment.ServiceMarkerVariable} service marker do not agree.");
    }

    if (!isTraining)
    {
      var nonTrainingCatalog = TryGetCatalog(connectionString);
      if (string.Equals(
        nonTrainingCatalog,
        TrainingEnvironment.RequiredDatabaseCatalog,
        StringComparison.OrdinalIgnoreCase))
      {
        throw new InvalidOperationException(
          $"Training startup blocked: database '{TrainingEnvironment.RequiredDatabaseCatalog}' may only be used " +
          $"when the host environment and {TrainingEnvironment.ServiceMarkerVariable} both select Training.");
      }

      return;
    }

    var connectionBuilder = GetConnectionBuilderOrThrow(connectionString);
    var catalog = connectionBuilder.InitialCatalog.Trim();
    if (!string.Equals(catalog, TrainingEnvironment.RequiredDatabaseCatalog, StringComparison.OrdinalIgnoreCase))
    {
      throw new InvalidOperationException(
        $"Training startup blocked: ConnectionStrings:OrionDb must target exactly " +
        $"'{TrainingEnvironment.RequiredDatabaseCatalog}', but targets '{catalog}'. " +
        "Production and development databases are never allowed in Training.");
    }
    if (connectionBuilder.IntegratedSecurity
        || !string.Equals(
          connectionBuilder.UserID?.Trim(),
          TrainingEnvironment.RequiredRuntimeLogin,
          StringComparison.Ordinal)
        || string.IsNullOrWhiteSpace(connectionBuilder.Password))
    {
      throw new InvalidOperationException(
        $"Training startup blocked: ConnectionStrings:OrionDb must use the fixed " +
        $"'{TrainingEnvironment.RequiredRuntimeLogin}' SQL-auth credential.");
    }
    if (!string.IsNullOrWhiteSpace(connectionBuilder.AttachDBFilename))
      throw new InvalidOperationException("Training startup blocked: AttachDBFilename is not allowed.");
    if (connectionBuilder.Encrypt == SqlConnectionEncryptOption.Optional)
      throw new InvalidOperationException("Training startup blocked: ConnectionStrings:OrionDb must enable encryption.");

    RequireDistinct(
      isolation.DataProtectionApplicationName,
      PlatformIsolationOptions.ProductionDataProtectionApplicationName,
      "Data Protection application name");
    var trainingKeyPath = isolation.ResolveDataProtectionKeyDirectory(AppContext.BaseDirectory);
    var productionKeyPath = Path.GetFullPath(Path.Combine(
      AppContext.BaseDirectory,
      PlatformIsolationOptions.ProductionDataProtectionKeyPath));
    RequireDistinct(trainingKeyPath, productionKeyPath, "Data Protection key path");
    RequireDistinct(
      isolation.AntiforgeryCookieName,
      PlatformIsolationOptions.ProductionAntiforgeryCookieName,
      "antiforgery cookie name");
    RequireDistinct(
      isolation.IdentityCookieName,
      PlatformIsolationOptions.ProductionIdentityCookieName,
      "Identity cookie name");
    RequireDistinct(
      isolation.KioskDeviceCookieName,
      PlatformIsolationOptions.ProductionKioskDeviceCookieName,
      "workforce kiosk cookie name");

    var hostEntries = (allowedHosts ?? string.Empty)
      .Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    if (hostEntries.Length == 0 || hostEntries.Any(host => host is "*" or "+"))
    {
      throw new InvalidOperationException(
        "Training startup blocked: AllowedHosts must contain explicit local and/or Cloudflare hostnames; wildcards are not allowed.");
    }
    if (!hostEntries.Contains("127.0.0.1", StringComparer.OrdinalIgnoreCase))
      throw new InvalidOperationException("Training startup blocked: AllowedHosts must include 127.0.0.1 for readiness.");

    if (!string.IsNullOrWhiteSpace(publicTrainingOrigin))
    {
      if (!Uri.TryCreate(publicTrainingOrigin, UriKind.Absolute, out var publicOriginUri)
          || string.IsNullOrWhiteSpace(publicOriginUri.Host)
          || !string.IsNullOrEmpty(publicOriginUri.UserInfo)
          || publicOriginUri.AbsolutePath != "/"
          || !string.IsNullOrEmpty(publicOriginUri.Query)
          || !string.IsNullOrEmpty(publicOriginUri.Fragment)
          || (publicOriginUri.Scheme != Uri.UriSchemeHttps
              && !(publicOriginUri.IsLoopback && publicOriginUri.Scheme == Uri.UriSchemeHttp)))
      {
        throw new InvalidOperationException(
          "Training startup blocked: Capacitacion:SandboxBaseUrl must be an HTTPS public origin or loopback HTTP origin.");
      }
      if (string.Equals(publicOriginUri.Host, "orionerp.orion.land", StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Training startup blocked: the production OrionERP origin is forbidden.");
      if (!hostEntries.Contains(publicOriginUri.Host, StringComparer.OrdinalIgnoreCase))
        throw new InvalidOperationException("Training startup blocked: the sandbox origin host must be in AllowedHosts.");
    }

    if (!Uri.TryCreate(windowsServiceUrl, UriKind.Absolute, out var serviceUri)
        || !serviceUri.IsLoopback
        || serviceUri.Scheme != Uri.UriSchemeHttp
        || !string.IsNullOrEmpty(serviceUri.UserInfo)
        || serviceUri.AbsolutePath != "/"
        || !string.IsNullOrEmpty(serviceUri.Query)
        || !string.IsNullOrEmpty(serviceUri.Fragment)
        || TrainingEnvironment.IsReservedProductionPort(serviceUri.Port))
    {
      throw new InvalidOperationException(
        "Training startup blocked: Hosting:WindowsServiceUrl must be a loopback HTTP origin URL " +
        "on a port not reserved by an OrionERP production service (5000, 5010, or 5020).");
    }
  }

  public static string GetCatalogOrThrow(string? connectionString)
    => GetConnectionBuilderOrThrow(connectionString).InitialCatalog.Trim();

  private static SqlConnectionStringBuilder GetConnectionBuilderOrThrow(string? connectionString)
  {
    if (string.IsNullOrWhiteSpace(connectionString))
      throw new InvalidOperationException("Training startup blocked: ConnectionStrings:OrionDb is missing or empty.");

    try
    {
      var builder = new SqlConnectionStringBuilder(connectionString);
      var catalog = builder.InitialCatalog?.Trim();
      if (string.IsNullOrWhiteSpace(catalog))
        throw new InvalidOperationException("Training startup blocked: ConnectionStrings:OrionDb has no database catalog.");

      if (string.IsNullOrWhiteSpace(builder.DataSource))
        throw new InvalidOperationException("Training startup blocked: ConnectionStrings:OrionDb has no SQL Server endpoint.");

      return builder;
    }
    catch (ArgumentException ex)
    {
      throw new InvalidOperationException(
        "Training startup blocked: ConnectionStrings:OrionDb is not a valid SQL Server connection string.", ex);
    }
  }

  private static string? TryGetCatalog(string? connectionString)
  {
    if (string.IsNullOrWhiteSpace(connectionString))
      return null;

    try
    {
      return new SqlConnectionStringBuilder(connectionString).InitialCatalog?.Trim();
    }
    catch (ArgumentException)
    {
      return null;
    }
  }

  private static void RequireDistinct(string trainingValue, string productionValue, string description)
  {
    if (string.IsNullOrWhiteSpace(trainingValue)
        || string.Equals(trainingValue.Trim(), productionValue, StringComparison.OrdinalIgnoreCase))
    {
      throw new InvalidOperationException(
        $"Training startup blocked: the {description} must be configured and distinct from production.");
    }
  }
}
