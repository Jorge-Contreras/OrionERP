using OrionERP.UnitTests.Common;

namespace OrionERP.UnitTests.Restaurante;

public sealed class NestedRecipeUnitCostRecalculationSqlTests
{
  private const string MigrationPath =
    "src/OrionERP.Infrastructure/Features/Restaurante/Sql/20260912_nested_recipe_unit_cost_recalculation.sql";

  [Fact]
  public void RuntimeAndMigration_UseTheStoredChildUnitCostWithoutDividingByYieldAgain()
  {
    var service = RepoFile.Read("src/OrionERP.Infrastructure/Features/Restaurante/BomRecipeService.cs");
    var migration = RepoFile.Read(MigrationPath);

    Assert.Contains("COALESCE(subBom.UnitCost, material.BaseUnitPrice", service, StringComparison.Ordinal);
    Assert.Contains("COALESCE(subBom.UnitCost,material.BaseUnitPrice", migration, StringComparison.Ordinal);
    Assert.DoesNotContain("subBom.FrozenTheoreticalCost / NULLIF(subBom.YieldQuantity", service, StringComparison.Ordinal);
    Assert.DoesNotContain("subBom.FrozenTheoreticalCost/NULLIF(subBom.YieldQuantity", migration, StringComparison.Ordinal);
    Assert.Contains("childVersion.FrozenTheoreticalCost AS UnitCost", service, StringComparison.Ordinal);
    Assert.Contains("childVersion.FrozenTheoreticalCost AS UnitCost", migration, StringComparison.Ordinal);
  }

  [Fact]
  public void Migration_IsPreviewSafeAuditedAndLeavesRetiredVersionsHistorical()
  {
    var migration = RepoFile.Read(MigrationPath);

    Assert.Contains("$(ExpectedDatabase)", migration, StringComparison.Ordinal);
    Assert.Contains("$(ApplyChanges)", migration, StringComparison.Ordinal);
    Assert.Contains("$(MigrationId)", migration, StringComparison.Ordinal);
    Assert.Contains("$(MigrationChecksum)", migration, StringComparison.Ordinal);
    Assert.Contains("SET TRANSACTION ISOLATION LEVEL SERIALIZABLE", migration, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("sys.sp_getapplock", migration, StringComparison.Ordinal);
    Assert.Contains("WHERE versionInfo.[Status]='Active'", migration, StringComparison.Ordinal);
    Assert.Contains("WHERE versionInfo.[Status]='Draft'", migration, StringComparison.Ordinal);
    Assert.DoesNotContain("WHERE versionInfo.[Status]='Retired'", migration, StringComparison.Ordinal);
    Assert.Contains("logistica.BomCostRecalculationLog", migration, StringComparison.Ordinal);
    Assert.Contains("ROLLBACK TRANSACTION", migration, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("COMMIT TRANSACTION", migration, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("INSERT orion.SchemaMigration", migration, StringComparison.OrdinalIgnoreCase);
  }
}
