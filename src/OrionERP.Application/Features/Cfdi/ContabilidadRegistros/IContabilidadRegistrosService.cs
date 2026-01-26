namespace OrionERP.Application.Features.Cfdi.ContabilidadRegistros;

public interface IContabilidadRegistrosService
{
  Task<IEnumerable<RegistrosContablesRow>> GetRegistrosAsync(
      DateTime startDate,
      DateTime endDate,
      string rfc,
      string nivel1,
      string nivel2,
      string nivel3);

  Task ReorderTransaccionAsync(
      int anchorTransaccionId,
      int targetTransaccionId,
      IReadOnlyList<int> orderedTransaccionIds);
}
