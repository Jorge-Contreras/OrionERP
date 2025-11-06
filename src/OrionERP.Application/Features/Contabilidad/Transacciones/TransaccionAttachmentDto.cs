namespace OrionERP.Application.Features.Contabilidad.Transacciones;

public sealed class TransaccionAttachmentDto
{
  public int Id { get; set; }
  public int TransaccionId { get; set; }
  public string? AttachmentName { get; set; }
  public string? AttachmentExtension { get; set; }
  public string? AttachmentDescription { get; set; }
  public long? Length { get; set; }
}
