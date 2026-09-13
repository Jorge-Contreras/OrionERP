using System.Text.RegularExpressions;
using OrionERP.UnitTests.Common;

namespace OrionERP.UnitTests.Logistica;

public sealed class PurchaseAccountingUxTests
{
  private static readonly string Panel = RepoFile.Read(
    "src/OrionERP.Web/Features/Logistica/Purchasing/PurchaseOrderAccountingPanel.razor");
  private static readonly string PanelCode = RepoFile.Read(
    "src/OrionERP.Web/Features/Logistica/Purchasing/PurchaseOrderAccountingPanel.razor.cs");
  private static readonly string Compras = RepoFile.Read(
    "src/OrionERP.Web/Features/Logistica/Purchasing/ComprasPage.razor");
  private static readonly string ComprasCode = RepoFile.Read(
    "src/OrionERP.Web/Features/Logistica/Purchasing/ComprasPage.razor.cs");

  [Fact]
  public void Panel_ConfirmsOnThePageInsteadOfBrowserDialogs()
  {
    // Hay navegadores que suprimen confirm(): el clic se volvía un "Cancelar" silencioso y la
    // persona no sabía por qué no pasaba nada.
    Assert.DoesNotContain("\"confirm\"", PanelCode, StringComparison.Ordinal);
    Assert.DoesNotContain("IJSRuntime", PanelCode, StringComparison.Ordinal);
    Assert.Contains("Una póliza creada ya no se puede borrar", Panel, StringComparison.Ordinal);
  }

  [Fact]
  public void Panel_ButtonsNeverSubmitAForm()
  {
    var buttons = Regex.Matches(Panel, "<button\\b").Count;
    var nonSubmitting = Regex.Matches(Panel, "<button\\s+type=\"button\"").Count;

    Assert.True(buttons > 0);
    Assert.Equal(buttons, nonSubmitting);
  }

  [Fact]
  public void ComprasPage_RendersAccountingOutsideTheOrderForm()
  {
    var orderFormEnd = Compras.IndexOf("</EditForm>", StringComparison.Ordinal);
    var panel = Compras.IndexOf("<PurchaseOrderAccountingPanel", StringComparison.Ordinal);

    Assert.True(orderFormEnd > 0, "La compra debe seguir editándose dentro de su formulario.");
    Assert.True(panel > orderFormEnd, "El panel contable debe quedar fuera del formulario de la compra.");
  }

  [Fact]
  public void APolicyLinksBackToTheOrderItCovers()
  {
    var transacciones = RepoFile.Read("src/OrionERP.Web/Features/Contabilidad/Transacciones/TransaccionPage.razor");

    Assert.Contains("[SupplyParameterFromQuery(Name = \"orden\")]", ComprasCode, StringComparison.Ordinal);
    Assert.Contains("/logistica/compras?orden=@link.PurchaseOrderId", transacciones, StringComparison.Ordinal);
  }
}
