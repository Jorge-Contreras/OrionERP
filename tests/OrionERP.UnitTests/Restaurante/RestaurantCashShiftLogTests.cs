namespace OrionERP.UnitTests.Restaurante;

public sealed class RestaurantCashShiftLogTests
{
  [Fact]
  public void ShiftPage_ExposesBitacoraOnlyToRestaurantAdministrators()
  {
    var page = ReadRepoFile("src/OrionERP.Web/Features/Restaurante/RestaurantCashShiftsPage.razor");
    var program = ReadRepoFile("src/OrionERP.Web/Program.cs");

    Assert.Equal(3, CountOccurrences(page, "<AuthorizeView Policy=\"RestaurantAdminOnly\">"));
    Assert.Contains("AuthorizationService.AuthorizeAsync(auth.User, \"RestaurantAdminOnly\")", page, StringComparison.Ordinal);
    Assert.Contains("\"RestaurantAdminOnly\"", program, StringComparison.Ordinal);
    Assert.Contains("policy.RequireCompanyRoles(\"RestauranteAdmin\")", program, StringComparison.Ordinal);
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

  [Fact]
  public void ShiftCards_ShowOpeningClosingAccountabilityAndSalesDetails()
  {
    var service = ReadRepoFile("src/OrionERP.Infrastructure/Features/Restaurante/RestaurantCashService.cs");
    var page = ReadRepoFile("src/OrionERP.Web/Features/Restaurante/RestaurantCashShiftsPage.razor");
    var summary = ReadRepoFile("src/OrionERP.Web/Features/Restaurante/RestaurantCashShiftSalesSummary.razor");

    Assert.Contains("AS GrossSales", service, StringComparison.Ordinal);
    Assert.Contains("paymentInfo.PaidAt>=shiftInfo.OpenedAt", service, StringComparison.Ordinal);
    Assert.Contains("refundInfo.RefundedAt>=shiftInfo.OpenedAt", service, StringComparison.Ordinal);
    Assert.Contains("QueryMultipleAsync", service, StringComparison.Ordinal);
    Assert.Contains("<dt>Apertura</dt>", page, StringComparison.Ordinal);
    Assert.Contains("<dt>Cierre</dt>", page, StringComparison.Ordinal);
    Assert.Contains("<dt>Abrió</dt>", page, StringComparison.Ordinal);
    Assert.Contains("<dt>Cerró</dt>", page, StringComparison.Ordinal);
    Assert.Contains("<dt>Fondo inicial</dt>", page, StringComparison.Ordinal);
    Assert.Contains("Ventas del turno", summary, StringComparison.Ordinal);
    Assert.Contains("Venta neta", summary, StringComparison.Ordinal);
    Assert.Contains("Venta bruta", summary, StringComparison.Ordinal);
    Assert.Contains("Reembolsos", summary, StringComparison.Ordinal);
    Assert.Contains("Shift.GrossSales - RefundTotal", summary, StringComparison.Ordinal);
    Assert.Contains("Neto recibido", summary, StringComparison.Ordinal);
    Assert.Contains("Desglose por forma de pago", summary, StringComparison.Ordinal);
  }

  [Fact]
  public void ShiftCards_PreserveBlindCountAndCashOnlyReconciliation()
  {
    var service = ReadRepoFile("src/OrionERP.Infrastructure/Features/Restaurante/RestaurantCashService.cs");
    var page = ReadRepoFile("src/OrionERP.Web/Features/Restaurante/RestaurantCashShiftsPage.razor");

    Assert.Contains("<Authorized><RestaurantCashShiftSalesSummary Shift=\"shift\" IsLive=\"true\" /></Authorized>", page, StringComparison.Ordinal);
    Assert.Contains("después de confirmar el conteo ciego", page, StringComparison.Ordinal);
    Assert.Equal(2, CountOccurrences(page, "<RestaurantCashShiftSalesSummary Shift=\"shift\""));
    Assert.Contains("MovementType IN ('OpeningFloat','Sale','CashIn') AND PaymentMethod='Cash'", service, StringComparison.Ordinal);
    Assert.Contains("MovementType IN ('Refund','CashOut') AND PaymentMethod='Cash'", service, StringComparison.Ordinal);
    Assert.DoesNotContain("PaymentMethod='ExternalCard' THEN Amount", service, StringComparison.Ordinal);
  }

  [Fact]
  public void Pos_RequiresAnOpenMatchingShiftForEverySale()
  {
    var page = ReadRepoFile("src/OrionERP.Web/Features/Restaurante/RestaurantPosPage.razor");
    var service = ReadRepoFile("src/OrionERP.Infrastructure/Features/Restaurante/RestaurantOrderService.cs");

    Assert.DoesNotContain(">Sin turno<", page, StringComparison.Ordinal);
    Assert.Contains("SelectedShift is not null", page, StringComparison.Ordinal);
    Assert.Contains("Se requiere un turno abierto", page, StringComparison.Ordinal);
    Assert.Contains("openShifts.Count == 1 ? openShifts[0].Id : null", page, StringComparison.Ordinal);
    Assert.Contains("Las ventas de Punto de Venta requieren seleccionar un turno de caja abierto.", service, StringComparison.Ordinal);
    Assert.Contains("shiftInfo.CashRegisterId=@CashRegisterId", service, StringComparison.Ordinal);
    Assert.Contains("registerInfo.IsActive=1", service, StringComparison.Ordinal);
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
