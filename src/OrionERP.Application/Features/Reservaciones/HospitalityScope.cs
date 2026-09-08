namespace OrionERP.Application.Features.Reservaciones;

public sealed record HospitalityScope(long CompanyId, long SiteId, string CompanyRfc);

public interface IHospitalityScopeAccessor
{
  Task<HospitalityScope> ResolveRequiredAsync(CancellationToken ct = default);
}

public sealed record HospitalitySiteOption(long SiteId, string DisplayName);

/// <summary>Selection is a preference, never authority; the accessor revalidates it against the authenticated company.</summary>
public sealed class HospitalitySiteSelection
{
  public long? SiteId { get; private set; }
  public void Select(long? siteId) => SiteId = siteId is > 0 ? siteId : null;
}
