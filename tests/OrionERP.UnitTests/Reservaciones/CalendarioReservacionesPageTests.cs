using Microsoft.AspNetCore.Components;
using OrionERP.Application.Features.OrdenesTrabajo;
using OrionERP.Application.Features.Reservaciones.ListaReservaciones;
using OrionERP.Web.Features.Reservaciones.Calendario;

namespace OrionERP.UnitTests.Reservaciones;

public class CalendarioReservacionesPageTests
{
  [Fact]
  public void WorkOrderBadgeLabel_UsesOwnerAndFolioSuffix()
  {
    var badge = new OrdenTrabajoCalendarBadgeDto
    {
      Folio = "OT-2026-000040",
      OwnerName = "Monse"
    };

    var label = TestableCalendarioReservacionesPage.FormatBadgeLabel(badge);

    Assert.Equal("Monse - 0040", label);
  }

  [Fact]
  public void WorkOrderBadgeLabel_IncludesHelpersAfterOwner()
  {
    var badge = new OrdenTrabajoCalendarBadgeDto
    {
      Folio = "OT-2026-000040",
      OwnerName = "Monse",
      HelperNames = "Alex/Miguel"
    };

    var label = TestableCalendarioReservacionesPage.FormatBadgeLabel(badge);

    Assert.Equal("Monse/Alex/Miguel - 0040", label);
  }

  [Fact]
  public void DefaultFilter_UsesCurrentAndNextMonth()
  {
    var filter = TestableCalendarioReservacionesPage.CreateDefault(new DateTime(2026, 6, 3));

    Assert.Equal(new DateTime(2026, 6, 1), filter.StartDate);
    Assert.Equal(new DateTime(2026, 8, 1), filter.EndDateExclusive);
    Assert.Equal("SUITE", filter.RoomType);
  }

  [Fact]
  public void MonthInputs_MapEndMonthToExclusiveFollowingMonth()
  {
    var page = new TestableCalendarioReservacionesPage();

    page.SetStartMonth("2026-06");
    page.SetEndMonth("2026-07");

    Assert.Equal(new DateTime(2026, 6, 1), page.FilterStartDate);
    Assert.Equal(new DateTime(2026, 8, 1), page.FilterEndDateExclusive);
    Assert.Equal("2026-06", page.StartMonth);
    Assert.Equal("2026-07", page.EndMonth);
  }

  [Fact]
  public void MonthRangeValidation_RejectsEndBeforeStart()
  {
    var filter = new RoomCalendarTimelineFilter
    {
      StartDate = new DateTime(2026, 8, 1),
      EndDateExclusive = new DateTime(2026, 8, 1)
    };

    Assert.False(TestableCalendarioReservacionesPage.IsValidRange(filter));
  }

  private sealed class TestableCalendarioReservacionesPage : CalendarioReservacionesPage
  {
    public static string FormatBadgeLabel(OrdenTrabajoCalendarBadgeDto badge)
      => GetCalendarOrderBadgeLabel(badge);

    public static RoomCalendarTimelineFilter CreateDefault(DateTime today)
      => CreateDefaultFilter(today);

    public static bool IsValidRange(RoomCalendarTimelineFilter filter)
      => IsValidMonthRange(filter);

    public DateTime FilterStartDate => Filter.StartDate;

    public DateTime FilterEndDateExclusive => Filter.EndDateExclusive;

    public string StartMonth => StartMonthValue;

    public string EndMonth => EndMonthValue;

    public void SetStartMonth(string value)
      => OnStartMonthChanged(new ChangeEventArgs { Value = value });

    public void SetEndMonth(string value)
      => OnEndMonthChanged(new ChangeEventArgs { Value = value });
  }
}
