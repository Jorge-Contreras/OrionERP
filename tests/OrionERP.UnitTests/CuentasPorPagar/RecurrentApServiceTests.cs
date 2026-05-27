using System.Data;
using System.Text;
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
  public async Task SaveAndGetPayableAsync_RoundTripsDescriptionProviderFieldsAndEncryptsPassword()
  {
    EnsureCredentialKey();
    byte[]? encryptedPassword = null;
    var connection = new FakeQueryDbConnection
    {
      NonQueryResultFactory = (_, _) => 1
    };
    var service = new RecurrentApService(new FakeQueryConnectionFactory(connection));

    await service.SavePayableAsync(
      new RecurrentApUpsertRequest
      {
        Id = 7,
        Rfc = "ohm191112q26",
        Name = "Portal internet",
        Description = "Notas de acceso",
        Website = "https://provider.example/pay",
        UserName = "portal-user",
        Password = "provider-secret",
        FrequencyUnit = RecurrentApFrequencyUnits.Months,
        IntervalCount = 1,
        StartDate = new DateTime(2026, 5, 1),
        Currency = "MXN",
        IsActive = false
      },
      "Ana");

    var update = Assert.Single(connection.ExecutedCommands, command => command.CommandText.Contains("UPDATE AP.RecurringPayable", StringComparison.Ordinal));
    AssertParameter(update.Parameters, "@Description", "Notas de acceso");
    AssertParameter(update.Parameters, "@Website", "https://provider.example/pay");
    AssertParameter(update.Parameters, "@UserName", "portal-user");
    encryptedPassword = Assert.IsType<byte[]>(GetParameterValue(update.Parameters, "@PasswordEnc"));
    Assert.True(encryptedPassword.Length > "provider-secret".Length);
    Assert.False(encryptedPassword.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes("provider-secret")));

    connection.ReaderResultFactory = (_, _) => CreatePayableTable(
      encryptedPassword,
      description: "Notas de acceso",
      website: "https://provider.example/pay",
      userName: "portal-user");

    var payable = await service.GetPayableAsync(7, "OHM191112Q26", includePassword: true);

    Assert.NotNull(payable);
    Assert.Equal("Notas de acceso", payable.Description);
    Assert.Equal("https://provider.example/pay", payable.Website);
    Assert.Equal("portal-user", payable.UserName);
    Assert.Equal("provider-secret", payable.Password);
  }

  [Fact]
  public async Task ReseedPayableOccurrencesAsync_RebuildsOnlyFutureUntouchedOccurrences()
  {
    var today = DateTime.Today;
    var connection = new FakeQueryDbConnection
    {
      ReaderResultFactory = (_, _) => CreatePayableTable(
        passwordEnc: null,
        startDate: today,
        endDate: today,
        frequencyUnit: RecurrentApFrequencyUnits.Days,
        isActive: true),
      ScalarResultFactory = (commandText, _) => commandText.Contains("SELECT COUNT(*)", StringComparison.Ordinal) ? 3 : 0,
      NonQueryResultFactory = (commandText, _) =>
      {
        if (commandText.Contains("DELETE o", StringComparison.Ordinal))
        {
          return 2;
        }

        if (commandText.Contains("INSERT INTO AP.PayableOccurrence", StringComparison.Ordinal))
        {
          return 1;
        }

        return 1;
      }
    };
    var service = new RecurrentApService(new FakeQueryConnectionFactory(connection));

    var result = await service.ReseedPayableOccurrencesAsync(7, "OHM191112Q26", "Ana");

    Assert.Equal(2, result.DeletedCount);
    Assert.Equal(1, result.CreatedCount);
    Assert.Equal(3, result.PreservedCount);

    var delete = Assert.Single(connection.ExecutedCommands, command => command.CommandText.Contains("DELETE o", StringComparison.Ordinal));
    Assert.Contains("o.UpdatedAt IS NULL", delete.CommandText, StringComparison.Ordinal);
    Assert.Contains("o.Notes IS NULL", delete.CommandText, StringComparison.Ordinal);
    Assert.Contains("NOT EXISTS (SELECT 1 FROM AP.OccurrencePayment", delete.CommandText, StringComparison.Ordinal);
    Assert.Contains("NOT EXISTS (SELECT 1 FROM AP.OccurrenceAttachment", delete.CommandText, StringComparison.Ordinal);
  }

  [Fact]
  public async Task CancelOccurrenceAsync_RejectsWhenPaymentsAreLinked()
  {
    var connection = new FakeQueryDbConnection
    {
      ScalarResultFactory = (_, _) => 1,
      NonQueryResultFactory = (_, _) => 1
    };
    var service = new RecurrentApService(new FakeQueryConnectionFactory(connection));

    var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CancelOccurrenceAsync(10, "OHM191112Q26", "Ana"));

    Assert.Contains("pólizas ligadas", ex.Message, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain(connection.ExecutedCommands, command => command.CommandText.Contains("SET [Status] = 'Cancelled'", StringComparison.Ordinal));
    Assert.True(connection.LastTransaction?.WasRolledBack);
  }

  [Fact]
  public async Task SetOccurrenceStatusAsync_RecalculatesLinkedPaymentStatusWithExpectedAmountOverride()
  {
    var connection = new FakeQueryDbConnection
    {
      ReaderResultFactory = (commandText, _) => commandText.Contains("COUNT(*) AS PaymentCount", StringComparison.Ordinal)
        ? CreatePaymentSummaryTable(paymentCount: 1, totalAmount: 90m, paymentDate: new DateTime(2026, 5, 10))
        : new DataTable(),
      NonQueryResultFactory = (_, _) => 1
    };
    var service = new RecurrentApService(new FakeQueryConnectionFactory(connection));

    await service.SetOccurrenceStatusAsync(
      new RecurrentApOccurrenceStatusRequest
      {
        OccurrenceId = 10,
        Rfc = "OHM191112Q26",
        Status = RecurrentApStatuses.PartiallyPaid,
        ExpectedAmount = 90m,
        ActualAmount = 20m,
        PaymentDate = new DateTime(2026, 5, 9),
        Notes = "Factura variable"
      },
      "Ana");

    var update = Assert.Single(connection.ExecutedCommands, command => command.CommandText.Contains("UPDATE AP.PayableOccurrence", StringComparison.Ordinal));
    AssertParameter(update.Parameters, "@ExpectedAmount", 90m);
    AssertParameter(update.Parameters, "@ActualPaidAmount", 90m);
    AssertParameter(update.Parameters, "@PaymentDate", new DateTime(2026, 5, 10));
    AssertParameter(update.Parameters, "@Status", RecurrentApStatuses.Paid);
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

  [Fact]
  public void LegacyServiciosImportSql_ImportsPortalFieldsIntoRecurringPayable()
  {
    var source = ReadRepositoryFile(
      "src",
      "OrionERP.Infrastructure",
      "Features",
      "CuentasPorPagar",
      "Recurrentes",
      "Sql",
      "20260519_import_legacy_servicios_to_ap.sql");

    Assert.Contains("ADD Website nvarchar(500) NULL", source, StringComparison.Ordinal);
    Assert.Contains("ADD UserName nvarchar(200) NULL", source, StringComparison.Ordinal);
    Assert.Contains("ADD PasswordEnc varbinary(max) NULL", source, StringComparison.Ordinal);
    Assert.Contains("s.Pagina_Web", source, StringComparison.Ordinal);
    Assert.Contains("s.Usuario", source, StringComparison.Ordinal);
    Assert.Contains("Website,", source, StringComparison.Ordinal);
    Assert.Contains("UserName,", source, StringComparison.Ordinal);
    Assert.Contains("RecurringPayablePortalFieldsUpdated", source, StringComparison.Ordinal);
    Assert.DoesNotContain("Credenciales legacy no importadas", source, StringComparison.Ordinal);
  }

  [Fact]
  public void LegacyServiciosCredentialImportScript_EncryptsPasswordsIntoAp()
  {
    var source = ReadRepositoryFile(
      "src",
      "OrionERP.Infrastructure",
      "Features",
      "CuentasPorPagar",
      "Recurrentes",
      "Sql",
      "20260520_import_legacy_servicios_credentials_to_ap.ps1");

    Assert.Contains("System.Security.Cryptography.AesGcm", source, StringComparison.Ordinal);
    Assert.Contains("s.Contrasena", source, StringComparison.Ordinal);
    Assert.Contains("AP.RecurringPayable", source, StringComparison.Ordinal);
    Assert.Contains("PasswordEnc", source, StringComparison.Ordinal);
    Assert.Contains("LegacyServicioId", source, StringComparison.Ordinal);
    Assert.Contains("Existing AP credential values were preserved", source, StringComparison.Ordinal);
  }

  [Fact]
  public void HomeDashboard_UsesConfigurableApNotificationWindowAndOpenOnlyFilter()
  {
    var homeSource = ReadRepositoryFile(
      "src",
      "OrionERP.Web",
      "Pages",
      "ErpHomeDashboard.razor");
    var serviceSource = ReadRepositoryFile(
      "src",
      "OrionERP.Infrastructure",
      "Features",
      "CuentasPorPagar",
      "Recurrentes",
      "RecurrentApService.cs");

    Assert.Contains("IAjustesService AjustesService", homeSource, StringComparison.Ordinal);
    Assert.Contains("AjustesService.GetGeneralSettingsAsync", homeSource, StringComparison.Ordinal);
    Assert.Contains("OpenOnly = true", homeSource, StringComparison.Ordinal);
    Assert.Contains("ApNotificationWindowLabel", homeSource, StringComparison.Ordinal);
    Assert.Contains("href=\"@BuildApOccurrenceUrl(item)\"", homeSource, StringComparison.Ordinal);
    Assert.Contains("occurrenceId={item.Id}", homeSource, StringComparison.Ordinal);
    Assert.DoesNotContain("ApDueSoonDays = 5", homeSource, StringComparison.Ordinal);
    Assert.Contains("filter.OpenOnly", serviceSource, StringComparison.Ordinal);
    Assert.Contains("o.[Status] IN ('Pending','PartiallyPaid')", serviceSource, StringComparison.Ordinal);
  }

  [Fact]
  public void RecurrentApPage_OpensRequestedOccurrenceFromDashboardQuery()
  {
    var pageSource = ReadRepositoryFile(
      "src",
      "OrionERP.Web",
      "Features",
      "CuentasPorPagar",
      "Recurrentes",
      "RecurrentApPage.razor.cs");
    var modelsSource = ReadRepositoryFile(
      "src",
      "OrionERP.Application",
      "Features",
      "CuentasPorPagar",
      "Recurrentes",
      "RecurrentApModels.cs");
    var serviceSource = ReadRepositoryFile(
      "src",
      "OrionERP.Infrastructure",
      "Features",
      "CuentasPorPagar",
      "Recurrentes",
      "RecurrentApService.cs");

    Assert.Contains("SupplyParameterFromQuery(Name = \"occurrenceId\")", pageSource, StringComparison.Ordinal);
    Assert.Contains("SelectRequestedOccurrenceAsync", pageSource, StringComparison.Ordinal);
    Assert.Contains("OccurrenceId = occurrenceId", pageSource, StringComparison.Ordinal);
    Assert.Contains("await SelectOccurrenceAsync(requestedOccurrence)", pageSource, StringComparison.Ordinal);
    Assert.Contains("public int? OccurrenceId", modelsSource, StringComparison.Ordinal);
    Assert.Contains("o.Id = @OccurrenceId", serviceSource, StringComparison.Ordinal);
  }

  [Fact]
  public void AjustesPage_ExposesCxcrNotificationDaySetting()
  {
    var pageSource = ReadRepositoryFile(
      "src",
      "OrionERP.Web",
      "Features",
      "Ajustes",
      "AjustesPage.razor");
    var serviceSource = ReadRepositoryFile(
      "src",
      "OrionERP.Infrastructure",
      "Features",
      "Ajustes",
      "AjustesService.cs");

    Assert.Contains("Notificaciones CxCR", pageSource, StringComparison.Ordinal);
    Assert.Contains("Dias de anticipacion", pageSource, StringComparison.Ordinal);
    Assert.Contains("SaveGeneralSettingsAsync", pageSource, StringComparison.Ordinal);
    Assert.Contains("CxcrApNotificationDays", serviceSource, StringComparison.Ordinal);
    Assert.Contains("PARAMETROS_CONFIGURACION", serviceSource, StringComparison.Ordinal);
  }

  [Fact]
  public void AjustesPage_ExposesCfdiCuentaDefaultSettings()
  {
    var pageSource = ReadRepositoryFile(
      "src",
      "OrionERP.Web",
      "Features",
      "Ajustes",
      "AjustesPage.razor");
    var serviceSource = ReadRepositoryFile(
      "src",
      "OrionERP.Infrastructure",
      "Features",
      "Ajustes",
      "AjustesService.cs");
    var dtoSource = ReadRepositoryFile(
      "src",
      "OrionERP.Application",
      "Features",
      "Ajustes",
      "CfdiPolizaCuentaDefaultDtos.cs");
    var cssSource = ReadRepositoryFile(
      "src",
      "OrionERP.Web",
      "Features",
      "Ajustes",
      "AjustesPage.razor.css");

    Assert.Contains("Cuentas contables CFDI", pageSource, StringComparison.Ordinal);
    Assert.Contains("CuentaContablePicker", pageSource, StringComparison.Ordinal);
    Assert.Contains("SelectedRfc=\"@selectedRfc\"", pageSource, StringComparison.Ordinal);
    Assert.Contains("ajustes-cfdi-selected-account", pageSource, StringComparison.Ordinal);
    Assert.Contains("Seleccionada", pageSource, StringComparison.Ordinal);
    Assert.Contains("ajustes-cfdi-selected-code", cssSource, StringComparison.Ordinal);
    Assert.Contains("SaveCfdiPolizaCuentaDefaultsAsync", pageSource, StringComparison.Ordinal);
    Assert.Contains("dbo.CfdiPolizaCuentaDefault", serviceSource, StringComparison.Ordinal);
    Assert.Contains("CfdiPolizaCuentaDefaultRoles.Required", serviceSource, StringComparison.Ordinal);

    var requiredRoles = new[]
    {
      "SUBTOTAL_GASTO",
      "SUBTOTAL_INGRESO",
      "IVA_TRASLADADO",
      "IVA_ACREDITABLE",
      "IEPS_TRASLADADO",
      "IEPS_ACREDITABLE",
      "RETENCION_IVA",
      "RETENCION_ISR",
      "RETENCION_IEPS",
      "TOTAL_GASTO",
      "TOTAL_INGRESO"
    };

    foreach (var role in requiredRoles)
    {
      Assert.Contains(role, dtoSource, StringComparison.Ordinal);
    }
  }

  [Fact]
  public void RegenerarPolizaDesdeComprobante_UsesConfiguredCfdiAccounts()
  {
    var spSource = ReadRepositoryFile(
      "tmp",
      "dbdefs",
      "contabilidad__Regenerar_Poliza_Desde_Comprobante_En_Transaccion.sql");
    var deploymentSource = ReadRepositoryFile(
      "src",
      "OrionERP.Infrastructure",
      "Features",
      "Contabilidad",
      "Transacciones",
      "Sql",
      "20260523_cfdi_poliza_cuenta_defaults.sql");

    Assert.Contains("dbo.CfdiPolizaCuentaDefault", spSource, StringComparison.Ordinal);
    Assert.Contains("Configura las cuentas contables CFDI", spSource, StringComparison.Ordinal);
    Assert.DoesNotContain("@N1_ACT16_G   VARCHAR(50) = '603'", spSource, StringComparison.Ordinal);
    Assert.DoesNotContain("@N1_TOTAL_I   VARCHAR(50) = '403'", spSource, StringComparison.Ordinal);
    Assert.Contains("CREATE TABLE dbo.CfdiPolizaCuentaDefault", deploymentSource, StringComparison.Ordinal);
    Assert.Contains("CREATE OR ALTER PROCEDURE [contabilidad].[Regenerar_Poliza_Desde_Comprobante_En_Transaccion]", deploymentSource, StringComparison.Ordinal);
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

  private static DataTable CreatePayableTable(
    byte[]? passwordEnc,
    string? description = null,
    string? website = null,
    string? userName = null,
    DateTime? startDate = null,
    DateTime? endDate = null,
    string frequencyUnit = RecurrentApFrequencyUnits.Months,
    bool isActive = false)
  {
    var table = new DataTable();
    table.Columns.Add("Id", typeof(int));
    table.Columns.Add("Rfc", typeof(string));
    table.Columns.Add("Name", typeof(string));
    table.Columns.Add("BusinessPartnerId", typeof(int));
    table.Columns.Add("PayeeNameSnapshot", typeof(string));
    table.Columns.Add("PayeeRfcSnapshot", typeof(string));
    table.Columns.Add("Category", typeof(string));
    table.Columns.Add("Description", typeof(string));
    table.Columns.Add("Website", typeof(string));
    table.Columns.Add("UserName", typeof(string));
    table.Columns.Add("PasswordEnc", typeof(byte[]));
    table.Columns.Add("FrequencyUnit", typeof(string));
    table.Columns.Add("IntervalCount", typeof(int));
    table.Columns.Add("StartDate", typeof(DateTime));
    table.Columns.Add("EndDate", typeof(DateTime));
    table.Columns.Add("DueDayOfMonth", typeof(int));
    table.Columns.Add("DueMonth", typeof(int));
    table.Columns.Add("ExpectedAmount", typeof(decimal));
    table.Columns.Add("Currency", typeof(string));
    table.Columns.Add("IsActive", typeof(bool));

    table.Rows.Add(
      7,
      "OHM191112Q26",
      "Portal internet",
      DBNull.Value,
      "Proveedor",
      "PRO010101ABC",
      "Servicios",
      (object?)description ?? DBNull.Value,
      (object?)website ?? DBNull.Value,
      (object?)userName ?? DBNull.Value,
      (object?)passwordEnc ?? DBNull.Value,
      frequencyUnit,
      1,
      startDate ?? new DateTime(2026, 5, 1),
      (object?)endDate ?? DBNull.Value,
      1,
      DBNull.Value,
      100m,
      "MXN",
      isActive);

    return table;
  }

  private static DataTable CreatePaymentSummaryTable(int paymentCount, decimal totalAmount, DateTime? paymentDate)
  {
    var table = new DataTable();
    table.Columns.Add("PaymentCount", typeof(int));
    table.Columns.Add("TotalAmount", typeof(decimal));
    table.Columns.Add("PaymentDate", typeof(DateTime));
    table.Rows.Add(paymentCount, totalAmount, (object?)paymentDate ?? DBNull.Value);
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

  private static object? GetParameterValue(IEnumerable<FakeQueryParameter> parameters, string name)
  {
    var normalizedName = name.TrimStart('@');
    return Assert.Single(parameters, item => string.Equals(item.Name.TrimStart('@'), normalizedName, StringComparison.OrdinalIgnoreCase)).Value;
  }

  private static string ReadRepositoryFile(params string[] pathSegments)
  {
    var path = Path.GetFullPath(Path.Combine(
      AppContext.BaseDirectory,
      "..",
      "..",
      "..",
      "..",
      ".."));

    foreach (var segment in pathSegments)
    {
      path = Path.Combine(path, segment);
    }

    return File.ReadAllText(path);
  }

  private static void EnsureCredentialKey()
  {
    var appData = Path.Combine(AppContext.BaseDirectory, "App_Data");
    Directory.CreateDirectory(appData);
    var keyPath = Path.Combine(appData, "rfc-register.aes.key");
    if (!File.Exists(keyPath))
    {
      File.WriteAllBytes(keyPath, Enumerable.Range(1, 32).Select(value => (byte)value).ToArray());
    }
  }
}
