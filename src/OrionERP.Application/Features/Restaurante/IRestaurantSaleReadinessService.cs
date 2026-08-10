namespace OrionERP.Application.Features.Restaurante;

public interface IRestaurantSaleReadinessService
{
  Task<RestaurantSaleReadinessReport> AnalyzeAsync(
    string rfc,
    int siteId,
    DateTimeOffset at,
    CancellationToken ct = default);
}

public interface IRestaurantSaleReadinessWorkbookService
{
  RestaurantSaleReadinessWorkbook Create(RestaurantSaleReadinessReport report);
}

