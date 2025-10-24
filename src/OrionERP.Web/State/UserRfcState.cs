using System.Security.Claims;

namespace OrionERP.Web.State;

public sealed class UserRfcState : IUserRfcState
{
  private readonly object _gate = new();
  private HashSet<string> _allowed = new(StringComparer.OrdinalIgnoreCase);
  private string? _current;

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

  public IReadOnlyList<string> AllowedRfcs
  {
    get { lock (_gate) return _allowed.OrderBy(r => r).ToList(); }
  }

  public event Action? Changed;

  public void InitializeFromClaims(ClaimsPrincipal user)
  {
    if (user?.Identity is null || !user.Identity.IsAuthenticated) return;

    var fromClaims = user.FindAll("rfc")
                         .Select(c => c.Value)
                         .Where(v => !string.IsNullOrWhiteSpace(v))
                         .ToHashSet(StringComparer.OrdinalIgnoreCase);

    lock (_gate)
    {
      _allowed = fromClaims;
      if (_allowed.Count == 0)
      {
        _current = null;
      }
      else if (_current is null || !_allowed.Contains(_current))
      {
        _current = _allowed.OrderBy(r => r).FirstOrDefault();
      }
    }
    Changed?.Invoke();
  }

  public bool TrySet(string rfc)
  {
    if (string.IsNullOrWhiteSpace(rfc)) return false;
    lock (_gate)
    {
      if (!_allowed.Contains(rfc)) return false;
    }
    CurrentRfc = rfc;
    return true;
  }

  public void ResetToDefault()
  {
    lock (_gate)
    {
      _current = _allowed.OrderBy(r => r).FirstOrDefault();
    }
    Changed?.Invoke();
  }
}
