using OrionERP.Bonhomia.Web.Features.Bonhomia;

namespace OrionERP.UnitTests.Bonhomia;

public class BonhomiaSiteContentTests
{
  [Fact]
  public void LegalIdentity_UsesVerifiedOrionHabitatDetails()
  {
    Assert.Equal("2026-05-31", BonhomiaSiteContent.LegalVersion);
    Assert.Equal("Orion Habitat de Mexico, S.A. de C.V.", BonhomiaSiteContent.LegalManagerName);
    Assert.Equal("OHM191112Q26", BonhomiaSiteContent.LegalRfc);
    Assert.Equal("info@orion.land", BonhomiaSiteContent.LegalArcoEmail);
    Assert.Contains("Lazaro Cardenas 105", BonhomiaSiteContent.LegalFiscalAddress, StringComparison.Ordinal);
  }

  [Fact]
  public void LegalIdentity_DoesNotExposePendingPlaceholderText()
  {
    var legalValues = new[]
    {
      BonhomiaSiteContent.LegalVersion,
      BonhomiaSiteContent.LegalManagerName,
      BonhomiaSiteContent.LegalRfc,
      BonhomiaSiteContent.LegalArcoEmail,
      BonhomiaSiteContent.LegalFiscalAddress,
      BonhomiaSiteContent.LegalResponsibleSummary
    };

    Assert.All(
      legalValues,
      value =>
      {
        Assert.DoesNotContain("Pendiente", value, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("placeholder", value, StringComparison.OrdinalIgnoreCase);
      });
  }
}
