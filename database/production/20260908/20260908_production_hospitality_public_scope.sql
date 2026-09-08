/*
  Production cutover 2026-09-08: 20260908_production_hospitality_public_scope.
  Dedicated contract for the existing Bonhomia/Bruno installations only.
  DDL semantics reviewed from 20260903_hospitality_public_scope_sandbox; original bytes remain untouched.
  Production requires independently authorized backup, reviewed preview, then apply.
  The isolated rehearsal target is a restored production copy, never Orion_Sandbox.
  No historical payment reassignment, invented TaxRfc, new client or integration enablement.
  See docs/production-migration-package-20260908.md for differences and exceptions.
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

DECLARE @ExpectedDatabase sysname = N'$(ExpectedDatabase)';
DECLARE @ApplyChangesInput nvarchar(20) = N'$(ApplyChanges)';
DECLARE @ApplyChanges bit = 0;
DECLARE @MigrationId nvarchar(200) = N'$(MigrationId)';
DECLARE @MigrationChecksum varchar(128) = '$(MigrationChecksum)';
DECLARE @AppVersionInput nvarchar(64) = N'$(AppVersion)';
DECLARE @AppVersion nvarchar(64) = NULL;

IF @ApplyChangesInput NOT LIKE N'$' + N'(%'
BEGIN
  IF @ApplyChangesInput NOT IN (N'0', N'1')
    THROW 51400, 'ApplyChanges debe ser 0 o 1.', 1;
  SET @ApplyChanges = CONVERT(bit, @ApplyChangesInput);
END;

IF @ExpectedDatabase LIKE N'$' + N'(%' OR @ExpectedDatabase NOT IN (N'grupocarpio', N'Orion_CutoverValidation_20260908')
  THROW 51401, 'Esta migracion admite grupocarpio o el ensayo aislado Orion_CutoverValidation_20260908.', 1;

IF DB_NAME() <> @ExpectedDatabase
  THROW 51402, 'La conexion no coincide con ExpectedDatabase.', 1;

IF @MigrationId LIKE N'$' + N'(%'
   OR NULLIF(LTRIM(RTRIM(@MigrationId)), N'') IS NULL
   OR LEN(@MigrationId) > 200
  THROW 51403, 'MigrationId es obligatorio y debe provenir del manifiesto.', 1;

IF @MigrationChecksum LIKE '$' + '(%'
   OR LEN(@MigrationChecksum) <> 64
   OR @MigrationChecksum LIKE '%[^0-9A-F]%' COLLATE Latin1_General_100_BIN2
  THROW 51404, 'MigrationChecksum debe ser un SHA-256 hexadecimal de 64 caracteres.', 1;

IF @AppVersionInput NOT LIKE N'$' + N'(%'
  SET @AppVersion = NULLIF(LTRIM(RTRIM(@AppVersionInput)), N'');

-- Production cutover contract: fixed package, no ambient tenant context.
IF @MigrationId <> N'20260908_production_hospitality_public_scope'
  THROW 51900, 'MigrationId no coincide con el contrato de este script productivo.', 1;
IF @ApplyChangesInput NOT IN (N'0',N'1') OR @ApplyChangesInput LIKE N'$' + N'(%'
  THROW 51901, 'El corte requiere ApplyChanges explicito (0 preview o 1 apply).', 1;
IF @AppVersion IS NULL
  THROW 51902, 'El corte requiere AppVersion identificable y respaldo verificado por el operador.', 1;
IF SESSION_CONTEXT(N'OrionERP.HospitalityCompanyId') IS NOT NULL
   OR SESSION_CONTEXT(N'OrionERP.HospitalitySiteId') IS NOT NULL
  THROW 51903, 'Use una conexion de migracion nueva, sin contexto de Hospedaje.', 1;
SELECT @MigrationId AS ProductionContract,DB_NAME() AS ExpectedTarget,@ApplyChanges AS ApplyChanges,
       @AppVersion AS AppVersion,N'Backup verificado + preview revisado antes de apply' AS RequiredOperatorEvidence;


IF OBJECT_ID(N'orion.SchemaMigration', N'U') IS NULL
   OR OBJECT_ID(N'orion.PublicSite', N'U') IS NULL
   OR OBJECT_ID(N'orion.Site', N'U') IS NULL
  THROW 51405, 'Falta la fundacion de plataforma.', 1;

IF NOT EXISTS
(
  SELECT 1 FROM orion.SchemaMigration
  WHERE MigrationId = N'20260908_production_platform_foundation'
)
   OR NOT EXISTS
(
  SELECT 1 FROM orion.SchemaMigration
  WHERE MigrationId = N'20260908_production_public_site_bindings'
)
  THROW 51406, 'Faltan las migraciones de plataforma y binding productivo.', 1;

DECLARE @ExistingChecksum char(64) =
(
  SELECT Checksum FROM orion.SchemaMigration WHERE MigrationId = @MigrationId
);

IF @ExistingChecksum IS NOT NULL
   AND UPPER(@ExistingChecksum) <> UPPER(@MigrationChecksum)
  THROW 51407, 'El mismo MigrationId ya existe con otro checksum.', 1;

DECLARE @RequiredTables TABLE (SchemaName sysname NOT NULL, TableName sysname NOT NULL);
INSERT @RequiredTables (SchemaName, TableName)
VALUES
  (N'dbo', N'ROOM'),
  (N'dbo', N'ROOM_CALENDAR'),
  (N'dbo', N'RESERVATION'),
  (N'dbo', N'Extra'),
  (N'dbo', N'Reservation_Extra'),
  (N'dbo', N'Reservation_Transacciones'),
  (N'dbo', N'RESERVATION_ATTACHMENT'),
  (N'dbo', N'ReservationAirbnbBreakdown'),
  (N'dbo', N'ExperienceProvider'),
  (N'dbo', N'Experience'),
  (N'dbo', N'ExperiencePackage'),
  (N'dbo', N'ExperienceAddOn'),
  (N'dbo', N'Reservation_Experience'),
  (N'dbo', N'Reservation_ExperienceAddOn');

IF EXISTS
(
  SELECT 1 FROM @RequiredTables requiredTable
  WHERE OBJECT_ID(QUOTENAME(requiredTable.SchemaName) + N'.' + QUOTENAME(requiredTable.TableName), N'U') IS NULL
)
  THROW 51408, 'Falta una tabla requerida por el checkout publico de hospedaje.', 1;

IF COL_LENGTH(N'dbo.RESERVATION', N'RFC') IS NULL
  THROW 51409, 'RESERVATION.RFC es obligatorio para la reconciliacion historica.', 1;

DECLARE @CompanyId bigint;
DECLARE @SiteId bigint;
DECLARE @LegacyRfc varchar(50) = 'OHM191112Q26';

SELECT @CompanyId = publicSite.CompanyId, @SiteId = publicSite.SiteId
FROM orion.PublicSite publicSite
INNER JOIN orion.Company companyInfo
  ON companyInfo.CompanyId = publicSite.CompanyId
INNER JOIN orion.Site siteInfo
  ON siteInfo.CompanyId = publicSite.CompanyId
 AND siteInfo.SiteId = publicSite.SiteId
INNER JOIN orion.Module moduleInfo
  ON moduleInfo.ModuleCode = publicSite.ModuleCode
INNER JOIN orion.CompanyModule assignment
  ON assignment.CompanyId = publicSite.CompanyId
 AND assignment.ModuleCode = publicSite.ModuleCode
INNER JOIN orion.SiteCapability capability
  ON capability.CompanyId = publicSite.CompanyId
 AND capability.SiteId = publicSite.SiteId
 AND capability.ModuleCode = publicSite.ModuleCode
WHERE publicSite.PublicSiteKey = 'bonhomia-main'
  AND publicSite.ModuleCode = 'HOSPITALITY'
  AND publicSite.CanonicalHost = 'bonhomiasuites.com'
  AND publicSite.IsActive = 1
  AND companyInfo.Rfc = @LegacyRfc
  AND companyInfo.IsActive = 1
  AND siteInfo.SiteKey = 'bonhomia-suites'
  AND siteInfo.IsActive = 1
  AND moduleInfo.IsActive = 1
  AND moduleInfo.RequiresSite = 1
  AND assignment.[Status] = 'Enabled'
  AND (assignment.EffectiveFromUtc IS NULL OR assignment.EffectiveFromUtc <= SYSUTCDATETIME())
  AND (assignment.EffectiveToUtc IS NULL OR assignment.EffectiveToUtc > SYSUTCDATETIME())
  AND capability.IsEnabled = 1;

IF @CompanyId IS NULL OR @SiteId IS NULL
  THROW 51410, 'No existe el binding activo y exacto bonhomia-main.', 1;

IF NOT EXISTS
(
  SELECT 1
  FROM orion.Site siteInfo
  WHERE siteInfo.CompanyId = @CompanyId
    AND siteInfo.SiteId = @SiteId
    AND siteInfo.SiteKey = 'bonhomia-suites'
    AND siteInfo.IsActive = 1
)
  THROW 51411, 'La sede bonhomia-suites no coincide con el binding.', 1;

IF OBJECT_ID(N'tempdb..#HospitalityPublicScopeState', N'U') IS NOT NULL
  DROP TABLE #HospitalityPublicScopeState;

CREATE TABLE #HospitalityPublicScopeState
(
  ExpectedDatabase sysname NOT NULL,
  ApplyChanges bit NOT NULL,
  MigrationId nvarchar(200) NOT NULL,
  MigrationChecksum varchar(128) NOT NULL,
  AppVersion nvarchar(64) NULL,
  CompanyId bigint NOT NULL,
  SiteId bigint NOT NULL,
  LegacyRfc varchar(50) NOT NULL
);

INSERT #HospitalityPublicScopeState
  (ExpectedDatabase, ApplyChanges, MigrationId, MigrationChecksum, AppVersion, CompanyId, SiteId, LegacyRfc)
VALUES
  (@ExpectedDatabase, @ApplyChanges, @MigrationId, @MigrationChecksum, @AppVersion, @CompanyId, @SiteId, @LegacyRfc);

BEGIN TRY
  BEGIN TRANSACTION;

  DECLARE @LockResult int;
  EXEC @LockResult = sys.sp_getapplock
    @Resource = N'OrionERP:Hospitality:PublicScope:ProductionCutover20260908',
    @LockMode = N'Exclusive',
    @LockOwner = N'Transaction',
    @LockTimeout = 15000;

  IF @LockResult < 0
    THROW 51412, 'No fue posible obtener el bloqueo de migracion.', 1;

  -- The production inventory was reviewed after its verified backup on 2026-09-08.
  -- This is the existing Bonhomia hospitality installation, not a generic
  -- attribution rule for all rooms in any future database. Owners stay global;
  -- their numeric IDs are never interpreted as CompanyId or fiscal RFC.
  IF @ExistingChecksum IS NULL
  BEGIN
    DECLARE @ReviewedRooms TABLE (RoomType varchar(30),ExpectedCount bigint);
    INSERT @ReviewedRooms VALUES ('ALMACEN',2),('DESCUENTO',1),('INACTIVA',1),('SERVICIO',22),('SUITE',10);
    IF EXISTS
    (SELECT 1 FROM @ReviewedRooms expected
     FULL JOIN (SELECT UPPER(LTRIM(RTRIM(ISNULL(ROOM_TYPE,'')))) AS RoomType,COUNT_BIG(*) AS ActualCount
                FROM dbo.ROOM WITH (UPDLOCK,HOLDLOCK)
                GROUP BY UPPER(LTRIM(RTRIM(ISNULL(ROOM_TYPE,''))))) actual
       ON actual.RoomType=expected.RoomType
     WHERE expected.ExpectedCount IS NULL OR actual.ActualCount IS NULL OR actual.ActualCount<>expected.ExpectedCount)
      THROW 51970, 'El inventario ROOM difiere del conjunto productivo revisado; no se atribuye automaticamente.', 1;
    IF EXISTS
    (SELECT 1 FROM dbo.ROOM WITH (UPDLOCK,HOLDLOCK)
     WHERE OWNER_ID IS NULL OR OWNER_ID NOT IN (4,2146,2191)
        OR (OWNER_ID IN (2146,2191) AND UPPER(LTRIM(RTRIM(ISNULL(ROOM_TYPE,''))))<>'SUITE'))
      THROW 51971, 'El inventario de propietarios difiere del legado revisado; no se reasigna el maestro.', 1;
    IF EXISTS
    (SELECT 1 FROM dbo.ROOM_CALENDAR calendarRow WITH (UPDLOCK,HOLDLOCK)
     LEFT JOIN dbo.ROOM room WITH (UPDLOCK,HOLDLOCK) ON room.ROOM_NAME=calendarRow.ROOM
     WHERE room.ID IS NULL OR UPPER(LTRIM(RTRIM(ISNULL(room.ROOM_TYPE,''))))<>'SUITE')
      THROW 51972, 'Hay calendario fuera de las suites verificadas de Bonhomia.', 1;
    IF EXISTS
    (SELECT 1 FROM dbo.RESERVATION WITH (UPDLOCK,HOLDLOCK)
     WHERE RFC IS NULL OR RFC NOT IN ('OHM191112Q26','SIN_RFC'))
      THROW 51973, 'Hay reservas de otra procedencia; este corte solo reviso OHM/SIN_RFC.', 1;
    IF EXISTS
    (SELECT 1 FROM dbo.RESERVATION reservation WITH (UPDLOCK,HOLDLOCK)
     WHERE reservation.RFC='SIN_RFC'
     AND NOT EXISTS
       (SELECT 1 FROM dbo.Reservation_Transacciones link JOIN dbo.Transacciones payment ON payment.ID=link.TransaccionID
        WHERE link.ReservationID=reservation.ID AND payment.RFC='OHM191112Q26')
     AND NOT EXISTS
       (SELECT 1 FROM dbo.ROOM_CALENDAR calendarRow JOIN dbo.ROOM room ON room.ROOM_NAME=calendarRow.ROOM
        WHERE TRY_CONVERT(int,NULLIF(LTRIM(RTRIM(calendarRow.LOCK_DESCRIPTION)),''))=reservation.ID
          AND UPPER(LTRIM(RTRIM(ISNULL(room.ROOM_TYPE,''))))='SUITE'))
      THROW 51974, 'Hay SIN_RFC sin evidencia OHM/calendario; requiere revisar antes de atribuir.', 1;
    IF EXISTS
    (SELECT 1 FROM dbo.ROOM_CALENDAR calendarRow
     LEFT JOIN dbo.RESERVATION reservation
       ON reservation.ID=TRY_CONVERT(int,NULLIF(LTRIM(RTRIM(calendarRow.LOCK_DESCRIPTION)),''))
     WHERE TRY_CONVERT(int,NULLIF(LTRIM(RTRIM(calendarRow.LOCK_DESCRIPTION)),''))>0
       AND reservation.ID IS NULL)
      THROW 51975, 'Existe referencia positiva a reserva inexistente en calendario.', 1;
    IF (SELECT COUNT_BIG(*) FROM dbo.Extra WITH (UPDLOCK,HOLDLOCK))<>22
       OR (SELECT COUNT_BIG(*) FROM dbo.ExperienceProvider WITH (UPDLOCK,HOLDLOCK))<>1
       OR (SELECT COUNT_BIG(*) FROM dbo.Experience WITH (UPDLOCK,HOLDLOCK))<>1
       OR (SELECT COUNT_BIG(*) FROM dbo.ExperiencePackage WITH (UPDLOCK,HOLDLOCK))<>3
       OR (SELECT COUNT_BIG(*) FROM dbo.ExperienceAddOn WITH (UPDLOCK,HOLDLOCK))<>1
      THROW 51976, 'Catalogos legacy difieren del inventario Bonhomia revisado; no se atribuyen automaticamente.', 1;
    SELECT N'REVIEWED_BONHOMIA_LEGACY_INSTALLATION' AS ProductionProvenance,
           N'No se atribuyen ni editan maestros de propietarios; OWNER_ID no es identidad fiscal' AS OwnerRule;
    SELECT RFC AS LegacyReservationRfc,COUNT_BIG(*) AS ReviewedReservationCount
      FROM dbo.RESERVATION GROUP BY RFC;
    SELECT ROOM_TYPE,OWNER_ID,COUNT_BIG(*) AS ReviewedRoomCount
      FROM dbo.ROOM GROUP BY ROOM_TYPE,OWNER_ID;
  END;


  IF OBJECT_ID(N'orion.HospitalitySiteCustomer', N'U') IS NULL
  BEGIN
    CREATE TABLE orion.HospitalitySiteCustomer
    (
      CompanyId bigint NOT NULL,
      SiteId bigint NOT NULL,
      ClienteId int NOT NULL,
      CreatedAtUtc datetime2(0) NOT NULL
        CONSTRAINT DF_orion_HospitalitySiteCustomer_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
      CONSTRAINT PK_orion_HospitalitySiteCustomer
        PRIMARY KEY (CompanyId, SiteId, ClienteId),
      /* A legacy customer record carries mutable PII. Reusing that same row
         across sites would let one tenant update another tenant's customer.
         A future cross-site match must create/reconcile a separate Cliente. */
      CONSTRAINT UQ_orion_HospitalitySiteCustomer_Cliente
        UNIQUE (ClienteId),
      CONSTRAINT FK_orion_HospitalitySiteCustomer_Site
        FOREIGN KEY (CompanyId, SiteId) REFERENCES orion.Site (CompanyId, SiteId),
      CONSTRAINT FK_orion_HospitalitySiteCustomer_Cliente
        FOREIGN KEY (ClienteId) REFERENCES dbo.Clientes (ID)
    );
  END;

  DECLARE @ScopedTables TABLE (SchemaName sysname NOT NULL, TableName sysname NOT NULL);
  INSERT @ScopedTables (SchemaName, TableName)
  SELECT SchemaName, TableName FROM @RequiredTables;

  IF OBJECT_ID(N'dbo.RESERVATION_DETAIL', N'U') IS NOT NULL
    INSERT @ScopedTables VALUES (N'dbo', N'RESERVATION_DETAIL');
  IF OBJECT_ID(N'dbo.ROOM_CALENDAR_OUTLOOK_SYNC', N'U') IS NOT NULL
    INSERT @ScopedTables VALUES (N'dbo', N'ROOM_CALENDAR_OUTLOOK_SYNC');

  DECLARE @SchemaName sysname;
  DECLARE @TableName sysname;
  DECLARE @Qualified nvarchar(517);
  DECLARE @Sql nvarchar(max);
  DECLARE scope_column_cursor CURSOR LOCAL FAST_FORWARD FOR
    SELECT SchemaName, TableName FROM @ScopedTables;

  OPEN scope_column_cursor;
  FETCH NEXT FROM scope_column_cursor INTO @SchemaName, @TableName;
  WHILE @@FETCH_STATUS = 0
  BEGIN
    SET @Qualified = QUOTENAME(@SchemaName) + N'.' + QUOTENAME(@TableName);
    IF COL_LENGTH(@SchemaName + N'.' + @TableName, N'OrionCompanyId') IS NULL
    BEGIN
      SET @Sql = N'ALTER TABLE ' + @Qualified + N' ADD OrionCompanyId bigint NULL;';
      EXEC sys.sp_executesql @Sql;
    END;
    IF COL_LENGTH(@SchemaName + N'.' + @TableName, N'OrionSiteId') IS NULL
    BEGIN
      SET @Sql = N'ALTER TABLE ' + @Qualified + N' ADD OrionSiteId bigint NULL;';
      EXEC sys.sp_executesql @Sql;
    END;

    IF COL_LENGTH(@SchemaName + N'.' + @TableName, N'OrionCompanyId') IS NULL
       OR COL_LENGTH(@SchemaName + N'.' + @TableName, N'OrionSiteId') IS NULL
      THROW 51413, 'No fue posible agregar un scope compuesto.', 1;

    FETCH NEXT FROM scope_column_cursor INTO @SchemaName, @TableName;
  END;
  CLOSE scope_column_cursor;
  DEALLOCATE scope_column_cursor;

  IF COL_LENGTH(N'dbo.ROOM_CALENDAR', N'RoomId') IS NULL
    ALTER TABLE dbo.ROOM_CALENDAR ADD RoomId int NULL;
  IF COL_LENGTH(N'dbo.ROOM_CALENDAR', N'ReservationId') IS NULL
    ALTER TABLE dbo.ROOM_CALENDAR ADD ReservationId int NULL;
  IF OBJECT_ID(N'dbo.ROOM_CALENDAR_OUTLOOK_SYNC', N'U') IS NOT NULL
     AND COL_LENGTH(N'dbo.ROOM_CALENDAR_OUTLOOK_SYNC', N'RoomId') IS NULL
    ALTER TABLE dbo.ROOM_CALENDAR_OUTLOOK_SYNC ADD RoomId int NULL;

END TRY
BEGIN CATCH
  IF CURSOR_STATUS('local', 'scope_column_cursor') >= 0
    CLOSE scope_column_cursor;
  IF CURSOR_STATUS('local', 'scope_column_cursor') >= -1
    DEALLOCATE scope_column_cursor;
  IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
  IF OBJECT_ID(N'tempdb..#HospitalityPublicScopeState', N'U') IS NOT NULL
    DROP TABLE #HospitalityPublicScopeState;
  THROW;
END CATCH;
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;

IF OBJECT_ID(N'tempdb..#HospitalityPublicScopeState', N'U') IS NULL
BEGIN
  IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
  THROW 51416, 'Se perdio el estado de migracion entre batches.', 1;
END;

IF @@TRANCOUNT <> 1 OR XACT_STATE() <> 1
BEGIN
  IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
  DROP TABLE #HospitalityPublicScopeState;
  THROW 51417, 'La transaccion no sobrevivio al cambio de batch.', 1;
END;

DECLARE @ExpectedDatabase sysname;
DECLARE @ApplyChanges bit;
DECLARE @MigrationId nvarchar(200);
DECLARE @MigrationChecksum varchar(128);
DECLARE @AppVersion nvarchar(64);
DECLARE @CompanyId bigint;
DECLARE @SiteId bigint;
DECLARE @LegacyRfc varchar(50);

SELECT
  @ExpectedDatabase=ExpectedDatabase,
  @ApplyChanges=ApplyChanges,
  @MigrationId=MigrationId,
  @MigrationChecksum=MigrationChecksum,
  @AppVersion=AppVersion,
  @CompanyId=CompanyId,
  @SiteId=SiteId,
  @LegacyRfc=LegacyRfc
FROM #HospitalityPublicScopeState;

IF DB_NAME() <> @ExpectedDatabase
BEGIN
  ROLLBACK TRANSACTION;
  DROP TABLE #HospitalityPublicScopeState;
  THROW 51418, 'La conexion cambio de base entre batches.', 1;
END;

DECLARE @ScopedTables TABLE (SchemaName sysname NOT NULL, TableName sysname NOT NULL);
INSERT @ScopedTables (SchemaName, TableName)
VALUES
  (N'dbo', N'ROOM'),
  (N'dbo', N'ROOM_CALENDAR'),
  (N'dbo', N'RESERVATION'),
  (N'dbo', N'Extra'),
  (N'dbo', N'Reservation_Extra'),
  (N'dbo', N'Reservation_Transacciones'),
  (N'dbo', N'RESERVATION_ATTACHMENT'),
  (N'dbo', N'ReservationAirbnbBreakdown'),
  (N'dbo', N'ExperienceProvider'),
  (N'dbo', N'Experience'),
  (N'dbo', N'ExperiencePackage'),
  (N'dbo', N'ExperienceAddOn'),
  (N'dbo', N'Reservation_Experience'),
  (N'dbo', N'Reservation_ExperienceAddOn');
IF OBJECT_ID(N'dbo.RESERVATION_DETAIL', N'U') IS NOT NULL
  INSERT @ScopedTables VALUES (N'dbo', N'RESERVATION_DETAIL');
IF OBJECT_ID(N'dbo.ROOM_CALENDAR_OUTLOOK_SYNC', N'U') IS NOT NULL
  INSERT @ScopedTables VALUES (N'dbo', N'ROOM_CALENDAR_OUTLOOK_SYNC');

DECLARE @SchemaName sysname;
DECLARE @TableName sysname;
DECLARE @Qualified nvarchar(517);
DECLARE @Sql nvarchar(max);

BEGIN TRY

  /* Only the concrete production room/calendar/catalog population checked in
     the first batch is attributed to the existing Bonhomia installation.
     No owner master or financial transaction is attributed or changed here.
     SIN_RFC requires the payment/calendar evidence checked above. */
  EXEC sys.sp_executesql N'
UPDATE dbo.ROOM
SET OrionCompanyId = @CompanyId, OrionSiteId = @SiteId
WHERE OrionCompanyId IS NULL AND OrionSiteId IS NULL;

UPDATE calendarRow
SET OrionCompanyId = @CompanyId,
    OrionSiteId = @SiteId,
    RoomId = room.ID
FROM dbo.ROOM_CALENDAR calendarRow
INNER JOIN dbo.ROOM room
  ON room.OrionCompanyId = @CompanyId
 AND room.OrionSiteId = @SiteId
 AND room.ROOM_NAME = calendarRow.ROOM
WHERE calendarRow.OrionCompanyId IS NULL AND calendarRow.OrionSiteId IS NULL;

UPDATE reservation
SET OrionCompanyId = @CompanyId, OrionSiteId = @SiteId
FROM dbo.RESERVATION reservation
WHERE reservation.OrionCompanyId IS NULL
  AND reservation.OrionSiteId IS NULL
  AND
  (
       reservation.RFC = @LegacyRfc
    OR
    (
      reservation.RFC = ''SIN_RFC''
      AND
      (
        EXISTS
        (
          SELECT 1
          FROM dbo.Reservation_Transacciones paymentLink
          INNER JOIN dbo.Transacciones payment ON payment.ID = paymentLink.TransaccionID
          WHERE paymentLink.ReservationID = reservation.ID
            AND payment.RFC = @LegacyRfc
        )
        OR EXISTS
        (
          SELECT 1
          FROM dbo.ROOM_CALENDAR calendarEvidence
          INNER JOIN dbo.ROOM roomEvidence
            ON roomEvidence.ID = calendarEvidence.RoomId
           AND roomEvidence.OrionCompanyId = @CompanyId
           AND roomEvidence.OrionSiteId = @SiteId
          WHERE TRY_CONVERT(int, NULLIF(LTRIM(RTRIM(calendarEvidence.LOCK_DESCRIPTION)), '''')) = reservation.ID
            AND UPPER(LTRIM(RTRIM(ISNULL(roomEvidence.ROOM_TYPE, '''')))) = ''SUITE''
        )
      )
    )
  );

UPDATE calendarRow
SET ReservationId = reservation.ID
FROM dbo.ROOM_CALENDAR calendarRow
INNER JOIN dbo.RESERVATION reservation
  ON reservation.ID = TRY_CONVERT(int, NULLIF(LTRIM(RTRIM(calendarRow.LOCK_DESCRIPTION)), ''''))
 AND reservation.OrionCompanyId = calendarRow.OrionCompanyId
 AND reservation.OrionSiteId = calendarRow.OrionSiteId
WHERE calendarRow.OrionCompanyId = @CompanyId
  AND calendarRow.OrionSiteId = @SiteId
  AND calendarRow.ReservationId IS NULL;

UPDATE dbo.Extra SET OrionCompanyId=@CompanyId, OrionSiteId=@SiteId
WHERE OrionCompanyId IS NULL AND OrionSiteId IS NULL;
UPDATE dbo.ExperienceProvider SET OrionCompanyId=@CompanyId, OrionSiteId=@SiteId
WHERE OrionCompanyId IS NULL AND OrionSiteId IS NULL;
UPDATE dbo.Experience SET OrionCompanyId=@CompanyId, OrionSiteId=@SiteId
WHERE OrionCompanyId IS NULL AND OrionSiteId IS NULL;
UPDATE dbo.ExperiencePackage SET OrionCompanyId=@CompanyId, OrionSiteId=@SiteId
WHERE OrionCompanyId IS NULL AND OrionSiteId IS NULL;
UPDATE dbo.ExperienceAddOn SET OrionCompanyId=@CompanyId, OrionSiteId=@SiteId
WHERE OrionCompanyId IS NULL AND OrionSiteId IS NULL;

IF EXISTS
(
  SELECT 1
  FROM dbo.RESERVATION reservation
  INNER JOIN orion.HospitalitySiteCustomer existing
    ON existing.ClienteId=reservation.CLIENTE_ID
  WHERE reservation.OrionCompanyId=@CompanyId
    AND reservation.OrionSiteId=@SiteId
    AND (existing.CompanyId<>@CompanyId OR existing.SiteId<>@SiteId)
)
  THROW 51419, ''Un cliente ya pertenece a otra sede; debe duplicarse o reconciliarse explicitamente.'', 1;

INSERT orion.HospitalitySiteCustomer (CompanyId, SiteId, ClienteId)
SELECT DISTINCT @CompanyId, @SiteId, reservation.CLIENTE_ID
FROM dbo.RESERVATION reservation
WHERE reservation.OrionCompanyId=@CompanyId AND reservation.OrionSiteId=@SiteId
  AND reservation.CLIENTE_ID IS NOT NULL
  AND NOT EXISTS
  (
    SELECT 1 FROM orion.HospitalitySiteCustomer existing
    WHERE existing.ClienteId=reservation.CLIENTE_ID
  );

UPDATE child SET OrionCompanyId=@CompanyId, OrionSiteId=@SiteId
FROM dbo.Reservation_Extra child
INNER JOIN dbo.RESERVATION parent ON parent.ID=child.ReservationID
WHERE parent.OrionCompanyId=@CompanyId AND parent.OrionSiteId=@SiteId
  AND child.OrionCompanyId IS NULL AND child.OrionSiteId IS NULL;

UPDATE child SET OrionCompanyId=@CompanyId, OrionSiteId=@SiteId
FROM dbo.Reservation_Transacciones child
INNER JOIN dbo.RESERVATION parent ON parent.ID=child.ReservationID
WHERE parent.OrionCompanyId=@CompanyId AND parent.OrionSiteId=@SiteId
  AND child.OrionCompanyId IS NULL AND child.OrionSiteId IS NULL;

UPDATE child SET OrionCompanyId=@CompanyId, OrionSiteId=@SiteId
FROM dbo.RESERVATION_ATTACHMENT child
INNER JOIN dbo.RESERVATION parent ON parent.ID=child.ReservationID
WHERE parent.OrionCompanyId=@CompanyId AND parent.OrionSiteId=@SiteId
  AND child.OrionCompanyId IS NULL AND child.OrionSiteId IS NULL;

UPDATE child SET OrionCompanyId=@CompanyId, OrionSiteId=@SiteId
FROM dbo.ReservationAirbnbBreakdown child
INNER JOIN dbo.RESERVATION parent ON parent.ID=child.ReservationID
WHERE parent.OrionCompanyId=@CompanyId AND parent.OrionSiteId=@SiteId
  AND child.OrionCompanyId IS NULL AND child.OrionSiteId IS NULL;

UPDATE child SET OrionCompanyId=@CompanyId, OrionSiteId=@SiteId
FROM dbo.Reservation_Experience child
INNER JOIN dbo.RESERVATION parent ON parent.ID=child.ReservationID
WHERE parent.OrionCompanyId=@CompanyId AND parent.OrionSiteId=@SiteId
  AND child.OrionCompanyId IS NULL AND child.OrionSiteId IS NULL;

UPDATE child SET OrionCompanyId=@CompanyId, OrionSiteId=@SiteId
FROM dbo.Reservation_ExperienceAddOn child
INNER JOIN dbo.Reservation_Experience parent
  ON parent.ReservationExperienceID=child.ReservationExperienceID
WHERE parent.OrionCompanyId=@CompanyId AND parent.OrionSiteId=@SiteId
  AND child.OrionCompanyId IS NULL AND child.OrionSiteId IS NULL;
', N'@CompanyId bigint,@SiteId bigint,@LegacyRfc varchar(50)', @CompanyId, @SiteId, @LegacyRfc;

  IF OBJECT_ID(N'dbo.RESERVATION_DETAIL', N'U') IS NOT NULL
  BEGIN
    EXEC sys.sp_executesql N'
UPDATE detail
SET OrionCompanyId=@CompanyId, OrionSiteId=@SiteId
FROM dbo.RESERVATION_DETAIL detail
INNER JOIN dbo.RESERVATION reservation ON reservation.ID=detail.RESERVATION_ID
WHERE reservation.OrionCompanyId=@CompanyId AND reservation.OrionSiteId=@SiteId
  AND detail.OrionCompanyId IS NULL AND detail.OrionSiteId IS NULL;',
      N'@CompanyId bigint,@SiteId bigint', @CompanyId, @SiteId;
  END;

  IF OBJECT_ID(N'dbo.ROOM_CALENDAR_OUTLOOK_SYNC', N'U') IS NOT NULL
  BEGIN
    EXEC sys.sp_executesql N'
UPDATE syncRow
SET OrionCompanyId=@CompanyId, OrionSiteId=@SiteId, RoomId=room.ID
FROM dbo.ROOM_CALENDAR_OUTLOOK_SYNC syncRow
INNER JOIN dbo.ROOM room
  ON room.OrionCompanyId=@CompanyId AND room.OrionSiteId=@SiteId
 AND room.ROOM_NAME=syncRow.ROOM_NAME
WHERE syncRow.OrionCompanyId IS NULL AND syncRow.OrionSiteId IS NULL;',
      N'@CompanyId bigint,@SiteId bigint', @CompanyId, @SiteId;
  END;

  /* A row is either fully unassigned or has a complete composite scope. */
  DECLARE scope_constraint_cursor CURSOR LOCAL FAST_FORWARD FOR
    SELECT SchemaName, TableName FROM @ScopedTables;
  OPEN scope_constraint_cursor;
  FETCH NEXT FROM scope_constraint_cursor INTO @SchemaName, @TableName;
  WHILE @@FETCH_STATUS = 0
  BEGIN
    DECLARE @ScopeConstraint sysname = LEFT(N'CK_' + @TableName + N'_OrionScopePair', 128);
    SET @Qualified = QUOTENAME(@SchemaName) + N'.' + QUOTENAME(@TableName);
    IF NOT EXISTS
    (
      SELECT 1 FROM sys.check_constraints
      WHERE parent_object_id = OBJECT_ID(@Qualified) AND name = @ScopeConstraint
    )
    BEGIN
      SET @Sql = N'ALTER TABLE ' + @Qualified + N' WITH CHECK ADD CONSTRAINT '
        + QUOTENAME(@ScopeConstraint)
        + N' CHECK ((OrionCompanyId IS NULL AND OrionSiteId IS NULL) OR '
        + N'(OrionCompanyId IS NOT NULL AND OrionSiteId IS NOT NULL));';
      EXEC sys.sp_executesql @Sql;
    END;

    DECLARE @SiteFk sysname = LEFT(N'FK_' + @TableName + N'_OrionSite', 128);
    IF NOT EXISTS
    (
      SELECT 1 FROM sys.foreign_keys
      WHERE parent_object_id = OBJECT_ID(@Qualified) AND name = @SiteFk
    )
    BEGIN
      SET @Sql = N'ALTER TABLE ' + @Qualified + N' WITH CHECK ADD CONSTRAINT '
        + QUOTENAME(@SiteFk)
        + N' FOREIGN KEY (OrionCompanyId, OrionSiteId) '
        + N'REFERENCES orion.Site (CompanyId, SiteId);';
      EXEC sys.sp_executesql @Sql;
    END;

    FETCH NEXT FROM scope_constraint_cursor INTO @SchemaName, @TableName;
  END;
  CLOSE scope_constraint_cursor;
  DEALLOCATE scope_constraint_cursor;

  /* Composite alternate keys make child ownership enforceable by SQL Server. */
  IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.ROOM') AND name=N'UX_ROOM_OrionScope_Id')
    CREATE UNIQUE INDEX UX_ROOM_OrionScope_Id ON dbo.ROOM (OrionCompanyId, OrionSiteId, ID);
  IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.ROOM') AND name=N'UX_ROOM_OrionScope_Name')
    CREATE UNIQUE INDEX UX_ROOM_OrionScope_Name ON dbo.ROOM (OrionCompanyId, OrionSiteId, ROOM_NAME)
      WHERE OrionCompanyId IS NOT NULL AND OrionSiteId IS NOT NULL;
  IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.RESERVATION') AND name=N'UX_RESERVATION_OrionScope_Id')
    CREATE UNIQUE INDEX UX_RESERVATION_OrionScope_Id ON dbo.RESERVATION (OrionCompanyId, OrionSiteId, ID);
  IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.Extra') AND name=N'UX_Extra_OrionScope_Id')
    CREATE UNIQUE INDEX UX_Extra_OrionScope_Id ON dbo.Extra (OrionCompanyId, OrionSiteId, ExtraID);
  IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.Experience') AND name=N'UX_Experience_OrionScope_Id')
    CREATE UNIQUE INDEX UX_Experience_OrionScope_Id ON dbo.Experience (OrionCompanyId, OrionSiteId, ExperienceID);
  IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.ExperiencePackage') AND name=N'UX_ExperiencePackage_OrionScope_Id')
    CREATE UNIQUE INDEX UX_ExperiencePackage_OrionScope_Id ON dbo.ExperiencePackage (OrionCompanyId, OrionSiteId, ExperiencePackageID);
  IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.ExperienceAddOn') AND name=N'UX_ExperienceAddOn_OrionScope_Id')
    CREATE UNIQUE INDEX UX_ExperienceAddOn_OrionScope_Id ON dbo.ExperienceAddOn (OrionCompanyId, OrionSiteId, ExperienceAddOnID);
  IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.Reservation_Experience') AND name=N'UX_ReservationExperience_OrionScope_Id')
    CREATE UNIQUE INDEX UX_ReservationExperience_OrionScope_Id
      ON dbo.Reservation_Experience (OrionCompanyId, OrionSiteId, ReservationExperienceID);

  /* Replace only the known legacy global room/date constraint. */
  IF EXISTS
  (
    SELECT 1 FROM sys.key_constraints
    WHERE parent_object_id=OBJECT_ID(N'dbo.ROOM_CALENDAR')
      AND name=N'UQ_ROOM_CALENDAR_ROOM_ROOM_DATE'
  )
    ALTER TABLE dbo.ROOM_CALENDAR DROP CONSTRAINT UQ_ROOM_CALENDAR_ROOM_ROOM_DATE;

  IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.ROOM_CALENDAR') AND name=N'UX_ROOM_CALENDAR_OrionScope_Room_Date')
    CREATE UNIQUE INDEX UX_ROOM_CALENDAR_OrionScope_Room_Date
      ON dbo.ROOM_CALENDAR (OrionCompanyId, OrionSiteId, RoomId, ROOM_DATE)
      WHERE OrionCompanyId IS NOT NULL AND OrionSiteId IS NOT NULL AND RoomId IS NOT NULL;
  IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.ROOM_CALENDAR') AND name=N'UX_ROOM_CALENDAR_Unscoped_Room_Date')
    CREATE UNIQUE INDEX UX_ROOM_CALENDAR_Unscoped_Room_Date
      ON dbo.ROOM_CALENDAR (ROOM, ROOM_DATE)
      WHERE OrionCompanyId IS NULL AND OrionSiteId IS NULL;

  IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE parent_object_id=OBJECT_ID(N'dbo.ROOM_CALENDAR') AND name=N'FK_ROOM_CALENDAR_OrionRoom')
    ALTER TABLE dbo.ROOM_CALENDAR WITH CHECK ADD CONSTRAINT FK_ROOM_CALENDAR_OrionRoom
      FOREIGN KEY (OrionCompanyId, OrionSiteId, RoomId)
      REFERENCES dbo.ROOM (OrionCompanyId, OrionSiteId, ID);
  IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE parent_object_id=OBJECT_ID(N'dbo.ROOM_CALENDAR') AND name=N'FK_ROOM_CALENDAR_OrionReservation')
    ALTER TABLE dbo.ROOM_CALENDAR WITH CHECK ADD CONSTRAINT FK_ROOM_CALENDAR_OrionReservation
      FOREIGN KEY (OrionCompanyId, OrionSiteId, ReservationId)
      REFERENCES dbo.RESERVATION (OrionCompanyId, OrionSiteId, ID);
  IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE parent_object_id=OBJECT_ID(N'dbo.RESERVATION') AND name=N'FK_RESERVATION_HospitalitySiteCustomer')
    ALTER TABLE dbo.RESERVATION WITH CHECK ADD CONSTRAINT FK_RESERVATION_HospitalitySiteCustomer
      FOREIGN KEY (OrionCompanyId, OrionSiteId, CLIENTE_ID)
      REFERENCES orion.HospitalitySiteCustomer (CompanyId, SiteId, ClienteId);

  IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE parent_object_id=OBJECT_ID(N'dbo.Reservation_Extra') AND name=N'FK_ReservationExtra_OrionReservation')
    ALTER TABLE dbo.Reservation_Extra WITH CHECK ADD CONSTRAINT FK_ReservationExtra_OrionReservation
      FOREIGN KEY (OrionCompanyId, OrionSiteId, ReservationID)
      REFERENCES dbo.RESERVATION (OrionCompanyId, OrionSiteId, ID);
  IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE parent_object_id=OBJECT_ID(N'dbo.Reservation_Extra') AND name=N'FK_ReservationExtra_OrionExtra')
    ALTER TABLE dbo.Reservation_Extra WITH CHECK ADD CONSTRAINT FK_ReservationExtra_OrionExtra
      FOREIGN KEY (OrionCompanyId, OrionSiteId, ExtraID)
      REFERENCES dbo.Extra (OrionCompanyId, OrionSiteId, ExtraID);
  IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE parent_object_id=OBJECT_ID(N'dbo.Reservation_Transacciones') AND name=N'FK_ReservationTransactions_OrionReservation')
    ALTER TABLE dbo.Reservation_Transacciones WITH CHECK ADD CONSTRAINT FK_ReservationTransactions_OrionReservation
      FOREIGN KEY (OrionCompanyId, OrionSiteId, ReservationID)
      REFERENCES dbo.RESERVATION (OrionCompanyId, OrionSiteId, ID);
  IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE parent_object_id=OBJECT_ID(N'dbo.RESERVATION_ATTACHMENT') AND name=N'FK_ReservationAttachment_OrionReservation')
    ALTER TABLE dbo.RESERVATION_ATTACHMENT WITH CHECK ADD CONSTRAINT FK_ReservationAttachment_OrionReservation
      FOREIGN KEY (OrionCompanyId, OrionSiteId, ReservationID)
      REFERENCES dbo.RESERVATION (OrionCompanyId, OrionSiteId, ID);
  IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE parent_object_id=OBJECT_ID(N'dbo.ReservationAirbnbBreakdown') AND name=N'FK_ReservationAirbnb_OrionReservation')
    ALTER TABLE dbo.ReservationAirbnbBreakdown WITH CHECK ADD CONSTRAINT FK_ReservationAirbnb_OrionReservation
      FOREIGN KEY (OrionCompanyId, OrionSiteId, ReservationID)
      REFERENCES dbo.RESERVATION (OrionCompanyId, OrionSiteId, ID);

  IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE parent_object_id=OBJECT_ID(N'dbo.Reservation_Experience') AND name=N'FK_ReservationExperience_OrionReservation')
    ALTER TABLE dbo.Reservation_Experience WITH CHECK ADD CONSTRAINT FK_ReservationExperience_OrionReservation
      FOREIGN KEY (OrionCompanyId, OrionSiteId, ReservationID)
      REFERENCES dbo.RESERVATION (OrionCompanyId, OrionSiteId, ID);
  IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE parent_object_id=OBJECT_ID(N'dbo.Reservation_Experience') AND name=N'FK_ReservationExperience_OrionExperience')
    ALTER TABLE dbo.Reservation_Experience WITH CHECK ADD CONSTRAINT FK_ReservationExperience_OrionExperience
      FOREIGN KEY (OrionCompanyId, OrionSiteId, ExperienceID)
      REFERENCES dbo.Experience (OrionCompanyId, OrionSiteId, ExperienceID);
  IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE parent_object_id=OBJECT_ID(N'dbo.Reservation_Experience') AND name=N'FK_ReservationExperience_OrionPackage')
    ALTER TABLE dbo.Reservation_Experience WITH CHECK ADD CONSTRAINT FK_ReservationExperience_OrionPackage
      FOREIGN KEY (OrionCompanyId, OrionSiteId, ExperiencePackageID)
      REFERENCES dbo.ExperiencePackage (OrionCompanyId, OrionSiteId, ExperiencePackageID);
  IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE parent_object_id=OBJECT_ID(N'dbo.Reservation_ExperienceAddOn') AND name=N'FK_ReservationExperienceAddOn_OrionParent')
    ALTER TABLE dbo.Reservation_ExperienceAddOn WITH CHECK ADD CONSTRAINT FK_ReservationExperienceAddOn_OrionParent
      FOREIGN KEY (OrionCompanyId, OrionSiteId, ReservationExperienceID)
      REFERENCES dbo.Reservation_Experience (OrionCompanyId, OrionSiteId, ReservationExperienceID);
  IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE parent_object_id=OBJECT_ID(N'dbo.Reservation_ExperienceAddOn') AND name=N'FK_ReservationExperienceAddOn_OrionCatalog')
    ALTER TABLE dbo.Reservation_ExperienceAddOn WITH CHECK ADD CONSTRAINT FK_ReservationExperienceAddOn_OrionCatalog
      FOREIGN KEY (OrionCompanyId, OrionSiteId, ExperienceAddOnID)
      REFERENCES dbo.ExperienceAddOn (OrionCompanyId, OrionSiteId, ExperienceAddOnID);

  IF EXISTS
  (
    SELECT 1 FROM dbo.ROOM_CALENDAR
    WHERE OrionCompanyId=@CompanyId AND OrionSiteId=@SiteId AND RoomId IS NULL
  )
    THROW 51414, 'Hay calendario atribuido sin RoomId reconciliado.', 1;

  IF EXISTS
  (
    SELECT 1
    FROM dbo.Reservation_Extra child
    INNER JOIN dbo.RESERVATION parent ON parent.ID=child.ReservationID
    WHERE child.OrionCompanyId IS NOT NULL
      AND (child.OrionCompanyId<>parent.OrionCompanyId OR child.OrionSiteId<>parent.OrionSiteId)
  )
    THROW 51415, 'Un extra de reservacion contradice el scope de su padre.', 1;

  SELECT
    @CompanyId AS CompanyId,
    @SiteId AS SiteId,
    (SELECT COUNT_BIG(*) FROM dbo.ROOM WHERE OrionCompanyId=@CompanyId AND OrionSiteId=@SiteId) AS ScopedRooms,
    (SELECT COUNT_BIG(*) FROM dbo.ROOM_CALENDAR WHERE OrionCompanyId=@CompanyId AND OrionSiteId=@SiteId) AS ScopedCalendarRows,
    (SELECT COUNT_BIG(*) FROM dbo.RESERVATION WHERE OrionCompanyId=@CompanyId AND OrionSiteId=@SiteId) AS ScopedReservations,
    (SELECT COUNT_BIG(*) FROM dbo.RESERVATION WHERE OrionCompanyId IS NULL AND OrionSiteId IS NULL) AS UnscopedReservations,
    (SELECT COUNT_BIG(*) FROM orion.HospitalitySiteCustomer WHERE CompanyId=@CompanyId AND SiteId=@SiteId) AS ScopedCustomers;

  SELECT reservation.ID, reservation.STATUS, reservation.RFC,
         N'SIN_EVIDENCIA_DE_EMPRESA_SEDE' AS ReconciliationStatus
  FROM dbo.RESERVATION reservation
  WHERE reservation.OrionCompanyId IS NULL AND reservation.OrionSiteId IS NULL
  ORDER BY reservation.ID;

  IF @ApplyChanges = 1
  BEGIN
    IF NOT EXISTS (SELECT 1 FROM orion.SchemaMigration WHERE MigrationId=@MigrationId)
    BEGIN
      INSERT orion.SchemaMigration
        (MigrationId, Checksum, AppliedBy, AppVersion, DatabaseName)
      VALUES
        (@MigrationId, @MigrationChecksum,
         COALESCE(CONVERT(nvarchar(256), SESSION_CONTEXT(N'OrionERP.UserName')), CONVERT(nvarchar(256), ORIGINAL_LOGIN())),
         @AppVersion, DB_NAME());
    END;

    COMMIT TRANSACTION;
    SELECT N'APLICADO_CORTE_20260908' AS Estado, DB_NAME() AS BaseDatos, @MigrationId AS MigrationId;
  END
  ELSE
  BEGIN
    ROLLBACK TRANSACTION;
    SELECT N'VALIDADO_SIN_CAMBIOS' AS Estado, DB_NAME() AS BaseDatos, @MigrationId AS MigrationId;
  END;

  DROP TABLE #HospitalityPublicScopeState;
END TRY
BEGIN CATCH
  IF CURSOR_STATUS('local', 'scope_constraint_cursor') >= 0
    CLOSE scope_constraint_cursor;
  IF CURSOR_STATUS('local', 'scope_constraint_cursor') >= -1
    DEALLOCATE scope_constraint_cursor;
  IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
  IF OBJECT_ID(N'tempdb..#HospitalityPublicScopeState', N'U') IS NOT NULL
    DROP TABLE #HospitalityPublicScopeState;
  THROW;
END CATCH;
