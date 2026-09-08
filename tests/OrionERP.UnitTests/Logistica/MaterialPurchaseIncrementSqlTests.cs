namespace OrionERP.UnitTests.Logistica;

public sealed class MaterialPurchaseIncrementSqlTests
{
  private const string ScriptPath =
    "src/OrionERP.Infrastructure/Features/Logistica/Sql/20260905_material_purchase_increment.sql";

  [Fact]
  public void Upgrade_AddsTheIncrementWhereTheRuleIsResolved()
  {
    var sql = ReadRepoFile(ScriptPath);

    Assert.Contains("COL_LENGTH('logistica.Material', 'PurchaseIncrement') IS NULL", sql, StringComparison.Ordinal);
    Assert.Contains("COL_LENGTH('logistica.MaterialVendor', 'PurchaseIncrement') IS NULL", sql, StringComparison.Ordinal);
    Assert.Contains("COL_LENGTH('logistica.PurchaseOrderLine', 'PurchaseIncrementSnapshot') IS NULL", sql, StringComparison.Ordinal);
    Assert.Contains("CK_Material_PurchaseIncrement CHECK (PurchaseIncrement >= 0)", sql, StringComparison.Ordinal);
    Assert.Contains("PurchaseIncrement IS NULL OR PurchaseIncrement >= 0", sql, StringComparison.Ordinal);
  }

  [Fact]
  public void Upgrade_KeepsEveryExistingMaterialOnWholePresentations()
  {
    var sql = ReadRepoFile(ScriptPath);

    // El default es 1 en las tres columnas: aplicar el script no cambia la conducta de nada.
    Assert.Contains("DF_Material_PurchaseIncrement DEFAULT (1)", sql, StringComparison.Ordinal);
    Assert.Contains("DF_PurchaseOrderLine_PurchaseIncrementSnapshot", sql, StringComparison.Ordinal);
    Assert.Contains("SET PurchaseIncrementSnapshot = 1", sql, StringComparison.Ordinal);

    // El override del proveedor nace en NULL para que herede del material.
    Assert.Contains("ADD PurchaseIncrement decimal(18,4) NULL", sql, StringComparison.Ordinal);
  }

  [Fact]
  public void Upgrade_MakesTheOrderSnapshotMandatoryOnlyAfterBackfilling()
  {
    var sql = ReadRepoFile(ScriptPath);

    var addNullable = sql.IndexOf("ADD PurchaseIncrementSnapshot decimal(18,4) NULL", StringComparison.Ordinal);
    var backfill = sql.IndexOf("SET PurchaseIncrementSnapshot = 1", StringComparison.Ordinal);
    var makeRequired = sql.IndexOf("ALTER COLUMN PurchaseIncrementSnapshot decimal(18,4) NOT NULL", StringComparison.Ordinal);

    Assert.True(addNullable >= 0 && backfill > addNullable && makeRequired > backfill);
  }

  [Fact]
  public void GreenfieldSchema_CreatesMaterialsWithTheIncrement()
  {
    var schema = ReadRepoFile("src/OrionERP.Infrastructure/Features/Logistica/Sql/20260323_logistics_wm_schema.sql");

    Assert.Contains(
      "PurchaseIncrement decimal(18,4) NOT NULL CONSTRAINT DF_Material_PurchaseIncrement DEFAULT (1)",
      schema,
      StringComparison.Ordinal);
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
