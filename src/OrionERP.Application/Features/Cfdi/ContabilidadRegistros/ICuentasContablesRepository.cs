namespace OrionERP.Application.Features.Cfdi.ContabilidadRegistros;

public interface ICuentasContablesRepository
{
  Task<IEnumerable<CuentasContablesDto>> SearchNivel1Async(string rfc, string term, int take = 25);
  Task<IEnumerable<CuentasContablesDto>> SearchNivel2Async(string rfc, string nivel1, string term, int take = 25);
  Task<IEnumerable<CuentasContablesDto>> SearchNivel3Async(string rfc, string nivel1, string nivel2, string term, int take = 25);
  Task<CuentasContablesDto?> GetByIdAsync(int id);
}
