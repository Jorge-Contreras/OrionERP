using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using OrionERP.Application.Features.Cfdi.DeclaracionPrevia.DTOs;
using OrionERP.Web.State;
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
    [Inject] private IConfiguration Configuration { get; set; } = default!;
    private int _placeholderTransaccionId;

    protected override async Task OnInitializedAsync()
    {
      var placeholderSetting = Configuration["SatXml:PlaceholderTransaccionId"];
      _placeholderTransaccionId = int.TryParse(placeholderSetting, out var parsedPlaceholder)
        ? parsedPlaceholder
        : 0;
      // Initialize filter defaults:
      disponibleYears = Enumerable.Range(2020, 7).ToList();  // 2020-2026
      disponibleMonths = new List<(int, string)> {
                (1,"ENERO"),(2,"FEBRERO"),(3,"MARZO"),(4,"ABRIL"),(5,"MAYO"),(6,"JUNIO"),
                (7,"JULIO"),(8,"AGOSTO"),(9,"SEPTIEMBRE"),(10,"OCTUBRE"),(11,"NOVIEMBRE"),(12,"DICIEMBRE")
            };
      try
      {
        // For RazonSocial list, query the Emisor table for distinct RFCs:
        var auth = await AuthStateProvider.GetAuthenticationStateAsync();
        var user = auth.User;

        disponiblesRFCs = user.FindAll("rfc")
            .Select(c => c.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(r => r)
            .ToList();
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
      // Setup sortable fields:
      emitidasSortableFields = new Dictionary<string, string> {
                {"Fecha", "Fecha"},
                {"Receptor", "RECEPTOR"},
                {"Total", "Total"},
                {"UUID", "FOLIO_FISCAL"}
            };
      recibidasSortableFields = new Dictionary<string, string> {
                {"Fecha", "Fecha"},
                {"Emisor", "EMISOR"},
                {"Total", "Total"},
                {"UUID", "FOLIO_FISCAL"}
            };
      emitidasSortColumn = "Fecha";
      emitidasSortOrder = "ASC";
      recibidasSortColumn = "Fecha";
      recibidasSortOrder = "ASC";
      // Load initial data:
      await LoadAllData();
    }

    private async Task LoadAllData()
    {
      ClearErrorMessage();
      statusMessage = null;

      try
      {
        var data = await DeclaracionPreviaService.GetDeclaracionPreviaDataAsync(selectedYear, selectedMonth, isAnnual, selectedRfc);

        emitidas = data.Emitidas;
        emitidasTotals = data.EmitidasTotals;
        emitidasNomina = data.EmitidasNomina;
        emitidasNominaTotals = data.EmitidasNominaTotals;
        recibidas = data.Recibidas;
        recibidasTotals = data.RecibidasTotals;
        recibidasNomina = data.RecibidasNomina;
        recibidasNominaTotals = data.RecibidasNominaTotals;
        tipoEEmitidas = data.TipoEEmitidas;
        tipoEEmitidasTotals = data.TipoEEmitidasTotals;
        tipoERecibidas = data.TipoERecibidas;
        tipoERecibidasTotals = data.TipoERecibidasTotals;
        desfase = data.Desfase;
        desfaseTotals = data.DesfaseTotals;
        polizasNoConsolidadas = data.PolizasNoConsolidadas;
        impuestosSummary = data.ImpuestosSummary;
        bancosCajaSummary = data.BancosCajaSummary;

        ApplySorting();
        selectedEmitida = null; selectedRecibida = null;
        emitidasComplementos = new List<PagoComplementoResumen>();
        recibidasComplementos = new List<PagoComplementoResumen>();
        ResetPagination();
      }
      catch (Exception ex)
      {
        SetErrorMessage("Error loading data: " + ex.Message);
      }
    }
  }
}
