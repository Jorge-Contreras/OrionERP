using Microsoft.AspNetCore.Components.Authorization;
using OrionERP.Application.Common;

namespace OrionERP.Web.Services;

public sealed class AuthenticationStateCurrentUserAccessor : ICurrentUserAccessor
{
  private readonly AuthenticationStateProvider _authenticationStateProvider;

  public AuthenticationStateCurrentUserAccessor(AuthenticationStateProvider authenticationStateProvider)
  {
    _authenticationStateProvider = authenticationStateProvider ?? throw new ArgumentNullException(nameof(authenticationStateProvider));
  }

  public async ValueTask<string?> GetUserNameAsync(CancellationToken cancellationToken = default)
  {
    var authState = await _authenticationStateProvider.GetAuthenticationStateAsync();
    return authState.User.Identity?.Name?.Trim();
  }
}
