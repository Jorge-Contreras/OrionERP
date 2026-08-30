namespace OrionERP.Application.Features.Restaurante;

public static class RestaurantServiceDayPolicy
{
  public static readonly TimeOnly StartTime = new(5, 0);

  public static DateOnly GetServiceDate(DateTimeOffset instant, TimeZoneInfo timeZone)
  {
    ArgumentNullException.ThrowIfNull(timeZone);
    var localTime = TimeZoneInfo.ConvertTime(instant, timeZone);
    var localDate = DateOnly.FromDateTime(localTime.DateTime);
    return TimeOnly.FromDateTime(localTime.DateTime) < StartTime
      ? localDate.AddDays(-1)
      : localDate;
  }

  public static RestaurantServiceDayUtcWindow GetUtcWindow(DateOnly serviceDate, TimeZoneInfo timeZone)
  {
    ArgumentNullException.ThrowIfNull(timeZone);
    var startLocal = DateTime.SpecifyKind(serviceDate.ToDateTime(StartTime), DateTimeKind.Unspecified);
    var endLocal = DateTime.SpecifyKind(serviceDate.AddDays(1).ToDateTime(StartTime), DateTimeKind.Unspecified);
    return new(
      TimeZoneInfo.ConvertTimeToUtc(startLocal, timeZone),
      TimeZoneInfo.ConvertTimeToUtc(endLocal, timeZone));
  }
}

public readonly record struct RestaurantServiceDayUtcWindow(DateTime StartUtc, DateTime EndUtcExclusive);
