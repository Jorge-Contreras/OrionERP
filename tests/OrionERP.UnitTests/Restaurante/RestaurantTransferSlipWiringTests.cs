namespace OrionERP.UnitTests.Restaurante;

public sealed class RestaurantTransferSlipWiringTests
{
  [Fact]
  public void PosPage_PrintsTheSpeiSlipFromAButtonNextToTheTransferField()
  {
    var source = ReadRepoFile("src/OrionERP.Web/Features/Restaurante/RestaurantPosPage.razor");
    var transferField = source.IndexOf("<label>Transferencia <input", StringComparison.Ordinal);
    var printButton = source.IndexOf("PrintTransferDetailsAsync", transferField, StringComparison.Ordinal);
    var tipField = source.IndexOf("<label>Propina <input", transferField, StringComparison.Ordinal);

    Assert.True(transferField >= 0);
    // El botón vive dentro de la celda de Transferencia, antes del siguiente campo.
    Assert.InRange(printButton, transferField, tipField - 1);
    Assert.Contains("disabled=\"@(!CanPrintTransferDetails)\"", source, StringComparison.Ordinal);
    Assert.Contains("restaurantUi.printPdf", source, StringComparison.Ordinal);
    Assert.Contains("ReceiptPdfService.GenerateTransferSlip(slip)", source, StringComparison.Ordinal);
    Assert.Contains("RestaurantTransferSlipDocumentModel.FromSite(", source, StringComparison.Ordinal);
  }

  [Fact]
  public void PosPage_KeepsTheButtonDisabledUntilTheSiteHasBankData()
  {
    var source = ReadRepoFile("src/OrionERP.Web/Features/Restaurante/RestaurantPosPage.razor");

    Assert.Contains(
      "private bool HasTransferPaymentDetails => catalog?.Site.HasTransferPaymentDetails == true;",
      source,
      StringComparison.Ordinal);
    Assert.Contains(
      "private bool CanPrintTransferDetails => HasTransferPaymentDetails && !isPrintingTransferSlip;",
      source,
      StringComparison.Ordinal);
    Assert.Contains("Restaurante › Administración › Sedes.", source, StringComparison.Ordinal);
    // Una impresión fallida no puede tumbar el cobro en curso.
    Assert.Contains("transferSlipWarning = $\"No se pudieron imprimir los datos de transferencia.", source, StringComparison.Ordinal);
  }

  [Fact]
  public void AdminPage_EditsAndPersistsTheTransferFieldsOfTheSite()
  {
    var source = ReadRepoFile("src/OrionERP.Web/Features/Restaurante/RestaurantAdminPage.razor");

    Assert.Contains("Transferencia electrónica (SPEI)", source, StringComparison.Ordinal);
    foreach (var field in new[]
             {
               "TransferAccountHolder", "TransferBankName", "TransferAccountNumber",
               "TransferClabe", "TransferCardNumber", "TransferInstructions"
             })
    {
      Assert.Contains($"@bind-Value=\"siteEditor.{field}\"", source, StringComparison.Ordinal);
      Assert.Contains($"{field} = site.{field}", source, StringComparison.Ordinal);
    }
  }

  [Fact]
  public void CatalogService_ReadsAndWritesTheTransferColumnsNormalized()
  {
    var source = ReadRepoFile("src/OrionERP.Infrastructure/Features/Restaurante/RestaurantCatalogService.cs");
    var siteQueries = source[..source.IndexOf("GetProductsAsync", StringComparison.Ordinal)];

    Assert.Contains("TransferBankName, TransferAccountHolder, TransferAccountNumber", siteQueries, StringComparison.Ordinal);
    Assert.Contains("TransferClabe = @TransferClabe", siteQueries, StringComparison.Ordinal);
    Assert.Contains(
      "TransferClabe = RestaurantTransferPaymentRules.NormalizeDigits(request.TransferClabe)",
      siteQueries,
      StringComparison.Ordinal);
    Assert.Contains(
      "TransferAccountHolder = RestaurantTransferPaymentRules.NormalizeText(request.TransferAccountHolder)",
      siteQueries,
      StringComparison.Ordinal);
  }

  [Fact]
  public void Migration_RequiresExplicitDatabaseAndApplyModeAndSupportsDryRun()
  {
    var sql = ReadMigration();

    Assert.Contains("DECLARE @ExpectedDatabase sysname = N'$(ExpectedDatabase)'", sql, StringComparison.Ordinal);
    Assert.Contains("DECLARE @ApplyChangesText nvarchar(10) = N'$(ApplyChanges)'", sql, StringComparison.Ordinal);
    Assert.Contains("IF @ApplyChangesText NOT IN (N'0', N'1')", sql, StringComparison.Ordinal);
    Assert.Contains("IF DB_NAME() <> @ExpectedDatabase", sql, StringComparison.Ordinal);
    Assert.Contains("BEGIN TRANSACTION", sql, StringComparison.Ordinal);
    Assert.Contains("IF @ApplyChanges = 1", sql, StringComparison.Ordinal);
    Assert.Contains("COMMIT TRANSACTION", sql, StringComparison.Ordinal);
    Assert.Contains("ROLLBACK TRANSACTION", sql, StringComparison.Ordinal);
    Assert.Contains("'DRY_RUN_VALIDATED'", sql, StringComparison.Ordinal);
  }

  [Fact]
  public void Migration_IsIdempotentAndGuardsTheStoredBankData()
  {
    var sql = ReadMigration();

    foreach (var column in new[]
             {
               "TransferBankName", "TransferAccountHolder", "TransferAccountNumber",
               "TransferClabe", "TransferCardNumber", "TransferInstructions"
             })
    {
      Assert.Contains($"COL_LENGTH('restaurante.Site', '{column}') IS NULL", sql, StringComparison.Ordinal);
    }

    Assert.Contains("CK_RestaurantSite_TransferClabe", sql, StringComparison.Ordinal);
    Assert.Contains("CK_RestaurantSite_TransferCard", sql, StringComparison.Ordinal);
    Assert.Contains("CK_RestaurantSite_TransferAccount", sql, StringComparison.Ordinal);
    Assert.Contains("CK_RestaurantSite_TransferHolder", sql, StringComparison.Ordinal);
    // Sólo dígitos: el formato de impresión se aplica en la aplicación.
    Assert.Contains("LEN(TransferClabe) = 18", sql, StringComparison.Ordinal);
    Assert.Contains("TransferClabe NOT LIKE ''%[^0-9]%''", sql, StringComparison.Ordinal);
    Assert.Contains("LEN(TransferCardNumber) BETWEEN 15 AND 19", sql, StringComparison.Ordinal);
  }

  private static string ReadMigration()
    => ReadRepoFile(
      "src/OrionERP.Infrastructure/Features/Restaurante/Sql/20260902_restaurant_transfer_payment_details.sql");

  private static string ReadRepoFile(string relativePath)
    => File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../", relativePath)));
}
