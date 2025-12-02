namespace OrionERP.Application.Features.Cfdi.ContabilidadRegistros;

public interface ICuentasContablesRepository
{
  Task<IEnumerable<CuentasContablesDto>> SearchNivel1Async(string rfc, string term, int take = 200);
  Task<IEnumerable<CuentasContablesDto>> SearchNivel2Async(string rfc, string nivel1, string term, int take = 200);
  Task<IEnumerable<CuentasContablesDto>> SearchNivel3Async(string rfc, string nivel1, string nivel2, string term, int take = 200);
  Task<CuentasContablesDto?> GetByIdAsync(int id);
  Task<int> CreateNivel3Async(string rfc, string nivel1, string nivel2, string nivel3, string descripcion);
  Task UpdateNivel3DescripcionAsync(int id, string descripcion);
}
