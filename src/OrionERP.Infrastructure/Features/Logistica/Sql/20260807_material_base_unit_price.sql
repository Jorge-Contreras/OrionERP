SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION;

IF COL_LENGTH('logistica.Material', 'BaseUnitPrice') IS NULL
BEGIN
    IF COL_LENGTH('logistica.Material', 'Price') IS NOT NULL
        EXEC sys.sp_rename 'logistica.Material.Price', 'BaseUnitPrice', 'COLUMN';
    ELSE
        ALTER TABLE logistica.Material ADD BaseUnitPrice decimal(18,6) NULL;
END;

ALTER TABLE logistica.Material ALTER COLUMN BaseUnitPrice decimal(18,6) NULL;

IF OBJECT_ID('logistica.PurchaseOrderLine', 'U') IS NOT NULL
BEGIN
    IF COL_LENGTH('logistica.PurchaseOrderLine', 'BaseUnitPrice') IS NULL
    BEGIN
        IF COL_LENGTH('logistica.PurchaseOrderLine', 'UnitPrice') IS NOT NULL
            EXEC sys.sp_rename 'logistica.PurchaseOrderLine.UnitPrice', 'BaseUnitPrice', 'COLUMN';
        ELSE
            ALTER TABLE logistica.PurchaseOrderLine ADD BaseUnitPrice decimal(18,6) NULL;
    END;

    ALTER TABLE logistica.PurchaseOrderLine ALTER COLUMN BaseUnitPrice decimal(18,6) NULL;
END;

COMMIT TRANSACTION;
GO
