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
  protected DateTime StartLocal { get; set; } = DateTime.UtcNow.Date;                 // 00:00 today UTC
  protected DateTime EndLocal { get; set; } = DateTime.UtcNow.Date.AddDays(1).AddSeconds(-1); // 23:59:59
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
      var rows = await SolicitudesRepo.ListAsync(100);
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
      var cert = await LoadCertAsync();
      foreach (var s in Solicitudes)
      {
        await Coordinator.VerifyAsync(s.Id, cert);
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
      LastSummary = await Coordinator.DownloadAndProcessAsync(solicitudId, cert);
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
