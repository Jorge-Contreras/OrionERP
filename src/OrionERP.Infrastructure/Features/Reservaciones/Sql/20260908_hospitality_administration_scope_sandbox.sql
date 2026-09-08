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
    THROW 51800, 'ApplyChanges debe ser 0 o 1.', 1;
  SET @ApplyChanges = CONVERT(bit, @ApplyChangesInput);
END;

IF @ExpectedDatabase LIKE N'$' + N'(%' OR @ExpectedDatabase <> N'Orion_Sandbox'
  THROW 51801, 'Esta migracion admite exclusivamente Orion_Sandbox.', 1;
IF DB_NAME() <> N'Orion_Sandbox'
  THROW 51802, 'La conexion no apunta a Orion_Sandbox.', 1;
IF @MigrationId LIKE N'$' + N'(%'
   OR NULLIF(LTRIM(RTRIM(@MigrationId)), N'') IS NULL
   OR LEN(@MigrationId) > 200
  THROW 51803, 'MigrationId es obligatorio y debe provenir del manifiesto.', 1;
IF @MigrationChecksum LIKE '$' + '(%'
   OR LEN(@MigrationChecksum) <> 64
   OR @MigrationChecksum LIKE '%[^0-9A-F]%' COLLATE Latin1_General_100_BIN2
  THROW 51804, 'MigrationChecksum debe ser un SHA-256 hexadecimal de 64 caracteres.', 1;
IF @AppVersionInput NOT LIKE N'$' + N'(%'
  SET @AppVersion = NULLIF(LTRIM(RTRIM(@AppVersionInput)), N'');

IF OBJECT_ID(N'orion.SchemaMigration', N'U') IS NULL
   OR OBJECT_ID(N'dbo.RESERVATION', N'U') IS NULL
  THROW 51805, 'Falta la fundacion de plataforma o dbo.RESERVATION.', 1;
IF NOT EXISTS
(
  SELECT 1 FROM orion.SchemaMigration
  WHERE MigrationId = N'20260903_hospitality_public_scope_sandbox'
)
   OR NOT EXISTS
(
  SELECT 1 FROM orion.SchemaMigration
  WHERE MigrationId = N'20260905_hospitality_legal_consent_sandbox'
)
  THROW 51806, 'Faltan el aislamiento de Hospedaje o la activacion versionada de presentaciones.', 1;

DECLARE @ExistingChecksum char(64) =
(
  SELECT Checksum FROM orion.SchemaMigration WHERE MigrationId = @MigrationId
);
IF @ExistingChecksum IS NOT NULL
   AND UPPER(@ExistingChecksum) <> UPPER(@MigrationChecksum)
  THROW 51807, 'El mismo MigrationId ya existe con otro checksum.', 1;


/* No privileged-user/job bypass. NULL context sees no rows. Existing unassigned
   records remain unassigned. The trusted application binds every connection. */
IF @ExistingChecksum IS NOT NULL
BEGIN
  IF NOT EXISTS (SELECT 1 FROM sys.security_policies WHERE object_id=OBJECT_ID(N'orion.HospitalityScopePolicy') AND is_enabled=1 AND is_schema_bound=1)
    THROW 51808, 'La politica registrada no esta habilitada.', 1;
  SELECT N'YA_APLICADO_SOLO_SANDBOX' AS Estado, @MigrationId AS MigrationId;
  RETURN;
END;
IF OBJECT_ID(N'orion.HospitalityScopePolicy') IS NOT NULL OR OBJECT_ID(N'orion.fn_HospitalityScopePredicate') IS NOT NULL
  THROW 51809, 'Existe una politica o funcion no registrada; requiere reconciliacion explicita.', 1;

DECLARE @Targets TABLE (SchemaName sysname, TableName sysname, CompanyColumn sysname, SiteColumn sysname);
INSERT @Targets VALUES
(N'dbo',N'ROOM',N'OrionCompanyId',N'OrionSiteId'),
(N'dbo',N'ROOM_CALENDAR',N'OrionCompanyId',N'OrionSiteId'),
(N'dbo',N'RESERVATION',N'OrionCompanyId',N'OrionSiteId'),
(N'dbo',N'Extra',N'OrionCompanyId',N'OrionSiteId'),
(N'dbo',N'Reservation_Extra',N'OrionCompanyId',N'OrionSiteId'),
(N'dbo',N'Reservation_Transacciones',N'OrionCompanyId',N'OrionSiteId'),
(N'dbo',N'RESERVATION_ATTACHMENT',N'OrionCompanyId',N'OrionSiteId'),
(N'dbo',N'ReservationAirbnbBreakdown',N'OrionCompanyId',N'OrionSiteId'),
(N'dbo',N'ExperienceProvider',N'OrionCompanyId',N'OrionSiteId'),
(N'dbo',N'Experience',N'OrionCompanyId',N'OrionSiteId'),
(N'dbo',N'ExperiencePackage',N'OrionCompanyId',N'OrionSiteId'),
(N'dbo',N'ExperienceAddOn',N'OrionCompanyId',N'OrionSiteId'),
(N'dbo',N'Reservation_Experience',N'OrionCompanyId',N'OrionSiteId'),
(N'dbo',N'Reservation_ExperienceAddOn',N'OrionCompanyId',N'OrionSiteId'),
(N'orion',N'HospitalitySiteCustomer',N'CompanyId',N'SiteId');
IF OBJECT_ID(N'dbo.RESERVATION_DETAIL',N'U') IS NOT NULL
  INSERT @Targets VALUES (N'dbo',N'RESERVATION_DETAIL',N'OrionCompanyId',N'OrionSiteId');
IF OBJECT_ID(N'dbo.ROOM_CALENDAR_OUTLOOK_SYNC',N'U') IS NOT NULL
  INSERT @Targets VALUES (N'dbo',N'ROOM_CALENDAR_OUTLOOK_SYNC',N'OrionCompanyId',N'OrionSiteId');
IF EXISTS (SELECT 1 FROM @Targets WHERE COL_LENGTH(SchemaName+N'.'+TableName,CompanyColumn) IS NULL OR COL_LENGTH(SchemaName+N'.'+TableName,SiteColumn) IS NULL)
  THROW 51810, 'Falta el alcance compuesto de una tabla de Hospedaje.', 1;
IF EXISTS (SELECT 1 FROM sys.security_predicates p JOIN @Targets t ON p.target_object_id=OBJECT_ID(t.SchemaName+N'.'+t.TableName))
  THROW 51811, 'Una tabla ya tiene predicados; no se reemplazan politicas ajenas.', 1;
IF OBJECT_ID(N'dbo.BusinessPartner',N'U') IS NULL OR COL_LENGTH(N'dbo.BusinessPartner',N'Id') IS NULL
  THROW 51812, 'Falta el maestro de perfiles fiscales.', 1;
IF OBJECT_ID(N'orion.HospitalityFiscalCustomer',N'U') IS NOT NULL
  THROW 51813, 'Existe un mapping fiscal sin registrar; requiere reconciliacion explicita.', 1;

BEGIN TRY
  BEGIN TRANSACTION;
  DECLARE @LockResult int;
  EXEC @LockResult=sys.sp_getapplock @Resource=N'OrionERP:Hospitality:AdministrationScope:Sandbox', @LockMode=N'Exclusive', @LockOwner=N'Transaction', @LockTimeout=15000;
  IF @LockResult < 0 THROW 51814, 'No fue posible obtener el bloqueo de migracion.', 1;

  CREATE TABLE orion.HospitalityFiscalCustomer
  (
    CompanyId bigint NOT NULL,
    SiteId bigint NOT NULL,
    BusinessPartnerId int NOT NULL,
    CreatedAtUtc datetime2(0) NOT NULL CONSTRAINT DF_HospitalityFiscalCustomer_CreatedAtUtc DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_HospitalityFiscalCustomer PRIMARY KEY (CompanyId,SiteId,BusinessPartnerId),
    CONSTRAINT UQ_HospitalityFiscalCustomer_BusinessPartner UNIQUE (BusinessPartnerId),
    CONSTRAINT FK_HospitalityFiscalCustomer_Site FOREIGN KEY (CompanyId,SiteId) REFERENCES orion.Site (CompanyId,SiteId),
    CONSTRAINT FK_HospitalityFiscalCustomer_BusinessPartner FOREIGN KEY (BusinessPartnerId) REFERENCES dbo.BusinessPartner (Id)
  );
  INSERT @Targets VALUES (N'orion',N'HospitalityFiscalCustomer',N'CompanyId',N'SiteId');

  /* Preserve contradictory historical links for explicit reconciliation.
     The payment predicate hides them; it never assigns or deletes them. */
  SELECT payment.RFC AS PaymentRfc,companyInfo.Rfc AS ScopedCompanyRfc,COUNT_BIG(*) AS ContradictoryPaymentLinks
  FROM dbo.Reservation_Transacciones link
  JOIN dbo.Transacciones payment ON payment.ID=link.TransaccionID
  JOIN orion.Company companyInfo ON companyInfo.CompanyId=link.OrionCompanyId
  WHERE payment.RFC<>companyInfo.Rfc OR payment.RFC IS NULL
  GROUP BY payment.RFC,companyInfo.Rfc;
  DECLARE @PaymentForeignKey sysname;
  SELECT @PaymentForeignKey=fk.name FROM sys.foreign_keys fk
  WHERE fk.parent_object_id=OBJECT_ID(N'dbo.Reservation_Transacciones')
    AND fk.referenced_object_id=OBJECT_ID(N'dbo.Transacciones') AND fk.delete_referential_action<>0;
  IF @PaymentForeignKey IS NOT NULL
  BEGIN
    DECLARE @DropPaymentForeignKey nvarchar(max)=N'ALTER TABLE dbo.Reservation_Transacciones DROP CONSTRAINT '+QUOTENAME(@PaymentForeignKey);
    EXEC sys.sp_executesql @DropPaymentForeignKey;
  END;
  ALTER TABLE dbo.Reservation_Transacciones WITH CHECK ADD CONSTRAINT FK_ReservationTransactions_PaymentRestrict
    FOREIGN KEY (TransaccionID) REFERENCES dbo.Transacciones(ID);

  EXEC(N'CREATE OR ALTER TRIGGER dbo.TR_RoomCalendar_HospitalityScope ON dbo.ROOM_CALENDAR AFTER INSERT,UPDATE AS
  BEGIN
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1 FROM inserted i
      LEFT JOIN dbo.ROOM room ON room.ID=i.RoomId AND room.OrionCompanyId=i.OrionCompanyId AND room.OrionSiteId=i.OrionSiteId AND room.ROOM_NAME=i.ROOM
      WHERE room.ID IS NULL)
      THROW 51820,''La habitacion del calendario no pertenece a la sede seleccionada.'',1;
    IF EXISTS (SELECT 1 FROM inserted i
      LEFT JOIN dbo.RESERVATION r ON r.ID=TRY_CONVERT(int,NULLIF(LTRIM(RTRIM(i.LOCK_DESCRIPTION)),'''')) AND r.OrionCompanyId=i.OrionCompanyId AND r.OrionSiteId=i.OrionSiteId
      WHERE TRY_CONVERT(int,NULLIF(LTRIM(RTRIM(i.LOCK_DESCRIPTION)),'''')) IS NOT NULL AND r.ID IS NULL)
      THROW 51821,''La reserva del calendario no pertenece a la sede seleccionada.'',1;
  END;');

  /* Legacy global templates and accounting accounts have no evidence-backed
     site mapping. Preserve signatures, fail before any global write. */
  EXEC(N'CREATE OR ALTER PROCEDURE dbo.CreateActividadForReservation
    @Tipo_OrdenCalendarID int,@Fecha_Inicio datetime2,@Fecha_Final datetime2,@Presupuesto money,
    @Asignacion int=1,@Descripcion varchar(800)=''LIMPIEZA'',@Tipo_Orden varchar(200),@Cuenta_SAT int,@Suite varchar(200)=''''
  AS BEGIN SET NOCOUNT ON; THROW 51822,''Configure plantillas de actividad por empresa y sede antes de usar este procedimiento heredado.'',1; END;');
  EXEC(N'CREATE OR ALTER PROCEDURE dbo.CreateTransaccionesForRoom
    @StartDate date,@EndDate date,@Room varchar(50),@RFC varchar(50)
  AS BEGIN SET NOCOUNT ON; THROW 51823,''Configure cuentas y categorias de arrendamiento por empresa y sede antes de usar este procedimiento heredado.'',1; END;');

  /* Complement relationships from the public-scope migration. */
  CREATE UNIQUE INDEX UX_ExperienceProvider_OrionScope_Id ON dbo.ExperienceProvider(OrionCompanyId,OrionSiteId,ExperienceProviderID);
  ALTER TABLE dbo.Experience WITH CHECK ADD CONSTRAINT FK_Experience_OrionProvider
    FOREIGN KEY (OrionCompanyId,OrionSiteId,ExperienceProviderID) REFERENCES dbo.ExperienceProvider(OrionCompanyId,OrionSiteId,ExperienceProviderID);
  ALTER TABLE dbo.ExperiencePackage WITH CHECK ADD CONSTRAINT FK_ExperiencePackage_OrionExperience
    FOREIGN KEY (OrionCompanyId,OrionSiteId,ExperienceID) REFERENCES dbo.Experience(OrionCompanyId,OrionSiteId,ExperienceID);
  ALTER TABLE dbo.ExperienceAddOn WITH CHECK ADD CONSTRAINT FK_ExperienceAddOn_OrionExperience
    FOREIGN KEY (OrionCompanyId,OrionSiteId,ExperienceID) REFERENCES dbo.Experience(OrionCompanyId,OrionSiteId,ExperienceID);
  IF OBJECT_ID(N'dbo.RESERVATION_DETAIL',N'U') IS NOT NULL
    EXEC(N'ALTER TABLE dbo.RESERVATION_DETAIL WITH CHECK ADD CONSTRAINT FK_ReservationDetail_OrionReservation FOREIGN KEY (OrionCompanyId,OrionSiteId,RESERVATION_ID) REFERENCES dbo.RESERVATION(OrionCompanyId,OrionSiteId,ID);');
  IF OBJECT_ID(N'dbo.ROOM_CALENDAR_OUTLOOK_SYNC',N'U') IS NOT NULL
  BEGIN
    EXEC(N'ALTER TABLE dbo.ROOM_CALENDAR_OUTLOOK_SYNC WITH CHECK ADD CONSTRAINT FK_OutlookSync_OrionRoom FOREIGN KEY (OrionCompanyId,OrionSiteId,RoomId) REFERENCES dbo.ROOM(OrionCompanyId,OrionSiteId,ID);
    SELECT COUNT_BIG(*) AS UnresolvedOutlookReservationLinks FROM dbo.ROOM_CALENDAR_OUTLOOK_SYNC s
    LEFT JOIN dbo.RESERVATION r ON r.ID=s.RESERVATION_ID AND r.OrionCompanyId=s.OrionCompanyId AND r.OrionSiteId=s.OrionSiteId
    WHERE s.RESERVATION_ID IS NOT NULL AND r.ID IS NULL;');
    EXEC(N'CREATE OR ALTER TRIGGER dbo.TR_OutlookSync_HospitalityScope ON dbo.ROOM_CALENDAR_OUTLOOK_SYNC AFTER INSERT,UPDATE AS
    BEGIN
      SET NOCOUNT ON;
      IF EXISTS (SELECT 1 FROM inserted i LEFT JOIN dbo.RESERVATION r ON r.ID=i.RESERVATION_ID AND r.OrionCompanyId=i.OrionCompanyId AND r.OrionSiteId=i.OrionSiteId
        WHERE i.RESERVATION_ID IS NOT NULL AND r.ID IS NULL)
        THROW 51824,''El mapping de Outlook no pertenece a una reserva de esta sede.'',1;
    END;');
  END;

  /* Codes may repeat in another company/site. NULL tuples remain unique. */
  IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.ExperienceProvider') AND name=N'UX_ExperienceProvider_Code')
    DROP INDEX UX_ExperienceProvider_Code ON dbo.ExperienceProvider;
  IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.Experience') AND name=N'UX_Experience_Code')
    DROP INDEX UX_Experience_Code ON dbo.Experience;
  CREATE UNIQUE INDEX UX_ExperienceProvider_OrionScope_Code ON dbo.ExperienceProvider(OrionCompanyId,OrionSiteId,Code);
  CREATE UNIQUE INDEX UX_Experience_OrionScope_Code ON dbo.Experience(OrionCompanyId,OrionSiteId,Code);

  DECLARE @Schema sysname,@Table sysname,@Company sysname,@Site sysname,@Qualified nvarchar(517),@Sql nvarchar(max),@Predicate nvarchar(1000),@Policy nvarchar(max)=N'CREATE SECURITY POLICY orion.HospitalityScopePolicy ';
  DECLARE scope_cursor CURSOR LOCAL FAST_FORWARD FOR SELECT SchemaName,TableName,CompanyColumn,SiteColumn FROM @Targets;
  OPEN scope_cursor;
  FETCH NEXT FROM scope_cursor INTO @Schema,@Table,@Company,@Site;
  WHILE @@FETCH_STATUS=0
  BEGIN
    SET @Qualified=QUOTENAME(@Schema)+N'.'+QUOTENAME(@Table);
    IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(@Qualified) AND name IN (@Company,@Site) AND default_object_id<>0)
      THROW 51815, 'Scope tiene defaults previos; requiere reconciliacion explicita.', 1;
    SET @Sql=N'ALTER TABLE '+@Qualified+N' ADD CONSTRAINT '+QUOTENAME(N'DF_'+@Table+N'_HospitalityCompany')+N' DEFAULT (TRY_CONVERT(bigint,SESSION_CONTEXT(N''OrionERP.HospitalityCompanyId''))) FOR '+QUOTENAME(@Company)+N';
    ALTER TABLE '+@Qualified+N' ADD CONSTRAINT '+QUOTENAME(N'DF_'+@Table+N'_HospitalitySite')+N' DEFAULT (TRY_CONVERT(bigint,SESSION_CONTEXT(N''OrionERP.HospitalitySiteId''))) FOR '+QUOTENAME(@Site)+N';
    SELECT N'''+@Schema+N'.'+@Table+N''' AS ScopedTable,COUNT_BIG(*) AS TotalRows,
      COALESCE(SUM(CASE WHEN '+QUOTENAME(@Company)+N' IS NULL OR '+QUOTENAME(@Site)+N' IS NULL THEN CONVERT(bigint,1) ELSE CONVERT(bigint,0) END),0) AS UnassignedRows FROM '+@Qualified+N';';
    EXEC sys.sp_executesql @Sql;
    SET @Predicate=CASE WHEN @Table=N'Reservation_Transacciones' THEN N'orion.fn_HospitalityPaymentScopePredicate('+QUOTENAME(@Company)+N','+QUOTENAME(@Site)+N',TransaccionID)' ELSE N'orion.fn_HospitalityScopePredicate('+QUOTENAME(@Company)+N','+QUOTENAME(@Site)+N')' END;
    SET @Policy+=N'ADD FILTER PREDICATE '+@Predicate+N' ON '+@Qualified+N',
      ADD BLOCK PREDICATE '+@Predicate+N' ON '+@Qualified+N' AFTER INSERT,
      ADD BLOCK PREDICATE '+@Predicate+N' ON '+@Qualified+N' AFTER UPDATE,';
    FETCH NEXT FROM scope_cursor INTO @Schema,@Table,@Company,@Site;
  END;
  CLOSE scope_cursor;
  DEALLOCATE scope_cursor;
  EXEC(N'CREATE FUNCTION orion.fn_HospitalityScopePredicate(@CompanyId bigint,@SiteId bigint)
  RETURNS TABLE WITH SCHEMABINDING AS RETURN
    SELECT 1 AS Allowed
    WHERE @CompanyId > 0 AND @SiteId > 0
      AND @CompanyId=TRY_CONVERT(bigint,SESSION_CONTEXT(N''OrionERP.HospitalityCompanyId''))
      AND @SiteId=TRY_CONVERT(bigint,SESSION_CONTEXT(N''OrionERP.HospitalitySiteId''));');
  EXEC(N'CREATE FUNCTION orion.fn_HospitalityPaymentScopePredicate(@CompanyId bigint,@SiteId bigint,@TransaccionId int)
  RETURNS TABLE WITH SCHEMABINDING AS RETURN
    SELECT 1 AS Allowed FROM dbo.Transacciones payment JOIN orion.Company companyInfo ON companyInfo.CompanyId=@CompanyId AND companyInfo.Rfc=payment.RFC
    WHERE payment.ID=@TransaccionId AND @CompanyId>0 AND @SiteId>0
      AND @CompanyId=TRY_CONVERT(bigint,SESSION_CONTEXT(N''OrionERP.HospitalityCompanyId''))
      AND @SiteId=TRY_CONVERT(bigint,SESSION_CONTEXT(N''OrionERP.HospitalitySiteId''));');
  SET @Policy=LEFT(@Policy,LEN(@Policy)-1)+N' WITH (STATE=ON,SCHEMABINDING=ON);';
  EXEC sys.sp_executesql @Policy;
  IF (SELECT COUNT(*) FROM sys.security_predicates WHERE object_id=OBJECT_ID(N'orion.HospitalityScopePolicy')) <> (SELECT COUNT(*)*3 FROM @Targets)
    THROW 51816, 'La politica no contiene todos los predicados esperados.', 1;
  SELECT p.name AS SecurityPolicy,p.is_enabled,p.is_schema_bound,(SELECT COUNT(*) FROM @Targets) AS ProtectedTables
  FROM sys.security_policies p WHERE p.object_id=OBJECT_ID(N'orion.HospitalityScopePolicy');
  IF @ApplyChanges=1
  BEGIN
    INSERT orion.SchemaMigration(MigrationId,Checksum,AppliedBy,AppVersion,DatabaseName)
      VALUES(@MigrationId,@MigrationChecksum,COALESCE(CONVERT(nvarchar(256),SESSION_CONTEXT(N'OrionERP.UserName')),CONVERT(nvarchar(256),ORIGINAL_LOGIN())),@AppVersion,DB_NAME());
    COMMIT TRANSACTION;
    SELECT N'APLICADO_SOLO_SANDBOX' AS Estado,DB_NAME() AS BaseDatos,@MigrationId AS MigrationId;
  END
  ELSE
  BEGIN
    ROLLBACK TRANSACTION;
    SELECT N'VALIDADO_SIN_CAMBIOS' AS Estado,DB_NAME() AS BaseDatos,@MigrationId AS MigrationId;
  END;
END TRY
BEGIN CATCH
  IF CURSOR_STATUS('local','scope_cursor')>=0 CLOSE scope_cursor;
  IF CURSOR_STATUS('local','scope_cursor')>=-1 DEALLOCATE scope_cursor;
  IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
  THROW;
END CATCH;

