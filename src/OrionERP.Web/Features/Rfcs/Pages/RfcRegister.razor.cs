using OrionERP.Application.Common;
using System;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using OrionERP.Web.State;
using OrionERP.Web.Services;
using OrionERP.Web.Features.Shared;
using OrionERP.Web.Features.TrainingSafety;
using AppRfcs = OrionERP.Application.Features.Rfcs.Contracts;
using Sat.MassiveDownload.Crypto;

namespace OrionERP.Web.Features.Rfcs.Pages
{
  public partial class RfcRegisterBase : ComponentBase, IDisposable
  {
    [Inject] protected AppRfcs.ISatRfcProfileRepository Repo { get; set; } = default!;
    [Inject] protected ICurrentCompanyContext RfcState { get; set; } = default!;
    [Inject] protected IUiMessageService UiMessages { get; set; } = default!;
    [Inject] protected ITrainingEnvironmentState TrainingEnvironmentState { get; set; } = default!;

    // Make the type accessible to the derived .razor
    protected class FormModel
    {
      public string? Rfc { get; set; }
      public string? RazonSocial { get; set; }
      public string? NombreComercial { get; set; }
      public string? RegimenCapital { get; set; }
      public DateTime? FechaInicioOperaciones { get; set; }
      public string? EstatusPadron { get; set; }
      public DateTime? FechaUltCambioEstatus { get; set; }
      public DateTime? EmisionFecha { get; set; }
      public string? AddressLine1 { get; set; }
      public string? AddressLine2 { get; set; }
      public string? Municipio { get; set; }
      public string? EntidadFederativa { get; set; }
      public string? CodigoPostal { get; set; }
      public string? CsfDataJson { get; set; }
      public string? Email { get; set; }

      public CredentialMode Mode { get; set; } = CredentialMode.Pfx;
      public byte[]? PfxBytes { get; set; }
      public byte[]? CerBytes { get; set; }
      public byte[]? KeyBytes { get; set; }
      public string? PasswordPlain { get; set; }
    }

    // Enum must also be accessible
    protected enum CredentialMode { Pfx, CerKey }

    // Expose the model to the derived .razor
    protected FormModel Model { get; } = new();

    // Estatus messages and notifications
    protected bool Busy { get; set; }
    protected bool Validating { get; set; }
    protected string? UiMessage { get; set; }
    protected string UiMessageCss { get; set; } = "alert-success";
    private CancellationTokenSource? _msgCts;
    protected override async Task OnInitializedAsync()
    {
      await base.OnInitializedAsync();
      if (TrainingEnvironmentState.IsTraining) return;
      await LoadCurrentRfcAsync();
    }


    protected async Task ShowMessageAsync(string text, string css = "alert-success", int ms = 3500)
    {
      _msgCts?.Cancel();
      _msgCts = new();
      UiMessage = text;
      UiMessageCss = css;
      PublishUiMessage(text, css);
      StateHasChanged();

      try { await Task.Delay(ms, _msgCts.Token); UiMessage = null; StateHasChanged(); }
      catch (TaskCanceledException) { /* ignore */ }
      finally
      {
        if (UiMessages.Current?.Message == text)
        {
          UiMessages.Clear();
        }
      }
    }

    protected async Task ShowSuccessAsync(string text) => await ShowMessageAsync(text, "alert-success");
    protected async Task ShowErrorAsync(string text) => await ShowMessageAsync(text, "alert-danger", 6000);





    // File handlers the .razor will call
    protected async Task OnPfxSelected(InputFileChangeEventArgs e)
      => Model.PfxBytes = await ReadAllAsync(e.File);

    protected async Task OnCerSelected(InputFileChangeEventArgs e)
      => Model.CerBytes = await ReadAllAsync(e.File);

    protected async Task OnKeySelected(InputFileChangeEventArgs e)
      => Model.KeyBytes = await ReadAllAsync(e.File);

    protected async Task ValidateFielAsync()
    {
      if (TrainingEnvironmentState.IsTraining)
      {
        await ShowErrorAsync("Las credenciales FIEL están bloqueadas en el entorno de capacitación.");
        return;
      }

      if (Validating) return;

      if (Model.Mode != CredentialMode.CerKey)
      {
        await ShowErrorAsync("La validación aplica únicamente para archivos .CER y .KEY.");
        return;
      }

      if (string.IsNullOrWhiteSpace(Model.PasswordPlain))
      {
        await ShowErrorAsync("Proporciona la contraseña de la FIEL.");
        return;
      }

      if (Model.CerBytes is not { Length: > 0 })
      {
        await ShowErrorAsync("Selecciona el archivo .CER.");
        return;
      }

      if (Model.KeyBytes is not { Length: > 0 })
      {
        await ShowErrorAsync("Selecciona el archivo .KEY.");
        return;
      }

      Validating = true;
      StateHasChanged();

      try
      {
        using var certificate = CertificateLoader.FromCerAndKeyBytes(
          Model.CerBytes,
          Model.KeyBytes,
          Model.PasswordPlain);

        await ShowSuccessAsync($"Certificado válido: {certificate.Subject}");
      }
      catch (Exception ex)
      {
        await ShowErrorAsync($"No se pudo validar la FIEL: {ex.Message}");
      }
      finally
      {
        Validating = false;
        StateHasChanged();
      }
    }

    private static async Task<byte[]> ReadAllAsync(IBrowserFile file)
    {
      await using var s = file.OpenReadStream(long.MaxValue);
      using var ms = new MemoryStream();
      await s.CopyToAsync(ms);
      return ms.ToArray();
    }

    // Submit handler the .razor will call
    protected async Task SaveAsync()
    {
      if (TrainingEnvironmentState.IsTraining)
      {
        await ShowErrorAsync("El entorno de capacitación no permite guardar credenciales fiscales.");
        return;
      }

      var sessionRfc = RfcState.RequireRfc();
      Model.Rfc = sessionRfc;
      var dto = new AppRfcs.SatRfcProfileUpsert
      {
        Rfc = sessionRfc,
        RazonSocial = Model.RazonSocial,
        NombreComercial = Model.NombreComercial,
        RegimenCapital = Model.RegimenCapital,
        FechaInicioOperaciones = Model.FechaInicioOperaciones,
        EstatusPadron = Model.EstatusPadron,
        FechaUltCambioEstatus = Model.FechaUltCambioEstatus,
        EmisionFecha = Model.EmisionFecha,
        AddressLine1 = Model.AddressLine1,
        AddressLine2 = Model.AddressLine2,
        Municipio = Model.Municipio,
        EntidadFederativa = Model.EntidadFederativa,
        CodigoPostal = Model.CodigoPostal,
        CsfDataJson = Model.CsfDataJson,
        Email = Model.Email
      };

      if (Model.Mode == CredentialMode.Pfx)
      {
        dto.SATFielPfx = Model.PfxBytes;
        dto.SATFielCertificate = null;
        dto.SATFielKey = null;
      }
      else
      {
        dto.SATFielCertificate = Model.CerBytes;
        dto.SATFielKey = Model.KeyBytes;
        dto.SATFielPfx = null;
      }

      dto.SATFielPasswordEnc = RazorPageDataProtector.ProtectUtf8OrNull(Model.PasswordPlain);

      if (Busy) return;
      Busy = true; StateHasChanged();
      try
      {
        await Repo.UpsertAsync(dto);     // your existing repo call
        await ShowSuccessAsync($"RFC {Model.Rfc} guardado correctamente.");
        // Optionally clear part of the form here if you want
      }
      catch (Exception ex)
      {
        await ShowErrorAsync($"Error al guardar: {ex.Message}");
      }
      finally
      {
        Busy = false; StateHasChanged();
      }
      // TODO: clear form or show a toast
    }

    private async Task LoadCurrentRfcAsync()
    {
      var current = RfcState.RequireRfc();

      try
      {
        var profile = await Repo.GetAsync(current);
        await InvokeAsync(() =>
        {
          ApplyProfile(profile, current);
          StateHasChanged();
          return Task.CompletedTask;
        });
      }
      catch (Exception ex)
      {
        await InvokeAsync(async () =>
        {
          ResetModel(current);
          await ShowErrorAsync($"Error al cargar RFC {current}: {ex.Message}");
        });
      }
    }

    private void ApplyProfile(AppRfcs.SatRfcProfile? profile, string rfc)
    {
      ResetModel(rfc);
      if (profile is null)
      {
        Model.Rfc = rfc;
        return;
      }

      RfcState.EnsureRfc(profile.Rfc);
      Model.Rfc = profile.Rfc;
      Model.RazonSocial = profile.RazonSocial;
      Model.NombreComercial = profile.NombreComercial;
      Model.RegimenCapital = profile.RegimenCapital;
      Model.FechaInicioOperaciones = profile.FechaInicioOperaciones;
      Model.EstatusPadron = profile.EstatusPadron;
      Model.FechaUltCambioEstatus = profile.FechaUltCambioEstatus;
      Model.EmisionFecha = profile.EmisionFecha;
      Model.AddressLine1 = profile.AddressLine1;
      Model.AddressLine2 = profile.AddressLine2;
      Model.Municipio = profile.Municipio;
      Model.EntidadFederativa = profile.EntidadFederativa;
      Model.CodigoPostal = profile.CodigoPostal;
      Model.CsfDataJson = profile.CsfDataJson;
      Model.Email = profile.Email;

      if (profile.SATFielPfx is { Length: > 0 })
      {
        Model.Mode = CredentialMode.Pfx;
        Model.PfxBytes = profile.SATFielPfx;
        Model.CerBytes = null;
        Model.KeyBytes = null;
      }
      else
      {
        Model.Mode = CredentialMode.CerKey;
        Model.CerBytes = profile.SATFielCertificate;
        Model.KeyBytes = profile.SATFielKey;
        Model.PfxBytes = null;
      }

      Model.PasswordPlain = RazorPageDataProtector.UnprotectUtf8OrNull(profile.SATFielPasswordEnc);
    }

    private void ResetModel(string? rfc = null)
    {
      Model.Rfc = rfc;
      Model.RazonSocial = null;
      Model.NombreComercial = null;
      Model.RegimenCapital = null;
      Model.FechaInicioOperaciones = null;
      Model.EstatusPadron = null;
      Model.FechaUltCambioEstatus = null;
      Model.EmisionFecha = null;
      Model.AddressLine1 = null;
      Model.AddressLine2 = null;
      Model.Municipio = null;
      Model.EntidadFederativa = null;
      Model.CodigoPostal = null;
      Model.CsfDataJson = null;
      Model.Email = null;
      Model.Mode = CredentialMode.Pfx;
      Model.PfxBytes = null;
      Model.CerBytes = null;
      Model.KeyBytes = null;
      Model.PasswordPlain = null;
    }

    public void Dispose()
    {
      _msgCts?.Cancel();
      _msgCts?.Dispose();
    }

    private void PublishUiMessage(string text, string css)
    {
      var level = css switch
      {
        "alert-danger" => UiMessageLevel.Error,
        "alert-warning" => UiMessageLevel.Warning,
        "alert-success" => UiMessageLevel.Success,
        _ => UiMessageLevel.Info
      };
      UiMessages.Show(new UiMessage(level, text));
    }
  }
}
