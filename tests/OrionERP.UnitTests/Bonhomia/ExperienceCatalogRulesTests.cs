using OrionERP.Application.Features.Reservaciones.Experiencias;

namespace OrionERP.UnitTests.Bonhomia;

public class ExperienceCatalogRulesTests
{
  [Fact]
  public void IsAvailableForStay_ReturnsFalseWhenSeasonStartsOnCheckout()
  {
    var experience = CreateLuciernagasExperience();

    var isAvailable = ExperienceCatalogRules.IsAvailableForStay(
      experience,
      new DateOnly(2026, 6, 10),
      new DateOnly(2026, 6, 15));

    Assert.False(isAvailable);
  }

  [Fact]
  public void ResolveDefaultDate_UsesFirstOverlappingSeasonDate()
  {
    var experience = CreateLuciernagasExperience();

    var isAvailable = ExperienceCatalogRules.IsAvailableForStay(
      experience,
      new DateOnly(2026, 6, 14),
      new DateOnly(2026, 6, 16));
    var defaultDate = ExperienceCatalogRules.ResolveDefaultDate(
      experience,
      new DateOnly(2026, 6, 14),
      new DateOnly(2026, 6, 16));

    Assert.True(isAvailable);
    Assert.Equal(new DateOnly(2026, 6, 15), defaultDate);
  }

  [Fact]
  public void GetParticipantLimit_UsesExperienceMaximumWhenLowerThanSuiteCapacity()
  {
    var experience = CreateLuciernagasExperience();
    experience.MaximumParticipants = 1;

    var limit = ExperienceCatalogRules.GetParticipantLimit(experience, suiteCapacity: 2);

    Assert.Equal(1, limit);
  }

  private static ExperienceCatalogItemDto CreateLuciernagasExperience()
    => new()
    {
      Code = "luciernagas-calpulalpan",
      Name = "Avistamiento de Luciernagas en Calpulalpan",
      SeasonStart = new DateOnly(2026, 6, 15),
      SeasonEnd = new DateOnly(2026, 8, 15),
      MinimumParticipants = 1,
      IsPublic = true,
      IsActive = true
    };
}
