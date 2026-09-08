using OrionERP.Application.Features.Platform;

namespace OrionERP.UnitTests.Platform;

public sealed class PublicSiteResolutionPolicyTests
{
  private static readonly DateTime Now = new(2026, 9, 1, 18, 0, 0, DateTimeKind.Utc);

  [Fact]
  public void Resolve_ReturnsOnlyTheExactEnabledBinding()
  {
    var binding = PublicSiteResolutionPolicy.Resolve(Request(), Candidate(), Now);

    Assert.Equal(17, binding.CompanyId);
    Assert.Equal(91, binding.SiteId);
    Assert.Equal("bonhomia-suites", binding.PublicSiteKey);
    Assert.Equal("OHM191112Q26", binding.CompanyRfc);
    Assert.Equal(PlatformModuleCodes.Hospitality, binding.ModuleCode);
    Assert.Equal(7, binding.CompanyModuleConfigurationVersion);
    Assert.Equal("bonhomiasuites.com", binding.CanonicalHost);
  }

  [Theory]
  [InlineData(PlatformCompanyModuleStatuses.Provisioning)]
  [InlineData(PlatformCompanyModuleStatuses.Suspended)]
  [InlineData("enabled")]
  [InlineData("Unknown")]
  public void Resolve_RejectsEveryCompanyModuleStatusExceptExactEnabled(string status)
  {
    var exception = Assert.Throws<PublicSiteResolutionException>(() =>
      PublicSiteResolutionPolicy.Resolve(Request(), Candidate() with { CompanyModuleStatus = status }, Now));

    Assert.Equal(PublicSiteResolutionFailure.CompanyModuleInactive, exception.Failure);
  }

  [Fact]
  public void Resolve_RejectsAnExpiredEntitlement()
  {
    var exception = Assert.Throws<PublicSiteResolutionException>(() =>
      PublicSiteResolutionPolicy.Resolve(
        Request(),
        Candidate() with { CompanyModuleEffectiveToUtc = Now },
        Now));

    Assert.Equal(PublicSiteResolutionFailure.CompanyModuleInactive, exception.Failure);
  }

  [Fact]
  public void Resolve_RejectsAccountingAsAPublicWebsite()
  {
    var candidate = Candidate() with
    {
      ModuleCode = PlatformModuleCodes.AccountingCore,
      ModuleRequiresSite = false
    };
    var request = Request() with { ExpectedModuleCode = PlatformModuleCodes.AccountingCore };

    var exception = Assert.Throws<PublicSiteResolutionException>(() =>
      PublicSiteResolutionPolicy.Resolve(request, candidate, Now));

    Assert.Equal(PublicSiteResolutionFailure.ModuleNotPublic, exception.Failure);
  }

  [Theory]
  [InlineData("OTHER010101AAA", "bonhomia", "HOSPITALITY", "bonhomiasuites.com", PublicSiteResolutionFailure.CompanyMismatch)]
  [InlineData("OHM191112Q26", "los-patos", "HOSPITALITY", "bonhomiasuites.com", PublicSiteResolutionFailure.SiteMismatch)]
  [InlineData("OHM191112Q26", "bonhomia", "RESTAURANT", "bonhomiasuites.com", PublicSiteResolutionFailure.ModuleMismatch)]
  [InlineData("OHM191112Q26", "bonhomia", "HOSPITALITY", "lospatos.example", PublicSiteResolutionFailure.CanonicalHostMismatch)]
  public void Resolve_RejectsAnyConfiguredBindingMismatch(
    string rfc,
    string siteKey,
    string moduleCode,
    string host,
    PublicSiteResolutionFailure expectedFailure)
  {
    var request = Request() with
    {
      ExpectedCompanyRfc = rfc,
      ExpectedSiteKey = siteKey,
      ExpectedModuleCode = moduleCode,
      ExpectedCanonicalHost = host
    };

    var exception = Assert.Throws<PublicSiteResolutionException>(() =>
      PublicSiteResolutionPolicy.Resolve(request, Candidate(), Now));

    Assert.Equal(expectedFailure, exception.Failure);
  }

  [Theory]
  [InlineData("https://bonhomiasuites.com")]
  [InlineData("bonhomiasuites.com:443")]
  [InlineData("*.bonhomiasuites.com")]
  [InlineData("localhost")]
  public void Resolve_RejectsANonCanonicalHostExpectation(string host)
  {
    var exception = Assert.Throws<PublicSiteResolutionException>(() =>
      PublicSiteResolutionPolicy.Resolve(Request() with { ExpectedCanonicalHost = host }, Candidate(), Now));

    Assert.Equal(PublicSiteResolutionFailure.InvalidExpectation, exception.Failure);
  }

  [Fact]
  public void Resolve_RejectsAPartialPresentationFallback()
  {
    var exception = Assert.Throws<PublicSiteResolutionException>(() =>
      PublicSiteResolutionPolicy.Resolve(
        Request(),
        Candidate() with { FallbackBrandingVersion = 12 },
        Now));

    Assert.Equal(PublicSiteResolutionFailure.InvalidRegistration, exception.Failure);
  }

  [Fact]
  public void Resolve_ProjectsTheCompletePresentationFallbackAsUtc()
  {
    var expiry = DateTime.SpecifyKind(Now.AddMinutes(30), DateTimeKind.Unspecified);
    var binding = PublicSiteResolutionPolicy.Resolve(
      Request(),
      Candidate() with
      {
        FallbackBrandingVersion = 12,
        FallbackContentVersion = 16,
        FallbackUntilUtc = expiry
      },
      Now);

    Assert.Equal(12, binding.FallbackBrandingVersion);
    Assert.Equal(16, binding.FallbackContentVersion);
    Assert.Equal(DateTimeKind.Utc, binding.FallbackUntilUtc!.Value.Kind);
  }

  private static PublicSiteResolutionRequest Request()
    => new(
      "bonhomia-suites",
      "OHM191112Q26",
      "bonhomia",
      PlatformModuleCodes.Hospitality,
      "bonhomiasuites.com");

  private static PublicSiteResolutionCandidate Candidate()
    => new(
      PublicSiteId: 3,
      PublicSiteKey: "bonhomia-suites",
      CompanyId: 17,
      CompanyRfc: "OHM191112Q26",
      TaxRfc: "OHM191112Q26",
      LegacyTenantKey: null,
      CompanyIsActive: true,
      SiteId: 91,
      SiteKey: "bonhomia",
      SiteDisplayName: "Bonhomía Suites",
      TimeZoneId: "Central Standard Time (Mexico)",
      SiteIsActive: true,
      ModuleCode: PlatformModuleCodes.Hospitality,
      ModuleIsActive: true,
      ModuleRequiresSite: true,
      CompanyModuleExists: true,
      CompanyModuleStatus: PlatformCompanyModuleStatuses.Enabled,
      CompanyModuleEffectiveFromUtc: Now.AddDays(-1),
      CompanyModuleEffectiveToUtc: Now.AddDays(1),
      CompanyModuleConfigurationVersion: 7,
      SiteCapabilityExists: true,
      SiteCapabilityIsEnabled: true,
      CanonicalHost: "bonhomiasuites.com",
      PublicSiteIsActive: true,
      ConfigurationVersion: 11,
      BrandingVersion: 13,
      ContentVersion: 17);
}
