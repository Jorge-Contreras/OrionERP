namespace OrionERP.Application.Features.Cfdi.HtmlCFDI;

public sealed class TransactionAttachment
{
  public int Id { get; set; }
  public int TranId { get; set; }
  public string? AttachmentName { get; set; }
  public string? AttachmentExtension { get; set; }
  public byte[] Content { get; set; } = Array.Empty<byte>();
}
