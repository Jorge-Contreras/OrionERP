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

    var tax = RoundCurrency(subtotal * TaxRate);
    var ish = 0m;
    if (checkIn.HasValue && checkIn.Value.Year < 2025)
    {
      ish = RoundCurrency(subtotal * IshRate);
    }

    return BuildBreakdown(
      checkIn,
      checkOut,
      roundedSuites,
      activeDiscountPercent,
      suiteDiscountAmount,
      roundedExtras,
      0m,
      subtotal,
      tax,
      ish,
      totalPagado);
  }

  public static ReservacionTotalsBreakdown Calculate(
    DateTime? checkIn,
    DateTime? checkOut,
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

    var tax = SumRoundedTax(taxableLines, TaxRate);
    var ish = 0m;
    if (checkIn.HasValue && checkIn.Value.Year < 2025)
    {
      ish = SumRoundedTax(taxableLines, IshRate);
    }

    return BuildBreakdown(
      checkIn,
      checkOut,
      roundedSuites,
      activeDiscountPercent,
      suiteDiscountAmount,
      roundedExtras,
      0m,
      subtotal,
      tax,
      ish,
      totalPagado);
  }

  public static ReservacionTotalsBreakdown Calculate(
    DateTime? checkIn,
    DateTime? checkOut,
    IEnumerable<decimal> suiteLineTotals,
    IEnumerable<decimal> extraLineTotals,
    IEnumerable<ReservationChargeLine> experienceLineTotals,
    decimal totalPagado,
    decimal suiteDiscountPercent = 0m)
  {
    ArgumentNullException.ThrowIfNull(extraLineTotals);

    return Calculate(
      checkIn,
      checkOut,
      suiteLineTotals,
      extraLineTotals.Select(line => new ReservationChargeLine(line)),
      experienceLineTotals,
      totalPagado,
      suiteDiscountPercent);
  }

  public static ReservacionTotalsBreakdown Calculate(
    DateTime? checkIn,
    DateTime? checkOut,
    IEnumerable<decimal> suiteLineTotals,
    IEnumerable<ReservationChargeLine> extraLineTotals,
    IEnumerable<ReservationChargeLine> experienceLineTotals,
    decimal totalPagado,
    decimal suiteDiscountPercent = 0m)
  {
    ArgumentNullException.ThrowIfNull(suiteLineTotals);
    ArgumentNullException.ThrowIfNull(extraLineTotals);
    ArgumentNullException.ThrowIfNull(experienceLineTotals);

    var activeDiscountPercent = NormalizeSuiteDiscountPercent(suiteDiscountPercent);
    var roundedSuiteLines = suiteLineTotals.Select(RoundCurrency).ToArray();
    var roundedExtraLines = extraLineTotals
      .Select(line => new ReservationChargeLine(RoundCurrency(line.Amount), line.TaxMode))
      .ToArray();
    var roundedExperienceLines = experienceLineTotals
      .Select(line => new ReservationChargeLine(RoundCurrency(line.Amount), line.TaxMode))
      .ToArray();

    var discountedSuiteLines = roundedSuiteLines
      .Select(line => RoundCurrency(line - RoundCurrency(line * (activeDiscountPercent / 100m))))
      .ToArray();

    var taxableIncludedExtraSubtotals = roundedExtraLines
      .Where(line => line.TaxMode == ReservationChargeTaxMode.TaxIncluded)
      .Select(line => RoundCurrency(line.Amount / (1m + TaxRate)))
      .ToArray();

    var nonTaxableExtraLines = roundedExtraLines
      .Where(line => line.TaxMode == ReservationChargeTaxMode.NonTaxable)
      .Select(line => line.Amount)
      .ToArray();

    var taxableExtraLines = roundedExtraLines
      .Where(line => line.TaxMode == ReservationChargeTaxMode.TaxableExclusive)
      .Select(line => line.Amount)
      .ToArray();

    var taxableExclusiveLines = discountedSuiteLines
      .Concat(taxableExtraLines)
      .ToArray();

    var taxableIncludedSubtotals = roundedExperienceLines
      .Where(line => line.TaxMode == ReservationChargeTaxMode.TaxIncluded)
      .Select(line => RoundCurrency(line.Amount / (1m + TaxRate)))
      .ToArray();

    var nonTaxableExperienceLines = roundedExperienceLines
      .Where(line => line.TaxMode == ReservationChargeTaxMode.NonTaxable)
      .Select(line => line.Amount)
      .ToArray();

    var taxableExperienceLines = roundedExperienceLines
      .Where(line => line.TaxMode == ReservationChargeTaxMode.TaxableExclusive)
      .Select(line => line.Amount)
      .ToArray();

    var roundedSuites = RoundCurrency(roundedSuiteLines.Sum());
    var discountedSuites = RoundCurrency(discountedSuiteLines.Sum());
    var suiteDiscountAmount = RoundCurrency(roundedSuites - discountedSuites);
    var roundedExtras = RoundCurrency(taxableExtraLines.Sum() + taxableIncludedExtraSubtotals.Sum() + nonTaxableExtraLines.Sum());
    var roundedExperiences = RoundCurrency(roundedExperienceLines.Sum(line => line.Amount));
    var subtotal = RoundCurrency(
      discountedSuites
      + taxableExtraLines.Sum()
      + taxableIncludedExtraSubtotals.Sum()
      + nonTaxableExtraLines.Sum()
      + taxableIncludedSubtotals.Sum()
      + taxableExperienceLines.Sum()
      + nonTaxableExperienceLines.Sum());

    var tax = SumRoundedTax(taxableExclusiveLines.Concat(taxableExperienceLines), TaxRate)
      + RoundCurrency(roundedExtraLines
        .Where(line => line.TaxMode == ReservationChargeTaxMode.TaxIncluded)
        .Sum(line => line.Amount)
        - taxableIncludedExtraSubtotals.Sum())
      + RoundCurrency(roundedExperienceLines
        .Where(line => line.TaxMode == ReservationChargeTaxMode.TaxIncluded)
        .Sum(line => line.Amount)
        - taxableIncludedSubtotals.Sum());
    tax = RoundCurrency(tax);

    var ish = 0m;
    if (checkIn.HasValue && checkIn.Value.Year < 2025)
    {
      ish = SumRoundedTax(taxableExclusiveLines.Concat(taxableExperienceLines), IshRate);
    }

    return BuildBreakdown(
      checkIn,
      checkOut,
      roundedSuites,
      activeDiscountPercent,
      suiteDiscountAmount,
      roundedExtras,
      roundedExperiences,
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
    decimal roundedExperiences,
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
      roundedExperiences,
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

public sealed record ReservationChargeLine(
  decimal Amount,
  ReservationChargeTaxMode TaxMode = ReservationChargeTaxMode.TaxableExclusive);

public enum ReservationChargeTaxMode
{
  TaxableExclusive,
  TaxIncluded,
  NonTaxable
}
