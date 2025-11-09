using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OrionERP.Application.Features.Contabilidad.Bancos;

public interface IBancosService
{
  Task<IReadOnlyList<BankAccountDto>> GetAccountsAsync(CancellationToken cancellationToken = default);
  Task<IReadOnlyList<int>> GetAvailableYearsAsync(string rfc, CancellationToken cancellationToken = default);
  Task<IReadOnlyList<BankMovementDto>> GetMovementsAsync(
      string rfc,
      int? accountId,
      int year,
      int month,
      string? textFilter,
      CancellationToken cancellationToken = default);
  Task<ProcessBbvaResult?> ProcessBbvaFileAsync(
      string fileContents,
      int accountId,
      CancellationToken cancellationToken = default);
}
