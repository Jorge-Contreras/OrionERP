SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET XACT_ABORT ON;
SET NOCOUNT ON;

IF OBJECT_ID('logistica.fn_RfcAccessPredicate', 'IF') IS NULL
  EXEC
  (
    'CREATE FUNCTION logistica.fn_RfcAccessPredicate(@Rfc varchar(50))
     RETURNS TABLE
     WITH SCHEMABINDING
     AS
     RETURN SELECT 1 AS IsAllowed
     WHERE SESSION_CONTEXT(N''OrionRfc'') IS NULL
        OR @Rfc = CONVERT(varchar(50), SESSION_CONTEXT(N''OrionRfc''));'
  );
GO

IF NOT EXISTS (SELECT 1 FROM sys.security_policies WHERE [name] = 'RfcSecurityPolicy' AND schema_id = SCHEMA_ID('logistica'))
BEGIN
  CREATE SECURITY POLICY logistica.RfcSecurityPolicy
    ADD FILTER PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON dbo.BusinessPartnerRfcScope,
    ADD BLOCK PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON dbo.BusinessPartnerRfcScope AFTER INSERT,
    ADD BLOCK PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON dbo.BusinessPartnerRfcScope AFTER UPDATE,

    ADD FILTER PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.MaterialCategory,
    ADD BLOCK PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.MaterialCategory AFTER INSERT,
    ADD BLOCK PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.MaterialCategory AFTER UPDATE,
    ADD FILTER PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.Material,
    ADD BLOCK PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.Material AFTER INSERT,
    ADD BLOCK PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.Material AFTER UPDATE,
    ADD FILTER PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.Location,
    ADD BLOCK PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.Location AFTER INSERT,
    ADD BLOCK PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.Location AFTER UPDATE,
    ADD FILTER PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.StockBalance,
    ADD BLOCK PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.StockBalance AFTER INSERT,
    ADD BLOCK PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.StockBalance AFTER UPDATE,
    ADD FILTER PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.StockTransaction,
    ADD BLOCK PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.StockTransaction AFTER INSERT,
    ADD BLOCK PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.StockTransaction AFTER UPDATE,
    ADD FILTER PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.LocationMaterialAttachment,
    ADD BLOCK PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.LocationMaterialAttachment AFTER INSERT,
    ADD BLOCK PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.LocationMaterialAttachment AFTER UPDATE,
    ADD FILTER PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.VendorProfile,
    ADD BLOCK PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.VendorProfile AFTER INSERT,
    ADD BLOCK PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.VendorProfile AFTER UPDATE,

    ADD FILTER PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.PhysicalCountSession,
    ADD BLOCK PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.PhysicalCountSession AFTER INSERT,
    ADD BLOCK PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.PhysicalCountSession AFTER UPDATE,
    ADD FILTER PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.PhysicalCountLine,
    ADD BLOCK PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.PhysicalCountLine AFTER INSERT,
    ADD BLOCK PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.PhysicalCountLine AFTER UPDATE,
    ADD FILTER PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.PhysicalCountAttachment,
    ADD BLOCK PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.PhysicalCountAttachment AFTER INSERT,
    ADD BLOCK PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.PhysicalCountAttachment AFTER UPDATE,
    ADD FILTER PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.PhysicalCountRecountPlan,
    ADD BLOCK PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.PhysicalCountRecountPlan AFTER INSERT,
    ADD BLOCK PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.PhysicalCountRecountPlan AFTER UPDATE,
    ADD FILTER PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.PhysicalCountRecountPlanLine,
    ADD BLOCK PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.PhysicalCountRecountPlanLine AFTER INSERT,
    ADD BLOCK PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.PhysicalCountRecountPlanLine AFTER UPDATE,
    ADD FILTER PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.PhysicalCountLotLine,
    ADD BLOCK PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.PhysicalCountLotLine AFTER INSERT,
    ADD BLOCK PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.PhysicalCountLotLine AFTER UPDATE,

    ADD FILTER PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.PurchaseOrder,
    ADD BLOCK PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.PurchaseOrder AFTER INSERT,
    ADD BLOCK PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.PurchaseOrder AFTER UPDATE,
    ADD FILTER PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.PurchaseOrderLine,
    ADD BLOCK PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.PurchaseOrderLine AFTER INSERT,
    ADD BLOCK PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.PurchaseOrderLine AFTER UPDATE,
    ADD FILTER PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.PurchaseOrderLineAllocation,
    ADD BLOCK PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.PurchaseOrderLineAllocation AFTER INSERT,
    ADD BLOCK PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.PurchaseOrderLineAllocation AFTER UPDATE,
    ADD FILTER PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.PurchaseOrderRoomScope,
    ADD BLOCK PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.PurchaseOrderRoomScope AFTER INSERT,
    ADD BLOCK PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.PurchaseOrderRoomScope AFTER UPDATE,
    ADD FILTER PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.PurchaseReceipt,
    ADD BLOCK PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.PurchaseReceipt AFTER INSERT,
    ADD BLOCK PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.PurchaseReceipt AFTER UPDATE,
    ADD FILTER PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.PurchaseReceiptLine,
    ADD BLOCK PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.PurchaseReceiptLine AFTER INSERT,
    ADD BLOCK PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.PurchaseReceiptLine AFTER UPDATE,

    ADD FILTER PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.MaterialUnitConversion,
    ADD BLOCK PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.MaterialUnitConversion AFTER INSERT,
    ADD BLOCK PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.MaterialUnitConversion AFTER UPDATE,
    ADD FILTER PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.MaterialLot,
    ADD BLOCK PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.MaterialLot AFTER INSERT,
    ADD BLOCK PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.MaterialLot AFTER UPDATE,
    ADD FILTER PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.LotBalance,
    ADD BLOCK PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.LotBalance AFTER INSERT,
    ADD BLOCK PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.LotBalance AFTER UPDATE,
    ADD FILTER PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.InventoryReservation,
    ADD BLOCK PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.InventoryReservation AFTER INSERT,
    ADD BLOCK PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.InventoryReservation AFTER UPDATE,
    ADD FILTER PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.InventoryReservationLine,
    ADD BLOCK PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.InventoryReservationLine AFTER INSERT,
    ADD BLOCK PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.InventoryReservationLine AFTER UPDATE,
    ADD FILTER PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.InventoryTransfer,
    ADD BLOCK PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.InventoryTransfer AFTER INSERT,
    ADD BLOCK PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.InventoryTransfer AFTER UPDATE,
    ADD FILTER PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.InventoryTransferLine,
    ADD BLOCK PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.InventoryTransferLine AFTER INSERT,
    ADD BLOCK PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.InventoryTransferLine AFTER UPDATE,
    ADD FILTER PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.InventoryAdjustment,
    ADD BLOCK PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.InventoryAdjustment AFTER INSERT,
    ADD BLOCK PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.InventoryAdjustment AFTER UPDATE,
    ADD FILTER PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.InventoryAdjustmentLine,
    ADD BLOCK PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.InventoryAdjustmentLine AFTER INSERT,
    ADD BLOCK PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.InventoryAdjustmentLine AFTER UPDATE,
    ADD FILTER PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.BomHeader,
    ADD BLOCK PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.BomHeader AFTER INSERT,
    ADD BLOCK PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.BomHeader AFTER UPDATE,
    ADD FILTER PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.BomVersion,
    ADD BLOCK PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.BomVersion AFTER INSERT,
    ADD BLOCK PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.BomVersion AFTER UPDATE,
    ADD FILTER PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.BomComponent,
    ADD BLOCK PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.BomComponent AFTER INSERT,
    ADD BLOCK PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.BomComponent AFTER UPDATE,
    ADD FILTER PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.Recipe,
    ADD BLOCK PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.Recipe AFTER INSERT,
    ADD BLOCK PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.Recipe AFTER UPDATE,
    ADD FILTER PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.RecipeStep,
    ADD BLOCK PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.RecipeStep AFTER INSERT,
    ADD BLOCK PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.RecipeStep AFTER UPDATE,
    ADD FILTER PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.MaterialAllergen,
    ADD BLOCK PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.MaterialAllergen AFTER INSERT,
    ADD BLOCK PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.MaterialAllergen AFTER UPDATE,
    ADD FILTER PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.ProductionOrder,
    ADD BLOCK PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.ProductionOrder AFTER INSERT,
    ADD BLOCK PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.ProductionOrder AFTER UPDATE
  WITH (STATE = ON);
END;
GO

DECLARE @RestaurantSchema sysname;
DECLARE @RestaurantTable sysname;
DECLARE @RestaurantRlsSql nvarchar(max);

DECLARE RestaurantRlsCursor CURSOR LOCAL FAST_FORWARD FOR
SELECT schemaInfo.[name],tableInfo.[name]
FROM sys.tables tableInfo
JOIN sys.schemas schemaInfo ON schemaInfo.schema_id=tableInfo.schema_id
WHERE schemaInfo.[name]='restaurante'
  AND EXISTS (SELECT 1 FROM sys.columns columnInfo WHERE columnInfo.object_id=tableInfo.object_id AND columnInfo.[name]='Rfc')
  AND NOT EXISTS
  (
    SELECT 1 FROM sys.security_predicates predicateInfo
    WHERE predicateInfo.object_id=OBJECT_ID('logistica.RfcSecurityPolicy')
      AND predicateInfo.target_object_id=tableInfo.object_id
  );

OPEN RestaurantRlsCursor;
FETCH NEXT FROM RestaurantRlsCursor INTO @RestaurantSchema,@RestaurantTable;
WHILE @@FETCH_STATUS=0
BEGIN
  SET @RestaurantRlsSql=N'ALTER SECURITY POLICY logistica.RfcSecurityPolicy
    ADD FILTER PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON '+QUOTENAME(@RestaurantSchema)+N'.'+QUOTENAME(@RestaurantTable)+N',
    ADD BLOCK PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON '+QUOTENAME(@RestaurantSchema)+N'.'+QUOTENAME(@RestaurantTable)+N' AFTER INSERT,
    ADD BLOCK PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON '+QUOTENAME(@RestaurantSchema)+N'.'+QUOTENAME(@RestaurantTable)+N' AFTER UPDATE;';
  EXEC sys.sp_executesql @RestaurantRlsSql;
  FETCH NEXT FROM RestaurantRlsCursor INTO @RestaurantSchema,@RestaurantTable;
END;
CLOSE RestaurantRlsCursor;
DEALLOCATE RestaurantRlsCursor;
GO
