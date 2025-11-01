using System.Threading;
using System.Threading.Tasks;

namespace OrionERP.Application.Features.Cfdi.CargarXmlSat.Contracts;

public interface ITransaccionService
{
  Task<TransaccionCommandResult> ApplyCategoriaPlantillaAsync(
      int transaccionId,
      int categoriaId,
      CancellationToken cancellationToken = default);

  Task<TransaccionCommandResult> ProcessSatXmlAsync(
      int attachmentId,
      int transaccionId,
      CancellationToken cancellationToken = default);
}
