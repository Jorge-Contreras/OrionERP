using System.Data;
using OrionERP.Application.Features.Logistica.Materials;
using OrionERP.Infrastructure.Features.Logistica.Materials;
using OrionERP.UnitTests.Common;

namespace OrionERP.UnitTests.Logistica;

public class MaterialServiceDeletionTests
{
  private const string Rfc = "OHM191112Q26";

  private static readonly string[] ExpectedBlockerCodes =
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

    var assessment = await service.GetMaterialDeletionAssessmentAsync(Rfc, 42);

    Assert.False(assessment.Exists);
    Assert.False(assessment.CanDelete);
    Assert.Empty(assessment.Blockers);
  }

  [Fact]
  public async Task GetAssessment_ReportsEveryVerifiedDependencyGroup()
  {
    var table = CreateAssessmentTable();
    for (var index = 0; index < ExpectedBlockerCodes.Length; index++)
    {
      AddAssessmentRow(
        table,
        ExpectedBlockerCodes[index],
        index + 1,
        referenceCount: index + 2,
        example: $"Referencia {index + 1}");
    }

    var connection = new FakeQueryDbConnection
    {
      ReaderResultFactory = (_, _) => table
    };
    var service = new MaterialService(new FakeQueryConnectionFactory(connection));

    var assessment = await service.GetMaterialDeletionAssessmentAsync(Rfc, 42);

    Assert.True(assessment.Exists);
    Assert.False(assessment.CanDelete);
    Assert.Equal(ExpectedBlockerCodes.Length, assessment.Blockers.Count);
    Assert.Equal(ExpectedBlockerCodes, assessment.Blockers.Select(blocker => blocker.Code));
    Assert.Equal(ExpectedBlockerCodes.Select((_, index) => (long)index + 2).Sum(), assessment.TotalReferences);
    Assert.All(assessment.Blockers, blocker => Assert.Single(blocker.Examples));

    var command = Assert.Single(connection.ExecutedCommands);
    foreach (var tableName in ExpectedBlockerCodes)
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
    AddAssessmentRow(table, "StockBalance", 10, 1, "Bodega · Existencia: 0.0000 · Reservada: 0.0000 · Retirado");
    AddAssessmentRow(table, "LocationMaterialAttachment", 30, 1, "evidencia.pdf · Archivado");
    AddAssessmentRow(table, "MaterialUnitConversion", 180, 1, "CAJA → PZA · Factor: 12.000000 · Inactiva");

    var connection = new FakeQueryDbConnection { ReaderResultFactory = (_, _) => table };
    var service = new MaterialService(new FakeQueryConnectionFactory(connection));

    var assessment = await service.GetMaterialDeletionAssessmentAsync(Rfc, 42);

    Assert.False(assessment.CanDelete);
    Assert.Contains("Retirado", assessment.Blockers.Single(blocker => blocker.Code == "StockBalance").Examples[0]);
    Assert.Contains("Archivado", assessment.Blockers.Single(blocker => blocker.Code == "LocationMaterialAttachment").Examples[0]);
    Assert.Contains("Inactiva", assessment.Blockers.Single(blocker => blocker.Code == "MaterialUnitConversion").Examples[0]);
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
    AddAssessmentRow(blockerTable, "StockBalance", 10, 1, "Bodega · Existencia: 0 · Retirado");

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

  private static DataTable CreateClearAssessmentTable()
  {
    var table = CreateAssessmentTable();
    AddAssessmentRow(table, blockerCode: null, blockerSortOrder: 0, referenceCount: 0, example: null);
    return table;
  }

  private static DataTable CreateAssessmentTable()
  {
    var table = new DataTable();
    table.Columns.Add("MaterialId", typeof(int));
    table.Columns.Add("MaterialCode", typeof(string));
    table.Columns.Add("Description", typeof(string));
    table.Columns.Add("BlockerCode", typeof(string));
    table.Columns.Add("BlockerSortOrder", typeof(int));
    table.Columns.Add("ReferenceCount", typeof(long));
    table.Columns.Add("Example", typeof(string));
    return table;
  }

  private static void AddAssessmentRow(
    DataTable table,
    string? blockerCode,
    int blockerSortOrder,
    long referenceCount,
    string? example)
    => table.Rows.Add(
      42,
      "MAT-000042",
      "Material de prueba",
      blockerCode is null ? DBNull.Value : blockerCode,
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
