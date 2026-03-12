using System;

namespace OrionERP.Application.Features.Reservaciones.ListaReservaciones;

public sealed class RoomCalendarEventDto
{
  public int RoomId { get; set; }
  public string RoomCode { get; set; } = string.Empty;
  public string RoomName { get; set; } = string.Empty;
  public DateTime StartDate { get; set; }
  public DateTime EndDateExclusive { get; set; }
  public string EventType { get; set; } = string.Empty;
  public int? ReservationId { get; set; }
  public string? ReservationStatus { get; set; }
  public string? LockedBy { get; set; }
  public string? LockDescription { get; set; }
  public string Title { get; set; } = string.Empty;
  public string? Subtitle { get; set; }
  public string? DataQualityFlag { get; set; }
}
