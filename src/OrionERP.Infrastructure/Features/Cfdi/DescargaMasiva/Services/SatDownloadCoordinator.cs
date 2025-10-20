using OrionERP.Application.Features.Cfdi.CargarXmlSat.Contracts;
using OrionERP.Application.Features.Cfdi.DescargaMasiva.Contracts;
using Sat.MassiveDownload.Core;           // ISatMassiveService
using Sat.MassiveDownload.Models;    // VerifyResult, EstadoSolicitud
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static OrionERP.Application.Features.Cfdi.DescargaMasiva.Contracts.VerifyResultDto;
using SatEstado = Sat.MassiveDownload.Models.EstadoSolicitud;
using App = OrionERP.Application.Features.Cfdi.DescargaMasiva.Contracts;
using SatISvc = Sat.MassiveDownload.Core.ISatMassiveService;
using System.Security.Cryptography.X509Certificates;

namespace OrionERP.Infrastructure.Features.Cfdi.DescargaMasiva.Services;




public sealed class SatDownloadCoordinator : ISatDownloadCoordinator
{

  private static OrionERP.Application.Features.Cfdi.DescargaMasiva.Contracts.EstadoSolicitud Map(SatEstado s)
    => (OrionERP.Application.Features.Cfdi.DescargaMasiva.Contracts.EstadoSolicitud)(int)s;

  private readonly ISatMassiveService _sat;
  private readonly ISatSolicitudesRepository _solicitudes;
  private readonly ISatPaquetesRepository _paquetes;
  private readonly ISatXmlInboxService _inbox;
  private readonly ISatMetadataIngestService _meta;
  

   public SatDownloadCoordinator(
     SatISvc sat,
     ISatSolicitudesRepository solicitudes,
     ISatPaquetesRepository paquetes,

    ISatXmlInboxService inbox,
    ISatMetadataIngestService meta)
{
    _sat = sat; _solicitudes = solicitudes; _paquetes = paquetes; _inbox = inbox;
    _meta = meta;
}

  public async Task<int> CreateSolicitudAsync(SolicitudParams p, CancellationToken ct = default)
  {
    var key = ComputeRequestKey(p);

    var existing = await _solicitudes.FindByRequestKeyAsync(key, ct);
    if (existing is not null) return existing.Id;

    // For Recibidos + CFDI, SAT v1.5 requires Vigente
    var estado = (!p.Issued && string.Equals(p.TipoSolicitud, "CFDI", StringComparison.OrdinalIgnoreCase))
        ? (p.EstadoComprobante ?? "Vigente")
        : p.EstadoComprobante;

    // We'll set Folio after RequestAsync succeeds
    var emisor = p.Issued ? p.RfcSolicitante : p.FilterRfc;
    var receptor = p.Issued ? p.FilterRfc : p.RfcSolicitante;

    var dto = new SatSolicitudDto
    {
      RfcSolicitante = p.RfcSolicitante,
      Issued = p.Issued,
      TipoSolicitud = p.TipoSolicitud,
      EstadoComprobante = estado,
      RfcEmisor = emisor,
      RfcReceptor = receptor,
      FechaInicialUtc = p.StartUtc.ToUniversalTime(),
      FechaFinalUtc = p.EndUtc.ToUniversalTime(),
      PackageCount = 0
    };

    // Insert now to get an Id; then actually call SAT
    var id = await _solicitudes.InsertAsync(dto, key, ct);

    return id;
  }

  public async Task<VerifyResultDto> VerifyAsync(int solicitudId, X509Certificate2 cert, CancellationToken ct = default)
  {
    var row = await _solicitudes.GetAsync(solicitudId, ct)
        ?? throw new InvalidOperationException("Solicitud no encontrada");

    await _sat.AuthenticateAsync(cert, ct);

    // If request wasn't sent yet (Folio null), send it now
    if (row.Folio is null || row.Folio == Guid.Empty)
    {
      var folioStr = await _sat.RequestAsync(
    row.FechaInicialUtc, row.FechaFinalUtc, row.Issued, row.RfcSolicitante, row.Issued ? row.RfcReceptor : row.RfcEmisor,
    row.TipoSolicitud, row.EstadoComprobante, ct);

      // Persist folio if it’s a valid GUID (SAT returns GUIDs in practice)
      if (Guid.TryParse(folioStr?.Trim(), out var folioGuid))
      {
        await _solicitudes.SetFolioAsync(row.Id, folioGuid, ct);
        row.Folio = folioGuid; // keep in-memory in sync
      }
      else
      {
        // Extremely unlikely with SAT, but log just in case
        Console.WriteLine($"[WARN] Folio no es GUID: '{folioStr}'");
      }

      // Save a first "Aceptada" snapshot (optional but useful)
      await _solicitudes.UpdateVerifySnapshotAsync(row.Id, new SatVerifySnapshot
      {
        Estado = Map(SatEstado.Aceptada),
        CodigoEstadoSolicitud = null,
        CodEstatus = "5000",
        Mensaje = "Solicitud Aceptada",
        NumeroCfdis = 0,
        PackageIds = Array.Empty<string>(),
        IsTerminated = false
      }, ct);

      // refresh row (optional)
      row = await _solicitudes.GetAsync(solicitudId, ct)
          ?? throw new InvalidOperationException("Solicitud no encontrada (post-insert)");

    }

    var v = await _sat.VerifyAsync(row.Folio!.ToString()!, row.RfcSolicitante, ct);

    // Update DB snapshot
    await _solicitudes.UpdateVerifySnapshotAsync(row.Id, new SatVerifySnapshot
    {
      Estado = Map(v.Estado),
      CodigoEstadoSolicitud = v.CodigoEstadoSolicitud,
      CodEstatus = v.CodEstatus,
      Mensaje = v.Mensaje,
      NumeroCfdis = v.NumeroCfdis,
      PackageIds = v.PackageIds,
      IsTerminated = Map(v.Estado) == Map(SatEstado.Terminada)
    }, ct);

    return new VerifyResultDto
    {
      Estado = Map(v.Estado),
      CodigoEstadoSolicitud = v.CodigoEstadoSolicitud,
      CodEstatus = v.CodEstatus,
      Mensaje = v.Mensaje,
      NumeroCfdis = v.NumeroCfdis,
      PackageIds = v.PackageIds,
      HumanStatus = v.HumanStatus
    };
  }

  public async Task<App.ProcessSummary> DownloadAndProcessAsync(int solicitudId, X509Certificate2 cert, CancellationToken ct = default)
  {
    var row = await _solicitudes.GetAsync(solicitudId, ct)
        ?? throw new InvalidOperationException("Solicitud no encontrada");

    await _sat.AuthenticateAsync(cert, ct);

    var pkgs = await _paquetes.ListBySolicitudAsync(solicitudId, ct);
    var summary = new App.ProcessSummary();

    bool isMetadata = string.Equals(row.TipoSolicitud, "Metadata", StringComparison.OrdinalIgnoreCase);

    foreach (var pkg in pkgs.Where(p => !p.Processed))
    {
      try
      {
        var bytes = await _sat.DownloadPackageAsync(pkg.PackageId, row.RfcSolicitante, ct);
        if (bytes is null || bytes.Length == 0)
        {
          await _paquetes.MarkProcessedAsync(solicitudId, pkg.PackageId, new App.SatPackageProcessInfo
          {
            XmlCount = 0,
            SuccessCount = 0,
            FailureCount = 0,
            ZipSizeBytes = 0,
            ErrorMessage = "ZIP vacío"
          }, ct);
          continue;
        }

        if (isMetadata)
        {
          var metaDetails = await SatZipIngestion.PushZipMetadataAsync(pkg.PackageId, bytes, _meta, ct);

          var ok = metaDetails.Count(x => x.Success);
          var fail = metaDetails.Count - ok;

          await _paquetes.MarkProcessedAsync(solicitudId, pkg.PackageId, new App.SatPackageProcessInfo
          {
            XmlCount = metaDetails.Count, // reusing column to store "files processed"
            SuccessCount = ok,
            FailureCount = fail,
            ZipSizeBytes = bytes.LongLength
          }, ct);

          summary.Packages++;
          summary.MetaFiles += metaDetails.Count;
          summary.MetaOk += ok;
          summary.MetaFail += fail;
          summary.MetaDetails.AddRange(metaDetails);
        }
        else
        {
          var details = await SatZipIngestion.PushZipToInboxWithMetadataAsync(pkg.PackageId, bytes, _inbox, ct);

          var ok = details.Count(x => x.Success);
          var fail = details.Count - ok;

          await _paquetes.MarkProcessedAsync(solicitudId, pkg.PackageId, new App.SatPackageProcessInfo
          {
            XmlCount = details.Count,
            SuccessCount = ok,
            FailureCount = fail,
            ZipSizeBytes = bytes.LongLength
          }, ct);

          summary.Packages++;
          summary.Xmls += details.Count;
          summary.Ok += ok;
          summary.Fail += fail;
          summary.Details.AddRange(details);
        }
      }
      catch (Exception ex)
      {
        await _paquetes.MarkProcessedAsync(solicitudId, pkg.PackageId, new App.SatPackageProcessInfo
        {
          XmlCount = 0,
          SuccessCount = 0,
          FailureCount = 0,
          ZipSizeBytes = 0,
          ErrorMessage = ex.Message
        }, ct);
      }
    }

    // CFDI aggregates (as before)
    foreach (var d in summary.Details)
    {
      if (!string.IsNullOrWhiteSpace(d.RfcEmisor))
        summary.ByEmisor[d.RfcEmisor] = (summary.ByEmisor.TryGetValue(d.RfcEmisor, out var c) ? c : 0) + 1;

      if (!string.IsNullOrWhiteSpace(d.RfcReceptor))
        summary.ByReceptor[d.RfcReceptor] = (summary.ByReceptor.TryGetValue(d.RfcReceptor, out var c2) ? c2 : 0) + 1;

      if (d.Success && d.Total is not null)
        summary.TotalImporte = (summary.TotalImporte ?? 0m) + d.Total.Value;
    }

    // Error buckets (CFDI)
    var buckets = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    foreach (var d in summary.Details)
    {
      if (!d.Success && !string.IsNullOrWhiteSpace(d.Error))
        buckets[d.Error] = (buckets.TryGetValue(d.Error, out var c) ? c : 0) + 1;
    }
    foreach (var kv in buckets)
      summary.Errors.Add(new App.ErrorBucket { Message = kv.Key, Count = kv.Value });

    return summary;
  }



  private static string ComputeRequestKey(SolicitudParams p)
  {
    var emisor = p.Issued ? p.RfcSolicitante : p.FilterRfc;
    var receptor = p.Issued ? p.FilterRfc : p.RfcSolicitante;

    var payload = string.Join("|", new[]
    {
            p.Issued ? "Emitidos" : "Recibidos",
            p.TipoSolicitud ?? "",
            p.EstadoComprobante ?? "",
            p.RfcSolicitante ?? "",
            emisor ?? "",
            receptor ?? "",
            p.StartUtc.ToUniversalTime().ToString("O"),
            p.EndUtc.ToUniversalTime().ToString("O")
        });

    using var sha = SHA256.Create();
    return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(payload)));
  }
}
