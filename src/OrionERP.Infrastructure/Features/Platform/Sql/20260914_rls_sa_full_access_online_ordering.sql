SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

-- Extiende el acceso completo de sa (20260914_rls_sa_full_access) al checkout en línea de Restaurante.
-- Va aparte porque la función nace con 20260914_restaurant_online_ordering, que llega a producción después.

DECLARE @ExpectedDatabase sysname=N'$(ExpectedDatabase)';
DECLARE @ApplyChangesInput nvarchar(20)=N'$(ApplyChanges)';
DECLARE @ApplyChanges bit=0;
DECLARE @MigrationId nvarchar(200)=N'$(MigrationId)';
DECLARE @MigrationChecksum varchar(128)='$(MigrationChecksum)';
DECLARE @AppVersionInput nvarchar(64)=N'$(AppVersion)';
DECLARE @AppVersion nvarchar(64)=NULL;

IF @ApplyChangesInput NOT LIKE N'$'+N'(%'
BEGIN
  IF @ApplyChangesInput NOT IN(N'0',N'1') THROW 54720,'ApplyChanges debe ser 0 o 1.',1;
  SET @ApplyChanges=CONVERT(bit,@ApplyChangesInput);
END;
IF @ExpectedDatabase LIKE N'$'+N'(%'
   OR @ExpectedDatabase NOT IN(N'Orion_Sandbox',N'grupocarpio')
  THROW 54721,'La base esperada no está autorizada para esta migración.',1;
IF DB_NAME()<>@ExpectedDatabase THROW 54722,'La conexión no apunta a la base declarada.',1;
IF @MigrationId<>N'20260914_rls_sa_full_access_online_ordering'
  THROW 54723,'MigrationId incorrecto.',1;
IF @MigrationChecksum LIKE '$'+N'(%' OR LEN(@MigrationChecksum)<>64
   OR @MigrationChecksum LIKE '%[^0-9A-F]%' COLLATE Latin1_General_100_BIN2
  THROW 54724,'MigrationChecksum inválido.',1;
IF @AppVersionInput NOT LIKE N'$'+N'(%'
  SET @AppVersion=NULLIF(LTRIM(RTRIM(@AppVersionInput)),N'');

IF SUSER_SID()=0x01
  THROW 54725,'Ejecute esta migración con el login de la aplicación, no con sa: la prueba fail-closed no demostraría nada.',1;

IF OBJECT_ID(N'orion.SchemaMigration',N'U') IS NULL
   OR OBJECT_ID(N'orion.TenantIsolationMigrationAudit',N'U') IS NULL
   OR NOT EXISTS(SELECT 1 FROM orion.SchemaMigration WHERE MigrationId=N'20260914_rls_sa_full_access')
   OR NOT EXISTS(SELECT 1 FROM orion.SchemaMigration WHERE MigrationId=N'20260914_restaurant_online_ordering')
  THROW 54726,'Faltan 20260914_rls_sa_full_access o 20260914_restaurant_online_ordering.',1;

DECLARE @ExistingChecksum char(64)=(SELECT Checksum FROM orion.SchemaMigration WHERE MigrationId=@MigrationId);
IF @ExistingChecksum IS NOT NULL AND UPPER(@ExistingChecksum)<>UPPER(@MigrationChecksum)
  THROW 54727,'El mismo MigrationId ya existe con otro checksum.',1;
IF @ExistingChecksum IS NOT NULL
BEGIN
  SELECT N'YA_APLICADO' Estado,@MigrationId MigrationId,@ExistingChecksum Checksum;
  RETURN;
END;

-- Huella de la función tal como la crea 20260914_restaurant_online_ordering. Si cambió, no se sobrescribe.
DECLARE @TargetFunctions TABLE(FunctionName sysname NOT NULL PRIMARY KEY,DefinitionHash varbinary(32) NOT NULL);
INSERT @TargetFunctions(FunctionName,DefinitionHash) VALUES
  (N'restaurante.fn_OnlineOrderingScopePredicate',0x62D29E393AF467CF065E776FA292F506AE1A53DA14884AE077A448964C93A9E9);

IF EXISTS
(
  SELECT 1 FROM @TargetFunctions target
  WHERE OBJECT_ID(target.FunctionName,N'IF') IS NULL
     OR HASHBYTES('SHA2_256',OBJECT_DEFINITION(OBJECT_ID(target.FunctionName)))<>target.DefinitionHash
)
  THROW 54728,'La función de predicado cambió desde que se preparó esta migración; no se sobrescribe.',1;

BEGIN TRANSACTION;
DECLARE @LockResult int;
EXEC @LockResult=sys.sp_getapplock
  @Resource=N'OrionERP:RlsSaFullAccess',@LockMode=N'Exclusive',
  @LockOwner=N'Transaction',@LockTimeout=15000;
IF @LockResult<0 THROW 54729,'No fue posible obtener el bloqueo de migración.',1;

DECLARE @ImpersonatingSa bit=0;

BEGIN TRY
  CREATE TABLE #PredicateRestore
  (
    PolicyObjectId int NOT NULL,
    PolicySchema sysname NOT NULL,
    PolicyName sysname NOT NULL,
    TargetSchema sysname NOT NULL,
    TargetTable sysname NOT NULL,
    PredicateType tinyint NOT NULL,
    OperationDesc nvarchar(60) NOT NULL,
    PredicateDefinition nvarchar(max) NOT NULL
  );
  INSERT #PredicateRestore
  SELECT policyInfo.object_id,OBJECT_SCHEMA_NAME(policyInfo.object_id),policyInfo.name,
    OBJECT_SCHEMA_NAME(predicateInfo.target_object_id),OBJECT_NAME(predicateInfo.target_object_id),
    predicateInfo.predicate_type,ISNULL(predicateInfo.operation_desc,N''),predicateInfo.predicate_definition
  FROM sys.security_policies policyInfo
  JOIN sys.security_predicates predicateInfo ON predicateInfo.object_id=policyInfo.object_id
  WHERE CHARINDEX(N'[restaurante].[fn_OnlineOrderingScopePredicate](',predicateInfo.predicate_definition)>0;

  IF NOT EXISTS(SELECT 1 FROM #PredicateRestore)
    THROW 54730,'La función de predicado no está ligada a ninguna política.',1;

  CREATE TABLE #PolicyState
  (
    PolicyObjectId int NOT NULL PRIMARY KEY,
    PolicySchema sysname NOT NULL,
    PolicyName sysname NOT NULL,
    WasEnabled bit NOT NULL,
    PredicateCount int NOT NULL
  );
  INSERT #PolicyState
  SELECT policyInfo.object_id,OBJECT_SCHEMA_NAME(policyInfo.object_id),policyInfo.name,policyInfo.is_enabled,
    (SELECT COUNT(*) FROM sys.security_predicates predicateInfo WHERE predicateInfo.object_id=policyInfo.object_id)
  FROM sys.security_policies policyInfo
  WHERE policyInfo.object_id IN(SELECT PolicyObjectId FROM #PredicateRestore);

  DECLARE @Statements nvarchar(max);
  SELECT @Statements=STRING_AGG(CONVERT(nvarchar(max),
    N'ALTER SECURITY POLICY '+QUOTENAME(PolicySchema)+N'.'+QUOTENAME(PolicyName)+N' WITH(STATE=OFF);'),CHAR(10))
  FROM #PolicyState WHERE WasEnabled=1;
  IF @Statements IS NOT NULL EXEC sys.sp_executesql @Statements;

  SELECT @Statements=STRING_AGG(CONVERT(nvarchar(max),
    N'ALTER SECURITY POLICY '+QUOTENAME(PolicySchema)+N'.'+QUOTENAME(PolicyName)+N' DROP '
    +CASE WHEN PredicateType=0 THEN N'FILTER' ELSE N'BLOCK' END+N' PREDICATE ON '
    +QUOTENAME(TargetSchema)+N'.'+QUOTENAME(TargetTable)
    +CASE WHEN PredicateType=1 THEN N' '+OperationDesc ELSE N'' END+N';'),CHAR(10))
  FROM #PredicateRestore;
  EXEC sys.sp_executesql @Statements;

  EXEC sys.sp_executesql N'
ALTER FUNCTION restaurante.fn_OnlineOrderingScopePredicate
(
  @PublicSiteId bigint,
  @Rfc varchar(50)
)
RETURNS TABLE WITH SCHEMABINDING AS RETURN
  SELECT 1 AS Allowed
  WHERE (SUSER_SID()=0x01 AND DATABASE_PRINCIPAL_ID()=1)
    OR
    (
      @Rfc=CONVERT(varchar(50),SESSION_CONTEXT(N''OrionRfc''))
      AND EXISTS
      (
        SELECT 1
        FROM orion.PublicSite publicSite
        JOIN orion.Company companyInfo ON companyInfo.CompanyId=publicSite.CompanyId
        WHERE publicSite.PublicSiteId=@PublicSiteId
          AND publicSite.ModuleCode=''RESTAURANT''
          AND publicSite.IsActive=CONVERT(bit,1)
          AND companyInfo.IsActive=CONVERT(bit,1)
          AND companyInfo.Rfc=@Rfc
          AND
          (
            EXISTS
            (
              SELECT 1 FROM orion.PublicSqlPrincipalBinding binding
              WHERE binding.PrincipalName=USER_NAME()
                AND binding.PublicSiteId=@PublicSiteId
                AND binding.IsActive=CONVERT(bit,1)
                AND @PublicSiteId=TRY_CONVERT(bigint,SESSION_CONTEXT(N''OrionERP.PublicSiteId''))
            )
            OR
            (
              NOT EXISTS
                (SELECT 1 FROM orion.PublicSqlPrincipalBinding binding WHERE binding.PrincipalName=USER_NAME())
              AND publicSite.CompanyId=TRY_CONVERT(bigint,SESSION_CONTEXT(N''OrionERP.CompanyId''))
              AND
              (
                SESSION_CONTEXT(N''OrionERP.SiteId'') IS NULL
                OR publicSite.SiteId=TRY_CONVERT(bigint,SESSION_CONTEXT(N''OrionERP.SiteId''))
              )
            )
          )
      )
    );';

  SELECT @Statements=STRING_AGG(CONVERT(nvarchar(max),
    N'ALTER SECURITY POLICY '+QUOTENAME(PolicySchema)+N'.'+QUOTENAME(PolicyName)+N' ADD '
    +CASE WHEN PredicateType=0 THEN N'FILTER' ELSE N'BLOCK' END+N' PREDICATE '
    +CASE WHEN LEFT(PredicateDefinition,1)=N'(' AND RIGHT(PredicateDefinition,1)=N')'
          THEN SUBSTRING(PredicateDefinition,2,LEN(PredicateDefinition)-2) ELSE PredicateDefinition END
    +N' ON '+QUOTENAME(TargetSchema)+N'.'+QUOTENAME(TargetTable)
    +CASE WHEN PredicateType=1 THEN N' '+OperationDesc ELSE N'' END+N';'),CHAR(10))
  FROM #PredicateRestore;
  EXEC sys.sp_executesql @Statements;

  SELECT @Statements=STRING_AGG(CONVERT(nvarchar(max),
    N'ALTER SECURITY POLICY '+QUOTENAME(PolicySchema)+N'.'+QUOTENAME(PolicyName)+N' WITH(STATE=ON);'),CHAR(10))
  FROM #PolicyState WHERE WasEnabled=1;
  IF @Statements IS NOT NULL EXEC sys.sp_executesql @Statements;

  IF EXISTS
  (
    SELECT PolicyObjectId,TargetSchema,TargetTable,PredicateType,OperationDesc,PredicateDefinition FROM #PredicateRestore
    EXCEPT
    SELECT predicateInfo.object_id,OBJECT_SCHEMA_NAME(predicateInfo.target_object_id),OBJECT_NAME(predicateInfo.target_object_id),
      predicateInfo.predicate_type,ISNULL(predicateInfo.operation_desc,N''),predicateInfo.predicate_definition
    FROM sys.security_predicates predicateInfo
  )
  OR EXISTS
  (
    SELECT 1 FROM #PolicyState savedPolicy
    LEFT JOIN sys.security_policies policyInfo ON policyInfo.object_id=savedPolicy.PolicyObjectId
    WHERE policyInfo.object_id IS NULL OR policyInfo.is_enabled<>savedPolicy.WasEnabled
       OR (SELECT COUNT(*) FROM sys.security_predicates predicateInfo WHERE predicateInfo.object_id=savedPolicy.PolicyObjectId)
          <>savedPolicy.PredicateCount
  )
    THROW 54731,'La política no recuperó exactamente sus predicados y su estado.',1;

  IF OBJECT_DEFINITION(OBJECT_ID(N'restaurante.fn_OnlineOrderingScopePredicate'))
     NOT LIKE N'%(SUSER_SID()=0x01 AND DATABASE_PRINCIPAL_ID()=1)%'
    THROW 54732,'La función no quedó con el acceso completo de sa.',1;

  EXEC sys.sp_set_session_context @key=N'OrionRfc',@value=NULL;
  EXEC sys.sp_set_session_context @key=N'OrionERP.CompanyId',@value=NULL;
  EXEC sys.sp_set_session_context @key=N'OrionERP.SiteId',@value=NULL;
  EXEC sys.sp_set_session_context @key=N'OrionERP.ModuleCode',@value=NULL;
  EXEC sys.sp_set_session_context @key=N'OrionERP.PublicSiteId',@value=NULL;

  -- La función, sin contexto: 0 filas para el login de la aplicación y 1 fila para sa.
  DECLARE @FunctionProof nvarchar(max)=N'
SELECT @AllowedRows=(SELECT COUNT(*) FROM restaurante.fn_OnlineOrderingScopePredicate(-1,''-''));';

  -- Cada tabla filtrada, sin contexto: el login de la aplicación no debe ver ninguna fila.
  DECLARE @FilterProbe nvarchar(max);
  SELECT @FilterProbe=STRING_AGG(CONVERT(nvarchar(max),
    N'IF EXISTS(SELECT 1 FROM '+QUOTENAME(TargetSchema)+N'.'+QUOTENAME(TargetTable)+N') SELECT @VisibleCount+=1,@VisibleTables+=N'''
    +REPLACE(TargetSchema+N'.'+TargetTable,N'''',N'''''')+N' '';'),CHAR(10))
  FROM (SELECT DISTINCT TargetSchema,TargetTable FROM #PredicateRestore WHERE PredicateType=0) filterTarget;
  DECLARE @FilterTables int=(SELECT COUNT(*) FROM (SELECT DISTINCT TargetSchema,TargetTable FROM #PredicateRestore WHERE PredicateType=0) filterTarget);

  DECLARE @ExecutorAllowedRows int=0,@ExecutorVisibleCount int=0,@ExecutorVisibleTables nvarchar(max)=N'';
  DECLARE @SaAllowedRows int=0,@SaVisibleCount int=0,@SaVisibleTables nvarchar(max)=N'';

  EXEC sys.sp_executesql @FunctionProof,N'@AllowedRows int OUTPUT',@AllowedRows=@ExecutorAllowedRows OUTPUT;
  EXEC sys.sp_executesql @FilterProbe,N'@VisibleCount int OUTPUT,@VisibleTables nvarchar(max) OUTPUT',
    @VisibleCount=@ExecutorVisibleCount OUTPUT,@VisibleTables=@ExecutorVisibleTables OUTPUT;

  EXECUTE AS LOGIN=N'sa';
  SET @ImpersonatingSa=1;
  EXEC sys.sp_executesql @FunctionProof,N'@AllowedRows int OUTPUT',@AllowedRows=@SaAllowedRows OUTPUT;
  EXEC sys.sp_executesql @FilterProbe,N'@VisibleCount int OUTPUT,@VisibleTables nvarchar(max) OUTPUT',
    @VisibleCount=@SaVisibleCount OUTPUT,@VisibleTables=@SaVisibleTables OUTPUT;
  REVERT;
  SET @ImpersonatingSa=0;

  IF @ExecutorAllowedRows<>0 OR @ExecutorVisibleCount<>0
  BEGIN
    DECLARE @LeakMessage nvarchar(2048)=LEFT(
      N'El login de la aplicación todavía ve filas sin contexto de empresa: '+@ExecutorVisibleTables,2048);
    THROW 54733,@LeakMessage,1;
  END;
  IF @SaAllowedRows<>1
    THROW 54734,'sa no obtuvo acceso completo sin contexto de empresa.',1;

  INSERT orion.TenantIsolationMigrationAudit
    (MigrationId,EntityType,ActionCode,SourceId,Details)
  SELECT @MigrationId,'RlsPredicate','ADD_SA_BYPASS',target.FunctionName,
    N'Sólo el login sa (SID 0x01) ve todas las filas sin contexto; la aplicación y los principales públicos siguen fail-closed.'
  FROM @TargetFunctions target;

  SELECT
    (SELECT COUNT(*) FROM #PredicateRestore) RebuiltPredicates,
    (SELECT COUNT(*) FROM #PolicyState) Policies,
    @FilterTables FilteredTables,
    @ExecutorVisibleCount TablesVisibleToApplicationLogin,
    @SaVisibleCount TablesVisibleToSa,
    N'SA_FULL_ACCESS_APPLICATION_FAIL_CLOSED' EstadoValidacion;

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
  IF @ImpersonatingSa=1 REVERT;
  IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
  THROW;
END CATCH;
