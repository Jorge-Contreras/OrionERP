using System;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.SqlClient;

namespace OrionERP.Web.Features.Cfdi.DeclaracionPrevia.Pages
{
  public partial class DeclaracionPrevia
  {
    // Row selection:
    private void SelectEmitida(DeclaracionEmitida item)
    {
      selectedEmitida = item;
      selectedRecibida = null;
    }
    private void SelectRecibida(DeclaracionRecibida item)
    {
      selectedRecibida = item;
      selectedEmitida = null;
    }

    // Toggle Include/Exclude for selected invoice (Emitidas)
    private async Task ToggleEmitidaSelected()
    {
      if (selectedEmitida == null)
      {
        statusMessage = "Selecciona una factura emitida primero.";
        return;
      }
      try
      {
        var selectedId = selectedEmitida.Comprobante_Id;
        var wasExcluded = selectedEmitida.D == "✓";
        using var conn = new SqlConnection(connectionString);
        string sql = "UPDATE Comprobante SET Incluir_En_Declaracion = CASE WHEN Incluir_En_Declaracion = 1 THEN 0 ELSE 1 END WHERE Comprobante_Id = @Id";
        await conn.ExecuteAsync(sql, new { Id = selectedEmitida.Comprobante_Id });
        // Refresh data (or at least refresh that one item):
        await LoadAllData();
        statusMessage = $"Factura recibida marcada como {(wasExcluded ? "EXCLUIDA" : "INCLUIDA")} en la declaración.";
      }
      catch (Exception ex)
      {
        SetErrorMessage("Error al actualizar factura emitida: " + ex.Message);
      }
    }

    // Toggle Include/Exclude for selected invoice (Recibidas)
    private async Task ToggleRecibidaSelected()
    {
      if (selectedRecibida == null)
      {
        statusMessage = "Selecciona una factura recibida primero.";
        return;
      }
      try
      {
        var selectedId = selectedRecibida.Comprobante_Id;
        var wasExcluded = selectedRecibida.D == "✓";
        using var conn = new SqlConnection(connectionString);
        string sql = "UPDATE Comprobante SET Incluir_En_Declaracion = CASE WHEN Incluir_En_Declaracion = 1 THEN 0 ELSE 1 END WHERE Comprobante_Id = @Id";
        await conn.ExecuteAsync(sql, new { Id = selectedRecibida.Comprobante_Id });
        await LoadAllData();
        statusMessage = $"Factura recibida marcada como {(wasExcluded ? "EXCLUIDA" : "INCLUIDA")} en la declaración.";
      }
      catch (Exception ex)
      {
        SetErrorMessage("Error al actualizar factura recibida: " + ex.Message);
      }
    }

    // Exclude all "Pago" or "Devolución" type invoices in Recibidas list
    private async Task ExcludePagosYDevoluciones()
    {
      try
      {
        using var conn = new SqlConnection(connectionString);
        string sql = @"
                    UPDATE C
                    SET Incluir_En_Declaracion = 0
                    FROM Comprobante C
                    JOIN Receptor R ON C.Comprobante_ID = R.Comprobante_ID
                    WHERE C.Incluir_En_Declaracion = 1
                      AND (R.UsoCFDI = 'G02' OR R.UsoCFDI = 'CP01')
                      AND R.RFC = @RFC
                      AND (YEAR(C.Fecha) = @Year AND (@Month IS NULL OR MONTH(C.Fecha) = @Month))";
        int affected = await conn.ExecuteAsync(sql, new { RFC = selectedRfc, Year = selectedYear, Month = isAnnual ? (object)DBNull.Value : selectedMonth });
        await LoadAllData();
        if (affected > 0)
        {
          statusMessage = $"Se excluyeron {affected} comprobantes de tipo Pago/Devolución de la declaración.";
        }
        else
        {
          statusMessage = "No se encontraron CFDIs de tipo Pago o Devolución para excluir.";
        }
      }
      catch (Exception ex)
      {
        SetErrorMessage("Error al excluir pagos/devoluciones: " + ex.Message);
      }
    }
  }
}
