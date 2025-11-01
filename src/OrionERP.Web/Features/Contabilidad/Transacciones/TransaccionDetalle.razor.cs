using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using OrionERP.Application.Features.Contabilidad.Transacciones;

namespace OrionERP.Web.Features.Contabilidad.Transacciones;

public partial class TransaccionDetalle : ComponentBase
{
  [Parameter]
  public int Id { get; set; }

  private TransaccionHeaderDto? header;
  private List<TransaccionMovimientoDto> movimientos = new();
  private List<TransaccionAttachmentDto> attachments = new();
  private List<TransaccionComprobanteDto> comprobantes = new();
  private MovimientoTotalsDto totals = new();
  private string? concepto;
  private DateTime fecha = DateTime.Today;
  private string? cuenta;
  private decimal monto;
  private bool isLoading = true;
  private bool isSaving;
  private string? errorMessage;
  private string? successMessage;
  private int? nuevoComprobanteId;

  protected override async Task OnParametersSetAsync()
  {
    await LoadAsync();
  }

  private async Task LoadAsync()
  {
    isLoading = true;
    errorMessage = null;
    successMessage = null;

    try
    {
      header = await TransaccionService.GetHeaderAsync(Id);
      if (header is null)
      {
        movimientos.Clear();
        attachments.Clear();
        comprobantes.Clear();
        totals = new MovimientoTotalsDto();
        return;
      }

      concepto = header.Concepto;
      fecha = header.Fecha;
      cuenta = header.Cuenta;
      monto = header.Monto;

      movimientos = (await TransaccionService.GetMovimientosAsync(Id)).ToList();
      totals = await TransaccionService.GetMovimientoTotalsAsync(Id);
      attachments = (await TransaccionService.GetAttachmentsAsync(Id)).ToList();
      comprobantes = (await TransaccionService.GetComprobantesAsync(Id)).ToList();
    }
    catch (Exception ex)
    {
      errorMessage = $"Error al cargar la transacción: {ex.Message}";
    }
    finally
    {
      isLoading = false;
    }
  }

  private async Task GuardarAsync()
  {
    if (header is null)
    {
      return;
    }

    isSaving = true;
    errorMessage = null;
    successMessage = null;

    try
    {
      var request = new TransaccionGuardarCerrarRequest
      {
        TransaccionId = header.Id,
        Concepto = concepto,
        Fecha = fecha,
        Cuenta = cuenta,
        Monto = monto
      };

      var result = await TransaccionService.GuardarYCerrarAsync(request);
      if (!result.Success)
      {
        errorMessage = result.Message ?? "No se pudo guardar la transacción.";
        return;
      }

      successMessage = result.Message ?? "Transacción guardada.";
      totals = result.Totals ?? totals;

      header = new TransaccionHeaderDto
      {
        Id = header.Id,
        Concepto = concepto,
        Fecha = fecha,
        Cuenta = cuenta,
        Monto = monto,
        Rfc = header.Rfc,
        ComprobanteId = header.ComprobanteId,
        ComprobanteMonto = header.ComprobanteMonto
      };
    }
    catch (Exception ex)
    {
      errorMessage = $"Error al guardar: {ex.Message}";
    }
    finally
    {
      isSaving = false;
    }
  }

  private async Task DescargarAdjuntoAsync(TransaccionAttachmentDto attachment)
  {
    errorMessage = null;
    successMessage = null;

    try
    {
      var content = await TransaccionService.GetAttachmentContentAsync(attachment.Id);
      if (content is null)
      {
        errorMessage = "No se encontró el adjunto solicitado.";
        return;
      }

      var base64 = Convert.ToBase64String(content.Bytes);
      var dataUrl = $"data:{content.ContentType};base64,{base64}";

      await JS.InvokeVoidAsync("triggerFileDownload", content.FileName, dataUrl);
    }
    catch (Exception ex)
    {
      errorMessage = $"No se pudo descargar el adjunto: {ex.Message}";
    }
  }

  private async Task VincularComprobanteAsync()
  {
    if (nuevoComprobanteId is null)
    {
      return;
    }

    errorMessage = null;
    successMessage = null;

    try
    {
      await TransaccionService.ToggleComprobanteAsync(Id, nuevoComprobanteId.Value, true);
      successMessage = "Comprobante vinculado.";
      await ReloadComprobantesAsync();
    }
    catch (Exception ex)
    {
      errorMessage = $"Error al vincular comprobante: {ex.Message}";
    }
    finally
    {
      nuevoComprobanteId = null;
    }
  }

  private async Task DesvincularComprobanteAsync(int comprobanteId)
  {
    errorMessage = null;
    successMessage = null;

    try
    {
      await TransaccionService.ToggleComprobanteAsync(Id, comprobanteId, false);
      successMessage = "Comprobante desvinculado.";
      await ReloadComprobantesAsync();
    }
    catch (Exception ex)
    {
      errorMessage = $"Error al desvincular comprobante: {ex.Message}";
    }
  }

  private async Task ReloadComprobantesAsync()
  {
    try
    {
      header = await TransaccionService.GetHeaderAsync(Id);
      comprobantes = (await TransaccionService.GetComprobantesAsync(Id)).ToList();
      totals = await TransaccionService.GetMovimientoTotalsAsync(Id);
    }
    catch (Exception ex)
    {
      errorMessage = $"Error al actualizar la vista: {ex.Message}";
    }
  }

  private static string FormatLength(long length)
  {
    var suffixes = new[] { "B", "KB", "MB", "GB" };
    double size = length;
    var index = 0;

    while (size >= 1024 && index < suffixes.Length - 1)
    {
      size /= 1024;
      index++;
    }

    return string.Format(CultureInfo.InvariantCulture, "{0:0.##} {1}", size, suffixes[index]);
  }
}
