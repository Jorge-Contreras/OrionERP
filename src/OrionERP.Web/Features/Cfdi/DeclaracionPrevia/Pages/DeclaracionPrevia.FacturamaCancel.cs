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
      var confirmed = await ConfirmCfdiCancellationAsync(selectedEmitida.FOLIO_FISCAL);
      if (!confirmed)
      {
        statusMessage = "No se canceló el CFDI. Debes escribir exactamente 'Delete' para confirmar.";
        UiMessages.ShowWarning(statusMessage);
        return;
      }
      try
      {
        await DeclaracionService.CancelEmitidaAsync(selectedEmitida.FOLIO_FISCAL ?? string.Empty, selectedEmitida.Comprobante_Id);
        await LoadAllData();
        statusMessage = $"Cancelación solicitada para CFDI UUID {selectedEmitida.FOLIO_FISCAL}.";
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
