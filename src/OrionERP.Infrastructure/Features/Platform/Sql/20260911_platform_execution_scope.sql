/*
  Additive platform scope foundation for brand-neutral public runtimes.
  Safe for existing binaries: legacy RFC/site columns remain authoritative only
  for compatibility triggers and are not removed by this migration.
*/
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
  IF @ApplyInput NOT IN(N'0',N'1') THROW 52400,'ApplyChanges debe ser 0 o 1.',1;
  SET @ApplyChanges=CONVERT(bit,@ApplyInput);
END;
IF @ExpectedDatabase LIKE N'$'+N'(%' OR @ExpectedDatabase NOT IN(N'Orion_Sandbox',N'grupocarpio',N'Orion_CutoverValidation_20260908')
  THROW 52401,'Base esperada no permitida.',1;
IF DB_NAME()<>@ExpectedDatabase THROW 52402,'La conexión no apunta a la base declarada.',1;
IF @MigrationId<>N'20260911_platform_execution_scope' THROW 52403,'MigrationId incorrecto.',1;
IF @MigrationChecksum LIKE '$'+'(%' OR LEN(@MigrationChecksum)<>64
   OR @MigrationChecksum LIKE '%[^0-9A-F]%' COLLATE Latin1_General_100_BIN2
  THROW 52404,'MigrationChecksum inválido.',1;
IF @AppVersionInput NOT LIKE N'$'+N'(%' SET @AppVersion=NULLIF(LTRIM(RTRIM(@AppVersionInput)),N'');

IF OBJECT_ID(N'orion.SchemaMigration',N'U') IS NULL
   OR OBJECT_ID(N'orion.Company',N'U') IS NULL
   OR OBJECT_ID(N'orion.Site',N'U') IS NULL
   OR OBJECT_ID(N'orion.PublicSite',N'U') IS NULL
   OR OBJECT_ID(N'restaurante.Site',N'U') IS NULL
   OR OBJECT_ID(N'restaurante.PublicSiteSettings',N'U') IS NULL
  THROW 52405,'Falta la fundación de plataforma o Restaurant.',1;

DECLARE @ExistingChecksum char(64)=(SELECT Checksum FROM orion.SchemaMigration WHERE MigrationId=@MigrationId);
IF @ExistingChecksum IS NOT NULL AND UPPER(@ExistingChecksum)<>UPPER(@MigrationChecksum)
  THROW 52406,'El MigrationId ya existe con otro checksum.',1;
IF @ExistingChecksum IS NOT NULL
BEGIN
  SELECT N'YA_APLICADO' Estado,@MigrationId MigrationId;
  RETURN;
END;

BEGIN TRY
  BEGIN TRANSACTION;
  DECLARE @LockResult int;
  EXEC @LockResult=sys.sp_getapplock
    @Resource=N'OrionERP:Platform:ExecutionScope:20260911',
    @LockMode=N'Exclusive',@LockOwner=N'Transaction',@LockTimeout=15000;
  IF @LockResult<0 THROW 52407,'No se obtuvo el candado de migración.',1;

  IF COL_LENGTH(N'restaurante.Site',N'OrionCompanyId') IS NULL
    ALTER TABLE restaurante.Site ADD OrionCompanyId bigint NULL;
  IF COL_LENGTH(N'restaurante.Site',N'OrionSiteId') IS NULL
    ALTER TABLE restaurante.Site ADD OrionSiteId bigint NULL;

  IF COL_LENGTH(N'restaurante.PublicSiteSettings',N'PublicSiteId') IS NULL
    ALTER TABLE restaurante.PublicSiteSettings ADD PublicSiteId bigint NULL;
  IF OBJECT_ID(N'auth.AspNetUserCompanies',N'U') IS NOT NULL
     AND COL_LENGTH(N'auth.AspNetUserCompanies',N'CompanyId') IS NULL
    ALTER TABLE auth.AspNetUserCompanies ADD CompanyId bigint NULL;
  IF OBJECT_ID(N'auth.AspNetUserCompanyRoles',N'U') IS NOT NULL
     AND COL_LENGTH(N'auth.AspNetUserCompanyRoles',N'CompanyId') IS NULL
    ALTER TABLE auth.AspNetUserCompanyRoles ADD CompanyId bigint NULL;
END TRY
BEGIN CATCH
  IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
  THROW;
END CATCH;
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;
DECLARE @ApplyInput2 nvarchar(20)=N'$(ApplyChanges)';
DECLARE @ApplyChanges2 bit=0;
DECLARE @MigrationId2 nvarchar(200)=N'$(MigrationId)';
DECLARE @MigrationChecksum2 varchar(128)='$(MigrationChecksum)';
DECLARE @AppVersionInput2 nvarchar(64)=N'$(AppVersion)';
DECLARE @AppVersion2 nvarchar(64)=NULL;
IF @ApplyInput2 NOT IN(N'0',N'1') THROW 52416,'ApplyChanges debe ser 0 o 1.',1;
SET @ApplyChanges2=CONVERT(bit,@ApplyInput2);
IF @AppVersionInput2 NOT LIKE N'$'+N'(%' SET @AppVersion2=NULLIF(LTRIM(RTRIM(@AppVersionInput2)),N'');
IF EXISTS(SELECT 1 FROM orion.SchemaMigration WHERE MigrationId=@MigrationId2)
BEGIN
  IF @@TRANCOUNT>0 ROLLBACK TRANSACTION;
  RETURN;
END;

BEGIN TRY

  IF EXISTS
  (
    SELECT 1
    FROM restaurante.Site legacy
    OUTER APPLY
    (
      SELECT COUNT_BIG(*) Matches
      FROM orion.Company company
      JOIN orion.Site site ON site.CompanyId=company.CompanyId
      WHERE company.Rfc=legacy.Rfc AND site.SiteKey=legacy.SiteCode
    ) candidate
    WHERE candidate.Matches<>1
  )
    THROW 52408,'Cada sede Restaurant debe tener exactamente un binding central explícito.',1;

  UPDATE legacy
  SET OrionCompanyId=company.CompanyId,OrionSiteId=site.SiteId
  FROM restaurante.Site legacy
  JOIN orion.Company company ON company.Rfc=legacy.Rfc
  JOIN orion.Site site ON site.CompanyId=company.CompanyId AND site.SiteKey=legacy.SiteCode
  WHERE legacy.OrionCompanyId IS NULL OR legacy.OrionSiteId IS NULL;

  IF EXISTS
  (
    SELECT 1 FROM restaurante.Site legacy
    JOIN orion.Site site ON site.CompanyId=legacy.OrionCompanyId AND site.SiteId=legacy.OrionSiteId
    JOIN orion.Company company ON company.CompanyId=site.CompanyId
    WHERE legacy.Rfc<>company.Rfc
  ) OR EXISTS(SELECT 1 FROM restaurante.Site WHERE OrionCompanyId IS NULL OR OrionSiteId IS NULL)
    THROW 52409,'El binding Restaurant no coincide con la empresa heredada.',1;

  ALTER TABLE restaurante.Site ALTER COLUMN OrionCompanyId bigint NOT NULL;
  ALTER TABLE restaurante.Site ALTER COLUMN OrionSiteId bigint NOT NULL;

  IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'restaurante.Site') AND name=N'UX_RestaurantSite_OrionSite')
    CREATE UNIQUE INDEX UX_RestaurantSite_OrionSite ON restaurante.Site(OrionCompanyId,OrionSiteId);
  IF NOT EXISTS(SELECT 1 FROM sys.foreign_keys WHERE parent_object_id=OBJECT_ID(N'restaurante.Site') AND name=N'FK_RestaurantSite_OrionSite')
    ALTER TABLE restaurante.Site WITH CHECK ADD CONSTRAINT FK_RestaurantSite_OrionSite
      FOREIGN KEY(OrionCompanyId,OrionSiteId) REFERENCES orion.Site(CompanyId,SiteId);

  IF EXISTS
  (
    SELECT 1
    FROM restaurante.PublicSiteSettings settings
    JOIN restaurante.Site legacy ON legacy.Rfc=settings.Rfc AND legacy.Id=settings.SiteId
    OUTER APPLY
    (
      SELECT COUNT_BIG(*) Matches
      FROM orion.PublicSite publicSite
      WHERE publicSite.CompanyId=legacy.OrionCompanyId
        AND publicSite.SiteId=legacy.OrionSiteId
        AND publicSite.ModuleCode=N'RESTAURANT'
    ) candidate
    WHERE candidate.Matches<>1
  )
    THROW 52410,'Cada configuración pública Restaurant requiere exactamente un PublicSite.',1;

  UPDATE settings
  SET PublicSiteId=publicSite.PublicSiteId
  FROM restaurante.PublicSiteSettings settings
  JOIN restaurante.Site legacy ON legacy.Rfc=settings.Rfc AND legacy.Id=settings.SiteId
  JOIN orion.PublicSite publicSite
    ON publicSite.CompanyId=legacy.OrionCompanyId
   AND publicSite.SiteId=legacy.OrionSiteId
   AND publicSite.ModuleCode=N'RESTAURANT'
  WHERE settings.PublicSiteId IS NULL;

  IF EXISTS(SELECT 1 FROM restaurante.PublicSiteSettings WHERE PublicSiteId IS NULL)
    THROW 52411,'PublicSiteId no pudo rellenarse en la configuración Restaurant.',1;
  ALTER TABLE restaurante.PublicSiteSettings ALTER COLUMN PublicSiteId bigint NOT NULL;
  IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'restaurante.PublicSiteSettings') AND name=N'UX_RestaurantPublicSettings_PublicSite')
    CREATE UNIQUE INDEX UX_RestaurantPublicSettings_PublicSite ON restaurante.PublicSiteSettings(PublicSiteId);
  IF NOT EXISTS(SELECT 1 FROM sys.foreign_keys WHERE parent_object_id=OBJECT_ID(N'restaurante.PublicSiteSettings') AND name=N'FK_RestaurantPublicSettings_PublicSite')
    ALTER TABLE restaurante.PublicSiteSettings WITH CHECK ADD CONSTRAINT FK_RestaurantPublicSettings_PublicSite
      FOREIGN KEY(PublicSiteId) REFERENCES orion.PublicSite(PublicSiteId);

  IF OBJECT_ID(N'auth.AspNetUserCompanies',N'U') IS NOT NULL
  BEGIN
    UPDATE membership SET CompanyId=company.CompanyId
    FROM auth.AspNetUserCompanies membership
    JOIN orion.Company company ON company.Rfc=membership.Rfc
    WHERE membership.CompanyId IS NULL;
    IF EXISTS(SELECT 1 FROM auth.AspNetUserCompanies WHERE CompanyId IS NULL)
      THROW 52412,'Una membresía de consola no corresponde a una empresa central.',1;
    IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'auth.AspNetUserCompanies') AND name=N'UX_AspNetUserCompanies_UserCompanyId')
      CREATE UNIQUE INDEX UX_AspNetUserCompanies_UserCompanyId ON auth.AspNetUserCompanies(UserId,CompanyId);
    IF NOT EXISTS(SELECT 1 FROM sys.foreign_keys WHERE parent_object_id=OBJECT_ID(N'auth.AspNetUserCompanies') AND name=N'FK_AspNetUserCompanies_CompanyId')
      ALTER TABLE auth.AspNetUserCompanies WITH CHECK ADD CONSTRAINT FK_AspNetUserCompanies_CompanyId
        FOREIGN KEY(CompanyId) REFERENCES orion.Company(CompanyId);
  END;

  IF OBJECT_ID(N'auth.AspNetUserCompanyRoles',N'U') IS NOT NULL
  BEGIN
    UPDATE roleLink SET CompanyId=company.CompanyId
    FROM auth.AspNetUserCompanyRoles roleLink
    JOIN orion.Company company ON company.Rfc=roleLink.Rfc
    WHERE roleLink.CompanyId IS NULL;
    IF EXISTS(SELECT 1 FROM auth.AspNetUserCompanyRoles WHERE CompanyId IS NULL)
      THROW 52413,'Un rol de consola no corresponde a una empresa central.',1;
    IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'auth.AspNetUserCompanyRoles') AND name=N'UX_AspNetUserCompanyRoles_UserCompanyRole')
      CREATE UNIQUE INDEX UX_AspNetUserCompanyRoles_UserCompanyRole ON auth.AspNetUserCompanyRoles(UserId,CompanyId,RoleId);
  END;

  EXEC(N'
    CREATE OR ALTER TRIGGER auth.TR_AspNetUserCompanies_CompanyIdentity
    ON auth.AspNetUserCompanies AFTER INSERT,UPDATE AS
    BEGIN
      SET NOCOUNT ON;
      IF EXISTS
      (
        SELECT 1 FROM inserted item
        LEFT JOIN orion.Company company ON company.Rfc=item.Rfc
        WHERE company.CompanyId IS NULL OR (item.CompanyId IS NOT NULL AND item.CompanyId<>company.CompanyId)
      ) THROW 52414,''La membresía no coincide con CompanyId.'',1;
      UPDATE target SET CompanyId=company.CompanyId
      FROM auth.AspNetUserCompanies target
      JOIN inserted item ON item.UserId=target.UserId AND item.Rfc=target.Rfc
      JOIN orion.Company company ON company.Rfc=item.Rfc
      WHERE target.CompanyId IS NULL;
    END;');

  EXEC(N'
    CREATE OR ALTER TRIGGER auth.TR_AspNetUserCompanyRoles_CompanyIdentity
    ON auth.AspNetUserCompanyRoles AFTER INSERT,UPDATE AS
    BEGIN
      SET NOCOUNT ON;
      IF EXISTS
      (
        SELECT 1 FROM inserted item
        LEFT JOIN orion.Company company ON company.Rfc=item.Rfc
        WHERE company.CompanyId IS NULL OR (item.CompanyId IS NOT NULL AND item.CompanyId<>company.CompanyId)
      ) THROW 52415,''El rol no coincide con CompanyId.'',1;
      UPDATE target SET CompanyId=company.CompanyId
      FROM auth.AspNetUserCompanyRoles target
      JOIN inserted item ON item.UserId=target.UserId AND item.Rfc=target.Rfc AND item.RoleId=target.RoleId
      JOIN orion.Company company ON company.Rfc=item.Rfc
      WHERE target.CompanyId IS NULL;
    END;');

  IF OBJECT_ID(N'orion.PublicSqlPrincipalBinding',N'U') IS NULL
  BEGIN
    CREATE TABLE orion.PublicSqlPrincipalBinding
    (
      PrincipalName sysname NOT NULL CONSTRAINT PK_PublicSqlPrincipalBinding PRIMARY KEY,
      PublicSiteId bigint NOT NULL,
      PermissionProfile varchar(40) NOT NULL,
      PermissionVersion int NOT NULL,
      IsActive bit NOT NULL CONSTRAINT DF_PublicSqlPrincipalBinding_Active DEFAULT(1),
      UpdatedAtUtc datetime2(3) NOT NULL CONSTRAINT DF_PublicSqlPrincipalBinding_Updated DEFAULT(SYSUTCDATETIME()),
      UpdatedBy nvarchar(256) NOT NULL,
      CONSTRAINT FK_PublicSqlPrincipalBinding_PublicSite FOREIGN KEY(PublicSiteId) REFERENCES orion.PublicSite(PublicSiteId),
      CONSTRAINT UX_PublicSqlPrincipalBinding_PublicSite UNIQUE(PublicSiteId),
      CONSTRAINT CK_PublicSqlPrincipalBinding_Profile CHECK(PermissionProfile IN('HOSPITALITY_PUBLIC','RESTAURANT_PUBLIC')),
      CONSTRAINT CK_PublicSqlPrincipalBinding_Version CHECK(PermissionVersion>0)
    );
  END;

  IF OBJECT_ID(N'orion.IntegrationBinding',N'U') IS NULL
  BEGIN
    CREATE TABLE orion.IntegrationBinding
    (
      IntegrationBindingId bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_IntegrationBinding PRIMARY KEY,
      CompanyId bigint NOT NULL,
      SiteId bigint NOT NULL,
      ModuleCode varchar(40) NOT NULL,
      PublicSiteId bigint NULL,
      IntegrationKind varchar(60) NOT NULL,
      SecretReference nvarchar(500) NULL,
      ConfigurationJson nvarchar(max) NOT NULL CONSTRAINT DF_IntegrationBinding_Config DEFAULT(N'{}'),
      IsEnabled bit NOT NULL CONSTRAINT DF_IntegrationBinding_Enabled DEFAULT(0),
      ConfigurationVersion bigint NOT NULL CONSTRAINT DF_IntegrationBinding_Version DEFAULT(1),
      UpdatedAtUtc datetime2(3) NOT NULL CONSTRAINT DF_IntegrationBinding_Updated DEFAULT(SYSUTCDATETIME()),
      UpdatedBy nvarchar(256) NOT NULL,
      CONSTRAINT FK_IntegrationBinding_Site FOREIGN KEY(CompanyId,SiteId) REFERENCES orion.Site(CompanyId,SiteId),
      CONSTRAINT FK_IntegrationBinding_Module FOREIGN KEY(ModuleCode) REFERENCES orion.Module(ModuleCode),
      CONSTRAINT FK_IntegrationBinding_PublicSite FOREIGN KEY(PublicSiteId) REFERENCES orion.PublicSite(PublicSiteId),
      CONSTRAINT UX_IntegrationBinding UNIQUE(CompanyId,SiteId,ModuleCode,IntegrationKind,PublicSiteId),
      CONSTRAINT CK_IntegrationBinding_ConfigJson CHECK(ISJSON(ConfigurationJson)=1),
      CONSTRAINT CK_IntegrationBinding_Version CHECK(ConfigurationVersion>0)
    );
  END;

  IF @ApplyChanges2=1
  BEGIN
    INSERT orion.SchemaMigration(MigrationId,Checksum,AppliedBy,AppVersion,DatabaseName)
    VALUES(
      @MigrationId2,
      @MigrationChecksum2,
      COALESCE(CONVERT(nvarchar(256),SESSION_CONTEXT(N'OrionERP.UserName')),CONVERT(nvarchar(256),ORIGINAL_LOGIN())),
      @AppVersion2,
      DB_NAME());
    COMMIT TRANSACTION;
    SELECT N'APLICADO' Estado,@MigrationId2 MigrationId,
      (SELECT COUNT(*) FROM restaurante.Site) RestaurantSites,
      (SELECT COUNT(*) FROM restaurante.PublicSiteSettings) PublicSettings;
  END
  ELSE
  BEGIN
    SELECT N'PREVIEW' Estado,@MigrationId2 MigrationId,
      (SELECT COUNT(*) FROM restaurante.Site) RestaurantSites,
      (SELECT COUNT(*) FROM restaurante.PublicSiteSettings) PublicSettings;
    ROLLBACK TRANSACTION;
  END;
END TRY
BEGIN CATCH
  IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
  THROW;
END CATCH;
