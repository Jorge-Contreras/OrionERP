using System.Text.Json;
using OrionERP.DatabaseMigrator;
using OrionERP.UnitTests.Common;

namespace OrionERP.UnitTests.Platform;

public sealed class ProductionE8MigrationTests
{
  private static readonly string RepositoryRoot = FindRepositoryRoot();
  private static readonly MigrationManifest Manifest = JsonSerializer.Deserialize<MigrationManifest>(
    RepoFile.Read("database/orion-production-migrations.json"),
    JsonOptions.Indented) ?? throw new InvalidOperationException("El manifiesto productivo está vacío.");

  private static readonly string[] MigrationIds =
  [
    "20260909_production_accounting_rls_scope",
    "20260909_production_inventory_core_rls_scope",
    "20260909_production_fiscal_rls_scope"
  ];

  [Fact]
  public void ProductionManifest_RegistersTheThreeE8PackagesWithExactAllowlist()
  {
    foreach (var migrationId in MigrationIds)
    {
      var migration = Manifest.Migrations.Single(item => item.Id == migrationId);
      Assert.Equal(
        ["Orion_CutoverValidation_20260908", "grupocarpio"],
        migration.AllowedDatabases.Order(StringComparer.Ordinal).ToArray());
      Assert.True(File.Exists(Path.Combine(RepositoryRoot, migration.Path)), migration.Path);
    }
  }

  [Fact]
  public void ProductionPackages_AreTransactionalFailClosedAndLedgered()
  {
    foreach (var migrationId in MigrationIds)
    {
      var migration = Manifest.Migrations.Single(item => item.Id == migrationId);
      var sql = File.ReadAllText(Path.Combine(RepositoryRoot, migration.Path));

      Assert.DoesNotContain("Orion_Sandbox", sql, StringComparison.OrdinalIgnoreCase);
      Assert.Contains("$(ExpectedDatabase)", sql, StringComparison.Ordinal);
      Assert.Contains("$(ApplyChanges)", sql, StringComparison.Ordinal);
      Assert.Contains("$(MigrationId)", sql, StringComparison.Ordinal);
      Assert.Contains("$(MigrationChecksum)", sql, StringComparison.Ordinal);
      Assert.Contains("BEGIN TRANSACTION", sql, StringComparison.OrdinalIgnoreCase);
      Assert.Contains("ROLLBACK TRANSACTION", sql, StringComparison.OrdinalIgnoreCase);
      Assert.Contains("INSERT orion.SchemaMigration", sql, StringComparison.OrdinalIgnoreCase);
      Assert.Contains("no debe admitir bypass por NULL", sql, StringComparison.Ordinal);
    }
  }

  [Fact]
  public void AccountingPackage_LocksTheReviewedSixRowBaselineAndOnlyItsTwoTables()
  {
    var sql = Read("20260909_production_accounting_rls_scope");

    Assert.Contains("ID NOT IN(53495,53496)", sql, StringComparison.Ordinal);
    Assert.Contains("id NOT IN(44843,44844,44845,44846)", sql, StringComparison.Ordinal);
    Assert.Contains("CREATE SECURITY POLICY contabilidad.AccountingScopePolicy", sql, StringComparison.Ordinal);
    Assert.Contains("ON dbo.Transacciones", sql, StringComparison.Ordinal);
    Assert.Contains("ON dbo.Registro_Contable", sql, StringComparison.Ordinal);
    Assert.DoesNotContain("ON dbo.TRANSACTION_ATTACHMENT", sql, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("ON dbo.CuentasContables", sql, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("ON cfdi.Comprobante", sql, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public void InventoryPackage_MovesOnlyTheReviewedThreeTablesAndPreservesTheRest()
  {
    var sql = Read("20260909_production_inventory_core_rls_scope");

    Assert.Contains("INSERT @Lote VALUES(N'Location'),(N'StockBalance'),(N'StockTransaction')", sql, StringComparison.Ordinal);
    Assert.Contains("CREATE SECURITY POLICY logistica.InventoryCoreScopePolicy", sql, StringComparison.Ordinal);
    Assert.Contains("<>294", sql, StringComparison.Ordinal);
    Assert.Contains("<>285", sql, StringComparison.Ordinal);
    Assert.DoesNotContain("(N'Material')", sql, StringComparison.Ordinal);
    Assert.DoesNotContain("(N'MaterialLot')", sql, StringComparison.Ordinal);
    Assert.DoesNotContain("(N'LotBalance')", sql, StringComparison.Ordinal);
  }

  [Fact]
  public void FiscalPackage_LeavesCfdiOwnershipAndDeployScriptOutsideTheBatch()
  {
    var sql = Read("20260909_production_fiscal_rls_scope");

    Assert.Contains("CREATE SECURITY POLICY fiscal.DeclarationScopePolicy", sql, StringComparison.Ordinal);
    Assert.Contains("E8d no debe imponer propiedad exclusiva a cfdi.Comprobante", sql, StringComparison.Ordinal);
    Assert.DoesNotContain("ALTER SECURITY POLICY fiscal.ComprobanteScopePolicy", sql, StringComparison.OrdinalIgnoreCase);
    foreach (var write in new[] { "INSERT cfdi.", "UPDATE cfdi.", "DELETE cfdi." })
      Assert.DoesNotContain(write, sql, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("20260907_fiscal_declaracion_deploy.ps1'", sql, StringComparison.OrdinalIgnoreCase);
  }

  private static string Read(string migrationId)
  {
    var migration = Manifest.Migrations.Single(item => item.Id == migrationId);
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
