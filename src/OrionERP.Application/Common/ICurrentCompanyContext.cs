namespace OrionERP.Application.Common;

public interface ICurrentCompanyContext
{
  string? CurrentRfc { get; }
  string? DisplayName { get; }
  int? EmployeeId { get; }

  string RequireRfc();
  void EnsureRfc(string rfc);
}
