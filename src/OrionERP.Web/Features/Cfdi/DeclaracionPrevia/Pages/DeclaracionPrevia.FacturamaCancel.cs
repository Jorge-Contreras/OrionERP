using System;
using System.Threading.Tasks;
using Microsoft.JSInterop;

namespace OrionERP.Web.Features.Cfdi.DeclaracionPrevia.Pages
{
  public partial class DeclaracionPrevia
  {
    // Cancel selected Emitida CFDI via Facturama API
    private async Task CancelSelectedEmitidaCfdi()
    {
      if (selectedEmitida == null)
      {
        statusMessage = "Selecciona una factura emitida a cancelar.";
        return;
      }
      // Confirm with user:
      bool confirm = await JS.InvokeAsync<bool>("confirm", $"¿Seguro que desea solicitar la cancelación del CFDI con UUID:\n{selectedEmitida.FOLIO_FISCAL}?\nEsta acción no se puede deshacer.");
      if (!confirm)
      {
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
  }
}
