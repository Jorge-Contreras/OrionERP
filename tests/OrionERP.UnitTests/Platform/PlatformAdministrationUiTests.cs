namespace OrionERP.UnitTests.Platform;

public sealed class PlatformAdministrationUiTests
{
  [Fact]
  public void Page_IsAdministratorOnlyAndShowsThePublicationChain()
  {
    var source = ReadRepositoryFile(
      "src", "OrionERP.Web", "Features", "Platform", "PlatformAdministrationPage.razor");

    Assert.Contains("[Authorize(Roles = \"Administrador\")]", source, StringComparison.Ordinal);
    Assert.Contains("Empresa → sede → módulo → capacidad → website", source, StringComparison.Ordinal);
    Assert.Contains("readonly aria-readonly=\"true\"", source, StringComparison.Ordinal);
    Assert.DoesNotContain("@bind=\"CompanyForm.Rfc\"", source, StringComparison.Ordinal);
    Assert.Contains("Confirmación requerida", source, StringComparison.Ordinal);
    Assert.Contains("Versión de marca", source, StringComparison.Ordinal);
    Assert.Contains("Versión de contenido", source, StringComparison.Ordinal);
    Assert.Contains("Confirmar reversión después de restaurar release", source, StringComparison.Ordinal);
    Assert.Contains("Finalizar activación", source, StringComparison.Ordinal);
    Assert.Contains("IsPresentationRollbackAvailable", source, StringComparison.Ordinal);
    Assert.Contains("primero desactiva y guarda el website", source, StringComparison.Ordinal);
  }

  [Fact]
  public void CompanyDirectory_ExposesTheCompanyScopedPlatformPage()
  {
    var source = ReadRepositoryFile(
      "src", "OrionERP.Web", "Features", "Auth", "Companies", "CompanyAdminPage.razor");

    Assert.Contains("/admin/plataforma", source, StringComparison.Ordinal);
    Assert.Contains("Plataforma y websites", source, StringComparison.Ordinal);
  }

  [Fact]
  public void EfMappings_DeclareEveryPlatformTriggerAndDisableSqlOutput()
  {
    var source = ReadRepositoryFile(
      "src", "OrionERP.Infrastructure", "Features", "Platform", "Data", "PlatformDbContext.cs");

    foreach (var trigger in new[]
    {
      "TR_Company_PlatformAudit",
      "TR_Site_GuardAudit",
      "TR_Module_GuardAudit",
      "TR_CompanyModule_GuardAudit",
      "TR_SiteCapability_GuardAudit",
      "TR_PublicSite_GuardAudit"
    })
      Assert.Contains($"HasTrigger(\"{trigger}\")", source, StringComparison.Ordinal);
    Assert.Equal(6, CountOccurrences(source, "UseSqlOutputClause(false)"));
  }

  [Fact]
  public void Writer_SetsAuditSessionContextAndHandlesPersistenceConflicts()
  {
    var source = ReadRepositoryFile(
      "src", "OrionERP.Infrastructure", "Features", "Platform", "PlatformAdministrationService.cs");

    Assert.Contains("OrionERP.UserName", source, StringComparison.Ordinal);
    Assert.Contains("OrionERP.Application", source, StringComparison.Ordinal);
    Assert.Contains("OrionERP.CorrelationId", source, StringComparison.Ordinal);
    Assert.Contains("catch (DbUpdateConcurrencyException)", source, StringComparison.Ordinal);
    Assert.Contains("catch (DbUpdateException exception)", source, StringComparison.Ordinal);
  }

  private static string ReadRepositoryFile(params string[] segments)
  {
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "OrionERP.sln")))
      directory = directory.Parent;
    Assert.NotNull(directory);
    return File.ReadAllText(Path.Combine([directory!.FullName, .. segments]));
  }

  private static int CountOccurrences(string source, string value)
    => (source.Length - source.Replace(value, string.Empty, StringComparison.Ordinal).Length) / value.Length;
}
