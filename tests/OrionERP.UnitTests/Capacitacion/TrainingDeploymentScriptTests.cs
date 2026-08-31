namespace OrionERP.UnitTests.Capacitacion;

public sealed class TrainingDeploymentScriptTests
{
  private static readonly string Configure = ReadRepoFile("Configure-TrainingService.ps1");
  private static readonly string Publish = ReadRepoFile("Publish-Training.ps1");
  private static readonly string TrainingSettings = ReadRepoFile(
    "src/OrionERP.Web/appsettings.Training.json");
  private static readonly string Runbook = ReadRepoFile("docs/orion-training-runbook.md");
  private static readonly string MainLayout = ReadRepoFile(
    "src/OrionERP.Web/Shared/MainLayout.razor");
  private static readonly string MainLayoutCss = ReadRepoFile(
    "src/OrionERP.Web/Shared/MainLayout.razor.css");
  private static readonly string TrainingBanner = ReadRepoFile(
    "src/OrionERP.Web/Features/TrainingSafety/TrainingEnvironmentBanner.razor");
  private static readonly string TrainingBannerCss = ReadRepoFile(
    "src/OrionERP.Web/Features/TrainingSafety/TrainingEnvironmentBanner.razor.css");

  [Fact]
  public void Configure_UsesTheProductionCloneAndNoSpecialRuntimeLogin()
  {
    Assert.Contains("Orion_Training", Configure, StringComparison.Ordinal);
    Assert.Contains("Encrypt=True is required", Configure, StringComparison.Ordinal);
    Assert.Contains("production-clone contract", Configure, StringComparison.Ordinal);
    Assert.Contains("existingUsersPreserved", Configure, StringComparison.Ordinal);
    Assert.DoesNotContain("orion_training_runtime", Configure, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("DatosSanitizados", Configure, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("DatosSinteticos", Configure, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public void Configure_MakesTheServiceBootResilient()
  {
    Assert.Contains("start= delayed-auto", Configure, StringComparison.Ordinal);
    Assert.Contains("depend= 'MSSQL$SQLEXPRESS'", Configure, StringComparison.Ordinal);
    Assert.Contains("restart/60000/restart/60000/restart/120000", Configure, StringComparison.Ordinal);
    Assert.Contains("ORION_TRAINING_SERVICE=1", Configure, StringComparison.Ordinal);
    Assert.Contains("http://localhost:5030", Configure, StringComparison.Ordinal);
  }

  [Fact]
  public void Publish_ValidatesTheSimpleCloneContractAndIncludesCopiedEncryptionSupport()
  {
    Assert.Contains("production_clone", Publish, StringComparison.Ordinal);
    Assert.Contains("existingUsersPreserved", Publish, StringComparison.Ordinal);
    Assert.Contains("database.reachable", Publish, StringComparison.Ordinal);
    Assert.DoesNotContain("safetyVerified", Publish, StringComparison.Ordinal);
    Assert.DoesNotContain("syntheticDataOnly", Publish, StringComparison.Ordinal);
    Assert.DoesNotContain("ExcludeProductionEncryptionKey", Publish, StringComparison.Ordinal);
  }

  [Fact]
  public void Documentation_ExplainsThatUsersAndDataArePreserved()
  {
    Assert.Contains("misma cuenta", Runbook, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("No hay saneamiento", Runbook, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("production_clone", Runbook, StringComparison.Ordinal);
    Assert.Contains("capacitacion.orion.land", TrainingSettings, StringComparison.Ordinal);
    Assert.Contains("\"DataProtectionApplicationName\": \"OrionERP\"", TrainingSettings, StringComparison.Ordinal);
  }

  [Fact]
  public void TrainingUi_KeepsAPermanentBottomMarkerAndDistinctBackground()
  {
    Assert.Contains("app-shell--training", MainLayout, StringComparison.Ordinal);
    Assert.Contains("app-shell--training .app-shell__main", MainLayoutCss, StringComparison.Ordinal);
    Assert.Contains("CAPACITACIÓN · AMBIENTE DE PRÁCTICA", TrainingBanner, StringComparison.Ordinal);
    Assert.Contains("position: fixed", TrainingBannerCss, StringComparison.Ordinal);
    Assert.Contains("inset: auto 0 0", TrainingBannerCss, StringComparison.Ordinal);
  }

  private static string ReadRepoFile(string relativePath)
  {
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null)
    {
      var candidate = Path.Combine(directory.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
      if (File.Exists(candidate)) return File.ReadAllText(candidate);
      directory = directory.Parent;
    }

    throw new FileNotFoundException($"No se encontró {relativePath} desde {AppContext.BaseDirectory}.");
  }
}
