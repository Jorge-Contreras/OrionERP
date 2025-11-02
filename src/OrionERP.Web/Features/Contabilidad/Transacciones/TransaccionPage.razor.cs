using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using OrionERP.Application.Features.Contabilidad.Transacciones;
using OrionERP.Web.Services;
using OrionERP.Web.State;

namespace OrionERP.Web.Features.Contabilidad.Transacciones;

public partial class TransaccionPage : ComponentBase, IDisposable
{
  private CancellationTokenSource? _loadCts;
  private TransaccionHeaderModel? _headerOriginal;
  private MovimientoModel? _movimientoTarget;
  private bool _rfcInitialized;
  private bool _isDisposed;

  [Parameter] public int Id { get; set; }

  [Inject] public ITransaccionService TransaccionService { get; set; } = default!;
  [Inject] public IUiMessageService UiMessages { get; set; } = default!;
  [Inject] public IUserRfcState RfcState { get; set; } = default!;

  protected TransaccionHeaderModel? Header { get; private set; }
  protected EditContext? HeaderEditContext { get; private set; }
  protected bool IsLoading { get; private set; } = true;
  protected bool IsSavingHeader { get; private set; }
  protected string? ErrorMessage { get; private set; }

  protected MovimientoTotalsDto Totals { get; private set; } = new();
  protected List<MovimientoModel> Movimientos { get; } = new();
  protected List<AttachmentModel> Attachments { get; } = new();
  protected List<ComprobanteModel> Comprobantes { get; } = new();

  protected bool ShowMovimientoModal { get; private set; }
  protected MovimientoModel? MovimientoDraft { get; private set; }
  protected EditContext? MovimientoEditContext { get; private set; }
  protected string MovimientoModalTitle => _movimientoTarget is null ? "Agregar movimiento" : "Editar movimiento";

  protected string HeaderStatus => Totals.Balance == 0m ? "Balanceada" : "Desbalanceada";
  protected string HeaderStatusCss => Totals.Balance == 0m ? "text-bg-success" : "text-bg-warning";

  protected override void OnInitialized()
  {
    _rfcInitialized = RfcState.AllowedRfcs.Any() || RfcState.CurrentRfc is not null;
    if (!_rfcInitialized)
    {
      IsLoading = true;
    }

    RfcState.Changed += OnRfcStateChanged;
  }

  protected override async Task OnParametersSetAsync()
  {
    if (!_rfcInitialized)
    {
      IsLoading = true;
      return;
    }

    await PerformLoadAsync();
  }

  private async Task PerformLoadAsync()
  {
    _loadCts?.Cancel();
    _loadCts?.Dispose();
    _loadCts = new CancellationTokenSource();

    await LoadAsync(_loadCts.Token);
  }

  private async Task LoadAsync(CancellationToken ct = default)
  {
    IsLoading = true;
    ErrorMessage = null;
    UiMessages.Clear();

    try
    {
      var headerDto = await TransaccionService.GetHeaderAsync(Id, ct);
      if (headerDto is null)
      {
        Header = null;
        _headerOriginal = null;
        Movimientos.Clear();
        Attachments.Clear();
        Comprobantes.Clear();
        Totals = new MovimientoTotalsDto();
        ErrorMessage = "No se encontró la transacción solicitada.";
        return;
      }

      Header = new TransaccionHeaderModel
      {
        Id = headerDto.Id,
        Folio = headerDto.Id.ToString("0000", CultureInfo.InvariantCulture),
        Rfc = headerDto.Rfc,
        Fecha = headerDto.Fecha,
        Cuenta = headerDto.Cuenta,
        Concepto = headerDto.Concepto,
        Monto = headerDto.Monto,
        ComprobanteId = headerDto.ComprobanteId,
        ComprobanteMonto = headerDto.ComprobanteMonto
      };
      _headerOriginal = Header.Clone();
      HeaderEditContext = new EditContext(Header);

      Movimientos.Clear();
      var movimientosDto = await TransaccionService.GetMovimientosAsync(Id, ct);
      Movimientos.AddRange(movimientosDto.Select(m => new MovimientoModel
      {
        Id = m.Id,
        NombreCuenta = m.NombreCuenta,
        Concepto = m.Concepto,
        Debe = m.Debe,
        Haber = m.Haber
      }));

      Totals = await TransaccionService.GetMovimientoTotalsAsync(Id, ct);
      Header.Status = HeaderStatus;

      Attachments.Clear();
      var attachmentsDto = await TransaccionService.GetAttachmentsAsync(Id, ct);
      Attachments.AddRange(attachmentsDto.Select(a => new AttachmentModel
      {
        Nombre = string.IsNullOrWhiteSpace(a.AttachmentName) ? $"Adjunto {a.Id}" : a.AttachmentName!,
        Extension = string.IsNullOrWhiteSpace(a.AttachmentExtension) ? "-" : a.AttachmentExtension!,
        TamanoBytes = a.Length ?? 0
      }));

      Comprobantes.Clear();
      var comprobantesDto = await TransaccionService.GetComprobantesAsync(Id, ct);
      Comprobantes.AddRange(comprobantesDto.Select(c => new ComprobanteModel
      {
        ComprobanteId = c.ComprobanteId,
        Serie = c.Serie ?? string.Empty,
        Folio = c.Folio ?? string.Empty,
        Fecha = c.Fecha,
        Total = c.Total,
        Vinculado = c.Vinculado
      }));
    }
    catch (OperationCanceledException)
    {
      // ignored
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

  protected string GetMovimientoRowClass(MovimientoModel movimiento)
    => _movimientoTarget == movimiento ? "table-active" : string.Empty;

  protected void ResetHeader()
  {
    if (_headerOriginal is null || Header is null)
      return;

    Header.CopyFrom(_headerOriginal);
    HeaderEditContext = new EditContext(Header);
    StateHasChanged();
  }

  protected async Task SaveHeaderAsync()
  {
    if (HeaderEditContext is null || Header is null)
      return;

    if (!HeaderEditContext.Validate())
      return;

    IsSavingHeader = true;
    try
    {
      var request = new TransaccionGuardarCerrarRequest
      {
        TransaccionId = Header.Id,
        Concepto = Header.Concepto,
        Fecha = Header.Fecha,
        Cuenta = Header.Cuenta,
        Monto = Header.Monto
      };

      var result = await TransaccionService.GuardarYCerrarAsync(request);
      if (!result.Success)
      {
        UiMessages.ShowError(result.Message ?? "No se pudo guardar la transacción.");
        return;
      }

      if (result.Totals is not null)
      {
        Totals = result.Totals;
        Header.Status = HeaderStatus;
      }

      _headerOriginal = Header.Clone();
      UiMessages.ShowSuccess(result.Message ?? "Datos de la transacción guardados.");
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"Error al guardar: {ex.Message}");
    }
    finally
    {
      IsSavingHeader = false;
    }
  }

  protected void OpenMovimientoModal(MovimientoModel? existing)
  {
    if (existing is null)
    {
      _movimientoTarget = null;
      MovimientoDraft = new MovimientoModel
      {
        Debe = Header?.Monto ?? 0m,
        Haber = 0m,
        Concepto = Header?.Concepto,
       };
    }
    else
    {
      _movimientoTarget = existing;
      MovimientoDraft = existing.Clone();
    }

    MovimientoEditContext = new EditContext(MovimientoDraft!);
    ShowMovimientoModal = true;
  }

  protected void CloseMovimientoModal()
  {
    ShowMovimientoModal = false;
    MovimientoDraft = null;
    MovimientoEditContext = null;
    _movimientoTarget = null;
  }

  protected async Task SaveMovimientoAsync()
  {
    if (MovimientoEditContext is null || MovimientoDraft is null)
      return;

    if (!MovimientoEditContext.Validate())
      return;

    if (_movimientoTarget is null)
    {
      MovimientoDraft.Id = Movimientos.Count == 0 ? 1 : Movimientos.Max(m => m.Id) + 1;
      Movimientos.Add(MovimientoDraft.Clone());
    }
    else
    {
      _movimientoTarget.CopyFrom(MovimientoDraft);
    }

    UiMessages.ShowSuccess("Movimiento guardado.");
    CloseMovimientoModal();
    await InvokeAsync(StateHasChanged);
  }

  protected void CopyMontoToMovimiento()
  {
    if (MovimientoDraft is null || Header is null)
      return;

    MovimientoDraft.Debe = Header.Monto;
    MovimientoEditContext?.NotifyFieldChanged(new FieldIdentifier(MovimientoDraft, nameof(MovimientoDraft.Debe)));
  }

  protected void CopyConceptoFromHeader()
  {
    if (MovimientoDraft is null || Header is null)
      return;

    MovimientoDraft.Concepto = Header.Concepto;
    MovimientoEditContext?.NotifyFieldChanged(new FieldIdentifier(MovimientoDraft, nameof(MovimientoDraft.Concepto)));
  }

  public void Dispose()
  {
    if (_isDisposed)
      return;

    _isDisposed = true;
    RfcState.Changed -= OnRfcStateChanged;
    _loadCts?.Cancel();
    _loadCts?.Dispose();
  }

  private async void OnRfcStateChanged()
  {
    if (_isDisposed)
      return;

    _rfcInitialized = true;

    await InvokeAsync(async () =>
    {
      await PerformLoadAsync();
      StateHasChanged();
    });
  }

  protected sealed class TransaccionHeaderModel
  {
    public int Id { get; set; }
    public string? Folio { get; set; }
    public string? Rfc { get; set; }

    [Required(ErrorMessage = "La fecha es obligatoria.")]
    public DateTime Fecha { get; set; }

    [Required(ErrorMessage = "Captura una cuenta.")]
    public string? Cuenta { get; set; }

    [Required(ErrorMessage = "Captura un concepto.")]
    public string? Concepto { get; set; }

    [Range(typeof(decimal), "0", "79228162514264337593543950335", ErrorMessage = "Monto inválido.")]
    public decimal Monto { get; set; }

    public int? ComprobanteId { get; set; }
    public decimal? ComprobanteMonto { get; set; }
    public string Status { get; set; } = "Desconocido";

    public string DisplayFolio => string.IsNullOrWhiteSpace(Folio)
      ? Id.ToString(CultureInfo.InvariantCulture)
      : Folio;

    public string ComprobanteResumen
      => ComprobanteId is null
        ? "Sin comprobante"
        : ComprobanteMonto.HasValue
          ? $"#{ComprobanteId} · {ComprobanteMonto.Value:N2}"
          : $"#{ComprobanteId}";

    public TransaccionHeaderModel Clone()
      => (TransaccionHeaderModel)MemberwiseClone();

    public void CopyFrom(TransaccionHeaderModel other)
    {
      Id = other.Id;
      Folio = other.Folio;
      Rfc = other.Rfc;
      Fecha = other.Fecha;
      Cuenta = other.Cuenta;
      Concepto = other.Concepto;
      Monto = other.Monto;
      ComprobanteId = other.ComprobanteId;
      ComprobanteMonto = other.ComprobanteMonto;
      Status = other.Status;
    }
  }

  protected sealed class MovimientoModel
  {
    public int Id { get; set; }

    [Required(ErrorMessage = "La cuenta es obligatoria.")]
    
    public string? NombreCuenta { get; set; }

    [Required(ErrorMessage = "El concepto es obligatorio.")]
    public string? Concepto { get; set; }

    [Range(typeof(decimal), "0", "79228162514264337593543950335", ErrorMessage = "Debe inválido.")]
    public decimal Debe { get; set; }

    [Range(typeof(decimal), "0", "79228162514264337593543950335", ErrorMessage = "Haber inválido.")]
    public decimal Haber { get; set; }

    public string? Memo { get; set; }

    public MovimientoModel Clone()
      => (MovimientoModel)MemberwiseClone();

    public void CopyFrom(MovimientoModel other)
    {
      Id = other.Id;
      NombreCuenta = other.NombreCuenta;
      Concepto = other.Concepto;
      Debe = other.Debe;
      Haber = other.Haber;
      Memo = other.Memo;
    }
  }

  protected sealed class AttachmentModel
  {
    public string Nombre { get; set; } = string.Empty;
    public string Extension { get; set; } = string.Empty;
    public long TamanoBytes { get; set; }
    public string TamanoHumano => FormatSize(TamanoBytes);
  }

  protected sealed class ComprobanteModel
  {
    public int ComprobanteId { get; set; }
    public string Serie { get; set; } = string.Empty;
    public string Folio { get; set; } = string.Empty;
    public DateTime Fecha { get; set; }
    public decimal Total { get; set; }
    public bool Vinculado { get; set; }
  }

  private static string FormatSize(long bytes)
  {
    if (bytes <= 0)
      return "0 B";

    var units = new[] { "B", "KB", "MB", "GB", "TB" };
    var magnitude = (int)Math.Min(units.Length - 1, Math.Log(bytes, 1024));
    var adjusted = bytes / Math.Pow(1024, magnitude);
    return $"{adjusted:0.##} {units[magnitude]}";
  }
}
