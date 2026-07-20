namespace OrionERP.UnitTests.Restaurante;

public sealed class RestaurantProductionServiceTests
{
  [Fact]
  public void WorkspaceQuery_UsesCanonicalLogisticsColumnNames()
  {
    var source = File.ReadAllText(GetRepoFile(
      "src/OrionERP.Infrastructure/Features/Restaurante/RestaurantProductionService.cs"));

    Assert.Contains("material.[Description] AS ProductName", source, StringComparison.Ordinal);
    Assert.Contains("unitInfo.UnitName", source, StringComparison.Ordinal);
    Assert.Contains("CONCAT(material.[Description]", source, StringComparison.Ordinal);
    Assert.DoesNotContain("material.[Name]", source, StringComparison.Ordinal);
    Assert.DoesNotContain("unitInfo.[Name]", source, StringComparison.Ordinal);
    Assert.DoesNotContain("material.IsRemoved", source, StringComparison.Ordinal);
  }

  private static string GetRepoFile(string relativePath)
    => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../", relativePath));
}
