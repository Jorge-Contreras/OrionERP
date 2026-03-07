namespace OrionERP.Application.Features.Reservaciones.ListaReservaciones;

public sealed class ListaReservacionCreateRequest
{
  public int ClienteId { get; set; }
  public string? Notes { get; set; }
}
