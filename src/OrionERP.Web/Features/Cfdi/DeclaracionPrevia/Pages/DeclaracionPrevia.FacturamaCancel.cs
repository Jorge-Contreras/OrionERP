using System;
using System.Threading.Tasks;
using Microsoft.JSInterop;
using OrionERP.Application.Features.Cfdi.DeclaracionPrevia;

namespace OrionERP.Web.Features.Cfdi.DeclaracionPrevia.Pages
{
  public partial class DeclaracionPrevia
  {
    private const string CfdiCancelConfirmationText = "Delete";

    private bool CanCancelCfdi(DeclaracionCfdiBase item)
    {
      return item.EsEmitida
        && !string.IsNullOrWhiteSpace(item.FOLIO_FISCAL)
        && item.FechaCancelacion is null
        && !string.Equals(item.Estatus?.Trim(), "Cancelado", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(item.Estatus?.Trim(), "Cancelada", StringComparison.OrdinalIgnoreCase);
    }

    private async Task CancelCfdiAsync(IDeclaracionComprobanteItem item)
    {
      if (item is not DeclaracionCfdiBase cfdi)
      {
        statusMessage = "Selecciona una factura emitida a cancelar.";
        return;
      }

      await SelectCfdiAsync(cfdi);
      await CancelSelectedEmitidaCfdi();
    }

    // Cancel selected Emitida CFDI via Facturama API
    private async Task CancelSelectedEmitidaCfdi()
    {
      if (selectedEmitida == null)
      {
        statusMessage = "Selecciona una factura emitida a cancelar.";
        return;
      }

      var uuid = selectedEmitida.FOLIO_FISCAL;
      var comprobanteId = selectedEmitida.Comprobante_Id;
      if (string.IsNullOrWhiteSpace(uuid))
      {
        statusMessage = "La factura emitida seleccionada no tiene UUID para cancelar.";
        UiMessages.ShowWarning(statusMessage);
        return;
      }

      var confirmed = await ConfirmCfdiCancellationAsync(uuid);
      if (!confirmed)
      {
        statusMessage = "No se canceló el CFDI. Debes escribir exactamente 'Delete' para confirmar.";
        UiMessages.ShowWarning(statusMessage);
        return;
      }
      try
      {
        await DeclaracionService.CancelEmitidaAsync(uuid, comprobanteId);
        await LoadAllData();
        statusMessage = $"Cancelación solicitada para CFDI UUID {uuid}.";
      }
      catch (Exception ex)
      {
        SetErrorMessage("Error en el proceso de cancelación: " + ex.Message);
      }
    }

    private async Task<bool> ConfirmCfdiCancellationAsync(string? uuid)
    {
      try
      {
        var confirmation = await JS.InvokeAsync<string?>(
          "prompt",
          $"Para cancelar el CFDI con UUID:\n{uuid}\n\nEscribe '{CfdiCancelConfirmationText}' y presiona Aceptar.\nEsta acción se enviará a Facturama y no se puede deshacer.");

        return confirmation == CfdiCancelConfirmationText;
      }
      catch
      {
        return false;
      }
    }
  }
}
