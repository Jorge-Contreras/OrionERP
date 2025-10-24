using System.Security.Claims;

namespace OrionERP.Web.State;

public interface IUserRfcState
{
  /// <summary>Current RFC selection for this user session/circuit.</summary>
  string? CurrentRfc { get; }

  /// <summary>RFCs allowed for the signed-in user, derived from "rfc" claims.</summary>
  IReadOnlyList<string> AllowedRfcs { get; }

  /// <summary>Initialize the allowed RFCs from the user's claims (idempotent).</summary>
  void InitializeFromClaims(ClaimsPrincipal user);

  /// <summary>Attempt to set a new current RFC. Returns true if it is allowed and changed.</summary>
  bool TrySet(string rfc);

  /// <summary>Reset CurrentRfc to the default (first allowed or null).</summary>
  void ResetToDefault();

  /// <summary>Raised whenever CurrentRfc changes.</summary>
  event Action? Changed;
}
