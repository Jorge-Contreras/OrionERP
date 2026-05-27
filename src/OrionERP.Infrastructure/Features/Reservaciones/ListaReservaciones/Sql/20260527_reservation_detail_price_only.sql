SET NOCOUNT ON;

IF COL_LENGTH('dbo.RESERVATION_DETAIL', 'PRICE') IS NOT NULL
   AND COL_LENGTH('dbo.RESERVATION_DETAIL', 'DISCOUNTED_PRICE') IS NOT NULL
BEGIN
    UPDATE dbo.RESERVATION_DETAIL
    SET PRICE = DISCOUNTED_PRICE
    WHERE DISCOUNTED_PRICE IS NOT NULL
      AND DISCOUNTED_PRICE <> 0
      AND ISNULL(PRICE, 0) <> DISCOUNTED_PRICE;
END;

DECLARE @DropDefaultConstraintsSql nvarchar(max) = N'';

SELECT @DropDefaultConstraintsSql +=
    N'ALTER TABLE ' + QUOTENAME(SCHEMA_NAME(t.schema_id)) + N'.' + QUOTENAME(t.name) +
    N' DROP CONSTRAINT ' + QUOTENAME(dc.name) + N';' + CHAR(13) + CHAR(10)
FROM sys.default_constraints dc
INNER JOIN sys.tables t
    ON t.object_id = dc.parent_object_id
INNER JOIN sys.columns c
    ON c.object_id = dc.parent_object_id
   AND c.column_id = dc.parent_column_id
WHERE dc.parent_object_id = OBJECT_ID(N'dbo.RESERVATION_DETAIL')
  AND c.name IN (N'DISCOUNT', N'DISCOUNTED_PRICE');

IF @DropDefaultConstraintsSql <> N''
BEGIN
    EXEC sys.sp_executesql @DropDefaultConstraintsSql;
END;

IF COL_LENGTH('dbo.RESERVATION_DETAIL', 'DISCOUNT') IS NOT NULL
BEGIN
    ALTER TABLE dbo.RESERVATION_DETAIL
    DROP COLUMN DISCOUNT;
END;

IF COL_LENGTH('dbo.RESERVATION_DETAIL', 'DISCOUNTED_PRICE') IS NOT NULL
BEGIN
    ALTER TABLE dbo.RESERVATION_DETAIL
    DROP COLUMN DISCOUNTED_PRICE;
END;
