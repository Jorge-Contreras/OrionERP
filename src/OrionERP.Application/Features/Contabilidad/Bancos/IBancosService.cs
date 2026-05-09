using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OrionERP.Application.Features.Contabilidad.Bancos;

public interface IBancosService
{
  Task<IReadOnlyList<BankAccountDto>> GetAccountsAsync(string rfc, CancellationToken cancellationToken = default);
  Task<BankAccountDto> CreateAccountAsync(BankAccountRequest request, CancellationToken cancellationToken = default);
  Task<BankAccountDto?> UpdateAccountAsync(int accountId, BankAccountRequest request, CancellationToken cancellationToken = default);
  Task DeleteAccountAsync(int accountId, string rfc, CancellationToken cancellationToken = default);
  Task<IReadOnlyList<int>> GetAvailableYearsAsync(string rfc, CancellationToken cancellationToken = default);
  Task<IReadOnlyList<BankMovementDto>> GetMovementsAsync(
      string rfc,
      int? accountId,
      int year,
      int month,
      string? textFilter,
      CancellationToken cancellationToken = default);
  Task<IReadOnlyList<BankMovementDto>> GetMovementsByTransactionAsync(
      int transaccionId,
      CancellationToken cancellationToken = default);
  Task<IReadOnlyList<PendingBankTransactionDto>> GetPendingTransactionsAsync(
      string rfc,
      int? accountId,
      int year,
      int month,
      CancellationToken cancellationToken = default);
  Task<ProcessBbvaResult?> ProcessBbvaFileAsync(
      string fileContents,
      int accountId,
      decimal initialBalance,
      CancellationToken cancellationToken = default);
  Task<int> CreateAutoPoliciesAsync(
      string rfc,
      int year,
      int month,
      int? accountId,
      CancellationToken cancellationToken = default);
  Task<int> AlignTransactionsToBankMovementsAsync(
      string rfc,
      int year,
      int month,
      int accountId,
      CancellationToken cancellationToken = default);
  Task LinkMovementToTransactionAsync(
      long movimientoId,
      int transaccionId,
      CancellationToken cancellationToken = default);
  Task UnlinkMovementAsync(
      long movimientoId,
      CancellationToken cancellationToken = default);
}
