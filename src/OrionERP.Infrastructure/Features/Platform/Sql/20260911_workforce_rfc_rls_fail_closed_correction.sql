/*
  Additive correction: 20260911_rfc_rls_fail_closed hardened
  logistica.fn_RfcAccessPredicate but rh.RfcSecurityPolicy still referenced the
  distinct legacy rh.fn_RfcAccessPredicate, whose NULL bypass remained active.
  Applied migrations stay immutable, so this migration closes the remaining RH
  boundary without changing the earlier checksum.
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
IF @ApplyInput NOT LIKE N'$'+N'(%'
BEGIN IF @ApplyInput NOT IN(N'0',N'1') THROW 53200,'ApplyChanges debe ser 0 o 1.',1; SET @ApplyChanges=CONVERT(bit,@ApplyInput); END;
IF @ExpectedDatabase LIKE N'$'+N'(%' OR @ExpectedDatabase NOT IN(N'Orion_Sandbox',N'grupocarpio',N'Orion_CutoverValidation_20260908')
  THROW 53201,'Base esperada no permitida.',1;
IF DB_NAME()<>@ExpectedDatabase THROW 53202,'La conexión no apunta a la base declarada.',1;
IF @MigrationId<>N'20260911_workforce_rfc_rls_fail_closed_correction' THROW 53203,'MigrationId incorrecto.',1;
IF @MigrationChecksum LIKE '$'+'(%' OR LEN(@MigrationChecksum)<>64
   OR @MigrationChecksum LIKE '%[^0-9A-F]%' COLLATE Latin1_General_100_BIN2
  THROW 53204,'MigrationChecksum inválido.',1;
IF OBJECT_ID(N'rh.fn_RfcAccessPredicate',N'IF') IS NULL
   OR OBJECT_ID(N'rh.RfcSecurityPolicy',N'SP') IS NULL
   OR OBJECT_ID(N'orion.PublicSqlPrincipalBinding',N'U') IS NULL
  THROW 53205,'Faltan el predicado, la política RH o los bindings requeridos.',1;
IF DATABASE_PRINCIPAL_ID(N'OrionWorkforceRlsVerifier_532') IS NOT NULL
  THROW 53206,'El principal temporal de verificación ya existe.',1;
DECLARE @ExistingChecksum char(64)=(SELECT Checksum FROM orion.SchemaMigration WHERE MigrationId=@MigrationId);
IF @ExistingChecksum IS NOT NULL AND UPPER(@ExistingChecksum)<>UPPER(@MigrationChecksum)
  THROW 53207,'El MigrationId ya existe con otro checksum.',1;
IF @ExistingChecksum IS NOT NULL
BEGIN SELECT N'YA_APLICADO' Estado,@MigrationId MigrationId,@ExistingChecksum Checksum; RETURN; END;

BEGIN TRANSACTION;
CREATE TABLE #RfcSecurityPredicates
(
  SecurityPredicateId int NOT NULL PRIMARY KEY,
  PredicateTypeDesc nvarchar(60) NOT NULL,
  OperationDesc nvarchar(60) NULL,
  PredicateDefinition nvarchar(max) NOT NULL,
  TargetSchema sysname NOT NULL,
  TargetTable sysname NOT NULL
);
INSERT #RfcSecurityPredicates
SELECT predicate.security_predicate_id,predicate.predicate_type_desc,predicate.operation_desc,
  CASE WHEN LEFT(predicate.predicate_definition,1)=N'(' AND RIGHT(predicate.predicate_definition,1)=N')'
    THEN SUBSTRING(predicate.predicate_definition,2,LEN(predicate.predicate_definition)-2)
    ELSE predicate.predicate_definition END,
  OBJECT_SCHEMA_NAME(predicate.target_object_id),OBJECT_NAME(predicate.target_object_id)
FROM sys.security_predicates predicate
WHERE predicate.object_id=OBJECT_ID(N'rh.RfcSecurityPolicy');
DECLARE @PredicateCount int=(SELECT COUNT(*) FROM #RfcSecurityPredicates);
IF @PredicateCount<30 THROW 53208,'La política RFC de RH no tiene el baseline esperado.',1;

CREATE TABLE #PolicyPermissions
(
  GranteeName sysname NOT NULL,
  PermissionState char(1) NOT NULL,
  PermissionName nvarchar(128) NOT NULL,
  PRIMARY KEY(GranteeName,PermissionName)
);
INSERT #PolicyPermissions(GranteeName,PermissionState,PermissionName)
SELECT USER_NAME(permissionRow.grantee_principal_id),permissionRow.state,permissionRow.permission_name
FROM sys.database_permissions permissionRow
WHERE permissionRow.class=1 AND permissionRow.major_id=OBJECT_ID(N'rh.RfcSecurityPolicy')
  AND permissionRow.minor_id=0;

DROP SECURITY POLICY rh.RfcSecurityPolicy;
GO

ALTER FUNCTION rh.fn_RfcAccessPredicate(@Rfc varchar(50))
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

DECLARE @PredicateCount int=(SELECT COUNT(*) FROM #RfcSecurityPredicates);
DECLARE @Predicates nvarchar(max),@CreateSql nvarchar(max);
SELECT @Predicates=STRING_AGG
(
  CONVERT(nvarchar(max),N'ADD '+PredicateTypeDesc+N' PREDICATE '+PredicateDefinition+
    N' ON '+QUOTENAME(TargetSchema)+N'.'+QUOTENAME(TargetTable)+
    CASE WHEN PredicateTypeDesc=N'BLOCK' THEN N' '+OperationDesc ELSE N'' END),
  N','+CHAR(13)+CHAR(10)
) WITHIN GROUP(ORDER BY SecurityPredicateId)
FROM #RfcSecurityPredicates;
SET @CreateSql=N'CREATE SECURITY POLICY [rh].[RfcSecurityPolicy] '+@Predicates+
  N' WITH(STATE=ON,SCHEMABINDING=ON);';
EXEC sys.sp_executesql @CreateSql;

DECLARE @GranteeName sysname,@PermissionState char(1),@PermissionName nvarchar(128),@PermissionSql nvarchar(max);
DECLARE permission_cursor CURSOR LOCAL FAST_FORWARD FOR
  SELECT GranteeName,PermissionState,PermissionName FROM #PolicyPermissions ORDER BY GranteeName,PermissionName;
OPEN permission_cursor;
FETCH NEXT FROM permission_cursor INTO @GranteeName,@PermissionState,@PermissionName;
WHILE @@FETCH_STATUS=0
BEGIN
  SET @PermissionSql=
    CASE WHEN @PermissionState=N'D' THEN N'DENY ' ELSE N'GRANT ' END+
    @PermissionName+N' ON OBJECT::[rh].[RfcSecurityPolicy] TO '+QUOTENAME(@GranteeName)+
    CASE WHEN @PermissionState=N'W' THEN N' WITH GRANT OPTION' ELSE N'' END+N';';
  EXEC sys.sp_executesql @PermissionSql;
  FETCH NEXT FROM permission_cursor INTO @GranteeName,@PermissionState,@PermissionName;
END;
CLOSE permission_cursor;
DEALLOCATE permission_cursor;

IF (SELECT COUNT(*) FROM sys.security_predicates WHERE object_id=OBJECT_ID(N'rh.RfcSecurityPolicy'))<>@PredicateCount
  THROW 53209,'La política RFC de RH no se recreó completa.',1;

DECLARE @CompanyId bigint,@Rfc varchar(50),@WrongRfc varchar(50);
SELECT TOP(1) @CompanyId=CompanyId,@Rfc=Rfc FROM orion.Company WHERE IsActive=1 ORDER BY CompanyId;
SELECT TOP(1) @WrongRfc=Rfc FROM orion.Company WHERE IsActive=1 AND CompanyId<>@CompanyId ORDER BY CompanyId;
IF @CompanyId IS NULL OR @WrongRfc IS NULL THROW 53210,'Se requieren dos empresas activas para verificar el predicado.',1;
CREATE USER [OrionWorkforceRlsVerifier_532] WITHOUT LOGIN;
GRANT SELECT ON OBJECT::rh.fn_RfcAccessPredicate TO [OrionWorkforceRlsVerifier_532];
DECLARE @MissingAllowed int=0,@RfcOnlyAllowed int=0,@MismatchAllowed int=0,@CorrectAllowed int=0;

EXEC sys.sp_set_session_context @key=N'OrionERP.PublicSiteId',@value=NULL,@read_only=0;
EXEC sys.sp_set_session_context @key=N'OrionERP.CompanyId',@value=NULL,@read_only=0;
EXEC sys.sp_set_session_context @key=N'OrionRfc',@value=NULL,@read_only=0;
EXECUTE AS USER=N'OrionWorkforceRlsVerifier_532';
SELECT @MissingAllowed=COUNT(*) FROM rh.fn_RfcAccessPredicate(@Rfc);
REVERT;

EXEC sys.sp_set_session_context @key=N'OrionRfc',@value=@Rfc,@read_only=0;
EXECUTE AS USER=N'OrionWorkforceRlsVerifier_532';
SELECT @RfcOnlyAllowed=COUNT(*) FROM rh.fn_RfcAccessPredicate(@Rfc);
REVERT;

EXEC sys.sp_set_session_context @key=N'OrionERP.CompanyId',@value=@CompanyId,@read_only=0;
EXEC sys.sp_set_session_context @key=N'OrionRfc',@value=@WrongRfc,@read_only=0;
EXECUTE AS USER=N'OrionWorkforceRlsVerifier_532';
SELECT @MismatchAllowed=COUNT(*) FROM rh.fn_RfcAccessPredicate(@Rfc);
REVERT;

EXEC sys.sp_set_session_context @key=N'OrionRfc',@value=@Rfc,@read_only=0;
EXECUTE AS USER=N'OrionWorkforceRlsVerifier_532';
SELECT @CorrectAllowed=COUNT(*) FROM rh.fn_RfcAccessPredicate(@Rfc);
REVERT;

EXEC sys.sp_set_session_context @key=N'OrionERP.CompanyId',@value=NULL,@read_only=0;
EXEC sys.sp_set_session_context @key=N'OrionRfc',@value=NULL,@read_only=0;
DROP USER [OrionWorkforceRlsVerifier_532];
IF @MissingAllowed<>0 OR @RfcOnlyAllowed<>0 OR @MismatchAllowed<>0 OR @CorrectAllowed<>1
  THROW 53211,'La verificación no-dbo del predicado RH no cumplió la matriz fail-closed.',1;

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
  SELECT N'APLICADO' Estado,@MigrationId2 MigrationId,@PredicateCount WorkforcePredicates,
    N'Missing=0;RfcOnly=0;Mismatch=0;Correct=1' Verification;
END
ELSE
BEGIN
  SELECT N'PREVIEW' Estado,@MigrationId2 MigrationId,@PredicateCount WorkforcePredicates,
    N'Missing=0;RfcOnly=0;Mismatch=0;Correct=1' Verification;
  ROLLBACK TRANSACTION;
END;
