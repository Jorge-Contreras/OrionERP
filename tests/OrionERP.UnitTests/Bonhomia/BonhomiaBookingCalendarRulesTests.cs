using System.Globalization;
using OrionERP.Application.Features.Bonhomia.PublicBooking;
using OrionERP.Bonhomia.Web.Features.Bonhomia;

namespace OrionERP.UnitTests.Bonhomia;

public class BonhomiaBookingCalendarRulesTests
{
  [Fact]
  public void CalendarWindow_BuildsExactlyTwoCompleteMonths()
  {
    var days = BonhomiaBookingCalendarRules.BuildVisibleCalendarDays(
      new DateOnly(2026, 5, 24),
      visibleMonthCount: 2);

    Assert.Equal(new DateOnly(2026, 5, 1), days.First());
    Assert.Equal(new DateOnly(2026, 6, 30), days.Last());
    Assert.Equal(61, days.Count);
  }

  [Fact]
  public void CalendarWindow_ClampsBetweenCurrentMonthAndSixMonthLimit()
  {
    var today = new DateOnly(2026, 5, 24);

    var beforeCurrentMonth = BonhomiaBookingCalendarRules.ClampCalendarStartMonth(
      new DateOnly(2026, 4, 1),
      today,
      maxMonthOffset: 6,
      visibleMonthCount: 2);
    var afterLimit = BonhomiaBookingCalendarRules.ClampCalendarStartMonth(
      new DateOnly(2026, 12, 1),
      today,
      maxMonthOffset: 6,
      visibleMonthCount: 2);

    Assert.Equal(new DateOnly(2026, 5, 1), beforeCurrentMonth);
    Assert.Equal(new DateOnly(2026, 10, 1), afterLimit);
  }

  [Fact]
  public void CalendarClicks_SelectCheckInThenCheckOutThenNewCheckIn()
  {
    var today = new DateOnly(2026, 1, 1);
    var selection = new BonhomiaStaySelection(
      new DateOnly(2026, 1, 8),
      new DateOnly(2026, 1, 10),
      BonhomiaCalendarSelectionTarget.CheckIn);

    selection = BonhomiaBookingCalendarRules.SelectCalendarDay(new DateOnly(2026, 1, 5), today, selection);

    Assert.Equal(new DateOnly(2026, 1, 5), selection.CheckIn);
    Assert.Equal(new DateOnly(2026, 1, 10), selection.CheckOut);
    Assert.Equal(BonhomiaCalendarSelectionTarget.CheckOut, selection.NextSelection);

    selection = BonhomiaBookingCalendarRules.SelectCalendarDay(new DateOnly(2026, 1, 7), today, selection);

    Assert.Equal(new DateOnly(2026, 1, 5), selection.CheckIn);
    Assert.Equal(new DateOnly(2026, 1, 7), selection.CheckOut);
    Assert.Equal(BonhomiaCalendarSelectionTarget.CheckIn, selection.NextSelection);

    selection = BonhomiaBookingCalendarRules.SelectCalendarDay(new DateOnly(2026, 1, 6), today, selection);

    Assert.Equal(new DateOnly(2026, 1, 6), selection.CheckIn);
    Assert.Equal(new DateOnly(2026, 1, 7), selection.CheckOut);
    Assert.Equal(BonhomiaCalendarSelectionTarget.CheckOut, selection.NextSelection);
  }

  [Fact]
  public void CalendarClick_AfterPreviousCheckOut_StartsThreeDayRange()
  {
    var selection = new BonhomiaStaySelection(
      new DateOnly(2026, 1, 5),
      new DateOnly(2026, 1, 7),
      BonhomiaCalendarSelectionTarget.CheckIn);

    var next = BonhomiaBookingCalendarRules.SelectCalendarDay(
      new DateOnly(2026, 1, 10),
      new DateOnly(2026, 1, 1),
      selection);

    Assert.Equal(new DateOnly(2026, 1, 10), next.CheckIn);
    Assert.Equal(new DateOnly(2026, 1, 13), next.CheckOut);
    Assert.Equal(BonhomiaCalendarSelectionTarget.CheckOut, next.NextSelection);
  }

  [Fact]
  public void CalendarCheckoutClick_OnOrBeforeCheckIn_RestartsCheckInSelection()
  {
    var selection = new BonhomiaStaySelection(
      new DateOnly(2026, 1, 5),
      new DateOnly(2026, 1, 8),
      BonhomiaCalendarSelectionTarget.CheckOut);

    var next = BonhomiaBookingCalendarRules.SelectCalendarDay(
      new DateOnly(2026, 1, 4),
      new DateOnly(2026, 1, 1),
      selection);

    Assert.Equal(new DateOnly(2026, 1, 4), next.CheckIn);
    Assert.Equal(new DateOnly(2026, 1, 8), next.CheckOut);
    Assert.Equal(BonhomiaCalendarSelectionTarget.CheckOut, next.NextSelection);
  }

  [Fact]
  public void SuiteAvailability_RequiresEverySelectedNightAvailable()
  {
    var room = CreateRoom(
      "Casa Berlin",
      (new DateOnly(2026, 1, 1), true),
      (new DateOnly(2026, 1, 2), false),
      (new DateOnly(2026, 1, 3), true));

    var unavailableDates = BonhomiaBookingCalendarRules.GetUnavailableDatesForStay(
      room,
      new DateOnly(2026, 1, 1),
      new DateOnly(2026, 1, 3));

    Assert.False(BonhomiaBookingCalendarRules.IsSuiteAvailableForStay(
      room,
      new DateOnly(2026, 1, 1),
      new DateOnly(2026, 1, 3)));
    Assert.Equal(new[] { new DateOnly(2026, 1, 2) }, unavailableDates);
    Assert.True(BonhomiaBookingCalendarRules.IsSuiteAvailableForStay(
      room,
      new DateOnly(2026, 1, 1),
      new DateOnly(2026, 1, 2)));
  }

  [Fact]
  public void UnavailableSuiteNote_ListsReservedDatesForSelectedRange()
  {
    var note = BonhomiaBookingCalendarRules.BuildUnavailableSuiteNote(
      new[] { new DateOnly(2026, 1, 3), new DateOnly(2026, 1, 2) },
      CultureInfo.GetCultureInfo("es-MX"));

    Assert.StartsWith("Reservada: ", note);
    Assert.Contains("2", note);
    Assert.Contains("3", note);
    Assert.Contains("ene", note.ToLowerInvariant());
    Assert.Contains("2026", note);
  }

  [Fact]
  public void ResolveSelectedRoomName_ClearsUnavailableSelectedSuite()
  {
    var unavailableRoom = CreateRoom(
      "Casa Berlin",
      (new DateOnly(2026, 1, 1), true),
      (new DateOnly(2026, 1, 2), false));
    var availableRoom = CreateRoom(
      "Suite Manhattan",
      (new DateOnly(2026, 1, 1), true),
      (new DateOnly(2026, 1, 2), true));
    var rooms = new[] { unavailableRoom, availableRoom };

    var cleared = BonhomiaBookingCalendarRules.ResolveSelectedRoomName(
      "Casa Berlin",
      rooms,
      new DateOnly(2026, 1, 1),
      new DateOnly(2026, 1, 3));
    var preserved = BonhomiaBookingCalendarRules.ResolveSelectedRoomName(
      "suite manhattan",
      rooms,
      new DateOnly(2026, 1, 1),
      new DateOnly(2026, 1, 3));

    Assert.Equal(string.Empty, cleared);
    Assert.Equal("Suite Manhattan", preserved);
  }

  private static BonhomiaRoomAvailabilityDto CreateRoom(
    string name,
    params (DateOnly Date, bool IsAvailable)[] days)
    => new()
    {
      RoomName = name,
      Days = days
        .Select(day => new BonhomiaDayAvailabilityDto
        {
          Date = day.Date,
          IsAvailable = day.IsAvailable
        })
        .ToArray()
    };
}
