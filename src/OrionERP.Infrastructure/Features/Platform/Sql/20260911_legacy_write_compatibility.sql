/* Preserve old-writer compatibility while keeping the new explicit Restaurant bindings fail-closed. */
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

DECLARE @ExpectedDatabase sysname=N'$(ExpectedDatabase)';
DECLARE @ApplyInput nvarchar(20)=N'$(ApplyChanges)';
DECLARE @ApplyChanges bit=0;
DECLARE @MigrationId nvarchar(200)=N'$(MigrationId)';
DECLARE @MigrationChecksum varchar(128)='$(MigrationChecksum)';
DECLARE @AppVersionInput nvarchar(64)=N'$(AppVersion)';
DECLARE @AppVersion nvarchar(64)=NULL;

IF @ApplyInput NOT LIKE N'$'+N'(%'
BEGIN
  IF @ApplyInput NOT IN(N'0',N'1') THROW 53100,'ApplyChanges debe ser 0 o 1.',1;
  SET @ApplyChanges=CONVERT(bit,@ApplyInput);
END;
IF @ExpectedDatabase LIKE N'$'+N'(%' OR @ExpectedDatabase NOT IN(N'Orion_Sandbox',N'grupocarpio',N'Orion_CutoverValidation_20260908')
  THROW 53101,'Base esperada no permitida.',1;
IF DB_NAME()<>@ExpectedDatabase THROW 53102,'La conexión no apunta a la base declarada.',1;
IF @MigrationId<>N'20260911_legacy_write_compatibility' THROW 53103,'MigrationId incorrecto.',1;
IF @MigrationChecksum LIKE '$'+'(%' OR LEN(@MigrationChecksum)<>64
   OR @MigrationChecksum LIKE '%[^0-9A-F]%' COLLATE Latin1_General_100_BIN2
  THROW 53104,'MigrationChecksum inválido.',1;
IF @AppVersionInput NOT LIKE N'$'+N'(%' SET @AppVersion=NULLIF(LTRIM(RTRIM(@AppVersionInput)),N'');
IF OBJECT_ID(N'orion.SchemaMigration',N'U') IS NULL
   OR OBJECT_ID(N'orion.Company',N'U') IS NULL
   OR OBJECT_ID(N'orion.Site',N'U') IS NULL
   OR OBJECT_ID(N'orion.PublicSite',N'U') IS NULL
   OR OBJECT_ID(N'restaurante.Site',N'U') IS NULL
   OR OBJECT_ID(N'restaurante.PublicSiteSettings',N'U') IS NULL
   OR COL_LENGTH(N'restaurante.Site',N'OrionCompanyId') IS NULL
   OR COL_LENGTH(N'restaurante.Site',N'OrionSiteId') IS NULL
   OR COL_LENGTH(N'restaurante.PublicSiteSettings',N'PublicSiteId') IS NULL
  THROW 53105,'Falta el binding explícito Restaurant requerido.',1;

DECLARE @ExistingChecksum char(64)=(SELECT Checksum FROM orion.SchemaMigration WHERE MigrationId=@MigrationId);
IF @ExistingChecksum IS NOT NULL AND UPPER(@ExistingChecksum)<>UPPER(@MigrationChecksum)
  THROW 53106,'El MigrationId ya existe con otro checksum.',1;
IF @ExistingChecksum IS NOT NULL
BEGIN
  SELECT N'YA_APLICADO' Estado,@MigrationId MigrationId,@ExistingChecksum Checksum;
  RETURN;
END;

BEGIN TRANSACTION;
BEGIN TRY
  DECLARE @LockResult int;
  EXEC @LockResult=sys.sp_getapplock
    @Resource=N'OrionERP:Migration:20260911_legacy_write_compatibility',
    @LockMode=N'Exclusive',@LockOwner=N'Transaction',@LockTimeout=15000;
  IF @LockResult<0 THROW 53107,'No se obtuvo el candado de migración.',1;

  IF EXISTS(SELECT 1 FROM restaurante.Site WHERE OrionCompanyId IS NULL OR OrionSiteId IS NULL)
     OR EXISTS(SELECT 1 FROM restaurante.PublicSiteSettings WHERE PublicSiteId IS NULL)
    THROW 53108,'El baseline contiene bindings nulos antes de habilitar compatibilidad.',1;

  IF EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'restaurante.Site') AND name=N'UX_RestaurantSite_OrionSite')
    DROP INDEX UX_RestaurantSite_OrionSite ON restaurante.Site;
  ALTER TABLE restaurante.Site ALTER COLUMN OrionCompanyId bigint NULL;
  ALTER TABLE restaurante.Site ALTER COLUMN OrionSiteId bigint NULL;
  CREATE UNIQUE INDEX UX_RestaurantSite_OrionSite
    ON restaurante.Site(OrionCompanyId,OrionSiteId)
    WHERE OrionCompanyId IS NOT NULL AND OrionSiteId IS NOT NULL;

  IF EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'restaurante.PublicSiteSettings') AND name=N'UX_RestaurantPublicSettings_PublicSite')
    DROP INDEX UX_RestaurantPublicSettings_PublicSite ON restaurante.PublicSiteSettings;
  ALTER TABLE restaurante.PublicSiteSettings ALTER COLUMN PublicSiteId bigint NULL;
  CREATE UNIQUE INDEX UX_RestaurantPublicSettings_PublicSite
    ON restaurante.PublicSiteSettings(PublicSiteId)
    WHERE PublicSiteId IS NOT NULL;

  EXEC(N'
    CREATE OR ALTER TRIGGER restaurante.TR_Site_LegacyBinding
    ON restaurante.Site AFTER INSERT,UPDATE AS
    BEGIN
      SET NOCOUNT ON;
      IF EXISTS
      (
        SELECT 1 FROM inserted item
        LEFT JOIN orion.Company companyInfo ON companyInfo.Rfc=item.Rfc AND companyInfo.IsActive=1
        LEFT JOIN orion.Site siteInfo ON siteInfo.CompanyId=companyInfo.CompanyId
          AND siteInfo.SiteKey=item.SiteCode AND siteInfo.IsActive=1
        WHERE companyInfo.CompanyId IS NULL OR siteInfo.SiteId IS NULL
          OR (item.OrionCompanyId IS NOT NULL AND item.OrionCompanyId<>companyInfo.CompanyId)
          OR (item.OrionSiteId IS NOT NULL AND item.OrionSiteId<>siteInfo.SiteId)
      ) THROW 53120,''La sede Restaurant heredada no tiene un binding central exacto.'',1;

      UPDATE target
      SET OrionCompanyId=companyInfo.CompanyId,OrionSiteId=siteInfo.SiteId
      FROM restaurante.Site target
      JOIN inserted item ON item.Id=target.Id
      JOIN orion.Company companyInfo ON companyInfo.Rfc=item.Rfc AND companyInfo.IsActive=1
      JOIN orion.Site siteInfo ON siteInfo.CompanyId=companyInfo.CompanyId
        AND siteInfo.SiteKey=item.SiteCode AND siteInfo.IsActive=1
      WHERE target.OrionCompanyId IS NULL OR target.OrionSiteId IS NULL;

      IF EXISTS
        (SELECT 1 FROM restaurante.Site target JOIN inserted item ON item.Id=target.Id
         WHERE target.OrionCompanyId IS NULL OR target.OrionSiteId IS NULL)
        THROW 53121,''La sede Restaurant quedó sin binding central.'',1;
    END;');

  EXEC(N'
    CREATE OR ALTER TRIGGER restaurante.TR_PublicSiteSettings_LegacyBinding
    ON restaurante.PublicSiteSettings AFTER INSERT,UPDATE AS
    BEGIN
      SET NOCOUNT ON;
      IF EXISTS
      (
        SELECT 1 FROM inserted item
        LEFT JOIN restaurante.Site localSite ON localSite.Rfc=item.Rfc AND localSite.Id=item.SiteId
        OUTER APPLY
        (
          SELECT COUNT_BIG(*) Matches,MIN(publicSite.PublicSiteId) PublicSiteId
          FROM orion.PublicSite publicSite
          WHERE publicSite.CompanyId=localSite.OrionCompanyId
            AND publicSite.SiteId=localSite.OrionSiteId
            AND publicSite.ModuleCode=''RESTAURANT'' AND publicSite.IsActive=1
        ) binding
        WHERE localSite.Id IS NULL OR binding.Matches<>1
          OR (item.PublicSiteId IS NOT NULL AND item.PublicSiteId<>binding.PublicSiteId)
      ) THROW 53122,''La configuración Restaurant heredada no corresponde a un PublicSite único.'',1;

      UPDATE target
      SET PublicSiteId=binding.PublicSiteId
      FROM restaurante.PublicSiteSettings target
      JOIN inserted item ON item.Rfc=target.Rfc AND item.SiteId=target.SiteId
      JOIN restaurante.Site localSite ON localSite.Rfc=item.Rfc AND localSite.Id=item.SiteId
      CROSS APPLY
      (
        SELECT MIN(publicSite.PublicSiteId) PublicSiteId
        FROM orion.PublicSite publicSite
        WHERE publicSite.CompanyId=localSite.OrionCompanyId
          AND publicSite.SiteId=localSite.OrionSiteId
          AND publicSite.ModuleCode=''RESTAURANT'' AND publicSite.IsActive=1
        HAVING COUNT_BIG(*)=1
      ) binding
      WHERE target.PublicSiteId IS NULL;

      IF EXISTS
        (SELECT 1 FROM restaurante.PublicSiteSettings target JOIN inserted item
          ON item.Rfc=target.Rfc AND item.SiteId=target.SiteId WHERE target.PublicSiteId IS NULL)
        THROW 53123,''La configuración Restaurant quedó sin PublicSite.'',1;
    END;');

  IF @ApplyChanges=1
  BEGIN
    INSERT orion.SchemaMigration(MigrationId,Checksum,AppliedBy,AppVersion,DatabaseName)
    VALUES(@MigrationId,@MigrationChecksum,
      COALESCE(CONVERT(nvarchar(256),SESSION_CONTEXT(N'OrionERP.UserName')),CONVERT(nvarchar(256),ORIGINAL_LOGIN())),
      @AppVersion,DB_NAME());
    COMMIT TRANSACTION;
    SELECT N'APLICADO' Estado,@MigrationId MigrationId,N'LegacyWriteBridge' CompatibilityMode;
  END
  ELSE
  BEGIN
    SELECT N'PREVIEW' Estado,@MigrationId MigrationId,N'LegacyWriteBridge' CompatibilityMode;
    ROLLBACK TRANSACTION;
  END;
END TRY
BEGIN CATCH
  IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
  THROW;
END CATCH;
