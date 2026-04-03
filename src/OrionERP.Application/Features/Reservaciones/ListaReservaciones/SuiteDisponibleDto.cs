using System;

namespace OrionERP.Application.Features.Reservaciones.ListaReservaciones;

public sealed class SuiteDisponibleDto
{
  public int Id { get; set; }
  public string Dia { get; set; } = string.Empty;
  public bool IsLocked { get; set; }
  public string Suite { get; set; } = string.Empty;
  public DateTime RoomDate { get; set; }
  public string? LockedBy { get; set; }
  public string? LockDescription { get; set; }
  public decimal Precio { get; set; }
  public string? Status { get; set; }
  public DateTime? VencimientoBloqueo { get; set; }
}
