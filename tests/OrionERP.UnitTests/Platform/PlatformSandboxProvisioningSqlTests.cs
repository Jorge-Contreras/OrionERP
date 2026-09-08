using System.Text.Json;
using System.Text.RegularExpressions;
using OrionERP.UnitTests.Common;

namespace OrionERP.UnitTests.Platform;

public sealed class PlatformSandboxProvisioningSqlTests
{
  private const string ProvisioningPath =
    "src/OrionERP.Infrastructure/Features/Platform/Sql/20260902_platform_sandbox_bonhomia_bruno.sql";

  private static readonly string Sql = RepoFile.Read(ProvisioningPath);

  [Fact]
  public void Provisioning_IsDataOnlyAndSandboxOnly()
  {
    Assert.Contains("@ExpectedDatabase <> N'Orion_Sandbox'", Sql, StringComparison.Ordinal);
    Assert.Contains("DB_NAME() <> N'Orion_Sandbox'", Sql, StringComparison.Ordinal);
    Assert.DoesNotContain("grupocarpio", Sql, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotMatch(
      new Regex(@"(?im)^\s*(?:CREATE|ALTER|DROP)\b", RegexOptions.CultureInvariant),
      Sql);
    Assert.DoesNotMatch(
      new Regex(@"(?is)INSERT\s+(?:INTO\s+)?orion\.Company\s*\(", RegexOptions.CultureInvariant),
      Sql);
  }

  [Fact]
  public void Provisioning_UsesPreviewChecksumLedgerAndAnExclusiveLock()
  {
    Assert.Contains("$(ExpectedDatabase)", Sql, StringComparison.Ordinal);
    Assert.Contains("$(ApplyChanges)", Sql, StringComparison.Ordinal);
    Assert.Contains("$(MigrationId)", Sql, StringComparison.Ordinal);
    Assert.Contains("$(MigrationChecksum)", Sql, StringComparison.Ordinal);
    Assert.Contains("SET XACT_ABORT ON", Sql, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("BEGIN TRANSACTION", Sql, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("ROLLBACK TRANSACTION", Sql, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("sys.sp_getapplock", Sql, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("INSERT orion.SchemaMigration", Sql, StringComparison.OrdinalIgnoreCase);
    Assert.Matches(
      new Regex(@"(?is)IF\s+@ApplyChanges\s*=\s*1\s+BEGIN.*?COMMIT\s+TRANSACTION.*?END\s+ELSE\s+BEGIN.*?ROLLBACK\s+TRANSACTION", RegexOptions.CultureInvariant),
      Sql);
  }

  [Fact]
  public void Provisioning_MatchesTheCurrentPublicConfigurationEvidence()
  {
    using var bonhomiaSettings = JsonDocument.Parse(
      RepoFile.Read("src/OrionERP.Bonhomia.Web/appsettings.Development.json"));
    var bonhomiaPublicSite = bonhomiaSettings.RootElement.GetProperty("PublicWebsite");
    using var bonhomiaBaseSettings = JsonDocument.Parse(
      RepoFile.Read("src/OrionERP.Bonhomia.Web/appsettings.json"));
    var bonhomiaTimeZone = bonhomiaBaseSettings.RootElement
      .GetProperty("BonhomiaCheckout").GetProperty("TimeZone").GetString();

    using var brunoSettings = JsonDocument.Parse(
      RepoFile.Read("src/OrionERP.Bruno.Web/appsettings.Development.json"));
    var brunoPublicSite = brunoSettings.RootElement.GetProperty("PublicWebsite");
    var brunoConstants = RepoFile.Read("src/OrionERP.Application/Features/Restaurante/BrunoRestaurantConstants.cs");
    var brunoRfc = Constant(brunoConstants, "Rfc");
    var brunoSiteCode = Constant(brunoConstants, "SiteCode");

    AssertPublicSiteConfigurationIsInSql(bonhomiaPublicSite);
    Assert.Contains($"N'{bonhomiaTimeZone}'", Sql, StringComparison.Ordinal);
    AssertPublicSiteConfigurationIsInSql(brunoPublicSite);
    Assert.Equal(brunoRfc, brunoPublicSite.GetProperty("ExpectedCompanyRfc").GetString());
    Assert.Contains($"'{brunoSiteCode}'", Sql, StringComparison.Ordinal);
    Assert.Equal(
      brunoSiteCode.ToLowerInvariant(),
      brunoPublicSite.GetProperty("SiteKey").GetString());
  }

  [Fact]
  public void Provisioning_UsesOnlyTheApprovedBindingsAndLeavesTaxRfcUnassigned()
  {
    foreach (var expected in new[]
    {
      "'bonhomia-main'",
      "'bonhomia-suites'",
      "'HOSPITALITY'",
      "'bonhomiasuites.com'",
      "'brunos-main'",
      "'brunos-01'",
      "'RESTAURANT'",
      "'brunosgarden.com'"
    })
      Assert.Contains(expected, Sql, StringComparison.Ordinal);

    Assert.DoesNotContain("patos", Sql, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotMatch(
      new Regex(@"(?is)\bSET\s+TaxRfc\s*=", RegexOptions.CultureInvariant),
      Sql);
    Assert.Matches(
      new Regex(@"(?is)UPDATE\s+orion\.Company\s+SET\s+LegacyTenantKey\s*=\s*Rfc", RegexOptions.CultureInvariant),
      Sql);
  }

  [Fact]
  public void Provisioning_EnablesOnlyTheTwoExplicitOptionalEntitlements()
  {
    Assert.Equal(
      2,
      Regex.Matches(
        Sql,
        @"(?is)INSERT\s+(?:INTO\s+)?orion\.CompanyModule\b",
        RegexOptions.CultureInvariant).Count);
    Assert.Contains("'HOSPITALITY', 'Enabled', 1, NULL, NULL", Sql, StringComparison.Ordinal);
    Assert.Contains("'RESTAURANT', 'Enabled', 1, NULL, NULL", Sql, StringComparison.Ordinal);
    Assert.DoesNotMatch(
      new Regex(@"(?is)CompanyModule.*?'ACCOUNTING_CORE'", RegexOptions.CultureInvariant),
      Sql);
    Assert.Equal(
      2,
      Regex.Matches(
        Sql,
        @"(?is)INSERT\s+(?:INTO\s+)?orion\.PublicSite\b",
        RegexOptions.CultureInvariant).Count);
  }

  private static string Constant(string source, string name)
  {
    var match = Regex.Match(
      source,
      $"public\\s+const\\s+string\\s+{Regex.Escape(name)}\\s*=\\s*\"(?<value>[^\"]+)\"",
      RegexOptions.CultureInvariant);
    Assert.True(match.Success, $"No se encontró {name} en BrunoRestaurantConstants.");
    return match.Groups["value"].Value;
  }

  private static void AssertPublicSiteConfigurationIsInSql(JsonElement publicSite)
  {
    foreach (var propertyName in new[]
    {
      "PublicSiteKey",
      "ExpectedCompanyRfc",
      "SiteKey",
      "ModuleCode",
      "CanonicalHost"
    })
    {
      var value = publicSite.GetProperty(propertyName).GetString();
      Assert.False(string.IsNullOrWhiteSpace(value));
      Assert.Contains($"'{value}'", Sql, StringComparison.Ordinal);
    }
  }
}
