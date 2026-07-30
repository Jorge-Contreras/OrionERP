/*
  Transfiere la operación de Bruno's de OHM191112Q26 a OHM260707L26.

  Requiere SQLCMD y dos variables explícitas:

    sqlcmd ... -v ExpectedDatabase="Orion_Sandbox" ApplyChanges="0" -i 20260730_transfer_bruno_restaurant_to_rfc.sql
    sqlcmd ... -v ExpectedDatabase="Orion_Sandbox" ApplyChanges="1" -i 20260730_transfer_bruno_restaurant_to_rfc.sql

  ApplyChanges=0 ejecuta exactamente la misma migración y validaciones, pero
  revierte la transacción al final. ApplyChanges=1 confirma los cambios.

  Para producción se debe proporcionar ExpectedDatabase="grupocarpio" de
  manera explícita, después de realizar el respaldo y detener temporalmente
  las capturas de Materiales y Restaurante.
*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET XACT_ABORT ON;
SET NOCOUNT ON;

DECLARE @ExpectedDatabase sysname = N'$(ExpectedDatabase)';
DECLARE @ApplyChanges bit = TRY_CONVERT(bit, N'$(ApplyChanges)');
DECLARE @SourceRfc varchar(50) = 'OHM191112Q26';
DECLARE @TargetRfc varchar(50) = 'OHM260707L26';
DECLARE @SourceSiteCode varchar(50) = 'BRUNOS-01';
DECLARE @SourceLocationCode varchar(50) = 'LOC-000127';
DECLARE @TargetLocationCode varchar(50) = 'BRUNOS-01-COCINA-REFRIGERADOR';
DECLARE @TargetLocationName varchar(200) = 'BRUNO''S - COCINA - REFRIGERADOR';
DECLARE @MigrationUser varchar(256) = '20260730_transfer_bruno_restaurant_to_rfc';
DECLARE @MigrationLockResult int;

IF @ExpectedDatabase NOT IN (N'Orion_Sandbox', N'Orion_SandBox', N'grupocarpio')
  THROW 51000, 'ExpectedDatabase debe ser Orion_Sandbox o grupocarpio.', 1;

IF DB_NAME() <> @ExpectedDatabase
  THROW 51001, 'La base conectada no coincide con ExpectedDatabase.', 1;

IF @ApplyChanges IS NULL
  THROW 51002, 'ApplyChanges debe ser 0 (simulacion) o 1 (aplicar).', 1;

IF SESSION_CONTEXT(N'OrionRfc') IS NOT NULL
  THROW 51003, 'La migracion requiere SESSION_CONTEXT OrionRfc en NULL para validar ambos RFC.', 1;

IF EXISTS
(
  SELECT 1
  FROM sys.tables tableInfo
  JOIN sys.schemas schemaInfo ON schemaInfo.schema_id = tableInfo.schema_id
  WHERE schemaInfo.name = 'restaurante'
    AND EXISTS
    (
      SELECT 1
      FROM sys.columns columnInfo
      WHERE columnInfo.object_id = tableInfo.object_id
        AND columnInfo.name = 'Rfc'
    )
    AND
    (
      SELECT COUNT(*)
      FROM sys.security_predicates predicateInfo
      WHERE predicateInfo.object_id = OBJECT_ID('logistica.RfcSecurityPolicy')
        AND predicateInfo.target_object_id = tableInfo.object_id
    ) < 3
)
  THROW 51043, 'Faltan predicados RLS para Restaurante. Aplica 20260713_zz_logistics_rls.sql antes de migrar.', 1;

CREATE TABLE #MaterialManifest
(
  MaterialId int NOT NULL CONSTRAINT PK_MaterialManifest PRIMARY KEY,
  MaterialCode varchar(20) NOT NULL CONSTRAINT UX_MaterialManifest_Code UNIQUE
);

INSERT INTO #MaterialManifest (MaterialId, MaterialCode)
VALUES
  (6928, 'MAT-006928'),
  (6929, 'MAT-006929'),
  (6930, 'MAT-006930'),
  (6931, 'MAT-006931'),
  (6932, 'MAT-006932'),
  (6933, 'MAT-006933'),
  (6934, 'MAT-006934'),
  (6935, 'MAT-006935'),
  (6936, 'MAT-006936'),
  (6937, 'MAT-006937'),
  (6938, 'MAT-006938'),
  (6939, 'MAT-006939'),
  (6940, 'MAT-006940'),
  (6941, 'MAT-006941'),
  (6942, 'MAT-006942'),
  (6943, 'MAT-006943'),
  (6944, 'MAT-006944'),
  (6945, 'MAT-006945'),
  (6946, 'MAT-006946'),
  (6947, 'MAT-006947'),
  (6948, 'MAT-006948'),
  (6949, 'MAT-006949'),
  (6950, 'MAT-006950'),
  (6951, 'MAT-006951'),
  (6952, 'MAT-006952'),
  (6953, 'MAT-006953'),
  (6954, 'MAT-006954'),
  (6955, 'MAT-006955'),
  (6956, 'MAT-006956'),
  (6957, 'MAT-006957'),
  (6958, 'MAT-006958'),
  (6959, 'MAT-006959'),
  (6962, 'MAT-006962'),
  (6969, 'MAT-006969'),
  (6970, 'MAT-006970'),
  (6971, 'MAT-006971'),
  (6972, 'MAT-006972'),
  (6973, 'MAT-006973'),
  (6974, 'MAT-006974'),
  (6975, 'MAT-006975'),
  (6976, 'MAT-006976'),
  (6977, 'MAT-006977'),
  (6978, 'MAT-006978'),
  (6979, 'MAT-006979'),
  (6980, 'MAT-006980'),
  (6981, 'MAT-006981');

IF (SELECT COUNT(*) FROM #MaterialManifest) <> 46
  THROW 51004, 'El manifiesto de materiales debe contener exactamente 46 registros.', 1;

CREATE TABLE #RestaurantExpected
(
  SchemaName sysname NOT NULL,
  TableName sysname NOT NULL,
  SourceRows int NOT NULL,
  TargetRows int NOT NULL,
  CONSTRAINT PK_RestaurantExpected PRIMARY KEY (SchemaName, TableName)
);

DECLARE @SchemaName sysname;
DECLARE @TableName sysname;
DECLARE @QualifiedTable nvarchar(520);
DECLARE @Sql nvarchar(max);
DECLARE @SourceRows int;
DECLARE @TargetRows int;

DECLARE RestaurantInventoryCursor CURSOR LOCAL FAST_FORWARD FOR
SELECT schemaInfo.name, tableInfo.name
FROM sys.tables tableInfo
JOIN sys.schemas schemaInfo ON schemaInfo.schema_id = tableInfo.schema_id
WHERE schemaInfo.name = 'restaurante'
  AND EXISTS
  (
    SELECT 1
    FROM sys.columns columnInfo
    WHERE columnInfo.object_id = tableInfo.object_id
      AND columnInfo.name = 'Rfc'
  )
  AND tableInfo.name NOT IN ('AccountingConfiguration', 'AccountingLink', 'AccountingOrderLink')
ORDER BY tableInfo.name;

OPEN RestaurantInventoryCursor;
FETCH NEXT FROM RestaurantInventoryCursor INTO @SchemaName, @TableName;
WHILE @@FETCH_STATUS = 0
BEGIN
  SET @QualifiedTable = QUOTENAME(@SchemaName) + N'.' + QUOTENAME(@TableName);
  SET @Sql = N'
    SELECT @SourceRowsOut = COALESCE(SUM(CASE WHEN Rfc = @SourceRfc THEN 1 ELSE 0 END), 0),
           @TargetRowsOut = COALESCE(SUM(CASE WHEN Rfc = @TargetRfc THEN 1 ELSE 0 END), 0)
    FROM ' + @QualifiedTable + N';';

  EXEC sys.sp_executesql
    @Sql,
    N'@SourceRfc varchar(50), @TargetRfc varchar(50), @SourceRowsOut int OUTPUT, @TargetRowsOut int OUTPUT',
    @SourceRfc,
    @TargetRfc,
    @SourceRows OUTPUT,
    @TargetRows OUTPUT;

  INSERT INTO #RestaurantExpected (SchemaName, TableName, SourceRows, TargetRows)
  VALUES (@SchemaName, @TableName, @SourceRows, @TargetRows);

  FETCH NEXT FROM RestaurantInventoryCursor INTO @SchemaName, @TableName;
END;
CLOSE RestaurantInventoryCursor;
DEALLOCATE RestaurantInventoryCursor;

DECLARE @SourceMaterialCount int =
(
  SELECT COUNT(*)
  FROM logistica.Material material
  JOIN #MaterialManifest manifest ON manifest.MaterialId = material.Id
  WHERE material.Rfc = @SourceRfc
);
DECLARE @TargetMaterialCount int =
(
  SELECT COUNT(*)
  FROM logistica.Material material
  JOIN #MaterialManifest manifest ON manifest.MaterialId = material.Id
  WHERE material.Rfc = @TargetRfc
);
DECLARE @RestaurantSourceTotal int = (SELECT COALESCE(SUM(SourceRows), 0) FROM #RestaurantExpected);
DECLARE @RestaurantTargetTotal int = (SELECT COALESCE(SUM(TargetRows), 0) FROM #RestaurantExpected);

IF @SourceMaterialCount = 0
   AND @TargetMaterialCount = 46
   AND @RestaurantSourceTotal = 0
BEGIN
  IF EXISTS
  (
    SELECT 1
    FROM #MaterialManifest manifest
    LEFT JOIN logistica.Material material
      ON material.Rfc = @TargetRfc
     AND material.Id = manifest.MaterialId
     AND material.MaterialCode = manifest.MaterialCode
    WHERE material.Id IS NULL
  )
    THROW 51005, 'La migracion parece aplicada, pero el manifiesto destino esta incompleto.', 1;

  SELECT
    'ALREADY_APPLIED' AS MigrationStatus,
    DB_NAME() AS DatabaseName,
    @TargetMaterialCount AS TargetMaterials,
    @RestaurantTargetTotal AS TargetRestaurantRows;
  RETURN;
END;

IF @SourceMaterialCount <> 46 OR @TargetMaterialCount <> 0
  THROW 51006, 'El manifiesto no esta completamente en el RFC origen o ya existe parcialmente en el destino.', 1;

IF @RestaurantTargetTotal <> 0
  THROW 51007, 'Brunos ya contiene datos de Restaurante; se requiere revisar colisiones antes de migrar.', 1;

IF EXISTS
(
  SELECT 1
  FROM #MaterialManifest manifest
  LEFT JOIN logistica.Material material
    ON material.Rfc = @SourceRfc
   AND material.Id = manifest.MaterialId
   AND material.MaterialCode = manifest.MaterialCode
  WHERE material.Id IS NULL
)
  THROW 51008, 'Un ID del manifiesto no coincide con su codigo de material en el RFC origen.', 1;

IF EXISTS
(
  SELECT 1
  FROM logistica.Material material
  JOIN #MaterialManifest manifest ON manifest.MaterialId = material.Id
  WHERE material.Rfc = @SourceRfc
    AND material.CreatedDate < CONVERT(date, '2026-06-29')
)
  THROW 51009, 'El manifiesto contiene un material anterior al 29 de junio de 2026.', 1;

IF (SELECT COUNT(*) FROM logistica.Material material JOIN #MaterialManifest manifest ON manifest.MaterialId = material.Id WHERE material.Rfc = @SourceRfc AND material.ProductType = 'FinishedGood') <> 23
   OR (SELECT COUNT(*) FROM logistica.Material material JOIN #MaterialManifest manifest ON manifest.MaterialId = material.Id WHERE material.Rfc = @SourceRfc AND material.ProductType = 'RawMaterial') <> 23
  THROW 51010, 'Se esperaba un manifiesto de 23 productos terminados y 23 materias primas.', 1;

IF EXISTS
(
  SELECT 1
  FROM logistica.Material sourceMaterial
  JOIN #MaterialManifest manifest ON manifest.MaterialId = sourceMaterial.Id
  JOIN logistica.Material targetMaterial
    ON targetMaterial.Rfc = @TargetRfc
   AND targetMaterial.MaterialCode = sourceMaterial.MaterialCode
  WHERE sourceMaterial.Rfc = @SourceRfc
)
  THROW 51011, 'Existe una colision de codigo de material en Brunos.', 1;

IF (SELECT COUNT(*) FROM restaurante.Site WHERE Rfc = @SourceRfc AND SiteCode = @SourceSiteCode) <> 1
   OR EXISTS (SELECT 1 FROM restaurante.Site WHERE Rfc = @SourceRfc AND SiteCode <> @SourceSiteCode)
  THROW 51012, 'El RFC origen debe contener unicamente la sede BRUNOS-01 dentro de Restaurante.', 1;

IF (SELECT COUNT(*) FROM restaurante.Product WHERE Rfc = @SourceRfc) <> 23
   OR EXISTS
   (
     SELECT 1
     FROM restaurante.Product product
     LEFT JOIN #MaterialManifest manifest ON manifest.MaterialId = product.MaterialId
     WHERE product.Rfc = @SourceRfc
       AND manifest.MaterialId IS NULL
   )
  THROW 51013, 'Todos los productos de Restaurante deben corresponder a los 23 productos terminados del manifiesto.', 1;

IF EXISTS
(
  SELECT 1
  FROM logistica.BomHeader headerInfo
  LEFT JOIN #MaterialManifest manifest ON manifest.MaterialId = headerInfo.ProductMaterialId
  WHERE headerInfo.Rfc = @SourceRfc
    AND manifest.MaterialId IS NULL
)
  THROW 51014, 'Existe un BOM origen cuyo producto no pertenece al manifiesto.', 1;

IF EXISTS
(
  SELECT 1
  FROM logistica.BomComponent component
  LEFT JOIN #MaterialManifest manifest ON manifest.MaterialId = component.ComponentMaterialId
  WHERE component.Rfc = @SourceRfc
    AND manifest.MaterialId IS NULL
)
  THROW 51015, 'Existe un componente de BOM origen que no pertenece al manifiesto.', 1;

IF EXISTS
(
  SELECT 1
  FROM restaurante.ModifierIngredientDelta delta
  LEFT JOIN #MaterialManifest manifest ON manifest.MaterialId = delta.MaterialId
  WHERE delta.Rfc = @SourceRfc
    AND manifest.MaterialId IS NULL
)
  THROW 51016, 'Existe un modificador con un material fuera del manifiesto.', 1;

IF EXISTS
(
  SELECT 1
  FROM logistica.PurchaseOrderLine line
  JOIN #MaterialManifest manifest ON manifest.MaterialId = line.MaterialId
  WHERE line.Rfc = @SourceRfc
)
   OR EXISTS
(
  SELECT 1
  FROM logistica.PurchaseReceiptLine line
  JOIN #MaterialManifest manifest ON manifest.MaterialId = line.MaterialId
  WHERE line.Rfc = @SourceRfc
)
  THROW 51017, 'Aparecieron compras o recepciones para el manifiesto; deben auditarse antes de migrar.', 1;

IF EXISTS
(
  SELECT 1
  FROM logistica.InventoryTransferLine line
  JOIN #MaterialManifest manifest ON manifest.MaterialId = line.MaterialId
  WHERE line.Rfc = @SourceRfc
)
  THROW 51018, 'Aparecieron transferencias de inventario para el manifiesto; deben auditarse antes de migrar.', 1;

IF EXISTS
(
  SELECT 1
  FROM restaurante.AccountingConfiguration
  WHERE Rfc IN (@SourceRfc, @TargetRfc)
)
   OR EXISTS
(
  SELECT 1
  FROM restaurante.AccountingLink
  WHERE Rfc IN (@SourceRfc, @TargetRfc)
)
   OR EXISTS
(
  SELECT 1
  FROM restaurante.AccountingOrderLink
  WHERE Rfc IN (@SourceRfc, @TargetRfc)
)
  THROW 51019, 'Existen configuraciones o vinculos contables; esta migracion los excluye deliberadamente.', 1;

DECLARE @SourceLocationId int =
(
  SELECT Id
  FROM logistica.Location
  WHERE Rfc = @SourceRfc
    AND LocationCode = @SourceLocationCode
);

IF @SourceLocationId IS NULL
  THROW 51020, 'No se encontro la ubicacion origen LOC-000127.', 1;

CREATE TABLE #BomHeader (Id bigint NOT NULL PRIMARY KEY);
CREATE TABLE #BomVersion (Id bigint NOT NULL PRIMARY KEY);
CREATE TABLE #Recipe (Id bigint NOT NULL PRIMARY KEY);
CREATE TABLE #StockBalance (Id int NOT NULL PRIMARY KEY);
CREATE TABLE #StockTransaction (Id int NOT NULL PRIMARY KEY);
CREATE TABLE #CountSession (Id int NOT NULL PRIMARY KEY);
CREATE TABLE #CountLine (Id int NOT NULL PRIMARY KEY);
CREATE TABLE #RecountPlan (Id int NOT NULL PRIMARY KEY);
CREATE TABLE #Reservation (Id bigint NOT NULL PRIMARY KEY);
CREATE TABLE #MaterialLot (Id bigint NOT NULL PRIMARY KEY);
CREATE TABLE #InventoryAdjustment (Id bigint NOT NULL PRIMARY KEY);
CREATE TABLE #ProductionOrder (Id uniqueidentifier NOT NULL PRIMARY KEY);

INSERT INTO #BomHeader (Id)
SELECT headerInfo.Id
FROM logistica.BomHeader headerInfo
JOIN #MaterialManifest manifest ON manifest.MaterialId = headerInfo.ProductMaterialId
WHERE headerInfo.Rfc = @SourceRfc;

INSERT INTO #BomVersion (Id)
SELECT versionInfo.Id
FROM logistica.BomVersion versionInfo
JOIN #BomHeader headerInfo ON headerInfo.Id = versionInfo.BomHeaderId
WHERE versionInfo.Rfc = @SourceRfc;

INSERT INTO #Recipe (Id)
SELECT recipe.Id
FROM logistica.Recipe recipe
JOIN #BomVersion versionInfo ON versionInfo.Id = recipe.BomVersionId
WHERE recipe.Rfc = @SourceRfc;

INSERT INTO #StockBalance (Id)
SELECT balance.Id
FROM logistica.StockBalance balance
JOIN #MaterialManifest manifest ON manifest.MaterialId = balance.MaterialId
WHERE balance.Rfc = @SourceRfc;

INSERT INTO #StockTransaction (Id)
SELECT transactionInfo.Id
FROM logistica.StockTransaction transactionInfo
JOIN #MaterialManifest manifest ON manifest.MaterialId = transactionInfo.MaterialId
WHERE transactionInfo.Rfc = @SourceRfc;

INSERT INTO #CountSession (Id)
SELECT DISTINCT sessionInfo.Id
FROM logistica.PhysicalCountSession sessionInfo
JOIN logistica.PhysicalCountLine line
  ON line.Rfc = sessionInfo.Rfc
 AND line.SessionId = sessionInfo.Id
JOIN #MaterialManifest manifest ON manifest.MaterialId = line.MaterialId
WHERE sessionInfo.Rfc = @SourceRfc;

INSERT INTO #CountLine (Id)
SELECT line.Id
FROM logistica.PhysicalCountLine line
JOIN #CountSession sessionInfo ON sessionInfo.Id = line.SessionId
WHERE line.Rfc = @SourceRfc;

INSERT INTO #RecountPlan (Id)
SELECT planInfo.Id
FROM logistica.PhysicalCountRecountPlan planInfo
JOIN #CountSession sessionInfo ON sessionInfo.Id = planInfo.SessionId
WHERE planInfo.Rfc = @SourceRfc;

INSERT INTO #Reservation (Id)
SELECT DISTINCT reservation.Id
FROM logistica.InventoryReservation reservation
JOIN logistica.InventoryReservationLine line
  ON line.Rfc = reservation.Rfc
 AND line.ReservationId = reservation.Id
JOIN #MaterialManifest manifest ON manifest.MaterialId = line.MaterialId
WHERE reservation.Rfc = @SourceRfc;

INSERT INTO #MaterialLot (Id)
SELECT lot.Id
FROM logistica.MaterialLot lot
JOIN #MaterialManifest manifest ON manifest.MaterialId = lot.MaterialId
WHERE lot.Rfc = @SourceRfc;

INSERT INTO #InventoryAdjustment (Id)
SELECT DISTINCT adjustment.Id
FROM logistica.InventoryAdjustment adjustment
JOIN logistica.InventoryAdjustmentLine line
  ON line.Rfc = adjustment.Rfc
 AND line.AdjustmentId = adjustment.Id
JOIN #MaterialManifest manifest ON manifest.MaterialId = line.MaterialId
WHERE adjustment.Rfc = @SourceRfc;

INSERT INTO #ProductionOrder (Id)
SELECT production.Id
FROM logistica.ProductionOrder production
JOIN #MaterialManifest manifest ON manifest.MaterialId = production.ProductMaterialId
WHERE production.Rfc = @SourceRfc;

IF EXISTS
(
  SELECT 1
  FROM logistica.PhysicalCountLine line
  JOIN #CountSession sessionInfo ON sessionInfo.Id = line.SessionId
  LEFT JOIN #MaterialManifest manifest ON manifest.MaterialId = line.MaterialId
  WHERE line.Rfc = @SourceRfc
    AND manifest.MaterialId IS NULL
)
  THROW 51021, 'Una sesion de conteo contiene materiales ajenos al manifiesto y no se puede mover completa.', 1;

IF EXISTS
(
  SELECT 1
  FROM logistica.InventoryReservationLine line
  JOIN #Reservation reservation ON reservation.Id = line.ReservationId
  LEFT JOIN #MaterialManifest manifest ON manifest.MaterialId = line.MaterialId
  WHERE line.Rfc = @SourceRfc
    AND manifest.MaterialId IS NULL
)
  THROW 51022, 'Una reservacion contiene materiales ajenos al manifiesto y no se puede mover completa.', 1;

IF EXISTS
(
  SELECT 1
  FROM logistica.InventoryAdjustmentLine line
  JOIN #InventoryAdjustment adjustment ON adjustment.Id = line.AdjustmentId
  LEFT JOIN #MaterialManifest manifest ON manifest.MaterialId = line.MaterialId
  WHERE line.Rfc = @SourceRfc
    AND manifest.MaterialId IS NULL
)
  THROW 51023, 'Un ajuste contiene materiales ajenos al manifiesto y no se puede mover completo.', 1;

IF EXISTS
(
  SELECT 1
  FROM logistica.StockBalance balance
  JOIN #StockBalance selected ON selected.Id = balance.Id
  WHERE balance.Rfc = @SourceRfc
    AND balance.LocationId <> @SourceLocationId
)
   OR EXISTS
(
  SELECT 1
  FROM logistica.StockTransaction transactionInfo
  JOIN #StockTransaction selected ON selected.Id = transactionInfo.Id
  WHERE transactionInfo.Rfc = @SourceRfc
    AND transactionInfo.LocationId <> @SourceLocationId
)
   OR EXISTS
(
  SELECT 1
  FROM logistica.PhysicalCountSession sessionInfo
  JOIN #CountSession selected ON selected.Id = sessionInfo.Id
  WHERE sessionInfo.Rfc = @SourceRfc
    AND sessionInfo.LocationId <> @SourceLocationId
)
   OR EXISTS
(
  SELECT 1
  FROM logistica.PhysicalCountLine line
  JOIN #CountLine selected ON selected.Id = line.Id
  WHERE line.Rfc = @SourceRfc
    AND line.LocationId <> @SourceLocationId
)
   OR EXISTS
(
  SELECT 1
  FROM logistica.InventoryReservationLine line
  JOIN #Reservation selected ON selected.Id = line.ReservationId
  WHERE line.Rfc = @SourceRfc
    AND line.LocationId <> @SourceLocationId
)
   OR EXISTS
(
  SELECT 1
  FROM logistica.LotBalance balance
  JOIN #MaterialManifest manifest ON manifest.MaterialId = balance.MaterialId
  WHERE balance.Rfc = @SourceRfc
    AND balance.LocationId <> @SourceLocationId
)
   OR EXISTS
(
  SELECT 1
  FROM logistica.LocationMaterialAttachment attachment
  JOIN #MaterialManifest manifest ON manifest.MaterialId = attachment.MaterialId
  WHERE attachment.Rfc = @SourceRfc
    AND attachment.LocationId <> @SourceLocationId
)
   OR EXISTS
(
  SELECT 1
  FROM logistica.InventoryAdjustmentLine line
  JOIN #InventoryAdjustment selected ON selected.Id = line.AdjustmentId
  WHERE line.Rfc = @SourceRfc
    AND line.LocationId <> @SourceLocationId
)
   OR EXISTS
(
  SELECT 1
  FROM logistica.ProductionOrder production
  JOIN #ProductionOrder selected ON selected.Id = production.Id
  WHERE production.Rfc = @SourceRfc
    AND production.OutputLocationId <> @SourceLocationId
)
   OR EXISTS
(
  SELECT 1
  FROM restaurante.SiteLocationPriority priorityInfo
  WHERE priorityInfo.Rfc = @SourceRfc
    AND priorityInfo.LocationId <> @SourceLocationId
)
  THROW 51024, 'La logistica seleccionada usa una ubicacion distinta de LOC-000127.', 1;

IF EXISTS
(
  SELECT 1
  FROM logistica.StockTransaction transactionInfo
  JOIN #StockTransaction selected ON selected.Id = transactionInfo.Id
  LEFT JOIN #StockBalance balance ON balance.Id = transactionInfo.StockBalanceId
  WHERE transactionInfo.Rfc = @SourceRfc
    AND balance.Id IS NULL
)
  THROW 51025, 'Un movimiento seleccionado apunta a una existencia fuera del manifiesto.', 1;

IF EXISTS
(
  SELECT 1
  FROM restaurante.[Order] orderInfo
  LEFT JOIN #Reservation reservation ON reservation.Id = orderInfo.InventoryReservationId
  WHERE orderInfo.Rfc = @SourceRfc
    AND orderInfo.InventoryReservationId IS NOT NULL
    AND reservation.Id IS NULL
)
  THROW 51026, 'Una orden de Restaurante apunta a una reservacion fuera del manifiesto.', 1;

CREATE TABLE #RfcForeignKey
(
  ForeignKeyId int NOT NULL PRIMARY KEY,
  SchemaName sysname NOT NULL,
  TableName sysname NOT NULL,
  ForeignKeyName sysname NOT NULL
);

INSERT INTO #RfcForeignKey (ForeignKeyId, SchemaName, TableName, ForeignKeyName)
SELECT DISTINCT
  foreignKey.object_id,
  schemaInfo.name,
  tableInfo.name,
  foreignKey.name
FROM sys.foreign_keys foreignKey
JOIN sys.tables tableInfo ON tableInfo.object_id = foreignKey.parent_object_id
JOIN sys.schemas schemaInfo ON schemaInfo.schema_id = tableInfo.schema_id
WHERE schemaInfo.name IN ('logistica', 'restaurante')
  AND EXISTS
  (
    SELECT 1
    FROM sys.foreign_key_columns keyColumn
    JOIN sys.columns childColumn
      ON childColumn.object_id = keyColumn.parent_object_id
     AND childColumn.column_id = keyColumn.parent_column_id
    JOIN sys.columns parentColumn
      ON parentColumn.object_id = keyColumn.referenced_object_id
     AND parentColumn.column_id = keyColumn.referenced_column_id
    WHERE keyColumn.constraint_object_id = foreignKey.object_id
      AND childColumn.name = 'Rfc'
      AND parentColumn.name = 'Rfc'
  );

IF EXISTS
(
  SELECT 1
  FROM #RfcForeignKey selected
  JOIN sys.foreign_keys foreignKey ON foreignKey.object_id = selected.ForeignKeyId
  WHERE foreignKey.is_disabled = 1
     OR foreignKey.is_not_trusted = 1
)
  THROW 51027, 'Una clave foranea por RFC ya esta deshabilitada o no es confiable.', 1;

DECLARE @OriginalStockQuantity decimal(38, 4) =
(
  SELECT COALESCE(SUM(balance.Quantity), 0)
  FROM logistica.StockBalance balance
  JOIN #StockBalance selected ON selected.Id = balance.Id
  WHERE balance.Rfc = @SourceRfc
);
DECLARE @OriginalReservedQuantity decimal(38, 4) =
(
  SELECT COALESCE(SUM(balance.ReservedQuantity), 0)
  FROM logistica.StockBalance balance
  JOIN #StockBalance selected ON selected.Id = balance.Id
  WHERE balance.Rfc = @SourceRfc
);
DECLARE @OriginalOrderTotal decimal(38, 4) =
(
  SELECT COALESCE(SUM(orderInfo.Total), 0)
  FROM restaurante.[Order] orderInfo
  WHERE orderInfo.Rfc = @SourceRfc
);
DECLARE @OriginalPaymentTotal decimal(38, 4) =
(
  SELECT COALESCE(SUM(payment.Amount + payment.TipAmount), 0)
  FROM restaurante.Payment payment
  WHERE payment.Rfc = @SourceRfc
);
DECLARE @OriginalCashMovementTotal decimal(38, 4) =
(
  SELECT COALESCE(SUM(movement.Amount), 0)
  FROM restaurante.CashMovement movement
  WHERE movement.Rfc = @SourceRfc
);

CREATE TABLE #MovedRows
(
  TableName varchar(200) NOT NULL PRIMARY KEY,
  RowsMoved int NOT NULL
);

SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;
BEGIN TRANSACTION;

BEGIN TRY
  EXEC @MigrationLockResult = sys.sp_getapplock
    @Resource = N'OrionERP.20260730.OrionBrunosTransfer',
    @LockMode = 'Exclusive',
    @LockOwner = 'Transaction',
    @LockTimeout = 0;

  IF @MigrationLockResult < 0
    THROW 51028, 'No se pudo obtener el bloqueo exclusivo de la migracion.', 1;

  CREATE TABLE #Supplier (BusinessPartnerId int NOT NULL PRIMARY KEY);
  INSERT INTO #Supplier (BusinessPartnerId)
  SELECT DISTINCT material.BusinessPartnerId
  FROM logistica.Material material
  JOIN #MaterialManifest manifest ON manifest.MaterialId = material.Id
  WHERE material.Rfc = @SourceRfc
    AND material.BusinessPartnerId IS NOT NULL;

  IF (SELECT COUNT(*) FROM #Supplier) <> 5
    THROW 51029, 'El manifiesto debe estar relacionado con exactamente cinco proveedores.', 1;

  IF EXISTS
  (
    SELECT 1
    FROM #Supplier supplier
    LEFT JOIN logistica.VendorProfile profile
      ON profile.Rfc = @SourceRfc
     AND profile.BusinessPartnerId = supplier.BusinessPartnerId
    WHERE profile.BusinessPartnerId IS NULL
  )
    THROW 51030, 'Un proveedor del manifiesto no tiene perfil logistico en el RFC origen.', 1;

  INSERT INTO dbo.BusinessPartnerRfcScope
  (
    Rfc,
    BusinessPartnerId,
    IsActive,
    CreatedAt,
    CreatedBy
  )
  SELECT
    @TargetRfc,
    supplier.BusinessPartnerId,
    1,
    SYSUTCDATETIME(),
    @MigrationUser
  FROM #Supplier supplier
  WHERE NOT EXISTS
  (
    SELECT 1
    FROM dbo.BusinessPartnerRfcScope targetScope
    WHERE targetScope.Rfc = @TargetRfc
      AND targetScope.BusinessPartnerId = supplier.BusinessPartnerId
  );

  INSERT INTO logistica.VendorProfile
  (
    BusinessPartnerId,
    PaymentTerms,
    DefaultLeadTimeDays,
    IsApproved,
    Notes,
    CreatedAt,
    UpdatedAt,
    Rfc
  )
  SELECT
    sourceProfile.BusinessPartnerId,
    sourceProfile.PaymentTerms,
    sourceProfile.DefaultLeadTimeDays,
    sourceProfile.IsApproved,
    sourceProfile.Notes,
    sourceProfile.CreatedAt,
    sourceProfile.UpdatedAt,
    @TargetRfc
  FROM logistica.VendorProfile sourceProfile
  JOIN #Supplier supplier ON supplier.BusinessPartnerId = sourceProfile.BusinessPartnerId
  WHERE sourceProfile.Rfc = @SourceRfc
    AND NOT EXISTS
    (
      SELECT 1
      FROM logistica.VendorProfile targetProfile
      WHERE targetProfile.Rfc = @TargetRfc
        AND targetProfile.BusinessPartnerId = sourceProfile.BusinessPartnerId
    );

  CREATE TABLE #SourceCategory
  (
    SourceCategoryId int NOT NULL PRIMARY KEY,
    CategoryName varchar(100) NOT NULL
  );

  INSERT INTO #SourceCategory (SourceCategoryId, CategoryName)
  SELECT DISTINCT category.Id, category.CategoryName
  FROM logistica.Material material
  JOIN #MaterialManifest manifest ON manifest.MaterialId = material.Id
  JOIN logistica.MaterialCategory category
    ON category.Rfc = material.Rfc
   AND category.Id = material.CategoryId
  WHERE material.Rfc = @SourceRfc;

  IF (SELECT COUNT(*) FROM #SourceCategory) <> 5
    THROW 51031, 'El manifiesto debe utilizar exactamente cinco categorias.', 1;

  INSERT INTO logistica.MaterialCategory
  (
    LegacyCategoryId,
    CategoryName,
    Description,
    IsActive,
    Rfc
  )
  SELECT
    NULL,
    sourceCategory.CategoryName,
    sourceCategory.Description,
    sourceCategory.IsActive,
    @TargetRfc
  FROM logistica.MaterialCategory sourceCategory
  JOIN #SourceCategory selected ON selected.SourceCategoryId = sourceCategory.Id
  WHERE sourceCategory.Rfc = @SourceRfc
    AND NOT EXISTS
    (
      SELECT 1
      FROM logistica.MaterialCategory targetCategory
      WHERE targetCategory.Rfc = @TargetRfc
        AND targetCategory.CategoryName = sourceCategory.CategoryName
    );

  CREATE TABLE #CategoryMap
  (
    SourceCategoryId int NOT NULL PRIMARY KEY,
    TargetCategoryId int NOT NULL
  );

  INSERT INTO #CategoryMap (SourceCategoryId, TargetCategoryId)
  SELECT sourceCategory.SourceCategoryId, targetCategory.Id
  FROM #SourceCategory sourceCategory
  JOIN logistica.MaterialCategory targetCategory
    ON targetCategory.Rfc = @TargetRfc
   AND targetCategory.CategoryName = sourceCategory.CategoryName;

  IF (SELECT COUNT(*) FROM #CategoryMap) <> 5
    THROW 51032, 'No se pudo construir el mapa completo de categorias para Brunos.', 1;

  DECLARE @TargetLocationId int =
  (
    SELECT Id
    FROM logistica.Location
    WHERE Rfc = @TargetRfc
      AND LocationCode = @TargetLocationCode
  );

  IF @TargetLocationId IS NOT NULL
     AND EXISTS
     (
       SELECT 1
       FROM logistica.Location
       WHERE Id = @TargetLocationId
         AND (LocationName <> @TargetLocationName OR LocationType <> 'Storage')
     )
    THROW 51033, 'La ubicacion destino ya existe con nombre o tipo diferente.', 1;

  IF @TargetLocationId IS NULL
  BEGIN
    INSERT INTO logistica.Location
    (
      LocationCode,
      LegacyEspacioId,
      LegacyRoomId,
      ParentLocationId,
      RoomId,
      LocationName,
      LocationType,
      Description,
      IsInventoryEnabled,
      IsActive,
      Rfc
    )
    VALUES
    (
      @TargetLocationCode,
      NULL,
      NULL,
      NULL,
      NULL,
      @TargetLocationName,
      'Storage',
      'Ubicacion operativa de inventario para la sede BRUNOS-01.',
      1,
      1,
      @TargetRfc
    );

    SET @TargetLocationId = CONVERT(int, SCOPE_IDENTITY());
  END;

  DECLARE ConstraintDisableCursor CURSOR LOCAL FAST_FORWARD FOR
  SELECT SchemaName, TableName, ForeignKeyName
  FROM #RfcForeignKey
  ORDER BY SchemaName, TableName, ForeignKeyName;

  DECLARE @ForeignKeyName sysname;
  OPEN ConstraintDisableCursor;
  FETCH NEXT FROM ConstraintDisableCursor INTO @SchemaName, @TableName, @ForeignKeyName;
  WHILE @@FETCH_STATUS = 0
  BEGIN
    SET @Sql =
      N'ALTER TABLE ' + QUOTENAME(@SchemaName) + N'.' + QUOTENAME(@TableName)
      + N' NOCHECK CONSTRAINT ' + QUOTENAME(@ForeignKeyName) + N';';
    EXEC sys.sp_executesql @Sql;
    FETCH NEXT FROM ConstraintDisableCursor INTO @SchemaName, @TableName, @ForeignKeyName;
  END;
  CLOSE ConstraintDisableCursor;
  DEALLOCATE ConstraintDisableCursor;

  DECLARE @RowsMoved int;

  UPDATE stepInfo
  SET Rfc = @TargetRfc
  FROM logistica.RecipeStep stepInfo
  JOIN #Recipe recipe ON recipe.Id = stepInfo.RecipeId
  WHERE stepInfo.Rfc = @SourceRfc;
  SET @RowsMoved = @@ROWCOUNT;
  INSERT INTO #MovedRows VALUES ('logistica.RecipeStep', @RowsMoved);

  UPDATE recipeInfo
  SET Rfc = @TargetRfc
  FROM logistica.Recipe recipeInfo
  JOIN #Recipe selected ON selected.Id = recipeInfo.Id
  WHERE recipeInfo.Rfc = @SourceRfc;
  SET @RowsMoved = @@ROWCOUNT;
  INSERT INTO #MovedRows VALUES ('logistica.Recipe', @RowsMoved);

  UPDATE component
  SET Rfc = @TargetRfc
  FROM logistica.BomComponent component
  JOIN #BomVersion versionInfo ON versionInfo.Id = component.BomVersionId
  WHERE component.Rfc = @SourceRfc;
  SET @RowsMoved = @@ROWCOUNT;
  INSERT INTO #MovedRows VALUES ('logistica.BomComponent', @RowsMoved);

  UPDATE versionInfo
  SET Rfc = @TargetRfc
  FROM logistica.BomVersion versionInfo
  JOIN #BomVersion selected ON selected.Id = versionInfo.Id
  WHERE versionInfo.Rfc = @SourceRfc;
  SET @RowsMoved = @@ROWCOUNT;
  INSERT INTO #MovedRows VALUES ('logistica.BomVersion', @RowsMoved);

  UPDATE headerInfo
  SET Rfc = @TargetRfc
  FROM logistica.BomHeader headerInfo
  JOIN #BomHeader selected ON selected.Id = headerInfo.Id
  WHERE headerInfo.Rfc = @SourceRfc;
  SET @RowsMoved = @@ROWCOUNT;
  INSERT INTO #MovedRows VALUES ('logistica.BomHeader', @RowsMoved);

  UPDATE conversionInfo
  SET Rfc = @TargetRfc
  FROM logistica.MaterialUnitConversion conversionInfo
  JOIN #MaterialManifest manifest ON manifest.MaterialId = conversionInfo.MaterialId
  WHERE conversionInfo.Rfc = @SourceRfc;
  SET @RowsMoved = @@ROWCOUNT;
  INSERT INTO #MovedRows VALUES ('logistica.MaterialUnitConversion', @RowsMoved);

  UPDATE allergenInfo
  SET Rfc = @TargetRfc
  FROM logistica.MaterialAllergen allergenInfo
  JOIN #MaterialManifest manifest ON manifest.MaterialId = allergenInfo.MaterialId
  WHERE allergenInfo.Rfc = @SourceRfc;
  SET @RowsMoved = @@ROWCOUNT;
  INSERT INTO #MovedRows VALUES ('logistica.MaterialAllergen', @RowsMoved);

  UPDATE lotInfo
  SET Rfc = @TargetRfc
  FROM logistica.MaterialLot lotInfo
  JOIN #MaterialLot selected ON selected.Id = lotInfo.Id
  WHERE lotInfo.Rfc = @SourceRfc;
  SET @RowsMoved = @@ROWCOUNT;
  INSERT INTO #MovedRows VALUES ('logistica.MaterialLot', @RowsMoved);

  UPDATE balance
  SET Rfc = @TargetRfc,
      LocationId = @TargetLocationId
  FROM logistica.LotBalance balance
  JOIN #MaterialManifest manifest ON manifest.MaterialId = balance.MaterialId
  WHERE balance.Rfc = @SourceRfc;
  SET @RowsMoved = @@ROWCOUNT;
  INSERT INTO #MovedRows VALUES ('logistica.LotBalance', @RowsMoved);

  UPDATE transactionInfo
  SET Rfc = @TargetRfc,
      LocationId = @TargetLocationId
  FROM logistica.StockTransaction transactionInfo
  JOIN #StockTransaction selected ON selected.Id = transactionInfo.Id
  WHERE transactionInfo.Rfc = @SourceRfc;
  SET @RowsMoved = @@ROWCOUNT;
  INSERT INTO #MovedRows VALUES ('logistica.StockTransaction', @RowsMoved);

  UPDATE balance
  SET Rfc = @TargetRfc,
      LocationId = @TargetLocationId
  FROM logistica.StockBalance balance
  JOIN #StockBalance selected ON selected.Id = balance.Id
  WHERE balance.Rfc = @SourceRfc;
  SET @RowsMoved = @@ROWCOUNT;
  INSERT INTO #MovedRows VALUES ('logistica.StockBalance', @RowsMoved);

  UPDATE attachment
  SET Rfc = @TargetRfc
  FROM logistica.PhysicalCountAttachment attachment
  JOIN #CountLine selected ON selected.Id = attachment.PhysicalCountLineId
  WHERE attachment.Rfc = @SourceRfc;
  SET @RowsMoved = @@ROWCOUNT;
  INSERT INTO #MovedRows VALUES ('logistica.PhysicalCountAttachment', @RowsMoved);

  UPDATE lotLine
  SET Rfc = @TargetRfc
  FROM logistica.PhysicalCountLotLine lotLine
  JOIN #CountLine selected ON selected.Id = lotLine.PhysicalCountLineId
  WHERE lotLine.Rfc = @SourceRfc;
  SET @RowsMoved = @@ROWCOUNT;
  INSERT INTO #MovedRows VALUES ('logistica.PhysicalCountLotLine', @RowsMoved);

  UPDATE planLine
  SET Rfc = @TargetRfc
  FROM logistica.PhysicalCountRecountPlanLine planLine
  JOIN #RecountPlan selected ON selected.Id = planLine.RecountPlanId
  WHERE planLine.Rfc = @SourceRfc;
  SET @RowsMoved = @@ROWCOUNT;
  INSERT INTO #MovedRows VALUES ('logistica.PhysicalCountRecountPlanLine', @RowsMoved);

  UPDATE planInfo
  SET Rfc = @TargetRfc
  FROM logistica.PhysicalCountRecountPlan planInfo
  JOIN #RecountPlan selected ON selected.Id = planInfo.Id
  WHERE planInfo.Rfc = @SourceRfc;
  SET @RowsMoved = @@ROWCOUNT;
  INSERT INTO #MovedRows VALUES ('logistica.PhysicalCountRecountPlan', @RowsMoved);

  UPDATE line
  SET Rfc = @TargetRfc,
      LocationId = @TargetLocationId
  FROM logistica.PhysicalCountLine line
  JOIN #CountLine selected ON selected.Id = line.Id
  WHERE line.Rfc = @SourceRfc;
  SET @RowsMoved = @@ROWCOUNT;
  INSERT INTO #MovedRows VALUES ('logistica.PhysicalCountLine', @RowsMoved);

  UPDATE sessionInfo
  SET Rfc = @TargetRfc,
      LocationId = @TargetLocationId
  FROM logistica.PhysicalCountSession sessionInfo
  JOIN #CountSession selected ON selected.Id = sessionInfo.Id
  WHERE sessionInfo.Rfc = @SourceRfc;
  SET @RowsMoved = @@ROWCOUNT;
  INSERT INTO #MovedRows VALUES ('logistica.PhysicalCountSession', @RowsMoved);

  UPDATE reservationLine
  SET Rfc = @TargetRfc,
      LocationId = @TargetLocationId
  FROM logistica.InventoryReservationLine reservationLine
  JOIN #Reservation selected ON selected.Id = reservationLine.ReservationId
  WHERE reservationLine.Rfc = @SourceRfc;
  SET @RowsMoved = @@ROWCOUNT;
  INSERT INTO #MovedRows VALUES ('logistica.InventoryReservationLine', @RowsMoved);

  UPDATE reservation
  SET Rfc = @TargetRfc
  FROM logistica.InventoryReservation reservation
  JOIN #Reservation selected ON selected.Id = reservation.Id
  WHERE reservation.Rfc = @SourceRfc;
  SET @RowsMoved = @@ROWCOUNT;
  INSERT INTO #MovedRows VALUES ('logistica.InventoryReservation', @RowsMoved);

  UPDATE attachment
  SET Rfc = @TargetRfc,
      LocationId = @TargetLocationId
  FROM logistica.LocationMaterialAttachment attachment
  JOIN #MaterialManifest manifest ON manifest.MaterialId = attachment.MaterialId
  WHERE attachment.Rfc = @SourceRfc;
  SET @RowsMoved = @@ROWCOUNT;
  INSERT INTO #MovedRows VALUES ('logistica.LocationMaterialAttachment', @RowsMoved);

  UPDATE adjustmentLine
  SET Rfc = @TargetRfc,
      LocationId = @TargetLocationId
  FROM logistica.InventoryAdjustmentLine adjustmentLine
  JOIN #InventoryAdjustment selected ON selected.Id = adjustmentLine.AdjustmentId
  WHERE adjustmentLine.Rfc = @SourceRfc;
  SET @RowsMoved = @@ROWCOUNT;
  INSERT INTO #MovedRows VALUES ('logistica.InventoryAdjustmentLine', @RowsMoved);

  UPDATE adjustment
  SET Rfc = @TargetRfc
  FROM logistica.InventoryAdjustment adjustment
  JOIN #InventoryAdjustment selected ON selected.Id = adjustment.Id
  WHERE adjustment.Rfc = @SourceRfc;
  SET @RowsMoved = @@ROWCOUNT;
  INSERT INTO #MovedRows VALUES ('logistica.InventoryAdjustment', @RowsMoved);

  UPDATE production
  SET Rfc = @TargetRfc,
      OutputLocationId = @TargetLocationId
  FROM logistica.ProductionOrder production
  JOIN #ProductionOrder selected ON selected.Id = production.Id
  WHERE production.Rfc = @SourceRfc;
  SET @RowsMoved = @@ROWCOUNT;
  INSERT INTO #MovedRows VALUES ('logistica.ProductionOrder', @RowsMoved);

  UPDATE priorityInfo
  SET LocationId = @TargetLocationId
  FROM restaurante.SiteLocationPriority priorityInfo
  WHERE priorityInfo.Rfc = @SourceRfc;

  DECLARE RestaurantMoveCursor CURSOR LOCAL FAST_FORWARD FOR
  SELECT SchemaName, TableName, SourceRows
  FROM #RestaurantExpected
  ORDER BY TableName;

  OPEN RestaurantMoveCursor;
  FETCH NEXT FROM RestaurantMoveCursor INTO @SchemaName, @TableName, @SourceRows;
  WHILE @@FETCH_STATUS = 0
  BEGIN
    SET @QualifiedTable = QUOTENAME(@SchemaName) + N'.' + QUOTENAME(@TableName);
    SET @Sql =
      N'UPDATE ' + @QualifiedTable + N'
        SET Rfc = @TargetRfc
        WHERE Rfc = @SourceRfc;
        SET @RowsMovedOut = @@ROWCOUNT;';

    EXEC sys.sp_executesql
      @Sql,
      N'@SourceRfc varchar(50), @TargetRfc varchar(50), @RowsMovedOut int OUTPUT',
      @SourceRfc,
      @TargetRfc,
      @RowsMoved OUTPUT;

    IF @RowsMoved <> @SourceRows
      THROW 51034, 'Cambio el numero de filas de Restaurante durante la migracion.', 1;

    INSERT INTO #MovedRows (TableName, RowsMoved)
    VALUES ('restaurante.' + @TableName, @RowsMoved);

    FETCH NEXT FROM RestaurantMoveCursor INTO @SchemaName, @TableName, @SourceRows;
  END;
  CLOSE RestaurantMoveCursor;
  DEALLOCATE RestaurantMoveCursor;

  UPDATE material
  SET Rfc = @TargetRfc,
      CategoryId = categoryMap.TargetCategoryId
  FROM logistica.Material material
  JOIN #MaterialManifest manifest ON manifest.MaterialId = material.Id
  JOIN #CategoryMap categoryMap ON categoryMap.SourceCategoryId = material.CategoryId
  WHERE material.Rfc = @SourceRfc;
  SET @RowsMoved = @@ROWCOUNT;
  INSERT INTO #MovedRows VALUES ('logistica.Material', @RowsMoved);

  IF @RowsMoved <> 46
    THROW 51035, 'No se transfirieron los 46 materiales del manifiesto.', 1;

  DELETE sourceCategory
  FROM logistica.MaterialCategory sourceCategory
  JOIN #SourceCategory selected ON selected.SourceCategoryId = sourceCategory.Id
  WHERE sourceCategory.Rfc = @SourceRfc
    AND NOT EXISTS
    (
      SELECT 1
      FROM logistica.Material remainingMaterial
      WHERE remainingMaterial.Rfc = @SourceRfc
        AND remainingMaterial.CategoryId = sourceCategory.Id
    );

  DECLARE ConstraintEnableCursor CURSOR LOCAL FAST_FORWARD FOR
  SELECT SchemaName, TableName, ForeignKeyName
  FROM #RfcForeignKey
  ORDER BY SchemaName, TableName, ForeignKeyName;

  OPEN ConstraintEnableCursor;
  FETCH NEXT FROM ConstraintEnableCursor INTO @SchemaName, @TableName, @ForeignKeyName;
  WHILE @@FETCH_STATUS = 0
  BEGIN
    SET @Sql =
      N'ALTER TABLE ' + QUOTENAME(@SchemaName) + N'.' + QUOTENAME(@TableName)
      + N' WITH CHECK CHECK CONSTRAINT ' + QUOTENAME(@ForeignKeyName) + N';';
    EXEC sys.sp_executesql @Sql;
    FETCH NEXT FROM ConstraintEnableCursor INTO @SchemaName, @TableName, @ForeignKeyName;
  END;
  CLOSE ConstraintEnableCursor;
  DEALLOCATE ConstraintEnableCursor;

  IF EXISTS
  (
    SELECT 1
    FROM #RfcForeignKey selected
    JOIN sys.foreign_keys foreignKey ON foreignKey.object_id = selected.ForeignKeyId
    WHERE foreignKey.is_disabled = 1
       OR foreignKey.is_not_trusted = 1
  )
    THROW 51036, 'Una clave foranea por RFC no quedo habilitada y confiable.', 1;

  IF EXISTS
  (
    SELECT 1
    FROM #MaterialManifest manifest
    LEFT JOIN logistica.Material material
      ON material.Rfc = @TargetRfc
     AND material.Id = manifest.MaterialId
     AND material.MaterialCode = manifest.MaterialCode
    WHERE material.Id IS NULL
  )
     OR EXISTS
  (
    SELECT 1
    FROM logistica.Material material
    JOIN #MaterialManifest manifest ON manifest.MaterialId = material.Id
    WHERE material.Rfc = @SourceRfc
  )
    THROW 51037, 'La validacion final del manifiesto de materiales fallo.', 1;

  DECLARE RestaurantValidationCursor CURSOR LOCAL FAST_FORWARD FOR
  SELECT SchemaName, TableName, SourceRows
  FROM #RestaurantExpected
  ORDER BY TableName;

  OPEN RestaurantValidationCursor;
  FETCH NEXT FROM RestaurantValidationCursor INTO @SchemaName, @TableName, @SourceRows;
  WHILE @@FETCH_STATUS = 0
  BEGIN
    SET @QualifiedTable = QUOTENAME(@SchemaName) + N'.' + QUOTENAME(@TableName);
    SET @Sql = N'
      SELECT @SourceRowsOut = COALESCE(SUM(CASE WHEN Rfc = @SourceRfc THEN 1 ELSE 0 END), 0),
             @TargetRowsOut = COALESCE(SUM(CASE WHEN Rfc = @TargetRfc THEN 1 ELSE 0 END), 0)
      FROM ' + @QualifiedTable + N';';

    EXEC sys.sp_executesql
      @Sql,
      N'@SourceRfc varchar(50), @TargetRfc varchar(50), @SourceRowsOut int OUTPUT, @TargetRowsOut int OUTPUT',
      @SourceRfc,
      @TargetRfc,
      @SourceRows OUTPUT,
      @TargetRows OUTPUT;

    IF @SourceRows <> 0
       OR @TargetRows <> (SELECT SourceRows FROM #RestaurantExpected WHERE SchemaName = @SchemaName AND TableName = @TableName)
      THROW 51038, 'La validacion final por tabla de Restaurante fallo.', 1;

    FETCH NEXT FROM RestaurantValidationCursor INTO @SchemaName, @TableName, @SourceRows;
  END;
  CLOSE RestaurantValidationCursor;
  DEALLOCATE RestaurantValidationCursor;

  IF (SELECT COALESCE(SUM(balance.Quantity), 0) FROM logistica.StockBalance balance JOIN #StockBalance selected ON selected.Id = balance.Id WHERE balance.Rfc = @TargetRfc) <> @OriginalStockQuantity
     OR (SELECT COALESCE(SUM(balance.ReservedQuantity), 0) FROM logistica.StockBalance balance JOIN #StockBalance selected ON selected.Id = balance.Id WHERE balance.Rfc = @TargetRfc) <> @OriginalReservedQuantity
    THROW 51039, 'Las cantidades de inventario cambiaron durante la transferencia.', 1;

  IF (SELECT COALESCE(SUM(orderInfo.Total), 0) FROM restaurante.[Order] orderInfo WHERE orderInfo.Rfc = @TargetRfc) <> @OriginalOrderTotal
     OR (SELECT COALESCE(SUM(payment.Amount + payment.TipAmount), 0) FROM restaurante.Payment payment WHERE payment.Rfc = @TargetRfc) <> @OriginalPaymentTotal
     OR (SELECT COALESCE(SUM(movement.Amount), 0) FROM restaurante.CashMovement movement WHERE movement.Rfc = @TargetRfc) <> @OriginalCashMovementTotal
    THROW 51040, 'Los totales operativos de Restaurante cambiaron durante la transferencia.', 1;

  IF (SELECT COUNT(*) FROM #Supplier supplier JOIN dbo.BusinessPartnerRfcScope scopeInfo ON scopeInfo.Rfc = @TargetRfc AND scopeInfo.BusinessPartnerId = supplier.BusinessPartnerId) <> 5
     OR (SELECT COUNT(*) FROM #Supplier supplier JOIN logistica.VendorProfile profile ON profile.Rfc = @TargetRfc AND profile.BusinessPartnerId = supplier.BusinessPartnerId) <> 5
    THROW 51041, 'No se compartieron correctamente los cinco proveedores con Brunos.', 1;

  IF EXISTS
  (
    SELECT 1
    FROM restaurante.AccountingConfiguration
    WHERE Rfc = @TargetRfc
  )
     OR EXISTS
  (
    SELECT 1
    FROM restaurante.AccountingLink
    WHERE Rfc = @TargetRfc
  )
     OR EXISTS
  (
    SELECT 1
    FROM restaurante.AccountingOrderLink
    WHERE Rfc = @TargetRfc
  )
    THROW 51042, 'Se detectaron datos contables en Brunos despues de la transferencia.', 1;

  SELECT TableName, RowsMoved
  FROM #MovedRows
  WHERE RowsMoved > 0
  ORDER BY TableName;

  SELECT
    DB_NAME() AS DatabaseName,
    CASE WHEN @ApplyChanges = 1 THEN 'READY_TO_COMMIT' ELSE 'DRY_RUN_VALIDATED' END AS ValidationStatus,
    46 AS Materials,
    (SELECT COUNT(*) FROM #Supplier) AS SharedSuppliers,
    (SELECT COUNT(*) FROM #SourceCategory) AS MaterialCategories,
    (SELECT COUNT(*) FROM #BomHeader) AS BomHeaders,
    (SELECT COUNT(*) FROM #Reservation) AS InventoryReservations,
    (SELECT COUNT(*) FROM #CountSession) AS PhysicalCountSessions,
    @OriginalStockQuantity AS StockQuantity,
    @OriginalReservedQuantity AS ReservedQuantity,
    @OriginalOrderTotal AS RestaurantOrderTotal,
    @OriginalPaymentTotal AS RestaurantPaymentAndTipTotal,
    @OriginalCashMovementTotal AS CashMovementTotal,
    @TargetLocationCode AS TargetLocationCode;

  IF @ApplyChanges = 1
  BEGIN
    COMMIT TRANSACTION;
    SELECT 'COMMITTED' AS MigrationStatus, DB_NAME() AS DatabaseName;
  END
  ELSE
  BEGIN
    ROLLBACK TRANSACTION;
    SELECT 'DRY_RUN_ROLLED_BACK' AS MigrationStatus, DB_NAME() AS DatabaseName;
  END;
END TRY
BEGIN CATCH
  IF XACT_STATE() <> 0
    ROLLBACK TRANSACTION;
  THROW;
END CATCH;
