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
  IF @ApplyChangesInput NOT IN(N'0',N'1') THROW 53540,'ApplyChanges debe ser 0 o 1.',1;
  SET @ApplyChanges=CONVERT(bit,@ApplyChangesInput);
END;
IF @ExpectedDatabase LIKE N'$'+N'(%'
   OR @ExpectedDatabase NOT IN(N'Orion_Sandbox',N'grupocarpio',N'Orion_CutoverValidation_20260908')
  THROW 53541,'La base esperada no está autorizada para esta migración.',1;
IF DB_NAME()<>@ExpectedDatabase THROW 53542,'La conexión no apunta a la base declarada.',1;
IF @MigrationId<>N'20260912_rfc_tenant_isolation_fail_closed_principals'
  THROW 53543,'MigrationId incorrecto.',1;
IF @MigrationChecksum LIKE '$'+N'(%' OR LEN(@MigrationChecksum)<>64
   OR @MigrationChecksum LIKE '%[^0-9A-F]%' COLLATE Latin1_General_100_BIN2
  THROW 53544,'MigrationChecksum inválido.',1;
IF @AppVersionInput NOT LIKE N'$'+N'(%'
  SET @AppVersion=NULLIF(LTRIM(RTRIM(@AppVersionInput)),N'');

IF OBJECT_ID(N'orion.SchemaMigration',N'U') IS NULL
   OR NOT EXISTS(SELECT 1 FROM orion.SchemaMigration WHERE MigrationId=N'20260912_rfc_tenant_isolation_company_controls')
   OR OBJECT_ID(N'logistica.fn_RfcAccessPredicate',N'IF') IS NULL
   OR OBJECT_ID(N'rh.fn_RfcAccessPredicate',N'IF') IS NULL
   OR OBJECT_ID(N'orion.fn_HospitalityScopePredicate',N'IF') IS NULL
  THROW 53545,'Faltan las funciones o migraciones de aislamiento requeridas.',1;

DECLARE @ExistingChecksum char(64)=(SELECT Checksum FROM orion.SchemaMigration WHERE MigrationId=@MigrationId);
IF @ExistingChecksum IS NOT NULL AND UPPER(@ExistingChecksum)<>UPPER(@MigrationChecksum)
  THROW 53546,'El mismo MigrationId ya existe con otro checksum.',1;
IF @ExistingChecksum IS NOT NULL
BEGIN
  SELECT N'YA_APLICADO' Estado,@MigrationId MigrationId,@ExistingChecksum Checksum;
  RETURN;
END;

BEGIN TRANSACTION;
DECLARE @LockResult int;
EXEC @LockResult=sys.sp_getapplock
  @Resource=N'OrionERP:RfcTenantIsolation:FailClosedPrincipals',@LockMode=N'Exclusive',
  @LockOwner=N'Transaction',@LockTimeout=15000;
IF @LockResult<0 THROW 53547,'No fue posible obtener el bloqueo de migración.',1;

BEGIN TRY
  IF NOT EXISTS(SELECT 1 FROM sys.security_policies WHERE object_id=OBJECT_ID(N'logistica.RfcSecurityPolicy') AND is_enabled=1)
     OR NOT EXISTS(SELECT 1 FROM sys.security_policies WHERE object_id=OBJECT_ID(N'rh.RfcSecurityPolicy') AND is_enabled=1)
     OR NOT EXISTS(SELECT 1 FROM sys.security_policies WHERE object_id=OBJECT_ID(N'orion.HospitalityScopePolicy') AND is_enabled=1)
    THROW 53548,'Las políticas que se van a endurecer deben estar activas.',1;

  ALTER SECURITY POLICY logistica.RfcSecurityPolicy WITH(STATE=OFF);
  ALTER SECURITY POLICY rh.RfcSecurityPolicy WITH(STATE=OFF);
  ALTER SECURITY POLICY orion.HospitalityScopePolicy WITH(STATE=OFF);

  CREATE TABLE #PredicateRestore
  (
    PolicySchema sysname NOT NULL,
    PolicyName sysname NOT NULL,
    TargetSchema sysname NOT NULL,
    TargetTable sysname NOT NULL,
    PredicateType tinyint NOT NULL,
    OperationDesc nvarchar(60) NOT NULL,
    PredicateDefinition nvarchar(max) NOT NULL
  );
  INSERT #PredicateRestore
  SELECT OBJECT_SCHEMA_NAME(policyInfo.object_id),policyInfo.name,
    OBJECT_SCHEMA_NAME(predicateInfo.target_object_id),OBJECT_NAME(predicateInfo.target_object_id),
    predicateInfo.predicate_type,ISNULL(predicateInfo.operation_desc,N''),predicateInfo.predicate_definition
  FROM sys.security_policies policyInfo
  JOIN sys.security_predicates predicateInfo ON predicateInfo.object_id=policyInfo.object_id
  WHERE (policyInfo.object_id=OBJECT_ID(N'logistica.RfcSecurityPolicy') AND CHARINDEX(N'[logistica].[fn_RfcAccessPredicate]',predicateInfo.predicate_definition)>0)
     OR (policyInfo.object_id=OBJECT_ID(N'rh.RfcSecurityPolicy') AND CHARINDEX(N'[rh].[fn_RfcAccessPredicate]',predicateInfo.predicate_definition)>0)
     OR (policyInfo.object_id=OBJECT_ID(N'orion.HospitalityScopePolicy') AND CHARINDEX(N'[orion].[fn_HospitalityScopePredicate]',predicateInfo.predicate_definition)>0);

  DECLARE @DropPredicates nvarchar(max);
  SELECT @DropPredicates=STRING_AGG(CONVERT(nvarchar(max),
    N'ALTER SECURITY POLICY '+QUOTENAME(PolicySchema)+N'.'+QUOTENAME(PolicyName)+N' DROP '
    +CASE WHEN PredicateType=0 THEN N'FILTER' ELSE N'BLOCK' END+N' PREDICATE ON '
    +QUOTENAME(TargetSchema)+N'.'+QUOTENAME(TargetTable)
    +CASE WHEN PredicateType=1 THEN N' '+OperationDesc ELSE N'' END+N';'),CHAR(10))
  FROM #PredicateRestore;
  IF @DropPredicates IS NULL THROW 53552,'No se encontraron los predicados que deben reconstruirse.',1;
  EXEC sys.sp_executesql @DropPredicates;

  EXEC sys.sp_executesql N'
ALTER FUNCTION logistica.fn_RfcAccessPredicate(@Rfc varchar(50))
RETURNS TABLE WITH SCHEMABINDING AS RETURN
  SELECT 1 AS IsAllowed
  WHERE @Rfc=CONVERT(varchar(50),SESSION_CONTEXT(N''OrionRfc''))
    AND
    (
      EXISTS
      (
        SELECT 1
        FROM orion.PublicSqlPrincipalBinding binding
        JOIN orion.PublicSite publicSite ON publicSite.PublicSiteId=binding.PublicSiteId
        JOIN orion.Company company ON company.CompanyId=publicSite.CompanyId
        WHERE binding.PrincipalName=USER_NAME() AND binding.IsActive=CONVERT(bit,1)
          AND publicSite.PublicSiteId=TRY_CONVERT(bigint,SESSION_CONTEXT(N''OrionERP.PublicSiteId''))
          AND publicSite.IsActive=CONVERT(bit,1) AND company.IsActive=CONVERT(bit,1)
          AND company.Rfc=@Rfc
      )
      OR
      (
        NOT EXISTS(SELECT 1 FROM orion.PublicSqlPrincipalBinding binding WHERE binding.PrincipalName=USER_NAME())
        AND EXISTS
        (
          SELECT 1 FROM orion.Company company
          WHERE company.CompanyId=TRY_CONVERT(bigint,SESSION_CONTEXT(N''OrionERP.CompanyId''))
            AND company.Rfc=@Rfc AND company.IsActive=CONVERT(bit,1)
        )
      )
    );';

  EXEC sys.sp_executesql N'
ALTER FUNCTION rh.fn_RfcAccessPredicate(@Rfc varchar(50))
RETURNS TABLE WITH SCHEMABINDING AS RETURN
  SELECT 1 AS IsAllowed
  WHERE @Rfc=CONVERT(varchar(50),SESSION_CONTEXT(N''OrionRfc''))
    AND
    (
      EXISTS
      (
        SELECT 1
        FROM orion.PublicSqlPrincipalBinding binding
        JOIN orion.PublicSite publicSite ON publicSite.PublicSiteId=binding.PublicSiteId
        JOIN orion.Company company ON company.CompanyId=publicSite.CompanyId
        WHERE binding.PrincipalName=USER_NAME() AND binding.IsActive=CONVERT(bit,1)
          AND publicSite.PublicSiteId=TRY_CONVERT(bigint,SESSION_CONTEXT(N''OrionERP.PublicSiteId''))
          AND publicSite.IsActive=CONVERT(bit,1) AND company.IsActive=CONVERT(bit,1)
          AND company.Rfc=@Rfc
      )
      OR
      (
        NOT EXISTS(SELECT 1 FROM orion.PublicSqlPrincipalBinding binding WHERE binding.PrincipalName=USER_NAME())
        AND EXISTS
        (
          SELECT 1 FROM orion.Company company
          WHERE company.CompanyId=TRY_CONVERT(bigint,SESSION_CONTEXT(N''OrionERP.CompanyId''))
            AND company.Rfc=@Rfc AND company.IsActive=CONVERT(bit,1)
        )
      )
    );';

  EXEC sys.sp_executesql N'
ALTER FUNCTION orion.fn_HospitalityScopePredicate(@CompanyId bigint,@SiteId bigint)
RETURNS TABLE WITH SCHEMABINDING AS RETURN
  SELECT 1 AS Allowed
  WHERE @CompanyId>0 AND @SiteId>0
    AND @CompanyId=TRY_CONVERT(bigint,SESSION_CONTEXT(N''OrionERP.HospitalityCompanyId''))
    AND @SiteId=TRY_CONVERT(bigint,SESSION_CONTEXT(N''OrionERP.HospitalitySiteId''))
    AND
    (
      EXISTS
      (
        SELECT 1
        FROM orion.PublicSqlPrincipalBinding binding
        JOIN orion.PublicSite publicSite ON publicSite.PublicSiteId=binding.PublicSiteId
        WHERE binding.PrincipalName=USER_NAME() AND binding.IsActive=CONVERT(bit,1)
          AND publicSite.PublicSiteId=TRY_CONVERT(bigint,SESSION_CONTEXT(N''OrionERP.PublicSiteId''))
          AND publicSite.CompanyId=@CompanyId AND publicSite.SiteId=@SiteId
          AND publicSite.ModuleCode=''HOSPITALITY'' AND publicSite.IsActive=CONVERT(bit,1)
      )
      OR
      (
        NOT EXISTS(SELECT 1 FROM orion.PublicSqlPrincipalBinding binding WHERE binding.PrincipalName=USER_NAME())
        AND @CompanyId=TRY_CONVERT(bigint,SESSION_CONTEXT(N''OrionERP.CompanyId''))
        AND EXISTS
        (
          SELECT 1 FROM orion.Company company
          WHERE company.CompanyId=@CompanyId
            AND company.Rfc=CONVERT(varchar(50),SESSION_CONTEXT(N''OrionRfc''))
            AND company.IsActive=CONVERT(bit,1)
        )
      )
    );';

  DECLARE @RestorePredicates nvarchar(max);
  SELECT @RestorePredicates=STRING_AGG(CONVERT(nvarchar(max),
    N'ALTER SECURITY POLICY '+QUOTENAME(PolicySchema)+N'.'+QUOTENAME(PolicyName)+N' ADD '
    +CASE WHEN PredicateType=0 THEN N'FILTER' ELSE N'BLOCK' END+N' PREDICATE '
    +CASE WHEN LEFT(PredicateDefinition,1)=N'(' AND RIGHT(PredicateDefinition,1)=N')'
          THEN SUBSTRING(PredicateDefinition,2,LEN(PredicateDefinition)-2) ELSE PredicateDefinition END
    +N' ON '+QUOTENAME(TargetSchema)+N'.'+QUOTENAME(TargetTable)
    +CASE WHEN PredicateType=1 THEN N' '+OperationDesc ELSE N'' END+N';'),CHAR(10))
  FROM #PredicateRestore;
  EXEC sys.sp_executesql @RestorePredicates;

  ALTER SECURITY POLICY logistica.RfcSecurityPolicy WITH(STATE=ON);
  ALTER SECURITY POLICY rh.RfcSecurityPolicy WITH(STATE=ON);
  ALTER SECURITY POLICY orion.HospitalityScopePolicy WITH(STATE=ON);

  IF OBJECT_DEFINITION(OBJECT_ID(N'logistica.fn_RfcAccessPredicate')) LIKE N'%USER_NAME()=N''dbo''%'
     OR OBJECT_DEFINITION(OBJECT_ID(N'rh.fn_RfcAccessPredicate')) LIKE N'%USER_NAME()=N''dbo''%'
     OR OBJECT_DEFINITION(OBJECT_ID(N'orion.fn_HospitalityScopePredicate')) LIKE N'%USER_NAME()=N''dbo''%'
    THROW 53549,'Persistió un bypass RLS por nombre de usuario.',1;

  EXEC sys.sp_set_session_context @key=N'OrionRfc',@value=NULL;
  EXEC sys.sp_set_session_context @key=N'OrionERP.CompanyId',@value=NULL;
  EXEC sys.sp_set_session_context @key=N'OrionERP.HospitalityCompanyId',@value=NULL;
  EXEC sys.sp_set_session_context @key=N'OrionERP.HospitalitySiteId',@value=NULL;
  IF EXISTS(SELECT 1 FROM dbo.BusinessPartner)
    THROW 53550,'Una conexión administrativa sin contexto todavía puede leer socios.',1;

  DECLARE @OhmRfc varchar(50)='OHM191112Q26';
  DECLARE @OhmCompanyId bigint=(SELECT CompanyId FROM orion.Company WHERE Rfc=@OhmRfc AND IsActive=1);
  EXEC sys.sp_set_session_context @key=N'OrionRfc',@value=@OhmRfc;
  EXEC sys.sp_set_session_context @key=N'OrionERP.CompanyId',@value=@OhmCompanyId;
  IF NOT EXISTS(SELECT 1 FROM dbo.BusinessPartner WHERE OwnerRfc=@OhmRfc)
    THROW 53551,'El contexto privado válido no puede leer su propia empresa.',1;

  INSERT orion.TenantIsolationMigrationAudit
    (MigrationId,EntityType,ActionCode,SourceId,Details)
  VALUES
    (@MigrationId,'RlsPredicate','REMOVE_DBO_BYPASS','logistica.fn_RfcAccessPredicate',N'El acceso administrativo exige CompanyId/RFC de sesión coherentes.'),
    (@MigrationId,'RlsPredicate','REMOVE_DBO_BYPASS','rh.fn_RfcAccessPredicate',N'El acceso administrativo exige CompanyId/RFC de sesión coherentes.'),
    (@MigrationId,'RlsPredicate','REMOVE_DBO_BYPASS','orion.fn_HospitalityScopePredicate',N'El acceso privado exige CompanyId/RFC/sede coherentes; los principales públicos conservan su binding explícito.');

  EXEC sys.sp_set_session_context @key=N'OrionRfc',@value=NULL;
  EXEC sys.sp_set_session_context @key=N'OrionERP.CompanyId',@value=NULL;

  SELECT 3 HardenedPredicates,N'NO_UNSCOPED_DBO_BYPASS' EstadoValidacion;

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
