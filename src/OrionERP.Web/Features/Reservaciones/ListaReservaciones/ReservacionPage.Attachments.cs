using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;
using OrionERP.Application.Features.Reservaciones.ListaReservaciones;

namespace OrionERP.Web.Features.Reservaciones.ListaReservaciones;

public partial class ReservacionPage
{
  internal async Task OnAttachmentSelectedAsync(InputFileChangeEventArgs args)
  {
    _pendingAttachment = args.FileCount > 0 ? args.File : null;
    await InvokeAsync(StateHasChanged);
  }

  internal async Task CargarAttachmentAsync()
  {
    if (_pendingAttachment is null)
    {
      UiMessages.ShowWarning("Selecciona un archivo.");
      return;
    }

    if (string.IsNullOrWhiteSpace(AttachmentDescription))
    {
      UiMessages.ShowWarning("Ingresa una descripción para el archivo.");
      return;
    }

    if (_pendingAttachment.Size > ReservacionAttachmentCreateRequest.MaxFileSizeBytes)
    {
      UiMessages.ShowError("El archivo excede el tamaño máximo permitido (5 MB).");
      return;
    }

    IsUploadingAttachment = true;
    try
    {
      await using var stream = _pendingAttachment.OpenReadStream(ReservacionAttachmentCreateRequest.MaxFileSizeBytes);
      using var ms = new MemoryStream();
      await stream.CopyToAsync(ms);

      var extension = Path.GetExtension(_pendingAttachment.Name)?.TrimStart('.');
      await ReservacionesService.AddAttachmentAsync(new ReservacionAttachmentCreateRequest
      {
        ReservationId = ReservationId,
        FileName = _pendingAttachment.Name,
        Extension = extension,
        Description = AttachmentDescription.Trim(),
        Content = ms.ToArray()
      });

      AttachmentDescription = string.Empty;
      _pendingAttachment = null;
      _attachmentInputKey++;
      await RefreshAttachmentsAsync();
      UiMessages.ShowSuccess("Archivo agregado.");
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo cargar el archivo. {ex.Message}");
    }
    finally
    {
      IsUploadingAttachment = false;
    }
  }

  internal async Task DescargarAttachmentAsync(ReservacionAttachmentDto attachment)
  {
    _attachmentDownloadingId = attachment.Id;
    try
    {
      var content = await ReservacionesService.GetAttachmentContentAsync(attachment.Id);
      if (content is null || content.Bytes.Length == 0)
      {
        UiMessages.ShowError("No se encontró el contenido del archivo.");
        return;
      }

      var dataUrl = $"data:{content.ContentType};base64,{Convert.ToBase64String(content.Bytes)}";
      await Js.InvokeVoidAsync("triggerFileDownload", content.FileName, dataUrl);
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo descargar el archivo. {ex.Message}");
    }
    finally
    {
      _attachmentDownloadingId = null;
    }
  }

  internal async Task EliminarAttachmentAsync(ReservacionAttachmentDto attachment)
  {
    var confirm = await Js.InvokeAsync<bool>("confirm", "¿Eliminar el archivo seleccionado?");
    if (!confirm)
    {
      return;
    }

    _attachmentDeletingId = attachment.Id;
    try
    {
      await ReservacionesService.DeleteAttachmentAsync(attachment.Id);
      await RefreshAttachmentsAsync();
      UiMessages.ShowSuccess("Archivo eliminado.");
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo eliminar el archivo. {ex.Message}");
    }
    finally
    {
      _attachmentDeletingId = null;
    }
  }

  internal async Task RefreshAttachmentsAsync()
  {
    Attachments = await ReservacionesService.GetAttachmentsAsync(ReservationId);
  }
}
