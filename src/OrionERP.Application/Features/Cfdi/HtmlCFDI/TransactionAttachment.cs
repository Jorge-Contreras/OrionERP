namespace OrionERP.Application.Features.Cfdi.HtmlCFDI;

public sealed class TransactionAttachment
{
  public int Id { get; set; }
  public int TranId { get; set; }
  public string AttachmentName { get; set; } = string.Empty;
  public string AttachmentExtension { get; set; } = string.Empty;
  public string AttachmentDescription { get; set; } = string.Empty;
  public byte[] Content { get; set; } = Array.Empty<byte>();
}
