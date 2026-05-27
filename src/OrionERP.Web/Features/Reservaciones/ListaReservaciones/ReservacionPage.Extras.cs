using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using OrionERP.Application.Features.Reservaciones.ListaReservaciones;

namespace OrionERP.Web.Features.Reservaciones.ListaReservaciones;

public partial class ReservacionPage
{
  internal void OpenNewExtraModal()
  {
    StartNewExtra();
  }

  internal void EditExtra(ReservacionExtraDto extra)
  {
    EditingExtraId = extra.Id;
    ExtraCatalogId = extra.ExtraId;
    ExtraUnitPrice = extra.UnitPrice;
    ExtraQuantity = extra.Quantity <= 0 ? 1 : extra.Quantity;
    ExtraNotes = extra.Notes;
    ShowExtraModal = true;
  }

  internal void OnExtraCatalogChanged(ChangeEventArgs args)
  {
    if (!int.TryParse(args.Value?.ToString(), out var id))
    {
      ExtraCatalogId = null;
      return;
    }

    ExtraCatalogId = id;
    var extra = ExtraCatalog.FirstOrDefault(item => item.ExtraId == id);
    if (extra is not null)
    {
      ExtraUnitPrice = extra.Price;
    }
  }

  internal void ConvertExtraPriceToSubtotal()
  {
    if (ExtraUnitPrice == 0m)
    {
      return;
    }

    ExtraUnitPrice = decimal.Round(ExtraUnitPrice / 1.16m, 2, MidpointRounding.ToEven);
  }

  internal async Task GuardarExtraAsync()
  {
    if (!ExtraCatalogId.HasValue || ExtraCatalogId.Value <= 0)
    {
      UiMessages.ShowWarning("Selecciona un extra del catálogo.");
      return;
    }

    if (ExtraQuantity <= 0)
    {
      UiMessages.ShowWarning("La cantidad del extra debe ser mayor a cero.");
      return;
    }

    if (ExtraUnitPrice < 0m)
    {
      UiMessages.ShowWarning("El precio del extra no puede ser negativo.");
      return;
    }

    ReservacionCommandResult result;
    if (IsEditingExtra)
    {
      result = await ReservacionesService.UpdateExtraAsync(new ReservacionExtraUpdateRequest
      {
        Id = EditingExtraId!.Value,
        ReservationId = ReservationId,
        ExtraId = ExtraCatalogId.Value,
        UnitPrice = ExtraUnitPrice,
        Quantity = ExtraQuantity,
        Notes = ExtraNotes
      });
    }
    else
    {
      result = await ReservacionesService.AddExtraAsync(new ReservacionExtraCreateRequest
      {
        ReservationId = ReservationId,
        ExtraId = ExtraCatalogId.Value,
        UnitPrice = ExtraUnitPrice,
        Quantity = ExtraQuantity,
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
    await InvokeAsync(StateHasChanged);
  }

  private void StartNewExtra()
  {
    EditingExtraId = null;
    ExtraCatalogId = null;
    ExtraUnitPrice = 0m;
    ExtraQuantity = 1;
    ExtraNotes = null;
    ShowExtraModal = true;
  }

  private void ResetExtraEditor()
  {
    EditingExtraId = null;
    ExtraCatalogId = null;
    ExtraUnitPrice = 0m;
    ExtraQuantity = 1;
    ExtraNotes = null;
    ShowExtraModal = false;
  }
}
