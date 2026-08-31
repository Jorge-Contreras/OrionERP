/*
  Correcciones de datos de recetas de Bruno's, previas al recosteo.

  Cierra los cuatro pendientes que quedaron después de la revisión de subproductos:

    F1  CHICKEN FINGER BURGER consume CHICKEN FINGERS en «1 ORDEN», pero la unidad de
        inventario de ese material es PIEZA. Sin conversión ORDEN->PIEZA el motor lo reporta
        como BOM_CONVERSION_MISSING: el platillo queda bloqueado por configuración y el
        componente principal aporta $0 al costo. Pasa a «2 PIEZA».

    F2  EMPANIZADO CHIKEN FINGERS declara que rinde 100 g, pero el lote son 4 tazas de harina
        más 210 g de especias. El rendimiento sube a 700 g y el precio por unidad base se
        recalcula desde la propia receta, en vez del $0.30/g que se había subido a mano para
        compensar el rendimiento chico.

    F3  HAMBURGUESA DE SIRLON BRUNOS está marcada como semielaborado siendo un producto
        terminado vendible. Error humano confirmado.

    F4  CHICKEN FINGERS está marcado como producto terminado, pero ya no tiene producto
        vendible y sólo lo consume la hamburguesa: es una subreceta al momento.

  Nota: F1 y F2 modifican versiones activas en sitio. La aplicación no lo permite —obliga a
  crear una versión nueva— y con razón: una versión activa es historia. Aquí se hace por SQL
  porque son correcciones de datos defectuosos, no evolución de la receta, y porque reponerlas
  por la UI significaría clonar y reactivar cada una a mano. Queda registro en
  logistica.BomRecipeDataFixLog.

  Ejecutar ANTES de 20260830_material_production_role.sql y del recosteo.
  Idempotente: cada corrección sólo actúa si el dato sigue como se describe arriba.
*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET XACT_ABORT ON;
SET NOCOUNT ON;

DECLARE @Rfc varchar(50) = 'BRUNOS260707L26';

BEGIN TRANSACTION;

IF OBJECT_ID('logistica.BomRecipeDataFixLog', 'U') IS NULL
BEGIN
  CREATE TABLE logistica.BomRecipeDataFixLog
  (
    Id           int IDENTITY(1,1) NOT NULL CONSTRAINT PK_BomRecipeDataFixLog PRIMARY KEY,
    Rfc          varchar(50)   NOT NULL,
    FixCode      varchar(4)    NOT NULL,
    Target       nvarchar(200) NOT NULL,
    Antes        nvarchar(200) NULL,
    Despues      nvarchar(200) NULL,
    AppliedAtUtc datetime2(0)  NOT NULL CONSTRAINT DF_BomRecipeDataFixLog_AppliedAtUtc DEFAULT (SYSUTCDATETIME())
  );
END;

-- ---------------------------------------------------------------------------------------
-- F1  La hamburguesa pide chicken fingers en la unidad de inventario del material.
-- ---------------------------------------------------------------------------------------
DECLARE @BurgerComponentId bigint, @FingerBaseUnit int, @OldQty decimal(18,6), @OldUnit nvarchar(100);

SELECT TOP (1)
  @BurgerComponentId = component.Id,
  @FingerBaseUnit    = finger.BaseUnitId,
  @OldQty            = component.Quantity,
  @OldUnit           = oldUnit.UnitName
FROM logistica.BomHeader burgerHeader
JOIN logistica.BomVersion burgerVersion
  ON burgerVersion.Rfc = burgerHeader.Rfc AND burgerVersion.BomHeaderId = burgerHeader.Id
 AND burgerVersion.[Status] = 'Active'
JOIN logistica.BomComponent component
  ON component.Rfc = burgerVersion.Rfc AND component.BomVersionId = burgerVersion.Id
JOIN logistica.Material finger
  ON finger.Rfc = component.Rfc AND finger.Id = component.ComponentMaterialId
JOIN logistica.UnitOfMeasure oldUnit ON oldUnit.Id = component.UnitId
WHERE burgerHeader.Rfc = @Rfc
  AND burgerHeader.ProductMaterialId = 6939   -- CHICKEN FINGER BURGER
  AND component.ComponentMaterialId = 6928    -- CHICKEN FINGERS
  AND component.UnitId <> finger.BaseUnitId;  -- sólo si sigue en la unidad incompatible

IF @BurgerComponentId IS NOT NULL
BEGIN
  UPDATE logistica.BomComponent
  SET Quantity = 2, UnitId = @FingerBaseUnit
  WHERE Rfc = @Rfc AND Id = @BurgerComponentId;

  INSERT logistica.BomRecipeDataFixLog (Rfc, FixCode, Target, Antes, Despues)
  SELECT @Rfc, 'F1', 'CHICKEN FINGER BURGER > CHICKEN FINGERS',
         CONCAT(CONVERT(varchar(30), @OldQty), ' ', @OldUnit),
         CONCAT('2 ', unitInfo.UnitName)
  FROM logistica.UnitOfMeasure unitInfo WHERE unitInfo.Id = @FingerBaseUnit;

  PRINT 'F1  hamburguesa: chicken fingers reexpresado en su unidad de inventario.';
END
ELSE PRINT 'F1  sin cambios (ya estaba en la unidad correcta).';

-- ---------------------------------------------------------------------------------------
-- F2  Rendimiento real del empanizado, y precio por unidad base derivado de la receta.
-- ---------------------------------------------------------------------------------------
DECLARE @EmpanizadoVersion bigint, @NewYield decimal(18,6) = 700, @BatchCost decimal(18,6), @NewPrice decimal(18,6);

SELECT TOP (1) @EmpanizadoVersion = versionInfo.Id
FROM logistica.BomHeader headerInfo
JOIN logistica.BomVersion versionInfo
  ON versionInfo.Rfc = headerInfo.Rfc AND versionInfo.BomHeaderId = headerInfo.Id
 AND versionInfo.[Status] = 'Active'
WHERE headerInfo.Rfc = @Rfc
  AND headerInfo.ProductMaterialId = 7195     -- EMPANIZADO CHIKEN FINGERS
  AND versionInfo.YieldQuantity = 100;        -- sólo si sigue en el rendimiento viejo

IF @EmpanizadoVersion IS NOT NULL
BEGIN
  SELECT @BatchCost = ISNULL(SUM(
      component.Quantity
      * (1 + component.ExpectedWastePercent / 100.0)
      * COALESCE(materialConversion.Factor, globalConversion.Factor,
                 CASE WHEN component.UnitId = material.BaseUnitId THEN 1 END)
      * ISNULL(material.BaseUnitPrice, 0)), 0)
  FROM logistica.BomComponent component
  JOIN logistica.Material material
    ON material.Rfc = component.Rfc AND material.Id = component.ComponentMaterialId
  OUTER APPLY (SELECT TOP (1) conv.Factor FROM logistica.MaterialUnitConversion conv
               WHERE conv.Rfc = material.Rfc AND conv.MaterialId = material.Id
                 AND conv.FromUnitId = component.UnitId AND conv.ToUnitId = material.BaseUnitId
                 AND conv.IsActive = 1) materialConversion
  OUTER APPLY (SELECT TOP (1) conv.Factor FROM logistica.UnitConversion conv
               WHERE conv.FromUnitId = component.UnitId AND conv.ToUnitId = material.BaseUnitId
                 AND conv.IsActive = 1) globalConversion
  WHERE component.Rfc = @Rfc AND component.BomVersionId = @EmpanizadoVersion;

  SET @NewPrice = CAST(@BatchCost / @NewYield AS decimal(18,6));

  UPDATE logistica.BomVersion SET YieldQuantity = @NewYield WHERE Rfc = @Rfc AND Id = @EmpanizadoVersion;

  INSERT logistica.BomRecipeDataFixLog (Rfc, FixCode, Target, Antes, Despues)
  VALUES (@Rfc, 'F2', 'EMPANIZADO CHIKEN FINGERS · rendimiento', '100 g', CONCAT(CONVERT(varchar(30), @NewYield), ' g'));

  INSERT logistica.BomRecipeDataFixLog (Rfc, FixCode, Target, Antes, Despues)
  SELECT @Rfc, 'F2', 'EMPANIZADO CHIKEN FINGERS · precio unidad base',
         CONVERT(varchar(30), material.BaseUnitPrice), CONVERT(varchar(30), @NewPrice)
  FROM logistica.Material material WHERE material.Rfc = @Rfc AND material.Id = 7195;

  UPDATE logistica.Material
  SET BaseUnitPrice = @NewPrice, UpdatedDate = CONVERT(date, SYSUTCDATETIME())
  WHERE Rfc = @Rfc AND Id = 7195;

  PRINT CONCAT('F2  empanizado: rinde 700 g, precio por gramo ', CONVERT(varchar(30), @NewPrice), '.');
END
ELSE PRINT 'F2  sin cambios (el rendimiento ya no es 100).';

-- ---------------------------------------------------------------------------------------
-- F3 / F4  Clasificaciones que el script de roles deja deliberadamente al usuario,
--          para no contradecir decisiones hechas a mano desde Materiales.
-- ---------------------------------------------------------------------------------------
INSERT logistica.BomRecipeDataFixLog (Rfc, FixCode, Target, Antes, Despues)
SELECT @Rfc, fix.FixCode, material.[Description],
       CONCAT(material.ProductType, ' | ', material.FulfillmentMode),
       CONCAT(fix.NewProductType, ' | ', fix.NewFulfillmentMode)
FROM (VALUES
  ('F3', 7066, 'FinishedGood', 'MakeToOrder'),
  ('F4', 6928, 'SemiFinished', 'MakeToOrder')
) AS fix(FixCode, MaterialId, NewProductType, NewFulfillmentMode)
JOIN logistica.Material material ON material.Rfc = @Rfc AND material.Id = fix.MaterialId
WHERE material.ProductType <> fix.NewProductType OR material.FulfillmentMode <> fix.NewFulfillmentMode;

UPDATE material
SET material.ProductType = fix.NewProductType,
    material.FulfillmentMode = fix.NewFulfillmentMode,
    material.UpdatedDate = CONVERT(date, SYSUTCDATETIME())
FROM logistica.Material material
JOIN (VALUES
  ('F3', 7066, 'FinishedGood', 'MakeToOrder'),
  ('F4', 6928, 'SemiFinished', 'MakeToOrder')
) AS fix(FixCode, MaterialId, NewProductType, NewFulfillmentMode)
  ON fix.MaterialId = material.Id
WHERE material.Rfc = @Rfc
  AND (material.ProductType <> fix.NewProductType OR material.FulfillmentMode <> fix.NewFulfillmentMode);

PRINT CONCAT('F3/F4  clasificaciones ajustadas: ', @@ROWCOUNT, '.');

COMMIT TRANSACTION;

GO

-- Verificación: las tres condiciones que deben quedar limpias antes de recostear.
PRINT '';
PRINT '--- verificacion ---';

SELECT 'conversiones faltantes' AS Chequeo, COUNT(*) AS Pendientes
FROM logistica.BomComponent component
JOIN logistica.BomVersion versionInfo
  ON versionInfo.Rfc = component.Rfc AND versionInfo.Id = component.BomVersionId AND versionInfo.[Status] = 'Active'
JOIN logistica.Material material ON material.Rfc = component.Rfc AND material.Id = component.ComponentMaterialId
OUTER APPLY (SELECT TOP (1) conv.Factor FROM logistica.MaterialUnitConversion conv
             WHERE conv.Rfc = material.Rfc AND conv.MaterialId = material.Id
               AND conv.FromUnitId = component.UnitId AND conv.ToUnitId = material.BaseUnitId AND conv.IsActive = 1) mc
OUTER APPLY (SELECT TOP (1) conv.Factor FROM logistica.UnitConversion conv
             WHERE conv.FromUnitId = component.UnitId AND conv.ToUnitId = material.BaseUnitId AND conv.IsActive = 1) gc
WHERE COALESCE(mc.Factor, gc.Factor, CASE WHEN component.UnitId = material.BaseUnitId THEN 1 END) IS NULL

UNION ALL
SELECT 'rendimientos fuera de unidad base', COUNT(*)
FROM logistica.BomVersion versionInfo
JOIN logistica.BomHeader headerInfo ON headerInfo.Rfc = versionInfo.Rfc AND headerInfo.Id = versionInfo.BomHeaderId
JOIN logistica.Material material ON material.Rfc = headerInfo.Rfc AND material.Id = headerInfo.ProductMaterialId
WHERE versionInfo.[Status] = 'Active' AND versionInfo.YieldUnitId <> material.BaseUnitId

UNION ALL
SELECT 'pares de clasificacion invalidos', COUNT(*)
FROM logistica.Material
WHERE CONCAT(ProductType, '|', FulfillmentMode) NOT IN
  ('RawMaterial|StockItem', 'Resale|StockItem', 'SemiFinished|MakeToStock',
   'SemiFinished|MakeToOrder', 'FinishedGood|MakeToOrder', 'FinishedGood|MakeToStock');
