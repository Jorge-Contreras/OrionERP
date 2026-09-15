SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

-- El login sa (SID 0x01) ve y edita todas las filas sin SESSION_CONTEXT, para diagnosticar desde SSMS.
-- orion también es sysadmin y entra como dbo, pero es el login de la aplicación: sigue exigiendo el
-- contexto de empresa. Por eso la condición usa el SID de sa y no USER_NAME().

DECLARE @ExpectedDatabase sysname=N'$(ExpectedDatabase)';
DECLARE @ApplyChangesInput nvarchar(20)=N'$(ApplyChanges)';
DECLARE @ApplyChanges bit=0;
DECLARE @MigrationId nvarchar(200)=N'$(MigrationId)';
DECLARE @MigrationChecksum varchar(128)='$(MigrationChecksum)';
DECLARE @AppVersionInput nvarchar(64)=N'$(AppVersion)';
DECLARE @AppVersion nvarchar(64)=NULL;

IF @ApplyChangesInput NOT LIKE N'$'+N'(%'
BEGIN
  IF @ApplyChangesInput NOT IN(N'0',N'1') THROW 54700,'ApplyChanges debe ser 0 o 1.',1;
  SET @ApplyChanges=CONVERT(bit,@ApplyChangesInput);
END;
IF @ExpectedDatabase LIKE N'$'+N'(%'
   OR @ExpectedDatabase NOT IN(N'Orion_Sandbox',N'grupocarpio',N'Orion_Training')
  THROW 54701,'La base esperada no está autorizada para esta migración.',1;
IF DB_NAME()<>@ExpectedDatabase THROW 54702,'La conexión no apunta a la base declarada.',1;
IF @MigrationId<>N'20260914_rls_sa_full_access'
  THROW 54703,'MigrationId incorrecto.',1;
IF @MigrationChecksum LIKE '$'+N'(%' OR LEN(@MigrationChecksum)<>64
   OR @MigrationChecksum LIKE '%[^0-9A-F]%' COLLATE Latin1_General_100_BIN2
  THROW 54704,'MigrationChecksum inválido.',1;
IF @AppVersionInput NOT LIKE N'$'+N'(%'
  SET @AppVersion=NULLIF(LTRIM(RTRIM(@AppVersionInput)),N'');

IF SUSER_SID()=0x01
  THROW 54705,'Ejecute esta migración con el login de la aplicación, no con sa: la prueba fail-closed no demostraría nada.',1;

IF OBJECT_ID(N'orion.SchemaMigration',N'U') IS NULL
   OR OBJECT_ID(N'orion.TenantIsolationMigrationAudit',N'U') IS NULL
   OR NOT EXISTS(SELECT 1 FROM orion.SchemaMigration WHERE MigrationId=N'20260912_rfc_tenant_isolation_fail_closed_principals')
  THROW 54706,'Falta la migración fail-closed de principales.',1;

DECLARE @ExistingChecksum char(64)=(SELECT Checksum FROM orion.SchemaMigration WHERE MigrationId=@MigrationId);
IF @ExistingChecksum IS NOT NULL AND UPPER(@ExistingChecksum)<>UPPER(@MigrationChecksum)
  THROW 54707,'El mismo MigrationId ya existe con otro checksum.',1;
IF @ExistingChecksum IS NOT NULL
BEGIN
  SELECT N'YA_APLICADO' Estado,@MigrationId MigrationId,@ExistingChecksum Checksum;
  RETURN;
END;

-- Huella de cada función tal como quedó tras las migraciones de aislamiento del 9 al 12 de septiembre
-- (idéntica en Orion_Sandbox, grupocarpio y Orion_Training). Si alguna cambió, no se sobrescribe.
DECLARE @TargetFunctions TABLE(FunctionName sysname NOT NULL PRIMARY KEY,DefinitionHash varbinary(32) NOT NULL);
INSERT @TargetFunctions(FunctionName,DefinitionHash) VALUES
  (N'contabilidad.fn_AccountingScopePredicate',0x188D1A1BB07C3B2D3673A21B32A241BEF94AED07E3CD292391F6C463F75BB1F2),
  (N'fiscal.fn_DeclarationScopePredicate',0x9ACA1D129AD045AE647F264B784460143A32CBF28CB5D0A9F34857C4C152A99C),
  (N'logistica.fn_InventoryCoreScopePredicate',0x1642D83695FE818B9887AB057BA7DF5C8D96A70DEB0D8A0710B1E9AB301AFC8B),
  (N'logistica.fn_RfcAccessPredicate',0xD6E69175B52D7821CC2A1000A00F1E35C9ECF090E31C85A0C8A8798937A5B759),
  (N'orion.fn_HospitalityPaymentScopePredicate',0xB62C6701F314E36A714057EB4BB0DC8B138DCE552759704B9F03783F2855366E),
  (N'orion.fn_HospitalityScopePredicate',0x9186B91BE7CB65A23A66666CE9BA025B7C8DBD2844F2CA553E9F93CC18DA3D71),
  (N'rh.fn_RfcAccessPredicate',0xBD4ABC1C2640C37A52DA0360CB2F47ED7F561D17A2A9BAAC5056296051F39D3F),
  (N'rh.fn_WorkforceScopePredicate',0x85991760194EA621FD6888B652DC9D48F2DE418609435A4CA7002F5F50BB2947);

IF EXISTS
(
  SELECT 1 FROM @TargetFunctions target
  WHERE OBJECT_ID(target.FunctionName,N'IF') IS NULL
     OR HASHBYTES('SHA2_256',OBJECT_DEFINITION(OBJECT_ID(target.FunctionName)))<>target.DefinitionHash
)
  THROW 54708,'Una función de predicado cambió desde que se preparó esta migración; no se sobrescribe.',1;

BEGIN TRANSACTION;
DECLARE @LockResult int;
EXEC @LockResult=sys.sp_getapplock
  @Resource=N'OrionERP:RlsSaFullAccess',@LockMode=N'Exclusive',
  @LockOwner=N'Transaction',@LockTimeout=15000;
IF @LockResult<0 THROW 54709,'No fue posible obtener el bloqueo de migración.',1;

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
  WHERE EXISTS
  (
    SELECT 1 FROM @TargetFunctions target
    WHERE CHARINDEX(QUOTENAME(PARSENAME(target.FunctionName,2))+N'.'+QUOTENAME(PARSENAME(target.FunctionName,1))+N'(',
      predicateInfo.predicate_definition)>0
  );

  IF EXISTS
  (
    SELECT 1 FROM @TargetFunctions target
    WHERE NOT EXISTS
    (
      SELECT 1 FROM #PredicateRestore saved
      WHERE CHARINDEX(QUOTENAME(PARSENAME(target.FunctionName,2))+N'.'+QUOTENAME(PARSENAME(target.FunctionName,1))+N'(',
        saved.PredicateDefinition)>0
    )
  )
    THROW 54710,'Una función de predicado no está ligada a ninguna política.',1;

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
ALTER FUNCTION contabilidad.fn_AccountingScopePredicate(@CompanyId bigint)
  RETURNS TABLE WITH SCHEMABINDING AS
  RETURN SELECT 1 AS IsAllowed
  WHERE (SUSER_SID()=0x01 AND DATABASE_PRINCIPAL_ID()=1)
    OR
    (
      @CompanyId=TRY_CONVERT(bigint,SESSION_CONTEXT(N''OrionERP.CompanyId''))
      AND EXISTS
      (
        SELECT 1 FROM orion.Company company
        WHERE company.CompanyId=@CompanyId AND company.IsActive=1
          AND company.Rfc=CONVERT(varchar(50),SESSION_CONTEXT(N''OrionRfc''))
      )
    );';

  EXEC sys.sp_executesql N'
ALTER FUNCTION fiscal.fn_DeclarationScopePredicate(@Rfc varchar(50))
  RETURNS TABLE WITH SCHEMABINDING AS
  RETURN SELECT 1 AS IsAllowed
  WHERE (SUSER_SID()=0x01 AND DATABASE_PRINCIPAL_ID()=1)
    OR
    (
      @Rfc=CONVERT(varchar(50),SESSION_CONTEXT(N''OrionRfc''))
      AND EXISTS(SELECT 1 FROM orion.Company company WHERE company.Rfc=@Rfc AND company.IsActive=1)
    );';

  EXEC sys.sp_executesql N'
ALTER FUNCTION logistica.fn_InventoryCoreScopePredicate(@Rfc varchar(50))
  RETURNS TABLE WITH SCHEMABINDING AS
  RETURN SELECT 1 AS IsAllowed
  WHERE (SUSER_SID()=0x01 AND DATABASE_PRINCIPAL_ID()=1)
    OR
    (
      @Rfc=CONVERT(varchar(50),SESSION_CONTEXT(N''OrionRfc''))
      AND EXISTS(SELECT 1 FROM orion.Company company WHERE company.Rfc=@Rfc AND company.IsActive=1)
    );';

  EXEC sys.sp_executesql N'
ALTER FUNCTION logistica.fn_RfcAccessPredicate(@Rfc varchar(50))
RETURNS TABLE WITH SCHEMABINDING AS RETURN
  SELECT 1 AS IsAllowed
  WHERE (SUSER_SID()=0x01 AND DATABASE_PRINCIPAL_ID()=1)
    OR
    (
      @Rfc=CONVERT(varchar(50),SESSION_CONTEXT(N''OrionRfc''))
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
      )
    );';

  EXEC sys.sp_executesql N'
ALTER FUNCTION orion.fn_HospitalityPaymentScopePredicate(@CompanyId bigint,@SiteId bigint,@TransaccionId int)
  RETURNS TABLE WITH SCHEMABINDING AS RETURN
    SELECT 1 AS Allowed
    WHERE (SUSER_SID()=0x01 AND DATABASE_PRINCIPAL_ID()=1)
      OR EXISTS
      (
        SELECT 1 FROM dbo.Transacciones payment JOIN orion.Company companyInfo ON companyInfo.CompanyId=@CompanyId AND companyInfo.Rfc=payment.RFC
        WHERE payment.ID=@TransaccionId AND @CompanyId>0 AND @SiteId>0
          AND @CompanyId=TRY_CONVERT(bigint,SESSION_CONTEXT(N''OrionERP.HospitalityCompanyId''))
          AND @SiteId=TRY_CONVERT(bigint,SESSION_CONTEXT(N''OrionERP.HospitalitySiteId''))
      );';

  EXEC sys.sp_executesql N'
ALTER FUNCTION orion.fn_HospitalityScopePredicate(@CompanyId bigint,@SiteId bigint)
RETURNS TABLE WITH SCHEMABINDING AS RETURN
  SELECT 1 AS Allowed
  WHERE (SUSER_SID()=0x01 AND DATABASE_PRINCIPAL_ID()=1)
    OR
    (
      @CompanyId>0 AND @SiteId>0
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
      )
    );';

  EXEC sys.sp_executesql N'
ALTER FUNCTION rh.fn_RfcAccessPredicate(@Rfc varchar(50))
RETURNS TABLE WITH SCHEMABINDING AS RETURN
  SELECT 1 AS IsAllowed
  WHERE (SUSER_SID()=0x01 AND DATABASE_PRINCIPAL_ID()=1)
    OR
    (
      @Rfc=CONVERT(varchar(50),SESSION_CONTEXT(N''OrionRfc''))
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
      )
    );';

  EXEC sys.sp_executesql N'
ALTER FUNCTION rh.fn_WorkforceScopePredicate(@Rfc varchar(50))
  RETURNS TABLE WITH SCHEMABINDING AS
  RETURN SELECT 1 AS IsAllowed
  WHERE (SUSER_SID()=0x01 AND DATABASE_PRINCIPAL_ID()=1)
     OR @Rfc=CONVERT(varchar(50),SESSION_CONTEXT(N''OrionRfc''));';

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
    THROW 54711,'Las políticas no recuperaron exactamente sus predicados y su estado.',1;

  IF EXISTS
  (
    SELECT 1 FROM @TargetFunctions target
    WHERE OBJECT_DEFINITION(OBJECT_ID(target.FunctionName)) NOT LIKE N'%(SUSER_SID()=0x01 AND DATABASE_PRINCIPAL_ID()=1)%'
  )
    THROW 54712,'Una función no quedó con el acceso completo de sa.',1;

  EXEC sys.sp_set_session_context @key=N'OrionRfc',@value=NULL;
  EXEC sys.sp_set_session_context @key=N'OrionERP.CompanyId',@value=NULL;
  EXEC sys.sp_set_session_context @key=N'OrionERP.SiteId',@value=NULL;
  EXEC sys.sp_set_session_context @key=N'OrionERP.ModuleCode',@value=NULL;
  EXEC sys.sp_set_session_context @key=N'OrionERP.PublicSiteId',@value=NULL;
  EXEC sys.sp_set_session_context @key=N'OrionERP.HospitalityCompanyId',@value=NULL;
  EXEC sys.sp_set_session_context @key=N'OrionERP.HospitalitySiteId',@value=NULL;
  EXEC sys.sp_set_session_context @key=N'OrionERP.HospitalityRfc',@value=NULL;

  -- Cada función, sin contexto: 0 filas para el login de la aplicación y 1 fila para sa.
  DECLARE @FunctionProof nvarchar(max)=N'
SELECT @AllowedRows=
  (SELECT COUNT(*) FROM contabilidad.fn_AccountingScopePredicate(-1))
  +(SELECT COUNT(*) FROM fiscal.fn_DeclarationScopePredicate(''-''))
  +(SELECT COUNT(*) FROM logistica.fn_InventoryCoreScopePredicate(''-''))
  +(SELECT COUNT(*) FROM logistica.fn_RfcAccessPredicate(''-''))
  +(SELECT COUNT(*) FROM orion.fn_HospitalityPaymentScopePredicate(-1,-1,-1))
  +(SELECT COUNT(*) FROM orion.fn_HospitalityScopePredicate(-1,-1))
  +(SELECT COUNT(*) FROM rh.fn_RfcAccessPredicate(''-''))
  +(SELECT COUNT(*) FROM rh.fn_WorkforceScopePredicate(''-''));';

  -- Cada tabla filtrada, sin contexto: el login de la aplicación no debe ver ninguna fila.
  DECLARE @FilterProbe nvarchar(max);
  SELECT @FilterProbe=STRING_AGG(CONVERT(nvarchar(max),
    N'IF EXISTS(SELECT 1 FROM '+QUOTENAME(TargetSchema)+N'.'+QUOTENAME(TargetTable)+N') SELECT @VisibleCount+=1,@VisibleTables+=N'''
    +REPLACE(TargetSchema+N'.'+TargetTable,N'''',N'''''')+N' '';'),CHAR(10))
  FROM (SELECT DISTINCT TargetSchema,TargetTable FROM #PredicateRestore WHERE PredicateType=0) filterTarget;
  DECLARE @FilterTables int=(SELECT COUNT(*) FROM (SELECT DISTINCT TargetSchema,TargetTable FROM #PredicateRestore WHERE PredicateType=0) filterTarget);

  DECLARE @ExecutorAllowedRows int=0,@ExecutorVisibleCount int=0,@ExecutorVisibleTables nvarchar(max)=N'';
  DECLARE @SaAllowedRows int=0,@SaVisibleCount int=0,@SaVisibleTables nvarchar(max)=N'',@SaSeesPartners bit=0;

  EXEC sys.sp_executesql @FunctionProof,N'@AllowedRows int OUTPUT',@AllowedRows=@ExecutorAllowedRows OUTPUT;
  EXEC sys.sp_executesql @FilterProbe,N'@VisibleCount int OUTPUT,@VisibleTables nvarchar(max) OUTPUT',
    @VisibleCount=@ExecutorVisibleCount OUTPUT,@VisibleTables=@ExecutorVisibleTables OUTPUT;

  EXECUTE AS LOGIN=N'sa';
  SET @ImpersonatingSa=1;
  EXEC sys.sp_executesql @FunctionProof,N'@AllowedRows int OUTPUT',@AllowedRows=@SaAllowedRows OUTPUT;
  EXEC sys.sp_executesql @FilterProbe,N'@VisibleCount int OUTPUT,@VisibleTables nvarchar(max) OUTPUT',
    @VisibleCount=@SaVisibleCount OUTPUT,@VisibleTables=@SaVisibleTables OUTPUT;
  IF EXISTS(SELECT 1 FROM dbo.BusinessPartner) SET @SaSeesPartners=1;
  REVERT;
  SET @ImpersonatingSa=0;

  IF @ExecutorAllowedRows<>0 OR @ExecutorVisibleCount<>0
  BEGIN
    DECLARE @LeakMessage nvarchar(2048)=LEFT(
      N'El login de la aplicación todavía ve filas sin contexto de empresa: '+@ExecutorVisibleTables,2048);
    THROW 54713,@LeakMessage,1;
  END;
  IF @SaAllowedRows<>(SELECT COUNT(*) FROM @TargetFunctions) OR @SaSeesPartners=0
    THROW 54714,'sa no obtuvo acceso completo sin contexto de empresa.',1;

  DECLARE @OhmRfc varchar(50)='OHM191112Q26';
  DECLARE @OhmCompanyId bigint=(SELECT CompanyId FROM orion.Company WHERE Rfc=@OhmRfc AND IsActive=1);
  EXEC sys.sp_set_session_context @key=N'OrionRfc',@value=@OhmRfc;
  EXEC sys.sp_set_session_context @key=N'OrionERP.CompanyId',@value=@OhmCompanyId;
  IF NOT EXISTS(SELECT 1 FROM dbo.BusinessPartner WHERE OwnerRfc=@OhmRfc)
     OR EXISTS(SELECT 1 FROM dbo.BusinessPartner WHERE OwnerRfc<>@OhmRfc)
    THROW 54715,'El contexto de OHM no quedó limitado a su propia empresa.',1;
  EXEC sys.sp_set_session_context @key=N'OrionRfc',@value=NULL;
  EXEC sys.sp_set_session_context @key=N'OrionERP.CompanyId',@value=NULL;

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
