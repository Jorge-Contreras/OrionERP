using Microsoft.AspNetCore.Components;
using OrionERP.Web.Services;
using System;
using System.Collections.Generic;
using System.Linq;

namespace OrionERP.Web.Features.Cfdi.DeclaracionPrevia.Pages
{
  public partial class DeclaracionPrevia
  {
    [Inject] private IUiMessageService UiMessages { get; set; } = default!;

    // UI State
    private string? connectionString;

    // Filter state
    private List<string>? disponiblesRFCs;
    private List<int>? disponibleYears;
    private List<(int, string)>? disponibleMonths;
    private string? selectedRfc;
    private int selectedYear;
    private int selectedMonth;
    private bool isAnnual;

    // Data lists and other outputs
    private List<DeclaracionEmitida>? emitidas;
    private List<DeclaracionRecibida>? recibidas;
    private List<DesfaseItem>? desfase;
    private List<PolizaNoConsolidada>? polizasNoConsolidadas;
    private DeclaracionTotales? emitidasTotals;
    private DeclaracionTotales? recibidasTotals;
    private DesfaseTotales? desfaseTotals;
    private string? impuestosSummary;
    private string? bancosCajaSummary;

    // For UI selection and messages
    private DeclaracionEmitida? selectedEmitida;
    private DeclaracionRecibida? selectedRecibida;
    private string? statusMessage;
    private string? errorMessage;

    // Sorting state
    private Dictionary<string, string>? emitidasSortableFields;
    private Dictionary<string, string>? recibidasSortableFields;
    private string? emitidasSortColumn;
    private string? emitidasSortOrder;
    private string? recibidasSortColumn;
    private string? recibidasSortOrder;

    // Pagination state (simple implementation)
    private int pageSize = 50;
    private int emitidasCurrentPage = 1;
    private int emitidasPageCount = 1;
    private IEnumerable<DeclaracionEmitida> emitidasPage => emitidas?.Skip((emitidasCurrentPage - 1) * pageSize).Take(pageSize) ?? Enumerable.Empty<DeclaracionEmitida>();
    private int recibidasCurrentPage = 1;
    private int recibidasPageCount = 1;
    private IEnumerable<DeclaracionRecibida> recibidasPage => recibidas?.Skip((recibidasCurrentPage - 1) * pageSize).Take(pageSize) ?? Enumerable.Empty<DeclaracionRecibida>();

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
