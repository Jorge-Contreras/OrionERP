using System.Text.RegularExpressions;
using OrionERP.UnitTests.Common;

namespace OrionERP.UnitTests.Platform;

public sealed class PlatformFoundationMigrationSqlTests
{
  private const string MigrationPath =
    "src/OrionERP.Infrastructure/Features/Platform/Sql/20260901_platform_foundation.sql";

  private static readonly string Sql = RepoFile.Read(MigrationPath);

  [Fact]
  public void Migration_DefaultsToPreviewAndRejectsTheWrongDatabase()
  {
    Assert.Contains("DECLARE @ExpectedDatabase sysname = N'$(ExpectedDatabase)'", Sql, StringComparison.Ordinal);
    Assert.Contains("N'Orion_Sandbox'", Sql, StringComparison.Ordinal);
    Assert.Contains("N'grupocarpio'", Sql, StringComparison.Ordinal);
    Assert.DoesNotContain("Orion_SandBox", Sql, StringComparison.Ordinal);
    Assert.Contains("IF DB_NAME() <> @ExpectedDatabase", Sql, StringComparison.Ordinal);
    Assert.Contains("SET XACT_ABORT ON", Sql, StringComparison.Ordinal);
    Assert.Matches(
      new Regex(@"(?is)DECLARE\s+@ApplyChangesInput\s+nvarchar\(20\)\s*=\s*N'\$\(ApplyChanges\)'", RegexOptions.CultureInvariant),
      Sql);
    Assert.Matches(
      new Regex(@"(?is)DECLARE\s+@ApplyChanges\s+bit\s*=\s*0", RegexOptions.CultureInvariant),
      Sql);
    Assert.Matches(
      new Regex(@"(?is)IF\s+@ApplyChangesInput\s+NOT\s+LIKE\s+N'\$'\s*\+\s*N'\(%'.*?@ApplyChangesInput\s+NOT\s+IN\s*\(N'0',\s*N'1'\).*?SET\s+@ApplyChanges\s*=\s*CONVERT\(bit,\s*@ApplyChangesInput\)", RegexOptions.CultureInvariant),
      Sql);

    Assert.Matches(
      new Regex(@"(?is)IF\s+@ApplyChanges\s*=\s*1\s+BEGIN.*?COMMIT\s+TRANSACTION.*?END\s+ELSE\s+BEGIN.*?ROLLBACK\s+TRANSACTION", RegexOptions.CultureInvariant),
      Sql);
  }

  [Theory]
  [InlineData("orion.CompanyModule")]
  [InlineData("orion.PublicSite")]
  public void Migration_CreatesTheRequiredPlatformTables(string tableName)
  {
    Assert.Matches(
      new Regex($@"(?is)CREATE\s+TABLE\s+{Regex.Escape(tableName)}\b", RegexOptions.CultureInvariant),
      Sql);
  }

  [Fact]
  public void Migration_SeedsOnlyThePlatformModuleCatalog()
  {
    var seed = Regex.Match(
      Sql,
      @"(?is)INSERT\s+(?:INTO\s+)?orion\.Module\b.*?FROM\s*\(\s*VALUES(?<rows>.*?)\)\s*seed\b",
      RegexOptions.CultureInvariant);
    Assert.True(seed.Success, "La migración debe sembrar el catálogo cerrado de módulos.");

    var moduleCodes = Regex.Matches(
        seed.Groups["rows"].Value,
        @"\(\s*N?'(?<code>[A-Z_]+)'",
        RegexOptions.CultureInvariant)
      .Select(match => match.Groups["code"].Value)
      .ToArray();
    Assert.Equal(["ACCOUNTING_CORE", "HOSPITALITY", "RESTAURANT"], moduleCodes);

    Assert.DoesNotMatch(
      new Regex(@"(?is)INSERT\s+(?:INTO\s+)?orion\.CompanyModule\b", RegexOptions.CultureInvariant),
      Sql);
  }

  [Fact]
  public void Migration_ProtectsAccountingFromDisableOrDelete()
  {
    var trigger = Regex.Match(
      Sql,
      @"(?is)CREATE\s+(?:OR\s+ALTER\s+)?TRIGGER\b.*?ON\s+orion\.CompanyModule\b.*?THROW\s+51237\b",
      RegexOptions.CultureInvariant);

    Assert.True(trigger.Success, "CompanyModule debe tener una guarda estructural para ACCOUNTING_CORE.");
    Assert.Contains("UPDATE", trigger.Value, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("DELETE", trigger.Value, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("ACCOUNTING_CORE", trigger.Value, StringComparison.Ordinal);
    Assert.Contains("THROW", trigger.Value, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("CK_orion_CompanyModule_AccountingCore", Sql, StringComparison.Ordinal);
  }

  [Fact]
  public void Migration_ModelsCompanyModuleLifecycleWithoutASecondMutableFlag()
  {
    Assert.Contains("[Status] varchar(20) NOT NULL", Sql, StringComparison.Ordinal);
    Assert.Contains("DF_orion_CompanyModule_Status DEFAULT ('Provisioning')", Sql, StringComparison.Ordinal);
    Assert.Contains(
      "IsEnabled AS CONVERT(bit, CASE WHEN [Status] = 'Enabled' THEN 1 ELSE 0 END) PERSISTED",
      Sql,
      StringComparison.Ordinal);
    Assert.Contains("ConfigurationVersion bigint NOT NULL", Sql, StringComparison.Ordinal);
    Assert.Contains("DF_orion_CompanyModule_ConfigurationVersion DEFAULT (1)", Sql, StringComparison.Ordinal);
    Assert.Contains("CK_orion_CompanyModule_Status", Sql, StringComparison.Ordinal);
    Assert.Contains("[Status] IN ('Provisioning', 'Enabled', 'Suspended')", Sql, StringComparison.Ordinal);
    Assert.Contains("CK_orion_CompanyModule_ConfigurationVersion", Sql, StringComparison.Ordinal);
    Assert.Contains("ConfigurationVersion > 0", Sql, StringComparison.Ordinal);
    Assert.Contains("ModuleCode <> 'ACCOUNTING_CORE' OR [Status] = 'Enabled'", Sql, StringComparison.Ordinal);
  }

  [Fact]
  public void Migration_DoesNotGuessLegacyCompanyOrSiteOwnership()
  {
    Assert.DoesNotContain("OHM191112Q26", Sql, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("BRUNOS260707L26", Sql, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("SIN_RFC", Sql, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public void Migration_UsesAnIdAndChecksumRegistryOnlyInsideTheApplyTransaction()
  {
    Assert.Contains("orion.SchemaMigration", Sql, StringComparison.Ordinal);
    Assert.Contains("$(MigrationId)", Sql, StringComparison.Ordinal);
    Assert.Contains("$(MigrationChecksum)", Sql, StringComparison.Ordinal);

    Assert.Matches(
      new Regex(@"(?is)MigrationId.*?MigrationChecksum.*?THROW", RegexOptions.CultureInvariant),
      Sql);
    Assert.Matches(
      new Regex(@"(?is)SchemaMigration.*?MigrationId.*?MigrationChecksum.*?(?:<>|!=).*?THROW", RegexOptions.CultureInvariant),
      Sql);

    var transaction = Sql.IndexOf("BEGIN TRANSACTION", StringComparison.Ordinal);
    var registryWrite = Regex.Match(
      Sql,
      @"(?is)INSERT\s+INTO\s+orion\.SchemaMigration\b",
      RegexOptions.CultureInvariant).Index;
    var applyDecision = Sql.IndexOf("IF @ApplyChanges = 1", StringComparison.Ordinal);
    var commit = Sql.IndexOf("COMMIT TRANSACTION", applyDecision, StringComparison.Ordinal);
    var rollback = Sql.IndexOf("ROLLBACK TRANSACTION", commit, StringComparison.Ordinal);

    Assert.True(transaction >= 0, "La migración debe abrir una transacción.");
    Assert.True(applyDecision > transaction, "La decisión apply/preview debe ocurrir dentro de la transacción.");
    Assert.True(registryWrite > applyDecision, "Preview no debe intentar escribir SchemaMigration.");
    Assert.True(commit > registryWrite, "Apply debe registrar la migración antes del commit.");
    Assert.True(rollback > commit, "La rama preview debe terminar en rollback.");
  }
}
