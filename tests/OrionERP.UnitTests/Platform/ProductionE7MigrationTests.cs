using System.Text.Json;
using OrionERP.DatabaseMigrator;
using OrionERP.UnitTests.Common;

namespace OrionERP.UnitTests.Platform;

public sealed class ProductionE7MigrationTests
{
  private const string MigrationId = "20260910_production_hospitality_legacy_mechanisms";
  private static readonly string RepositoryRoot = FindRepositoryRoot();
  private static readonly MigrationManifest Manifest = JsonSerializer.Deserialize<MigrationManifest>(
    RepoFile.Read("database/orion-production-migrations.json"),
    JsonOptions.Indented) ?? throw new InvalidOperationException("El manifiesto productivo está vacío.");

  [Fact]
  public void ProductionManifest_RegistersTheConsolidatedE7Package()
  {
    var migration = Manifest.Migrations.Single(item => item.Id == MigrationId);

    Assert.Equal(
      ["Orion_CutoverValidation_20260908", "grupocarpio"],
      migration.AllowedDatabases.Order(StringComparer.Ordinal).ToArray());
    Assert.True(File.Exists(Path.Combine(RepositoryRoot, migration.Path)), migration.Path);
  }

  [Fact]
  public void ProductionPackage_IsTransactionalLedgeredAndCannotTargetSandbox()
  {
    var sql = Read();

    Assert.DoesNotContain("Orion_Sandbox", sql, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("@MigrationId<>N'20260910_production_hospitality_legacy_mechanisms'", sql, StringComparison.Ordinal);
    Assert.Contains("$(ExpectedDatabase)", sql, StringComparison.Ordinal);
    Assert.Contains("$(ApplyChanges)", sql, StringComparison.Ordinal);
    Assert.Contains("$(MigrationChecksum)", sql, StringComparison.Ordinal);
    Assert.Contains("BEGIN TRANSACTION", sql, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("ROLLBACK TRANSACTION", sql, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("INSERT orion.SchemaMigration", sql, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public void OutlookDisposition_IsExactLocalAndDoesNotCallGraph()
  {
    var sql = Read();

    Assert.Contains("ID IN (18,61,105,107)", sql, StringComparison.Ordinal);
    Assert.Contains("VALUES (105,24222", sql, StringComparison.Ordinal);
    Assert.Contains("(107,24228", sql, StringComparison.Ordinal);
    Assert.Contains("DELETE FROM dbo.ROOM_CALENDAR_OUTLOOK_SYNC WHERE ID IN (18,61)", sql, StringComparison.Ordinal);
    Assert.Contains("HospitalityOutlookMappingQuarantine", sql, StringComparison.Ordinal);
    Assert.Contains("HospitalityOutlookMappingRepairAudit", sql, StringComparison.Ordinal);
    Assert.DoesNotContain("graph.microsoft", sql, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("HttpClient", sql, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public void LegacyProcedures_UseProtectedCoresRollbackWrappersAndSeparatedPolicyBatches()
  {
    var sql = Read();

    Assert.Contains("CREATE OR ALTER PROCEDURE dbo.CreateActividadForReservation_E7Core", sql, StringComparison.Ordinal);
    Assert.Contains("CREATE OR ALTER PROCEDURE orion.ReconcileHospitalityPaymentLinks_E7Core", sql, StringComparison.Ordinal);
    Assert.Contains("CREATE OR ALTER PROCEDURE dbo.CreateActividadForReservation", sql, StringComparison.Ordinal);
    Assert.Contains("CREATE OR ALTER PROCEDURE orion.ReconcileHospitalityPaymentLinks", sql, StringComparison.Ordinal);
    Assert.Contains("IF XACT_STATE()<>0 ROLLBACK TRANSACTION", sql, StringComparison.Ordinal);
    Assert.Contains("DENY EXECUTE ON OBJECT::dbo.CreateActividadForReservation_E7Core TO public", sql, StringComparison.Ordinal);
    Assert.Contains("DENY EXECUTE ON OBJECT::orion.ReconcileHospitalityPaymentLinks_E7Core TO public", sql, StringComparison.Ordinal);
    Assert.DoesNotContain(
      "DROP BLOCK PREDICATE ON dbo.Reservation_Transacciones AFTER UPDATE;\n      ALTER SECURITY POLICY",
      sql.Replace("\r\n", "\n", StringComparison.Ordinal),
      StringComparison.Ordinal);
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
