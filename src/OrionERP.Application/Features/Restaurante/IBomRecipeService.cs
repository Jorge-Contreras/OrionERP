namespace OrionERP.Application.Features.Restaurante;

public interface IBomRecipeService
{
  Task<IReadOnlyList<BomVersionDto>> GetBomVersionsAsync(string rfc, CancellationToken ct = default);
  Task<BomVersionDto?> GetBomVersionAsync(string rfc, long bomVersionId, CancellationToken ct = default);
  Task<RestaurantCommandResult> SaveDraftAsync(BomDraftSaveRequest request, CancellationToken ct = default);
  Task<RestaurantCommandResult> ActivateAsync(string rfc, long bomVersionId, string userName, CancellationToken ct = default);
  Task<IReadOnlyList<RestaurantAllergenDto>> GetAllergensAsync(string rfc, CancellationToken ct = default);
  Task<RestaurantCommandResult> SaveAllergenAsync(RestaurantAllergenSaveRequest request, CancellationToken ct = default);
  Task<RestaurantCommandResult> SaveMaterialAllergensAsync(string rfc, int materialId, IReadOnlyCollection<int> allergenIds, CancellationToken ct = default);
  Task<IReadOnlyList<MaterialUnitConversionDto>> GetMaterialUnitConversionsAsync(string rfc, CancellationToken ct = default);
  Task<RestaurantCommandResult> SaveMaterialUnitConversionAsync(MaterialUnitConversionSaveRequest request, CancellationToken ct = default);
}
