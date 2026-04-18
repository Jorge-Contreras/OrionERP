using System.Data;
using OrionERP.Application.Features.Logistica.Purchasing;
using OrionERP.Infrastructure.Features.Logistica.Purchasing;
using OrionERP.UnitTests.Common;

namespace OrionERP.UnitTests.Logistica;

public class PurchaseOrderServiceTests
{
  [Fact]
  public async Task SaveDraftAsync_Fails_WhenMaterialIsRepeated()
  {
    var service = new PurchaseOrderService(new FakeQueryConnectionFactory(new FakeQueryDbConnection()));

    var result = await service.SaveDraftAsync(new PurchaseOrderUpsertRequest
    {
      BusinessPartnerId = 7,
      OrderDate = new DateTime(2026, 4, 17),
      Lines =
      [
        new PurchaseOrderLineUpsertRequest
        {
          MaterialId = 11,
          Allocations =
          [
            new PurchaseOrderAllocationUpsertRequest { LocationId = 3, PlannedQuantity = 2m }
          ]
        },
        new PurchaseOrderLineUpsertRequest
        {
          MaterialId = 11,
          Allocations =
          [
            new PurchaseOrderAllocationUpsertRequest { LocationId = 4, PlannedQuantity = 1m }
          ]
        }
      ]
    }, "Ana");

    Assert.False(result.Success);
    Assert.Equal("No puedes repetir el mismo material dentro de la orden.", result.Message);
  }

  [Fact]
  public async Task SaveDraftAsync_Fails_WhenLineRepeatsLocation()
  {
    var service = new PurchaseOrderService(new FakeQueryConnectionFactory(new FakeQueryDbConnection()));

    var result = await service.SaveDraftAsync(new PurchaseOrderUpsertRequest
    {
      BusinessPartnerId = 7,
      OrderDate = new DateTime(2026, 4, 17),
      Lines =
      [
        new PurchaseOrderLineUpsertRequest
        {
          MaterialId = 11,
          Allocations =
          [
            new PurchaseOrderAllocationUpsertRequest { LocationId = 3, PlannedQuantity = 2m },
            new PurchaseOrderAllocationUpsertRequest { LocationId = 3, PlannedQuantity = 1m }
          ]
        }
      ]
    }, "Ana");

    Assert.False(result.Success);
    Assert.Equal("No puedes repetir la misma ubicación dentro del mismo material.", result.Message);
  }

  [Fact]
  public async Task ReceiveAsync_Fails_WhenQuantityExceedsRemaining()
  {
    var connection = new FakeQueryDbConnection
    {
      ReaderResultFactory = (commandText, _) =>
      {
        if (commandText.Contains("FROM logistica.PurchaseOrder po", StringComparison.Ordinal))
        {
          return CreatePurchaseOrderStateTable(PurchaseOrderStatuses.Issued);
        }

        if (commandText.Contains("FROM logistica.PurchaseOrderLineAllocation allocation", StringComparison.Ordinal))
        {
          return CreateAllocationStateTable(
            allocationId: 11,
            purchaseOrderLineId: 21,
            locationId: 5,
            locationName: "Minibar",
            materialId: 9,
            materialDescription: "Agua",
            plannedQuantity: 5m,
            receivedQuantity: 4m);
        }

        return new DataTable();
      }
    };

    var service = new PurchaseOrderService(new FakeQueryConnectionFactory(connection));

    var result = await service.ReceiveAsync(new PurchaseReceiptCreateRequest
    {
      PurchaseOrderId = 40,
      ReceiptDate = new DateTime(2026, 4, 17),
      Lines =
      [
        new PurchaseReceiptLineCreateRequest
        {
          PurchaseOrderLineAllocationId = 11,
          Quantity = 2m
        }
      ]
    }, "Ana");

    Assert.False(result.Success);
    Assert.Equal("La recepción excede la cantidad pendiente para Agua en Minibar.", result.Message);
    Assert.NotNull(connection.LastTransaction);
    Assert.True(connection.LastTransaction!.WasRolledBack);
  }

  [Fact]
  public async Task ReceiveAsync_UpdatesStockAndWritesPurchaseReceiptAudit()
  {
    var connection = new FakeQueryDbConnection
    {
      ReaderResultFactory = (commandText, _) =>
      {
        if (commandText.Contains("FROM logistica.PurchaseOrder po", StringComparison.Ordinal))
        {
          return CreatePurchaseOrderStateTable(PurchaseOrderStatuses.Issued);
        }

        if (commandText.Contains("FROM logistica.PurchaseOrderLineAllocation allocation", StringComparison.Ordinal))
        {
          return CreateAllocationStateTable(
            allocationId: 11,
            purchaseOrderLineId: 21,
            locationId: 5,
            locationName: "Minibar",
            materialId: 9,
            materialDescription: "Agua",
            plannedQuantity: 6m,
            receivedQuantity: 4m);
        }

        if (commandText.Contains("FROM logistica.StockBalance sb", StringComparison.Ordinal))
        {
          return CreateStockBalanceStateTable(id: 41, quantity: 6m, isRemoved: true);
        }

        return new DataTable();
      },
      ScalarResultFactory = (commandText, _) =>
      {
        if (commandText.Contains("INSERT INTO logistica.PurchaseReceipt", StringComparison.Ordinal))
        {
          return 70;
        }

        if (commandText.Contains("SELECT CAST(ISNULL(SUM(line.OrderedQuantity - line.ReceivedQuantity), 0) AS decimal(18,4))", StringComparison.Ordinal))
        {
          return 0m;
        }

        return null;
      },
      NonQueryResultFactory = (_, _) => 1
    };

    var service = new PurchaseOrderService(new FakeQueryConnectionFactory(connection));

    var result = await service.ReceiveAsync(new PurchaseReceiptCreateRequest
    {
      PurchaseOrderId = 40,
      ReceiptDate = new DateTime(2026, 4, 17),
      Notes = "Entrega completa",
      Lines =
      [
        new PurchaseReceiptLineCreateRequest
        {
          PurchaseOrderLineAllocationId = 11,
          Quantity = 2m
        }
      ]
    }, "Ana");

    Assert.True(result.Success);
    Assert.Equal(70, result.EntityId);
    Assert.NotNull(connection.LastTransaction);
    Assert.True(connection.LastTransaction!.WasCommitted);

    var stockUpdate = Assert.Single(connection.ExecutedCommands, command => command.CommandText.Contains("SET Quantity = Quantity + @Quantity", StringComparison.Ordinal)
      && command.CommandText.Contains("IsRemoved = 0", StringComparison.Ordinal));
    AssertParameter(stockUpdate.Parameters, "@StockBalanceId", 41);
    AssertParameter(stockUpdate.Parameters, "@Quantity", 2m);

    var receiptLineInsert = Assert.Single(connection.ExecutedCommands, command => command.CommandText.Contains("INSERT INTO logistica.PurchaseReceiptLine", StringComparison.Ordinal));
    AssertParameter(receiptLineInsert.Parameters, "@PurchaseReceiptId", 70);
    AssertParameter(receiptLineInsert.Parameters, "@PurchaseOrderLineAllocationId", 11);

    var auditInsert = Assert.Single(connection.ExecutedCommands, command => command.CommandText.Contains("INSERT INTO logistica.StockTransaction", StringComparison.Ordinal));
    Assert.Contains("'PurchaseReceipt'", auditInsert.CommandText, StringComparison.Ordinal);
    AssertParameter(auditInsert.Parameters, "@QuantityDelta", 2m);
    AssertParameter(auditInsert.Parameters, "@QuantityAfter", 8m);
    AssertParameter(auditInsert.Parameters, "@ReferenceId", 70);

    var orderStatusUpdate = Assert.Single(connection.ExecutedCommands, command => command.CommandText.Contains("SET [Status] = @Status", StringComparison.Ordinal)
      && command.CommandText.Contains("CompletedAt = CASE WHEN @IsCompleted = 1", StringComparison.Ordinal));
    AssertParameter(orderStatusUpdate.Parameters, "@Status", PurchaseOrderStatuses.Completed);
  }

  private static void AssertParameter(IReadOnlyList<FakeQueryParameter> parameters, string name, object expectedValue)
  {
    var parameter = Assert.Single(parameters, parameter => HasParameterName(parameter, name));
    Assert.Equal(expectedValue, parameter.Value);
  }

  private static bool HasParameterName(FakeQueryParameter parameter, string expectedName)
    => string.Equals(parameter.Name.TrimStart('@'), expectedName.TrimStart('@'), StringComparison.OrdinalIgnoreCase);

  private static DataTable CreatePurchaseOrderStateTable(string status)
  {
    var table = new DataTable();
    table.Columns.Add("Id", typeof(int));
    table.Columns.Add("Status", typeof(string));
    table.Rows.Add(40, status);
    return table;
  }

  private static DataTable CreateAllocationStateTable(
    int allocationId,
    int purchaseOrderLineId,
    int locationId,
    string locationName,
    int materialId,
    string materialDescription,
    decimal plannedQuantity,
    decimal receivedQuantity)
  {
    var table = new DataTable();
    table.Columns.Add("AllocationId", typeof(int));
    table.Columns.Add("PurchaseOrderLineId", typeof(int));
    table.Columns.Add("LocationId", typeof(int));
    table.Columns.Add("LocationName", typeof(string));
    table.Columns.Add("MaterialId", typeof(int));
    table.Columns.Add("MaterialDescription", typeof(string));
    table.Columns.Add("PlannedQuantity", typeof(decimal));
    table.Columns.Add("ReceivedQuantity", typeof(decimal));
    table.Rows.Add(allocationId, purchaseOrderLineId, locationId, locationName, materialId, materialDescription, plannedQuantity, receivedQuantity);
    return table;
  }

  private static DataTable CreateStockBalanceStateTable(int id, decimal quantity, bool isRemoved)
  {
    var table = new DataTable();
    table.Columns.Add("Id", typeof(int));
    table.Columns.Add("Quantity", typeof(decimal));
    table.Columns.Add("IsRemoved", typeof(bool));
    table.Rows.Add(id, quantity, isRemoved);
    return table;
  }
}
