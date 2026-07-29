namespace OrionERP.UnitTests.Restaurante;

public sealed class RestaurantCashShiftLogTests
{
  [Fact]
  public void ShiftPage_ExposesBitacoraOnlyToRestaurantAdministrators()
  {
    var page = ReadRepoFile("src/OrionERP.Web/Features/Restaurante/RestaurantCashShiftsPage.razor");
    var program = ReadRepoFile("src/OrionERP.Web/Program.cs");

    Assert.Equal(2, CountOccurrences(page, "<AuthorizeView Policy=\"RestaurantAdminOnly\">"));
    Assert.Contains("AuthorizationService.AuthorizeAsync(auth.User, \"RestaurantAdminOnly\")", page, StringComparison.Ordinal);
    Assert.Contains("\"RestaurantAdminOnly\"", program, StringComparison.Ordinal);
    Assert.Contains("new RoleForRfcRequirement(\"RestauranteAdmin\")", program, StringComparison.Ordinal);
  }

  [Fact]
  public void ShiftLog_ReconstructsFinancialOperationalAndAuthorizationHistory()
  {
    var service = ReadRepoFile("src/OrionERP.Infrastructure/Features/Restaurante/RestaurantCashService.cs");
    var page = ReadRepoFile("src/OrionERP.Web/Features/Restaurante/RestaurantCashShiftsPage.razor");
    var styles = ReadRepoFile("src/OrionERP.Web/Features/Restaurante/RestaurantCashShiftsPage.razor.css");

    Assert.Contains("GetShiftLogAsync", service, StringComparison.Ordinal);
    Assert.Contains("FROM restaurante.Payment paymentInfo", service, StringComparison.Ordinal);
    Assert.Contains("FROM restaurante.PaymentRefund refundInfo", service, StringComparison.Ordinal);
    Assert.Contains("FROM restaurante.CashMovement movement", service, StringComparison.Ordinal);
    Assert.Contains("FROM restaurante.OrderEvent eventInfo", service, StringComparison.Ordinal);
    Assert.Contains("\"ShiftOpened\"", service, StringComparison.Ordinal);
    Assert.Contains("\"ShiftCounted\"", service, StringComparison.Ordinal);
    Assert.Contains("\"ShiftDifferenceApproved\"", service, StringComparison.Ordinal);

    Assert.Contains("Bitácora del turno", page, StringComparison.Ordinal);
    Assert.Contains("Firmó el conteo", page, StringComparison.Ordinal);
    Assert.Contains("Autorizó diferencia", page, StringComparison.Ordinal);
    Assert.Contains("Resumen por forma de pago", page, StringComparison.OrdinalIgnoreCase);
    Assert.Contains(".shift-log__timeline", styles, StringComparison.Ordinal);
    Assert.Contains(".shift-log__accountability", styles, StringComparison.Ordinal);
  }

  private static int CountOccurrences(string value, string search)
  {
    var count = 0;
    var startIndex = 0;
    while ((startIndex = value.IndexOf(search, startIndex, StringComparison.Ordinal)) >= 0)
    {
      count++;
      startIndex += search.Length;
    }
    return count;
  }

  private static string ReadRepoFile(string relativePath)
    => File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../", relativePath)));
}
