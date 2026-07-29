SET NOCOUNT ON;
SET XACT_ABORT ON;

IF OBJECT_ID('restaurante.OrderLine', 'U') IS NULL
  THROW 51120, 'Ejecuta primero 20260713_restaurant_operations.sql.', 1;

BEGIN TRANSACTION;

IF COL_LENGTH('restaurante.OrderLine', 'IsCustom') IS NULL
BEGIN
  ALTER TABLE restaurante.OrderLine
    ADD IsCustom bit NOT NULL
      CONSTRAINT DF_OrderLine_IsCustom DEFAULT (0) WITH VALUES;
END;

IF EXISTS
(
  SELECT 1
  FROM sys.columns
  WHERE object_id = OBJECT_ID('restaurante.OrderLine')
    AND [name] = 'ProductId'
    AND is_nullable = 0
)
BEGIN
  IF EXISTS
  (
    SELECT 1
    FROM sys.foreign_keys
    WHERE parent_object_id = OBJECT_ID('restaurante.OrderLine')
      AND [name] = 'FK_OrderLine_Product_Rfc'
  )
  BEGIN
    ALTER TABLE restaurante.OrderLine
      DROP CONSTRAINT FK_OrderLine_Product_Rfc;
  END;

  ALTER TABLE restaurante.OrderLine
    ALTER COLUMN ProductId bigint NULL;
END;

IF NOT EXISTS
(
  SELECT 1
  FROM sys.foreign_keys
  WHERE parent_object_id = OBJECT_ID('restaurante.OrderLine')
    AND [name] = 'FK_OrderLine_Product_Rfc'
)
BEGIN
  ALTER TABLE restaurante.OrderLine WITH CHECK
    ADD CONSTRAINT FK_OrderLine_Product_Rfc
      FOREIGN KEY (Rfc, ProductId)
      REFERENCES restaurante.Product (Rfc, Id);
END;

IF NOT EXISTS
(
  SELECT 1
  FROM sys.check_constraints
  WHERE parent_object_id = OBJECT_ID('restaurante.OrderLine')
    AND [name] = 'CK_OrderLine_CustomProduct'
)
BEGIN
  EXEC sys.sp_executesql N'
    ALTER TABLE restaurante.OrderLine WITH CHECK
      ADD CONSTRAINT CK_OrderLine_CustomProduct
        CHECK
        (
          (IsCustom = 0 AND ProductId IS NOT NULL)
          OR
          (IsCustom = 1 AND ProductId IS NULL)
        );';
END;

COMMIT TRANSACTION;
