/*
  Recosteo de recetas activas tras invertir la precedencia del costo.

  Antes, el costo de un ingrediente salía de COALESCE(BaseUnitPrice, costo de subreceta). Como
  todos los subproductos tienen un BaseUnitPrice capturado a mano, su receta nunca influía en el
  costo del platillo padre. Ahora un material que realmente se produce —subproducto por lote o
  subreceta al momento— se valora con el costo de su receta activa, y BaseUnitPrice queda como
  respaldo para lo que se compra hecho.

  FrozenTheoreticalCost se calcula una sola vez al activar, así que los valores guardados quedaron
  obsoletos. Este script los recalcula en cascada: repite la pasada completa hasta que ningún
  costo cambia, de modo que cada nivel use el costo ya actualizado del nivel de abajo.

  Requisito: ejecutar DESPUÉS de 20260830_material_production_role.sql, porque la nueva regla
  depende de que FulfillmentMode refleje la realidad.

  Idempotente. Deja el detalle antes/después en logistica.BomCostRecalculationLog.
*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET XACT_ABORT ON;
SET NOCOUNT ON;

BEGIN TRANSACTION;

IF OBJECT_ID('logistica.BomCostRecalculationLog', 'U') IS NULL
BEGIN
  CREATE TABLE logistica.BomCostRecalculationLog
  (
    Id            int IDENTITY(1,1) NOT NULL CONSTRAINT PK_BomCostRecalculationLog PRIMARY KEY,
    Rfc           varchar(50)   NOT NULL,
    BomVersionId  bigint        NOT NULL,
    MaterialId    int           NOT NULL,
    [Description] nvarchar(800) NULL,
    OldUnitCost   decimal(18,6) NOT NULL,
    NewUnitCost   decimal(18,6) NOT NULL,
    AppliedAtUtc  datetime2(0)  NOT NULL CONSTRAINT DF_BomCostRecalculationLog_AppliedAtUtc DEFAULT (SYSUTCDATETIME())
  );
END;

GO

/*
  Guarda de seguridad. Una receta activa cuyo rendimiento está expresado en una unidad distinta
  a la unidad base de su material atribuye el costo de todo el lote a una sola unidad base: un
  lote de 1 KILOGRAMO cargado sobre 1 GRAMO infla el costo mil veces, y ese error se propaga a
  todos los platillos que la usan. Mientras exista una de esas recetas el recosteo no debe correr,
  porque produciría costos absurdos que además quedarían congelados.

  Para ver cuáles son:
    SELECT m.Id, m.[Description], v.YieldQuantity, yu.UnitName AS UnidadRendimiento, bu.UnitName AS UnidadBase
    FROM logistica.BomHeader h
    JOIN logistica.BomVersion v ON v.Rfc = h.Rfc AND v.BomHeaderId = h.Id AND v.[Status] = 'Active'
    JOIN logistica.Material m ON m.Rfc = h.Rfc AND m.Id = h.ProductMaterialId
    JOIN logistica.UnitOfMeasure yu ON yu.Id = v.YieldUnitId
    JOIN logistica.UnitOfMeasure bu ON bu.Id = m.BaseUnitId
    WHERE v.YieldUnitId <> m.BaseUnitId;
*/
IF EXISTS
(
  SELECT 1
  FROM logistica.BomHeader h
  JOIN logistica.BomVersion v ON v.Rfc = h.Rfc AND v.BomHeaderId = h.Id AND v.[Status] = 'Active'
  JOIN logistica.Material m ON m.Rfc = h.Rfc AND m.Id = h.ProductMaterialId
  WHERE v.YieldUnitId <> m.BaseUnitId
)
BEGIN
  THROW 50002, 'Hay recetas activas cuyo rendimiento no está en la unidad base de su material. Corrígelas antes de recostear: el costo saldría inflado y quedaría congelado.', 1;
END;

IF OBJECT_ID('tempdb..#Before') IS NOT NULL DROP TABLE #Before;

SELECT v.Rfc, v.Id AS BomVersionId, h.ProductMaterialId AS MaterialId, m.[Description],
       CAST(ISNULL(v.FrozenTheoreticalCost, 0) AS decimal(18,6)) AS OldUnitCost
INTO #Before
FROM logistica.BomVersion v
JOIN logistica.BomHeader h ON h.Rfc = v.Rfc AND h.Id = v.BomHeaderId
JOIN logistica.Material m ON m.Rfc = h.Rfc AND m.Id = h.ProductMaterialId
WHERE v.[Status] = 'Active';

DECLARE @Pass int = 0;
DECLARE @Changed int = 1;

-- El grafo de recetas es acíclico (SaveDraft lo garantiza), así que la iteración converge.
-- El tope de 32 pasadas coincide con la profundidad máxima de BOM que admite la aplicación.
WHILE @Changed > 0 AND @Pass < 32
BEGIN
  SET @Pass += 1;

  UPDATE v
  SET FrozenTheoreticalCost = recalculated.UnitCost
  FROM logistica.BomVersion v
  CROSS APPLY
  (
    SELECT CAST(ISNULL(SUM(
      component.Quantity
      * (1 + component.ExpectedWastePercent / 100.0)
      * COALESCE(materialConversion.Factor, globalConversion.Factor,
                 CASE WHEN component.UnitId = material.BaseUnitId THEN 1 END)
      * COALESCE(subBom.FrozenTheoreticalCost / NULLIF(subBom.YieldQuantity, 0), material.BaseUnitPrice, 0)
    ), 0) / NULLIF(v.YieldQuantity, 0) AS decimal(18,6)) AS UnitCost
    FROM logistica.BomComponent component
    JOIN logistica.Material material
      ON material.Rfc = component.Rfc AND material.Id = component.ComponentMaterialId
    OUTER APPLY
    (
      SELECT TOP (1) conversionInfo.Factor
      FROM logistica.MaterialUnitConversion conversionInfo
      WHERE conversionInfo.Rfc = material.Rfc AND conversionInfo.MaterialId = material.Id
        AND conversionInfo.FromUnitId = component.UnitId AND conversionInfo.ToUnitId = material.BaseUnitId
        AND conversionInfo.IsActive = 1
    ) materialConversion
    OUTER APPLY
    (
      SELECT TOP (1) conversionInfo.Factor
      FROM logistica.UnitConversion conversionInfo
      WHERE conversionInfo.FromUnitId = component.UnitId AND conversionInfo.ToUnitId = material.BaseUnitId
        AND conversionInfo.IsActive = 1
    ) globalConversion
    OUTER APPLY
    (
      SELECT TOP (1) childVersion.FrozenTheoreticalCost, childVersion.YieldQuantity
      FROM logistica.BomHeader childHeader
      JOIN logistica.BomVersion childVersion
        ON childVersion.Rfc = childHeader.Rfc AND childVersion.BomHeaderId = childHeader.Id
       AND childVersion.[Status] = 'Active'
      WHERE childHeader.Rfc = material.Rfc AND childHeader.ProductMaterialId = material.Id
        AND material.FulfillmentMode IN ('MakeToStock', 'MakeToOrder')
    ) subBom
    WHERE component.Rfc = v.Rfc AND component.BomVersionId = v.Id
  ) recalculated
  WHERE v.[Status] = 'Active'
    AND ISNULL(v.FrozenTheoreticalCost, -1) <> ISNULL(recalculated.UnitCost, 0);

  SET @Changed = @@ROWCOUNT;
  PRINT CONCAT('Pasada ', @Pass, ': ', @Changed, ' versiones recosteadas.');
END;

IF @Pass >= 32 AND @Changed > 0
  THROW 50001, 'El recosteo no convergió en 32 pasadas; revisa si hay un ciclo entre recetas.', 1;

INSERT logistica.BomCostRecalculationLog (Rfc, BomVersionId, MaterialId, [Description], OldUnitCost, NewUnitCost)
SELECT b.Rfc, b.BomVersionId, b.MaterialId, b.[Description], b.OldUnitCost,
       CAST(ISNULL(v.FrozenTheoreticalCost, 0) AS decimal(18,6))
FROM #Before b
JOIN logistica.BomVersion v ON v.Rfc = b.Rfc AND v.Id = b.BomVersionId
WHERE CAST(ISNULL(v.FrozenTheoreticalCost, 0) AS decimal(18,6)) <> b.OldUnitCost;

GO

DROP TABLE #Before;

COMMIT TRANSACTION;
