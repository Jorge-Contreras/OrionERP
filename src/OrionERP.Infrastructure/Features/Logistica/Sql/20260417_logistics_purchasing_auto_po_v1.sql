SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET NOCOUNT ON;

IF COL_LENGTH('logistica.PurchaseOrderLine', 'PurchaseQuantitySnapshot') IS NULL
BEGIN
    ALTER TABLE logistica.PurchaseOrderLine
        ADD PurchaseQuantitySnapshot decimal(18,4) NULL;
END;
GO

IF COL_LENGTH('logistica.PurchaseOrderLine', 'PurchaseUnitNameSnapshot') IS NULL
BEGIN
    ALTER TABLE logistica.PurchaseOrderLine
        ADD PurchaseUnitNameSnapshot varchar(50) NULL;
END;
GO

UPDATE line
SET
    PurchaseQuantitySnapshot = CASE
        WHEN line.PurchaseQuantitySnapshot IS NULL OR line.PurchaseQuantitySnapshot <= 0
            THEN CASE
                WHEN material.PurchaseQuantity IS NULL OR material.PurchaseQuantity <= 0 THEN 1
                ELSE material.PurchaseQuantity
            END
        ELSE line.PurchaseQuantitySnapshot
    END,
    PurchaseUnitNameSnapshot = COALESCE(NULLIF(line.PurchaseUnitNameSnapshot, ''), purchaseU.UnitName)
FROM logistica.PurchaseOrderLine line
JOIN logistica.Material material
  ON material.Id = line.MaterialId
LEFT JOIN logistica.UnitOfMeasure purchaseU
  ON purchaseU.Id = material.PurchaseUnitId
WHERE line.PurchaseQuantitySnapshot IS NULL
   OR line.PurchaseQuantitySnapshot <= 0
   OR line.PurchaseUnitNameSnapshot IS NULL;
GO

UPDATE logistica.PurchaseOrderLine
SET PurchaseQuantitySnapshot = 1
WHERE PurchaseQuantitySnapshot IS NULL
   OR PurchaseQuantitySnapshot <= 0;
GO

IF EXISTS (
    SELECT 1
    FROM sys.columns
    WHERE object_id = OBJECT_ID('logistica.PurchaseOrderLine')
      AND name = 'PurchaseQuantitySnapshot'
      AND is_nullable = 1
)
BEGIN
    ALTER TABLE logistica.PurchaseOrderLine
        ALTER COLUMN PurchaseQuantitySnapshot decimal(18,4) NOT NULL;
END;
GO

IF NOT EXISTS (
    SELECT 1
    FROM sys.default_constraints dc
    JOIN sys.columns c
      ON c.object_id = dc.parent_object_id
     AND c.column_id = dc.parent_column_id
    WHERE dc.parent_object_id = OBJECT_ID('logistica.PurchaseOrderLine')
      AND c.name = 'PurchaseQuantitySnapshot'
)
BEGIN
    ALTER TABLE logistica.PurchaseOrderLine
        ADD CONSTRAINT DF_PurchaseOrderLine_PurchaseQuantitySnapshot
            DEFAULT (1) FOR PurchaseQuantitySnapshot;
END;
GO
