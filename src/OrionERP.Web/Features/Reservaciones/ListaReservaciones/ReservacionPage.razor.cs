using System;
using System.Globalization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using OrionERP.Application.Features.Reservaciones.ListaReservaciones;
using OrionERP.Web.Services;

namespace OrionERP.Web.Features.Reservaciones.ListaReservaciones;

[Authorize(Roles = "Administrador,SatOperator")]
public partial class ReservacionPage : ComponentBase
{
  [Parameter] public int ReservationId { get; set; }

  [Inject] public IListaReservacionesService ReservacionesService { get; set; } = default!;
  [Inject] public IUiMessageService UiMessages { get; set; } = default!;

  protected ReservacionDetailDto? Detail { get; set; }
  protected bool IsLoading { get; set; }
  protected bool IsSavingNotes { get; set; }
  protected string? ErrorMessage { get; set; }
  protected string? Notes { get; set; }

  protected override async Task OnParametersSetAsync()
  {
    await CargarAsync();
  }

  protected async Task GuardarNotasAsync()
  {
    if (Detail is null)
    {
      return;
    }

    IsSavingNotes = true;
    try
    {
      var result = await ReservacionesService.UpdateNotesAsync(Detail.Id, Notes);
      if (!result.Success)
      {
        UiMessages.ShowError(result.Message);
        return;
      }

      Detail.Notes = Notes;
      UiMessages.ShowSuccess(result.Message);
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudieron actualizar las notas. {ex.Message}");
    }
    finally
    {
      IsSavingNotes = false;
    }
  }

  protected static string FormatDate(DateTime? value)
    => value.HasValue ? value.Value.ToString("d", CultureInfo.CurrentCulture) : string.Empty;

  private async Task CargarAsync()
  {
    IsLoading = true;
    ErrorMessage = null;

    try
    {
      Detail = await ReservacionesService.GetReservacionDetailAsync(ReservationId);
      Notes = Detail?.Notes;
    }
    catch (Exception ex)
    {
      ErrorMessage = ex.Message;
    }
    finally
    {
      IsLoading = false;
    }
  }
}
