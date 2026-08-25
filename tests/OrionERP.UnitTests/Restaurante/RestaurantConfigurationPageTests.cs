namespace OrionERP.UnitTests.Restaurante;

public sealed class RestaurantConfigurationPageTests
{
  [Fact]
  public void AccountingConfiguration_UsesScopedLevelThreePickerInsteadOfFreeText()
  {
    var page = ReadRepoFile("src/OrionERP.Web/Features/Restaurante/RestaurantConfigurationPage.razor");

    Assert.Contains("<CuentaContablePicker", page, StringComparison.Ordinal);
    Assert.Contains("Rfc=\"@CurrentRfc\"", page, StringComparison.Ordinal);
    Assert.Contains("AllowAccountManagement=\"false\"", page, StringComparison.Ordinal);
    Assert.DoesNotContain("@bind=\"editor.Accounting.CashAccount\"", page, StringComparison.Ordinal);
    Assert.DoesNotContain("@bind=\"editor.Accounting.SalesAccount\"", page, StringComparison.Ordinal);

    var expectedFields = new[]
    {
      "CashAccount", "CardBankAccount", "TransferBankAccount", "PlatformReceivableAccount",
      "SalesAccount", "VatAccount", "DiscountAccount", "TipsPayableAccount",
      "PlatformCommissionAccount", "InventoryAccount", "CostOfSalesAccount", "WasteAccount"
    };
    foreach (var field in expectedFields)
    {
      Assert.Contains($"nameof(RestaurantAccountingConfigurationDto.{field})", page, StringComparison.Ordinal);
    }
  }

  [Fact]
  public void AccountingConfiguration_ValidatesSelectedAccountAgainstActiveRfc()
  {
    var service = ReadRepoFile("src/OrionERP.Infrastructure/Features/Restaurante/RestaurantCatalogService.cs");

    Assert.Contains("FindInvalidAccountingAccountAsync", service, StringComparison.Ordinal);
    Assert.Contains("FROM dbo.CuentasContables", service, StringComparison.Ordinal);
    Assert.Contains("RFC=@Rfc", service, StringComparison.Ordinal);
    Assert.Contains("Nivel3<>'00'", service, StringComparison.Ordinal);
  }

  private static string ReadRepoFile(string relativePath)
    => File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../", relativePath)));
}
