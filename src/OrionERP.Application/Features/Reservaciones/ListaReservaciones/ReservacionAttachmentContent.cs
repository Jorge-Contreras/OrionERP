namespace OrionERP.Application.Features.Reservaciones.ListaReservaciones;

public sealed class ReservacionAttachmentContent
{
  public int AttachmentId { get; set; }
  public string FileName { get; set; } = string.Empty;
  public string ContentType { get; set; } = "application/octet-stream";
  public byte[] Bytes { get; set; } = [];
}
