using OrionERP.Application.Features.Cfdi.CargarXmlSat.Contracts; // ISatXmlInboxService
using OrionERP.Application.Features.Cfdi.DescargaMasiva.Contracts;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using App = OrionERP.Application.Features.Cfdi.DescargaMasiva.Contracts;

namespace OrionERP.Infrastructure.Features.Cfdi.DescargaMasiva.Services;

internal static class SatZipIngestion
{
  // CFDI path: parse XML metadata + push to inbox pipeline
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
          using var buf = new MemoryStream();
          await es.CopyToAsync(buf, ct);
          xmlBytes = buf.ToArray();
        }

        // Extract metadata
        var meta = CfdiInfoExtractor.TryExtract(xmlBytes);

        var res = await inbox.SaveAndProcessAsync(xmlBytes, entry.Name, ct);

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

  // Metadata path: read non-XML entries (txt/csv etc.), send full text to SQL SP
  public static async Task<List<App.MetadataProcessedItem>> PushZipMetadataAsync(
      string packageId,
      byte[] zipBytes,
      ISatMetadataIngestService ingest,
      CancellationToken ct = default)
  {
    var items = new List<App.MetadataProcessedItem>();

    using var ms = new MemoryStream(zipBytes, writable: false);
    using var zip = new ZipArchive(ms, ZipArchiveMode.Read, leaveOpen: false);

    foreach (var entry in zip.Entries)
    {
      if (string.IsNullOrWhiteSpace(entry.Name)) continue;

      // Skip XML; treat everything else as metadata
      if (entry.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
        continue;

      try
      {
        string text;
        int bytesLen;
        int lineCount;

        await using (var es = entry.Open())
        using (var buf = new MemoryStream())
        {
          await es.CopyToAsync(buf, ct);
          bytesLen = (int)buf.Length;

          buf.Position = 0;
          using var sr = new StreamReader(buf, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
          text = await sr.ReadToEndAsync(ct);
        }

        lineCount = text.Split('\n').Length;

        await ingest.IngestAsync(text, ct);

        items.Add(new App.MetadataProcessedItem
        {
          PackageId = packageId,
          FileName = entry.Name,
          ByteCount = bytesLen,
          LineCount = lineCount,
          Success = true
        });
      }
      catch (Exception ex)
      {
        items.Add(new App.MetadataProcessedItem
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
