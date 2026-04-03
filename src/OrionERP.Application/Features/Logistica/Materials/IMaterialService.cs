using System.Threading;
using System.Threading.Tasks;
using OrionERP.Application.Features.Logistica.Shared;

namespace OrionERP.Application.Features.Logistica.Materials;

public interface IMaterialService
{
  Task<IReadOnlyList<MaterialListItemDto>> GetMaterialsAsync(MaterialFilter filter, CancellationToken ct = default);
  Task<MaterialDetailDto?> GetMaterialAsync(int materialId, CancellationToken ct = default);
  Task<MaterialCatalogDto> GetCatalogAsync(CancellationToken ct = default);
  Task<LogisticsBinaryContent?> GetMaterialImageAsync(int materialId, CancellationToken ct = default);
  Task<LogisticsBinaryContent?> GetMaterialThumbnailAsync(int materialId, CancellationToken ct = default);
  Task<IReadOnlyList<LogisticsBinaryContent>> GetMaterialThumbnailsAsync(IEnumerable<int> materialIds, CancellationToken ct = default);
  Task<LogisticsCommandResult> SaveMaterialAsync(MaterialUpsertRequest request, CancellationToken ct = default);
}
