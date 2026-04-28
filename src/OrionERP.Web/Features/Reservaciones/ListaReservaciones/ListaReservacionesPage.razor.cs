using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using OrionERP.Application.Features.Reservaciones.CalendarSync;
using OrionERP.Application.Features.Reservaciones.ListaReservaciones;
using OrionERP.Web.Services;

namespace OrionERP.Web.Features.Reservaciones.ListaReservaciones;

[Authorize(Roles = "Administrador,SatOperator")]
public partial class ListaReservacionesPage : ComponentBase, IDisposable
{
  private const int PageSize = 100;
  private const int QueryTake = PageSize + 1;
  private const int FilterInputDebounceMs = 300;

  [Inject] public IListaReservacionesService ReservacionesService { get; set; } = default!;
  [Inject] public IBonhomiaRoomCalendarSyncService BonhomiaRoomCalendarSyncService { get; set; } = default!;
  [Inject] public IUiMessageService UiMessages { get; set; } = default!;
  [Inject] public IJSRuntime Js { get; set; } = default!;
  [Inject] public NavigationManager Nav { get; set; } = default!;

  protected ListaReservacionFilter Filter { get; set; } = new();
  protected List<ListaReservacionItemDto> Reservaciones { get; set; } = new();
  protected bool IsLoading { get; set; }
  protected bool IsLoadingMore { get; set; }
  protected bool IsSyncingAirbnb { get; set; }
  protected bool HasMoreReservaciones { get; set; }
  protected int? SelectedReservacionId { get; set; }
  protected string? ErrorMessage { get; set; }

  private DateTime? _checkInFrom;
  private DateTime? _checkInTo;
  private CancellationTokenSource? _filterSearchDebounceCts;
  private int _searchVersion;
  protected bool IsBusy => IsLoading || IsLoadingMore || IsSyncingAirbnb;
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
    CancelPendingFilterSearch();

    var searchVersion = Interlocked.Increment(ref _searchVersion);
    IsLoading = true;
    ErrorMessage = null;
    HasMoreReservaciones = false;
    SelectedReservacionId = null;
    StateHasChanged();

    try
    {
      var page = await GetReservacionesPageAsync(0);
      if (searchVersion != _searchVersion)
      {
        return;
      }

      Reservaciones = page.Items;
      HasMoreReservaciones = page.HasMore;
    }
    catch (Exception ex)
    {
      if (searchVersion != _searchVersion)
      {
        return;
      }

      ErrorMessage = ex.Message;
      UiMessages.ShowError($"No se pudo cargar la lista de reservaciones. {ex.Message}");
    }
    finally
    {
      if (searchVersion == _searchVersion)
      {
        IsLoading = false;
        StateHasChanged();
      }
    }
  }

  protected async Task LimpiarAsync()
  {
    CancelPendingFilterSearch();
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

    var searchVersion = _searchVersion;
    IsLoadingMore = true;
    ErrorMessage = null;
    StateHasChanged();

    try
    {
      var page = await GetReservacionesPageAsync(Reservaciones.Count);
      if (searchVersion != _searchVersion)
      {
        return;
      }

      Reservaciones.AddRange(page.Items);
      HasMoreReservaciones = page.HasMore;
    }
    catch (Exception ex)
    {
      if (searchVersion != _searchVersion)
      {
        return;
      }

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

  protected async Task SyncConAirbnbAsync()
  {
    if (IsBusy)
    {
      return;
    }

    IsSyncingAirbnb = true;
    ErrorMessage = null;
    StateHasChanged();

    try
    {
      var today = DateTime.Today;
      var endDateExclusive = new DateTime(today.Year + 1, 1, 1);
      var result = await BonhomiaRoomCalendarSyncService.SyncAsync(today, endDateExclusive);
      var summary = BuildSyncSummary(result);

      if (result.ErrorCount >= result.Rooms.Count && result.Rooms.Count > 0)
      {
        UiMessages.ShowError(summary);
      }
      else if (result.ErrorCount > 0)
      {
        UiMessages.ShowWarning(summary);
      }
      else
      {
        UiMessages.ShowSuccess(summary);
      }
    }
    catch (Exception ex)
    {
      ErrorMessage = ex.Message;
      UiMessages.ShowError($"No se pudo sincronizar Outlook/Airbnb. {ex.Message}");
    }
    finally
    {
      IsSyncingAirbnb = false;
      StateHasChanged();
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

  protected void SelectReservacionRow(int reservationId)
    => SelectedReservacionId = reservationId;

  protected string GetReservacionRowClass(int reservationId)
    => SelectedReservacionId == reservationId
      ? "reservaciones-list-row reservaciones-list-row-selected"
      : "reservaciones-list-row";

  protected static string FormatDate(DateTime? value)
    => value.HasValue ? value.Value.ToString("d", CultureInfo.CurrentCulture) : string.Empty;

  protected static string Short(string? value, int max)
  {
    if (string.IsNullOrWhiteSpace(value))
      return string.Empty;

    return value.Length <= max ? value : value[..max] + "...";
  }

  protected Task OnFilterChangedAsync()
    => BuscarAsync();

  protected async Task OnFilterInputChangedAsync()
  {
    CancelPendingFilterSearch();
    _filterSearchDebounceCts = new CancellationTokenSource();
    var localCts = _filterSearchDebounceCts;

    try
    {
      await Task.Delay(TimeSpan.FromMilliseconds(FilterInputDebounceMs), localCts.Token);
      if (!ReferenceEquals(_filterSearchDebounceCts, localCts) || localCts.IsCancellationRequested)
      {
        return;
      }
    }
    catch (TaskCanceledException)
    {
      return;
    }
    finally
    {
      if (ReferenceEquals(_filterSearchDebounceCts, localCts))
      {
        _filterSearchDebounceCts = null;
      }
    }

    await BuscarAsync();
  }

  protected async Task OnCheckInFromChangedAsync(ChangeEventArgs args)
  {
    _checkInFrom = ParseDate(args.Value?.ToString());
    await OnFilterChangedAsync();
  }

  protected async Task OnCheckInToChangedAsync(ChangeEventArgs args)
  {
    _checkInTo = ParseDate(args.Value?.ToString());
    await OnFilterChangedAsync();
  }

  public void Dispose()
  {
    CancelPendingFilterSearch();
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

  private void CancelPendingFilterSearch()
  {
    _filterSearchDebounceCts?.Cancel();
    _filterSearchDebounceCts?.Dispose();
    _filterSearchDebounceCts = null;
  }

  private static string BuildSyncSummary(BonhomiaRoomCalendarSyncResult result)
  {
    var summary = $"Sync Outlook/Airbnb: {result.CreatedCount} creados, {result.UpdatedCount} actualizados, {result.DeletedCount} borrados, {result.SkippedCount} sin cambios.";
    if (result.RecoveredMappingCount > 0)
    {
      summary += $" {result.RecoveredMappingCount} mapeos recuperados.";
    }

    if (result.ErrorCount <= 0)
    {
      return summary;
    }

    var errorRooms = result.Rooms
      .Where(item => !string.IsNullOrWhiteSpace(item.ErrorMessage))
      .Select(item => item.RoomName)
      .ToArray();

    return errorRooms.Length > 0
      ? $"{summary} Con errores en: {string.Join(", ", errorRooms)}."
      : $"{summary} Se detectaron {result.ErrorCount} errores.";
  }
}
