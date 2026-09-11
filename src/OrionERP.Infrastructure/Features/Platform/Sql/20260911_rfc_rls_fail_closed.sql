/* Make the shared RFC predicate fail closed and bind public callers to their registered PublicSite. */
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

DECLARE @ExpectedDatabase sysname=N'$(ExpectedDatabase)';
DECLARE @ApplyInput nvarchar(20)=N'$(ApplyChanges)';
DECLARE @ApplyChanges bit=0;
DECLARE @MigrationId nvarchar(200)=N'$(MigrationId)';
DECLARE @MigrationChecksum varchar(128)='$(MigrationChecksum)';
IF @ApplyInput NOT LIKE N'$'+N'(%'
BEGIN IF @ApplyInput NOT IN(N'0',N'1') THROW 53000,'ApplyChanges debe ser 0 o 1.',1; SET @ApplyChanges=CONVERT(bit,@ApplyInput); END;
IF @ExpectedDatabase LIKE N'$'+N'(%' OR @ExpectedDatabase NOT IN(N'Orion_Sandbox',N'grupocarpio',N'Orion_CutoverValidation_20260908')
  THROW 53001,'Base esperada no permitida.',1;
IF DB_NAME()<>@ExpectedDatabase THROW 53002,'La conexión no apunta a la base declarada.',1;
IF @MigrationId<>N'20260911_rfc_rls_fail_closed' THROW 53003,'MigrationId incorrecto.',1;
IF @MigrationChecksum LIKE '$'+'(%' OR LEN(@MigrationChecksum)<>64
   OR @MigrationChecksum LIKE '%[^0-9A-F]%' COLLATE Latin1_General_100_BIN2
  THROW 53004,'MigrationChecksum inválido.',1;
IF OBJECT_ID(N'logistica.fn_RfcAccessPredicate',N'IF') IS NULL
   OR OBJECT_ID(N'logistica.RfcSecurityPolicy',N'SP') IS NULL
   OR OBJECT_ID(N'rh.RfcSecurityPolicy',N'SP') IS NULL
   OR OBJECT_ID(N'orion.PublicSqlPrincipalBinding',N'U') IS NULL
  THROW 53005,'Faltan el predicado, las políticas o los bindings requeridos.',1;
DECLARE @ExistingChecksum char(64)=(SELECT Checksum FROM orion.SchemaMigration WHERE MigrationId=@MigrationId);
IF @ExistingChecksum IS NOT NULL AND UPPER(@ExistingChecksum)<>UPPER(@MigrationChecksum)
  THROW 53006,'El MigrationId ya existe con otro checksum.',1;
IF @ExistingChecksum IS NOT NULL
BEGIN SELECT N'YA_APLICADO' Estado,@MigrationId MigrationId,@ExistingChecksum Checksum; RETURN; END;

BEGIN TRANSACTION;
CREATE TABLE #RfcSecurityPredicates
(
  PolicySchema sysname NOT NULL,
  PolicyName sysname NOT NULL,
  SecurityPredicateId int NOT NULL,
  PredicateTypeDesc nvarchar(60) NOT NULL,
  OperationDesc nvarchar(60) NULL,
  PredicateDefinition nvarchar(max) NOT NULL,
  TargetSchema sysname NOT NULL,
  TargetTable sysname NOT NULL,
  PRIMARY KEY(PolicySchema,PolicyName,SecurityPredicateId)
);
INSERT #RfcSecurityPredicates
SELECT OBJECT_SCHEMA_NAME(policy.object_id),policy.name,predicate.security_predicate_id,
  predicate.predicate_type_desc,predicate.operation_desc,
  CASE WHEN LEFT(predicate.predicate_definition,1)=N'(' AND RIGHT(predicate.predicate_definition,1)=N')'
    THEN SUBSTRING(predicate.predicate_definition,2,LEN(predicate.predicate_definition)-2)
    ELSE predicate.predicate_definition END,
  OBJECT_SCHEMA_NAME(predicate.target_object_id),OBJECT_NAME(predicate.target_object_id)
FROM sys.security_policies policy
JOIN sys.security_predicates predicate ON predicate.object_id=policy.object_id
WHERE policy.object_id IN(OBJECT_ID(N'logistica.RfcSecurityPolicy'),OBJECT_ID(N'rh.RfcSecurityPolicy'));
DECLARE @LogisticsCount int=(SELECT COUNT(*) FROM #RfcSecurityPredicates WHERE PolicySchema=N'logistica');
DECLARE @WorkforceCount int=(SELECT COUNT(*) FROM #RfcSecurityPredicates WHERE PolicySchema=N'rh');
IF @LogisticsCount<200 OR @WorkforceCount<30 THROW 53007,'Las políticas RFC no tienen el baseline esperado.',1;
DROP SECURITY POLICY logistica.RfcSecurityPolicy;
DROP SECURITY POLICY rh.RfcSecurityPolicy;
GO

ALTER FUNCTION logistica.fn_RfcAccessPredicate(@Rfc varchar(50))
RETURNS TABLE WITH SCHEMABINDING AS RETURN
  SELECT 1 AS IsAllowed
  WHERE USER_NAME()=N'dbo'
     OR
     (
       @Rfc=CONVERT(varchar(50),SESSION_CONTEXT(N'OrionRfc'))
       AND
       (
         EXISTS
         (
           SELECT 1
           FROM orion.PublicSqlPrincipalBinding binding
           JOIN orion.PublicSite publicSite ON publicSite.PublicSiteId=binding.PublicSiteId
           JOIN orion.Company company ON company.CompanyId=publicSite.CompanyId
           WHERE binding.PrincipalName=USER_NAME() AND binding.IsActive=CONVERT(bit,1)
             AND publicSite.PublicSiteId=TRY_CONVERT(bigint,SESSION_CONTEXT(N'OrionERP.PublicSiteId'))
             AND publicSite.IsActive=CONVERT(bit,1) AND company.IsActive=CONVERT(bit,1)
             AND company.Rfc=@Rfc
         )
         OR
         (
           NOT EXISTS
             (SELECT 1 FROM orion.PublicSqlPrincipalBinding binding WHERE binding.PrincipalName=USER_NAME())
           AND EXISTS
           (
             SELECT 1 FROM orion.Company company
             WHERE company.CompanyId=TRY_CONVERT(bigint,SESSION_CONTEXT(N'OrionERP.CompanyId'))
               AND company.Rfc=@Rfc AND company.IsActive=CONVERT(bit,1)
           )
         )
       )
     );
GO

DECLARE @LogisticsCount int=(SELECT COUNT(*) FROM #RfcSecurityPredicates WHERE PolicySchema=N'logistica');
DECLARE @WorkforceCount int=(SELECT COUNT(*) FROM #RfcSecurityPredicates WHERE PolicySchema=N'rh');
DECLARE @PolicySchema sysname,@PolicyName sysname,@Predicates nvarchar(max),@CreateSql nvarchar(max);
DECLARE policy_cursor CURSOR LOCAL FAST_FORWARD FOR
  SELECT DISTINCT PolicySchema,PolicyName FROM #RfcSecurityPredicates ORDER BY PolicySchema,PolicyName;
OPEN policy_cursor;
FETCH NEXT FROM policy_cursor INTO @PolicySchema,@PolicyName;
WHILE @@FETCH_STATUS=0
BEGIN
  SELECT @Predicates=STRING_AGG
  (
    CONVERT(nvarchar(max),N'ADD '+PredicateTypeDesc+N' PREDICATE '+PredicateDefinition+
      N' ON '+QUOTENAME(TargetSchema)+N'.'+QUOTENAME(TargetTable)+
      CASE WHEN PredicateTypeDesc=N'BLOCK' THEN N' '+OperationDesc ELSE N'' END),
    N','+CHAR(13)+CHAR(10)
  ) WITHIN GROUP(ORDER BY SecurityPredicateId)
  FROM #RfcSecurityPredicates
  WHERE PolicySchema=@PolicySchema AND PolicyName=@PolicyName;
  SET @CreateSql=N'CREATE SECURITY POLICY '+QUOTENAME(@PolicySchema)+N'.'+QUOTENAME(@PolicyName)+N' '+@Predicates+
    N' WITH(STATE=ON,SCHEMABINDING=ON);';
  EXEC sys.sp_executesql @CreateSql;
  FETCH NEXT FROM policy_cursor INTO @PolicySchema,@PolicyName;
END;
CLOSE policy_cursor;
DEALLOCATE policy_cursor;

IF (SELECT COUNT(*) FROM sys.security_predicates WHERE object_id=OBJECT_ID(N'logistica.RfcSecurityPolicy'))<>@LogisticsCount
   OR (SELECT COUNT(*) FROM sys.security_predicates WHERE object_id=OBJECT_ID(N'rh.RfcSecurityPolicy'))<>@WorkforceCount
  THROW 53008,'Las políticas RFC no se recrearon completas.',1;
IF EXISTS(SELECT 1 FROM logistica.fn_RfcAccessPredicate('ANY') WHERE SESSION_CONTEXT(N'OrionRfc') IS NULL AND USER_NAME()<>N'dbo')
  THROW 53009,'El predicado RFC aún admite un contexto ausente.',1;

DECLARE @ApplyInput2 nvarchar(20)=N'$(ApplyChanges)';
DECLARE @ApplyChanges2 bit=CASE WHEN @ApplyInput2=N'1' THEN 1 ELSE 0 END;
DECLARE @MigrationId2 nvarchar(200)=N'$(MigrationId)';
DECLARE @MigrationChecksum2 varchar(128)='$(MigrationChecksum)';
DECLARE @AppVersionInput2 nvarchar(64)=N'$(AppVersion)';
DECLARE @AppVersion2 nvarchar(64)=CASE WHEN @AppVersionInput2 LIKE N'$'+N'(%' THEN NULL ELSE NULLIF(LTRIM(RTRIM(@AppVersionInput2)),N'') END;
IF @ApplyChanges2=1
BEGIN
  INSERT orion.SchemaMigration(MigrationId,Checksum,AppliedBy,AppVersion,DatabaseName)
  VALUES(@MigrationId2,@MigrationChecksum2,
    COALESCE(CONVERT(nvarchar(256),SESSION_CONTEXT(N'OrionERP.UserName')),CONVERT(nvarchar(256),ORIGINAL_LOGIN())),
    @AppVersion2,DB_NAME());
  COMMIT TRANSACTION;
  SELECT N'APLICADO' Estado,@MigrationId2 MigrationId,@LogisticsCount LogisticsPredicates,@WorkforceCount WorkforcePredicates;
END
ELSE
BEGIN
  SELECT N'PREVIEW' Estado,@MigrationId2 MigrationId,@LogisticsCount LogisticsPredicates,@WorkforceCount WorkforcePredicates;
  ROLLBACK TRANSACTION;
END;
