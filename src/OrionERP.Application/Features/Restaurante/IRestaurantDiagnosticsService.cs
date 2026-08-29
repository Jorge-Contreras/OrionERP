namespace OrionERP.Application.Features.Restaurante;

/// <summary>
/// Motor de reglas del diagnóstico contable-fiscal del Restaurante y las
/// acciones guiadas que corrigen lo que el diagnóstico encuentra.
/// </summary>
public interface IRestaurantDiagnosticsService
{
  /// <summary>Corre las reglas sobre el periodo y guarda la corrida con sus hallazgos.</summary>
  Task<RestaurantDiagnosticRunDto> RunAsync(
    RestaurantAnalyticsQuery query,
    string userName,
    CancellationToken ct = default);

  /// <summary>Última corrida guardada del RFC y sede, sin recalcular.</summary>
  Task<RestaurantDiagnosticRunDto?> GetLatestRunAsync(
    string rfc,
    int siteId,
    CancellationToken ct = default);

  Task<IReadOnlyList<RestaurantDiagnosticRunDto>> GetHistoryAsync(
    string rfc,
    int siteId,
    int take = 12,
    CancellationToken ct = default);

  /// <summary>Marca un hallazgo como aceptado con la justificación del administrador.</summary>
  Task<RestaurantCommandResult> AcceptFindingAsync(
    string rfc,
    long findingId,
    string justificacion,
    string userName,
    CancellationToken ct = default);

  /// <summary>Cuentas de detalle que el restaurante necesita y no existen en el catálogo del RFC.</summary>
  Task<IReadOnlyList<RestaurantMissingAccountDto>> GetMissingAccountsAsync(
    string rfc,
    CancellationToken ct = default);

  /// <summary>Da de alta las cuentas de detalle seleccionadas. Acción guiada A1.</summary>
  Task<RestaurantCommandResult> CreateMissingAccountsAsync(
    string rfc,
    IReadOnlyList<RestaurantMissingAccountDto> accounts,
    string userName,
    CancellationToken ct = default);

  /// <summary>Genera las pólizas diarias faltantes del rango, día por día. Acción guiada A3.</summary>
  Task<RestaurantPolicyBackfillResultDto> BackfillDailyPoliciesAsync(
    RestaurantAnalyticsQuery query,
    string userName,
    CancellationToken ct = default);
}
