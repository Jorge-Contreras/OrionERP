using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace OrionERP.Infrastructure.Auth;

public sealed class PublicIdentityDbContext
  : IdentityDbContext<
    PublicSiteUser,
    PublicSiteRole,
    string,
    PublicSiteUserClaim,
    PublicSiteUserRole,
    PublicSiteUserLogin,
    PublicSiteRoleClaim,
    PublicSiteUserToken>
{
  private readonly IPublicIdentityScopeAccessor _identityScope;

  public PublicIdentityDbContext(
    DbContextOptions<PublicIdentityDbContext> options,
    IPublicIdentityScopeAccessor identityScope)
    : base(options)
  {
    _identityScope = identityScope ?? throw new ArgumentNullException(nameof(identityScope));
  }

  private long CurrentPublicSiteId => _identityScope.Current.PublicSiteId;

  protected override void OnModelCreating(ModelBuilder builder)
  {
    base.OnModelCreating(builder);
    builder.HasDefaultSchema("public_identity");

    builder.Entity<PublicSiteUser>(entity =>
    {
      entity.ToTable("AspNetUsers", "public_identity", table =>
      {
        table.HasTrigger("TR_AspNetUsers_PublicIdentityScope");
        table.UseSqlOutputClause(false);
      });
      entity.HasQueryFilter(user => user.PublicSiteId == CurrentPublicSiteId);
      entity.Property(user => user.PublicSiteId).IsRequired();
      entity.Property(user => user.FirstName).HasMaxLength(100).IsRequired();
      entity.Property(user => user.LastName).HasMaxLength(100).IsRequired();
      entity.Property(user => user.CreatedAt).HasPrecision(0);
      entity.Property(user => user.ClosedAt).HasPrecision(0);

      var normalizedUserName = entity.Metadata.FindProperty(nameof(PublicSiteUser.NormalizedUserName))!;
      var legacyUserNameIndex = entity.Metadata.FindIndex(normalizedUserName);
      if (legacyUserNameIndex is not null)
      {
        entity.Metadata.RemoveIndex(legacyUserNameIndex);
      }

      var normalizedEmail = entity.Metadata.FindProperty(nameof(PublicSiteUser.NormalizedEmail))!;
      var legacyEmailIndex = entity.Metadata.FindIndex(normalizedEmail);
      if (legacyEmailIndex is not null)
      {
        entity.Metadata.RemoveIndex(legacyEmailIndex);
      }

      entity.HasIndex(user => new { user.PublicSiteId, user.NormalizedUserName })
        .IsUnique()
        .HasDatabaseName("UserNameIndex_PublicSite")
        .HasFilter("[NormalizedUserName] IS NOT NULL");
      entity.HasIndex(user => new { user.PublicSiteId, user.NormalizedEmail })
        .IsUnique()
        .HasDatabaseName("EmailIndex_PublicSite")
        .HasFilter("[NormalizedEmail] IS NOT NULL");
      entity.HasIndex(user => new { user.Id, user.PublicSiteId })
        .IsUnique()
        .HasDatabaseName("UX_PublicIdentityUsers_IdPublicSite");
    });

    builder.Entity<PublicSiteRole>(entity =>
    {
      entity.ToTable("AspNetRoles", "public_identity");
      entity.HasQueryFilter(role => role.PublicSiteId == CurrentPublicSiteId);
      entity.Property(role => role.PublicSiteId).IsRequired();
      var normalizedRoleName = entity.Metadata.FindProperty(nameof(PublicSiteRole.NormalizedName))!;
      var legacyRoleNameIndex = entity.Metadata.FindIndex(normalizedRoleName);
      if (legacyRoleNameIndex is not null)
        entity.Metadata.RemoveIndex(legacyRoleNameIndex);
      entity.HasIndex(role => new { role.Id, role.PublicSiteId }).IsUnique()
        .HasDatabaseName("UX_PublicIdentityRoles_IdPublicSite");
      entity.HasIndex(role => new { role.PublicSiteId, role.NormalizedName }).IsUnique()
        .HasDatabaseName("RoleNameIndex_PublicSite")
        .HasFilter("[NormalizedName] IS NOT NULL");
    });

    builder.Entity<PublicSiteUserClaim>(entity => ConfigureChild(entity, "AspNetUserClaims"));
    builder.Entity<PublicSiteUserRole>(entity =>
    {
      ConfigureChild(entity, "AspNetUserRoles");
      entity.HasKey(item => new { item.PublicSiteId, item.UserId, item.RoleId });
    });
    builder.Entity<PublicSiteUserLogin>(entity =>
    {
      ConfigureChild(entity, "AspNetUserLogins");
      entity.HasKey(item => new { item.PublicSiteId, item.LoginProvider, item.ProviderKey });
    });
    builder.Entity<PublicSiteRoleClaim>(entity => ConfigureChild(entity, "AspNetRoleClaims"));
    builder.Entity<PublicSiteUserToken>(entity =>
    {
      ConfigureChild(entity, "AspNetUserTokens");
      entity.HasKey(item => new { item.PublicSiteId, item.UserId, item.LoginProvider, item.Name });
    });
  }

  public override int SaveChanges(bool acceptAllChangesOnSuccess)
  {
    StampAndValidatePublicSite();
    return base.SaveChanges(acceptAllChangesOnSuccess);
  }

  public override Task<int> SaveChangesAsync(
    bool acceptAllChangesOnSuccess,
    CancellationToken cancellationToken = default)
  {
    StampAndValidatePublicSite();
    return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
  }

  private void ConfigureChild<TEntity>(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<TEntity> entity, string table)
    where TEntity : class
  {
    entity.ToTable(table, "public_identity");
    entity.Property<long>(nameof(PublicSiteUser.PublicSiteId)).IsRequired();
    entity.HasQueryFilter(item => EF.Property<long>(item, nameof(PublicSiteUser.PublicSiteId)) == CurrentPublicSiteId);
  }

  private void StampAndValidatePublicSite()
  {
    var publicSiteId = CurrentPublicSiteId;
    if (publicSiteId <= 0)
      throw new InvalidOperationException("Public identity requires a verified PublicSiteId.");

    foreach (var entry in ChangeTracker.Entries().Where(entry =>
      (entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted) &&
      entry.Metadata.FindProperty(nameof(PublicSiteUser.PublicSiteId)) is not null))
    {
      var property = entry.Property(nameof(PublicSiteUser.PublicSiteId));
      var existing = Convert.ToInt64(property.CurrentValue ?? 0L, System.Globalization.CultureInfo.InvariantCulture);
      if (entry.State == EntityState.Added && existing == 0)
        property.CurrentValue = publicSiteId;
      else if (existing != publicSiteId)
        throw new UnauthorizedAccessException("The Identity row does not belong to the verified PublicSite.");
    }
  }
}
