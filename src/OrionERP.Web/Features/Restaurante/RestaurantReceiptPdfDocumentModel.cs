namespace OrionERP.Web.Features.Restaurante;

using OrionERP.Application.Features.Restaurante;

public sealed class RestaurantReceiptPdfDocumentModel
{
  public const string CustomItemSectionName = "Cargo personalizado";

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
  public IReadOnlyList<RestaurantPromotionAdjustmentDto> Promotions { get; init; } = Array.Empty<RestaurantPromotionAdjustmentDto>();
  public string? MembershipNumber { get; init; }
  public int PointsEarned { get; init; }
  public int? PointsBalance { get; init; }
  public IReadOnlyList<RestaurantReceiptPdfLineModel> Lines { get; init; } = Array.Empty<RestaurantReceiptPdfLineModel>();

  public int SectionTicketCount
    => Lines
      .Select(GetTicketSectionName)
      .OfType<string>()
      .Distinct(StringComparer.OrdinalIgnoreCase)
      .Count();

  internal static string? GetTicketSectionName(RestaurantReceiptPdfLineModel line)
    => line.IsCustom
      ? CustomItemSectionName
      : string.IsNullOrWhiteSpace(line.SectionName) ? null : line.SectionName.Trim();
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
