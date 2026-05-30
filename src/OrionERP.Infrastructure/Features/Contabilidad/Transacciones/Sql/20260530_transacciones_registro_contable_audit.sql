SET ANSI_NULLS ON;
GO
SET QUOTED_IDENTIFIER ON;
GO

IF SCHEMA_ID(N'contabilidad') IS NULL
BEGIN
    EXEC(N'CREATE SCHEMA contabilidad');
END;
GO

IF OBJECT_ID(N'contabilidad.TransaccionesRegistroContableAudit', N'U') IS NULL
BEGIN
    CREATE TABLE contabilidad.TransaccionesRegistroContableAudit
    (
        AuditId bigint IDENTITY(1,1) NOT NULL,
        ChangeSetId uniqueidentifier NOT NULL,
        ChangedAtUtc datetime2(3) NOT NULL CONSTRAINT DF_TransaccionesRegistroContableAudit_ChangedAtUtc DEFAULT SYSUTCDATETIME(),
        ChangedBy nvarchar(256) NOT NULL,
        Action char(1) NOT NULL,
        SourceTable sysname NOT NULL,
        TransaccionId int NULL,
        RegistroContableId int NULL,
        SessionId smallint NOT NULL,
        LoginName nvarchar(256) NOT NULL,
        HostName nvarchar(128) NULL,
        ApplicationName nvarchar(128) NULL,
        OldRowJson nvarchar(max) NULL,
        NewRowJson nvarchar(max) NULL,
        CONSTRAINT PK_TransaccionesRegistroContableAudit PRIMARY KEY CLUSTERED (AuditId),
        CONSTRAINT CK_TransaccionesRegistroContableAudit_Action CHECK (Action IN ('I', 'U', 'D'))
    );
END;
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'contabilidad.TransaccionesRegistroContableAudit')
      AND name = N'IX_TransaccionesRegistroContableAudit_ChangedAtUtc'
)
BEGIN
    CREATE INDEX IX_TransaccionesRegistroContableAudit_ChangedAtUtc
        ON contabilidad.TransaccionesRegistroContableAudit (ChangedAtUtc DESC)
        INCLUDE (ChangedBy, Action, SourceTable, TransaccionId, RegistroContableId);
END;
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'contabilidad.TransaccionesRegistroContableAudit')
      AND name = N'IX_TransaccionesRegistroContableAudit_TransaccionId'
)
BEGIN
    CREATE INDEX IX_TransaccionesRegistroContableAudit_TransaccionId
        ON contabilidad.TransaccionesRegistroContableAudit (TransaccionId, ChangedAtUtc DESC)
        INCLUDE (ChangedBy, Action, SourceTable, RegistroContableId);
END;
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'contabilidad.TransaccionesRegistroContableAudit')
      AND name = N'IX_TransaccionesRegistroContableAudit_ChangedBy'
)
BEGIN
    CREATE INDEX IX_TransaccionesRegistroContableAudit_ChangedBy
        ON contabilidad.TransaccionesRegistroContableAudit (ChangedBy, ChangedAtUtc DESC)
        INCLUDE (Action, SourceTable, TransaccionId, RegistroContableId);
END;
GO

CREATE OR ALTER TRIGGER dbo.trg_Transacciones_Audit
ON dbo.Transacciones
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @ChangedBy nvarchar(256) = CONVERT(nvarchar(256), SESSION_CONTEXT(N'OrionERP.UserName'));
    DECLARE @ApplicationName nvarchar(128) = CONVERT(nvarchar(128), SESSION_CONTEXT(N'OrionERP.Application'));
    DECLARE @ChangeSetId uniqueidentifier = NEWID();

    SET @ChangedBy = NULLIF(LTRIM(RTRIM(@ChangedBy)), N'');
    SET @ApplicationName = NULLIF(LTRIM(RTRIM(@ApplicationName)), N'');

    IF @ChangedBy IS NULL
    BEGIN
        SET @ChangedBy = COALESCE(NULLIF(CONVERT(nvarchar(256), ORIGINAL_LOGIN()), N''), NULLIF(CONVERT(nvarchar(256), SUSER_SNAME()), N''), N'SQL Server');
    END;

    INSERT INTO contabilidad.TransaccionesRegistroContableAudit
    (
        ChangeSetId,
        ChangedBy,
        Action,
        SourceTable,
        TransaccionId,
        RegistroContableId,
        SessionId,
        LoginName,
        HostName,
        ApplicationName,
        OldRowJson,
        NewRowJson
    )
    SELECT
        @ChangeSetId,
        @ChangedBy,
        CASE
            WHEN i.ID IS NOT NULL AND d.ID IS NOT NULL THEN 'U'
            WHEN i.ID IS NOT NULL THEN 'I'
            ELSE 'D'
        END,
        N'dbo.Transacciones',
        COALESCE(i.ID, d.ID),
        NULL,
        @@SPID,
        COALESCE(NULLIF(CONVERT(nvarchar(256), ORIGINAL_LOGIN()), N''), NULLIF(CONVERT(nvarchar(256), SUSER_SNAME()), N''), N'SQL Server'),
        HOST_NAME(),
        COALESCE(@ApplicationName, APP_NAME()),
        CASE WHEN d.ID IS NULL THEN NULL ELSE
            (
                SELECT snapshot.*
                FROM deleted AS snapshot
                WHERE snapshot.ID = d.ID
                FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES
            )
        END,
        CASE WHEN i.ID IS NULL THEN NULL ELSE
            (
                SELECT snapshot.*
                FROM inserted AS snapshot
                WHERE snapshot.ID = i.ID
                FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES
            )
        END
    FROM inserted AS i
    FULL OUTER JOIN deleted AS d
        ON d.ID = i.ID;
END;
GO

CREATE OR ALTER TRIGGER dbo.trg_Registro_Contable_Audit
ON dbo.Registro_Contable
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @ChangedBy nvarchar(256) = CONVERT(nvarchar(256), SESSION_CONTEXT(N'OrionERP.UserName'));
    DECLARE @ApplicationName nvarchar(128) = CONVERT(nvarchar(128), SESSION_CONTEXT(N'OrionERP.Application'));
    DECLARE @ChangeSetId uniqueidentifier = NEWID();

    SET @ChangedBy = NULLIF(LTRIM(RTRIM(@ChangedBy)), N'');
    SET @ApplicationName = NULLIF(LTRIM(RTRIM(@ApplicationName)), N'');

    IF @ChangedBy IS NULL
    BEGIN
        SET @ChangedBy = COALESCE(NULLIF(CONVERT(nvarchar(256), ORIGINAL_LOGIN()), N''), NULLIF(CONVERT(nvarchar(256), SUSER_SNAME()), N''), N'SQL Server');
    END;

    INSERT INTO contabilidad.TransaccionesRegistroContableAudit
    (
        ChangeSetId,
        ChangedBy,
        Action,
        SourceTable,
        TransaccionId,
        RegistroContableId,
        SessionId,
        LoginName,
        HostName,
        ApplicationName,
        OldRowJson,
        NewRowJson
    )
    SELECT
        @ChangeSetId,
        @ChangedBy,
        CASE
            WHEN i.ID IS NOT NULL AND d.ID IS NOT NULL THEN 'U'
            WHEN i.ID IS NOT NULL THEN 'I'
            ELSE 'D'
        END,
        N'dbo.Registro_Contable',
        COALESCE(i.TransaccionID, d.TransaccionID),
        COALESCE(i.ID, d.ID),
        @@SPID,
        COALESCE(NULLIF(CONVERT(nvarchar(256), ORIGINAL_LOGIN()), N''), NULLIF(CONVERT(nvarchar(256), SUSER_SNAME()), N''), N'SQL Server'),
        HOST_NAME(),
        COALESCE(@ApplicationName, APP_NAME()),
        CASE WHEN d.ID IS NULL THEN NULL ELSE
            (
                SELECT snapshot.*
                FROM deleted AS snapshot
                WHERE snapshot.ID = d.ID
                FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES
            )
        END,
        CASE WHEN i.ID IS NULL THEN NULL ELSE
            (
                SELECT snapshot.*
                FROM inserted AS snapshot
                WHERE snapshot.ID = i.ID
                FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES
            )
        END
    FROM inserted AS i
    FULL OUTER JOIN deleted AS d
        ON d.ID = i.ID;
END;
GO

CREATE OR ALTER VIEW contabilidad.TransaccionesRegistroContableRecentChanges
AS
    SELECT
        AuditId,
        ChangeSetId,
        ChangedAtUtc,
        ChangedBy,
        Action,
        SourceTable,
        TransaccionId,
        RegistroContableId,
        SessionId,
        LoginName,
        HostName,
        ApplicationName,
        OldRowJson,
        NewRowJson
    FROM contabilidad.TransaccionesRegistroContableAudit;
GO

/*
Recent modified or deleted accounting records:

SELECT TOP (200)
    ChangedAtUtc,
    ChangedBy,
    Action,
    SourceTable,
    TransaccionId,
    RegistroContableId,
    OldRowJson,
    NewRowJson
FROM contabilidad.TransaccionesRegistroContableRecentChanges
WHERE Action IN ('U', 'D')
ORDER BY ChangedAtUtc DESC, AuditId DESC;
*/
