using System.Threading;
using System.Threading.Tasks;
using OrionERP.Application.Features.Reservaciones.ListaReservaciones;

namespace OrionERP.Application.Features.Reservaciones.OpenClaw;

public interface IOpenClawReservationsService
{
  Task<OpenClawReservationCreateResult> CreateReservationAsync(OpenClawReservationCreateRequest request, CancellationToken ct = default);
  Task<ReservacionDetailDto?> GetReservationDetailAsync(int reservationId, CancellationToken ct = default);
}
