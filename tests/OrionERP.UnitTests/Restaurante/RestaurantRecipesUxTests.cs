namespace OrionERP.UnitTests.Restaurante;

public sealed class RestaurantRecipesUxTests
{
  [Fact]
  public void RecipesPage_UsesChefLanguageAndExplicitVersionLifecycle()
  {
    var page = ReadRepoFile("src/OrionERP.Web/Features/Restaurante/RestaurantRecipesPage.razor");

    Assert.Contains("Recetas de cocina", page, StringComparison.Ordinal);
    Assert.Contains("Crear nueva versión desde ésta", page, StringComparison.Ordinal);
    Assert.Contains("Esta versión es de sólo lectura", page, StringComparison.Ordinal);
    Assert.Contains("Cambios sin guardar", page, StringComparison.Ordinal);
    Assert.Contains("NavigationLock", page, StringComparison.Ordinal);
    Assert.Contains("private string statusFilter = \"Active\";", page, StringComparison.Ordinal);
    Assert.Contains("VisibleVersions(family)", page, StringComparison.Ordinal);
    Assert.Contains("v@(version.VersionNumber)", page, StringComparison.Ordinal);
    Assert.DoesNotContain(">v@version.VersionNumber<", page, StringComparison.Ordinal);
    Assert.Contains("VersionCountLabel", page, StringComparison.Ordinal);
    Assert.DoesNotContain("versión@(visibleVersions.Count", page, StringComparison.Ordinal);
    Assert.DoesNotContain("Alérgenos por material", page, StringComparison.Ordinal);
    Assert.DoesNotContain("Conversiones especiales", page, StringComparison.Ordinal);
  }

  [Fact]
  public void RecipeSettings_AreSeparatedAndKeepRestaurantAuthorization()
  {
    var settings = ReadRepoFile("src/OrionERP.Web/Features/Restaurante/RestaurantRecipeSettingsPage.razor");
    var materialService = ReadRepoFile("src/OrionERP.Infrastructure/Features/Logistica/Materials/MaterialService.cs");

    Assert.Contains("@page \"/restaurante/recetas/configuracion\"", settings, StringComparison.Ordinal);
    Assert.Contains("Authorize(Policy = \"RestaurantAdmin\")", settings, StringComparison.Ordinal);
    Assert.Contains("1 @FromUnitLabel equivale a", settings, StringComparison.Ordinal);
    Assert.Contains("/restaurante/recetas/configuracion", materialService, StringComparison.Ordinal);
  }

  [Fact]
  public void RecipeService_PersistsCreatorAndAcceptsConfiguredUnits()
  {
    var service = ReadRepoFile("src/OrionERP.Infrastructure/Features/Restaurante/BomRecipeService.cs");

    Assert.Contains("CreatedBy = NormalizeActor(userName)", service, StringComparison.Ordinal);
    Assert.Contains("GetRecipeUnitOptionsAsync", service, StringComparison.Ordinal);
    Assert.Contains("MaterialUnitConversion", service, StringComparison.Ordinal);
    Assert.Contains("UnitConversion", service, StringComparison.Ordinal);
    Assert.Contains("unitInfo.Abbreviation AS UnitCode", service, StringComparison.Ordinal);
    Assert.DoesNotContain("unitInfo.UnitCode", service, StringComparison.Ordinal);
    Assert.Contains("ya tiene un borrador", service, StringComparison.Ordinal);
    Assert.Contains("GetActivationReadinessAsync", service, StringComparison.Ordinal);
    Assert.Contains("Agrega al menos un paso con instrucciones", service, StringComparison.Ordinal);
    Assert.Contains("AllergenMaterials", service, StringComparison.Ordinal);
    Assert.DoesNotContain("SELECT TOP (1) child.Id", service, StringComparison.Ordinal);
  }

  [Fact]
  public void KitchenPreview_ScalesWithoutMutatingTheStoredRecipe()
  {
    var preview = ReadRepoFile("src/OrionERP.Web/Features/Restaurante/RestaurantRecipeKitchenPreview.razor");

    Assert.Contains("RestaurantRecipeScaling.ScaleQuantity", preview, StringComparison.Ordinal);
    Assert.Contains("BORRADOR — NO USAR", preview, StringComparison.Ordinal);
    Assert.Contains("restaurantUi.printPage", preview, StringComparison.Ordinal);
    Assert.Contains("Version.Allergens", preview, StringComparison.Ordinal);
  }

  [Fact]
  public void RecipeCosts_AreAdminOnlyAndExplainEveryIngredientContribution()
  {
    var page = ReadRepoFile("src/OrionERP.Web/Features/Restaurante/RestaurantRecipesPage.razor");
    var costComponent = ReadRepoFile("src/OrionERP.Web/Features/Restaurante/RestaurantRecipeCostBreakdown.razor");
    var costStyles = ReadRepoFile("src/OrionERP.Web/Features/Restaurante/RestaurantRecipeCostBreakdown.razor.css");
    var service = ReadRepoFile("src/OrionERP.Infrastructure/Features/Restaurante/BomRecipeService.cs");

    Assert.Equal(3, page.Split("<AuthorizeView Policy=\"RestaurantAdminOnly\">", StringSplitOptions.None).Length - 1);
    Assert.Contains("AuthorizationService.AuthorizeAsync(authenticationState.User, \"RestaurantAdminOnly\")", page, StringComparison.Ordinal);
    Assert.Contains("canViewCosts", page, StringComparison.Ordinal);
    Assert.Contains("BomService.GetCostBreakdownAsync", page, StringComparison.Ordinal);
    Assert.Contains("<RestaurantRecipeCostBreakdown", page, StringComparison.Ordinal);

    Assert.Contains("Costo de elaboración", costComponent, StringComparison.Ordinal);
    Assert.Contains("Sólo administradores", costComponent, StringComparison.Ordinal);
    Assert.Contains("Aporte al lote", costComponent, StringComparison.Ordinal);
    Assert.Contains("Aporte por unidad", costComponent, StringComparison.Ordinal);
    Assert.Contains("Sin costo configurado", service, StringComparison.Ordinal);
    Assert.Contains("Costo de subreceta activa", service, StringComparison.Ordinal);
    Assert.Contains("Precio de Materiales", service, StringComparison.Ordinal);
    Assert.Contains("COALESCE(material.BaseUnitPrice, subBom.FrozenTheoreticalCost", service, StringComparison.Ordinal);
    Assert.DoesNotContain("stockCost.AverageUnitCost", service, StringComparison.Ordinal);
    Assert.Contains("component.ExpectedWastePercent", service, StringComparison.Ordinal);
    Assert.Contains("materialConversion.Factor", service, StringComparison.Ordinal);
    Assert.Contains("@media(max-width:650px)", costStyles, StringComparison.Ordinal);
    Assert.DoesNotContain("overflow-x:auto", costStyles, StringComparison.Ordinal);
  }

  private static string ReadRepoFile(string relativePath)
    => File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../", relativePath)));
}
