using System.Text.RegularExpressions;
using OrionERP.UnitTests.Common;

namespace OrionERP.UnitTests.Platform;

public sealed class PublicSitePresentationTransitionMigrationSqlTests
{
  private const string MigrationPath =
    "src/OrionERP.Infrastructure/Features/Platform/Sql/20260904_public_site_presentation_transition_sandbox.sql";

  private static readonly string Sql = RepoFile.Read(MigrationPath);

  [Fact]
  public void Migration_IsSandboxOnlyAndPreviewSafe()
  {
    Assert.Contains("@ExpectedDatabase <> N'Orion_Sandbox'", Sql, StringComparison.Ordinal);
    Assert.Contains("IF DB_NAME() <> N'Orion_Sandbox'", Sql, StringComparison.Ordinal);
    Assert.DoesNotContain("grupocarpio", Sql, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("SET XACT_ABORT ON", Sql, StringComparison.OrdinalIgnoreCase);
    Assert.Matches(
      new Regex(
        @"(?is)IF\s+@ApplyChanges\s*=\s*1.*?INSERT\s+(?:INTO\s+)?orion\.SchemaMigration.*?COMMIT\s+TRANSACTION.*?ELSE.*?ROLLBACK\s+TRANSACTION",
        RegexOptions.CultureInvariant),
      Sql);
  }

  [Fact]
  public void Migration_AddsOneCompleteBoundedFallbackTuple()
  {
    Assert.Contains("FallbackBrandingVersion bigint NULL", Sql, StringComparison.Ordinal);
    Assert.Contains("FallbackContentVersion bigint NULL", Sql, StringComparison.Ordinal);
    Assert.Contains("FallbackUntilUtc datetime2(7) NULL", Sql, StringComparison.Ordinal);
    Assert.Contains("CK_orion_PublicSite_PresentationFallback", Sql, StringComparison.Ordinal);
    Assert.Matches(
      new Regex(
        @"(?is)FallbackBrandingVersion\s+IS\s+NULL.*?FallbackContentVersion\s+IS\s+NULL.*?FallbackUntilUtc\s+IS\s+NULL.*?OR.*?FallbackBrandingVersion\s*>\s*0.*?FallbackContentVersion\s*>\s*0.*?FallbackUntilUtc\s*>\s*UpdatedAtUtc.*?FallbackUntilUtc\s*<=\s*DATEADD\s*\(\s*hour\s*,\s*2\s*,\s*UpdatedAtUtc\s*\).*?FallbackBrandingVersion\s*<>\s*BrandingVersion.*?OR\s+FallbackContentVersion\s*<>\s*ContentVersion",
        RegexOptions.CultureInvariant),
      Sql);
  }

  [Fact]
  public void Migration_RebuildsTheAuditTriggerWithBothPresentationSlots()
  {
    Assert.Matches(
      new Regex(
        @"(?is)CREATE\s+OR\s+ALTER\s+TRIGGER\s+orion\.TR_PublicSite_GuardAudit.*?previousRow\.FallbackBrandingVersion.*?previousRow\.FallbackContentVersion.*?previousRow\.FallbackUntilUtc.*?currentRow\.FallbackBrandingVersion.*?currentRow\.FallbackContentVersion.*?currentRow\.FallbackUntilUtc",
        RegexOptions.CultureInvariant),
      Sql);
    Assert.Contains("INSERT orion.PlatformAudit", Sql, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public void Migration_FailsOnAPartialExistingSchemaOrTuple()
  {
    Assert.Contains("@FallbackColumnsPresent NOT IN (0, 3)", Sql, StringComparison.Ordinal);
    Assert.Contains("THROW 51608", Sql, StringComparison.Ordinal);
    Assert.Contains("THROW 51610", Sql, StringComparison.Ordinal);
  }
}
