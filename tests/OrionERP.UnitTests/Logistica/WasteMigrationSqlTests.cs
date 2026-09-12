using OrionERP.UnitTests.Common;

namespace OrionERP.UnitTests.Logistica;

public sealed class WasteMigrationSqlTests
{
  private const string ScriptPath = "src/OrionERP.Infrastructure/Features/Logistica/Sql/20260912_inventory_waste_review.sql";

  [Fact]
  public void Script_AddsOnlyTheColumnsTheReviewFlowNeeds()
  {
    var sql = RepoFile.Read(ScriptPath);

    Assert.Contains("ADD OccurredOn date NULL", sql, StringComparison.Ordinal);
    Assert.Contains("ADD ReversalOfAdjustmentId bigint NULL", sql, StringComparison.Ordinal);
    Assert.Contains("ADD ReversedByAdjustmentId bigint NULL", sql, StringComparison.Ordinal);
    // Ninguna tabla nueva: la merma vive en el documento que ya existe.
    Assert.DoesNotContain("CREATE TABLE", sql, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public void Script_IsIdempotentAndTransactional()
  {
    var sql = RepoFile.Read(ScriptPath);

    Assert.Contains("SET XACT_ABORT ON;", sql, StringComparison.Ordinal);
    Assert.Contains("BEGIN TRANSACTION;", sql, StringComparison.Ordinal);
    Assert.Contains("COMMIT TRANSACTION;", sql, StringComparison.Ordinal);
    foreach (var column in new[] { "OccurredOn", "ReversalOfAdjustmentId", "ReversedByAdjustmentId" })
    {
      Assert.Contains($"IF COL_LENGTH('logistica.InventoryAdjustment', '{column}') IS NULL", sql, StringComparison.Ordinal);
    }
  }

  [Fact]
  public void Script_BlocksASecondReversalInTheDatabaseNotOnlyInTheService()
  {
    var sql = RepoFile.Read(ScriptPath);

    Assert.Contains("CREATE UNIQUE INDEX UX_InventoryAdjustment_ReversalOf", sql, StringComparison.Ordinal);
    Assert.Contains("WHERE ReversalOfAdjustmentId IS NOT NULL", sql, StringComparison.Ordinal);
  }

  [Fact]
  public void Script_KeepsTheRfcInsideEveryReversalLink()
  {
    var sql = RepoFile.Read(ScriptPath);

    Assert.Contains("FOREIGN KEY (Rfc, ReversalOfAdjustmentId)", sql, StringComparison.Ordinal);
    Assert.Contains("FOREIGN KEY (Rfc, ReversedByAdjustmentId)", sql, StringComparison.Ordinal);
    Assert.Contains("REFERENCES logistica.InventoryAdjustment (Rfc, Id)", sql, StringComparison.Ordinal);
  }

  [Fact]
  public void Script_VerifiesItsOwnWorkAndDocumentsItsRollback()
  {
    var sql = RepoFile.Read(ScriptPath);

    Assert.Contains("THROW 51523", sql, StringComparison.Ordinal);
    Assert.Contains("THROW 51527", sql, StringComparison.Ordinal);
    Assert.Contains("DROP COLUMN OccurredOn;", sql, StringComparison.Ordinal);
  }

  [Fact]
  public void Script_StaysOutOfThePlatformManifest()
  {
    var manifest = RepoFile.Read("database/orion-migrations.json");

    // orion-migrations.json sólo lleva los scripts de plataforma y de aislamiento por inquilino
    // —los que exigen $(ExpectedDatabase) y el registro en orion.SchemaMigration—. Una migración
    // de función de Logística se aplica igual que 20260905_material_purchase_increment.sql.
    Assert.DoesNotContain("20260912_inventory_waste_review", manifest, StringComparison.Ordinal);
  }

  [Fact]
  public void Script_FollowsTheFeatureMigrationPreamble()
  {
    var sql = RepoFile.Read(ScriptPath);
    var reference = RepoFile.Read("src/OrionERP.Infrastructure/Features/Logistica/Sql/20260905_material_purchase_increment.sql");

    foreach (var directive in new[] { "SET ANSI_NULLS ON;", "SET QUOTED_IDENTIFIER ON;", "SET XACT_ABORT ON;", "SET NOCOUNT ON;" })
    {
      Assert.Contains(directive, reference, StringComparison.Ordinal);
      Assert.Contains(directive, sql, StringComparison.Ordinal);
    }
  }
}
