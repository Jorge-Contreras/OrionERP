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
  [Inject] protected IUserRfcState RfcState { get; set; } = default!;
  [Inject] protected IUiMessageService UiMessages { get; set; } = default!;
  


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
      UiMessages.ShowError($"Error al cargar solicitudes: {ex.Message}");
    }
  }

  private async Task<X509Certificate2> LoadCertAsync()
  {
    var currentRfc = RfcState.CurrentRfc;
    if (string.IsNullOrWhiteSpace(currentRfc))
      throw new InvalidOperationException("Selecciona un RFC para continuar.");

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
      var p = new SolicitudParams(
          Issued: Issued,
          RfcSolicitante: RfcState.CurrentRfc,
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
      UiMessages.ShowError($"Error al solicitar descarga: {ex.Message}");
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
      UiMessages.ShowError($"Error al actualizar estado: {ex.Message}");
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
      LastSummary = await Coordinator.DownloadAndProcessAsync(solicitudId, cert);

      // 2) Mark as Procesada (7) BEFORE notifying the user
      var current = await SolicitudesRepo.GetAsync(solicitudId);
      await SolicitudesRepo.UpdateVerifySnapshotAsync(
        solicitudId,
        new SatVerifySnapshot
        {
          Estado = EstadoSolicitud.Procesada, // = 7
                                              // Keep whatever SAT codes you might already have:
          CodigoEstadoSolicitud = current?.CodigoEstadoSolicitud,
          CodEstatus = current?.CodEstatus,
          Mensaje = "Procesada localmente",
          NumeroCfdis = current?.NumeroCfdis ?? 0,
          PackageIds = Array.Empty<string>(),
          // Also close the lifecycle if it wasn’t already closed:
          IsTerminated = true
        });

      // 3) Refresh grid and notify
      await LoadSolicitudesAsync();
      UiMessages.ShowSuccess("Descarga y procesamiento completados.");
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"Error al descargar y procesar: {ex.Message}");
    }
    finally
    {
      Busy = false; StateHasChanged();
    }
  }

}
