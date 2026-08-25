namespace OrionERP.UnitTests.Auth;

public sealed class CompanySessionRefactorRegressionTests
{
  [Fact]
  public void Mutable_rfc_state_and_picker_contracts_are_removed()
  {
    var webRoot = RepoPath("src/OrionERP.Web");
    var source = string.Join('\n', Directory.EnumerateFiles(webRoot, "*", SearchOption.AllDirectories)
      .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
      .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
      .Select(File.ReadAllText));

    Assert.DoesNotContain("IUserRfcState", source, StringComparison.Ordinal);
    Assert.DoesNotContain("UserRfcState", source, StringComparison.Ordinal);
    Assert.DoesNotContain("SelectedRfcChanged", source, StringComparison.Ordinal);
    Assert.DoesNotContain("RfcState.Changed", source, StringComparison.Ordinal);
    Assert.False(File.Exists(Path.Combine(webRoot, "Shared", "RfcPicker.razor")));
  }

  [Fact]
  public void Account_picker_has_one_read_only_company_parameter_and_rejects_cross_company_results()
  {
    var source = File.ReadAllText(RepoPath("src/OrionERP.Web/Shared/CuentaContablePicker.razor"));

    Assert.Contains("[Parameter, EditorRequired] public string Rfc", source, StringComparison.Ordinal);
    Assert.DoesNotContain("SelectedRfc", source, StringComparison.Ordinal);
    Assert.DoesNotContain("SelectedRfcChanged", source, StringComparison.Ordinal);
    Assert.Contains("account.Rfc", source, StringComparison.Ordinal);
    Assert.Contains("no pertenece", source, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public void Balanza_has_no_editable_rfc_or_change_handler()
  {
    var page = File.ReadAllText(RepoPath("src/OrionERP.Web/Features/ReportesFinancieros/BalanzaComprobacion/BalanzaComprobacionPage.razor"));
    var code = File.ReadAllText(RepoPath("src/OrionERP.Web/Features/ReportesFinancieros/BalanzaComprobacion/BalanzaComprobacionPage.razor.cs"));

    Assert.DoesNotContain("OnRfc", page, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("OnRfc", code, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("@bind-Value=\"CurrentRfc\"", page, StringComparison.Ordinal);
    Assert.Contains("RfcState.RequireRfc()", code, StringComparison.Ordinal);
  }

  [Fact]
  public void Prenomina_export_asserts_query_rfc_and_passes_the_session_company()
  {
    var source = File.ReadAllText(RepoPath("src/OrionERP.Web/Program.cs"));
    var endpointStart = source.IndexOf("/api/workforce/prenomina/exports", StringComparison.Ordinal);
    var endpoint = source.Substring(endpointStart, Math.Min(1_200, source.Length - endpointStart));

    Assert.Contains("companyContext.EnsureRfc(rfc)", endpoint, StringComparison.Ordinal);
    Assert.Contains("service.GetAsync(exportId, companyContext.RequireRfc()", endpoint, StringComparison.Ordinal);
  }

  private static string RepoPath(string relativePath)
    => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../", relativePath));
}
