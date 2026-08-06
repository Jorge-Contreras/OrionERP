using OrionERP.Application.Features.CapitalHumano.Workforce;

namespace OrionERP.UnitTests.CapitalHumano;

public sealed class WorkforceRulesTests
{
  private static readonly WorkSiteDto Site = new()
  {
    Latitude = 19.432608m,
    Longitude = -99.133209m,
    RadiusMeters = 150,
    MaxAccuracyMeters = 100
  };

  [Fact]
  public void Geofence_InsideBoundary_IsAccepted()
  {
    var result = GeofenceEvaluator.Evaluate(Site, new LocationEvidenceDto
    {
      Latitude = 19.432608m,
      Longitude = -99.133209m,
      AccuracyMeters = 12
    }, true);

    Assert.Equal("INSIDE", result.Status);
    Assert.False(result.RequiresReview);
  }

  [Fact]
  public void Geofence_MissingInaccurateAndOutside_AreRecordedForReview()
  {
    Assert.True(GeofenceEvaluator.Evaluate(Site, null, true).RequiresReview);
    Assert.Equal("INACCURATE", GeofenceEvaluator.Evaluate(Site, new LocationEvidenceDto
    {
      Latitude = Site.Latitude,
      Longitude = Site.Longitude,
      AccuracyMeters = 101
    }, true).Status);
    Assert.Equal("OUTSIDE", GeofenceEvaluator.Evaluate(Site, new LocationEvidenceDto
    {
      Latitude = 19.45m,
      Longitude = -99.133209m,
      AccuracyMeters = 10
    }, true).Status);
  }

  [Fact]
  public void Geofence_WhenPolicyDoesNotRequireLocation_AllowsUnavailableEvidence()
  {
    var result = GeofenceEvaluator.Evaluate(Site, null, false);
    Assert.Equal("NOT_REQUIRED", result.Status);
    Assert.False(result.RequiresReview);
  }

  [Theory]
  [InlineData(null, "IN", true)]
  [InlineData("IN", "BREAK_START", true)]
  [InlineData("BREAK_START", "BREAK_END", true)]
  [InlineData("BREAK_END", "OUT", true)]
  [InlineData("IN", "IN", false)]
  [InlineData("OUT", "BREAK_START", false)]
  public void PunchTransitions_AreDeterministic(string? current, string requested, bool expected)
    => Assert.Equal(expected, AttendanceTransitionRules.IsAllowed(current, requested));

  [Fact]
  public void AttendanceCalculator_HandlesOvernightShiftBreakGraceAndRounding()
  {
    var date = new DateOnly(2028, 7, 10);
    var result = AttendanceCalculator.Calculate(new AttendanceCalculationInput(
      date,
      new TimeSpan(22, 0, 0),
      new TimeSpan(6, 0, 0),
      30,
      5,
      5,
      [
        new("IN", new DateTime(2028,7,11,4,7,0,DateTimeKind.Utc), new DateTime(2028,7,10,22,7,0)),
        new("BREAK_START", new DateTime(2028,7,11,7,0,0,DateTimeKind.Utc), new DateTime(2028,7,11,1,0,0)),
        new("BREAK_END", new DateTime(2028,7,11,7,30,0,DateTimeKind.Utc), new DateTime(2028,7,11,1,30,0)),
        new("OUT", new DateTime(2028,7,11,12,15,0,DateTimeKind.Utc), new DateTime(2028,7,11,6,15,0))
      ]));

    Assert.Equal(450, result.ScheduledMinutes);
    Assert.Equal(460, result.WorkedMinutes);
    Assert.Equal(30, result.BreakMinutes);
    Assert.Equal(0, result.AbsenceMinutes);
    Assert.Equal(2, result.LateMinutes);
    Assert.Equal(10, result.OvertimeCandidateMinutes);
    Assert.False(result.HasUnpairedEvents);
  }

  [Fact]
  public void AttendanceCalculator_ReportsScheduledShortfallAsAbsenceMinutes()
  {
    var date = new DateOnly(2028, 7, 10);
    var result = AttendanceCalculator.Calculate(new AttendanceCalculationInput(
      date,
      new TimeSpan(9, 0, 0),
      new TimeSpan(17, 0, 0),
      60,
      0,
      1,
      [
        new("IN", new DateTime(2028,7,10,15,0,0,DateTimeKind.Utc), new DateTime(2028,7,10,9,0,0)),
        new("OUT", new DateTime(2028,7,10,19,0,0,DateTimeKind.Utc), new DateTime(2028,7,10,13,0,0))
      ]));

    Assert.Equal(420, result.ScheduledMinutes);
    Assert.Equal(240, result.WorkedMinutes);
    Assert.Equal(180, result.AbsenceMinutes);
  }

  [Fact]
  public void AttendanceCalculator_SupportsSplitShiftUsingMultipleInOutPairs()
  {
    var date = new DateOnly(2028, 7, 10);
    var result = AttendanceCalculator.Calculate(new AttendanceCalculationInput(
      date, new TimeSpan(8, 0, 0), new TimeSpan(18, 0, 0), 120, 0, 1,
      [
        new("IN", new DateTime(2028,7,10,14,0,0,DateTimeKind.Utc), new DateTime(2028,7,10,8,0,0)),
        new("OUT", new DateTime(2028,7,10,18,0,0,DateTimeKind.Utc), new DateTime(2028,7,10,12,0,0)),
        new("IN", new DateTime(2028,7,10,20,0,0,DateTimeKind.Utc), new DateTime(2028,7,10,14,0,0)),
        new("OUT", new DateTime(2028,7,11,0,0,0,DateTimeKind.Utc), new DateTime(2028,7,10,18,0,0))
      ]));

    Assert.Equal(480, result.ScheduledMinutes);
    Assert.Equal(480, result.WorkedMinutes);
    Assert.Equal(0, result.AbsenceMinutes);
    Assert.False(result.HasUnpairedEvents);
  }

  [Theory]
  [InlineData(2026, 48, 9)]
  [InlineData(2027, 46, 9)]
  [InlineData(2028, 44, 10)]
  [InlineData(2029, 42, 11)]
  [InlineData(2030, 40, 12)]
  public void MexicoTransition_IsEffectiveDated(int year, int ordinaryHours, int doubleHours)
  {
    var policy = MexicoWorkweekPolicy.GetForYear(year);
    Assert.Equal(ordinaryHours * 60, policy.WeeklyOrdinaryMinutes);
    Assert.Equal(doubleHours * 60, policy.WeeklyDoubleOvertimeMinutes);
  }

  [Theory]
  [InlineData(0, 0)]
  [InlineData(1, 12)]
  [InlineData(5, 20)]
  [InlineData(6, 22)]
  [InlineData(11, 24)]
  public void VacationEntitlement_FollowsStatutoryAnniversaries(int years, int expectedDays)
    => Assert.Equal(expectedDays, MexicoVacationAccrualCalculator.GetAnnualEntitlementDays(years));
}
