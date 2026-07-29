namespace OrionERP.Web.Features.Restaurante;

public sealed class RestaurantReceiptPdfDocumentModel
{
  public string SiteName { get; init; } = string.Empty;
  public int Folio { get; init; }
  public string CustomerName { get; init; } = string.Empty;
  public string OrderType { get; init; } = string.Empty;
  public string? TableName { get; init; }
  public DateTimeOffset CreatedAt { get; init; }
  public string? OrderNotes { get; init; }
  public decimal Subtotal { get; init; }
  public decimal DiscountTotal { get; init; }
  public decimal SubtotalBeforeTax { get; init; }
  public decimal Tax { get; init; }
  public decimal TaxRate { get; init; }
  public bool PricesIncludeTax { get; init; }
  public decimal Delivery { get; init; }
  public decimal Total { get; init; }
  public decimal Tip { get; init; }
  public decimal CashReceived { get; init; }
  public decimal CardAmount { get; init; }
  public decimal TransferAmount { get; init; }
  public decimal Change { get; init; }
  public decimal BalanceDue { get; init; }
  public IReadOnlyList<RestaurantReceiptPdfLineModel> Lines { get; init; } = Array.Empty<RestaurantReceiptPdfLineModel>();

  public int SectionTicketCount
    => Lines
      .Where(line => !line.IsCustom && !string.IsNullOrWhiteSpace(line.SectionName))
      .Select(line => line.SectionName!.Trim())
      .Distinct(StringComparer.OrdinalIgnoreCase)
      .Count();
}

public sealed class RestaurantReceiptPdfLineModel
{
  public string ProductName { get; init; } = string.Empty;
  public decimal Quantity { get; init; }
  public decimal UnitPrice { get; init; }
  public decimal DiscountAmount { get; init; }
  public string? Notes { get; init; }
  public IReadOnlyList<string> Modifiers { get; init; } = Array.Empty<string>();
  public bool IsCustom { get; init; }
  public string? SectionName { get; init; }
  public int SectionSortOrder { get; init; }
}
