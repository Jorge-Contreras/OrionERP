using OrionERP.Application.Features.Reservaciones.Experiencias;
using OrionERP.Application.Features.Reservaciones.ListaReservaciones;

namespace OrionERP.Application.Features.Bonhomia.PublicBooking;

/// <summary>
/// Read-only hospitality data surface for a public website. Implementations
/// must resolve their company/site scope from IHospitalityWebsiteScopeAccessor;
/// callers cannot supply a tenant identifier.
/// </summary>
public interface IBonhomiaScopedPublicDataReader
{
  Task<RoomCalendarTimelineDto> GetCalendarTimelineAsync(
    DateOnly startDate,
    DateOnly endDateExclusive,
    CancellationToken ct = default);

  Task<IReadOnlyList<BonhomiaExtraOptionDto>> GetExtraOptionsAsync(
    CancellationToken ct = default);

  Task<IReadOnlyList<ExperienceCatalogItemDto>> GetExperienceCatalogAsync(
    DateOnly startDate,
    DateOnly endDateExclusive,
    CancellationToken ct = default);

  Task<IReadOnlyList<int>> GetRoomCalendarIdsAsync(
    string roomName,
    DateOnly checkIn,
    DateOnly checkOut,
    CancellationToken ct = default);

  Task<ReservacionDetailDto?> GetReservationDetailAsync(
    int reservationId,
    CancellationToken ct = default);

  Task ValidateSchemaAsync(CancellationToken ct = default);
}
