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

    return Classify(order.Lines);
  }

  public static RestaurantKitchenProgress Classify(IEnumerable<RestaurantOrderLineDto> lines)
  {
    ArgumentNullException.ThrowIfNull(lines);

    var activeLines = lines
      .Where(line => line.Status != "Cancelled")
      .ToList();
    if (activeLines.Count == 0)
    {
      return RestaurantKitchenProgress.NotStarted;
    }
    if (activeLines.All(line => line.Status is "Ready" or "Delivered"))
    {
      return RestaurantKitchenProgress.Ready;
    }

    return activeLines.Any(line => line.Status is "Preparing" or "Ready" or "Delivered")
      ? RestaurantKitchenProgress.Preparing
      : RestaurantKitchenProgress.NotStarted;
  }
}
