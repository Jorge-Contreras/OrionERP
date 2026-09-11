/* Bind RLS itself to the SQL principal's registered PublicSite, not only to caller-set session context. */
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
BEGIN IF @ApplyInput NOT IN(N'0',N'1') THROW 52800,'ApplyChanges debe ser 0 o 1.',1; SET @ApplyChanges=CONVERT(bit,@ApplyInput); END;
IF @ExpectedDatabase LIKE N'$'+N'(%' OR @ExpectedDatabase NOT IN(N'Orion_Sandbox',N'grupocarpio',N'Orion_CutoverValidation_20260908')
  THROW 52801,'Base esperada no permitida.',1;
IF DB_NAME()<>@ExpectedDatabase THROW 52802,'La conexión no apunta a la base declarada.',1;
IF @MigrationId<>N'20260911_public_rls_principal_binding' THROW 52803,'MigrationId incorrecto.',1;
IF @MigrationChecksum LIKE '$'+'(%' OR LEN(@MigrationChecksum)<>64
   OR @MigrationChecksum LIKE '%[^0-9A-F]%' COLLATE Latin1_General_100_BIN2
  THROW 52804,'MigrationChecksum inválido.',1;
IF OBJECT_ID(N'orion.PublicSqlPrincipalBinding',N'U') IS NULL
   OR OBJECT_ID(N'orion.PublicIdentityScopePolicy',N'SP') IS NULL
   OR OBJECT_ID(N'orion.HospitalityScopePolicy',N'SP') IS NULL
  THROW 52805,'Faltan las políticas o bindings requeridos.',1;
DECLARE @ExistingChecksum char(64)=(SELECT Checksum FROM orion.SchemaMigration WHERE MigrationId=@MigrationId);
IF @ExistingChecksum IS NOT NULL AND UPPER(@ExistingChecksum)<>UPPER(@MigrationChecksum)
  THROW 52806,'El MigrationId ya existe con otro checksum.',1;
IF @ExistingChecksum IS NOT NULL
BEGIN SELECT N'YA_APLICADO' Estado,@MigrationId MigrationId,@ExistingChecksum Checksum; RETURN; END;
BEGIN TRANSACTION;

CREATE TABLE #PreservedSecurityPredicates
(
  PolicyName sysname NOT NULL,
  SecurityPredicateId int NOT NULL,
  PredicateTypeDesc nvarchar(60) NOT NULL,
  OperationDesc nvarchar(60) NULL,
  PredicateDefinition nvarchar(max) NOT NULL,
  TargetSchema sysname NOT NULL,
  TargetTable sysname NOT NULL,
  PRIMARY KEY(PolicyName,SecurityPredicateId)
);

INSERT #PreservedSecurityPredicates
(
  PolicyName,SecurityPredicateId,PredicateTypeDesc,OperationDesc,
  PredicateDefinition,TargetSchema,TargetTable
)
SELECT
  policy.name,predicate.security_predicate_id,predicate.predicate_type_desc,
  predicate.operation_desc,
  CASE
    WHEN LEFT(predicate.predicate_definition,1)=N'('
     AND RIGHT(predicate.predicate_definition,1)=N')'
      THEN SUBSTRING(predicate.predicate_definition,2,LEN(predicate.predicate_definition)-2)
    ELSE predicate.predicate_definition
  END,
  OBJECT_SCHEMA_NAME(predicate.target_object_id),OBJECT_NAME(predicate.target_object_id)
FROM sys.security_policies policy
JOIN sys.security_predicates predicate ON predicate.object_id=policy.object_id
WHERE policy.object_id IN
(
  OBJECT_ID(N'orion.PublicIdentityScopePolicy'),
  OBJECT_ID(N'orion.HospitalityScopePolicy')
);

IF (SELECT COUNT(*) FROM #PreservedSecurityPredicates WHERE PolicyName=N'PublicIdentityScopePolicy')<>21
  THROW 52807,'La política de identidad no conserva 21 predicados.',1;
IF (SELECT COUNT(*) FROM #PreservedSecurityPredicates WHERE PolicyName=N'HospitalityScopePolicy')<54
  THROW 52808,'La política Hospitality perdió predicados.',1;

/* SQL Server does not allow ALTER FUNCTION while a security policy references it,
   even when that policy is disabled. Preserve and recreate every predicate atomically. */
DROP SECURITY POLICY orion.PublicIdentityScopePolicy;
DROP SECURITY POLICY orion.HospitalityScopePolicy;
GO

ALTER FUNCTION orion.fn_PublicIdentityScopePredicate(@PublicSiteId bigint)
RETURNS TABLE WITH SCHEMABINDING AS RETURN
  SELECT 1 Allowed
  WHERE USER_NAME()=N'dbo'
     OR
     (
       @PublicSiteId=TRY_CONVERT(bigint,SESSION_CONTEXT(N'OrionERP.PublicSiteId'))
       AND EXISTS
       (
         SELECT 1
         FROM orion.PublicSqlPrincipalBinding binding
         WHERE binding.PrincipalName=USER_NAME()
           AND binding.PublicSiteId=@PublicSiteId
           AND binding.IsActive=CONVERT(bit,1)
       )
     );
GO

ALTER FUNCTION orion.fn_HospitalityScopePredicate(@CompanyId bigint,@SiteId bigint)
RETURNS TABLE WITH SCHEMABINDING AS RETURN
  SELECT 1 AS Allowed
  WHERE @CompanyId>0 AND @SiteId>0
    AND @CompanyId=TRY_CONVERT(bigint,SESSION_CONTEXT(N'OrionERP.HospitalityCompanyId'))
    AND @SiteId=TRY_CONVERT(bigint,SESSION_CONTEXT(N'OrionERP.HospitalitySiteId'))
    AND
    (
      USER_NAME()=N'dbo'
      OR EXISTS
      (
        SELECT 1
        FROM orion.PublicSqlPrincipalBinding binding
        JOIN orion.PublicSite publicSite ON publicSite.PublicSiteId=binding.PublicSiteId
        WHERE binding.PrincipalName=USER_NAME() AND binding.IsActive=CONVERT(bit,1)
          AND publicSite.PublicSiteId=TRY_CONVERT(bigint,SESSION_CONTEXT(N'OrionERP.PublicSiteId'))
          AND publicSite.CompanyId=@CompanyId AND publicSite.SiteId=@SiteId
          AND publicSite.ModuleCode='HOSPITALITY' AND publicSite.IsActive=CONVERT(bit,1)
      )
    );
GO

DECLARE @IdentityPredicates nvarchar(max)=
(
  SELECT STRING_AGG
  (
    CONVERT(nvarchar(max),
      N'ADD '+PredicateTypeDesc+N' PREDICATE '+PredicateDefinition+
      N' ON '+QUOTENAME(TargetSchema)+N'.'+QUOTENAME(TargetTable)+
      CASE WHEN PredicateTypeDesc=N'BLOCK' THEN N' '+OperationDesc ELSE N'' END),
    N','+CHAR(13)+CHAR(10)
  ) WITHIN GROUP(ORDER BY SecurityPredicateId)
  FROM #PreservedSecurityPredicates
  WHERE PolicyName=N'PublicIdentityScopePolicy'
);
DECLARE @HospitalityPredicates nvarchar(max)=
(
  SELECT STRING_AGG
  (
    CONVERT(nvarchar(max),
      N'ADD '+PredicateTypeDesc+N' PREDICATE '+PredicateDefinition+
      N' ON '+QUOTENAME(TargetSchema)+N'.'+QUOTENAME(TargetTable)+
      CASE WHEN PredicateTypeDesc=N'BLOCK' THEN N' '+OperationDesc ELSE N'' END),
    N','+CHAR(13)+CHAR(10)
  ) WITHIN GROUP(ORDER BY SecurityPredicateId)
  FROM #PreservedSecurityPredicates
  WHERE PolicyName=N'HospitalityScopePolicy'
);

EXEC(N'CREATE SECURITY POLICY orion.PublicIdentityScopePolicy '+@IdentityPredicates+
  N' WITH(STATE=ON,SCHEMABINDING=ON);');
EXEC(N'CREATE SECURITY POLICY orion.HospitalityScopePolicy '+@HospitalityPredicates+
  N' WITH(STATE=ON,SCHEMABINDING=ON);');

IF (SELECT COUNT(*) FROM sys.security_predicates WHERE object_id=OBJECT_ID(N'orion.PublicIdentityScopePolicy'))<>21
  THROW 52809,'La política de identidad no se recreó completa.',1;
IF (SELECT COUNT(*) FROM sys.security_predicates WHERE object_id=OBJECT_ID(N'orion.HospitalityScopePolicy'))<54
  THROW 52810,'La política Hospitality no se recreó completa.',1;

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
  SELECT N'APLICADO' Estado,@MigrationId2 MigrationId,21 IdentityPredicates,
    (SELECT COUNT(*) FROM sys.security_predicates WHERE object_id=OBJECT_ID(N'orion.HospitalityScopePolicy')) HospitalityPredicates;
END
ELSE
BEGIN
  SELECT N'PREVIEW' Estado,@MigrationId2 MigrationId,21 IdentityPredicates,
    (SELECT COUNT(*) FROM sys.security_predicates WHERE object_id=OBJECT_ID(N'orion.HospitalityScopePolicy')) HospitalityPredicates;
  ROLLBACK TRANSACTION;
END;
