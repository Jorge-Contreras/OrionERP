using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.SqlClient;

namespace OrionERP.Web.Features.Cfdi.DeclaracionPrevia.Pages
{
  public partial class DeclaracionPrevia
  {
    // Row selection:
    private async Task SelectEmitidaAsync(DeclaracionEmitida item) => await SelectCfdiAsync(item);

    private async Task SelectRecibidaAsync(DeclaracionRecibida item) => await SelectCfdiAsync(item);

    private async Task SelectCfdiAsync(DeclaracionCfdiBase? item)
    {
      selectedEmitida = null;
      selectedRecibida = null;
      emitidasComplementos = new List<PagoComplementoResumen>();
      recibidasComplementos = new List<PagoComplementoResumen>();

      if (item == null)
      {
        return;
      }

      if (item is DeclaracionEmitida emitida)
      {
        selectedEmitida = emitida;
        await LoadComplementosAsync(item.FOLIO_FISCAL, isEmitida: true);
        return;
      }

      if (item is DeclaracionRecibida recibida)
      {
        selectedRecibida = recibida;
        await LoadComplementosAsync(item.FOLIO_FISCAL, isEmitida: false);
        return;
      }

      if (item.EsEmitida)
      {
        selectedEmitida = new DeclaracionEmitida(item);
        await LoadComplementosAsync(item.FOLIO_FISCAL, isEmitida: true);
        return;
      }

      if (item.EsRecibida)
      {
        selectedRecibida = new DeclaracionRecibida(item);
        await LoadComplementosAsync(item.FOLIO_FISCAL, isEmitida: false);
      }
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

    private async Task LoadComplementosAsync(string? uuidText, bool isEmitida)
    {
      if (string.IsNullOrWhiteSpace(uuidText) || !Guid.TryParse(uuidText, out var uuid))
      {
        if (isEmitida)
        {
          emitidasComplementos = new List<PagoComplementoResumen>();
        }
        else
        {
          recibidasComplementos = new List<PagoComplementoResumen>();
        }
        return;
      }

      try
      {
        using var conn = new SqlConnection(connectionString);
        var resultados = (await conn.QueryAsync<PagoComplementoResumen>(
          "EXEC cfdi.Complemento_Resumen_By_UUID @UUID_DoctoRelacionado",
          new { UUID_DoctoRelacionado = uuid })).AsList();

        if (isEmitida)
        {
          emitidasComplementos = resultados;
        }
        else
        {
          recibidasComplementos = resultados;
        }
      }
      catch (Exception ex)
      {
        SetErrorMessage("Error al cargar complementos de pago: " + ex.Message);
      }
    }

    private string GetEmitidaRowClass(DeclaracionEmitida item)
    {
      var classes = new List<string>();

      if (selectedEmitida?.Comprobante_Id == item.Comprobante_Id)
      {
        classes.Add("table-active");
      }

      if (string.Equals(item.MetodoPago, "PPD", StringComparison.OrdinalIgnoreCase))
      {
        classes.Add("highlight-table-row");
      }

      return string.Join(" ", classes);
    }

    private string GetRecibidaRowClass(DeclaracionRecibida item)
    {
      var classes = new List<string>();

      if (selectedRecibida?.Comprobante_Id == item.Comprobante_Id)
      {
        classes.Add("table-active");
      }

      if (string.Equals(item.MetodoPago, "PPD", StringComparison.OrdinalIgnoreCase))
      {
        classes.Add("highlight-table-row");
      }

      return string.Join(" ", classes);
    }
  }
}
