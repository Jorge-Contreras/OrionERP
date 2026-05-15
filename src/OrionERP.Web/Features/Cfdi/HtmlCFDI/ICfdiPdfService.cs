using OrionERP.Application.Features.Cfdi.HtmlCFDI;

namespace OrionERP.Web.Features.Cfdi.HtmlCFDI;

public interface ICfdiPdfService
{
  byte[] Generate(CfdiReadableDocument document);
}
