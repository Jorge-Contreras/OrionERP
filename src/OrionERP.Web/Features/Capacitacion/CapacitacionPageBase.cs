using OrionERP.Application.Common;
using Microsoft.AspNetCore.Components;
using OrionERP.Application.Features.CapitalHumano.Workforce;
using OrionERP.Application.Features.Capacitacion;
using OrionERP.Web.State;

namespace OrionERP.Web.Features.Capacitacion;

public abstract class CapacitacionPageBase : ComponentBase, IAsyncDisposable
{
  private readonly CancellationTokenSource _lifetime = new();

  [Inject] protected ICapacitacionService Capacitacion { get; set; } = default!;
  [Inject] protected ICurrentEmployeeAccessor CurrentEmployeeAccessor { get; set; } = default!;
  [Inject] protected ICurrentCompanyContext RfcState { get; set; } = default!;
  [Inject] protected NavigationManager Navigation { get; set; } = default!;
  [Inject] protected IConfiguration Configuration { get; set; } = default!;
  [Inject] protected ILogger<CapacitacionPageBase> Logger { get; set; } = default!;

  protected CurrentEmployeeContext? Actor { get; private set; }
  protected string Rfc { get; private set; } = string.Empty;
  protected CancellationToken LifetimeToken => _lifetime.Token;
  protected string? PageError { get; set; }
  protected string? PageMessage { get; set; }
  protected bool MessageIsError { get; set; }

  /// <summary>Cada página carga aquí sus datos para la empresa ligada a la sesión.</summary>
  protected virtual Task LoadPageDataAsync() => Task.CompletedTask;

  protected override async Task OnInitializedAsync()
  {
    await base.OnInitializedAsync();
    await ReloadPageDataAsync();
  }

  protected async Task ReloadPageDataAsync()
  {
    try { await LoadPageDataAsync(); }
    catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    if (!_lifetime.IsCancellationRequested) StateHasChanged();
  }

  protected async Task<bool> InitializeActorAsync()
  {
    Actor = await CurrentEmployeeAccessor.GetCurrentAsync(_lifetime.Token);
    Rfc = RfcState.RequireRfc();

    if (Actor?.EmployeeId is null)
    {
      PageError = "Tu usuario no está vinculado a un colaborador. Capital Humano debe completar el vínculo antes de usar Capacitación.";
      return false;
    }

    // Capacitación siempre se cursa en la empresa donde el usuario está dado de alta.
    if (!string.IsNullOrWhiteSpace(Actor.CompanyRfc)
        && !string.Equals(Actor.CompanyRfc, Rfc, StringComparison.OrdinalIgnoreCase))
    {
      PageError = $"Tu sesión pertenece a la empresa {Actor.CompanyRfc}, "
        + $"no a {Rfc}. Cierra sesión e ingresa con la empresa correcta para usar Capacitación.";
      return false;
    }

    return true;
  }

  protected CapacitacionActorContext CreateActorContext() => new()
  {
    Rfc = Rfc,
    EmployeeId = Actor?.EmployeeId ?? 0,
    Actor = Actor?.UserName ?? "OrionERP"
  };

  protected void ShowResult(CapacitacionCommandResult result)
  {
    PageMessage = result.Message;
    MessageIsError = !result.Success;
  }

  protected void ShowException(Exception exception, string fallback)
  {
    LogException(exception, fallback);
    PageMessage = fallback;
    MessageIsError = true;
  }

  protected void ShowPageException(Exception exception, string fallback)
  {
    LogException(exception, fallback);
    PageError = fallback;
  }

  protected void LogException(Exception exception, string operation)
    => Logger.LogError(
      exception,
      "Capacitación UI operation failed: {Operation}. Route: {Route}",
      operation,
      Navigation.ToBaseRelativePath(Navigation.Uri));

  protected string? BuildSandboxUrl(string? relativePath)
  {
    var configuredOrigin = Configuration["Capacitacion:SandboxBaseUrl"];
    if (!Uri.TryCreate(configuredOrigin, UriKind.Absolute, out var origin)
        || origin.Scheme is not ("http" or "https")
        || origin.UserInfo.Length > 0)
      return null;

    var safeOrigin = new Uri(origin.GetLeftPart(UriPartial.Authority).TrimEnd('/') + "/", UriKind.Absolute);
    if (string.IsNullOrWhiteSpace(relativePath)) return safeOrigin.ToString();
    relativePath = relativePath.Trim();
    if (relativePath.Any(char.IsControl)
        || relativePath.Contains('\\')
        || relativePath.StartsWith("//", StringComparison.Ordinal)) return null;
    if (Uri.TryCreate(relativePath, UriKind.Absolute, out var absolute))
      return absolute.Scheme is "http" or "https"
        && absolute.UserInfo.Length == 0
        && absolute.GetLeftPart(UriPartial.Authority).Equals(
        safeOrigin.GetLeftPart(UriPartial.Authority),
        StringComparison.OrdinalIgnoreCase) ? absolute.ToString() : null;

    var normalizedPath = relativePath.TrimStart('/');
    return new Uri(safeOrigin, normalizedPath).ToString();
  }

  protected string? BuildResourceUrl(string? value)
  {
    if (string.IsNullOrWhiteSpace(value)) return null;
    value = value.Trim();
    if (value.Any(char.IsControl) || value.Contains('\\')) return null;

    if (value[0] == '/' && !value.StartsWith("//", StringComparison.Ordinal))
      return Uri.TryCreate(value, UriKind.Relative, out _) ? value : null;

    if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.UserInfo.Length > 0)
      return null;

    var candidateAuthority = uri.GetLeftPart(UriPartial.Authority);
    var allowedAuthorities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    if (Uri.TryCreate(Configuration["Capacitacion:SandboxBaseUrl"], UriKind.Absolute, out var sandbox)
        && sandbox.Scheme is "http" or "https")
      allowedAuthorities.Add(sandbox.GetLeftPart(UriPartial.Authority));

    foreach (var child in Configuration.GetSection("Capacitacion:AllowedVisualAidOrigins").GetChildren())
    {
      if (Uri.TryCreate(child.Value, UriKind.Absolute, out var allowed)
          && allowed.Scheme == Uri.UriSchemeHttps
          && allowed.UserInfo.Length == 0)
        allowedAuthorities.Add(allowed.GetLeftPart(UriPartial.Authority));
    }

    return uri.Scheme is "http" or "https" && allowedAuthorities.Contains(candidateAuthority)
      ? uri.AbsoluteUri
      : null;
  }

  public virtual ValueTask DisposeAsync()
  {
    _lifetime.Cancel();
    _lifetime.Dispose();
    return ValueTask.CompletedTask;
  }
}
