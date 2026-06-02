SET NOCOUNT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF OBJECT_ID(N'dbo.Reservation_Extra', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.Reservation_Extra', N'TaxMode') IS NULL
BEGIN
    ALTER TABLE dbo.Reservation_Extra
    ADD TaxMode nvarchar(40) NOT NULL
        CONSTRAINT DF_Reservation_Extra_TaxMode DEFAULT (N'TaxableExclusive');
END;

IF OBJECT_ID(N'dbo.Reservation_Extra', N'U') IS NOT NULL
   AND NOT EXISTS (
       SELECT 1
       FROM sys.check_constraints
       WHERE parent_object_id = OBJECT_ID(N'dbo.Reservation_Extra')
         AND name = N'CK_Reservation_Extra_TaxMode'
   )
BEGIN
    EXEC(N'
    ALTER TABLE dbo.Reservation_Extra WITH CHECK
    ADD CONSTRAINT CK_Reservation_Extra_TaxMode
        CHECK (TaxMode IN (N''TaxableExclusive'', N''TaxIncluded'', N''NonTaxable''));');
END;
