namespace OrionERP.UnitTests.Logistica;

public class MaterialesPageUxTests
{
  [Fact]
  public void Page_KeepsHighFrequencyMasterDataActionsInContext()
  {
    var page = ReadRepoFile("src/OrionERP.Web/Features/Logistica/Materials/MaterialesPage.razor");

    Assert.Contains("OnSearchInputAsync", page, StringComparison.Ordinal);
    Assert.Contains("VendorCombobox", page, StringComparison.Ordinal);
    Assert.Contains("Nueva categoría", page, StringComparison.Ordinal);
    Assert.Contains("Nueva UoM", page, StringComparison.Ordinal);
    Assert.Contains("Nuevo proveedor", page, StringComparison.Ordinal);
    Assert.Contains("Crear y seleccionar", page, StringComparison.Ordinal);
  }

  [Fact]
  public void Page_ProvidesCleanupAndOperationalSignals()
  {
    var page = ReadRepoFile("src/OrionERP.Web/Features/Logistica/Materials/MaterialesPage.razor");

    Assert.Contains("Por completar", page, StringComparison.Ordinal);
    Assert.Contains("Sin existencia", page, StringComparison.Ordinal);
    Assert.Contains("MaterialReadinessPercent", page, StringComparison.Ordinal);
    Assert.Contains("PurchaseConversionSummary", page, StringComparison.Ordinal);
    Assert.Contains("aria-live=\"polite\"", page, StringComparison.Ordinal);
  }

  [Fact]
  public void Catalog_KeepsLegacyLinkedMasterDataAvailableDuringEdit()
  {
    var service = ReadRepoFile("src/OrionERP.Infrastructure/Features/Logistica/Materials/MaterialService.cs");

    Assert.Contains("material.CategoryId = category.Id", service, StringComparison.Ordinal);
    Assert.Contains("material.BaseUnitId = unit.Id OR material.PurchaseUnitId = unit.Id", service, StringComparison.Ordinal);
    Assert.Contains("material.BusinessPartnerId = bp.Id", service, StringComparison.Ordinal);
  }

  [Fact]
  public void Page_RecoversWhenPersistedRfcCancelsTheInitialListRequest()
  {
    var codeBehind = ReadRepoFile("src/OrionERP.Web/Features/Logistica/Materials/MaterialesPage.razor.cs");

    Assert.Contains("rfcChangedDuringInitialization || Materials.Count == 0", codeBehind, StringComparison.Ordinal);
    Assert.Contains("ReloadForRfcChangeAsync", codeBehind, StringComparison.Ordinal);
    Assert.Contains("Task.Delay(120, reloadToken)", codeBehind, StringComparison.Ordinal);
    Assert.Contains("await BuscarAsync();", codeBehind, StringComparison.Ordinal);
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
