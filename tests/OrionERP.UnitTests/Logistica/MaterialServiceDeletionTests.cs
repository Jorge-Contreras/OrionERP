using System.Data;
using OrionERP.Application.Features.Logistica.Materials;
using OrionERP.Infrastructure.Features.Logistica.Materials;
using OrionERP.UnitTests.Common;

namespace OrionERP.UnitTests.Logistica;

public class MaterialServiceLifecycleTests
{
  private const string Rfc = "OHM191112Q26";

  private static readonly string[] ExpectedDependencyCodes =
  [
    "StockBalance",
    "StockTransaction",
    "LocationMaterialAttachment",
    "PhysicalCountLine",
    "MaterialLot",
    "LotBalance",
    "InventoryReservationLine",
    "InventoryTransferLine",
    "InventoryAdjustmentLine",
    "PurchaseOrderLine",
    "PurchaseReceiptLine",
    "BomHeader",
    "BomComponent",
    "ProductionOrder",
    "RestaurantProduct",
    "ModifierIngredientDelta",
    "MaterialAllergen",
    "MaterialUnitConversion"
  ];

  [Fact]
  public async Task GetAssessment_ReturnsMissing_WhenMaterialDoesNotExist()
  {
    var connection = new FakeQueryDbConnection
    {
      ReaderResultFactory = (_, _) => CreateAssessmentTable()
    };
    var service = new MaterialService(new FakeQueryConnectionFactory(connection));

    var assessment = await service.GetMaterialLifecycleAssessmentAsync(Rfc, 42);

    Assert.False(assessment.Exists);
    Assert.False(assessment.CanDelete);
    Assert.Empty(assessment.Dependencies);
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
        index % 3 == 0 ? MaterialDependencyKinds.Operational : index % 3 == 1 ? MaterialDependencyKinds.Historical : MaterialDependencyKinds.Configuration,
        index + 1,
        referenceCount: index + 2,
        example: $"Referencia {index + 1}");
    }

    var connection = new FakeQueryDbConnection
    {
      ReaderResultFactory = (_, _) => table
    };
    var service = new MaterialService(new FakeQueryConnectionFactory(connection));

    var assessment = await service.GetMaterialLifecycleAssessmentAsync(Rfc, 42);

    Assert.True(assessment.Exists);
    Assert.False(assessment.CanDelete);
    Assert.Equal(ExpectedDependencyCodes.Length, assessment.Dependencies.Count);
    Assert.Equal(ExpectedDependencyCodes.Where((_, index) => index % 3 == 0), assessment.OperationalBlockers.Select(dependency => dependency.Code));
    Assert.Equal(ExpectedDependencyCodes.Where((_, index) => index % 3 == 1), assessment.HistoricalReferences.Select(dependency => dependency.Code));
    Assert.Equal(ExpectedDependencyCodes.Where((_, index) => index % 3 == 2), assessment.ConfigurationReferences.Select(dependency => dependency.Code));
    Assert.Equal(ExpectedDependencyCodes.Select((_, index) => (long)index + 2).Sum(), assessment.TotalReferences);
    Assert.All(assessment.Dependencies, dependency => Assert.Single(dependency.Examples));

    var command = Assert.Single(connection.ExecutedCommands);
    foreach (var tableName in ExpectedDependencyCodes)
    {
      var sqlTableName = tableName == "RestaurantProduct" ? "restaurante.Product" : tableName;
      Assert.Contains(sqlTableName, command.CommandText, StringComparison.Ordinal);
    }

    AssertParameter(command.Parameters, "Rfc", Rfc);
    AssertParameter(command.Parameters, "MaterialId", 42);
    AssertParameter(command.Parameters, "ExampleLimit", 5);
  }

  [Fact]
  public async Task GetAssessment_BlocksZeroRemovedArchivedAndInactiveReferences()
  {
    var table = CreateAssessmentTable();
    AddAssessmentRow(table, "StockBalance", MaterialDependencyKinds.Historical, 10, 1, "Bodega · Existencia: 0.0000 · Reservada: 0.0000 · Retirado");
    AddAssessmentRow(table, "LocationMaterialAttachment", MaterialDependencyKinds.Historical, 30, 1, "evidencia.pdf · Archivado");
    AddAssessmentRow(table, "MaterialUnitConversion", MaterialDependencyKinds.Configuration, 180, 1, "CAJA → PZA · Factor: 12.000000 · Inactiva");

    var connection = new FakeQueryDbConnection { ReaderResultFactory = (_, _) => table };
    var service = new MaterialService(new FakeQueryConnectionFactory(connection));

    var assessment = await service.GetMaterialLifecycleAssessmentAsync(Rfc, 42);

    Assert.False(assessment.CanDelete);
    Assert.True(assessment.CanDeactivate);
    Assert.Contains("Retirado", assessment.HistoricalReferences.Single(dependency => dependency.Code == "StockBalance").Examples[0]);
    Assert.Contains("Archivado", assessment.HistoricalReferences.Single(dependency => dependency.Code == "LocationMaterialAttachment").Examples[0]);
    Assert.Contains("Inactiva", assessment.ConfigurationReferences.Single(dependency => dependency.Code == "MaterialUnitConversion").Examples[0]);
  }

  [Fact]
  public async Task GetAssessment_ComputesEveryLifecycleOutcome()
  {
    var pristine = await AssessAsync(CreateClearAssessmentTable());
    Assert.True(pristine.CanDelete);
    Assert.False(pristine.CanDeactivate);

    var operationalTable = CreateAssessmentTable();
    AddAssessmentRow(operationalTable, "StockBalance", MaterialDependencyKinds.Operational, 10, 2, "Bodega · Activo");
    var operational = await AssessAsync(operationalTable);
    Assert.False(operational.CanDelete);
    Assert.False(operational.CanDeactivate);

    var historyTable = CreateAssessmentTable();
    AddAssessmentRow(historyTable, "StockTransaction", MaterialDependencyKinds.Historical, 20, 4, "Movimiento #4");
    var history = await AssessAsync(historyTable);
    Assert.False(history.CanDelete);
    Assert.True(history.CanDeactivate);

    var configurationTable = CreateAssessmentTable();
    AddAssessmentRow(configurationTable, "MaterialUnitConversion", MaterialDependencyKinds.Configuration, 180, 1, "CAJA → PZA · Inactiva");
    var configuration = await AssessAsync(configurationTable);
    Assert.False(configuration.CanDelete);
    Assert.False(configuration.CanDeactivate);

    var inactiveTable = CreateAssessmentTable();
    AddAssessmentRow(inactiveTable, "StockTransaction", MaterialDependencyKinds.Historical, 20, 4, "Movimiento #4", isActive: false);
    var inactive = await AssessAsync(inactiveTable);
    Assert.False(inactive.CanDelete);
    Assert.False(inactive.CanDeactivate);
    Assert.True(inactive.CanReactivate);
  }

  [Fact]
  public async Task GetAssessment_QueryFailsClosedForUnknownWorkflowStatuses()
  {
    var connection = new FakeQueryDbConnection { ReaderResultFactory = (_, _) => CreateClearAssessmentTable() };
    var service = new MaterialService(new FakeQueryConnectionFactory(connection));

    await service.GetMaterialLifecycleAssessmentAsync(Rfc, 42);

    var sql = Assert.Single(connection.ExecutedCommands).CommandText;
    Assert.Contains("countSession.Status IN ('Posted', 'Canceled')", sql, StringComparison.Ordinal);
    Assert.Contains("reservationInfo.Status IN ('Released', 'Consumed')", sql, StringComparison.Ordinal);
    Assert.Contains("transferInfo.Status = 'Posted'", sql, StringComparison.Ordinal);
    Assert.Contains("adjustmentInfo.Status = 'Approved'", sql, StringComparison.Ordinal);
    Assert.Contains("purchaseOrder.Status IN ('Completed', 'Cancelled')", sql, StringComparison.Ordinal);
    Assert.Contains("productionOrder.Status IN ('Completed', 'Cancelled')", sql, StringComparison.Ordinal);
    Assert.Contains("unknownVersion.Status IS NULL OR unknownVersion.Status NOT IN ('Draft', 'Active', 'Retired')", sql, StringComparison.Ordinal);
    Assert.Contains("WHEN bomVersion.Status = 'Retired' THEN N'Historical' ELSE N'Operational'", sql, StringComparison.Ordinal);
    Assert.Contains("ELSE N'Operational'", sql, StringComparison.Ordinal);
  }

  [Fact]
  public async Task GetMaterials_HidesInactiveByDefault_AndIncludesThemOnlyWhenRequested()
  {
    var connection = new FakeQueryDbConnection { ReaderResultFactory = (_, _) => new DataTable() };
    var service = new MaterialService(new FakeQueryConnectionFactory(connection));

    await service.GetMaterialsAsync(new MaterialFilter { Rfc = Rfc, Take = 0 });
    await service.GetMaterialsAsync(new MaterialFilter { Rfc = Rfc, IncludeInactive = true, Take = 0 });

    Assert.Contains("m.IsActive = 1", connection.ExecutedCommands[0].CommandText, StringComparison.Ordinal);
    Assert.DoesNotContain("m.IsActive = 1", connection.ExecutedCommands[1].CommandText, StringComparison.Ordinal);
    Assert.Contains("m.IsActive", connection.ExecutedCommands[1].CommandText, StringComparison.Ordinal);
  }

  [Theory]
  [InlineData("")]
  [InlineData("delete")]
  [InlineData("DELETE")]
  [InlineData(" Delete")]
  public async Task Delete_RejectsAnythingExceptExactConfirmation(string confirmationText)
  {
    var connection = new FakeQueryDbConnection();
    var service = new MaterialService(new FakeQueryConnectionFactory(connection));

    var result = await service.DeleteMaterialAsync(new MaterialDeleteRequest
    {
      Rfc = Rfc,
      MaterialId = 42,
      ConfirmationText = confirmationText,
      DeletedBy = "admin@orionerp.local"
    });

    Assert.False(result.Success);
    Assert.Contains("Delete", result.Message, StringComparison.Ordinal);
    Assert.Empty(connection.ExecutedCommands);
  }

  [Fact]
  public async Task Delete_RollsBackWithoutDelete_WhenAssessmentFindsBlockers()
  {
    var blockerTable = CreateAssessmentTable();
    AddAssessmentRow(blockerTable, "StockBalance", MaterialDependencyKinds.Operational, 10, 1, "Bodega · Existencia: 2 · Activo");

    var connection = CreateDeleteConnection(blockerTable, deleteResult: 1);
    var service = new MaterialService(new FakeQueryConnectionFactory(connection));

    var result = await service.DeleteMaterialAsync(CreateDeleteRequest());

    Assert.False(result.Success);
    Assert.NotNull(connection.LastTransaction);
    Assert.Equal(IsolationLevel.Serializable, connection.LastTransaction!.IsolationLevel);
    Assert.True(connection.LastTransaction.WasRolledBack);
    Assert.False(connection.LastTransaction.WasCommitted);
    Assert.DoesNotContain(
      connection.ExecutedCommands,
      command => command.CommandText.StartsWith("DELETE FROM logistica.Material", StringComparison.Ordinal));
    Assert.Contains(
      connection.ExecutedCommands,
      command => command.CommandText.Contains("UPDLOCK, HOLDLOCK", StringComparison.Ordinal));
  }

  [Fact]
  public async Task Delete_CommitsOnlyAfterLockedAssessmentIsClear()
  {
    var connection = CreateDeleteConnection(CreateClearAssessmentTable(), deleteResult: 1);
    var service = new MaterialService(new FakeQueryConnectionFactory(connection));

    var result = await service.DeleteMaterialAsync(CreateDeleteRequest());

    Assert.True(result.Success);
    Assert.NotNull(connection.LastTransaction);
    Assert.True(connection.LastTransaction!.WasCommitted);
    Assert.False(connection.LastTransaction.WasRolledBack);

    var lockIndex = connection.ExecutedCommands.ToList().FindIndex(
      command => command.CommandText.Contains("UPDLOCK, HOLDLOCK", StringComparison.Ordinal));
    var assessmentIndex = connection.ExecutedCommands.ToList().FindIndex(
      command => command.CommandText.Contains("WITH DependencyRows", StringComparison.Ordinal));
    var deleteIndex = connection.ExecutedCommands.ToList().FindIndex(
      command => command.CommandText.StartsWith("DELETE FROM logistica.Material", StringComparison.Ordinal));

    Assert.True(lockIndex >= 0);
    Assert.True(assessmentIndex > lockIndex);
    Assert.True(deleteIndex > assessmentIndex);
    Assert.All(connection.ExecutedCommands, command =>
    {
      AssertParameter(command.Parameters, "Rfc", Rfc);
      AssertParameter(command.Parameters, "MaterialId", 42);
    });
  }

  [Fact]
  public async Task Delete_RollsBack_WhenMaterialDisappearsBeforeLock()
  {
    var connection = new FakeQueryDbConnection
    {
      ReaderResultFactory = (_, _) => CreateLockTable(includeRow: false)
    };
    var service = new MaterialService(new FakeQueryConnectionFactory(connection));

    var result = await service.DeleteMaterialAsync(CreateDeleteRequest());

    Assert.False(result.Success);
    Assert.Contains("ya no existe", result.Message, StringComparison.OrdinalIgnoreCase);
    Assert.True(connection.LastTransaction!.WasRolledBack);
    Assert.Single(connection.ExecutedCommands);
  }

  [Fact]
  public async Task Delete_RollsBack_WhenDeleteLosesConcurrencyRace()
  {
    var connection = CreateDeleteConnection(CreateClearAssessmentTable(), deleteResult: 0);
    var service = new MaterialService(new FakeQueryConnectionFactory(connection));

    var result = await service.DeleteMaterialAsync(CreateDeleteRequest());

    Assert.False(result.Success);
    Assert.Contains("cambió", result.Message, StringComparison.OrdinalIgnoreCase);
    Assert.True(connection.LastTransaction!.WasRolledBack);
    Assert.False(connection.LastTransaction.WasCommitted);
  }

  [Fact]
  public async Task Deactivate_CommitsOnlyAfterLockedReassessmentFindsHistoryAndNoOperationalLinks()
  {
    var table = CreateAssessmentTable();
    AddAssessmentRow(table, "StockTransaction", MaterialDependencyKinds.Historical, 20, 3, "Movimiento #3");
    AddAssessmentRow(table, "MaterialUnitConversion", MaterialDependencyKinds.Configuration, 180, 1, "CAJA → PZA · Inactiva");
    var connection = new FakeQueryDbConnection
    {
      ReaderResultFactory = (_, _) => table,
      NonQueryResultFactory = (sql, _) => sql.Contains("SET IsActive = 0", StringComparison.Ordinal) ? 1 : 0
    };
    var service = new MaterialService(new FakeQueryConnectionFactory(connection));

    var result = await service.DeactivateMaterialAsync(new MaterialDeactivateRequest
    {
      Rfc = Rfc,
      MaterialId = 42,
      DeactivatedBy = "admin@orionerp.local"
    });

    Assert.True(result.Success);
    Assert.Equal(IsolationLevel.Serializable, connection.LastTransaction!.IsolationLevel);
    Assert.True(connection.LastTransaction.WasCommitted);
    Assert.False(connection.LastTransaction.WasRolledBack);
    var assessmentIndex = connection.ExecutedCommands.ToList().FindIndex(command => command.CommandText.Contains("WITH DependencyRows", StringComparison.Ordinal));
    var updateIndex = connection.ExecutedCommands.ToList().FindIndex(command => command.CommandText.Contains("SET IsActive = 0", StringComparison.Ordinal));
    Assert.True(assessmentIndex >= 0);
    Assert.True(updateIndex > assessmentIndex);
    Assert.Contains("UPDLOCK, HOLDLOCK", connection.ExecutedCommands[assessmentIndex].CommandText, StringComparison.Ordinal);
    Assert.Contains("MaterialStatus = 'INACTIVO'", connection.ExecutedCommands[updateIndex].CommandText, StringComparison.Ordinal);
    Assert.All(connection.ExecutedCommands, command =>
    {
      AssertParameter(command.Parameters, "Rfc", Rfc);
      AssertParameter(command.Parameters, "MaterialId", 42);
    });
  }

  [Fact]
  public async Task Deactivate_RollsBackForOperationalLinksOrConfigurationWithoutHistory()
  {
    var operationalTable = CreateAssessmentTable();
    AddAssessmentRow(operationalTable, "PurchaseOrderLine", MaterialDependencyKinds.Operational, 100, 1, "OC-1 · Draft");
    var operationalConnection = new FakeQueryDbConnection { ReaderResultFactory = (_, _) => operationalTable };
    var operationalResult = await new MaterialService(new FakeQueryConnectionFactory(operationalConnection))
      .DeactivateMaterialAsync(new MaterialDeactivateRequest { Rfc = Rfc, MaterialId = 42 });

    Assert.False(operationalResult.Success);
    Assert.Contains("operativo", operationalResult.Message, StringComparison.OrdinalIgnoreCase);
    Assert.True(operationalConnection.LastTransaction!.WasRolledBack);
    Assert.Single(operationalConnection.ExecutedCommands);

    var configurationTable = CreateAssessmentTable();
    AddAssessmentRow(configurationTable, "MaterialUnitConversion", MaterialDependencyKinds.Configuration, 180, 1, "Conversión inactiva");
    var configurationConnection = new FakeQueryDbConnection { ReaderResultFactory = (_, _) => configurationTable };
    var configurationResult = await new MaterialService(new FakeQueryConnectionFactory(configurationConnection))
      .DeactivateMaterialAsync(new MaterialDeactivateRequest { Rfc = Rfc, MaterialId = 42 });

    Assert.False(configurationResult.Success);
    Assert.Contains("no tiene historial", configurationResult.Message, StringComparison.OrdinalIgnoreCase);
    Assert.True(configurationConnection.LastTransaction!.WasRolledBack);
    Assert.Single(configurationConnection.ExecutedCommands);
  }

  [Fact]
  public async Task Deactivate_RollsBackWhenConcurrentUpdateLosesRace()
  {
    var table = CreateAssessmentTable();
    AddAssessmentRow(table, "StockTransaction", MaterialDependencyKinds.Historical, 20, 1, "Movimiento #1");
    var connection = new FakeQueryDbConnection
    {
      ReaderResultFactory = (_, _) => table,
      NonQueryResultFactory = (_, _) => 0
    };
    var result = await new MaterialService(new FakeQueryConnectionFactory(connection))
      .DeactivateMaterialAsync(new MaterialDeactivateRequest { Rfc = Rfc, MaterialId = 42 });

    Assert.False(result.Success);
    Assert.Contains("cambió", result.Message, StringComparison.OrdinalIgnoreCase);
    Assert.True(connection.LastTransaction!.WasRolledBack);
    Assert.False(connection.LastTransaction.WasCommitted);
  }

  [Fact]
  public async Task Reactivate_SynchronizesActiveStateAndCommits()
  {
    var connection = new FakeQueryDbConnection
    {
      ReaderResultFactory = (_, _) => CreateLifecycleStateTable(isActive: false),
      NonQueryResultFactory = (sql, _) => sql.Contains("SET IsActive = 1", StringComparison.Ordinal) ? 1 : 0
    };
    var service = new MaterialService(new FakeQueryConnectionFactory(connection));

    var result = await service.ReactivateMaterialAsync(new MaterialReactivateRequest
    {
      Rfc = Rfc,
      MaterialId = 42,
      ReactivatedBy = "admin@orionerp.local"
    });

    Assert.True(result.Success);
    Assert.True(connection.LastTransaction!.WasCommitted);
    Assert.Equal(IsolationLevel.Serializable, connection.LastTransaction.IsolationLevel);
    Assert.Contains("UPDLOCK, HOLDLOCK", connection.ExecutedCommands[0].CommandText, StringComparison.Ordinal);
    Assert.Contains("MaterialStatus = 'ACTIVO'", connection.ExecutedCommands[1].CommandText, StringComparison.Ordinal);
    Assert.All(connection.ExecutedCommands, command => AssertParameter(command.Parameters, "Rfc", Rfc));
  }

  [Fact]
  public async Task Reactivate_RollsBackWhenMaterialIsAlreadyActive()
  {
    var connection = new FakeQueryDbConnection { ReaderResultFactory = (_, _) => CreateLifecycleStateTable(isActive: true) };
    var result = await new MaterialService(new FakeQueryConnectionFactory(connection))
      .ReactivateMaterialAsync(new MaterialReactivateRequest { Rfc = Rfc, MaterialId = 42 });

    Assert.False(result.Success);
    Assert.Contains("ya está activo", result.Message, StringComparison.OrdinalIgnoreCase);
    Assert.True(connection.LastTransaction!.WasRolledBack);
    Assert.Single(connection.ExecutedCommands);
  }

  private static FakeQueryDbConnection CreateDeleteConnection(DataTable assessmentTable, int deleteResult)
    => new()
    {
      ReaderResultFactory = (commandText, _) => commandText.Contains("UPDLOCK, HOLDLOCK", StringComparison.Ordinal)
        ? CreateLockTable(includeRow: true)
        : assessmentTable,
      NonQueryResultFactory = (commandText, _) => commandText.StartsWith("DELETE FROM logistica.Material", StringComparison.Ordinal)
        ? deleteResult
        : 0
    };

  private static MaterialDeleteRequest CreateDeleteRequest()
    => new()
    {
      Rfc = Rfc,
      MaterialId = 42,
      ConfirmationText = "Delete",
      DeletedBy = "admin@orionerp.local"
    };

  private static async Task<MaterialLifecycleAssessmentDto> AssessAsync(DataTable table)
  {
    var connection = new FakeQueryDbConnection { ReaderResultFactory = (_, _) => table };
    return await new MaterialService(new FakeQueryConnectionFactory(connection))
      .GetMaterialLifecycleAssessmentAsync(Rfc, 42);
  }

  private static DataTable CreateClearAssessmentTable()
  {
    var table = CreateAssessmentTable();
    AddAssessmentRow(table, blockerCode: null, dependencyKind: string.Empty, blockerSortOrder: 0, referenceCount: 0, example: null);
    return table;
  }

  private static DataTable CreateAssessmentTable()
  {
    var table = new DataTable();
    table.Columns.Add("MaterialId", typeof(int));
    table.Columns.Add("MaterialCode", typeof(string));
    table.Columns.Add("Description", typeof(string));
    table.Columns.Add("IsActive", typeof(bool));
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
    bool isActive = true)
    => table.Rows.Add(
      42,
      "MAT-000042",
      "Material de prueba",
      isActive,
      blockerCode is null ? DBNull.Value : blockerCode,
      dependencyKind,
      blockerSortOrder,
      referenceCount,
      example is null ? DBNull.Value : example);

  private static DataTable CreateLockTable(bool includeRow)
  {
    var table = new DataTable();
    table.Columns.Add("Id", typeof(int));
    if (includeRow)
    {
      table.Rows.Add(42);
    }

    return table;
  }

  private static DataTable CreateLifecycleStateTable(bool isActive)
  {
    var table = new DataTable();
    table.Columns.Add("Id", typeof(int));
    table.Columns.Add("MaterialCode", typeof(string));
    table.Columns.Add("Description", typeof(string));
    table.Columns.Add("IsActive", typeof(bool));
    table.Rows.Add(42, "MAT-000042", "Material de prueba", isActive);
    return table;
  }

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
