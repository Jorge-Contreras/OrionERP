using OrionERP.Application.Features.OrdenesTrabajo;
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

  private sealed class TestableCalendarioReservacionesPage : CalendarioReservacionesPage
  {
    public static string FormatBadgeLabel(OrdenTrabajoCalendarBadgeDto badge)
      => GetCalendarOrderBadgeLabel(badge);
  }
}
