namespace OrionERP.Application.Features.Ajustes;

public interface IAjustesService
{
  Task<IReadOnlyList<PlantillaContableListItemDto>> GetPlantillasAsync(
      string? rfc,
      string? search,
      bool includeInactive,
      CancellationToken ct = default);

  Task<PlantillaContableDetailDto?> GetPlantillaAsync(int plantillaContableId, CancellationToken ct = default);

  Task<AjustesCommandResult> SavePlantillaAsync(PlantillaContableSaveRequest request, CancellationToken ct = default);

  Task<AjustesCommandResult> DeletePlantillaAsync(int plantillaContableId, CancellationToken ct = default);
}
