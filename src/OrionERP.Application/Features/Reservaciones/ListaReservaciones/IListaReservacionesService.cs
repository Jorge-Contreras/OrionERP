using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OrionERP.Application.Features.Reservaciones.ListaReservaciones;

public interface IListaReservacionesService
{
  Task<IReadOnlyList<ListaReservacionItemDto>> GetListaAsync(ListaReservacionFilter filter, CancellationToken ct = default);
  Task<int> CreateReservationAsync(ListaReservacionCreateRequest request, CancellationToken ct = default);
  Task<ReservacionCommandResult> UpdateNotesAsync(int reservationId, string? notes, CancellationToken ct = default);
  Task<ReservacionCommandResult> DeleteEmptyReservationsAsync(CancellationToken ct = default);
  Task<ReservacionDetailDto?> GetReservacionDetailAsync(int reservationId, CancellationToken ct = default);
}
