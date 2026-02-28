using Microsoft.AspNetCore.Components;
using OrionERP.Application.Features.Cfdi.DeclaracionPrevia;
using OrionERP.Web.Services;
using System;
using System.Collections.Generic;
using System.Linq;

namespace OrionERP.Web.Features.Cfdi.DeclaracionPrevia.Pages
{
  public partial class DeclaracionPrevia
  {
    [Inject] private IUiMessageService UiMessages { get; set; } = default!;

    // Filter state
    private List<string>? disponiblesRFCs;
    private List<int>? disponibleYears;
    private List<(int, string)>? disponibleMonths;
    private string? selectedRfc;
    private int selectedYear;
    private int selectedMonth;
    private bool isAnnual;

    // Data lists and other outputs
    private List<DeclaracionCfdiBase>? allCfdiBase;
    private List<DeclaracionCfdiBase>? emitidasBase;
    private List<DeclaracionCfdiBase>? recibidasBase;
    private List<DeclaracionCfdiBase>? emitidasPpdBase;
    private List<DeclaracionCfdiBase>? recibidasPpdBase;
    private List<DeclaracionCfdiBase>? emitidasNominaBase;
    private List<DeclaracionCfdiBase>? recibidasNominaBase;
    private List<DeclaracionCfdiBase>? tipoEEmitidasBase;
    private List<DeclaracionCfdiBase>? tipoERecibidasBase;
    private List<DeclaracionCfdiBase>? canceladasOmitidasBase;
    private List<DeclaracionComplementoBase>? complementosBase;
    private List<DeclaracionComplementoBase>? complementosEmitidosBase;
    private List<DeclaracionComplementoBase>? complementosRecibidosBase;
    private List<DeclaracionEmitida>? emitidas;
    private List<DeclaracionEmitida>? emitidasPpd;
    private List<DeclaracionEmitida>? emitidasNomina;
    private List<DeclaracionRecibida>? recibidas;
    private List<DeclaracionRecibida>? recibidasPpd;
    private List<DeclaracionRecibida>? recibidasNomina;
    private List<DeclaracionEmitida>? tipoEEmitidas;
    private List<DeclaracionRecibida>? tipoERecibidas;
    private List<DeclaracionEmitida>? canceladasOmitidas;
    private List<DeclaracionComplementoEmitido>? complementosEmitidos;
    private List<DeclaracionComplementoRecibido>? complementosRecibidos;
    private List<DesfaseItem>? desfase;
    private List<PolizaNoConsolidada>? polizasNoConsolidadas;
    private DeclaracionTotales? emitidasTotals;
    private DeclaracionTotales? emitidasPpdTotals;
    private DeclaracionTotales? emitidasNominaTotals;
    private DeclaracionTotales? recibidasTotals;
    private DeclaracionTotales? recibidasPpdTotals;
    private DeclaracionTotales? recibidasNominaTotals;
    private DeclaracionTotales? tipoEEmitidasTotals;
    private DeclaracionTotales? tipoERecibidasTotals;
    private DeclaracionTotales? canceladasOmitidasTotals;
    private DesfaseTotales? desfaseTotals;
    private string? impuestosSummary;
    private string? bancosCajaSummary;

    private List<PagoComplementoResumen>? emitidasComplementos;
    private List<PagoComplementoResumen>? recibidasComplementos;

    // For UI selection and messages
    private DeclaracionEmitida? selectedEmitida;
    private DeclaracionRecibida? selectedRecibida;
    private string? statusMessage;
    private string? errorMessage;

    private void ClearErrorMessage()
    {
      errorMessage = null;
      if (UiMessages.Current?.Level == UiMessageLevel.Error)
      {
        UiMessages.Clear();
      }
    }

    private void SetErrorMessage(string message)
    {
      errorMessage = message;
      if (!string.IsNullOrWhiteSpace(message))
      {
        UiMessages.ShowError(message);
      }
    }
  }
}
