namespace OrionERP.Application.Features.Contabilidad.Transacciones;

public sealed class TransaccionAttachmentContent
{
  public int AttachmentId { get; set; }
  public string FileName { get; set; } = string.Empty;
  public string ContentType { get; set; } = "application/octet-stream";
  public byte[] Bytes { get; set; } = Array.Empty<byte>();
}
