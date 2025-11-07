using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OrionERP.Application.Features.Cfdi.CargarXmlSat.Contracts;
using OrionERP.Application.Features.Cfdi.DescargaMasiva.Contracts;
using Sat.MassiveDownload.Core;
using SatEstado = Sat.MassiveDownload.Models.EstadoSolicitud;
using App = OrionERP.Application.Features.Cfdi.DescargaMasiva.Contracts;

namespace OrionERP.Infrastructure.Features.Cfdi.DescargaMasiva.Services;

public sealed class SatDownloadCoordinator : ISatDownloadCoordinator
{
    private static EstadoSolicitud Map(SatEstado estado)
        => (EstadoSolicitud)(int)estado;

    private readonly ISatMassiveService _sat;
    private readonly ISatSolicitudesRepository _solicitudes;
    private readonly ISatPaquetesRepository _paquetes;
    private readonly ISatXmlInboxService _inbox;
    private readonly ISatMetadataIngestService _meta;

    public SatDownloadCoordinator(
        ISatMassiveService sat,
        ISatSolicitudesRepository solicitudes,
        ISatPaquetesRepository paquetes,
        ISatXmlInboxService inbox,
        ISatMetadataIngestService meta)
    {
        _sat = sat ?? throw new ArgumentNullException(nameof(sat));
        _solicitudes = solicitudes ?? throw new ArgumentNullException(nameof(solicitudes));
        _paquetes = paquetes ?? throw new ArgumentNullException(nameof(paquetes));
        _inbox = inbox ?? throw new ArgumentNullException(nameof(inbox));
        _meta = meta ?? throw new ArgumentNullException(nameof(meta));
    }

    public async Task<int> CreateSolicitudAsync(SolicitudParams parameters, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        var requestKey = ComputeRequestKey(parameters);
        var existing = await _solicitudes.FindByRequestKeyAsync(requestKey, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing.Id;
        }

        var estado = !parameters.Issued && string.Equals(parameters.TipoSolicitud, "CFDI", StringComparison.OrdinalIgnoreCase)
            ? parameters.EstadoComprobante ?? "Vigente"
            : parameters.EstadoComprobante;

        var emisor = parameters.Issued ? parameters.RfcSolicitante : parameters.FilterRfc;
        var receptor = parameters.Issued ? parameters.FilterRfc : parameters.RfcSolicitante;

        var dto = new SatSolicitudDto
        {
            RfcSolicitante = parameters.RfcSolicitante,
            Issued = parameters.Issued,
            TipoSolicitud = parameters.TipoSolicitud,
            EstadoComprobante = estado,
            RfcEmisor = emisor,
            RfcReceptor = receptor,
            FechaInicialUtc = parameters.StartUtc.ToUniversalTime(),
            FechaFinalUtc = parameters.EndUtc.ToUniversalTime(),
            PackageCount = 0
        };

        var id = await _solicitudes.InsertAsync(dto, requestKey, ct).ConfigureAwait(false);
        return id;
    }

    public async Task<VerifyResultDto> VerifyAsync(int solicitudId, X509Certificate2 cert, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cert);

        var solicitud = await _solicitudes.GetAsync(solicitudId, ct).ConfigureAwait(false)
                       ?? throw new InvalidOperationException("Solicitud no encontrada");

        await _sat.AuthenticateAsync(cert, ct).ConfigureAwait(false);

        if (!solicitud.Folio.HasValue || solicitud.Folio.Value == Guid.Empty)
        {
            var folioStr = await _sat.RequestAsync(
                solicitud.FechaInicialUtc,
                solicitud.FechaFinalUtc,
                solicitud.Issued,
                solicitud.RfcSolicitante,
                solicitud.Issued ? solicitud.RfcReceptor : solicitud.RfcEmisor,
                solicitud.TipoSolicitud,
                solicitud.EstadoComprobante,
                ct).ConfigureAwait(false);

            if (Guid.TryParse(folioStr?.Trim(), out var folioGuid))
            {
                await _solicitudes.SetFolioAsync(solicitud.Id, folioGuid, ct).ConfigureAwait(false);
                solicitud.Folio = folioGuid;
            }
            else if (!string.IsNullOrWhiteSpace(folioStr))
            {
                Console.Error.WriteLine($"[WARN] Folio no es GUID: '{folioStr}'");
            }

            await _solicitudes.UpdateVerifySnapshotAsync(solicitud.Id, new SatVerifySnapshot
            {
                Estado = Map(SatEstado.Aceptada),
                CodigoEstadoSolicitud = null,
                CodEstatus = "5000",
                Mensaje = "Solicitud Aceptada",
                NumeroCfdis = 0,
                PackageIds = Array.Empty<string>(),
                IsTerminated = false
            }, ct).ConfigureAwait(false);

            solicitud = await _solicitudes.GetAsync(solicitudId, ct).ConfigureAwait(false)
                         ?? throw new InvalidOperationException("Solicitud no encontrada (post-insert)");
        }

        var folio = solicitud.Folio ?? throw new InvalidOperationException("La solicitud no tiene folio asignado.");
        var verify = await _sat.VerifyAsync(folio.ToString(), solicitud.RfcSolicitante, ct).ConfigureAwait(false);

        await _solicitudes.UpdateVerifySnapshotAsync(solicitud.Id, new SatVerifySnapshot
        {
            Estado = Map(verify.Estado),
            CodigoEstadoSolicitud = verify.CodigoEstadoSolicitud,
            CodEstatus = verify.CodEstatus,
            Mensaje = verify.Mensaje,
            NumeroCfdis = verify.NumeroCfdis,
            PackageIds = verify.PackageIds,
            IsTerminated = Map(verify.Estado) == Map(SatEstado.Terminada)
        }, ct).ConfigureAwait(false);

        return new VerifyResultDto
        {
            Estado = Map(verify.Estado),
            CodigoEstadoSolicitud = verify.CodigoEstadoSolicitud,
            CodEstatus = verify.CodEstatus,
            Mensaje = verify.Mensaje,
            NumeroCfdis = verify.NumeroCfdis,
            PackageIds = verify.PackageIds,
            HumanStatus = verify.HumanStatus
        };
    }

    public async Task<App.ProcessSummary> DownloadAndProcessAsync(int solicitudId, X509Certificate2 cert, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cert);

        var solicitud = await _solicitudes.GetAsync(solicitudId, ct).ConfigureAwait(false)
                       ?? throw new InvalidOperationException("Solicitud no encontrada");

        await _sat.AuthenticateAsync(cert, ct).ConfigureAwait(false);

        var paquetes = await _paquetes.ListBySolicitudAsync(solicitudId, ct).ConfigureAwait(false);
        var summary = new App.ProcessSummary();
        var isMetadata = string.Equals(solicitud.TipoSolicitud, "Metadata", StringComparison.OrdinalIgnoreCase);

        foreach (var paquete in paquetes.Where(p => !p.Processed))
        {
            try
            {
                var bytes = await _sat.DownloadPackageAsync(paquete.PackageId, solicitud.RfcSolicitante, ct).ConfigureAwait(false);
                if (bytes is null || bytes.Length == 0)
                {
                    await _paquetes.MarkProcessedAsync(solicitudId, paquete.PackageId, new App.SatPackageProcessInfo
                    {
                        XmlCount = 0,
                        SuccessCount = 0,
                        FailureCount = 0,
                        ZipSizeBytes = 0,
                        ErrorMessage = "ZIP vacío"
                    }, ct).ConfigureAwait(false);
                    continue;
                }

                if (isMetadata)
                {
                    var metaDetails = await SatZipIngestion.PushZipMetadataAsync(paquete.PackageId, bytes, _meta, ct).ConfigureAwait(false);
                    var ok = metaDetails.Count(d => d.Success);
                    var fail = metaDetails.Count - ok;

                    await _paquetes.MarkProcessedAsync(solicitudId, paquete.PackageId, new App.SatPackageProcessInfo
                    {
                        XmlCount = metaDetails.Count,
                        SuccessCount = ok,
                        FailureCount = fail,
                        ZipSizeBytes = bytes.LongLength
                    }, ct).ConfigureAwait(false);

                    summary.Packages++;
                    summary.MetaFiles += metaDetails.Count;
                    summary.MetaOk += ok;
                    summary.MetaFail += fail;
                    summary.MetaDetails.AddRange(metaDetails);
                }
                else
                {
                    var details = await SatZipIngestion.PushZipToInboxWithMetadataAsync(paquete.PackageId, bytes, _inbox, ct).ConfigureAwait(false);
                    var ok = details.Count(d => d.Success);
                    var fail = details.Count - ok;

                    await _paquetes.MarkProcessedAsync(solicitudId, paquete.PackageId, new App.SatPackageProcessInfo
                    {
                        XmlCount = details.Count,
                        SuccessCount = ok,
                        FailureCount = fail,
                        ZipSizeBytes = bytes.LongLength
                    }, ct).ConfigureAwait(false);

                    summary.Packages++;
                    summary.Xmls += details.Count;
                    summary.Ok += ok;
                    summary.Fail += fail;
                    summary.Details.AddRange(details);
                }
            }
            catch (Exception ex)
            {
                await _paquetes.MarkProcessedAsync(solicitudId, paquete.PackageId, new App.SatPackageProcessInfo
                {
                    XmlCount = 0,
                    SuccessCount = 0,
                    FailureCount = 0,
                    ZipSizeBytes = 0,
                    ErrorMessage = ex.Message
                }, ct).ConfigureAwait(false);
            }
        }

        foreach (var detail in summary.Details)
        {
            if (!string.IsNullOrWhiteSpace(detail.RfcEmisor))
            {
                summary.ByEmisor[detail.RfcEmisor] = summary.ByEmisor.TryGetValue(detail.RfcEmisor, out var current)
                    ? current + 1
                    : 1;
            }

            if (!string.IsNullOrWhiteSpace(detail.RfcReceptor))
            {
                summary.ByReceptor[detail.RfcReceptor] = summary.ByReceptor.TryGetValue(detail.RfcReceptor, out var current)
                    ? current + 1
                    : 1;
            }

            if (detail.Success && detail.Total is not null)
            {
                summary.TotalImporte = (summary.TotalImporte ?? 0m) + detail.Total.Value;
            }
        }

        var errorBuckets = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var detail in summary.Details)
        {
            if (!detail.Success && !string.IsNullOrWhiteSpace(detail.Error))
            {
                errorBuckets[detail.Error] = errorBuckets.TryGetValue(detail.Error, out var current)
                    ? current + 1
                    : 1;
            }
        }

        foreach (var (message, count) in errorBuckets)
        {
            summary.Errors.Add(new App.ErrorBucket { Message = message, Count = count });
        }

        return summary;
    }

    private static string ComputeRequestKey(SolicitudParams parameters)
    {
        var emisor = parameters.Issued ? parameters.RfcSolicitante : parameters.FilterRfc;
        var receptor = parameters.Issued ? parameters.FilterRfc : parameters.RfcSolicitante;

        var payload = string.Join("|", new[]
        {
            parameters.Issued ? "Emitidos" : "Recibidos",
            parameters.TipoSolicitud ?? string.Empty,
            parameters.EstadoComprobante ?? string.Empty,
            parameters.RfcSolicitante ?? string.Empty,
            emisor ?? string.Empty,
            receptor ?? string.Empty,
            parameters.StartUtc.ToUniversalTime().ToString("O"),
            parameters.EndUtc.ToUniversalTime().ToString("O")
        });

        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(payload)));
    }
}
