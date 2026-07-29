namespace OrionERP.Application.Features.Restaurante;

public enum RestaurantKitchenProgress
{
  NotStarted,
  Preparing,
  Ready
}

public static class RestaurantKitchenProgressRules
{
  public static RestaurantKitchenProgress Classify(RestaurantOrderDto order)
  {
    ArgumentNullException.ThrowIfNull(order);

    var activeLines = order.Lines
      .Where(line => line.Status != "Cancelled")
      .ToList();
    var kitchenLines = activeLines.Where(line => !line.IsCustom).ToList();
    if (kitchenLines.Count == 0)
    {
      return activeLines.Count > 0 && activeLines.All(line => line.Status is "Ready" or "Delivered")
        ? RestaurantKitchenProgress.Ready
        : RestaurantKitchenProgress.NotStarted;
    }
    if (kitchenLines.All(line => line.Status is "Ready" or "Delivered"))
    {
      return RestaurantKitchenProgress.Ready;
    }

    return kitchenLines.Any(line => line.Status is "Preparing" or "Ready" or "Delivered")
      ? RestaurantKitchenProgress.Preparing
      : RestaurantKitchenProgress.NotStarted;
  }
}
