namespace OrionERP.Application.Features.Arrendadores;

public interface IArrendadoresEstadoCuentaService
{
  Task<IReadOnlyList<ArrendadorListItemDto>> GetArrendadoresAsync(string? searchText = null, CancellationToken ct = default);

  Task<IReadOnlyList<ArrendadorRoomListItemDto>> GetRoomsAsync(int ownerId, CancellationToken ct = default);

  Task<ArrendadorEstadoCuentaDto> GetEstadoCuentaAsync(
    int ownerId,
    int roomId,
    int year,
    int month,
    CancellationToken ct = default);
}
