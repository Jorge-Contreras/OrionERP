namespace OrionERP.Application.Features.Hospitality.PublicBooking;

public static class HospitalityBookingCutoffPolicy
{
  public const string DefaultTimeZone = "America/Mexico_City";
  public static readonly TimeOnly DefaultSameDayCutoff = new(17, 0);

  public static DateOnly GetEarliestCheckInDate(
    DateTimeOffset nowUtc,
    string? timeZoneId = null,
    TimeOnly? sameDayCutoff = null)
  {
    var localNow = GetLocalNow(nowUtc, timeZoneId);
    var localToday = DateOnly.FromDateTime(localNow.Date);
    var localTime = TimeOnly.FromDateTime(localNow);

    return localTime >= (sameDayCutoff ?? DefaultSameDayCutoff)
      ? localToday.AddDays(1)
      : localToday;
  }

  public static void EnsureCheckInIsAllowed(
    DateOnly checkIn,
    DateTimeOffset nowUtc,
    string? timeZoneId = null,
    TimeOnly? sameDayCutoff = null)
  {
    var cutoff = sameDayCutoff ?? DefaultSameDayCutoff;
    var earliestCheckIn = GetEarliestCheckInDate(nowUtc, timeZoneId, cutoff);
    if (checkIn >= earliestCheckIn)
    {
      return;
    }

    throw new HospitalityPublicBookingException(
      "same_day_cutoff",
      $"Las reservaciones para llegada el mismo dia solo estan disponibles antes de las {cutoff:HH\\:mm} hrs. Selecciona una fecha posterior.");
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
