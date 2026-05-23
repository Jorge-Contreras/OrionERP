using System;
using OrionERP.Application.Features.Reservaciones.ListaReservaciones;

namespace OrionERP.Web.Features.Reservaciones.ListaReservaciones;

public sealed record ReservationCalendarSyncSnapshot(
  int? ClienteId,
  string? Status,
  DateTime? CheckIn,
  DateTime? CheckOut);

public static class ReservationCalendarSyncDecision
{
  public static ReservationCalendarSyncSnapshot FromDetail(ReservacionDetailDto? detail)
    => detail is null
      ? new ReservationCalendarSyncSnapshot(null, null, null, null)
      : new ReservationCalendarSyncSnapshot(
        detail.ClienteId,
        NormalizeStatus(detail.Status),
        NormalizeDate(detail.CheckIn),
        NormalizeDate(detail.CheckOut));

  public static ReservationCalendarSyncSnapshot FromForm(
    int? clienteId,
    string? status,
    DateTime? checkIn,
    DateTime? checkOut)
    => new(
      clienteId,
      NormalizeStatus(status),
      NormalizeDate(checkIn),
      NormalizeDate(checkOut));

  public static bool ShouldSync(
    ReservationCalendarSyncSnapshot before,
    ReservationCalendarSyncSnapshot after,
    bool suitesChanged)
    => suitesChanged
      || before.ClienteId != after.ClienteId
      || !string.Equals(NormalizeStatus(before.Status), NormalizeStatus(after.Status), StringComparison.OrdinalIgnoreCase)
      || NormalizeDate(before.CheckIn) != NormalizeDate(after.CheckIn)
      || NormalizeDate(before.CheckOut) != NormalizeDate(after.CheckOut);

  private static string? NormalizeStatus(string? value)
    => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

  private static DateTime? NormalizeDate(DateTime? value)
    => value?.Date;
}
