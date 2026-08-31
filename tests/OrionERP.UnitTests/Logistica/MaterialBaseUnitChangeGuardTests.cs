namespace OrionERP.UnitTests.Logistica;

/// <summary>
/// Cambiar la unidad base de un material rompe en silencio las recetas activas que ya lo
/// referencian: la que lo consume en la unidad anterior deja de convertir, el ingrediente aporta
/// $0 al costo y el motor de venta reporta BOM_CONVERSION_MISSING. Pasó dos veces en Bruno's
/// —MEZCLA DE ESPECIAS y CHICKEN FINGERS— antes de existir esta guarda.
/// </summary>
public sealed class MaterialBaseUnitChangeGuardTests
{
  [Fact]
  public void SaveMaterial_ChecksForBreakageBeforeWritingTheNewBaseUnit()
  {
    var service = ReadRepoFile("src/OrionERP.Infrastructure/Features/Logistica/Materials/MaterialService.cs");

    Assert.Contains("FindBaseUnitChangeBreakageAsync", service, StringComparison.Ordinal);

    // La comprobación va antes del UPDATE, y aborta la transacción.
    var guardIndex = service.IndexOf("var baseUnitBreakage = await FindBaseUnitChangeBreakageAsync", StringComparison.Ordinal);
    var updateIndex = service.IndexOf("UPDATE logistica.Material\n          SET [Description] = @Description", StringComparison.Ordinal);
    if (updateIndex < 0)
    {
      updateIndex = service.IndexOf("SET [Description] = @Description", StringComparison.Ordinal);
    }
    Assert.True(guardIndex > 0, "La guarda debe existir en la ruta de actualización.");
    Assert.True(updateIndex > guardIndex, "La guarda debe correr antes de escribir la unidad base.");
  }

  [Fact]
  public void Guard_IsSilentWhenTheBaseUnitDoesNotChange()
  {
    var service = ReadRepoFile("src/OrionERP.Infrastructure/Features/Logistica/Materials/MaterialService.cs");

    Assert.Contains("currentBaseUnitId == newBaseUnitId", service, StringComparison.Ordinal);
  }

  [Fact]
  public void Guard_CoversBothConsumingRecipesAndTheMaterialsOwnYield()
  {
    var service = ReadRepoFile("src/OrionERP.Infrastructure/Features/Logistica/Materials/MaterialService.cs");

    // Rama 1: recetas activas que lo consumen en una unidad que ya no convertiría.
    Assert.Contains("'component' AS Kind", service, StringComparison.Ordinal);
    Assert.Contains("component.ComponentMaterialId = @MaterialId", service, StringComparison.Ordinal);
    Assert.Contains("logistica.MaterialUnitConversion materialConversion", service, StringComparison.Ordinal);
    Assert.Contains("logistica.UnitConversion globalConversion", service, StringComparison.Ordinal);

    // Rama 2: su propia receta activa quedaría rindiendo fuera de la unidad de inventario.
    Assert.Contains("'yield'", service, StringComparison.Ordinal);
    Assert.Contains("ownVersion.YieldUnitId <> @NewBaseUnitId", service, StringComparison.Ordinal);

    // Sólo las versiones activas bloquean; el historial no debe impedir un cambio.
    Assert.Contains("parentVersion.[Status] = 'Active'", service, StringComparison.Ordinal);
  }

  [Fact]
  public void Guard_ExplainsWhichRecipeBlocksAndWhat()
  {
    var service = ReadRepoFile("src/OrionERP.Infrastructure/Features/Logistica/Materials/MaterialService.cs");

    Assert.Contains("No se puede cambiar la unidad base a {newUnitName}", service, StringComparison.Ordinal);
    Assert.Contains("Ajusta esas recetas o crea la conversión antes de cambiar la unidad.", service, StringComparison.Ordinal);
    Assert.Contains("Corrige primero el rendimiento de esa receta.", service, StringComparison.Ordinal);
  }

  private static string ReadRepoFile(string relativePath)
    => File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../", relativePath)));
}
