using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Options;
using OrionERP.Application.Features.Cfdi.CargarXmlSat.Contracts; // ISatXmlInboxService if needed later
using OrionERP.Application.Features.Cfdi.DescargaMasiva.Contracts;
using OrionERP.Web.Features.Cfdi.DescargaMasiva;
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
  [Inject] protected IOptions<SatIntegrationOptions> Opts { get; set; } = default!;

  protected bool Busy { get; set; }
  protected List<SatSolicitudDto> Solicitudes { get; set; } = new();
  protected ProcessSummary? LastSummary { get; set; }

  // UI state
  protected DateTime StartLocal { get; set; } = DateTime.UtcNow.Date;                 // 00:00 today UTC
  protected DateTime EndLocal { get; set; } = DateTime.UtcNow.Date.AddDays(1).AddSeconds(-1); // 23:59:59
  protected bool Issued { get; set; } = false;
  protected string? FilterRfc { get; set; }
  protected string TipoSolicitud { get; set; } = "CFDI";
  protected string? EstadoComprobante { get; set; } = null;

  protected override async Task OnInitializedAsync()
  {
    await LoadSolicitudesAsync();
  }

  private async Task LoadSolicitudesAsync()
  {
    var rows = await SolicitudesRepo.ListAsync(100);
    Solicitudes = new List<SatSolicitudDto>(rows);
    StateHasChanged();
  }

  private X509Certificate2 LoadCert()
  {
    var o = Opts.Value;
    if (o.UsePfx)
    {
      if (string.IsNullOrWhiteSpace(o.PfxPath)) throw new InvalidOperationException("PfxPath vacío");
      return SatCertLoader.FromPfx(o.PfxPath, o.PfxPassword ?? "");
    }
    else
    {
      if (string.IsNullOrWhiteSpace(o.CerPath) || string.IsNullOrWhiteSpace(o.KeyPath))
        throw new InvalidOperationException("CerPath/KeyPath vacíos");
      return SatCertLoader.FromCerAndKey(o.CerPath, o.KeyPath, o.KeyPassword ?? "");
    }
  }

  protected async Task SolicitarAsync()
  {
    if (Busy) return;
    Busy = true; StateHasChanged();
    try
    {
      var cert = LoadCert();
      var p = new SolicitudParams(
          Issued: Issued,
          RfcSolicitante: Opts.Value.RfcSolicitante,
          FilterRfc: FilterRfc,
          TipoSolicitud: TipoSolicitud,
          EstadoComprobante: EstadoComprobante,
          StartUtc: DateTime.SpecifyKind(StartLocal, DateTimeKind.Utc),
          EndUtc: DateTime.SpecifyKind(EndLocal, DateTimeKind.Utc)
      );

      var id = await Coordinator.CreateSolicitudAsync(p);
      // Send/Verify immediately to get a folio and packages if available
      await Coordinator.VerifyAsync(id, LoadCert());
      await LoadSolicitudesAsync();
    }
    catch (Exception ex)
    {
      Console.WriteLine(ex);
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
      foreach (var s in Solicitudes)
      {
        var cert = LoadCert();
        await Coordinator.VerifyAsync(s.Id, cert);
      }
      await LoadSolicitudesAsync();
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
      var cert = LoadCert();
      LastSummary = await Coordinator.DownloadAndProcessAsync(solicitudId, cert);
      await LoadSolicitudesAsync();
    }
    finally
    {
      Busy = false; StateHasChanged();
    }
  }
}
