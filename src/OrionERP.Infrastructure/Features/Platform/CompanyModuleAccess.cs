using System.Collections.Frozen;
using Dapper;
using Microsoft.Extensions.Logging;
using OrionERP.Application.Common;
using OrionERP.Application.Features.Platform;

namespace OrionERP.Infrastructure.Features.Platform;

/// <summary>
/// Lee la habilitación de <c>orion.CompanyModule</c> y <c>orion.SiteCapability</c>, que no
/// están bajo RLS, así que responde igual sin contexto de empresa en la conexión. Un módulo
/// cuenta sólo si además tiene al menos una sede activa con la capacidad encendida: sin sede,
/// cada servicio del módulo niega la operación y la pantalla sólo mostraría errores.
/// </summary>
/// <remarks>
/// El resultado se guarda en la instancia con alcance de circuito o de solicitud: la empresa
/// de una sesión no cambia sin volver a iniciar sesión. Los fallos no se guardan.
/// </remarks>
public sealed class CompanyModuleAccess : ICompanyModuleAccess
{
  private const string EnabledModulesSql = """
    SELECT DISTINCT cm.ModuleCode
    FROM orion.Company c
    JOIN orion.CompanyModule cm ON cm.CompanyId = c.CompanyId
    JOIN orion.Module m ON m.ModuleCode = cm.ModuleCode AND m.IsActive = 1
    WHERE c.Rfc = @Rfc AND c.IsActive = 1
      AND cm.[Status] = 'Enabled'
      AND (cm.EffectiveFromUtc IS NULL OR cm.EffectiveFromUtc <= SYSUTCDATETIME())
      AND (cm.EffectiveToUtc IS NULL OR cm.EffectiveToUtc > SYSUTCDATETIME())
      AND EXISTS
      (
        SELECT 1
        FROM orion.SiteCapability sc
        JOIN orion.Site s ON s.CompanyId = sc.CompanyId AND s.SiteId = sc.SiteId
        WHERE sc.CompanyId = c.CompanyId AND sc.ModuleCode = cm.ModuleCode
          AND sc.IsEnabled = 1 AND s.IsActive = 1
      );
    """;

  private static readonly IReadOnlySet<string> CoreOnly = ToSet([PlatformModuleCodes.AccountingCore]);

  private readonly IDbConnectionFactory _connectionFactory;
  private readonly ICurrentCompanyContext? _company;
  private readonly ILogger<CompanyModuleAccess>? _logger;
  private readonly Dictionary<string, IReadOnlySet<string>> _cache = new(StringComparer.Ordinal);

  public CompanyModuleAccess(
    IDbConnectionFactory connectionFactory,
    ICurrentCompanyContext? company = null,
    ILogger<CompanyModuleAccess>? logger = null)
  {
    _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    _company = company;
    _logger = logger;
  }

  public Task<IReadOnlySet<string>> GetEnabledModulesAsync(CancellationToken ct = default)
  {
    string? rfc;
    try
    {
      rfc = _company?.RequireRfc();
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
      // Sin empresa en la sesión no hay módulos que ofrecer.
      return Task.FromResult(CoreOnly);
    }

    return GetEnabledModulesForRfcAsync(rfc ?? string.Empty, ct);
  }

  public async Task<IReadOnlySet<string>> GetEnabledModulesForRfcAsync(string rfc, CancellationToken ct = default)
  {
    var normalizedRfc = Normalize(rfc);
    if (normalizedRfc.Length == 0)
      return CoreOnly;

    lock (_cache)
    {
      if (_cache.TryGetValue(normalizedRfc, out var cached))
        return cached;
    }

    try
    {
      using var connection = _connectionFactory.Create();
      var codes = await connection.QueryAsync<string>(new CommandDefinition(
        EnabledModulesSql,
        new { Rfc = normalizedRfc },
        cancellationToken: ct));
      var enabled = ToSet(codes.Append(PlatformModuleCodes.AccountingCore));
      lock (_cache)
      {
        _cache[normalizedRfc] = enabled;
      }

      return enabled;
    }
    catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
    {
      _logger?.LogWarning(
        ex,
        "No se pudo leer la habilitación de módulos de {Rfc}; la interfaz muestra sólo el núcleo contable.",
        normalizedRfc);
      return CoreOnly;
    }
  }

  public async Task<bool> IsEnabledAsync(string moduleCode, CancellationToken ct = default)
  {
    var normalizedModule = Normalize(moduleCode);
    return normalizedModule.Length > 0
      && (await GetEnabledModulesAsync(ct)).Contains(normalizedModule);
  }

  private static IReadOnlySet<string> ToSet(IEnumerable<string> codes)
    => codes
      .Select(Normalize)
      .Where(code => code.Length > 0)
      .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

  private static string Normalize(string? value)
    => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToUpperInvariant();
}
