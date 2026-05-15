namespace OrionERP.Web.Features.Arrendadores;

public interface IArrendadorEstadoCuentaPdfService
{
  byte[] Generate(ArrendadorEstadoCuentaPdfDocumentModel model);
}
