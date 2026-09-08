using System.Text.Json;

namespace OrionERP.Bruno.Web.Services;

public sealed record RestaurantOpeningHoursDay(string DayName, string Hours);

public static class RestaurantOpeningHours
{
  private static readonly (DayOfWeek Day, string JsonName, string DisplayName)[] Days =
  [
    (DayOfWeek.Monday, "Monday", "Lunes"),
    (DayOfWeek.Tuesday, "Tuesday", "Martes"),
    (DayOfWeek.Wednesday, "Wednesday", "Miércoles"),
    (DayOfWeek.Thursday, "Thursday", "Jueves"),
    (DayOfWeek.Friday, "Friday", "Viernes"),
    (DayOfWeek.Saturday, "Saturday", "Sábado"),
    (DayOfWeek.Sunday, "Sunday", "Domingo")
  ];

  public static DateTimeOffset LocalNow(string timeZoneId, DateTimeOffset utcNow)
    => TimeZoneInfo.ConvertTime(utcNow, TimeZoneInfo.FindSystemTimeZoneById(timeZoneId));

  public static string Today(
    string? openingHoursJson,
    string timeZoneId,
    DateTimeOffset utcNow)
  {
    DayOfWeek day;
    try
    {
      day = LocalNow(timeZoneId, utcNow).DayOfWeek;
    }
    catch (TimeZoneNotFoundException)
    {
      return "Consulta el horario";
    }
    catch (InvalidTimeZoneException)
    {
      return "Consulta el horario";
    }

    var item = Days.Single(candidate => candidate.Day == day);
    return Read(openingHoursJson, item.JsonName) ?? "Consulta el horario";
  }

  public static bool? IsOpen(
    string? openingHoursJson,
    string timeZoneId,
    DateTimeOffset utcNow)
  {
    try
    {
      var localNow = LocalNow(timeZoneId, utcNow);
      using var document = JsonDocument.Parse(openingHoursJson ?? string.Empty);
      if (document.RootElement.ValueKind != JsonValueKind.Object)
      {
        return null;
      }

      var today = Days.Single(item => item.Day == localNow.DayOfWeek);
      var previousDayValue = (DayOfWeek)(((int)localNow.DayOfWeek + 6) % 7);
      var previousDay = Days.Single(item => item.Day == previousDayValue);

      var hasTodayIntervals = TryReadIntervals(document.RootElement, today.JsonName, out var todayIntervals);
      var hasPreviousIntervals = TryReadIntervals(document.RootElement, previousDay.JsonName, out var previousIntervals);

      var currentTime = TimeOnly.FromDateTime(localNow.DateTime);
      var isOpen = todayIntervals.Any(interval =>
          interval.Opens < interval.Closes
            ? currentTime >= interval.Opens && currentTime < interval.Closes
            : interval.Opens > interval.Closes && currentTime >= interval.Opens)
        || previousIntervals.Any(interval =>
          interval.Opens > interval.Closes && currentTime < interval.Closes);
      return isOpen ? true : hasTodayIntervals && hasPreviousIntervals ? false : null;
    }
    catch (JsonException)
    {
      return null;
    }
    catch (TimeZoneNotFoundException)
    {
      return null;
    }
    catch (InvalidTimeZoneException)
    {
      return null;
    }
  }

  public static IReadOnlyList<RestaurantOpeningHoursDay> Week(string? openingHoursJson)
    => Days
      .Select(day => new RestaurantOpeningHoursDay(
        day.DisplayName,
        Read(openingHoursJson, day.JsonName) ?? "Horario no disponible"))
      .ToArray();

  private static string? Read(string? openingHoursJson, string dayName)
  {
    if (string.IsNullOrWhiteSpace(openingHoursJson))
    {
      return null;
    }

    try
    {
      using var document = JsonDocument.Parse(openingHoursJson);
      if (document.RootElement.ValueKind != JsonValueKind.Object
          || !TryReadIntervals(document.RootElement, dayName, out var intervals))
      {
        return null;
      }

      if (intervals.Count == 0)
      {
        return "Cerrado";
      }

      return string.Join(" · ", intervals.Select(interval =>
        $"{interval.Opens:HH\\:mm}–{interval.Closes:HH\\:mm}"));
    }
    catch (JsonException)
    {
      return null;
    }
  }

  private static bool TryReadIntervals(
    JsonElement root,
    string dayName,
    out IReadOnlyList<(TimeOnly Opens, TimeOnly Closes)> values)
  {
    values = Array.Empty<(TimeOnly Opens, TimeOnly Closes)>();
    if (!TryGetProperty(root, dayName, out var intervals)
        || intervals.ValueKind != JsonValueKind.Array)
    {
      return false;
    }

    var parsed = new List<(TimeOnly Opens, TimeOnly Closes)>();
    foreach (var interval in intervals.EnumerateArray())
    {
      if (interval.ValueKind != JsonValueKind.Object
          || !TryGetProperty(interval, "opens", out var opensElement)
          || !TryGetProperty(interval, "closes", out var closesElement)
          || opensElement.ValueKind != JsonValueKind.String
          || closesElement.ValueKind != JsonValueKind.String
          || !TimeOnly.TryParseExact(opensElement.GetString(), "HH:mm", out var opens)
          || !TimeOnly.TryParseExact(closesElement.GetString(), "HH:mm", out var closes)
          || opens == closes)
      {
        return false;
      }

      parsed.Add((opens, closes));
    }

    values = parsed;
    return true;
  }

  private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
  {
    foreach (var property in element.EnumerateObject())
    {
      if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
      {
        value = property.Value;
        return true;
      }
    }

    value = default;
    return false;
  }
}
