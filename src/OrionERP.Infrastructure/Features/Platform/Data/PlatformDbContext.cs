using Microsoft.EntityFrameworkCore;

namespace OrionERP.Infrastructure.Features.Platform.Data;

public sealed class PlatformDbContext : DbContext
{
  public PlatformDbContext(DbContextOptions<PlatformDbContext> options)
    : base(options)
  {
  }

  public DbSet<PlatformCompanyEntity> Companies => Set<PlatformCompanyEntity>();
  public DbSet<PlatformSiteEntity> Sites => Set<PlatformSiteEntity>();
  public DbSet<PlatformModuleEntity> Modules => Set<PlatformModuleEntity>();
  public DbSet<PlatformCompanyModuleEntity> CompanyModules => Set<PlatformCompanyModuleEntity>();
  public DbSet<PlatformSiteCapabilityEntity> SiteCapabilities => Set<PlatformSiteCapabilityEntity>();
  public DbSet<PlatformPublicSiteEntity> PublicSites => Set<PlatformPublicSiteEntity>();

  protected override void OnModelCreating(ModelBuilder modelBuilder)
  {
    modelBuilder.HasDefaultSchema("orion");

    modelBuilder.Entity<PlatformCompanyEntity>(entity =>
    {
      entity.ToTable("Company", table =>
      {
        table.HasTrigger("TR_Company_PlatformAudit");
        table.UseSqlOutputClause(false);
      });
      entity.HasKey(company => company.Rfc);
      entity.HasAlternateKey(company => company.CompanyId);
      entity.Property(company => company.CompanyId).ValueGeneratedOnAdd();
      entity.Property(company => company.Rfc).HasMaxLength(50).IsUnicode(false);
      entity.Property(company => company.TaxRfc).HasMaxLength(13).IsUnicode(false);
      entity.Property(company => company.LegacyTenantKey).HasMaxLength(50).IsUnicode(false);
      entity.Property(company => company.DisplayName).HasMaxLength(200);
      entity.Property(company => company.LegalName).HasMaxLength(300);
      entity.Property(company => company.UpdatedBy).HasMaxLength(450);
      entity.Property(company => company.RowVersion).IsRowVersion();
    });

    modelBuilder.Entity<PlatformSiteEntity>(entity =>
    {
      entity.ToTable("Site", table =>
      {
        table.HasTrigger("TR_Site_GuardAudit");
        table.UseSqlOutputClause(false);
      });
      entity.HasKey(site => site.SiteId);
      entity.HasAlternateKey(site => new { site.CompanyId, site.SiteId });
      entity.HasIndex(site => new { site.CompanyId, site.SiteKey }).IsUnique();
      entity.Property(site => site.SiteId).ValueGeneratedOnAdd();
      entity.Property(site => site.SiteKey).HasMaxLength(100).IsUnicode(false);
      entity.Property(site => site.DisplayName).HasMaxLength(200);
      entity.Property(site => site.TimeZoneId).HasMaxLength(100);
      entity.Property(site => site.UpdatedBy).HasMaxLength(450);
      entity.Property(site => site.RowVersion).IsRowVersion();
      entity.HasOne<PlatformCompanyEntity>()
        .WithMany()
        .HasForeignKey(site => site.CompanyId)
        .HasPrincipalKey(company => company.CompanyId)
        .OnDelete(DeleteBehavior.Restrict);
    });

    modelBuilder.Entity<PlatformModuleEntity>(entity =>
    {
      entity.ToTable("Module", table =>
      {
        table.HasTrigger("TR_Module_GuardAudit");
        table.UseSqlOutputClause(false);
      });
      entity.HasKey(module => module.ModuleCode);
      entity.Property(module => module.ModuleCode).HasMaxLength(40).IsUnicode(false);
      entity.Property(module => module.DisplayName).HasMaxLength(100);
      entity.Property(module => module.Description).HasMaxLength(500);
      entity.Property(module => module.UpdatedBy).HasMaxLength(450);
      entity.Property(module => module.RowVersion).IsRowVersion();
    });

    modelBuilder.Entity<PlatformCompanyModuleEntity>(entity =>
    {
      entity.ToTable("CompanyModule", table =>
      {
        table.HasTrigger("TR_CompanyModule_GuardAudit");
        table.UseSqlOutputClause(false);
      });
      entity.HasKey(companyModule => new { companyModule.CompanyId, companyModule.ModuleCode });
      entity.Property(companyModule => companyModule.ModuleCode).HasMaxLength(40).IsUnicode(false);
      entity.Property(companyModule => companyModule.Status).HasMaxLength(20).IsUnicode(false);
      entity.Property(companyModule => companyModule.IsEnabled)
        .HasComputedColumnSql("CONVERT(bit, CASE WHEN [Status]='Enabled' THEN 1 ELSE 0 END)", stored: true);
      entity.Property(companyModule => companyModule.UpdatedBy).HasMaxLength(450);
      entity.Property(companyModule => companyModule.RowVersion).IsRowVersion();
      entity.HasOne<PlatformCompanyEntity>()
        .WithMany()
        .HasForeignKey(companyModule => companyModule.CompanyId)
        .HasPrincipalKey(company => company.CompanyId)
        .OnDelete(DeleteBehavior.Restrict);
      entity.HasOne<PlatformModuleEntity>()
        .WithMany()
        .HasForeignKey(companyModule => companyModule.ModuleCode)
        .OnDelete(DeleteBehavior.Restrict);
    });

    modelBuilder.Entity<PlatformSiteCapabilityEntity>(entity =>
    {
      entity.ToTable("SiteCapability", table =>
      {
        table.HasTrigger("TR_SiteCapability_GuardAudit");
        table.UseSqlOutputClause(false);
      });
      entity.HasKey(capability => new { capability.CompanyId, capability.SiteId, capability.ModuleCode });
      entity.Property(capability => capability.ModuleCode).HasMaxLength(40).IsUnicode(false);
      entity.Property(capability => capability.UpdatedBy).HasMaxLength(450);
      entity.Property(capability => capability.RowVersion).IsRowVersion();
      entity.HasOne<PlatformSiteEntity>()
        .WithMany()
        .HasForeignKey(capability => new { capability.CompanyId, capability.SiteId })
        .HasPrincipalKey(site => new { site.CompanyId, site.SiteId })
        .OnDelete(DeleteBehavior.Restrict);
      entity.HasOne<PlatformCompanyModuleEntity>()
        .WithMany()
        .HasForeignKey(capability => new { capability.CompanyId, capability.ModuleCode })
        .OnDelete(DeleteBehavior.Restrict);
    });

    modelBuilder.Entity<PlatformPublicSiteEntity>(entity =>
    {
      entity.ToTable("PublicSite", table =>
      {
        table.HasTrigger("TR_PublicSite_GuardAudit");
        table.HasCheckConstraint(
          "CK_orion_PublicSite_PresentationFallback",
          "([FallbackBrandingVersion] IS NULL AND [FallbackContentVersion] IS NULL AND [FallbackUntilUtc] IS NULL) OR " +
          "([FallbackBrandingVersion] > 0 AND [FallbackContentVersion] > 0 AND [FallbackUntilUtc] > [UpdatedAtUtc] AND " +
          "[FallbackUntilUtc] <= DATEADD(hour, 2, [UpdatedAtUtc]) AND " +
          "([FallbackBrandingVersion] <> [BrandingVersion] OR [FallbackContentVersion] <> [ContentVersion]))");
        table.UseSqlOutputClause(false);
      });
      entity.HasKey(publicSite => publicSite.PublicSiteId);
      entity.HasIndex(publicSite => publicSite.PublicSiteKey).IsUnique();
      entity.HasIndex(publicSite => publicSite.CanonicalHost).IsUnique();
      entity.HasIndex(publicSite => new { publicSite.CompanyId, publicSite.SiteId, publicSite.ModuleCode }).IsUnique();
      entity.Property(publicSite => publicSite.PublicSiteId).ValueGeneratedOnAdd();
      entity.Property(publicSite => publicSite.PublicSiteKey).HasMaxLength(100).IsUnicode(false);
      entity.Property(publicSite => publicSite.ModuleCode).HasMaxLength(40).IsUnicode(false);
      entity.Property(publicSite => publicSite.CanonicalHost).HasMaxLength(253).IsUnicode(false);
      entity.Property(publicSite => publicSite.UpdatedBy).HasMaxLength(450);
      entity.Property(publicSite => publicSite.RowVersion).IsRowVersion();
      entity.HasOne<PlatformCompanyEntity>()
        .WithMany()
        .HasForeignKey(publicSite => publicSite.CompanyId)
        .HasPrincipalKey(company => company.CompanyId)
        .OnDelete(DeleteBehavior.Restrict);
      entity.HasOne<PlatformSiteEntity>()
        .WithMany()
        .HasForeignKey(publicSite => new { publicSite.CompanyId, publicSite.SiteId })
        .HasPrincipalKey(site => new { site.CompanyId, site.SiteId })
        .OnDelete(DeleteBehavior.Restrict);
      entity.HasOne<PlatformCompanyModuleEntity>()
        .WithMany()
        .HasForeignKey(publicSite => new { publicSite.CompanyId, publicSite.ModuleCode })
        .OnDelete(DeleteBehavior.Restrict);
      entity.HasOne<PlatformSiteCapabilityEntity>()
        .WithMany()
        .HasForeignKey(publicSite => new { publicSite.CompanyId, publicSite.SiteId, publicSite.ModuleCode })
        .OnDelete(DeleteBehavior.Restrict);
    });
  }
}
