using System.Data.Common;

namespace OrionERP.Application.Features.Restaurante;

/// <summary>
/// Empresa y sede con el módulo Restaurante habilitado. <see cref="LegacySiteId"/>
/// es <c>restaurante.Site.Id</c>, el identificador que reciben los servicios;
/// <see cref="SiteId"/> es <c>orion.Site.SiteId</c>, donde vive la autoridad de
/// habilitación. Los dos no son intercambiables.
/// </summary>
public sealed record RestaurantScope(long CompanyId, long SiteId, int LegacySiteId, string CompanyRfc);

/// <summary>
/// Autoridad de habilitación de Restaurante: <c>orion.CompanyModule</c> y
/// <c>orion.SiteCapability</c>. Ni el menú ni <c>restaurante.Site.IsEnabled</c>
/// autorizan. Suspender un módulo niega la operación; no escribe ni borra datos.
/// </summary>
public interface IRestaurantScopeAccessor
{
  /// <summary>
  /// Resuelve la sede legada contra la empresa de la sesión. Falla si el módulo
  /// está suspendido, fuera de vigencia, o si la sede es ajena a la sesión.
  /// </summary>
  Task<RestaurantScope> ResolveRequiredAsync(string rfc, int legacySiteId, CancellationToken ct = default);

  /// <summary>
  /// Revalida el mismo alcance sobre la conexión y transacción del llamador, antes
  /// de mutar. Un cierre concurrente del módulo no alcanza a colarse.
  /// </summary>
  Task EnsureStillEnabledAsync(
    DbConnection connection,
    DbTransaction? transaction,
    RestaurantScope scope,
    CancellationToken ct = default);

  /// <summary>
  /// Sondeo sin sesión, para trabajos de fondo: de los RFC recibidos, cuáles tienen
  /// el módulo habilitado a nivel empresa. Sólo consulta <c>orion.*</c>, que no está
  /// bajo RLS, así que se evalúa igual sin contexto de empresa en la conexión.
  /// </summary>
  Task<IReadOnlySet<string>> GetEnabledCompanyRfcsAsync(
    IReadOnlyCollection<string> rfcs,
    CancellationToken ct = default);
}
