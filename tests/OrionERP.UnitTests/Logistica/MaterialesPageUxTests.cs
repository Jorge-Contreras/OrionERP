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
  public void Page_SeparatesCanonicalBasePriceFromDerivedPurchasePresentationPrice()
  {
    var page = ReadRepoFile("src/OrionERP.Web/Features/Logistica/Materials/MaterialesPage.razor");
    var codeBehind = ReadRepoFile("src/OrionERP.Web/Features/Logistica/Materials/MaterialesPage.razor.cs");
    var materialService = ReadRepoFile("src/OrionERP.Infrastructure/Features/Logistica/Materials/MaterialService.cs");
    var bomService = ReadRepoFile("src/OrionERP.Infrastructure/Features/Restaurante/BomRecipeService.cs");

    Assert.Contains("material-base-unit-price", page, StringComparison.Ordinal);
    Assert.Contains("material-presentation-price", page, StringComparison.Ordinal);
    Assert.Contains("Precio por unidad base", codeBehind, StringComparison.Ordinal);
    Assert.Contains("Precio por presentación", codeBehind, StringComparison.Ordinal);
    Assert.Contains("OnBaseUnitPriceChanged", codeBehind, StringComparison.Ordinal);
    Assert.Contains("OnPurchasePresentationPriceChanged", codeBehind, StringComparison.Ordinal);
    Assert.Contains("@oninput=\"OnBaseUnitPriceChanged\"", page, StringComparison.Ordinal);
    Assert.Contains("@oninput=\"OnPurchasePresentationPriceChanged\"", page, StringComparison.Ordinal);
    Assert.Contains("BaseUnitPrice = @BaseUnitPrice", materialService, StringComparison.Ordinal);
    Assert.Contains("material.BaseUnitPrice", bomService, StringComparison.Ordinal);
    Assert.DoesNotContain("Editor.Price", page, StringComparison.Ordinal);
    Assert.DoesNotContain("Editor.Price", codeBehind, StringComparison.Ordinal);
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

  [Fact]
  public void Page_ProvidesHistoricalAwareLifecycleReport()
  {
    var page = ReadRepoFile("src/OrionERP.Web/Features/Logistica/Materials/MaterialesPage.razor");
    var styles = ReadRepoFile("src/OrionERP.Web/Features/Logistica/Materials/MaterialesPage.razor.css");

    Assert.Contains("Revisar retiro", page, StringComparison.Ordinal);
    Assert.Contains("Revisión de retiro seguro", page, StringComparison.Ordinal);
    Assert.Contains("Vínculos operativos por resolver", ReadRepoFile("src/OrionERP.Web/Features/Logistica/Materials/MaterialesPage.razor.cs"), StringComparison.Ordinal);
    Assert.Contains("Historial que debe conservarse", ReadRepoFile("src/OrionERP.Web/Features/Logistica/Materials/MaterialesPage.razor.cs"), StringComparison.Ordinal);
    Assert.Contains("Configuración desvinculable", ReadRepoFile("src/OrionERP.Web/Features/Logistica/Materials/MaterialesPage.razor.cs"), StringComparison.Ordinal);
    Assert.Contains("dependency.ReferenceCount", page, StringComparison.Ordinal);
    Assert.Contains("dependency.Examples", page, StringComparison.Ordinal);
    Assert.Contains("Desactivación recomendada", page, StringComparison.Ordinal);
    Assert.Contains("value=\"@DeletionConfirmationText\"", page, StringComparison.Ordinal);
    Assert.Contains("Eliminar permanentemente", page, StringComparison.Ordinal);
    Assert.Contains("Desactivar material", page, StringComparison.Ordinal);
    Assert.Contains("Reactivar material", page, StringComparison.Ordinal);
    Assert.Contains("materiales-delete-blockers", styles, StringComparison.Ordinal);
    Assert.Contains("materiales-dependency-section", styles, StringComparison.Ordinal);
    Assert.Contains("materiales-delete-confirmation", styles, StringComparison.Ordinal);
  }

  [Fact]
  public void Page_EnforcesAdministratorLifecycleAndExactDeleteConfirmation()
  {
    var codeBehind = ReadRepoFile("src/OrionERP.Web/Features/Logistica/Materials/MaterialesPage.razor.cs");
    var page = ReadRepoFile("src/OrionERP.Web/Features/Logistica/Materials/MaterialesPage.razor");
    var service = ReadRepoFile("src/OrionERP.Infrastructure/Features/Logistica/Materials/MaterialService.cs");

    Assert.Contains("IsInRole(\"Administrador\")", codeBehind, StringComparison.Ordinal);
    Assert.Contains("ShowInactiveMaterials", codeBehind, StringComparison.Ordinal);
    Assert.Contains("IncludeInactive = IsAdministrator && ShowInactiveMaterials", codeBehind, StringComparison.Ordinal);
    Assert.Contains("Mostrar materiales desactivados", page, StringComparison.Ordinal);
    Assert.Contains("string.Equals(DeletionConfirmationText, \"Delete\", StringComparison.Ordinal)", codeBehind, StringComparison.Ordinal);
    Assert.Contains("ConfirmationText = DeletionConfirmationText", codeBehind, StringComparison.Ordinal);
    Assert.Contains("DeleteConfirmationText = \"Delete\"", service, StringComparison.Ordinal);
    Assert.Contains("IsolationLevel.Serializable", service, StringComparison.Ordinal);
    Assert.Contains("UPDLOCK, HOLDLOCK", service, StringComparison.Ordinal);
    Assert.Contains("DELETE FROM logistica.Material WHERE Rfc = @Rfc AND Id = @MaterialId", service, StringComparison.Ordinal);
    Assert.Contains("MaterialStatus = 'INACTIVO'", service, StringComparison.Ordinal);
    Assert.Contains("MaterialStatus = 'ACTIVO'", service, StringComparison.Ordinal);
    Assert.Contains("ResetLifecycleReport();", codeBehind, StringComparison.Ordinal);
  }

  [Fact]
  public void RecipePage_ExposesOnlyExplicitSafeCleanupActions()
  {
    var page = ReadRepoFile("src/OrionERP.Web/Features/Restaurante/RestaurantRecipesPage.razor");
    var service = ReadRepoFile("src/OrionERP.Infrastructure/Features/Restaurante/BomRecipeService.cs");

    Assert.Contains("Eliminar borrador", page, StringComparison.Ordinal);
    Assert.Contains("Retirar versión", page, StringComparison.Ordinal);
    Assert.Contains("Eliminar conversión", page, StringComparison.Ordinal);
    Assert.Contains("string.Equals(version.Status, \"Draft\"", service, StringComparison.Ordinal);
    Assert.Contains("string.Equals(version.Status, \"Active\"", service, StringComparison.Ordinal);
    Assert.Contains("[Status] IN ('Planned', 'Started')", service, StringComparison.Ordinal);
    Assert.Contains("versionInfo.[Status] IN ('Draft', 'Active')", service, StringComparison.Ordinal);
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
