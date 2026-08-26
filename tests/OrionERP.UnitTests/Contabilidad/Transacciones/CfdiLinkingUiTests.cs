namespace OrionERP.UnitTests.Contabilidad.Transacciones;

public class CfdiLinkingUiTests
{
  [Fact]
  public void TransactionLinkingPage_ShowsPartiesAndConceptSummary()
  {
    var page = ReadRepositoryFile(
      "src", "OrionERP.Web", "Features", "Contabilidad", "Transacciones", "TransaccionesLinkingPage.razor");
    var codeBehind = ReadRepositoryFile(
      "src", "OrionERP.Web", "Features", "Contabilidad", "Transacciones", "TransaccionesLinkingPage.razor.cs");
    var service = ReadRepositoryFile(
      "src", "OrionERP.Infrastructure", "Features", "Contabilidad", "Transacciones", "Services", "TransaccionService.cs");

    Assert.Contains("Emisor / receptor", page, StringComparison.Ordinal);
    Assert.Contains("SummarizeConcepts(linked.Conceptos)", page, StringComparison.Ordinal);
    Assert.Contains("SummarizeConcepts(candidate.Conceptos)", page, StringComparison.Ordinal);
    Assert.Contains("protected static string SummarizeConcepts", codeBehind, StringComparison.Ordinal);
    Assert.Contains("FROM cfdi.Comprobante_Detalle", service, StringComparison.Ordinal);
    Assert.Contains("item.Emisor = party.Emisor", service, StringComparison.Ordinal);
    Assert.Contains("item.Receptor = party.Receptor", service, StringComparison.Ordinal);
  }

  [Fact]
  public void CfdiPolicyLinkingPage_OffersUnlinkAction()
  {
    var page = ReadRepositoryFile(
      "src", "OrionERP.Web", "Features", "Cfdi", "DeclaracionPrevia", "Pages", "LigarCFDIPolizaPage.razor");
    var codeBehind = ReadRepositoryFile(
      "src", "OrionERP.Web", "Features", "Cfdi", "DeclaracionPrevia", "Pages", "LigarCFDIPolizaPage.razor.cs");

    Assert.Contains("DesligarPolizaAsync(poliza)", page, StringComparison.Ordinal);
    Assert.Contains("IsLinkedPolizaBusy(poliza)", page, StringComparison.Ordinal);
    Assert.Contains("TransaccionService.UnlinkRegularCfdiAsync", codeBehind, StringComparison.Ordinal);
    Assert.Contains("Se liberarán", codeBehind, StringComparison.Ordinal);
    Assert.Contains("await LoadWorkspaceAsync();", codeBehind, StringComparison.Ordinal);
  }

  private static string ReadRepositoryFile(params string[] paths)
  {
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "OrionERP.sln")))
    {
      directory = directory.Parent;
    }

    if (directory is null)
    {
      throw new InvalidOperationException("Could not locate repository root.");
    }

    var allSegments = new string[paths.Length + 1];
    allSegments[0] = directory.FullName;
    Array.Copy(paths, 0, allSegments, 1, paths.Length);
    return File.ReadAllText(Path.Combine(allSegments));
  }
}
