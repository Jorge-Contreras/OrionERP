namespace OrionERP.Application.Features.Restaurante;

/// <summary>
/// Reportes contables del Restaurante construidos sobre los códigos agrupadores
/// nivel 1 del SAT. Todo importe que devuelve este servicio viaja acompañado de
/// los agrupadores que lo componen.
/// </summary>
public interface IRestaurantAnalyticsService
{
  Task<RestaurantAccountingReportDto> GetAccountingReportAsync(
    RestaurantAnalyticsQuery query,
    CancellationToken ct = default);

  /// <summary>Mapeo concepto ↔ agrupador del RFC, con el importe de cada código en el periodo.</summary>
  Task<RestaurantAgrupadorMapDto> GetAgrupadorMapAsync(
    RestaurantAnalyticsQuery query,
    CancellationToken ct = default);

  Task<RestaurantCommandResult> SaveAgrupadorMapRowAsync(
    string rfc,
    RestaurantAgrupadorMapRowDto row,
    string userName,
    CancellationToken ct = default);

  Task<RestaurantCommandResult> DeleteAgrupadorMapRowAsync(
    string rfc,
    int id,
    string userName,
    CancellationToken ct = default);

  Task<RestaurantCommandResult> ResetAgrupadorMapAsync(
    string rfc,
    string userName,
    CancellationToken ct = default);

  /// <summary>Desglose de un agrupador: nivel 2 cuando no se indica, nivel 3 cuando sí.</summary>
  Task<IReadOnlyList<RestaurantLedgerNodeDto>> GetLedgerBreakdownAsync(
    RestaurantAnalyticsQuery query,
    string nivel1,
    string? nivel2,
    CancellationToken ct = default);

  /// <summary>Movimientos con su póliza para una cuenta de detalle.</summary>
  Task<IReadOnlyList<RestaurantLedgerEntryDto>> GetLedgerEntriesAsync(
    RestaurantAnalyticsQuery query,
    string nivel1,
    string? nivel2,
    string? nivel3,
    CancellationToken ct = default);

  /// <summary>Costo recalculado desde la receta activa para los productos vendidos en el periodo.</summary>
  Task<IReadOnlyList<RestaurantRecipeCostDto>> GetRecipeCostsAsync(
    RestaurantAnalyticsQuery query,
    CancellationToken ct = default);

  /// <summary>Códigos agrupadores nivel 1 disponibles en el catálogo del RFC.</summary>
  Task<IReadOnlyList<RestaurantAgrupadorDto>> GetAvailableAgrupadoresAsync(
    string rfc,
    CancellationToken ct = default);
}
