using OrionERP.Application.Features.Reservaciones.Extras;

namespace OrionERP.Application.Features.Ajustes;

public interface IAjustesService
{
  Task<AjustesGeneralSettingsDto> GetGeneralSettingsAsync(CancellationToken ct = default);

  Task<AjustesCommandResult> SaveGeneralSettingsAsync(AjustesGeneralSettingsSaveRequest request, CancellationToken ct = default);

  Task<IReadOnlyList<ExtraCatalogItemDto>> GetExtraCatalogAsync(
      string? search,
      bool includeInactive,
      CancellationToken ct = default);

  Task<AjustesCommandResult> SaveExtraCatalogItemAsync(ExtraCatalogSaveRequest request, CancellationToken ct = default);

  Task<AjustesCommandResult> DeleteExtraCatalogItemAsync(int extraId, CancellationToken ct = default);

  Task<CfdiPolizaCuentaDefaultsDto> GetCfdiPolizaCuentaDefaultsAsync(string? rfc, CancellationToken ct = default);

  Task<AjustesCommandResult> SaveCfdiPolizaCuentaDefaultsAsync(CfdiPolizaCuentaDefaultsSaveRequest request, CancellationToken ct = default);

  Task<IReadOnlyList<PlantillaContableListItemDto>> GetPlantillasAsync(
      string? rfc,
      string? search,
      bool includeInactive,
      CancellationToken ct = default);

  Task<PlantillaContableDetailDto?> GetPlantillaAsync(int plantillaContableId, CancellationToken ct = default);

  Task<AjustesCommandResult> SavePlantillaAsync(PlantillaContableSaveRequest request, CancellationToken ct = default);

  Task<AjustesCommandResult> DeletePlantillaAsync(int plantillaContableId, CancellationToken ct = default);
}
