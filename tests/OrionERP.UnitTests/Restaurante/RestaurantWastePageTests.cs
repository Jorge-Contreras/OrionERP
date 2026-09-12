using OrionERP.UnitTests.Common;

namespace OrionERP.UnitTests.Restaurante;

public sealed class RestaurantWastePageTests
{
  private const string PagePath = "src/OrionERP.Web/Features/Restaurante/RestaurantWastePage.razor";
  private const string CodePath = "src/OrionERP.Web/Features/Restaurante/RestaurantWastePage.razor.cs";
  private const string StylePath = "src/OrionERP.Web/Features/Restaurante/RestaurantWastePage.razor.css";

  [Fact]
  public void Page_IsReachableByKitchenAndCashiersNotOnlyByAdministrators()
  {
    var page = RepoFile.Read(PagePath);
    var program = RepoFile.Read("src/OrionERP.Web/Program.cs");

    Assert.Contains("@page \"/restaurante/mermas\"", page, StringComparison.Ordinal);
    Assert.Contains("[Authorize(Policy = \"RestaurantWaste\")]", page, StringComparison.Ordinal);
    Assert.Contains("options.AddPolicy(\"RestaurantWaste\"", program, StringComparison.Ordinal);
    Assert.Contains("\"RestauranteCocina\", \"RestauranteCaja\", \"RestauranteSupervisor\", \"RestauranteAdmin\"", program, StringComparison.Ordinal);
  }

  [Fact]
  public void Quantity_IsCapturedInPositiveNeverAsANegativeDelta()
  {
    var page = RepoFile.Read(PagePath);

    Assert.Contains("min=\"0.0001\"", page, StringComparison.Ordinal);
    Assert.Contains("aria-label=\"Cantidad que se tira\"", page, StringComparison.Ordinal);
    Assert.DoesNotContain("negativa", page, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("QuantityDelta", page, StringComparison.Ordinal);
  }

  [Fact]
  public void MaterialSelection_UsesTheSearchablePickerAndNotARawSelect()
  {
    var page = RepoFile.Read(PagePath);

    Assert.Contains("<RestaurantMaterialPicker", page, StringComparison.Ordinal);
    Assert.Contains("@bind-Value=\"entry.MaterialId\"", page, StringComparison.Ordinal);
    Assert.DoesNotContain("<select @bind=\"entry.MaterialId\"", page, StringComparison.Ordinal);
  }

  [Fact]
  public void DestroyingInventory_AsksForConfirmationAndShowsTheCostFirst()
  {
    var page = RepoFile.Read(PagePath);

    Assert.Contains("aria-labelledby=\"waste-confirm-title\"", page, StringComparison.Ordinal);
    Assert.Contains("Confirmar baja", page, StringComparison.Ordinal);
    Assert.Contains("se corrige con una reversa: no se puede borrar", page, StringComparison.Ordinal);
    Assert.Contains("Costo total de la merma", page, StringComparison.Ordinal);
    // El botón abre el diálogo; nunca aplica la baja directamente.
    Assert.Contains("@onclick=\"() => showConfirm = true\"", page, StringComparison.Ordinal);
  }

  [Fact]
  public void ReviewAndReversal_AreReservedForSupervisors()
  {
    var page = RepoFile.Read(PagePath);
    var code = RepoFile.Read(CodePath);

    Assert.Contains("<AuthorizeView Policy=\"RestaurantAdmin\">", page, StringComparison.Ordinal);
    var gate = page.IndexOf("<AuthorizeView Policy=\"RestaurantAdmin\">", StringComparison.Ordinal);
    Assert.InRange(gate, 0, page.IndexOf(">Aprobar<", StringComparison.Ordinal));
    Assert.InRange(gate, 0, page.IndexOf(">Reversar<", StringComparison.Ordinal));
    // Ocultar los botones no basta: el rango se vuelve a resolver en el servidor.
    Assert.Contains("AuthorizationService.AuthorizeAsync(auth.User, \"RestaurantAdmin\")", code, StringComparison.Ordinal);
  }

  [Fact]
  public void Reversal_RefusesToRunWithoutAReason()
  {
    var page = RepoFile.Read(PagePath);

    Assert.Contains("string.IsNullOrWhiteSpace(reversalReason)", page, StringComparison.Ordinal);
    Assert.Contains("Motivo de la reversa", page, StringComparison.Ordinal);
  }

  [Fact]
  public void Evidence_IsRequestedInPageAndServedOutsideTheCircuit()
  {
    var page = RepoFile.Read(PagePath);
    var api = RepoFile.Read("src/OrionERP.Web/Features/Logistica/Stock/WasteEvidenceApi.cs");

    Assert.Contains("<InputFile", page, StringComparison.Ordinal);
    Assert.Contains("accept=\"image/*,.pdf\"", page, StringComparison.Ordinal);
    Assert.Contains("capture=\"environment\"", page, StringComparison.Ordinal);
    Assert.Contains("/api/logistica/merma/@document.Id/evidencia", page, StringComparison.Ordinal);
    Assert.Contains("RequireAuthorization(\"RestaurantWaste\")", api, StringComparison.Ordinal);
    // El fetch corre fuera del circuito, donde IHospitalityScopeAccessor no puede leer el estado
    // de autenticación: el endpoint arma el servicio sin ese alcance a propósito.
    Assert.Contains("new WasteService(connectionFactory)", api, StringComparison.Ordinal);
    Assert.DoesNotContain("IWasteService", api, StringComparison.Ordinal);
  }

  [Fact]
  public void HistoryTotal_SaysItSumsTheListedRowsNotTheNetWaste()
  {
    var page = RepoFile.Read(PagePath);

    // El listado muestra las reversas, así que su suma no es la merma neta; los indicadores
    // de arriba sí lo son. La etiqueta tiene que decir cuál es cuál.
    Assert.Contains("suma de lo listado", page, StringComparison.Ordinal);
  }

  [Fact]
  public void LocationPicker_ForcesAnExplicitChoiceWhenThereIsMoreThanOne()
  {
    var page = RepoFile.Read(PagePath);

    Assert.Contains("<option value=\"0\">Seleccionar…</option>", page, StringComparison.Ordinal);
    Assert.Contains("workspace.Locations.Count != 1", page, StringComparison.Ordinal);
  }

  [Fact]
  public void Failures_AreTranslatedInsteadOfLeakingRawExceptionText()
  {
    var code = RepoFile.Read(CodePath);

    Assert.Contains("Errors.ToUserMessage(ex", code, StringComparison.Ordinal);
    Assert.DoesNotContain("Show(ex.Message", code, StringComparison.Ordinal);
  }

  [Fact]
  public void Layout_HasOneHeadingAndCollapsesOnAKitchenTablet()
  {
    var page = RepoFile.Read(PagePath);
    var style = RepoFile.Read(StylePath);

    Assert.Equal(1, page.Split("<h1>", StringSplitOptions.None).Length - 1);
    Assert.Contains("@media(max-width:650px)", style, StringComparison.Ordinal);
    Assert.Contains(".waste-kpis{grid-template-columns:1fr}", style, StringComparison.Ordinal);
  }

  [Fact]
  public void Navigation_OffersTheRouteAsDailyCapture()
  {
    var navigation = RepoFile.Read("src/OrionERP.Web/Shared/NavigationCatalog.cs");

    Assert.Contains("new(\"/restaurante/mermas\", \"Merma\"", navigation, StringComparison.Ordinal);
    Assert.Contains("\"caducidad\", \"desperdicio\"", navigation, StringComparison.Ordinal);
  }

  [Fact]
  public void Kardex_NamesTheReversalMovement()
  {
    var materials = RepoFile.Read("src/OrionERP.Web/Features/Logistica/Materials/MaterialesPage.razor.cs");

    Assert.Contains("\"WasteReversal\" => \"Reversa de merma\"", materials, StringComparison.Ordinal);
    Assert.Contains("\"WasteReversal\" => \"bi-arrow-counterclockwise\"", materials, StringComparison.Ordinal);
  }
}
