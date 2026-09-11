/*
  Read-only acceptance suite for the deterministic dual-module fixture.
  It may only run against Orion_Sandbox and persists no data. Session context
  changes are cleared before every REVERT and at the end of the batch.
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF DB_NAME()<>N'Orion_Sandbox'
  THROW 53000,'Esta validación solo puede ejecutarse en Orion_Sandbox.',1;

DECLARE @Rfc varchar(50)='TST260910DUAL01';
DECLARE @CompanyId bigint=(SELECT CompanyId FROM orion.Company WHERE Rfc=@Rfc AND LegacyTenantKey='synthetic-dual-01');
DECLARE @CampusSiteId bigint=(SELECT SiteId FROM orion.Site WHERE CompanyId=@CompanyId AND SiteKey='dual-campus');
DECLARE @ControlSiteId bigint=(SELECT SiteId FROM orion.Site WHERE CompanyId=@CompanyId AND SiteKey='dual-control');
DECLARE @HospitalityPublicSiteId bigint=(SELECT PublicSiteId FROM orion.PublicSite WHERE PublicSiteKey='synthetic-hospitality-main');
DECLARE @RestaurantPublicSiteId bigint=(SELECT PublicSiteId FROM orion.PublicSite WHERE PublicSiteKey='synthetic-restaurant-main');
DECLARE @HospitalityPrincipal sysname=N'orion_public_synthetic_h_01';
DECLARE @RestaurantPrincipal sysname=N'orion_public_synthetic_r_01';

IF @CompanyId IS NULL OR @CampusSiteId IS NULL OR @ControlSiteId IS NULL
   OR @HospitalityPublicSiteId IS NULL OR @RestaurantPublicSiteId IS NULL
  THROW 53001,'No se encontró la topología sintética completa.',1;

IF (SELECT COUNT(*) FROM orion.CompanyModule WHERE CompanyId=@CompanyId AND [Status]='Enabled'
      AND ModuleCode IN('HOSPITALITY','RESTAURANT'))<>2
   OR (SELECT COUNT(*) FROM orion.SiteCapability WHERE CompanyId=@CompanyId AND SiteId=@CampusSiteId
      AND ModuleCode IN('HOSPITALITY','RESTAURANT') AND IsEnabled=1)<>2
   OR (SELECT COUNT(*) FROM orion.SiteCapability WHERE CompanyId=@CompanyId AND SiteId=@ControlSiteId
      AND ModuleCode IN('HOSPITALITY','RESTAURANT') AND IsEnabled=0)<>2
  THROW 53002,'Módulos o capacidades no coinciden con el fixture dual.',1;

IF EXISTS
(
  SELECT 1 FROM orion.PublicSite
  WHERE PublicSiteId IN(@HospitalityPublicSiteId,@RestaurantPublicSiteId)
    AND (CompanyId<>@CompanyId OR SiteId<>@CampusSiteId OR IsActive=0)
)
   OR NOT EXISTS(SELECT 1 FROM orion.PublicSite WHERE PublicSiteId=@HospitalityPublicSiteId AND ModuleCode='HOSPITALITY')
   OR NOT EXISTS(SELECT 1 FROM orion.PublicSite WHERE PublicSiteId=@RestaurantPublicSiteId AND ModuleCode='RESTAURANT')
  THROW 53003,'Los PublicSites no comparten la empresa/sede esperada o tienen módulos incorrectos.',1;

IF (SELECT COUNT(*) FROM restaurante.Site WHERE OrionCompanyId=@CompanyId
      AND OrionSiteId IN(@CampusSiteId,@ControlSiteId))<>2
   OR EXISTS
   (
     SELECT OrionCompanyId,OrionSiteId FROM restaurante.Site
     WHERE OrionCompanyId=@CompanyId GROUP BY OrionCompanyId,OrionSiteId HAVING COUNT(*)<>1
   )
  THROW 53004,'El binding explícito de sedes Restaurant es incompleto o ambiguo.',1;

IF (SELECT COUNT(*) FROM orion.ProvisioningOperationStep
    WHERE OperationId='26091000-0000-0000-0000-000000000001' AND [Status]='Completed')<>7
  THROW 53005,'El provisionamiento no tiene siete etapas completas.',1;

/* Permission drift: every expected entry exists, no extra object/schema permission exists, and no role is inherited. */
CREATE TABLE #PermissionPreview
(
  ApplyChanges bit,PublicSiteId bigint,ProfileCode varchar(40),ProfileVersion int,
  Estado varchar(5),Permiso varchar(20),Securable nvarchar(800),YaExiste bit
);
INSERT #PermissionPreview EXEC orion.ApplyPublicPermissionProfile
  @PublicSiteKey='synthetic-hospitality-main',@PrincipalName=@HospitalityPrincipal,@ApplyChanges=0;
INSERT #PermissionPreview EXEC orion.ApplyPublicPermissionProfile
  @PublicSiteKey='synthetic-restaurant-main',@PrincipalName=@RestaurantPrincipal,@ApplyChanges=0;
IF EXISTS(SELECT 1 FROM #PermissionPreview WHERE YaExiste=0)
  THROW 53006,'Falta un permiso declarado por un perfil público.',1;
IF EXISTS
(
  SELECT principalInfo.name
  FROM (VALUES(@HospitalityPrincipal,'HOSPITALITY_PUBLIC'),(@RestaurantPrincipal,'RESTAURANT_PUBLIC')) expected(PrincipalName,ProfileCode)
  JOIN sys.database_principals principalInfo ON principalInfo.name=expected.PrincipalName
  CROSS APPLY
  (
    SELECT COUNT(*) ActualCount FROM sys.database_permissions permissionInfo
    WHERE permissionInfo.grantee_principal_id=principalInfo.principal_id AND permissionInfo.class IN(1,3)
  ) actual
  CROSS APPLY
  (
    SELECT COUNT(*) ExpectedCount FROM orion.PublicPermissionProfileEntry profileEntry
    WHERE profileEntry.ProfileCode=expected.ProfileCode AND profileEntry.ProfileVersion=2
  ) profile
  WHERE actual.ActualCount<>profile.ExpectedCount
)
  THROW 53007,'Un principal público tiene permisos de objeto/esquema fuera de su perfil.',1;
IF EXISTS
(
  SELECT 1 FROM sys.database_role_members
  WHERE member_principal_id IN(DATABASE_PRINCIPAL_ID(@HospitalityPrincipal),DATABASE_PRINCIPAL_ID(@RestaurantPrincipal))
)
  THROW 53008,'Un principal público pertenece a un rol de base.',1;

IF NOT EXISTS(SELECT 1 FROM orion.PublicSqlPrincipalBinding WHERE PrincipalName=@HospitalityPrincipal
      AND PublicSiteId=@HospitalityPublicSiteId AND PermissionProfile='HOSPITALITY_PUBLIC' AND PermissionVersion=2 AND IsActive=1)
   OR NOT EXISTS(SELECT 1 FROM orion.PublicSqlPrincipalBinding WHERE PrincipalName=@RestaurantPrincipal
      AND PublicSiteId=@RestaurantPublicSiteId AND PermissionProfile='RESTAURANT_PUBLIC' AND PermissionVersion=2 AND IsActive=1)
  THROW 53009,'El binding principal/PublicSite no coincide con el perfil v2.',1;

/* Every neutral Identity table carries PublicSiteId and participates in the enabled RLS policy. */
DECLARE @IdentityTables TABLE(TableName sysname PRIMARY KEY);
INSERT @IdentityTables VALUES
  ('AspNetUsers'),('AspNetRoles'),('AspNetUserClaims'),('AspNetRoleClaims'),
  ('AspNetUserLogins'),('AspNetUserRoles'),('AspNetUserTokens');
IF EXISTS
(
  SELECT 1 FROM @IdentityTables expected
  WHERE COL_LENGTH(N'public_identity.'+expected.TableName,N'PublicSiteId') IS NULL
     OR NOT EXISTS
     (
       SELECT 1 FROM sys.security_predicates predicateInfo
       JOIN sys.security_policies policyInfo ON policyInfo.object_id=predicateInfo.object_id
       WHERE predicateInfo.target_object_id=OBJECT_ID(N'public_identity.'+expected.TableName)
         AND policyInfo.is_enabled=1 AND policyInfo.is_schema_bound=1
     )
)
  THROW 53010,'Una tabla neutral de Identity no está acotada por PublicSiteId y RLS.',1;
IF EXISTS
(
  SELECT 1 FROM brunos_auth.AspNetUsers legacy
  LEFT JOIN public_identity.AspNetUsers neutral ON neutral.Id=legacy.Id
  WHERE neutral.Id IS NULL OR ISNULL(neutral.PasswordHash,N'')<>ISNULL(legacy.PasswordHash,N'')
    OR neutral.SecurityStamp<>legacy.SecurityStamp OR neutral.ConcurrencyStamp<>legacy.ConcurrencyStamp
)
  THROW 53011,'Una identidad heredada no fue preservada exactamente en el esquema neutral.',1;
IF (SELECT COUNT(*) FROM public_identity.AspNetUsers
    WHERE NormalizedEmail=N'SHARED-MEMBER@SYNTHETIC-DUAL.INVALID'
      AND PublicSiteId IN(@HospitalityPublicSiteId,@RestaurantPublicSiteId))<>2
  THROW 53012,'No se preservó la independencia del mismo correo entre PublicSites.',1;
IF NOT EXISTS(SELECT 1 FROM orion.PublicIdentityCompatibilityState WHERE SingletonId=1 AND BridgeMode='Reverse')
  THROW 53013,'El bridge de rollback no está en modo Reverse.',1;

/* RLS and permission behavior under the exact database users used by each public instance. */
DECLARE @HospitalityNoContext int,@HospitalityPartial int,@HospitalityOwn int,@HospitalitySpoof int;
DECLARE @RestaurantNoContext int,@RestaurantOwn int,@RestaurantSpoof int;
DECLARE @HospitalityCrossDenied bit=0,@RestaurantCrossDenied bit=0;

BEGIN TRY
  EXECUTE AS USER=@HospitalityPrincipal;
  EXEC sys.sp_set_session_context @key=N'OrionERP.CompanyId',@value=NULL;
  EXEC sys.sp_set_session_context @key=N'OrionRfc',@value=NULL;
  EXEC sys.sp_set_session_context @key=N'OrionERP.SiteId',@value=NULL;
  EXEC sys.sp_set_session_context @key=N'OrionERP.ModuleCode',@value=NULL;
  EXEC sys.sp_set_session_context @key=N'OrionERP.PublicSiteId',@value=NULL;
  EXEC sys.sp_set_session_context @key=N'OrionERP.HospitalityCompanyId',@value=NULL;
  EXEC sys.sp_set_session_context @key=N'OrionERP.HospitalitySiteId',@value=NULL;
  SELECT @HospitalityNoContext=COUNT(*) FROM dbo.ROOM;
  EXEC sys.sp_set_session_context @key=N'OrionERP.CompanyId',@value=@CompanyId;
  SELECT @HospitalityPartial=COUNT(*) FROM dbo.ROOM;
  EXEC sys.sp_set_session_context @key=N'OrionRfc',@value=@Rfc;
  EXEC sys.sp_set_session_context @key=N'OrionERP.SiteId',@value=@CampusSiteId;
  EXEC sys.sp_set_session_context @key=N'OrionERP.ModuleCode',@value=N'HOSPITALITY';
  EXEC sys.sp_set_session_context @key=N'OrionERP.PublicSiteId',@value=@HospitalityPublicSiteId;
  EXEC sys.sp_set_session_context @key=N'OrionERP.HospitalityCompanyId',@value=@CompanyId;
  EXEC sys.sp_set_session_context @key=N'OrionERP.HospitalitySiteId',@value=@CampusSiteId;
  SELECT @HospitalityOwn=COUNT(*) FROM dbo.ROOM WHERE ROOM_NAME='SYNTHETIC-ROOM-01';
  EXEC sys.sp_set_session_context @key=N'OrionERP.PublicSiteId',@value=@RestaurantPublicSiteId;
  SELECT @HospitalitySpoof=COUNT(*) FROM dbo.ROOM;
  BEGIN TRY SELECT COUNT(*) Ignored FROM restaurante.Menu; END TRY
  BEGIN CATCH IF ERROR_NUMBER()=229 SET @HospitalityCrossDenied=1; ELSE THROW; END CATCH;
  EXEC sys.sp_set_session_context @key=N'OrionERP.CompanyId',@value=NULL;
  EXEC sys.sp_set_session_context @key=N'OrionRfc',@value=NULL;
  EXEC sys.sp_set_session_context @key=N'OrionERP.SiteId',@value=NULL;
  EXEC sys.sp_set_session_context @key=N'OrionERP.ModuleCode',@value=NULL;
  EXEC sys.sp_set_session_context @key=N'OrionERP.PublicSiteId',@value=NULL;
  EXEC sys.sp_set_session_context @key=N'OrionERP.HospitalityCompanyId',@value=NULL;
  EXEC sys.sp_set_session_context @key=N'OrionERP.HospitalitySiteId',@value=NULL;
  REVERT;
END TRY
BEGIN CATCH
  IF USER_NAME()=@HospitalityPrincipal REVERT;
  THROW;
END CATCH;

BEGIN TRY
  EXECUTE AS USER=@RestaurantPrincipal;
  EXEC sys.sp_set_session_context @key=N'OrionERP.CompanyId',@value=NULL;
  EXEC sys.sp_set_session_context @key=N'OrionRfc',@value=NULL;
  EXEC sys.sp_set_session_context @key=N'OrionERP.SiteId',@value=NULL;
  EXEC sys.sp_set_session_context @key=N'OrionERP.ModuleCode',@value=NULL;
  EXEC sys.sp_set_session_context @key=N'OrionERP.PublicSiteId',@value=NULL;
  SELECT @RestaurantNoContext=COUNT(*) FROM restaurante.Menu;
  EXEC sys.sp_set_session_context @key=N'OrionERP.CompanyId',@value=@CompanyId;
  EXEC sys.sp_set_session_context @key=N'OrionRfc',@value=@Rfc;
  EXEC sys.sp_set_session_context @key=N'OrionERP.SiteId',@value=@CampusSiteId;
  EXEC sys.sp_set_session_context @key=N'OrionERP.ModuleCode',@value=N'RESTAURANT';
  EXEC sys.sp_set_session_context @key=N'OrionERP.PublicSiteId',@value=@RestaurantPublicSiteId;
  SELECT @RestaurantOwn=COUNT(*) FROM restaurante.Menu WHERE MenuCode='SYNTHETIC-MAIN';
  EXEC sys.sp_set_session_context @key=N'OrionERP.PublicSiteId',@value=@HospitalityPublicSiteId;
  SELECT @RestaurantSpoof=COUNT(*) FROM restaurante.Menu;
  BEGIN TRY SELECT COUNT(*) Ignored FROM dbo.ROOM; END TRY
  BEGIN CATCH IF ERROR_NUMBER()=229 SET @RestaurantCrossDenied=1; ELSE THROW; END CATCH;
  EXEC sys.sp_set_session_context @key=N'OrionERP.CompanyId',@value=NULL;
  EXEC sys.sp_set_session_context @key=N'OrionRfc',@value=NULL;
  EXEC sys.sp_set_session_context @key=N'OrionERP.SiteId',@value=NULL;
  EXEC sys.sp_set_session_context @key=N'OrionERP.ModuleCode',@value=NULL;
  EXEC sys.sp_set_session_context @key=N'OrionERP.PublicSiteId',@value=NULL;
  REVERT;
END TRY
BEGIN CATCH
  IF USER_NAME()=@RestaurantPrincipal REVERT;
  THROW;
END CATCH;

IF @HospitalityNoContext<>0 OR @HospitalityPartial<>0 OR @HospitalityOwn<>1 OR @HospitalitySpoof<>0
   OR @RestaurantNoContext<>0 OR @RestaurantOwn<>1 OR @RestaurantSpoof<>0
   OR @HospitalityCrossDenied<>1 OR @RestaurantCrossDenied<>1
  THROW 53014,'Una prueba negativa RLS, de scope parcial, suplantación o cruce de módulo falló.',1;

/* The fixture itself must contain no production brand, RFC, email, host or presentation identity. */
IF EXISTS
(
  SELECT 1 FROM
  (
    SELECT CONVERT(nvarchar(max),DisplayName) Value FROM orion.Company WHERE CompanyId=@CompanyId
    UNION ALL SELECT CONVERT(nvarchar(max),LegalName) FROM orion.Company WHERE CompanyId=@CompanyId
    UNION ALL SELECT CONVERT(nvarchar(max),Rfc) FROM orion.Company WHERE CompanyId=@CompanyId
    UNION ALL SELECT CONVERT(nvarchar(max),PublicSiteKey) FROM orion.PublicSite WHERE CompanyId=@CompanyId
    UNION ALL SELECT CONVERT(nvarchar(max),CanonicalHost) FROM orion.PublicSite WHERE CompanyId=@CompanyId
    UNION ALL SELECT CONVERT(nvarchar(max),PublicName) FROM restaurante.PublicSiteSettings WHERE PublicSiteId=@RestaurantPublicSiteId
    UNION ALL SELECT CONVERT(nvarchar(max),Email) FROM public_identity.AspNetUsers
      WHERE PublicSiteId IN(@HospitalityPublicSiteId,@RestaurantPublicSiteId)
    UNION ALL SELECT CONVERT(nvarchar(max),SecretReference) FROM orion.IntegrationBinding WHERE CompanyId=@CompanyId
  ) fixture
  WHERE fixture.Value LIKE N'%bonhomia%' OR fixture.Value LIKE N'%bruno%'
     OR fixture.Value LIKE N'%OHM191112Q26%' OR fixture.Value LIKE N'%BRUNOS260707L26%'
)
  THROW 53015,'El fixture sintético contiene una identidad real.',1;

SELECT N'PASS' AcceptanceStatus,@CompanyId CompanyId,@CampusSiteId CampusSiteId,@ControlSiteId ControlSiteId,
  @HospitalityPublicSiteId HospitalityPublicSiteId,@RestaurantPublicSiteId RestaurantPublicSiteId,
  @HospitalityOwn HospitalityRows,@RestaurantOwn RestaurantRows,
  (SELECT COUNT(*) FROM #PermissionPreview) PermissionAssertions,
  (SELECT COUNT(*) FROM sys.security_policies WHERE is_enabled=1 AND is_schema_bound=1) EnabledRlsPolicies,
  (SELECT COUNT(*) FROM public_identity.AspNetUsers) NeutralIdentityUsers,
  (SELECT COUNT(*) FROM brunos_auth.AspNetUsers) RollbackIdentityUsers,
  (SELECT BridgeMode FROM orion.PublicIdentityCompatibilityState WHERE SingletonId=1) IdentityBridgeMode,
  (SELECT COUNT(*) FROM orion.ProvisioningOperationStep
    WHERE OperationId='26091000-0000-0000-0000-000000000001' AND [Status]='Completed') CompletedProvisioningSteps;

SELECT schemaInfo.name SchemaName,policyInfo.name PolicyName,COUNT(*) PredicateCount,
  policyInfo.is_enabled IsEnabled,policyInfo.is_schema_bound IsSchemaBound
FROM sys.security_policies policyInfo
JOIN sys.schemas schemaInfo ON schemaInfo.schema_id=policyInfo.schema_id
JOIN sys.security_predicates predicateInfo ON predicateInfo.object_id=policyInfo.object_id
GROUP BY schemaInfo.name,policyInfo.name,policyInfo.is_enabled,policyInfo.is_schema_bound
ORDER BY schemaInfo.name,policyInfo.name;
