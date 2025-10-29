using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using OrionERP.Web.State;
using OrionERP.Web.Services;
using AppRfcs = OrionERP.Application.Features.Rfcs.Contracts;
using Sat.MassiveDownload.Crypto;

namespace OrionERP.Web.Features.Rfcs.Pages
{
  public partial class RfcRegisterBase : ComponentBase, IDisposable
  {
    [Inject] protected AppRfcs.ISatRfcProfileRepository Repo { get; set; } = default!;
    [Inject] protected IUserRfcState RfcState { get; set; } = default!;
    [Inject] protected IUiMessageService UiMessages { get; set; } = default!;

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
    private const string EncryptionKeyFileName = "rfc-register.aes.key";
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private static readonly Lazy<byte[]> EncryptionKey = new(LoadOrCreateKey, true);

    protected override void OnInitialized()
    {
      base.OnInitialized();
      RfcState.Changed += OnRfcChanged;
      _ = LoadCurrentRfcAsync();

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
      var dto = new AppRfcs.SatRfcProfileUpsert
      {
        Rfc = Model.Rfc?.Trim()?.ToUpperInvariant() ?? string.Empty,
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

      dto.SATFielPasswordEnc = ProtectUtf8OrNull(Model.PasswordPlain);

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

    // Can stay private; only SaveAsync uses it
    private static byte[] LoadOrCreateKey()
    {
      var appDataDirectory = Path.Combine(AppContext.BaseDirectory, "App_Data");
      Directory.CreateDirectory(appDataDirectory);
      var keyPath = Path.Combine(appDataDirectory, EncryptionKeyFileName);

      if (File.Exists(keyPath))
      {
        var existing = File.ReadAllBytes(keyPath);
        if (existing.Length == 32)
        {
          return existing;
        }
      }

      var key = RandomNumberGenerator.GetBytes(32);
      using var fileStream = new FileStream(keyPath, FileMode.Create, FileAccess.Write, FileShare.None);
      fileStream.Write(key, 0, key.Length);
      return key;
    }

    private static byte[]? ProtectUtf8OrNull(string? plaintext)
    {
      if (string.IsNullOrEmpty(plaintext)) return null;
      var bytes = Encoding.UTF8.GetBytes(plaintext);
      var nonce = RandomNumberGenerator.GetBytes(NonceSize);
      var ciphertext = new byte[bytes.Length];
      var tag = new byte[TagSize];

      using var aesGcm = new AesGcm(EncryptionKey.Value);
      aesGcm.Encrypt(nonce, bytes, ciphertext, tag);

      var payload = new byte[NonceSize + TagSize + ciphertext.Length];
      Buffer.BlockCopy(nonce, 0, payload, 0, NonceSize);
      Buffer.BlockCopy(tag, 0, payload, NonceSize, TagSize);
      Buffer.BlockCopy(ciphertext, 0, payload, NonceSize + TagSize, ciphertext.Length);
      return payload;
    }

    private static string? UnprotectUtf8OrNull(byte[]? ciphertext)
    {
      if (ciphertext is not { Length: > 0 }) return null;

      if (ciphertext.Length < NonceSize + TagSize) return null;

      try
      {
        var ciphertextSpan = ciphertext.AsSpan();
        var nonce = ciphertextSpan[..NonceSize];
        var tag = ciphertextSpan.Slice(NonceSize, TagSize);
        var encryptedData = ciphertextSpan[(NonceSize + TagSize)..];
        var plaintext = new byte[encryptedData.Length];

        using var aesGcm = new AesGcm(EncryptionKey.Value);
        aesGcm.Decrypt(nonce, encryptedData, tag, plaintext);
        return Encoding.UTF8.GetString(plaintext);
      }
      catch (CryptographicException)
      {
        return null;
      }
    }

    private void OnRfcChanged() => _ = InvokeAsync(LoadCurrentRfcAsync);

    private async Task LoadCurrentRfcAsync()
    {
      var current = RfcState.CurrentRfc;
      if (string.IsNullOrWhiteSpace(current))
      {
        await InvokeAsync(() =>
        {
          ResetModel();
          StateHasChanged();
          return Task.CompletedTask;
        });
        return;
      }

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

      Model.PasswordPlain = UnprotectUtf8OrNull(profile.SATFielPasswordEnc);
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
     RfcState.Changed -= OnRfcChanged;
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
