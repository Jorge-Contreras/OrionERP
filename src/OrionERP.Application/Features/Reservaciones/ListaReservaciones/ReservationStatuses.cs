using System;
using System.Collections.Generic;

namespace OrionERP.Application.Features.Reservaciones.ListaReservaciones;

public static class ReservationStatuses
{
  public const string Nueva = "NUEVA";
  public const string Pagada = "PAGADA";
  public const string Cancelada = "Cancelada";
  public const string Cotizacion = "COTIZACION";

  public static IReadOnlyList<string> EditableOptions { get; } =
  [
    Cotizacion,
    Nueva,
    Pagada,
    Cancelada
  ];

  public static string NormalizeOrDefault(string? status, string defaultStatus = Nueva)
    => string.IsNullOrWhiteSpace(status) ? defaultStatus : status.Trim();

  public static bool IsQuote(string? status)
    => string.Equals(NormalizeOrDefault(status, string.Empty), Cotizacion, StringComparison.OrdinalIgnoreCase);
}
