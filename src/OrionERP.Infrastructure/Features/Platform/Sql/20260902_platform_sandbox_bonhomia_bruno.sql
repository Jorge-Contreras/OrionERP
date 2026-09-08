/*
  OrionERP Sandbox provisioning for the two currently verifiable public sites.

  This is deliberately a data-only migration. The platform foundation already
  supplies the schema required by phases 3 and 4.

  Evidence for the fixed bindings:
    - OrionERP.Bonhomia.Web/appsettings.Development.json:
        PublicWebsite identity and host binding.
    - OrionERP.Bonhomia.Web/appsettings.json:
        BonhomiaCheckout.TimeZone.
    - OrionERP.Application/Features/Restaurante/BrunoRestaurantConstants.cs:
        Bruno legacy RFC and operational SiteCode.
    - OrionERP.Bruno.Web/appsettings.Development.json:
        PublicWebsite identity and host binding.

  No TaxRfc is inferred. The explicit legacy keys below are copied only to
  LegacyTenantKey for these two known records. No third site is created.
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
    THROW 51300, 'ApplyChanges debe ser 0 o 1.', 1;

  SET @ApplyChanges = CONVERT(bit, @ApplyChangesInput);
END;

IF @ExpectedDatabase LIKE N'$' + N'(%'
   OR @ExpectedDatabase <> N'Orion_Sandbox'
  THROW 51301, 'Este aprovisionamiento admite exclusivamente Orion_Sandbox.', 1;

IF DB_NAME() <> N'Orion_Sandbox'
  THROW 51302, 'La conexion no apunta a Orion_Sandbox.', 1;

IF @MigrationId LIKE N'$' + N'(%'
   OR NULLIF(LTRIM(RTRIM(@MigrationId)), N'') IS NULL
   OR LEN(@MigrationId) > 200
  THROW 51303, 'MigrationId es obligatorio y debe provenir del manifiesto.', 1;

IF @MigrationChecksum LIKE '$' + '(%'
   OR LEN(@MigrationChecksum) <> 64
   OR @MigrationChecksum LIKE '%[^0-9A-F]%' COLLATE Latin1_General_100_BIN2
  THROW 51304, 'MigrationChecksum debe ser un SHA-256 hexadecimal de 64 caracteres.', 1;

IF @AppVersionInput NOT LIKE N'$' + N'(%'
  SET @AppVersion = NULLIF(LTRIM(RTRIM(@AppVersionInput)), N'');

IF OBJECT_ID(N'orion.SchemaMigration', N'U') IS NULL
   OR OBJECT_ID(N'orion.Company', N'U') IS NULL
   OR OBJECT_ID(N'orion.Site', N'U') IS NULL
   OR OBJECT_ID(N'orion.Module', N'U') IS NULL
   OR OBJECT_ID(N'orion.CompanyModule', N'U') IS NULL
   OR OBJECT_ID(N'orion.SiteCapability', N'U') IS NULL
   OR OBJECT_ID(N'orion.PublicSite', N'U') IS NULL
  THROW 51305, 'Falta aplicar la fundacion de plataforma antes del aprovisionamiento.', 1;

IF NOT EXISTS
(
  SELECT 1
  FROM orion.SchemaMigration
  WHERE MigrationId = N'20260901_platform_foundation'
)
  THROW 51306, 'El ledger no confirma la fundacion de plataforma requerida.', 1;

DECLARE @ExistingChecksum char(64) =
(
  SELECT Checksum
  FROM orion.SchemaMigration
  WHERE MigrationId = @MigrationId
);

IF @ExistingChecksum IS NOT NULL
   AND UPPER(@ExistingChecksum) <> UPPER(@MigrationChecksum)
  THROW 51307, 'El mismo MigrationId ya existe con otro checksum.', 1;

DECLARE @BonhomiaLegacyRfc varchar(50) = 'OHM191112Q26';
DECLARE @BonhomiaSiteKey varchar(100) = 'bonhomia-suites';
DECLARE @BonhomiaSiteName nvarchar(200) = N'Bonhomia Suites';
DECLARE @BonhomiaTimeZone nvarchar(100) = N'America/Mexico_City';
DECLARE @BonhomiaPublicSiteKey varchar(100) = 'bonhomia-main';
DECLARE @BonhomiaHost varchar(253) = 'bonhomiasuites.com';

DECLARE @BrunoLegacyRfc varchar(50) = 'BRUNOS260707L26';
DECLARE @BrunoOperationalSiteCode varchar(30) = 'BRUNOS-01';
DECLARE @BrunoSiteKey varchar(100) = 'brunos-01';
DECLARE @BrunoPublicSiteKey varchar(100) = 'brunos-main';
DECLARE @BrunoHost varchar(253) = 'brunosgarden.com';

DECLARE @BonhomiaCompanyId bigint;
DECLARE @BrunoCompanyId bigint;
DECLARE @BonhomiaSiteId bigint;
DECLARE @BrunoSiteId bigint;
DECLARE @BrunoSiteName nvarchar(200);
DECLARE @BrunoTimeZone nvarchar(100);

BEGIN TRY
  BEGIN TRANSACTION;

  DECLARE @LockResult int;
  EXEC @LockResult = sys.sp_getapplock
    @Resource = N'OrionERP:Platform:Sandbox:BonhomiaBruno',
    @LockMode = N'Exclusive',
    @LockOwner = N'Transaction',
    @LockTimeout = 15000;

  IF @LockResult < 0
    THROW 51308, 'No fue posible obtener el bloqueo de aprovisionamiento.', 1;

  IF NOT EXISTS
  (
    SELECT 1 FROM orion.Module
    WHERE ModuleCode = 'HOSPITALITY'
      AND IsCore = 0 AND RequiresSite = 1 AND IsActive = 1
  )
    THROW 51309, 'HOSPITALITY no coincide con el catalogo requerido.', 1;

  IF NOT EXISTS
  (
    SELECT 1 FROM orion.Module
    WHERE ModuleCode = 'RESTAURANT'
      AND IsCore = 0 AND RequiresSite = 1 AND IsActive = 1
  )
    THROW 51310, 'RESTAURANT no coincide con el catalogo requerido.', 1;

  SELECT @BonhomiaCompanyId = CompanyId
  FROM orion.Company
  WHERE Rfc COLLATE Latin1_General_100_BIN2 = @BonhomiaLegacyRfc;

  SELECT @BrunoCompanyId = CompanyId
  FROM orion.Company
  WHERE Rfc COLLATE Latin1_General_100_BIN2 = @BrunoLegacyRfc;

  IF @BonhomiaCompanyId IS NULL OR @BrunoCompanyId IS NULL
    THROW 51311, 'Las dos empresas configuradas deben existir antes de aprovisionar.', 1;

  IF @BonhomiaCompanyId = @BrunoCompanyId
    THROW 51312, 'Bonhomia y Bruno no pueden compartir CompanyId.', 1;

  IF EXISTS
  (
    SELECT 1
    FROM orion.Company
    WHERE CompanyId IN (@BonhomiaCompanyId, @BrunoCompanyId)
      AND IsActive = 0
  )
    THROW 51313, 'Las empresas configuradas deben estar activas.', 1;

  -- TaxRfc remains deliberately unassigned. Never erase a reconciled value.
  IF EXISTS
  (
    SELECT 1
    FROM orion.Company
    WHERE CompanyId IN (@BonhomiaCompanyId, @BrunoCompanyId)
      AND TaxRfc IS NOT NULL
  )
    THROW 51314, 'TaxRfc ya fue reconciliado; este script no puede modificarlo ni asumirlo.', 1;

  IF EXISTS
  (
    SELECT 1
    FROM orion.Company
    WHERE CompanyId = @BonhomiaCompanyId
      AND LegacyTenantKey IS NOT NULL
      AND LegacyTenantKey COLLATE Latin1_General_100_BIN2 <> @BonhomiaLegacyRfc
  )
     OR EXISTS
  (
    SELECT 1
    FROM orion.Company
    WHERE CompanyId = @BrunoCompanyId
      AND LegacyTenantKey IS NOT NULL
      AND LegacyTenantKey COLLATE Latin1_General_100_BIN2 <> @BrunoLegacyRfc
  )
    THROW 51315, 'LegacyTenantKey existente contradice la configuracion comprobada.', 1;

  IF OBJECT_ID(N'restaurante.Site', N'U') IS NULL
    THROW 51316, 'Falta restaurante.Site para comprobar BRUNOS-01.', 1;

  IF
  (
    SELECT COUNT_BIG(*)
    FROM restaurante.Site
    WHERE Rfc COLLATE Latin1_General_100_BIN2 = @BrunoLegacyRfc
      AND SiteCode COLLATE Latin1_General_100_BIN2 = @BrunoOperationalSiteCode
  ) <> 1
    THROW 51317, 'La sede operativa BRUNOS-01 no existe de forma unica para Bruno.', 1;

  SELECT
    @BrunoSiteName = CONVERT(nvarchar(200), NULLIF(LTRIM(RTRIM([Name])), '')),
    @BrunoTimeZone = CONVERT(nvarchar(100), NULLIF(LTRIM(RTRIM(TimeZoneId)), ''))
  FROM restaurante.Site
  WHERE Rfc COLLATE Latin1_General_100_BIN2 = @BrunoLegacyRfc
    AND SiteCode COLLATE Latin1_General_100_BIN2 = @BrunoOperationalSiteCode
    AND IsEnabled = 1;

  IF @BrunoSiteName IS NULL OR @BrunoTimeZone IS NULL
    THROW 51318, 'BRUNOS-01 debe estar habilitada y tener nombre y zona horaria.', 1;

  UPDATE orion.Company
  SET LegacyTenantKey = Rfc,
      UpdatedAtUtc = SYSUTCDATETIME(),
      UpdatedBy = @MigrationId
  WHERE CompanyId IN (@BonhomiaCompanyId, @BrunoCompanyId)
    AND LegacyTenantKey IS NULL;

  IF NOT EXISTS
  (
    SELECT 1 FROM orion.Site
    WHERE CompanyId = @BonhomiaCompanyId AND SiteKey = @BonhomiaSiteKey
  )
  BEGIN
    INSERT orion.Site
      (CompanyId, SiteKey, DisplayName, TimeZoneId, IsActive, UpdatedBy)
    VALUES
      (@BonhomiaCompanyId, @BonhomiaSiteKey, @BonhomiaSiteName, @BonhomiaTimeZone, 1, @MigrationId);
  END;

  IF NOT EXISTS
  (
    SELECT 1 FROM orion.Site
    WHERE CompanyId = @BrunoCompanyId AND SiteKey = @BrunoSiteKey
  )
  BEGIN
    INSERT orion.Site
      (CompanyId, SiteKey, DisplayName, TimeZoneId, IsActive, UpdatedBy)
    VALUES
      (@BrunoCompanyId, @BrunoSiteKey, @BrunoSiteName, @BrunoTimeZone, 1, @MigrationId);
  END;

  IF EXISTS
  (
    SELECT 1 FROM orion.Site
    WHERE CompanyId = @BonhomiaCompanyId AND SiteKey = @BonhomiaSiteKey
      AND
      (
        DisplayName COLLATE Latin1_General_100_BIN2 <> @BonhomiaSiteName
        OR TimeZoneId COLLATE Latin1_General_100_BIN2 <> @BonhomiaTimeZone
        OR IsActive = 0
      )
  )
    THROW 51319, 'La sede Bonhomia existente contradice la configuracion comprobada.', 1;

  IF EXISTS
  (
    SELECT 1 FROM orion.Site
    WHERE CompanyId = @BrunoCompanyId AND SiteKey = @BrunoSiteKey
      AND
      (
        DisplayName COLLATE Latin1_General_100_BIN2 <> @BrunoSiteName
        OR TimeZoneId COLLATE Latin1_General_100_BIN2 <> @BrunoTimeZone
        OR IsActive = 0
      )
  )
    THROW 51320, 'La sede Bruno existente contradice la sede operativa comprobada.', 1;

  SELECT @BonhomiaSiteId = SiteId
  FROM orion.Site
  WHERE CompanyId = @BonhomiaCompanyId AND SiteKey = @BonhomiaSiteKey;

  SELECT @BrunoSiteId = SiteId
  FROM orion.Site
  WHERE CompanyId = @BrunoCompanyId AND SiteKey = @BrunoSiteKey;

  IF NOT EXISTS
  (
    SELECT 1 FROM orion.CompanyModule
    WHERE CompanyId = @BonhomiaCompanyId AND ModuleCode = 'HOSPITALITY'
  )
  BEGIN
    INSERT orion.CompanyModule
      (CompanyId, ModuleCode, [Status], ConfigurationVersion, EffectiveFromUtc, EffectiveToUtc, UpdatedBy)
    VALUES
      (@BonhomiaCompanyId, 'HOSPITALITY', 'Enabled', 1, NULL, NULL, @MigrationId);
  END;

  IF NOT EXISTS
  (
    SELECT 1 FROM orion.CompanyModule
    WHERE CompanyId = @BrunoCompanyId AND ModuleCode = 'RESTAURANT'
  )
  BEGIN
    INSERT orion.CompanyModule
      (CompanyId, ModuleCode, [Status], ConfigurationVersion, EffectiveFromUtc, EffectiveToUtc, UpdatedBy)
    VALUES
      (@BrunoCompanyId, 'RESTAURANT', 'Enabled', 1, NULL, NULL, @MigrationId);
  END;

  IF EXISTS
  (
    SELECT 1 FROM orion.CompanyModule
    WHERE CompanyId = @BonhomiaCompanyId AND ModuleCode = 'HOSPITALITY'
      AND
      (
        [Status] <> 'Enabled'
        OR ConfigurationVersion <> 1
        OR EffectiveFromUtc IS NOT NULL
        OR EffectiveToUtc IS NOT NULL
      )
  )
     OR EXISTS
  (
    SELECT 1 FROM orion.CompanyModule
    WHERE CompanyId = @BrunoCompanyId AND ModuleCode = 'RESTAURANT'
      AND
      (
        [Status] <> 'Enabled'
        OR ConfigurationVersion <> 1
        OR EffectiveFromUtc IS NOT NULL
        OR EffectiveToUtc IS NOT NULL
      )
  )
    THROW 51321, 'Un CompanyModule existente contradice el aprovisionamiento esperado.', 1;

  IF NOT EXISTS
  (
    SELECT 1 FROM orion.SiteCapability
    WHERE CompanyId = @BonhomiaCompanyId
      AND SiteId = @BonhomiaSiteId AND ModuleCode = 'HOSPITALITY'
  )
  BEGIN
    INSERT orion.SiteCapability
      (CompanyId, SiteId, ModuleCode, IsEnabled, UpdatedBy)
    VALUES
      (@BonhomiaCompanyId, @BonhomiaSiteId, 'HOSPITALITY', 1, @MigrationId);
  END;

  IF NOT EXISTS
  (
    SELECT 1 FROM orion.SiteCapability
    WHERE CompanyId = @BrunoCompanyId
      AND SiteId = @BrunoSiteId AND ModuleCode = 'RESTAURANT'
  )
  BEGIN
    INSERT orion.SiteCapability
      (CompanyId, SiteId, ModuleCode, IsEnabled, UpdatedBy)
    VALUES
      (@BrunoCompanyId, @BrunoSiteId, 'RESTAURANT', 1, @MigrationId);
  END;

  IF EXISTS
  (
    SELECT 1 FROM orion.SiteCapability
    WHERE CompanyId = @BonhomiaCompanyId
      AND SiteId = @BonhomiaSiteId AND ModuleCode = 'HOSPITALITY'
      AND IsEnabled = 0
  )
     OR EXISTS
  (
    SELECT 1 FROM orion.SiteCapability
    WHERE CompanyId = @BrunoCompanyId
      AND SiteId = @BrunoSiteId AND ModuleCode = 'RESTAURANT'
      AND IsEnabled = 0
  )
    THROW 51322, 'Una SiteCapability existente esta deshabilitada.', 1;

  IF EXISTS
  (
    SELECT 1 FROM orion.PublicSite
    WHERE CanonicalHost = @BonhomiaHost AND PublicSiteKey <> @BonhomiaPublicSiteKey
  )
     OR EXISTS
  (
    SELECT 1 FROM orion.PublicSite
    WHERE CanonicalHost = @BrunoHost AND PublicSiteKey <> @BrunoPublicSiteKey
  )
    THROW 51323, 'Un host canonico ya pertenece a otro PublicSiteKey.', 1;

  IF NOT EXISTS
  (
    SELECT 1 FROM orion.PublicSite
    WHERE PublicSiteKey = @BonhomiaPublicSiteKey
  )
  BEGIN
    INSERT orion.PublicSite
      (PublicSiteKey, CompanyId, SiteId, ModuleCode, CanonicalHost, IsActive,
       ConfigurationVersion, BrandingVersion, ContentVersion, UpdatedBy)
    VALUES
      (@BonhomiaPublicSiteKey, @BonhomiaCompanyId, @BonhomiaSiteId,
       'HOSPITALITY', @BonhomiaHost, 1, 1, 1, 1, @MigrationId);
  END;

  IF NOT EXISTS
  (
    SELECT 1 FROM orion.PublicSite
    WHERE PublicSiteKey = @BrunoPublicSiteKey
  )
  BEGIN
    INSERT orion.PublicSite
      (PublicSiteKey, CompanyId, SiteId, ModuleCode, CanonicalHost, IsActive,
       ConfigurationVersion, BrandingVersion, ContentVersion, UpdatedBy)
    VALUES
      (@BrunoPublicSiteKey, @BrunoCompanyId, @BrunoSiteId,
       'RESTAURANT', @BrunoHost, 1, 1, 1, 1, @MigrationId);
  END;

  IF EXISTS
  (
    SELECT 1 FROM orion.PublicSite
    WHERE PublicSiteKey = @BonhomiaPublicSiteKey
      AND
      (
        CompanyId <> @BonhomiaCompanyId OR SiteId <> @BonhomiaSiteId
        OR ModuleCode <> 'HOSPITALITY' OR CanonicalHost <> @BonhomiaHost
        OR IsActive = 0 OR ConfigurationVersion <> 1
        OR BrandingVersion <> 1 OR ContentVersion <> 1
      )
  )
     OR EXISTS
  (
    SELECT 1 FROM orion.PublicSite
    WHERE PublicSiteKey = @BrunoPublicSiteKey
      AND
      (
        CompanyId <> @BrunoCompanyId OR SiteId <> @BrunoSiteId
        OR ModuleCode <> 'RESTAURANT' OR CanonicalHost <> @BrunoHost
        OR IsActive = 0 OR ConfigurationVersion <> 1
        OR BrandingVersion <> 1 OR ContentVersion <> 1
      )
  )
    THROW 51324, 'Un PublicSite existente contradice el binding esperado.', 1;

  IF (SELECT COUNT_BIG(*) FROM orion.PublicSite WHERE PublicSiteKey IN (@BonhomiaPublicSiteKey, @BrunoPublicSiteKey)) <> 2
    THROW 51325, 'No quedaron exactamente los dos PublicSite esperados.', 1;

  SELECT
    publicSite.PublicSiteKey,
    companyInfo.CompanyId,
    companyInfo.Rfc AS LegacyRfc,
    companyInfo.TaxRfc,
    companyInfo.LegacyTenantKey,
    siteInfo.SiteKey,
    publicSite.ModuleCode,
    assignment.[Status] AS CompanyModuleStatus,
    assignment.ConfigurationVersion AS CompanyModuleConfigurationVersion,
    capability.IsEnabled AS SiteCapabilityEnabled,
    publicSite.CanonicalHost,
    publicSite.IsActive,
    publicSite.ConfigurationVersion AS PublicSiteConfigurationVersion
  FROM orion.PublicSite publicSite
  JOIN orion.Company companyInfo ON companyInfo.CompanyId = publicSite.CompanyId
  JOIN orion.Site siteInfo
    ON siteInfo.CompanyId = publicSite.CompanyId AND siteInfo.SiteId = publicSite.SiteId
  JOIN orion.CompanyModule assignment
    ON assignment.CompanyId = publicSite.CompanyId AND assignment.ModuleCode = publicSite.ModuleCode
  JOIN orion.SiteCapability capability
    ON capability.CompanyId = publicSite.CompanyId
   AND capability.SiteId = publicSite.SiteId
   AND capability.ModuleCode = publicSite.ModuleCode
  WHERE publicSite.PublicSiteKey IN (@BonhomiaPublicSiteKey, @BrunoPublicSiteKey)
  ORDER BY publicSite.PublicSiteKey;

  IF @ApplyChanges = 1
  BEGIN
    IF NOT EXISTS
    (
      SELECT 1 FROM orion.SchemaMigration WHERE MigrationId = @MigrationId
    )
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
