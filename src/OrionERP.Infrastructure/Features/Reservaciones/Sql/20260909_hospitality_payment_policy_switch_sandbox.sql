/*
  E7 follow-up: SQL Server compiles all ALTER SECURITY POLICY statements in a
  dynamic batch against the same starting metadata. The payment corrector must
  therefore separate DROP and ADD into distinct batches. The public wrapper from
  the prior migration continues to guarantee rollback on every error.
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
  IF @ApplyChangesInput NOT IN (N'0',N'1') THROW 52230,'ApplyChanges debe ser 0 o 1.',1;
  SET @ApplyChanges=CONVERT(bit,@ApplyChangesInput);
END;
IF @ExpectedDatabase LIKE N'$'+N'(%' OR @ExpectedDatabase<>N'Orion_Sandbox'
  THROW 52231,'Esta migracion admite exclusivamente Orion_Sandbox.',1;
IF DB_NAME()<>N'Orion_Sandbox' THROW 52232,'La conexion no apunta a Orion_Sandbox.',1;
IF @MigrationId LIKE N'$'+N'(%' OR NULLIF(LTRIM(RTRIM(@MigrationId)),N'') IS NULL OR LEN(@MigrationId)>200
  THROW 52233,'MigrationId es obligatorio y debe provenir del manifiesto.',1;
IF @MigrationChecksum LIKE '$'+'(%' OR LEN(@MigrationChecksum)<>64
   OR @MigrationChecksum LIKE '%[^0-9A-F]%' COLLATE Latin1_General_100_BIN2
  THROW 52234,'MigrationChecksum debe ser un SHA-256 hexadecimal de 64 caracteres.',1;
IF @AppVersionInput NOT LIKE N'$'+N'(%' SET @AppVersion=NULLIF(LTRIM(RTRIM(@AppVersionInput)),N'');

IF OBJECT_ID(N'orion.SchemaMigration',N'U') IS NULL
   OR NOT EXISTS (SELECT 1 FROM orion.SchemaMigration WHERE MigrationId=N'20260909_hospitality_legacy_transaction_guards_sandbox')
   OR OBJECT_ID(N'orion.ReconcileHospitalityPaymentLinks',N'P') IS NULL
   OR OBJECT_ID(N'orion.ReconcileHospitalityPaymentLinks_E7Core',N'P') IS NULL
  THROW 52235,'Falta el corrector E7 protegido.',1;

DECLARE @ExistingChecksum char(64)=(SELECT Checksum FROM orion.SchemaMigration WHERE MigrationId=@MigrationId);
IF @ExistingChecksum IS NOT NULL AND UPPER(@ExistingChecksum)<>UPPER(@MigrationChecksum)
  THROW 52236,'El mismo MigrationId ya existe con otro checksum.',1;
IF @ExistingChecksum IS NOT NULL
BEGIN SELECT N'YA_APLICADO_SOLO_SANDBOX' Estado,@MigrationId MigrationId; RETURN; END;

BEGIN TRY
  BEGIN TRANSACTION;
  DECLARE @LockResult int;
  EXEC @LockResult=sys.sp_getapplock @Resource=N'OrionERP:Hospitality:PaymentPolicySwitch:Sandbox',
    @LockMode=N'Exclusive',@LockOwner=N'Transaction',@LockTimeout=15000;
  IF @LockResult<0 THROW 52237,'No se obtuvo el candado de la correccion del corrector.',1;

  EXEC(N'CREATE OR ALTER PROCEDURE orion.ReconcileHospitalityPaymentLinks_E7Core
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
      DROP BLOCK PREDICATE ON dbo.Reservation_Transacciones AFTER UPDATE;'');
    EXEC(N''ALTER SECURITY POLICY orion.HospitalityScopePolicy
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
      DROP BLOCK PREDICATE ON dbo.Reservation_Transacciones AFTER UPDATE;'');
    EXEC(N''ALTER SECURITY POLICY orion.HospitalityScopePolicy
      ADD FILTER PREDICATE orion.fn_HospitalityPaymentScopePredicate(OrionCompanyId,OrionSiteId,TransaccionID) ON dbo.Reservation_Transacciones,
      ADD BLOCK PREDICATE orion.fn_HospitalityPaymentScopePredicate(OrionCompanyId,OrionSiteId,TransaccionID) ON dbo.Reservation_Transacciones AFTER INSERT,
      ADD BLOCK PREDICATE orion.fn_HospitalityPaymentScopePredicate(OrionCompanyId,OrionSiteId,TransaccionID) ON dbo.Reservation_Transacciones AFTER UPDATE;'');
    IF @ApplyChanges=1
    BEGIN COMMIT TRANSACTION; SELECT N''APLICADO'' Estado,@BatchKey BatchKey,@Rows Filas,@Hash PreviewChecksum; END
    ELSE
    BEGIN ROLLBACK TRANSACTION; SELECT N''VALIDADO_SIN_CAMBIOS'' Estado,@BatchKey BatchKey,@Rows Filas,@Hash PreviewChecksum; END;
  END;');

  IF OBJECT_DEFINITION(OBJECT_ID(N'orion.ReconcileHospitalityPaymentLinks_E7Core')) NOT LIKE '%UPDATE link SET TransaccionID%'
     OR OBJECT_DEFINITION(OBJECT_ID(N'orion.ReconcileHospitalityPaymentLinks_E7Core')) NOT LIKE '%ExpectedPreviewChecksum%'
    THROW 52238,'El core corregido no conserva los controles E7.',1;
  IF NOT EXISTS (SELECT 1 FROM sys.security_policies WHERE object_id=OBJECT_ID(N'orion.HospitalityScopePolicy') AND is_enabled=1 AND is_schema_bound=1)
    THROW 52239,'La politica RLS dejo de estar activa.',1;

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
