using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace OrionERP.Infrastructure.Auth;

public sealed class BrunoIdentityDbContext
  : IdentityDbContext<BrunoMemberUser, IdentityRole, string>
{
  private readonly IRestaurantPublicIdentityScopeAccessor _identityScope;

  public BrunoIdentityDbContext(
    DbContextOptions<BrunoIdentityDbContext> options,
    IRestaurantPublicIdentityScopeAccessor identityScope)
    : base(options)
  {
    _identityScope = identityScope ?? throw new ArgumentNullException(nameof(identityScope));
  }

  private long CurrentPublicSiteId => _identityScope.Current.PublicSiteId;

  protected override void OnModelCreating(ModelBuilder builder)
  {
    base.OnModelCreating(builder);
    builder.HasDefaultSchema("brunos_auth");

    builder.Entity<BrunoMemberUser>(entity =>
    {
      entity.ToTable("AspNetUsers", "brunos_auth", table =>
      {
        table.HasTrigger("TR_AspNetUsers_RestaurantIdentityScope");
        table.UseSqlOutputClause(false);
      });
      entity.HasQueryFilter(user => user.PublicSiteId == CurrentPublicSiteId);
      entity.Property(user => user.PublicSiteId).IsRequired();
      entity.Property(user => user.FirstName).HasMaxLength(100).IsRequired();
      entity.Property(user => user.LastName).HasMaxLength(100).IsRequired();
      entity.Property(user => user.CreatedAt).HasPrecision(0);
      entity.Property(user => user.ClosedAt).HasPrecision(0);

      var normalizedUserName = entity.Metadata.FindProperty(nameof(BrunoMemberUser.NormalizedUserName))!;
      var legacyUserNameIndex = entity.Metadata.FindIndex(normalizedUserName);
      if (legacyUserNameIndex is not null)
      {
        entity.Metadata.RemoveIndex(legacyUserNameIndex);
      }

      var normalizedEmail = entity.Metadata.FindProperty(nameof(BrunoMemberUser.NormalizedEmail))!;
      var legacyEmailIndex = entity.Metadata.FindIndex(normalizedEmail);
      if (legacyEmailIndex is not null)
      {
        entity.Metadata.RemoveIndex(legacyEmailIndex);
      }

      entity.HasIndex(user => new { user.PublicSiteId, user.NormalizedUserName })
        .IsUnique()
        .HasDatabaseName("UserNameIndex_Bruno")
        .HasFilter("[NormalizedUserName] IS NOT NULL");
      entity.HasIndex(user => new { user.PublicSiteId, user.NormalizedEmail })
        .IsUnique()
        .HasDatabaseName("EmailIndex_Bruno")
        .HasFilter("[NormalizedEmail] IS NOT NULL");
      entity.HasIndex(user => new { user.Id, user.PublicSiteId })
        .IsUnique()
        .HasDatabaseName("UX_BrunoAspNetUsers_IdPublicSite");
    });
  }
}
