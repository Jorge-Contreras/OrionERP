using System.Data;
using OrionERP.Application.Features.Logistica.Purchasing;
using OrionERP.Infrastructure.Features.Logistica.Purchasing;
using OrionERP.UnitTests.Common;

namespace OrionERP.UnitTests.Logistica;

public class PurchaseOrderServiceTests
{
  [Fact]
  public async Task CreateAutoDraftAsync_RoundsUpToWholePurchasePack()
  {
    var nextLineId = 201;
    var connection = new FakeQueryDbConnection
    {
      ReaderResultFactory = (commandText, _) =>
      {
        if (commandText.Contains("SELECT TOP (1) po.Id", StringComparison.Ordinal))
        {
          return CreateExistingDraftTable();
        }

        if (commandText.Contains("WITH OpenPurchaseAllocations", StringComparison.Ordinal))
        {
          return CreateAutoPurchaseCandidateTable(
            new AutoCandidateTestRow(
              MaterialId: 11,
              MaterialCode: "MAT-000011",
              MaterialDescription: "Papel higienico",
              PurchaseQuantity: 24m,
              UnitPrice: 160m,
              LocationId: 3,
              LocationName: "Almacen principal",
              RawNeedQuantity: 40m,
              CurrentQuantity: 10m,
              MinQuantity: 12m,
              MaxQuantity: 50m,
              RemainingOpenQuantity: 0m,
              ProjectedQuantity: 10m));
        }

        if (commandText.Contains("FROM logistica.VendorProfile vp", StringComparison.Ordinal))
        {
          return CreateNullableIntTable("DefaultLeadTimeDays", 3);
        }

        if (commandText.Contains("FROM logistica.Material m", StringComparison.Ordinal))
        {
          return CreateMaterialRowsTable(
            new MaterialTestRow(11, "MAT-000011", "Papel higienico", "TP-24", "Pieza", "Paquete", 24m, 160m));
        }

        if (commandText.Contains("FROM logistica.Location l", StringComparison.Ordinal))
        {
          return CreateLocationRowsTable(new LocationTestRow(3, "Almacen principal", "LOC-000003"));
        }

        return new DataTable();
      },
      ScalarResultFactory = (commandText, _) =>
      {
        if (commandText.Contains("FROM dbo.BusinessPartner bp", StringComparison.Ordinal))
        {
          return true;
        }

        if (commandText.Contains("INSERT INTO logistica.PurchaseOrderLine", StringComparison.Ordinal))
        {
          return nextLineId++;
        }

        if (commandText.Contains("INSERT INTO logistica.PurchaseOrder", StringComparison.Ordinal))
        {
          return 81;
        }

        return null;
      },
      NonQueryResultFactory = (_, _) => 1
    };

    var service = new PurchaseOrderService(new FakeQueryConnectionFactory(connection));

    var result = await service.CreateAutoDraftAsync(new AutoPurchaseOrderCreateRequest
    {
      BusinessPartnerId = 7,
      OrderDate = new DateTime(2026, 4, 17)
    }, "Ana");

    Assert.True(result.Success);
    Assert.Equal(81, result.EntityId);

    var orderInsert = Assert.Single(connection.ExecutedCommands, command => command.CommandText.Contains("INSERT INTO logistica.PurchaseOrder", StringComparison.Ordinal)
      && !command.CommandText.Contains("PurchaseOrderLine", StringComparison.Ordinal));
    AssertParameter(orderInsert.Parameters, "@ExpectedDate", new DateTime(2026, 4, 20));

    var lineInsert = Assert.Single(connection.ExecutedCommands, command => command.CommandText.Contains("INSERT INTO logistica.PurchaseOrderLine", StringComparison.Ordinal)
      && !command.CommandText.Contains("PurchaseOrderLineAllocation", StringComparison.Ordinal));
    AssertParameter(lineInsert.Parameters, "@PurchaseQuantitySnapshot", 24m);
    AssertParameter(lineInsert.Parameters, "@PurchaseUnitNameSnapshot", "Paquete");
    AssertParameter(lineInsert.Parameters, "@OrderedQuantity", 48m);
  }

  [Fact]
  public async Task CreateAutoDraftAsync_RoundsEachLocationToWholePurchasePack()
  {
    var nextLineId = 301;
    var connection = new FakeQueryDbConnection
    {
      ReaderResultFactory = (commandText, _) =>
      {
        if (commandText.Contains("SELECT TOP (1) po.Id", StringComparison.Ordinal))
        {
          return CreateExistingDraftTable();
        }

        if (commandText.Contains("WITH OpenPurchaseAllocations", StringComparison.Ordinal))
        {
          return CreateAutoPurchaseCandidateTable(
            new AutoCandidateTestRow(
              MaterialId: 11,
              MaterialCode: "MAT-000011",
              MaterialDescription: "Papel higienico",
              PurchaseQuantity: 24m,
              UnitPrice: 160m,
              LocationId: 3,
              LocationName: "Almacen principal",
              RawNeedQuantity: 20m,
              CurrentQuantity: 4m,
              MinQuantity: 8m,
              MaxQuantity: 24m,
              RemainingOpenQuantity: 0m,
              ProjectedQuantity: 4m),
            new AutoCandidateTestRow(
              MaterialId: 11,
              MaterialCode: "MAT-000011",
              MaterialDescription: "Papel higienico",
              PurchaseQuantity: 24m,
              UnitPrice: 160m,
              LocationId: 4,
              LocationName: "Bodega",
              RawNeedQuantity: 5m,
              CurrentQuantity: 1m,
              MinQuantity: 2m,
              MaxQuantity: 6m,
              RemainingOpenQuantity: 0m,
              ProjectedQuantity: 1m));
        }

        if (commandText.Contains("FROM logistica.VendorProfile vp", StringComparison.Ordinal))
        {
          return CreateNullableIntTable("DefaultLeadTimeDays", null);
        }

        if (commandText.Contains("FROM logistica.Material m", StringComparison.Ordinal))
        {
          return CreateMaterialRowsTable(
            new MaterialTestRow(11, "MAT-000011", "Papel higienico", "TP-24", "Pieza", "Paquete", 24m, 160m));
        }

        if (commandText.Contains("FROM logistica.Location l", StringComparison.Ordinal))
        {
          return CreateLocationRowsTable(
            new LocationTestRow(3, "Almacen principal", "LOC-000003"),
            new LocationTestRow(4, "Bodega", "LOC-000004"));
        }

        return new DataTable();
      },
      ScalarResultFactory = (commandText, _) =>
      {
        if (commandText.Contains("FROM dbo.BusinessPartner bp", StringComparison.Ordinal))
        {
          return true;
        }

        if (commandText.Contains("INSERT INTO logistica.PurchaseOrderLine", StringComparison.Ordinal))
        {
          return nextLineId++;
        }

        if (commandText.Contains("INSERT INTO logistica.PurchaseOrder", StringComparison.Ordinal))
        {
          return 91;
        }

        return null;
      },
      NonQueryResultFactory = (_, _) => 1
    };

    var service = new PurchaseOrderService(new FakeQueryConnectionFactory(connection));

    var result = await service.CreateAutoDraftAsync(new AutoPurchaseOrderCreateRequest
    {
      BusinessPartnerId = 7,
      OrderDate = new DateTime(2026, 4, 17)
    }, "Ana");

    Assert.True(result.Success);
    Assert.Equal(91, result.EntityId);

    var allocationInserts = connection.ExecutedCommands
      .Where(command => command.CommandText.Contains("INSERT INTO logistica.PurchaseOrderLineAllocation", StringComparison.Ordinal))
      .ToList();

    Assert.Equal(2, allocationInserts.Count);
    AssertParameter(allocationInserts[0].Parameters, "@LocationId", 3);
    AssertParameter(allocationInserts[0].Parameters, "@PlannedQuantity", 24m);
    AssertParameter(allocationInserts[1].Parameters, "@LocationId", 4);
    AssertParameter(allocationInserts[1].Parameters, "@PlannedQuantity", 24m);

    var lineInsert = Assert.Single(connection.ExecutedCommands, command => command.CommandText.Contains("INSERT INTO logistica.PurchaseOrderLine", StringComparison.Ordinal)
      && !command.CommandText.Contains("PurchaseOrderLineAllocation", StringComparison.Ordinal));
    AssertParameter(lineInsert.Parameters, "@OrderedQuantity", 48m);
  }

  [Fact]
  public async Task CreateAutoDraftAsync_DoesNotSplitPurchasePackAcrossLocations()
  {
    var nextLineId = 401;
    var connection = new FakeQueryDbConnection
    {
      ReaderResultFactory = (commandText, _) =>
      {
        if (commandText.Contains("SELECT TOP (1) po.Id", StringComparison.Ordinal))
        {
          return CreateExistingDraftTable();
        }

        if (commandText.Contains("WITH OpenPurchaseAllocations", StringComparison.Ordinal))
        {
          return CreateAutoPurchaseCandidateTable(
            new AutoCandidateTestRow(
              MaterialId: 5094,
              MaterialCode: "MAT-005094",
              MaterialDescription: "PAPEL HIGIENICO GREAT VALUE",
              PurchaseQuantity: 32m,
              UnitPrice: 160m,
              LocationId: 112,
              LocationName: "GRECIA",
              RawNeedQuantity: 32m,
              CurrentQuantity: 0m,
              MinQuantity: 10m,
              MaxQuantity: 32m,
              RemainingOpenQuantity: 0m,
              ProjectedQuantity: 0m,
              LocationCode: "LOC-001264"),
            new AutoCandidateTestRow(
              MaterialId: 5094,
              MaterialCode: "MAT-005094",
              MaterialDescription: "PAPEL HIGIENICO GREAT VALUE",
              PurchaseQuantity: 32m,
              UnitPrice: 160m,
              LocationId: 18,
              LocationName: "MOSCU",
              RawNeedQuantity: 26m,
              CurrentQuantity: 6m,
              MinQuantity: 10m,
              MaxQuantity: 32m,
              RemainingOpenQuantity: 0m,
              ProjectedQuantity: 6m,
              LocationCode: "LOC-001128"));
        }

        if (commandText.Contains("FROM logistica.VendorProfile vp", StringComparison.Ordinal))
        {
          return CreateNullableIntTable("DefaultLeadTimeDays", null);
        }

        if (commandText.Contains("FROM logistica.Material m", StringComparison.Ordinal))
        {
          return CreateMaterialRowsTable(
            new MaterialTestRow(5094, "MAT-005094", "PAPEL HIGIENICO GREAT VALUE", "TP-32", "PIEZA", "PAQUETE", 32m, 160m));
        }

        if (commandText.Contains("FROM logistica.Location l", StringComparison.Ordinal))
        {
          return CreateLocationRowsTable(
            new LocationTestRow(18, "MOSCU", "LOC-001128"),
            new LocationTestRow(112, "GRECIA", "LOC-001264"));
        }

        return new DataTable();
      },
      ScalarResultFactory = (commandText, _) =>
      {
        if (commandText.Contains("FROM dbo.BusinessPartner bp", StringComparison.Ordinal))
        {
          return true;
        }

        if (commandText.Contains("INSERT INTO logistica.PurchaseOrderLine", StringComparison.Ordinal))
        {
          return nextLineId++;
        }

        if (commandText.Contains("INSERT INTO logistica.PurchaseOrder", StringComparison.Ordinal))
        {
          return 101;
        }

        return null;
      },
      NonQueryResultFactory = (_, _) => 1
    };

    var service = new PurchaseOrderService(new FakeQueryConnectionFactory(connection));

    var result = await service.CreateAutoDraftAsync(new AutoPurchaseOrderCreateRequest
    {
      BusinessPartnerId = 7,
      OrderDate = new DateTime(2026, 4, 17)
    }, "Ana");

    Assert.True(result.Success);
    Assert.Equal(101, result.EntityId);

    var allocationInserts = connection.ExecutedCommands
      .Where(command => command.CommandText.Contains("INSERT INTO logistica.PurchaseOrderLineAllocation", StringComparison.Ordinal))
      .ToList();

    Assert.Equal(2, allocationInserts.Count);
    AssertParameter(allocationInserts[0].Parameters, "@LocationId", 18);
    AssertParameter(allocationInserts[0].Parameters, "@PlannedQuantity", 32m);
    AssertParameter(allocationInserts[1].Parameters, "@LocationId", 112);
    AssertParameter(allocationInserts[1].Parameters, "@PlannedQuantity", 32m);

    var lineInsert = Assert.Single(connection.ExecutedCommands, command => command.CommandText.Contains("INSERT INTO logistica.PurchaseOrderLine", StringComparison.Ordinal)
      && !command.CommandText.Contains("PurchaseOrderLineAllocation", StringComparison.Ordinal));
    AssertParameter(lineInsert.Parameters, "@OrderedQuantity", 64m);
  }

  [Fact]
  public async Task CreateAutoDraftAsync_ReturnsExistingDraft_WhenVendorAlreadyHasDraft()
  {
    var connection = new FakeQueryDbConnection
    {
      ReaderResultFactory = (commandText, _) =>
      {
        if (commandText.Contains("SELECT TOP (1) po.Id", StringComparison.Ordinal))
        {
          return CreateExistingDraftTable(90);
        }

        return new DataTable();
      },
      ScalarResultFactory = (commandText, _) =>
      {
        if (commandText.Contains("FROM dbo.BusinessPartner bp", StringComparison.Ordinal))
        {
          return true;
        }

        return null;
      }
    };

    var service = new PurchaseOrderService(new FakeQueryConnectionFactory(connection));

    var result = await service.CreateAutoDraftAsync(new AutoPurchaseOrderCreateRequest
    {
      BusinessPartnerId = 7,
      OrderDate = new DateTime(2026, 4, 17)
    }, "Ana");

    Assert.True(result.Success);
    Assert.Equal(90, result.EntityId);
    Assert.Equal("El proveedor ya tiene un borrador abierto. Se abrirá ese documento para revisión.", result.Message);
    Assert.DoesNotContain(connection.ExecutedCommands, command => command.CommandText.Contains("INSERT INTO logistica.PurchaseOrder", StringComparison.Ordinal));
  }

  [Fact]
  public async Task CreateAutoDraftAsync_ReturnsFriendlyMessage_WhenOpenSupplyKeepsStockAboveMinimum()
  {
    var connection = new FakeQueryDbConnection
    {
      ReaderResultFactory = (commandText, _) =>
      {
        if (commandText.Contains("SELECT TOP (1) po.Id", StringComparison.Ordinal))
        {
          return CreateExistingDraftTable();
        }

        if (commandText.Contains("WITH OpenPurchaseAllocations", StringComparison.Ordinal))
        {
          return CreateAutoPurchaseCandidateTable(
            new AutoCandidateTestRow(
              MaterialId: 11,
              MaterialCode: "MAT-000011",
              MaterialDescription: "Papel higienico",
              PurchaseQuantity: 24m,
              UnitPrice: 160m,
              LocationId: 3,
              LocationName: "Almacen principal",
              RawNeedQuantity: 35m,
              CurrentQuantity: 10m,
              MinQuantity: 12m,
              MaxQuantity: 45m,
              RemainingOpenQuantity: 10m,
              ProjectedQuantity: 20m));
        }

        return new DataTable();
      },
      ScalarResultFactory = (commandText, _) =>
      {
        if (commandText.Contains("FROM dbo.BusinessPartner bp", StringComparison.Ordinal))
        {
          return true;
        }

        return null;
      }
    };

    var service = new PurchaseOrderService(new FakeQueryConnectionFactory(connection));

    var result = await service.CreateAutoDraftAsync(new AutoPurchaseOrderCreateRequest
    {
      BusinessPartnerId = 7,
      OrderDate = new DateTime(2026, 4, 17)
    }, "Ana");

    Assert.False(result.Success);
    Assert.Equal("No hay materiales por reordenar para el proveedor seleccionado.", result.Message);
    Assert.DoesNotContain(connection.ExecutedCommands, command => command.CommandText.Contains("INSERT INTO logistica.PurchaseOrder", StringComparison.Ordinal));
  }

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
  public async Task SaveDraftAsync_Fails_WhenLineTotalIsNotWholePurchaseMultiple()
  {
    var connection = new FakeQueryDbConnection
    {
      ReaderResultFactory = (commandText, _) =>
      {
        if (commandText.Contains("FROM logistica.Material m", StringComparison.Ordinal))
        {
          return CreateMaterialRowsTable(
            new MaterialTestRow(11, "MAT-000011", "Papel higienico", "TP-24", "Pieza", "Paquete", 24m, 160m));
        }

        if (commandText.Contains("FROM logistica.Location l", StringComparison.Ordinal))
        {
          return CreateLocationRowsTable(new LocationTestRow(3, "Almacen principal", "LOC-000003"));
        }

        return new DataTable();
      },
      ScalarResultFactory = (commandText, _) =>
      {
        if (commandText.Contains("FROM dbo.BusinessPartner bp", StringComparison.Ordinal))
        {
          return true;
        }

        return null;
      }
    };

    var service = new PurchaseOrderService(new FakeQueryConnectionFactory(connection));

    var result = await service.SaveDraftAsync(new PurchaseOrderUpsertRequest
    {
      BusinessPartnerId = 7,
      OrderDate = new DateTime(2026, 4, 17),
      Lines =
      [
        new PurchaseOrderLineUpsertRequest
        {
          MaterialId = 11,
          PurchaseQuantitySnapshot = 24m,
          PurchaseUnitNameSnapshot = "Paquete",
          Allocations =
          [
            new PurchaseOrderAllocationUpsertRequest { LocationId = 3, PlannedQuantity = 25m }
          ]
        }
      ]
    }, "Ana");

    Assert.False(result.Success);
    Assert.Contains("debe ser múltiplo", result.Message, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain(connection.ExecutedCommands, command => command.CommandText.Contains("INSERT INTO logistica.PurchaseOrder", StringComparison.Ordinal));
  }

  [Fact]
  public async Task SaveDraftAsync_Fails_WhenAllocationSplitsPurchasePackAcrossLocations()
  {
    var connection = new FakeQueryDbConnection
    {
      ReaderResultFactory = (commandText, _) =>
      {
        if (commandText.Contains("FROM logistica.Material m", StringComparison.Ordinal))
        {
          return CreateMaterialRowsTable(
            new MaterialTestRow(11, "MAT-000011", "Papel higienico", "TP-32", "Pieza", "Paquete", 32m, 160m));
        }

        if (commandText.Contains("FROM logistica.Location l", StringComparison.Ordinal))
        {
          return CreateLocationRowsTable(
            new LocationTestRow(3, "Almacen principal", "LOC-000003"),
            new LocationTestRow(4, "Bodega", "LOC-000004"));
        }

        return new DataTable();
      },
      ScalarResultFactory = (commandText, _) =>
      {
        if (commandText.Contains("FROM dbo.BusinessPartner bp", StringComparison.Ordinal))
        {
          return true;
        }

        return null;
      }
    };

    var service = new PurchaseOrderService(new FakeQueryConnectionFactory(connection));

    var result = await service.SaveDraftAsync(new PurchaseOrderUpsertRequest
    {
      BusinessPartnerId = 7,
      OrderDate = new DateTime(2026, 4, 17),
      Lines =
      [
        new PurchaseOrderLineUpsertRequest
        {
          MaterialId = 11,
          PurchaseQuantitySnapshot = 32m,
          PurchaseUnitNameSnapshot = "Paquete",
          Allocations =
          [
            new PurchaseOrderAllocationUpsertRequest { LocationId = 3, PlannedQuantity = 38m },
            new PurchaseOrderAllocationUpsertRequest { LocationId = 4, PlannedQuantity = 26m }
          ]
        }
      ]
    }, "Ana");

    Assert.False(result.Success);
    Assert.Contains("cantidad planeada", result.Message, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("LOC-000003", result.Message, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain(connection.ExecutedCommands, command => command.CommandText.Contains("INSERT INTO logistica.PurchaseOrder", StringComparison.Ordinal));
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

  private static DataTable CreateExistingDraftTable(int? purchaseOrderId = null)
  {
    var table = new DataTable();
    table.Columns.Add("Id", typeof(int));

    if (purchaseOrderId.HasValue)
    {
      table.Rows.Add(purchaseOrderId.Value);
    }

    return table;
  }

  private static DataTable CreateNullableIntTable(string columnName, int? value)
  {
    var table = new DataTable();
    table.Columns.Add(columnName, typeof(int));

    if (value.HasValue)
    {
      table.Rows.Add(value.Value);
    }

    return table;
  }

  private static DataTable CreateAutoPurchaseCandidateTable(params AutoCandidateTestRow[] rows)
  {
    var table = new DataTable();
    table.Columns.Add("MaterialId", typeof(int));
    table.Columns.Add("MaterialCode", typeof(string));
    table.Columns.Add("MaterialDescription", typeof(string));
    table.Columns.Add("VendorCode", typeof(string));
    table.Columns.Add("BaseUnitName", typeof(string));
    table.Columns.Add("PurchaseUnitName", typeof(string));
    table.Columns.Add("PurchaseQuantity", typeof(decimal));
    table.Columns.Add("UnitPrice", typeof(decimal));
    table.Columns.Add("LocationId", typeof(int));
    table.Columns.Add("LocationName", typeof(string));
    table.Columns.Add("LocationCode", typeof(string));
    table.Columns.Add("CurrentQuantity", typeof(decimal));
    table.Columns.Add("MinQuantity", typeof(decimal));
    table.Columns.Add("MaxQuantity", typeof(decimal));
    table.Columns.Add("RemainingOpenQuantity", typeof(decimal));
    table.Columns.Add("ProjectedQuantity", typeof(decimal));
    table.Columns.Add("RawNeedQuantity", typeof(decimal));

    foreach (var row in rows)
    {
      table.Rows.Add(
        row.MaterialId,
        row.MaterialCode,
        row.MaterialDescription,
        row.VendorCode,
        row.BaseUnitName,
        row.PurchaseUnitName,
        row.PurchaseQuantity,
        row.UnitPrice,
        row.LocationId,
        row.LocationName,
        row.LocationCode,
        row.CurrentQuantity,
        row.MinQuantity,
        row.MaxQuantity,
        row.RemainingOpenQuantity,
        row.ProjectedQuantity,
        row.RawNeedQuantity);
    }

    return table;
  }

  private static DataTable CreateMaterialRowsTable(params MaterialTestRow[] rows)
  {
    var table = new DataTable();
    table.Columns.Add("Id", typeof(int));
    table.Columns.Add("MaterialCode", typeof(string));
    table.Columns.Add("Description", typeof(string));
    table.Columns.Add("VendorCode", typeof(string));
    table.Columns.Add("BaseUnitName", typeof(string));
    table.Columns.Add("PurchaseUnitName", typeof(string));
    table.Columns.Add("PurchaseQuantity", typeof(decimal));
    table.Columns.Add("Price", typeof(decimal));

    foreach (var row in rows)
    {
      table.Rows.Add(
        row.Id,
        row.MaterialCode,
        row.Description,
        row.VendorCode,
        row.BaseUnitName,
        row.PurchaseUnitName,
        row.PurchaseQuantity,
        row.Price);
    }

    return table;
  }

  private static DataTable CreateLocationRowsTable(params LocationTestRow[] rows)
  {
    var table = new DataTable();
    table.Columns.Add("Id", typeof(int));
    table.Columns.Add("LocationName", typeof(string));
    table.Columns.Add("LocationCode", typeof(string));

    foreach (var row in rows)
    {
      table.Rows.Add(row.Id, row.LocationName, row.LocationCode);
    }

    return table;
  }

  private sealed record AutoCandidateTestRow(
    int MaterialId,
    string MaterialCode,
    string MaterialDescription,
    decimal PurchaseQuantity,
    decimal? UnitPrice,
    int LocationId,
    string LocationName,
    decimal RawNeedQuantity,
    decimal CurrentQuantity,
    decimal MinQuantity,
    decimal MaxQuantity,
    decimal RemainingOpenQuantity,
    decimal ProjectedQuantity,
    string VendorCode = "TP-24",
    string BaseUnitName = "Pieza",
    string PurchaseUnitName = "Paquete",
    string LocationCode = "LOC");

  private sealed record MaterialTestRow(
    int Id,
    string MaterialCode,
    string Description,
    string? VendorCode,
    string? BaseUnitName,
    string? PurchaseUnitName,
    decimal PurchaseQuantity,
    decimal? Price);

  private sealed record LocationTestRow(int Id, string LocationName, string LocationCode);
}
