using System.Globalization;
using OrionERP.Application.Features.Bonhomia.PublicBooking;

namespace OrionERP.Bonhomia.Web.Features.Bonhomia;

public enum BonhomiaCalendarSelectionTarget
{
  CheckIn,
  CheckOut
}

public readonly record struct BonhomiaStaySelection(
  DateOnly CheckIn,
  DateOnly CheckOut,
  BonhomiaCalendarSelectionTarget NextSelection);

public static class BonhomiaBookingCalendarRules
{
  public static DateOnly GetMonthStart(DateOnly date)
    => new(date.Year, date.Month, 1);

  public static DateOnly GetLatestCalendarStartMonth(
    DateOnly today,
    int maxMonthOffset,
    int visibleMonthCount)
  {
    var earliestMonth = GetMonthStart(today);
    var latestAllowedMonth = earliestMonth.AddMonths(Math.Max(0, maxMonthOffset));
    var latestStartMonth = latestAllowedMonth.AddMonths(-Math.Max(0, visibleMonthCount - 1));

    return latestStartMonth < earliestMonth ? earliestMonth : latestStartMonth;
  }

  public static DateOnly ClampCalendarStartMonth(
    DateOnly requestedMonth,
    DateOnly today,
    int maxMonthOffset,
    int visibleMonthCount)
  {
    var month = GetMonthStart(requestedMonth);
    var earliestMonth = GetMonthStart(today);
    var latestMonth = GetLatestCalendarStartMonth(today, maxMonthOffset, visibleMonthCount);

    if (month < earliestMonth)
    {
      return earliestMonth;
    }

    return month > latestMonth ? latestMonth : month;
  }

  public static IReadOnlyList<DateOnly> BuildVisibleCalendarDays(DateOnly startMonth, int visibleMonthCount)
  {
    var month = GetMonthStart(startMonth);
    var endExclusive = month.AddMonths(Math.Max(1, visibleMonthCount));

    return Enumerable
      .Range(0, endExclusive.DayNumber - month.DayNumber)
      .Select(offset => month.AddDays(offset))
      .ToArray();
  }

  public static BonhomiaStaySelection SelectCalendarDay(
    DateOnly day,
    DateOnly today,
    BonhomiaStaySelection current)
  {
    if (day < today)
    {
      return current;
    }

    if (current.NextSelection == BonhomiaCalendarSelectionTarget.CheckOut
        && day > current.CheckIn)
    {
      return current with
      {
        CheckOut = day,
        NextSelection = BonhomiaCalendarSelectionTarget.CheckIn
      };
    }

    return StartNewCheckIn(day, current.CheckOut);
  }

  public static BonhomiaStaySelection SetCheckIn(
    DateOnly value,
    DateOnly today,
    BonhomiaStaySelection current)
  {
    var checkIn = value < today ? today : value;
    var checkOut = current.CheckOut <= checkIn ? checkIn.AddDays(1) : current.CheckOut;

    return new BonhomiaStaySelection(
      checkIn,
      checkOut,
      BonhomiaCalendarSelectionTarget.CheckOut);
  }

  public static BonhomiaStaySelection SetCheckOut(DateOnly value, BonhomiaStaySelection current)
  {
    var checkOut = value <= current.CheckIn ? current.CheckIn.AddDays(1) : value;

    return new BonhomiaStaySelection(
      current.CheckIn,
      checkOut,
      BonhomiaCalendarSelectionTarget.CheckIn);
  }

  public static bool IsAnySuiteAvailableOnDate(IEnumerable<BonhomiaRoomAvailabilityDto> rooms, DateOnly day)
    => rooms.Any(room => IsRoomDayAvailable(room, day));

  public static bool IsSuiteAvailableForStay(
    BonhomiaRoomAvailabilityDto room,
    DateOnly checkIn,
    DateOnly checkOut)
    => GetUnavailableDatesForStay(room, checkIn, checkOut).Count == 0;

  public static IReadOnlyList<DateOnly> GetUnavailableDatesForStay(
    BonhomiaRoomAvailabilityDto room,
    DateOnly checkIn,
    DateOnly checkOut)
  {
    if (checkOut <= checkIn)
    {
      return Array.Empty<DateOnly>();
    }

    var unavailableDates = new List<DateOnly>();
    for (var day = checkIn; day < checkOut; day = day.AddDays(1))
    {
      if (!IsRoomDayAvailable(room, day))
      {
        unavailableDates.Add(day);
      }
    }

    return unavailableDates;
  }

  public static string ResolveSelectedRoomName(
    string? selectedRoomName,
    IEnumerable<BonhomiaRoomAvailabilityDto> rooms,
    DateOnly checkIn,
    DateOnly checkOut)
  {
    if (string.IsNullOrWhiteSpace(selectedRoomName))
    {
      return string.Empty;
    }

    var selectedRoom = rooms.FirstOrDefault(room =>
      string.Equals(room.RoomName, selectedRoomName, StringComparison.OrdinalIgnoreCase));

    return selectedRoom is not null && IsSuiteAvailableForStay(selectedRoom, checkIn, checkOut)
      ? selectedRoom.RoomName
      : string.Empty;
  }

  public static string BuildUnavailableSuiteNote(
    IEnumerable<DateOnly> unavailableDates,
    CultureInfo culture)
  {
    var dates = unavailableDates
      .Distinct()
      .OrderBy(date => date.DayNumber)
      .ToArray();

    return dates.Length == 0
      ? "Disponible para tus fechas."
      : $"Reservada: {FormatUnavailableDates(dates, culture)}";
  }

  public static string FormatUnavailableDates(IEnumerable<DateOnly> unavailableDates, CultureInfo culture)
    => string.Join(
      ", ",
      unavailableDates
        .Distinct()
        .OrderBy(date => date.DayNumber)
        .Select(date => date.ToString("d MMM yyyy", culture)));

  private static BonhomiaStaySelection StartNewCheckIn(DateOnly day, DateOnly previousCheckOut)
  {
    var checkOut = previousCheckOut;
    if (day > previousCheckOut)
    {
      checkOut = day.AddDays(3);
    }
    else if (checkOut <= day)
    {
      checkOut = day.AddDays(1);
    }

    return new BonhomiaStaySelection(
      day,
      checkOut,
      BonhomiaCalendarSelectionTarget.CheckOut);
  }

  private static bool IsRoomDayAvailable(BonhomiaRoomAvailabilityDto room, DateOnly day)
    => room.Days.FirstOrDefault(item => item.Date == day)?.IsAvailable == true;
}
