using System;
using System.Collections.Generic;
using System.Linq;

namespace OrionERP.Application.Features.Reservaciones.ListaReservaciones;

public static class AirbnbReservationDefaults
{
  public const decimal CleaningFee = 200m;
  public const decimal IvaRate = 0.16m;
  public const decimal IvaRetentionRate = 0.08m;
  public const decimal IsrRetentionRate = 0.025m;
  public const decimal HostServiceFeeRate = 0.03m;
  public const decimal HostServiceFeeIvaRate = 0.16m;
}

public class AirbnbReservationBreakdownInput
{
  public decimal PayoutAmount { get; set; }
  public decimal CleaningFee { get; set; } = AirbnbReservationDefaults.CleaningFee;
  public decimal IvaRate { get; set; } = AirbnbReservationDefaults.IvaRate;
  public decimal IvaRetentionRate { get; set; } = AirbnbReservationDefaults.IvaRetentionRate;
  public decimal IsrRetentionRate { get; set; } = AirbnbReservationDefaults.IsrRetentionRate;
  public decimal HostServiceFeeRate { get; set; } = AirbnbReservationDefaults.HostServiceFeeRate;
  public decimal HostServiceFeeIvaRate { get; set; } = AirbnbReservationDefaults.HostServiceFeeIvaRate;
}

public sealed class AirbnbReservationBreakdownApplyRequest : AirbnbReservationBreakdownInput
{
  public int ReservationId { get; set; }
  public IReadOnlyCollection<int> RoomCalendarIds { get; set; } = Array.Empty<int>();
}

public sealed class AirbnbReservationBreakdownDto
{
  public int ReservationId { get; set; }
  public decimal PayoutAmount { get; set; }
  public decimal TaxableBase { get; set; }
  public decimal RoomRateAmount { get; set; }
  public decimal CleaningFee { get; set; }
  public decimal IvaTransferredAmount { get; set; }
  public decimal IvaRetainedAmount { get; set; }
  public decimal IsrRetainedAmount { get; set; }
  public decimal HostServiceFeeBaseAmount { get; set; }
  public decimal HostServiceFeeIvaAmount { get; set; }
  public decimal HostServiceFeeTotalAmount { get; set; }
  public decimal GrossCfdiTotal { get; set; }
  public decimal DebitTotal { get; set; }
  public decimal CreditTotal { get; set; }
  public decimal BalanceDifference { get; set; }
  public decimal IvaRate { get; set; }
  public decimal IvaRetentionRate { get; set; }
  public decimal IsrRetentionRate { get; set; }
  public decimal HostServiceFeeRate { get; set; }
  public decimal HostServiceFeeIvaRate { get; set; }
  public DateTime? CreatedAtUtc { get; set; }
  public DateTime? UpdatedAtUtc { get; set; }

  public bool IsBalanced => Math.Abs(BalanceDifference) < 0.005m;
}

public static class AirbnbReservationBreakdownCalculator
{
  public static AirbnbReservationBreakdownDto Calculate(AirbnbReservationBreakdownInput input)
  {
    ArgumentNullException.ThrowIfNull(input);

    if (input.PayoutAmount <= 0m)
    {
      throw new ArgumentOutOfRangeException(nameof(input), "Airbnb payout must be greater than zero.");
    }

    ValidateRate(input.IvaRate, nameof(input.IvaRate));
    ValidateRate(input.IvaRetentionRate, nameof(input.IvaRetentionRate));
    ValidateRate(input.IsrRetentionRate, nameof(input.IsrRetentionRate));
    ValidateRate(input.HostServiceFeeRate, nameof(input.HostServiceFeeRate));
    ValidateRate(input.HostServiceFeeIvaRate, nameof(input.HostServiceFeeIvaRate));

    if (input.CleaningFee < 0m)
    {
      throw new ArgumentOutOfRangeException(nameof(input), "Cleaning fee cannot be negative.");
    }

    var payout = RoundCurrency(input.PayoutAmount);
    var coefficient = 1m
        + input.IvaRate
        - input.IvaRetentionRate
        - input.IsrRetentionRate
        - (input.HostServiceFeeRate * (1m + input.HostServiceFeeIvaRate));

    if (coefficient <= 0m)
    {
      throw new InvalidOperationException("Airbnb rates produce an invalid payout coefficient.");
    }

    var taxableBase = RoundCurrency(payout / coefficient);
    if (taxableBase <= 0m)
    {
      throw new InvalidOperationException("Airbnb taxable base must be greater than zero.");
    }

    var cleaningFee = RoundCurrency(input.CleaningFee);
    var roomRateAmount = RoundCurrency(taxableBase - cleaningFee);
    if (roomRateAmount < 0m)
    {
      throw new InvalidOperationException("Cleaning fee cannot exceed the Airbnb taxable base.");
    }

    var ivaTransferred = RoundCurrency(taxableBase * input.IvaRate);
    var ivaRetained = RoundCurrency(taxableBase * input.IvaRetentionRate);
    var isrRetained = RoundCurrency(taxableBase * input.IsrRetentionRate);
    var grossCfdiTotal = RoundCurrency(taxableBase + ivaTransferred);

    // Make the service fee the balancing remainder so the stored payout is always authoritative.
    var hostServiceFeeTotal = RoundCurrency(grossCfdiTotal - payout - ivaRetained - isrRetained);
    if (hostServiceFeeTotal < 0m)
    {
      throw new InvalidOperationException("Airbnb payout exceeds the calculated gross amount after retentions.");
    }

    var hostServiceFeeBase = input.HostServiceFeeIvaRate <= 0m
        ? hostServiceFeeTotal
        : RoundCurrency(hostServiceFeeTotal / (1m + input.HostServiceFeeIvaRate));
    var hostServiceFeeIva = RoundCurrency(hostServiceFeeTotal - hostServiceFeeBase);
    var debitTotal = RoundCurrency(payout + ivaRetained + isrRetained + hostServiceFeeTotal);
    var creditTotal = RoundCurrency(taxableBase + ivaTransferred);

    return new AirbnbReservationBreakdownDto
    {
      PayoutAmount = payout,
      TaxableBase = taxableBase,
      RoomRateAmount = roomRateAmount,
      CleaningFee = cleaningFee,
      IvaTransferredAmount = ivaTransferred,
      IvaRetainedAmount = ivaRetained,
      IsrRetainedAmount = isrRetained,
      HostServiceFeeBaseAmount = hostServiceFeeBase,
      HostServiceFeeIvaAmount = hostServiceFeeIva,
      HostServiceFeeTotalAmount = hostServiceFeeTotal,
      GrossCfdiTotal = grossCfdiTotal,
      DebitTotal = debitTotal,
      CreditTotal = creditTotal,
      BalanceDifference = RoundCurrency(debitTotal - creditTotal),
      IvaRate = input.IvaRate,
      IvaRetentionRate = input.IvaRetentionRate,
      IsrRetentionRate = input.IsrRetentionRate,
      HostServiceFeeRate = input.HostServiceFeeRate,
      HostServiceFeeIvaRate = input.HostServiceFeeIvaRate
    };
  }

  public static decimal[] SplitCurrency(decimal total, int count)
  {
    if (count <= 0)
    {
      return Array.Empty<decimal>();
    }

    var totalCents = (long)decimal.Round(total * 100m, 0, MidpointRounding.ToEven);
    var baseCents = totalCents / count;
    var remainder = totalCents % count;
    var amounts = new decimal[count];

    for (var i = 0; i < count; i++)
    {
      amounts[i] = (baseCents + (i < remainder ? 1 : 0)) / 100m;
    }

    return amounts;
  }

  public static bool UsesDefaultRates(AirbnbReservationBreakdownInput input)
  {
    ArgumentNullException.ThrowIfNull(input);

    var rates = new[]
    {
      (input.CleaningFee, AirbnbReservationDefaults.CleaningFee),
      (input.IvaRate, AirbnbReservationDefaults.IvaRate),
      (input.IvaRetentionRate, AirbnbReservationDefaults.IvaRetentionRate),
      (input.IsrRetentionRate, AirbnbReservationDefaults.IsrRetentionRate),
      (input.HostServiceFeeRate, AirbnbReservationDefaults.HostServiceFeeRate),
      (input.HostServiceFeeIvaRate, AirbnbReservationDefaults.HostServiceFeeIvaRate)
    };

    return rates.All(rate => Math.Abs(rate.Item1 - rate.Item2) < 0.000001m);
  }

  private static void ValidateRate(decimal rate, string parameterName)
  {
    if (rate < 0m || rate >= 1m)
    {
      throw new ArgumentOutOfRangeException(parameterName, "Airbnb rates must be greater than or equal to zero and less than one.");
    }
  }

  private static decimal RoundCurrency(decimal value)
    => decimal.Round(value, 2, MidpointRounding.ToEven);
}
