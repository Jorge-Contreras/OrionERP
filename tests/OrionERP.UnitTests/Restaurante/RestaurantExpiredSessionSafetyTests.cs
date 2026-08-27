namespace OrionERP.UnitTests.Restaurante;

public sealed class RestaurantExpiredSessionSafetyTests
{
  [Fact]
  public void ReconnectOverlay_BlocksTheApplicationUntilTheConnectionRecovers()
  {
    var component = ReadRepoFile("src/OrionERP.Web/Shared/ConnectionStatus.razor");
    var styles = ReadRepoFile("src/OrionERP.Web/Shared/ConnectionStatus.razor.css");
    var script = ReadRepoFile("src/OrionERP.Web/wwwroot/js/orion-work-order-reconnect.js");
    var layout = ReadRepoFile("src/OrionERP.Web/Shared/MainLayout.razor");

    Assert.Contains("role=\"alertdialog\"", component, StringComparison.Ordinal);
    Assert.Contains("aria-modal=\"true\"", component, StringComparison.Ordinal);
    Assert.Contains("inset: 0;", styles, StringComparison.Ordinal);
    Assert.Contains("pointer-events: auto;", styles, StringComparison.Ordinal);
    Assert.Contains("id=\"orion-app-shell\"", layout, StringComparison.Ordinal);
    Assert.Contains("appShell?.setAttribute(\"inert\", \"\")", script, StringComparison.Ordinal);
    Assert.Contains("appShell?.removeAttribute(\"inert\")", script, StringComparison.Ordinal);
  }

  [Fact]
  public void Pos_PersistsTheOrderAttemptAndDoesNotRepeatDuplicateSideEffects()
  {
    var page = ReadRepoFile("src/OrionERP.Web/Features/Restaurante/RestaurantPosPage.razor");

    Assert.Contains("new StoredPosDraft { IdempotencyKey = idempotencyKey, Cart = cart }", page, StringComparison.Ordinal);
    Assert.Contains("if (IsValidIdempotencyKey(draft?.IdempotencyKey))", page, StringComparison.Ordinal);
    Assert.Contains("if (catalog is null || cart.Count == 0 || isSubmitting) return;", page, StringComparison.Ordinal);
    Assert.Contains("shouldOpenCashDrawer && !lastOrder.WasDuplicate", page, StringComparison.Ordinal);
    Assert.Contains("OrderService.GetReceiptAsync(CurrentRfc, lastOrder.OrderId)", page, StringComparison.Ordinal);
    Assert.Contains("RestaurantReceiptReprintMapper.Create(recoveredReceipt, catalog)", page, StringComparison.Ordinal);
    Assert.Contains("Esta orden ya se había registrado", page, StringComparison.Ordinal);

    var rotateKey = page.IndexOf("idempotencyKey = Guid.NewGuid().ToString(\"N\");\n      await PersistCartAsync();", StringComparison.Ordinal);
    Assert.True(rotateKey >= 0, "The next order key must be persisted only after the completed attempt is cleared.");
  }

  private static string ReadRepoFile(string relativePath)
    => File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../", relativePath)));
}
