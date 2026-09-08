using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace OrionERP.DatabaseMigrator;

internal sealed class MigrationRunner(MigratorOptions options)
{
  private const string ProductionDatabase = "grupocarpio";
  private static readonly IReadOnlySet<string> KnownDatabases = new HashSet<string>(StringComparer.Ordinal)
  {
    "Orion_Sandbox",
    // Isolated restore of the approved production backup for this cutover.
    "Orion_CutoverValidation_20260908",
    ProductionDatabase
  };
  private static readonly TimeSpan MaximumPreviewAge = TimeSpan.FromHours(24);

  public async Task<int> RunAsync(CancellationToken cancellationToken)
  {
    var manifest = await LoadManifestAsync(cancellationToken);
    var selected = SelectMigrations(manifest);
    var connectionString = ReadConnectionString();

    await using var connection = new SqlConnection(connectionString);
    connection.InfoMessage += (_, eventArgs) => Console.WriteLine(eventArgs.Message);
    await connection.OpenAsync(cancellationToken);
    await ValidateDatabaseAsync(connection, cancellationToken);

    var applied = await LoadAppliedMigrationsAsync(connection, cancellationToken);
    if (options.Mode is MigrationMode.Plan)
      return PrintPlan(selected, applied);
    if (options.Mode is MigrationMode.Verify)
      return await VerifyAsync(selected, applied, cancellationToken);

    foreach (var migration in selected)
      await ExecuteMigrationAsync(connection, migration, applied, cancellationToken);

    return 0;
  }

  private async Task<MigrationManifest> LoadManifestAsync(CancellationToken cancellationToken)
  {
    if (!File.Exists(options.ManifestPath))
      throw new FileNotFoundException("No se encontró el manifiesto de migraciones.", options.ManifestPath);

    await using var stream = File.OpenRead(options.ManifestPath);
    var manifest = await JsonSerializer.DeserializeAsync<MigrationManifest>(stream, JsonOptions.Indented, cancellationToken)
      ?? throw new InvalidOperationException("El manifiesto de migraciones está vacío.");

    if (manifest.Version != 1)
      throw new InvalidOperationException($"Versión de manifiesto no soportada: {manifest.Version}.");
    if (manifest.Migrations.Count == 0)
      throw new InvalidOperationException("El manifiesto no contiene migraciones.");

    var duplicate = manifest.Migrations
      .GroupBy(item => item.Id, StringComparer.Ordinal)
      .FirstOrDefault(group => group.Count() > 1);
    if (duplicate is not null)
      throw new InvalidOperationException($"MigrationId duplicado: {duplicate.Key}.");

    foreach (var migration in manifest.Migrations)
    {
      if (!Regex.IsMatch(migration.Id, "^[0-9]{8}_[a-z0-9_]+$", RegexOptions.CultureInvariant))
        throw new InvalidOperationException($"MigrationId inválido: {migration.Id}.");
      if (!migration.Path.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException($"La ruta de {migration.Id} debe terminar en .sql.");
      if (migration.AllowedDatabases.Count == 0)
        throw new InvalidOperationException($"{migration.Id} no declara bases permitidas.");
      if (migration.AllowedDatabases.Distinct(StringComparer.Ordinal).Count() != migration.AllowedDatabases.Count ||
          migration.AllowedDatabases.Any(database => !KnownDatabases.Contains(database)))
      {
        throw new InvalidOperationException(
          $"{migration.Id} contiene una base repetida, desconocida o con casing no canónico.");
      }
    }

    return manifest;
  }

  private IReadOnlyList<MigrationDefinition> SelectMigrations(MigrationManifest manifest)
  {
    var selected = options.MigrationId is null
      ? manifest.Migrations
        .Where(item => item.AllowedDatabases.Contains(options.Database!, StringComparer.Ordinal))
        .ToArray()
      : manifest.Migrations.Where(item => string.Equals(item.Id, options.MigrationId, StringComparison.Ordinal)).ToArray();

    if (selected.Length == 0)
      throw new InvalidOperationException(options.MigrationId is null
        ? $"No hay migraciones declaradas para {options.Database}."
        : $"No existe la migración '{options.MigrationId}'.");
    if (options.Mode is MigrationMode.Apply && selected.Length != 1)
      throw new InvalidOperationException("Apply ejecuta una migración por vez; use --migration <id>.");

    foreach (var migration in selected)
    {
      if (!migration.AllowedDatabases.Contains(options.Database!, StringComparer.OrdinalIgnoreCase))
        throw new InvalidOperationException($"{migration.Id} no permite la base {options.Database}.");
    }

    return selected;
  }

  private string ReadConnectionString()
  {
    var connectionString = Environment.GetEnvironmentVariable(options.ConnectionEnvironmentVariable);
    if (string.IsNullOrWhiteSpace(connectionString))
      throw new InvalidOperationException($"Falta la variable {options.ConnectionEnvironmentVariable}.");

    var builder = new SqlConnectionStringBuilder(connectionString);
    if (string.IsNullOrWhiteSpace(builder.InitialCatalog))
      throw new InvalidOperationException("La conexión debe declarar Database/Initial Catalog.");
    if (!string.Equals(builder.InitialCatalog, options.Database, StringComparison.OrdinalIgnoreCase))
      throw new InvalidOperationException("La base de la conexión no coincide con --database.");

    return connectionString;
  }

  private async Task ValidateDatabaseAsync(SqlConnection connection, CancellationToken cancellationToken)
  {
    await using var command = connection.CreateCommand();
    command.CommandText = "SELECT DB_NAME();";
    command.CommandTimeout = options.CommandTimeoutSeconds;
    var actualDatabase = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken));
    if (!string.Equals(actualDatabase, options.Database, StringComparison.OrdinalIgnoreCase))
      throw new InvalidOperationException("La conexión abrió una base distinta a --database.");
  }

  private async Task<Dictionary<string, AppliedMigration>> LoadAppliedMigrationsAsync(
    SqlConnection connection,
    CancellationToken cancellationToken)
  {
    await using var command = connection.CreateCommand();
    command.CommandTimeout = options.CommandTimeoutSeconds;
    command.CommandText = """
      IF OBJECT_ID(N'orion.SchemaMigration', N'U') IS NOT NULL
      BEGIN
        SELECT MigrationId, Checksum, AppliedAtUtc, AppVersion
        FROM orion.SchemaMigration;
      END;
      """;

    var applied = new Dictionary<string, AppliedMigration>(StringComparer.Ordinal);
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken))
    {
      var item = new AppliedMigration(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetDateTime(2),
        reader.IsDBNull(3) ? null : reader.GetString(3));
      applied.Add(item.MigrationId, item);
    }

    return applied;
  }

  private int PrintPlan(
    IReadOnlyList<MigrationDefinition> selected,
    IReadOnlyDictionary<string, AppliedMigration> applied)
  {
    Console.WriteLine($"Database: {options.Database}");
    foreach (var migration in selected)
    {
      var (_, checksum) = ReadScript(migration);
      var status = applied.TryGetValue(migration.Id, out var existing)
        ? string.Equals(existing.Checksum, checksum, StringComparison.OrdinalIgnoreCase) ? "APPLIED" : "CHECKSUM_MISMATCH"
        : "PENDING";
      Console.WriteLine($"{migration.Id}  {status}  {checksum[..12]}  {migration.Description}");
    }

    return 0;
  }

  private async Task<int> VerifyAsync(
    IReadOnlyList<MigrationDefinition> selected,
    IReadOnlyDictionary<string, AppliedMigration> applied,
    CancellationToken cancellationToken)
  {
    var failed = false;
    foreach (var migration in selected)
    {
      cancellationToken.ThrowIfCancellationRequested();
      var (_, checksum) = ReadScript(migration);
      if (!applied.TryGetValue(migration.Id, out var existing))
      {
        failed = true;
        Console.WriteLine($"{migration.Id}: PENDING");
        continue;
      }

      if (!string.Equals(existing.Checksum, checksum, StringComparison.OrdinalIgnoreCase))
      {
        failed = true;
        Console.WriteLine($"{migration.Id}: CHECKSUM_MISMATCH");
      }
      else
      {
        Console.WriteLine($"{migration.Id}: VERIFIED");
      }
    }

    return failed ? 2 : 0;
  }

  private async Task ExecuteMigrationAsync(
    SqlConnection connection,
    MigrationDefinition migration,
    IReadOnlyDictionary<string, AppliedMigration> applied,
    CancellationToken cancellationToken)
  {
    var (rawScript, checksum) = ReadScript(migration);
    ValidateScriptContract(migration, rawScript);
    if (applied.TryGetValue(migration.Id, out var existing))
    {
      if (!string.Equals(existing.Checksum, checksum, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException($"La migración aplicada {migration.Id} cambió de checksum.");

      Console.WriteLine($"{migration.Id}: ya aplicada; se omite.");
      return;
    }

    if (options.Mode is MigrationMode.Apply)
      await ValidateApplyAuthorizationAsync(migration, checksum, cancellationToken);

    var applyChanges = options.Mode is MigrationMode.Apply ? "1" : "0";
    var script = rawScript
      .Replace("$(ExpectedDatabase)", options.Database, StringComparison.Ordinal)
      .Replace("$(ApplyChanges)", applyChanges, StringComparison.Ordinal)
      .Replace("$(MigrationId)", migration.Id, StringComparison.Ordinal)
      .Replace("$(MigrationChecksum)", checksum, StringComparison.Ordinal)
      .Replace("$(AppVersion)", typeof(MigrationRunner).Assembly.GetName().Version?.ToString() ?? "unknown", StringComparison.Ordinal);

    if (script.Contains("$(", StringComparison.Ordinal))
      throw new InvalidOperationException($"{migration.Id} conserva variables SQLCMD sin resolver.");

    Console.WriteLine($"{migration.Id}: {(options.Mode is MigrationMode.Apply ? "APPLY" : "PREVIEW")}");
    foreach (var batch in SqlBatchSplitter.Split(script))
      await ExecuteBatchAsync(connection, batch, cancellationToken);

    await EnsureNoOpenTransactionAsync(connection, migration, cancellationToken);

    if (options.Mode is MigrationMode.Preview)
    {
      await EnsurePreviewDidNotPersistAsync(connection, migration, cancellationToken);
      await WritePreviewReceiptAsync(migration, checksum, cancellationToken);
    }
    else
    {
      await EnsureMigrationRecordedAsync(connection, migration, checksum, cancellationToken);
    }
  }

  private async Task ValidateApplyAuthorizationAsync(
    MigrationDefinition migration,
    string checksum,
    CancellationToken cancellationToken)
  {
    if (options.PreviewReceiptPath is null || !File.Exists(options.PreviewReceiptPath))
      throw new InvalidOperationException("Apply exige un --preview-receipt existente.");

    await using var stream = File.OpenRead(options.PreviewReceiptPath);
    var receipt = await JsonSerializer.DeserializeAsync<PreviewReceipt>(stream, JsonOptions.Indented, cancellationToken)
      ?? throw new InvalidOperationException("El recibo de preview está vacío.");

    var now = DateTime.UtcNow;
    if (!receipt.IsSuccessful ||
        !string.Equals(receipt.Database, options.Database, StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(receipt.MigrationId, migration.Id, StringComparison.Ordinal) ||
        !string.Equals(receipt.Checksum, checksum, StringComparison.OrdinalIgnoreCase) ||
        receipt.PreviewedAtUtc > now.AddMinutes(5) ||
        now - receipt.PreviewedAtUtc > MaximumPreviewAge)
    {
      throw new InvalidOperationException("El recibo de preview no coincide o tiene más de 24 horas.");
    }

    if (!string.Equals(options.Database, ProductionDatabase, StringComparison.OrdinalIgnoreCase))
      return;

    if (!string.Equals(options.ProductionApproval, $"APPLY {ProductionDatabase}", StringComparison.Ordinal))
      throw new InvalidOperationException($"Producción exige --production-approval \"APPLY {ProductionDatabase}\".");
    if (string.IsNullOrWhiteSpace(options.BackupReference))
      throw new InvalidOperationException("Producción exige --backup-reference.");
  }

  private static void ValidateScriptContract(MigrationDefinition migration, string script)
  {
    var requiredTokens = new[]
    {
      "$(ExpectedDatabase)",
      "$(ApplyChanges)",
      "$(MigrationId)",
      "$(MigrationChecksum)",
      "SET XACT_ABORT ON",
      "BEGIN TRANSACTION",
      "ROLLBACK TRANSACTION"
    };

    var missing = requiredTokens
      .Where(token => !script.Contains(token, StringComparison.OrdinalIgnoreCase))
      .ToArray();
    if (missing.Length > 0)
      throw new InvalidOperationException($"{migration.Id} no cumple el contrato seguro: {string.Join(", ", missing)}.");
  }

  private async Task EnsurePreviewDidNotPersistAsync(
    SqlConnection connection,
    MigrationDefinition migration,
    CancellationToken cancellationToken)
  {
    await using var command = connection.CreateCommand();
    command.CommandTimeout = options.CommandTimeoutSeconds;
    command.CommandText = """
      IF OBJECT_ID(N'orion.SchemaMigration', N'U') IS NULL
        SELECT CONVERT(bit, 0);
      ELSE
        SELECT CONVERT(bit, CASE WHEN EXISTS
        (
          SELECT 1 FROM orion.SchemaMigration WHERE MigrationId = @MigrationId
        ) THEN 1 ELSE 0 END);
      """;
    command.Parameters.AddWithValue("@MigrationId", migration.Id);
    var persisted = Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken));
    if (persisted)
      throw new InvalidOperationException($"El preview de {migration.Id} persistió una fila de ledger.");
  }

  private async Task EnsureNoOpenTransactionAsync(
    SqlConnection connection,
    MigrationDefinition migration,
    CancellationToken cancellationToken)
  {
    await using var command = connection.CreateCommand();
    command.CommandTimeout = options.CommandTimeoutSeconds;
    command.CommandText = "SELECT @@TRANCOUNT;";
    var transactionCount = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    if (transactionCount == 0)
      return;

    command.CommandText = "IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;";
    await command.ExecuteNonQueryAsync(cancellationToken);
    throw new InvalidOperationException(
      $"{migration.Id} terminó con una transacción abierta; se revirtió y no se emitirá recibo.");
  }

  private async Task EnsureMigrationRecordedAsync(
    SqlConnection connection,
    MigrationDefinition migration,
    string checksum,
    CancellationToken cancellationToken)
  {
    await using var command = connection.CreateCommand();
    command.CommandTimeout = options.CommandTimeoutSeconds;
    command.CommandText = """
      SELECT Checksum
      FROM orion.SchemaMigration
      WHERE MigrationId = @MigrationId;
      """;
    command.Parameters.AddWithValue("@MigrationId", migration.Id);
    var recordedChecksum = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken));
    if (!string.Equals(recordedChecksum, checksum, StringComparison.OrdinalIgnoreCase))
      throw new InvalidOperationException($"{migration.Id} no quedó registrada con el checksum ejecutado.");
  }

  private async Task ExecuteBatchAsync(SqlConnection connection, string batch, CancellationToken cancellationToken)
  {
    await using var command = connection.CreateCommand();
    command.CommandText = batch;
    command.CommandTimeout = options.CommandTimeoutSeconds;

    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    do
    {
      if (reader.FieldCount == 0)
        continue;

      var headers = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName);
      Console.WriteLine(string.Join(" | ", headers));
      var rows = 0;
      while (await reader.ReadAsync(cancellationToken))
      {
        if (++rows > 200)
          throw new InvalidOperationException("Una migración devolvió más de 200 filas de revisión.");

        var values = Enumerable.Range(0, reader.FieldCount)
          .Select(index => reader.IsDBNull(index) ? "NULL" : Convert.ToString(reader.GetValue(index)) ?? string.Empty);
        Console.WriteLine(string.Join(" | ", values));
      }
    }
    while (await reader.NextResultAsync(cancellationToken));
  }

  private (string Script, string Checksum) ReadScript(MigrationDefinition migration)
  {
    var path = Path.GetFullPath(Path.Combine(options.RepositoryRoot, migration.Path));
    var relative = Path.GetRelativePath(options.RepositoryRoot, path);
    if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
      throw new InvalidOperationException($"La ruta de {migration.Id} sale del repositorio.");
    if (!File.Exists(path))
      throw new FileNotFoundException($"No se encontró el script de {migration.Id}.", path);

    var bytes = File.ReadAllBytes(path);
    var checksum = Convert.ToHexString(SHA256.HashData(bytes));
    return (Encoding.UTF8.GetString(bytes), checksum);
  }

  private async Task WritePreviewReceiptAsync(
    MigrationDefinition migration,
    string checksum,
    CancellationToken cancellationToken)
  {
    var receiptPath = options.OutputPath ?? Path.Combine(
      options.RepositoryRoot,
      "artifacts",
      "database-migrations",
      options.Database!,
      $"{migration.Id}-{checksum[..12]}.preview.json");

    var scriptPath = Path.GetFullPath(Path.Combine(options.RepositoryRoot, migration.Path));
    var receipt = new PreviewReceipt(
      options.Database!,
      migration.Id,
      checksum,
      DateTime.UtcNow,
      Environment.MachineName,
      Path.GetRelativePath(options.RepositoryRoot, options.ManifestPath).Replace('\\', '/'),
      Path.GetRelativePath(options.RepositoryRoot, scriptPath).Replace('\\', '/'),
      "DRY_RUN_VALIDATED");

    Directory.CreateDirectory(Path.GetDirectoryName(receiptPath)!);
    await File.WriteAllTextAsync(
      receiptPath,
      JsonSerializer.Serialize(receipt, JsonOptions.Indented),
      cancellationToken);
    Console.WriteLine($"Preview receipt: {receiptPath}");
  }
}
