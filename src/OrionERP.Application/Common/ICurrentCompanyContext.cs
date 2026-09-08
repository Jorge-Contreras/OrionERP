namespace OrionERP.Application.Common;

public interface ICurrentCompanyContext : ICurrentRfcAccessor
{
  string? DisplayName { get; }
  int? EmployeeId { get; }

  string RequireRfc();
  void EnsureRfc(string rfc);

  /// <summary>
  /// Identidad técnica de la empresa de la sesión, resuelta desde
  /// <c>orion.Company</c> por el vínculo exacto de la clave legacy. Falla antes de
  /// tocar tablas si la empresa no existe o está inactiva. No sustituye a
  /// <see cref="ICurrentRfcAccessor.CurrentRfc"/>, que sigue siendo lo que leen los
  /// consumidores heredados.
  /// </summary>
  Task<long> RequireCompanyIdAsync(CancellationToken ct = default);
}
