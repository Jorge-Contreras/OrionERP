using System.Data;
using OrionERP.Application.Features.CuentasPorPagar.Recurrentes;
using OrionERP.Infrastructure.Features.CuentasPorPagar.Recurrentes;
using OrionERP.UnitTests.Common;

namespace OrionERP.UnitTests.CuentasPorPagar;

public class RecurrentApServiceTests
{
  [Fact]
  public async Task SearchTransactionsAsync_FiltersBySelectedRfc()
  {
    var connection = new FakeQueryDbConnection
    {
      ReaderResultFactory = (_, _) => CreateTransactionCandidateTable()
    };
    var service = new RecurrentApService(new FakeQueryConnectionFactory(connection));

    _ = await service.SearchTransactionsAsync("ohm191112q26", "luz");

    Assert.Contains("WHERE t.RFC = @Rfc", connection.LastCommandText, StringComparison.Ordinal);
    AssertParameter(connection.LastParameters, "@Rfc", "OHM191112Q26");
    AssertParameter(connection.LastParameters, "@Search", "%luz%");
  }

  [Fact]
  public async Task LinkTransactionAsync_RejectsTransactionFromDifferentRfc()
  {
    var connection = new FakeQueryDbConnection
    {
      ReaderResultFactory = (commandText, _) =>
      {
        if (commandText.Contains("FROM AP.PayableOccurrence", StringComparison.Ordinal))
        {
          return CreateOccurrenceStateTable("OHM191112Q26", 100m);
        }

        if (commandText.Contains("FROM dbo.Transacciones", StringComparison.Ordinal))
        {
          return CreateTransactionStateTable("ABC010101ABC", 100m);
        }

        return new DataTable();
      },
      NonQueryResultFactory = (_, _) => 1
    };
    var service = new RecurrentApService(new FakeQueryConnectionFactory(connection));

    var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.LinkTransactionAsync(
      new RecurrentApTransactionLinkRequest
      {
        OccurrenceId = 10,
        Rfc = "OHM191112Q26",
        TransaccionId = 99
      },
      "Ana"));

    Assert.Contains("otro RFC", ex.Message, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain(connection.ExecutedCommands, command => command.CommandText.Contains("INSERT INTO AP.OccurrencePayment", StringComparison.Ordinal));
    Assert.True(connection.LastTransaction?.WasRolledBack);
  }

  [Fact]
  public void IdentitySeeder_IncludesApRoles()
  {
    var source = File.ReadAllText(Path.GetFullPath(Path.Combine(
      AppContext.BaseDirectory,
      "..",
      "..",
      "..",
      "..",
      "..",
      "src",
      "OrionERP.Web",
      "Identity",
      "IdentitySeeder.cs")));

    Assert.Contains("\"APAdmin\"", source, StringComparison.Ordinal);
    Assert.Contains("\"APOperator\"", source, StringComparison.Ordinal);
    Assert.Contains("\"APReadOnly\"", source, StringComparison.Ordinal);
  }

  private static DataTable CreateTransactionCandidateTable()
  {
    var table = new DataTable();
    table.Columns.Add("Id", typeof(int));
    table.Columns.Add("Fecha", typeof(DateTime));
    table.Columns.Add("Concepto", typeof(string));
    table.Columns.Add("Monto", typeof(decimal));
    table.Columns.Add("TipoPoliza", typeof(string));
    table.Columns.Add("FormaPago", typeof(string));
    table.Columns.Add("IsLinkedToAp", typeof(bool));
    table.Rows.Add(99, new DateTime(2026, 5, 1), "Pago luz", 100m, "EGRESO", "03", false);
    return table;
  }

  private static DataTable CreateOccurrenceStateTable(string rfc, decimal expectedAmount)
  {
    var table = new DataTable();
    table.Columns.Add("Id", typeof(int));
    table.Columns.Add("Rfc", typeof(string));
    table.Columns.Add("ExpectedAmount", typeof(decimal));
    table.Columns.Add("Status", typeof(string));
    table.Rows.Add(10, rfc, expectedAmount, RecurrentApStatuses.Pending);
    return table;
  }

  private static DataTable CreateTransactionStateTable(string rfc, decimal amount)
  {
    var table = new DataTable();
    table.Columns.Add("Id", typeof(int));
    table.Columns.Add("Rfc", typeof(string));
    table.Columns.Add("Fecha", typeof(DateTime));
    table.Columns.Add("Amount", typeof(decimal));
    table.Rows.Add(99, rfc, new DateTime(2026, 5, 1), amount);
    return table;
  }

  private static void AssertParameter(IEnumerable<FakeQueryParameter> parameters, string name, object? expectedValue)
  {
    var normalizedName = name.TrimStart('@');
    var parameter = Assert.Single(parameters, item => string.Equals(item.Name.TrimStart('@'), normalizedName, StringComparison.OrdinalIgnoreCase));
    Assert.Equal(expectedValue, parameter.Value);
  }
}
