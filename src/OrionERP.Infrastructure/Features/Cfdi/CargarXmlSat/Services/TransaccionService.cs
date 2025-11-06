using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OrionERP.Application.Common;
using OrionERP.Application.Features.Cfdi.CargarXmlSat.Contracts;

namespace OrionERP.Infrastructure.Features.Cfdi.CargarXmlSat.Services;

public sealed class TransaccionService : ITransaccionService
{
  private readonly IDbStoredProcService _storedProcService;
  private readonly ILogger<TransaccionService> _logger;

  public TransaccionService(IDbStoredProcService storedProcService, ILogger<TransaccionService> logger)
  {
    _storedProcService = storedProcService ?? throw new ArgumentNullException(nameof(storedProcService));
    _logger = logger ?? throw new ArgumentNullException(nameof(logger));
  }

  public async Task<TransaccionCommandResult> ApplyCategoriaPlantillaAsync(
      int transaccionId,
      int categoriaId,
      CancellationToken cancellationToken = default)
  {
    var parameters = new Dictionary<string, object?>
    {
      ["@TransactionID"] = transaccionId,
      ["@CategoriaID"] = categoriaId
    };

    try
    {
      _logger.LogInformation(
          "Applying category template {CategoriaId} to transaction {TransactionId}",
          categoriaId,
          transaccionId);

      await _storedProcService.ExecuteAsync(
          "dbo.APLICAR_PLANTILLA_CATEGORIA",
          parameters,
          cancellationToken);

      return TransaccionCommandResult.Ok("Plantilla aplicada correctamente a la transacción seleccionada.");
    }
    catch (Exception ex)
    {
      _logger.LogError(
          ex,
          "Failed to apply category template {CategoriaId} to transaction {TransactionId}",
          categoriaId,
          transaccionId);

      return TransaccionCommandResult.Fail("No se pudo aplicar la plantilla de categoría. Revisa los datos e inténtalo nuevamente.");
    }
  }

  public async Task<TransaccionCommandResult> ProcessSatXmlAsync(
      int attachmentId,
      int transaccionId,
      CancellationToken cancellationToken = default)
  {
    var parameters = new Dictionary<string, object?>
    {
      ["@AttachmentID"] = attachmentId,
      ["@TransaccionID"] = transaccionId
    };

    try
    {
      _logger.LogInformation(
          "Processing SAT XML attachment {AttachmentId} for transaction {TransactionId}",
          attachmentId,
          transaccionId);

      await _storedProcService.ExecuteAsync(
          "dbo.PROCESAR_SAT_XML",
          parameters,
          cancellationToken);

      return TransaccionCommandResult.Ok("El XML del SAT se procesó correctamente para la transacción seleccionada.");
    }
    catch (Exception ex)
    {
      _logger.LogError(
          ex,
          "Failed to process SAT XML attachment {AttachmentId} for transaction {TransactionId}",
          attachmentId,
          transaccionId);

      return TransaccionCommandResult.Fail("No se pudo procesar el XML del SAT. Verifica el adjunto y vuelve a intentar.");
    }
  }
}

