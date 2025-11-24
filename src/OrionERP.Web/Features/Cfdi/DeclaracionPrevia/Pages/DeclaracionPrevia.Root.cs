using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Text;
using System.Text.Json;
using System.Net.Http;
using System.Net.Http.Headers;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using OfficeOpenXml;

namespace OrionERP.Web.Features.Cfdi.DeclaracionPrevia.Pages
{


  public partial class DeclaracionPrevia : ComponentBase
  {
    private bool _filtering;
    private async Task OpenResumenTab()
    {
      // Persist the two strings into sessionStorage (works across pages/tabs)
      await JS.InvokeVoidAsync("sessionStorage.setItem", "bancosCajaSummary", bancosCajaSummary ?? "");
      await JS.InvokeVoidAsync("sessionStorage.setItem", "impuestosSummary", impuestosSummary ?? "");

      // Open the summary page in a NEW TAB
      //await JS.InvokeVoidAsync("open", "/cfdi/resumen", "_blank");

      // If you prefer same tab instead, use:
      Nav.NavigateTo("/cfdi/resumen");
    }
    // Keep this too if you use it elsewhere:
    // Data models corresponding to stored procedure outputs:
    [Inject] private NavigationManager Nav { get; set; } = default!;

    private async Task<int> GenerarPolizaDesdeComprobante(int comprobanteId)
    {
        using var conn = new SqlConnection(connectionString);
        var parameters = new DynamicParameters();
        parameters.Add("@Comprobante_Id", comprobanteId);
        parameters.Add("@RFC", RfcState.CurrentRfc);
        parameters.Add("@TransaccionID", dbType: System.Data.DbType.Int32, direction: System.Data.ParameterDirection.Output);

        await conn.ExecuteAsync("[contabilidad].[Generar_Poliza_Desde_Comprobante]", parameters, commandType: System.Data.CommandType.StoredProcedure);

        return parameters.Get<int>("@TransaccionID");
    }
  }
}
