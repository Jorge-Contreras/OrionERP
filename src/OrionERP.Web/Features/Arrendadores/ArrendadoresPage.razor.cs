using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Identity;
using Microsoft.JSInterop;
using OrionERP.Application.Features.Arrendadores;
using OrionERP.Infrastructure.Auth;
using OrionERP.Web.Services;

namespace OrionERP.Web.Features.Arrendadores;

public partial class ArrendadoresPage : ComponentBase
{
  private static readonly CultureInfo MoneyCulture = CultureInfo.GetCultureInfo("es-MX");
  private static readonly ArrendadorEstadoCuentaResumenDto EmptySummary = new();

  [Inject] private IArrendadoresEstadoCuentaService ArrendadoresService { get; set; } = default!;
  [Inject] private IArrendadorEstadoCuentaPdfService PdfService { get; set; } = default!;
  [Inject] private AuthenticationStateProvider AuthenticationStateProvider { get; set; } = default!;
  [Inject] private UserManager<ApplicationUser> UserManager { get; set; } = default!;
  [Inject] private IJSRuntime Js { get; set; } = default!;
  [Inject] private IUiMessageService UiMessages { get; set; } = default!;

  protected List<ArrendadorListItemDto> Arrendadores { get; set; } = [];
  protected List<ArrendadorRoomListItemDto> Rooms { get; set; } = [];
  protected ArrendadorEstadoCuentaDto? Report { get; set; }
  protected string SearchText { get; set; } = string.Empty;
  protected int? SelectedArrendadorId { get; set; }
  protected int? SelectedRoomId { get; set; }
  protected int SelectedYear { get; set; } = DateTime.Today.Year;
  protected int SelectedMonth { get; set; } = DateTime.Today.Month;
  protected bool IsLoadingArrendadores { get; set; }
  protected bool IsLoadingRooms { get; set; }
  protected bool IsLoadingReport { get; set; }
  protected bool IsGeneratingPdf { get; set; }
  protected bool IsAdministrator { get; set; }
  protected bool IsArrendadorOnly { get; set; }
  protected int? CurrentArrendadorProveedorId { get; set; }
  protected string? AccessMessage { get; set; }

  private int? OwnerIdScope => IsArrendadorOnly ? CurrentArrendadorProveedorId : null;

  protected bool CanSearchArrendadores => !IsArrendadorOnly;

  protected bool CanGenerate => HasUsableScope
    && SelectedArrendadorId.HasValue
    && SelectedRoomId.HasValue
    && IsOwnerAllowed(SelectedArrendadorId.Value)
    && SelectedYear is >= 2000 and <= 2100
    && SelectedMonth is >= 1 and <= 12;

  private bool HasUsableScope => !IsArrendadorOnly || CurrentArrendadorProveedorId.HasValue;

  protected IReadOnlyList<MonthOption> MonthOptions { get; } = Enumerable
    .Range(1, 12)
    .Select(month => new MonthOption(month, CultureInfo.GetCultureInfo("es-MX").DateTimeFormat.GetMonthName(month)))
    .ToList();

  protected override async Task OnInitializedAsync()
  {
    await ResolveCurrentUserAsync();
    await BuscarArrendadoresAsync();
  }

  protected async Task BuscarArrendadoresAsync()
  {
    if (!HasUsableScope)
    {
      Arrendadores = [];
      Rooms = [];
      Report = null;
      AccessMessage = "Tu usuario tiene el rol Arrendadores, pero no tiene un proveedor ligado.";
      return;
    }

    IsLoadingArrendadores = true;
    try
    {
      Arrendadores = (await ArrendadoresService.GetArrendadoresAsync(SearchText, OwnerIdScope)).ToList();

      if (IsArrendadorOnly && CurrentArrendadorProveedorId.HasValue)
      {
        var scopedOwnerId = CurrentArrendadorProveedorId.Value;
        if (!SelectedArrendadorId.HasValue || SelectedArrendadorId.Value != scopedOwnerId)
        {
          await SeleccionarArrendadorAsync(scopedOwnerId);
        }
      }
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo cargar la lista de arrendadores. {ex.Message}");
    }
    finally
    {
      IsLoadingArrendadores = false;
    }
  }

  protected Task OnSearchKeyUpAsync(KeyboardEventArgs args)
    => args.Key == "Enter" ? BuscarArrendadoresAsync() : Task.CompletedTask;

  protected async Task SeleccionarArrendadorAsync(int ownerId)
  {
    if (!IsOwnerAllowed(ownerId))
    {
      UiMessages.ShowWarning("No tienes acceso a este arrendador.");
      return;
    }

    SelectedArrendadorId = ownerId;
    SelectedRoomId = null;
    Rooms = [];
    Report = null;

    IsLoadingRooms = true;
    try
    {
      Rooms = (await ArrendadoresService.GetRoomsAsync(ownerId, OwnerIdScope)).ToList();
      if (Rooms.Count == 1)
      {
        SelectedRoomId = Rooms[0].RoomId;
      }
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudieron cargar las propiedades del arrendador. {ex.Message}");
    }
    finally
    {
      IsLoadingRooms = false;
    }
  }

  protected void SeleccionarRoom(int roomId)
  {
    if (!Rooms.Any(room => room.RoomId == roomId))
    {
      UiMessages.ShowWarning("No tienes acceso a esta propiedad.");
      return;
    }

    SelectedRoomId = roomId;
    Report = null;
  }

  protected async Task GenerarAsync()
  {
    if (!CanGenerate)
    {
      UiMessages.ShowWarning("Selecciona arrendador, propiedad, anio y mes.");
      return;
    }

    IsLoadingReport = true;
    try
    {
      Report = await ArrendadoresService.GetEstadoCuentaAsync(
        SelectedArrendadorId!.Value,
        SelectedRoomId!.Value,
        SelectedYear,
        SelectedMonth,
        OwnerIdScope);

      if (Report.Context is null)
      {
        UiMessages.ShowWarning("No se encontro la propiedad para el arrendador seleccionado.");
      }
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo generar el estado de cuenta. {ex.Message}");
    }
    finally
    {
      IsLoadingReport = false;
    }
  }

  protected async Task AbrirPdfAsync()
  {
    if (Report?.Context is null || Report.Summary is null || IsGeneratingPdf)
    {
      UiMessages.ShowWarning("Genera primero el estado de cuenta.");
      return;
    }

    if (!IsOwnerAllowed(Report.Context.OwnerId))
    {
      UiMessages.ShowWarning("No tienes acceso al estado de cuenta seleccionado.");
      return;
    }

    IsGeneratingPdf = true;
    try
    {
      var document = BuildPdfDocument(Report);
      var pdfBytes = PdfService.Generate(document);
      var fileName = BuildPdfFileName(Report.Context);
      var dataUrl = $"data:application/pdf;base64,{Convert.ToBase64String(pdfBytes)}";

      await Js.InvokeVoidAsync("triggerFileDownload", fileName, dataUrl);
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo generar el PDF. {ex.Message}");
    }
    finally
    {
      IsGeneratingPdf = false;
    }
  }

  protected static string Money(decimal value)
    => value.ToString("C2", MoneyCulture);

  protected static string Date(DateTime value)
    => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

  private static ArrendadorEstadoCuentaPdfDocumentModel BuildPdfDocument(ArrendadorEstadoCuentaDto report)
  {
    var context = report.Context ?? throw new InvalidOperationException("El estado de cuenta no tiene contexto.");
    var summary = report.Summary ?? EmptySummary;
    var periodo = $"{context.Year}-{context.Month:00}";

    return new ArrendadorEstadoCuentaPdfDocumentModel(
      context.RazonSocial,
      context.RoomName,
      periodo,
      DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
      summary.NochesOcupadas.ToString("N0", MoneyCulture),
      Money(summary.Cobrado),
      Money(summary.Arrendador30),
      Money(summary.Isr10),
      Money(summary.PagoFinalArrendador),
      report.Details.Select(item => new ArrendadorEstadoCuentaPdfDetalleRow(
        Date(item.Noche),
        item.HuespedOBloqueo ?? string.Empty,
        item.ReservationId.ToString(CultureInfo.InvariantCulture),
        Date(item.CheckIn),
        Date(item.CheckOut),
        Money(item.CobradoNoche),
        Money(item.Arrendador30),
        Money(item.Isr10),
        Money(item.PagoFinalArrendador))).ToList(),
      report.Exclusions.Select(item => new ArrendadorEstadoCuentaPdfExclusionRow(
        Date(item.Noche),
        item.HuespedOBloqueo ?? string.Empty,
        item.ReservationId?.ToString(CultureInfo.InvariantCulture) ?? "N/A",
        Money(item.CobradoNoche),
        item.MotivoExclusion)).ToList());
  }

  private static string BuildPdfFileName(ArrendadorEstadoCuentaContextDto context)
  {
    var room = NormalizeFileNamePart(context.RoomName);
    var owner = NormalizeFileNamePart(context.RazonSocial);
    return $"estado-cuenta-{owner}-{room}-{context.Year}-{context.Month:00}.pdf";
  }

  private static string NormalizeFileNamePart(string value)
  {
    var normalized = new string(value
      .Trim()
      .ToLowerInvariant()
      .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')
      .ToArray());

    while (normalized.Contains("--", StringComparison.Ordinal))
    {
      normalized = normalized.Replace("--", "-", StringComparison.Ordinal);
    }

    return normalized.Trim('-');
  }

  private async Task ResolveCurrentUserAsync()
  {
    var authState = await AuthenticationStateProvider.GetAuthenticationStateAsync();
    var user = authState.User;

    IsAdministrator = user.IsInRole("Administrador");
    IsArrendadorOnly = user.IsInRole("Arrendadores") && !IsAdministrator;

    var appUser = await UserManager.GetUserAsync(user);
    CurrentArrendadorProveedorId = appUser?.ArrendadorProveedorId;

    if (IsArrendadorOnly && !CurrentArrendadorProveedorId.HasValue)
    {
      AccessMessage = "Tu usuario tiene el rol Arrendadores, pero no tiene un proveedor ligado.";
    }
  }

  private bool IsOwnerAllowed(int ownerId)
    => !IsArrendadorOnly || CurrentArrendadorProveedorId == ownerId;

  protected sealed record MonthOption(int Value, string Label);
}
