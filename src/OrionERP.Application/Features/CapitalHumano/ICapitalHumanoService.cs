namespace OrionERP.Application.Features.CapitalHumano;

public interface ICapitalHumanoService
{
  Task<IReadOnlyList<CapitalHumanoListItemDto>> GetEmployeesAsync(CapitalHumanoFilter filter, CancellationToken ct = default);
  Task<CapitalHumanoDetailDto?> GetEmployeeAsync(int id, string rfc, CancellationToken ct = default);
  Task<CapitalHumanoCatalogDto> GetCatalogAsync(string rfc, CancellationToken ct = default);
  Task<CapitalHumanoBinaryContent?> GetPhotoAsync(int id, string rfc, CancellationToken ct = default);
  Task<IReadOnlyList<CapitalHumanoBinaryContent>> GetThumbnailsAsync(string rfc, IEnumerable<int> employeeIds, CancellationToken ct = default);
  Task<CapitalHumanoCommandResult> SaveEmployeeAsync(CapitalHumanoSaveRequest request, CancellationToken ct = default);
  Task<CapitalHumanoCommandResult> DeactivateEmployeeAsync(int id, string rfc, CancellationToken ct = default);
}
