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
    private IReadOnlyList<string>? disponiblesRFCs;
    private IReadOnlyList<int>? disponibleYears;
    private IReadOnlyList<(int, string)>? disponibleMonths;
    private string? selectedRfc;
    private int selectedYear;
    private int selectedMonth;
    private bool isAnnual;

    // Data lists and other outputs
    private IReadOnlyList<DeclaracionCfdiBase>? allCfdiBase;
    private IReadOnlyList<DeclaracionCfdiBase>? emitidasBase;
    private IReadOnlyList<DeclaracionCfdiBase>? recibidasBase;
    private IReadOnlyList<DeclaracionCfdiBase>? emitidasPpdBase;
    private IReadOnlyList<DeclaracionCfdiBase>? recibidasPpdBase;
    private IReadOnlyList<DeclaracionCfdiBase>? emitidasNominaBase;
    private IReadOnlyList<DeclaracionCfdiBase>? recibidasNominaBase;
    private IReadOnlyList<DeclaracionCfdiBase>? tipoEEmitidasBase;
    private IReadOnlyList<DeclaracionCfdiBase>? tipoERecibidasBase;
    private IReadOnlyList<DeclaracionCfdiBase>? canceladasOmitidasBase;
    private IReadOnlyList<DeclaracionComplementoBase>? complementosBase;
    private IReadOnlyList<DeclaracionComplementoBase>? complementosEmitidosBase;
    private IReadOnlyList<DeclaracionComplementoBase>? complementosRecibidosBase;
    private IReadOnlyList<DeclaracionEmitida>? emitidas;
    private IReadOnlyList<DeclaracionEmitida>? emitidasPpd;
    private IReadOnlyList<DeclaracionEmitida>? emitidasNomina;
    private IReadOnlyList<DeclaracionRecibida>? recibidas;
    private IReadOnlyList<DeclaracionRecibida>? recibidasPpd;
    private IReadOnlyList<DeclaracionRecibida>? recibidasNomina;
    private IReadOnlyList<DeclaracionEmitida>? tipoEEmitidas;
    private IReadOnlyList<DeclaracionRecibida>? tipoERecibidas;
    private IReadOnlyList<DeclaracionEmitida>? canceladasOmitidas;
    private IReadOnlyList<DeclaracionComplementoEmitido>? complementosEmitidos;
    private IReadOnlyList<DeclaracionComplementoRecibido>? complementosRecibidos;
    private IReadOnlyList<DesfaseItem>? desfase;
    private IReadOnlyList<PolizaNoConsolidada>? polizasNoConsolidadas;
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
