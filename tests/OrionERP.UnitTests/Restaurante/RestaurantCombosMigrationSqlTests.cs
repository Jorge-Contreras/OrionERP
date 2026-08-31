namespace OrionERP.UnitTests.Restaurante;

public sealed class RestaurantCombosMigrationSqlTests
{
  [Fact]
  public void Migration_RequiresExplicitDatabaseAndApplyModeAndSupportsDryRun()
  {
    var sql = ReadMigration();

    Assert.Contains("DECLARE @ExpectedDatabase sysname = N'$(ExpectedDatabase)'", sql, StringComparison.Ordinal);
    Assert.Contains("DECLARE @ApplyChangesText nvarchar(10) = N'$(ApplyChanges)'", sql, StringComparison.Ordinal);
    Assert.DoesNotContain("LTRIM(RTRIM(N'$(ApplyChanges)'))", sql, StringComparison.Ordinal);
    Assert.Contains("IF @ApplyChangesText NOT IN (N'0', N'1')", sql, StringComparison.Ordinal);
    Assert.Contains("SET @ApplyChanges = CONVERT(bit, @ApplyChangesText)", sql, StringComparison.Ordinal);
    Assert.DoesNotContain("TRY_CONVERT(bit, N'$(ApplyChanges)')", sql, StringComparison.Ordinal);
    Assert.DoesNotContain(":setvar ExpectedDatabase", sql, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain(":setvar ApplyChanges", sql, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("IF DB_NAME() <> @ExpectedDatabase", sql, StringComparison.Ordinal);
    Assert.Contains("BEGIN TRANSACTION", sql, StringComparison.Ordinal);
    Assert.Contains("IF @ApplyChanges = 1", sql, StringComparison.Ordinal);
    Assert.Contains("COMMIT TRANSACTION", sql, StringComparison.Ordinal);
    Assert.Contains("ROLLBACK TRANSACTION", sql, StringComparison.Ordinal);
    Assert.Contains("'DRY_RUN_VALIDATED'", sql, StringComparison.Ordinal);
  }

  [Fact]
  public void Migration_IsIdempotentAndCreatesProductComboAndSemanticEffectRules()
  {
    var sql = ReadMigration();

    Assert.Contains("COL_LENGTH('restaurante.Product', 'ProductKind') IS NULL", sql, StringComparison.Ordinal);
    Assert.Contains("UX_RestaurantProduct_Material_Filtered", sql, StringComparison.Ordinal);
    Assert.Contains("WHERE MaterialId IS NOT NULL", sql, StringComparison.Ordinal);
    Assert.Contains("CK_RestaurantProduct_KindMaterial", sql, StringComparison.Ordinal);
    Assert.Contains("OBJECT_ID('restaurante.ComboSlot', 'U') IS NULL", sql, StringComparison.Ordinal);
    Assert.Contains("OBJECT_ID('restaurante.ComboSlotOption', 'U') IS NULL", sql, StringComparison.Ordinal);
    Assert.Contains("OBJECT_ID('restaurante.ComboSlotOptionRoute', 'U') IS NULL", sql, StringComparison.Ordinal);
    Assert.Contains("FK_ComboSlotOption_Product_Rfc", sql, StringComparison.Ordinal);
    Assert.Contains("FK_ComboSlotOptionRoute_Section_Rfc", sql, StringComparison.Ordinal);
    Assert.Contains("CK_ModifierIngredientDelta_Effect", sql, StringComparison.Ordinal);
    Assert.Contains("'AddQuantity'", sql, StringComparison.Ordinal);
    Assert.Contains("'RemoveIngredient'", sql, StringComparison.Ordinal);
    Assert.Contains("'AdjustQuantity'", sql, StringComparison.Ordinal);
  }

  [Fact]
  public void Migration_CreatesHistoricalOrderHierarchyAndSnapshotIndexes()
  {
    var sql = ReadMigration();

    Assert.Contains("COL_LENGTH('restaurante.OrderLine', 'LineKind') IS NULL", sql, StringComparison.Ordinal);
    Assert.Contains("ParentOrderLineId", sql, StringComparison.Ordinal);
    Assert.Contains("ComboSlotId", sql, StringComparison.Ordinal);
    Assert.Contains("ComboSlotOptionId", sql, StringComparison.Ordinal);
    Assert.Contains("ParentProductNameSnapshot", sql, StringComparison.Ordinal);
    Assert.Contains("ComboSlotNameSnapshot", sql, StringComparison.Ordinal);
    Assert.Contains("BaseUnitPrice", sql, StringComparison.Ordinal);
    Assert.Contains("ChoicePriceDelta", sql, StringComparison.Ordinal);
    Assert.Contains("FK_OrderLine_Parent_Rfc", sql, StringComparison.Ordinal);
    Assert.Contains("IX_OrderLine_Parent", sql, StringComparison.Ordinal);
    Assert.Contains("ModifierGroupNameSnapshot", sql, StringComparison.Ordinal);
    Assert.Contains("COL_LENGTH('restaurante.OrderLineModifier', 'EffectKind') IS NULL", sql, StringComparison.Ordinal);
    Assert.Contains("OrderLineModifierIngredientEffect", sql, StringComparison.Ordinal);
    Assert.Contains("BaseQuantityDelta", sql, StringComparison.Ordinal);
    Assert.Contains("FrozenBaseUnitCost", sql, StringComparison.Ordinal);
    Assert.Contains("TR_OrderLineModifier_SnapshotIngredientEffects", sql, StringComparison.Ordinal);
    Assert.DoesNotContain("CAP-COMBO-CHILA", sql, StringComparison.OrdinalIgnoreCase);
  }

  private static string ReadMigration()
  {
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "OrionERP.sln")))
    {
      directory = directory.Parent;
    }

    if (directory is null)
    {
      throw new InvalidOperationException("Could not locate repository root.");
    }

    return File.ReadAllText(Path.Combine(
      directory.FullName,
      "src",
      "OrionERP.Infrastructure",
      "Features",
      "Restaurante",
      "Sql",
      "20260831_restaurant_combos_personalizations.sql"));
  }
}
