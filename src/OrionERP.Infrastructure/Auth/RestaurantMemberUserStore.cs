using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace OrionERP.Infrastructure.Auth;

/// <summary>
/// Tenant-aware Identity store for the reusable restaurant website. Every
/// lookup and mutation is constrained to the database-verified PublicSiteId.
/// </summary>
public sealed class RestaurantMemberUserStore
  : UserStore<BrunoMemberUser, IdentityRole, BrunoIdentityDbContext, string>
{
  private readonly BrunoIdentityDbContext _db;
  private readonly IRestaurantPublicIdentityScopeAccessor _scopeAccessor;

  public RestaurantMemberUserStore(
    BrunoIdentityDbContext db,
    IRestaurantPublicIdentityScopeAccessor scopeAccessor,
    IdentityErrorDescriber describer)
    : base(db, describer)
  {
    _db = db ?? throw new ArgumentNullException(nameof(db));
    _scopeAccessor = scopeAccessor ?? throw new ArgumentNullException(nameof(scopeAccessor));
  }

  public override Task<BrunoMemberUser?> FindByIdAsync(
    string userId,
    CancellationToken cancellationToken = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(userId);
    var publicSiteId = CurrentPublicSiteId;
    return _db.Users.SingleOrDefaultAsync(
      user => user.Id == userId && user.PublicSiteId == publicSiteId,
      cancellationToken);
  }

  public override Task<BrunoMemberUser?> FindByNameAsync(
    string normalizedUserName,
    CancellationToken cancellationToken = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(normalizedUserName);
    var publicSiteId = CurrentPublicSiteId;
    return _db.Users.SingleOrDefaultAsync(
      user => user.PublicSiteId == publicSiteId
        && user.NormalizedUserName == normalizedUserName,
      cancellationToken);
  }

  public override Task<BrunoMemberUser?> FindByEmailAsync(
    string normalizedEmail,
    CancellationToken cancellationToken = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(normalizedEmail);
    var publicSiteId = CurrentPublicSiteId;
    return _db.Users.SingleOrDefaultAsync(
      user => user.PublicSiteId == publicSiteId
        && user.NormalizedEmail == normalizedEmail,
      cancellationToken);
  }

  public override async Task<BrunoMemberUser?> FindByLoginAsync(
    string loginProvider,
    string providerKey,
    CancellationToken cancellationToken = default)
  {
    var user = await base.FindByLoginAsync(loginProvider, providerKey, cancellationToken);
    return user is not null && IsCurrent(user) ? user : null;
  }

  public override Task<IdentityResult> CreateAsync(
    BrunoMemberUser user,
    CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(user);
    var publicSiteId = CurrentPublicSiteId;
    if (user.PublicSiteId != 0 && user.PublicSiteId != publicSiteId)
    {
      return Task.FromResult(ScopeMismatch());
    }

    user.PublicSiteId = publicSiteId;
    return base.CreateAsync(user, cancellationToken);
  }

  public override Task<IdentityResult> UpdateAsync(
    BrunoMemberUser user,
    CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(user);
    return IsCurrent(user)
      ? base.UpdateAsync(user, cancellationToken)
      : Task.FromResult(ScopeMismatch());
  }

  public override Task<IdentityResult> DeleteAsync(
    BrunoMemberUser user,
    CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(user);
    return IsCurrent(user)
      ? base.DeleteAsync(user, cancellationToken)
      : Task.FromResult(ScopeMismatch());
  }

  private long CurrentPublicSiteId
  {
    get
    {
      var publicSiteId = _scopeAccessor.Current.PublicSiteId;
      return publicSiteId > 0
        ? publicSiteId
        : throw new InvalidOperationException("The restaurant identity scope is not database verified.");
    }
  }

  private bool IsCurrent(BrunoMemberUser user)
    => user.PublicSiteId == CurrentPublicSiteId;

  private static IdentityResult ScopeMismatch() => IdentityResult.Failed(new IdentityError
  {
    Code = "PublicSiteMismatch",
    Description = "The member account does not belong to this public restaurant site."
  });
}
