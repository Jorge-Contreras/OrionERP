using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace OrionERP.Infrastructure.Auth;

public sealed class BrunoIdentityDbContext
  : IdentityDbContext<BrunoMemberUser, IdentityRole, string>
{
  public BrunoIdentityDbContext(DbContextOptions<BrunoIdentityDbContext> options)
    : base(options)
  {
  }

  protected override void OnModelCreating(ModelBuilder builder)
  {
    base.OnModelCreating(builder);
    builder.HasDefaultSchema("brunos_auth");

    builder.Entity<BrunoMemberUser>(entity =>
    {
      entity.Property(user => user.FirstName).HasMaxLength(100).IsRequired();
      entity.Property(user => user.LastName).HasMaxLength(100).IsRequired();
      entity.Property(user => user.CreatedAt).HasPrecision(0);
      entity.Property(user => user.ClosedAt).HasPrecision(0);
    });
  }
}
