using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using OrionERP.Application.Features.Reservaciones.ListaReservaciones;

namespace OrionERP.Web.Features.Reservaciones.ListaReservaciones;

public partial class ReservacionPage
{
  internal void ToggleExtraForm()
  {
    if (ShowExtraForm && !IsEditingExtra)
    {
      ResetExtraEditor();
      return;
    }

    StartNewExtra();
  }

  internal void EditExtra(ReservacionExtraDto extra)
  {
    EditingExtraId = extra.Id;
    ExtraRoomId = extra.RoomId;
    ExtraPrice = extra.Price;
    ExtraDiscount = extra.Discount;
    ExtraNotes = extra.Notes;
    ShowExtraForm = true;
  }

  internal void OnExtraRoomChanged(ChangeEventArgs args)
  {
    if (!int.TryParse(args.Value?.ToString(), out var id))
    {
      ExtraRoomId = null;
      return;
    }

    ExtraRoomId = id;
    var room = Rooms.FirstOrDefault(r => r.Id == id);
    if (room is not null)
    {
      ExtraPrice = room.BasePrice;
    }
  }

  internal async Task GuardarExtraAsync()
  {
    if (!ExtraRoomId.HasValue)
    {
      UiMessages.ShowWarning("Selecciona una suite para el extra.");
      return;
    }

    ReservacionCommandResult result;
    if (IsEditingExtra)
    {
      result = await ReservacionesService.UpdateExtraAsync(new ReservacionExtraUpdateRequest
      {
        Id = EditingExtraId!.Value,
        ReservationId = ReservationId,
        RoomId = ExtraRoomId.Value,
        Price = ExtraPrice,
        Discount = ExtraDiscount,
        DiscountedPrice = ExtraTotal,
        Notes = ExtraNotes
      });
    }
    else
    {
      result = await ReservacionesService.AddExtraAsync(new ReservacionExtraCreateRequest
      {
        ReservationId = ReservationId,
        RoomId = ExtraRoomId.Value,
        Price = ExtraPrice,
        Discount = ExtraDiscount,
        DiscountedPrice = ExtraTotal,
        Notes = ExtraNotes
      });
    }

    if (result.Success)
    {
      UiMessages.ShowSuccess(result.Message);
      ResetExtraEditor();
      await RefreshExtrasAsync();
    }
    else
    {
      UiMessages.ShowError(result.Message);
    }
  }

  internal async Task EliminarExtraAsync(int extraId)
  {
    var confirm = await Js.InvokeAsync<bool>("confirm", "¿Eliminar el extra seleccionado?");
    if (!confirm)
    {
      return;
    }

    var result = await ReservacionesService.DeleteExtraAsync(extraId);
    if (result.Success)
    {
      if (EditingExtraId == extraId)
      {
        ResetExtraEditor();
      }

      UiMessages.ShowSuccess(result.Message);
      await RefreshExtrasAsync();
    }
    else
    {
      UiMessages.ShowError(result.Message);
    }
  }

  internal void CancelExtraEdit()
  {
    ResetExtraEditor();
  }

  internal async Task RefreshExtrasAsync()
  {
    Extras = await ReservacionesService.GetExtrasAsync(ReservationId);
    RecalculateTotals();
  }

  private void StartNewExtra()
  {
    EditingExtraId = null;
    ExtraRoomId = null;
    ExtraPrice = 0m;
    ExtraDiscount = 0m;
    ExtraNotes = null;
    ShowExtraForm = true;
  }

  private void ResetExtraEditor()
  {
    EditingExtraId = null;
    ExtraRoomId = null;
    ExtraPrice = 0m;
    ExtraDiscount = 0m;
    ExtraNotes = null;
    ShowExtraForm = false;
  }
}
