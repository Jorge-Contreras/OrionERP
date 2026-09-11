using OrionERP.Application.Features.Hospitality.PublicBooking;

namespace OrionERP.UnitTests.Bonhomia;

public class HospitalityBookingCutoffPolicyTests
{
  [Fact]
  public void GetEarliestCheckInDate_BeforeCutoff_AllowsSameDay()
  {
    var nowUtc = new DateTimeOffset(2026, 6, 10, 16, 59, 0, TimeSpan.FromHours(-6))
      .ToUniversalTime();

    var result = HospitalityBookingCutoffPolicy.GetEarliestCheckInDate(nowUtc);

    Assert.Equal(new DateOnly(2026, 6, 10), result);
  }

  [Fact]
  public void GetEarliestCheckInDate_AtCutoff_ReturnsNextDay()
  {
    var nowUtc = new DateTimeOffset(2026, 6, 10, 17, 0, 0, TimeSpan.FromHours(-6))
      .ToUniversalTime();

    var result = HospitalityBookingCutoffPolicy.GetEarliestCheckInDate(nowUtc);

    Assert.Equal(new DateOnly(2026, 6, 11), result);
  }

  [Fact]
  public void EnsureCheckInIsAllowed_AfterCutoff_RejectsSameDay()
  {
    var nowUtc = new DateTimeOffset(2026, 6, 10, 17, 1, 0, TimeSpan.FromHours(-6))
      .ToUniversalTime();

    var exception = Assert.Throws<HospitalityPublicBookingException>(() =>
      HospitalityBookingCutoffPolicy.EnsureCheckInIsAllowed(new DateOnly(2026, 6, 10), nowUtc));

    Assert.Equal("same_day_cutoff", exception.ErrorCode);
  }
}
