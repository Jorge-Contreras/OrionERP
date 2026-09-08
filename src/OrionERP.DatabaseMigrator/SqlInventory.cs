using System.Text.Json;
using System.Text.RegularExpressions;

namespace OrionERP.DatabaseMigrator;

internal static partial class SqlInventory
{
  public static async Task<int> RunAsync(MigratorOptions options, CancellationToken cancellationToken)
  {
    var infrastructureRoot = Path.Combine(options.RepositoryRoot, "src", "OrionERP.Infrastructure", "Features");
    var files = Directory.EnumerateFiles(infrastructureRoot, "*.sql", SearchOption.AllDirectories)
      .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
      .ToArray();

    var entries = new List<object>(files.Length);
    var guardedDatabase = 0;
    var preview = 0;
    var xactAbort = 0;
    var transaction = 0;
    var hardcodedLegacy = 0;

    foreach (var file in files)
    {
      var sql = await File.ReadAllTextAsync(file, cancellationToken);
      var hasDatabaseGuard = sql.Contains("ExpectedDatabase", StringComparison.OrdinalIgnoreCase);
      var hasPreview = sql.Contains("ApplyChanges", StringComparison.OrdinalIgnoreCase);
      var hasXactAbort = XactAbort().IsMatch(sql);
      var hasTransaction = BeginTransaction().IsMatch(sql);
      var hasLegacyLiteral = ContainsLegacyTenantLiteral(sql);

      guardedDatabase += hasDatabaseGuard ? 1 : 0;
      preview += hasPreview ? 1 : 0;
      xactAbort += hasXactAbort ? 1 : 0;
      transaction += hasTransaction ? 1 : 0;
      hardcodedLegacy += hasLegacyLiteral ? 1 : 0;

      entries.Add(new
      {
        path = Path.GetRelativePath(options.RepositoryRoot, file).Replace('\\', '/'),
        expectedDatabase = hasDatabaseGuard,
        applyChanges = hasPreview,
        xactAbort = hasXactAbort,
        transaction = hasTransaction,
        legacyTenantLiteral = hasLegacyLiteral
      });
    }

    var report = new
    {
      generatedAtUtc = DateTime.UtcNow,
      total = files.Length,
      guardedDatabase,
      preview,
      xactAbort,
      transaction,
      hardcodedLegacy,
      entries
    };

    var json = JsonSerializer.Serialize(report, JsonOptions.Indented);
    Console.WriteLine($"SQL files: {files.Length}");
    Console.WriteLine($"ExpectedDatabase: {guardedDatabase}");
    Console.WriteLine($"ApplyChanges: {preview}");
    Console.WriteLine($"XACT_ABORT: {xactAbort}");
    Console.WriteLine($"Transaction: {transaction}");
    Console.WriteLine($"Legacy tenant literals: {hardcodedLegacy}");

    if (options.OutputPath is not null)
    {
      Directory.CreateDirectory(Path.GetDirectoryName(options.OutputPath)!);
      await File.WriteAllTextAsync(options.OutputPath, json, cancellationToken);
      Console.WriteLine($"Inventory: {options.OutputPath}");
    }

    return 0;
  }

  internal static bool ContainsLegacyTenantLiteral(string sql)
    => LegacyLiteral().IsMatch(sql);

  [GeneratedRegex(@"SET\s+XACT_ABORT\s+ON", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
  private static partial Regex XactAbort();

  [GeneratedRegex(@"BEGIN\s+TRAN(?:SACTION)?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
  private static partial Regex BeginTransaction();

  [GeneratedRegex(@"N?'(?:OHM191112Q26|BRUNOS260707L26|SIN_RFC|NO_RFC|SEED)'", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
  private static partial Regex LegacyLiteral();
}

internal static class JsonOptions
{
  public static readonly JsonSerializerOptions Indented = new()
  {
    PropertyNameCaseInsensitive = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = true
  };
}
