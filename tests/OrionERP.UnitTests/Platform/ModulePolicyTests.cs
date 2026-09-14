using OrionERP.UnitTests.Common;

namespace OrionERP.UnitTests.Platform;

/// <summary>
/// Cada política de Restaurante y de Hospedaje exige el módulo además del rol, y el
/// manejador que lo evalúa está registrado. Sin eso, una empresa sin el módulo vuelve a
/// entrar a pantallas que sólo muestran errores de alcance.
/// </summary>
public sealed class ModulePolicyTests
{
  [Theory]
  [InlineData("RestaurantAdmin", "Restaurant")]
  [InlineData("RestaurantAdminOnly", "Restaurant")]
  [InlineData("RestaurantPos", "Restaurant")]
  [InlineData("RestaurantKitchen", "Restaurant")]
  [InlineData("RestaurantDisplay", "Restaurant")]
  [InlineData("RestaurantCash", "Restaurant")]
  [InlineData("RestaurantWaste", "Restaurant")]
  [InlineData("RestaurantQzBridge", "Restaurant")]
  [InlineData("HospitalityAdministration", "Hospitality")]
  [InlineData("HospitalityCalendar", "Hospitality")]
  [InlineData("HospitalityArrendadores", "Hospitality")]
  public void ModulePolicy_RequiresItsModule(string policyName, string moduleConstant)
  {
    var program = RepoFile.Read("src/OrionERP.Web/Program.cs");

    Assert.Matches(
      $@"""{policyName}"",\s*policy\s*=>[^;]*\.RequireCompanyModule\(PlatformModuleCodes\.{moduleConstant}\)\);",
      program);
  }

  [Fact]
  public void ModuleRequirementHandler_IsRegistered()
  {
    var program = RepoFile.Read("src/OrionERP.Web/Program.cs");

    Assert.Contains("AddScoped<IAuthorizationHandler, CompanyModuleAuthorizationHandler>()", program, StringComparison.Ordinal);
  }
}
