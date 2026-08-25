using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace OrionERP.Infrastructure.Auth
{
    public class OrionIdentityDbContext
        : IdentityDbContext<ApplicationUser, IdentityRole, string>
    {
        public OrionIdentityDbContext(DbContextOptions<OrionIdentityDbContext> options)
            : base(options)
        {
        }

        public DbSet<OrionCompany> Companies => Set<OrionCompany>();
        public DbSet<UserCompany> UserCompanies => Set<UserCompany>();
        public DbSet<UserCompanyRole> UserCompanyRoles => Set<UserCompanyRole>();
        public DbSet<CompanyAccessAudit> CompanyAccessAudits => Set<CompanyAccessAudit>();

        protected override void OnModelCreating(ModelBuilder b)
        {
            base.OnModelCreating(b);
            b.HasDefaultSchema("auth");

            b.Entity<ApplicationUser>()
                .HasIndex(user => user.ArrendadorProveedorId)
                .IsUnique()
                .HasDatabaseName("IX_AspNetUsers_ArrendadorProveedorId")
                .HasFilter("[ArrendadorProveedorId] IS NOT NULL");

            b.Entity<IdentityRole>()
                .Property<string>("Scope")
                .HasMaxLength(20)
                .HasDefaultValue(IdentityRoleScopes.Company);

            b.Entity<OrionCompany>(entity =>
            {
                entity.ToTable("Company", "orion", table =>
                {
                    table.HasTrigger("TR_Company_BlockDelete");
                    table.HasTrigger("TR_Company_ImmutableRfc");
                    table.UseSqlOutputClause(false);
                });
                entity.HasKey(company => company.Rfc);
                entity.Property(company => company.Rfc).HasMaxLength(50).IsUnicode(false);
                entity.Property(company => company.DisplayName).HasMaxLength(200);
                entity.Property(company => company.LegalName).HasMaxLength(300);
                entity.Property(company => company.LogoContentType).HasMaxLength(50).IsUnicode(false);
                entity.Property(company => company.RowVersion).IsRowVersion();
            });

            b.Entity<UserCompany>(entity =>
            {
                entity.ToTable("AspNetUserCompanies", "auth");
                entity.HasKey(membership => new { membership.UserId, membership.Rfc });
                entity.Property(membership => membership.Rfc).HasMaxLength(50).IsUnicode(false);
                entity.Property(membership => membership.RowVersion).IsRowVersion();
                entity.HasIndex(membership => membership.EmployeeId)
                    .IsUnique()
                    .HasFilter("[EmployeeId] IS NOT NULL");
                entity.HasOne(membership => membership.User)
                    .WithMany(user => user.Companies)
                    .HasForeignKey(membership => membership.UserId)
                    .OnDelete(DeleteBehavior.Cascade);
                entity.HasOne(membership => membership.Company)
                    .WithMany(company => company.Users)
                    .HasForeignKey(membership => membership.Rfc)
                    .OnDelete(DeleteBehavior.Restrict);
            });

            b.Entity<UserCompanyRole>(entity =>
            {
                entity.ToTable("AspNetUserCompanyRoles", "auth");
                entity.HasKey(link => new { link.UserId, link.Rfc, link.RoleId });
                entity.Property(link => link.Rfc).HasMaxLength(50).IsUnicode(false);
                entity.HasOne(link => link.Membership)
                    .WithMany(membership => membership.Roles)
                    .HasForeignKey(link => new { link.UserId, link.Rfc })
                    .OnDelete(DeleteBehavior.Cascade);
                entity.HasOne(link => link.Role)
                    .WithMany()
                    .HasForeignKey(link => link.RoleId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            b.Entity<CompanyAccessAudit>(entity =>
            {
                entity.ToTable("CompanyAccessAudit", "auth", table =>
                {
                    table.HasTrigger("TR_CompanyAccessAudit_AppendOnly");
                    table.UseSqlOutputClause(false);
                });
                entity.HasKey(audit => audit.Id);
                entity.Property(audit => audit.ActorUserId).HasMaxLength(450);
                entity.Property(audit => audit.Action).HasMaxLength(80).IsUnicode(false);
                entity.Property(audit => audit.TargetUserId).HasMaxLength(450);
                entity.Property(audit => audit.Rfc).HasMaxLength(50).IsUnicode(false);
                entity.Property(audit => audit.RoleId).HasMaxLength(450);
            });
        }
    }
}
