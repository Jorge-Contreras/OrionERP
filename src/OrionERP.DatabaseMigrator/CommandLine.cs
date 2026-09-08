namespace OrionERP.DatabaseMigrator;

internal static class CommandLine
{
  private const string DefaultConnectionEnvironmentVariable = "ASPNETCORE_ConnectionStrings__OrionDb";
  private static readonly IReadOnlySet<string> AllowedArguments = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
  {
    "mode",
    "repository-root",
    "manifest",
    "database",
    "migration",
    "connection-env",
    "preview-receipt",
    "backup-reference",
    "production-approval",
    "output",
    "timeout-seconds"
  };

  public static MigratorOptions Parse(string[] args)
  {
    var values = ParseValues(args);
    var mode = ParseMode(GetRequired(values, "mode"));
    var repositoryRoot = ResolveRepositoryRoot(values.GetValueOrDefault("repository-root"));
    var manifestPath = ResolvePath(
      repositoryRoot,
      values.GetValueOrDefault("manifest") ?? "database/orion-migrations.json");

    var database = values.GetValueOrDefault("database");
    if (mode is not MigrationMode.Inventory && string.IsNullOrWhiteSpace(database))
      throw new ArgumentException("--database es obligatorio para plan, preview, apply y verify.");

    var timeout = 300;
    if (values.TryGetValue("timeout-seconds", out var timeoutText) &&
        (!int.TryParse(timeoutText, out timeout) || timeout is < 1 or > 3600))
    {
      throw new ArgumentException("--timeout-seconds debe estar entre 1 y 3600.");
    }

    return new MigratorOptions(
      mode,
      repositoryRoot,
      manifestPath,
      database?.Trim(),
      values.GetValueOrDefault("migration")?.Trim(),
      values.GetValueOrDefault("connection-env")?.Trim() ?? DefaultConnectionEnvironmentVariable,
      ResolveOptionalPath(repositoryRoot, values.GetValueOrDefault("preview-receipt")),
      values.GetValueOrDefault("backup-reference")?.Trim(),
      values.GetValueOrDefault("production-approval")?.Trim(),
      ResolveOptionalPath(repositoryRoot, values.GetValueOrDefault("output")),
      timeout);
  }

  public static string Usage => """
    OrionERP.DatabaseMigrator

    Inventario local, sin conexión:
      --mode inventory [--output artifacts/sql-inventory.json]

    Plan o validación:
      --mode plan|preview|verify --database Orion_Sandbox [--migration <id>]

    Aplicación explícita en Sandbox:
      --mode apply --database Orion_Sandbox --preview-receipt <archivo>
      [--migration <id>]

    Producción exige además:
      --preview-receipt <archivo> --backup-reference <referencia>
      --production-approval "APPLY grupocarpio"

    La conexión se lee exclusivamente de la variable
    ASPNETCORE_ConnectionStrings__OrionDb, o de --connection-env <nombre>.
    Nunca se acepta una cadena de conexión como argumento.
    """;

  private static Dictionary<string, string> ParseValues(string[] args)
  {
    var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var index = 0; index < args.Length; index++)
    {
      var token = args[index];
      if (!token.StartsWith("--", StringComparison.Ordinal))
        throw new ArgumentException($"Argumento inesperado: {token}");

      var key = token[2..];
      if (!AllowedArguments.Contains(key))
        throw new ArgumentException($"Argumento no permitido: --{key}.");
      if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
        throw new ArgumentException($"Falta el valor para --{key}.");

      if (!values.TryAdd(key, args[++index]))
        throw new ArgumentException($"El argumento --{key} está repetido.");
    }

    return values;
  }

  private static MigrationMode ParseMode(string value) => value.Trim().ToLowerInvariant() switch
  {
    "inventory" => MigrationMode.Inventory,
    "plan" => MigrationMode.Plan,
    "preview" => MigrationMode.Preview,
    "apply" => MigrationMode.Apply,
    "verify" => MigrationMode.Verify,
    _ => throw new ArgumentException("--mode debe ser inventory, plan, preview, apply o verify.")
  };

  private static string GetRequired(IReadOnlyDictionary<string, string> values, string key) =>
    values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
      ? value
      : throw new ArgumentException($"--{key} es obligatorio.");

  private static string ResolveRepositoryRoot(string? configuredRoot)
  {
    var current = new DirectoryInfo(Path.GetFullPath(configuredRoot ?? Directory.GetCurrentDirectory()));
    while (current is not null)
    {
      if (File.Exists(Path.Combine(current.FullName, "OrionERP.sln")))
        return current.FullName;
      current = current.Parent;
    }

    throw new DirectoryNotFoundException("No se encontró OrionERP.sln. Use --repository-root.");
  }

  private static string ResolvePath(string root, string path) =>
    Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(root, path));

  private static string? ResolveOptionalPath(string root, string? path) =>
    string.IsNullOrWhiteSpace(path) ? null : ResolvePath(root, path);
}
