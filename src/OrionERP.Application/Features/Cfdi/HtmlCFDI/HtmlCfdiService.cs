using System.Text;

namespace OrionERP.Application.Features.Cfdi.HtmlCFDI;

public sealed class HtmlCfdiService : IHtmlCfdiService
{
  private readonly ITransactionAttachmentRepository _attachments;
  private readonly CfdiReadableParser _parser;

  public HtmlCfdiService(ITransactionAttachmentRepository attachments, CfdiReadableParser parser)
  {
    _attachments = attachments ?? throw new ArgumentNullException(nameof(attachments));
    _parser = parser ?? throw new ArgumentNullException(nameof(parser));
  }

  public async Task<CfdiReadableDocument> GetHtmlCfdiAsync(int attachmentId, CancellationToken ct = default)
  {
    var attachment = await _attachments.GetAttachmentAsync(attachmentId, ct)
                    ?? throw new InvalidOperationException("No se encontró el adjunto solicitado o no pertenece al RFC seleccionado.");

    if (attachment.Content is null || attachment.Content.Length == 0)
      throw new InvalidOperationException("El adjunto no contiene datos.");

    if (!string.Equals(attachment.AttachmentExtension, "xml", StringComparison.OrdinalIgnoreCase))
      throw new InvalidOperationException("El adjunto no es un XML.");

    var xmlText = Encoding.UTF8.GetString(attachment.Content);
    xmlText = xmlText.TrimStart('\uFEFF');

    return _parser.Parse(xmlText);
  }
}
