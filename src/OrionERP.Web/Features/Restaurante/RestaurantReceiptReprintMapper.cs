namespace OrionERP.Web.Features.Restaurante;

using OrionERP.Application.Features.Restaurante;

public static class RestaurantReceiptReprintMapper
{
  public static RestaurantReceiptPdfDocumentModel Create(
    RestaurantReceiptDto receipt,
    RestaurantPosCatalogDto catalog)
  {
    ArgumentNullException.ThrowIfNull(receipt);
    ArgumentNullException.ThrowIfNull(catalog);

    var sectionByProduct = catalog.Sections
      .SelectMany(section => section.Products.Select(product => new { product.Id, Section = section }))
      .GroupBy(item => item.Id)
      .ToDictionary(group => group.Key, group => group.First().Section);

    var lines = receipt.Lines.Select(line =>
    {
      RestaurantMenuSectionDto? section = null;
      if (!line.IsCustom && line.ProductId.HasValue)
      {
        sectionByProduct.TryGetValue(line.ProductId.Value, out section);
      }

      return new RestaurantReceiptPdfLineModel
      {
        ProductName = line.ProductName,
        Quantity = line.Quantity,
        UnitPrice = line.UnitPrice,
        BaseUnitPrice = line.BaseUnitPrice,
        ChoicePriceDelta = line.ChoicePriceDelta,
        DiscountAmount = line.DiscountAmount,
        Notes = line.Notes,
        Modifiers = line.Modifiers,
        StructuredModifiers = line.StructuredModifiers,
        IsCustom = line.IsCustom,
        LineKind = line.LineKind,
        ParentOrderLineId = line.ParentOrderLineId,
        ParentProductName = line.ParentProductName,
        ComboSlotName = line.ComboSlotName,
        SectionName = line.MenuSectionName ?? section?.Name,
        SectionSortOrder = line.MenuSectionSortOrder ?? section?.SortOrder ?? int.MaxValue
      };
    }).ToList();

    var merchandiseSubtotal = decimal.Round(
      lines
        .Where(line => line.LineKind != RestaurantOrderLineKinds.ComboComponent)
        .Sum(line => line.UnitPrice * line.Quantity),
      2,
      MidpointRounding.AwayFromZero);
    var subtotalBeforeTax = decimal.Round(
      Math.Max(0, receipt.Total - receipt.DeliveryCost - receipt.TaxTotal),
      2,
      MidpointRounding.AwayFromZero);

    return new RestaurantReceiptPdfDocumentModel
    {
      IsReprint = true,
      SiteName = receipt.SiteName,
      Folio = receipt.Folio,
      CustomerName = receipt.CustomerName ?? string.Empty,
      OrderType = receipt.OrderType,
      TableName = receipt.TableName,
      CreatedAt = ToSiteTime(receipt.CreatedAt, receipt.SiteTimeZoneId),
      OrderNotes = receipt.Notes,
      Subtotal = merchandiseSubtotal,
      DiscountTotal = receipt.DiscountTotal,
      SubtotalBeforeTax = subtotalBeforeTax,
      Tax = receipt.TaxTotal,
      TaxRate = receipt.TaxRate,
      PricesIncludeTax = receipt.PricesIncludeTax,
      Delivery = receipt.DeliveryCost,
      Total = receipt.Total,
      Tip = receipt.TipTotal,
      CashReceived = NetPaymentTotal(receipt.Payments, "Cash"),
      CardAmount = NetPaymentTotal(receipt.Payments, "ExternalCard"),
      TransferAmount = NetPaymentTotal(receipt.Payments, "Transfer"),
      PlatformAmount = NetPaymentTotal(receipt.Payments, "Platform"),
      BalanceDue = receipt.BalanceDue,
      Promotions = receipt.Promotions,
      MembershipNumber = receipt.MembershipNumber,
      PointsEarned = receipt.PointsEarned,
      PointsRedeemed = receipt.PointsRedeemed,
      RedemptionValue = receipt.RedemptionValue,
      PointsBalance = receipt.PointsBalance,
      Lines = lines
    };
  }

  private static decimal NetPaymentTotal(IEnumerable<RestaurantPaymentDto> payments, string method)
    => decimal.Round(
      payments
        .Where(payment => string.Equals(payment.PaymentMethod, method, StringComparison.OrdinalIgnoreCase))
        .Sum(payment => Math.Max(0, payment.Amount - payment.RefundedAmount)),
      2,
      MidpointRounding.AwayFromZero);

  private static DateTimeOffset ToSiteTime(DateTime createdAt, string timeZoneId)
  {
    var utc = new DateTimeOffset(DateTime.SpecifyKind(createdAt, DateTimeKind.Utc));
    try
    {
      return TimeZoneInfo.ConvertTime(utc, TimeZoneInfo.FindSystemTimeZoneById(timeZoneId));
    }
    catch (TimeZoneNotFoundException)
    {
      return utc.ToLocalTime();
    }
    catch (InvalidTimeZoneException)
    {
      return utc.ToLocalTime();
    }
  }
}
