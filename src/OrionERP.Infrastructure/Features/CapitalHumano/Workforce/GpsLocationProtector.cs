using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using OrionERP.Application.Features.CapitalHumano.Workforce;

namespace OrionERP.Infrastructure.Features.CapitalHumano.Workforce;

public interface IGpsLocationProtector
{
  byte[]? Protect(LocationEvidenceDto? evidence);
  LocationEvidenceDto? Unprotect(byte[]? protectedEvidence);
}

public sealed class GpsLocationProtector : IGpsLocationProtector
{
  private readonly IDataProtector _protector;

  public GpsLocationProtector(IDataProtectionProvider provider)
  {
    _protector = provider.CreateProtector("OrionERP.CapitalHumano.Attendance.Gps.v1");
  }

  public byte[]? Protect(LocationEvidenceDto? evidence)
  {
    if (evidence?.Latitude is null || evidence.Longitude is null)
      return null;

    return _protector.Protect(JsonSerializer.SerializeToUtf8Bytes(evidence));
  }

  public LocationEvidenceDto? Unprotect(byte[]? protectedEvidence)
  {
    if (protectedEvidence is null || protectedEvidence.Length == 0)
      return null;

    return JsonSerializer.Deserialize<LocationEvidenceDto>(_protector.Unprotect(protectedEvidence));
  }
}
