using OrionERP.Application.Features.Reservaciones.ListaReservaciones;

namespace OrionERP.UnitTests.Reservaciones;

public class ReservationQuoteStatusTests
{
  [Fact]
  public void ReservationStatuses_NormalizesAndIdentifiesQuoteStatus()
  {
    Assert.Equal(ReservationStatuses.Nueva, ReservationStatuses.NormalizeOrDefault(null));
    Assert.Equal(ReservationStatuses.Nueva, ReservationStatuses.NormalizeOrDefault("   "));
    Assert.Equal(ReservationStatuses.Cotizacion, ReservationStatuses.NormalizeOrDefault(" COTIZACION "));
    Assert.True(ReservationStatuses.IsQuote(" cotizacion "));
    Assert.False(ReservationStatuses.IsQuote(ReservationStatuses.Nueva));
  }

  [Fact]
  public void ReservationStatuses_ExposeCotizacionAsEditableStatus()
  {
    Assert.Contains(ReservationStatuses.Cotizacion, ReservationStatuses.EditableOptions);
    Assert.Contains(ReservationStatuses.Nueva, ReservationStatuses.EditableOptions);
    Assert.Contains(ReservationStatuses.Pagada, ReservationStatuses.EditableOptions);
    Assert.Contains(ReservationStatuses.Cancelada, ReservationStatuses.EditableOptions);
  }

  [Fact]
  public void CalendarTimelineSql_UsesReservationStatusForSoftHold()
  {
    var sql = ReadRepositoryFile(
      "src",
      "OrionERP.Infrastructure",
      "Features",
      "Reservaciones",
      "ListaReservaciones",
      "Sql",
      "20260310_calendar_get_room_timeline.sql");

    Assert.Contains("r.STATUS AS ReservationStatus", sql, StringComparison.Ordinal);
    Assert.Contains("ISNULL(r.STATUS, '')))) COLLATE Latin1_General_100_CI_AI = N'COTIZACION' THEN 'soft_hold'", sql, StringComparison.Ordinal);
    Assert.DoesNotContain("ISNULL(rc.LOCKED_BY, '')))) COLLATE Latin1_General_100_CI_AI = N'COTIZACION' THEN 'soft_hold'", sql, StringComparison.Ordinal);
  }

  [Fact]
  public void OutlookSyncRepository_ExcludesQuoteReservationsByReservationStatus()
  {
    var source = ReadRepositoryFile(
      "src",
      "OrionERP.Infrastructure",
      "Features",
      "Reservaciones",
      "CalendarSync",
      "OutlookRoomCalendarSyncRepository.cs");

    Assert.Contains("ISNULL(r.STATUS, '')))) COLLATE Latin1_General_100_CI_AI <> N'COTIZACION'", source, StringComparison.Ordinal);
    Assert.Contains("COALESCE(NULLIF(LTRIM(RTRIM(r.STATUS)), ''), NULLIF(LTRIM(RTRIM(rc.STATUS)), '')) AS Status", source, StringComparison.Ordinal);
    Assert.DoesNotContain("ISNULL(rc.LOCKED_BY, '')))) COLLATE Latin1_General_100_CI_AI <> N'COTIZACION'", source, StringComparison.Ordinal);
  }

  [Fact]
  public void QuoteBackfillSql_MarksOnlyNonFinalQuoteClientReservations()
  {
    var sql = ReadRepositoryFile(
      "src",
      "OrionERP.Infrastructure",
      "Features",
      "Reservaciones",
      "ListaReservaciones",
      "Sql",
      "20260524_reservation_quote_status_backfill.sql");

    Assert.Contains("LIKE N'%COTIZAC%'", sql, StringComparison.Ordinal);
    Assert.Contains("(N'PAGADA', N'CANCELADA', N'CANCELADO')", sql, StringComparison.Ordinal);
    Assert.Contains("UPDATE r", sql, StringComparison.Ordinal);
    Assert.Contains("SET STATUS = 'COTIZACION'", sql, StringComparison.Ordinal);
    Assert.Contains("UPDATE rc", sql, StringComparison.Ordinal);
    Assert.Contains("q.ID = TRY_CAST(rc.LOCK_DESCRIPTION AS int)", sql, StringComparison.Ordinal);
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
