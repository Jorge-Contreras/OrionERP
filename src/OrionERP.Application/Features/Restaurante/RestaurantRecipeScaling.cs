namespace OrionERP.Application.Features.Restaurante;

public static class RestaurantRecipeScaling
{
  public static decimal ScaleQuantity(decimal originalQuantity, decimal originalYield, decimal targetYield)
  {
    if (originalQuantity < 0) throw new ArgumentOutOfRangeException(nameof(originalQuantity));
    if (originalYield <= 0) throw new ArgumentOutOfRangeException(nameof(originalYield));
    if (targetYield <= 0) throw new ArgumentOutOfRangeException(nameof(targetYield));
    return originalQuantity * targetYield / originalYield;
  }
}
