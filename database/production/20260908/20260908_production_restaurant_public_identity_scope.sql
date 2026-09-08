/*
  Production cutover 2026-09-08: 20260908_production_restaurant_public_identity_scope.
  Dedicated contract for the existing Bonhomia/Bruno installations only.
  DDL semantics reviewed from 20260903_restaurant_public_identity_scope_sandbox; original bytes remain untouched.
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

IF @ExpectedDatabase LIKE N'$' + N'(%'
   OR @ExpectedDatabase NOT IN (N'grupocarpio', N'Orion_CutoverValidation_20260908')
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
IF @MigrationId <> N'20260908_production_restaurant_public_identity_scope'
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
   OR OBJECT_ID(N'orion.Company', N'U') IS NULL
   OR OBJECT_ID(N'orion.Site', N'U') IS NULL
   OR OBJECT_ID(N'orion.PublicSite', N'U') IS NULL
   OR OBJECT_ID(N'brunos_auth.AspNetUsers', N'U') IS NULL
   OR OBJECT_ID(N'fidelidad.MemberAccount', N'U') IS NULL
  THROW 51405, 'Faltan la fundacion de plataforma o las tablas de identidad/membresia.', 1;

IF NOT EXISTS
(
  SELECT 1 FROM orion.SchemaMigration
  WHERE MigrationId=N'20260908_production_public_site_bindings'
)
  THROW 51406, 'El ledger no confirma el aprovisionamiento de sitios productivos.', 1;

DECLARE @ExistingChecksum char(64) =
(
  SELECT Checksum FROM orion.SchemaMigration WHERE MigrationId=@MigrationId
);
IF @ExistingChecksum IS NOT NULL
   AND UPPER(@ExistingChecksum) <> UPPER(@MigrationChecksum)
  THROW 51407, 'El mismo MigrationId ya existe con otro checksum.', 1;

DECLARE @ConfiguredPublicSiteKey varchar(100) = 'brunos-main';
DECLARE @ConfiguredCompanyRfc varchar(50) = 'BRUNOS260707L26';
DECLARE @ConfiguredSiteKey varchar(100) = 'brunos-01';
DECLARE @PublicSiteId bigint;
DECLARE @CompanyId bigint;
DECLARE @SiteId bigint;

IF OBJECT_ID(N'tempdb..#RestaurantPublicIdentityScopeState', N'U') IS NOT NULL
  DROP TABLE #RestaurantPublicIdentityScopeState;

CREATE TABLE #RestaurantPublicIdentityScopeState
(
  ExpectedDatabase sysname NOT NULL,
  ApplyChanges bit NOT NULL,
  MigrationId nvarchar(200) NOT NULL,
  MigrationChecksum varchar(128) NOT NULL,
  AppVersion nvarchar(64) NULL,
  ConfiguredPublicSiteKey varchar(100) NOT NULL,
  ConfiguredCompanyRfc varchar(50) NOT NULL,
  ConfiguredSiteKey varchar(100) NOT NULL,
  PublicSiteId bigint NOT NULL,
  CompanyId bigint NOT NULL,
  SiteId bigint NOT NULL
);

BEGIN TRY
  SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;
  BEGIN TRANSACTION;

  DECLARE @LockResult int;
  EXEC @LockResult = sys.sp_getapplock
    @Resource=N'OrionERP:ProductionCutover20260908:RestaurantPublicIdentityScope:20260903',
    @LockMode=N'Exclusive',
    @LockOwner=N'Transaction',
    @LockTimeout=15000;
  IF @LockResult < 0
    THROW 51408, 'No fue posible obtener el bloqueo exclusivo de migracion.', 1;

  SELECT
    @PublicSiteId=publicSite.PublicSiteId,
    @CompanyId=publicSite.CompanyId,
    @SiteId=publicSite.SiteId
  FROM orion.PublicSite publicSite
  JOIN orion.Company companyInfo
    ON companyInfo.CompanyId=publicSite.CompanyId
  JOIN orion.Site siteInfo
    ON siteInfo.CompanyId=publicSite.CompanyId
   AND siteInfo.SiteId=publicSite.SiteId
  JOIN orion.CompanyModule assignment
    ON assignment.CompanyId=publicSite.CompanyId
   AND assignment.ModuleCode=publicSite.ModuleCode
  JOIN orion.SiteCapability capability
    ON capability.CompanyId=publicSite.CompanyId
   AND capability.SiteId=publicSite.SiteId
   AND capability.ModuleCode=publicSite.ModuleCode
  WHERE publicSite.PublicSiteKey=@ConfiguredPublicSiteKey
    AND companyInfo.Rfc=@ConfiguredCompanyRfc
    AND siteInfo.SiteKey=@ConfiguredSiteKey
    AND publicSite.ModuleCode='RESTAURANT'
    AND publicSite.IsActive=1
    AND companyInfo.IsActive=1
    AND siteInfo.IsActive=1
    AND assignment.[Status]='Enabled'
    AND capability.IsEnabled=1;

  IF @PublicSiteId IS NULL OR @CompanyId IS NULL OR @SiteId IS NULL
    THROW 51409, 'brunos-main no coincide con el binding activo esperado de empresa/sede/RESTAURANT.', 1;

  -- Unlike the Sandbox fixture, production backfill must prove the legacy
  -- membership population has exactly one operational destination.
  IF (SELECT COUNT_BIG(*) FROM restaurante.Site WHERE Rfc=@ConfiguredCompanyRfc AND IsEnabled=1)<>1
     OR NOT EXISTS (SELECT 1 FROM restaurante.Site WHERE Rfc=@ConfiguredCompanyRfc AND SiteCode='BRUNOS-01' AND IsEnabled=1)
    THROW 51910, 'La identidad legacy no tiene una unica sede operativa Bruno habilitada.', 1;
  IF NOT EXISTS (SELECT 1 FROM orion.PublicSite WHERE PublicSiteId=@PublicSiteId AND CanonicalHost='brunosgarden.com')
    THROW 51911, 'El destino de identidad no corresponde al dominio productivo Bruno.', 1;
  IF EXISTS (SELECT IdentityUserId FROM fidelidad.MemberAccount GROUP BY IdentityUserId HAVING COUNT_BIG(*)<>1)
    THROW 51912, 'Una identidad tiene membresias ambiguas; se requiere reconciliacion antes del corte.', 1;
  SELECT N'BRUNO_IDENTITY_MEMBERSHIP_PROVENANCE' AS ProductionPreflight,
         (SELECT COUNT_BIG(*) FROM brunos_auth.AspNetUsers) AS LegacyIdentityUsers,
         (SELECT COUNT_BIG(*) FROM fidelidad.MemberAccount) AS LegacyMemberAccounts;


  IF EXISTS
  (
    SELECT 1
    FROM fidelidad.MemberAccount member
    WHERE member.Rfc<>@ConfiguredCompanyRfc
  )
    THROW 51410, 'Existen membresias de otro RFC en la identidad publica heredada; se requiere reconciliacion manual.', 1;

  IF EXISTS
  (
    SELECT 1
    FROM fidelidad.MemberAccount member
    LEFT JOIN brunos_auth.AspNetUsers identityUser
      ON identityUser.Id=member.IdentityUserId
    WHERE identityUser.Id IS NULL
  )
    THROW 51411, 'Existe una membresia sin cuenta de identidad correspondiente.', 1;

  IF EXISTS
  (
    SELECT 1
    FROM brunos_auth.AspNetUsers identityUser
    LEFT JOIN fidelidad.MemberAccount member
      ON member.IdentityUserId=identityUser.Id
    WHERE member.IdentityUserId IS NULL
  )
    THROW 51417, 'Existe una cuenta sin membresia; su empresa y sede requieren reconciliacion manual.', 1;

  INSERT #RestaurantPublicIdentityScopeState
    (ExpectedDatabase,ApplyChanges,MigrationId,MigrationChecksum,AppVersion,
     ConfiguredPublicSiteKey,ConfiguredCompanyRfc,ConfiguredSiteKey,
     PublicSiteId,CompanyId,SiteId)
  VALUES
    (@ExpectedDatabase,@ApplyChanges,@MigrationId,@MigrationChecksum,@AppVersion,
     @ConfiguredPublicSiteKey,@ConfiguredCompanyRfc,@ConfiguredSiteKey,
     @PublicSiteId,@CompanyId,@SiteId);

  IF COL_LENGTH(N'brunos_auth.AspNetUsers', N'PublicSiteId') IS NULL
    ALTER TABLE brunos_auth.AspNetUsers ADD PublicSiteId bigint NULL;

  IF COL_LENGTH(N'fidelidad.MemberAccount', N'PublicSiteId') IS NULL
    ALTER TABLE fidelidad.MemberAccount ADD PublicSiteId bigint NULL;

  IF COL_LENGTH(N'brunos_auth.AspNetUsers', N'PublicSiteId') IS NULL
     OR COL_LENGTH(N'fidelidad.MemberAccount', N'PublicSiteId') IS NULL
    THROW 51418, 'No fue posible agregar PublicSiteId a identidad y membresia.', 1;
END TRY
BEGIN CATCH
  IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
  IF OBJECT_ID(N'tempdb..#RestaurantPublicIdentityScopeState', N'U') IS NOT NULL
    DROP TABLE #RestaurantPublicIdentityScopeState;
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

IF OBJECT_ID(N'tempdb..#RestaurantPublicIdentityScopeState', N'U') IS NULL
BEGIN
  IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
  THROW 51425, 'Se perdio el estado de migracion entre batches.', 1;
END;

IF @@TRANCOUNT<>1 OR XACT_STATE()<>1
BEGIN
  IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
  DROP TABLE #RestaurantPublicIdentityScopeState;
  THROW 51426, 'La transaccion no sobrevivio al cambio de batch.', 1;
END;

DECLARE @ExpectedDatabase sysname;
DECLARE @ApplyChanges bit;
DECLARE @MigrationId nvarchar(200);
DECLARE @MigrationChecksum varchar(128);
DECLARE @AppVersion nvarchar(64);
DECLARE @ConfiguredPublicSiteKey varchar(100);
DECLARE @ConfiguredCompanyRfc varchar(50);
DECLARE @ConfiguredSiteKey varchar(100);
DECLARE @PublicSiteId bigint;
DECLARE @CompanyId bigint;
DECLARE @SiteId bigint;

SELECT
  @ExpectedDatabase=ExpectedDatabase,
  @ApplyChanges=ApplyChanges,
  @MigrationId=MigrationId,
  @MigrationChecksum=MigrationChecksum,
  @AppVersion=AppVersion,
  @ConfiguredPublicSiteKey=ConfiguredPublicSiteKey,
  @ConfiguredCompanyRfc=ConfiguredCompanyRfc,
  @ConfiguredSiteKey=ConfiguredSiteKey,
  @PublicSiteId=PublicSiteId,
  @CompanyId=CompanyId,
  @SiteId=SiteId
FROM #RestaurantPublicIdentityScopeState;

IF DB_NAME()<>@ExpectedDatabase
BEGIN
  ROLLBACK TRANSACTION;
  DROP TABLE #RestaurantPublicIdentityScopeState;
  THROW 51427, 'La base cambio entre batches; migracion cancelada.', 1;
END;

BEGIN TRY

  IF EXISTS
  (
    SELECT 1
    FROM brunos_auth.AspNetUsers
    WHERE PublicSiteId IS NOT NULL AND PublicSiteId<>@PublicSiteId
  )
    THROW 51412, 'Una cuenta ya esta ligada a otro PublicSite; no se reasignara.', 1;

  IF EXISTS
  (
    SELECT 1
    FROM fidelidad.MemberAccount
    WHERE PublicSiteId IS NOT NULL AND PublicSiteId<>@PublicSiteId
  )
    THROW 51413, 'Una membresia ya esta ligada a otro PublicSite; no se reasignara.', 1;

  UPDATE brunos_auth.AspNetUsers
  SET PublicSiteId=@PublicSiteId
  WHERE PublicSiteId IS NULL;

  UPDATE fidelidad.MemberAccount
  SET PublicSiteId=@PublicSiteId
  WHERE PublicSiteId IS NULL;

  IF EXISTS(SELECT 1 FROM brunos_auth.AspNetUsers WHERE PublicSiteId IS NULL)
     OR EXISTS(SELECT 1 FROM fidelidad.MemberAccount WHERE PublicSiteId IS NULL)
    THROW 51414, 'No fue posible completar el backfill de PublicSiteId.', 1;

  ALTER TABLE brunos_auth.AspNetUsers ALTER COLUMN PublicSiteId bigint NOT NULL;
  ALTER TABLE fidelidad.MemberAccount ALTER COLUMN PublicSiteId bigint NOT NULL;

  IF EXISTS
  (
    SELECT 1
    FROM brunos_auth.AspNetUsers
    GROUP BY PublicSiteId,NormalizedUserName
    HAVING NormalizedUserName IS NOT NULL AND COUNT_BIG(*)>1
  )
    THROW 51415, 'Existen nombres de usuario duplicados dentro del mismo PublicSite.', 1;

  IF EXISTS
  (
    SELECT 1
    FROM brunos_auth.AspNetUsers
    GROUP BY PublicSiteId,NormalizedEmail
    HAVING NormalizedEmail IS NOT NULL AND COUNT_BIG(*)>1
  )
    THROW 51416, 'Existen correos duplicados dentro del mismo PublicSite.', 1;

  IF EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'brunos_auth.AspNetUsers') AND name=N'UserNameIndex_Bruno')
    DROP INDEX UserNameIndex_Bruno ON brunos_auth.AspNetUsers;
  IF EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'brunos_auth.AspNetUsers') AND name=N'EmailIndex_Bruno')
    DROP INDEX EmailIndex_Bruno ON brunos_auth.AspNetUsers;

  CREATE UNIQUE INDEX UserNameIndex_Bruno
    ON brunos_auth.AspNetUsers(PublicSiteId,NormalizedUserName)
    WHERE NormalizedUserName IS NOT NULL;
  CREATE UNIQUE INDEX EmailIndex_Bruno
    ON brunos_auth.AspNetUsers(PublicSiteId,NormalizedEmail)
    WHERE NormalizedEmail IS NOT NULL;

  IF NOT EXISTS
  (
    SELECT 1 FROM sys.indexes
    WHERE object_id=OBJECT_ID(N'brunos_auth.AspNetUsers')
      AND name=N'UX_BrunoAspNetUsers_IdPublicSite'
  )
    CREATE UNIQUE INDEX UX_BrunoAspNetUsers_IdPublicSite
      ON brunos_auth.AspNetUsers(Id,PublicSiteId);

  IF NOT EXISTS
  (
    SELECT 1 FROM sys.indexes
    WHERE object_id=OBJECT_ID(N'fidelidad.MemberAccount')
      AND name=N'IX_LoyaltyMember_PublicSite'
  )
    CREATE INDEX IX_LoyaltyMember_PublicSite
      ON fidelidad.MemberAccount(PublicSiteId,[Status],Id);

  IF NOT EXISTS
  (
    SELECT 1 FROM sys.foreign_keys
    WHERE parent_object_id=OBJECT_ID(N'brunos_auth.AspNetUsers')
      AND name=N'FK_BrunoAspNetUsers_PublicSite'
  )
    ALTER TABLE brunos_auth.AspNetUsers WITH CHECK
      ADD CONSTRAINT FK_BrunoAspNetUsers_PublicSite
      FOREIGN KEY(PublicSiteId) REFERENCES orion.PublicSite(PublicSiteId);

  IF NOT EXISTS
  (
    SELECT 1 FROM sys.foreign_keys
    WHERE parent_object_id=OBJECT_ID(N'fidelidad.MemberAccount')
      AND name=N'FK_LoyaltyMember_PublicSite'
  )
    ALTER TABLE fidelidad.MemberAccount WITH CHECK
      ADD CONSTRAINT FK_LoyaltyMember_PublicSite
      FOREIGN KEY(PublicSiteId) REFERENCES orion.PublicSite(PublicSiteId);

  IF NOT EXISTS
  (
    SELECT 1 FROM sys.foreign_keys
    WHERE parent_object_id=OBJECT_ID(N'fidelidad.MemberAccount')
      AND name=N'FK_LoyaltyMember_IdentityScope'
  )
    ALTER TABLE fidelidad.MemberAccount WITH CHECK
      ADD CONSTRAINT FK_LoyaltyMember_IdentityScope
      FOREIGN KEY(IdentityUserId,PublicSiteId)
      REFERENCES brunos_auth.AspNetUsers(Id,PublicSiteId);

  EXEC(N'
  CREATE OR ALTER TRIGGER brunos_auth.TR_AspNetUsers_RestaurantIdentityScope
  ON brunos_auth.AspNetUsers
  AFTER INSERT, UPDATE
  AS
  BEGIN
    SET NOCOUNT ON;

    IF EXISTS
    (
      SELECT 1
      FROM inserted currentRow
      JOIN deleted previousRow ON previousRow.Id=currentRow.Id
      WHERE currentRow.PublicSiteId<>previousRow.PublicSiteId
    )
      THROW 51420, ''PublicSiteId es inmutable para una cuenta publica.'', 1;

    IF EXISTS
    (
      SELECT 1
      FROM inserted currentRow
      LEFT JOIN orion.PublicSite publicSite
        ON publicSite.PublicSiteId=currentRow.PublicSiteId
       AND publicSite.ModuleCode=''RESTAURANT''
      WHERE publicSite.PublicSiteId IS NULL
    )
      THROW 51421, ''La cuenta publica debe pertenecer a un PublicSite de restaurante.'', 1;
  END;');

  EXEC(N'
  CREATE OR ALTER TRIGGER fidelidad.TR_MemberAccount_PublicIdentityScope
  ON fidelidad.MemberAccount
  AFTER INSERT, UPDATE
  AS
  BEGIN
    SET NOCOUNT ON;

    IF EXISTS
    (
      SELECT 1
      FROM inserted currentRow
      JOIN deleted previousRow ON previousRow.Id=currentRow.Id
      WHERE currentRow.PublicSiteId<>previousRow.PublicSiteId
    )
      THROW 51422, ''PublicSiteId es inmutable para una membresia.'', 1;

    IF EXISTS
    (
      SELECT 1
      FROM inserted currentRow
      JOIN orion.PublicSite publicSite
        ON publicSite.PublicSiteId=currentRow.PublicSiteId
      JOIN orion.Company companyInfo
        ON companyInfo.CompanyId=publicSite.CompanyId
      WHERE publicSite.ModuleCode<>''RESTAURANT''
         OR companyInfo.Rfc<>currentRow.Rfc
    )
      THROW 51423, ''La membresia no coincide con la empresa y sede de su PublicSite.'', 1;
  END;');

  IF EXISTS
  (
    SELECT 1
    FROM fidelidad.MemberAccount member
    JOIN brunos_auth.AspNetUsers identityUser
      ON identityUser.Id=member.IdentityUserId
    JOIN orion.PublicSite publicSite
      ON publicSite.PublicSiteId=member.PublicSiteId
    JOIN orion.Company companyInfo
      ON companyInfo.CompanyId=publicSite.CompanyId
    WHERE member.PublicSiteId<>identityUser.PublicSiteId
       OR publicSite.ModuleCode<>'RESTAURANT'
       OR companyInfo.Rfc<>member.Rfc
  )
    THROW 51424, 'La validacion final encontro una membresia fuera de su identidad/empresa/sede.', 1;

  SELECT
    @ConfiguredPublicSiteKey AS PublicSiteKey,
    @ConfiguredCompanyRfc AS CompanyRfc,
    @ConfiguredSiteKey AS SiteKey,
    @PublicSiteId AS PublicSiteId,
    (SELECT COUNT_BIG(*) FROM brunos_auth.AspNetUsers WHERE PublicSiteId=@PublicSiteId) AS IdentityUsers,
    (SELECT COUNT_BIG(*) FROM fidelidad.MemberAccount WHERE PublicSiteId=@PublicSiteId) AS MemberAccounts,
    (SELECT COUNT_BIG(*)
       FROM fidelidad.MemberAccount member
       JOIN brunos_auth.AspNetUsers identityUser
         ON identityUser.Id=member.IdentityUserId
        AND identityUser.PublicSiteId=member.PublicSiteId
      WHERE member.PublicSiteId=@PublicSiteId) AS ScopedIdentityLinks;

  IF @ApplyChanges=1
  BEGIN
    IF NOT EXISTS(SELECT 1 FROM orion.SchemaMigration WHERE MigrationId=@MigrationId)
    BEGIN
      INSERT orion.SchemaMigration
        (MigrationId,Checksum,AppliedBy,AppVersion,DatabaseName)
      VALUES
        (@MigrationId,@MigrationChecksum,
         COALESCE(CONVERT(nvarchar(256),SESSION_CONTEXT(N'OrionERP.UserName')),CONVERT(nvarchar(256),ORIGINAL_LOGIN())),
         @AppVersion,DB_NAME());
    END;

    COMMIT TRANSACTION;
    SELECT N'APLICADO_CORTE_20260908' AS Estado,DB_NAME() AS BaseDatos,@MigrationId AS MigrationId;
  END
  ELSE
  BEGIN
    ROLLBACK TRANSACTION;
    SELECT N'VALIDADO_SIN_CAMBIOS' AS Estado,DB_NAME() AS BaseDatos,@MigrationId AS MigrationId;
  END;

  DROP TABLE #RestaurantPublicIdentityScopeState;
END TRY
BEGIN CATCH
  IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
  IF OBJECT_ID(N'tempdb..#RestaurantPublicIdentityScopeState', N'U') IS NOT NULL
    DROP TABLE #RestaurantPublicIdentityScopeState;
  THROW;
END CATCH;
