using System;

namespace OrionERP.Application.Features.Reservaciones.ListaReservaciones;

public static class ReservacionTotalsCalculator
{
  public static ReservacionTotalsBreakdown Calculate(
    DateTime? checkIn,
    DateTime? checkOut,
    bool taxable,
    decimal totalSuites,
    decimal totalExtras,
    decimal totalPagado)
  {
    var roundedSuites = decimal.Round(totalSuites, 2, MidpointRounding.ToEven);
    var roundedExtras = decimal.Round(totalExtras, 2, MidpointRounding.ToEven);
    var subtotal = decimal.Round(roundedSuites + roundedExtras, 2, MidpointRounding.ToEven);

    var tax = 0m;
    var ish = 0m;
    if (taxable)
    {
      tax = decimal.Round(subtotal * 0.16m, 2, MidpointRounding.ToEven);
      if (checkIn.HasValue && checkIn.Value.Year < 2025)
      {
        ish = decimal.Round(subtotal * 0.02m, 2, MidpointRounding.ToEven);
      }
    }

    var totalReservacion = decimal.Round(subtotal + tax + ish, 2, MidpointRounding.ToEven);
    var pagado = decimal.Round(totalPagado, 2, MidpointRounding.ToEven);
    var porPagar = decimal.Round(totalReservacion - pagado, 2, MidpointRounding.ToEven);

    var numNoches = 0;
    if (checkIn.HasValue && checkOut.HasValue)
    {
      var inDate = checkIn.Value.Date;
      var outDate = checkOut.Value.Date;
      if (outDate >= inDate)
      {
        numNoches = (int)(outDate - inDate).TotalDays;
      }
    }

    return new ReservacionTotalsBreakdown(
      roundedSuites,
      roundedExtras,
      subtotal,
      tax,
      ish,
      totalReservacion,
      pagado,
      porPagar,
      numNoches);
  }
}
