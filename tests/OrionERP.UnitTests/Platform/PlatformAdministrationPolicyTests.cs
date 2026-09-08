using OrionERP.Application.Features.Platform;

namespace OrionERP.UnitTests.Platform;

public sealed class PlatformAdministrationPolicyTests
{
  [Theory]
  [InlineData("  Los-Patos  ", "los-patos")]
  [InlineData("sede-01", "sede-01")]
  public void NormalizeSiteKey_ReturnsAStableSlug(string value, string expected)
    => Assert.Equal(expected, PlatformAdministrationPolicy.NormalizeSiteKey(value));

  [Theory]
  [InlineData("-sede")]
  [InlineData("sede_")]
  [InlineData("área")]
  public void NormalizeSiteKey_RejectsUnsupportedKeys(string value)
    => Assert.Throws<PlatformAdministrationValidationException>(
      () => PlatformAdministrationPolicy.NormalizeSiteKey(value));

  [Fact]
  public void NormalizeCanonicalHost_ReturnsLowercaseDnsOnly()
    => Assert.Equal(
      "reservaciones.example.com",
      PlatformAdministrationPolicy.NormalizeCanonicalHost(" Reservaciones.Example.com. "));

  [Theory]
  [InlineData("https://example.com")]
  [InlineData("example.com:443")]
  [InlineData("*.example.com")]
  [InlineData("localhost")]
  public void NormalizeCanonicalHost_RejectsUrlsPortsWildcardsAndLocalNames(string value)
    => Assert.Throws<PlatformAdministrationValidationException>(
      () => PlatformAdministrationPolicy.NormalizeCanonicalHost(value));

  [Theory]
  [InlineData("OHM191112Q26", "OHM191112Q26")]
  [InlineData(" abc010101ab1 ", "ABC010101AB1")]
  public void NormalizeTaxRfc_NormalizesValidIdentifiers(string value, string expected)
    => Assert.Equal(expected, PlatformAdministrationPolicy.NormalizeTaxRfc(value));

  [Fact]
  public void NormalizeEffectiveRange_RejectsAnInvertedRange()
    => Assert.Throws<PlatformAdministrationValidationException>(() =>
      PlatformAdministrationPolicy.NormalizeEffectiveRange(
        new DateTime(2026, 10, 2),
        new DateTime(2026, 10, 1)));

  [Theory]
  [InlineData(PlatformCompanyModuleStatuses.Provisioning)]
  [InlineData(PlatformCompanyModuleStatuses.Suspended)]
  public void PublicActivationChain_RequiresAnEffectiveModule(string status)
  {
    var result = PlatformAdministrationPolicy.IsCompletePublicActivationChain(
      companyIsActive: true,
      siteIsActive: true,
      moduleIsActive: true,
      moduleRequiresSite: true,
      PlatformModuleCodes.Hospitality,
      status,
      effectiveFromUtc: null,
      effectiveToUtc: null,
      capabilityIsEnabled: true,
      DateTime.UtcNow);

    Assert.False(result);
  }

  [Fact]
  public void PublicActivationChain_RequiresEveryLink()
  {
    var now = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

    Assert.True(PlatformAdministrationPolicy.IsCompletePublicActivationChain(
      true, true, true, true, PlatformModuleCodes.Restaurant,
      PlatformCompanyModuleStatuses.Enabled, now.AddDays(-1), now.AddDays(1), true, now));
    Assert.False(PlatformAdministrationPolicy.IsCompletePublicActivationChain(
      true, true, true, true, PlatformModuleCodes.Restaurant,
      PlatformCompanyModuleStatuses.Enabled, now.AddDays(-1), now.AddDays(1), false, now));
  }

  [Fact]
  public void DecodeConcurrencyToken_RequiresASqlRowVersion()
  {
    Assert.Equal(8, PlatformAdministrationPolicy.DecodeConcurrencyToken(
      Convert.ToBase64String(new byte[8])).Length);
    Assert.Throws<PlatformAdministrationConcurrencyException>(
      () => PlatformAdministrationPolicy.DecodeConcurrencyToken("not-a-rowversion"));
  }

  [Theory]
  [InlineData(0)]
  [InlineData(-1)]
  public void NormalizePublicSiteVersion_RejectsNonPositiveValues(long value)
    => Assert.Throws<PlatformAdministrationValidationException>(() =>
      PlatformAdministrationPolicy.NormalizePublicSiteVersion(value, "marca"));
}
