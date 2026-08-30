SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION;

IF COL_LENGTH('logistica.PurchaseReceiptLine', 'SubtotalAmount') IS NULL
    ALTER TABLE logistica.PurchaseReceiptLine ADD SubtotalAmount decimal(18,2) NULL;

IF COL_LENGTH('logistica.PurchaseReceiptLine', 'IvaAmount') IS NULL
    ALTER TABLE logistica.PurchaseReceiptLine ADD IvaAmount decimal(18,2) NULL;

IF COL_LENGTH('logistica.PurchaseReceiptLine', 'TotalAmount') IS NULL
    ALTER TABLE logistica.PurchaseReceiptLine ADD TotalAmount decimal(18,2) NULL;

IF COL_LENGTH('logistica.PurchaseReceiptLine', 'IncludesIva') IS NULL
BEGIN
    ALTER TABLE logistica.PurchaseReceiptLine
        ADD IncludesIva bit NOT NULL
            CONSTRAINT DF_PurchaseReceiptLine_IncludesIva DEFAULT (0) WITH VALUES;
END;

IF OBJECT_ID('logistica.CK_PurchaseReceiptLine_ReceiptAmounts', 'C') IS NULL
BEGIN
    EXEC sys.sp_executesql N'
        ALTER TABLE logistica.PurchaseReceiptLine WITH CHECK
            ADD CONSTRAINT CK_PurchaseReceiptLine_ReceiptAmounts CHECK
            (
                (SubtotalAmount IS NULL AND IvaAmount IS NULL AND TotalAmount IS NULL)
                OR
                (SubtotalAmount >= 0 AND IvaAmount >= 0 AND TotalAmount > 0
                 AND SubtotalAmount + IvaAmount = TotalAmount)
            );';
END;

COMMIT TRANSACTION;
GO
