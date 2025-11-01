using System.Threading;
using System.Threading.Tasks;

namespace OrionERP.Application.Features.Cfdi.CargarXmlSat.Contracts;

public record TransaccionCommandResult(bool Success, string Message)
{
  public static TransaccionCommandResult Ok(string message)
    => new(true, message);

  public static TransaccionCommandResult Fail(string message)
    => new(false, message);
}

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

