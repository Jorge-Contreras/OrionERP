using System.Security.Claims;
using OrionERP.Application.Common;
using OrionERP.Infrastructure.Auth;

namespace OrionERP.Web.State;

public sealed class CurrentCompanyContext : ICurrentCompanyContext
{
  private readonly ICompanyIdentityResolver _identityResolver;
  private readonly object _gate = new();
  private string? _currentRfc;
  private string? _displayName;
  private int? _employeeId;
  private long? _companyId;

  public CurrentCompanyContext(ICompanyIdentityResolver identityResolver)
    => _identityResolver = identityResolver ?? throw new ArgumentNullException(nameof(identityResolver));

  public string? CurrentRfc { get { lock (_gate) return _currentRfc; } }
  public string? DisplayName { get { lock (_gate) return _displayName; } }
  public int? EmployeeId { get { lock (_gate) return _employeeId; } }

  public void InitializeFromClaims(ClaimsPrincipal? user)
  {
    if (user?.Identity?.IsAuthenticated != true)
    {
      Clear();
      return;
    }

    var rfcs = user.FindAll(CompanyClaimTypes.Rfc)
      .Select(claim => Normalize(claim.Value))
      .Where(value => value is not null)
      .ToArray();
    if (rfcs.Length != 1)
    {
      Clear();
      return;
    }

    var nextRfc = rfcs[0]!;
    var nextDisplayName = NullIfWhiteSpace(user.FindFirst(CompanyClaimTypes.CompanyName)?.Value);
    var nextEmployeeId = int.TryParse(user.FindFirst(CompanyClaimTypes.EmployeeId)?.Value, out var parsedEmployeeId)
      ? parsedEmployeeId
      : (int?)null;

    lock (_gate)
    {
      if (_currentRfc is not null && !string.Equals(_currentRfc, nextRfc, StringComparison.OrdinalIgnoreCase))
        throw new UnauthorizedAccessException("La empresa de una sesión activa no puede cambiarse sin volver a iniciar sesión.");

      _currentRfc = nextRfc;
      _displayName = nextDisplayName;
      _employeeId = nextEmployeeId;
    }
  }

  public string RequireRfc()
    => CurrentRfc ?? throw new UnauthorizedAccessException("La sesión no tiene una empresa activa. Vuelve a iniciar sesión.");

  /// <summary>
  /// La empresa de una sesión no cambia sin volver a iniciarla, así que resolverla
  /// una vez por sesión es correcto; <see cref="Clear"/> descarta el valor.
  /// </summary>
  public async Task<long> RequireCompanyIdAsync(CancellationToken ct = default)
  {
    var rfc = RequireRfc();
    lock (_gate)
    {
      if (_companyId is { } cached) return cached;
    }

    var resolved = await _identityResolver.ResolveCompanyIdAsync(rfc, ct)
      ?? throw new UnauthorizedAccessException(
        "La empresa de la sesión no está dada de alta en la plataforma o está inactiva.");
    lock (_gate)
    {
      // Otro hilo pudo resolverla mientras tanto; la sesión tiene una sola empresa.
      _companyId ??= resolved;
      return _companyId.Value;
    }
  }

  public void EnsureRfc(string rfc)
  {
    if (!string.Equals(RequireRfc(), Normalize(rfc), StringComparison.OrdinalIgnoreCase))
      throw new UnauthorizedAccessException("El RFC solicitado no corresponde a la empresa de la sesión.");
  }

  private void Clear()
  {
    lock (_gate)
    {
      _currentRfc = null;
      _displayName = null;
      _employeeId = null;
      _companyId = null;
    }
  }

  private static string? Normalize(string? value)
  {
    var normalized = value?.Trim().ToUpperInvariant();
    return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
  }

  private static string? NullIfWhiteSpace(string? value)
    => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
