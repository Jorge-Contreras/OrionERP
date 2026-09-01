namespace OrionERP.Application.Features.Restaurante;

public sealed class RestaurantInventoryOverrideRequiredException : InvalidOperationException
{
  public RestaurantInventoryOverrideRequiredException(string message)
    : base(message)
  {
  }
}
