using System;

namespace OrionERP.Application.Features.Reservaciones.ListaReservaciones;

public sealed class ReservacionPagoDto
{
  public int TransaccionId { get; set; }
  public DateTime Fecha { get; set; }
  public string Concepto { get; set; } = string.Empty;
  public decimal Monto { get; set; }
}
