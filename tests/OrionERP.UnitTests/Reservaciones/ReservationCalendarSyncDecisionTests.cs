using OrionERP.Web.Features.Reservaciones.ListaReservaciones;

namespace OrionERP.UnitTests.Reservaciones;

public class ReservationCalendarSyncDecisionTests
{
  [Fact]
  public void ShouldSync_ReturnsFalse_WhenOnlyNonCalendarFieldsWouldChange()
  {
    var before = new ReservationCalendarSyncSnapshot(12, "NUEVA", new DateTime(2026, 5, 1), new DateTime(2026, 5, 3));
    var after = new ReservationCalendarSyncSnapshot(12, " NUEVA ", new DateTime(2026, 5, 1, 12, 0, 0), new DateTime(2026, 5, 3));

    var shouldSync = ReservationCalendarSyncDecision.ShouldSync(before, after, suitesChanged: false);

    Assert.False(shouldSync);
  }

  [Theory]
  [InlineData("cliente")]
  [InlineData("status")]
  [InlineData("checkin")]
  [InlineData("checkout")]
  public void ShouldSync_ReturnsTrue_ForCalendarRelevantFormChanges(string changedField)
  {
    var before = new ReservationCalendarSyncSnapshot(12, "NUEVA", new DateTime(2026, 5, 1), new DateTime(2026, 5, 3));
    var after = changedField switch
    {
      "cliente" => before with { ClienteId = 13 },
      "status" => before with { Status = "PAGADA" },
      "checkin" => before with { CheckIn = new DateTime(2026, 5, 2) },
      "checkout" => before with { CheckOut = new DateTime(2026, 5, 4) },
      _ => before
    };

    var shouldSync = ReservationCalendarSyncDecision.ShouldSync(before, after, suitesChanged: false);

    Assert.True(shouldSync);
  }

  [Fact]
  public void ShouldSync_ReturnsTrue_WhenSuitesChanged()
  {
    var snapshot = new ReservationCalendarSyncSnapshot(12, "NUEVA", new DateTime(2026, 5, 1), new DateTime(2026, 5, 3));

    var shouldSync = ReservationCalendarSyncDecision.ShouldSync(snapshot, snapshot, suitesChanged: true);

    Assert.True(shouldSync);
  }
}
