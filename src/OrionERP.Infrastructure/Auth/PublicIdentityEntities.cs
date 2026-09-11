using Microsoft.AspNetCore.Identity;

namespace OrionERP.Infrastructure.Auth;

public sealed class PublicSiteRole : IdentityRole
{
  public long PublicSiteId { get; set; }
}

public sealed class PublicSiteUserClaim : IdentityUserClaim<string>
{
  public long PublicSiteId { get; set; }
}

public sealed class PublicSiteUserRole : IdentityUserRole<string>
{
  public long PublicSiteId { get; set; }
}

public sealed class PublicSiteUserLogin : IdentityUserLogin<string>
{
  public long PublicSiteId { get; set; }
}

public sealed class PublicSiteRoleClaim : IdentityRoleClaim<string>
{
  public long PublicSiteId { get; set; }
}

public sealed class PublicSiteUserToken : IdentityUserToken<string>
{
  public long PublicSiteId { get; set; }
}
