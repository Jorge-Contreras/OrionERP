using System.Threading;
using System.Threading.Tasks;

namespace OrionERP.Application.Features.Cfdi.CargarXmlSat.Contracts;

public sealed class TransaccionCommandResult
{
  private TransaccionCommandResult(bool success, string message)
  {
    Success = success;
    Message = message;
  }

  public bool Success { get; }

  public string Message { get; }

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

