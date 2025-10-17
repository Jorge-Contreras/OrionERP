using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;

namespace OrionERP.Web.Features.Cfdi.DeclaracionPrevia.Pages
{
  public partial class DeclaracionPrevia
  {
    // Filter change handlers:
    private async Task OnFiltersChangedAsync()
    {
      emitidasCurrentPage = 1;
      recibidasCurrentPage = 1;
      await LoadAllData();
    }

    private async Task OnFilterChanged(ChangeEventArgs e)
    {
      // Whenever any filter (RFC, Year, Month, Annual) changes, reload data:
      await OnFiltersChangedAsync();
    }

    // Sorting:
    private void ApplySorting()
    {
      if (emitidas != null)
      {
        // Sort emitidas list based on emitidasSortColumn and emitidasSortOrder
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
        emitidas.Sort(comparison);
        if (emitidasSortOrder == "DESC")
        {
          emitidas.Reverse();
        }
      }
      if (recibidas != null)
      {
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
        recibidas.Sort(comparison);
        if (recibidasSortOrder == "DESC")
        {
          recibidas.Reverse();
        }
      }
    }

    private async Task OnEmitidasSortsChangedAsync()
    {
      ApplySorting();
      emitidasCurrentPage = 1;
      await LoadAllData();
    }
    private async Task OnEmitidasSortChanged(ChangeEventArgs e)
    {
      await OnEmitidasSortsChangedAsync();
    }
    private async Task OnRecibidasSortChangedAsync()
    {
      ApplySorting();
      recibidasCurrentPage = 1;
      await LoadAllData();
    }
    private async Task OnRecibidasSortChanged(ChangeEventArgs e)
    {
      await OnRecibidasSortChangedAsync();
    }
  }
}
