/*
  Renombra el identificador de RFC de Bruno's en toda la base de datos:

    OHM260707L26 -> BRUNOS260707L26

  Requiere SQLCMD y dos variables explicitas:

    sqlcmd ... -v ExpectedDatabase="Orion_Sandbox" ApplyChanges="0" -i 20260730_rename_bruno_rfc_database_wide.sql
    sqlcmd ... -v ExpectedDatabase="Orion_Sandbox" ApplyChanges="1" -i 20260730_rename_bruno_rfc_database_wide.sql

  ApplyChanges=0 ejecuta la migracion y todas sus validaciones dentro de una
  transaccion que se revierte al final. ApplyChanges=1 confirma los cambios.

  El script:
    - inspecciona todas las columnas de texto de la base;
    - exige el manifiesto revisado de 42 columnas;
    - rechaza cualquier coexistencia del identificador anterior y el nuevo;
    - actualiza claims, contabilidad, proveedores, logistica y restaurante;
    - reemplaza el RFC dentro de los JSON historicos de auditoria;
    - deshabilita solamente las FK y CHECK afectadas y las vuelve a validar;
    - comprueba que no quede ninguna referencia al identificador anterior;
    - es idempotente cuando el cambio ya fue aplicado completamente.

  Antes de usar ApplyChanges=1 en produccion se requiere un respaldo verificado,
  una ventana sin capturas y autorizacion explicita.
*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET XACT_ABORT ON;
SET NOCOUNT ON;

DECLARE @ExpectedDatabase sysname = N'$(ExpectedDatabase)';
DECLARE @ApplyChanges bit = TRY_CONVERT(bit, N'$(ApplyChanges)');
DECLARE @OldRfc nvarchar(50) = N'OHM260707L26';
DECLARE @NewRfc nvarchar(50) = N'BRUNOS260707L26';
DECLARE @MigrationLockResult int;
DECLARE @Sql nvarchar(max);
DECLARE @ObjectId int;
DECLARE @ColumnId int;
DECLARE @SchemaName sysname;
DECLARE @TableName sysname;
DECLARE @ColumnName sysname;
DECLARE @DataType sysname;
DECLARE @QualifiedTable nvarchar(520);
DECLARE @OldExact bigint;
DECLARE @OldContains bigint;
DECLARE @NewExact bigint;
DECLARE @NewContains bigint;
DECLARE @RowsUpdated bigint;
DECLARE @InvalidAuditJsonBefore bigint;
DECLARE @InvalidAuditJsonAfter bigint;

IF @ExpectedDatabase NOT IN (N'Orion_Sandbox', N'Orion_SandBox', N'grupocarpio')
  THROW 51100, 'ExpectedDatabase debe ser Orion_Sandbox o grupocarpio.', 1;

IF DB_NAME() <> @ExpectedDatabase
  THROW 51101, 'La base conectada no coincide con ExpectedDatabase.', 1;

IF @ApplyChanges IS NULL
  THROW 51102, 'ApplyChanges debe ser 0 (simulacion) o 1 (aplicar).', 1;

IF SESSION_CONTEXT(N'OrionRfc') IS NOT NULL
  THROW 51103, 'La migracion requiere SESSION_CONTEXT OrionRfc en NULL.', 1;

IF LEN(@NewRfc) > 50
  THROW 51104, 'El nuevo identificador excede la longitud de trabajo permitida.', 1;

SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;

BEGIN TRY
  BEGIN TRANSACTION;

  EXEC @MigrationLockResult = sys.sp_getapplock
    @Resource = N'OrionERP:RenameRfc:OHM260707L26:BRUNOS260707L26',
    @LockMode = N'Exclusive',
    @LockOwner = N'Transaction',
    @LockTimeout = 15000;

  IF @MigrationLockResult < 0
    THROW 51105, 'No fue posible obtener el bloqueo exclusivo de migracion.', 1;

  CREATE TABLE #ExpectedColumns
  (
    SchemaName sysname NOT NULL,
    TableName sysname NOT NULL,
    ColumnName sysname NOT NULL,
    IsEmbedded bit NOT NULL,
    CONSTRAINT PK_ExpectedRfcRenameColumn
      PRIMARY KEY (SchemaName, TableName, ColumnName)
  );

  INSERT INTO #ExpectedColumns (SchemaName, TableName, ColumnName, IsEmbedded)
  VALUES
    (N'auth', N'AspNetUserClaims', N'ClaimValue', 0),
    (N'contabilidad', N'TransaccionesRegistroContableAudit', N'NewRowJson', 1),
    (N'contabilidad', N'TransaccionesRegistroContableAudit', N'OldRowJson', 1),
    (N'dbo', N'BusinessPartnerRfcScope', N'Rfc', 0),
    (N'dbo', N'CuentasContables', N'RFC', 0),
    (N'dbo', N'Transacciones', N'RFC', 0),
    (N'logistica', N'BomComponent', N'Rfc', 0),
    (N'logistica', N'BomHeader', N'Rfc', 0),
    (N'logistica', N'BomVersion', N'Rfc', 0),
    (N'logistica', N'InventoryReservation', N'Rfc', 0),
    (N'logistica', N'InventoryReservationLine', N'Rfc', 0),
    (N'logistica', N'Location', N'Rfc', 0),
    (N'logistica', N'Material', N'Rfc', 0),
    (N'logistica', N'MaterialCategory', N'Rfc', 0),
    (N'logistica', N'PhysicalCountLine', N'Rfc', 0),
    (N'logistica', N'PhysicalCountRecountPlan', N'Rfc', 0),
    (N'logistica', N'PhysicalCountRecountPlanLine', N'Rfc', 0),
    (N'logistica', N'PhysicalCountSession', N'Rfc', 0),
    (N'logistica', N'Recipe', N'Rfc', 0),
    (N'logistica', N'RecipeStep', N'Rfc', 0),
    (N'logistica', N'StockBalance', N'Rfc', 0),
    (N'logistica', N'StockTransaction', N'Rfc', 0),
    (N'logistica', N'VendorProfile', N'Rfc', 0),
    (N'restaurante', N'CashMovement', N'Rfc', 0),
    (N'restaurante', N'CashRegister', N'Rfc', 0),
    (N'restaurante', N'CashShift', N'Rfc', 0),
    (N'restaurante', N'DailySequence', N'Rfc', 0),
    (N'restaurante', N'EventOutbox', N'Rfc', 0),
    (N'restaurante', N'Menu', N'Rfc', 0),
    (N'restaurante', N'MenuItem', N'Rfc', 0),
    (N'restaurante', N'MenuSection', N'Rfc', 0),
    (N'restaurante', N'Order', N'Rfc', 0),
    (N'restaurante', N'OrderEvent', N'Rfc', 0),
    (N'restaurante', N'OrderLine', N'Rfc', 0),
    (N'restaurante', N'Payment', N'Rfc', 0),
    (N'restaurante', N'PaymentRefund', N'Rfc', 0),
    (N'restaurante', N'Product', N'Rfc', 0),
    (N'restaurante', N'ProductCard', N'Rfc', 0),
    (N'restaurante', N'QuickPin', N'Rfc', 0),
    (N'restaurante', N'QuickPinAttempt', N'Rfc', 0),
    (N'restaurante', N'Site', N'Rfc', 0),
    (N'restaurante', N'SupervisorAuthorization', N'Rfc', 0);

  IF (SELECT COUNT(*) FROM #ExpectedColumns) <> 42
    THROW 51106, 'El manifiesto debe contener exactamente 42 columnas.', 1;

  CREATE TABLE #TargetColumns
  (
    ObjectId int NOT NULL,
    ColumnId int NOT NULL,
    SchemaName sysname NOT NULL,
    TableName sysname NOT NULL,
    ColumnName sysname NOT NULL,
    DataType sysname NOT NULL,
    MaxCharacters int NOT NULL,
    IsEmbedded bit NOT NULL,
    OldExact bigint NOT NULL,
    OldContains bigint NOT NULL,
    NewExact bigint NOT NULL,
    NewContains bigint NOT NULL,
    RowsUpdated bigint NOT NULL
      CONSTRAINT DF_TargetColumns_RowsUpdated DEFAULT (0),
    CONSTRAINT PK_TargetRfcRenameColumn PRIMARY KEY (ObjectId, ColumnId),
    CONSTRAINT UX_TargetRfcRenameColumn
      UNIQUE (SchemaName, TableName, ColumnName)
  );

  DECLARE TextColumnCursor CURSOR LOCAL FAST_FORWARD FOR
  SELECT
    tableInfo.object_id,
    columnInfo.column_id,
    schemaInfo.name,
    tableInfo.name,
    columnInfo.name,
    TYPE_NAME(columnInfo.system_type_id)
  FROM sys.tables tableInfo
  JOIN sys.schemas schemaInfo
    ON schemaInfo.schema_id = tableInfo.schema_id
  JOIN sys.columns columnInfo
    ON columnInfo.object_id = tableInfo.object_id
  WHERE tableInfo.is_ms_shipped = 0
    AND columnInfo.is_computed = 0
    AND columnInfo.system_type_id IN (35, 99, 167, 175, 231, 239)
  ORDER BY schemaInfo.name, tableInfo.name, columnInfo.column_id;

  OPEN TextColumnCursor;
  FETCH NEXT FROM TextColumnCursor
    INTO @ObjectId, @ColumnId, @SchemaName, @TableName, @ColumnName, @DataType;

  WHILE @@FETCH_STATUS = 0
  BEGIN
    SET @QualifiedTable = QUOTENAME(@SchemaName) + N'.' + QUOTENAME(@TableName);
    SET @Sql = N'
      SELECT
        @OldExactOut = COALESCE(SUM(CASE
          WHEN CONVERT(nvarchar(max), ' + QUOTENAME(@ColumnName) + N') = @OldRfc
            THEN CONVERT(bigint, 1) ELSE CONVERT(bigint, 0) END), 0),
        @OldContainsOut = COALESCE(SUM(CASE
          WHEN CHARINDEX(@OldRfc, CONVERT(nvarchar(max), ' + QUOTENAME(@ColumnName) + N')) > 0
            THEN CONVERT(bigint, 1) ELSE CONVERT(bigint, 0) END), 0),
        @NewExactOut = COALESCE(SUM(CASE
          WHEN CONVERT(nvarchar(max), ' + QUOTENAME(@ColumnName) + N') = @NewRfc
            THEN CONVERT(bigint, 1) ELSE CONVERT(bigint, 0) END), 0),
        @NewContainsOut = COALESCE(SUM(CASE
          WHEN CHARINDEX(@NewRfc, CONVERT(nvarchar(max), ' + QUOTENAME(@ColumnName) + N')) > 0
            THEN CONVERT(bigint, 1) ELSE CONVERT(bigint, 0) END), 0)
      FROM ' + @QualifiedTable + N';';

    EXEC sys.sp_executesql
      @Sql,
      N'@OldRfc nvarchar(50), @NewRfc nvarchar(50),
        @OldExactOut bigint OUTPUT, @OldContainsOut bigint OUTPUT,
        @NewExactOut bigint OUTPUT, @NewContainsOut bigint OUTPUT',
      @OldRfc,
      @NewRfc,
      @OldExact OUTPUT,
      @OldContains OUTPUT,
      @NewExact OUTPUT,
      @NewContains OUTPUT;

    IF @OldContains > 0 OR @NewContains > 0
    BEGIN
      INSERT INTO #TargetColumns
      (
        ObjectId,
        ColumnId,
        SchemaName,
        TableName,
        ColumnName,
        DataType,
        MaxCharacters,
        IsEmbedded,
        OldExact,
        OldContains,
        NewExact,
        NewContains
      )
      SELECT
        @ObjectId,
        @ColumnId,
        @SchemaName,
        @TableName,
        @ColumnName,
        @DataType,
        CASE
          WHEN columnInfo.system_type_id IN (35, 99) OR columnInfo.max_length = -1 THEN -1
          WHEN columnInfo.system_type_id IN (231, 239) THEN columnInfo.max_length / 2
          ELSE columnInfo.max_length
        END,
        CASE WHEN @OldContains > @OldExact OR @NewContains > @NewExact THEN 1 ELSE 0 END,
        @OldExact,
        @OldContains,
        @NewExact,
        @NewContains
      FROM sys.columns columnInfo
      WHERE columnInfo.object_id = @ObjectId
        AND columnInfo.column_id = @ColumnId;
    END;

    FETCH NEXT FROM TextColumnCursor
      INTO @ObjectId, @ColumnId, @SchemaName, @TableName, @ColumnName, @DataType;
  END;

  CLOSE TextColumnCursor;
  DEALLOCATE TextColumnCursor;

  DECLARE @OldReferenceRows bigint =
  (
    SELECT COALESCE(SUM(OldContains), 0)
    FROM #TargetColumns
  );
  DECLARE @NewReferenceRows bigint =
  (
    SELECT COALESCE(SUM(NewContains), 0)
    FROM #TargetColumns
  );

  IF @OldReferenceRows = 0 AND @NewReferenceRows = 0
    THROW 51107, 'No existe el identificador anterior ni el nuevo en la base.', 1;

  IF @OldReferenceRows = 0 AND @NewReferenceRows > 0
  BEGIN
    IF EXISTS
    (
      SELECT expected.SchemaName, expected.TableName, expected.ColumnName
      FROM #ExpectedColumns expected
      EXCEPT
      SELECT target.SchemaName, target.TableName, target.ColumnName
      FROM #TargetColumns target
      WHERE target.NewContains > 0
    )
      THROW 51108, 'El cambio parece aplicado, pero faltan columnas esperadas con el RFC nuevo.', 1;

    IF EXISTS
    (
      SELECT target.SchemaName, target.TableName, target.ColumnName
      FROM #TargetColumns target
      WHERE target.NewContains > 0
      EXCEPT
      SELECT expected.SchemaName, expected.TableName, expected.ColumnName
      FROM #ExpectedColumns expected
    )
      THROW 51109, 'El cambio parece aplicado, pero hay columnas nuevas fuera del manifiesto.', 1;

    SELECT
      'ALREADY_APPLIED' AS MigrationStatus,
      DB_NAME() AS DatabaseName,
      @OldRfc AS OldRfc,
      @NewRfc AS NewRfc,
      @NewReferenceRows AS NewReferenceRows;

    ROLLBACK TRANSACTION;
    RETURN;
  END;

  IF @NewReferenceRows > 0
    THROW 51110, 'El RFC anterior y el nuevo coexisten. Se requiere revision manual.', 1;

  IF EXISTS
  (
    SELECT expected.SchemaName, expected.TableName, expected.ColumnName
    FROM #ExpectedColumns expected
    EXCEPT
    SELECT target.SchemaName, target.TableName, target.ColumnName
    FROM #TargetColumns target
    WHERE target.OldContains > 0
  )
    THROW 51111, 'Faltan columnas esperadas con el RFC anterior.', 1;

  IF EXISTS
  (
    SELECT target.SchemaName, target.TableName, target.ColumnName
    FROM #TargetColumns target
    WHERE target.OldContains > 0
    EXCEPT
    SELECT expected.SchemaName, expected.TableName, expected.ColumnName
    FROM #ExpectedColumns expected
  )
    THROW 51112, 'Se encontraron referencias fuera del manifiesto revisado.', 1;

  IF EXISTS
  (
    SELECT 1
    FROM #TargetColumns target
    JOIN #ExpectedColumns expected
      ON expected.SchemaName = target.SchemaName
     AND expected.TableName = target.TableName
     AND expected.ColumnName = target.ColumnName
    WHERE target.IsEmbedded <> expected.IsEmbedded
  )
    THROW 51113, 'Una columna no coincide con el modo exacto/embebido esperado.', 1;

  IF EXISTS
  (
    SELECT 1
    FROM #TargetColumns target
    WHERE target.IsEmbedded = 0
      AND target.MaxCharacters <> -1
      AND target.MaxCharacters < LEN(@NewRfc)
  )
    THROW 51114, 'El RFC nuevo no cabe en una o mas columnas.', 1;

  IF EXISTS
  (
    SELECT 1
    FROM #TargetColumns target
    WHERE target.IsEmbedded = 1
      AND target.MaxCharacters <> -1
  )
    THROW 51115, 'Una referencia embebida no usa una columna MAX; requiere revision manual.', 1;

  IF EXISTS
  (
    SELECT 1
    FROM sys.sql_modules moduleInfo
    WHERE moduleInfo.definition LIKE N'%' + @OldRfc + N'%'
       OR moduleInfo.definition LIKE N'%' + @NewRfc + N'%'
  )
    THROW 51116, 'Hay referencias al RFC dentro de modulos SQL.', 1;

  SELECT @InvalidAuditJsonBefore =
    COALESCE(SUM(CASE
      WHEN OldRowJson IS NOT NULL AND ISJSON(OldRowJson) <> 1 THEN CONVERT(bigint, 1)
      ELSE CONVERT(bigint, 0)
    END), 0)
    +
    COALESCE(SUM(CASE
      WHEN NewRowJson IS NOT NULL AND ISJSON(NewRowJson) <> 1 THEN CONVERT(bigint, 1)
      ELSE CONVERT(bigint, 0)
    END), 0)
  FROM contabilidad.TransaccionesRegistroContableAudit;

  CREATE TABLE #AffectedForeignKeys
  (
    ForeignKeyId int NOT NULL CONSTRAINT PK_AffectedRfcRenameForeignKey PRIMARY KEY,
    SchemaName sysname NOT NULL,
    TableName sysname NOT NULL,
    ConstraintName sysname NOT NULL
  );

  INSERT INTO #AffectedForeignKeys
  (
    ForeignKeyId,
    SchemaName,
    TableName,
    ConstraintName
  )
  SELECT DISTINCT
    foreignKey.object_id,
    childSchema.name,
    childTable.name,
    foreignKey.name
  FROM sys.foreign_keys foreignKey
  JOIN sys.tables childTable
    ON childTable.object_id = foreignKey.parent_object_id
  JOIN sys.schemas childSchema
    ON childSchema.schema_id = childTable.schema_id
  JOIN sys.foreign_key_columns foreignKeyColumn
    ON foreignKeyColumn.constraint_object_id = foreignKey.object_id
  JOIN #TargetColumns target
    ON target.IsEmbedded = 0
   AND target.OldContains > 0
   AND
   (
     (
       target.ObjectId = foreignKeyColumn.parent_object_id
       AND target.ColumnId = foreignKeyColumn.parent_column_id
     )
     OR
     (
       target.ObjectId = foreignKeyColumn.referenced_object_id
       AND target.ColumnId = foreignKeyColumn.referenced_column_id
     )
   );

  IF EXISTS
  (
    SELECT 1
    FROM #AffectedForeignKeys affected
    JOIN sys.foreign_keys foreignKey
      ON foreignKey.object_id = affected.ForeignKeyId
    WHERE foreignKey.is_disabled = 1
       OR foreignKey.is_not_trusted = 1
  )
    THROW 51117, 'Una FK afectada ya estaba deshabilitada o no era confiable.', 1;

  CREATE TABLE #AffectedCheckConstraints
  (
    CheckConstraintId int NOT NULL
      CONSTRAINT PK_AffectedRfcRenameCheckConstraint PRIMARY KEY,
    SchemaName sysname NOT NULL,
    TableName sysname NOT NULL,
    ConstraintName sysname NOT NULL
  );

  INSERT INTO #AffectedCheckConstraints
  (
    CheckConstraintId,
    SchemaName,
    TableName,
    ConstraintName
  )
  SELECT DISTINCT
    checkConstraint.object_id,
    schemaInfo.name,
    tableInfo.name,
    checkConstraint.name
  FROM sys.check_constraints checkConstraint
  JOIN sys.tables tableInfo
    ON tableInfo.object_id = checkConstraint.parent_object_id
  JOIN sys.schemas schemaInfo
    ON schemaInfo.schema_id = tableInfo.schema_id
  JOIN #TargetColumns target
    ON target.ObjectId = tableInfo.object_id
   AND target.IsEmbedded = 0
   AND target.OldContains > 0
   AND
   (
     checkConstraint.parent_column_id = target.ColumnId
     OR EXISTS
     (
       SELECT 1
       FROM sys.sql_expression_dependencies dependency
       WHERE dependency.referencing_id = checkConstraint.object_id
         AND dependency.referenced_id = target.ObjectId
         AND dependency.referenced_minor_id = target.ColumnId
     )
   );

  IF EXISTS
  (
    SELECT 1
    FROM #AffectedCheckConstraints affected
    JOIN sys.check_constraints checkConstraint
      ON checkConstraint.object_id = affected.CheckConstraintId
    WHERE checkConstraint.is_disabled = 1
       OR checkConstraint.is_not_trusted = 1
  )
    THROW 51118, 'Una restriccion CHECK afectada ya estaba deshabilitada o no era confiable.', 1;

  DECLARE DisableForeignKeyCursor CURSOR LOCAL FAST_FORWARD FOR
  SELECT SchemaName, TableName, ConstraintName
  FROM #AffectedForeignKeys
  ORDER BY SchemaName, TableName, ConstraintName;

  DECLARE @ConstraintName sysname;

  OPEN DisableForeignKeyCursor;
  FETCH NEXT FROM DisableForeignKeyCursor
    INTO @SchemaName, @TableName, @ConstraintName;

  WHILE @@FETCH_STATUS = 0
  BEGIN
    SET @Sql =
      N'ALTER TABLE ' + QUOTENAME(@SchemaName) + N'.' + QUOTENAME(@TableName)
      + N' NOCHECK CONSTRAINT ' + QUOTENAME(@ConstraintName) + N';';
    EXEC sys.sp_executesql @Sql;

    FETCH NEXT FROM DisableForeignKeyCursor
      INTO @SchemaName, @TableName, @ConstraintName;
  END;

  CLOSE DisableForeignKeyCursor;
  DEALLOCATE DisableForeignKeyCursor;

  DECLARE DisableCheckCursor CURSOR LOCAL FAST_FORWARD FOR
  SELECT SchemaName, TableName, ConstraintName
  FROM #AffectedCheckConstraints
  ORDER BY SchemaName, TableName, ConstraintName;

  OPEN DisableCheckCursor;
  FETCH NEXT FROM DisableCheckCursor
    INTO @SchemaName, @TableName, @ConstraintName;

  WHILE @@FETCH_STATUS = 0
  BEGIN
    SET @Sql =
      N'ALTER TABLE ' + QUOTENAME(@SchemaName) + N'.' + QUOTENAME(@TableName)
      + N' NOCHECK CONSTRAINT ' + QUOTENAME(@ConstraintName) + N';';
    EXEC sys.sp_executesql @Sql;

    FETCH NEXT FROM DisableCheckCursor
      INTO @SchemaName, @TableName, @ConstraintName;
  END;

  CLOSE DisableCheckCursor;
  DEALLOCATE DisableCheckCursor;

  DECLARE ExactUpdateCursor CURSOR LOCAL FAST_FORWARD FOR
  SELECT ObjectId, ColumnId, SchemaName, TableName, ColumnName
  FROM #TargetColumns
  WHERE OldContains > 0
    AND IsEmbedded = 0
  ORDER BY SchemaName, TableName, ColumnName;

  OPEN ExactUpdateCursor;
  FETCH NEXT FROM ExactUpdateCursor
    INTO @ObjectId, @ColumnId, @SchemaName, @TableName, @ColumnName;

  WHILE @@FETCH_STATUS = 0
  BEGIN
    SET @QualifiedTable = QUOTENAME(@SchemaName) + N'.' + QUOTENAME(@TableName);
    SET @Sql =
      N'UPDATE ' + @QualifiedTable
      + N' SET ' + QUOTENAME(@ColumnName) + N' = @NewRfc'
      + N' WHERE CONVERT(nvarchar(max), ' + QUOTENAME(@ColumnName) + N') = @OldRfc;'
      + N' SET @RowsUpdatedOut = @@ROWCOUNT;';

    EXEC sys.sp_executesql
      @Sql,
      N'@OldRfc nvarchar(50), @NewRfc nvarchar(50), @RowsUpdatedOut bigint OUTPUT',
      @OldRfc,
      @NewRfc,
      @RowsUpdated OUTPUT;

    UPDATE #TargetColumns
    SET RowsUpdated = @RowsUpdated
    WHERE ObjectId = @ObjectId
      AND ColumnId = @ColumnId;

    IF @RowsUpdated <>
    (
      SELECT OldExact
      FROM #TargetColumns
      WHERE ObjectId = @ObjectId
        AND ColumnId = @ColumnId
    )
      THROW 51119, 'El numero de filas actualizado no coincide con el inventario previo.', 1;

    FETCH NEXT FROM ExactUpdateCursor
      INTO @ObjectId, @ColumnId, @SchemaName, @TableName, @ColumnName;
  END;

  CLOSE ExactUpdateCursor;
  DEALLOCATE ExactUpdateCursor;

  DECLARE EmbeddedUpdateCursor CURSOR LOCAL FAST_FORWARD FOR
  SELECT ObjectId, ColumnId, SchemaName, TableName, ColumnName
  FROM #TargetColumns
  WHERE OldContains > 0
    AND IsEmbedded = 1
  ORDER BY SchemaName, TableName, ColumnName;

  OPEN EmbeddedUpdateCursor;
  FETCH NEXT FROM EmbeddedUpdateCursor
    INTO @ObjectId, @ColumnId, @SchemaName, @TableName, @ColumnName;

  WHILE @@FETCH_STATUS = 0
  BEGIN
    SET @QualifiedTable = QUOTENAME(@SchemaName) + N'.' + QUOTENAME(@TableName);
    SET @Sql =
      N'UPDATE ' + @QualifiedTable
      + N' SET ' + QUOTENAME(@ColumnName)
      + N' = REPLACE(CONVERT(nvarchar(max), ' + QUOTENAME(@ColumnName) + N'), @OldRfc, @NewRfc)'
      + N' WHERE CHARINDEX(@OldRfc, CONVERT(nvarchar(max), ' + QUOTENAME(@ColumnName) + N')) > 0;'
      + N' SET @RowsUpdatedOut = @@ROWCOUNT;';

    EXEC sys.sp_executesql
      @Sql,
      N'@OldRfc nvarchar(50), @NewRfc nvarchar(50), @RowsUpdatedOut bigint OUTPUT',
      @OldRfc,
      @NewRfc,
      @RowsUpdated OUTPUT;

    UPDATE #TargetColumns
    SET RowsUpdated = @RowsUpdated
    WHERE ObjectId = @ObjectId
      AND ColumnId = @ColumnId;

    IF @RowsUpdated <
    (
      SELECT OldContains
      FROM #TargetColumns
      WHERE ObjectId = @ObjectId
        AND ColumnId = @ColumnId
    )
      THROW 51120, 'No se actualizaron todas las referencias embebidas.', 1;

    FETCH NEXT FROM EmbeddedUpdateCursor
      INTO @ObjectId, @ColumnId, @SchemaName, @TableName, @ColumnName;
  END;

  CLOSE EmbeddedUpdateCursor;
  DEALLOCATE EmbeddedUpdateCursor;

  DECLARE EnableCheckCursor CURSOR LOCAL FAST_FORWARD FOR
  SELECT SchemaName, TableName, ConstraintName
  FROM #AffectedCheckConstraints
  ORDER BY SchemaName, TableName, ConstraintName;

  OPEN EnableCheckCursor;
  FETCH NEXT FROM EnableCheckCursor
    INTO @SchemaName, @TableName, @ConstraintName;

  WHILE @@FETCH_STATUS = 0
  BEGIN
    SET @Sql =
      N'ALTER TABLE ' + QUOTENAME(@SchemaName) + N'.' + QUOTENAME(@TableName)
      + N' WITH CHECK CHECK CONSTRAINT ' + QUOTENAME(@ConstraintName) + N';';
    EXEC sys.sp_executesql @Sql;

    FETCH NEXT FROM EnableCheckCursor
      INTO @SchemaName, @TableName, @ConstraintName;
  END;

  CLOSE EnableCheckCursor;
  DEALLOCATE EnableCheckCursor;

  DECLARE EnableForeignKeyCursor CURSOR LOCAL FAST_FORWARD FOR
  SELECT SchemaName, TableName, ConstraintName
  FROM #AffectedForeignKeys
  ORDER BY SchemaName, TableName, ConstraintName;

  OPEN EnableForeignKeyCursor;
  FETCH NEXT FROM EnableForeignKeyCursor
    INTO @SchemaName, @TableName, @ConstraintName;

  WHILE @@FETCH_STATUS = 0
  BEGIN
    SET @Sql =
      N'ALTER TABLE ' + QUOTENAME(@SchemaName) + N'.' + QUOTENAME(@TableName)
      + N' WITH CHECK CHECK CONSTRAINT ' + QUOTENAME(@ConstraintName) + N';';
    EXEC sys.sp_executesql @Sql;

    FETCH NEXT FROM EnableForeignKeyCursor
      INTO @SchemaName, @TableName, @ConstraintName;
  END;

  CLOSE EnableForeignKeyCursor;
  DEALLOCATE EnableForeignKeyCursor;

  IF EXISTS
  (
    SELECT 1
    FROM #AffectedForeignKeys affected
    JOIN sys.foreign_keys foreignKey
      ON foreignKey.object_id = affected.ForeignKeyId
    WHERE foreignKey.is_disabled = 1
       OR foreignKey.is_not_trusted = 1
  )
    THROW 51121, 'Una FK afectada no quedo habilitada y confiable.', 1;

  IF EXISTS
  (
    SELECT 1
    FROM #AffectedCheckConstraints affected
    JOIN sys.check_constraints checkConstraint
      ON checkConstraint.object_id = affected.CheckConstraintId
    WHERE checkConstraint.is_disabled = 1
       OR checkConstraint.is_not_trusted = 1
  )
    THROW 51122, 'Una restriccion CHECK afectada no quedo habilitada y confiable.', 1;

  DECLARE PostValidationCursor CURSOR LOCAL FAST_FORWARD FOR
  SELECT ObjectId, ColumnId, SchemaName, TableName, ColumnName, IsEmbedded
  FROM #TargetColumns
  ORDER BY SchemaName, TableName, ColumnName;

  DECLARE @IsEmbedded bit;

  OPEN PostValidationCursor;
  FETCH NEXT FROM PostValidationCursor
    INTO @ObjectId, @ColumnId, @SchemaName, @TableName, @ColumnName, @IsEmbedded;

  WHILE @@FETCH_STATUS = 0
  BEGIN
    SET @QualifiedTable = QUOTENAME(@SchemaName) + N'.' + QUOTENAME(@TableName);
    SET @Sql = N'
      SELECT
        @OldExactOut = COALESCE(SUM(CASE
          WHEN CONVERT(nvarchar(max), ' + QUOTENAME(@ColumnName) + N') = @OldRfc
            THEN CONVERT(bigint, 1) ELSE CONVERT(bigint, 0) END), 0),
        @OldContainsOut = COALESCE(SUM(CASE
          WHEN CHARINDEX(@OldRfc, CONVERT(nvarchar(max), ' + QUOTENAME(@ColumnName) + N')) > 0
            THEN CONVERT(bigint, 1) ELSE CONVERT(bigint, 0) END), 0),
        @NewExactOut = COALESCE(SUM(CASE
          WHEN CONVERT(nvarchar(max), ' + QUOTENAME(@ColumnName) + N') = @NewRfc
            THEN CONVERT(bigint, 1) ELSE CONVERT(bigint, 0) END), 0),
        @NewContainsOut = COALESCE(SUM(CASE
          WHEN CHARINDEX(@NewRfc, CONVERT(nvarchar(max), ' + QUOTENAME(@ColumnName) + N')) > 0
            THEN CONVERT(bigint, 1) ELSE CONVERT(bigint, 0) END), 0)
      FROM ' + @QualifiedTable + N';';

    EXEC sys.sp_executesql
      @Sql,
      N'@OldRfc nvarchar(50), @NewRfc nvarchar(50),
        @OldExactOut bigint OUTPUT, @OldContainsOut bigint OUTPUT,
        @NewExactOut bigint OUTPUT, @NewContainsOut bigint OUTPUT',
      @OldRfc,
      @NewRfc,
      @OldExact OUTPUT,
      @OldContains OUTPUT,
      @NewExact OUTPUT,
      @NewContains OUTPUT;

    IF @OldContains <> 0
      THROW 51123, 'Persisten referencias al RFC anterior despues de actualizar.', 1;

    IF @IsEmbedded = 0
       AND @NewExact <>
       (
         SELECT target.OldExact
         FROM #TargetColumns target
         WHERE target.ObjectId = @ObjectId
           AND target.ColumnId = @ColumnId
       )
      THROW 51124, 'El conteo final del RFC nuevo no coincide con el conteo original.', 1;

    IF @IsEmbedded = 1
       AND @NewContains <
       (
         SELECT target.OldContains
         FROM #TargetColumns target
         WHERE target.ObjectId = @ObjectId
           AND target.ColumnId = @ColumnId
       )
      THROW 51125, 'El conteo final de referencias embebidas es menor al original.', 1;

    FETCH NEXT FROM PostValidationCursor
      INTO @ObjectId, @ColumnId, @SchemaName, @TableName, @ColumnName, @IsEmbedded;
  END;

  CLOSE PostValidationCursor;
  DEALLOCATE PostValidationCursor;

  SELECT @InvalidAuditJsonAfter =
    COALESCE(SUM(CASE
      WHEN OldRowJson IS NOT NULL AND ISJSON(OldRowJson) <> 1 THEN CONVERT(bigint, 1)
      ELSE CONVERT(bigint, 0)
    END), 0)
    +
    COALESCE(SUM(CASE
      WHEN NewRowJson IS NOT NULL AND ISJSON(NewRowJson) <> 1 THEN CONVERT(bigint, 1)
      ELSE CONVERT(bigint, 0)
    END), 0)
  FROM contabilidad.TransaccionesRegistroContableAudit;

  IF @InvalidAuditJsonAfter <> @InvalidAuditJsonBefore
    THROW 51126, 'La migracion altero la validez de los JSON de auditoria.', 1;

  /*
    Segunda inspeccion completa, independiente del manifiesto inicial. Detecta
    referencias nuevas que pudieran haber sido creadas por triggers.
  */
  DECLARE FinalTextColumnCursor CURSOR LOCAL FAST_FORWARD FOR
  SELECT
    schemaInfo.name,
    tableInfo.name,
    columnInfo.name
  FROM sys.tables tableInfo
  JOIN sys.schemas schemaInfo
    ON schemaInfo.schema_id = tableInfo.schema_id
  JOIN sys.columns columnInfo
    ON columnInfo.object_id = tableInfo.object_id
  WHERE tableInfo.is_ms_shipped = 0
    AND columnInfo.is_computed = 0
    AND columnInfo.system_type_id IN (35, 99, 167, 175, 231, 239)
  ORDER BY schemaInfo.name, tableInfo.name, columnInfo.column_id;

  OPEN FinalTextColumnCursor;
  FETCH NEXT FROM FinalTextColumnCursor
    INTO @SchemaName, @TableName, @ColumnName;

  WHILE @@FETCH_STATUS = 0
  BEGIN
    SET @QualifiedTable = QUOTENAME(@SchemaName) + N'.' + QUOTENAME(@TableName);
    SET @Sql =
      N'SELECT @OldContainsOut = COUNT_BIG(*) FROM ' + @QualifiedTable
      + N' WHERE CHARINDEX(@OldRfc, CONVERT(nvarchar(max), '
      + QUOTENAME(@ColumnName) + N')) > 0;';

    EXEC sys.sp_executesql
      @Sql,
      N'@OldRfc nvarchar(50), @OldContainsOut bigint OUTPUT',
      @OldRfc,
      @OldContains OUTPUT;

    IF @OldContains > 0
      THROW 51127, 'La inspeccion final encontro una referencia residual fuera del manifiesto.', 1;

    FETCH NEXT FROM FinalTextColumnCursor
      INTO @SchemaName, @TableName, @ColumnName;
  END;

  CLOSE FinalTextColumnCursor;
  DEALLOCATE FinalTextColumnCursor;

  SELECT
    target.SchemaName,
    target.TableName,
    target.ColumnName,
    target.OldExact,
    target.OldContains,
    target.RowsUpdated
  FROM #TargetColumns target
  ORDER BY target.SchemaName, target.TableName, target.ColumnName;

  DECLARE @UpdatedReferenceRows bigint =
  (
    SELECT COALESCE(SUM(RowsUpdated), 0)
    FROM #TargetColumns
  );

  IF @ApplyChanges = 0
  BEGIN
    SELECT
      'DRY_RUN_VALIDATED' AS MigrationStatus,
      DB_NAME() AS DatabaseName,
      @OldRfc AS OldRfc,
      @NewRfc AS NewRfc,
      @UpdatedReferenceRows AS UpdatedReferenceRows,
      (SELECT COUNT(*) FROM #AffectedForeignKeys) AS ForeignKeysValidated,
      (SELECT COUNT(*) FROM #AffectedCheckConstraints) AS CheckConstraintsValidated;

    ROLLBACK TRANSACTION;
    RETURN;
  END;

  COMMIT TRANSACTION;

  SELECT
    'COMMITTED' AS MigrationStatus,
    DB_NAME() AS DatabaseName,
    @OldRfc AS OldRfc,
    @NewRfc AS NewRfc,
    @UpdatedReferenceRows AS UpdatedReferenceRows;
END TRY
BEGIN CATCH
  IF XACT_STATE() <> 0
    ROLLBACK TRANSACTION;

  THROW;
END CATCH;
