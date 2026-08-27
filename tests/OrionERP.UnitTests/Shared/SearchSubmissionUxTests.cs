namespace OrionERP.UnitTests.Shared;

public class SearchSubmissionUxTests
{
  [Fact]
  public void CommandCenter_SubmitsTheDomValueOnlyWhenEnterIsPressed()
  {
    var component = ReadRepoFile("src/OrionERP.Web/Shared/CommandCenter.razor");
    var script = ReadRepoFile("src/OrionERP.Web/wwwroot/js/orion-command-center.js");

    Assert.DoesNotContain("@oninput=\"HandleSearchInput\"", component, StringComparison.Ordinal);
    Assert.DoesNotContain("@onkeydown", component, StringComparison.Ordinal);
    Assert.Contains("SubmitSearch(string? searchText)", component, StringComparison.Ordinal);
    Assert.Contains("event.key === \"Enter\"", script, StringComparison.Ordinal);
    Assert.Contains("invokeMethodAsync(\"SubmitSearch\", searchInput.value)", script, StringComparison.Ordinal);
  }

  [Fact]
  public void HighTrafficDataSearches_UseEnterInsteadOfTypingDebounces()
  {
    var reservations = ReadRepoFile("src/OrionERP.Web/Features/Reservaciones/ListaReservaciones/ListaReservacionesPage.razor");
    var reservationCode = ReadRepoFile("src/OrionERP.Web/Features/Reservaciones/ListaReservaciones/ListaReservacionesPage.razor.cs");
    var materials = ReadRepoFile("src/OrionERP.Web/Features/Logistica/Materials/MaterialesPage.razor");
    var materialCode = ReadRepoFile("src/OrionERP.Web/Features/Logistica/Materials/MaterialesPage.razor.cs");
    var workOrders = ReadRepoFile("src/OrionERP.Web/Features/OrdenesTrabajo/OrdenesTrabajoPage.razor");
    var workOrderCode = ReadRepoFile("src/OrionERP.Web/Features/OrdenesTrabajo/OrdenesTrabajoPage.razor.cs");

    Assert.Contains("OnFilterSearchKeyUpAsync", reservations, StringComparison.Ordinal);
    Assert.DoesNotContain("FilterInputDebounceMs", reservationCode, StringComparison.Ordinal);
    Assert.Contains("OnSearchKeyUpAsync", materials, StringComparison.Ordinal);
    Assert.DoesNotContain("SearchDebounceMilliseconds", materialCode, StringComparison.Ordinal);
    Assert.Contains("OnSearchKeyUpAsync", workOrders, StringComparison.Ordinal);
    Assert.DoesNotContain("SearchDebounce", workOrderCode, StringComparison.Ordinal);
  }

  [Fact]
  public void ExistingBuscarButtonScreens_AlsoExposeEnterSubmission()
  {
    Assert.Contains("OnSearchKeyUpAsync", ReadRepoFile("src/OrionERP.Web/Features/CapitalHumano/CapitalHumanoPage.razor"), StringComparison.Ordinal);
    Assert.Contains("OnOrderSearchKeyUpAsync", ReadRepoFile("src/OrionERP.Web/Features/Logistica/Purchasing/ComprasPage.razor"), StringComparison.Ordinal);
    Assert.Contains("OnWorkspaceSearchKeyUpAsync", ReadRepoFile("src/OrionERP.Web/Features/CuentasPorPagar/Recurrentes/RecurrentApPage.razor"), StringComparison.Ordinal);
    Assert.Contains("OnStockSearchKeyUpAsync", ReadRepoFile("src/OrionERP.Web/Features/Logistica/Locations/UbicacionesPage.razor"), StringComparison.Ordinal);
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
