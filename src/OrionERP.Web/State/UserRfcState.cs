using System.Security.Claims;
using OrionERP.Application.Common;
using OrionERP.Infrastructure.Auth;

namespace OrionERP.Web.State;

public sealed class UserRfcState : IUserRfcState, ICurrentCompanyContext
{
  private readonly object _gate = new();
  private string? _current;
  private string? _displayName;
  private int? _employeeId;

  public string? CurrentRfc
  {
    get { lock (_gate) return _current; }
    private set
    {
      bool changed;
      lock (_gate)
      {
        if (string.Equals(_current, value, StringComparison.OrdinalIgnoreCase))
        {
          changed = false;
        }
        else
        {
          _current = value;
          changed = true;
        }
      }
      if (changed) Changed?.Invoke();
    }
  }

  public string? DisplayName { get { lock (_gate) return _displayName; } }
  public int? EmployeeId { get { lock (_gate) return _employeeId; } }

  public event Action? Changed;

  public void InitializeFromClaims(ClaimsPrincipal user)
  {
    if (user?.Identity is null || !user.Identity.IsAuthenticated)
    {
      bool cleared;
      lock (_gate)
      {
        cleared = _current is not null || _displayName is not null || _employeeId is not null;
        if (cleared)
        {
          _current = null;
          _displayName = null;
          _employeeId = null;
        }
      }

      if (cleared)
      {
        Changed?.Invoke();
      }

      return;
    }

    var fromClaims = user.FindAll(CompanyClaimTypes.Rfc)
      .Select(claim => claim.Value.Trim().ToUpperInvariant())
      .Where(value => value.Length > 0)
      .Distinct(StringComparer.OrdinalIgnoreCase)
      .ToArray();
    var nextRfc = fromClaims.Length == 1 ? fromClaims[0] : null;
    var nextDisplayName = user.FindFirst(CompanyClaimTypes.CompanyName)?.Value;
    var nextEmployeeId = int.TryParse(user.FindFirst(CompanyClaimTypes.EmployeeId)?.Value, out var parsedEmployeeId)
      ? parsedEmployeeId
      : (int?)null;

    bool changed = false;
    lock (_gate)
    {
      if (!string.Equals(_current, nextRfc, StringComparison.OrdinalIgnoreCase))
      {
        _current = nextRfc;
        changed = true;
      }
      if (!string.Equals(_displayName, nextDisplayName, StringComparison.Ordinal))
      {
        _displayName = nextDisplayName;
        changed = true;
      }
      if (_employeeId != nextEmployeeId)
      {
        _employeeId = nextEmployeeId;
        changed = true;
      }
    }

    if (changed)
    {
      Changed?.Invoke();
    }
  }

  public string RequireRfc()
    => CurrentRfc ?? throw new UnauthorizedAccessException("La sesión no tiene una empresa activa.");

  public void EnsureRfc(string rfc)
  {
    if (!string.Equals(RequireRfc(), rfc?.Trim(), StringComparison.OrdinalIgnoreCase))
      throw new UnauthorizedAccessException("El RFC solicitado no corresponde a la empresa de la sesión.");
  }
}
