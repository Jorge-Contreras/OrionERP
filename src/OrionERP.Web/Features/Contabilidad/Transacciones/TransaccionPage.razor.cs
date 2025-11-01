using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using OrionERP.Web.Services;

namespace OrionERP.Web.Features.Contabilidad.Transacciones;

public partial class TransaccionPage : ComponentBase, IDisposable
{
  private readonly List<CategoriaItem> _categorias = new();
  private TransaccionHeaderModel? _headerOriginal;
  private MovimientoModel? _movimientoTarget;

  [Parameter] public int Id { get; set; }

  [Inject] public ITransaccionDetailService DetailService { get; set; } = default!;
  [Inject] public IBreadcrumbService Breadcrumbs { get; set; } = default!;
  [Inject] public IUiMessageService UiMessages { get; set; } = default!;
  protected TransaccionHeaderModel? Header { get; private set; }
  protected EditContext? HeaderEditContext { get; private set; }
  protected bool IsLoading { get; private set; } = true;
  protected bool IsSavingHeader { get; private set; }
  protected string? ErrorMessage { get; private set; }

  protected List<MovimientoModel> Movimientos { get; } = new();
  protected List<AttachmentModel> Attachments { get; } = new();
  protected List<ComprobanteModel> Comprobantes { get; } = new();

  protected bool ShowMovimientoModal { get; private set; }
  protected MovimientoModel? MovimientoDraft { get; private set; }
  protected EditContext? MovimientoEditContext { get; private set; }
  protected string MovimientoModalTitle => _movimientoTarget is null ? "Agregar movimiento" : "Editar movimiento";

  protected bool ShowCategoriaPicker { get; private set; }
  protected string CategoriaSearchTerm { get; set; } = string.Empty;
  protected IEnumerable<CategoriaItem> FilteredCategorias
    => string.IsNullOrWhiteSpace(CategoriaSearchTerm)
      ? _categorias
      : _categorias.Where(c => c.Clave.Contains(CategoriaSearchTerm, StringComparison.OrdinalIgnoreCase)
                               || c.Descripcion.Contains(CategoriaSearchTerm, StringComparison.OrdinalIgnoreCase))
                   .Take(30);

  protected bool ShowMemoZoom { get; private set; }
  protected string? MemoZoomText { get; private set; }

  protected override async Task OnParametersSetAsync()
  {
    await LoadAsync();
  }

  private async Task LoadAsync(CancellationToken ct = default)
  {
    IsLoading = true;
    ErrorMessage = null;

    try
    {
      var dto = await DetailService.GetAsync(Id, ct);

      Header = new TransaccionHeaderModel
      {
        Folio = dto.Folio,
        Rfc = dto.Rfc,
        Fecha = dto.Fecha,
        Categoria = dto.Categoria,
        Concepto = dto.Concepto,
        Referencia = dto.Referencia,
        Memo = dto.Memo,
        Subtotal = dto.Subtotal,
        Iva = dto.Iva,
        Monto = dto.Monto,
        Divisa = dto.Divisa,
        Status = dto.Status
      };
      _headerOriginal = Header.Clone();
      HeaderEditContext = new EditContext(Header);

      Movimientos.Clear();
      Movimientos.AddRange(dto.Movimientos.Select(m => new MovimientoModel
      {
        Id = m.Id,
        Cuenta = m.Cuenta,
        Concepto = m.Concepto,
        Debe = m.Debe,
        Haber = m.Haber,
        Memo = m.Memo
      }));

      Attachments.Clear();
      Attachments.AddRange(dto.Adjuntos.Select(a => new AttachmentModel
      {
        Nombre = a.Nombre,
        TamanoBytes = a.TamanoBytes,
        CargadoEn = a.CargadoEn
      }));

      Comprobantes.Clear();
      Comprobantes.AddRange(dto.Comprobantes.Select(c => new ComprobanteModel
      {
        Uuid = c.Uuid,
        Emisor = c.Emisor,
        Total = c.Total
      }));

      _categorias.Clear();
      _categorias.AddRange(dto.Categorias.Select(c => new CategoriaItem(c.Clave, c.Descripcion)));

      Breadcrumbs.Set(
        new BreadcrumbItem("Contabilidad", "/contabilidad"),
        new BreadcrumbItem("Transacciones", "/contabilidad/transacciones"),
        new BreadcrumbItem($"Transacción {Header.Folio ?? Id.ToString(CultureInfo.InvariantCulture)}", null, true));
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
      await Task.Delay(350); // simulate persistence
      _headerOriginal = Header.Clone();
      UiMessages.ShowSuccess("Datos de la transacción guardados.");
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
        Haber = 0m
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

  protected void RemoveMovimiento(MovimientoModel movimiento)
  {
    Movimientos.Remove(movimiento);
  }

  protected void CopyMontoToMovimiento()
  {
    if (MovimientoDraft is null || Header is null)
      return;

    MovimientoDraft.Debe = Header.Monto;
    MovimientoEditContext?.NotifyFieldChanged(new FieldIdentifier(MovimientoDraft, nameof(MovimientoDraft.Debe)));
  }

  protected void CopySubtotalToMovimiento()
  {
    if (MovimientoDraft is null || Header is null)
      return;

    MovimientoDraft.Debe = Header.Subtotal;
    MovimientoEditContext?.NotifyFieldChanged(new FieldIdentifier(MovimientoDraft, nameof(MovimientoDraft.Debe)));
  }

  protected void CopyIvaToMovimiento()
  {
    if (MovimientoDraft is null || Header is null)
      return;

    MovimientoDraft.Debe = Header.Iva;
    MovimientoEditContext?.NotifyFieldChanged(new FieldIdentifier(MovimientoDraft, nameof(MovimientoDraft.Debe)));
  }

  protected void CopyConceptoFromHeader()
  {
    if (MovimientoDraft is null || Header is null)
      return;

    MovimientoDraft.Concepto = Header.Concepto;
    MovimientoEditContext?.NotifyFieldChanged(new FieldIdentifier(MovimientoDraft, nameof(MovimientoDraft.Concepto)));
  }

  protected void OpenCategoriaPicker()
  {
    CategoriaSearchTerm = string.Empty;
    ShowCategoriaPicker = true;
  }

  protected void CloseCategoriaPicker()
  {
    ShowCategoriaPicker = false;
  }

  protected void SelectCategoria(CategoriaItem categoria)
  {
    if (Header is null)
      return;

    Header.Categoria = categoria.Descripcion;
    HeaderEditContext?.NotifyFieldChanged(new FieldIdentifier(Header, nameof(Header.Categoria)));
    ShowCategoriaPicker = false;
  }

  protected void OpenMemoZoom(string memo)
  {
    MemoZoomText = memo;
    ShowMemoZoom = true;
  }

  protected void CloseMemoZoom()
  {
    MemoZoomText = null;
    ShowMemoZoom = false;
  }

  protected async Task OnAttachmentsSelected(InputFileChangeEventArgs e)
  {
    foreach (var file in e.GetMultipleFiles())
    {
      Attachments.Add(new AttachmentModel
      {
        Nombre = file.Name,
        TamanoBytes = file.Size,
        CargadoEn = DateTimeOffset.Now
      });
    }

    UiMessages.ShowInfo("Archivo(s) agregados a la transacción.");
    await InvokeAsync(StateHasChanged);
  }

  protected void RemoveAttachment(AttachmentModel attachment)
  {
    Attachments.Remove(attachment);
  }

  public void Dispose()
  {
    Breadcrumbs.Clear();
  }

  protected sealed class TransaccionHeaderModel
  {
    public string? Folio { get; set; }
    public string? Rfc { get; set; }

    [Required(ErrorMessage = "La fecha es obligatoria.")]
    public DateTime Fecha { get; set; }

    [Required(ErrorMessage = "Selecciona una categoría.")]
    public string? Categoria { get; set; }

    [Required(ErrorMessage = "Captura un concepto.")]
    public string? Concepto { get; set; }

    public string? Referencia { get; set; }
    public string? Memo { get; set; }

    [Range(typeof(decimal), "0", "79228162514264337593543950335", ErrorMessage = "Subtotal inválido.")]
    public decimal Subtotal { get; set; }

    [Range(typeof(decimal), "0", "79228162514264337593543950335", ErrorMessage = "IVA inválido.")]
    public decimal Iva { get; set; }

    [Range(typeof(decimal), "0", "79228162514264337593543950335", ErrorMessage = "Monto inválido.")]
    public decimal Monto { get; set; }

    public string? Divisa { get; set; }
    public string Status { get; set; } = "Pendiente";

    public TransaccionHeaderModel Clone()
      => (TransaccionHeaderModel)MemberwiseClone();

    public void CopyFrom(TransaccionHeaderModel other)
    {
      Folio = other.Folio;
      Rfc = other.Rfc;
      Fecha = other.Fecha;
      Categoria = other.Categoria;
      Concepto = other.Concepto;
      Referencia = other.Referencia;
      Memo = other.Memo;
      Subtotal = other.Subtotal;
      Iva = other.Iva;
      Monto = other.Monto;
      Divisa = other.Divisa;
      Status = other.Status;
    }
  }

  protected sealed class MovimientoModel
  {
    public int Id { get; set; }

    [Required(ErrorMessage = "La cuenta es obligatoria.")]
    public string? Cuenta { get; set; }

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
      Cuenta = other.Cuenta;
      Concepto = other.Concepto;
      Debe = other.Debe;
      Haber = other.Haber;
      Memo = other.Memo;
    }
  }

  protected sealed class AttachmentModel
  {
    public string Nombre { get; set; } = string.Empty;
    public long TamanoBytes { get; set; }
    public DateTimeOffset CargadoEn { get; set; }
    public string TamanoHumano => FormatSize(TamanoBytes);
  }

  protected sealed class ComprobanteModel
  {
    public string Uuid { get; set; } = string.Empty;
    public string Emisor { get; set; } = string.Empty;
    public decimal Total { get; set; }
  }

  protected sealed record CategoriaItem(string Clave, string Descripcion);

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
