namespace OrionERP.Web.Features.ReportesFinancieros.SaludEmpresa;

public interface ISaludEmpresaPdfService
{
  byte[] Generate(SaludEmpresaPdfDocumentModel model);
}
