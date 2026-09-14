using OrionERP.UnitTests.Common;

namespace OrionERP.UnitTests.Platform;

/// <summary>
/// Las pantallas universales no deben consultar Hospedaje para una empresa sin ese módulo:
/// su alcance niega la conexión y cada carga terminaba en un error para el usuario
/// (Bruno's en pólizas y en Ajustes). Estas pruebas vigilan que la guarda siga en su lugar.
/// </summary>
public sealed class ModuleAwareScreensTests
{
  [Fact]
  public void TransaccionPage_QueriesReservationsOnlyWithHospitality()
  {
    var code = RepoFile.Read("src/OrionERP.Web/Features/Contabilidad/Transacciones/TransaccionPage.razor.cs");
    var markup = RepoFile.Read("src/OrionERP.Web/Features/Contabilidad/Transacciones/TransaccionPage.razor");

    Assert.Matches(@"Task ReloadReservacionLinksAsync\([^)]*\)\s*\{\s*ReservacionLinks\.Clear\(\);\s*if \(!HasHospitality\)", code);
    Assert.Matches(@"Task SearchReservacionesAsync\([^)]*\)\s*\{\s*if \(!HasHospitality\)", code);
    Assert.Matches(@"@if \(HasHospitality\)\s*\{\s*<li class=""nav-item"">\s*<button type=""button""\s*class=""@GetTabButtonClass\(SectionPanel\.Reservaciones\)""", markup);
    Assert.Contains("else if (HasHospitality && IsActiveSection(SectionPanel.Reservaciones))", markup);
  }

  [Fact]
  public void AjustesPage_LoadsExtrasOnlyWithHospitality_AndDoesNotShowExceptionText()
  {
    var page = RepoFile.Read("src/OrionERP.Web/Features/Ajustes/AjustesPage.razor");

    Assert.Matches(@"if \(hasHospitality\)\s*\{\s*await LoadExtraCatalogAsync\(\);", page);
    var guard = page.IndexOf("@if (hasHospitality)", StringComparison.Ordinal);
    Assert.True(guard >= 0, "La sección de extras debe estar condicionada a Hospedaje.");
    Assert.True(guard < page.IndexOf("@onclick=\"NewExtra\"", StringComparison.Ordinal));
    Assert.DoesNotContain("{ex.Message}", page);
  }

  [Fact]
  public void CatalogosPage_OffersArrendadoresOnlyWithHospitality()
  {
    var code = RepoFile.Read("src/OrionERP.Web/Features/Ajustes/Catalogos/CatalogosPage.razor.cs");

    Assert.Contains("hasHospitality || descriptor.Key != CatalogoKey.Arrendadores", code);
  }
}
