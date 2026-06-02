namespace OrionERP.UnitTests.Reservaciones;

public class ReservationPaidStatusDashboardTests
{
  [Fact]
  public void ListaReservacionesService_RecalculatesListTotalsWithExperiences()
  {
    var source = ReadRepositoryFile(
      "src",
      "OrionERP.Infrastructure",
      "Features",
      "Reservaciones",
      "ListaReservaciones",
      "Services",
      "ListaReservacionesService.cs");

    Assert.Contains("dbo.Reservation_Experience", source, StringComparison.Ordinal);
    Assert.Contains("re.TotalSnapshot", source, StringComparison.Ordinal);
    Assert.Contains("experiencesByReservation", source, StringComparison.Ordinal);
    Assert.Contains("experiencesByReservation[row.Id]", source, StringComparison.Ordinal);
    Assert.DoesNotContain("Array.Empty<ReservationChargeLine>(),\r\n        pagosByReservation[row.Id].Sum()", source, StringComparison.Ordinal);
  }

  [Fact]
  public void ReservationPaidStatusSync_UsesCalculatedDebtAndUpdatesCalendarStatus()
  {
    var interfaceSource = ReadRepositoryFile(
      "src",
      "OrionERP.Application",
      "Features",
      "Reservaciones",
      "ListaReservaciones",
      "IListaReservacionesService.cs");
    var serviceSource = ReadRepositoryFile(
      "src",
      "OrionERP.Infrastructure",
      "Features",
      "Reservaciones",
      "ListaReservaciones",
      "Services",
      "ListaReservacionesService.cs");

    Assert.Contains("SyncPaidReservationStatusesAsync", interfaceSource, StringComparison.Ordinal);
    Assert.Contains("SyncPaidReservationStatusesAsync", serviceSource, StringComparison.Ordinal);
    Assert.Contains("row.TotalPrice > 5m", serviceSource, StringComparison.Ordinal);
    Assert.Contains("row.PorPagar >= -5m", serviceSource, StringComparison.Ordinal);
    Assert.Contains("row.PorPagar <= 5m", serviceSource, StringComparison.Ordinal);
    Assert.Contains("SET STATUS = @PagadaStatus", serviceSource, StringComparison.Ordinal);
    Assert.Contains("dbo.ROOM_CALENDAR", serviceSource, StringComparison.Ordinal);
  }

  [Fact]
  public void HomeDashboard_ShowsPaidReservationsForNextThreeCalendarDays()
  {
    var interfaceSource = ReadRepositoryFile(
      "src",
      "OrionERP.Application",
      "Features",
      "Reservaciones",
      "ListaReservaciones",
      "IListaReservacionesService.cs");
    var dtoSource = ReadRepositoryFile(
      "src",
      "OrionERP.Application",
      "Features",
      "Reservaciones",
      "ListaReservaciones",
      "ListaReservacionItemDto.cs");
    var serviceSource = ReadRepositoryFile(
      "src",
      "OrionERP.Infrastructure",
      "Features",
      "Reservaciones",
      "ListaReservaciones",
      "Services",
      "ListaReservacionesService.cs");
    var homeSource = ReadRepositoryFile(
      "src",
      "OrionERP.Web",
      "Pages",
      "ErpHomeDashboard.razor");
    var homeCss = ReadRepositoryFile(
      "src",
      "OrionERP.Web",
      "Pages",
      "ErpHomeDashboard.razor.css");

    Assert.Contains("GetUpcomingPaidReservationsAsync", interfaceSource, StringComparison.Ordinal);
    Assert.Contains("public DateTime? RoomDate", dtoSource, StringComparison.Ordinal);
    Assert.Contains("public string? Suite", dtoSource, StringComparison.Ordinal);
    Assert.Contains("CAST(rc.ROOM_DATE AS date) AS RoomDate", serviceSource, StringComparison.Ordinal);
    Assert.Contains("INNER JOIN dbo.ROOM_CALENDAR rc", serviceSource, StringComparison.Ordinal);
    Assert.Contains("r.ID = TRY_CAST(rc.LOCK_DESCRIPTION AS int)", serviceSource, StringComparison.Ordinal);
    Assert.Contains("rc.ROOM_DATE >= @FromDate", serviceSource, StringComparison.Ordinal);
    Assert.Contains("rc.ROOM_DATE < @ToDate", serviceSource, StringComparison.Ordinal);
    Assert.Contains("RoomDate = row.RoomDate", serviceSource, StringComparison.Ordinal);
    Assert.Contains("Suite = row.Suite", serviceSource, StringComparison.Ordinal);
    Assert.Contains("IListaReservacionesService ReservacionesService", homeSource, StringComparison.Ordinal);
    Assert.Contains("home-reservations", homeSource, StringComparison.Ordinal);
    Assert.Contains("Reservaciones", homeSource, StringComparison.Ordinal);
    Assert.Contains("Hoy", homeSource, StringComparison.Ordinal);
    Assert.Contains("Mañana", homeSource, StringComparison.Ordinal);
    Assert.Contains("Pasado Mañana", homeSource, StringComparison.Ordinal);
    Assert.Contains("SyncPaidReservationStatusesAsync", homeSource, StringComparison.Ordinal);
    Assert.Contains("GetUpcomingPaidReservationsAsync(DateTime.Today, 3)", homeSource, StringComparison.Ordinal);
    Assert.Contains("item.RoomDate?.Date == date", homeSource, StringComparison.Ordinal);
    Assert.Contains("item.Suite", homeSource, StringComparison.Ordinal);
    Assert.Contains(".home-reservation-days", homeCss, StringComparison.Ordinal);
  }

  private static string ReadRepositoryFile(params string[] paths)
  {
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "OrionERP.sln")))
    {
      directory = directory.Parent;
    }

    if (directory is null)
    {
      throw new InvalidOperationException("Could not locate repository root.");
    }

    var fullPathSegments = new string[paths.Length + 1];
    fullPathSegments[0] = directory.FullName;
    Array.Copy(paths, 0, fullPathSegments, 1, paths.Length);

    return File.ReadAllText(Path.Combine(fullPathSegments));
  }
}
