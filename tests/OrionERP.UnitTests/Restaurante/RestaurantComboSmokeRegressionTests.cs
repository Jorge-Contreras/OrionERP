using System.Data;
using OrionERP.Application.Common;
using OrionERP.Application.Features.Restaurante;
using OrionERP.Infrastructure.Features.Restaurante;

namespace OrionERP.UnitTests.Restaurante;

public sealed class RestaurantComboSmokeRegressionTests
{
  [Fact]
  public void ProductEditor_UpdatesRequiredFieldsOnInputAndSavesCombosWithoutMaterial()
  {
    var page = ReadRepoFile("src/OrionERP.Web/Features/Restaurante/RestaurantAdminPage.razor");

    Assert.Contains(
      "<input @bind=\"productEditor.Sku\" @bind:event=\"oninput\" />",
      page,
      StringComparison.Ordinal);
    Assert.Contains(
      "<input type=\"number\" min=\"0\" step=\"0.01\" @bind=\"productEditor.Price\" @bind:event=\"oninput\" />",
      page,
      StringComparison.Ordinal);
    Assert.Contains(
      "<input @bind=\"productEditor.Name\" @bind:event=\"oninput\" />",
      page,
      StringComparison.Ordinal);
    Assert.DoesNotContain("@oninput=\"OnProduct", page, StringComparison.Ordinal);

    var comboSaveStart = page.IndexOf("if (IsComboProduct)", page.IndexOf("private async Task SaveProductAsync()", StringComparison.Ordinal), StringComparison.Ordinal);
    var saveCall = page.IndexOf("CatalogService.SaveProductAsync(productEditor)", comboSaveStart, StringComparison.Ordinal);
    var comboSave = page[comboSaveStart..saveCall];

    Assert.Contains("productEditor.MaterialId = null;", comboSave, StringComparison.Ordinal);
    Assert.Contains("productEditor.KitchenStationId = null;", comboSave, StringComparison.Ordinal);
    Assert.Contains("productEditor.PreparationMinutes = null;", comboSave, StringComparison.Ordinal);
    Assert.Contains("productEditor.ProductionRole = MaterialProductionRoles.Unclassified;", comboSave, StringComparison.Ordinal);
  }

  [Fact]
  public void CommercialEditors_KeyReplacedModelsAndNestedRowsToAvoidStaleVisualState()
  {
    var page = ReadRepoFile("src/OrionERP.Web/Features/Restaurante/RestaurantMenuManagementPage.razor");

    Assert.Contains("@key=\"menuEditor\"", page, StringComparison.Ordinal);
    Assert.Contains("@key=\"comboEditor\"", page, StringComparison.Ordinal);
    Assert.Contains("@key=\"modifierEditor\"", page, StringComparison.Ordinal);
    Assert.Contains("@key=\"section\"", page, StringComparison.Ordinal);
    Assert.Contains("@key=\"slot\"", page, StringComparison.Ordinal);
    Assert.Equal(2, CountOccurrences(page, "@key=\"option\""));
    Assert.Contains("@key=\"route\"", page, StringComparison.Ordinal);
    Assert.Contains("@key=\"delta\"", page, StringComparison.Ordinal);

    Assert.Contains("menuEditor=NewMenuEditor()", page, StringComparison.Ordinal);
    Assert.Contains("comboEditor=new RestaurantComboSaveRequest", page, StringComparison.Ordinal);
    Assert.Contains("modifierEditor=NewModifierEditor()", page, StringComparison.Ordinal);
  }

  [Fact]
  public async Task SaveModifierGroupAsync_RejectsBlankNameBeforeOpeningAConnection()
  {
    var connectionFactory = new FailIfOpenedConnectionFactory();
    var service = new RestaurantCatalogService(connectionFactory);
    var request = new RestaurantModifierSaveRequest
    {
      Rfc = "BRUNOS260707L26",
      Name = "   ",
      MinSelections = 0,
      MaxSelections = 1,
      Options = [new RestaurantModifierOptionSaveRequest { Name = "Sin cebolla" }]
    };

    var result = await service.SaveModifierGroupAsync(request);

    Assert.False(result.Success);
    Assert.Contains("requiere nombre", result.Message, StringComparison.OrdinalIgnoreCase);
    Assert.Equal(0, connectionFactory.CreateCalls);
  }

  private static int CountOccurrences(string source, string value)
  {
    var count = 0;
    var searchStart = 0;
    while ((searchStart = source.IndexOf(value, searchStart, StringComparison.Ordinal)) >= 0)
    {
      count++;
      searchStart += value.Length;
    }

    return count;
  }

  private static string ReadRepoFile(string relativePath)
    => File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../", relativePath)));

  private sealed class FailIfOpenedConnectionFactory : IDbConnectionFactory
  {
    public int CreateCalls { get; private set; }

    public IDbConnection Create()
    {
      CreateCalls++;
      throw new InvalidOperationException("La validación debe ocurrir antes de abrir una conexión.");
    }
  }
}
