using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;

using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using OrionERP.Application.Features.Cfdi.CargarXmlSat.Contracts;
using OrionERP.Web.State;
using System.Threading.Tasks;

namespace OrionERP.Web.Features.Cfdi.CargarXmlSat.Pages
{
  public partial class CargarXmlSat : ComponentBase
  {
    [Inject] protected ISatXmlInboxService InboxService { get; set; } = default!;
    [Inject] protected IComprobanteQueryService ComprobanteQuery { get; set; } = default!;
    [Inject] protected ITransaccionQueryService TransaccionQuery { get; set; } = default!;
    [Inject] protected IConciliacionService Conciliacion { get; set; } = default!;
    [Inject] protected IUserRfcState RfcState { get; set; } = default!;




    protected List<SatXmlProcessResult> ProcessResults { get; } = new();
    protected List<ComprobanteListItem> Invoices { get; } = new();
    // ---- UI constants (adjust if needed) ----
    protected const int MaxFiles = 50;
    protected const long MaxFileSizeBytes = 5 * 1024 * 1024; // 5 MB per file
    protected static readonly string MaxFileSizeDisplay = "5 MB";
    

    protected ComprobanteListItem? SelectedComprobante { get; set; }
    protected List<TransaccionListItem> FilteredTransacciones { get; } = new();
    protected int? SelectedTransaccionId { get; set; }
    protected string? ConciliarMessage { get; set; }
    protected bool IsConciliando { get; set; }


    // ---- UI State ----
    protected List<SelectedFileVm> SelectedFiles { get; } = new();
    protected List<string> ValidationMessages { get; } = new();
    protected bool IsProcessing { get; set; }
    protected bool CanContinue =>
    SelectedFiles.Count > 0
    && SelectedFiles.All(f => string.IsNullOrWhiteSpace(f.Error))
    && !IsProcessing;


    // ---- Events ----


    protected void OnSelectTransaccion(TransaccionListItem t)
    {
      SelectedTransaccionId = t.Id;
      ConciliarMessage = null;
    }

    protected async Task ConciliarAsync()
    {
      if (SelectedComprobante is null || SelectedTransaccionId is null)
      {
        ConciliarMessage = "Seleccione un comprobante y una transacción.";
        return;
      }

      IsConciliando = true;
      StateHasChanged();

      var result = await Conciliacion.ConciliarAsync(
          comprobanteId: SelectedComprobante.ComprobanteId,
          transaccionId: SelectedTransaccionId.Value);

      IsConciliando = false;
      ConciliarMessage = result.Message;

      if (result.Success)
      {
        // ✅ Refresh the top list so the reconciled Comprobante disappears from the 5505 list
        await RefreshInvoicesAsync();

        // ✅ UX reset: clear selections so the lower table collapses
        SelectedTransaccionId = null;          // clear radio selection
        SelectedComprobante = null;            // clear selected comprobante → hides candidates section
        FilteredTransacciones.Clear();         // clear the list so no stale rows remain
                                               // (Do NOT call RefreshCandidatesAsync() because SelectedComprobante is now null)
      }
      else
      {
        // optional: keep selections so user can try again
      }

      StateHasChanged();
    }




    protected async Task OnSelectComprobanteAsync(ComprobanteListItem item)
    {
      SelectedComprobante = item;

      // The ComprobanteListItem.Total you already cast to decimal in Step 4 SQL.
      var montoAbs = Math.Abs(item.Total);
      var fechaXml = item.Fecha;

      var currentRfc = RfcState.CurrentRfc;
      if (string.IsNullOrWhiteSpace(currentRfc))
      {
        FilteredTransacciones.Clear();
        StateHasChanged();
        return;
      }

      FilteredTransacciones.Clear();
      var rows = await TransaccionQuery.GetCandidatesAsync(
          fechaXml: fechaXml,
          montoAbs: montoAbs,
          rfc: currentRfc,
          daysBack: 60,
          top: 200
      );
      FilteredTransacciones.AddRange(rows);
      StateHasChanged();
    }



    protected async Task OnFilesSelected(InputFileChangeEventArgs e)
    {
      ValidationMessages.Clear();

      // Limit total selected count
      var incoming = e.GetMultipleFiles(int.MaxValue);
      if (SelectedFiles.Count + incoming.Count > MaxFiles)
      {
        ValidationMessages.Add($"Límite de archivos superado: máximo {MaxFiles}.");
      }

      // We will only take up to the remaining capacity
      var capacity = Math.Max(0, MaxFiles - SelectedFiles.Count);
      foreach (var file in incoming.Take(capacity))
      {
        var vm = ValidateFile(file, SelectedFiles);
        SelectedFiles.Add(vm);

        // Optional tiny peek to confirm it's XML: read first ~64 bytes for "<?xml"
        // (Does not load the whole file; safe for Step 2)
        if (string.IsNullOrWhiteSpace(vm.Error))
        {
          try
          {
            using var peek = file.OpenReadStream(MaxFileSizeBytes);
            var buffer = new byte[64];
            var read = await peek.ReadAsync(buffer, 0, buffer.Length);
            var head = System.Text.Encoding.UTF8.GetString(buffer, 0, read);
            if (!head.Contains("<?xml", StringComparison.OrdinalIgnoreCase))
            {
              vm.Error = "Contenido no parece XML (sin encabezado <?xml).";
            }
          }
          catch (IOException)
          {
            vm.Error = "No se pudo leer el archivo para verificación.";
          }
          catch (Exception ex)
          {
            vm.Error = $"Error validando archivo: {ex.Message}";
          }
        }
      }

      // If there were more files than capacity, warn user
      if (incoming.Count > capacity)
      {
        ValidationMessages.Add($"Se ignoraron {incoming.Count - capacity} archivos adicionales (límite alcanzado).");
      }

      StateHasChanged();
    }

    // Basic client-side validation
    private SelectedFileVm ValidateFile(IBrowserFile file, List<SelectedFileVm> current)
    {
      var vm = new SelectedFileVm
      {
        Id = Guid.NewGuid(),
        BrowserFile = file,
        Name = file.Name,
        SizeBytes = file.Size,
        SizeDisplay = FormatSize(file.Size),
        ContentType = string.IsNullOrWhiteSpace(file.ContentType) ? "(desconocido)" : file.ContentType,
        Error = null
      };

      // 1) Extension check
      var ext = Path.GetExtension(file.Name);
      if (!ext.Equals(".xml", StringComparison.OrdinalIgnoreCase))
      {
        vm.Error = "Extensión inválida (se requiere .xml).";
        return vm;
      }

      // 2) MIME hint (best-effort)
      if (!string.IsNullOrWhiteSpace(file.ContentType) &&
          !file.ContentType.Contains("xml", StringComparison.OrdinalIgnoreCase))
      {
        vm.Error = "Tipo MIME no es XML.";
        return vm;
      }

      // 3) Size check
      if (file.Size <= 0)
      {
        vm.Error = "Archivo vacío.";
        return vm;
      }

      if (file.Size > MaxFileSizeBytes)
      {
        vm.Error = $"Excede tamaño máximo ({MaxFileSizeDisplay}).";
        return vm;
      }

      // 4) Duplicate check by filename (simple, user-friendly)
      if (current.Any(x => x.Name.Equals(file.Name, StringComparison.OrdinalIgnoreCase)))
      {
        vm.Error = "Duplicado (mismo nombre).";
        return vm;
      }

      return vm;
    }

    protected void RemoveFile(Guid id)
    {
      var idx = SelectedFiles.FindIndex(f => f.Id == id);
      if (idx >= 0) SelectedFiles.RemoveAt(idx);
    }

    protected void ClearSelection()
    {
      SelectedFiles.Clear();
      ValidationMessages.Clear();
    }

    // Placeholder for Step 3 hook

    protected async Task ContinueToProcess()
    {
      if (IsProcessing) return;
      if (SelectedFiles.Count == 0) return;

      IsProcessing = true;
      StateHasChanged();

      // Take a snapshot; user might change selection while we process
      var batch = SelectedFiles.Where(x => string.IsNullOrWhiteSpace(x.Error)).ToList();

      foreach (var f in batch)
      {
        try
        {
          using var stream = f.BrowserFile.OpenReadStream(MaxFileSizeBytes);
          var result = await InboxService.SaveAndProcessAsync(stream, f.Name);
          ProcessResults.Add(result);   // ✅ append, don’t Clear() first
        }
        catch (Exception ex)
        {
          ProcessResults.Add(new SatXmlProcessResult(f.Name, 0, false, ex.Message));
        }
      }

      // If at least one succeeded, we can clear the file selection (keeps results visible)
      var anySuccess = ProcessResults.TakeLast(batch.Count).Any(r => r.Success);
      if (anySuccess)
        SelectedFiles.Clear();

      // Always refresh invoices from SQL, independent of SelectedFiles
      await RefreshInvoicesAsync();

      IsProcessing = false;
      StateHasChanged();
    }

    protected void ClearResults() => ProcessResults.Clear();

    private static string FormatSize(long bytes)
    {
      if (bytes < 1024) return $"{bytes} B";
      double kb = bytes / 1024d;
      if (kb < 1024) return $"{kb:N1} KB";
      double mb = kb / 1024d;
      return $"{mb:N2} MB";
    }

    //INVOICE HELPERS

    protected async Task RefreshCandidatesAsync()
    {
      if (SelectedComprobante is null) return;
      await OnSelectComprobanteAsync(SelectedComprobante);
    }
   

    protected async Task RefreshInvoicesAsync()
    {
      var currentRfc = RfcState.CurrentRfc;
      if (string.IsNullOrWhiteSpace(currentRfc))
      {
        Invoices.Clear();
        StateHasChanged();
        return;
      }

      Invoices.Clear();
      var list = await ComprobanteQuery.GetRecentFromPlaceholderAsync(
          rfc: currentRfc,
          placeholderTransaccionId: 5505,
          top: 100);
      Invoices.AddRange(list);
      StateHasChanged();
    }

    // ViewModel for selected files
    protected class SelectedFileVm
    {
      public Guid Id { get; set; }
      public IBrowserFile BrowserFile { get; set; } = default!;
      public string Name { get; set; } = string.Empty;
      public long SizeBytes { get; set; }
      public string SizeDisplay { get; set; } = string.Empty;
      public string ContentType { get; set; } = string.Empty;
      public string? Error { get; set; }
    }
  }
}
