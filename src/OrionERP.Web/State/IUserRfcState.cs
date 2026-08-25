using System.Security.Claims;

namespace OrionERP.Web.State;

public interface IUserRfcState
{
  /// <summary>The RFC fixed into the authenticated company session.</summary>
  string? CurrentRfc { get; }

  /// <summary>Initialize the company from the authenticated session claims.</summary>
  void InitializeFromClaims(ClaimsPrincipal user);

  /// <summary>Raised only when authentication establishes or clears a company.</summary>
  event Action? Changed;
}
