namespace OrionERP.UnitTests.Logistica;

public sealed class PurchaseReceiptAmountsSqlTests
{
  [Fact]
  public void Migration_AddsAuditableReceiptAmountColumnsIdempotently()
  {
    var sql = ReadRepoFile("src/OrionERP.Infrastructure/Features/Logistica/Sql/20260829_purchasing_receipt_amounts.sql");

    Assert.Contains("COL_LENGTH('logistica.PurchaseReceiptLine', 'SubtotalAmount') IS NULL", sql, StringComparison.Ordinal);
    Assert.Contains("COL_LENGTH('logistica.PurchaseReceiptLine', 'IvaAmount') IS NULL", sql, StringComparison.Ordinal);
    Assert.Contains("COL_LENGTH('logistica.PurchaseReceiptLine', 'TotalAmount') IS NULL", sql, StringComparison.Ordinal);
    Assert.Contains("COL_LENGTH('logistica.PurchaseReceiptLine', 'IncludesIva') IS NULL", sql, StringComparison.Ordinal);
    Assert.Contains("CK_PurchaseReceiptLine_ReceiptAmounts", sql, StringComparison.Ordinal);
    Assert.Contains("SubtotalAmount + IvaAmount = TotalAmount", sql, StringComparison.Ordinal);
  }

  private static string ReadRepoFile(string relativePath)
  {
    var current = new DirectoryInfo(AppContext.BaseDirectory);
    while (current is not null && !File.Exists(Path.Combine(current.FullName, "OrionERP.sln")))
    {
      current = current.Parent;
    }

    Assert.NotNull(current);
    return File.ReadAllText(Path.Combine(current!.FullName, relativePath));
  }
}
