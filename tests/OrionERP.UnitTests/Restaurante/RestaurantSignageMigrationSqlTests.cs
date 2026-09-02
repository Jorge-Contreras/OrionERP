namespace OrionERP.UnitTests.Restaurante;

public sealed class RestaurantSignageMigrationSqlTests
{
  [Fact]
  public void Migration_RequiresExplicitDatabaseAndApplyModeAndSupportsDryRun()
  {
    var sql = ReadMigration();

    Assert.Contains("DECLARE @ExpectedDatabase sysname = N'$(ExpectedDatabase)'", sql, StringComparison.Ordinal);
    Assert.Contains("DECLARE @ApplyChangesText nvarchar(10) = N'$(ApplyChanges)'", sql, StringComparison.Ordinal);
    Assert.Contains("IF @ApplyChangesText NOT IN (N'0', N'1')", sql, StringComparison.Ordinal);
    Assert.Contains("IF DB_NAME() <> @ExpectedDatabase", sql, StringComparison.Ordinal);
    Assert.Contains("BEGIN TRANSACTION", sql, StringComparison.Ordinal);
    Assert.Contains("IF @ApplyChanges = 1", sql, StringComparison.Ordinal);
    Assert.Contains("COMMIT TRANSACTION", sql, StringComparison.Ordinal);
    Assert.Contains("ROLLBACK TRANSACTION", sql, StringComparison.Ordinal);
    Assert.Contains("'DRY_RUN_VALIDATED'", sql, StringComparison.Ordinal);
  }

  [Fact]
  public void Migration_IsIdempotentAndFollowsTheRfcScopingConventions()
  {
    var sql = ReadMigration();

    Assert.Contains("OBJECT_ID('restaurante.SignageScreen', 'U') IS NULL", sql, StringComparison.Ordinal);
    Assert.Contains("OBJECT_ID('restaurante.SignageScreenImage', 'U') IS NULL", sql, StringComparison.Ordinal);

    // Índice compuesto (Rfc, Id) que sostiene las llaves foráneas por RFC.
    Assert.Contains("UX_SignageScreen_RfcId ON restaurante.SignageScreen (Rfc, Id)", sql, StringComparison.Ordinal);
    Assert.Contains("UX_SignageScreenImage_RfcId ON restaurante.SignageScreenImage (Rfc, Id)", sql, StringComparison.Ordinal);
    Assert.Contains("FK_SignageScreen_Site_Rfc FOREIGN KEY (Rfc, SiteId)", sql, StringComparison.Ordinal);
    Assert.Contains("FK_SignageScreenImage_Screen_Rfc FOREIGN KEY (Rfc, ScreenId)", sql, StringComparison.Ordinal);
    Assert.Contains("UX_SignageScreen_Key UNIQUE (Rfc, ScreenKey)", sql, StringComparison.Ordinal);
  }

  [Fact]
  public void Migration_ConstrainsScreenKeysAndImagePayloads()
  {
    var sql = ReadMigration();

    // La colación binaria es lo que realmente impide claves en mayúsculas.
    Assert.Contains("ScreenKey COLLATE Latin1_General_BIN2 NOT LIKE '%[^a-z0-9-]%'", sql, StringComparison.Ordinal);
    Assert.Contains("ScreenKey NOT IN ('media', 'manifest.json')", sql, StringComparison.Ordinal);
    Assert.Contains("CK_SignageScreenImage_Type", sql, StringComparison.Ordinal);
    Assert.Contains("ContentType IN ('image/png', 'image/jpeg', 'image/webp')", sql, StringComparison.Ordinal);
    Assert.Contains("ByteLength > 0 AND ByteLength <= 26214400", sql, StringComparison.Ordinal);
    Assert.Contains("ContentHash binary(32) NOT NULL", sql, StringComparison.Ordinal);

    // SortOrder se indexa pero nunca de forma única: reordenar reescribe el
    // conjunto entero y una restricción única provocaría colisiones transitorias.
    Assert.Contains("IX_SignageScreenImage_Screen", sql, StringComparison.Ordinal);
    Assert.DoesNotContain("UNIQUE (Rfc, ScreenId, SortOrder)", sql, StringComparison.Ordinal);
  }

  [Fact]
  public void Migration_EnrollsTheNewTablesInRowLevelSecurity()
  {
    var sql = ReadMigration();

    Assert.Contains("ALTER SECURITY POLICY logistica.RfcSecurityPolicy", sql, StringComparison.Ordinal);
    Assert.Contains("logistica.fn_RfcAccessPredicate(Rfc)", sql, StringComparison.Ordinal);
    Assert.Contains("ADD FILTER PREDICATE", sql, StringComparison.Ordinal);
    Assert.Contains("ADD BLOCK PREDICATE", sql, StringComparison.Ordinal);

    // Una migración que crea tablas con Rfc y no verifica la inscripción deja un
    // hueco silencioso entre inquilinos.
    Assert.Contains("THROW 51913", sql, StringComparison.Ordinal);
    Assert.Contains("target_object_id = OBJECT_ID('restaurante.SignageScreen')", sql, StringComparison.Ordinal);
  }

  [Fact]
  public void Migration_SeedsTheTwoBrunoScreensWithoutImageBytes()
  {
    var sql = ReadMigration();

    Assert.Contains("BRUNOS260707L26", sql, StringComparison.Ordinal);
    Assert.Contains("'comida'", sql, StringComparison.Ordinal);
    Assert.Contains("'bebidas'", sql, StringComparison.Ordinal);
    Assert.Contains("SiteCode = 'BRUNOS-01'", sql, StringComparison.Ordinal);

    // El servidor SQL es remoto y no puede leer archivos de la estación: los
    // tableros se cargan desde la pestaña Pantallas, no desde la migración.
    Assert.DoesNotContain("OPENROWSET", sql, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("BULK", sql, StringComparison.OrdinalIgnoreCase);
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
      "src/OrionERP.Infrastructure/Features/Restaurante/Sql/20260901_restaurant_digital_signage.sql"));
  }
}
