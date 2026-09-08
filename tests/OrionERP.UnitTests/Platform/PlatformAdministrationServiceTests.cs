using Microsoft.EntityFrameworkCore;
using OrionERP.Application.Features.Platform;
using OrionERP.Infrastructure.Features.Platform;
using OrionERP.Infrastructure.Features.Platform.Data;

namespace OrionERP.UnitTests.Platform;

public sealed class PlatformAdministrationServiceTests
{
  private static readonly DateTime Now = new(2026, 9, 2, 14, 0, 0, DateTimeKind.Utc);

  [Fact]
  public async Task CreateSite_AlwaysUsesTheCompanyFromTheTrustedScope()
  {
    await using var db = CreateContext();
    await SeedCompaniesAsync(db);
    var service = CreateService(db, "AAA010101AAA");

    var siteId = await service.CreateSiteAsync(new(
      "centro",
      "Sucursal Centro",
      "Central Standard Time (Mexico)",
      true));

    var site = await db.Sites.SingleAsync(item => item.SiteId == siteId);
    Assert.Equal(11, site.CompanyId);
    Assert.False(await db.Sites.AnyAsync(item => item.CompanyId == 22));
  }

  [Fact]
  public async Task UpdateCompanyMetadata_ChangesOnlyFiscalAndLegacyIdentityForTheScopedCompany()
  {
    await using var db = CreateContext();
    await SeedCompaniesAsync(db);
    var service = CreateService(db, "AAA010101AAA");

    await service.UpdateCompanyMetadataAsync(new(
      "aaa010101aa1",
      "legacy-a",
      Token(1)));

    var company = await db.Companies.SingleAsync(item => item.CompanyId == 11);
    var other = await db.Companies.SingleAsync(item => item.CompanyId == 22);
    Assert.Equal("AAA010101AA1", company.TaxRfc);
    Assert.Equal("legacy-a", company.LegacyTenantKey);
    Assert.Equal("AAA010101AAA", company.Rfc);
    Assert.Null(other.TaxRfc);
  }

  [Fact]
  public async Task UpdateSite_DoesNotRevealOrModifyAnotherCompanySite()
  {
    await using var db = CreateContext();
    await SeedCompaniesAsync(db);
    db.Sites.Add(new PlatformSiteEntity
    {
      SiteId = 220,
      CompanyId = 22,
      SiteKey = "other",
      DisplayName = "Other site",
      TimeZoneId = "UTC",
      IsActive = true,
      RowVersion = Bytes(2)
    });
    await db.SaveChangesAsync();
    db.ChangeTracker.Clear();
    var service = CreateService(db, "AAA010101AAA");

    var exception = await Assert.ThrowsAsync<PlatformAdministrationValidationException>(() =>
      service.UpdateSiteAsync(new(220, "Changed", "UTC", true, Token(2))));

    Assert.Contains("empresa actual", exception.Message, StringComparison.OrdinalIgnoreCase);
    Assert.Equal("Other site", (await db.Sites.SingleAsync(item => item.SiteId == 220)).DisplayName);
  }

  [Fact]
  public async Task UpdateSite_RejectsAStaleRowVersion()
  {
    await using var db = CreateContext();
    await SeedCompaniesAsync(db);
    db.Sites.Add(Site(companyId: 11, siteId: 110, rowVersionSeed: 3));
    await db.SaveChangesAsync();
    db.ChangeTracker.Clear();
    var service = CreateService(db, "AAA010101AAA");

    await Assert.ThrowsAsync<PlatformAdministrationConcurrencyException>(() =>
      service.UpdateSiteAsync(new(110, "Changed", "UTC", true, Token(9))));
  }

  [Fact]
  public async Task CreatePublicSite_RejectsAMissingCapabilityEvenWhenOtherLinksExist()
  {
    await using var db = CreateContext();
    await SeedCompaniesAsync(db);
    db.Sites.Add(Site(companyId: 11, siteId: 110, rowVersionSeed: 3));
    db.Modules.Add(Module(PlatformModuleCodes.Hospitality));
    db.CompanyModules.Add(new PlatformCompanyModuleEntity
    {
      CompanyId = 11,
      ModuleCode = PlatformModuleCodes.Hospitality,
      Status = PlatformCompanyModuleStatuses.Enabled,
      ConfigurationVersion = 1,
      RowVersion = Bytes(4)
    });
    await db.SaveChangesAsync();
    db.ChangeTracker.Clear();
    var service = CreateService(db, "AAA010101AAA");

    var exception = await Assert.ThrowsAsync<PlatformAdministrationValidationException>(() =>
      service.CreatePublicSiteAsync(new(
        "empresa-a-hospedaje",
        110,
        PlatformModuleCodes.Hospitality,
        "reservaciones.example.com",
        true)));

    Assert.Contains("capacidad", exception.Message, StringComparison.OrdinalIgnoreCase);
    Assert.Empty(await db.PublicSites.ToListAsync());
  }

  [Fact]
  public async Task CreatePublicSite_EnforcesKeysAndHostsAcrossCompanies()
  {
    await using var db = CreateContext();
    await SeedCompaniesAsync(db);
    db.Sites.Add(Site(companyId: 11, siteId: 110, rowVersionSeed: 3));
    db.Modules.Add(Module(PlatformModuleCodes.Hospitality));
    db.CompanyModules.Add(new PlatformCompanyModuleEntity
    {
      CompanyId = 11,
      ModuleCode = PlatformModuleCodes.Hospitality,
      Status = PlatformCompanyModuleStatuses.Enabled,
      ConfigurationVersion = 1,
      RowVersion = Bytes(4)
    });
    db.SiteCapabilities.Add(new PlatformSiteCapabilityEntity
    {
      CompanyId = 11,
      SiteId = 110,
      ModuleCode = PlatformModuleCodes.Hospitality,
      IsEnabled = true,
      RowVersion = Bytes(5)
    });
    db.PublicSites.Add(new PlatformPublicSiteEntity
    {
      PublicSiteId = 220,
      PublicSiteKey = "already-global",
      CompanyId = 22,
      SiteId = 220,
      ModuleCode = PlatformModuleCodes.Hospitality,
      CanonicalHost = "already.example.com",
      ConfigurationVersion = 1,
      BrandingVersion = 1,
      ContentVersion = 1,
      RowVersion = Bytes(6)
    });
    await db.SaveChangesAsync();
    db.ChangeTracker.Clear();
    var service = CreateService(db, "AAA010101AAA");

    var exception = await Assert.ThrowsAsync<PlatformAdministrationValidationException>(() =>
      service.CreatePublicSiteAsync(new(
        "already-global",
        110,
        PlatformModuleCodes.Hospitality,
        "new.example.com",
        false)));

    Assert.Contains("registrad", exception.Message, StringComparison.OrdinalIgnoreCase);
    Assert.Single(await db.PublicSites.ToListAsync());
  }

  [Fact]
  public async Task UpdatePublicSite_RejectsDecreasingPresentationVersions()
  {
    await using var db = CreateContext();
    await SeedCompaniesAsync(db);
    db.Sites.Add(Site(companyId: 11, siteId: 110, rowVersionSeed: 3));
    db.Modules.Add(Module(PlatformModuleCodes.Hospitality));
    db.CompanyModules.Add(new PlatformCompanyModuleEntity
    {
      CompanyId = 11,
      ModuleCode = PlatformModuleCodes.Hospitality,
      Status = PlatformCompanyModuleStatuses.Enabled,
      ConfigurationVersion = 1,
      RowVersion = Bytes(4)
    });
    db.SiteCapabilities.Add(new PlatformSiteCapabilityEntity
    {
      CompanyId = 11,
      SiteId = 110,
      ModuleCode = PlatformModuleCodes.Hospitality,
      IsEnabled = true,
      RowVersion = Bytes(5)
    });
    db.PublicSites.Add(new PlatformPublicSiteEntity
    {
      PublicSiteId = 111,
      PublicSiteKey = "empresa-a-hospedaje",
      CompanyId = 11,
      SiteId = 110,
      ModuleCode = PlatformModuleCodes.Hospitality,
      CanonicalHost = "reservaciones.example.com",
      ConfigurationVersion = 2,
      BrandingVersion = 3,
      ContentVersion = 4,
      RowVersion = Bytes(6)
    });
    await db.SaveChangesAsync();
    db.ChangeTracker.Clear();
    var service = CreateService(db, "AAA010101AAA");

    var exception = await Assert.ThrowsAsync<PlatformAdministrationValidationException>(() =>
      service.UpdatePublicSiteAsync(new(
        111,
        "reservaciones.example.com",
        false,
        2,
        4,
        Token(6))));

    Assert.Contains("no pueden disminuir", exception.Message, StringComparison.OrdinalIgnoreCase);
    var publicSite = await db.PublicSites.SingleAsync(item => item.PublicSiteId == 111);
    Assert.Equal(3, publicSite.BrandingVersion);
    Assert.Equal(4, publicSite.ContentVersion);
  }

  [Fact]
  public async Task UpdatePublicSite_PreparesThePreviousPresentationPairForRollback()
  {
    await using var db = CreateContext();
    await SeedCompaniesAsync(db);
    await SeedPublicSiteAsync(db, brandingVersion: 1, contentVersion: 1);
    var service = CreateService(db, "AAA010101AAA");

    await service.UpdatePublicSiteAsync(new(
      111,
      "reservaciones.example.com",
      false,
      2,
      3,
      Token(6)));

    var publicSite = await db.PublicSites.AsNoTracking().SingleAsync(item => item.PublicSiteId == 111);
    Assert.Equal(2, publicSite.BrandingVersion);
    Assert.Equal(3, publicSite.ContentVersion);
    Assert.Equal(1, publicSite.FallbackBrandingVersion);
    Assert.Equal(1, publicSite.FallbackContentVersion);
    Assert.Equal(Now.Add(PlatformAdministrationService.PresentationRollbackWindow), publicSite.FallbackUntilUtc);
    Assert.Equal(3, publicSite.ConfigurationVersion);
  }

  [Fact]
  public async Task RollbackPublicSitePresentation_RestoresTheWholePairWithinTheWindow()
  {
    await using var db = CreateContext();
    await SeedCompaniesAsync(db);
    await SeedPublicSiteAsync(db, brandingVersion: 1, contentVersion: 1);
    var service = CreateService(db, "AAA010101AAA");
    await service.UpdatePublicSiteAsync(new(
      111,
      "reservaciones.example.com",
      false,
      2,
      3,
      Token(6)));

    await service.RollbackPublicSitePresentationAsync(111, Token(6));

    var publicSite = await db.PublicSites.AsNoTracking().SingleAsync(item => item.PublicSiteId == 111);
    Assert.Equal(1, publicSite.BrandingVersion);
    Assert.Equal(1, publicSite.ContentVersion);
    Assert.Null(publicSite.FallbackBrandingVersion);
    Assert.Null(publicSite.FallbackContentVersion);
    Assert.Null(publicSite.FallbackUntilUtc);
    Assert.Equal(4, publicSite.ConfigurationVersion);
  }

  [Fact]
  public async Task RollbackPublicSitePresentation_RejectsAnExpiredWindow()
  {
    await using var db = CreateContext();
    await SeedCompaniesAsync(db);
    await SeedPublicSiteAsync(
      db,
      brandingVersion: 2,
      contentVersion: 3,
      fallbackBrandingVersion: 1,
      fallbackContentVersion: 1,
      fallbackUntilUtc: Now);
    var service = CreateService(db, "AAA010101AAA");

    var exception = await Assert.ThrowsAsync<PlatformAdministrationValidationException>(() =>
      service.RollbackPublicSitePresentationAsync(111, Token(6)));

    Assert.Contains("venció", exception.Message, StringComparison.OrdinalIgnoreCase);
    var publicSite = await db.PublicSites.AsNoTracking().SingleAsync(item => item.PublicSiteId == 111);
    Assert.Equal(2, publicSite.BrandingVersion);
    Assert.Equal(3, publicSite.ContentVersion);
  }

  [Fact]
  public async Task FinalizePublicSitePresentation_ClearsFallbackWithoutChangingActivePair()
  {
    await using var db = CreateContext();
    await SeedCompaniesAsync(db);
    await SeedPublicSiteAsync(
      db,
      brandingVersion: 2,
      contentVersion: 3,
      fallbackBrandingVersion: 1,
      fallbackContentVersion: 1,
      fallbackUntilUtc: Now.AddMinutes(20));
    var service = CreateService(db, "AAA010101AAA");

    await service.FinalizePublicSitePresentationAsync(111, Token(6));

    var publicSite = await db.PublicSites.AsNoTracking().SingleAsync(item => item.PublicSiteId == 111);
    Assert.Equal(2, publicSite.BrandingVersion);
    Assert.Equal(3, publicSite.ContentVersion);
    Assert.Null(publicSite.FallbackUntilUtc);
    Assert.Equal(3, publicSite.ConfigurationVersion);
  }

  [Fact]
  public async Task DeleteSite_RequiresDeactivationBeforeDeletion()
  {
    await using var db = CreateContext();
    await SeedCompaniesAsync(db);
    db.Sites.Add(Site(companyId: 11, siteId: 110, rowVersionSeed: 3));
    await db.SaveChangesAsync();
    db.ChangeTracker.Clear();
    var service = CreateService(db, "AAA010101AAA");

    var exception = await Assert.ThrowsAsync<PlatformAdministrationValidationException>(() =>
      service.DeleteSiteAsync(110, Token(3)));

    Assert.Contains("Desactiva", exception.Message, StringComparison.OrdinalIgnoreCase);
    Assert.True(await db.Sites.AnyAsync(item => item.SiteId == 110));
  }

  [Fact]
  public void WriteCommands_DoNotAcceptCompanyIdOrRfc()
  {
    var commandTypes = new[]
    {
      typeof(UpdatePlatformCompanyMetadataCommand),
      typeof(CreatePlatformSiteCommand),
      typeof(UpdatePlatformSiteCommand),
      typeof(SetPlatformCompanyModuleCommand),
      typeof(SetPlatformSiteCapabilityCommand),
      typeof(CreatePlatformPublicSiteCommand),
      typeof(UpdatePlatformPublicSiteCommand)
    };

    foreach (var commandType in commandTypes)
    {
      var parameterNames = commandType.GetConstructors().Single().GetParameters()
        .Select(parameter => parameter.Name)
        .ToArray();
      Assert.DoesNotContain(parameterNames, name =>
        string.Equals(name, "companyId", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "rfc", StringComparison.OrdinalIgnoreCase));
    }
  }

  [Fact]
  public void PublicSiteKeysAndCanonicalHosts_AreGloballyUniqueInTheEfModel()
  {
    using var db = CreateContext();
    var entity = db.Model.FindEntityType(typeof(PlatformPublicSiteEntity));

    Assert.NotNull(entity);
    Assert.Contains(entity!.GetIndexes(), index => index.IsUnique
      && index.Properties.Select(property => property.Name)
        .SequenceEqual([nameof(PlatformPublicSiteEntity.PublicSiteKey)]));
    Assert.Contains(entity.GetIndexes(), index => index.IsUnique
      && index.Properties.Select(property => property.Name)
        .SequenceEqual([nameof(PlatformPublicSiteEntity.CanonicalHost)]));
  }

  private static PlatformDbContext CreateContext()
  {
    var options = new DbContextOptionsBuilder<PlatformDbContext>()
      .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
      .Options;
    return new PlatformDbContext(options);
  }

  private static PlatformAdministrationService CreateService(PlatformDbContext db, string rfc)
    => new(db, new FixedScopeAccessor(rfc), new FixedTimeProvider());

  private static async Task SeedCompaniesAsync(PlatformDbContext db)
  {
    db.Companies.AddRange(
      Company(11, "AAA010101AAA", 1),
      Company(22, "BBB010101BBB", 2));
    await db.SaveChangesAsync();
    db.ChangeTracker.Clear();
  }

  private static async Task SeedPublicSiteAsync(
    PlatformDbContext db,
    long brandingVersion,
    long contentVersion,
    long? fallbackBrandingVersion = null,
    long? fallbackContentVersion = null,
    DateTime? fallbackUntilUtc = null)
  {
    db.Sites.Add(Site(companyId: 11, siteId: 110, rowVersionSeed: 3));
    db.Modules.Add(Module(PlatformModuleCodes.Hospitality));
    db.CompanyModules.Add(new PlatformCompanyModuleEntity
    {
      CompanyId = 11,
      ModuleCode = PlatformModuleCodes.Hospitality,
      Status = PlatformCompanyModuleStatuses.Enabled,
      ConfigurationVersion = 1,
      RowVersion = Bytes(4)
    });
    db.SiteCapabilities.Add(new PlatformSiteCapabilityEntity
    {
      CompanyId = 11,
      SiteId = 110,
      ModuleCode = PlatformModuleCodes.Hospitality,
      IsEnabled = true,
      RowVersion = Bytes(5)
    });
    db.PublicSites.Add(new PlatformPublicSiteEntity
    {
      PublicSiteId = 111,
      PublicSiteKey = "empresa-a-hospedaje",
      CompanyId = 11,
      SiteId = 110,
      ModuleCode = PlatformModuleCodes.Hospitality,
      CanonicalHost = "reservaciones.example.com",
      IsActive = false,
      ConfigurationVersion = 2,
      BrandingVersion = brandingVersion,
      ContentVersion = contentVersion,
      FallbackBrandingVersion = fallbackBrandingVersion,
      FallbackContentVersion = fallbackContentVersion,
      FallbackUntilUtc = fallbackUntilUtc,
      CreatedAtUtc = Now,
      UpdatedAtUtc = Now,
      RowVersion = Bytes(6)
    });
    await db.SaveChangesAsync();
    db.ChangeTracker.Clear();
  }

  private static PlatformCompanyEntity Company(long companyId, string rfc, byte rowVersionSeed)
    => new()
    {
      CompanyId = companyId,
      Rfc = rfc,
      DisplayName = rfc,
      IsActive = true,
      BrandingVersion = 1,
      CreatedAtUtc = Now,
      UpdatedAtUtc = Now,
      RowVersion = Bytes(rowVersionSeed)
    };

  private static PlatformSiteEntity Site(long companyId, long siteId, byte rowVersionSeed)
    => new()
    {
      SiteId = siteId,
      CompanyId = companyId,
      SiteKey = $"site-{siteId}",
      DisplayName = $"Site {siteId}",
      TimeZoneId = "UTC",
      IsActive = true,
      CreatedAtUtc = Now,
      UpdatedAtUtc = Now,
      RowVersion = Bytes(rowVersionSeed)
    };

  private static PlatformModuleEntity Module(string moduleCode)
    => new()
    {
      ModuleCode = moduleCode,
      DisplayName = moduleCode,
      RequiresSite = true,
      IsActive = true,
      CreatedAtUtc = Now,
      UpdatedAtUtc = Now,
      RowVersion = Bytes(5)
    };

  private static byte[] Bytes(byte seed)
    => Enumerable.Repeat(seed, 8).ToArray();

  private static string Token(byte seed)
    => Convert.ToBase64String(Bytes(seed));

  private sealed class FixedScopeAccessor(string rfc) : IPlatformAdministrationScopeAccessor
  {
    public Task<PlatformAdministrationScope> GetRequiredScopeAsync(CancellationToken ct = default)
      => Task.FromResult(new PlatformAdministrationScope("admin-17", rfc));
  }

  private sealed class FixedTimeProvider : TimeProvider
  {
    public override DateTimeOffset GetUtcNow()
      => new(Now);
  }
}
