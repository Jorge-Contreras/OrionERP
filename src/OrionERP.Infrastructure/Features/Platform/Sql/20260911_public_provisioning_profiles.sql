/* Neutral, versioned public permission profiles and resumable provisioning ledger. */
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

DECLARE @ExpectedDatabase sysname=N'$(ExpectedDatabase)';
DECLARE @ApplyInput nvarchar(20)=N'$(ApplyChanges)';
DECLARE @ApplyChanges bit=0;
DECLARE @MigrationId nvarchar(200)=N'$(MigrationId)';
DECLARE @MigrationChecksum varchar(128)='$(MigrationChecksum)';
DECLARE @AppVersionInput nvarchar(64)=N'$(AppVersion)';
DECLARE @AppVersion nvarchar(64)=NULL;

IF @ApplyInput NOT LIKE N'$'+N'(%'
BEGIN
  IF @ApplyInput NOT IN(N'0',N'1') THROW 52600,'ApplyChanges debe ser 0 o 1.',1;
  SET @ApplyChanges=CONVERT(bit,@ApplyInput);
END;
IF @ExpectedDatabase LIKE N'$'+N'(%' OR @ExpectedDatabase NOT IN(N'Orion_Sandbox',N'grupocarpio',N'Orion_CutoverValidation_20260908')
  THROW 52601,'Base esperada no permitida.',1;
IF DB_NAME()<>@ExpectedDatabase THROW 52602,'La conexión no apunta a la base declarada.',1;
IF @MigrationId<>N'20260911_public_provisioning_profiles' THROW 52603,'MigrationId incorrecto.',1;
IF @MigrationChecksum LIKE '$'+'(%' OR LEN(@MigrationChecksum)<>64
   OR @MigrationChecksum LIKE '%[^0-9A-F]%' COLLATE Latin1_General_100_BIN2
  THROW 52604,'MigrationChecksum inválido.',1;
IF @AppVersionInput NOT LIKE N'$'+N'(%' SET @AppVersion=NULLIF(LTRIM(RTRIM(@AppVersionInput)),N'');
IF OBJECT_ID(N'orion.SchemaMigration',N'U') IS NULL
   OR OBJECT_ID(N'orion.PublicSqlPrincipalBinding',N'U') IS NULL
   OR OBJECT_ID(N'public_identity.AspNetUsers',N'U') IS NULL
  THROW 52605,'Faltan las migraciones neutrales de scope o identidad.',1;

DECLARE @ExistingChecksum char(64)=(SELECT Checksum FROM orion.SchemaMigration WHERE MigrationId=@MigrationId);
IF @ExistingChecksum IS NOT NULL AND UPPER(@ExistingChecksum)<>UPPER(@MigrationChecksum)
  THROW 52606,'El MigrationId ya existe con otro checksum.',1;
IF @ExistingChecksum IS NOT NULL
BEGIN
  SELECT N'YA_APLICADO' Estado,@MigrationId MigrationId,@ExistingChecksum Checksum;
  RETURN;
END;

BEGIN TRANSACTION;

IF OBJECT_ID(N'orion.ProvisioningOperation',N'U') IS NULL
BEGIN
  CREATE TABLE orion.ProvisioningOperation
  (
    OperationId uniqueidentifier NOT NULL CONSTRAINT PK_ProvisioningOperation PRIMARY KEY,
    OperationKind varchar(40) NOT NULL,
    [Status] varchar(20) NOT NULL,
    CompanyId bigint NULL,
    RequestJson nvarchar(max) NOT NULL,
    LastError nvarchar(2000) NULL,
    CreatedAtUtc datetime2(3) NOT NULL,
    UpdatedAtUtc datetime2(3) NOT NULL,
    CONSTRAINT FK_ProvisioningOperation_Company FOREIGN KEY(CompanyId) REFERENCES orion.Company(CompanyId),
    CONSTRAINT CK_ProvisioningOperation_Status CHECK([Status] IN('Pending','Applying','Completed','Failed')),
    CONSTRAINT CK_ProvisioningOperation_RequestJson CHECK(ISJSON(RequestJson)=1)
  );
END;

IF OBJECT_ID(N'orion.ProvisioningOperationStep',N'U') IS NULL
BEGIN
  CREATE TABLE orion.ProvisioningOperationStep
  (
    OperationId uniqueidentifier NOT NULL,
    StepCode varchar(60) NOT NULL,
    [Status] varchar(20) NOT NULL,
    DetailJson nvarchar(max) NULL,
    UpdatedAtUtc datetime2(3) NOT NULL CONSTRAINT DF_ProvisioningOperationStep_Updated DEFAULT(SYSUTCDATETIME()),
    CONSTRAINT PK_ProvisioningOperationStep PRIMARY KEY(OperationId,StepCode),
    CONSTRAINT FK_ProvisioningOperationStep_Operation FOREIGN KEY(OperationId) REFERENCES orion.ProvisioningOperation(OperationId),
    CONSTRAINT CK_ProvisioningOperationStep_Status CHECK([Status] IN('Pending','Completed','Failed','External')),
    CONSTRAINT CK_ProvisioningOperationStep_DetailJson CHECK(DetailJson IS NULL OR ISJSON(DetailJson)=1)
  );
END;

IF OBJECT_ID(N'orion.PublicPermissionProfile',N'U') IS NULL
BEGIN
  CREATE TABLE orion.PublicPermissionProfile
  (
    ProfileCode varchar(40) NOT NULL,
    ProfileVersion int NOT NULL,
    ModuleCode varchar(40) NOT NULL,
    ProfileChecksum varbinary(32) NULL,
    UpdatedAtUtc datetime2(3) NOT NULL CONSTRAINT DF_PublicPermissionProfile_Updated DEFAULT(SYSUTCDATETIME()),
    CONSTRAINT PK_PublicPermissionProfile PRIMARY KEY(ProfileCode,ProfileVersion),
    CONSTRAINT FK_PublicPermissionProfile_Module FOREIGN KEY(ModuleCode) REFERENCES orion.Module(ModuleCode),
    CONSTRAINT CK_PublicPermissionProfile_Version CHECK(ProfileVersion>0)
  );
END;

IF OBJECT_ID(N'orion.PublicPermissionProfileEntry',N'U') IS NULL
BEGIN
  CREATE TABLE orion.PublicPermissionProfileEntry
  (
    ProfileCode varchar(40) NOT NULL,
    ProfileVersion int NOT NULL,
    PermissionState varchar(5) NOT NULL,
    PermissionName varchar(20) NOT NULL,
    SecurableClass varchar(10) NOT NULL,
    SchemaName sysname NOT NULL,
    ObjectName sysname NOT NULL CONSTRAINT DF_PublicPermissionProfileEntry_Object DEFAULT(N''),
    CONSTRAINT PK_PublicPermissionProfileEntry PRIMARY KEY
      (ProfileCode,ProfileVersion,PermissionState,PermissionName,SecurableClass,SchemaName,ObjectName),
    CONSTRAINT FK_PublicPermissionProfileEntry_Profile FOREIGN KEY(ProfileCode,ProfileVersion)
      REFERENCES orion.PublicPermissionProfile(ProfileCode,ProfileVersion),
    CONSTRAINT CK_PublicPermissionProfileEntry_State CHECK(PermissionState IN('GRANT','DENY')),
    CONSTRAINT CK_PublicPermissionProfileEntry_Class CHECK(SecurableClass IN('OBJECT','SCHEMA')),
    CONSTRAINT CK_PublicPermissionProfileEntry_Object CHECK
      ((SecurableClass='OBJECT' AND ObjectName<>N'') OR (SecurableClass='SCHEMA' AND ObjectName=N''))
  );
END;

INSERT orion.PublicPermissionProfile(ProfileCode,ProfileVersion,ModuleCode)
SELECT source.ProfileCode,1,source.ModuleCode
FROM (VALUES('HOSPITALITY_PUBLIC','HOSPITALITY'),('RESTAURANT_PUBLIC','RESTAURANT')) source(ProfileCode,ModuleCode)
WHERE NOT EXISTS(SELECT 1 FROM orion.PublicPermissionProfile target WHERE target.ProfileCode=source.ProfileCode AND target.ProfileVersion=1);

DECLARE @Entries TABLE
(
  ProfileCode varchar(40),PermissionState varchar(5),PermissionName varchar(20),
  SecurableClass varchar(10),SchemaName sysname,ObjectName sysname
);

/* Common platform resolution/readiness. */
INSERT @Entries
SELECT profile.ProfileCode,'GRANT','SELECT','OBJECT','orion',objectInfo.ObjectName
FROM (VALUES('HOSPITALITY_PUBLIC'),('RESTAURANT_PUBLIC')) profile(ProfileCode)
CROSS JOIN (VALUES('Company'),('CompanyModule'),('Module'),('PublicSite'),('Site'),('SiteCapability'),('SchemaMigration')) objectInfo(ObjectName);

/* Hospitality quote, checkout and document surface. */
INSERT @Entries VALUES
('HOSPITALITY_PUBLIC','GRANT','SELECT','OBJECT','orion','HospitalitySiteCustomer'),
('HOSPITALITY_PUBLIC','GRANT','INSERT','OBJECT','orion','HospitalitySiteCustomer'),
('HOSPITALITY_PUBLIC','GRANT','SELECT','OBJECT','dbo','Clientes'),
('HOSPITALITY_PUBLIC','GRANT','INSERT','OBJECT','dbo','Clientes'),
('HOSPITALITY_PUBLIC','GRANT','UPDATE','OBJECT','dbo','Clientes'),
('HOSPITALITY_PUBLIC','GRANT','SELECT','OBJECT','dbo','Experience'),
('HOSPITALITY_PUBLIC','GRANT','SELECT','OBJECT','dbo','ExperienceAddOn'),
('HOSPITALITY_PUBLIC','GRANT','SELECT','OBJECT','dbo','ExperiencePackage'),
('HOSPITALITY_PUBLIC','GRANT','SELECT','OBJECT','dbo','ExperienceProvider'),
('HOSPITALITY_PUBLIC','GRANT','SELECT','OBJECT','dbo','Extra'),
('HOSPITALITY_PUBLIC','GRANT','SELECT','OBJECT','dbo','RESERVATION'),
('HOSPITALITY_PUBLIC','GRANT','INSERT','OBJECT','dbo','RESERVATION'),
('HOSPITALITY_PUBLIC','GRANT','SELECT','OBJECT','dbo','RESERVATION_ATTACHMENT'),
('HOSPITALITY_PUBLIC','GRANT','SELECT','OBJECT','dbo','RESERVATION_DETAIL'),
('HOSPITALITY_PUBLIC','GRANT','SELECT','OBJECT','dbo','ROOM'),
('HOSPITALITY_PUBLIC','GRANT','SELECT','OBJECT','dbo','ROOM_CALENDAR'),
('HOSPITALITY_PUBLIC','GRANT','UPDATE','OBJECT','dbo','ROOM_CALENDAR'),
('HOSPITALITY_PUBLIC','GRANT','SELECT','OBJECT','dbo','ReservationAirbnbBreakdown'),
('HOSPITALITY_PUBLIC','GRANT','SELECT','OBJECT','dbo','Reservation_Experience'),
('HOSPITALITY_PUBLIC','GRANT','INSERT','OBJECT','dbo','Reservation_Experience'),
('HOSPITALITY_PUBLIC','GRANT','SELECT','OBJECT','dbo','Reservation_ExperienceAddOn'),
('HOSPITALITY_PUBLIC','GRANT','INSERT','OBJECT','dbo','Reservation_ExperienceAddOn'),
('HOSPITALITY_PUBLIC','GRANT','SELECT','OBJECT','dbo','Reservation_Extra'),
('HOSPITALITY_PUBLIC','GRANT','INSERT','OBJECT','dbo','Reservation_Extra'),
('HOSPITALITY_PUBLIC','GRANT','SELECT','OBJECT','dbo','Reservation_Transacciones'),
('HOSPITALITY_PUBLIC','GRANT','INSERT','OBJECT','dbo','Reservation_Transacciones'),
('HOSPITALITY_PUBLIC','GRANT','SELECT','OBJECT','dbo','Transacciones'),
('HOSPITALITY_PUBLIC','GRANT','INSERT','OBJECT','dbo','Transacciones'),
('HOSPITALITY_PUBLIC','GRANT','VIEW DEFINITION','OBJECT','orion','HospitalityScopePolicy'),
('HOSPITALITY_PUBLIC','GRANT','VIEW DEFINITION','OBJECT','dbo','ROOM_CALENDAR');

INSERT @Entries SELECT 'HOSPITALITY_PUBLIC','DENY',permissionInfo.PermissionName,'SCHEMA',schemaInfo.SchemaName,N''
FROM (VALUES('SELECT'),('INSERT'),('UPDATE'),('DELETE'),('EXECUTE')) permissionInfo(PermissionName)
CROSS JOIN (VALUES('public_identity'),('fidelidad'),('restaurante'),('auth'),('rh'),('contabilidad'),('bancos'),('fiscal'),('reporteFinanciero'),('cfdi'),('logistica')) schemaInfo(SchemaName);

/* Restaurant publication, membership and neutral public identity. */
INSERT @Entries SELECT 'RESTAURANT_PUBLIC','GRANT','SELECT','OBJECT','restaurante',objectInfo.ObjectName
FROM (VALUES('Site'),('PublicSiteSettings'),('Menu'),('MenuItem'),('MenuSchedule'),('MenuSection'),
 ('Product'),('ProductCard'),('ProductDietaryTag'),('ProductModifierGroup'),('ModifierGroup'),('ModifierOption'),
 ('ModifierIngredientDelta'),('ComboSlot'),('ComboSlotOption'),('ComboSlotOptionRoute'),('Promotion'),
 ('PromotionCode'),('PromotionProduct'),('PromotionSchedule'),('PromotionMaterialCategory'),
 ('PromotionRedemption'),('OrderPromotion'),('Order'),('Payment'),('PaymentRefund')) objectInfo(ObjectName);
INSERT @Entries SELECT 'RESTAURANT_PUBLIC','GRANT','SELECT','OBJECT','logistica',objectInfo.ObjectName
FROM (VALUES('Material'),('MaterialAllergen'),('Allergen'),('UnitOfMeasure'),('BomHeader'),('BomVersion'),('BomComponent')) objectInfo(ObjectName);
INSERT @Entries VALUES
('RESTAURANT_PUBLIC','GRANT','SELECT','SCHEMA','public_identity',''),
('RESTAURANT_PUBLIC','GRANT','INSERT','SCHEMA','public_identity',''),
('RESTAURANT_PUBLIC','GRANT','UPDATE','SCHEMA','public_identity',''),
('RESTAURANT_PUBLIC','GRANT','DELETE','SCHEMA','public_identity',''),
('RESTAURANT_PUBLIC','GRANT','SELECT','OBJECT','fidelidad','MemberAccount'),
('RESTAURANT_PUBLIC','GRANT','INSERT','OBJECT','fidelidad','MemberAccount'),
('RESTAURANT_PUBLIC','GRANT','UPDATE','OBJECT','fidelidad','MemberAccount'),
('RESTAURANT_PUBLIC','GRANT','SELECT','OBJECT','fidelidad','MemberQrToken'),
('RESTAURANT_PUBLIC','GRANT','INSERT','OBJECT','fidelidad','MemberQrToken'),
('RESTAURANT_PUBLIC','GRANT','DELETE','OBJECT','fidelidad','MemberQrToken'),
('RESTAURANT_PUBLIC','GRANT','SELECT','OBJECT','fidelidad','PointLedger'),
('RESTAURANT_PUBLIC','GRANT','INSERT','OBJECT','fidelidad','PointLedger'),
('RESTAURANT_PUBLIC','GRANT','SELECT','OBJECT','fidelidad','MemberClosureRequest'),
('RESTAURANT_PUBLIC','GRANT','INSERT','OBJECT','fidelidad','MemberClosureRequest'),
('RESTAURANT_PUBLIC','GRANT','SELECT','OBJECT','fidelidad','MemberConsent'),
('RESTAURANT_PUBLIC','GRANT','INSERT','OBJECT','fidelidad','MemberConsent'),
('RESTAURANT_PUBLIC','GRANT','SELECT','OBJECT','fidelidad','ProgramSettings'),
('RESTAURANT_PUBLIC','GRANT','UPDATE','OBJECT','fidelidad','ProgramSettings'),
('RESTAURANT_PUBLIC','GRANT','UPDATE','OBJECT','restaurante','Order');
INSERT @Entries SELECT 'RESTAURANT_PUBLIC','DENY',permissionInfo.PermissionName,'SCHEMA',schemaInfo.SchemaName,N''
FROM (VALUES('SELECT'),('INSERT'),('UPDATE'),('DELETE'),('EXECUTE')) permissionInfo(PermissionName)
CROSS JOIN (VALUES('auth'),('rh'),('contabilidad'),('bancos'),('fiscal'),('reporteFinanciero'),('cfdi')) schemaInfo(SchemaName);
INSERT @Entries SELECT 'RESTAURANT_PUBLIC','DENY',permissionInfo.PermissionName,'OBJECT','dbo',objectInfo.ObjectName
FROM (VALUES('SELECT'),('INSERT'),('UPDATE'),('DELETE')) permissionInfo(PermissionName)
CROSS JOIN (VALUES('RESERVATION'),('RESERVATION_DETAIL'),('RESERVATION_ATTACHMENT'),('ROOM'),('ROOM_CALENDAR'),('Extra'),('Reservation_Extra'),('Reservation_Transacciones'),('Transacciones'),('Clientes')) objectInfo(ObjectName);
INSERT @Entries VALUES('RESTAURANT_PUBLIC','DENY','SELECT','OBJECT','orion','HospitalitySiteCustomer'),
 ('RESTAURANT_PUBLIC','DENY','INSERT','OBJECT','orion','HospitalitySiteCustomer'),
 ('RESTAURANT_PUBLIC','DENY','UPDATE','OBJECT','orion','HospitalitySiteCustomer'),
 ('RESTAURANT_PUBLIC','DENY','DELETE','OBJECT','orion','HospitalitySiteCustomer');

IF EXISTS
(
  SELECT 1 FROM @Entries requested
  WHERE (requested.SecurableClass='OBJECT' AND OBJECT_ID(QUOTENAME(requested.SchemaName)+N'.'+QUOTENAME(requested.ObjectName)) IS NULL)
     OR (requested.SecurableClass='SCHEMA' AND SCHEMA_ID(requested.SchemaName) IS NULL)
)
  THROW 52607,'El perfil menciona un securable que no existe.',1;

INSERT orion.PublicPermissionProfileEntry
(ProfileCode,ProfileVersion,PermissionState,PermissionName,SecurableClass,SchemaName,ObjectName)
SELECT requested.ProfileCode,1,requested.PermissionState,requested.PermissionName,requested.SecurableClass,requested.SchemaName,requested.ObjectName
FROM @Entries requested WHERE NOT EXISTS
(
  SELECT 1 FROM orion.PublicPermissionProfileEntry target
  WHERE target.ProfileCode=requested.ProfileCode AND target.ProfileVersion=1
    AND target.PermissionState=requested.PermissionState AND target.PermissionName=requested.PermissionName
    AND target.SecurableClass=requested.SecurableClass AND target.SchemaName=requested.SchemaName
    AND target.ObjectName=requested.ObjectName
);

UPDATE profileInfo SET ProfileChecksum=checksumInfo.ProfileChecksum,UpdatedAtUtc=SYSUTCDATETIME()
FROM orion.PublicPermissionProfile profileInfo
CROSS APPLY
(
  SELECT HASHBYTES('SHA2_256',STRING_AGG(CONVERT(nvarchar(max),
    entryInfo.PermissionState+N'|'+entryInfo.PermissionName+N'|'+entryInfo.SecurableClass+N'|'+entryInfo.SchemaName+N'|'+entryInfo.ObjectName),N';')
    WITHIN GROUP(ORDER BY entryInfo.PermissionState,entryInfo.PermissionName,entryInfo.SecurableClass,entryInfo.SchemaName,entryInfo.ObjectName)) ProfileChecksum
  FROM orion.PublicPermissionProfileEntry entryInfo
  WHERE entryInfo.ProfileCode=profileInfo.ProfileCode AND entryInfo.ProfileVersion=profileInfo.ProfileVersion
) checksumInfo
WHERE profileInfo.ProfileVersion=1;
GO

CREATE OR ALTER PROCEDURE orion.ApplyPublicPermissionProfile
  @PublicSiteKey varchar(100),
  @PrincipalName sysname,
  @ApplyChanges bit=0
AS
BEGIN
  SET NOCOUNT ON;
  SET XACT_ABORT ON;
  DECLARE @PublicSiteId bigint,@ModuleCode varchar(40),@ProfileCode varchar(40),@ProfileVersion int=1;
  SELECT @PublicSiteId=PublicSiteId,@ModuleCode=ModuleCode FROM orion.PublicSite
  WHERE PublicSiteKey=@PublicSiteKey AND IsActive=1;
  IF @PublicSiteId IS NULL THROW 52620,'PublicSite activo no encontrado.',1;
  SET @ProfileCode=CASE @ModuleCode WHEN 'HOSPITALITY' THEN 'HOSPITALITY_PUBLIC' WHEN 'RESTAURANT' THEN 'RESTAURANT_PUBLIC' END;
  IF @ProfileCode IS NULL THROW 52621,'El módulo no tiene un perfil público.',1;
  IF DATABASE_PRINCIPAL_ID(@PrincipalName) IS NULL THROW 52622,'El principal de base no existe.',1;
  IF EXISTS(SELECT 1 FROM sys.database_role_members WHERE member_principal_id=DATABASE_PRINCIPAL_ID(@PrincipalName))
    THROW 52623,'Un principal público no puede pertenecer a roles de base.',1;
  IF EXISTS(SELECT 1 FROM orion.PublicSqlPrincipalBinding WHERE (PrincipalName=@PrincipalName OR PublicSiteId=@PublicSiteId)
    AND NOT(PrincipalName=@PrincipalName AND PublicSiteId=@PublicSiteId))
    THROW 52624,'El principal o PublicSite ya está enlazado a otro alcance.',1;

  DECLARE @Expected TABLE
  (PermissionState varchar(5),PermissionName varchar(20),SecurableClass varchar(10),SchemaName sysname,ObjectName sysname,
   PRIMARY KEY(PermissionState,PermissionName,SecurableClass,SchemaName,ObjectName));
  INSERT @Expected SELECT PermissionState,PermissionName,SecurableClass,SchemaName,ObjectName
  FROM orion.PublicPermissionProfileEntry WHERE ProfileCode=@ProfileCode AND ProfileVersion=@ProfileVersion;
  IF NOT EXISTS(SELECT 1 FROM @Expected) THROW 52625,'El perfil no tiene entradas.',1;

  SELECT @ApplyChanges ApplyChanges,@PublicSiteId PublicSiteId,@ProfileCode ProfileCode,@ProfileVersion ProfileVersion,
    expected.PermissionState Estado,expected.PermissionName Permiso,
    expected.SecurableClass+N'::'+QUOTENAME(expected.SchemaName)+CASE WHEN expected.SecurableClass='OBJECT' THEN N'.'+QUOTENAME(expected.ObjectName) ELSE N'' END Securable,
    CONVERT(bit,CASE WHEN permissionInfo.permission_name IS NULL THEN 0 ELSE 1 END) YaExiste
  FROM @Expected expected
  LEFT JOIN sys.database_permissions permissionInfo
    ON permissionInfo.grantee_principal_id=DATABASE_PRINCIPAL_ID(@PrincipalName)
   AND permissionInfo.permission_name COLLATE DATABASE_DEFAULT=expected.PermissionName
   AND permissionInfo.state COLLATE DATABASE_DEFAULT=CASE expected.PermissionState WHEN 'GRANT' THEN 'G' ELSE 'D' END
   AND permissionInfo.class=CASE expected.SecurableClass WHEN 'OBJECT' THEN 1 ELSE 3 END
   AND permissionInfo.major_id=CASE expected.SecurableClass WHEN 'OBJECT' THEN OBJECT_ID(QUOTENAME(expected.SchemaName)+N'.'+QUOTENAME(expected.ObjectName)) ELSE SCHEMA_ID(expected.SchemaName) END
  ORDER BY expected.PermissionState,expected.SecurableClass,expected.SchemaName,expected.ObjectName,expected.PermissionName;

  IF @ApplyChanges=0 RETURN;
  BEGIN TRANSACTION;
  BEGIN TRY
    DECLARE @Sql nvarchar(max),@PermissionName varchar(20),@Class varchar(10),@SchemaName sysname,@ObjectName sysname,@State varchar(5);
    DECLARE existing_cursor CURSOR LOCAL FAST_FORWARD FOR
      SELECT DISTINCT permission_name,CASE class WHEN 1 THEN 'OBJECT' ELSE 'SCHEMA' END,
        CASE class WHEN 1 THEN OBJECT_SCHEMA_NAME(major_id) ELSE SCHEMA_NAME(major_id) END,
        CASE class WHEN 1 THEN OBJECT_NAME(major_id) ELSE N'' END
      FROM sys.database_permissions WHERE grantee_principal_id=DATABASE_PRINCIPAL_ID(@PrincipalName) AND class IN(1,3);
    OPEN existing_cursor; FETCH NEXT FROM existing_cursor INTO @PermissionName,@Class,@SchemaName,@ObjectName;
    WHILE @@FETCH_STATUS=0
    BEGIN
      SET @Sql=N'REVOKE '+@PermissionName+N' ON '+@Class+N'::'+QUOTENAME(@SchemaName)+CASE WHEN @Class='OBJECT' THEN N'.'+QUOTENAME(@ObjectName) ELSE N'' END+N' FROM '+QUOTENAME(@PrincipalName)+N';';
      EXEC sys.sp_executesql @Sql;
      FETCH NEXT FROM existing_cursor INTO @PermissionName,@Class,@SchemaName,@ObjectName;
    END;
    CLOSE existing_cursor; DEALLOCATE existing_cursor;

    DECLARE expected_cursor CURSOR LOCAL FAST_FORWARD FOR
      SELECT PermissionState,PermissionName,SecurableClass,SchemaName,ObjectName FROM @Expected
      ORDER BY PermissionState,SecurableClass,SchemaName,ObjectName,PermissionName;
    OPEN expected_cursor; FETCH NEXT FROM expected_cursor INTO @State,@PermissionName,@Class,@SchemaName,@ObjectName;
    WHILE @@FETCH_STATUS=0
    BEGIN
      SET @Sql=@State+N' '+@PermissionName+N' ON '+@Class+N'::'+QUOTENAME(@SchemaName)+CASE WHEN @Class='OBJECT' THEN N'.'+QUOTENAME(@ObjectName) ELSE N'' END+N' TO '+QUOTENAME(@PrincipalName)+N';';
      EXEC sys.sp_executesql @Sql;
      FETCH NEXT FROM expected_cursor INTO @State,@PermissionName,@Class,@SchemaName,@ObjectName;
    END;
    CLOSE expected_cursor; DEALLOCATE expected_cursor;

    MERGE orion.PublicSqlPrincipalBinding target
    USING(SELECT @PrincipalName PrincipalName) source ON target.PrincipalName=source.PrincipalName
    WHEN MATCHED THEN UPDATE SET PublicSiteId=@PublicSiteId,PermissionProfile=@ProfileCode,
      PermissionVersion=@ProfileVersion,IsActive=1,UpdatedAtUtc=SYSUTCDATETIME(),UpdatedBy=ORIGINAL_LOGIN()
    WHEN NOT MATCHED THEN INSERT(PrincipalName,PublicSiteId,PermissionProfile,PermissionVersion,IsActive,UpdatedBy)
      VALUES(@PrincipalName,@PublicSiteId,@ProfileCode,@ProfileVersion,1,ORIGINAL_LOGIN());
    COMMIT TRANSACTION;
  END TRY
  BEGIN CATCH
    IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
    THROW;
  END CATCH;
END;
GO

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
  SELECT N'APLICADO' Estado,@MigrationId2 MigrationId,(SELECT COUNT(*) FROM orion.PublicPermissionProfileEntry) ProfileEntries;
END
ELSE
BEGIN
  SELECT N'PREVIEW' Estado,@MigrationId2 MigrationId,(SELECT COUNT(*) FROM orion.PublicPermissionProfileEntry) ProfileEntries;
  ROLLBACK TRANSACTION;
END;
