/*
  Rol de producción del material.

  ProductType y FulfillmentMode se agregaron como columnas independientes y sin validación
  cruzada. FulfillmentMode gobierna todo el comportamiento (venta, producción, costeo, guardas)
  mientras que ProductType quedó como etiqueta decorativa, así que ambas derivaron hacia
  combinaciones que se contradicen: materiales con receta activa marcados como insumo comprado,
  reventa marcada como producto terminado, semielaborados que nadie puede producir.

  Este script normaliza los pares existentes con reglas deterministas basadas en evidencia
  (¿tiene receta activa? ¿lo usa otra receta como ingrediente?) y después fija un CHECK que
  impide volver a crear combinaciones inválidas.

  Sólo toca lo que nadie decidió (ProductType = 'RawMaterial', el default del esquema) o lo que
  es inválido de origen (FinishedGood + StockItem, que el CHECK rechazaría). Un par válido que
  alguien eligió a mano se respeta: ahora existe el selector de rol en Materiales y no
  corresponde que un script contradiga esa decisión.

  Idempotente. No borra datos: sólo reescribe ProductType/FulfillmentMode.

  Pares válidos a partir de aquí:
    RawMaterial  + StockItem    Insumo comprado
    Resale       + StockItem    Artículo de reventa
    SemiFinished + MakeToStock  Subproducto por lote
    SemiFinished + MakeToOrder  Subreceta al momento
    FinishedGood + MakeToOrder  Producto terminado al momento
    FinishedGood + MakeToStock  Producto terminado por lote
*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET XACT_ABORT ON;
SET NOCOUNT ON;

BEGIN TRANSACTION;

-- Fotografía previa, para poder auditar qué movió el script.
IF OBJECT_ID('logistica.MaterialProductionRoleBackfill', 'U') IS NULL
BEGIN
  CREATE TABLE logistica.MaterialProductionRoleBackfill
  (
    Id                  int IDENTITY(1,1) NOT NULL CONSTRAINT PK_MaterialProductionRoleBackfill PRIMARY KEY,
    Rfc                 varchar(50)  NOT NULL,
    MaterialId          int          NOT NULL,
    [Description]       nvarchar(800) NULL,
    OldProductType      varchar(30)  NOT NULL,
    OldFulfillmentMode  varchar(30)  NOT NULL,
    NewProductType      varchar(30)  NOT NULL,
    NewFulfillmentMode  varchar(30)  NOT NULL,
    RuleCode            varchar(4)   NOT NULL,
    AppliedAtUtc        datetime2(0) NOT NULL CONSTRAINT DF_MaterialProductionRoleBackfill_AppliedAtUtc DEFAULT (SYSUTCDATETIME())
  );
END;

GO

-- Evidencia por material: qué es en realidad, según los datos y no según la etiqueta.
IF OBJECT_ID('tempdb..#Evidence') IS NOT NULL DROP TABLE #Evidence;

SELECT
  m.Rfc,
  m.Id AS MaterialId,
  m.[Description],
  m.ProductType,
  m.FulfillmentMode,
  CAST(CASE WHEN EXISTS (
    SELECT 1 FROM logistica.BomHeader h
    JOIN logistica.BomVersion v ON v.Rfc = h.Rfc AND v.BomHeaderId = h.Id AND v.[Status] = 'Active'
    WHERE h.Rfc = m.Rfc AND h.ProductMaterialId = m.Id) THEN 1 ELSE 0 END AS bit) AS HasActiveRecipe,
  CAST(CASE WHEN EXISTS (
    SELECT 1 FROM logistica.BomHeader h
    JOIN logistica.BomVersion v ON v.Rfc = h.Rfc AND v.BomHeaderId = h.Id
    WHERE h.Rfc = m.Rfc AND h.ProductMaterialId = m.Id) THEN 1 ELSE 0 END AS bit) AS HasAnyRecipe,
  CAST(CASE WHEN EXISTS (
    SELECT 1 FROM logistica.BomComponent c
    JOIN logistica.BomVersion v ON v.Rfc = c.Rfc AND v.Id = c.BomVersionId AND v.[Status] = 'Active'
    WHERE c.Rfc = m.Rfc AND c.ComponentMaterialId = m.Id) THEN 1 ELSE 0 END AS bit) AS IsIngredient,
  CAST(CASE WHEN EXISTS (
    SELECT 1 FROM restaurante.Product p
    WHERE p.Rfc = m.Rfc AND p.MaterialId = m.Id) THEN 1 ELSE 0 END AS bit) AS HasSellableProduct
INTO #Evidence
FROM logistica.Material m;

-- Rol correcto por evidencia. NULL = no hay razón para tocarlo.
IF OBJECT_ID('tempdb..#Target') IS NOT NULL DROP TABLE #Target;

SELECT
  e.Rfc,
  e.MaterialId,
  e.[Description],
  e.ProductType AS OldProductType,
  e.FulfillmentMode AS OldFulfillmentMode,
  target.NewProductType,
  target.NewFulfillmentMode,
  target.RuleCode
INTO #Target
FROM #Evidence e
CROSS APPLY
(
  SELECT TOP (1) rules.NewProductType, rules.NewFulfillmentMode, rules.RuleCode
  FROM
  (
    -- C1  Receta activa + lo usa otra receta + guarda inventario -> subproducto por lote.
    SELECT 1 AS Priority, 'SemiFinished' AS NewProductType, 'MakeToStock' AS NewFulfillmentMode, 'C1' AS RuleCode
    WHERE e.HasActiveRecipe = 1 AND e.IsIngredient = 1 AND e.FulfillmentMode = 'StockItem'

    UNION ALL
    -- C3  Insumo sin clasificar, con receta activa y que nadie usa como ingrediente:
    --     es un producto terminado. El modo sólo cambia cuando no hay producto vendible,
    --     para no alterar el POS.
    SELECT 3, 'FinishedGood',
      CASE WHEN e.FulfillmentMode = 'StockItem' AND e.HasSellableProduct = 0
           THEN 'MakeToOrder' ELSE e.FulfillmentMode END, 'C3'
    WHERE e.HasActiveRecipe = 1 AND e.IsIngredient = 0 AND e.ProductType = 'RawMaterial'

    UNION ALL
    -- C4  Terminado que descuenta inventario y tiene receta -> producto terminado por lote.
    SELECT 4, 'FinishedGood', 'MakeToStock', 'C4'
    WHERE e.ProductType = 'FinishedGood' AND e.FulfillmentMode = 'StockItem' AND e.HasAnyRecipe = 1

    UNION ALL
    -- C5  Terminado que descuenta inventario y no tiene receta -> reventa.
    SELECT 5, 'Resale', 'StockItem', 'C5'
    WHERE e.ProductType = 'FinishedGood' AND e.FulfillmentMode = 'StockItem' AND e.HasAnyRecipe = 0
  ) rules
  ORDER BY rules.Priority
) target
WHERE target.NewProductType <> e.ProductType
   OR target.NewFulfillmentMode <> e.FulfillmentMode;

INSERT logistica.MaterialProductionRoleBackfill
  (Rfc, MaterialId, [Description], OldProductType, OldFulfillmentMode, NewProductType, NewFulfillmentMode, RuleCode)
SELECT Rfc, MaterialId, [Description], OldProductType, OldFulfillmentMode, NewProductType, NewFulfillmentMode, RuleCode
FROM #Target;

UPDATE m
SET m.ProductType = t.NewProductType,
    m.FulfillmentMode = t.NewFulfillmentMode,
    m.UpdatedDate = CONVERT(date, SYSUTCDATETIME())
FROM logistica.Material m
JOIN #Target t ON t.Rfc = m.Rfc AND t.MaterialId = m.Id;

DECLARE @Reclassified int = (SELECT COUNT(*) FROM #Target);
PRINT CONCAT('Materiales reclasificados: ', @Reclassified);

GO

-- Cualquier par que siga fuera del catálogo se normaliza al rol más conservador antes
-- de fijar la restricción, para que el script no falle por datos heredados desconocidos.
UPDATE logistica.Material
SET ProductType = 'RawMaterial',
    FulfillmentMode = 'StockItem',
    UpdatedDate = CONVERT(date, SYSUTCDATETIME())
WHERE CONCAT(ProductType, '|', FulfillmentMode) NOT IN
  ('RawMaterial|StockItem', 'Resale|StockItem', 'SemiFinished|MakeToStock',
   'SemiFinished|MakeToOrder', 'FinishedGood|MakeToOrder', 'FinishedGood|MakeToStock');

GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_Material_ProductionRole')
BEGIN
  ALTER TABLE logistica.Material WITH CHECK
    ADD CONSTRAINT CK_Material_ProductionRole CHECK
    (
      CONCAT(ProductType, '|', FulfillmentMode) IN
      ('RawMaterial|StockItem', 'Resale|StockItem', 'SemiFinished|MakeToStock',
       'SemiFinished|MakeToOrder', 'FinishedGood|MakeToOrder', 'FinishedGood|MakeToStock')
    );
END;

GO

DROP TABLE #Evidence;
DROP TABLE #Target;

COMMIT TRANSACTION;
