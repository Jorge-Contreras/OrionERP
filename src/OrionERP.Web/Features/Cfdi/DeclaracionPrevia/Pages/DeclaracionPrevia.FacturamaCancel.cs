using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.SqlClient;
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
        // Facturama API credentials (should be in config ideally)
        string facturamaUser = Configuration["Facturama:User"] ?? "jorgecontreras82";
        string facturamaPassword = Configuration["Facturama:Password"] ?? "Orion2020";
        string authHeader = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{facturamaUser}:{facturamaPassword}"));
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authHeader);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        // 1. GET CFDI by UUID to retrieve its internal ID
        string queryUrl = $"https://api.facturama.mx/cfdi?type=issued&uuid={selectedEmitida.FOLIO_FISCAL}";
        var getResp = await client.GetAsync(queryUrl);
        if (!getResp.IsSuccessStatusCode)
        {
          SetErrorMessage($"Error al buscar CFDI en Facturama. Status: {(int)getResp.StatusCode} - {getResp.ReasonPhrase}");
          return;
        }
        string getBody = await getResp.Content.ReadAsStringAsync();
        string? cfdiId = null;
        try
        {
          using var jdoc = JsonDocument.Parse(getBody);
          if (jdoc.RootElement.ValueKind == JsonValueKind.Array && jdoc.RootElement.GetArrayLength() > 0)
          {
            cfdiId = jdoc.RootElement[0].GetProperty("Id").GetString();
          }
        }
        catch
        {
          // If parsing fails
          SetErrorMessage("No se pudo interpretar la respuesta de Facturama (CFDI no encontrado?).");
          return;
        }
        if (string.IsNullOrEmpty(cfdiId))
        {
          SetErrorMessage("No se encontró el CFDI en Facturama para ese UUID.");
          return;
        }
        // 2. DELETE request to cancel
        string cancelUrl = $"https://api.facturama.mx/cfdi/{cfdiId}?type=issued&motive=02";
        var deleteResp = await client.DeleteAsync(cancelUrl);
        string deleteBody = await deleteResp.Content.ReadAsStringAsync();
        if (!deleteResp.IsSuccessStatusCode)
        {
          SetErrorMessage($"Error al solicitar la cancelación. Status: {(int)deleteResp.StatusCode}. Detalles: {deleteBody}");
          return;
        }
        // Parse status if possible:
        string statusReturned = "Desconocido";
        try
        {
          using var jdoc2 = JsonDocument.Parse(deleteBody);
          if (jdoc2.RootElement.TryGetProperty("Status", out var statusProp))
          {
            statusReturned = statusProp.GetString() ?? statusReturned;
          }
        }
        catch { /* ignore parse errors */ }
        // Mark as excluded in DB:
        try
        {
          using var conn = new SqlConnection(connectionString);
          await conn.ExecuteAsync("UPDATE Comprobante SET Incluir_En_Declaracion = 0 WHERE Comprobante_Id = @Id", new { Id = selectedEmitida.Comprobante_Id });
        }
        catch { /* even if this fails, proceed */ }
        await LoadAllData();
        statusMessage = $"Cancelación solicitada para CFDI UUID {selectedEmitida.FOLIO_FISCAL}. Estado devuelto: {statusReturned}";
      }
      catch (Exception ex)
      {
        SetErrorMessage("Error en el proceso de cancelación: " + ex.Message);
      }
    }
  }
}
