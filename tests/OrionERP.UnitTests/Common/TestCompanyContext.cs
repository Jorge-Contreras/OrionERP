using OrionERP.Application.Common;

namespace OrionERP.UnitTests.Common;

internal sealed class TestCompanyContext(string rfc = "TEST") : ICurrentCompanyContext
{
  public string CurrentRfc => rfc;
  public string DisplayName => rfc;
  public int? EmployeeId => null;
  public string RequireRfc() => rfc;
  public void EnsureRfc(string requestedRfc)
  {
    if (!string.Equals(rfc, requestedRfc, StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException("Foreign company.");
  }

  public Task<long> RequireCompanyIdAsync(CancellationToken ct = default) => Task.FromResult(1L);
}
