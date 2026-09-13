SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

DECLARE @ExpectedDatabase sysname=N'$(ExpectedDatabase)';
DECLARE @ApplyChangesInput nvarchar(20)=N'$(ApplyChanges)';
DECLARE @ApplyChanges bit=0;
DECLARE @MigrationId nvarchar(200)=N'$(MigrationId)';
DECLARE @MigrationChecksum varchar(128)='$(MigrationChecksum)';
DECLARE @AppVersionInput nvarchar(64)=N'$(AppVersion)';
DECLARE @AppVersion nvarchar(64)=NULL;

IF @ApplyChangesInput NOT LIKE N'$'+N'(%'
BEGIN
  IF @ApplyChangesInput NOT IN(N'0',N'1') THROW 53520,'ApplyChanges debe ser 0 o 1.',1;
  SET @ApplyChanges=CONVERT(bit,@ApplyChangesInput);
END;
IF @ExpectedDatabase LIKE N'$'+N'(%'
   OR @ExpectedDatabase NOT IN(N'Orion_Sandbox',N'grupocarpio',N'Orion_CutoverValidation_20260908')
  THROW 53521,'La base esperada no está autorizada para esta migración.',1;
IF DB_NAME()<>@ExpectedDatabase THROW 53522,'La conexión no apunta a la base declarada.',1;
IF @MigrationId<>N'20260912_rfc_tenant_isolation_company_controls'
  THROW 53523,'MigrationId incorrecto.',1;
IF @MigrationChecksum LIKE '$'+N'(%' OR LEN(@MigrationChecksum)<>64
   OR @MigrationChecksum LIKE '%[^0-9A-F]%' COLLATE Latin1_General_100_BIN2
  THROW 53524,'MigrationChecksum inválido.',1;
IF @AppVersionInput NOT LIKE N'$'+N'(%'
  SET @AppVersion=NULLIF(LTRIM(RTRIM(@AppVersionInput)),N'');

IF OBJECT_ID(N'orion.SchemaMigration',N'U') IS NULL
   OR NOT EXISTS(SELECT 1 FROM orion.SchemaMigration WHERE MigrationId=N'20260912_rfc_tenant_isolation_expand')
   OR OBJECT_ID(N'orion.TenantTableClassification',N'U') IS NULL
   OR OBJECT_ID(N'orion.TenantIsolationMigrationAudit',N'U') IS NULL
   OR OBJECT_ID(N'contabilidad.AccountingScopePolicy',N'SP') IS NULL
   OR OBJECT_ID(N'contabilidad.fn_AccountingScopePredicate',N'IF') IS NULL
  THROW 53525,'Falta la expansión RFC o la política contable requerida.',1;

DECLARE @ExistingChecksum char(64)=(SELECT Checksum FROM orion.SchemaMigration WHERE MigrationId=@MigrationId);
IF @ExistingChecksum IS NOT NULL AND UPPER(@ExistingChecksum)<>UPPER(@MigrationChecksum)
  THROW 53526,'El mismo MigrationId ya existe con otro checksum.',1;
IF @ExistingChecksum IS NOT NULL
BEGIN
  SELECT N'YA_APLICADO' Estado,@MigrationId MigrationId,@ExistingChecksum Checksum;
  RETURN;
END;

BEGIN TRANSACTION;
DECLARE @LockResult int;
EXEC @LockResult=sys.sp_getapplock
  @Resource=N'OrionERP:RfcTenantIsolation:CompanyControls',@LockMode=N'Exclusive',
  @LockOwner=N'Transaction',@LockTimeout=15000;
IF @LockResult<0 THROW 53527,'No fue posible obtener el bloqueo de migración.',1;

BEGIN TRY
  IF NOT EXISTS
  (
    SELECT 1 FROM sys.security_predicates
    WHERE target_object_id=OBJECT_ID(N'contabilidad.AccountingPeriod') AND predicate_type=0
  )
    ALTER SECURITY POLICY contabilidad.AccountingScopePolicy
      ADD FILTER PREDICATE contabilidad.fn_AccountingScopePredicate(CompanyId) ON contabilidad.AccountingPeriod,
      ADD BLOCK PREDICATE contabilidad.fn_AccountingScopePredicate(CompanyId) ON contabilidad.AccountingPeriod AFTER INSERT,
      ADD BLOCK PREDICATE contabilidad.fn_AccountingScopePredicate(CompanyId) ON contabilidad.AccountingPeriod AFTER UPDATE;

  IF NOT EXISTS
  (
    SELECT 1 FROM sys.security_predicates
    WHERE target_object_id=OBJECT_ID(N'contabilidad.CompanyCycleActivation') AND predicate_type=0
  )
    ALTER SECURITY POLICY contabilidad.AccountingScopePolicy
      ADD FILTER PREDICATE contabilidad.fn_AccountingScopePredicate(CompanyId) ON contabilidad.CompanyCycleActivation,
      ADD BLOCK PREDICATE contabilidad.fn_AccountingScopePredicate(CompanyId) ON contabilidad.CompanyCycleActivation AFTER INSERT,
      ADD BLOCK PREDICATE contabilidad.fn_AccountingScopePredicate(CompanyId) ON contabilidad.CompanyCycleActivation AFTER UPDATE;

  IF NOT EXISTS
  (
    SELECT 1 FROM sys.security_predicates
    WHERE target_object_id=OBJECT_ID(N'contabilidad.HospitalityAccountingMapping') AND predicate_type=0
  )
    ALTER SECURITY POLICY contabilidad.AccountingScopePolicy
      ADD FILTER PREDICATE contabilidad.fn_AccountingScopePredicate(CompanyId) ON contabilidad.HospitalityAccountingMapping,
      ADD BLOCK PREDICATE contabilidad.fn_AccountingScopePredicate(CompanyId) ON contabilidad.HospitalityAccountingMapping AFTER INSERT,
      ADD BLOCK PREDICATE contabilidad.fn_AccountingScopePredicate(CompanyId) ON contabilidad.HospitalityAccountingMapping AFTER UPDATE;

  UPDATE classification
  SET Classification='GLOBAL_PLATFORM_METADATA',OwnerColumn=NULL,ReviewedInMigrationId=@MigrationId,
      Notes=N'Orquestación de plataforma deliberadamente global; no contiene un maestro operativo compartido.'
  FROM orion.TenantTableClassification classification
  WHERE classification.SchemaName=N'orion'
    AND classification.TableName IN(N'CompanyModule',N'IntegrationBinding',N'ProvisioningOperation',N'SiteCapability');

  UPDATE classification
  SET OwnerColumn=NULL,ReviewedInMigrationId=@MigrationId,
      Notes=N'El RFC es fiscal, no el dueño del tenant; la propiedad se hereda del comprobante padre.'
  FROM orion.TenantTableClassification classification
  WHERE classification.SchemaName=N'cfdi' AND classification.TableName IN(N'Emisor',N'Receptor');

  INSERT orion.TenantIsolationMigrationAudit
    (MigrationId,EntityType,ActionCode,SourceId,Details)
  VALUES
    (@MigrationId,'TenantTableClassification','RLS_COMPANY_CONTROL','contabilidad.AccountingPeriod',N'Filtro y bloqueos CompanyId agregados a la política contable.'),
    (@MigrationId,'TenantTableClassification','RLS_COMPANY_CONTROL','contabilidad.CompanyCycleActivation',N'Filtro y bloqueos CompanyId agregados a la política contable.'),
    (@MigrationId,'TenantTableClassification','RLS_COMPANY_CONTROL','contabilidad.HospitalityAccountingMapping',N'Filtro y bloqueos CompanyId agregados a la política contable.'),
    (@MigrationId,'TenantTableClassification','CLASSIFY_GLOBAL_PLATFORM','orion.CompanyModule',N'Metadato global de orquestación.'),
    (@MigrationId,'TenantTableClassification','CLASSIFY_GLOBAL_PLATFORM','orion.IntegrationBinding',N'Metadato global de orquestación.'),
    (@MigrationId,'TenantTableClassification','CLASSIFY_GLOBAL_PLATFORM','orion.ProvisioningOperation',N'Metadato global de orquestación.'),
    (@MigrationId,'TenantTableClassification','CLASSIFY_GLOBAL_PLATFORM','orion.SiteCapability',N'Metadato global de orquestación.');

  IF EXISTS
  (
    SELECT required.TableName
    FROM(VALUES(N'AccountingPeriod'),(N'CompanyCycleActivation'),(N'HospitalityAccountingMapping')) required(TableName)
    LEFT JOIN sys.security_predicates predicateInfo
      ON predicateInfo.target_object_id=OBJECT_ID(N'contabilidad.'+QUOTENAME(required.TableName))
    LEFT JOIN sys.security_policies policyInfo
      ON policyInfo.object_id=predicateInfo.object_id AND policyInfo.is_enabled=1
    GROUP BY required.TableName
    HAVING SUM(CASE WHEN policyInfo.object_id IS NOT NULL AND predicateInfo.predicate_type=0 THEN 1 ELSE 0 END)<1
        OR SUM(CASE WHEN policyInfo.object_id IS NOT NULL AND predicateInfo.predicate_type=1 THEN 1 ELSE 0 END)<2
  ) THROW 53528,'Un control contable CompanyId no quedó fail-closed.',1;

  IF EXISTS
  (
    SELECT 1 FROM orion.TenantTableClassification
    WHERE SchemaName=N'orion' AND TableName IN(N'CompanyModule',N'IntegrationBinding',N'ProvisioningOperation',N'SiteCapability')
      AND Classification<>'GLOBAL_PLATFORM_METADATA'
  ) THROW 53529,'La clasificación de metadatos globales no quedó aplicada.',1;

  SELECT
    (SELECT COUNT(*) FROM sys.security_predicates predicateInfo JOIN sys.security_policies policyInfo ON policyInfo.object_id=predicateInfo.object_id
     WHERE policyInfo.is_enabled=1 AND predicateInfo.predicate_type=0
       AND predicateInfo.target_object_id IN(OBJECT_ID(N'contabilidad.AccountingPeriod'),OBJECT_ID(N'contabilidad.CompanyCycleActivation'),OBJECT_ID(N'contabilidad.HospitalityAccountingMapping'))) CompanyControlFilters,
    (SELECT COUNT(*) FROM orion.TenantTableClassification WHERE Classification='GLOBAL_PLATFORM_METADATA') GlobalPlatformTables;

  IF @ApplyChanges=1
  BEGIN
    INSERT orion.SchemaMigration(MigrationId,Checksum,AppliedBy,AppVersion,DatabaseName)
    VALUES(@MigrationId,@MigrationChecksum,
      COALESCE(CONVERT(nvarchar(256),SESSION_CONTEXT(N'OrionERP.UserName')),CONVERT(nvarchar(256),ORIGINAL_LOGIN())),
      @AppVersion,DB_NAME());
    COMMIT TRANSACTION;
    SELECT N'APLICADO' Estado,DB_NAME() BaseDatos,@MigrationId MigrationId;
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
