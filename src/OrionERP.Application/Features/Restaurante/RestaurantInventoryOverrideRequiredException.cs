namespace OrionERP.Application.Features.Restaurante;

public sealed class RestaurantInventoryOverrideRequiredException : RestaurantOrderBusinessRejectionException
{
  public RestaurantInventoryOverrideRequiredException(string message)
    : base(RestaurantOrderRejectionCategory.Inventory, message)
  {
  }
}
