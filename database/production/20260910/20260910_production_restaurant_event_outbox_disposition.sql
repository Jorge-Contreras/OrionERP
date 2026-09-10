/*
  Cierre auditado de los 2,000 eventos historicos de Bruno anteriores al
  broadcaster multiempresa.

  Decision empresarial autorizada el 2026-09-10:
    - Los eventos 125..2124 no se publican retrospectivamente.
    - Cada evento queda conservado en EventOutbox y respaldado por una fila
      inmutable de auditoria con el hash de su payload original.
    - Los eventos posteriores al Id 2124 quedan fuera de este paquete.
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;

DECLARE @ExpectedDatabase sysname=N'$(ExpectedDatabase)';
DECLARE @ApplyChangesInput nvarchar(20)=N'$(ApplyChanges)';
DECLARE @ApplyChanges bit=0;
DECLARE @MigrationId nvarchar(200)=N'$(MigrationId)';
DECLARE @MigrationChecksum varchar(128)='$(MigrationChecksum)';
DECLARE @AppVersionInput nvarchar(64)=N'$(AppVersion)';
DECLARE @AppVersion nvarchar(64)=NULL;

IF @ApplyChangesInput NOT LIKE N'$'+N'(%'
BEGIN
  IF @ApplyChangesInput NOT IN (N'0',N'1') THROW 52400,'ApplyChanges debe ser 0 o 1.',1;
  SET @ApplyChanges=CONVERT(bit,@ApplyChangesInput);
END;
IF @ExpectedDatabase LIKE N'$'+N'(%'
   OR @ExpectedDatabase NOT IN(N'grupocarpio',N'Orion_CutoverValidation_20260908')
  THROW 52401,'Esta migracion admite solo produccion o su base de ensayo autorizada.',1;
IF DB_NAME()<>@ExpectedDatabase
  THROW 52402,'La conexion no apunta a la base declarada.',1;
IF @MigrationId<>N'20260910_production_restaurant_event_outbox_disposition'
  THROW 52403,'MigrationId no coincide con este paquete productivo.',1;
IF @MigrationChecksum LIKE '$'+'(%' OR LEN(@MigrationChecksum)<>64
   OR @MigrationChecksum LIKE '%[^0-9A-F]%' COLLATE Latin1_General_100_BIN2
  THROW 52404,'MigrationChecksum debe ser un SHA-256 hexadecimal de 64 caracteres.',1;
IF @AppVersionInput NOT LIKE N'$'+N'(%' SET @AppVersion=NULLIF(LTRIM(RTRIM(@AppVersionInput)),N'');

IF OBJECT_ID(N'orion.SchemaMigration',N'U') IS NULL
   OR OBJECT_ID(N'restaurante.EventOutbox',N'U') IS NULL
  THROW 52405,'Falta el ledger o la bandeja de eventos de Restaurante.',1;

DECLARE @ExistingChecksum char(64)=(SELECT Checksum FROM orion.SchemaMigration WHERE MigrationId=@MigrationId);
IF @ExistingChecksum IS NOT NULL AND UPPER(@ExistingChecksum)<>UPPER(@MigrationChecksum)
  THROW 52406,'El mismo MigrationId ya existe con otro checksum.',1;
IF @ExistingChecksum IS NOT NULL
BEGIN
  SELECT N'YA_APLICADO_PRODUCCION' Estado,@MigrationId MigrationId;
  RETURN;
END;

DECLARE @Rfc varchar(50)='BRUNOS260707L26';
DECLARE @FirstEventId bigint=125;
DECLARE @LastEventId bigint=2124;
DECLARE @ExpectedRows int=2000;
DECLARE @ReviewedSetChecksum char(64)='04EBA7B4621BDA85F36439B882ABCD7984379CFC52977274CACD6F4587B58931';

BEGIN TRY
  BEGIN TRANSACTION;

  DECLARE @LockResult int;
  EXEC @LockResult=sys.sp_getapplock
    @Resource=N'OrionERP:Restaurant:EventOutboxDisposition:BRUNOS:125-2124',
    @LockMode=N'Exclusive',@LockOwner=N'Transaction',@LockTimeout=15000;
  IF @LockResult<0 THROW 52407,'No se obtuvo el candado exclusivo del cierre historico.',1;

  IF OBJECT_ID(N'restaurante.EventOutboxDispositionAudit',N'U') IS NOT NULL
     OR OBJECT_ID(N'restaurante.TR_EventOutboxDispositionAudit_Immutable',N'TR') IS NOT NULL
    THROW 52408,'Hay objetos parciales sin una fila de ledger; se requiere revision manual.',1;

  SELECT Id,Rfc,SiteId,EventType,AggregateId,Payload,OccurredAt,Attempts
  INTO #ReviewedEvents
  FROM restaurante.EventOutbox WITH (UPDLOCK,HOLDLOCK)
  WHERE Rfc=@Rfc
    AND Id BETWEEN @FirstEventId AND @LastEventId
    AND PublishedAt IS NULL
    AND Attempts=0;

  IF (SELECT COUNT(*) FROM #ReviewedEvents)<>@ExpectedRows
    THROW 52409,'El conjunto ya no contiene exactamente los 2,000 eventos autorizados.',1;
  IF (SELECT MIN(Id) FROM #ReviewedEvents)<>@FirstEventId
     OR (SELECT MAX(Id) FROM #ReviewedEvents)<>@LastEventId
     OR EXISTS
       (SELECT 1 FROM #ReviewedEvents GROUP BY Id HAVING COUNT(*)<>1)
    THROW 52410,'El intervalo de eventos autorizado cambio.',1;

  DECLARE @ActualReviewedSetChecksum char(64);
  SELECT @ActualReviewedSetChecksum=CONVERT(char(64),HASHBYTES('SHA2_256',CONVERT(nvarchar(max),
    STRING_AGG(CONVERT(nvarchar(max),CONCAT(
      Id,N'|',Rfc,N'|',SiteId,N'|',EventType,N'|',AggregateId,N'|',
      CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),Payload)),2),N'|',
      CONVERT(nvarchar(33),OccurredAt,126),N'|',Attempts)),NCHAR(10))
      WITHIN GROUP (ORDER BY Id))),2)
  FROM #ReviewedEvents;
  IF @ActualReviewedSetChecksum<>@ReviewedSetChecksum
    THROW 52411,'El fingerprint de los 2,000 eventos no coincide con el conjunto autorizado.',1;

  SELECT N'PREVIEW_VALIDADO' Estado,@ExpectedRows Eventos,@FirstEventId PrimerId,
    @LastEventId UltimoId,@ReviewedSetChecksum ReviewedSetChecksum;

  IF @ApplyChanges=1
  BEGIN
    CREATE TABLE restaurante.EventOutboxDispositionAudit
    (
      EventOutboxId bigint NOT NULL CONSTRAINT PK_EventOutboxDispositionAudit PRIMARY KEY,
      Rfc varchar(50) NOT NULL,
      SiteId int NOT NULL,
      EventType varchar(80) NOT NULL,
      AggregateId varchar(80) NOT NULL,
      PayloadHash char(64) NOT NULL,
      OccurredAt datetime2(3) NOT NULL,
      OriginalAttempts int NOT NULL,
      DispositionCode varchar(80) NOT NULL,
      Reason nvarchar(1000) NOT NULL,
      AuthorizedAtUtc datetime2(3) NOT NULL,
      AuthorizedBy nvarchar(256) NOT NULL,
      DisposedAtUtc datetime2(3) NOT NULL,
      DisposedBy nvarchar(256) NOT NULL,
      MigrationId nvarchar(200) NOT NULL,
      MigrationChecksum char(64) NOT NULL,
      ReviewedSetChecksum char(64) NOT NULL,
      CONSTRAINT FK_EventOutboxDispositionAudit_Event
        FOREIGN KEY (EventOutboxId) REFERENCES restaurante.EventOutbox(Id),
      CONSTRAINT CK_EventOutboxDispositionAudit_Hashes
        CHECK (LEN(PayloadHash)=64 AND LEN(MigrationChecksum)=64 AND LEN(ReviewedSetChecksum)=64),
      CONSTRAINT CK_EventOutboxDispositionAudit_Reason
        CHECK (NULLIF(LTRIM(RTRIM(Reason)),N'') IS NOT NULL)
    );

    EXEC(N'CREATE TRIGGER restaurante.TR_EventOutboxDispositionAudit_Immutable
      ON restaurante.EventOutboxDispositionAudit AFTER UPDATE,DELETE AS
    BEGIN
      SET NOCOUNT ON;
      THROW 52412,''La auditoria de disposicion de eventos es inmutable.'',1;
    END;');

    DECLARE @DisposedAtUtc datetime2(3)=SYSUTCDATETIME();
    DECLARE @Actor nvarchar(256)=COALESCE(
      CONVERT(nvarchar(256),SESSION_CONTEXT(N'OrionERP.UserName')),
      CONVERT(nvarchar(256),ORIGINAL_LOGIN()));

    INSERT restaurante.EventOutboxDispositionAudit
    (
      EventOutboxId,Rfc,SiteId,EventType,AggregateId,PayloadHash,OccurredAt,
      OriginalAttempts,DispositionCode,Reason,AuthorizedAtUtc,AuthorizedBy,
      DisposedAtUtc,DisposedBy,MigrationId,MigrationChecksum,ReviewedSetChecksum
    )
    SELECT
      Id,Rfc,SiteId,EventType,AggregateId,
      CONVERT(char(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),Payload)),2),
      OccurredAt,Attempts,'SUPERSEDED_BEFORE_SCOPED_BROADCASTER',
      N'Historial anterior al broadcaster multiempresa; se conserva localmente y no se publica retrospectivamente.',
      CONVERT(datetime2(3),'2026-09-10T00:00:00.000'),
      N'Decision empresarial proporcionada por el usuario el 2026-09-10',
      @DisposedAtUtc,@Actor,@MigrationId,@MigrationChecksum,@ReviewedSetChecksum
    FROM #ReviewedEvents;
    IF @@ROWCOUNT<>@ExpectedRows
      THROW 52413,'No se generaron exactamente las 2,000 filas de auditoria.',1;

    UPDATE sourceEvent
      SET PublishedAt=@DisposedAtUtc
    FROM restaurante.EventOutbox sourceEvent
    JOIN #ReviewedEvents reviewed ON reviewed.Id=sourceEvent.Id
    WHERE sourceEvent.PublishedAt IS NULL AND sourceEvent.Attempts=0;
    IF @@ROWCOUNT<>@ExpectedRows
      THROW 52414,'No se cerraron exactamente los 2,000 eventos autorizados.',1;

    IF EXISTS
    (
      SELECT 1
      FROM restaurante.EventOutboxDispositionAudit auditRow
      JOIN restaurante.EventOutbox sourceEvent ON sourceEvent.Id=auditRow.EventOutboxId
      WHERE sourceEvent.PublishedAt IS NULL
         OR sourceEvent.Attempts<>auditRow.OriginalAttempts
         OR auditRow.ReviewedSetChecksum<>@ReviewedSetChecksum
         OR auditRow.PayloadHash<>CONVERT(char(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),sourceEvent.Payload)),2)
    )
      THROW 52415,'La comprobacion posterior al cierre historico fallo.',1;

    INSERT orion.SchemaMigration (MigrationId,Checksum,AppliedBy,AppVersion,DatabaseName)
    VALUES (@MigrationId,@MigrationChecksum,@Actor,@AppVersion,DB_NAME());

    COMMIT TRANSACTION;
    SELECT N'APLICADO' Estado,DB_NAME() BaseDatos,@ExpectedRows EventosCerrados,
      @LastEventId UltimoIdHistorico,@MigrationId MigrationId;
  END
  ELSE
  BEGIN
    ROLLBACK TRANSACTION;
    SELECT N'VALIDADO_SIN_CAMBIOS' Estado,DB_NAME() BaseDatos,@ExpectedRows EventosRevisados,
      @LastEventId UltimoIdHistorico,@MigrationId MigrationId;
  END;
END TRY
BEGIN CATCH
  IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
  THROW;
END CATCH;
