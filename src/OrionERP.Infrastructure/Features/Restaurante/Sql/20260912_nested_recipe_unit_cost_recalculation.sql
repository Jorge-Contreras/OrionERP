/*
  Corrects the stored cost of active and draft BOM versions after fixing the
  nested-recipe unit-cost calculation.

  FrozenTheoreticalCost is already the cost per yield/base unit. The previous
  calculation divided that value by the child recipe yield a second time when
  the material was consumed by another recipe. This migration recalculates
  active recipes to a fixed point and then recalculates drafts against the
  corrected active child recipes. Retired versions remain historical.
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

DECLARE @ExpectedDatabase sysname=N'$(ExpectedDatabase)';
DECLARE @ApplyInput nvarchar(20)=N'$(ApplyChanges)';
DECLARE @ApplyChanges bit=0;
DECLARE @MigrationId nvarchar(200)=N'$(MigrationId)';
DECLARE @MigrationChecksum varchar(128)='$(MigrationChecksum)';
IF @ApplyInput NOT LIKE N'$'+N'(%'
BEGIN
  IF @ApplyInput NOT IN(N'0',N'1') THROW 53300,'ApplyChanges debe ser 0 o 1.',1;
  SET @ApplyChanges=CONVERT(bit,@ApplyInput);
END;
IF @ExpectedDatabase LIKE N'$'+N'(%'
   OR @ExpectedDatabase NOT IN(N'Orion_Sandbox',N'grupocarpio',N'Orion_CutoverValidation_20260908')
  THROW 53301,'Base esperada no permitida.',1;
IF DB_NAME()<>@ExpectedDatabase THROW 53302,'La conexión no apunta a la base declarada.',1;
IF @MigrationId<>N'20260912_nested_recipe_unit_cost_recalculation' THROW 53303,'MigrationId incorrecto.',1;
IF @MigrationChecksum LIKE '$'+'(%' OR LEN(@MigrationChecksum)<>64
   OR @MigrationChecksum LIKE '%[^0-9A-F]%' COLLATE Latin1_General_100_BIN2
  THROW 53304,'MigrationChecksum inválido.',1;
IF OBJECT_ID(N'orion.SchemaMigration',N'U') IS NULL
   OR OBJECT_ID(N'logistica.BomVersion',N'U') IS NULL
   OR OBJECT_ID(N'logistica.BomComponent',N'U') IS NULL
  THROW 53305,'Falta el esquema requerido para recostear recetas.',1;
IF USER_NAME()<>N'dbo' THROW 53306,'El recosteo completo requiere una conexión dbo.',1;
IF SESSION_CONTEXT(N'OrionRfc') IS NOT NULL OR SESSION_CONTEXT(N'OrionERP.CompanyId') IS NOT NULL
  THROW 53307,'La sesión de migración no debe estar limitada a una empresa.',1;

DECLARE @ExistingChecksum char(64)=(SELECT Checksum FROM orion.SchemaMigration WHERE MigrationId=@MigrationId);
IF @ExistingChecksum IS NOT NULL AND UPPER(@ExistingChecksum)<>UPPER(@MigrationChecksum)
  THROW 53308,'El MigrationId ya existe con otro checksum.',1;
IF @ExistingChecksum IS NOT NULL
BEGIN
  SELECT N'YA_APLICADO' Estado,@MigrationId MigrationId,@ExistingChecksum Checksum;
  RETURN;
END;

SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;
BEGIN TRANSACTION;

DECLARE @LockResult int;
EXEC @LockResult=sys.sp_getapplock
  @Resource=N'OrionERP:Restaurant:NestedRecipeUnitCostRecalculation',
  @LockMode=N'Exclusive',@LockOwner=N'Transaction',@LockTimeout=30000;
IF @LockResult<0 THROW 53309,'No se pudo obtener el bloqueo de recosteo.',1;

IF EXISTS
(
  SELECT 1
  FROM logistica.BomVersion versionInfo
  JOIN logistica.BomHeader headerInfo
    ON headerInfo.Rfc=versionInfo.Rfc AND headerInfo.Id=versionInfo.BomHeaderId
  JOIN logistica.Material product
    ON product.Rfc=headerInfo.Rfc AND product.Id=headerInfo.ProductMaterialId
  WHERE versionInfo.[Status] IN('Active','Draft')
    AND versionInfo.YieldUnitId<>product.BaseUnitId
)
  THROW 53310,'Hay recetas vigentes cuyo rendimiento no coincide con la unidad base del producto.',1;

IF EXISTS
(
  SELECT 1
  FROM logistica.BomVersion versionInfo
  JOIN logistica.BomComponent component
    ON component.Rfc=versionInfo.Rfc AND component.BomVersionId=versionInfo.Id
  JOIN logistica.Material material
    ON material.Rfc=component.Rfc AND material.Id=component.ComponentMaterialId
  OUTER APPLY
  (
    SELECT TOP(1) conversionInfo.Factor
    FROM logistica.MaterialUnitConversion conversionInfo
    WHERE conversionInfo.Rfc=material.Rfc AND conversionInfo.MaterialId=material.Id
      AND conversionInfo.FromUnitId=component.UnitId AND conversionInfo.ToUnitId=material.BaseUnitId
      AND conversionInfo.IsActive=1
  ) materialConversion
  OUTER APPLY
  (
    SELECT TOP(1) conversionInfo.Factor
    FROM logistica.UnitConversion conversionInfo
    WHERE conversionInfo.FromUnitId=component.UnitId AND conversionInfo.ToUnitId=material.BaseUnitId
      AND conversionInfo.IsActive=1
  ) globalConversion
  WHERE versionInfo.[Status] IN('Active','Draft')
    AND COALESCE(materialConversion.Factor,globalConversion.Factor,
                 CASE WHEN component.UnitId=material.BaseUnitId THEN 1 END) IS NULL
)
  THROW 53311,'Hay ingredientes vigentes sin conversión hacia su unidad base.',1;

CREATE TABLE #Before
(
  Rfc varchar(50) NOT NULL,
  BomVersionId bigint NOT NULL,
  MaterialId int NOT NULL,
  MaterialCode varchar(50) NOT NULL,
  [Description] nvarchar(800) NULL,
  [Status] varchar(20) NOT NULL,
  OldUnitCost decimal(18,6) NOT NULL,
  PRIMARY KEY(Rfc,BomVersionId)
);

INSERT #Before(Rfc,BomVersionId,MaterialId,MaterialCode,[Description],[Status],OldUnitCost)
SELECT versionInfo.Rfc,versionInfo.Id,headerInfo.ProductMaterialId,product.MaterialCode,
       product.[Description],versionInfo.[Status],
       CONVERT(decimal(18,6),ISNULL(versionInfo.FrozenTheoreticalCost,0))
FROM logistica.BomVersion versionInfo
JOIN logistica.BomHeader headerInfo
  ON headerInfo.Rfc=versionInfo.Rfc AND headerInfo.Id=versionInfo.BomHeaderId
JOIN logistica.Material product
  ON product.Rfc=headerInfo.Rfc AND product.Id=headerInfo.ProductMaterialId
WHERE versionInfo.[Status] IN('Active','Draft');

DECLARE @Pass int=0,@Changed int=1;
WHILE @Changed>0 AND @Pass<32
BEGIN
  SET @Pass+=1;

  UPDATE versionInfo
  SET FrozenTheoreticalCost=recalculated.UnitCost
  FROM logistica.BomVersion versionInfo
  CROSS APPLY
  (
    SELECT CONVERT(decimal(18,6),ISNULL(SUM(
      component.Quantity
      * (1+component.ExpectedWastePercent/100.0)
      * COALESCE(materialConversion.Factor,globalConversion.Factor,
                 CASE WHEN component.UnitId=material.BaseUnitId THEN 1 END)
      * COALESCE(subBom.UnitCost,material.BaseUnitPrice,0)
    ),0)/NULLIF(versionInfo.YieldQuantity,0)) AS UnitCost
    FROM logistica.BomComponent component
    JOIN logistica.Material material
      ON material.Rfc=component.Rfc AND material.Id=component.ComponentMaterialId
    OUTER APPLY
    (
      SELECT TOP(1) conversionInfo.Factor
      FROM logistica.MaterialUnitConversion conversionInfo
      WHERE conversionInfo.Rfc=material.Rfc AND conversionInfo.MaterialId=material.Id
        AND conversionInfo.FromUnitId=component.UnitId AND conversionInfo.ToUnitId=material.BaseUnitId
        AND conversionInfo.IsActive=1
    ) materialConversion
    OUTER APPLY
    (
      SELECT TOP(1) conversionInfo.Factor
      FROM logistica.UnitConversion conversionInfo
      WHERE conversionInfo.FromUnitId=component.UnitId AND conversionInfo.ToUnitId=material.BaseUnitId
        AND conversionInfo.IsActive=1
    ) globalConversion
    OUTER APPLY
    (
      SELECT TOP(1) childVersion.FrozenTheoreticalCost AS UnitCost
      FROM logistica.BomHeader childHeader
      JOIN logistica.BomVersion childVersion
        ON childVersion.Rfc=childHeader.Rfc AND childVersion.BomHeaderId=childHeader.Id
       AND childVersion.[Status]='Active'
      WHERE childHeader.Rfc=material.Rfc AND childHeader.ProductMaterialId=material.Id
        AND material.FulfillmentMode IN('MakeToStock','MakeToOrder')
    ) subBom
    WHERE component.Rfc=versionInfo.Rfc AND component.BomVersionId=versionInfo.Id
  ) recalculated
  WHERE versionInfo.[Status]='Active'
    AND ISNULL(versionInfo.FrozenTheoreticalCost,-1)<>recalculated.UnitCost;

  SET @Changed=@@ROWCOUNT;
END;

IF @Pass>=32 AND @Changed>0
  THROW 53312,'El recosteo no convergió en 32 pasadas; revisa ciclos entre recetas.',1;

UPDATE versionInfo
SET FrozenTheoreticalCost=recalculated.UnitCost
FROM logistica.BomVersion versionInfo
CROSS APPLY
(
  SELECT CONVERT(decimal(18,6),ISNULL(SUM(
    component.Quantity
    * (1+component.ExpectedWastePercent/100.0)
    * COALESCE(materialConversion.Factor,globalConversion.Factor,
               CASE WHEN component.UnitId=material.BaseUnitId THEN 1 END)
    * COALESCE(subBom.UnitCost,material.BaseUnitPrice,0)
  ),0)/NULLIF(versionInfo.YieldQuantity,0)) AS UnitCost
  FROM logistica.BomComponent component
  JOIN logistica.Material material
    ON material.Rfc=component.Rfc AND material.Id=component.ComponentMaterialId
  OUTER APPLY
  (
    SELECT TOP(1) conversionInfo.Factor
    FROM logistica.MaterialUnitConversion conversionInfo
    WHERE conversionInfo.Rfc=material.Rfc AND conversionInfo.MaterialId=material.Id
      AND conversionInfo.FromUnitId=component.UnitId AND conversionInfo.ToUnitId=material.BaseUnitId
      AND conversionInfo.IsActive=1
  ) materialConversion
  OUTER APPLY
  (
    SELECT TOP(1) conversionInfo.Factor
    FROM logistica.UnitConversion conversionInfo
    WHERE conversionInfo.FromUnitId=component.UnitId AND conversionInfo.ToUnitId=material.BaseUnitId
      AND conversionInfo.IsActive=1
  ) globalConversion
  OUTER APPLY
  (
    SELECT TOP(1) childVersion.FrozenTheoreticalCost AS UnitCost
    FROM logistica.BomHeader childHeader
    JOIN logistica.BomVersion childVersion
      ON childVersion.Rfc=childHeader.Rfc AND childVersion.BomHeaderId=childHeader.Id
     AND childVersion.[Status]='Active'
    WHERE childHeader.Rfc=material.Rfc AND childHeader.ProductMaterialId=material.Id
      AND material.FulfillmentMode IN('MakeToStock','MakeToOrder')
  ) subBom
  WHERE component.Rfc=versionInfo.Rfc AND component.BomVersionId=versionInfo.Id
) recalculated
WHERE versionInfo.[Status]='Draft'
  AND ISNULL(versionInfo.FrozenTheoreticalCost,-1)<>recalculated.UnitCost;

IF EXISTS
(
  SELECT 1
  FROM logistica.BomVersion versionInfo
  CROSS APPLY
  (
    SELECT CONVERT(decimal(18,6),ISNULL(SUM(
      component.Quantity
      * (1+component.ExpectedWastePercent/100.0)
      * COALESCE(materialConversion.Factor,globalConversion.Factor,
                 CASE WHEN component.UnitId=material.BaseUnitId THEN 1 END)
      * COALESCE(subBom.UnitCost,material.BaseUnitPrice,0)
    ),0)/NULLIF(versionInfo.YieldQuantity,0)) AS UnitCost
    FROM logistica.BomComponent component
    JOIN logistica.Material material
      ON material.Rfc=component.Rfc AND material.Id=component.ComponentMaterialId
    OUTER APPLY
    (
      SELECT TOP(1) conversionInfo.Factor
      FROM logistica.MaterialUnitConversion conversionInfo
      WHERE conversionInfo.Rfc=material.Rfc AND conversionInfo.MaterialId=material.Id
        AND conversionInfo.FromUnitId=component.UnitId AND conversionInfo.ToUnitId=material.BaseUnitId
        AND conversionInfo.IsActive=1
    ) materialConversion
    OUTER APPLY
    (
      SELECT TOP(1) conversionInfo.Factor
      FROM logistica.UnitConversion conversionInfo
      WHERE conversionInfo.FromUnitId=component.UnitId AND conversionInfo.ToUnitId=material.BaseUnitId
        AND conversionInfo.IsActive=1
    ) globalConversion
    OUTER APPLY
    (
      SELECT TOP(1) childVersion.FrozenTheoreticalCost AS UnitCost
      FROM logistica.BomHeader childHeader
      JOIN logistica.BomVersion childVersion
        ON childVersion.Rfc=childHeader.Rfc AND childVersion.BomHeaderId=childHeader.Id
       AND childVersion.[Status]='Active'
      WHERE childHeader.Rfc=material.Rfc AND childHeader.ProductMaterialId=material.Id
        AND material.FulfillmentMode IN('MakeToStock','MakeToOrder')
    ) subBom
    WHERE component.Rfc=versionInfo.Rfc AND component.BomVersionId=versionInfo.Id
  ) recalculated
  WHERE versionInfo.[Status] IN('Active','Draft')
    AND ISNULL(versionInfo.FrozenTheoreticalCost,-1)<>recalculated.UnitCost
)
  THROW 53313,'El recosteo terminó con versiones vigentes fuera del costo corregido.',1;

IF OBJECT_ID(N'logistica.BomCostRecalculationLog',N'U') IS NULL
BEGIN
  CREATE TABLE logistica.BomCostRecalculationLog
  (
    Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_BomCostRecalculationLog PRIMARY KEY,
    Rfc varchar(50) NOT NULL,
    BomVersionId bigint NOT NULL,
    MaterialId int NOT NULL,
    [Description] nvarchar(800) NULL,
    OldUnitCost decimal(18,6) NOT NULL,
    NewUnitCost decimal(18,6) NOT NULL,
    AppliedAtUtc datetime2(0) NOT NULL CONSTRAINT DF_BomCostRecalculationLog_AppliedAtUtc DEFAULT(SYSUTCDATETIME())
  );
END;

INSERT logistica.BomCostRecalculationLog
  (Rfc,BomVersionId,MaterialId,[Description],OldUnitCost,NewUnitCost)
SELECT beforeInfo.Rfc,beforeInfo.BomVersionId,beforeInfo.MaterialId,beforeInfo.[Description],
       beforeInfo.OldUnitCost,CONVERT(decimal(18,6),ISNULL(versionInfo.FrozenTheoreticalCost,0))
FROM #Before beforeInfo
JOIN logistica.BomVersion versionInfo
  ON versionInfo.Rfc=beforeInfo.Rfc AND versionInfo.Id=beforeInfo.BomVersionId
WHERE beforeInfo.OldUnitCost<>CONVERT(decimal(18,6),ISNULL(versionInfo.FrozenTheoreticalCost,0));

SELECT beforeInfo.Rfc,COUNT(*) ChangedVersions,
       SUM(beforeInfo.OldUnitCost) OldUnitCostTotal,
       SUM(CONVERT(decimal(18,6),ISNULL(versionInfo.FrozenTheoreticalCost,0))) NewUnitCostTotal,
       @Pass ActivePasses
FROM #Before beforeInfo
JOIN logistica.BomVersion versionInfo
  ON versionInfo.Rfc=beforeInfo.Rfc AND versionInfo.Id=beforeInfo.BomVersionId
WHERE beforeInfo.OldUnitCost<>CONVERT(decimal(18,6),ISNULL(versionInfo.FrozenTheoreticalCost,0))
GROUP BY beforeInfo.Rfc
ORDER BY beforeInfo.Rfc;

SELECT beforeInfo.Rfc,beforeInfo.MaterialCode,beforeInfo.[Description],beforeInfo.[Status],
       beforeInfo.OldUnitCost,
       CONVERT(decimal(18,6),ISNULL(versionInfo.FrozenTheoreticalCost,0)) NewUnitCost
FROM #Before beforeInfo
JOIN logistica.BomVersion versionInfo
  ON versionInfo.Rfc=beforeInfo.Rfc AND versionInfo.Id=beforeInfo.BomVersionId
WHERE beforeInfo.Rfc='BRUNOS260707L26'
  AND beforeInfo.MaterialCode IN('MAT-006958','MAT-007252')
ORDER BY beforeInfo.MaterialCode,beforeInfo.[Status];

DECLARE @ApplyInput2 nvarchar(20)=N'$(ApplyChanges)';
DECLARE @ApplyChanges2 bit=CASE WHEN @ApplyInput2=N'1' THEN 1 ELSE 0 END;
DECLARE @MigrationId2 nvarchar(200)=N'$(MigrationId)';
DECLARE @MigrationChecksum2 varchar(128)='$(MigrationChecksum)';
DECLARE @AppVersionInput2 nvarchar(64)=N'$(AppVersion)';
DECLARE @AppVersion2 nvarchar(64)=CASE WHEN @AppVersionInput2 LIKE N'$'+N'(%'
  THEN NULL ELSE NULLIF(LTRIM(RTRIM(@AppVersionInput2)),N'') END;
IF @ApplyChanges2=1
BEGIN
  INSERT orion.SchemaMigration(MigrationId,Checksum,AppliedBy,AppVersion,DatabaseName)
  VALUES(@MigrationId2,@MigrationChecksum2,
    COALESCE(CONVERT(nvarchar(256),SESSION_CONTEXT(N'OrionERP.UserName')),CONVERT(nvarchar(256),ORIGINAL_LOGIN())),
    @AppVersion2,DB_NAME());
  COMMIT TRANSACTION;
  SELECT N'APLICADO' Estado,@MigrationId2 MigrationId;
END
ELSE
BEGIN
  SELECT N'PREVIEW' Estado,@MigrationId2 MigrationId;
  ROLLBACK TRANSACTION;
END;
