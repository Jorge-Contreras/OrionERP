using System.Data;
using OrionERP.Application.Features.Logistica.BusinessPartners;
using OrionERP.Infrastructure.Features.Logistica.BusinessPartners;
using OrionERP.UnitTests.Common;

namespace OrionERP.UnitTests.Logistica;

public class BusinessPartnerServiceLifecycleTests
{
  private const string Rfc = "OHM191112Q26";

  private static readonly string[] ExpectedDependencyCodes =
  [
    "MaterialVendor",
    "PurchaseOrder",
    "RecurringPayable",
    "HospitalityFiscalCustomer",
    "OtherCompanyScope",
    "LegacyPurchase",
    "LegacyMaterial",
    "LegacyRoomOwner",
    "LegacySiteOwner",
    "LegacyService",
    "LegacyPortalUser"
  ];

  private static readonly string[] ExpectedOwnedCodes =
  [
    "RfcScope",
    "PartnerRole",
    "VendorProfile",
    "CfdiProfile",
    "LegacyPartner",
    "MaterialVendorBackfill"
  ];

  private static readonly IReadOnlyDictionary<string, string> DependencySourceTables =
    new Dictionary<string, string>(StringComparer.Ordinal)
    {
      ["MaterialVendor"] = "logistica.MaterialVendor",
      ["PurchaseOrder"] = "logistica.PurchaseOrder",
      ["RecurringPayable"] = "AP.RecurringPayable",
      ["HospitalityFiscalCustomer"] = "#OrionVendorFiscalCustomers",
      ["OtherCompanyScope"] = "dbo.BusinessPartnerRfcScope",
      ["LegacyPurchase"] = "dbo.Compra",
      ["LegacyMaterial"] = "logistica.MATERIALES",
      ["LegacyRoomOwner"] = "dbo.ROOM",
      ["LegacySiteOwner"] = "orion.HospitalitySiteOwner",
      ["LegacyService"] = "dbo.Servicios",
      ["LegacyPortalUser"] = "auth.AspNetUsers",
      ["RfcScope"] = "dbo.BusinessPartnerRfcScope",
      ["PartnerRole"] = "dbo.BusinessPartnerRole",
      ["VendorProfile"] = "logistica.VendorProfile",
      ["CfdiProfile"] = "dbo.BusinessPartnerCfdiProfile",
      ["LegacyPartner"] = "dbo.Proveedores",
      ["MaterialVendorBackfill"] = "logistica.MaterialVendorBackfill"
    };

  [Fact]
  public async Task GetAssessment_ReturnsMissing_WhenPartnerIsNotScopedToTheCompany()
  {
    var connection = new FakeQueryDbConnection { ReaderResultFactory = (_, _) => CreateAssessmentTable() };
    var service = new BusinessPartnerService(new FakeQueryConnectionFactory(connection));

    var assessment = await service.GetVendorLifecycleAssessmentAsync(Rfc, 42);

    Assert.False(assessment.Exists);
    Assert.False(assessment.CanDelete);
    Assert.Empty(assessment.Dependencies);
    Assert.Empty(assessment.OwnedRecords);
  }

  [Fact]
  public async Task GetAssessment_SkipsTheDatabase_WhenNoPartnerIsSelected()
  {
    var connection = new FakeQueryDbConnection();
    var service = new BusinessPartnerService(new FakeQueryConnectionFactory(connection));

    var assessment = await service.GetVendorLifecycleAssessmentAsync(Rfc, 0);

    Assert.False(assessment.Exists);
    Assert.Empty(connection.ExecutedCommands);
  }

  [Fact]
  public async Task GetAssessment_ReportsEveryVerifiedDependencyGroup()
  {
    var table = CreateAssessmentTable();
    for (var index = 0; index < ExpectedDependencyCodes.Length; index++)
    {
      AddAssessmentRow(
        table,
        ExpectedDependencyCodes[index],
        index % 3 == 0 ? VendorDependencyKinds.Operational : index % 3 == 1 ? VendorDependencyKinds.Historical : VendorDependencyKinds.Configuration,
        (index + 1) * 10,
        referenceCount: index + 2,
        example: $"Referencia {index + 1}");
    }

    var connection = new FakeQueryDbConnection { ReaderResultFactory = (_, _) => table };
    var service = new BusinessPartnerService(new FakeQueryConnectionFactory(connection));

    var assessment = await service.GetVendorLifecycleAssessmentAsync(Rfc, 42);

    Assert.True(assessment.Exists);
    Assert.False(assessment.CanDelete);
    Assert.Equal(ExpectedDependencyCodes.Length, assessment.Dependencies.Count);
    Assert.Equal(ExpectedDependencyCodes.Where((_, index) => index % 3 == 0), assessment.OperationalBlockers.Select(dependency => dependency.Code));
    Assert.Equal(ExpectedDependencyCodes.Where((_, index) => index % 3 == 1), assessment.HistoricalReferences.Select(dependency => dependency.Code));
    Assert.Equal(ExpectedDependencyCodes.Where((_, index) => index % 3 == 2), assessment.ConfigurationReferences.Select(dependency => dependency.Code));
    Assert.Equal(ExpectedDependencyCodes.Select((_, index) => (long)index + 2).Sum(), assessment.TotalReferences);
    Assert.All(assessment.Dependencies, dependency => Assert.Single(dependency.Examples));
    Assert.All(assessment.Dependencies, dependency => Assert.False(string.IsNullOrWhiteSpace(dependency.Title)));

    var command = Assert.Single(LifecycleCommands(connection));
    foreach (var code in ExpectedDependencyCodes.Concat(ExpectedOwnedCodes))
    {
      Assert.Contains(DependencySourceTables[code], command.CommandText, StringComparison.Ordinal);
    }

    AssertParameter(command.Parameters, "Rfc", Rfc);
    AssertParameter(command.Parameters, "BusinessPartnerId", 42);
    AssertParameter(command.Parameters, "ExampleLimit", 5);
  }

  [Fact]
  public async Task GetAssessment_KeepsOwnedRecordsOutOfTheBlockers()
  {
    var table = CreateAssessmentTable();
    foreach (var code in ExpectedOwnedCodes)
    {
      AddAssessmentRow(table, code, VendorDependencyKinds.Owned, 200, referenceCount: 1, example: $"{code} propio");
    }

    var assessment = await AssessAsync(table);

    Assert.True(assessment.Exists);
    Assert.True(assessment.CanDelete);
    Assert.Empty(assessment.Dependencies);
    Assert.Equal(ExpectedOwnedCodes.Length, assessment.OwnedRecords.Count);
    Assert.Equal(ExpectedOwnedCodes.Length, assessment.OwnedRecordCount);
  }

  [Fact]
  public async Task GetAssessment_ComputesEveryLifecycleOutcome()
  {
    var pristine = await AssessAsync(CreateClearAssessmentTable());
    Assert.True(pristine.CanDelete);
    Assert.Empty(pristine.Dependencies);

    var operationalTable = CreateAssessmentTable();
    AddAssessmentRow(operationalTable, "PurchaseOrder", VendorDependencyKinds.Operational, 20, 2, "PO-000001 · Draft");
    var operational = await AssessAsync(operationalTable);
    Assert.False(operational.CanDelete);
    Assert.False(operational.HasHistory);
    Assert.Equal(2, operational.OperationalReferenceCount);

    var historyTable = CreateAssessmentTable();
    AddAssessmentRow(historyTable, "LegacyPurchase", VendorDependencyKinds.Historical, 60, 4, "Compra #5413");
    var history = await AssessAsync(historyTable);
    Assert.False(history.CanDelete);
    Assert.True(history.HasHistory);
    Assert.Equal(4, history.HistoricalReferenceCount);

    var configurationTable = CreateAssessmentTable();
    AddAssessmentRow(configurationTable, "MaterialVendor", VendorDependencyKinds.Configuration, 10, 1, "MAT-1 · Inactivo");
    var configuration = await AssessAsync(configurationTable);
    Assert.False(configuration.CanDelete);
    Assert.Equal(1, configuration.ConfigurationReferenceCount);
  }

  [Fact]
  public async Task GetAssessment_SweepsHospitalitySitesBeforeReadingTheReport()
  {
    var connection = new FakeQueryDbConnection { ReaderResultFactory = (_, _) => CreateClearAssessmentTable() };
    var service = new BusinessPartnerService(new FakeQueryConnectionFactory(connection));

    await service.GetVendorLifecycleAssessmentAsync(Rfc, 42);

    var commands = connection.ExecutedCommands.ToList();
    var createIndex = commands.FindIndex(command => command.CommandText.Contains("CREATE TABLE #OrionVendorFiscalCustomers", StringComparison.Ordinal));
    var sweepIndex = commands.FindIndex(command => command.CommandText.Contains("DECLARE hospitalitySites CURSOR", StringComparison.Ordinal));
    var reportIndex = commands.FindIndex(command => command.CommandText.Contains("WITH PartnerIdentity", StringComparison.Ordinal));

    Assert.True(createIndex >= 0);
    Assert.True(sweepIndex > createIndex);
    Assert.True(reportIndex > sweepIndex);

    // Sin parámetros el lote viaja tal cual; con ellos Dapper usa sp_executesql y la tabla
    // temporal moriría en ese ámbito antes de que el reporte pudiera leerla.
    Assert.Empty(commands[createIndex].Parameters);
    AssertParameter(commands[sweepIndex].Parameters, "BusinessPartnerId", 42);
    Assert.Contains("orion.Site", commands[sweepIndex].CommandText, StringComparison.Ordinal);
    Assert.Contains("orion.HospitalityFiscalCustomer", commands[sweepIndex].CommandText, StringComparison.Ordinal);
    Assert.EndsWith(
      "EXEC sys.sp_set_session_context @key=N'OrionERP.HospitalitySiteId', @value=NULL, @read_only=0;",
      commands[sweepIndex].CommandText.TrimEnd(),
      StringComparison.Ordinal);
  }

  [Fact]
  public async Task GetAssessment_QueryFailsClosedForUnknownWorkflowStatuses()
  {
    var connection = new FakeQueryDbConnection { ReaderResultFactory = (_, _) => CreateClearAssessmentTable() };
    var service = new BusinessPartnerService(new FakeQueryConnectionFactory(connection));

    await service.GetVendorLifecycleAssessmentAsync(Rfc, 42);

    var sql = Assert.Single(LifecycleCommands(connection)).CommandText;
    Assert.Contains("purchaseOrder.Status IN ('Completed', 'Cancelled') THEN N'Historical' ELSE N'Operational'", sql, StringComparison.Ordinal);
    Assert.Contains("payable.IsActive = 1 THEN N'Operational' ELSE N'Historical'", sql, StringComparison.Ordinal);
    Assert.Contains("link.IsActive = 1 THEN N'Operational' ELSE N'Configuration'", sql, StringComparison.Ordinal);
    Assert.Contains("siteOwner.IsActive = 1 THEN N'Operational' ELSE N'Configuration'", sql, StringComparison.Ordinal);
    Assert.Contains("scope.BusinessPartnerId = @BusinessPartnerId AND scope.Rfc <> @Rfc", sql, StringComparison.Ordinal);
  }

  [Theory]
  [InlineData("")]
  [InlineData("delete")]
  [InlineData("DELETE")]
  [InlineData(" Delete")]
  public async Task Delete_RejectsAnythingExceptExactConfirmation(string confirmationText)
  {
    var connection = new FakeQueryDbConnection();
    var service = new BusinessPartnerService(new FakeQueryConnectionFactory(connection));

    var result = await service.DeleteVendorAsync(new VendorDeleteRequest
    {
      OwnerRfc = Rfc,
      BusinessPartnerId = 42,
      ConfirmationText = confirmationText,
      DeletedBy = "admin@orionerp.local"
    });

    Assert.False(result.Success);
    Assert.Contains("Delete", result.Message, StringComparison.Ordinal);
    Assert.Empty(connection.ExecutedCommands);
  }

  [Fact]
  public async Task Delete_RollsBackWithoutTouchingAnything_WhenAssessmentFindsBlockers()
  {
    var blockerTable = CreateAssessmentTable();
    AddAssessmentRow(blockerTable, "OtherCompanyScope", VendorDependencyKinds.Operational, 50, 1, "Bruno's · BRUNOS260707L26");

    var connection = CreateDeleteConnection(blockerTable, deleteResult: 1);
    var service = new BusinessPartnerService(new FakeQueryConnectionFactory(connection));

    var result = await service.DeleteVendorAsync(CreateDeleteRequest());

    Assert.False(result.Success);
    Assert.NotNull(connection.LastTransaction);
    Assert.Equal(IsolationLevel.Serializable, connection.LastTransaction!.IsolationLevel);
    Assert.True(connection.LastTransaction.WasRolledBack);
    Assert.False(connection.LastTransaction.WasCommitted);
    Assert.DoesNotContain(
      LifecycleCommands(connection),
      command => command.CommandText.Contains("DELETE FROM", StringComparison.Ordinal));
    Assert.Contains("UPDLOCK, HOLDLOCK", Assert.Single(LifecycleCommands(connection)).CommandText, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Delete_RemovesOwnedRecordsAndTheLegacyProviderOnlyAfterTheLockedAssessmentIsClear()
  {
    var connection = CreateDeleteConnection(CreateClearAssessmentTable(), deleteResult: 1);
    var service = new BusinessPartnerService(new FakeQueryConnectionFactory(connection));

    var result = await service.DeleteVendorAsync(CreateDeleteRequest());

    Assert.True(result.Success);
    Assert.True(connection.LastTransaction!.WasCommitted);
    Assert.False(connection.LastTransaction.WasRolledBack);

    var commands = LifecycleCommands(connection).ToList();
    var assessmentIndex = commands.FindIndex(command => command.CommandText.Contains("WITH PartnerIdentity", StringComparison.Ordinal));
    var ownedIndex = commands.FindIndex(command => command.CommandText.Contains("DELETE FROM dbo.BusinessPartnerRfcScope", StringComparison.Ordinal));
    var partnerIndex = commands.FindIndex(command => command.CommandText.StartsWith("DELETE FROM dbo.BusinessPartner WHERE", StringComparison.Ordinal));
    var legacyIndex = commands.FindIndex(command => command.CommandText.StartsWith("DELETE FROM dbo.Proveedores", StringComparison.Ordinal));

    Assert.True(assessmentIndex >= 0);
    Assert.Contains("UPDLOCK, HOLDLOCK", commands[assessmentIndex].CommandText, StringComparison.Ordinal);
    Assert.True(ownedIndex > assessmentIndex);
    Assert.True(partnerIndex > ownedIndex);
    Assert.True(legacyIndex > partnerIndex);

    var ownedBatch = commands[ownedIndex].CommandText;
    Assert.Contains("DELETE FROM logistica.MaterialVendorBackfill", ownedBatch, StringComparison.Ordinal);
    Assert.Contains("DELETE FROM logistica.VendorProfile", ownedBatch, StringComparison.Ordinal);
    Assert.Contains("DELETE FROM dbo.BusinessPartnerCfdiProfile", ownedBatch, StringComparison.Ordinal);
    Assert.Contains("DELETE FROM dbo.BusinessPartnerRole", ownedBatch, StringComparison.Ordinal);
    Assert.All(
      commands.Where(command => !command.CommandText.StartsWith("DELETE FROM dbo.Proveedores", StringComparison.Ordinal)),
      command => AssertParameter(command.Parameters, "BusinessPartnerId", 42));
    AssertParameter(commands[legacyIndex].Parameters, "LegacyProveedorId", 83);
  }

  [Fact]
  public async Task Delete_KeepsTheLegacyCatalogUntouched_WhenThePartnerHasNoLegacyRow()
  {
    var connection = CreateDeleteConnection(CreateClearAssessmentTable(legacyProveedorId: null), deleteResult: 1);
    var service = new BusinessPartnerService(new FakeQueryConnectionFactory(connection));

    var result = await service.DeleteVendorAsync(CreateDeleteRequest());

    Assert.True(result.Success);
    Assert.DoesNotContain(
      LifecycleCommands(connection),
      command => command.CommandText.StartsWith("DELETE FROM dbo.Proveedores", StringComparison.Ordinal));
  }

  [Fact]
  public async Task Delete_RollsBack_WhenPartnerIsNotScopedToTheCompany()
  {
    var connection = CreateDeleteConnection(CreateAssessmentTable(), deleteResult: 1);
    var service = new BusinessPartnerService(new FakeQueryConnectionFactory(connection));

    var result = await service.DeleteVendorAsync(CreateDeleteRequest());

    Assert.False(result.Success);
    Assert.Contains("ya no existe", result.Message, StringComparison.OrdinalIgnoreCase);
    Assert.True(connection.LastTransaction!.WasRolledBack);
    Assert.Single(LifecycleCommands(connection));
  }

  [Fact]
  public async Task Delete_RollsBack_WhenDeleteLosesConcurrencyRace()
  {
    var connection = CreateDeleteConnection(CreateClearAssessmentTable(), deleteResult: 0);
    var service = new BusinessPartnerService(new FakeQueryConnectionFactory(connection));

    var result = await service.DeleteVendorAsync(CreateDeleteRequest());

    Assert.False(result.Success);
    Assert.Contains("cambió", result.Message, StringComparison.OrdinalIgnoreCase);
    Assert.True(connection.LastTransaction!.WasRolledBack);
    Assert.False(connection.LastTransaction.WasCommitted);
    Assert.DoesNotContain(
      LifecycleCommands(connection),
      command => command.CommandText.StartsWith("DELETE FROM dbo.Proveedores", StringComparison.Ordinal));
  }

  // Aquí sólo interesan las consultas de negocio: la autorización de RFC y el barrido de
  // sedes se verifican por separado.
  private static IReadOnlyList<FakeQueryCommandLog> LifecycleCommands(FakeQueryDbConnection connection)
    => connection.ExecutedCommands.Where(command => !IsSetupBatch(command.CommandText)).ToArray();

  private static bool IsSetupBatch(string commandText)
    => commandText.TrimStart().StartsWith("IF ", StringComparison.Ordinal)
       || commandText.Contains("CREATE TABLE #OrionVendorFiscalCustomers", StringComparison.Ordinal)
       || commandText.Contains("DECLARE hospitalitySites CURSOR", StringComparison.Ordinal);

  private static FakeQueryDbConnection CreateDeleteConnection(DataTable assessmentTable, int deleteResult)
    => new()
    {
      ReaderResultFactory = (_, _) => assessmentTable,
      NonQueryResultFactory = (commandText, _) => commandText.StartsWith("DELETE FROM dbo.BusinessPartner WHERE", StringComparison.Ordinal)
        ? deleteResult
        : 0
    };

  private static VendorDeleteRequest CreateDeleteRequest()
    => new()
    {
      OwnerRfc = Rfc,
      BusinessPartnerId = 42,
      ConfirmationText = "Delete",
      DeletedBy = "admin@orionerp.local"
    };

  private static async Task<VendorLifecycleAssessmentDto> AssessAsync(DataTable table)
  {
    var connection = new FakeQueryDbConnection { ReaderResultFactory = (_, _) => table };
    return await new BusinessPartnerService(new FakeQueryConnectionFactory(connection))
      .GetVendorLifecycleAssessmentAsync(Rfc, 42);
  }

  private static DataTable CreateClearAssessmentTable(int? legacyProveedorId = 83)
  {
    var table = CreateAssessmentTable();
    AddAssessmentRow(
      table,
      blockerCode: null,
      dependencyKind: string.Empty,
      blockerSortOrder: 0,
      referenceCount: 0,
      example: null,
      legacyProveedorId: legacyProveedorId);
    return table;
  }

  private static DataTable CreateAssessmentTable()
  {
    var table = new DataTable();
    table.Columns.Add("BusinessPartnerId", typeof(int));
    table.Columns.Add("DisplayName", typeof(string));
    table.Columns.Add("Rfc", typeof(string));
    table.Columns.Add("IsActive", typeof(bool));
    table.Columns.Add("LegacyProveedorId", typeof(int));
    table.Columns.Add("BlockerCode", typeof(string));
    table.Columns.Add("DependencyKind", typeof(string));
    table.Columns.Add("BlockerSortOrder", typeof(int));
    table.Columns.Add("ReferenceCount", typeof(long));
    table.Columns.Add("Example", typeof(string));
    return table;
  }

  private static void AddAssessmentRow(
    DataTable table,
    string? blockerCode,
    string dependencyKind,
    int blockerSortOrder,
    long referenceCount,
    string? example,
    bool isActive = true,
    int? legacyProveedorId = 83)
    => table.Rows.Add(
      42,
      "AMAZON",
      "AAA010101AAA",
      isActive,
      legacyProveedorId is null ? DBNull.Value : legacyProveedorId,
      blockerCode is null ? DBNull.Value : blockerCode,
      dependencyKind,
      blockerSortOrder,
      referenceCount,
      example is null ? DBNull.Value : example);

  private static void AssertParameter(
    IReadOnlyList<FakeQueryParameter> parameters,
    string expectedName,
    object expectedValue)
  {
    var parameter = Assert.Single(
      parameters,
      parameter => string.Equals(
        parameter.Name.TrimStart('@'),
        expectedName.TrimStart('@'),
        StringComparison.OrdinalIgnoreCase));

    Assert.Equal(expectedValue, parameter.Value);
  }
}
