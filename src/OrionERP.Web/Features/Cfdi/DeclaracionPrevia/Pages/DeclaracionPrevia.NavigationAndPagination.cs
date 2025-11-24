using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.JSInterop;

namespace OrionERP.Web.Features.Cfdi.DeclaracionPrevia.Pages
{
  public partial class DeclaracionPrevia
  {
    // Navigation or open detail functions:
    private void OpenEmitidaDetails(DeclaracionEmitida item)
    {
      // For now, navigate to Comprobante detail page if exists
      if (item != null)
      {
        Nav.NavigateTo($"/cfdi/comprobante/{item.Comprobante_Id}");
      }
    }
    private void OpenRecibidaDetails(DeclaracionRecibida item)
    {
      if (item != null)
      {
        Nav.NavigateTo($"/cfdi/comprobante/{item.Comprobante_Id}");
      }
    }
    // Change method signature to async Task to allow use of 'await'
    private async Task HandlePolizaClick(object item)
    {
        if (item is DeclaracionEmitida de && de.Poliza == _placeholderTransaccionId.ToString())
        {
            try
            {
                var newTransaccionId = await GenerarPolizaDesdeComprobante(de.Comprobante_Id);
                de.Poliza = newTransaccionId.ToString();
                await OpenLinkedTransaction(de);
            }
            catch (Exception ex)
            {
                errorMessage = $"Error al crear la póliza: {ex.Message}";
            }
        }
        else if (item is DeclaracionRecibida dr && dr.Poliza == _placeholderTransaccionId.ToString())
        {
            try
            {
                var newTransaccionId = await GenerarPolizaDesdeComprobante(dr.Comprobante_Id);
                dr.Poliza = newTransaccionId.ToString();
                await OpenLinkedTransaction(dr);
            }
            catch (Exception ex)
            {
                errorMessage = $"Error al crear la póliza: {ex.Message}";
            }
        }
        else
        {
            await OpenLinkedTransaction(item);
        }
    }

    private async Task OpenLinkedTransaction(object item)
    {
      long? transId = null;
      if (item is DeclaracionEmitida de)
      {
        try
        {
          using var conn = new SqlConnection(connectionString);
          transId = conn.ExecuteScalar<long?>("SELECT top 1 Transaccion_ID FROM Transaccion_Comprobante WHERE Comprobante_ID = @Cid", new { Cid = de.Comprobante_Id });
        }
        catch { transId = null; }
      }
      else if (item is DeclaracionRecibida dr)
      {
        // Fix: Try to parse Poliza (string?) to long? if possible
        if (!string.IsNullOrWhiteSpace(dr.Poliza) && long.TryParse(dr.Poliza, out var polizaId))
        {
          transId = polizaId;
        }
        else
        {
          try
          {
            using var conn = new SqlConnection(connectionString);
            transId = conn.ExecuteScalar<long?>("SELECT Transaccion_ID FROM Transaccion_Comprobante WHERE Comprobante_ID = @Cid", new { Cid = dr.Comprobante_Id });
          }
          catch { transId = null; }
        }
      }


      if (!transId.HasValue)
      {
        statusMessage = "No se encontró una Transacción vinculada a este CFDI.";
        return;
      }

      var url = $"/Contabilidad/transacciones/{transId.Value}";

      try
      {
        // Open in a new tab (safer flags to avoid tab-nabbing)
        await JS.InvokeVoidAsync("open", url, "_blank", "noopener,noreferrer");
      }
      catch
      {
        // Fallback in same tab if JS interop/popup blocked
        Nav.NavigateTo(url);
      }
    }

  }
}
