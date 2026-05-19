namespace OrionERP.Application.Features.CuentasPorPagar.Recurrentes;

public sealed record RecurrentApOccurrenceSeed(DateTime PeriodStartDate, DateTime DueDate, decimal? ExpectedAmount);

public static class RecurrentApOccurrenceGenerator
{
  public static IReadOnlyList<RecurrentApOccurrenceSeed> Generate(RecurrentApPayableSummaryDto payable, DateTime throughDate)
  {
    if (payable is null)
    {
      throw new ArgumentNullException(nameof(payable));
    }

    return Generate(
      payable.StartDate,
      payable.EndDate,
      payable.FrequencyUnit,
      payable.IntervalCount,
      payable.DueDayOfMonth,
      payable.DueMonth,
      payable.ExpectedAmount,
      throughDate);
  }

  public static IReadOnlyList<RecurrentApOccurrenceSeed> Generate(
    DateTime startDate,
    DateTime? endDate,
    string frequencyUnit,
    int intervalCount,
    int? dueDayOfMonth,
    int? dueMonth,
    decimal? expectedAmount,
    DateTime throughDate)
  {
    var normalizedInterval = Math.Max(intervalCount, 1);
    var normalizedStart = startDate.Date;
    var normalizedThrough = throughDate.Date;
    var normalizedEnd = endDate?.Date;

    if (normalizedEnd.HasValue && normalizedEnd.Value < normalizedStart)
    {
      return Array.Empty<RecurrentApOccurrenceSeed>();
    }

    var maxDate = normalizedEnd.HasValue && normalizedEnd.Value < normalizedThrough
      ? normalizedEnd.Value
      : normalizedThrough;

    var seeds = new List<RecurrentApOccurrenceSeed>();
    var periodStart = normalizedStart;
    var guard = 0;

    while (periodStart <= maxDate && guard++ < 2000)
    {
      var dueDate = ResolveDueDate(periodStart, frequencyUnit, dueDayOfMonth, dueMonth);
      if (dueDate >= normalizedStart && dueDate <= maxDate)
      {
        seeds.Add(new RecurrentApOccurrenceSeed(periodStart, dueDate, expectedAmount));
      }

      var next = AddInterval(periodStart, frequencyUnit, normalizedInterval);
      if (next <= periodStart)
      {
        break;
      }

      periodStart = next;
    }

    return seeds;
  }

  private static DateTime AddInterval(DateTime date, string frequencyUnit, int intervalCount)
  {
    if (string.Equals(frequencyUnit, RecurrentApFrequencyUnits.Days, StringComparison.OrdinalIgnoreCase))
    {
      return date.AddDays(intervalCount);
    }

    if (string.Equals(frequencyUnit, RecurrentApFrequencyUnits.Weeks, StringComparison.OrdinalIgnoreCase))
    {
      return date.AddDays(intervalCount * 7);
    }

    if (string.Equals(frequencyUnit, RecurrentApFrequencyUnits.Years, StringComparison.OrdinalIgnoreCase))
    {
      return date.AddYears(intervalCount);
    }

    return date.AddMonths(intervalCount);
  }

  private static DateTime ResolveDueDate(DateTime periodStart, string frequencyUnit, int? dueDayOfMonth, int? dueMonth)
  {
    if (string.Equals(frequencyUnit, RecurrentApFrequencyUnits.Years, StringComparison.OrdinalIgnoreCase))
    {
      var month = Clamp(dueMonth ?? periodStart.Month, 1, 12);
      var day = Clamp(dueDayOfMonth ?? periodStart.Day, 1, DateTime.DaysInMonth(periodStart.Year, month));
      return new DateTime(periodStart.Year, month, day);
    }

    if (string.Equals(frequencyUnit, RecurrentApFrequencyUnits.Months, StringComparison.OrdinalIgnoreCase))
    {
      var day = Clamp(dueDayOfMonth ?? periodStart.Day, 1, DateTime.DaysInMonth(periodStart.Year, periodStart.Month));
      return new DateTime(periodStart.Year, periodStart.Month, day);
    }

    return periodStart;
  }

  private static int Clamp(int value, int min, int max)
    => Math.Min(Math.Max(value, min), max);
}
