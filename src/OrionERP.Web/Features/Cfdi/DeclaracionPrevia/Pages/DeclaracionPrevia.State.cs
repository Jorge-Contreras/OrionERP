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
    private List<DeclaracionCfdiBase>? emitidasNominaBase;
    private List<DeclaracionCfdiBase>? recibidasNominaBase;
    private List<DeclaracionCfdiBase>? tipoEEmitidasBase;
    private List<DeclaracionCfdiBase>? tipoERecibidasBase;
    private List<DeclaracionEmitida>? emitidas;
    private List<DeclaracionEmitida>? emitidasNomina;
    private List<DeclaracionRecibida>? recibidas;
    private List<DeclaracionRecibida>? recibidasNomina;
    private List<DeclaracionEmitida>? tipoEEmitidas;
    private List<DeclaracionRecibida>? tipoERecibidas;
    private List<DesfaseItem>? desfase;
    private List<PolizaNoConsolidada>? polizasNoConsolidadas;
    private DeclaracionTotales? emitidasTotals;
    private DeclaracionTotales? emitidasNominaTotals;
    private DeclaracionTotales? recibidasTotals;
    private DeclaracionTotales? recibidasNominaTotals;
    private DeclaracionTotales? tipoEEmitidasTotals;
    private DeclaracionTotales? tipoERecibidasTotals;
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

    // Pagination state (simple implementation)
    private int pageSize = 50;
    private int emitidasCurrentPage = 1;
    private int emitidasPageCount = 1;
    private IEnumerable<DeclaracionEmitida> emitidasPage => emitidas?.Skip((emitidasCurrentPage - 1) * pageSize).Take(pageSize) ?? Enumerable.Empty<DeclaracionEmitida>();
    private int recibidasCurrentPage = 1;
    private int recibidasPageCount = 1;
    private IEnumerable<DeclaracionRecibida> recibidasPage => recibidas?.Skip((recibidasCurrentPage - 1) * pageSize).Take(pageSize) ?? Enumerable.Empty<DeclaracionRecibida>();
    private int tipoEEmitidasCurrentPage = 1;
    private int tipoEEmitidasPageCount = 1;
    private IEnumerable<DeclaracionEmitida> tipoEEmitidasPage => tipoEEmitidas?.Skip((tipoEEmitidasCurrentPage - 1) * pageSize).Take(pageSize) ?? Enumerable.Empty<DeclaracionEmitida>();
    private int tipoERecibidasCurrentPage = 1;
    private int tipoERecibidasPageCount = 1;
    private IEnumerable<DeclaracionRecibida> tipoERecibidasPage => tipoERecibidas?.Skip((tipoERecibidasCurrentPage - 1) * pageSize).Take(pageSize) ?? Enumerable.Empty<DeclaracionRecibida>();

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
