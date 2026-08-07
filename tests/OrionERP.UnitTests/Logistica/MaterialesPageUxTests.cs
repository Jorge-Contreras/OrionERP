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

  [Fact]
  public void Page_ProvidesDetailedStrictDeletionReport()
  {
    var page = ReadRepoFile("src/OrionERP.Web/Features/Logistica/Materials/MaterialesPage.razor");
    var styles = ReadRepoFile("src/OrionERP.Web/Features/Logistica/Materials/MaterialesPage.razor.css");

    Assert.Contains("Revisar eliminación", page, StringComparison.Ordinal);
    Assert.Contains("Revisión de eliminación permanente", page, StringComparison.Ordinal);
    Assert.Contains("blockedAssessment.Blockers", page, StringComparison.Ordinal);
    Assert.Contains("blocker.ReferenceCount", page, StringComparison.Ordinal);
    Assert.Contains("blocker.Examples", page, StringComparison.Ordinal);
    Assert.Contains("No se elimina ni se desvincula información automáticamente", page, StringComparison.Ordinal);
    Assert.Contains("value=\"@DeletionConfirmationText\"", page, StringComparison.Ordinal);
    Assert.Contains("Eliminar permanentemente", page, StringComparison.Ordinal);
    Assert.Contains("materiales-delete-blockers", styles, StringComparison.Ordinal);
    Assert.Contains("materiales-delete-confirmation", styles, StringComparison.Ordinal);
  }

  [Fact]
  public void Page_EnforcesAdministratorAndExactDeleteConfirmation()
  {
    var codeBehind = ReadRepoFile("src/OrionERP.Web/Features/Logistica/Materials/MaterialesPage.razor.cs");
    var service = ReadRepoFile("src/OrionERP.Infrastructure/Features/Logistica/Materials/MaterialService.cs");

    Assert.Contains("IsInRole(\"Administrador\")", codeBehind, StringComparison.Ordinal);
    Assert.Contains("string.Equals(DeletionConfirmationText, \"Delete\", StringComparison.Ordinal)", codeBehind, StringComparison.Ordinal);
    Assert.Contains("ConfirmationText = DeletionConfirmationText", codeBehind, StringComparison.Ordinal);
    Assert.Contains("DeleteConfirmationText = \"Delete\"", service, StringComparison.Ordinal);
    Assert.Contains("IsolationLevel.Serializable", service, StringComparison.Ordinal);
    Assert.Contains("UPDLOCK, HOLDLOCK", service, StringComparison.Ordinal);
    Assert.Contains("DELETE FROM logistica.Material WHERE Rfc = @Rfc AND Id = @MaterialId", service, StringComparison.Ordinal);
    Assert.Contains("ResetDeletionReport();", codeBehind, StringComparison.Ordinal);
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
