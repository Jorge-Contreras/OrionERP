using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using OrionERP.Application.Features.Reservaciones.ListaReservaciones;
using OrionERP.Web.Services;

namespace OrionERP.Web.Features.Reservaciones.ListaReservaciones;

[Authorize(Roles = "Administrador,SatOperator")]
public partial class ListaReservacionesPage : ComponentBase
{
  private const int PageSize = 100;
  private const int QueryTake = PageSize + 1;

  [Inject] public IListaReservacionesService ReservacionesService { get; set; } = default!;
  [Inject] public IUiMessageService UiMessages { get; set; } = default!;
  [Inject] public IJSRuntime Js { get; set; } = default!;
  [Inject] public NavigationManager Nav { get; set; } = default!;

  protected ListaReservacionFilter Filter { get; set; } = new();
  protected List<ListaReservacionItemDto> Reservaciones { get; set; } = new();
  protected bool IsLoading { get; set; }
  protected bool IsLoadingMore { get; set; }
  protected bool HasMoreReservaciones { get; set; }
  protected string? ErrorMessage { get; set; }

  private DateTime? _checkInFrom;
  private DateTime? _checkInTo;
  protected bool IsBusy => IsLoading || IsLoadingMore;
  protected bool IsBusyDanger => IsBusy;

  protected string CheckInFromText
  {
    get => _checkInFrom?.ToString("yyyy-MM-dd") ?? string.Empty;
    set => _checkInFrom = ParseDate(value);
  }

  protected string CheckInToText
  {
    get => _checkInTo?.ToString("yyyy-MM-dd") ?? string.Empty;
    set => _checkInTo = ParseDate(value);
  }

  protected override async Task OnInitializedAsync()
  {
    await BuscarAsync();
  }

  protected async Task BuscarAsync()
  {
    IsLoading = true;
    ErrorMessage = null;
    HasMoreReservaciones = false;
    StateHasChanged();

    try
    {
      var page = await GetReservacionesPageAsync(0);
      Reservaciones = page.Items;
      HasMoreReservaciones = page.HasMore;
    }
    catch (Exception ex)
    {
      ErrorMessage = ex.Message;
      UiMessages.ShowError($"No se pudo cargar la lista de reservaciones. {ex.Message}");
    }
    finally
    {
      IsLoading = false;
      StateHasChanged();
    }
  }

  protected async Task LimpiarAsync()
  {
    Filter = new ListaReservacionFilter();
    _checkInFrom = null;
    _checkInTo = null;
    await BuscarAsync();
  }

  protected async Task CargarMasAsync()
  {
    if (IsBusy || !HasMoreReservaciones)
    {
      return;
    }

    IsLoadingMore = true;
    ErrorMessage = null;
    StateHasChanged();

    try
    {
      var page = await GetReservacionesPageAsync(Reservaciones.Count);
      Reservaciones.AddRange(page.Items);
      HasMoreReservaciones = page.HasMore;
    }
    catch (Exception ex)
    {
      ErrorMessage = ex.Message;
      UiMessages.ShowError($"No se pudieron cargar más reservaciones. {ex.Message}");
    }
    finally
    {
      IsLoadingMore = false;
      StateHasChanged();
    }
  }

  protected async Task NuevaReservacionAsync()
  {
    try
    {
      var cliente = await ReservacionesService.GetDefaultClienteForNewReservationAsync();
      if (cliente is null || cliente.Id <= 0)
      {
        UiMessages.ShowError("No se encontró un cliente de cotización para crear la reservación.");
        return;
      }

      var id = await ReservacionesService.CreateReservationAsync(new ListaReservacionCreateRequest
      {
        ClienteId = cliente.Id
      });

      UiMessages.ShowSuccess($"Reservación {id} creada con cliente {cliente.Nombre}.");
      Nav.NavigateTo($"/reservaciones/{id}");
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo crear la reservación. {ex.Message}");
    }
  }

  protected async Task BorrarVaciasAsync()
  {
    var confirm = await Js.InvokeAsync<bool>(
      "confirm",
      "¿Deseas borrar todas las reservaciones vacías (sin calendario, detalle y transacciones ligadas)?");

    if (!confirm)
    {
      return;
    }

    try
    {
      var result = await ReservacionesService.DeleteEmptyReservationsAsync();
      if (result.Success)
      {
        UiMessages.ShowSuccess(result.Message);
      }
      else
      {
        UiMessages.ShowError(result.Message);
      }
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudieron borrar las reservaciones vacías. {ex.Message}");
    }
    finally
    {
      await BuscarAsync();
    }
  }

  protected async Task AbrirReciboAsync(int reservationId)
  {
    var url = $"/reservaciones/recibo/{reservationId}";
    try
    {
      await Js.InvokeVoidAsync("open", url, "_blank", "noopener,noreferrer");
    }
    catch
    {
      Nav.NavigateTo(url);
    }
  }

  protected static string FormatDate(DateTime? value)
    => value.HasValue ? value.Value.ToString("d", CultureInfo.CurrentCulture) : string.Empty;

  protected static string Short(string? value, int max)
  {
    if (string.IsNullOrWhiteSpace(value))
      return string.Empty;

    return value.Length <= max ? value : value[..max] + "...";
  }

  protected void SetCheckInFrom(ChangeEventArgs args)
  {
    _checkInFrom = ParseDate(args.Value?.ToString());
  }

  protected void SetCheckInTo(ChangeEventArgs args)
  {
    _checkInTo = ParseDate(args.Value?.ToString());
  }

  private async Task<(List<ListaReservacionItemDto> Items, bool HasMore)> GetReservacionesPageAsync(int skip)
  {
    var rows = (await ReservacionesService.GetListaAsync(CreateQueryFilter(skip, QueryTake))).ToList();
    var hasMore = rows.Count > PageSize;
    if (hasMore)
    {
      rows = rows.Take(PageSize).ToList();
    }

    return (rows, hasMore);
  }

  private ListaReservacionFilter CreateQueryFilter(int skip, int take)
    => new()
    {
      Id = Filter.Id,
      Cliente = Filter.Cliente,
      Status = Filter.Status,
      CheckInFrom = _checkInFrom,
      CheckInTo = _checkInTo,
      IncluirCanceladas = Filter.IncluirCanceladas,
      Skip = skip,
      Take = take
    };

  private static DateTime? ParseDate(string? value)
  {
    if (string.IsNullOrWhiteSpace(value))
      return null;

    return DateTime.TryParse(value, out var parsed) ? parsed.Date : null;
  }
}
