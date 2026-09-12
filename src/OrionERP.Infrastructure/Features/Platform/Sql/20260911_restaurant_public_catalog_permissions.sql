/* Version 6 adds the site-scoping lookup required by the public Restaurant catalog. */
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
  IF @ApplyInput NOT IN(N'0',N'1') THROW 53300,'ApplyChanges debe ser 0 o 1.',1;
  SET @ApplyChanges=CONVERT(bit,@ApplyInput);
END;
IF @ExpectedDatabase LIKE N'$'+N'(%'
   OR @ExpectedDatabase NOT IN(N'Orion_Sandbox',N'grupocarpio',N'Orion_CutoverValidation_20260908')
  THROW 53301,'Base esperada no permitida.',1;
IF DB_NAME()<>@ExpectedDatabase THROW 53302,'La conexión no apunta a la base declarada.',1;
IF @MigrationId<>N'20260911_restaurant_public_catalog_permissions'
  THROW 53303,'MigrationId incorrecto.',1;
IF @MigrationChecksum LIKE '$'+'(%' OR LEN(@MigrationChecksum)<>64
   OR @MigrationChecksum LIKE '%[^0-9A-F]%' COLLATE Latin1_General_100_BIN2
  THROW 53304,'MigrationChecksum inválido.',1;
IF @AppVersionInput NOT LIKE N'$'+N'(%'
  SET @AppVersion=NULLIF(LTRIM(RTRIM(@AppVersionInput)),N'');
IF OBJECT_ID(N'orion.PublicPermissionProfile',N'U') IS NULL
   OR OBJECT_ID(N'orion.PublicPermissionProfileEntry',N'U') IS NULL
   OR OBJECT_ID(N'orion.PublicSqlPrincipalBinding',N'U') IS NULL
   OR OBJECT_ID(N'restaurante.KitchenStation',N'U') IS NULL
  THROW 53305,'Faltan los perfiles, bindings u objeto de catálogo requeridos.',1;
IF NOT EXISTS
(
  SELECT 1 FROM orion.PublicPermissionProfile
  WHERE ProfileCode='RESTAURANT_PUBLIC' AND ProfileVersion=5
)
  THROW 53306,'Falta el perfil Restaurant v5.',1;

DECLARE @ExistingChecksum char(64)=
  (SELECT Checksum FROM orion.SchemaMigration WHERE MigrationId=@MigrationId);
IF @ExistingChecksum IS NOT NULL AND UPPER(@ExistingChecksum)<>UPPER(@MigrationChecksum)
  THROW 53307,'El MigrationId ya existe con otro checksum.',1;
IF @ExistingChecksum IS NOT NULL
BEGIN
  SELECT N'YA_APLICADO' Estado,@MigrationId MigrationId,@ExistingChecksum Checksum;
  RETURN;
END;

BEGIN TRANSACTION;
DECLARE @LockResult int;
EXEC @LockResult=sys.sp_getapplock
  @Resource=N'OrionERP:Migration:20260911_restaurant_public_catalog_permissions',
  @LockMode=N'Exclusive',@LockOwner=N'Transaction',@LockTimeout=15000;
IF @LockResult<0 THROW 53308,'No se obtuvo el candado de migración.',1;

INSERT orion.PublicPermissionProfile(ProfileCode,ProfileVersion,ModuleCode)
SELECT 'RESTAURANT_PUBLIC',6,'RESTAURANT'
WHERE NOT EXISTS
(
  SELECT 1 FROM orion.PublicPermissionProfile
  WHERE ProfileCode='RESTAURANT_PUBLIC' AND ProfileVersion=6
);

INSERT orion.PublicPermissionProfileEntry
(ProfileCode,ProfileVersion,PermissionState,PermissionName,SecurableClass,SchemaName,ObjectName)
SELECT source.ProfileCode,6,source.PermissionState,source.PermissionName,
  source.SecurableClass,source.SchemaName,source.ObjectName
FROM orion.PublicPermissionProfileEntry source
WHERE source.ProfileCode='RESTAURANT_PUBLIC' AND source.ProfileVersion=5
  AND NOT EXISTS
  (
    SELECT 1 FROM orion.PublicPermissionProfileEntry target
    WHERE target.ProfileCode=source.ProfileCode AND target.ProfileVersion=6
      AND target.PermissionState=source.PermissionState
      AND target.PermissionName=source.PermissionName
      AND target.SecurableClass=source.SecurableClass
      AND target.SchemaName=source.SchemaName
      AND target.ObjectName=source.ObjectName
  );

INSERT orion.PublicPermissionProfileEntry
(ProfileCode,ProfileVersion,PermissionState,PermissionName,SecurableClass,SchemaName,ObjectName)
SELECT 'RESTAURANT_PUBLIC',6,'GRANT','SELECT','OBJECT','restaurante','KitchenStation'
WHERE NOT EXISTS
(
  SELECT 1 FROM orion.PublicPermissionProfileEntry
  WHERE ProfileCode='RESTAURANT_PUBLIC' AND ProfileVersion=6
    AND PermissionState='GRANT' AND PermissionName='SELECT'
    AND SecurableClass='OBJECT' AND SchemaName='restaurante' AND ObjectName='KitchenStation'
);

UPDATE profileInfo
SET ProfileChecksum=checksumInfo.ProfileChecksum,UpdatedAtUtc=SYSUTCDATETIME()
FROM orion.PublicPermissionProfile profileInfo
CROSS APPLY
(
  SELECT HASHBYTES('SHA2_256',STRING_AGG(CONVERT(nvarchar(max),
    entryInfo.PermissionState+N'|'+entryInfo.PermissionName+N'|'+entryInfo.SecurableClass+N'|'+
    entryInfo.SchemaName+N'|'+entryInfo.ObjectName),N';')
    WITHIN GROUP(ORDER BY entryInfo.PermissionState,entryInfo.PermissionName,
      entryInfo.SecurableClass,entryInfo.SchemaName,entryInfo.ObjectName)) ProfileChecksum
  FROM orion.PublicPermissionProfileEntry entryInfo
  WHERE entryInfo.ProfileCode=profileInfo.ProfileCode
    AND entryInfo.ProfileVersion=profileInfo.ProfileVersion
) checksumInfo
WHERE profileInfo.ProfileCode='RESTAURANT_PUBLIC' AND profileInfo.ProfileVersion=6;

IF EXISTS
(
  SELECT 1
  FROM orion.PublicSqlPrincipalBinding binding
  JOIN orion.PublicSite publicSite ON publicSite.PublicSiteId=binding.PublicSiteId
  WHERE binding.IsActive=1 AND binding.PermissionProfile='RESTAURANT_PUBLIC'
    AND publicSite.ModuleCode='RESTAURANT'
    AND DATABASE_PRINCIPAL_ID(binding.PrincipalName) IS NULL
)
  THROW 53309,'Un binding Restaurant activo no tiene principal de base.',1;

DECLARE @PrincipalName sysname,@GrantSql nvarchar(max);
DECLARE @MissingBefore int=
(
  SELECT COUNT(*)
  FROM orion.PublicSqlPrincipalBinding binding
  JOIN orion.PublicSite publicSite ON publicSite.PublicSiteId=binding.PublicSiteId
  WHERE binding.IsActive=1 AND binding.PermissionProfile='RESTAURANT_PUBLIC'
    AND publicSite.ModuleCode='RESTAURANT'
    AND NOT EXISTS
    (
      SELECT 1 FROM sys.database_permissions permissionInfo
      WHERE permissionInfo.grantee_principal_id=DATABASE_PRINCIPAL_ID(binding.PrincipalName)
        AND permissionInfo.permission_name='SELECT' AND permissionInfo.state='G'
        AND permissionInfo.class=1
        AND permissionInfo.major_id=OBJECT_ID(N'restaurante.KitchenStation')
    )
);
DECLARE principal_cursor CURSOR LOCAL FAST_FORWARD FOR
  SELECT binding.PrincipalName
  FROM orion.PublicSqlPrincipalBinding binding
  JOIN orion.PublicSite publicSite ON publicSite.PublicSiteId=binding.PublicSiteId
  WHERE binding.IsActive=1 AND binding.PermissionProfile='RESTAURANT_PUBLIC'
    AND publicSite.ModuleCode='RESTAURANT';
OPEN principal_cursor;
FETCH NEXT FROM principal_cursor INTO @PrincipalName;
WHILE @@FETCH_STATUS=0
BEGIN
  SET @GrantSql=N'GRANT SELECT ON OBJECT::[restaurante].[KitchenStation] TO '+QUOTENAME(@PrincipalName)+N';';
  EXEC sys.sp_executesql @GrantSql;
  FETCH NEXT FROM principal_cursor INTO @PrincipalName;
END;
CLOSE principal_cursor;
DEALLOCATE principal_cursor;

UPDATE binding
SET PermissionVersion=6,UpdatedAtUtc=SYSUTCDATETIME(),UpdatedBy=ORIGINAL_LOGIN()
FROM orion.PublicSqlPrincipalBinding binding
JOIN orion.PublicSite publicSite ON publicSite.PublicSiteId=binding.PublicSiteId
WHERE binding.IsActive=1 AND binding.PermissionProfile='RESTAURANT_PUBLIC'
  AND publicSite.ModuleCode='RESTAURANT';
DECLARE @UpdatedBindings int=@@ROWCOUNT;

IF EXISTS
(
  SELECT 1
  FROM orion.PublicSqlPrincipalBinding binding
  JOIN orion.PublicSite publicSite ON publicSite.PublicSiteId=binding.PublicSiteId
  WHERE binding.IsActive=1 AND binding.PermissionProfile='RESTAURANT_PUBLIC'
    AND publicSite.ModuleCode='RESTAURANT'
    AND NOT EXISTS
    (
      SELECT 1 FROM sys.database_permissions permissionInfo
      WHERE permissionInfo.grantee_principal_id=DATABASE_PRINCIPAL_ID(binding.PrincipalName)
        AND permissionInfo.permission_name='SELECT' AND permissionInfo.state='G'
        AND permissionInfo.class=1
        AND permissionInfo.major_id=OBJECT_ID(N'restaurante.KitchenStation')
    )
)
  THROW 53310,'No quedó aplicado el permiso de catálogo a todos los principals Restaurant.',1;

IF @ApplyChanges=1
BEGIN
  INSERT orion.SchemaMigration(MigrationId,Checksum,AppliedBy,AppVersion,DatabaseName)
  VALUES(@MigrationId,@MigrationChecksum,
    COALESCE(CONVERT(nvarchar(256),SESSION_CONTEXT(N'OrionERP.UserName')),
      CONVERT(nvarchar(256),ORIGINAL_LOGIN())),
    @AppVersion,DB_NAME());
  COMMIT TRANSACTION;
  SELECT N'APLICADO' Estado,@MigrationId MigrationId,
    @MissingBefore PermissionChanges,@UpdatedBindings UpdatedBindings;
END
ELSE
BEGIN
  SELECT N'PREVIEW' Estado,@MigrationId MigrationId,
    @MissingBefore PermissionChanges,@UpdatedBindings UpdatedBindings;
  ROLLBACK TRANSACTION;
END;
