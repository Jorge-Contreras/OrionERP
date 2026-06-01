using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OrionERP.Application.Features.Reservaciones.ListaReservaciones;

namespace OrionERP.Application.Features.Reservaciones.Experiencias;

public interface IReservacionExperiencesService
{
  Task<IReadOnlyList<ExperienceCatalogItemDto>> GetActiveExperienceCatalogAsync(CancellationToken ct = default);

  Task<IReadOnlyList<ExperienceCatalogItemDto>> GetPublicExperienceCatalogAsync(
    DateOnly startDate,
    DateOnly endDateExclusive,
    CancellationToken ct = default);

  Task<IReadOnlyList<ReservacionExperienceDto>> GetExperiencesAsync(int reservationId, CancellationToken ct = default);

  Task<ReservacionCommandResult> AddExperienceAsync(ReservacionExperienceCreateRequest request, CancellationToken ct = default);

  Task<ReservacionCommandResult> UpdateExperienceAsync(ReservacionExperienceUpdateRequest request, CancellationToken ct = default);

  Task<ReservacionCommandResult> DeleteExperienceAsync(int reservationExperienceId, CancellationToken ct = default);
}
