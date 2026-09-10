using System.Text.Json;
using OrionERP.DatabaseMigrator;
using OrionERP.UnitTests.Common;

namespace OrionERP.UnitTests.Platform;

public sealed class ProductionRestaurantEventDispositionTests
{
  private const string MigrationId = "20260910_production_restaurant_event_outbox_disposition";
  private static readonly string RepositoryRoot = FindRepositoryRoot();
  private static readonly MigrationManifest Manifest = JsonSerializer.Deserialize<MigrationManifest>(
    RepoFile.Read("database/orion-production-migrations.json"),
    JsonOptions.Indented) ?? throw new InvalidOperationException("El manifiesto productivo está vacío.");

  [Fact]
  public void ProductionManifest_RegistersTheAuditedHistoricalDisposition()
  {
    var migration = Manifest.Migrations.Single(item => item.Id == MigrationId);

    Assert.Equal(
      ["Orion_CutoverValidation_20260908", "grupocarpio"],
      migration.AllowedDatabases.Order(StringComparer.Ordinal).ToArray());
    Assert.True(File.Exists(Path.Combine(RepositoryRoot, migration.Path)), migration.Path);
  }

  [Fact]
  public void HistoricalSet_IsExactFingerprintProtectedAndDoesNotReachNewEvents()
  {
    var sql = Read();

    Assert.Contains("@FirstEventId bigint=125", sql, StringComparison.Ordinal);
    Assert.Contains("@LastEventId bigint=2124", sql, StringComparison.Ordinal);
    Assert.Contains("@ExpectedRows int=2000", sql, StringComparison.Ordinal);
    Assert.Contains("04EBA7B4621BDA85F36439B882ABCD7984379CFC52977274CACD6F4587B58931", sql, StringComparison.Ordinal);
    Assert.Contains("Id BETWEEN @FirstEventId AND @LastEventId", sql, StringComparison.Ordinal);
    Assert.Contains("PublishedAt IS NULL", sql, StringComparison.Ordinal);
    Assert.Contains("Attempts=0", sql, StringComparison.Ordinal);
    Assert.Contains("IF @ActualReviewedSetChecksum<>@ReviewedSetChecksum", sql, StringComparison.Ordinal);
  }

  [Fact]
  public void Disposition_IsTransactionalLedgeredAndAuditedPerEvent()
  {
    var sql = Read();

    Assert.DoesNotContain("Orion_Sandbox", sql, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("BEGIN TRANSACTION", sql, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("ROLLBACK TRANSACTION", sql, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("CREATE TABLE restaurante.EventOutboxDispositionAudit", sql, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("TR_EventOutboxDispositionAudit_Immutable", sql, StringComparison.Ordinal);
    Assert.Contains("PayloadHash", sql, StringComparison.Ordinal);
    Assert.Contains("SUPERSEDED_BEFORE_SCOPED_BROADCASTER", sql, StringComparison.Ordinal);
    Assert.Contains("INSERT orion.SchemaMigration", sql, StringComparison.OrdinalIgnoreCase);
  }

  private static string Read()
  {
    var migration = Manifest.Migrations.Single(item => item.Id == MigrationId);
    return File.ReadAllText(Path.Combine(RepositoryRoot, migration.Path));
  }

  private static string FindRepositoryRoot()
  {
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "OrionERP.sln")))
      directory = directory.Parent;
    return directory?.FullName
      ?? throw new InvalidOperationException("No se encontró la raíz del repositorio.");
  }
}
