using OrionERP.UnitTests.Common;

namespace OrionERP.UnitTests.Restaurante;

/// <summary>
/// La página de promociones enlaza <c>editor</c> a un <c>EditForm</c> con
/// <c>DataAnnotationsValidator</c>, y <c>RestaurantPromotionSaveRequest.Rfc</c> es
/// <c>[Required]</c>. Ese campo no se captura en ningún control: lo llena la página.
///
/// Si la instancia inicial del editor nace sin RFC, el validador rechaza el envío antes
/// de que <c>OnValidSubmit</c> dispare, así que la asignación que vive dentro de
/// <c>SavePromotionAsync</c> nunca llega a ejecutarse y el usuario sólo ve
/// "The Rfc field is required." sin ningún campo que pueda corregir.
///
/// A diferencia de las demás pantallas de restaurante, aquí <c>LoadAsync</c> no
/// reinicia el editor, por lo que el RFC tiene que venir desde la fábrica.
/// </summary>
public class RestaurantPromotionsRfcValidationTests
{
  private const string PromotionsPage = "src/OrionERP.Web/Features/Restaurante/RestaurantPromotionsPage.razor";

  [Fact]
  public void NewEditor_NaceConRfcParaQueElValidadorNoBloqueeElGuardado()
  {
    var page = RepoFile.Read(PromotionsPage);

    Assert.Contains("NewEditor() => new() { Rfc = CurrentRfc,", page, StringComparison.Ordinal);
  }

  [Fact]
  public void EditorInicial_UsaLaFabricaQueYaTraeRfc()
  {
    var page = RepoFile.Read(PromotionsPage);

    Assert.Contains("editor = NewEditor();", page, StringComparison.Ordinal);
    Assert.Contains("<DataAnnotationsValidator />", page, StringComparison.Ordinal);
    Assert.Contains("OnValidSubmit=\"SavePromotionAsync\"", page, StringComparison.Ordinal);
  }
}
