using System;
using System.Collections.Generic;
using System.Data;
using OrionERP.Application.Features.Contabilidad.Bancos;
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
  public async Task GetPendingTransactionsAsync_MapsMatchingBankRegistroAmounts()
  {
    var connection = new FakeQueryDbConnection
    {
      ReaderResultFactory = static (_, _) =>
      {
        var table = new DataTable();
        table.Columns.Add("TransaccionId", typeof(int));
        table.Columns.Add("Fecha", typeof(DateTime));
        table.Columns.Add("FormaPago", typeof(string));
        table.Columns.Add("Concepto", typeof(string));
        table.Columns.Add("Monto", typeof(decimal));
        table.Columns.Add("BankRegistroLineCount", typeof(int));
        table.Columns.Add("BankRegistroDebe", typeof(decimal));
        table.Columns.Add("BankRegistroHaber", typeof(decimal));

        table.Rows.Add(
          9001,
          new DateTime(2026, 5, 3),
          "Transferencia",
          "Pago cliente",
          1250m,
          2,
          0m,
          1180m);

        return table;
      }
    };

    var service = new BancosService(new FakeQueryConnectionFactory(connection));

    var rows = await service.GetPendingTransactionsAsync("RFC123456789", 18, 2026, 5);

    var pending = Assert.Single(rows);
    Assert.Equal(9001, pending.TransaccionId);
    Assert.Equal(1250m, pending.Monto);
    Assert.Equal(2, pending.BankRegistroLineCount);
    Assert.Equal(0m, pending.BankRegistroDebe);
    Assert.Equal(1180m, pending.BankRegistroHaber);

    var command = Assert.Single(connection.ExecutedCommands);
    Assert.Contains("RegistroBancoPendiente", command.CommandText, StringComparison.Ordinal);
    Assert.Contains("rb.BankRegistroDebe", command.CommandText, StringComparison.Ordinal);
    Assert.Contains("rb.BankRegistroHaber", command.CommandText, StringComparison.Ordinal);
    AssertParameter(command.Parameters, "@Rfc", "RFC123456789");
    AssertParameter(command.Parameters, "@AccountId", 18);
    AssertParameter(command.Parameters, "@StartDate", new DateTime(2026, 5, 1));
    AssertParameter(command.Parameters, "@EndDate", new DateTime(2026, 6, 1));
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
    Assert.Contains("bancos.Movimiento_Transaccion AS MT", command.CommandText, StringComparison.Ordinal);

    AssertParameter(command.Parameters, "@Rfc", "RFC123456789");
    AssertParameter(command.Parameters, "@StartDate", new DateTime(2026, 5, 1));
    AssertParameter(command.Parameters, "@EndDate", new DateTime(2026, 6, 1));
    AssertParameter(command.Parameters, "@AccountId", 18);
  }

  [Fact]
  public async Task SaveMovementLinksAsync_BlocksWhenBankAccountMappingIsMissing()
  {
    var connection = new FakeQueryDbConnection
    {
      ReaderResultFactory = static (_, _) => CreateMovementValidationContext(mappingValid: false)
    };

    var service = new BancosService(new FakeQueryConnectionFactory(connection));

    var result = await service.SaveMovementLinksAsync(new BankMovementLinkSaveRequest
    {
      MovimientoId = 1001,
      Links =
      {
        new BankMovementLinkSaveItem { TransaccionId = 9001, Debe = 350m }
      }
    });

    Assert.False(result.Success);
    Assert.Contains("Cuenta_Contable_ID", result.Message, StringComparison.Ordinal);
    Assert.True(connection.LastTransaction?.WasRolledBack);
    Assert.DoesNotContain(connection.ExecutedCommands, command => command.CommandText.Contains("DELETE FROM bancos.Movimiento_Transaccion", StringComparison.Ordinal));
  }

  [Fact]
  public async Task SaveMovementLinksAsync_RequiresExactMovementAllocation()
  {
    var connection = new FakeQueryDbConnection
    {
      ReaderResultFactory = static (_, _) => CreateMovementValidationContext(mappingValid: true, expectedDebe: 350m)
    };

    var service = new BancosService(new FakeQueryConnectionFactory(connection));

    var result = await service.SaveMovementLinksAsync(new BankMovementLinkSaveRequest
    {
      MovimientoId = 1001,
      Links =
      {
        new BankMovementLinkSaveItem { TransaccionId = 9001, Debe = 200m }
      }
    });

    Assert.False(result.Success);
    Assert.Contains("exactamente", result.Message, StringComparison.OrdinalIgnoreCase);
    Assert.True(connection.LastTransaction?.WasRolledBack);
    Assert.DoesNotContain(connection.ExecutedCommands, command => command.CommandText.Contains("FROM dbo.Transacciones AS T", StringComparison.Ordinal));
  }

  [Fact]
  public async Task SaveMovementLinksAsync_RejectsOverAllocationAgainstBankRegistroLine()
  {
    var connection = new FakeQueryDbConnection
    {
      ReaderResultFactory = static (commandText, _) =>
      {
        if (commandText.Contains("FROM dbo.Transacciones AS T", StringComparison.Ordinal))
        {
          return CreateTransactionCapacity(bankRegistroDebe: 200m);
        }

        return CreateMovementValidationContext(mappingValid: true, expectedDebe: 350m);
      }
    };

    var service = new BancosService(new FakeQueryConnectionFactory(connection));

    var result = await service.SaveMovementLinksAsync(new BankMovementLinkSaveRequest
    {
      MovimientoId = 1001,
      Links =
      {
        new BankMovementLinkSaveItem { TransaccionId = 9001, Debe = 350m }
      }
    });

    Assert.False(result.Success);
    Assert.Contains("disponible en Debe", result.Message, StringComparison.Ordinal);
    Assert.True(connection.LastTransaction?.WasRolledBack);
    Assert.DoesNotContain(connection.ExecutedCommands, command => command.CommandText.Contains("INSERT INTO bancos.Movimiento_Transaccion", StringComparison.Ordinal));
  }

  private static void AssertParameter(IReadOnlyList<FakeQueryParameter> parameters, string name, object? expectedValue)
  {
    var normalizedName = name.TrimStart('@');
    var parameter = Assert.Single(parameters, item => string.Equals(item.Name.TrimStart('@'), normalizedName, StringComparison.OrdinalIgnoreCase));
    Assert.Equal(expectedValue, parameter.Value);
  }

  private static DataTable CreateMovementValidationContext(
      bool mappingValid,
      decimal expectedDebe = 350m,
      decimal expectedHaber = 0m)
  {
    var table = new DataTable();
    table.Columns.Add("MovimientoId", typeof(long));
    table.Columns.Add("Rfc", typeof(string));
    table.Columns.Add("CuentaBancoId", typeof(int));
    table.Columns.Add("ExpectedDebe", typeof(decimal));
    table.Columns.Add("ExpectedHaber", typeof(decimal));
    table.Columns.Add("CuentaContableId", typeof(int));
    table.Columns.Add("Nivel1", typeof(string));
    table.Columns.Add("Nivel2", typeof(string));
    table.Columns.Add("Nivel3", typeof(string));
    table.Columns.Add("BankAccountDescription", typeof(string));
    table.Columns.Add("MappingValid", typeof(bool));
    table.Columns.Add("SetupIssue", typeof(string));

    table.Rows.Add(
      1001L,
      "RFC123456789",
      18,
      expectedDebe,
      expectedHaber,
      mappingValid ? 42 : DBNull.Value,
      mappingValid ? "102" : string.Empty,
      mappingValid ? "01" : string.Empty,
      mappingValid ? "00" : string.Empty,
      mappingValid ? "Banco principal" : string.Empty,
      mappingValid,
      mappingValid ? DBNull.Value : "La cuenta bancaria no tiene Cuenta_Contable_ID.");

    return table;
  }

  private static DataTable CreateTransactionCapacity(decimal bankRegistroDebe)
  {
    var table = new DataTable();
    table.Columns.Add("TransaccionId", typeof(int));
    table.Columns.Add("Rfc", typeof(string));
    table.Columns.Add("BankRegistroDebe", typeof(decimal));
    table.Columns.Add("BankRegistroHaber", typeof(decimal));
    table.Columns.Add("OtherLinkedDebe", typeof(decimal));
    table.Columns.Add("OtherLinkedHaber", typeof(decimal));

    table.Rows.Add(9001, "RFC123456789", bankRegistroDebe, 0m, 0m, 0m);
    return table;
  }
}
