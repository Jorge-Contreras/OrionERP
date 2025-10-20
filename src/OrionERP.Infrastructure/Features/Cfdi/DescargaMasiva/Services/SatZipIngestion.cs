using OrionERP.Application.Features.Cfdi.CargarXmlSat.Contracts; // ISatXmlInboxService
using OrionERP.Application.Features.Cfdi.DescargaMasiva.Contracts;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using static OrionERP.Application.Features.Cfdi.DescargaMasiva.Contracts.VerifyResultDto;
using static System.Runtime.InteropServices.JavaScript.JSType;
using App = OrionERP.Application.Features.Cfdi.DescargaMasiva.Contracts;

namespace OrionERP.Infrastructure.Features.Cfdi.DescargaMasiva.Services;

internal static class SatZipIngestion
{
  // New: returns XmlProcessedItem (richer info)
  public static async Task<List<App.XmlProcessedItem>> PushZipToInboxWithMetadataAsync(
      string packageId,
      byte[] zipBytes,
      ISatXmlInboxService inbox,
      CancellationToken ct = default)
  {
    var items = new List<App.XmlProcessedItem>();

    using var ms = new MemoryStream(zipBytes, writable: false);
    using var zip = new ZipArchive(ms, ZipArchiveMode.Read, leaveOpen: false);

    foreach (var entry in zip.Entries)
    {
      if (string.IsNullOrWhiteSpace(entry.Name)) continue;
      if (!entry.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) continue;

      try
      {
        // Buffer XML to byte[] so we can both extract metadata and pass to InboxService
        byte[] xmlBytes;
        await using (var es = entry.Open())
        {
          using var buf = new MemoryStream(capacity: entry.Length > 0 ? (int)Math.Min(entry.Length, int.MaxValue) : 0);
          await es.CopyToAsync(buf, ct);
          xmlBytes = buf.ToArray();
        }

        // Extract metadata
        var meta = CfdiInfoExtractor.TryExtract(xmlBytes);

        // Call your existing pipeline
        using var xmlStream = new MemoryStream(xmlBytes, writable: false);
        var res = await inbox.SaveAndProcessAsync(xmlStream, entry.Name);

        items.Add(new App.XmlProcessedItem
        {
          PackageId = packageId,
          FileName = entry.Name,
          Uuid = meta.Uuid,
          RfcEmisor = meta.RfcEmisor,
          RfcReceptor = meta.RfcReceptor,
          FechaEmisionUtc = meta.FechaUtc,
          SubTotal = meta.SubTotal,
          Total = meta.Total,
          TipoDeComprobante = meta.Tipo,
          Success = res.Success,
          Error = res.Success ? null : "Error al procesar"
        });
      }
      catch (Exception ex)
      {
        items.Add(new App.XmlProcessedItem
        {
          PackageId = packageId,
          FileName = entry.FullName,
          Success = false,
          Error = ex.Message
        });
      }
    }

    return items;
  }
}
