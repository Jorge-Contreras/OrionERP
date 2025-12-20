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
  }
}
