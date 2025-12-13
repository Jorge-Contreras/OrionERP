using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using OrionERP.Application.Features.Cfdi.DeclaracionPrevia;

namespace OrionERP.Web.Features.Cfdi.DeclaracionPrevia.Pages
{
  public partial class DeclaracionPrevia
  {
    // Filter change handlers:
    private async Task OnFiltersChangedAsync()
    {
      _filtering = true;
      emitidasCurrentPage = 1;
      recibidasCurrentPage = 1;
      tipoEEmitidasCurrentPage = 1;
      tipoERecibidasCurrentPage = 1;
      try
      {
        await LoadAllData();
      }
      finally
      {
        _filtering = false;
      }
    }

    private async Task OnFilterChanged(ChangeEventArgs e)
    {
      // Whenever any filter (RFC, Year, Month, Annual) changes, reload data:
      await OnFiltersChangedAsync();
    }

    // Sorting:
    private void ApplySorting()
    {
      SortEmitidasList(emitidas);
      SortEmitidasList(emitidasNomina);
      SortEmitidasList(tipoEEmitidas);

      SortRecibidasList(recibidas);
      SortRecibidasList(recibidasNomina);
      SortRecibidasList(tipoERecibidas);
    }

    private void SortEmitidasList(List<DeclaracionEmitida>? items)
    {
      if (items == null)
      {
        return;
      }

      System.Comparison<DeclaracionEmitida> comparison = (a, b) => 0;
      switch (emitidasSortColumn)
      {
        case "Fecha":
          comparison = (a, b) => a.Fecha.CompareTo(b.Fecha);
          break;
        case "RECEPTOR":
          comparison = (a, b) => string.Compare(a.RECEPTOR, b.RECEPTOR, System.StringComparison.CurrentCultureIgnoreCase);
          break;
        case "Total":
          comparison = (a, b) => a.Total.CompareTo(b.Total);
          break;
        case "FOLIO_FISCAL":
          comparison = (a, b) => string.Compare(a.FOLIO_FISCAL, b.FOLIO_FISCAL, System.StringComparison.CurrentCultureIgnoreCase);
          break;
      }

      items.Sort(comparison);
      if (emitidasSortOrder == "DESC")
      {
        items.Reverse();
      }
    }

    private void SortRecibidasList(List<DeclaracionRecibida>? items)
    {
      if (items == null)
      {
        return;
      }

      System.Comparison<DeclaracionRecibida> comparison = (a, b) => 0;
      switch (recibidasSortColumn)
      {
        case "Fecha":
          comparison = (a, b) => a.Fecha.CompareTo(b.Fecha);
          break;
        case "EMISOR":
          comparison = (a, b) => string.Compare(a.EMISOR, b.EMISOR, System.StringComparison.CurrentCultureIgnoreCase);
          break;
        case "Total":
          comparison = (a, b) => a.Total.CompareTo(b.Total);
          break;
        case "FOLIO_FISCAL":
          comparison = (a, b) => string.Compare(a.FOLIO_FISCAL, b.FOLIO_FISCAL, System.StringComparison.CurrentCultureIgnoreCase);
          break;
      }

      items.Sort(comparison);
      if (recibidasSortOrder == "DESC")
      {
        items.Reverse();
      }
    }

    private async Task OnEmitidasSortsChangedAsync()
    {
      ApplySorting();
      emitidasCurrentPage = 1;
      tipoEEmitidasCurrentPage = 1;
      await LoadAllData();
    }

    private async Task OnRecibidasSortChangedAsync()
    {
      ApplySorting();
      recibidasCurrentPage = 1;
      tipoERecibidasCurrentPage = 1;
      await LoadAllData();
    }

    private async Task SortEmitidasByColumn(string columnName)
    {
        if (emitidasSortColumn == columnName)
        {
            emitidasSortOrder = emitidasSortOrder == "ASC" ? "DESC" : "ASC";
        }
        else
        {
            emitidasSortColumn = columnName;
            emitidasSortOrder = "ASC";
        }
        await OnEmitidasSortsChangedAsync();
    }

    private async Task SortRecibidasByColumn(string columnName)
    {
        if (recibidasSortColumn == columnName)
        {
            recibidasSortOrder = recibidasSortOrder == "ASC" ? "DESC" : "ASC";
        }
        else
        {
            recibidasSortColumn = columnName;
            recibidasSortOrder = "ASC";
        }
        await OnRecibidasSortChangedAsync();
    }

    private string GetSortIndicator(string columnName, string currentSortColumn, string currentSortOrder)
    {
        if (currentSortColumn != columnName) return "";
        return currentSortOrder == "ASC" ? "▲" : "▼";
    }
  }
}
