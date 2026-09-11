using System.Text.Json.Serialization;

namespace OrionERP.DatabaseMigrator;

internal sealed record MigrationManifest(
  int Version,
  IReadOnlyList<MigrationDefinition> Migrations);

internal sealed record MigrationDefinition(
  string Id,
  string Path,
  string Description,
  IReadOnlyList<string> AllowedDatabases,
  IReadOnlyList<MigrationSatisfaction>? SatisfiedBy = null);

internal sealed record MigrationSatisfaction(
  string Id,
  string Path);

internal sealed record AppliedMigration(
  string MigrationId,
  string Checksum,
  DateTime AppliedAtUtc,
  string? AppVersion);

internal sealed record PreviewReceipt(
  string Database,
  string MigrationId,
  string Checksum,
  DateTime PreviewedAtUtc,
  string MachineName,
  string ManifestPath,
  string ScriptPath,
  string Result)
{
  [JsonIgnore]
  public bool IsSuccessful => string.Equals(Result, "DRY_RUN_VALIDATED", StringComparison.Ordinal);
}

internal enum MigrationMode
{
  Inventory,
  Plan,
  Preview,
  Apply,
  Verify
}

internal sealed record MigratorOptions(
  MigrationMode Mode,
  string RepositoryRoot,
  string ManifestPath,
  string? Database,
  string? MigrationId,
  string ConnectionEnvironmentVariable,
  string? PreviewReceiptPath,
  string? BackupReference,
  string? ProductionApproval,
  string? OutputPath,
  int CommandTimeoutSeconds);
