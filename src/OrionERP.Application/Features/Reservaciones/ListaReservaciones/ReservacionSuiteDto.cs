using System;

namespace OrionERP.Application.Features.Reservaciones.ListaReservaciones;

public sealed class ReservacionSuiteDto
{
  public int Id { get; set; }
  public DateTime Fecha { get; set; }
  public string Suite { get; set; } = string.Empty;
  public decimal Precio { get; set; }
  public string? LockDescription { get; set; }
  public bool LimpiezaProfunda { get; set; }
}
