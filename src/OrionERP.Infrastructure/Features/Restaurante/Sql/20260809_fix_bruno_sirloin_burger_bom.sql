/*
  Corrige el BOM activo de BRUN-SIR-01 sin alterar su historial: la versión
  vigente referencia el material legado MAT-006938 (fabricación bajo pedido),
  cuando el insumo inventariable correcto es MAT-006977.

  Uso:
    sqlcmd ... -f 65001 -v ExpectedDatabase="Orion_SandBox" ApplyChanges="0" -i 20260809_fix_bruno_sirloin_burger_bom.sql
    sqlcmd ... -f 65001 -v ExpectedDatabase="Orion_SandBox" ApplyChanges="1" -i 20260809_fix_bruno_sirloin_burger_bom.sql

  ApplyChanges=0 crea y valida la nueva versión dentro de una transacción que
  se revierte. ApplyChanges=1 confirma. Producción requiere respaldo y
  autorización explícita.
*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET XACT_ABORT ON;
SET NOCOUNT ON;

DECLARE @ExpectedDatabase sysname = N'$(ExpectedDatabase)';
DECLARE @ApplyChanges bit = TRY_CONVERT(bit, N'$(ApplyChanges)');
DECLARE @Rfc varchar(50) = 'BRUNOS260707L26';
DECLARE @Sku varchar(50) = 'BRUN-SIR-01';
DECLARE @LegacyMaterialCode varchar(20) = 'MAT-006938';
DECLARE @SirloinPattyMaterialCode varchar(20) = 'MAT-006977';
DECLARE @MigrationUser varchar(256) = '20260809_fix_bruno_sirloin_burger_bom';
DECLARE @LockResult int;
DECLARE @ProductMaterialId int;
DECLARE @LegacyMaterialId int;
DECLARE @SirloinPattyMaterialId int;
DECLARE @BomHeaderId bigint;
DECLARE @OldActiveVersionId bigint;
DECLARE @NewActiveVersionId bigint;
DECLARE @NewVersionNumber int;
DECLARE @SourceRecipeId bigint;
DECLARE @NewRecipeId bigint;
DECLARE @OldComponentCount int;
DECLARE @OldRecipeStepCount int;
DECLARE @Now datetime2 = SYSUTCDATETIME();

IF @ExpectedDatabase NOT IN (N'Orion_Sandbox', N'Orion_SandBox', N'grupocarpio')
  THROW 51400, 'ExpectedDatabase debe ser Orion_Sandbox o grupocarpio.', 1;
IF DB_NAME() <> @ExpectedDatabase
  THROW 51401, 'La base conectada no coincide con ExpectedDatabase.', 1;
IF @ApplyChanges IS NULL
  THROW 51402, 'ApplyChanges debe ser 0 o 1.', 1;
IF SESSION_CONTEXT(N'OrionRfc') IS NOT NULL
  THROW 51403, 'La migración requiere SESSION_CONTEXT OrionRfc en NULL.', 1;
IF OBJECT_ID('restaurante.Product', 'U') IS NULL
   OR OBJECT_ID('logistica.BomVersion', 'U') IS NULL
   OR OBJECT_ID('logistica.BomComponent', 'U') IS NULL
  THROW 51404, 'Falta aplicar el esquema de restaurante y BOM.', 1;

SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;

BEGIN TRY
  BEGIN TRANSACTION;

  EXEC @LockResult = sys.sp_getapplock
    @Resource = N'OrionERP:Bruno:SirloinBurgerBom:20260809',
    @LockMode = N'Exclusive',
    @LockOwner = N'Transaction',
    @LockTimeout = 15000;
  IF @LockResult < 0
    THROW 51405, 'No fue posible obtener el bloqueo exclusivo de migración.', 1;

  IF (SELECT COUNT(*) FROM restaurante.Product WITH (UPDLOCK, HOLDLOCK) WHERE Rfc = @Rfc AND Sku = @Sku) <> 1
    THROW 51406, 'Debe existir exactamente un producto BRUN-SIR-01 en el RFC de Bruno.', 1;

  SELECT @ProductMaterialId = product.MaterialId
  FROM restaurante.Product product WITH (UPDLOCK, HOLDLOCK)
  WHERE product.Rfc = @Rfc AND product.Sku = @Sku;

  IF @ProductMaterialId <> 7066
    THROW 51407, 'BRUN-SIR-01 ya no corresponde al material terminado revisado 7066.', 1;

  SELECT @LegacyMaterialId = material.Id
  FROM logistica.Material material WITH (UPDLOCK, HOLDLOCK)
  WHERE material.Rfc = @Rfc AND material.MaterialCode = @LegacyMaterialCode;

  SELECT @SirloinPattyMaterialId = material.Id
  FROM logistica.Material material WITH (UPDLOCK, HOLDLOCK)
  WHERE material.Rfc = @Rfc AND material.MaterialCode = @SirloinPattyMaterialCode;

  IF @LegacyMaterialId <> 6938 OR @SirloinPattyMaterialId <> 6977
    THROW 51408, 'Los códigos de material ya no corresponden a los IDs revisados 6938 y 6977.', 1;

  IF NOT EXISTS
  (
    SELECT 1
    FROM logistica.Material
    WHERE Rfc = @Rfc
      AND Id = @LegacyMaterialId
      AND FulfillmentMode = 'MakeToOrder'
  )
    THROW 51409, 'El material legado ya no conserva la configuración MakeToOrder que originó el incidente.', 1;

  IF NOT EXISTS
  (
    SELECT 1
    FROM logistica.Material
    WHERE Rfc = @Rfc
      AND Id = @SirloinPattyMaterialId
      AND ProductType = 'RawMaterial'
      AND FulfillmentMode = 'StockItem'
      AND IsActive = 1
  )
    THROW 51410, 'El medallón de sirloin 6977 no está activo como materia prima inventariable.', 1;

  IF (SELECT COUNT(*) FROM logistica.BomHeader WITH (UPDLOCK, HOLDLOCK) WHERE Rfc = @Rfc AND ProductMaterialId = @ProductMaterialId) <> 1
    THROW 51411, 'Debe existir exactamente un encabezado BOM para el material 7066.', 1;

  SELECT @BomHeaderId = headerInfo.Id
  FROM logistica.BomHeader headerInfo WITH (UPDLOCK, HOLDLOCK)
  WHERE headerInfo.Rfc = @Rfc AND headerInfo.ProductMaterialId = @ProductMaterialId;

  IF (SELECT COUNT(*) FROM logistica.BomVersion WITH (UPDLOCK, HOLDLOCK) WHERE Rfc = @Rfc AND BomHeaderId = @BomHeaderId AND [Status] = 'Active') <> 1
    THROW 51412, 'El BOM de BRUN-SIR-01 debe tener exactamente una versión activa.', 1;

  SELECT @OldActiveVersionId = versionInfo.Id
  FROM logistica.BomVersion versionInfo WITH (UPDLOCK, HOLDLOCK)
  WHERE versionInfo.Rfc = @Rfc AND versionInfo.BomHeaderId = @BomHeaderId AND versionInfo.[Status] = 'Active';

  IF EXISTS
  (
    SELECT 1
    FROM logistica.BomComponent
    WHERE Rfc = @Rfc AND BomVersionId = @OldActiveVersionId AND ComponentMaterialId = @SirloinPattyMaterialId
  )
  AND NOT EXISTS
  (
    SELECT 1
    FROM logistica.BomComponent
    WHERE Rfc = @Rfc AND BomVersionId = @OldActiveVersionId AND ComponentMaterialId = @LegacyMaterialId
  )
  BEGIN
    SELECT
      'ALREADY_APPLIED' AS MigrationStatus,
      DB_NAME() AS DatabaseName,
      @OldActiveVersionId AS ActiveBomVersionId,
      @SirloinPattyMaterialId AS SirloinPattyMaterialId;

    IF @ApplyChanges = 1 COMMIT TRANSACTION;
    ELSE ROLLBACK TRANSACTION;
    RETURN;
  END;

  IF EXISTS
  (
    SELECT 1
    FROM logistica.BomVersion
    WHERE Rfc = @Rfc AND BomHeaderId = @BomHeaderId AND [Status] = 'Draft'
  )
    THROW 51413, 'Existe un borrador del BOM de BRUN-SIR-01; revísalo antes de ejecutar la corrección.', 1;

  IF
  (
    SELECT COUNT(*)
    FROM logistica.BomComponent
    WHERE Rfc = @Rfc AND BomVersionId = @OldActiveVersionId AND ComponentMaterialId = @LegacyMaterialId
  ) <> 1
    THROW 51414, 'La versión activa debe contener exactamente una referencia al material legado 6938.', 1;

  IF EXISTS
  (
    SELECT 1
    FROM logistica.BomComponent component
    JOIN logistica.Material replacement
      ON replacement.Rfc = component.Rfc AND replacement.Id = @SirloinPattyMaterialId
    WHERE component.Rfc = @Rfc
      AND component.BomVersionId = @OldActiveVersionId
      AND component.ComponentMaterialId = @LegacyMaterialId
      AND (component.Quantity <> 1 OR component.UnitId <> replacement.BaseUnitId)
  )
    THROW 51415, 'La cantidad o unidad del componente legado ya no coincide con el reemplazo revisado de una pieza.', 1;

  SELECT @OldComponentCount = COUNT(*)
  FROM logistica.BomComponent
  WHERE Rfc = @Rfc AND BomVersionId = @OldActiveVersionId;

  SELECT @SourceRecipeId = recipe.Id
  FROM logistica.Recipe recipe WITH (UPDLOCK, HOLDLOCK)
  WHERE recipe.Rfc = @Rfc AND recipe.BomVersionId = @OldActiveVersionId;

  IF @SourceRecipeId IS NULL
    THROW 51416, 'La versión activa del BOM no tiene una receta asociada.', 1;

  SELECT @OldRecipeStepCount = COUNT(*)
  FROM logistica.RecipeStep
  WHERE Rfc = @Rfc AND RecipeId = @SourceRecipeId;

  SELECT @NewVersionNumber = MAX(VersionNumber) + 1
  FROM logistica.BomVersion WITH (UPDLOCK, HOLDLOCK)
  WHERE Rfc = @Rfc AND BomHeaderId = @BomHeaderId;

  INSERT INTO logistica.BomVersion
    (Rfc, BomHeaderId, VersionNumber, [Status], YieldQuantity, YieldUnitId,
     ExpectedWastePercent, FrozenTheoreticalCost, EffectiveFrom, RetiredAt, CreatedAt, CreatedBy)
  SELECT
    Rfc, BomHeaderId, @NewVersionNumber, 'Draft', YieldQuantity, YieldUnitId,
    ExpectedWastePercent, FrozenTheoreticalCost, NULL, NULL, @Now, @MigrationUser
  FROM logistica.BomVersion
  WHERE Rfc = @Rfc AND Id = @OldActiveVersionId;
  SET @NewActiveVersionId = CONVERT(bigint, SCOPE_IDENTITY());

  INSERT INTO logistica.BomComponent
    (Rfc, BomVersionId, ComponentMaterialId, Quantity, UnitId,
     ExpectedWastePercent, IsOptional, SortOrder)
  SELECT
    Rfc,
    @NewActiveVersionId,
    CASE WHEN ComponentMaterialId = @LegacyMaterialId THEN @SirloinPattyMaterialId ELSE ComponentMaterialId END,
    Quantity,
    UnitId,
    ExpectedWastePercent,
    IsOptional,
    SortOrder
  FROM logistica.BomComponent
  WHERE Rfc = @Rfc AND BomVersionId = @OldActiveVersionId;

  INSERT INTO logistica.Recipe (Rfc, BomVersionId, [Name], SafetyNotes, IsActive)
  SELECT Rfc, @NewActiveVersionId, [Name], SafetyNotes, IsActive
  FROM logistica.Recipe
  WHERE Rfc = @Rfc AND Id = @SourceRecipeId;
  SET @NewRecipeId = CONVERT(bigint, SCOPE_IDENTITY());

  INSERT INTO logistica.RecipeStep
    (Rfc, RecipeId, StepNumber, Instruction, DurationMinutes, TemperatureC,
     Equipment, Image, ImageFileName, ImageContentType)
  SELECT
    Rfc, @NewRecipeId, StepNumber, Instruction, DurationMinutes, TemperatureC,
    Equipment, Image, ImageFileName, ImageContentType
  FROM logistica.RecipeStep
  WHERE Rfc = @Rfc AND RecipeId = @SourceRecipeId;

  UPDATE logistica.BomVersion
  SET [Status] = 'Retired', RetiredAt = @Now
  WHERE Rfc = @Rfc AND Id = @OldActiveVersionId AND [Status] = 'Active';

  UPDATE logistica.BomVersion
  SET [Status] = 'Active', EffectiveFrom = @Now, RetiredAt = NULL
  WHERE Rfc = @Rfc AND Id = @NewActiveVersionId AND [Status] = 'Draft';

  UPDATE logistica.BomHeader
  SET IsActive = 1
  WHERE Rfc = @Rfc AND Id = @BomHeaderId;

  IF (SELECT COUNT(*) FROM logistica.BomVersion WHERE Rfc = @Rfc AND BomHeaderId = @BomHeaderId AND [Status] = 'Active') <> 1
    THROW 51417, 'La corrección no dejó exactamente una versión activa.', 1;
  IF NOT EXISTS (SELECT 1 FROM logistica.BomVersion WHERE Rfc = @Rfc AND Id = @OldActiveVersionId AND [Status] = 'Retired')
    THROW 51418, 'La versión anterior no quedó preservada como retirada.', 1;
  IF (SELECT COUNT(*) FROM logistica.BomComponent WHERE Rfc = @Rfc AND BomVersionId = @NewActiveVersionId) <> @OldComponentCount
    THROW 51419, 'La nueva versión no conservó la cantidad de componentes.', 1;
  IF EXISTS (SELECT 1 FROM logistica.BomComponent WHERE Rfc = @Rfc AND BomVersionId = @NewActiveVersionId AND ComponentMaterialId = @LegacyMaterialId)
    THROW 51420, 'La nueva versión todavía referencia el material legado 6938.', 1;
  IF (SELECT COUNT(*) FROM logistica.BomComponent WHERE Rfc = @Rfc AND BomVersionId = @NewActiveVersionId AND ComponentMaterialId = @SirloinPattyMaterialId) <> 1
    THROW 51421, 'La nueva versión no contiene exactamente un medallón inventariable 6977.', 1;
  IF (SELECT COUNT(*) FROM logistica.RecipeStep WHERE Rfc = @Rfc AND RecipeId = @NewRecipeId) <> @OldRecipeStepCount
    THROW 51422, 'La nueva versión no conservó todos los pasos de la receta.', 1;

  SELECT
    CASE WHEN @ApplyChanges = 1 THEN 'COMMITTED' ELSE 'DRY_RUN_VALIDATED' END AS MigrationStatus,
    DB_NAME() AS DatabaseName,
    @Sku AS Sku,
    @OldActiveVersionId AS RetiredBomVersionId,
    @NewActiveVersionId AS ActiveBomVersionId,
    @LegacyMaterialId AS ReplacedMaterialId,
    @SirloinPattyMaterialId AS SirloinPattyMaterialId;

  IF @ApplyChanges = 1
    COMMIT TRANSACTION;
  ELSE
  BEGIN
    ROLLBACK TRANSACTION;
    PRINT 'SIMULACIÓN COMPLETA: todos los cambios fueron revertidos.';
  END;
END TRY
BEGIN CATCH
  IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
  THROW;
END CATCH;
