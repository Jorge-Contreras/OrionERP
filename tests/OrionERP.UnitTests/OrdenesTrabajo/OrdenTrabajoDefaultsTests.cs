namespace OrionERP.UnitTests.OrdenesTrabajo;

public class OrdenTrabajoDefaultsTests
{
  [Fact]
  public void PhotoDefaultMigration_UsesOptionalWithoutBackfill()
  {
    var sql = ReadRepositoryFile(
      "src",
      "OrionERP.Infrastructure",
      "Features",
      "OrdenesTrabajo",
      "Sql",
      "20260825_ordenes_trabajo_photo_optional_default.sql");

    Assert.Contains("DF_OrdenTrabajoPaso_PoliticaFoto DEFAULT ('OPCIONAL')", sql, StringComparison.Ordinal);
    Assert.Contains("DF_OrdenTrabajoPlantillaPaso_PoliticaFoto DEFAULT ('OPCIONAL')", sql, StringComparison.Ordinal);
    Assert.DoesNotContain("UPDATE dbo.OrdenTrabajoPaso", sql, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("UPDATE dbo.OrdenTrabajoPlantillaPaso", sql, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public void DetailPage_UsesHechoToggleAndOptionalNewSteps()
  {
    var page = ReadRepositoryFile(
      "src",
      "OrionERP.Web",
      "Features",
      "OrdenesTrabajo",
      "OrdenTrabajoDetailPage.razor");
    var codeBehind = ReadRepositoryFile(
      "src",
      "OrionERP.Web",
      "Features",
      "OrdenesTrabajo",
      "OrdenTrabajoDetailPage.razor.cs");

    Assert.Contains("OrdenTrabajoCodes.TogglePasoHecho(step.Estado)", page, StringComparison.Ordinal);
    Assert.Contains("aria-pressed", page, StringComparison.Ordinal);
    Assert.Contains("PoliticaFoto = OrdenTrabajoCodes.FotoOpcional", codeBehind, StringComparison.Ordinal);
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

    return File.ReadAllText(Path.Combine([directory.FullName, .. paths]));
  }
}
