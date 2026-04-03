using System.Threading;
using System.Threading.Tasks;
using OrionERP.Application.Features.Logistica.Shared;

namespace OrionERP.Application.Features.Logistica.Locations;

public interface ILocationService
{
  Task<IReadOnlyList<LocationListItemDto>> GetLocationsAsync(LocationFilter filter, CancellationToken ct = default);
  Task<LocationDetailDto?> GetLocationAsync(int locationId, CancellationToken ct = default);
  Task<IReadOnlyList<LocationTreeNodeDto>> GetLocationTreeAsync(CancellationToken ct = default);
  Task<IReadOnlyList<LookupOptionDto>> GetLocationLookupAsync(bool inventoryOnly = false, CancellationToken ct = default);
  Task<IReadOnlyList<LookupOptionDto>> GetRoomLookupAsync(CancellationToken ct = default);
  Task<LogisticsCommandResult> SaveLocationAsync(LocationUpsertRequest request, CancellationToken ct = default);
}
