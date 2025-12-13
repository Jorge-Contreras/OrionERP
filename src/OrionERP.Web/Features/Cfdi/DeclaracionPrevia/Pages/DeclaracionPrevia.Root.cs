using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Text;
using System.Text.Json;
using System.Net.Http;
using System.Net.Http.Headers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Text;
using System.Text.Json;
using System.Net.Http;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using OfficeOpenXml;
using OrionERP.Application.Features.Cfdi.DeclaracionPrevia.Interfaces;

namespace OrionERP.Web.Features.Cfdi.DeclaracionPrevia.Pages
{


  public partial class DeclaracionPrevia : ComponentBase
  {
    [Inject]
    private IDeclaracionPreviaService DeclaracionPreviaService { get; set; } = default!;
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
        return await DeclaracionPreviaService.GenerarPolizaDesdeComprobanteAsync(comprobanteId, RfcState.CurrentRfc);
    }
  }
}
