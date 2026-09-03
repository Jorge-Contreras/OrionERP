using OrionERP.Application.Common;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Options;
using OrionERP.Application.Features.Cfdi.CargarXmlSat.Contracts; // ISatXmlInboxService if needed later
using OrionERP.Application.Features.Cfdi.DescargaMasiva.Contracts;
using OrionERP.Application.Features.Rfcs.Contracts;
using OrionERP.Web.Features.Cfdi.DescargaMasiva;
using OrionERP.Web.State;
using OrionERP.Web.Services;
using OrionERP.Web.Features.Shared;
using Sat.MassiveDownload.Crypto; // CertificateLoader (from your Sat.MassiveDownload lib)
using System;
using System.Collections.Generic;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using SatCertLoader = Sat.MassiveDownload.Crypto.CertificateLoader; // alias ours


namespace OrionERP.Web.Features.Cfdi.DescargaMasiva.Pages;

public class SatDescargaPage : ComponentBase
{
  [Inject] protected ISatDownloadCoordinator Coordinator { get; set; } = default!;
  [Inject] protected ISatSolicitudesRepository SolicitudesRepo { get; set; } = default!;
  [Inject] protected ISatRfcProfileRepository RfcProfiles { get; set; } = default!;
  [Inject] protected ICurrentCompanyContext RfcState { get; set; } = default!;
  [Inject] protected IUiMessageService UiMessages { get; set; } = default!;
  [Inject] protected IOperationErrorPresenter Errors { get; set; } = default!;
  


  protected bool Busy { get; set; }
  protected List<SatSolicitudDto> Solicitudes { get; set; } = new();
  protected ProcessSummary? LastSummary { get; set; }

  // UI state
  protected DateTime StartLocal { get; set; } = DateTime.Now.Date;                
  protected DateTime EndLocal { get; set; } = DateTime.Now.Date.AddDays(1).AddSeconds(-1); // 23:59:59
  protected bool Issued { get; set; } = false;
  protected string? FilterRfc { get; set; }

  protected string? RfcSolicitante { get; set; }
  protected string TipoSolicitud { get; set; } = "CFDI";
  protected string? EstadoComprobante { get; set; } = null;

  protected override async Task OnInitializedAsync()
  {
    await LoadSolicitudesAsync();
  }

  public async Task LoadSolicitudesAsync()
  {
    try
    {
      var rows = await SolicitudesRepo.ListAsync(15);
      Solicitudes = new List<SatSolicitudDto>(rows);
      StateHasChanged();
    }
    catch (Exception ex)
    {
      UiMessages.ShowError(Errors.ToUserMessage(ex, "cargar las solicitudes de descarga del SAT"));
    }
  }

  private async Task<X509Certificate2> LoadCertAsync()
  {
    var currentRfc = RfcState.RequireRfc();

    var profile = await RfcProfiles.GetAsync(currentRfc);
    if (profile is null)
      throw new InvalidOperationException($"No se encontraron credenciales para el RFC {currentRfc}.");

    if (profile.SATFielCertificate is not { Length: > 0 } || profile.SATFielKey is not { Length: > 0 })
      throw new InvalidOperationException($"El RFC {currentRfc} no tiene certificados .CER/.KEY registrados.");

    var password = RazorPageDataProtector.UnprotectUtf8OrNull(profile.SATFielPasswordEnc) ?? string.Empty;

    return SatCertLoader.FromCerAndKeyBytes(
      profile.SATFielCertificate,
      profile.SATFielKey,
      password);
  }

  protected async Task SolicitarAsync()
  {
    if (Busy) return;
      Busy = true; StateHasChanged();
      try
      {
        var cert = await LoadCertAsync();
        var rfcSolicitante = RfcState.RequireRfc();
        var p = new SolicitudParams(
            Issued: Issued,
            RfcSolicitante: rfcSolicitante,
            FilterRfc: FilterRfc,
            TipoSolicitud: TipoSolicitud,
            EstadoComprobante: EstadoComprobante,
          StartUtc: DateTime.SpecifyKind(StartLocal, DateTimeKind.Utc),
          EndUtc: DateTime.SpecifyKind(EndLocal, DateTimeKind.Utc)
      );

      var id = await Coordinator.CreateSolicitudAsync(p);
      // Send/Verify immediately to get a folio and packages if available
      await Coordinator.VerifyAsync(id, cert);
      await LoadSolicitudesAsync();
      UiMessages.ShowSuccess("Solicitud enviada al SAT correctamente.");
    }
    catch (Exception ex)
    {
      UiMessages.ShowError(Errors.ToUserMessage(ex, "enviar la solicitud de descarga al SAT",
        new { Issued, TipoSolicitud, EstadoComprobante, FilterRfc }));
    }
    finally
    {
      Busy = false; StateHasChanged();
    }
  }

  protected async Task RefrescarAsync()
  {
    if (Busy) return;
    Busy = true; StateHasChanged();

    try
    {
      // Only those with EstadoSolicitud == 1
      var toVerify = Solicitudes
        .Where(s => s.EstadoSolicitud.HasValue && (s.EstadoSolicitud.Value == 1 || s.EstadoSolicitud.Value== 2 ))
        .ToList();

      if (toVerify.Count > 0)
      {
        var cert = await LoadCertAsync();

        foreach (var s in toVerify)
        {
          await Coordinator.VerifyAsync(s.Id, cert);
        }
      }

      await LoadSolicitudesAsync();
      UiMessages.ShowSuccess("Estado de solicitudes actualizado.");
    }
    catch (Exception ex)
    {
      UiMessages.ShowError(Errors.ToUserMessage(ex, "actualizar el estado de las solicitudes ante el SAT"));
    }
    finally
    {
      Busy = false; StateHasChanged();
    }
  }


  protected async Task DescargarYProcesarAsync(int solicitudId)
  {
    if (Busy) return;
    Busy = true; StateHasChanged();
    try
    {
      var cert = await LoadCertAsync();

      // 1) Download + process
      var summary = await Coordinator.DownloadAndProcessAsync(solicitudId, cert);
      LastSummary = summary;

      var processedFiles = SatProcessingOutcome.ProcessedFiles(summary);
      var failures = SatProcessingOutcome.Failures(summary);
      var completedCleanly = SatProcessingOutcome.CompletedCleanly(summary);

      // 2) Solo se cierra la solicitud (Procesada / terminada) cuando TODO se descargó y
      // procesó sin fallas. Cerrarla con paquetes pendientes o con errores dejaría CFDIs
      // fiscales fuera del sistema sin posibilidad de reintento desde esta pantalla.
      var current = await SolicitudesRepo.GetAsync(solicitudId);
      if (completedCleanly)
      {
        await SolicitudesRepo.UpdateVerifySnapshotAsync(
          solicitudId,
          new SatVerifySnapshot
          {
            Estado = EstadoSolicitud.Procesada, // = 7
            CodigoEstadoSolicitud = current?.CodigoEstadoSolicitud,
            CodEstatus = current?.CodEstatus,
            Mensaje = "Procesada localmente",
            NumeroCfdis = current?.NumeroCfdis ?? processedFiles,
            PackageIds = Array.Empty<string>(),
            IsTerminated = true
          });
      }

      // 3) Refresh grid and notify
      await LoadSolicitudesAsync();

      if (completedCleanly)
      {
        UiMessages.ShowSuccess($"Descarga y procesamiento completados: {processedFiles} comprobante(s) en {summary.Packages} paquete(s).");
      }
      else if (SatProcessingOutcome.NoPackagesYet(summary))
      {
        UiMessages.ShowWarning("El SAT todavía no tiene listos los paquetes de esta solicitud. Usa \"Actualizar estado\" en unos minutos y vuelve a intentar la descarga. La solicitud sigue abierta.");
      }
      else
      {
        UiMessages.ShowWarning($"Se procesaron {processedFiles - failures} de {processedFiles} comprobante(s); {failures} con error. La solicitud queda abierta para reintentar los paquetes pendientes. Revisa el detalle por paquete en la lista.");
      }
    }
    catch (Exception ex)
    {
      UiMessages.ShowError(Errors.ToUserMessage(ex, "descargar y procesar los paquetes del SAT", new { solicitudId }));
    }
    finally
    {
      Busy = false; StateHasChanged();
    }
  }

}
