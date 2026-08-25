using OrionERP.Application.Common;
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
    [Inject] protected ICurrentCompanyContext RfcState { get; set; } = default!;
    [Inject] private IDeclaracionPreviaService DeclaracionService { get; set; } = default!;

    protected override async Task OnInitializedAsync()
    {
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
        
        var request = new DeclaracionPreviaRequest(CurrentRfc, selectedYear, isAnnual ? null : selectedMonth, isAnnual);
        var data = await DeclaracionService.GetDeclaracionAsync(request);

        disponibleYears = data.DisponibleYears;
        disponibleMonths = data.DisponibleMonths;
        allCfdiBase = data.AllCfdiBase;
        emitidasBase = data.EmitidasBase;
        recibidasBase = data.RecibidasBase;
        emitidasPpdBase = data.EmitidasPpdBase;
        recibidasPpdBase = data.RecibidasPpdBase;
        emitidasNominaBase = data.EmitidasNominaBase;
        recibidasNominaBase = data.RecibidasNominaBase;
        tipoEEmitidasBase = data.TipoEEmitidasBase;
        tipoERecibidasBase = data.TipoERecibidasBase;
        canceladasOmitidasBase = data.CanceladasOmitidasBase;
        complementosBase = data.ComplementosBase;
        complementosEmitidosBase = data.ComplementosEmitidosBase;
        complementosRecibidosBase = data.ComplementosRecibidosBase;

        emitidas = data.Emitidas;
        emitidasPpd = data.EmitidasPpd;
        emitidasTotals = data.EmitidasTotals;
        emitidasNomina = data.EmitidasNomina;
        emitidasNominaTotals = data.EmitidasNominaTotals;
        recibidas = data.Recibidas;
        recibidasPpd = data.RecibidasPpd;
        recibidasTotals = data.RecibidasTotals;
        recibidasNomina = data.RecibidasNomina;
        recibidasNominaTotals = data.RecibidasNominaTotals;
        tipoEEmitidas = data.TipoEEmitidas;
        tipoEEmitidasTotals = data.TipoEEmitidasTotals;
        tipoERecibidas = data.TipoERecibidas;
        tipoERecibidasTotals = data.TipoERecibidasTotals;
        canceladasOmitidas = data.CanceladasOmitidas;
        canceladasOmitidasTotals = data.CanceladasOmitidasTotals;
        complementosEmitidos = data.ComplementosEmitidos;
        complementosRecibidos = data.ComplementosRecibidos;
        emitidasPpdTotals = data.EmitidasPpdTotals;
        desfase = data.Desfase;
        recibidasPpdTotals = data.RecibidasPpdTotals;
        desfaseTotals = data.DesfaseTotals;
        polizasNoConsolidadas = data.PolizasNoConsolidadas;
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
