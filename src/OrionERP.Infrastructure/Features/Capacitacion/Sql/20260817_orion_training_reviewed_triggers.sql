/*
  Reinstalls only the three reviewed operational triggers required by Training.
  The dynamic DDL keeps every mutation in the same fail-closed guard batch.
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF DB_NAME() COLLATE Latin1_General_100_BIN2 <> N'Orion_Training' COLLATE Latin1_General_100_BIN2
  THROW 51840, 'TRIGGER INSTALL BLOCKED: the active database is not exactly Orion_Training.', 1;
IF ISNULL(TRY_CONVERT(nvarchar(64), SESSION_CONTEXT(N'OrionTrainingSanitizerApply')), N'') <> N'20260817-v1'
  THROW 51841, 'TRIGGER INSTALL BLOCKED: the guarded sanitizer session did not authorize this batch.', 1;
IF OBJECT_ID(N'dbo.Transacciones', N'U') IS NULL
   OR OBJECT_ID(N'dbo.Registro_Contable', N'U') IS NULL
   OR OBJECT_ID(N'dbo.Transaccion_Comprobante', N'U') IS NULL
   OR OBJECT_ID(N'cfdi.Comprobante', N'U') IS NULL
   OR OBJECT_ID(N'contabilidad.TransaccionesRegistroContableAudit', N'U') IS NULL
   OR OBJECT_ID(N'capacitacion.EntornoSeguridad', N'U') IS NULL
   OR OBJECT_ID(N'capacitacion.EsquemaVersion', N'U') IS NULL
  THROW 51842, 'TRIGGER INSTALL BLOCKED: a reviewed parent or audit table is missing.', 1;

EXEC sys.sp_executesql N'CREATE OR ALTER TRIGGER dbo.trg_Transacciones_Audit
ON dbo.Transacciones
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @ChangedBy nvarchar(256) = CONVERT(nvarchar(256), SESSION_CONTEXT(N''OrionERP.UserName''));
    DECLARE @ApplicationName nvarchar(128) = CONVERT(nvarchar(128), SESSION_CONTEXT(N''OrionERP.Application''));
    DECLARE @ChangeSetId uniqueidentifier = NEWID();
    SET @ChangedBy = NULLIF(LTRIM(RTRIM(@ChangedBy)), N'''');
    SET @ApplicationName = NULLIF(LTRIM(RTRIM(@ApplicationName)), N'''');
    IF @ChangedBy IS NULL
      SET @ChangedBy = COALESCE(NULLIF(CONVERT(nvarchar(256), ORIGINAL_LOGIN()), N''''), NULLIF(CONVERT(nvarchar(256), SUSER_SNAME()), N''''), N''SQL Server'');

    INSERT INTO contabilidad.TransaccionesRegistroContableAudit
      (ChangeSetId, ChangedBy, Action, SourceTable, TransaccionId,
       RegistroContableId, SessionId, LoginName, HostName, ApplicationName,
       OldRowJson, NewRowJson)
    SELECT
      @ChangeSetId,
      @ChangedBy,
      CASE WHEN i.ID IS NOT NULL AND d.ID IS NOT NULL THEN ''U''
           WHEN i.ID IS NOT NULL THEN ''I'' ELSE ''D'' END,
      N''dbo.Transacciones'',
      COALESCE(i.ID, d.ID),
      NULL,
      @@SPID,
      COALESCE(NULLIF(CONVERT(nvarchar(256), ORIGINAL_LOGIN()), N''''), NULLIF(CONVERT(nvarchar(256), SUSER_SNAME()), N''''), N''SQL Server''),
      HOST_NAME(),
      COALESCE(@ApplicationName, APP_NAME()),
      CASE WHEN d.ID IS NULL THEN NULL ELSE
        (SELECT snapshot.* FROM deleted AS snapshot WHERE snapshot.ID = d.ID
         FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END,
      CASE WHEN i.ID IS NULL THEN NULL ELSE
        (SELECT snapshot.* FROM inserted AS snapshot WHERE snapshot.ID = i.ID
         FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i
    FULL OUTER JOIN deleted AS d ON d.ID = i.ID;
END;';

EXEC sys.sp_executesql N'CREATE OR ALTER TRIGGER dbo.trg_Registro_Contable_Audit
ON dbo.Registro_Contable
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @ChangedBy nvarchar(256) = CONVERT(nvarchar(256), SESSION_CONTEXT(N''OrionERP.UserName''));
    DECLARE @ApplicationName nvarchar(128) = CONVERT(nvarchar(128), SESSION_CONTEXT(N''OrionERP.Application''));
    DECLARE @ChangeSetId uniqueidentifier = NEWID();
    SET @ChangedBy = NULLIF(LTRIM(RTRIM(@ChangedBy)), N'''');
    SET @ApplicationName = NULLIF(LTRIM(RTRIM(@ApplicationName)), N'''');
    IF @ChangedBy IS NULL
      SET @ChangedBy = COALESCE(NULLIF(CONVERT(nvarchar(256), ORIGINAL_LOGIN()), N''''), NULLIF(CONVERT(nvarchar(256), SUSER_SNAME()), N''''), N''SQL Server'');

    INSERT INTO contabilidad.TransaccionesRegistroContableAudit
      (ChangeSetId, ChangedBy, Action, SourceTable, TransaccionId,
       RegistroContableId, SessionId, LoginName, HostName, ApplicationName,
       OldRowJson, NewRowJson)
    SELECT
      @ChangeSetId,
      @ChangedBy,
      CASE WHEN i.ID IS NOT NULL AND d.ID IS NOT NULL THEN ''U''
           WHEN i.ID IS NOT NULL THEN ''I'' ELSE ''D'' END,
      N''dbo.Registro_Contable'',
      COALESCE(i.TransaccionID, d.TransaccionID),
      COALESCE(i.ID, d.ID),
      @@SPID,
      COALESCE(NULLIF(CONVERT(nvarchar(256), ORIGINAL_LOGIN()), N''''), NULLIF(CONVERT(nvarchar(256), SUSER_SNAME()), N''''), N''SQL Server''),
      HOST_NAME(),
      COALESCE(@ApplicationName, APP_NAME()),
      CASE WHEN d.ID IS NULL THEN NULL ELSE
        (SELECT snapshot.* FROM deleted AS snapshot WHERE snapshot.ID = d.ID
         FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END,
      CASE WHEN i.ID IS NULL THEN NULL ELSE
        (SELECT snapshot.* FROM inserted AS snapshot WHERE snapshot.ID = i.ID
         FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) END
    FROM inserted AS i
    FULL OUTER JOIN deleted AS d ON d.ID = i.ID;
END;';

EXEC sys.sp_executesql N'CREATE OR ALTER TRIGGER dbo.TR_Transaccion_Comprobante_BlockPago20Direct
ON dbo.Transaccion_Comprobante
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS
    (
      SELECT 1
      FROM inserted AS newLink
      JOIN cfdi.Comprobante AS c ON c.Comprobante_Id = newLink.Comprobante_ID
      WHERE c.TipoDeComprobante = ''P''
    )
      THROW 51020, ''Los CFDI tipo P deben ligarse mediante Transaccion_DoctoRelacionado.'', 1;
END;';

EXEC sys.sp_executesql N'CREATE OR ALTER TRIGGER capacitacion.TR_EntornoSeguridad_MaintenanceOnly
ON capacitacion.EntornoSeguridad
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF ORIGINAL_LOGIN() = N''orion_training_runtime''
       OR ISNULL(IS_SRVROLEMEMBER(N''sysadmin''), 0) <> 1
       OR ISNULL(TRY_CONVERT(nvarchar(64), SESSION_CONTEXT(N''OrionTrainingSanitizerApply'')), N'''') <> N''20260817-v1''
      THROW 51847, ''EntornoSeguridad sólo puede cambiar en el flujo sysadmin guardado de Training.'', 1;
END;';

EXEC sys.sp_executesql N'CREATE OR ALTER TRIGGER capacitacion.TR_EsquemaVersion_MaintenanceOnly
ON capacitacion.EsquemaVersion
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF ORIGINAL_LOGIN() = N''orion_training_runtime''
       OR ISNULL(IS_SRVROLEMEMBER(N''sysadmin''), 0) <> 1
       OR ISNULL(TRY_CONVERT(nvarchar(64), SESSION_CONTEXT(N''OrionTrainingSanitizerApply'')), N'''') <> N''20260817-v1''
      THROW 51848, ''EsquemaVersion sólo puede cambiar en el flujo sysadmin guardado de Training.'', 1;
END;';
