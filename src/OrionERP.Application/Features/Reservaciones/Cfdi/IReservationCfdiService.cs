using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OrionERP.Application.Features.Contabilidad.Transacciones;

namespace OrionERP.Application.Features.Reservaciones.Cfdi;

public interface IReservationCfdiService
{
  Task<ReservationCfdiContextDto?> GetContextAsync(int reservationId, string issuerRfc, CancellationToken ct = default);
  Task<ReservationFacturacionStatusDto> GetFacturacionStatusAsync(int reservationId, CancellationToken ct = default);
  Task<IReadOnlyList<ReservationCfdiCustomerSuggestionDto>> SearchCustomersAsync(string? searchText, CancellationToken ct = default);
  Task<ReservationCfdiReceiverValidationDto> ValidateReceiverAsync(
      ReservationCfdiCustomerUpsertRequest request,
      CancellationToken ct = default);
  Task<ReservationCfdiCustomerSaveResult> SaveCustomerAsync(ReservationCfdiCustomerUpsertRequest request, CancellationToken ct = default);
  Task<TransaccionCommandResult> ApplyAirbnbAccountingAsync(ReservationAirbnbAccountingRequest request, CancellationToken ct = default);
  Task<ReservationCfdiCreateResult> CreateCfdiAsync(ReservationCfdiCreateRequest request, CancellationToken ct = default);
}
