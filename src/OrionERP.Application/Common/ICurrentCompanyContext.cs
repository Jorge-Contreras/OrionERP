namespace OrionERP.Application.Common;

public interface ICurrentCompanyContext : ICurrentRfcAccessor
{
  string? DisplayName { get; }
  int? EmployeeId { get; }

  string RequireRfc();
  void EnsureRfc(string rfc);
}
