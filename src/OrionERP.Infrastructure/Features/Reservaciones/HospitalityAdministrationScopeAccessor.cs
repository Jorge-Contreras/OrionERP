using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using OrionERP.Application.Common;
using OrionERP.Application.Features.Reservaciones;

namespace OrionERP.Infrastructure.Features.Reservaciones;

public sealed class HospitalityAdministrationScopeAccessor(
  IConfiguration configuration,
  ICurrentCompanyContext company,
  HospitalitySiteSelection selection,
  HospitalityAdministrationSessionGuard? sessionGuard = null) : IHospitalityScopeAccessor
{
  private const string SitesSql = """
    SELECT c.CompanyId, s.SiteId, c.Rfc AS CompanyRfc, s.DisplayName
    FROM orion.Company c
    JOIN orion.Site s ON s.CompanyId = c.CompanyId
    JOIN orion.CompanyModule cm ON cm.CompanyId = c.CompanyId AND cm.ModuleCode = 'HOSPITALITY'
    JOIN orion.Module m ON m.ModuleCode = cm.ModuleCode AND m.IsActive = 1
    JOIN orion.SiteCapability sc ON sc.CompanyId = c.CompanyId AND sc.SiteId = s.SiteId AND sc.ModuleCode = cm.ModuleCode
    WHERE c.Rfc = @Rfc AND c.IsActive = 1 AND s.IsActive = 1 AND sc.IsEnabled = 1
      AND cm.Status = 'Enabled'
      AND (cm.EffectiveFromUtc IS NULL OR cm.EffectiveFromUtc <= SYSUTCDATETIME())
      AND (cm.EffectiveToUtc IS NULL OR cm.EffectiveToUtc > SYSUTCDATETIME())
    ORDER BY s.DisplayName, s.SiteId;
    """;

  public async Task<IReadOnlyList<HospitalitySiteOption>> GetSitesAsync(CancellationToken ct = default)
    => (await LoadAsync(ct)).Select(row => new HospitalitySiteOption(row.SiteId, row.DisplayName)).ToArray();

  public async Task<HospitalityScope> ResolveRequiredAsync(CancellationToken ct = default)
  {
    var sites = await LoadAsync(ct);
    var selected = selection.SiteId;
    var site = selected.HasValue ? sites.SingleOrDefault(row => row.SiteId == selected)
      : sites.Count == 1 ? sites[0] : null;
    if (site is null)
      throw new UnauthorizedAccessException("Selecciona una sede de Hospedaje habilitada para la empresa de tu sesión.");
    company.EnsureRfc(site.CompanyRfc);
    return new HospitalityScope(site.CompanyId, site.SiteId, site.CompanyRfc);
  }

  private async Task<List<ScopeRow>> LoadAsync(CancellationToken ct)
  {
    var rfc = await (sessionGuard ?? throw new UnauthorizedAccessException("Falta validar la sesión de Hospedaje.")).RequireCompanyRfcAsync(ct);
    await using var connection = new SqlConnection(configuration.GetConnectionString("OrionDb")
      ?? throw new InvalidOperationException("Missing ConnectionStrings:OrionDb."));
    return (await connection.QueryAsync<ScopeRow>(new CommandDefinition(SitesSql, new { Rfc = rfc }, cancellationToken: ct))).AsList();
  }

  private sealed class ScopeRow
  {
    public long CompanyId { get; set; }
    public long SiteId { get; set; }
    public string CompanyRfc { get; set; } = "";
    public string DisplayName { get; set; } = "";
  }
}
