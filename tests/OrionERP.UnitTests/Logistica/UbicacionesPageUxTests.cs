namespace OrionERP.UnitTests.Logistica;

public class UbicacionesPageUxTests
{
  [Fact]
  public void Page_MakesInventoryTheDefaultGuidedWorkflow()
  {
    var page = ReadRepoFile("src/OrionERP.Web/Features/Logistica/Locations/UbicacionesPage.razor");
    var codeBehind = ReadRepoFile("src/OrionERP.Web/Features/Logistica/Locations/UbicacionesPage.razor.cs");

    Assert.Contains("ActiveMode { get; set; } = InventoryMode", codeBehind, StringComparison.Ordinal);
    Assert.Contains("¿Dónde vas a trabajar?", page, StringComparison.Ordinal);
    Assert.Contains("ubicaciones-step-number\">1", page, StringComparison.Ordinal);
    Assert.Contains("ubicaciones-step-number\">2", page, StringComparison.Ordinal);
    Assert.Contains("Revisa el inventario", page, StringComparison.Ordinal);
    Assert.Contains("Administrar ubicaciones", page, StringComparison.Ordinal);
    Assert.Contains("IsInventoryMode", page, StringComparison.Ordinal);
    Assert.Contains("IsManagementMode", page, StringComparison.Ordinal);
  }

  [Fact]
  public void Page_UsesPlainSpanishWithoutExposingImplementationDetails()
  {
    var page = ReadRepoFile("src/OrionERP.Web/Features/Logistica/Locations/UbicacionesPage.razor");
    var codeBehind = ReadRepoFile("src/OrionERP.Web/Features/Logistica/Locations/UbicacionesPage.razor.cs");

    Assert.Contains("\"Room\" => \"Habitación\"", codeBehind, StringComparison.Ordinal);
    Assert.Contains("\"Storage\" => \"Almacén\"", codeBehind, StringComparison.Ordinal);
    Assert.Contains("\"Disposal\" => \"Descarte\"", codeBehind, StringComparison.Ordinal);
    Assert.Contains("\"Service\" => \"Servicio\"", codeBehind, StringComparison.Ordinal);
    Assert.DoesNotContain("ROOM_TYPE", page, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("filtro SQL", page, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("sincroniza inventario, miniaturas", page, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public void Page_RemembersAndValidatesUserCompanyLocationContext()
  {
    var codeBehind = ReadRepoFile("src/OrionERP.Web/Features/Logistica/Locations/UbicacionesPage.razor.cs");

    Assert.Contains("orionerp.logistica.ubicaciones.selection", codeBehind, StringComparison.Ordinal);
    Assert.Contains("CurrentRfc", codeBehind, StringComparison.Ordinal);
    Assert.Contains("_currentUserId", codeBehind, StringComparison.Ordinal);
    Assert.Contains("RestoreRememberedSelectionAsync", codeBehind, StringComparison.Ordinal);
    Assert.Contains("RoomOptions.All", codeBehind, StringComparison.Ordinal);
    Assert.Contains("location.RoomId == remembered.RoomId", codeBehind, StringComparison.Ordinal);
    Assert.Contains("localStorage.removeItem", codeBehind, StringComparison.Ordinal);
    Assert.Contains("catch (JSException)", codeBehind, StringComparison.Ordinal);
  }

  [Fact]
  public void Page_ProvidesDedicatedMobileCardsAndFocusedPanels()
  {
    var page = ReadRepoFile("src/OrionERP.Web/Features/Logistica/Locations/UbicacionesPage.razor");
    var styles = ReadRepoFile("src/OrionERP.Web/Features/Logistica/Locations/UbicacionesPage.razor.css");

    Assert.Contains("ubicaciones-stock-cards", page, StringComparison.Ordinal);
    Assert.Contains("ubicaciones-stock-card", page, StringComparison.Ordinal);
    Assert.Contains("ubicaciones-detail-panel", page, StringComparison.Ordinal);
    Assert.Contains("ubicaciones-picker-grid", page, StringComparison.Ordinal);
    Assert.Contains("ubicaciones-location-editor", page, StringComparison.Ordinal);
    Assert.Contains("@media (max-width: 767.98px)", styles, StringComparison.Ordinal);
    Assert.Contains("position: fixed", styles, StringComparison.Ordinal);
    Assert.Contains("min-height: 2.8rem", styles, StringComparison.Ordinal);
    Assert.Contains("overflow-x: hidden", styles, StringComparison.Ordinal);
  }

  [Fact]
  public void Page_GuardsNonOperationalLocationsAndUnsavedEdits()
  {
    var page = ReadRepoFile("src/OrionERP.Web/Features/Logistica/Locations/UbicacionesPage.razor");
    var codeBehind = ReadRepoFile("src/OrionERP.Web/Features/Logistica/Locations/UbicacionesPage.razor.cs");

    Assert.Contains("CanAddMaterialToSelectedLocation", page, StringComparison.Ordinal);
    Assert.Contains("La ubicación debe estar activa y habilitada para inventario", codeBehind, StringComparison.Ordinal);
    Assert.Contains("CanDiscardLocationEditorAsync", codeBehind, StringComparison.Ordinal);
    Assert.Contains("Hay cambios sin guardar", codeBehind, StringComparison.Ordinal);
    Assert.Contains("LocationEditContext.OnFieldChanged", codeBehind, StringComparison.Ordinal);
    Assert.Contains("VerInventarioDeUbicacionAsync", page, StringComparison.Ordinal);
  }

  [Fact]
  public void Page_KeepsManagementHighlightSeparateFromRememberedInventoryContext()
  {
    var page = ReadRepoFile("src/OrionERP.Web/Features/Logistica/Locations/UbicacionesPage.razor");
    var codeBehind = ReadRepoFile("src/OrionERP.Web/Features/Logistica/Locations/UbicacionesPage.razor.cs");
    var styles = ReadRepoFile("src/OrionERP.Web/Features/Logistica/Locations/UbicacionesPage.razor.css");

    Assert.Contains("ManagementLocationId", codeBehind, StringComparison.Ordinal);
    Assert.Contains("GetLocationCardClass(item, ManagementLocationId)", page, StringComparison.Ordinal);
    Assert.Contains("await LoadLocationEditorAsync(locationId)", codeBehind, StringComparison.Ordinal);
    Assert.DoesNotContain("SeleccionarUbicacionAsync(locationId, openEditor: true", codeBehind, StringComparison.Ordinal);
    Assert.Contains("min-height: 44px", styles, StringComparison.Ordinal);
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
