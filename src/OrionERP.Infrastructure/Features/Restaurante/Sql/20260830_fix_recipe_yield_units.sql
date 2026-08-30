/*
  Corrige los rendimientos que están expresados fuera de la unidad base del material.

  Una receta activa cuyo YieldUnitId no es la unidad base de su material atribuye el costo de
  todo el lote a una sola unidad base. En Bruno's eso infló CHICKEN FINGERS a $3,567 y
  CHICKEN FINGER BURGER a $3,939 en cuanto el costo empezó a salir de la receta.

  Este script sólo corrige el caso **derivable sin adivinar**: cuando existe una conversión
  activa de la unidad del rendimiento hacia la unidad base, se reexpresa la misma cantidad en
  la unidad base. No es una estimación, es la misma cantidad en otra unidad.
    Ej. EMPANIZADO CHIKEN FINGERS: 1 KILOGRAMO -> 1000 GRAMO (factor 1000).

  Los casos sin conversión disponible NO se tocan: el rendimiento real es conocimiento de
  cocina y adivinarlo corrompería el costo de todo el menú en silencio. El script los lista al
  final para que se corrijan a mano desde /restaurante/recetas.
    En Bruno's, al 2026-08-30, quedan dos:
      MEZCLA DE ESPECIAS PARA SUERO DE LECHE  1 GRAMO  vs base MILILITRO
      CURTIDO DE PEPINOS                      1 LITRO  vs base REBANADA

  Ejecutar ANTES de 20260830_recipe_cost_recalculation.sql, que se niega a correr mientras
  quede alguna receta con el rendimiento fuera de su unidad base.

  Idempotente. Deja el antes/después en logistica.BomYieldUnitFixLog.
*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET XACT_ABORT ON;
SET NOCOUNT ON;

BEGIN TRANSACTION;

IF OBJECT_ID('logistica.BomYieldUnitFixLog', 'U') IS NULL
BEGIN
  CREATE TABLE logistica.BomYieldUnitFixLog
  (
    Id             int IDENTITY(1,1) NOT NULL CONSTRAINT PK_BomYieldUnitFixLog PRIMARY KEY,
    Rfc            varchar(50)    NOT NULL,
    BomVersionId   bigint         NOT NULL,
    MaterialId     int            NOT NULL,
    [Description]  nvarchar(800)  NULL,
    OldYieldQuantity decimal(18,6) NOT NULL,
    OldYieldUnitId int            NOT NULL,
    NewYieldQuantity decimal(18,6) NOT NULL,
    NewYieldUnitId int            NOT NULL,
    Factor         decimal(18,10) NOT NULL,
    AppliedAtUtc   datetime2(0)   NOT NULL CONSTRAINT DF_BomYieldUnitFixLog_AppliedAtUtc DEFAULT (SYSUTCDATETIME())
  );
END;

GO

IF OBJECT_ID('tempdb..#Convertible') IS NOT NULL DROP TABLE #Convertible;

SELECT v.Rfc, v.Id AS BomVersionId, m.Id AS MaterialId, m.[Description],
       v.YieldQuantity AS OldYieldQuantity, v.YieldUnitId AS OldYieldUnitId,
       CAST(v.YieldQuantity * conversion.Factor AS decimal(18,6)) AS NewYieldQuantity,
       m.BaseUnitId AS NewYieldUnitId,
       conversion.Factor
INTO #Convertible
FROM logistica.BomVersion v
JOIN logistica.BomHeader h ON h.Rfc = v.Rfc AND h.Id = v.BomHeaderId
JOIN logistica.Material m ON m.Rfc = h.Rfc AND m.Id = h.ProductMaterialId
CROSS APPLY
(
  -- La conversión específica del material gana sobre la global, igual que en el costeo.
  SELECT TOP (1) candidate.Factor
  FROM
  (
    SELECT 1 AS Priority, mc.Factor
    FROM logistica.MaterialUnitConversion mc
    WHERE mc.Rfc = m.Rfc AND mc.MaterialId = m.Id
      AND mc.FromUnitId = v.YieldUnitId AND mc.ToUnitId = m.BaseUnitId AND mc.IsActive = 1
    UNION ALL
    SELECT 2, gc.Factor
    FROM logistica.UnitConversion gc
    WHERE gc.FromUnitId = v.YieldUnitId AND gc.ToUnitId = m.BaseUnitId AND gc.IsActive = 1
  ) candidate
  ORDER BY candidate.Priority
) conversion
WHERE v.[Status] = 'Active'
  AND v.YieldUnitId <> m.BaseUnitId;

INSERT logistica.BomYieldUnitFixLog
  (Rfc, BomVersionId, MaterialId, [Description], OldYieldQuantity, OldYieldUnitId, NewYieldQuantity, NewYieldUnitId, Factor)
SELECT Rfc, BomVersionId, MaterialId, [Description], OldYieldQuantity, OldYieldUnitId, NewYieldQuantity, NewYieldUnitId, Factor
FROM #Convertible;

UPDATE v
SET v.YieldQuantity = c.NewYieldQuantity,
    v.YieldUnitId = c.NewYieldUnitId
FROM logistica.BomVersion v
JOIN #Convertible c ON c.Rfc = v.Rfc AND c.BomVersionId = v.Id;

DECLARE @Fixed int = (SELECT COUNT(*) FROM #Convertible);
PRINT CONCAT('Rendimientos reexpresados en su unidad base: ', @Fixed);

DROP TABLE #Convertible;

GO

PRINT '';
PRINT 'Rendimientos que siguen fuera de la unidad base y NO se pueden derivar.';
PRINT 'Corrígelos a mano en /restaurante/recetas antes de recostear:';

SELECT m.Id AS MaterialId, m.[Description], v.YieldQuantity,
       yieldUnit.UnitName AS UnidadRendimiento, baseUnit.UnitName AS UnidadBase
FROM logistica.BomVersion v
JOIN logistica.BomHeader h ON h.Rfc = v.Rfc AND h.Id = v.BomHeaderId
JOIN logistica.Material m ON m.Rfc = h.Rfc AND m.Id = h.ProductMaterialId
JOIN logistica.UnitOfMeasure yieldUnit ON yieldUnit.Id = v.YieldUnitId
JOIN logistica.UnitOfMeasure baseUnit ON baseUnit.Id = m.BaseUnitId
WHERE v.[Status] = 'Active' AND v.YieldUnitId <> m.BaseUnitId
ORDER BY m.[Description];

COMMIT TRANSACTION;
