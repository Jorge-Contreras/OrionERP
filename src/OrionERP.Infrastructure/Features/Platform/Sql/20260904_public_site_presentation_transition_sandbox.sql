/*
  OrionERP public-site presentation transition window.

  Adds one indivisible fallback pair for BrandingVersion + ContentVersion.
  The pair expires and exists only to keep the previous website artifact valid
  during a controlled activation or automatic binary rollback.

  This migration is intentionally restricted to Orion_Sandbox. Production is
  a later, independently approved migration after backup and preview review.
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
    THROW 51600, 'ApplyChanges debe ser 0 o 1.', 1;
  SET @ApplyChanges = CONVERT(bit, @ApplyChangesInput);
END;

IF @ExpectedDatabase LIKE N'$' + N'(%' OR @ExpectedDatabase <> N'Orion_Sandbox'
  THROW 51601, 'Esta migracion admite exclusivamente Orion_Sandbox.', 1;
IF DB_NAME() <> N'Orion_Sandbox'
  THROW 51602, 'La conexion no apunta a Orion_Sandbox.', 1;
IF @MigrationId LIKE N'$' + N'(%'
   OR NULLIF(LTRIM(RTRIM(@MigrationId)), N'') IS NULL
   OR LEN(@MigrationId) > 200
  THROW 51603, 'MigrationId es obligatorio y debe provenir del manifiesto.', 1;
IF @MigrationChecksum LIKE '$' + '(%'
   OR LEN(@MigrationChecksum) <> 64
   OR @MigrationChecksum LIKE '%[^0-9A-F]%' COLLATE Latin1_General_100_BIN2
  THROW 51604, 'MigrationChecksum debe ser un SHA-256 hexadecimal de 64 caracteres.', 1;
IF @AppVersionInput NOT LIKE N'$' + N'(%'
  SET @AppVersion = NULLIF(LTRIM(RTRIM(@AppVersionInput)), N'');

IF OBJECT_ID(N'orion.SchemaMigration', N'U') IS NULL
   OR OBJECT_ID(N'orion.PublicSite', N'U') IS NULL
   OR OBJECT_ID(N'orion.PlatformAudit', N'U') IS NULL
  THROW 51605, 'Falta aplicar la fundacion de plataforma.', 1;
IF NOT EXISTS
(
  SELECT 1 FROM orion.SchemaMigration
  WHERE MigrationId = N'20260901_platform_foundation'
)
  THROW 51606, 'El ledger no confirma la fundacion de plataforma.', 1;

DECLARE @ExistingChecksum char(64) =
(
  SELECT Checksum FROM orion.SchemaMigration WHERE MigrationId = @MigrationId
);
IF @ExistingChecksum IS NOT NULL
   AND UPPER(@ExistingChecksum) <> UPPER(@MigrationChecksum)
  THROW 51607, 'El mismo MigrationId ya existe con otro checksum.', 1;

DECLARE @FallbackColumnsPresent int =
  CASE WHEN COL_LENGTH(N'orion.PublicSite', N'FallbackBrandingVersion') IS NULL THEN 0 ELSE 1 END
  + CASE WHEN COL_LENGTH(N'orion.PublicSite', N'FallbackContentVersion') IS NULL THEN 0 ELSE 1 END
  + CASE WHEN COL_LENGTH(N'orion.PublicSite', N'FallbackUntilUtc') IS NULL THEN 0 ELSE 1 END;
IF @FallbackColumnsPresent NOT IN (0, 3)
  THROW 51608, 'PublicSite contiene una implementacion parcial del fallback.', 1;

BEGIN TRY
  BEGIN TRANSACTION;

  IF @FallbackColumnsPresent = 0
  BEGIN
    ALTER TABLE orion.PublicSite ADD
      FallbackBrandingVersion bigint NULL,
      FallbackContentVersion bigint NULL,
      FallbackUntilUtc datetime2(7) NULL;
  END;

  IF NOT EXISTS
  (
    SELECT 1 FROM sys.check_constraints
    WHERE parent_object_id = OBJECT_ID(N'orion.PublicSite')
      AND name = N'CK_orion_PublicSite_PresentationFallback'
  )
  BEGIN
    EXEC(N'
    ALTER TABLE orion.PublicSite WITH CHECK ADD
      CONSTRAINT CK_orion_PublicSite_PresentationFallback CHECK
      (
        (
          FallbackBrandingVersion IS NULL
          AND FallbackContentVersion IS NULL
          AND FallbackUntilUtc IS NULL
        )
        OR
        (
          FallbackBrandingVersion > 0
          AND FallbackContentVersion > 0
          AND FallbackUntilUtc > UpdatedAtUtc
          AND FallbackUntilUtc <= DATEADD(hour, 2, UpdatedAtUtc)
          AND
          (
            FallbackBrandingVersion <> BrandingVersion
            OR FallbackContentVersion <> ContentVersion
          )
        )
      );');
  END;

  EXEC(N'
  CREATE OR ALTER TRIGGER orion.TR_PublicSite_GuardAudit
  ON orion.PublicSite
  AFTER INSERT, UPDATE, DELETE
  AS
  BEGIN
    SET NOCOUNT ON;

    IF EXISTS
    (
      SELECT 1
      FROM deleted previousRow
      JOIN inserted currentRow ON currentRow.PublicSiteId = previousRow.PublicSiteId
      WHERE currentRow.PublicSiteKey <> previousRow.PublicSiteKey
         OR currentRow.CompanyId <> previousRow.CompanyId
         OR currentRow.SiteId <> previousRow.SiteId
         OR currentRow.ModuleCode <> previousRow.ModuleCode
    )
      THROW 51241, ''PublicSiteKey y su binding de empresa/sitio/modulo son inmutables.'', 1;

    IF EXISTS
    (
      SELECT 1
      FROM inserted currentRow
      JOIN orion.Company companyInfo ON companyInfo.CompanyId = currentRow.CompanyId
      JOIN orion.Site siteInfo
        ON siteInfo.CompanyId = currentRow.CompanyId
       AND siteInfo.SiteId = currentRow.SiteId
      JOIN orion.CompanyModule assignment
        ON assignment.CompanyId = currentRow.CompanyId
       AND assignment.ModuleCode = currentRow.ModuleCode
      JOIN orion.SiteCapability capability
        ON capability.CompanyId = currentRow.CompanyId
       AND capability.SiteId = currentRow.SiteId
       AND capability.ModuleCode = currentRow.ModuleCode
      JOIN orion.Module moduleInfo ON moduleInfo.ModuleCode = currentRow.ModuleCode
      WHERE currentRow.IsActive = 1
        AND
        (
          companyInfo.IsActive = 0
          OR siteInfo.IsActive = 0
          OR assignment.IsEnabled = 0
          OR capability.IsEnabled = 0
          OR moduleInfo.IsActive = 0
          OR moduleInfo.RequiresSite = 0
        )
    )
      THROW 51242, ''PublicSite activo requiere empresa, sitio, modulo y capacidad activos.'', 1;

    DECLARE @Actor nvarchar(256) = COALESCE(CONVERT(nvarchar(256), SESSION_CONTEXT(N''OrionERP.UserName'')), CONVERT(nvarchar(256), ORIGINAL_LOGIN()));
    DECLARE @ApplicationName nvarchar(128) = COALESCE(CONVERT(nvarchar(128), SESSION_CONTEXT(N''OrionERP.Application'')), CONVERT(nvarchar(128), APP_NAME()));
    DECLARE @CorrelationId uniqueidentifier = TRY_CONVERT(uniqueidentifier, SESSION_CONTEXT(N''OrionERP.CorrelationId''));

    INSERT orion.PlatformAudit
    (
      Actor, ApplicationName, CorrelationId, [Action], EntityType, EntityKey,
      CompanyId, SiteId, ModuleCode, PublicSiteId, BeforeJson, AfterJson
    )
    SELECT @Actor, @ApplicationName, @CorrelationId,
           CASE WHEN previousRow.PublicSiteId IS NULL THEN ''INSERT''
                WHEN currentRow.PublicSiteId IS NULL THEN ''DELETE'' ELSE ''UPDATE'' END,
           ''PublicSite'',
           COALESCE(currentRow.PublicSiteKey, previousRow.PublicSiteKey),
           COALESCE(currentRow.CompanyId, previousRow.CompanyId),
           COALESCE(currentRow.SiteId, previousRow.SiteId),
           COALESCE(currentRow.ModuleCode, previousRow.ModuleCode),
           COALESCE(currentRow.PublicSiteId, previousRow.PublicSiteId),
           CASE WHEN previousRow.PublicSiteId IS NULL THEN NULL ELSE
             (SELECT previousRow.PublicSiteId, previousRow.PublicSiteKey, previousRow.CompanyId,
                     previousRow.SiteId, previousRow.ModuleCode, previousRow.CanonicalHost,
                     previousRow.IsActive, previousRow.ConfigurationVersion,
                     previousRow.BrandingVersion, previousRow.ContentVersion,
                     previousRow.FallbackBrandingVersion, previousRow.FallbackContentVersion,
                     previousRow.FallbackUntilUtc
              FOR JSON PATH, WITHOUT_ARRAY_WRAPPER) END,
           CASE WHEN currentRow.PublicSiteId IS NULL THEN NULL ELSE
             (SELECT currentRow.PublicSiteId, currentRow.PublicSiteKey, currentRow.CompanyId,
                     currentRow.SiteId, currentRow.ModuleCode, currentRow.CanonicalHost,
                     currentRow.IsActive, currentRow.ConfigurationVersion,
                     currentRow.BrandingVersion, currentRow.ContentVersion,
                     currentRow.FallbackBrandingVersion, currentRow.FallbackContentVersion,
                     currentRow.FallbackUntilUtc
              FOR JSON PATH, WITHOUT_ARRAY_WRAPPER) END
    FROM inserted currentRow
    FULL OUTER JOIN deleted previousRow
      ON previousRow.PublicSiteId = currentRow.PublicSiteId;
  END;');

  IF NOT EXISTS
  (
    SELECT 1
    FROM sys.columns
    WHERE object_id = OBJECT_ID(N'orion.PublicSite')
      AND name = N'FallbackBrandingVersion'
      AND system_type_id = TYPE_ID(N'bigint')
      AND is_nullable = 1
  )
     OR NOT EXISTS
  (
    SELECT 1
    FROM sys.columns
    WHERE object_id = OBJECT_ID(N'orion.PublicSite')
      AND name = N'FallbackContentVersion'
      AND system_type_id = TYPE_ID(N'bigint')
      AND is_nullable = 1
  )
     OR NOT EXISTS
  (
    SELECT 1
    FROM sys.columns
    WHERE object_id = OBJECT_ID(N'orion.PublicSite')
      AND name = N'FallbackUntilUtc'
      AND system_type_id = TYPE_ID(N'datetime2')
      AND is_nullable = 1
  )
    THROW 51609, 'Las columnas de fallback no tienen el contrato esperado.', 1;

  EXEC(N'
  IF EXISTS
  (
    SELECT 1 FROM orion.PublicSite
    WHERE
      (FallbackBrandingVersion IS NULL AND (FallbackContentVersion IS NOT NULL OR FallbackUntilUtc IS NOT NULL))
      OR (FallbackBrandingVersion IS NOT NULL AND (FallbackContentVersion IS NULL OR FallbackUntilUtc IS NULL))
      OR FallbackBrandingVersion <= 0
      OR FallbackContentVersion <= 0
  )
    THROW 51610, ''PublicSite contiene una pareja fallback incompleta o invalida.'', 1;

  SELECT
    PublicSiteKey,
    BrandingVersion,
    ContentVersion,
    FallbackBrandingVersion,
    FallbackContentVersion,
    FallbackUntilUtc
  FROM orion.PublicSite
  ORDER BY PublicSiteKey;');

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
