using System;

namespace OrionERP.Application.Features.Reservaciones.ListaReservaciones;

public sealed class ListaReservacionFilter
{
  public int? Id { get; set; }
  public string? Cliente { get; set; }
  public string? Status { get; set; }
  public DateTime? CheckInFrom { get; set; }
  public DateTime? CheckInTo { get; set; }
  public bool IncluirCanceladas { get; set; }
}
