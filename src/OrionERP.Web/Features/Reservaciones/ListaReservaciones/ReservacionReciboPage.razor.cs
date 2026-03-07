using System;
using System.Globalization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using OrionERP.Application.Features.Reservaciones.ListaReservaciones;

namespace OrionERP.Web.Features.Reservaciones.ListaReservaciones;

[Authorize(Roles = "Administrador,SatOperator")]
public partial class ReservacionReciboPage : ComponentBase
{
  [Parameter] public int ReservationId { get; set; }

  [Inject] public IListaReservacionesService ReservacionesService { get; set; } = default!;
  [Inject] public IJSRuntime Js { get; set; } = default!;

  protected ReservacionDetailDto? Detail { get; set; }
  protected bool IsLoading { get; set; }

  protected override async Task OnParametersSetAsync()
  {
    IsLoading = true;
    try
    {
      Detail = await ReservacionesService.GetReservacionDetailAsync(ReservationId);
    }
    finally
    {
      IsLoading = false;
    }
  }

  protected async Task PrintAsync()
    => await Js.InvokeVoidAsync("print");

  protected static string FormatDate(DateTime? value)
    => value.HasValue ? value.Value.ToString("d", CultureInfo.CurrentCulture) : string.Empty;
}
