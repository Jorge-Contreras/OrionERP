/*
  Hospitality checkout legal-consent evidence — Orion_Sandbox only.

  Historical reservations remain NULL in all three columns. A public checkout
  created after this migration must persist the privacy version, terms version
  and the server-generated UTC acceptance timestamp as one indivisible tuple.

  This migration is intentionally restricted to Orion_Sandbox. Production is
  outside the authorization for this change.
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
    THROW 51700, 'ApplyChanges debe ser 0 o 1.', 1;
  SET @ApplyChanges = CONVERT(bit, @ApplyChangesInput);
END;

IF @ExpectedDatabase LIKE N'$' + N'(%' OR @ExpectedDatabase <> N'Orion_Sandbox'
  THROW 51701, 'Esta migracion admite exclusivamente Orion_Sandbox.', 1;
IF DB_NAME() <> N'Orion_Sandbox'
  THROW 51702, 'La conexion no apunta a Orion_Sandbox.', 1;
IF @MigrationId LIKE N'$' + N'(%'
   OR NULLIF(LTRIM(RTRIM(@MigrationId)), N'') IS NULL
   OR LEN(@MigrationId) > 200
  THROW 51703, 'MigrationId es obligatorio y debe provenir del manifiesto.', 1;
IF @MigrationChecksum LIKE '$' + '(%'
   OR LEN(@MigrationChecksum) <> 64
   OR @MigrationChecksum LIKE '%[^0-9A-F]%' COLLATE Latin1_General_100_BIN2
  THROW 51704, 'MigrationChecksum debe ser un SHA-256 hexadecimal de 64 caracteres.', 1;
IF @AppVersionInput NOT LIKE N'$' + N'(%'
  SET @AppVersion = NULLIF(LTRIM(RTRIM(@AppVersionInput)), N'');

IF OBJECT_ID(N'orion.SchemaMigration', N'U') IS NULL
   OR OBJECT_ID(N'dbo.RESERVATION', N'U') IS NULL
  THROW 51705, 'Falta la fundacion de plataforma o dbo.RESERVATION.', 1;
IF NOT EXISTS
(
  SELECT 1 FROM orion.SchemaMigration
  WHERE MigrationId = N'20260903_hospitality_public_scope_sandbox'
)
   OR NOT EXISTS
(
  SELECT 1 FROM orion.SchemaMigration
  WHERE MigrationId = N'20260904_public_site_presentation_transition_sandbox'
)
  THROW 51706, 'Faltan el aislamiento de Hospedaje o la activacion versionada de presentaciones.', 1;

DECLARE @ExistingChecksum char(64) =
(
  SELECT Checksum FROM orion.SchemaMigration WHERE MigrationId = @MigrationId
);
IF @ExistingChecksum IS NOT NULL
   AND UPPER(@ExistingChecksum) <> UPPER(@MigrationChecksum)
  THROW 51707, 'El mismo MigrationId ya existe con otro checksum.', 1;

DECLARE @ConsentColumnsPresent int =
  CASE WHEN COL_LENGTH(N'dbo.RESERVATION', N'PrivacyVersionAccepted') IS NULL THEN 0 ELSE 1 END
  + CASE WHEN COL_LENGTH(N'dbo.RESERVATION', N'TermsVersionAccepted') IS NULL THEN 0 ELSE 1 END
  + CASE WHEN COL_LENGTH(N'dbo.RESERVATION', N'LegalAcceptedAtUtc') IS NULL THEN 0 ELSE 1 END;
IF @ConsentColumnsPresent NOT IN (0, 3)
  THROW 51708, 'RESERVATION contiene una implementacion parcial del consentimiento legal.', 1;

BEGIN TRY
  BEGIN TRANSACTION;

  DECLARE @LockResult int;
  EXEC @LockResult = sys.sp_getapplock
    @Resource = N'OrionERP:Hospitality:LegalConsent:Sandbox',
    @LockMode = N'Exclusive',
    @LockOwner = N'Transaction',
    @LockTimeout = 15000;
  IF @LockResult < 0
    THROW 51709, 'No fue posible obtener el bloqueo de migracion.', 1;

  IF @ConsentColumnsPresent = 0
  BEGIN
    ALTER TABLE dbo.RESERVATION ADD
      PrivacyVersionAccepted nvarchar(30) NULL,
      TermsVersionAccepted nvarchar(30) NULL,
      LegalAcceptedAtUtc datetime2(0) NULL;
  END;

  IF NOT EXISTS
  (
    SELECT 1
    FROM sys.columns
    WHERE object_id = OBJECT_ID(N'dbo.RESERVATION')
      AND name = N'PrivacyVersionAccepted'
      AND system_type_id = TYPE_ID(N'nvarchar')
      AND max_length = 60
      AND is_nullable = 1
  )
     OR NOT EXISTS
  (
    SELECT 1
    FROM sys.columns
    WHERE object_id = OBJECT_ID(N'dbo.RESERVATION')
      AND name = N'TermsVersionAccepted'
      AND system_type_id = TYPE_ID(N'nvarchar')
      AND max_length = 60
      AND is_nullable = 1
  )
     OR NOT EXISTS
  (
    SELECT 1
    FROM sys.columns
    WHERE object_id = OBJECT_ID(N'dbo.RESERVATION')
      AND name = N'LegalAcceptedAtUtc'
      AND system_type_id = TYPE_ID(N'datetime2')
      AND scale = 0
      AND is_nullable = 1
  )
    THROW 51710, 'Las columnas de consentimiento no tienen el contrato esperado.', 1;

  EXEC(N'
  IF EXISTS
  (
    SELECT 1 FROM dbo.RESERVATION
    WHERE
      (PrivacyVersionAccepted IS NULL AND (TermsVersionAccepted IS NOT NULL OR LegalAcceptedAtUtc IS NOT NULL))
      OR (PrivacyVersionAccepted IS NOT NULL AND (TermsVersionAccepted IS NULL OR LegalAcceptedAtUtc IS NULL))
      OR (PrivacyVersionAccepted IS NOT NULL AND NULLIF(LTRIM(RTRIM(PrivacyVersionAccepted)), N'''') IS NULL)
      OR (TermsVersionAccepted IS NOT NULL AND NULLIF(LTRIM(RTRIM(TermsVersionAccepted)), N'''') IS NULL)
  )
    THROW 51711, ''RESERVATION contiene evidencia legal parcial o vacia.'', 1;');

  IF @ExistingChecksum IS NULL
     AND EXISTS
  (
    SELECT 1 FROM sys.check_constraints
    WHERE parent_object_id = OBJECT_ID(N'dbo.RESERVATION')
      AND name = N'CK_RESERVATION_HospitalityLegalConsent_AllOrNone'
  )
  BEGIN
    ALTER TABLE dbo.RESERVATION
      DROP CONSTRAINT CK_RESERVATION_HospitalityLegalConsent_AllOrNone;
  END;

  IF NOT EXISTS
  (
    SELECT 1 FROM sys.check_constraints
    WHERE parent_object_id = OBJECT_ID(N'dbo.RESERVATION')
      AND name = N'CK_RESERVATION_HospitalityLegalConsent_AllOrNone'
  )
  BEGIN
    EXEC(N'
    ALTER TABLE dbo.RESERVATION WITH CHECK ADD
      CONSTRAINT CK_RESERVATION_HospitalityLegalConsent_AllOrNone CHECK
      (
        (
          PrivacyVersionAccepted IS NULL
          AND TermsVersionAccepted IS NULL
          AND LegalAcceptedAtUtc IS NULL
        )
        OR
        (
          PrivacyVersionAccepted IS NOT NULL
          AND TermsVersionAccepted IS NOT NULL
          AND LegalAcceptedAtUtc IS NOT NULL
          AND NULLIF(LTRIM(RTRIM(PrivacyVersionAccepted)), N'''') IS NOT NULL
          AND NULLIF(LTRIM(RTRIM(TermsVersionAccepted)), N'''') IS NOT NULL
        )
      );
    ALTER TABLE dbo.RESERVATION
      CHECK CONSTRAINT CK_RESERVATION_HospitalityLegalConsent_AllOrNone;');
  END;

  IF NOT EXISTS
  (
    SELECT 1 FROM sys.check_constraints
    WHERE parent_object_id = OBJECT_ID(N'dbo.RESERVATION')
      AND name = N'CK_RESERVATION_HospitalityLegalConsent_AllOrNone'
      AND is_disabled = 0
      AND is_not_trusted = 0
  )
    THROW 51712, 'La restriccion all-or-none de consentimiento no esta habilitada y confiable.', 1;

  EXEC(N'
  SELECT
    COUNT_BIG(*) AS TotalReservations,
    SUM(CASE WHEN PrivacyVersionAccepted IS NOT NULL THEN CONVERT(bigint, 1) ELSE CONVERT(bigint, 0) END) AS ReservationsWithLegalConsent,
    SUM(CASE WHEN
      (PrivacyVersionAccepted IS NULL AND (TermsVersionAccepted IS NOT NULL OR LegalAcceptedAtUtc IS NOT NULL))
      OR (PrivacyVersionAccepted IS NOT NULL AND (TermsVersionAccepted IS NULL OR LegalAcceptedAtUtc IS NULL))
      THEN CONVERT(bigint, 1) ELSE CONVERT(bigint, 0) END) AS PartialLegalConsentRows
  FROM dbo.RESERVATION;');

  IF @ApplyChanges = 1
  BEGIN
    IF NOT EXISTS (SELECT 1 FROM orion.SchemaMigration WHERE MigrationId = @MigrationId)
    BEGIN
      INSERT orion.SchemaMigration
        (MigrationId, Checksum, AppliedBy, AppVersion, DatabaseName)
      VALUES
        (@MigrationId, @MigrationChecksum,
         COALESCE(CONVERT(nvarchar(256), SESSION_CONTEXT(N'OrionERP.UserName')), CONVERT(nvarchar(256), ORIGINAL_LOGIN())),
         @AppVersion, DB_NAME());
    END;

    COMMIT TRANSACTION;
    SELECT N'APLICADO_SOLO_SANDBOX' AS Estado, DB_NAME() AS BaseDatos, @MigrationId AS MigrationId;
  END
  ELSE
  BEGIN
    ROLLBACK TRANSACTION;
    SELECT N'VALIDADO_SIN_CAMBIOS' AS Estado, DB_NAME() AS BaseDatos, @MigrationId AS MigrationId;
  END;
END TRY
BEGIN CATCH
  IF XACT_STATE() <> 0
    ROLLBACK TRANSACTION;
  THROW;
END CATCH;
