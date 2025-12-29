using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using OrionERP.Web.State;
using OrionERP.Application.Features.Cfdi.DeclaracionPrevia;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;

namespace OrionERP.Web.Features.Cfdi.DeclaracionPrevia.Pages
{
  public partial class DeclaracionPrevia
  {
    [Inject] private AuthenticationStateProvider AuthStateProvider { get; set; } = default!;
    [Inject] protected IUserRfcState RfcState { get; set; } = default!;
    [Inject] private IDeclaracionPreviaService DeclaracionService { get; set; } = default!;

    protected override async Task OnInitializedAsync()
    {
      try
      {
        // For RazonSocial list, query the Emisor table for distinct RFCs:
        var auth = await AuthStateProvider.GetAuthenticationStateAsync();
        var user = auth.User;

        disponiblesRFCs = (await DeclaracionService.GetAvailableRfcsAsync(user)).ToList();
      }
      catch
      {
        disponiblesRFCs = new List<string>(); // if fails, fallback
      }
      if (disponiblesRFCs == null || disponiblesRFCs.Count == 0)
      {
        // If none found, just use a default from config or known value
        disponiblesRFCs = new List<string> { "" };
      }
      selectedRfc = RfcState.CurrentRfc ;
      selectedYear = DateTime.Now.Year;
      selectedMonth = DateTime.Now.Month;
      isAnnual = false;
      // Load initial data:
      await LoadAllData();
    }

    private async Task LoadAllData()
    {
      ClearErrorMessage();
      statusMessage = null;

      try
      {
        
        var request = new DeclaracionPreviaRequest(selectedRfc ?? string.Empty, selectedYear, isAnnual ? null : selectedMonth, isAnnual);
        var data = await DeclaracionService.GetDeclaracionAsync(request);

        disponibleYears = data.DisponibleYears.ToList();
        disponibleMonths = data.DisponibleMonths.ToList();
        allCfdiBase = data.AllCfdiBase.ToList();
        emitidasBase = data.EmitidasBase.ToList();
        recibidasBase = data.RecibidasBase.ToList();
        emitidasPpdBase = data.EmitidasPpdBase.ToList();
        recibidasPpdBase = data.RecibidasPpdBase.ToList();
        emitidasNominaBase = data.EmitidasNominaBase.ToList();
        recibidasNominaBase = data.RecibidasNominaBase.ToList();
        tipoEEmitidasBase = data.TipoEEmitidasBase.ToList();
        tipoERecibidasBase = data.TipoERecibidasBase.ToList();
        complementosBase = data.ComplementosBase.ToList();
        complementosEmitidosBase = data.ComplementosEmitidosBase.ToList();
        complementosRecibidosBase = data.ComplementosRecibidosBase.ToList();

        emitidas = data.Emitidas.ToList();
        emitidasPpd = data.EmitidasPpd.ToList();
        emitidasTotals = data.EmitidasTotals;
        emitidasNomina = data.EmitidasNomina.ToList();
        emitidasNominaTotals = data.EmitidasNominaTotals;
        recibidas = data.Recibidas.ToList();
        recibidasPpd = data.RecibidasPpd.ToList();
        recibidasTotals = data.RecibidasTotals;
        recibidasNomina = data.RecibidasNomina.ToList();
        recibidasNominaTotals = data.RecibidasNominaTotals;
        tipoEEmitidas = data.TipoEEmitidas.ToList();
        tipoEEmitidasTotals = data.TipoEEmitidasTotals;
        tipoERecibidas = data.TipoERecibidas.ToList();
        tipoERecibidasTotals = data.TipoERecibidasTotals;
        complementosEmitidos = data.ComplementosEmitidos.ToList();
        complementosRecibidos = data.ComplementosRecibidos.ToList();
        emitidasPpdTotals = data.EmitidasPpdTotals;
        desfase = data.Desfase.ToList();
        recibidasPpdTotals = data.RecibidasPpdTotals;
        desfaseTotals = data.DesfaseTotals;
        polizasNoConsolidadas = data.PolizasNoConsolidadas.ToList();
        impuestosSummary = data.ImpuestosSummary;
        bancosCajaSummary = data.BancosCajaSummary;

        selectedEmitida = null; selectedRecibida = null;
        emitidasComplementos = new List<PagoComplementoResumen>();
        recibidasComplementos = new List<PagoComplementoResumen>();
      }
      catch (Exception ex)
      {
        SetErrorMessage("Error loading data: " + ex.Message);
      }

    }
  }
}
