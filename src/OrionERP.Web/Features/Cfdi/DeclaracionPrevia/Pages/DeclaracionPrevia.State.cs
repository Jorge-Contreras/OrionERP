using Microsoft.AspNetCore.Components;
using OrionERP.Web.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using OrionERP.Application.Features.Cfdi.DeclaracionPrevia.DTOs;

namespace OrionERP.Web.Features.Cfdi.DeclaracionPrevia.Pages
{
  public partial class DeclaracionPrevia
  {
    [Inject] private IUiMessageService UiMessages { get; set; } = default!;

    // Filter state
    private List<string> disponiblesRFCs = new();
    private List<int> disponibleYears = new();
    private List<(int, string)> disponibleMonths = new();
    private string selectedRfc = "";
    private int selectedYear;
    private int selectedMonth;
    private bool isAnnual;

    // Data lists and other outputs
    private List<DeclaracionEmitida> emitidas = new();
    private List<DeclaracionEmitida> emitidasNomina = new();
    private List<DeclaracionRecibida> recibidas = new();
    private List<DeclaracionRecibida> recibidasNomina = new();
    private List<DeclaracionEmitida> tipoEEmitidas = new();
    private List<DeclaracionRecibida> tipoERecibidas = new();
    private List<DesfaseItem> desfase = new();
    private List<PolizaNoConsolidada> polizasNoConsolidadas = new();
    private DeclaracionTotales emitidasTotals = new();
    private DeclaracionTotales emitidasNominaTotals = new();
    private DeclaracionTotales recibidasTotals = new();
    private DeclaracionTotales recibidasNominaTotals = new();
    private DeclaracionTotales tipoEEmitidasTotals = new();
    private DeclaracionTotales tipoERecibidasTotals = new();
    private DesfaseTotales desfaseTotals = new();
    private string impuestosSummary = "";
    private string bancosCajaSummary = "";

    private List<PagoComplementoResumen> emitidasComplementos = new();
    private List<PagoComplementoResumen> recibidasComplementos = new();

    // For UI selection and messages
    private DeclaracionEmitida? selectedEmitida;
    private DeclaracionRecibida? selectedRecibida;
    private string? statusMessage;
    private string? errorMessage;

    // Sorting state
    private Dictionary<string, string> emitidasSortableFields = new();
    private Dictionary<string, string> recibidasSortableFields = new();
    private string emitidasSortColumn = "";
    private string emitidasSortOrder = "";
    private string recibidasSortColumn = "";
    private string recibidasSortOrder = "";

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
