/*
  E7: mecanismos verificables para retirar los cuatro remanentes legacy de Hospedaje.

  - Arrendadores se asocian por empresa/sede sin modificar dbo.Proveedores.
  - Las actividades automáticas requieren un mapping explícito y estructural.
  - Los pagos contradictorios sólo se corrigen desde un manifiesto aprobado, con
    preview enlazado por checksum y lotes de hasta 25.
  - Dos mappings Outlook se reparan con evidencia exacta y dos se aíslan localmente.
    Esta migración no llama Microsoft Graph ni crea, modifica o elimina eventos remotos.

  Exclusiva de Orion_Sandbox. No se toca 20260907_fiscal_declaracion_deploy.ps1.
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_PADDING ON;
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
  IF @ApplyChangesInput NOT IN (N'0',N'1') THROW 52160,'ApplyChanges debe ser 0 o 1.',1;
  SET @ApplyChanges=CONVERT(bit,@ApplyChangesInput);
END;
IF @ExpectedDatabase LIKE N'$'+N'(%' OR @ExpectedDatabase<>N'Orion_Sandbox'
  THROW 52161,'Esta migracion admite exclusivamente Orion_Sandbox.',1;
IF DB_NAME()<>N'Orion_Sandbox'
  THROW 52162,'La conexion no apunta a Orion_Sandbox.',1;
IF @MigrationId LIKE N'$'+N'(%' OR NULLIF(LTRIM(RTRIM(@MigrationId)),N'') IS NULL OR LEN(@MigrationId)>200
  THROW 52163,'MigrationId es obligatorio y debe provenir del manifiesto.',1;
IF @MigrationChecksum LIKE '$'+'(%' OR LEN(@MigrationChecksum)<>64
   OR @MigrationChecksum LIKE '%[^0-9A-F]%' COLLATE Latin1_General_100_BIN2
  THROW 52164,'MigrationChecksum debe ser un SHA-256 hexadecimal de 64 caracteres.',1;
IF @AppVersionInput NOT LIKE N'$'+N'(%' SET @AppVersion=NULLIF(LTRIM(RTRIM(@AppVersionInput)),N'');

IF OBJECT_ID(N'orion.SchemaMigration',N'U') IS NULL
   OR OBJECT_ID(N'orion.HospitalityScopePolicy',N'SP') IS NULL
   OR OBJECT_ID(N'orion.fn_HospitalityScopePredicate',N'IF') IS NULL
   OR OBJECT_ID(N'orion.fn_HospitalityPaymentScopePredicate',N'IF') IS NULL
  THROW 52165,'Falta el aislamiento administrativo de Hospedaje.',1;
IF NOT EXISTS (SELECT 1 FROM orion.SchemaMigration WHERE MigrationId IN
  (N'20260908_hospitality_administration_scope_sandbox',N'20260908_production_hospitality_administration_scope'))
  THROW 52166,'Falta la migracion base de aislamiento de Hospedaje.',1;
IF NOT EXISTS (SELECT 1 FROM sys.security_policies WHERE object_id=OBJECT_ID(N'orion.HospitalityScopePolicy') AND is_enabled=1 AND is_schema_bound=1)
  THROW 52167,'HospitalityScopePolicy no esta activa y enlazada al esquema.',1;

DECLARE @ExistingChecksum char(64)=(SELECT Checksum FROM orion.SchemaMigration WHERE MigrationId=@MigrationId);
IF @ExistingChecksum IS NOT NULL AND UPPER(@ExistingChecksum)<>UPPER(@MigrationChecksum)
  THROW 52168,'El mismo MigrationId ya existe con otro checksum.',1;
IF @ExistingChecksum IS NOT NULL
BEGIN
  SELECT N'YA_APLICADO_SOLO_SANDBOX' Estado,@MigrationId MigrationId;
  RETURN;
END;

BEGIN TRY
  BEGIN TRANSACTION;
  DECLARE @LockResult int;
  EXEC @LockResult=sys.sp_getapplock @Resource=N'OrionERP:Hospitality:LegacyMechanisms:Sandbox',
    @LockMode=N'Exclusive',@LockOwner=N'Transaction',@LockTimeout=15000;
  IF @LockResult<0 THROW 52169,'No se obtuvo el candado de la migracion E7.',1;

  IF OBJECT_ID(N'orion.HospitalitySiteOwner',N'U') IS NOT NULL
     OR OBJECT_ID(N'orion.HospitalityActivityTemplateMapping',N'U') IS NOT NULL
     OR OBJECT_ID(N'orion.HospitalityGeneratedActivity',N'U') IS NOT NULL
     OR OBJECT_ID(N'orion.HospitalityPaymentCorrectionManifest',N'U') IS NOT NULL
     OR OBJECT_ID(N'orion.HospitalityPaymentCorrectionAudit',N'U') IS NOT NULL
     OR OBJECT_ID(N'orion.HospitalityOutlookMappingQuarantine',N'U') IS NOT NULL
     OR OBJECT_ID(N'orion.HospitalityOutlookMappingRepairAudit',N'U') IS NOT NULL
    THROW 52170,'Hay objetos parciales de E7 sin una fila de ledger; se requiere revision manual.',1;

  CREATE TABLE orion.HospitalitySiteOwner
  (
    CompanyId bigint NOT NULL CONSTRAINT DF_HospitalitySiteOwner_Company DEFAULT (TRY_CONVERT(bigint,SESSION_CONTEXT(N'OrionERP.HospitalityCompanyId'))),
    SiteId bigint NOT NULL CONSTRAINT DF_HospitalitySiteOwner_Site DEFAULT (TRY_CONVERT(bigint,SESSION_CONTEXT(N'OrionERP.HospitalitySiteId'))),
    ProveedorId int NOT NULL,
    IsActive bit NOT NULL CONSTRAINT DF_HospitalitySiteOwner_Active DEFAULT (1),
    CreatedAtUtc datetime2(3) NOT NULL CONSTRAINT DF_HospitalitySiteOwner_Created DEFAULT (SYSUTCDATETIME()),
    CreatedBy nvarchar(256) NOT NULL,
    UpdatedAtUtc datetime2(3) NOT NULL CONSTRAINT DF_HospitalitySiteOwner_Updated DEFAULT (SYSUTCDATETIME()),
    UpdatedBy nvarchar(256) NOT NULL,
    CONSTRAINT PK_HospitalitySiteOwner PRIMARY KEY (CompanyId,SiteId,ProveedorId),
    CONSTRAINT FK_HospitalitySiteOwner_Site FOREIGN KEY (CompanyId,SiteId) REFERENCES orion.Site(CompanyId,SiteId),
    CONSTRAINT FK_HospitalitySiteOwner_Provider FOREIGN KEY (ProveedorId) REFERENCES dbo.Proveedores(id)
  );

  CREATE TABLE orion.HospitalityActivityTemplateMapping
  (
    CompanyId bigint NOT NULL CONSTRAINT DF_HospitalityActivityMapping_Company DEFAULT (TRY_CONVERT(bigint,SESSION_CONTEXT(N'OrionERP.HospitalityCompanyId'))),
    SiteId bigint NOT NULL CONSTRAINT DF_HospitalityActivityMapping_Site DEFAULT (TRY_CONVERT(bigint,SESSION_CONTEXT(N'OrionERP.HospitalitySiteId'))),
    RoomId int NOT NULL,
    ActivityType varchar(200) NOT NULL,
    TemplateActivityId int NOT NULL,
    AssigneeEmployeeId int NOT NULL,
    CuentaSatId int NOT NULL,
    IsEnabled bit NOT NULL CONSTRAINT DF_HospitalityActivityMapping_Enabled DEFAULT (0),
    CreatedAtUtc datetime2(3) NOT NULL CONSTRAINT DF_HospitalityActivityMapping_Created DEFAULT (SYSUTCDATETIME()),
    CreatedBy nvarchar(256) NOT NULL,
    UpdatedAtUtc datetime2(3) NOT NULL CONSTRAINT DF_HospitalityActivityMapping_Updated DEFAULT (SYSUTCDATETIME()),
    UpdatedBy nvarchar(256) NOT NULL,
    CONSTRAINT PK_HospitalityActivityTemplateMapping PRIMARY KEY (CompanyId,SiteId,RoomId,ActivityType),
    CONSTRAINT CK_HospitalityActivityMapping_Type CHECK (NULLIF(LTRIM(RTRIM(ActivityType)),'') IS NOT NULL),
    CONSTRAINT CK_HospitalityActivityMapping_Ids CHECK (TemplateActivityId>0 AND AssigneeEmployeeId>0 AND CuentaSatId>0),
    CONSTRAINT FK_HospitalityActivityMapping_Room FOREIGN KEY (CompanyId,SiteId,RoomId) REFERENCES dbo.ROOM(OrionCompanyId,OrionSiteId,ID),
    CONSTRAINT FK_HospitalityActivityMapping_Template FOREIGN KEY (TemplateActivityId) REFERENCES dbo.Actividad(ID),
    CONSTRAINT FK_HospitalityActivityMapping_Assignee FOREIGN KEY (AssigneeEmployeeId) REFERENCES dbo.Capital_Humano(ID)
  );

  /* ROOM_CALENDAR already has a global identity PK; this candidate key lets
     scoped children prove the same company/site without changing that PK. */
  CREATE UNIQUE INDEX UX_ROOM_CALENDAR_OrionScope_Id
    ON dbo.ROOM_CALENDAR(OrionCompanyId,OrionSiteId,ID);

  CREATE TABLE orion.HospitalityGeneratedActivity
  (
    CompanyId bigint NOT NULL CONSTRAINT DF_HospitalityGeneratedActivity_Company DEFAULT (TRY_CONVERT(bigint,SESSION_CONTEXT(N'OrionERP.HospitalityCompanyId'))),
    SiteId bigint NOT NULL CONSTRAINT DF_HospitalityGeneratedActivity_Site DEFAULT (TRY_CONVERT(bigint,SESSION_CONTEXT(N'OrionERP.HospitalitySiteId'))),
    RoomCalendarId int NOT NULL,
    RoomId int NOT NULL,
    ActivityType varchar(200) NOT NULL,
    ReservationId int NOT NULL,
    ActivityId int NOT NULL,
    TemplateActivityId int NOT NULL,
    CreatedAtUtc datetime2(3) NOT NULL CONSTRAINT DF_HospitalityGeneratedActivity_Created DEFAULT (SYSUTCDATETIME()),
    CreatedBy nvarchar(256) NOT NULL,
    CONSTRAINT PK_HospitalityGeneratedActivity PRIMARY KEY (CompanyId,SiteId,RoomCalendarId,ActivityType),
    CONSTRAINT UQ_HospitalityGeneratedActivity_Activity UNIQUE (ActivityId),
    CONSTRAINT FK_HospitalityGeneratedActivity_Mapping FOREIGN KEY (CompanyId,SiteId,RoomId,ActivityType)
      REFERENCES orion.HospitalityActivityTemplateMapping(CompanyId,SiteId,RoomId,ActivityType),
    CONSTRAINT FK_HospitalityGeneratedActivity_Calendar FOREIGN KEY (CompanyId,SiteId,RoomCalendarId)
      REFERENCES dbo.ROOM_CALENDAR(OrionCompanyId,OrionSiteId,ID),
    CONSTRAINT FK_HospitalityGeneratedActivity_Reservation FOREIGN KEY (CompanyId,SiteId,ReservationId)
      REFERENCES dbo.RESERVATION(OrionCompanyId,OrionSiteId,ID),
    CONSTRAINT FK_HospitalityGeneratedActivity_Activity FOREIGN KEY (ActivityId) REFERENCES dbo.Actividad(ID),
    CONSTRAINT FK_HospitalityGeneratedActivity_Template FOREIGN KEY (TemplateActivityId) REFERENCES dbo.Actividad(ID)
  );

  CREATE TABLE orion.HospitalityPaymentCorrectionManifest
  (
    CompanyId bigint NOT NULL CONSTRAINT DF_HospitalityPaymentManifest_Company DEFAULT (TRY_CONVERT(bigint,SESSION_CONTEXT(N'OrionERP.HospitalityCompanyId'))),
    SiteId bigint NOT NULL CONSTRAINT DF_HospitalityPaymentManifest_Site DEFAULT (TRY_CONVERT(bigint,SESSION_CONTEXT(N'OrionERP.HospitalitySiteId'))),
    BatchKey uniqueidentifier NOT NULL,
    OriginalReservationId int NOT NULL,
    OriginalTransactionId int NOT NULL,
    ProposedTransactionId int NOT NULL,
    ExpectedLinkAmount money NOT NULL,
    ExpectedOldPaymentRfc varchar(50) NOT NULL,
    ExpectedOldPaymentAmount money NOT NULL,
    ExpectedNewPaymentRfc varchar(50) NOT NULL,
    ExpectedNewPaymentAmount money NOT NULL,
    Evidence nvarchar(1000) NOT NULL,
    Reason nvarchar(500) NOT NULL,
    IsApproved bit NOT NULL CONSTRAINT DF_HospitalityPaymentManifest_Approved DEFAULT (0),
    CreatedAtUtc datetime2(3) NOT NULL CONSTRAINT DF_HospitalityPaymentManifest_Created DEFAULT (SYSUTCDATETIME()),
    CreatedBy nvarchar(256) NOT NULL,
    CONSTRAINT PK_HospitalityPaymentCorrectionManifest PRIMARY KEY (CompanyId,SiteId,BatchKey,OriginalReservationId,OriginalTransactionId),
    CONSTRAINT CK_HospitalityPaymentManifest_Ids CHECK (OriginalReservationId>0 AND OriginalTransactionId>0 AND ProposedTransactionId>0 AND OriginalTransactionId<>ProposedTransactionId),
    CONSTRAINT CK_HospitalityPaymentManifest_Text CHECK (NULLIF(LTRIM(RTRIM(Evidence)),N'') IS NOT NULL AND NULLIF(LTRIM(RTRIM(Reason)),N'') IS NOT NULL),
    CONSTRAINT FK_HospitalityPaymentManifest_Site FOREIGN KEY (CompanyId,SiteId) REFERENCES orion.Site(CompanyId,SiteId)
  );

  CREATE TABLE orion.HospitalityPaymentCorrectionAudit
  (
    AuditId bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalityPaymentCorrectionAudit PRIMARY KEY,
    CompanyId bigint NOT NULL,
    SiteId bigint NOT NULL,
    BatchKey uniqueidentifier NOT NULL,
    ReservationId int NOT NULL,
    OldTransactionId int NOT NULL,
    NewTransactionId int NOT NULL,
    LinkAmount money NOT NULL,
    PreviewChecksum char(64) NOT NULL,
    Evidence nvarchar(1000) NOT NULL,
    Reason nvarchar(500) NOT NULL,
    AppliedAtUtc datetime2(3) NOT NULL CONSTRAINT DF_HospitalityPaymentAudit_Applied DEFAULT (SYSUTCDATETIME()),
    AppliedBy nvarchar(256) NOT NULL,
    CONSTRAINT UQ_HospitalityPaymentAudit_Source UNIQUE (CompanyId,SiteId,BatchKey,ReservationId,OldTransactionId),
    CONSTRAINT FK_HospitalityPaymentAudit_Site FOREIGN KEY (CompanyId,SiteId) REFERENCES orion.Site(CompanyId,SiteId)
  );

  CREATE TABLE orion.HospitalityOutlookMappingQuarantine
  (
    QuarantineId bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalityOutlookMappingQuarantine PRIMARY KEY,
    CompanyId bigint NOT NULL,
    SiteId bigint NOT NULL,
    OriginalSyncId int NOT NULL,
    SourceKey nvarchar(200) NOT NULL,
    RoomName varchar(50) NOT NULL,
    ReservationId int NULL,
    StartDate date NOT NULL,
    EndDateExclusive date NOT NULL,
    OutlookCalendarId nvarchar(512) NOT NULL,
    OutlookEventId nvarchar(512) NOT NULL,
    ContentHash char(64) NOT NULL,
    RemoteIdentityHash char(64) NOT NULL,
    LastSyncedUtc datetime2(3) NOT NULL,
    Reason nvarchar(500) NOT NULL,
    Evidence nvarchar(1000) NOT NULL,
    MigrationId nvarchar(200) NOT NULL,
    QuarantinedAtUtc datetime2(3) NOT NULL CONSTRAINT DF_HospitalityOutlookQuarantine_At DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT UQ_HospitalityOutlookQuarantine_Source UNIQUE (CompanyId,SiteId,OriginalSyncId),
    CONSTRAINT FK_HospitalityOutlookQuarantine_Site FOREIGN KEY (CompanyId,SiteId) REFERENCES orion.Site(CompanyId,SiteId)
  );

  CREATE TABLE orion.HospitalityOutlookMappingRepairAudit
  (
    RepairAuditId bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalityOutlookMappingRepairAudit PRIMARY KEY,
    CompanyId bigint NOT NULL,
    SiteId bigint NOT NULL,
    SyncId int NOT NULL,
    OldReservationId int NOT NULL,
    NewReservationId int NOT NULL,
    OldSourceKey nvarchar(200) NOT NULL,
    NewSourceKey nvarchar(200) NOT NULL,
    ContentHash char(64) NOT NULL,
    RemoteIdentityHash char(64) NOT NULL,
    Evidence nvarchar(1000) NOT NULL,
    MigrationId nvarchar(200) NOT NULL,
    RepairedAtUtc datetime2(3) NOT NULL CONSTRAINT DF_HospitalityOutlookRepair_At DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT UQ_HospitalityOutlookRepair_Source UNIQUE (CompanyId,SiteId,SyncId),
    CONSTRAINT FK_HospitalityOutlookRepair_Site FOREIGN KEY (CompanyId,SiteId) REFERENCES orion.Site(CompanyId,SiteId)
  );

  /* Backfill only explicit ROOM.OWNER_ID values while each RLS scope is active. */
  DECLARE @CompanyId bigint,@SiteId bigint,@Rfc varchar(50);
  DECLARE owner_scope CURSOR LOCAL FAST_FORWARD FOR
    SELECT company.CompanyId,site.SiteId,company.Rfc
    FROM orion.Company company JOIN orion.Site site ON site.CompanyId=company.CompanyId
    WHERE company.IsActive=1 AND site.IsActive=1;
  OPEN owner_scope;
  FETCH NEXT FROM owner_scope INTO @CompanyId,@SiteId,@Rfc;
  WHILE @@FETCH_STATUS=0
  BEGIN
    EXEC sys.sp_set_session_context @key=N'OrionERP.HospitalityCompanyId',@value=@CompanyId;
    EXEC sys.sp_set_session_context @key=N'OrionERP.HospitalitySiteId',@value=@SiteId;
    EXEC sys.sp_set_session_context @key=N'OrionERP.HospitalityRfc',@value=@Rfc;
    INSERT orion.HospitalitySiteOwner (CompanyId,SiteId,ProveedorId,IsActive,CreatedBy,UpdatedBy)
      SELECT DISTINCT @CompanyId,@SiteId,room.OWNER_ID,1,N'E7 explicit ROOM.OWNER_ID',N'E7 explicit ROOM.OWNER_ID'
      FROM dbo.ROOM room
      WHERE room.OWNER_ID>0
        AND EXISTS (SELECT 1 FROM dbo.Proveedores provider WHERE provider.id=room.OWNER_ID)
        AND NOT EXISTS (SELECT 1 FROM orion.HospitalitySiteOwner target
                        WHERE target.CompanyId=@CompanyId AND target.SiteId=@SiteId AND target.ProveedorId=room.OWNER_ID);
    FETCH NEXT FROM owner_scope INTO @CompanyId,@SiteId,@Rfc;
  END;
  CLOSE owner_scope;
  DEALLOCATE owner_scope;

  IF EXISTS
  (
    SELECT 1 FROM orion.Company company JOIN orion.Site site ON site.CompanyId=company.CompanyId
    CROSS APPLY
    (
      SELECT COUNT_BIG(*) MissingOwners FROM dbo.ROOM room
      WHERE room.OrionCompanyId=company.CompanyId AND room.OrionSiteId=site.SiteId
        AND NOT EXISTS (SELECT 1 FROM orion.HospitalitySiteOwner ownerMap
                        WHERE ownerMap.CompanyId=room.OrionCompanyId AND ownerMap.SiteId=room.OrionSiteId AND ownerMap.ProveedorId=room.OWNER_ID)
    ) checkRows
    WHERE checkRows.MissingOwners>0
  )
    THROW 52171,'No todos los ROOM.OWNER_ID tienen una asociacion explicita.',1;

  ALTER TABLE dbo.ROOM WITH CHECK ADD CONSTRAINT FK_ROOM_HospitalitySiteOwner
    FOREIGN KEY (OrionCompanyId,OrionSiteId,OWNER_ID)
    REFERENCES orion.HospitalitySiteOwner(CompanyId,SiteId,ProveedorId);

  /* La política nunca se apaga: los siete agregados nuevos entran fail-closed. */
  EXEC(N'ALTER SECURITY POLICY orion.HospitalityScopePolicy
    ADD FILTER PREDICATE orion.fn_HospitalityScopePredicate(CompanyId,SiteId) ON orion.HospitalitySiteOwner,
    ADD BLOCK PREDICATE orion.fn_HospitalityScopePredicate(CompanyId,SiteId) ON orion.HospitalitySiteOwner AFTER INSERT,
    ADD BLOCK PREDICATE orion.fn_HospitalityScopePredicate(CompanyId,SiteId) ON orion.HospitalitySiteOwner AFTER UPDATE,
    ADD FILTER PREDICATE orion.fn_HospitalityScopePredicate(CompanyId,SiteId) ON orion.HospitalityActivityTemplateMapping,
    ADD BLOCK PREDICATE orion.fn_HospitalityScopePredicate(CompanyId,SiteId) ON orion.HospitalityActivityTemplateMapping AFTER INSERT,
    ADD BLOCK PREDICATE orion.fn_HospitalityScopePredicate(CompanyId,SiteId) ON orion.HospitalityActivityTemplateMapping AFTER UPDATE,
    ADD FILTER PREDICATE orion.fn_HospitalityScopePredicate(CompanyId,SiteId) ON orion.HospitalityGeneratedActivity,
    ADD BLOCK PREDICATE orion.fn_HospitalityScopePredicate(CompanyId,SiteId) ON orion.HospitalityGeneratedActivity AFTER INSERT,
    ADD BLOCK PREDICATE orion.fn_HospitalityScopePredicate(CompanyId,SiteId) ON orion.HospitalityGeneratedActivity AFTER UPDATE,
    ADD FILTER PREDICATE orion.fn_HospitalityScopePredicate(CompanyId,SiteId) ON orion.HospitalityPaymentCorrectionManifest,
    ADD BLOCK PREDICATE orion.fn_HospitalityScopePredicate(CompanyId,SiteId) ON orion.HospitalityPaymentCorrectionManifest AFTER INSERT,
    ADD BLOCK PREDICATE orion.fn_HospitalityScopePredicate(CompanyId,SiteId) ON orion.HospitalityPaymentCorrectionManifest AFTER UPDATE,
    ADD FILTER PREDICATE orion.fn_HospitalityScopePredicate(CompanyId,SiteId) ON orion.HospitalityPaymentCorrectionAudit,
    ADD BLOCK PREDICATE orion.fn_HospitalityScopePredicate(CompanyId,SiteId) ON orion.HospitalityPaymentCorrectionAudit AFTER INSERT,
    ADD BLOCK PREDICATE orion.fn_HospitalityScopePredicate(CompanyId,SiteId) ON orion.HospitalityPaymentCorrectionAudit AFTER UPDATE,
    ADD FILTER PREDICATE orion.fn_HospitalityScopePredicate(CompanyId,SiteId) ON orion.HospitalityOutlookMappingQuarantine,
    ADD BLOCK PREDICATE orion.fn_HospitalityScopePredicate(CompanyId,SiteId) ON orion.HospitalityOutlookMappingQuarantine AFTER INSERT,
    ADD BLOCK PREDICATE orion.fn_HospitalityScopePredicate(CompanyId,SiteId) ON orion.HospitalityOutlookMappingQuarantine AFTER UPDATE,
    ADD FILTER PREDICATE orion.fn_HospitalityScopePredicate(CompanyId,SiteId) ON orion.HospitalityOutlookMappingRepairAudit,
    ADD BLOCK PREDICATE orion.fn_HospitalityScopePredicate(CompanyId,SiteId) ON orion.HospitalityOutlookMappingRepairAudit AFTER INSERT,
    ADD BLOCK PREDICATE orion.fn_HospitalityScopePredicate(CompanyId,SiteId) ON orion.HospitalityOutlookMappingRepairAudit AFTER UPDATE;');

  /* Evidencia cerrada de los cuatro huérfanos. No hay operación remota. */
  SET @CompanyId=9; SET @SiteId=3; SET @Rfc='OHM191112Q26';
  IF NOT EXISTS (SELECT 1 FROM orion.Company company JOIN orion.Site site ON site.CompanyId=company.CompanyId
                 WHERE company.CompanyId=@CompanyId AND site.SiteId=@SiteId AND company.Rfc=@Rfc)
    THROW 52172,'El alcance esperado de Bonhomia no existe.',1;
  EXEC sys.sp_set_session_context @key=N'OrionERP.HospitalityCompanyId',@value=@CompanyId;
  EXEC sys.sp_set_session_context @key=N'OrionERP.HospitalitySiteId',@value=@SiteId;
  EXEC sys.sp_set_session_context @key=N'OrionERP.HospitalityRfc',@value=@Rfc;

  IF (SELECT COUNT(*) FROM dbo.ROOM_CALENDAR_OUTLOOK_SYNC WHERE ID IN (18,61,105,107))<>4
    THROW 52173,'Cambió el conjunto de cuatro mappings Outlook revisado.',1;
  IF EXISTS
  (
    SELECT 1 FROM dbo.ROOM_CALENDAR_OUTLOOK_SYNC syncRow
    JOIN (VALUES
      (18,'E322B4E75A0F9B8CEEBA34C9AA841BAA2698C35D53FBFD1C042E88DDF4DA882F','EA62F95A990534C5F0868F121F17309B3CEF88526D9370D0430823682205AE46'),
      (61,'C57F538D794C59D6C60C3B19BD8B1152BF1D2B8D17D3F03DA1165EE33299B8FA','DCCBDB352448D73632022728ACC3A239F882E8DCFCF2AE0F3F4B1DCD4B27D7FA'),
      (105,'582FD6153E3EEB7818825B016285296CC1597CE0247D6EF93BEC2F82AF3DBC56','F41AFC0D1F3EA3C05ED2598E6177CE85330B29486E641B4D271ACDC6C3F74F1F'),
      (107,'DB52B828A0F9B6495293BDCA579D6DABD719CA4AD0A59974BC120FCE9F421A73','4234EF1F9A38A71BB8051E1B6674023178E42F096298F17B63AA7B0734CDB06A')
    ) expected(ID,ContentHash,RemoteHash) ON expected.ID=syncRow.ID
    WHERE syncRow.CONTENT_HASH<>expected.ContentHash
       OR CONVERT(char(64),HASHBYTES('SHA2_256',CONVERT(nvarchar(max),CONCAT(syncRow.OUTLOOK_CALENDAR_ID,N'|',syncRow.OUTLOOK_EVENT_ID))),2)<>expected.RemoteHash
  )
    THROW 52174,'Cambió la identidad remota o el contenido de un mapping Outlook.',1;

  IF NOT EXISTS
  (
    SELECT 1 FROM dbo.ROOM_CALENDAR_OUTLOOK_SYNC syncRow
    JOIN dbo.ROOM_CALENDAR calendarRow ON calendarRow.ID=10059 AND calendarRow.RoomId=syncRow.RoomId AND calendarRow.ROOM_DATE=syncRow.START_DATE
    JOIN dbo.RESERVATION reservation ON reservation.ID=24222 AND reservation.CHECKIN=syncRow.START_DATE AND reservation.CHECKOUT=syncRow.END_DATE_EXCLUSIVE
    WHERE syncRow.ID=105 AND syncRow.RESERVATION_ID=24210 AND syncRow.SOURCE_KEY=N'reservation:24210:PENTHOUSE'
      AND calendarRow.ReservationId=reservation.ID
  ) OR NOT EXISTS
  (
    SELECT 1 FROM dbo.ROOM_CALENDAR_OUTLOOK_SYNC syncRow
    JOIN dbo.ROOM_CALENDAR calendarRow ON calendarRow.ID=2407 AND calendarRow.RoomId=syncRow.RoomId AND calendarRow.ROOM_DATE=syncRow.START_DATE
    JOIN dbo.RESERVATION reservation ON reservation.ID=24228 AND reservation.CHECKIN=syncRow.START_DATE AND reservation.CHECKOUT=syncRow.END_DATE_EXCLUSIVE
    WHERE syncRow.ID=107 AND syncRow.RESERVATION_ID=24215 AND syncRow.SOURCE_KEY=N'reservation:24215:BERLIN'
      AND calendarRow.ReservationId=reservation.ID
  )
    THROW 52175,'La evidencia local para reparar Outlook 105/107 ya no coincide.',1;

  INSERT orion.HospitalityOutlookMappingRepairAudit
    (CompanyId,SiteId,SyncId,OldReservationId,NewReservationId,OldSourceKey,NewSourceKey,ContentHash,RemoteIdentityHash,Evidence,MigrationId)
  SELECT syncRow.OrionCompanyId,syncRow.OrionSiteId,syncRow.ID,syncRow.RESERVATION_ID,evidence.NewReservationId,
         syncRow.SOURCE_KEY,evidence.NewSourceKey,syncRow.CONTENT_HASH,
         CONVERT(char(64),HASHBYTES('SHA2_256',CONVERT(nvarchar(max),CONCAT(syncRow.OUTLOOK_CALENDAR_ID,N'|',syncRow.OUTLOOK_EVENT_ID))),2),
         evidence.Evidence,@MigrationId
  FROM dbo.ROOM_CALENDAR_OUTLOOK_SYNC syncRow
  JOIN (VALUES
    (105,24222,N'reservation:24222:PENTHOUSE',N'ROOM_CALENDAR 10059 y RESERVATION 24222 coinciden en habitación, sede y estancia 2026-08-01/02.'),
    (107,24228,N'reservation:24228:BERLIN',N'ROOM_CALENDAR 2407 y RESERVATION 24228 coinciden en habitación, sede y estancia 2026-08-02/03.')
  ) evidence(SyncId,NewReservationId,NewSourceKey,Evidence) ON evidence.SyncId=syncRow.ID;

  UPDATE syncRow SET RESERVATION_ID=evidence.NewReservationId,SOURCE_KEY=evidence.NewSourceKey
  FROM dbo.ROOM_CALENDAR_OUTLOOK_SYNC syncRow
  JOIN (VALUES (105,24222,N'reservation:24222:PENTHOUSE'),(107,24228,N'reservation:24228:BERLIN'))
    evidence(SyncId,NewReservationId,NewSourceKey) ON evidence.SyncId=syncRow.ID;
  IF @@ROWCOUNT<>2 THROW 52176,'No se repararon exactamente dos mappings Outlook.',1;

  IF EXISTS (SELECT 1 FROM dbo.RESERVATION WHERE ID IN (23962,24148))
    THROW 52177,'Apareció una reserva fuente para un mapping destinado a cuarentena.',1;
  INSERT orion.HospitalityOutlookMappingQuarantine
    (CompanyId,SiteId,OriginalSyncId,SourceKey,RoomName,ReservationId,StartDate,EndDateExclusive,
     OutlookCalendarId,OutlookEventId,ContentHash,RemoteIdentityHash,LastSyncedUtc,Reason,Evidence,MigrationId)
  SELECT syncRow.OrionCompanyId,syncRow.OrionSiteId,syncRow.ID,syncRow.SOURCE_KEY,syncRow.ROOM_NAME,syncRow.RESERVATION_ID,
         syncRow.START_DATE,syncRow.END_DATE_EXCLUSIVE,syncRow.OUTLOOK_CALENDAR_ID,syncRow.OUTLOOK_EVENT_ID,syncRow.CONTENT_HASH,
         CONVERT(char(64),HASHBYTES('SHA2_256',CONVERT(nvarchar(max),CONCAT(syncRow.OUTLOOK_CALENDAR_ID,N'|',syncRow.OUTLOOK_EVENT_ID))),2),
         syncRow.LAST_SYNCED_UTC,evidence.Reason,evidence.Evidence,@MigrationId
  FROM dbo.ROOM_CALENDAR_OUTLOOK_SYNC syncRow
  JOIN (VALUES
    (18,N'Reserva fuente ausente y sin sustituto local demostrable.',N'BERLIN 2026-05-04/05; ROOM_CALENDAR 2317 no tiene reserva ni bloqueo atribuible.'),
    (61,N'Reserva fuente ausente y calendario local contradictorio.',N'BERLIN 2026-07-03/05 apunta a 24155, cuya estancia real inicia 2026-07-06; no se infiere reemplazo.')
  ) evidence(SyncId,Reason,Evidence) ON evidence.SyncId=syncRow.ID;
  IF @@ROWCOUNT<>2 THROW 52178,'No se archivaron exactamente dos mappings Outlook.',1;
  DELETE FROM dbo.ROOM_CALENDAR_OUTLOOK_SYNC WHERE ID IN (18,61);
  IF @@ROWCOUNT<>2 THROW 52179,'No se aislaron exactamente dos mappings Outlook.',1;

  EXEC sys.sp_set_session_context @key=N'OrionERP.HospitalityCompanyId',@value=NULL;
  EXEC sys.sp_set_session_context @key=N'OrionERP.HospitalitySiteId',@value=NULL;
  EXEC sys.sp_set_session_context @key=N'OrionERP.HospitalityRfc',@value=NULL;

  /* Generador heredado: sólo opera con un mapping exacto y revalidado dentro de la misma transacción. */
  EXEC(N'CREATE OR ALTER PROCEDURE dbo.CreateActividadForReservation
    @Tipo_OrdenCalendarID int,@Fecha_Inicio datetime2,@Fecha_Final datetime2,@Presupuesto money,
    @Asignacion int=1,@Descripcion varchar(800)=''LIMPIEZA'',@Tipo_Orden varchar(200),@Cuenta_SAT int,@Suite varchar(200)=''''
  AS
  BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    DECLARE @CompanyId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N''OrionERP.HospitalityCompanyId'')),
            @SiteId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N''OrionERP.HospitalitySiteId'')),
            @Rfc varchar(50)=CONVERT(varchar(50),SESSION_CONTEXT(N''OrionERP.HospitalityRfc'')),
            @RoomId int,@RoomName varchar(50),@ReservationId int,@TemplateId int,@MappedAssignee int,@MappedAccount int,
            @ActivityId int,@ActivityType varchar(200)=UPPER(LTRIM(RTRIM(@Tipo_Orden))),@LockResult int,@LockResource nvarchar(255);
    IF @CompanyId IS NULL OR @SiteId IS NULL OR NULLIF(LTRIM(RTRIM(@Rfc)),'''') IS NULL
      THROW 52180,''Selecciona una empresa y sede autorizadas de Hospedaje.'',1;
    IF @Fecha_Final<=@Fecha_Inicio OR NULLIF(@ActivityType,'''') IS NULL
      THROW 52181,''Fechas y tipo de orden no son validos.'',1;
    BEGIN TRANSACTION;
    SET @LockResource=CONCAT(N''OrionERP:Hospitality:Activity:'',@CompanyId,N'':'',@SiteId,N'':'',@Tipo_OrdenCalendarID,N'':'',@ActivityType);
    EXEC @LockResult=sys.sp_getapplock @Resource=@LockResource,
      @LockMode=N''Exclusive'',@LockOwner=N''Transaction'',@LockTimeout=15000;
    IF @LockResult<0 THROW 52182,''No se obtuvo el candado para crear la actividad.'',1;
    SELECT @RoomId=calendarRow.RoomId,@RoomName=calendarRow.ROOM,
           @ReservationId=COALESCE(calendarRow.ReservationId,TRY_CONVERT(int,NULLIF(LTRIM(RTRIM(calendarRow.LOCK_DESCRIPTION)),'''')))
    FROM dbo.ROOM_CALENDAR calendarRow WITH (UPDLOCK,HOLDLOCK)
    WHERE calendarRow.ID=@Tipo_OrdenCalendarID AND calendarRow.OrionCompanyId=@CompanyId AND calendarRow.OrionSiteId=@SiteId;
    IF @RoomId IS NULL OR @ReservationId IS NULL
      THROW 52183,''El calendario no tiene una reservacion demostrable en esta sede.'',1;
    IF NULLIF(LTRIM(RTRIM(@Suite)),'''') IS NOT NULL AND LTRIM(RTRIM(@Suite))<>@RoomName
      THROW 52184,''La suite indicada no coincide con el calendario.'',1;
    IF NOT EXISTS (SELECT 1 FROM dbo.RESERVATION reservation WITH (UPDLOCK,HOLDLOCK)
                   WHERE reservation.ID=@ReservationId AND reservation.OrionCompanyId=@CompanyId AND reservation.OrionSiteId=@SiteId
                     AND @Fecha_Inicio>=reservation.CHECKIN AND CONVERT(date,@Fecha_Final)<=reservation.CHECKOUT)
      THROW 52185,''La reservacion o sus fechas no coinciden con el calendario.'',1;
    SELECT @TemplateId=mapping.TemplateActivityId,@MappedAssignee=mapping.AssigneeEmployeeId,@MappedAccount=mapping.CuentaSatId
    FROM orion.HospitalityActivityTemplateMapping mapping WITH (UPDLOCK,HOLDLOCK)
    WHERE mapping.CompanyId=@CompanyId AND mapping.SiteId=@SiteId AND mapping.RoomId=@RoomId
      AND mapping.ActivityType=@ActivityType AND mapping.IsEnabled=1;
    IF @TemplateId IS NULL THROW 52186,''No existe una plantilla activa para esta habitacion y tipo de orden.'',1;
    IF @Asignacion<>@MappedAssignee OR @Cuenta_SAT<>@MappedAccount
      THROW 52187,''Responsable o cuenta SAT no coinciden con el mapping aprobado.'',1;
    SELECT @ActivityId=generated.ActivityId FROM orion.HospitalityGeneratedActivity generated WITH (UPDLOCK,HOLDLOCK)
    WHERE generated.CompanyId=@CompanyId AND generated.SiteId=@SiteId AND generated.RoomCalendarId=@Tipo_OrdenCalendarID
      AND generated.ActivityType=@ActivityType;
    IF @ActivityId IS NOT NULL
    BEGIN SELECT @ActivityId ID; COMMIT TRANSACTION; RETURN; END;
    IF NOT EXISTS (SELECT 1 FROM dbo.Actividad template WITH (UPDLOCK,HOLDLOCK)
                   WHERE template.ID=@TemplateId AND template.RFC=@Rfc
                     AND EXISTS (SELECT 1 FROM dbo.Actividad_Ruta_Critica step WITH (UPDLOCK,HOLDLOCK) WHERE step.Actividad_ID=template.ID)
                     AND NOT EXISTS (SELECT 1 FROM dbo.Actividad_RoomCalendar link WITH (UPDLOCK,HOLDLOCK) WHERE link.Actividad_ID=template.ID))
      THROW 52188,''La plantilla dejo de ser estructuralmente valida.'',1;
    IF NOT EXISTS (SELECT 1 FROM dbo.Capital_Humano employee WITH (UPDLOCK,HOLDLOCK)
                   WHERE employee.ID=@MappedAssignee AND employee.RFC=@Rfc
                     AND UPPER(LTRIM(RTRIM(ISNULL(employee.[Status],''''))))=''ACTIVO'')
      THROW 52189,''El responsable configurado ya no esta activo en esta empresa.'',1;
    INSERT dbo.Actividad
      (Fecha_Inicio,Fecha_Final,Ubicacion,Descripcion,RazonSocial,Departamento,Tipo_Proyecto,Cliente,Asignacion,
       Presupuesto,Gasto_Total,Cobro_Cliente,Estatus_Autorizacion,Priorizacion,Ejecucion,Estatus,Memo,Cuenta_SAT_ID,RFC)
    SELECT @Fecha_Inicio,@Fecha_Final,@RoomName,@Descripcion,template.RazonSocial,template.Departamento,template.Tipo_Proyecto,
           template.Cliente,@MappedAssignee,@Presupuesto,0,0,template.Estatus_Autorizacion,template.Priorizacion,
           template.Ejecucion,''ACTIVO'',template.Memo,@MappedAccount,@Rfc
    FROM dbo.Actividad template WHERE template.ID=@TemplateId;
    SET @ActivityId=CONVERT(int,SCOPE_IDENTITY());
    INSERT dbo.Actividad_Ruta_Critica (Paso_Numero,Actividad_ID,Descripcion,Finalizado,Procedimiento_ID,Notas,Imagen)
      SELECT step.Paso_Numero,@ActivityId,step.Descripcion,0,step.Procedimiento_ID,step.Notas,step.Imagen
      FROM dbo.Actividad_Ruta_Critica step WHERE step.Actividad_ID=@TemplateId;
    INSERT dbo.Actividad_RoomCalendar (Actividad_ID,RoomCalendar_ID) VALUES (@ActivityId,@Tipo_OrdenCalendarID);
    IF NOT EXISTS (SELECT 1 FROM dbo.Reservacion_Actividad WHERE Reservacion_ID=@ReservationId AND Actividad_ID=@ActivityId)
      INSERT dbo.Reservacion_Actividad (Reservacion_ID,Actividad_ID) VALUES (@ReservationId,@ActivityId);
    INSERT orion.HospitalityGeneratedActivity
      (CompanyId,SiteId,RoomCalendarId,RoomId,ActivityType,ReservationId,ActivityId,TemplateActivityId,CreatedBy)
    VALUES (@CompanyId,@SiteId,@Tipo_OrdenCalendarID,@RoomId,@ActivityType,@ReservationId,@ActivityId,@TemplateId,
            COALESCE(CONVERT(nvarchar(256),SESSION_CONTEXT(N''OrionERP.UserName'')),CONVERT(nvarchar(256),ORIGINAL_LOGIN())));
    COMMIT TRANSACTION;
    SELECT @ActivityId ID;
  END;');

  /* Corrector por manifiesto. El intercambio temporal conserva RLS encendido y ocurre bajo
     transacción/candado; al salir, el predicado fuerte vuelve a estar vigente. */
  EXEC(N'CREATE OR ALTER PROCEDURE orion.ReconcileHospitalityPaymentLinks
    @BatchKey uniqueidentifier,@ApplyChanges bit=0,@ExpectedPreviewChecksum char(64)=NULL,@Actor nvarchar(256)=NULL
  AS
  BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    DECLARE @CompanyId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N''OrionERP.HospitalityCompanyId'')),
            @SiteId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N''OrionERP.HospitalitySiteId'')),
            @Rfc varchar(50)=CONVERT(varchar(50),SESSION_CONTEXT(N''OrionERP.HospitalityRfc'')),
            @Rows int,@Hash char(64),@LockResult int,@LockResource nvarchar(255);
    IF @CompanyId IS NULL OR @SiteId IS NULL OR NULLIF(LTRIM(RTRIM(@Rfc)),'''') IS NULL
      THROW 52190,''Selecciona una empresa y sede autorizadas de Hospedaje.'',1;
    SELECT @Rows=COUNT(*) FROM orion.HospitalityPaymentCorrectionManifest WHERE BatchKey=@BatchKey;
    IF @Rows NOT BETWEEN 1 AND 25 THROW 52191,''El lote debe contener entre 1 y 25 correcciones.'',1;
    IF EXISTS (SELECT 1 FROM orion.HospitalityPaymentCorrectionManifest WHERE BatchKey=@BatchKey AND IsApproved=0)
      THROW 52192,''Todas las correcciones del lote deben estar aprobadas.'',1;
    IF (SELECT COUNT(*) FROM orion.HospitalityPaymentCorrectionAudit WHERE BatchKey=@BatchKey)=@Rows
    BEGIN SELECT N''YA_APLICADO'' Estado,@BatchKey BatchKey,@Rows Filas; RETURN; END;
    IF EXISTS (SELECT 1 FROM orion.HospitalityPaymentCorrectionAudit WHERE BatchKey=@BatchKey)
      THROW 52193,''El lote tiene una aplicacion parcial y requiere revision manual.'',1;
    BEGIN TRANSACTION;
    SET @LockResource=CONCAT(N''OrionERP:Hospitality:PaymentCorrection:'',@CompanyId,N'':'',@SiteId);
    EXEC @LockResult=sys.sp_getapplock @Resource=@LockResource,
      @LockMode=N''Exclusive'',@LockOwner=N''Transaction'',@LockTimeout=15000;
    IF @LockResult<0 THROW 52194,''No se obtuvo el candado del corrector de pagos.'',1;
    EXEC(N''ALTER SECURITY POLICY orion.HospitalityScopePolicy
      DROP FILTER PREDICATE ON dbo.Reservation_Transacciones,
      DROP BLOCK PREDICATE ON dbo.Reservation_Transacciones AFTER INSERT,
      DROP BLOCK PREDICATE ON dbo.Reservation_Transacciones AFTER UPDATE;
      ALTER SECURITY POLICY orion.HospitalityScopePolicy
      ADD FILTER PREDICATE orion.fn_HospitalityScopePredicate(OrionCompanyId,OrionSiteId) ON dbo.Reservation_Transacciones,
      ADD BLOCK PREDICATE orion.fn_HospitalityScopePredicate(OrionCompanyId,OrionSiteId) ON dbo.Reservation_Transacciones AFTER INSERT,
      ADD BLOCK PREDICATE orion.fn_HospitalityScopePredicate(OrionCompanyId,OrionSiteId) ON dbo.Reservation_Transacciones AFTER UPDATE;'');
    IF (SELECT COUNT(*) FROM dbo.Reservation_Transacciones link JOIN orion.HospitalityPaymentCorrectionManifest manifest
        ON manifest.BatchKey=@BatchKey AND manifest.OriginalReservationId=link.ReservationID AND manifest.OriginalTransactionId=link.TransaccionID)<>@Rows
      THROW 52195,''Una relacion de pago original ya no coincide con el manifiesto.'',1;
    IF EXISTS
    (
      SELECT 1 FROM orion.HospitalityPaymentCorrectionManifest manifest
      LEFT JOIN dbo.RESERVATION reservation ON reservation.ID=manifest.OriginalReservationId
        AND reservation.OrionCompanyId=@CompanyId AND reservation.OrionSiteId=@SiteId
      LEFT JOIN dbo.Reservation_Transacciones link ON link.ReservationID=manifest.OriginalReservationId
        AND link.TransaccionID=manifest.OriginalTransactionId
      LEFT JOIN dbo.Transacciones oldPayment ON oldPayment.ID=manifest.OriginalTransactionId
      LEFT JOIN dbo.Transacciones newPayment ON newPayment.ID=manifest.ProposedTransactionId
      WHERE manifest.BatchKey=@BatchKey
        AND (reservation.ID IS NULL OR link.ReservationID IS NULL OR link.Amount<>manifest.ExpectedLinkAmount
          OR oldPayment.ID IS NULL OR oldPayment.RFC<>manifest.ExpectedOldPaymentRfc OR oldPayment.Monto<>manifest.ExpectedOldPaymentAmount
          OR oldPayment.RFC=@Rfc OR newPayment.ID IS NULL OR newPayment.RFC<>manifest.ExpectedNewPaymentRfc
          OR newPayment.Monto<>manifest.ExpectedNewPaymentAmount OR newPayment.RFC<>@Rfc
          OR EXISTS (SELECT 1 FROM dbo.Reservation_Transacciones collision
                     WHERE collision.ReservationID=manifest.OriginalReservationId AND collision.TransaccionID=manifest.ProposedTransactionId))
    ) THROW 52196,''El estado actual no coincide exactamente con la evidencia aprobada.'',1;
    SELECT @Hash=CONVERT(char(64),HASHBYTES(''SHA2_256'',CONVERT(nvarchar(max),
      STRING_AGG(CONVERT(nvarchar(max),CONCAT(manifest.OriginalReservationId,N''|'',manifest.OriginalTransactionId,N''|'',
        manifest.ProposedTransactionId,N''|'',CONVERT(decimal(19,4),link.Amount),N''|'',oldPayment.RFC,N''|'',
        CONVERT(decimal(19,4),oldPayment.Monto),N''|'',newPayment.RFC,N''|'',CONVERT(decimal(19,4),newPayment.Monto),N''|'',
        manifest.Evidence,N''|'',manifest.Reason)),NCHAR(10)) WITHIN GROUP (ORDER BY manifest.OriginalReservationId,manifest.OriginalTransactionId))),2)
    FROM orion.HospitalityPaymentCorrectionManifest manifest
    JOIN dbo.Reservation_Transacciones link ON link.ReservationID=manifest.OriginalReservationId AND link.TransaccionID=manifest.OriginalTransactionId
    JOIN dbo.Transacciones oldPayment ON oldPayment.ID=manifest.OriginalTransactionId
    JOIN dbo.Transacciones newPayment ON newPayment.ID=manifest.ProposedTransactionId
    WHERE manifest.BatchKey=@BatchKey;
    SELECT manifest.OriginalReservationId ReservationId,manifest.OriginalTransactionId OldTransactionId,
           manifest.ProposedTransactionId NewTransactionId,manifest.ExpectedLinkAmount LinkAmount,
           oldPayment.RFC OldRfc,newPayment.RFC NewRfc,@Hash PreviewChecksum
    FROM orion.HospitalityPaymentCorrectionManifest manifest
    JOIN dbo.Transacciones oldPayment ON oldPayment.ID=manifest.OriginalTransactionId
    JOIN dbo.Transacciones newPayment ON newPayment.ID=manifest.ProposedTransactionId
    WHERE manifest.BatchKey=@BatchKey ORDER BY manifest.OriginalReservationId,manifest.OriginalTransactionId;
    IF @ApplyChanges=1 AND (NULLIF(@ExpectedPreviewChecksum,'''') IS NULL OR UPPER(@ExpectedPreviewChecksum)<>@Hash)
      THROW 52197,''El checksum del preview no coincide; vuelve a ejecutar preview.'',1;
    IF @ApplyChanges=1
    BEGIN
      UPDATE link SET TransaccionID=manifest.ProposedTransactionId
      FROM dbo.Reservation_Transacciones link
      JOIN orion.HospitalityPaymentCorrectionManifest manifest
        ON manifest.BatchKey=@BatchKey AND manifest.OriginalReservationId=link.ReservationID AND manifest.OriginalTransactionId=link.TransaccionID;
      IF @@ROWCOUNT<>@Rows THROW 52198,''No se actualizaron exactamente las relaciones aprobadas.'',1;
      INSERT orion.HospitalityPaymentCorrectionAudit
        (CompanyId,SiteId,BatchKey,ReservationId,OldTransactionId,NewTransactionId,LinkAmount,PreviewChecksum,Evidence,Reason,AppliedBy)
      SELECT @CompanyId,@SiteId,@BatchKey,OriginalReservationId,OriginalTransactionId,ProposedTransactionId,
             ExpectedLinkAmount,@Hash,Evidence,Reason,COALESCE(NULLIF(LTRIM(RTRIM(@Actor)),N''''),CONVERT(nvarchar(256),ORIGINAL_LOGIN()))
      FROM orion.HospitalityPaymentCorrectionManifest WHERE BatchKey=@BatchKey;
    END;
    EXEC(N''ALTER SECURITY POLICY orion.HospitalityScopePolicy
      DROP FILTER PREDICATE ON dbo.Reservation_Transacciones,
      DROP BLOCK PREDICATE ON dbo.Reservation_Transacciones AFTER INSERT,
      DROP BLOCK PREDICATE ON dbo.Reservation_Transacciones AFTER UPDATE;
      ALTER SECURITY POLICY orion.HospitalityScopePolicy
      ADD FILTER PREDICATE orion.fn_HospitalityPaymentScopePredicate(OrionCompanyId,OrionSiteId,TransaccionID) ON dbo.Reservation_Transacciones,
      ADD BLOCK PREDICATE orion.fn_HospitalityPaymentScopePredicate(OrionCompanyId,OrionSiteId,TransaccionID) ON dbo.Reservation_Transacciones AFTER INSERT,
      ADD BLOCK PREDICATE orion.fn_HospitalityPaymentScopePredicate(OrionCompanyId,OrionSiteId,TransaccionID) ON dbo.Reservation_Transacciones AFTER UPDATE;'');
    IF @ApplyChanges=1
    BEGIN COMMIT TRANSACTION; SELECT N''APLICADO'' Estado,@BatchKey BatchKey,@Rows Filas,@Hash PreviewChecksum; END
    ELSE
    BEGIN ROLLBACK TRANSACTION; SELECT N''VALIDADO_SIN_CAMBIOS'' Estado,@BatchKey BatchKey,@Rows Filas,@Hash PreviewChecksum; END;
  END;');

  IF OBJECT_DEFINITION(OBJECT_ID(N'dbo.CreateActividadForReservation')) LIKE '%THROW 51822%'
    THROW 52199,'El generador de actividades sigue bloqueado por el stub anterior.',1;
  IF OBJECT_DEFINITION(OBJECT_ID(N'dbo.CreateActividadForReservation')) NOT LIKE '%HospitalityActivityTemplateMapping%'
    THROW 52200,'El generador no exige el mapping explícito.',1;
  IF OBJECT_DEFINITION(OBJECT_ID(N'orion.ReconcileHospitalityPaymentLinks')) NOT LIKE '%ExpectedPreviewChecksum%'
    THROW 52201,'El corrector no enlaza apply con preview.',1;
  IF NOT EXISTS (SELECT 1 FROM sys.security_policies WHERE object_id=OBJECT_ID(N'orion.HospitalityScopePolicy') AND is_enabled=1 AND is_schema_bound=1)
    THROW 52202,'La politica RLS dejo de estar activa.',1;
  IF (SELECT COUNT(*) FROM sys.security_predicates WHERE object_id=OBJECT_ID(N'orion.HospitalityScopePolicy'))<75
    THROW 52203,'Faltan predicados RLS para los agregados E7.',1;

  SELECT
    (SELECT COUNT_BIG(*) FROM orion.HospitalitySiteOwner) OwnerAssociationsVisibleWithoutScope,
    (SELECT COUNT_BIG(*) FROM orion.HospitalityActivityTemplateMapping) ActivityMappingsVisibleWithoutScope,
    (SELECT COUNT_BIG(*) FROM orion.HospitalityPaymentCorrectionManifest) PaymentManifestRowsVisibleWithoutScope,
    (SELECT COUNT_BIG(*) FROM orion.HospitalityOutlookMappingQuarantine) OutlookQuarantineVisibleWithoutScope,
    (SELECT COUNT_BIG(*) FROM orion.HospitalityOutlookMappingRepairAudit) OutlookRepairAuditVisibleWithoutScope,
    (SELECT COUNT(*) FROM sys.security_predicates WHERE object_id=OBJECT_ID(N'orion.HospitalityScopePolicy')) HospitalityPredicateCount;

  IF @ApplyChanges=1
  BEGIN
    INSERT orion.SchemaMigration (MigrationId,Checksum,AppliedBy,AppVersion,DatabaseName)
    VALUES (@MigrationId,@MigrationChecksum,
      COALESCE(CONVERT(nvarchar(256),SESSION_CONTEXT(N'OrionERP.UserName')),CONVERT(nvarchar(256),ORIGINAL_LOGIN())),
      @AppVersion,DB_NAME());
    COMMIT TRANSACTION;
    SELECT N'APLICADO_SOLO_SANDBOX' Estado,DB_NAME() BaseDatos,@MigrationId MigrationId;
  END
  ELSE
  BEGIN
    ROLLBACK TRANSACTION;
    SELECT N'VALIDADO_SIN_CAMBIOS' Estado,DB_NAME() BaseDatos,@MigrationId MigrationId;
  END;
END TRY
BEGIN CATCH
  IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
  BEGIN TRY
    EXEC sys.sp_set_session_context @key=N'OrionERP.HospitalityCompanyId',@value=NULL;
    EXEC sys.sp_set_session_context @key=N'OrionERP.HospitalitySiteId',@value=NULL;
    EXEC sys.sp_set_session_context @key=N'OrionERP.HospitalityRfc',@value=NULL;
  END TRY BEGIN CATCH END CATCH;
  THROW;
END CATCH;
