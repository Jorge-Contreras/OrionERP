namespace OrionERP.UnitTests.Logistica;

public sealed class MaterialBaseUnitPriceSqlTests
{
  [Fact]
  public void Upgrade_RenamesLegacyColumnsWithoutConvertingStoredValues()
  {
    var sql = ReadRepoFile("src/OrionERP.Infrastructure/Features/Logistica/Sql/20260807_material_base_unit_price.sql");

    Assert.Contains("COL_LENGTH('logistica.Material', 'BaseUnitPrice') IS NULL", sql, StringComparison.Ordinal);
    Assert.Contains("sp_rename 'logistica.Material.Price', 'BaseUnitPrice'", sql, StringComparison.Ordinal);
    Assert.Contains("sp_rename 'logistica.PurchaseOrderLine.UnitPrice', 'BaseUnitPrice'", sql, StringComparison.Ordinal);
    Assert.Contains("ALTER COLUMN BaseUnitPrice decimal(18,6) NULL", sql, StringComparison.Ordinal);
    Assert.DoesNotContain("PurchaseQuantity", sql, StringComparison.Ordinal);
    Assert.DoesNotContain("UPDATE logistica.Material", sql, StringComparison.Ordinal);
  }

  [Fact]
  public void GreenfieldAndLegacyScripts_UseExplicitBaseUnitPrice()
  {
    var schema = ReadRepoFile("src/OrionERP.Infrastructure/Features/Logistica/Sql/20260323_logistics_wm_schema.sql");
    var legacyMigration = ReadRepoFile("src/OrionERP.Infrastructure/Features/Logistica/Sql/20260323_logistics_wm_migration.sql");
    var purchasing = ReadRepoFile("src/OrionERP.Infrastructure/Features/Logistica/Sql/20260417_logistics_purchasing_v1.sql");

    Assert.Contains("BaseUnitPrice decimal(18,6) NULL", schema, StringComparison.Ordinal);
    Assert.Contains("CAST(legacy.PRECIO AS decimal(18,6)) AS BaseUnitPrice", legacyMigration, StringComparison.Ordinal);
    Assert.Contains("BaseUnitPrice decimal(18,6) NULL", purchasing, StringComparison.Ordinal);
  }

  private static string ReadRepoFile(string relativePath)
  {
    var current = new DirectoryInfo(AppContext.BaseDirectory);
    while (current is not null && !File.Exists(Path.Combine(current.FullName, "OrionERP.sln")))
    {
      current = current.Parent;
    }

    Assert.NotNull(current);
    return File.ReadAllText(Path.Combine(current!.FullName, relativePath));
  }
}
