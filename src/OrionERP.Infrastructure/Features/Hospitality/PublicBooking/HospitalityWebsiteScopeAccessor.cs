using OrionERP.Application.Features.Bonhomia.PublicBooking;
using OrionERP.Application.Features.Platform;

namespace OrionERP.Infrastructure.Features.Bonhomia.PublicBooking;

public sealed class HospitalityWebsiteScopeAccessor : IHospitalityWebsiteScopeAccessor
{
  private readonly IPublicWebsiteInstanceContext _website;

  public HospitalityWebsiteScopeAccessor(IPublicWebsiteInstanceContext website)
  {
    _website = website ?? throw new ArgumentNullException(nameof(website));
  }

  public async Task<HospitalityWebsiteScope> ResolveRequiredAsync(CancellationToken ct = default)
    => HospitalityWebsiteScopePolicy.FromBinding(await _website.ResolveRequiredAsync(ct));
}
