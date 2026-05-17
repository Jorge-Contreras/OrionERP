using System;
using System.Collections.Generic;
using System.Linq;

namespace OrionERP.Application.Features.Reservaciones.ListaReservaciones;

public static class ReservacionTotalsCalculator
{
  private const decimal TaxRate = 0.16m;
  private const decimal IshRate = 0.02m;

  public static ReservacionTotalsBreakdown Calculate(
    DateTime? checkIn,
    DateTime? checkOut,
    bool taxable,
    decimal totalSuites,
    decimal totalExtras,
    decimal totalPagado,
    decimal suiteDiscountPercent = 0m)
  {
    var activeDiscountPercent = NormalizeSuiteDiscountPercent(suiteDiscountPercent);
    var roundedSuites = RoundCurrency(totalSuites);
    var roundedExtras = RoundCurrency(totalExtras);
    var suiteDiscountAmount = RoundCurrency(roundedSuites * (activeDiscountPercent / 100m));
    var discountedSuites = RoundCurrency(roundedSuites - suiteDiscountAmount);
    var subtotal = RoundCurrency(discountedSuites + roundedExtras);

    var tax = 0m;
    var ish = 0m;
    if (taxable)
    {
      tax = RoundCurrency(subtotal * TaxRate);
      if (checkIn.HasValue && checkIn.Value.Year < 2025)
      {
        ish = RoundCurrency(subtotal * IshRate);
      }
    }

    return BuildBreakdown(
      checkIn,
      checkOut,
      roundedSuites,
      activeDiscountPercent,
      suiteDiscountAmount,
      roundedExtras,
      subtotal,
      tax,
      ish,
      totalPagado);
  }

  public static ReservacionTotalsBreakdown Calculate(
    DateTime? checkIn,
    DateTime? checkOut,
    bool taxable,
    IEnumerable<decimal> suiteLineTotals,
    IEnumerable<decimal> extraLineTotals,
    decimal totalPagado,
    decimal suiteDiscountPercent = 0m)
  {
    ArgumentNullException.ThrowIfNull(suiteLineTotals);
    ArgumentNullException.ThrowIfNull(extraLineTotals);

    var activeDiscountPercent = NormalizeSuiteDiscountPercent(suiteDiscountPercent);
    var roundedSuiteLines = suiteLineTotals.Select(RoundCurrency).ToArray();
    var roundedExtraLines = extraLineTotals.Select(RoundCurrency).ToArray();
    var discountedSuiteLines = roundedSuiteLines
      .Select(line => RoundCurrency(line - RoundCurrency(line * (activeDiscountPercent / 100m))))
      .ToArray();
    var taxableLines = discountedSuiteLines.Concat(roundedExtraLines).ToArray();

    var roundedSuites = RoundCurrency(roundedSuiteLines.Sum());
    var discountedSuites = RoundCurrency(discountedSuiteLines.Sum());
    var suiteDiscountAmount = RoundCurrency(roundedSuites - discountedSuites);
    var roundedExtras = RoundCurrency(roundedExtraLines.Sum());
    var subtotal = RoundCurrency(discountedSuites + roundedExtras);

    var tax = 0m;
    var ish = 0m;
    if (taxable)
    {
      tax = SumRoundedTax(taxableLines, TaxRate);
      if (checkIn.HasValue && checkIn.Value.Year < 2025)
      {
        ish = SumRoundedTax(taxableLines, IshRate);
      }
    }

    return BuildBreakdown(
      checkIn,
      checkOut,
      roundedSuites,
      activeDiscountPercent,
      suiteDiscountAmount,
      roundedExtras,
      subtotal,
      tax,
      ish,
      totalPagado);
  }

  private static ReservacionTotalsBreakdown BuildBreakdown(
    DateTime? checkIn,
    DateTime? checkOut,
    decimal roundedSuites,
    decimal suiteDiscountPercent,
    decimal suiteDiscountAmount,
    decimal roundedExtras,
    decimal subtotal,
    decimal tax,
    decimal ish,
    decimal totalPagado)
  {
    var totalReservacion = RoundCurrency(subtotal + tax + ish);
    var pagado = RoundCurrency(totalPagado);
    var porPagar = RoundCurrency(totalReservacion - pagado);

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
      suiteDiscountPercent,
      suiteDiscountAmount,
      roundedExtras,
      subtotal,
      tax,
      ish,
      totalReservacion,
      pagado,
      porPagar,
      numNoches);
  }

  private static decimal SumRoundedTax(IEnumerable<decimal> lineTotals, decimal rate)
    => RoundCurrency(lineTotals.Sum(line => RoundCurrency(line * rate)));

  public static decimal NormalizeSuiteDiscountPercent(decimal value)
  {
    if (value > 100m)
    {
      throw new ArgumentOutOfRangeException(nameof(value), "Suite discount percent must be less than or equal to 100.");
    }

    return value > 1m ? RoundCurrency(value) : 0m;
  }

  private static decimal RoundCurrency(decimal value)
    => decimal.Round(value, 2, MidpointRounding.ToEven);
}
