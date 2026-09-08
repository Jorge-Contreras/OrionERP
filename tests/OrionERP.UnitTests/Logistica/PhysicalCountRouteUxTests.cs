using OrionERP.Application.Features.Logistica.PhysicalCounts;
using OrionERP.UnitTests.Common;

namespace OrionERP.UnitTests.Logistica;

/// <summary>
/// Un conteo por material cruza varias ubicaciones. Si la pantalla no dice a dónde ir y no agrupa
/// por parada, el contador termina cruzando el almacén de ida y vuelta por cada renglón. Estas
/// pruebas cuidan el recorrido y la etiqueta con la que se nombra una sesión sin ubicación única.
/// </summary>
public class PhysicalCountRouteUxTests
{
  private const string PagePath = "src/OrionERP.Web/Features/Logistica/PhysicalCounts/ConteosFisicosPage.razor";
  private const string CodeBehindPath = "src/OrionERP.Web/Features/Logistica/PhysicalCounts/ConteosFisicosPage.razor.cs";
  private const string StylesPath = "src/OrionERP.Web/Features/Logistica/PhysicalCounts/ConteosFisicosPage.razor.css";

  [Fact]
  public void CapturePage_TellsTheCounterWhereToGoAndGroupsEachStop()
  {
    var page = RepoFile.Read(PagePath);
    var codeBehind = RepoFile.Read(CodeBehindPath);
    var styles = RepoFile.Read(StylesPath);

    Assert.Contains("conteos-focus-location", page, StringComparison.Ordinal);
    Assert.Contains("GetLineLocationLabel(SelectedLine)", page, StringComparison.Ordinal);
    Assert.Contains("Ubicación @CurrentLocationPosition de @CaptureLocationCount", page, StringComparison.Ordinal);
    Assert.Contains("GroupedSessionLines", page, StringComparison.Ordinal);
    Assert.Contains("conteos-location-group", page, StringComparison.Ordinal);

    Assert.Contains("SessionSpansMultipleLocations", codeBehind, StringComparison.Ordinal);
    Assert.Contains("CaptureLocationOrder", codeBehind, StringComparison.Ordinal);

    Assert.Contains(".conteos-focus-location", styles, StringComparison.Ordinal);
    Assert.Contains(".conteos-location-group", styles, StringComparison.Ordinal);
  }

  [Fact]
  public void CreateDialog_OffersBothScopesWithAPreviewOfWhatItWillGenerate()
  {
    var page = RepoFile.Read(PagePath);
    var codeBehind = RepoFile.Read(CodeBehindPath);

    Assert.Contains("Por ubicación", page, StringComparison.Ordinal);
    Assert.Contains("Por material", page, StringComparison.Ordinal);
    Assert.Contains("PhysicalCountSessionScopeTypes.Material", page, StringComparison.Ordinal);
    Assert.Contains("conteos-scope-preview", page, StringComparison.Ordinal);
    Assert.Contains("GetScopePreviewSummary()", page, StringComparison.Ordinal);

    // El bloqueo por solapamiento tiene que verse antes de crear, no como error al guardar.
    Assert.Contains("HasScopeConflicts", page, StringComparison.Ordinal);
    Assert.Contains("conflict.SessionCode", page, StringComparison.Ordinal);
    Assert.Contains("PreviewScopeAsync", codeBehind, StringComparison.Ordinal);
  }

  [Theory]
  [InlineData("Almacén general", null, 0, 0, "Almacén general")]
  [InlineData(null, "7113 · Tornillo hex", 1, 6, "7113 · Tornillo hex — 6 ubicaciones")]
  [InlineData(null, "7113 · Tornillo hex", 1, 1, "7113 · Tornillo hex — 1 ubicación")]
  [InlineData(null, "7113 · Tornillo hex", 3, 11, "3 materiales — 11 ubicaciones")]
  public void ScopeLabel_NamesASessionThatHasNoSingleLocation(
    string? locationName,
    string? primaryMaterialLabel,
    int materialCount,
    int locationCount,
    string expected)
  {
    var scopeType = locationName is null
      ? PhysicalCountSessionScopeTypes.Material
      : PhysicalCountSessionScopeTypes.Location;

    Assert.Equal(
      expected,
      PhysicalCountScopeLabel.Format(scopeType, locationName, primaryMaterialLabel, materialCount, locationCount));
  }

  [Fact]
  public void ScopeLabel_FallsBackWhenTheScopeHasNotGeneratedLinesYet()
  {
    Assert.Equal(
      PhysicalCountScopeLabel.UnnamedLocation,
      PhysicalCountScopeLabel.Format(PhysicalCountSessionScopeTypes.Location, "  ", null, 0, 0));

    Assert.Equal(
      PhysicalCountScopeLabel.EmptyMaterialScope,
      PhysicalCountScopeLabel.Format(PhysicalCountSessionScopeTypes.Material, null, null, 1, 0));
  }
}
