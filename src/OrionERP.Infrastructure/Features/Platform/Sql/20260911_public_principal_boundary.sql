/* Version 2 adds the exact binding read needed to reject cross-PublicSite SQL principals. */
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
  IF @ApplyInput NOT IN(N'0',N'1') THROW 52700,'ApplyChanges debe ser 0 o 1.',1;
  SET @ApplyChanges=CONVERT(bit,@ApplyInput);
END;
IF @ExpectedDatabase LIKE N'$'+N'(%' OR @ExpectedDatabase NOT IN(N'Orion_Sandbox',N'grupocarpio',N'Orion_CutoverValidation_20260908')
  THROW 52701,'Base esperada no permitida.',1;
IF DB_NAME()<>@ExpectedDatabase THROW 52702,'La conexión no apunta a la base declarada.',1;
IF @MigrationId<>N'20260911_public_principal_boundary' THROW 52703,'MigrationId incorrecto.',1;
IF @MigrationChecksum LIKE '$'+'(%' OR LEN(@MigrationChecksum)<>64
   OR @MigrationChecksum LIKE '%[^0-9A-F]%' COLLATE Latin1_General_100_BIN2
  THROW 52704,'MigrationChecksum inválido.',1;
IF @AppVersionInput NOT LIKE N'$'+N'(%' SET @AppVersion=NULLIF(LTRIM(RTRIM(@AppVersionInput)),N'');
IF OBJECT_ID(N'orion.PublicPermissionProfileEntry',N'U') IS NULL
   OR OBJECT_ID(N'orion.ApplyPublicPermissionProfile',N'P') IS NULL
  THROW 52705,'Falta la versión inicial de perfiles públicos.',1;
DECLARE @ExistingChecksum char(64)=(SELECT Checksum FROM orion.SchemaMigration WHERE MigrationId=@MigrationId);
IF @ExistingChecksum IS NOT NULL AND UPPER(@ExistingChecksum)<>UPPER(@MigrationChecksum)
  THROW 52706,'El MigrationId ya existe con otro checksum.',1;
IF @ExistingChecksum IS NOT NULL
BEGIN SELECT N'YA_APLICADO' Estado,@MigrationId MigrationId,@ExistingChecksum Checksum; RETURN; END;

BEGIN TRANSACTION;
INSERT orion.PublicPermissionProfile(ProfileCode,ProfileVersion,ModuleCode)
SELECT source.ProfileCode,2,source.ModuleCode
FROM (VALUES('HOSPITALITY_PUBLIC','HOSPITALITY'),('RESTAURANT_PUBLIC','RESTAURANT')) source(ProfileCode,ModuleCode)
WHERE NOT EXISTS(SELECT 1 FROM orion.PublicPermissionProfile target WHERE target.ProfileCode=source.ProfileCode AND target.ProfileVersion=2);
INSERT orion.PublicPermissionProfileEntry
(ProfileCode,ProfileVersion,PermissionState,PermissionName,SecurableClass,SchemaName,ObjectName)
SELECT ProfileCode,2,PermissionState,PermissionName,SecurableClass,SchemaName,ObjectName
FROM orion.PublicPermissionProfileEntry source WHERE ProfileVersion=1 AND NOT EXISTS
(
  SELECT 1 FROM orion.PublicPermissionProfileEntry target
  WHERE target.ProfileCode=source.ProfileCode AND target.ProfileVersion=2
    AND target.PermissionState=source.PermissionState AND target.PermissionName=source.PermissionName
    AND target.SecurableClass=source.SecurableClass AND target.SchemaName=source.SchemaName
    AND target.ObjectName=source.ObjectName
);
INSERT orion.PublicPermissionProfileEntry
(ProfileCode,ProfileVersion,PermissionState,PermissionName,SecurableClass,SchemaName,ObjectName)
SELECT source.ProfileCode,2,'GRANT','SELECT','OBJECT','orion','PublicSqlPrincipalBinding'
FROM (VALUES('HOSPITALITY_PUBLIC'),('RESTAURANT_PUBLIC')) source(ProfileCode)
WHERE NOT EXISTS
(
  SELECT 1 FROM orion.PublicPermissionProfileEntry target
  WHERE target.ProfileCode=source.ProfileCode AND target.ProfileVersion=2
    AND target.PermissionState='GRANT' AND target.PermissionName='SELECT'
    AND target.SecurableClass='OBJECT' AND target.SchemaName='orion'
    AND target.ObjectName='PublicSqlPrincipalBinding'
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
) checksumInfo WHERE profileInfo.ProfileVersion=2;
GO

CREATE OR ALTER PROCEDURE orion.ApplyPublicPermissionProfile
  @PublicSiteKey varchar(100),
  @PrincipalName sysname,
  @ApplyChanges bit=0
AS
BEGIN
  SET NOCOUNT ON;
  SET XACT_ABORT ON;
  DECLARE @PublicSiteId bigint,@ModuleCode varchar(40),@ProfileCode varchar(40),@ProfileVersion int;
  SELECT @PublicSiteId=PublicSiteId,@ModuleCode=ModuleCode FROM orion.PublicSite
  WHERE PublicSiteKey=@PublicSiteKey AND IsActive=1;
  IF @PublicSiteId IS NULL THROW 52620,'PublicSite activo no encontrado.',1;
  SET @ProfileCode=CASE @ModuleCode WHEN 'HOSPITALITY' THEN 'HOSPITALITY_PUBLIC' WHEN 'RESTAURANT' THEN 'RESTAURANT_PUBLIC' END;
  IF @ProfileCode IS NULL THROW 52621,'El módulo no tiene un perfil público.',1;
  SELECT @ProfileVersion=MAX(ProfileVersion) FROM orion.PublicPermissionProfile WHERE ProfileCode=@ProfileCode;
  IF @ProfileVersion IS NULL THROW 52625,'El perfil no tiene una versión activa.',1;
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

  SELECT @ApplyChanges ApplyChanges,@PublicSiteId PublicSiteId,@ProfileCode ProfileCode,@ProfileVersion ProfileVersion,
    expected.PermissionState Estado,expected.PermissionName Permiso,
    expected.SecurableClass+N'::'+QUOTENAME(expected.SchemaName)+CASE WHEN expected.SecurableClass='OBJECT' THEN N'.'+QUOTENAME(expected.ObjectName) ELSE N'' END Securable,
    CONVERT(bit,CASE WHEN permissionInfo.permission_name IS NULL THEN 0 ELSE 1 END) YaExiste
  FROM @Expected expected LEFT JOIN sys.database_permissions permissionInfo
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
    MERGE orion.PublicSqlPrincipalBinding target USING(SELECT @PrincipalName PrincipalName) source
      ON target.PrincipalName=source.PrincipalName
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
  SELECT N'APLICADO' Estado,@MigrationId2 MigrationId,(SELECT COUNT(*) FROM orion.PublicPermissionProfileEntry WHERE ProfileVersion=2) ProfileEntries;
END
ELSE
BEGIN
  SELECT N'PREVIEW' Estado,@MigrationId2 MigrationId,(SELECT COUNT(*) FROM orion.PublicPermissionProfileEntry WHERE ProfileVersion=2) ProfileEntries;
  ROLLBACK TRANSACTION;
END;
