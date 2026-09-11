using System.Text.RegularExpressions;
using OrionERP.UnitTests.Common;

namespace OrionERP.UnitTests.Platform;

public sealed class PublicWebsiteSqlIdentityGrantTests
{
  private static readonly string BonhomiaSql = RepoFile.Read(
    "src/OrionERP.Infrastructure/Features/Platform/Sql/Identities/grant-bonhomia-web.sql");
  private static readonly string BrunoSql = RepoFile.Read(
    "src/OrionERP.Infrastructure/Features/Platform/Sql/Identities/grant-bruno-web.sql");

  [Fact]
  public void Bonhomia_ReadinessGetsOnlyItsRequiredMetadataVisibility()
  {
    Assert.Contains(
      "GRANT VIEW DEFINITION ON OBJECT::[orion].[HospitalityScopePolicy]",
      BonhomiaSql,
      StringComparison.Ordinal);
    Assert.Contains(
      "GRANT VIEW DEFINITION ON OBJECT::[dbo].[ROOM_CALENDAR]",
      BonhomiaSql,
      StringComparison.Ordinal);
    Assert.DoesNotContain("GRANT VIEW DEFINITION ON DATABASE", BonhomiaSql, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("GRANT VIEW ANY DEFINITION", BonhomiaSql, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public void Restaurant_ReadinessCanReadOnlyTheIdentityBridgeStateItValidates()
  {
    Assert.Contains(
      "(N'orion.PublicIdentityCompatibilityState','SELECT')",
      BrunoSql,
      StringComparison.Ordinal);
  }

  [Theory]
  [MemberData(nameof(GrantScripts))]
  public void PublicGrantScripts_DoNotCreateLoginsOrAssignBroadDatabaseRoles(string sql)
  {
    Assert.DoesNotMatch(new Regex(@"(?im)^\s*CREATE\s+LOGIN\b", RegexOptions.CultureInvariant), sql);
    Assert.DoesNotMatch(new Regex(@"(?im)^\s*ALTER\s+ROLE\b", RegexOptions.CultureInvariant), sql);
    Assert.DoesNotMatch(new Regex(@"(?im)^\s*EXEC\s+sp_addrolemember\b", RegexOptions.CultureInvariant), sql);
  }

  public static TheoryData<string> GrantScripts => new()
  {
    BonhomiaSql,
    BrunoSql
  };
}
