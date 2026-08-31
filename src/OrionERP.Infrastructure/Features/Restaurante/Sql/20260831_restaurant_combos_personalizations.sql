/*
  Combos y personalizaciones semánticas para Restaurante.

  Simulación:
    sqlcmd ... -f 65001 -v ExpectedDatabase="Orion_Sandbox" ApplyChanges="0" -i 20260831_restaurant_combos_personalizations.sql
  Aplicación:
    sqlcmd ... -f 65001 -v ExpectedDatabase="Orion_Sandbox" ApplyChanges="1" -i 20260831_restaurant_combos_personalizations.sql

  ApplyChanges=0 ejecuta la misma migración y validaciones dentro de una
  transacción que se revierte. La migración no siembra combos ni modificadores.
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET QUOTED_IDENTIFIER ON;
SET NUMERIC_ROUNDABORT OFF;

DECLARE @ExpectedDatabase sysname = N'$(ExpectedDatabase)';
DECLARE @ApplyChangesText nvarchar(10) = N'$(ApplyChanges)';
DECLARE @ApplyChanges bit;

IF @ExpectedDatabase NOT IN (N'Orion_Sandbox', N'Orion_SandBox', N'grupocarpio')
  THROW 51800, 'ExpectedDatabase debe ser Orion_Sandbox o grupocarpio.', 1;
IF DB_NAME() <> @ExpectedDatabase
  THROW 51801, 'La base conectada no coincide con ExpectedDatabase.', 1;
IF @ApplyChangesText NOT IN (N'0', N'1')
  THROW 51802, 'ApplyChanges debe ser 0 o 1.', 1;
SET @ApplyChanges = CONVERT(bit, @ApplyChangesText);
IF OBJECT_ID('restaurante.Product', 'U') IS NULL
   OR OBJECT_ID('restaurante.OrderLine', 'U') IS NULL
   OR OBJECT_ID('restaurante.ModifierIngredientDelta', 'U') IS NULL
  THROW 51803, 'Ejecuta primero las migraciones base de Restaurante.', 1;

BEGIN TRY
  BEGIN TRANSACTION;

  /* Producto estándar vs. combo. */
  IF COL_LENGTH('restaurante.Product', 'ProductKind') IS NULL
  BEGIN
    ALTER TABLE restaurante.Product
      ADD ProductKind varchar(20) NOT NULL
        CONSTRAINT DF_RestaurantProduct_ProductKind DEFAULT ('Standard') WITH VALUES;
  END;

  IF EXISTS
  (
    SELECT 1
    FROM sys.foreign_keys
    WHERE parent_object_id = OBJECT_ID('restaurante.Product')
      AND [name] = 'FK_RestaurantProduct_Material_Rfc'
  )
  BEGIN
    ALTER TABLE restaurante.Product DROP CONSTRAINT FK_RestaurantProduct_Material_Rfc;
  END;

  IF EXISTS
  (
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID('restaurante.Product')
      AND [name] = 'UX_RestaurantProduct_Material'
      AND is_unique_constraint = 1
  )
  BEGIN
    ALTER TABLE restaurante.Product DROP CONSTRAINT UX_RestaurantProduct_Material;
  END
  ELSE IF EXISTS
  (
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID('restaurante.Product')
      AND [name] = 'UX_RestaurantProduct_Material'
  )
  BEGIN
    DROP INDEX UX_RestaurantProduct_Material ON restaurante.Product;
  END;

  IF EXISTS
  (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('restaurante.Product')
      AND [name] = 'MaterialId' AND is_nullable = 0
  )
  BEGIN
    ALTER TABLE restaurante.Product ALTER COLUMN MaterialId int NULL;
  END;

  IF NOT EXISTS
  (
    SELECT 1 FROM sys.foreign_keys
    WHERE parent_object_id = OBJECT_ID('restaurante.Product')
      AND [name] = 'FK_RestaurantProduct_Material_Rfc'
  )
  BEGIN
    ALTER TABLE restaurante.Product WITH CHECK
      ADD CONSTRAINT FK_RestaurantProduct_Material_Rfc
        FOREIGN KEY (Rfc, MaterialId) REFERENCES logistica.Material (Rfc, Id);
  END;

  IF NOT EXISTS
  (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID('restaurante.Product')
      AND [name] = 'UX_RestaurantProduct_Material_Filtered'
  )
  BEGIN
    CREATE UNIQUE INDEX UX_RestaurantProduct_Material_Filtered
      ON restaurante.Product (Rfc, MaterialId)
      WHERE MaterialId IS NOT NULL;
  END;

  IF NOT EXISTS
  (
    SELECT 1 FROM sys.check_constraints
    WHERE parent_object_id = OBJECT_ID('restaurante.Product')
      AND [name] = 'CK_RestaurantProduct_KindMaterial'
  )
  BEGIN
    EXEC sys.sp_executesql N'
      ALTER TABLE restaurante.Product WITH CHECK
        ADD CONSTRAINT CK_RestaurantProduct_KindMaterial CHECK
        (
          (ProductKind = ''Standard'' AND MaterialId IS NOT NULL)
          OR
          (ProductKind = ''Combo'' AND MaterialId IS NULL
            AND KitchenStationId IS NULL AND PreparationMinutes IS NULL)
        );';
  END;

  /* Efectos de ingredientes. Se conserva la tabla histórica para compatibilidad. */
  IF COL_LENGTH('restaurante.ModifierIngredientDelta', 'EffectKind') IS NULL
  BEGIN
    ALTER TABLE restaurante.ModifierIngredientDelta
      ADD EffectKind varchar(20) NOT NULL
        CONSTRAINT DF_ModifierIngredientDelta_EffectKind DEFAULT ('AdjustQuantity') WITH VALUES;
  END;

  IF EXISTS
  (
    SELECT 1 FROM sys.check_constraints
    WHERE parent_object_id = OBJECT_ID('restaurante.ModifierIngredientDelta')
      AND [name] = 'CK_ModifierIngredientDelta_Quantity'
  )
  BEGIN
    ALTER TABLE restaurante.ModifierIngredientDelta
      DROP CONSTRAINT CK_ModifierIngredientDelta_Quantity;
  END;

  IF EXISTS
  (
    SELECT 1 FROM sys.foreign_keys
    WHERE parent_object_id = OBJECT_ID('restaurante.ModifierIngredientDelta')
      AND [name] = 'FK_ModifierIngredientDelta_Unit'
  )
  BEGIN
    ALTER TABLE restaurante.ModifierIngredientDelta
      DROP CONSTRAINT FK_ModifierIngredientDelta_Unit;
  END;

  IF EXISTS
  (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('restaurante.ModifierIngredientDelta')
      AND [name] = 'UnitId' AND is_nullable = 0
  )
  BEGIN
    ALTER TABLE restaurante.ModifierIngredientDelta ALTER COLUMN UnitId int NULL;
  END;

  IF NOT EXISTS
  (
    SELECT 1 FROM sys.foreign_keys
    WHERE parent_object_id = OBJECT_ID('restaurante.ModifierIngredientDelta')
      AND [name] = 'FK_ModifierIngredientDelta_Unit'
  )
  BEGIN
    ALTER TABLE restaurante.ModifierIngredientDelta WITH CHECK
      ADD CONSTRAINT FK_ModifierIngredientDelta_Unit
        FOREIGN KEY (UnitId) REFERENCES logistica.UnitOfMeasure (Id);
  END;

  IF EXISTS
  (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID('restaurante.ModifierIngredientDelta')
      AND [name] = 'UX_ModifierIngredientDelta'
      AND is_unique_constraint = 1
  )
  BEGIN
    ALTER TABLE restaurante.ModifierIngredientDelta DROP CONSTRAINT UX_ModifierIngredientDelta;
  END
  ELSE IF EXISTS
  (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID('restaurante.ModifierIngredientDelta')
      AND [name] = 'UX_ModifierIngredientDelta'
  )
  BEGIN
    DROP INDEX UX_ModifierIngredientDelta ON restaurante.ModifierIngredientDelta;
  END;

  IF NOT EXISTS
  (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID('restaurante.ModifierIngredientDelta')
      AND [name] = 'UX_ModifierIngredientEffect'
  )
  BEGIN
    EXEC sys.sp_executesql N'
      CREATE UNIQUE INDEX UX_ModifierIngredientEffect
        ON restaurante.ModifierIngredientDelta (Rfc, ModifierOptionId, MaterialId, EffectKind);';
  END;

  IF NOT EXISTS
  (
    SELECT 1 FROM sys.check_constraints
    WHERE parent_object_id = OBJECT_ID('restaurante.ModifierIngredientDelta')
      AND [name] = 'CK_ModifierIngredientDelta_Effect'
  )
  BEGIN
    EXEC sys.sp_executesql N'
      ALTER TABLE restaurante.ModifierIngredientDelta WITH CHECK
        ADD CONSTRAINT CK_ModifierIngredientDelta_Effect CHECK
        (
          (EffectKind = ''AddQuantity'' AND QuantityDelta > 0 AND UnitId IS NOT NULL)
          OR (EffectKind = ''RemoveIngredient'' AND QuantityDelta = 0)
          OR (EffectKind = ''AdjustQuantity'' AND QuantityDelta <> 0 AND UnitId IS NOT NULL)
        );';
  END;

  /* Definición jerárquica del combo. */
  IF OBJECT_ID('restaurante.ComboSlot', 'U') IS NULL
  BEGIN
    CREATE TABLE restaurante.ComboSlot
    (
      Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_ComboSlot PRIMARY KEY,
      Rfc varchar(50) NOT NULL,
      ComboProductId bigint NOT NULL,
      [Name] varchar(120) NOT NULL,
      MinSelections int NOT NULL CONSTRAINT DF_ComboSlot_Min DEFAULT (1),
      MaxSelections int NOT NULL CONSTRAINT DF_ComboSlot_Max DEFAULT (1),
      SortOrder int NOT NULL CONSTRAINT DF_ComboSlot_Sort DEFAULT (0),
      IsActive bit NOT NULL CONSTRAINT DF_ComboSlot_Active DEFAULT (1),
      CONSTRAINT CK_ComboSlot_Selections CHECK (MinSelections >= 0 AND MaxSelections >= 1 AND MaxSelections >= MinSelections),
      CONSTRAINT FK_ComboSlot_Product_Rfc FOREIGN KEY (Rfc, ComboProductId)
        REFERENCES restaurante.Product (Rfc, Id),
      CONSTRAINT UX_ComboSlot_Name UNIQUE (Rfc, ComboProductId, [Name])
    );
  END;

  IF NOT EXISTS
  (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID('restaurante.ComboSlot') AND [name] = 'UX_ComboSlot_RfcId'
  )
    CREATE UNIQUE INDEX UX_ComboSlot_RfcId ON restaurante.ComboSlot (Rfc, Id);

  IF OBJECT_ID('restaurante.ComboSlotOption', 'U') IS NULL
  BEGIN
    CREATE TABLE restaurante.ComboSlotOption
    (
      Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_ComboSlotOption PRIMARY KEY,
      Rfc varchar(50) NOT NULL,
      ComboSlotId bigint NOT NULL,
      ComponentProductId bigint NOT NULL,
      Quantity decimal(18,4) NOT NULL CONSTRAINT DF_ComboSlotOption_Quantity DEFAULT (1),
      PriceDelta decimal(18,2) NOT NULL CONSTRAINT DF_ComboSlotOption_Price DEFAULT (0),
      IsDefault bit NOT NULL CONSTRAINT DF_ComboSlotOption_Default DEFAULT (0),
      SortOrder int NOT NULL CONSTRAINT DF_ComboSlotOption_Sort DEFAULT (0),
      IsActive bit NOT NULL CONSTRAINT DF_ComboSlotOption_Active DEFAULT (1),
      CONSTRAINT CK_ComboSlotOption_Quantity CHECK (Quantity > 0),
      CONSTRAINT CK_ComboSlotOption_Price CHECK (PriceDelta >= 0),
      CONSTRAINT FK_ComboSlotOption_Slot_Rfc FOREIGN KEY (Rfc, ComboSlotId)
        REFERENCES restaurante.ComboSlot (Rfc, Id),
      CONSTRAINT FK_ComboSlotOption_Product_Rfc FOREIGN KEY (Rfc, ComponentProductId)
        REFERENCES restaurante.Product (Rfc, Id),
      CONSTRAINT UX_ComboSlotOption_Product UNIQUE (Rfc, ComboSlotId, ComponentProductId)
    );
  END;

  IF NOT EXISTS
  (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID('restaurante.ComboSlotOption') AND [name] = 'UX_ComboSlotOption_RfcId'
  )
    CREATE UNIQUE INDEX UX_ComboSlotOption_RfcId ON restaurante.ComboSlotOption (Rfc, Id);

  IF NOT EXISTS
  (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID('restaurante.MenuSection') AND [name] = 'UX_MenuSection_RfcMenuIdId'
  )
    CREATE UNIQUE INDEX UX_MenuSection_RfcMenuIdId ON restaurante.MenuSection (Rfc, MenuId, Id);

  IF OBJECT_ID('restaurante.ComboSlotOptionRoute', 'U') IS NULL
  BEGIN
    CREATE TABLE restaurante.ComboSlotOptionRoute
    (
      Rfc varchar(50) NOT NULL,
      ComboSlotOptionId bigint NOT NULL,
      MenuId bigint NOT NULL,
      MenuSectionId bigint NOT NULL,
      CONSTRAINT PK_ComboSlotOptionRoute PRIMARY KEY (Rfc, ComboSlotOptionId, MenuId),
      CONSTRAINT FK_ComboSlotOptionRoute_Option_Rfc FOREIGN KEY (Rfc, ComboSlotOptionId)
        REFERENCES restaurante.ComboSlotOption (Rfc, Id),
      CONSTRAINT FK_ComboSlotOptionRoute_Menu_Rfc FOREIGN KEY (Rfc, MenuId)
        REFERENCES restaurante.Menu (Rfc, Id),
      CONSTRAINT FK_ComboSlotOptionRoute_Section_Rfc FOREIGN KEY (Rfc, MenuId, MenuSectionId)
        REFERENCES restaurante.MenuSection (Rfc, MenuId, Id)
    );
  END;

  /* Jerarquía y snapshots históricos de la orden. */
  IF COL_LENGTH('restaurante.OrderLine', 'LineKind') IS NULL
    ALTER TABLE restaurante.OrderLine ADD LineKind varchar(20) NOT NULL CONSTRAINT DF_OrderLine_LineKind DEFAULT ('Standard') WITH VALUES;
  IF COL_LENGTH('restaurante.OrderLine', 'ParentOrderLineId') IS NULL
    ALTER TABLE restaurante.OrderLine ADD ParentOrderLineId bigint NULL;
  IF COL_LENGTH('restaurante.OrderLine', 'ComboSlotId') IS NULL
    ALTER TABLE restaurante.OrderLine ADD ComboSlotId bigint NULL;
  IF COL_LENGTH('restaurante.OrderLine', 'ComboSlotOptionId') IS NULL
    ALTER TABLE restaurante.OrderLine ADD ComboSlotOptionId bigint NULL;
  IF COL_LENGTH('restaurante.OrderLine', 'ParentProductNameSnapshot') IS NULL
    ALTER TABLE restaurante.OrderLine ADD ParentProductNameSnapshot varchar(180) NULL;
  IF COL_LENGTH('restaurante.OrderLine', 'ComboSlotNameSnapshot') IS NULL
    ALTER TABLE restaurante.OrderLine ADD ComboSlotNameSnapshot varchar(120) NULL;
  IF COL_LENGTH('restaurante.OrderLine', 'BaseUnitPrice') IS NULL
    ALTER TABLE restaurante.OrderLine ADD BaseUnitPrice decimal(18,2) NOT NULL CONSTRAINT DF_OrderLine_BaseUnitPrice DEFAULT (0) WITH VALUES;
  IF COL_LENGTH('restaurante.OrderLine', 'ChoicePriceDelta') IS NULL
    ALTER TABLE restaurante.OrderLine ADD ChoicePriceDelta decimal(18,2) NOT NULL CONSTRAINT DF_OrderLine_ChoicePriceDelta DEFAULT (0) WITH VALUES;

  EXEC sys.sp_executesql N'
    UPDATE restaurante.OrderLine
    SET BaseUnitPrice = UnitPrice
    WHERE LineKind <> ''ComboComponent'' AND BaseUnitPrice = 0 AND UnitPrice <> 0;';

  IF NOT EXISTS
  (
    SELECT 1 FROM sys.foreign_keys
    WHERE parent_object_id = OBJECT_ID('restaurante.OrderLine') AND [name] = 'FK_OrderLine_Parent_Rfc'
  )
    EXEC sys.sp_executesql N'
      ALTER TABLE restaurante.OrderLine WITH CHECK ADD CONSTRAINT FK_OrderLine_Parent_Rfc
        FOREIGN KEY (Rfc, ParentOrderLineId) REFERENCES restaurante.OrderLine (Rfc, Id);';

  IF NOT EXISTS
  (
    SELECT 1 FROM sys.foreign_keys
    WHERE parent_object_id = OBJECT_ID('restaurante.OrderLine') AND [name] = 'FK_OrderLine_ComboSlot_Rfc'
  )
    EXEC sys.sp_executesql N'
      ALTER TABLE restaurante.OrderLine WITH CHECK ADD CONSTRAINT FK_OrderLine_ComboSlot_Rfc
        FOREIGN KEY (Rfc, ComboSlotId) REFERENCES restaurante.ComboSlot (Rfc, Id);';

  IF NOT EXISTS
  (
    SELECT 1 FROM sys.foreign_keys
    WHERE parent_object_id = OBJECT_ID('restaurante.OrderLine') AND [name] = 'FK_OrderLine_ComboOption_Rfc'
  )
    EXEC sys.sp_executesql N'
      ALTER TABLE restaurante.OrderLine WITH CHECK ADD CONSTRAINT FK_OrderLine_ComboOption_Rfc
        FOREIGN KEY (Rfc, ComboSlotOptionId) REFERENCES restaurante.ComboSlotOption (Rfc, Id);';

  IF NOT EXISTS
  (
    SELECT 1 FROM sys.check_constraints
    WHERE parent_object_id = OBJECT_ID('restaurante.OrderLine') AND [name] = 'CK_OrderLine_ComboHierarchy'
  )
    EXEC sys.sp_executesql N'
      ALTER TABLE restaurante.OrderLine WITH CHECK ADD CONSTRAINT CK_OrderLine_ComboHierarchy CHECK
      (
        (LineKind IN (''Standard'', ''Combo'') AND ParentOrderLineId IS NULL AND ComboSlotId IS NULL AND ComboSlotOptionId IS NULL)
        OR
        (LineKind = ''ComboComponent'' AND ParentOrderLineId IS NOT NULL AND ComboSlotId IS NOT NULL AND ComboSlotOptionId IS NOT NULL)
      );';

  IF NOT EXISTS
  (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID('restaurante.OrderLine') AND [name] = 'IX_OrderLine_Parent'
  )
    EXEC sys.sp_executesql N'
      CREATE INDEX IX_OrderLine_Parent ON restaurante.OrderLine (Rfc, ParentOrderLineId, Id) WHERE ParentOrderLineId IS NOT NULL;';

  IF COL_LENGTH('restaurante.OrderLineModifier', 'ModifierGroupNameSnapshot') IS NULL
    ALTER TABLE restaurante.OrderLineModifier ADD ModifierGroupNameSnapshot varchar(120) NULL;
  IF COL_LENGTH('restaurante.OrderLineModifier', 'EffectKind') IS NULL
    ALTER TABLE restaurante.OrderLineModifier ADD EffectKind varchar(20) NULL;

  EXEC sys.sp_executesql N'
    UPDATE lineModifier
    SET ModifierGroupNameSnapshot = COALESCE(lineModifier.ModifierGroupNameSnapshot, modifierGroup.[Name]),
        EffectKind = COALESCE(lineModifier.EffectKind, effectInfo.EffectKind)
    FROM restaurante.OrderLineModifier lineModifier
    JOIN restaurante.ModifierOption modifierOption
      ON modifierOption.Rfc = lineModifier.Rfc AND modifierOption.Id = lineModifier.ModifierOptionId
    JOIN restaurante.ModifierGroup modifierGroup
      ON modifierGroup.Rfc = modifierOption.Rfc AND modifierGroup.Id = modifierOption.ModifierGroupId
    OUTER APPLY
    (
      SELECT CASE WHEN COUNT(DISTINCT delta.EffectKind) = 1 THEN MAX(delta.EffectKind) END AS EffectKind
      FROM restaurante.ModifierIngredientDelta delta
      WHERE delta.Rfc = lineModifier.Rfc AND delta.ModifierOptionId = lineModifier.ModifierOptionId
    ) effectInfo
    WHERE lineModifier.ModifierGroupNameSnapshot IS NULL OR lineModifier.EffectKind IS NULL;';

  IF NOT EXISTS
  (
    SELECT 1 FROM sys.indexes
    WHERE object_id=OBJECT_ID('restaurante.OrderLineModifier') AND [name]='UX_OrderLineModifier_RfcId'
  )
    CREATE UNIQUE INDEX UX_OrderLineModifier_RfcId ON restaurante.OrderLineModifier (Rfc,Id);

  IF OBJECT_ID('restaurante.OrderLineModifierIngredientEffect','U') IS NULL
  BEGIN
    CREATE TABLE restaurante.OrderLineModifierIngredientEffect
    (
      Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_OrderLineModifierIngredientEffect PRIMARY KEY,
      Rfc varchar(50) NOT NULL,
      OrderLineModifierId bigint NOT NULL,
      MaterialId int NOT NULL,
      EffectKind varchar(20) NOT NULL,
      QuantityDelta decimal(18,6) NOT NULL,
      UnitId int NULL,
      BaseQuantityDelta decimal(18,8) NULL,
      MaterialNameSnapshot varchar(200) NOT NULL,
      UnitNameSnapshot varchar(100) NULL,
      FrozenBaseUnitCost decimal(18,6) NOT NULL,
      CONSTRAINT CK_OrderLineModifierIngredientEffect_Kind CHECK
      (
        (EffectKind='RemoveIngredient' AND QuantityDelta=0)
        OR (EffectKind='AddQuantity' AND QuantityDelta>0 AND UnitId IS NOT NULL AND BaseQuantityDelta>0)
        OR (EffectKind='AdjustQuantity' AND QuantityDelta<>0 AND UnitId IS NOT NULL AND BaseQuantityDelta IS NOT NULL)
      ),
      CONSTRAINT FK_OrderLineModifierIngredientEffect_Modifier_Rfc
        FOREIGN KEY (Rfc,OrderLineModifierId) REFERENCES restaurante.OrderLineModifier (Rfc,Id),
      CONSTRAINT FK_OrderLineModifierIngredientEffect_Material_Rfc
        FOREIGN KEY (Rfc,MaterialId) REFERENCES logistica.Material (Rfc,Id),
      CONSTRAINT FK_OrderLineModifierIngredientEffect_Unit
        FOREIGN KEY (UnitId) REFERENCES logistica.UnitOfMeasure (Id),
      CONSTRAINT UX_OrderLineModifierIngredientEffect
        UNIQUE (Rfc,OrderLineModifierId,MaterialId,EffectKind)
    );
  END;

  EXEC sys.sp_executesql N'
    INSERT INTO restaurante.OrderLineModifierIngredientEffect
      (Rfc,OrderLineModifierId,MaterialId,EffectKind,QuantityDelta,UnitId,BaseQuantityDelta,
       MaterialNameSnapshot,UnitNameSnapshot,FrozenBaseUnitCost)
    SELECT insertedModifier.Rfc,insertedModifier.Id,delta.MaterialId,delta.EffectKind,
           delta.QuantityDelta,delta.UnitId,
           CASE WHEN delta.EffectKind=''RemoveIngredient'' THEN NULL
                ELSE delta.QuantityDelta*COALESCE(materialConversion.Factor,globalConversion.Factor,
                  CASE WHEN delta.UnitId=material.BaseUnitId THEN 1 END) END,
           material.[Description],COALESCE(NULLIF(unitInfo.Abbreviation,''''),unitInfo.UnitName),
           CAST(ISNULL(material.BaseUnitPrice,0) AS decimal(18,6))
    FROM restaurante.OrderLineModifier insertedModifier
    JOIN restaurante.ModifierIngredientDelta delta
      ON delta.Rfc=insertedModifier.Rfc AND delta.ModifierOptionId=insertedModifier.ModifierOptionId
    JOIN logistica.Material material ON material.Rfc=delta.Rfc AND material.Id=delta.MaterialId
    LEFT JOIN logistica.UnitOfMeasure unitInfo ON unitInfo.Id=delta.UnitId
    OUTER APPLY
    (
      SELECT TOP (1) conversionInfo.Factor
      FROM logistica.MaterialUnitConversion conversionInfo
      WHERE conversionInfo.Rfc=material.Rfc AND conversionInfo.MaterialId=material.Id
        AND conversionInfo.FromUnitId=delta.UnitId AND conversionInfo.ToUnitId=material.BaseUnitId
        AND conversionInfo.IsActive=1
    ) materialConversion
    OUTER APPLY
    (
      SELECT TOP (1) conversionInfo.Factor
      FROM logistica.UnitConversion conversionInfo
      WHERE conversionInfo.FromUnitId=delta.UnitId AND conversionInfo.ToUnitId=material.BaseUnitId
        AND conversionInfo.IsActive=1
    ) globalConversion
    WHERE NOT EXISTS
    (
      SELECT 1 FROM restaurante.OrderLineModifierIngredientEffect snapshotInfo
      WHERE snapshotInfo.Rfc=insertedModifier.Rfc
        AND snapshotInfo.OrderLineModifierId=insertedModifier.Id
        AND snapshotInfo.MaterialId=delta.MaterialId
        AND snapshotInfo.EffectKind=delta.EffectKind
    );';

  EXEC sys.sp_executesql N'
    CREATE OR ALTER TRIGGER restaurante.TR_OrderLineModifier_SnapshotIngredientEffects
    ON restaurante.OrderLineModifier
    AFTER INSERT
    AS
    BEGIN
      SET NOCOUNT ON;
      INSERT INTO restaurante.OrderLineModifierIngredientEffect
        (Rfc,OrderLineModifierId,MaterialId,EffectKind,QuantityDelta,UnitId,BaseQuantityDelta,
         MaterialNameSnapshot,UnitNameSnapshot,FrozenBaseUnitCost)
      SELECT insertedModifier.Rfc,insertedModifier.Id,delta.MaterialId,delta.EffectKind,
             delta.QuantityDelta,delta.UnitId,
             CASE WHEN delta.EffectKind=''RemoveIngredient'' THEN NULL
                  ELSE delta.QuantityDelta*COALESCE(materialConversion.Factor,globalConversion.Factor,
                    CASE WHEN delta.UnitId=material.BaseUnitId THEN 1 END) END,
             material.[Description],COALESCE(NULLIF(unitInfo.Abbreviation,''''),unitInfo.UnitName),
             CAST(ISNULL(material.BaseUnitPrice,0) AS decimal(18,6))
      FROM inserted insertedModifier
      JOIN restaurante.ModifierIngredientDelta delta
        ON delta.Rfc=insertedModifier.Rfc AND delta.ModifierOptionId=insertedModifier.ModifierOptionId
      JOIN logistica.Material material ON material.Rfc=delta.Rfc AND material.Id=delta.MaterialId
      LEFT JOIN logistica.UnitOfMeasure unitInfo ON unitInfo.Id=delta.UnitId
      OUTER APPLY
      (
        SELECT TOP (1) conversionInfo.Factor
        FROM logistica.MaterialUnitConversion conversionInfo
        WHERE conversionInfo.Rfc=material.Rfc AND conversionInfo.MaterialId=material.Id
          AND conversionInfo.FromUnitId=delta.UnitId AND conversionInfo.ToUnitId=material.BaseUnitId
          AND conversionInfo.IsActive=1
      ) materialConversion
      OUTER APPLY
      (
        SELECT TOP (1) conversionInfo.Factor
        FROM logistica.UnitConversion conversionInfo
        WHERE conversionInfo.FromUnitId=delta.UnitId AND conversionInfo.ToUnitId=material.BaseUnitId
          AND conversionInfo.IsActive=1
      ) globalConversion;
    END;';

  /* Validaciones de datos y relaciones que también se ejecutan en simulación. */
  EXEC sys.sp_executesql N'
    IF EXISTS
    (
      SELECT 1 FROM restaurante.Product
      WHERE ProductKind NOT IN (''Standard'', ''Combo'')
         OR (ProductKind = ''Standard'' AND MaterialId IS NULL)
         OR (ProductKind = ''Combo'' AND (MaterialId IS NOT NULL OR KitchenStationId IS NOT NULL OR PreparationMinutes IS NOT NULL))
    )
      THROW 51810, ''Existe un producto incompatible con su ProductKind.'', 1;

    IF EXISTS
    (
      SELECT 1
      FROM restaurante.ComboSlot slotInfo
      JOIN restaurante.Product comboProduct
        ON comboProduct.Rfc = slotInfo.Rfc AND comboProduct.Id = slotInfo.ComboProductId
      WHERE comboProduct.ProductKind <> ''Combo''
    )
      THROW 51811, ''Existe un slot cuyo padre no es un combo.'', 1;

    IF EXISTS
    (
      SELECT 1
      FROM restaurante.ComboSlotOption optionInfo
      JOIN restaurante.Product component
        ON component.Rfc = optionInfo.Rfc AND component.Id = optionInfo.ComponentProductId
      WHERE component.ProductKind <> ''Standard''
    )
      THROW 51812, ''V1 no permite combos dentro de combos.'', 1;

    IF EXISTS
    (
      SELECT 1
      FROM restaurante.ComboSlot slotInfo
      WHERE slotInfo.IsActive = 1
        AND (SELECT COUNT(*) FROM restaurante.ComboSlotOption optionInfo
             WHERE optionInfo.Rfc = slotInfo.Rfc AND optionInfo.ComboSlotId = slotInfo.Id AND optionInfo.IsActive = 1) < slotInfo.MinSelections
    )
      THROW 51813, ''Un slot activo no tiene suficientes opciones activas para cumplir su mínimo.'', 1;';

  DECLARE @ComboProductCount int;
  DECLARE @ComboSlotCount int;
  DECLARE @ComboOptionCount int;
  DECLARE @RemoveIngredientEffectCount int;
  EXEC sys.sp_executesql N'
    SELECT @ComboProductCount = COUNT(*) FROM restaurante.Product WHERE ProductKind = ''Combo'';
    SELECT @ComboSlotCount = COUNT(*) FROM restaurante.ComboSlot;
    SELECT @ComboOptionCount = COUNT(*) FROM restaurante.ComboSlotOption;
    SELECT @RemoveIngredientEffectCount = COUNT(*) FROM restaurante.ModifierIngredientDelta WHERE EffectKind = ''RemoveIngredient'';',
    N'@ComboProductCount int OUTPUT,@ComboSlotCount int OUTPUT,@ComboOptionCount int OUTPUT,@RemoveIngredientEffectCount int OUTPUT',
    @ComboProductCount OUTPUT,@ComboSlotCount OUTPUT,@ComboOptionCount OUTPUT,@RemoveIngredientEffectCount OUTPUT;

  SELECT DB_NAME() AS DatabaseName,
         @ApplyChanges AS ApplyChanges,
         @ComboProductCount AS ComboProductCount,
         @ComboSlotCount AS ComboSlotCount,
         @ComboOptionCount AS ComboOptionCount,
         @RemoveIngredientEffectCount AS RemoveIngredientEffectCount,
         CASE WHEN @ApplyChanges = 1 THEN 'COMMITTED' ELSE 'DRY_RUN_VALIDATED' END AS MigrationStatus;

  IF @ApplyChanges = 1
    COMMIT TRANSACTION;
  ELSE
    ROLLBACK TRANSACTION;
END TRY
BEGIN CATCH
  IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
  THROW;
END CATCH;
