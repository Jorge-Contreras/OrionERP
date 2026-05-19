SET XACT_ABORT ON;
GO

BEGIN TRANSACTION;

IF EXISTS (
    SELECT 1
    FROM auth.AspNetUsers au
    LEFT JOIN dbo.Capital_Humano ch ON ch.ID = au.EmployeeId
    WHERE au.EmployeeId IS NOT NULL
      AND ch.ID IS NULL
)
BEGIN
    SELECT
        au.Id,
        au.UserName,
        au.Email,
        au.EmployeeId
    FROM auth.AspNetUsers au
    LEFT JOIN dbo.Capital_Humano ch ON ch.ID = au.EmployeeId
    WHERE au.EmployeeId IS NOT NULL
      AND ch.ID IS NULL
    ORDER BY au.UserName, au.Email;

    THROW 51000, 'Cannot repair auth.AspNetUsers.EmployeeId foreign key because at least one user points to an EmployeeId that does not exist in dbo.Capital_Humano.', 1;
END;

DECLARE @fkName sysname;
DECLARE @dropSql nvarchar(max);

DECLARE wrong_employee_fk CURSOR LOCAL FAST_FORWARD FOR
    SELECT fk.name
    FROM sys.foreign_keys fk
    JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
    JOIN sys.tables parent_table ON parent_table.object_id = fkc.parent_object_id
    JOIN sys.schemas parent_schema ON parent_schema.schema_id = parent_table.schema_id
    JOIN sys.columns parent_column
        ON parent_column.object_id = parent_table.object_id
        AND parent_column.column_id = fkc.parent_column_id
    JOIN sys.tables principal_table ON principal_table.object_id = fkc.referenced_object_id
    JOIN sys.schemas principal_schema ON principal_schema.schema_id = principal_table.schema_id
    JOIN sys.columns principal_column
        ON principal_column.object_id = principal_table.object_id
        AND principal_column.column_id = fkc.referenced_column_id
    WHERE parent_schema.name = 'auth'
      AND parent_table.name = 'AspNetUsers'
      AND parent_column.name = 'EmployeeId'
      AND NOT (
          principal_schema.name = 'dbo'
          AND principal_table.name = 'Capital_Humano'
          AND principal_column.name = 'ID'
      );

OPEN wrong_employee_fk;
FETCH NEXT FROM wrong_employee_fk INTO @fkName;

WHILE @@FETCH_STATUS = 0
BEGIN
    SET @dropSql = N'ALTER TABLE auth.AspNetUsers DROP CONSTRAINT ' + QUOTENAME(@fkName) + N';';
    EXEC sys.sp_executesql @dropSql;

    FETCH NEXT FROM wrong_employee_fk INTO @fkName;
END;

CLOSE wrong_employee_fk;
DEALLOCATE wrong_employee_fk;

IF NOT EXISTS (
    SELECT 1
    FROM sys.foreign_keys fk
    JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
    JOIN sys.tables parent_table ON parent_table.object_id = fkc.parent_object_id
    JOIN sys.schemas parent_schema ON parent_schema.schema_id = parent_table.schema_id
    JOIN sys.columns parent_column
        ON parent_column.object_id = parent_table.object_id
        AND parent_column.column_id = fkc.parent_column_id
    JOIN sys.tables principal_table ON principal_table.object_id = fkc.referenced_object_id
    JOIN sys.schemas principal_schema ON principal_schema.schema_id = principal_table.schema_id
    JOIN sys.columns principal_column
        ON principal_column.object_id = principal_table.object_id
        AND principal_column.column_id = fkc.referenced_column_id
    WHERE parent_schema.name = 'auth'
      AND parent_table.name = 'AspNetUsers'
      AND parent_column.name = 'EmployeeId'
      AND principal_schema.name = 'dbo'
      AND principal_table.name = 'Capital_Humano'
      AND principal_column.name = 'ID'
)
BEGIN
    ALTER TABLE auth.AspNetUsers WITH CHECK
    ADD CONSTRAINT FK_AspNetUsers_Capital_Humano_EmployeeId
        FOREIGN KEY (EmployeeId)
        REFERENCES dbo.Capital_Humano (ID);

    ALTER TABLE auth.AspNetUsers
    CHECK CONSTRAINT FK_AspNetUsers_Capital_Humano_EmployeeId;
END;

COMMIT TRANSACTION;
