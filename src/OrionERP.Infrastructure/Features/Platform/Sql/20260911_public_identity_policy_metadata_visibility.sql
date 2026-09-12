/* Version 4 exposes only the public Identity RLS policy metadata used by readiness. */
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
  IF @ApplyInput NOT IN(N'0',N'1') THROW 52750,'ApplyChanges debe ser 0 o 1.',1;
  SET @ApplyChanges=CONVERT(bit,@ApplyInput);
END;
IF @ExpectedDatabase LIKE N'$'+N'(%'
   OR @ExpectedDatabase NOT IN(N'Orion_Sandbox',N'grupocarpio',N'Orion_CutoverValidation_20260908')
  THROW 52751,'Base esperada no permitida.',1;
IF DB_NAME()<>@ExpectedDatabase THROW 52752,'La conexión no apunta a la base declarada.',1;
IF @MigrationId<>N'20260911_public_identity_policy_metadata_visibility'
  THROW 52753,'MigrationId incorrecto.',1;
IF @MigrationChecksum LIKE '$'+'(%' OR LEN(@MigrationChecksum)<>64
   OR @MigrationChecksum LIKE '%[^0-9A-F]%' COLLATE Latin1_General_100_BIN2
  THROW 52754,'MigrationChecksum inválido.',1;
IF @AppVersionInput NOT LIKE N'$'+N'(%'
  SET @AppVersion=NULLIF(LTRIM(RTRIM(@AppVersionInput)),N'');
IF OBJECT_ID(N'orion.PublicIdentityScopePolicy',N'SP') IS NULL
   OR OBJECT_ID(N'orion.PublicPermissionProfile',N'U') IS NULL
   OR OBJECT_ID(N'orion.PublicPermissionProfileEntry',N'U') IS NULL
  THROW 52755,'Faltan la política de identidad pública o los perfiles de permisos.',1;
IF NOT EXISTS
(
  SELECT 1 FROM orion.PublicPermissionProfile
  WHERE ProfileCode='RESTAURANT_PUBLIC' AND ProfileVersion=3
)
  THROW 52756,'Falta el perfil público Restaurant v3.',1;

DECLARE @ExistingChecksum char(64)=
  (SELECT Checksum FROM orion.SchemaMigration WHERE MigrationId=@MigrationId);
IF @ExistingChecksum IS NOT NULL AND UPPER(@ExistingChecksum)<>UPPER(@MigrationChecksum)
  THROW 52757,'El MigrationId ya existe con otro checksum.',1;
IF @ExistingChecksum IS NOT NULL
BEGIN
  SELECT N'YA_APLICADO' Estado,@MigrationId MigrationId,@ExistingChecksum Checksum;
  RETURN;
END;

BEGIN TRANSACTION;

INSERT orion.PublicPermissionProfile(ProfileCode,ProfileVersion,ModuleCode)
SELECT source.ProfileCode,4,source.ModuleCode
FROM orion.PublicPermissionProfile source
WHERE source.ProfileVersion=3
  AND NOT EXISTS
  (
    SELECT 1 FROM orion.PublicPermissionProfile target
    WHERE target.ProfileCode=source.ProfileCode AND target.ProfileVersion=4
  );

INSERT orion.PublicPermissionProfileEntry
(ProfileCode,ProfileVersion,PermissionState,PermissionName,SecurableClass,SchemaName,ObjectName)
SELECT source.ProfileCode,4,source.PermissionState,source.PermissionName,
  source.SecurableClass,source.SchemaName,source.ObjectName
FROM orion.PublicPermissionProfileEntry source
WHERE source.ProfileVersion=3
  AND NOT EXISTS
  (
    SELECT 1 FROM orion.PublicPermissionProfileEntry target
    WHERE target.ProfileCode=source.ProfileCode AND target.ProfileVersion=4
      AND target.PermissionState=source.PermissionState
      AND target.PermissionName=source.PermissionName
      AND target.SecurableClass=source.SecurableClass
      AND target.SchemaName=source.SchemaName
      AND target.ObjectName=source.ObjectName
  );

INSERT orion.PublicPermissionProfileEntry
(ProfileCode,ProfileVersion,PermissionState,PermissionName,SecurableClass,SchemaName,ObjectName)
SELECT 'RESTAURANT_PUBLIC',4,'GRANT','VIEW DEFINITION','OBJECT','orion','PublicIdentityScopePolicy'
WHERE NOT EXISTS
(
  SELECT 1 FROM orion.PublicPermissionProfileEntry
  WHERE ProfileCode='RESTAURANT_PUBLIC' AND ProfileVersion=4
    AND PermissionState='GRANT' AND PermissionName='VIEW DEFINITION'
    AND SecurableClass='OBJECT' AND SchemaName='orion'
    AND ObjectName='PublicIdentityScopePolicy'
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
WHERE profileInfo.ProfileVersion=4;

IF NOT EXISTS
(
  SELECT 1 FROM orion.PublicPermissionProfileEntry
  WHERE ProfileCode='RESTAURANT_PUBLIC' AND ProfileVersion=4
    AND PermissionState='GRANT' AND PermissionName='VIEW DEFINITION'
    AND SecurableClass='OBJECT' AND SchemaName='orion'
    AND ObjectName='PublicIdentityScopePolicy'
)
  THROW 52758,'El perfil Restaurant v4 no contiene la visibilidad mínima de la política.',1;

IF EXISTS
(
  SELECT 1 FROM orion.PublicSqlPrincipalBinding binding
  WHERE binding.IsActive=1
    AND binding.PermissionProfile IN('HOSPITALITY_PUBLIC','RESTAURANT_PUBLIC')
    AND DATABASE_PRINCIPAL_ID(binding.PrincipalName) IS NULL
)
  THROW 52759,'Un binding público activo no tiene principal de base.',1;

DECLARE @PrincipalName sysname,@GrantSql nvarchar(max);
DECLARE principal_cursor CURSOR LOCAL FAST_FORWARD FOR
  SELECT binding.PrincipalName
  FROM orion.PublicSqlPrincipalBinding binding
  WHERE binding.IsActive=1 AND binding.PermissionProfile='RESTAURANT_PUBLIC';
OPEN principal_cursor;
FETCH NEXT FROM principal_cursor INTO @PrincipalName;
WHILE @@FETCH_STATUS=0
BEGIN
  SET @GrantSql=N'GRANT VIEW DEFINITION ON OBJECT::[orion].[PublicIdentityScopePolicy] TO '
    +QUOTENAME(@PrincipalName)+N';';
  EXEC sys.sp_executesql @GrantSql;
  FETCH NEXT FROM principal_cursor INTO @PrincipalName;
END;
CLOSE principal_cursor;
DEALLOCATE principal_cursor;

UPDATE binding
SET PermissionVersion=4,UpdatedAtUtc=SYSUTCDATETIME(),UpdatedBy=ORIGINAL_LOGIN()
FROM orion.PublicSqlPrincipalBinding binding
WHERE binding.IsActive=1
  AND binding.PermissionProfile IN('HOSPITALITY_PUBLIC','RESTAURANT_PUBLIC');
DECLARE @UpdatedBindings int=@@ROWCOUNT;

IF @ApplyChanges=1
BEGIN
  INSERT orion.SchemaMigration(MigrationId,Checksum,AppliedBy,AppVersion,DatabaseName)
  VALUES(@MigrationId,@MigrationChecksum,
    COALESCE(CONVERT(nvarchar(256),SESSION_CONTEXT(N'OrionERP.UserName')),
      CONVERT(nvarchar(256),ORIGINAL_LOGIN())),
    @AppVersion,DB_NAME());
  COMMIT TRANSACTION;
  SELECT N'APLICADO' Estado,@MigrationId MigrationId,
    (SELECT COUNT(*) FROM orion.PublicPermissionProfileEntry WHERE ProfileVersion=4) ProfileEntries,
    @UpdatedBindings UpdatedBindings;
END
ELSE
BEGIN
  SELECT N'PREVIEW' Estado,@MigrationId MigrationId,
    (SELECT COUNT(*) FROM orion.PublicPermissionProfileEntry WHERE ProfileVersion=4) ProfileEntries,
    @UpdatedBindings UpdatedBindings;
  ROLLBACK TRANSACTION;
END;
