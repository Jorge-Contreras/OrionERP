namespace OrionERP.Application.Features.Restaurante;

public static class RestaurantCashShiftPaymentSummaryRules
{
  public static IReadOnlyList<RestaurantCashShiftPaymentSummaryDto> Combine(
    IEnumerable<RestaurantCashShiftPaymentSummaryDto> summaries)
  {
    ArgumentNullException.ThrowIfNull(summaries);

    return summaries
      .GroupBy(summary => NormalizeMethod(summary.PaymentMethod), StringComparer.OrdinalIgnoreCase)
      .Select(group => new RestaurantCashShiftPaymentSummaryDto
      {
        PaymentMethod = group.Key,
        PaymentCount = group.Sum(summary => summary.PaymentCount),
        RefundCount = group.Sum(summary => summary.RefundCount),
        CancellationCount = group.Sum(summary => summary.CancellationCount),
        Sales = group.Sum(summary => summary.Sales),
        Tips = group.Sum(summary => summary.Tips),
        Refunds = group.Sum(summary => summary.Refunds),
        Cancellations = group.Sum(summary => summary.Cancellations)
      })
      .OrderBy(summary => SortOrder(summary.PaymentMethod))
      .ThenBy(summary => summary.PaymentMethod, StringComparer.OrdinalIgnoreCase)
      .ToList();
  }

  public static string NormalizeMethod(string? method)
  {
    var normalized = method?.Trim();
    if (string.IsNullOrWhiteSpace(normalized))
    {
      return "Sin especificar";
    }

    return normalized.ToLowerInvariant() switch
    {
      "cash" or "efectivo" => "Cash",
      "card" or "externalcard" or "tarjeta" => "ExternalCard",
      "transfer" or "transferencia" => "Transfer",
      "platform" or "deliveryprovider" or "plataforma" => "Platform",
      _ => normalized
    };
  }

  private static int SortOrder(string method)
    => method switch
    {
      "Cash" => 0,
      "ExternalCard" => 1,
      "Transfer" => 2,
      "Platform" => 3,
      _ => 4
    };
}
