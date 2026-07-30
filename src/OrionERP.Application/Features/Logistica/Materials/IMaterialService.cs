using System.Threading;
using System.Threading.Tasks;
using OrionERP.Application.Features.Logistica.Shared;

namespace OrionERP.Application.Features.Logistica.Materials;

public interface IMaterialService
{
  Task<IReadOnlyList<MaterialListItemDto>> GetMaterialsAsync(MaterialFilter filter, CancellationToken ct = default);
  Task<MaterialDetailDto?> GetMaterialAsync(string rfc, int materialId, CancellationToken ct = default);
  Task<MaterialCatalogDto> GetCatalogAsync(string rfc, CancellationToken ct = default);
  Task<LogisticsBinaryContent?> GetMaterialImageAsync(string rfc, int materialId, CancellationToken ct = default);
  Task<LogisticsBinaryContent?> GetMaterialThumbnailAsync(string rfc, int materialId, CancellationToken ct = default);
  Task<IReadOnlyList<LogisticsBinaryContent>> GetMaterialThumbnailsAsync(string rfc, IEnumerable<int> materialIds, CancellationToken ct = default);
  Task<LogisticsCommandResult> SaveMaterialAsync(MaterialUpsertRequest request, CancellationToken ct = default);
  Task<LogisticsCommandResult> CreateCategoryAsync(MaterialCategoryCreateRequest request, CancellationToken ct = default);
  Task<LogisticsCommandResult> CreateUnitAsync(UnitOfMeasureCreateRequest request, CancellationToken ct = default);
}
