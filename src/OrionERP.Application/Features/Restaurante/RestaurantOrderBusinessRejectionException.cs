namespace OrionERP.Application.Features.Restaurante;

/// <summary>
/// Identifies a deterministic restaurant rule rejection. Public paid orders cannot be
/// retried into success after one of these failures and must enter compensating-refund recovery.
/// </summary>
public class RestaurantOrderBusinessRejectionException : InvalidOperationException
{
  public RestaurantOrderBusinessRejectionException(
    RestaurantOrderRejectionCategory category,
    string message)
    : base(message)
  {
    Category = category;
  }

  public RestaurantOrderBusinessRejectionException(
    RestaurantOrderRejectionCategory category,
    string message,
    Exception innerException)
    : base(message, innerException)
  {
    Category = category;
  }

  public RestaurantOrderRejectionCategory Category { get; }
}

public enum RestaurantOrderRejectionCategory
{
  Availability,
  Product,
  Modifier,
  Combo,
  Promotion,
  PromotionCode,
  Member,
  Inventory,
  Pricing
}
