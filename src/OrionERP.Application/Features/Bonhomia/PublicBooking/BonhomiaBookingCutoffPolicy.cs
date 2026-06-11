namespace OrionERP.Application.Features.Bonhomia.PublicBooking;

public static class BonhomiaBookingCutoffPolicy
{
  public const string DefaultTimeZone = "America/Mexico_City";
  public static readonly TimeOnly SameDayCutoff = new(17, 0);

  public static DateOnly GetEarliestCheckInDate(DateTimeOffset nowUtc, string? timeZoneId = null)
  {
    var localNow = GetLocalNow(nowUtc, timeZoneId);
    var localToday = DateOnly.FromDateTime(localNow.Date);
    var localTime = TimeOnly.FromDateTime(localNow);

    return localTime >= SameDayCutoff
      ? localToday.AddDays(1)
      : localToday;
  }

  public static void EnsureCheckInIsAllowed(DateOnly checkIn, DateTimeOffset nowUtc, string? timeZoneId = null)
  {
    var earliestCheckIn = GetEarliestCheckInDate(nowUtc, timeZoneId);
    if (checkIn >= earliestCheckIn)
    {
      return;
    }

    throw new BonhomiaPublicBookingException(
      "same_day_cutoff",
      "Las reservaciones para llegada el mismo dia solo estan disponibles antes de las 17:00 hrs. Selecciona una fecha posterior.");
  }

  private static DateTime GetLocalNow(DateTimeOffset nowUtc, string? timeZoneId)
  {
    var timeZone = ResolveTimeZoneInfo(timeZoneId);
    return TimeZoneInfo.ConvertTime(nowUtc, timeZone).DateTime;
  }

  private static TimeZoneInfo ResolveTimeZoneInfo(string? timeZoneId)
  {
    var candidates = GetTimeZoneCandidates(timeZoneId);
    foreach (var candidate in candidates)
    {
      try
      {
        return TimeZoneInfo.FindSystemTimeZoneById(candidate);
      }
      catch (TimeZoneNotFoundException)
      {
      }
      catch (InvalidTimeZoneException)
      {
      }
    }

    return TimeZoneInfo.Local;
  }

  private static IReadOnlyList<string> GetTimeZoneCandidates(string? timeZoneId)
  {
    var configured = string.IsNullOrWhiteSpace(timeZoneId)
      ? DefaultTimeZone
      : timeZoneId.Trim();

    return configured switch
    {
      "America/Mexico_City" => ["America/Mexico_City", "Central Standard Time (Mexico)"],
      "Central Standard Time (Mexico)" => ["Central Standard Time (Mexico)", "America/Mexico_City"],
      _ => [configured]
    };
  }
}
