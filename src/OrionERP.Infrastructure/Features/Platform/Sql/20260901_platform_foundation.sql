/*
  OrionERP Platform Foundation - Phase 2 (additive only).

  SQLCMD variables supplied by OrionERP.DatabaseMigrator:
    ExpectedDatabase   = Orion_Sandbox | grupocarpio
    ApplyChanges       = 0 | 1 (an unresolved variable defaults to 0)
    MigrationId        = immutable manifest migration id
    MigrationChecksum  = SHA-256 of this file
    AppVersion         = optional migrator version

  This migration deliberately does not infer or backfill TaxRfc,
  LegacyTenantKey, CompanyModule, Site, SiteCapability, or PublicSite.
  ApplyChanges=0 executes the complete validation inside a transaction and
  rolls it back after returning the reconciliation result sets.
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
    THROW 51200, 'ApplyChanges debe ser 0 o 1.', 1;

  SET @ApplyChanges = CONVERT(bit, @ApplyChangesInput);
END;

IF @ExpectedDatabase LIKE N'$' + N'(%'
   OR @ExpectedDatabase NOT IN (N'Orion_Sandbox', N'grupocarpio')
  THROW 51201, 'ExpectedDatabase debe ser Orion_Sandbox o grupocarpio.', 1;

IF DB_NAME() <> @ExpectedDatabase
  THROW 51202, 'La base conectada no coincide con ExpectedDatabase.', 1;

IF @MigrationId LIKE N'$' + N'(%'
   OR NULLIF(LTRIM(RTRIM(@MigrationId)), N'') IS NULL
   OR LEN(@MigrationId) > 200
  THROW 51203, 'MigrationId es obligatorio y debe provenir del manifiesto.', 1;

IF @MigrationChecksum LIKE '$' + '(%'
   OR LEN(@MigrationChecksum) <> 64
   OR @MigrationChecksum LIKE '%[^0-9A-F]%' COLLATE Latin1_General_100_BIN2
  THROW 51204, 'MigrationChecksum debe ser un SHA-256 hexadecimal de 64 caracteres.', 1;

IF @AppVersionInput NOT LIKE N'$' + N'(%'
  SET @AppVersion = NULLIF(LTRIM(RTRIM(@AppVersionInput)), N'');

IF SCHEMA_ID(N'orion') IS NULL
  THROW 51205, 'Falta el esquema orion requerido por la fundacion de plataforma.', 1;

IF OBJECT_ID(N'orion.Company', N'U') IS NULL
  THROW 51206, 'Falta orion.Company; aplique primero la fundacion de empresas.', 1;

IF NOT EXISTS
(
  SELECT 1
  FROM sys.columns columnInfo
  WHERE columnInfo.object_id = OBJECT_ID(N'orion.Company')
    AND columnInfo.name = N'Rfc'
    AND columnInfo.system_type_id = TYPE_ID(N'varchar')
    AND columnInfo.max_length = 50
    AND columnInfo.is_nullable = 0
)
  THROW 51207, 'orion.Company.Rfc no conserva la forma legacy varchar(50) NOT NULL.', 1;

IF NOT EXISTS
(
  SELECT 1
  FROM sys.key_constraints keyInfo
  JOIN sys.index_columns indexColumn
    ON indexColumn.object_id = keyInfo.parent_object_id
   AND indexColumn.index_id = keyInfo.unique_index_id
   AND indexColumn.key_ordinal > 0
  JOIN sys.columns columnInfo
    ON columnInfo.object_id = indexColumn.object_id
   AND columnInfo.column_id = indexColumn.column_id
  WHERE keyInfo.parent_object_id = OBJECT_ID(N'orion.Company')
    AND keyInfo.[type] = N'PK'
  GROUP BY keyInfo.object_id
  HAVING COUNT_BIG(*) = 1 AND MAX(columnInfo.name) = N'Rfc'
)
  THROW 51208, 'La PK legacy de orion.Company debe seguir siendo exclusivamente Rfc.', 1;

IF COL_LENGTH(N'orion.Company', N'CompanyId') IS NULL
   AND EXISTS
   (
     SELECT 1
     FROM sys.identity_columns
     WHERE object_id = OBJECT_ID(N'orion.Company')
   )
  THROW 51209, 'orion.Company ya tiene otra columna IDENTITY; CompanyId no puede agregarse con seguridad.', 1;

IF OBJECT_ID(N'orion.SchemaMigration') IS NOT NULL
   AND OBJECT_ID(N'orion.SchemaMigration', N'U') IS NULL
  THROW 51210, 'orion.SchemaMigration existe pero no es una tabla.', 1;

IF OBJECT_ID(N'orion.Site') IS NOT NULL
   AND OBJECT_ID(N'orion.Site', N'U') IS NULL
  THROW 51211, 'orion.Site existe pero no es una tabla.', 1;

IF OBJECT_ID(N'orion.Module') IS NOT NULL
   AND OBJECT_ID(N'orion.Module', N'U') IS NULL
  THROW 51212, 'orion.Module existe pero no es una tabla.', 1;

IF OBJECT_ID(N'orion.CompanyModule') IS NOT NULL
   AND OBJECT_ID(N'orion.CompanyModule', N'U') IS NULL
  THROW 51213, 'orion.CompanyModule existe pero no es una tabla.', 1;

IF OBJECT_ID(N'orion.SiteCapability') IS NOT NULL
   AND OBJECT_ID(N'orion.SiteCapability', N'U') IS NULL
  THROW 51214, 'orion.SiteCapability existe pero no es una tabla.', 1;

IF OBJECT_ID(N'orion.PublicSite') IS NOT NULL
   AND OBJECT_ID(N'orion.PublicSite', N'U') IS NULL
  THROW 51215, 'orion.PublicSite existe pero no es una tabla.', 1;

IF OBJECT_ID(N'orion.PlatformAudit') IS NOT NULL
   AND OBJECT_ID(N'orion.PlatformAudit', N'U') IS NULL
  THROW 51216, 'orion.PlatformAudit existe pero no es una tabla.', 1;

IF OBJECT_ID(N'orion.SchemaMigration', N'U') IS NOT NULL
   AND
   (
     COL_LENGTH(N'orion.SchemaMigration', N'MigrationId') IS NULL
     OR COL_LENGTH(N'orion.SchemaMigration', N'Checksum') IS NULL
     OR COL_LENGTH(N'orion.SchemaMigration', N'AppliedAtUtc') IS NULL
     OR COL_LENGTH(N'orion.SchemaMigration', N'AppVersion') IS NULL
   )
  THROW 51217, 'orion.SchemaMigration existente no tiene el contrato requerido.', 1;

DECLARE @CompanyRowsBefore bigint;
DECLARE @TaxRfcRowsBefore bigint = 0;
DECLARE @LegacyTenantKeyRowsBefore bigint = 0;
DECLARE @SiteRowsBefore bigint = 0;
DECLARE @CompanyModuleRowsBefore bigint = 0;
DECLARE @SiteCapabilityRowsBefore bigint = 0;
DECLARE @PublicSiteRowsBefore bigint = 0;
DECLARE @ExistingMigrationChecksum varchar(64) = NULL;

SELECT @CompanyRowsBefore = COUNT_BIG(*) FROM orion.Company;

IF COL_LENGTH(N'orion.Company', N'TaxRfc') IS NOT NULL
  EXEC sys.sp_executesql
    N'SELECT @RowCount = COUNT_BIG(*) FROM orion.Company WHERE TaxRfc IS NOT NULL;',
    N'@RowCount bigint OUTPUT', @TaxRfcRowsBefore OUTPUT;

IF COL_LENGTH(N'orion.Company', N'LegacyTenantKey') IS NOT NULL
  EXEC sys.sp_executesql
    N'SELECT @RowCount = COUNT_BIG(*) FROM orion.Company WHERE LegacyTenantKey IS NOT NULL;',
    N'@RowCount bigint OUTPUT', @LegacyTenantKeyRowsBefore OUTPUT;

IF OBJECT_ID(N'orion.Site', N'U') IS NOT NULL
  EXEC sys.sp_executesql
    N'SELECT @RowCount = COUNT_BIG(*) FROM orion.Site;',
    N'@RowCount bigint OUTPUT', @SiteRowsBefore OUTPUT;

IF OBJECT_ID(N'orion.CompanyModule', N'U') IS NOT NULL
  EXEC sys.sp_executesql
    N'SELECT @RowCount = COUNT_BIG(*) FROM orion.CompanyModule;',
    N'@RowCount bigint OUTPUT', @CompanyModuleRowsBefore OUTPUT;

IF OBJECT_ID(N'orion.SiteCapability', N'U') IS NOT NULL
  EXEC sys.sp_executesql
    N'SELECT @RowCount = COUNT_BIG(*) FROM orion.SiteCapability;',
    N'@RowCount bigint OUTPUT', @SiteCapabilityRowsBefore OUTPUT;

IF OBJECT_ID(N'orion.PublicSite', N'U') IS NOT NULL
  EXEC sys.sp_executesql
    N'SELECT @RowCount = COUNT_BIG(*) FROM orion.PublicSite;',
    N'@RowCount bigint OUTPUT', @PublicSiteRowsBefore OUTPUT;

IF OBJECT_ID(N'orion.SchemaMigration', N'U') IS NOT NULL
BEGIN
  EXEC sys.sp_executesql
    N'SELECT @Checksum = Checksum FROM orion.SchemaMigration WHERE MigrationId = @Id;',
    N'@Id nvarchar(200), @Checksum varchar(64) OUTPUT',
    @MigrationId, @ExistingMigrationChecksum OUTPUT;

  IF @ExistingMigrationChecksum IS NOT NULL
     AND UPPER(@ExistingMigrationChecksum) <> UPPER(@MigrationChecksum)
    THROW 51218, 'SchemaMigration contiene el mismo MigrationId con otro MigrationChecksum.', 1;
END;

IF OBJECT_ID(N'tempdb..#OrionPlatformFoundationState', N'U') IS NOT NULL
  DROP TABLE #OrionPlatformFoundationState;

CREATE TABLE #OrionPlatformFoundationState
(
  ExpectedDatabase sysname NOT NULL,
  ApplyChanges bit NOT NULL,
  MigrationId nvarchar(200) NOT NULL,
  MigrationChecksum varchar(128) NOT NULL,
  AppVersion nvarchar(64) NULL,
  CompanyRowsBefore bigint NOT NULL,
  TaxRfcRowsBefore bigint NOT NULL,
  LegacyTenantKeyRowsBefore bigint NOT NULL,
  SiteRowsBefore bigint NOT NULL,
  CompanyModuleRowsBefore bigint NOT NULL,
  SiteCapabilityRowsBefore bigint NOT NULL,
  PublicSiteRowsBefore bigint NOT NULL
);

INSERT #OrionPlatformFoundationState
(
  ExpectedDatabase, ApplyChanges, MigrationId, MigrationChecksum, AppVersion,
  CompanyRowsBefore, TaxRfcRowsBefore, LegacyTenantKeyRowsBefore,
  SiteRowsBefore, CompanyModuleRowsBefore, SiteCapabilityRowsBefore, PublicSiteRowsBefore
)
VALUES
(
  @ExpectedDatabase, @ApplyChanges, @MigrationId, @MigrationChecksum, @AppVersion,
  @CompanyRowsBefore, @TaxRfcRowsBefore, @LegacyTenantKeyRowsBefore,
  @SiteRowsBefore, @CompanyModuleRowsBefore, @SiteCapabilityRowsBefore, @PublicSiteRowsBefore
);

BEGIN TRY
  BEGIN TRANSACTION;

  IF OBJECT_ID(N'orion.SchemaMigration', N'U') IS NULL
  BEGIN
    CREATE TABLE orion.SchemaMigration
    (
      MigrationId nvarchar(200) NOT NULL
        CONSTRAINT PK_orion_SchemaMigration PRIMARY KEY,
      Checksum char(64) NOT NULL,
      AppliedAtUtc datetime2(7) NOT NULL
        CONSTRAINT DF_orion_SchemaMigration_AppliedAtUtc DEFAULT (SYSUTCDATETIME()),
      AppliedBy nvarchar(256) NOT NULL,
      AppVersion nvarchar(64) NULL,
      DatabaseName sysname NOT NULL,
      CONSTRAINT CK_orion_SchemaMigration_MigrationId
        CHECK (NULLIF(LTRIM(RTRIM(MigrationId)), N'') IS NOT NULL),
      CONSTRAINT CK_orion_SchemaMigration_Checksum
        CHECK
        (
          LEN(Checksum) = 64
          AND Checksum NOT LIKE '%[^0-9A-F]%' COLLATE Latin1_General_100_BIN2
        )
    );
  END;

  IF COL_LENGTH(N'orion.Company', N'CompanyId') IS NULL
    ALTER TABLE orion.Company
      ADD CompanyId bigint IDENTITY(1,1) NOT NULL;

  IF COL_LENGTH(N'orion.Company', N'TaxRfc') IS NULL
    ALTER TABLE orion.Company
      ADD TaxRfc varchar(13) NULL;

  IF COL_LENGTH(N'orion.Company', N'LegacyTenantKey') IS NULL
    ALTER TABLE orion.Company
      ADD LegacyTenantKey varchar(50) NULL;

  /*
    Company existed when this batch was compiled. SQL Server must compile the
    following batch after the additive ALTER statements so it can bind the new
    columns. The local temp table and transaction both survive GO because the
    migrator executes every batch on the same open connection.
  */
END TRY
BEGIN CATCH
  IF XACT_STATE() <> 0
    ROLLBACK TRANSACTION;

  IF OBJECT_ID(N'tempdb..#OrionPlatformFoundationState', N'U') IS NOT NULL
    DROP TABLE #OrionPlatformFoundationState;

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

IF OBJECT_ID(N'tempdb..#OrionPlatformFoundationState', N'U') IS NULL
BEGIN
  IF XACT_STATE() <> 0
    ROLLBACK TRANSACTION;
  THROW 51254, 'Se perdio el estado de la migracion entre batches.', 1;
END;

IF @@TRANCOUNT <> 1 OR XACT_STATE() <> 1
BEGIN
  IF XACT_STATE() <> 0
    ROLLBACK TRANSACTION;
  DROP TABLE #OrionPlatformFoundationState;
  THROW 51255, 'La transaccion de la migracion no sobrevivio al cambio de batch.', 1;
END;

DECLARE @ExpectedDatabase sysname;
DECLARE @ApplyChanges bit;
DECLARE @MigrationId nvarchar(200);
DECLARE @MigrationChecksum varchar(128);
DECLARE @AppVersion nvarchar(64);
DECLARE @CompanyRowsBefore bigint;
DECLARE @TaxRfcRowsBefore bigint;
DECLARE @LegacyTenantKeyRowsBefore bigint;
DECLARE @SiteRowsBefore bigint;
DECLARE @CompanyModuleRowsBefore bigint;
DECLARE @SiteCapabilityRowsBefore bigint;
DECLARE @PublicSiteRowsBefore bigint;

SELECT
  @ExpectedDatabase = ExpectedDatabase,
  @ApplyChanges = ApplyChanges,
  @MigrationId = MigrationId,
  @MigrationChecksum = MigrationChecksum,
  @AppVersion = AppVersion,
  @CompanyRowsBefore = CompanyRowsBefore,
  @TaxRfcRowsBefore = TaxRfcRowsBefore,
  @LegacyTenantKeyRowsBefore = LegacyTenantKeyRowsBefore,
  @SiteRowsBefore = SiteRowsBefore,
  @CompanyModuleRowsBefore = CompanyModuleRowsBefore,
  @SiteCapabilityRowsBefore = SiteCapabilityRowsBefore,
  @PublicSiteRowsBefore = PublicSiteRowsBefore
FROM #OrionPlatformFoundationState;

IF DB_NAME() <> @ExpectedDatabase
BEGIN
  ROLLBACK TRANSACTION;
  DROP TABLE #OrionPlatformFoundationState;
  THROW 51256, 'La conexion cambio de base entre batches.', 1;
END;

BEGIN TRY

  IF NOT EXISTS
  (
    SELECT 1
    FROM sys.columns columnInfo
    JOIN sys.types typeInfo ON typeInfo.user_type_id = columnInfo.user_type_id
    WHERE columnInfo.object_id = OBJECT_ID(N'orion.Company')
      AND columnInfo.name = N'CompanyId'
      AND typeInfo.name = N'bigint'
      AND columnInfo.is_nullable = 0
      AND columnInfo.is_identity = 1
  )
    THROW 51219, 'orion.Company.CompanyId no es bigint IDENTITY NOT NULL.', 1;

  IF NOT EXISTS
  (
    SELECT 1
    FROM sys.columns columnInfo
    JOIN sys.types typeInfo ON typeInfo.user_type_id = columnInfo.user_type_id
    WHERE columnInfo.object_id = OBJECT_ID(N'orion.Company')
      AND columnInfo.name = N'TaxRfc'
      AND typeInfo.name = N'varchar'
      AND columnInfo.max_length = 13
      AND columnInfo.is_nullable = 1
  )
    THROW 51220, 'orion.Company.TaxRfc no es varchar(13) NULL.', 1;

  IF NOT EXISTS
  (
    SELECT 1
    FROM sys.columns columnInfo
    JOIN sys.types typeInfo ON typeInfo.user_type_id = columnInfo.user_type_id
    WHERE columnInfo.object_id = OBJECT_ID(N'orion.Company')
      AND columnInfo.name = N'LegacyTenantKey'
      AND typeInfo.name = N'varchar'
      AND columnInfo.max_length = 50
      AND columnInfo.is_nullable = 1
  )
    THROW 51221, 'orion.Company.LegacyTenantKey no es varchar(50) NULL.', 1;

  IF EXISTS
  (
    SELECT CompanyId
    FROM orion.Company
    GROUP BY CompanyId
    HAVING COUNT_BIG(*) > 1
  )
    THROW 51222, 'CompanyId contiene duplicados y no puede convertirse en clave alterna.', 1;

  IF EXISTS
  (
    SELECT TaxRfc
    FROM orion.Company
    WHERE TaxRfc IS NOT NULL
    GROUP BY TaxRfc
    HAVING COUNT_BIG(*) > 1
  )
    THROW 51223, 'TaxRfc contiene duplicados.', 1;

  IF EXISTS
  (
    SELECT LegacyTenantKey
    FROM orion.Company
    WHERE LegacyTenantKey IS NOT NULL
    GROUP BY LegacyTenantKey
    HAVING COUNT_BIG(*) > 1
  )
    THROW 51224, 'LegacyTenantKey contiene duplicados.', 1;

  IF NOT EXISTS
  (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'orion.Company')
      AND name = N'UX_orion_Company_CompanyId'
  )
    CREATE UNIQUE INDEX UX_orion_Company_CompanyId
      ON orion.Company(CompanyId);

  IF NOT EXISTS
  (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'orion.Company')
      AND name = N'UX_orion_Company_TaxRfc'
  )
    CREATE UNIQUE INDEX UX_orion_Company_TaxRfc
      ON orion.Company(TaxRfc)
      WHERE TaxRfc IS NOT NULL;

  IF NOT EXISTS
  (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'orion.Company')
      AND name = N'UX_orion_Company_LegacyTenantKey'
  )
    CREATE UNIQUE INDEX UX_orion_Company_LegacyTenantKey
      ON orion.Company(LegacyTenantKey)
      WHERE LegacyTenantKey IS NOT NULL;

  IF NOT EXISTS
  (
    SELECT 1 FROM sys.check_constraints
    WHERE parent_object_id = OBJECT_ID(N'orion.Company')
      AND name = N'CK_orion_Company_TaxRfc'
  )
    ALTER TABLE orion.Company WITH CHECK
      ADD CONSTRAINT CK_orion_Company_TaxRfc CHECK
      (
        TaxRfc IS NULL
        OR
        (
          DATALENGTH(TaxRfc) IN (12, 13)
          AND TaxRfc = LTRIM(RTRIM(TaxRfc))
          AND TaxRfc COLLATE Latin1_General_100_BIN2 = UPPER(TaxRfc) COLLATE Latin1_General_100_BIN2
          AND TaxRfc NOT LIKE '%[^A-Z0-9&Ñ]%' COLLATE Latin1_General_100_BIN2
        )
      );

  IF NOT EXISTS
  (
    SELECT 1 FROM sys.check_constraints
    WHERE parent_object_id = OBJECT_ID(N'orion.Company')
      AND name = N'CK_orion_Company_LegacyTenantKey'
  )
    ALTER TABLE orion.Company WITH CHECK
      ADD CONSTRAINT CK_orion_Company_LegacyTenantKey CHECK
      (
        LegacyTenantKey IS NULL
        OR
        (
          NULLIF(LTRIM(RTRIM(LegacyTenantKey)), '') IS NOT NULL
          AND LegacyTenantKey = LTRIM(RTRIM(LegacyTenantKey))
        )
      );

  IF OBJECT_ID(N'orion.Module', N'U') IS NULL
  BEGIN
    CREATE TABLE orion.Module
    (
      ModuleCode varchar(40) NOT NULL
        CONSTRAINT PK_orion_Module PRIMARY KEY,
      DisplayName nvarchar(100) NOT NULL,
      [Description] nvarchar(500) NULL,
      IsCore bit NOT NULL
        CONSTRAINT DF_orion_Module_IsCore DEFAULT (0),
      RequiresSite bit NOT NULL
        CONSTRAINT DF_orion_Module_RequiresSite DEFAULT (1),
      IsActive bit NOT NULL
        CONSTRAINT DF_orion_Module_IsActive DEFAULT (1),
      CreatedAtUtc datetime2(0) NOT NULL
        CONSTRAINT DF_orion_Module_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
      UpdatedAtUtc datetime2(0) NOT NULL
        CONSTRAINT DF_orion_Module_UpdatedAtUtc DEFAULT (SYSUTCDATETIME()),
      UpdatedBy nvarchar(450) NULL,
      RowVersion rowversion NOT NULL,
      CONSTRAINT CK_orion_Module_ModuleCode CHECK
      (
        LEN(ModuleCode) BETWEEN 2 AND 40
        AND ModuleCode = LTRIM(RTRIM(ModuleCode))
        AND ModuleCode NOT LIKE '%[^A-Z0-9_]%' COLLATE Latin1_General_100_BIN2
      ),
      CONSTRAINT CK_orion_Module_CoreRequiresSite CHECK (IsCore = 0 OR RequiresSite = 0),
      CONSTRAINT CK_orion_Module_AccountingCore CHECK
      (
        ModuleCode <> 'ACCOUNTING_CORE'
        OR (IsCore = 1 AND RequiresSite = 0 AND IsActive = 1)
      )
    );
  END;

  IF OBJECT_ID(N'orion.Site', N'U') IS NULL
  BEGIN
    CREATE TABLE orion.Site
    (
      SiteId bigint IDENTITY(1,1) NOT NULL
        CONSTRAINT PK_orion_Site PRIMARY KEY,
      CompanyId bigint NOT NULL,
      SiteKey varchar(100) NOT NULL,
      DisplayName nvarchar(200) NOT NULL,
      TimeZoneId nvarchar(100) NOT NULL,
      IsActive bit NOT NULL
        CONSTRAINT DF_orion_Site_IsActive DEFAULT (1),
      CreatedAtUtc datetime2(0) NOT NULL
        CONSTRAINT DF_orion_Site_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
      UpdatedAtUtc datetime2(0) NOT NULL
        CONSTRAINT DF_orion_Site_UpdatedAtUtc DEFAULT (SYSUTCDATETIME()),
      UpdatedBy nvarchar(450) NULL,
      RowVersion rowversion NOT NULL,
      CONSTRAINT UQ_orion_Site_Company_SiteId UNIQUE (CompanyId, SiteId),
      CONSTRAINT UQ_orion_Site_Company_SiteKey UNIQUE (CompanyId, SiteKey),
      CONSTRAINT FK_orion_Site_Company FOREIGN KEY (CompanyId)
        REFERENCES orion.Company(CompanyId),
      CONSTRAINT CK_orion_Site_SiteKey CHECK
      (
        LEN(SiteKey) BETWEEN 2 AND 100
        AND SiteKey = LTRIM(RTRIM(SiteKey))
        AND SiteKey COLLATE Latin1_General_100_BIN2 = LOWER(SiteKey) COLLATE Latin1_General_100_BIN2
        AND SiteKey NOT LIKE '%[^a-z0-9-]%' COLLATE Latin1_General_100_BIN2
      )
    );

    CREATE INDEX IX_orion_Site_Company_Active
      ON orion.Site(CompanyId, IsActive)
      INCLUDE (SiteId, SiteKey, DisplayName, TimeZoneId);
  END;

  IF OBJECT_ID(N'orion.CompanyModule', N'U') IS NULL
  BEGIN
    CREATE TABLE orion.CompanyModule
    (
      CompanyId bigint NOT NULL,
      ModuleCode varchar(40) NOT NULL,
      [Status] varchar(20) NOT NULL
        CONSTRAINT DF_orion_CompanyModule_Status DEFAULT ('Provisioning'),
      IsEnabled AS CONVERT(bit, CASE WHEN [Status] = 'Enabled' THEN 1 ELSE 0 END) PERSISTED,
      ConfigurationVersion bigint NOT NULL
        CONSTRAINT DF_orion_CompanyModule_ConfigurationVersion DEFAULT (1),
      EffectiveFromUtc datetime2(0) NULL,
      EffectiveToUtc datetime2(0) NULL,
      CreatedAtUtc datetime2(0) NOT NULL
        CONSTRAINT DF_orion_CompanyModule_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
      UpdatedAtUtc datetime2(0) NOT NULL
        CONSTRAINT DF_orion_CompanyModule_UpdatedAtUtc DEFAULT (SYSUTCDATETIME()),
      UpdatedBy nvarchar(450) NULL,
      RowVersion rowversion NOT NULL,
      CONSTRAINT PK_orion_CompanyModule PRIMARY KEY (CompanyId, ModuleCode),
      CONSTRAINT FK_orion_CompanyModule_Company FOREIGN KEY (CompanyId)
        REFERENCES orion.Company(CompanyId),
      CONSTRAINT FK_orion_CompanyModule_Module FOREIGN KEY (ModuleCode)
        REFERENCES orion.Module(ModuleCode),
      CONSTRAINT CK_orion_CompanyModule_Status CHECK
        ([Status] IN ('Provisioning', 'Enabled', 'Suspended')),
      CONSTRAINT CK_orion_CompanyModule_ConfigurationVersion CHECK
        (ConfigurationVersion > 0),
      CONSTRAINT CK_orion_CompanyModule_EffectiveRange CHECK
      (
        EffectiveFromUtc IS NULL
        OR EffectiveToUtc IS NULL
        OR EffectiveToUtc > EffectiveFromUtc
      ),
      CONSTRAINT CK_orion_CompanyModule_AccountingCore CHECK
      (
        ModuleCode <> 'ACCOUNTING_CORE' OR [Status] = 'Enabled'
      )
    );

    CREATE INDEX IX_orion_CompanyModule_Module_Enabled
      ON orion.CompanyModule(ModuleCode, IsEnabled)
      INCLUDE (CompanyId, [Status], ConfigurationVersion, EffectiveFromUtc, EffectiveToUtc);
  END;

  IF OBJECT_ID(N'orion.SiteCapability', N'U') IS NULL
  BEGIN
    CREATE TABLE orion.SiteCapability
    (
      CompanyId bigint NOT NULL,
      SiteId bigint NOT NULL,
      ModuleCode varchar(40) NOT NULL,
      IsEnabled bit NOT NULL
        CONSTRAINT DF_orion_SiteCapability_IsEnabled DEFAULT (1),
      CreatedAtUtc datetime2(0) NOT NULL
        CONSTRAINT DF_orion_SiteCapability_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
      UpdatedAtUtc datetime2(0) NOT NULL
        CONSTRAINT DF_orion_SiteCapability_UpdatedAtUtc DEFAULT (SYSUTCDATETIME()),
      UpdatedBy nvarchar(450) NULL,
      RowVersion rowversion NOT NULL,
      CONSTRAINT PK_orion_SiteCapability PRIMARY KEY (CompanyId, SiteId, ModuleCode),
      CONSTRAINT FK_orion_SiteCapability_Site FOREIGN KEY (CompanyId, SiteId)
        REFERENCES orion.Site(CompanyId, SiteId),
      CONSTRAINT FK_orion_SiteCapability_CompanyModule FOREIGN KEY (CompanyId, ModuleCode)
        REFERENCES orion.CompanyModule(CompanyId, ModuleCode),
      CONSTRAINT CK_orion_SiteCapability_NoAccountingCore CHECK
        (ModuleCode <> 'ACCOUNTING_CORE')
    );

    CREATE INDEX IX_orion_SiteCapability_Module_Enabled
      ON orion.SiteCapability(ModuleCode, IsEnabled)
      INCLUDE (CompanyId, SiteId);
  END;

  IF OBJECT_ID(N'orion.PublicSite', N'U') IS NULL
  BEGIN
    CREATE TABLE orion.PublicSite
    (
      PublicSiteId bigint IDENTITY(1,1) NOT NULL
        CONSTRAINT PK_orion_PublicSite PRIMARY KEY,
      PublicSiteKey varchar(100) NOT NULL,
      CompanyId bigint NOT NULL,
      SiteId bigint NOT NULL,
      ModuleCode varchar(40) NOT NULL,
      CanonicalHost varchar(253) NOT NULL,
      IsActive bit NOT NULL
        CONSTRAINT DF_orion_PublicSite_IsActive DEFAULT (1),
      ConfigurationVersion bigint NOT NULL
        CONSTRAINT DF_orion_PublicSite_ConfigurationVersion DEFAULT (1),
      BrandingVersion bigint NOT NULL
        CONSTRAINT DF_orion_PublicSite_BrandingVersion DEFAULT (1),
      ContentVersion bigint NOT NULL
        CONSTRAINT DF_orion_PublicSite_ContentVersion DEFAULT (1),
      CreatedAtUtc datetime2(0) NOT NULL
        CONSTRAINT DF_orion_PublicSite_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
      UpdatedAtUtc datetime2(0) NOT NULL
        CONSTRAINT DF_orion_PublicSite_UpdatedAtUtc DEFAULT (SYSUTCDATETIME()),
      UpdatedBy nvarchar(450) NULL,
      RowVersion rowversion NOT NULL,
      CONSTRAINT UQ_orion_PublicSite_PublicSiteKey UNIQUE (PublicSiteKey),
      CONSTRAINT UQ_orion_PublicSite_CanonicalHost UNIQUE (CanonicalHost),
      CONSTRAINT UQ_orion_PublicSite_Capability UNIQUE (CompanyId, SiteId, ModuleCode),
      CONSTRAINT FK_orion_PublicSite_SiteCapability FOREIGN KEY (CompanyId, SiteId, ModuleCode)
        REFERENCES orion.SiteCapability(CompanyId, SiteId, ModuleCode),
      CONSTRAINT FK_orion_PublicSite_CompanyModule FOREIGN KEY (CompanyId, ModuleCode)
        REFERENCES orion.CompanyModule(CompanyId, ModuleCode),
      CONSTRAINT FK_orion_PublicSite_Site FOREIGN KEY (CompanyId, SiteId)
        REFERENCES orion.Site(CompanyId, SiteId),
      CONSTRAINT CK_orion_PublicSite_PublicSiteKey CHECK
      (
        LEN(PublicSiteKey) BETWEEN 3 AND 100
        AND PublicSiteKey = LTRIM(RTRIM(PublicSiteKey))
        AND PublicSiteKey COLLATE Latin1_General_100_BIN2 = LOWER(PublicSiteKey) COLLATE Latin1_General_100_BIN2
        AND PublicSiteKey NOT LIKE '%[^a-z0-9-]%' COLLATE Latin1_General_100_BIN2
      ),
      CONSTRAINT CK_orion_PublicSite_CanonicalHost CHECK
      (
        LEN(CanonicalHost) BETWEEN 3 AND 253
        AND CanonicalHost = LTRIM(RTRIM(CanonicalHost))
        AND CanonicalHost COLLATE Latin1_General_100_BIN2 = LOWER(CanonicalHost) COLLATE Latin1_General_100_BIN2
        AND CanonicalHost NOT LIKE '%[^a-z0-9.-]%' COLLATE Latin1_General_100_BIN2
        AND CanonicalHost NOT LIKE '.%'
        AND CanonicalHost NOT LIKE '%.'
        AND CanonicalHost NOT LIKE '%..%'
      ),
      CONSTRAINT CK_orion_PublicSite_Versions CHECK
        (ConfigurationVersion > 0 AND BrandingVersion > 0 AND ContentVersion > 0),
      CONSTRAINT CK_orion_PublicSite_NoAccountingCore CHECK
        (ModuleCode <> 'ACCOUNTING_CORE')
    );

    CREATE INDEX IX_orion_PublicSite_Company_Active
      ON orion.PublicSite(CompanyId, IsActive)
      INCLUDE (PublicSiteId, PublicSiteKey, SiteId, ModuleCode, CanonicalHost, ConfigurationVersion);
  END;

  IF OBJECT_ID(N'orion.PlatformAudit', N'U') IS NULL
  BEGIN
    CREATE TABLE orion.PlatformAudit
    (
      PlatformAuditId bigint IDENTITY(1,1) NOT NULL
        CONSTRAINT PK_orion_PlatformAudit PRIMARY KEY,
      OccurredAtUtc datetime2(7) NOT NULL
        CONSTRAINT DF_orion_PlatformAudit_OccurredAtUtc DEFAULT (SYSUTCDATETIME()),
      Actor nvarchar(256) NOT NULL,
      ApplicationName nvarchar(128) NULL,
      CorrelationId uniqueidentifier NULL,
      [Action] varchar(20) NOT NULL,
      EntityType varchar(50) NOT NULL,
      EntityKey nvarchar(450) NOT NULL,
      CompanyId bigint NULL,
      SiteId bigint NULL,
      ModuleCode varchar(40) NULL,
      PublicSiteId bigint NULL,
      BeforeJson nvarchar(max) NULL,
      AfterJson nvarchar(max) NULL,
      CONSTRAINT CK_orion_PlatformAudit_Action CHECK
        ([Action] IN ('INSERT', 'UPDATE', 'DELETE')),
      CONSTRAINT CK_orion_PlatformAudit_BeforeJson CHECK
        (BeforeJson IS NULL OR ISJSON(BeforeJson) = 1),
      CONSTRAINT CK_orion_PlatformAudit_AfterJson CHECK
        (AfterJson IS NULL OR ISJSON(AfterJson) = 1)
    );

    CREATE INDEX IX_orion_PlatformAudit_Company_Time
      ON orion.PlatformAudit(CompanyId, OccurredAtUtc DESC)
      INCLUDE (EntityType, EntityKey, [Action], SiteId, ModuleCode, PublicSiteId);

    CREATE INDEX IX_orion_PlatformAudit_Entity_Time
      ON orion.PlatformAudit(EntityType, EntityKey, OccurredAtUtc DESC)
      INCLUDE ([Action], CompanyId, SiteId, ModuleCode, PublicSiteId);
  END;

  EXEC(N'
  CREATE OR ALTER TRIGGER orion.TR_SchemaMigration_AppendOnly
  ON orion.SchemaMigration
  INSTEAD OF UPDATE, DELETE
  AS
  BEGIN
    SET NOCOUNT ON;
    THROW 51230, ''SchemaMigration es append-only.'', 1;
  END;');

  EXEC(N'
  CREATE OR ALTER TRIGGER orion.TR_PlatformAudit_AppendOnly
  ON orion.PlatformAudit
  INSTEAD OF UPDATE, DELETE
  AS
  BEGIN
    SET NOCOUNT ON;
    THROW 51231, ''PlatformAudit es append-only.'', 1;
  END;');

  EXEC(N'
  CREATE OR ALTER TRIGGER orion.TR_Company_PlatformAudit
  ON orion.Company
  AFTER INSERT, UPDATE
  AS
  BEGIN
    SET NOCOUNT ON;

    DECLARE @Actor nvarchar(256) = COALESCE(
      CONVERT(nvarchar(256), SESSION_CONTEXT(N''OrionERP.UserName'')),
      CONVERT(nvarchar(256), ORIGINAL_LOGIN()));
    DECLARE @ApplicationName nvarchar(128) = COALESCE(
      CONVERT(nvarchar(128), SESSION_CONTEXT(N''OrionERP.Application'')),
      CONVERT(nvarchar(128), APP_NAME()));
    DECLARE @CorrelationId uniqueidentifier = TRY_CONVERT(
      uniqueidentifier, SESSION_CONTEXT(N''OrionERP.CorrelationId''));

    INSERT orion.PlatformAudit
    (
      Actor, ApplicationName, CorrelationId, [Action], EntityType, EntityKey,
      CompanyId, BeforeJson, AfterJson
    )
    SELECT @Actor, @ApplicationName, @CorrelationId,
           CASE WHEN previousRow.CompanyId IS NULL THEN ''INSERT'' ELSE ''UPDATE'' END,
           ''Company'', currentRow.Rfc, currentRow.CompanyId,
           CASE WHEN previousRow.CompanyId IS NULL THEN NULL ELSE
             (SELECT previousRow.CompanyId, previousRow.Rfc, previousRow.TaxRfc,
                     previousRow.LegacyTenantKey, previousRow.IsActive
              FOR JSON PATH, WITHOUT_ARRAY_WRAPPER) END,
           (SELECT currentRow.CompanyId, currentRow.Rfc, currentRow.TaxRfc,
                   currentRow.LegacyTenantKey, currentRow.IsActive
            FOR JSON PATH, WITHOUT_ARRAY_WRAPPER)
    FROM inserted currentRow
    LEFT JOIN deleted previousRow ON previousRow.CompanyId = currentRow.CompanyId;
  END;');

  EXEC(N'
  CREATE OR ALTER TRIGGER orion.TR_Module_GuardAudit
  ON orion.Module
  AFTER INSERT, UPDATE, DELETE
  AS
  BEGIN
    SET NOCOUNT ON;

    IF EXISTS
    (
      SELECT 1
      FROM deleted previousRow
      WHERE previousRow.ModuleCode = ''ACCOUNTING_CORE''
        AND NOT EXISTS
        (
          SELECT 1 FROM inserted currentRow
          WHERE currentRow.ModuleCode = previousRow.ModuleCode
        )
    )
      THROW 51232, ''ACCOUNTING_CORE no puede eliminarse ni cambiar de clave.'', 1;

    IF EXISTS
    (
      SELECT 1 FROM inserted
      WHERE ModuleCode = ''ACCOUNTING_CORE''
        AND (IsCore = 0 OR RequiresSite = 1 OR IsActive = 0)
    )
      THROW 51233, ''ACCOUNTING_CORE debe permanecer core, activo y sin requisito de sitio.'', 1;

    IF EXISTS
    (
      SELECT 1
      FROM inserted currentRow
      WHERE currentRow.IsActive = 0
        AND EXISTS
        (
          SELECT 1 FROM orion.CompanyModule assignment
          WHERE assignment.ModuleCode = currentRow.ModuleCode
            AND assignment.IsEnabled = 1
        )
    )
      THROW 51234, ''No puede desactivarse un modulo con empresas habilitadas.'', 1;

    DECLARE @Actor nvarchar(256) = COALESCE(CONVERT(nvarchar(256), SESSION_CONTEXT(N''OrionERP.UserName'')), CONVERT(nvarchar(256), ORIGINAL_LOGIN()));
    DECLARE @ApplicationName nvarchar(128) = COALESCE(CONVERT(nvarchar(128), SESSION_CONTEXT(N''OrionERP.Application'')), CONVERT(nvarchar(128), APP_NAME()));
    DECLARE @CorrelationId uniqueidentifier = TRY_CONVERT(uniqueidentifier, SESSION_CONTEXT(N''OrionERP.CorrelationId''));

    INSERT orion.PlatformAudit
    (
      Actor, ApplicationName, CorrelationId, [Action], EntityType, EntityKey,
      ModuleCode, BeforeJson, AfterJson
    )
    SELECT @Actor, @ApplicationName, @CorrelationId,
           CASE WHEN previousRow.ModuleCode IS NULL THEN ''INSERT''
                WHEN currentRow.ModuleCode IS NULL THEN ''DELETE'' ELSE ''UPDATE'' END,
           ''Module'', COALESCE(currentRow.ModuleCode, previousRow.ModuleCode),
           COALESCE(currentRow.ModuleCode, previousRow.ModuleCode),
           CASE WHEN previousRow.ModuleCode IS NULL THEN NULL ELSE
             (SELECT previousRow.ModuleCode, previousRow.DisplayName, previousRow.IsCore,
                     previousRow.RequiresSite, previousRow.IsActive
              FOR JSON PATH, WITHOUT_ARRAY_WRAPPER) END,
           CASE WHEN currentRow.ModuleCode IS NULL THEN NULL ELSE
             (SELECT currentRow.ModuleCode, currentRow.DisplayName, currentRow.IsCore,
                     currentRow.RequiresSite, currentRow.IsActive
              FOR JSON PATH, WITHOUT_ARRAY_WRAPPER) END
    FROM inserted currentRow
    FULL OUTER JOIN deleted previousRow
      ON previousRow.ModuleCode = currentRow.ModuleCode;
  END;');

  EXEC(N'
  CREATE OR ALTER TRIGGER orion.TR_Site_GuardAudit
  ON orion.Site
  AFTER INSERT, UPDATE, DELETE
  AS
  BEGIN
    SET NOCOUNT ON;

    IF EXISTS
    (
      SELECT 1
      FROM deleted previousRow
      JOIN inserted currentRow ON currentRow.SiteId = previousRow.SiteId
      WHERE currentRow.CompanyId <> previousRow.CompanyId
         OR currentRow.SiteKey <> previousRow.SiteKey
    )
      THROW 51235, ''CompanyId y SiteKey son inmutables para un sitio.'', 1;

    IF EXISTS
    (
      SELECT 1
      FROM inserted currentRow
      WHERE currentRow.IsActive = 0
        AND EXISTS
        (
          SELECT 1 FROM orion.PublicSite publicSite
          WHERE publicSite.CompanyId = currentRow.CompanyId
            AND publicSite.SiteId = currentRow.SiteId
            AND publicSite.IsActive = 1
        )
    )
      THROW 51236, ''No puede desactivarse un sitio con una superficie publica activa.'', 1;

    DECLARE @Actor nvarchar(256) = COALESCE(CONVERT(nvarchar(256), SESSION_CONTEXT(N''OrionERP.UserName'')), CONVERT(nvarchar(256), ORIGINAL_LOGIN()));
    DECLARE @ApplicationName nvarchar(128) = COALESCE(CONVERT(nvarchar(128), SESSION_CONTEXT(N''OrionERP.Application'')), CONVERT(nvarchar(128), APP_NAME()));
    DECLARE @CorrelationId uniqueidentifier = TRY_CONVERT(uniqueidentifier, SESSION_CONTEXT(N''OrionERP.CorrelationId''));

    INSERT orion.PlatformAudit
    (
      Actor, ApplicationName, CorrelationId, [Action], EntityType, EntityKey,
      CompanyId, SiteId, BeforeJson, AfterJson
    )
    SELECT @Actor, @ApplicationName, @CorrelationId,
           CASE WHEN previousRow.SiteId IS NULL THEN ''INSERT''
                WHEN currentRow.SiteId IS NULL THEN ''DELETE'' ELSE ''UPDATE'' END,
           ''Site'', CONVERT(nvarchar(450), COALESCE(currentRow.SiteId, previousRow.SiteId)),
           COALESCE(currentRow.CompanyId, previousRow.CompanyId),
           COALESCE(currentRow.SiteId, previousRow.SiteId),
           CASE WHEN previousRow.SiteId IS NULL THEN NULL ELSE
             (SELECT previousRow.SiteId, previousRow.CompanyId, previousRow.SiteKey,
                     previousRow.DisplayName, previousRow.TimeZoneId, previousRow.IsActive
              FOR JSON PATH, WITHOUT_ARRAY_WRAPPER) END,
           CASE WHEN currentRow.SiteId IS NULL THEN NULL ELSE
             (SELECT currentRow.SiteId, currentRow.CompanyId, currentRow.SiteKey,
                     currentRow.DisplayName, currentRow.TimeZoneId, currentRow.IsActive
              FOR JSON PATH, WITHOUT_ARRAY_WRAPPER) END
    FROM inserted currentRow
    FULL OUTER JOIN deleted previousRow ON previousRow.SiteId = currentRow.SiteId;
  END;');

  EXEC(N'
  CREATE OR ALTER TRIGGER orion.TR_CompanyModule_GuardAudit
  ON orion.CompanyModule
  AFTER INSERT, UPDATE, DELETE
  AS
  BEGIN
    SET NOCOUNT ON;

    IF EXISTS
    (
      SELECT 1
      FROM deleted previousRow
      WHERE previousRow.ModuleCode = ''ACCOUNTING_CORE''
        AND NOT EXISTS
        (
          SELECT 1 FROM inserted currentRow
          WHERE currentRow.CompanyId = previousRow.CompanyId
            AND currentRow.ModuleCode = previousRow.ModuleCode
            AND currentRow.[Status] = ''Enabled''
        )
    )
      THROW 51237, ''ACCOUNTING_CORE no puede deshabilitarse ni eliminarse para una empresa.'', 1;

    IF EXISTS
    (
      SELECT 1
      FROM inserted currentRow
      LEFT JOIN deleted previousRow
        ON previousRow.CompanyId = currentRow.CompanyId
       AND previousRow.ModuleCode = currentRow.ModuleCode
      WHERE currentRow.[Status] <> ''Enabled''
        AND (previousRow.[Status] = ''Enabled'' OR previousRow.[Status] IS NULL)
        AND EXISTS
        (
          SELECT 1 FROM orion.SiteCapability capability
          WHERE capability.CompanyId = currentRow.CompanyId
            AND capability.ModuleCode = currentRow.ModuleCode
            AND capability.IsEnabled = 1
        )
    )
      THROW 51238, ''No puede deshabilitarse un modulo con capacidades de sitio activas.'', 1;

    DECLARE @Actor nvarchar(256) = COALESCE(CONVERT(nvarchar(256), SESSION_CONTEXT(N''OrionERP.UserName'')), CONVERT(nvarchar(256), ORIGINAL_LOGIN()));
    DECLARE @ApplicationName nvarchar(128) = COALESCE(CONVERT(nvarchar(128), SESSION_CONTEXT(N''OrionERP.Application'')), CONVERT(nvarchar(128), APP_NAME()));
    DECLARE @CorrelationId uniqueidentifier = TRY_CONVERT(uniqueidentifier, SESSION_CONTEXT(N''OrionERP.CorrelationId''));

    INSERT orion.PlatformAudit
    (
      Actor, ApplicationName, CorrelationId, [Action], EntityType, EntityKey,
      CompanyId, ModuleCode, BeforeJson, AfterJson
    )
    SELECT @Actor, @ApplicationName, @CorrelationId,
           CASE WHEN previousRow.CompanyId IS NULL THEN ''INSERT''
                WHEN currentRow.CompanyId IS NULL THEN ''DELETE'' ELSE ''UPDATE'' END,
           ''CompanyModule'',
           CONCAT(COALESCE(currentRow.CompanyId, previousRow.CompanyId), N''|'', COALESCE(currentRow.ModuleCode, previousRow.ModuleCode)),
           COALESCE(currentRow.CompanyId, previousRow.CompanyId),
           COALESCE(currentRow.ModuleCode, previousRow.ModuleCode),
           CASE WHEN previousRow.CompanyId IS NULL THEN NULL ELSE
             (SELECT previousRow.CompanyId, previousRow.ModuleCode, previousRow.[Status],
                     previousRow.IsEnabled, previousRow.ConfigurationVersion,
                     previousRow.EffectiveFromUtc, previousRow.EffectiveToUtc
              FOR JSON PATH, WITHOUT_ARRAY_WRAPPER) END,
           CASE WHEN currentRow.CompanyId IS NULL THEN NULL ELSE
             (SELECT currentRow.CompanyId, currentRow.ModuleCode, currentRow.[Status],
                     currentRow.IsEnabled, currentRow.ConfigurationVersion,
                     currentRow.EffectiveFromUtc, currentRow.EffectiveToUtc
              FOR JSON PATH, WITHOUT_ARRAY_WRAPPER) END
    FROM inserted currentRow
    FULL OUTER JOIN deleted previousRow
      ON previousRow.CompanyId = currentRow.CompanyId
     AND previousRow.ModuleCode = currentRow.ModuleCode;
  END;');

  EXEC(N'
  CREATE OR ALTER TRIGGER orion.TR_SiteCapability_GuardAudit
  ON orion.SiteCapability
  AFTER INSERT, UPDATE, DELETE
  AS
  BEGIN
    SET NOCOUNT ON;

    IF EXISTS
    (
      SELECT 1
      FROM inserted currentRow
      JOIN orion.Site siteInfo
        ON siteInfo.CompanyId = currentRow.CompanyId
       AND siteInfo.SiteId = currentRow.SiteId
      JOIN orion.Company companyInfo ON companyInfo.CompanyId = currentRow.CompanyId
      JOIN orion.CompanyModule assignment
        ON assignment.CompanyId = currentRow.CompanyId
       AND assignment.ModuleCode = currentRow.ModuleCode
      JOIN orion.Module moduleInfo ON moduleInfo.ModuleCode = currentRow.ModuleCode
      WHERE currentRow.IsEnabled = 1
        AND
        (
          siteInfo.IsActive = 0
          OR companyInfo.IsActive = 0
          OR assignment.IsEnabled = 0
          OR moduleInfo.IsActive = 0
          OR moduleInfo.RequiresSite = 0
        )
    )
      THROW 51239, ''Una capacidad activa requiere empresa, sitio y modulo habilitados, y un modulo que requiera sitio.'', 1;

    IF EXISTS
    (
      SELECT 1
      FROM inserted currentRow
      LEFT JOIN deleted previousRow
        ON previousRow.CompanyId = currentRow.CompanyId
       AND previousRow.SiteId = currentRow.SiteId
       AND previousRow.ModuleCode = currentRow.ModuleCode
      WHERE currentRow.IsEnabled = 0
        AND (previousRow.IsEnabled = 1 OR previousRow.IsEnabled IS NULL)
        AND EXISTS
        (
          SELECT 1 FROM orion.PublicSite publicSite
          WHERE publicSite.CompanyId = currentRow.CompanyId
            AND publicSite.SiteId = currentRow.SiteId
            AND publicSite.ModuleCode = currentRow.ModuleCode
            AND publicSite.IsActive = 1
        )
    )
      THROW 51240, ''No puede deshabilitarse una capacidad con una superficie publica activa.'', 1;

    DECLARE @Actor nvarchar(256) = COALESCE(CONVERT(nvarchar(256), SESSION_CONTEXT(N''OrionERP.UserName'')), CONVERT(nvarchar(256), ORIGINAL_LOGIN()));
    DECLARE @ApplicationName nvarchar(128) = COALESCE(CONVERT(nvarchar(128), SESSION_CONTEXT(N''OrionERP.Application'')), CONVERT(nvarchar(128), APP_NAME()));
    DECLARE @CorrelationId uniqueidentifier = TRY_CONVERT(uniqueidentifier, SESSION_CONTEXT(N''OrionERP.CorrelationId''));

    INSERT orion.PlatformAudit
    (
      Actor, ApplicationName, CorrelationId, [Action], EntityType, EntityKey,
      CompanyId, SiteId, ModuleCode, BeforeJson, AfterJson
    )
    SELECT @Actor, @ApplicationName, @CorrelationId,
           CASE WHEN previousRow.CompanyId IS NULL THEN ''INSERT''
                WHEN currentRow.CompanyId IS NULL THEN ''DELETE'' ELSE ''UPDATE'' END,
           ''SiteCapability'',
           CONCAT(COALESCE(currentRow.CompanyId, previousRow.CompanyId), N''|'',
                  COALESCE(currentRow.SiteId, previousRow.SiteId), N''|'',
                  COALESCE(currentRow.ModuleCode, previousRow.ModuleCode)),
           COALESCE(currentRow.CompanyId, previousRow.CompanyId),
           COALESCE(currentRow.SiteId, previousRow.SiteId),
           COALESCE(currentRow.ModuleCode, previousRow.ModuleCode),
           CASE WHEN previousRow.CompanyId IS NULL THEN NULL ELSE
             (SELECT previousRow.CompanyId, previousRow.SiteId, previousRow.ModuleCode, previousRow.IsEnabled
              FOR JSON PATH, WITHOUT_ARRAY_WRAPPER) END,
           CASE WHEN currentRow.CompanyId IS NULL THEN NULL ELSE
             (SELECT currentRow.CompanyId, currentRow.SiteId, currentRow.ModuleCode, currentRow.IsEnabled
              FOR JSON PATH, WITHOUT_ARRAY_WRAPPER) END
    FROM inserted currentRow
    FULL OUTER JOIN deleted previousRow
      ON previousRow.CompanyId = currentRow.CompanyId
     AND previousRow.SiteId = currentRow.SiteId
     AND previousRow.ModuleCode = currentRow.ModuleCode;
  END;');

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
                     previousRow.BrandingVersion, previousRow.ContentVersion
              FOR JSON PATH, WITHOUT_ARRAY_WRAPPER) END,
           CASE WHEN currentRow.PublicSiteId IS NULL THEN NULL ELSE
             (SELECT currentRow.PublicSiteId, currentRow.PublicSiteKey, currentRow.CompanyId,
                     currentRow.SiteId, currentRow.ModuleCode, currentRow.CanonicalHost,
                     currentRow.IsActive, currentRow.ConfigurationVersion,
                     currentRow.BrandingVersion, currentRow.ContentVersion
              FOR JSON PATH, WITHOUT_ARRAY_WRAPPER) END
    FROM inserted currentRow
    FULL OUTER JOIN deleted previousRow
      ON previousRow.PublicSiteId = currentRow.PublicSiteId;
  END;');

  INSERT orion.Module
  (
    ModuleCode, DisplayName, [Description], IsCore, RequiresSite, IsActive, UpdatedBy
  )
  SELECT seed.ModuleCode, seed.DisplayName, seed.[Description],
         seed.IsCore, seed.RequiresSite, 1, @MigrationId
  FROM
  (
    VALUES
      ('ACCOUNTING_CORE', N'Contabilidad', N'Capacidad contable base, siempre disponible para toda empresa.', CONVERT(bit, 1), CONVERT(bit, 0)),
      ('HOSPITALITY', N'Hospedaje y reservaciones', N'Operacion publica y administrativa de hospedaje.', CONVERT(bit, 0), CONVERT(bit, 1)),
      ('RESTAURANT', N'Restaurante', N'Operacion publica y administrativa de restaurante.', CONVERT(bit, 0), CONVERT(bit, 1))
  ) seed(ModuleCode, DisplayName, [Description], IsCore, RequiresSite)
  WHERE NOT EXISTS
  (
    SELECT 1 FROM orion.Module existing
    WHERE existing.ModuleCode = seed.ModuleCode
  );

  IF NOT EXISTS
  (
    SELECT 1 FROM orion.Module
    WHERE ModuleCode = 'ACCOUNTING_CORE'
      AND IsCore = 1 AND RequiresSite = 0 AND IsActive = 1
  )
    THROW 51243, 'El catalogo no conserva ACCOUNTING_CORE como modulo core obligatorio.', 1;

  IF NOT EXISTS
  (
    SELECT 1 FROM orion.Module
    WHERE ModuleCode = 'HOSPITALITY'
      AND IsCore = 0 AND RequiresSite = 1 AND IsActive = 1
  )
    THROW 51244, 'El catalogo HOSPITALITY no coincide con la definicion aprobada.', 1;

  IF NOT EXISTS
  (
    SELECT 1 FROM orion.Module
    WHERE ModuleCode = 'RESTAURANT'
      AND IsCore = 0 AND RequiresSite = 1 AND IsActive = 1
  )
    THROW 51245, 'El catalogo RESTAURANT no coincide con la definicion aprobada.', 1;

  IF NOT EXISTS
  (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'orion.Company')
      AND name = N'UX_orion_Company_CompanyId'
      AND is_unique = 1
  )
    THROW 51246, 'Falta la clave alterna unica CompanyId.', 1;

  IF NOT EXISTS
  (
    SELECT 1 FROM sys.foreign_keys
    WHERE parent_object_id = OBJECT_ID(N'orion.PublicSite')
      AND name = N'FK_orion_PublicSite_SiteCapability'
      AND is_not_trusted = 0
  )
    THROW 51247, 'PublicSite no conserva la FK confiable hacia SiteCapability.', 1;

  IF NOT EXISTS
  (
    SELECT 1 FROM sys.foreign_keys
    WHERE parent_object_id = OBJECT_ID(N'orion.PublicSite')
      AND name = N'FK_orion_PublicSite_CompanyModule'
      AND is_not_trusted = 0
  )
    THROW 51248, 'PublicSite no conserva la FK confiable hacia CompanyModule.', 1;

  IF NOT EXISTS
  (
    SELECT 1 FROM sys.foreign_keys
    WHERE parent_object_id = OBJECT_ID(N'orion.PublicSite')
      AND name = N'FK_orion_PublicSite_Site'
      AND is_not_trusted = 0
  )
    THROW 51249, 'PublicSite no conserva la FK confiable hacia Site.', 1;

  IF NOT EXISTS
  (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'orion.PublicSite')
      AND name = N'SiteId'
      AND is_nullable = 0
  )
    THROW 51250, 'PublicSite.SiteId debe ser obligatorio.', 1;

  IF NOT EXISTS
  (
    SELECT 1 FROM sys.triggers
    WHERE parent_id = OBJECT_ID(N'orion.CompanyModule')
      AND name = N'TR_CompanyModule_GuardAudit'
      AND is_disabled = 0
  )
    THROW 51251, 'Falta la guarda auditable de CompanyModule.', 1;

  DECLARE @CompanyRowsAfter bigint;
  DECLARE @TaxRfcRowsAfter bigint;
  DECLARE @LegacyTenantKeyRowsAfter bigint;
  DECLARE @SiteRowsAfter bigint;
  DECLARE @CompanyModuleRowsAfter bigint;
  DECLARE @SiteCapabilityRowsAfter bigint;
  DECLARE @PublicSiteRowsAfter bigint;

  SELECT @CompanyRowsAfter = COUNT_BIG(*),
         @TaxRfcRowsAfter = COUNT_BIG(TaxRfc),
         @LegacyTenantKeyRowsAfter = COUNT_BIG(LegacyTenantKey)
  FROM orion.Company;

  SELECT @SiteRowsAfter = COUNT_BIG(*) FROM orion.Site;
  SELECT @CompanyModuleRowsAfter = COUNT_BIG(*) FROM orion.CompanyModule;
  SELECT @SiteCapabilityRowsAfter = COUNT_BIG(*) FROM orion.SiteCapability;
  SELECT @PublicSiteRowsAfter = COUNT_BIG(*) FROM orion.PublicSite;

  IF @CompanyRowsAfter <> @CompanyRowsBefore
     OR @TaxRfcRowsAfter <> @TaxRfcRowsBefore
     OR @LegacyTenantKeyRowsAfter <> @LegacyTenantKeyRowsBefore
     OR @SiteRowsAfter <> @SiteRowsBefore
     OR @CompanyModuleRowsAfter <> @CompanyModuleRowsBefore
     OR @SiteCapabilityRowsAfter <> @SiteCapabilityRowsBefore
     OR @PublicSiteRowsAfter <> @PublicSiteRowsBefore
    THROW 51252, 'La migracion intento inferir o rellenar asignaciones legacy.', 1;

  SELECT
    @MigrationId AS MigrationId,
    @MigrationChecksum AS MigrationChecksum,
    DB_NAME() AS DatabaseName,
    @ApplyChanges AS ApplyChanges,
    CASE WHEN @ApplyChanges = 1 THEN N'APPLY_SOLICITADO' ELSE N'PREVIEW_SIN_CAMBIOS' END AS ExecutionMode;

  SELECT
    companyInfo.CompanyId,
    companyInfo.Rfc AS LegacyRfcPrimaryKey,
    companyInfo.TaxRfc,
    companyInfo.LegacyTenantKey,
    companyInfo.DisplayName,
    companyInfo.IsActive,
    CASE
      WHEN companyInfo.TaxRfc IS NULL AND companyInfo.LegacyTenantKey IS NULL THEN N'PENDIENTE_MAPEO_EXPLICITO'
      WHEN companyInfo.TaxRfc IS NULL THEN N'PENDIENTE_TAX_RFC'
      WHEN companyInfo.LegacyTenantKey IS NULL THEN N'PENDIENTE_LEGACY_TENANT_KEY'
      ELSE N'MAPEADO'
    END AS ReconciliationStatus
  FROM orion.Company companyInfo
  ORDER BY companyInfo.CompanyId;

  SELECT
    moduleInfo.ModuleCode,
    moduleInfo.DisplayName,
    moduleInfo.IsCore,
    moduleInfo.RequiresSite,
    moduleInfo.IsActive,
    COUNT(DISTINCT assignment.CompanyId) AS AssignedCompanies,
    COUNT(DISTINCT CASE WHEN assignment.[Status] = 'Provisioning' THEN assignment.CompanyId END) AS ProvisioningCompanies,
    COUNT(DISTINCT CASE WHEN assignment.[Status] = 'Enabled' THEN assignment.CompanyId END) AS EnabledCompanies,
    COUNT(DISTINCT CASE WHEN assignment.[Status] = 'Suspended' THEN assignment.CompanyId END) AS SuspendedCompanies,
    COUNT(DISTINCT capability.SiteId) AS AssignedSites,
    COUNT(DISTINCT publicSite.PublicSiteId) AS PublicSites
  FROM orion.Module moduleInfo
  LEFT JOIN orion.CompanyModule assignment ON assignment.ModuleCode = moduleInfo.ModuleCode
  LEFT JOIN orion.SiteCapability capability
    ON capability.CompanyId = assignment.CompanyId
   AND capability.ModuleCode = assignment.ModuleCode
  LEFT JOIN orion.PublicSite publicSite
    ON publicSite.CompanyId = capability.CompanyId
   AND publicSite.SiteId = capability.SiteId
   AND publicSite.ModuleCode = capability.ModuleCode
  GROUP BY moduleInfo.ModuleCode, moduleInfo.DisplayName,
           moduleInfo.IsCore, moduleInfo.RequiresSite, moduleInfo.IsActive
  ORDER BY moduleInfo.ModuleCode;

  SELECT
    @CompanyRowsAfter AS Companies,
    @CompanyRowsAfter - @TaxRfcRowsAfter AS CompaniesWithoutTaxRfc,
    @CompanyRowsAfter - @LegacyTenantKeyRowsAfter AS CompaniesWithoutLegacyTenantKey,
    @SiteRowsAfter AS Sites,
    @CompanyModuleRowsAfter AS CompanyModuleAssignments,
    @SiteCapabilityRowsAfter AS SiteCapabilities,
    @PublicSiteRowsAfter AS PublicSites,
    (SELECT COUNT_BIG(*) FROM orion.PlatformAudit) AS PlatformAuditRows;

  IF @ApplyChanges = 1
  BEGIN
    IF NOT EXISTS
    (
      SELECT 1 FROM orion.SchemaMigration
      WHERE MigrationId = @MigrationId
    )
    BEGIN
      INSERT INTO orion.SchemaMigration
      (
        MigrationId, Checksum, AppliedBy, AppVersion, DatabaseName
      )
      VALUES
      (
        @MigrationId, @MigrationChecksum,
        COALESCE(CONVERT(nvarchar(256), SESSION_CONTEXT(N'OrionERP.UserName')), CONVERT(nvarchar(256), ORIGINAL_LOGIN())),
        @AppVersion, DB_NAME()
      );
    END;

    IF EXISTS
    (
      SELECT 1 FROM orion.SchemaMigration
      WHERE MigrationId = @MigrationId
        AND UPPER(Checksum) <> UPPER(@MigrationChecksum)
    )
      THROW 51253, 'SchemaMigration no coincide con el checksum aplicado.', 1;

    COMMIT TRANSACTION;
    SELECT N'APLICADO' AS Estado, DB_NAME() AS BaseDatos, @MigrationId AS MigrationId;
  END
  ELSE
  BEGIN
    ROLLBACK TRANSACTION;
    SELECT N'VALIDADO_SIN_CAMBIOS' AS Estado, DB_NAME() AS BaseDatos, @MigrationId AS MigrationId;
  END;

  DROP TABLE #OrionPlatformFoundationState;
END TRY
BEGIN CATCH
  IF XACT_STATE() <> 0
    ROLLBACK TRANSACTION;

  IF OBJECT_ID(N'tempdb..#OrionPlatformFoundationState', N'U') IS NOT NULL
    DROP TABLE #OrionPlatformFoundationState;

  THROW;
END CATCH;
