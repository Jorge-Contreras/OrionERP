using System;
using System.Collections.Generic;
using System.Data;
using OrionERP.Infrastructure.Features.Contabilidad.Bancos;
using OrionERP.UnitTests.Common;

namespace OrionERP.UnitTests.Contabilidad.Bancos;

public class BancosServiceTests
{
  [Fact]
  public async Task GetMovementsAsync_MapsAccountingAuditFields()
  {
    var connection = new FakeQueryDbConnection
    {
      ReaderResultFactory = static (_, _) =>
      {
        var table = new DataTable();
        table.Columns.Add("MovimientoId", typeof(long));
        table.Columns.Add("Dia", typeof(DateTime));
        table.Columns.Add("Line", typeof(int));
        table.Columns.Add("Concepto", typeof(string));
        table.Columns.Add("Tipo", typeof(string));
        table.Columns.Add("Cargo", typeof(decimal));
        table.Columns.Add("Abono", typeof(decimal));
        table.Columns.Add("Saldo", typeof(decimal));
        table.Columns.Add("FechaCarga", typeof(DateTime));
        table.Columns.Add("NombreBanco", typeof(string));
        table.Columns.Add("NumeroCuenta", typeof(string));
        table.Columns.Add("SecuenciaClave", typeof(long));
        table.Columns.Add("Policy", typeof(int));
        table.Columns.Add("Issues", typeof(string));
        table.Columns.Add("PolicyDate", typeof(DateTime));
        table.Columns.Add("OrdenBalance", typeof(long));
        table.Columns.Add("AccountingSequence", typeof(int));
        table.Columns.Add("BankAccountNivel1", typeof(string));
        table.Columns.Add("BankAccountNivel2", typeof(string));
        table.Columns.Add("BankAccountNivel3", typeof(string));
        table.Columns.Add("BankRegistroLineCount", typeof(int));
        table.Columns.Add("BankRegistroDebe", typeof(decimal));
        table.Columns.Add("BankRegistroHaber", typeof(decimal));
        table.Columns.Add("AccountingRunningBalance", typeof(decimal));
        table.Columns.Add("BankAccountingVariance", typeof(decimal));
        table.Columns.Add("HasBankAccountingDifference", typeof(bool));
        table.Columns.Add("BalanceOk", typeof(bool));
        table.Columns.Add("AuditSeverity", typeof(string));

        table.Rows.Add(
          1001L,
          new DateTime(2026, 5, 3),
          2,
          "Pago proveedor",
          "E",
          0m,
          350m,
          1250m,
          new DateTime(2026, 5, 4, 10, 30, 0),
          "BBVA",
          "1234",
          202605030002L,
          9001,
          "OK",
          new DateTime(2026, 5, 3, 0, 0, 0),
          8811L,
          7,
          "102",
          "01",
          "00",
          1,
          0m,
          350m,
          1250m,
          0m,
          false,
          true,
          "OK");

        return table;
      }
    };

    var service = new BancosService(new FakeQueryConnectionFactory(connection));

    var rows = await service.GetMovementsAsync("RFC123456789", 18, 2026, 5, null);

    var movement = Assert.Single(rows);
    Assert.Equal(9001, movement.Policy);
    Assert.Equal(new DateTime(2026, 5, 3), movement.PolicyDate);
    Assert.Equal(8811L, movement.OrdenBalance);
    Assert.Equal(7, movement.AccountingSequence);
    Assert.Equal("102", movement.BankAccountNivel1);
    Assert.Equal("01", movement.BankAccountNivel2);
    Assert.Equal("00", movement.BankAccountNivel3);
    Assert.Equal(1, movement.BankRegistroLineCount);
    Assert.Equal(0m, movement.BankRegistroDebe);
    Assert.Equal(350m, movement.BankRegistroHaber);
    Assert.Equal(1250m, movement.AccountingRunningBalance);
    Assert.Equal(0m, movement.BankAccountingVariance);
    Assert.False(movement.HasBankAccountingDifference);
    Assert.True(movement.BalanceOk);
    Assert.Equal("OK", movement.AuditSeverity);

    var command = Assert.Single(connection.ExecutedCommands);
    Assert.Contains("bancos.sp_Movimientos_Bancarios", command.CommandText, StringComparison.Ordinal);
    AssertParameter(command.Parameters, "@Rfc", "RFC123456789");
    AssertParameter(command.Parameters, "@AccountId", 18);
    AssertParameter(command.Parameters, "@Year", 2026);
    AssertParameter(command.Parameters, "@Month", 5);
  }

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

  [Fact]
  public async Task AlignTransactionsToBankMovementsAsync_UpdatesTransactionDateAndOrderFromBankMovement()
  {
    var connection = new FakeQueryDbConnection
    {
      ReaderResultFactory = static (_, _) =>
      {
        var table = new DataTable();
        table.Columns.Add("Aligned", typeof(int));
        table.Rows.Add(4);
        return table;
      }
    };

    var service = new BancosService(new FakeQueryConnectionFactory(connection));

    var aligned = await service.AlignTransactionsToBankMovementsAsync("RFC123456789", 2026, 5, 18);

    Assert.Equal(4, aligned);

    var command = Assert.Single(connection.ExecutedCommands);
    Assert.Contains("T.Fecha = CAST(A.BankDate AS datetime)", command.CommandText, StringComparison.Ordinal);
    Assert.Contains("T.OrdenBalance = A.BankOrdenBalance", command.CommandText, StringComparison.Ordinal);
    Assert.Contains("M.Secuencia_Clave", command.CommandText, StringComparison.Ordinal);
    Assert.Contains("M.Cuenta_Banco_ID = @AccountId", command.CommandText, StringComparison.Ordinal);
    Assert.Contains("M.Transaccion_ID IS NOT NULL", command.CommandText, StringComparison.Ordinal);

    AssertParameter(command.Parameters, "@Rfc", "RFC123456789");
    AssertParameter(command.Parameters, "@StartDate", new DateTime(2026, 5, 1));
    AssertParameter(command.Parameters, "@EndDate", new DateTime(2026, 6, 1));
    AssertParameter(command.Parameters, "@AccountId", 18);
  }

  private static void AssertParameter(IReadOnlyList<FakeQueryParameter> parameters, string name, object? expectedValue)
  {
    var normalizedName = name.TrimStart('@');
    var parameter = Assert.Single(parameters, item => string.Equals(item.Name.TrimStart('@'), normalizedName, StringComparison.OrdinalIgnoreCase));
    Assert.Equal(expectedValue, parameter.Value);
  }
}
