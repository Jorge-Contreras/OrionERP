using System;

namespace OrionERP.Application.Features.Reservaciones.ListaReservaciones;

public sealed class RoomCalendarDayCellDto
{
  public int RoomId { get; set; }
  public string RoomCode { get; set; } = string.Empty;
  public string RoomName { get; set; } = string.Empty;
  public DateTime RoomDate { get; set; }
  public int? RoomCalendarId { get; set; }
  public bool IsLocked { get; set; }
  public string? LockedBy { get; set; }
  public string? LockDescription { get; set; }
  public string StateCode { get; set; } = string.Empty;
  public int? ReservationId { get; set; }
  public string? ReservationStatus { get; set; }
  public bool IsArrival { get; set; }
  public bool IsDeparture { get; set; }
  public bool HasExtras { get; set; }
  public bool HasDeepCleaning { get; set; }
  public bool HasDailyCheck { get; set; }
  public decimal Price { get; set; }
  public string? Notes { get; set; }
  public string? DataQualityFlag { get; set; }
}
