using OrionERP.Application.Features.Logistica.Materials;
using OrionERP.Web.Features.Restaurante;

namespace OrionERP.UnitTests.Restaurante;

public sealed class RestaurantMaterialPickerTests
{
  [Theory]
  [InlineData("mat-104")]
  [InlineData("harina")]
  [InlineData("panaderia")]
  [InlineData("kilogramo")]
  public void Search_MatchesCodeDescriptionAndDetailsWithoutAccentSensitivity(string searchText)
  {
    var option = new RestaurantMaterialOption(
      104,
      "MAT-104",
      "Harína de trigo",
      "Panadería · Kilógramo");

    Assert.True(option.Matches(searchText));
  }

  [Fact]
  public void MaterialProjection_OrdersByDescriptionAndPreservesUsefulDetails()
  {
    var materials = new[]
    {
      new MaterialListItemDto { Id = 2, MaterialCode = "Z-02", Description = "Zanahoria" },
      new MaterialListItemDto
      {
        Id = 1,
        MaterialCode = "A-01",
        Description = "Aceite",
        BaseUnitId = 7,
        CategoryName = "Abarrotes",
        BaseUnitName = "Litro"
      }
    };

    var options = RestaurantMaterialOption.FromMaterials(materials);

    Assert.Equal(new[] { 1, 2 }, options.Select(option => option.Id));
    Assert.Equal("Abarrotes · Litro", options[0].Detail);
  }

  [Fact]
  public void RestaurantMaterialSelections_UseTheSharedSearchablePicker()
  {
    var picker = ReadRepoFile("src/OrionERP.Web/Features/Restaurante/RestaurantMaterialPicker.razor");
    Assert.Contains("role=\"combobox\"", picker, StringComparison.Ordinal);
    Assert.Contains("@oninput=\"Filter\"", picker, StringComparison.Ordinal);
    Assert.Contains("MaximumResults", picker, StringComparison.Ordinal);

    var admin = ReadRepoFile("src/OrionERP.Web/Features/Restaurante/RestaurantAdminPage.razor");
    var recipes = ReadRepoFile("src/OrionERP.Web/Features/Restaurante/RestaurantRecipesPage.razor");
    var recipeSettings = ReadRepoFile("src/OrionERP.Web/Features/Restaurante/RestaurantRecipeSettingsPage.razor");
    var menus = ReadRepoFile("src/OrionERP.Web/Features/Restaurante/RestaurantMenuManagementPage.razor");
    var movements = ReadRepoFile("src/OrionERP.Web/Features/Restaurante/RestaurantInventoryMovementsPage.razor");

    Assert.Contains("<RestaurantMaterialPicker Label=\"Material logístico\"", admin, StringComparison.Ordinal);
    Assert.Equal(2, Count(recipes, "<RestaurantMaterialPicker"));
    Assert.Equal(2, Count(recipeSettings, "<RestaurantMaterialPicker"));
    Assert.Contains("@bind-Value=\"delta.MaterialId\"", menus, StringComparison.Ordinal);
    Assert.Contains("@bind-Value=\"line.MaterialId\"", movements, StringComparison.Ordinal);
    Assert.DoesNotContain("<select @bind=\"component.MaterialId\"", recipes, StringComparison.Ordinal);
    Assert.DoesNotContain("<select @bind=\"delta.MaterialId\"", menus, StringComparison.Ordinal);
    Assert.DoesNotContain("<select @bind=\"line.MaterialId\"", movements, StringComparison.Ordinal);
  }

  [Fact]
  public void RecipeEditor_DefaultsToBaseUnitsAndOffersConfiguredKitchenUnits()
  {
    var recipes = ReadRepoFile("src/OrionERP.Web/Features/Restaurante/RestaurantRecipesPage.razor");

    Assert.Contains("Changed=\"SetProductMaterialAsync\"", recipes, StringComparison.Ordinal);
    Assert.Contains("SetComponentMaterial(component, materialId)", recipes, StringComparison.Ordinal);
    Assert.Contains("GetRecipeUnitOptionsAsync", recipes, StringComparison.Ordinal);
    Assert.Contains("UnitOptionsFor(component.MaterialId)", recipes, StringComparison.Ordinal);
    Assert.Contains("UnitId = item.UnitId", recipes, StringComparison.Ordinal);
    Assert.Contains("option.IsBase", recipes, StringComparison.Ordinal);
  }

  [Fact]
  public void RecipeLayout_UsesResponsiveCardsWithoutIngredientTableScrolling()
  {
    var css = ReadRepoFile("src/OrionERP.Web/Features/Restaurante/RestaurantRecipesPage.razor.css");

    Assert.Contains(".recipe-workspace", css, StringComparison.Ordinal);
    Assert.Contains(".recipe-ingredient", css, StringComparison.Ordinal);
    Assert.Contains("min-width:0", css, StringComparison.Ordinal);
    Assert.Contains("@media(max-width:650px)", css, StringComparison.Ordinal);
    Assert.Contains("grid-template-columns:2.4rem minmax(0,1fr)", css, StringComparison.Ordinal);
    Assert.Contains(".recipe-toolbar>div{display:flex;flex-wrap:wrap", css, StringComparison.Ordinal);
    Assert.DoesNotContain(".recipe-table", css, StringComparison.Ordinal);
  }

  [Fact]
  public void Picker_ProvidesKeyboardAndOutsideDismissalBehavior()
  {
    var picker = ReadRepoFile("src/OrionERP.Web/Features/Restaurante/RestaurantMaterialPicker.razor");
    var script = ReadRepoFile("src/OrionERP.Web/wwwroot/js/restaurant-ui.js");

    Assert.Contains("ArrowDown", picker, StringComparison.Ordinal);
    Assert.Contains("aria-activedescendant", picker, StringComparison.Ordinal);
    Assert.Contains("CloseFromOutside", picker, StringComparison.Ordinal);
    Assert.Contains("registerDismissable", script, StringComparison.Ordinal);
  }

  private static int Count(string source, string value)
  {
    var count = 0;
    var index = 0;
    while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
    {
      count++;
      index += value.Length;
    }
    return count;
  }

  private static string ReadRepoFile(string relativePath)
    => File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../", relativePath)));
}
