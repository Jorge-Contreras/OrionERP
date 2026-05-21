using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OrionERP.Application.Features.Reservaciones.ListaReservaciones;

public interface IListaReservacionesService
{
  Task<IReadOnlyList<ListaReservacionItemDto>> GetListaAsync(ListaReservacionFilter filter, CancellationToken ct = default);
  Task<int> CreateReservationAsync(ListaReservacionCreateRequest request, CancellationToken ct = default);
  Task<ClienteOptionDto?> GetDefaultClienteForNewReservationAsync(CancellationToken ct = default);
  Task<ReservacionCommandResult> UpdateNotesAsync(int reservationId, string? notes, CancellationToken ct = default);
  Task<ReservacionCommandResult> DeleteEmptyReservationsAsync(CancellationToken ct = default);
  Task<ReservacionCommandResult> DeleteReservationAsync(int reservationId, CancellationToken ct = default);
  Task<ReservacionDetailDto?> GetReservacionDetailAsync(int reservationId, CancellationToken ct = default);

  Task<IReadOnlyList<ClienteOptionDto>> GetClientesAsync(string? searchText = null, int maxResults = 5, CancellationToken ct = default);
  Task<ClienteOptionDto?> ResolveClienteAsync(int? clienteId, string? clienteNombre, CancellationToken ct = default);
  Task<ClienteOptionDto> CreateClienteAsync(string clienteNombre, CancellationToken ct = default);
  Task<IReadOnlyList<RoomOptionDto>> GetRoomsForExtrasAsync(CancellationToken ct = default);
  Task<RoomCalendarTimelineDto> GetCalendarTimelineAsync(RoomCalendarTimelineFilter filter, CancellationToken ct = default);
  Task<IReadOnlyList<ReservacionSuiteDto>> GetSuitesByReservationAsync(int reservationId, CancellationToken ct = default);
  Task<IReadOnlyList<SuiteDisponibleDto>> GetSuitesDisponiblesAsync(DateTime checkIn, DateTime checkOut, CancellationToken ct = default);
  Task<IReadOnlyList<ReservacionExtraDto>> GetExtrasAsync(int reservationId, CancellationToken ct = default);
  Task<IReadOnlyList<ReservacionPagoDto>> GetPagosAsync(int reservationId, CancellationToken ct = default);
  Task<IReadOnlyList<ReservacionAttachmentDto>> GetAttachmentsAsync(int reservationId, CancellationToken ct = default);

  Task<ReservacionAttachmentDto> AddAttachmentAsync(ReservacionAttachmentCreateRequest request, CancellationToken ct = default);
  Task<ReservacionAttachmentContent?> GetAttachmentContentAsync(int attachmentId, CancellationToken ct = default);
  Task DeleteAttachmentAsync(int attachmentId, CancellationToken ct = default);

  Task<ReservacionCommandResult> SaveReservationAsync(ReservacionUpdateRequest request, CancellationToken ct = default);
  Task<ReservacionCommandResult> SyncSuiteStatusAsync(int reservationId, string? status, CancellationToken ct = default);
  Task<ReservacionCommandResult> SyncSuiteLockedByAsync(int reservationId, int? clienteId, CancellationToken ct = default);

  Task<ReservacionCommandResult> AddSuitesToReservationAsync(
      int reservationId,
      string? status,
      string? clienteNombre,
      IReadOnlyCollection<int> roomCalendarIds,
      CancellationToken ct = default);

  Task<ReservacionCommandResult> DeleteSuitesAsync(IReadOnlyCollection<int> roomCalendarIds, CancellationToken ct = default);
  Task<ReservacionCommandResult> SetSuitesPriceAsync(IReadOnlyCollection<int> roomCalendarIds, decimal price, CancellationToken ct = default);
  Task<ReservacionCommandResult> SetSuitesPriceWithIvaAsync(IReadOnlyCollection<int> roomCalendarIds, decimal priceWithIva, CancellationToken ct = default);
  Task<ReservacionCommandResult> ToggleSuitesLimpiezaAsync(IReadOnlyCollection<int> roomCalendarIds, bool nextState, CancellationToken ct = default);
  Task<ReservacionCommandResult> DistributeSuitesTotalWithIvaAsync(IReadOnlyCollection<int> roomCalendarIds, decimal totalWithIva, CancellationToken ct = default);
  Task<ReservacionCommandResult> ApplyAirbnbBreakdownAsync(AirbnbReservationBreakdownApplyRequest request, CancellationToken ct = default);
  Task<ReservacionCommandResult> ClearAirbnbBreakdownIfNoPolizaAsync(int reservationId, CancellationToken ct = default);

  Task<ReservacionCommandResult> AddExtraAsync(ReservacionExtraCreateRequest request, CancellationToken ct = default);
  Task<ReservacionCommandResult> UpdateExtraAsync(ReservacionExtraUpdateRequest request, CancellationToken ct = default);
  Task<ReservacionCommandResult> DeleteExtraAsync(int reservationDetailId, CancellationToken ct = default);
}
