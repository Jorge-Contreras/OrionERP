using Dapper;
using Microsoft.Data.SqlClient;

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
    private void OpenLinkedTransaction(object item)
    {
      long? transId = null;
      if (item is DeclaracionEmitida de)
      {
        try
        {
          using var conn = new SqlConnection(connectionString);
          transId = conn.ExecuteScalar<long?>("SELECT Transaccion_ID FROM Transaccion_Comprobante WHERE Comprobante_ID = @Cid", new { Cid = de.Comprobante_Id });
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
      if (transId.HasValue)
      {
        Nav.NavigateTo($"/transacciones/{transId.Value}");
      }
      else
      {
        statusMessage = "No se encontró una Transacción vinculada a este CFDI.";
      }
    }

    // Pagination controls:
    private void NextEmitidasPage()
    {
      if (emitidasCurrentPage < emitidasPageCount)
      {
        emitidasCurrentPage++;
      }
    }
    private void PrevEmitidasPage()
    {
      if (emitidasCurrentPage > 1)
      {
        emitidasCurrentPage--;
      }
    }
    private void NextRecibidasPage()
    {
      if (recibidasCurrentPage < recibidasPageCount)
      {
        recibidasCurrentPage++;
      }
    }
    private void PrevRecibidasPage()
    {
      if (recibidasCurrentPage > 1)
      {
        recibidasCurrentPage--;
      }
    }
  }
}
