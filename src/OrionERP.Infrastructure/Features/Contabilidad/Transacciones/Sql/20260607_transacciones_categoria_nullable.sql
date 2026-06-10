SET XACT_ABORT ON;
GO

IF COL_LENGTH('dbo.Transacciones', 'Categoria') IS NOT NULL
   AND EXISTS
   (
       SELECT 1
       FROM sys.columns
       WHERE object_id = OBJECT_ID('dbo.Transacciones')
         AND name = 'Categoria'
         AND is_nullable = 0
   )
BEGIN
    ALTER TABLE dbo.Transacciones ALTER COLUMN Categoria int NULL;
END;
GO
