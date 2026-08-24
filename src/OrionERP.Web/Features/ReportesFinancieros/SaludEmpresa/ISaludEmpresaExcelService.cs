namespace OrionERP.Web.Features.ReportesFinancieros.SaludEmpresa;

public interface ISaludEmpresaExcelService
{
  byte[] Generate(SaludEmpresaPdfDocumentModel model);
}
