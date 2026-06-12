SET XACT_ABORT ON;
GO

IF COL_LENGTH(N'dbo.Transacciones', N'Memo') IS NOT NULL
   AND EXISTS
   (
       SELECT 1
       FROM sys.columns AS c
       INNER JOIN sys.types AS t
           ON t.user_type_id = c.user_type_id
       WHERE c.object_id = OBJECT_ID(N'dbo.Transacciones')
         AND c.name = N'Memo'
         AND (t.name <> N'nvarchar' OR c.max_length <> -1)
   )
BEGIN
    DECLARE @MemoCheckConstraintName sysname = N'SSMA_CC$Transacciones$Memo$disallow_zero_length';
    DECLARE @HadMemoCheckConstraint bit = 0;
    DECLARE @MemoCheckWasDisabled bit = 0;
    DECLARE @MemoCheckWasNotTrusted bit = 0;
    DECLARE @DropMemoCheckSql nvarchar(max);
    DECLARE @AddMemoCheckSql nvarchar(max);

    SELECT
        @HadMemoCheckConstraint = 1,
        @MemoCheckWasDisabled = cc.is_disabled,
        @MemoCheckWasNotTrusted = cc.is_not_trusted
    FROM sys.check_constraints AS cc
    WHERE cc.parent_object_id = OBJECT_ID(N'dbo.Transacciones')
      AND cc.name = @MemoCheckConstraintName;

    IF @HadMemoCheckConstraint = 1
    BEGIN
        SET @DropMemoCheckSql =
            N'ALTER TABLE dbo.Transacciones DROP CONSTRAINT '
            + QUOTENAME(@MemoCheckConstraintName)
            + N';';

        EXEC sys.sp_executesql @DropMemoCheckSql;
    END;

    DECLARE @AlterMemoSql nvarchar(max);

    SELECT @AlterMemoSql =
        N'ALTER TABLE dbo.Transacciones ALTER COLUMN Memo nvarchar(max) '
        + CASE WHEN c.is_nullable = 1 THEN N'NULL' ELSE N'NOT NULL' END
        + N';'
    FROM sys.columns AS c
    WHERE c.object_id = OBJECT_ID(N'dbo.Transacciones')
      AND c.name = N'Memo';

    EXEC sys.sp_executesql @AlterMemoSql;

    IF @HadMemoCheckConstraint = 1
    BEGIN
        SET @AddMemoCheckSql =
            N'ALTER TABLE dbo.Transacciones WITH '
            + CASE WHEN @MemoCheckWasNotTrusted = 1 THEN N'NOCHECK' ELSE N'CHECK' END
            + N' ADD CONSTRAINT '
            + QUOTENAME(@MemoCheckConstraintName)
            + N' CHECK ((len([Memo])>(0)));';

        EXEC sys.sp_executesql @AddMemoCheckSql;

        IF @MemoCheckWasDisabled = 1
        BEGIN
            SET @AddMemoCheckSql =
                N'ALTER TABLE dbo.Transacciones NOCHECK CONSTRAINT '
                + QUOTENAME(@MemoCheckConstraintName)
                + N';';

            EXEC sys.sp_executesql @AddMemoCheckSql;
        END;
    END;
END;
GO
