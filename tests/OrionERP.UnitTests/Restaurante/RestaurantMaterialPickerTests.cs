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
    var menus = ReadRepoFile("src/OrionERP.Web/Features/Restaurante/RestaurantMenuManagementPage.razor");
    var movements = ReadRepoFile("src/OrionERP.Web/Features/Restaurante/RestaurantInventoryMovementsPage.razor");

    Assert.Contains("<RestaurantMaterialPicker Label=\"Material logístico\"", admin, StringComparison.Ordinal);
    Assert.Equal(4, Count(recipes, "<RestaurantMaterialPicker"));
    Assert.Contains("@bind-Value=\"delta.MaterialId\"", menus, StringComparison.Ordinal);
    Assert.Contains("@bind-Value=\"line.MaterialId\"", movements, StringComparison.Ordinal);
    Assert.DoesNotContain("<select @bind=\"component.MaterialId\"", recipes, StringComparison.Ordinal);
    Assert.DoesNotContain("<select @bind=\"delta.MaterialId\"", menus, StringComparison.Ordinal);
    Assert.DoesNotContain("<select @bind=\"line.MaterialId\"", movements, StringComparison.Ordinal);
  }

  [Fact]
  public void RecipeLayout_ConstrainsGridItemsAndProvidesHorizontalScrolling()
  {
    var css = ReadRepoFile("src/OrionERP.Web/Features/Restaurante/RestaurantRecipesPage.razor.css");

    Assert.Contains("grid-template-columns: minmax(260px, 300px) minmax(0, 1fr);", css, StringComparison.Ordinal);
    Assert.Contains(".recipe-editor", css, StringComparison.Ordinal);
    Assert.Contains("min-width: 0;", css, StringComparison.Ordinal);
    Assert.Contains(".recipe-table", css, StringComparison.Ordinal);
    Assert.Contains("overflow-x: auto;", css, StringComparison.Ordinal);
    Assert.Contains("scrollbar-gutter: stable both-edges;", css, StringComparison.Ordinal);
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
