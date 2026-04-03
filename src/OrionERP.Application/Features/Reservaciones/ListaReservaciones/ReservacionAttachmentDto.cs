namespace OrionERP.Application.Features.Reservaciones.ListaReservaciones;

public sealed class ReservacionAttachmentDto
{
  public int Id { get; set; }
  public int ReservationId { get; set; }
  public string AttachmentName { get; set; } = string.Empty;
  public string AttachmentExtension { get; set; } = string.Empty;
  public string? AttachmentDescription { get; set; }
  public long Length { get; set; }
}
