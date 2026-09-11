using System.Data;
using System.Data.Common;
using Dapper;
using OrionERP.Application.Common;
using OrionERP.Application.Features.Platform;
using OrionERP.Application.Features.Restaurante;

namespace OrionERP.Infrastructure.Features.Restaurante;

/// <summary>
/// Resolves the explicit relationship between the platform site and the
/// module-local Restaurant site. Textual site codes are never authority.
/// </summary>
public sealed class RestaurantScopeAccessor : IRestaurantScopeAccessor
{
  private const string Module = PlatformModuleCodes.Restaurant;

  private const string EnabledSiteSql = """
    SELECT c.CompanyId, s.SiteId, legacy.Id AS LegacySiteId, c.Rfc AS CompanyRfc
    FROM orion.Company c
    JOIN orion.Site s ON s.CompanyId = c.CompanyId
    JOIN orion.CompanyModule cm ON cm.CompanyId = c.CompanyId AND cm.ModuleCode = @Module
    JOIN orion.Module m ON m.ModuleCode = cm.ModuleCode AND m.IsActive = 1
    JOIN orion.SiteCapability sc
      ON sc.CompanyId = c.CompanyId AND sc.SiteId = s.SiteId AND sc.ModuleCode = cm.ModuleCode
    JOIN restaurante.Site legacy
      ON legacy.OrionCompanyId = c.CompanyId AND legacy.OrionSiteId = s.SiteId
    WHERE c.Rfc = @Rfc AND c.IsActive = 1 AND s.IsActive = 1 AND sc.IsEnabled = 1
      AND cm.[Status] = 'Enabled'
      AND (cm.EffectiveFromUtc IS NULL OR cm.EffectiveFromUtc <= SYSUTCDATETIME())
      AND (cm.EffectiveToUtc IS NULL OR cm.EffectiveToUtc > SYSUTCDATETIME())
      AND legacy.Id = @LegacySiteId;
    """;

  private readonly IDbConnectionFactory _connectionFactory;
  private readonly ICurrentCompanyContext _company;

  public RestaurantScopeAccessor(IDbConnectionFactory connectionFactory, ICurrentCompanyContext company)
  {
    _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    _company = company ?? throw new ArgumentNullException(nameof(company));
  }

  public async Task<RestaurantScope> ResolveRequiredAsync(string rfc, int legacySiteId, CancellationToken ct = default)
  {
    var normalizedRfc = Normalize(rfc);
    if (normalizedRfc.Length == 0 || legacySiteId <= 0) throw Denied();
    await using var connection = await OpenAsync(ct);
    var scope = await connection.QuerySingleOrDefaultAsync<ScopeRow>(new CommandDefinition(
      EnabledSiteSql,
      new { Rfc = normalizedRfc, Module, LegacySiteId = legacySiteId },
      cancellationToken: ct));
    if (scope is null) throw Denied();
    // Una sede de otra empresa no pasa aunque el módulo esté habilitado allá.
    _company.EnsureRfc(scope.CompanyRfc);
    return new RestaurantScope(scope.CompanyId, scope.SiteId, scope.LegacySiteId, scope.CompanyRfc);
  }

  /// <summary>
  /// Mismo predicado que <see cref="EnsureStillEnabledAsync"/>, para incrustarlo
  /// en un lote que abre su propia transacción. Sus parámetros llevan prefijo
  /// <c>@Scope</c> porque los lotes anfitriones ya usan <c>@SiteId</c> para la sede
  /// legada. HOLDLOCK retiene la habilitación dentro de la transacción del
  /// llamador: una suspensión concurrente espera al commit en vez de colarse entre
  /// la lectura y la escritura.
  /// </summary>
  public const string EnsureEnabledSql = """
    IF NOT EXISTS
    (
      SELECT 1
      FROM orion.Company c WITH (HOLDLOCK)
      JOIN orion.Site s WITH (HOLDLOCK) ON s.CompanyId = c.CompanyId
      JOIN orion.CompanyModule cm WITH (HOLDLOCK)
        ON cm.CompanyId = c.CompanyId AND cm.ModuleCode = @ScopeModule
      JOIN orion.Module m WITH (HOLDLOCK) ON m.ModuleCode = cm.ModuleCode AND m.IsActive = 1
      JOIN orion.SiteCapability sc WITH (HOLDLOCK)
        ON sc.CompanyId = c.CompanyId AND sc.SiteId = s.SiteId AND sc.ModuleCode = cm.ModuleCode
      JOIN restaurante.Site legacy WITH (HOLDLOCK)
        ON legacy.OrionCompanyId = c.CompanyId AND legacy.OrionSiteId = s.SiteId
      WHERE c.CompanyId = @ScopeCompanyId AND s.SiteId = @ScopePlatformSiteId
        AND legacy.Id = @ScopeLegacySiteId AND c.Rfc = @ScopeCompanyRfc
        AND c.IsActive = 1 AND s.IsActive = 1 AND sc.IsEnabled = 1
        AND cm.[Status] = 'Enabled'
        AND (cm.EffectiveFromUtc IS NULL OR cm.EffectiveFromUtc <= SYSUTCDATETIME())
        AND (cm.EffectiveToUtc IS NULL OR cm.EffectiveToUtc > SYSUTCDATETIME())
    )
      THROW 51940,'El modulo Restaurante no esta habilitado para la empresa y sede autorizadas.',1;
    """;

  /// <summary>Parámetros de <see cref="EnsureEnabledSql"/> para un alcance dado.</summary>
  public static object EnsureEnabledParameters(RestaurantScope scope)
  {
    ArgumentNullException.ThrowIfNull(scope);
    return new
    {
      ScopeCompanyId = scope.CompanyId,
      ScopePlatformSiteId = scope.SiteId,
      ScopeLegacySiteId = scope.LegacySiteId,
      ScopeCompanyRfc = scope.CompanyRfc,
      ScopeModule = Module
    };
  }

  public Task EnsureStillEnabledAsync(
    DbConnection connection,
    DbTransaction? transaction,
    RestaurantScope scope,
    CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(connection);
    return connection.ExecuteAsync(new CommandDefinition(
      EnsureEnabledSql, EnsureEnabledParameters(scope), transaction, cancellationToken: ct));
  }

  public async Task<IReadOnlySet<string>> GetEnabledCompanyRfcsAsync(
    IReadOnlyCollection<string> rfcs,
    CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(rfcs);
    var normalized = rfcs.Select(Normalize).Where(value => value.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
    if (normalized.Length == 0) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    await using var connection = await OpenAsync(ct);
    // Sin SiteCapability a propósito: la sede legada vive en restaurante.Site, que
    // sí está bajo RLS y no es legible sin contexto de empresa. La suspensión de
    // empresa sí es evaluable aquí, y es la que este trabajo debe respetar.
    var enabled = await connection.QueryAsync<string>(new CommandDefinition("""
      SELECT c.Rfc
      FROM orion.Company c
      JOIN orion.CompanyModule cm ON cm.CompanyId = c.CompanyId AND cm.ModuleCode = @Module
      JOIN orion.Module m ON m.ModuleCode = cm.ModuleCode AND m.IsActive = 1
      WHERE c.Rfc IN @Rfcs AND c.IsActive = 1
        AND cm.[Status] = 'Enabled'
        AND (cm.EffectiveFromUtc IS NULL OR cm.EffectiveFromUtc <= SYSUTCDATETIME())
        AND (cm.EffectiveToUtc IS NULL OR cm.EffectiveToUtc > SYSUTCDATETIME());
      """, new { Rfcs = normalized, Module }, cancellationToken: ct));
    return enabled.ToHashSet(StringComparer.OrdinalIgnoreCase);
  }

  private async Task<DbConnection> OpenAsync(CancellationToken ct)
  {
    var connection = _connectionFactory.Create() as DbConnection
      ?? throw new InvalidOperationException("Restaurante requiere una conexión de base de datos.");
    try
    {
      if (connection.State != ConnectionState.Open) await connection.OpenAsync(ct);
      return connection;
    }
    catch { await connection.DisposeAsync(); throw; }
  }

  private static UnauthorizedAccessException Denied()
    => new("El módulo Restaurante no está habilitado para la empresa y sede solicitadas.");

  private static string Normalize(string? value)
    => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToUpperInvariant();

  private sealed class ScopeRow
  {
    public long CompanyId { get; set; }
    public long SiteId { get; set; }
    public int LegacySiteId { get; set; }
    public string CompanyRfc { get; set; } = "";
  }
}
