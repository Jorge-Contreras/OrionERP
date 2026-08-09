namespace OrionERP.UnitTests.Restaurante;

public sealed class BrunoSirloinBurgerBomSqlTests
{
  [Fact]
  public void Migration_UsesExplicitDatabaseTransactionAndLockGuards()
  {
    var sql = ReadMigration();

    Assert.Contains("DECLARE @ExpectedDatabase sysname = N'$(ExpectedDatabase)'", sql, StringComparison.Ordinal);
    Assert.Contains("DECLARE @ApplyChanges bit = TRY_CONVERT(bit, N'$(ApplyChanges)')", sql, StringComparison.Ordinal);
    Assert.Contains("IF DB_NAME() <> @ExpectedDatabase", sql, StringComparison.Ordinal);
    Assert.Contains("IF SESSION_CONTEXT(N'OrionRfc') IS NOT NULL", sql, StringComparison.Ordinal);
    Assert.Contains("SET TRANSACTION ISOLATION LEVEL SERIALIZABLE", sql, StringComparison.Ordinal);
    Assert.Contains("sys.sp_getapplock", sql, StringComparison.Ordinal);
    Assert.Contains("ROLLBACK TRANSACTION", sql, StringComparison.Ordinal);
    Assert.Contains("COMMIT TRANSACTION", sql, StringComparison.Ordinal);
  }

  [Fact]
  public void Migration_ClonesBomAndRecipeBeforeReplacingOnlyReviewedComponent()
  {
    var sql = ReadMigration();

    Assert.Contains("'BRUN-SIR-01'", sql, StringComparison.Ordinal);
    Assert.Contains("'MAT-006938'", sql, StringComparison.Ordinal);
    Assert.Contains("'MAT-006977'", sql, StringComparison.Ordinal);
    Assert.Contains("@ProductMaterialId <> 7066", sql, StringComparison.Ordinal);
    Assert.Contains("INSERT INTO logistica.BomVersion", sql, StringComparison.Ordinal);
    Assert.Contains("INSERT INTO logistica.BomComponent", sql, StringComparison.Ordinal);
    Assert.Contains("INSERT INTO logistica.Recipe ", sql, StringComparison.Ordinal);
    Assert.Contains("INSERT INTO logistica.RecipeStep", sql, StringComparison.Ordinal);
    Assert.Contains("CASE WHEN ComponentMaterialId = @LegacyMaterialId THEN @SirloinPattyMaterialId", sql, StringComparison.Ordinal);
    Assert.Contains("'ALREADY_APPLIED' AS MigrationStatus", sql, StringComparison.Ordinal);
    Assert.Contains("'DRY_RUN_VALIDATED'", sql, StringComparison.Ordinal);
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
      "20260809_fix_bruno_sirloin_burger_bom.sql"));
  }
}
