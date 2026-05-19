using OrionERP.Application.Features.CuentasPorPagar.Recurrentes;

namespace OrionERP.UnitTests.CuentasPorPagar;

public class RecurrentApOccurrenceGeneratorTests
{
  [Fact]
  public void Generate_ClampsMonthlyDueDay_ToLastDayOfMonth()
  {
    var rows = RecurrentApOccurrenceGenerator.Generate(
      new DateTime(2026, 1, 31),
      endDate: null,
      RecurrentApFrequencyUnits.Months,
      intervalCount: 1,
      dueDayOfMonth: 31,
      dueMonth: null,
      expectedAmount: 100m,
      throughDate: new DateTime(2026, 3, 31));

    Assert.Equal(
      [new DateTime(2026, 1, 31), new DateTime(2026, 2, 28), new DateTime(2026, 3, 31)],
      rows.Select(row => row.DueDate).ToArray());
    Assert.All(rows, row => Assert.Equal(100m, row.ExpectedAmount));
  }

  [Fact]
  public void Generate_UsesCustomMonthInterval()
  {
    var rows = RecurrentApOccurrenceGenerator.Generate(
      new DateTime(2026, 1, 15),
      endDate: null,
      RecurrentApFrequencyUnits.Months,
      intervalCount: 2,
      dueDayOfMonth: 20,
      dueMonth: null,
      expectedAmount: null,
      throughDate: new DateTime(2026, 7, 31));

    Assert.Equal(
      [new DateTime(2026, 1, 20), new DateTime(2026, 3, 20), new DateTime(2026, 5, 20), new DateTime(2026, 7, 20)],
      rows.Select(row => row.DueDate).ToArray());
  }

  [Fact]
  public void Generate_StopsAtEndDate_BeforeRollingWindow()
  {
    var rows = RecurrentApOccurrenceGenerator.Generate(
      new DateTime(2026, 1, 1),
      new DateTime(2026, 1, 22),
      RecurrentApFrequencyUnits.Weeks,
      intervalCount: 1,
      dueDayOfMonth: null,
      dueMonth: null,
      expectedAmount: null,
      throughDate: new DateTime(2027, 7, 1));

    Assert.Equal(
      [new DateTime(2026, 1, 1), new DateTime(2026, 1, 8), new DateTime(2026, 1, 15), new DateTime(2026, 1, 22)],
      rows.Select(row => row.DueDate).ToArray());
  }

  [Fact]
  public void Generate_SupportsAnnualDueMonthAndDay()
  {
    var rows = RecurrentApOccurrenceGenerator.Generate(
      new DateTime(2026, 1, 1),
      endDate: null,
      RecurrentApFrequencyUnits.Years,
      intervalCount: 1,
      dueDayOfMonth: 17,
      dueMonth: 3,
      expectedAmount: null,
      throughDate: new DateTime(2028, 12, 31));

    Assert.Equal(
      [new DateTime(2026, 3, 17), new DateTime(2027, 3, 17), new DateTime(2028, 3, 17)],
      rows.Select(row => row.DueDate).ToArray());
  }
}
