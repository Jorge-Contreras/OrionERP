using System;
using System.Collections.Generic;
using System.Data;
using OrionERP.Infrastructure.Features.Contabilidad.Bancos;
using OrionERP.UnitTests.Common;

namespace OrionERP.UnitTests.Contabilidad.Bancos;

public class BancosServiceTests
{
  [Fact]
  public async Task CreateAutoPoliciesAsync_UsesCandidateCountToIterateWholeBatch()
  {
    var connection = new FakeQueryDbConnection
    {
      ReaderResultFactory = static (_, _) =>
      {
        var table = new DataTable();
        table.Columns.Add("Processed", typeof(int));
        table.Rows.Add(3);
        return table;
      }
    };

    var service = new BancosService(new FakeQueryConnectionFactory(connection));

    var processed = await service.CreateAutoPoliciesAsync("RFC123456789", 2026, 4, 18);

    Assert.Equal(3, processed);

    var command = Assert.Single(connection.ExecutedCommands);
    Assert.Contains("SELECT @MaxRow = COUNT(*)", command.CommandText, StringComparison.Ordinal);
    Assert.DoesNotContain("DECLARE @MaxRow int = @@ROWCOUNT;", command.CommandText, StringComparison.Ordinal);

    AssertParameter(command.Parameters, "@Rfc", "RFC123456789");
    AssertParameter(command.Parameters, "@StartDate", new DateTime(2026, 4, 1));
    AssertParameter(command.Parameters, "@EndDate", new DateTime(2026, 5, 1));
    AssertParameter(command.Parameters, "@AccountId", 18);
  }

  private static void AssertParameter(IReadOnlyList<FakeQueryParameter> parameters, string name, object? expectedValue)
  {
    var normalizedName = name.TrimStart('@');
    var parameter = Assert.Single(parameters, item => string.Equals(item.Name.TrimStart('@'), normalizedName, StringComparison.OrdinalIgnoreCase));
    Assert.Equal(expectedValue, parameter.Value);
  }
}
