/*
  E7 follow-up: the two legacy mechanisms intentionally THROW on stale or
  incomplete evidence. Their transaction must never escape that rejection.

  The applied predecessor remains byte-for-byte immutable. Its implementations
  become protected cores; the public signatures get a TRY/CATCH boundary that
  always rolls back an open transaction before returning the original error.
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
  IF @ApplyChangesInput NOT IN (N'0',N'1') THROW 52210,'ApplyChanges debe ser 0 o 1.',1;
  SET @ApplyChanges=CONVERT(bit,@ApplyChangesInput);
END;
IF @ExpectedDatabase LIKE N'$'+N'(%' OR @ExpectedDatabase<>N'Orion_Sandbox'
  THROW 52211,'Esta migracion admite exclusivamente Orion_Sandbox.',1;
IF DB_NAME()<>N'Orion_Sandbox' THROW 52212,'La conexion no apunta a Orion_Sandbox.',1;
IF @MigrationId LIKE N'$'+N'(%' OR NULLIF(LTRIM(RTRIM(@MigrationId)),N'') IS NULL OR LEN(@MigrationId)>200
  THROW 52213,'MigrationId es obligatorio y debe provenir del manifiesto.',1;
IF @MigrationChecksum LIKE '$'+'(%' OR LEN(@MigrationChecksum)<>64
   OR @MigrationChecksum LIKE '%[^0-9A-F]%' COLLATE Latin1_General_100_BIN2
  THROW 52214,'MigrationChecksum debe ser un SHA-256 hexadecimal de 64 caracteres.',1;
IF @AppVersionInput NOT LIKE N'$'+N'(%' SET @AppVersion=NULLIF(LTRIM(RTRIM(@AppVersionInput)),N'');

IF OBJECT_ID(N'orion.SchemaMigration',N'U') IS NULL
   OR NOT EXISTS (SELECT 1 FROM orion.SchemaMigration WHERE MigrationId=N'20260909_hospitality_legacy_mechanisms_sandbox')
   OR OBJECT_ID(N'dbo.CreateActividadForReservation',N'P') IS NULL
   OR OBJECT_ID(N'orion.ReconcileHospitalityPaymentLinks',N'P') IS NULL
  THROW 52215,'Falta la migracion E7 o uno de sus procedimientos.',1;

DECLARE @ExistingChecksum char(64)=(SELECT Checksum FROM orion.SchemaMigration WHERE MigrationId=@MigrationId);
IF @ExistingChecksum IS NOT NULL AND UPPER(@ExistingChecksum)<>UPPER(@MigrationChecksum)
  THROW 52216,'El mismo MigrationId ya existe con otro checksum.',1;
IF @ExistingChecksum IS NOT NULL
BEGIN SELECT N'YA_APLICADO_SOLO_SANDBOX' Estado,@MigrationId MigrationId; RETURN; END;

BEGIN TRY
  BEGIN TRANSACTION;
  DECLARE @LockResult int;
  EXEC @LockResult=sys.sp_getapplock @Resource=N'OrionERP:Hospitality:LegacyTransactionGuards:Sandbox',
    @LockMode=N'Exclusive',@LockOwner=N'Transaction',@LockTimeout=15000;
  IF @LockResult<0 THROW 52217,'No se obtuvo el candado de la correccion E7.',1;

  IF OBJECT_ID(N'dbo.CreateActividadForReservation_E7Core',N'P') IS NOT NULL
     OR OBJECT_ID(N'orion.ReconcileHospitalityPaymentLinks_E7Core',N'P') IS NOT NULL
    THROW 52218,'Los cores E7 ya existen sin una fila de ledger; se requiere revision manual.',1;

  EXEC sys.sp_rename N'dbo.CreateActividadForReservation',N'CreateActividadForReservation_E7Core',N'OBJECT';
  EXEC sys.sp_rename N'orion.ReconcileHospitalityPaymentLinks',N'ReconcileHospitalityPaymentLinks_E7Core',N'OBJECT';

  EXEC(N'CREATE PROCEDURE dbo.CreateActividadForReservation
    @Tipo_OrdenCalendarID int,@Fecha_Inicio datetime2,@Fecha_Final datetime2,@Presupuesto money,
    @Asignacion int=1,@Descripcion varchar(800)=''LIMPIEZA'',@Tipo_Orden varchar(200),@Cuenta_SAT int,@Suite varchar(200)=''''
  AS
  BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    BEGIN TRY
      EXEC dbo.CreateActividadForReservation_E7Core
        @Tipo_OrdenCalendarID=@Tipo_OrdenCalendarID,@Fecha_Inicio=@Fecha_Inicio,@Fecha_Final=@Fecha_Final,
        @Presupuesto=@Presupuesto,@Asignacion=@Asignacion,@Descripcion=@Descripcion,
        @Tipo_Orden=@Tipo_Orden,@Cuenta_SAT=@Cuenta_SAT,@Suite=@Suite;
    END TRY
    BEGIN CATCH
      IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
      THROW;
    END CATCH;
  END;');

  EXEC(N'CREATE PROCEDURE orion.ReconcileHospitalityPaymentLinks
    @BatchKey uniqueidentifier,@ApplyChanges bit=0,@ExpectedPreviewChecksum char(64)=NULL,@Actor nvarchar(256)=NULL
  AS
  BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    BEGIN TRY
      EXEC orion.ReconcileHospitalityPaymentLinks_E7Core
        @BatchKey=@BatchKey,@ApplyChanges=@ApplyChanges,
        @ExpectedPreviewChecksum=@ExpectedPreviewChecksum,@Actor=@Actor;
    END TRY
    BEGIN CATCH
      IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
      THROW;
    END CATCH;
  END;');

  DENY EXECUTE ON OBJECT::dbo.CreateActividadForReservation_E7Core TO public;
  DENY EXECUTE ON OBJECT::orion.ReconcileHospitalityPaymentLinks_E7Core TO public;

  IF OBJECT_DEFINITION(OBJECT_ID(N'dbo.CreateActividadForReservation')) NOT LIKE '%IF XACT_STATE()<>0 ROLLBACK TRANSACTION%'
     OR OBJECT_DEFINITION(OBJECT_ID(N'orion.ReconcileHospitalityPaymentLinks')) NOT LIKE '%IF XACT_STATE()<>0 ROLLBACK TRANSACTION%'
    THROW 52219,'Los wrappers no garantizan rollback en error.',1;
  IF OBJECT_DEFINITION(OBJECT_ID(N'dbo.CreateActividadForReservation_E7Core')) NOT LIKE '%HospitalityActivityTemplateMapping%'
     OR OBJECT_DEFINITION(OBJECT_ID(N'orion.ReconcileHospitalityPaymentLinks_E7Core')) NOT LIKE '%ExpectedPreviewChecksum%'
    THROW 52220,'Los cores no conservan los controles E7.',1;

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
  THROW;
END CATCH;
