namespace OrionERP.Application.Features.CapitalHumano.Workforce;

public sealed record GeofenceEvaluation(
  string Status,
  decimal? DistanceMeters,
  bool RequiresReview,
  string Detail);

public static class GeofenceEvaluator
{
  private const double EarthRadiusMeters = 6_371_000d;

  public static GeofenceEvaluation Evaluate(WorkSiteDto site, LocationEvidenceDto? evidence, bool locationRequired)
  {
    if (evidence?.Latitude is null || evidence.Longitude is null)
    {
      return locationRequired
        ? new("UNAVAILABLE", null, true, "No se pudo obtener la ubicacion al registrar la asistencia.")
        : new("NOT_REQUIRED", null, false, "La politica no requiere ubicacion.");
    }

    if (evidence.Latitude is < -90 or > 90 || evidence.Longitude is < -180 or > 180)
    {
      return new("INVALID", null, true, "Las coordenadas recibidas no son validas.");
    }

    var distance = HaversineMeters(
      (double)site.Latitude,
      (double)site.Longitude,
      (double)evidence.Latitude.Value,
      (double)evidence.Longitude.Value);
    var roundedDistance = Math.Round((decimal)distance, 1);

    if (evidence.AccuracyMeters is null || evidence.AccuracyMeters > site.MaxAccuracyMeters)
    {
      return new("INACCURATE", roundedDistance, true, "La precision de la ubicacion no cumple la politica del sitio.");
    }

    if (distance > site.RadiusMeters)
    {
      return new("OUTSIDE", roundedDistance, true, "El registro se realizo fuera del perimetro del sitio.");
    }

    return new("INSIDE", roundedDistance, false, "Ubicacion validada dentro del sitio.");
  }

  private static double HaversineMeters(double lat1, double lon1, double lat2, double lon2)
  {
    static double Radians(double degrees) => degrees * Math.PI / 180d;

    var dLat = Radians(lat2 - lat1);
    var dLon = Radians(lon2 - lon1);
    var a = Math.Pow(Math.Sin(dLat / 2d), 2d)
      + Math.Cos(Radians(lat1)) * Math.Cos(Radians(lat2)) * Math.Pow(Math.Sin(dLon / 2d), 2d);
    return EarthRadiusMeters * 2d * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1d - a));
  }
}

public sealed record AttendanceCalculationInput(
  DateOnly WorkDate,
  TimeSpan? ScheduledStart,
  TimeSpan? ScheduledEnd,
  int ScheduledUnpaidBreakMinutes,
  int GraceMinutes,
  int RoundingMinutes,
  IReadOnlyList<AttendanceCalculationEvent> Events);

public sealed record AttendanceCalculationEvent(string EventType, DateTime OccurredAtUtc, DateTime LocalDateTime);

public sealed record AttendanceCalculationResult(
  int ScheduledMinutes,
  int WorkedMinutes,
  int BreakMinutes,
  int AbsenceMinutes,
  int LateMinutes,
  int EarlyDepartureMinutes,
  int OvertimeCandidateMinutes,
  bool HasUnpairedEvents,
  string Status);

public static class AttendanceCalculator
{
  public static AttendanceCalculationResult Calculate(AttendanceCalculationInput input)
  {
    var scheduledMinutes = CalculateScheduledMinutes(input.ScheduledStart, input.ScheduledEnd, input.ScheduledUnpaidBreakMinutes);
    var ordered = input.Events.OrderBy(item => item.OccurredAtUtc).ToArray();
    var workedMinutes = 0d;
    var breakMinutes = 0d;
    DateTime? workingSince = null;
    DateTime? breakSince = null;
    DateTime? firstIn = null;
    DateTime? lastOut = null;
    var invalid = false;

    foreach (var item in ordered)
    {
      switch (item.EventType.ToUpperInvariant())
      {
        case AttendanceEventTypes.In when workingSince is null && breakSince is null:
          workingSince = item.OccurredAtUtc;
          firstIn ??= item.LocalDateTime;
          break;
        case AttendanceEventTypes.BreakStart when workingSince is not null && breakSince is null:
          workedMinutes += (item.OccurredAtUtc - workingSince.Value).TotalMinutes;
          workingSince = null;
          breakSince = item.OccurredAtUtc;
          break;
        case AttendanceEventTypes.BreakEnd when breakSince is not null && workingSince is null:
          breakMinutes += (item.OccurredAtUtc - breakSince.Value).TotalMinutes;
          breakSince = null;
          workingSince = item.OccurredAtUtc;
          break;
        case AttendanceEventTypes.Out when workingSince is not null && breakSince is null:
          workedMinutes += (item.OccurredAtUtc - workingSince.Value).TotalMinutes;
          workingSince = null;
          lastOut = item.LocalDateTime;
          break;
        default:
          invalid = true;
          break;
      }
    }

    invalid |= workingSince is not null || breakSince is not null;

    var roundedWorked = RoundMinutes(Math.Max(0, workedMinutes), input.RoundingMinutes);
    var roundedBreak = (int)Math.Round(Math.Max(0, breakMinutes), MidpointRounding.AwayFromZero);
    var lateMinutes = CalculateLateMinutes(input, firstIn);
    var earlyMinutes = CalculateEarlyMinutes(input, lastOut);
    var overtime = Math.Max(0, roundedWorked - scheduledMinutes);
    var absence = Math.Max(0, scheduledMinutes - roundedWorked);

    return new(
      scheduledMinutes,
      roundedWorked,
      roundedBreak,
      absence,
      lateMinutes,
      earlyMinutes,
      overtime,
      invalid,
      invalid ? "EXCEPTION" : ordered.Length == 0 ? "OPEN" : "READY");
  }

  private static int CalculateScheduledMinutes(TimeSpan? start, TimeSpan? end, int unpaidBreak)
  {
    if (start is null || end is null)
    {
      return 0;
    }

    var duration = end.Value - start.Value;
    if (duration <= TimeSpan.Zero)
    {
      duration += TimeSpan.FromDays(1);
    }

    return Math.Max(0, (int)Math.Round(duration.TotalMinutes) - Math.Max(0, unpaidBreak));
  }

  private static int CalculateLateMinutes(AttendanceCalculationInput input, DateTime? firstIn)
  {
    if (input.ScheduledStart is null || firstIn is null)
    {
      return 0;
    }

    var scheduled = input.WorkDate.ToDateTime(TimeOnly.FromTimeSpan(input.ScheduledStart.Value));
    return Math.Max(0, (int)Math.Floor((firstIn.Value - scheduled).TotalMinutes) - input.GraceMinutes);
  }

  private static int CalculateEarlyMinutes(AttendanceCalculationInput input, DateTime? lastOut)
  {
    if (input.ScheduledStart is null || input.ScheduledEnd is null || lastOut is null)
    {
      return 0;
    }

    var scheduled = input.WorkDate.ToDateTime(TimeOnly.FromTimeSpan(input.ScheduledEnd.Value));
    if (input.ScheduledEnd <= input.ScheduledStart)
    {
      scheduled = scheduled.AddDays(1);
    }

    return Math.Max(0, (int)Math.Floor((scheduled - lastOut.Value).TotalMinutes) - input.GraceMinutes);
  }

  private static int RoundMinutes(double minutes, int interval)
  {
    if (interval <= 1)
    {
      return (int)Math.Round(minutes, MidpointRounding.AwayFromZero);
    }

    return (int)(Math.Round(minutes / interval, MidpointRounding.AwayFromZero) * interval);
  }
}

public static class AttendanceTransitionRules
{
  public static string GetNextEventType(string? currentEventType)
    => currentEventType?.ToUpperInvariant() switch
    {
      AttendanceEventTypes.In => AttendanceEventTypes.BreakStart,
      AttendanceEventTypes.BreakStart => AttendanceEventTypes.BreakEnd,
      AttendanceEventTypes.BreakEnd => AttendanceEventTypes.Out,
      _ => AttendanceEventTypes.In
    };

  public static bool IsAllowed(string? currentEventType, string requestedEventType)
  {
    var current = currentEventType?.ToUpperInvariant();
    var requested = requestedEventType.ToUpperInvariant();
    return requested switch
    {
      AttendanceEventTypes.In => current is null or AttendanceEventTypes.Out,
      AttendanceEventTypes.BreakStart => current is AttendanceEventTypes.In or AttendanceEventTypes.BreakEnd,
      AttendanceEventTypes.BreakEnd => current is AttendanceEventTypes.BreakStart,
      AttendanceEventTypes.Out => current is AttendanceEventTypes.In or AttendanceEventTypes.BreakEnd,
      _ => false
    };
  }
}

public static class MexicoWorkweekPolicy
{
  public static (int WeeklyOrdinaryMinutes, int WeeklyDoubleOvertimeMinutes) GetForYear(int year)
    => year switch
    {
      <= 2026 => (48 * 60, 9 * 60),
      2027 => (46 * 60, 9 * 60),
      2028 => (44 * 60, 10 * 60),
      2029 => (42 * 60, 11 * 60),
      _ => (40 * 60, 12 * 60)
    };
}

public static class MexicoVacationAccrualCalculator
{
  public static int GetAnnualEntitlementDays(int completedYears)
  {
    if (completedYears < 1) return 0;
    if (completedYears <= 5) return 10 + completedYears * 2;
    return 20 + ((completedYears - 1) / 5) * 2;
  }
}
