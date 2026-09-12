/* Version 5 restores minimal policy metadata visibility after RLS policy recreation. */
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
  IF @ApplyInput NOT IN(N'0',N'1') THROW 52760,'ApplyChanges debe ser 0 o 1.',1;
  SET @ApplyChanges=CONVERT(bit,@ApplyInput);
END;
IF @ExpectedDatabase LIKE N'$'+N'(%'
   OR @ExpectedDatabase NOT IN(N'Orion_Sandbox',N'grupocarpio',N'Orion_CutoverValidation_20260908')
  THROW 52761,'Base esperada no permitida.',1;
IF DB_NAME()<>@ExpectedDatabase THROW 52762,'La conexión no apunta a la base declarada.',1;
IF @MigrationId<>N'20260911_public_rls_policy_metadata_permissions'
  THROW 52763,'MigrationId incorrecto.',1;
IF @MigrationChecksum LIKE '$'+'(%' OR LEN(@MigrationChecksum)<>64
   OR @MigrationChecksum LIKE '%[^0-9A-F]%' COLLATE Latin1_General_100_BIN2
  THROW 52764,'MigrationChecksum inválido.',1;
IF @AppVersionInput NOT LIKE N'$'+N'(%'
  SET @AppVersion=NULLIF(LTRIM(RTRIM(@AppVersionInput)),N'');
IF OBJECT_ID(N'orion.PublicIdentityScopePolicy',N'SP') IS NULL
   OR OBJECT_ID(N'orion.HospitalityScopePolicy',N'SP') IS NULL
   OR OBJECT_ID(N'orion.PublicPermissionProfile',N'U') IS NULL
   OR OBJECT_ID(N'orion.PublicPermissionProfileEntry',N'U') IS NULL
  THROW 52765,'Faltan las políticas RLS o los perfiles de permisos.',1;
IF NOT EXISTS
(
  SELECT 1 FROM orion.SchemaMigration
  WHERE MigrationId=N'20260911_public_rls_principal_binding'
)
  THROW 52766,'Debe aplicarse primero el binding RLS por principal público.',1;
IF NOT EXISTS
(
  SELECT 1 FROM orion.PublicPermissionProfile
  WHERE ProfileCode='RESTAURANT_PUBLIC' AND ProfileVersion=4
)
  THROW 52767,'Falta el perfil público v4.',1;

DECLARE @ExistingChecksum char(64)=
  (SELECT Checksum FROM orion.SchemaMigration WHERE MigrationId=@MigrationId);
IF @ExistingChecksum IS NOT NULL AND UPPER(@ExistingChecksum)<>UPPER(@MigrationChecksum)
  THROW 52768,'El MigrationId ya existe con otro checksum.',1;
IF @ExistingChecksum IS NOT NULL
BEGIN
  SELECT N'YA_APLICADO' Estado,@MigrationId MigrationId,@ExistingChecksum Checksum;
  RETURN;
END;

BEGIN TRANSACTION;

INSERT orion.PublicPermissionProfile(ProfileCode,ProfileVersion,ModuleCode)
SELECT source.ProfileCode,5,source.ModuleCode
FROM orion.PublicPermissionProfile source
WHERE source.ProfileVersion=4
  AND NOT EXISTS
  (
    SELECT 1 FROM orion.PublicPermissionProfile target
    WHERE target.ProfileCode=source.ProfileCode AND target.ProfileVersion=5
  );

INSERT orion.PublicPermissionProfileEntry
(ProfileCode,ProfileVersion,PermissionState,PermissionName,SecurableClass,SchemaName,ObjectName)
SELECT source.ProfileCode,5,source.PermissionState,source.PermissionName,
  source.SecurableClass,source.SchemaName,source.ObjectName
FROM orion.PublicPermissionProfileEntry source
WHERE source.ProfileVersion=4
  AND NOT EXISTS
  (
    SELECT 1 FROM orion.PublicPermissionProfileEntry target
    WHERE target.ProfileCode=source.ProfileCode AND target.ProfileVersion=5
      AND target.PermissionState=source.PermissionState
      AND target.PermissionName=source.PermissionName
      AND target.SecurableClass=source.SecurableClass
      AND target.SchemaName=source.SchemaName
      AND target.ObjectName=source.ObjectName
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
WHERE profileInfo.ProfileVersion=5;

IF NOT EXISTS
(
  SELECT 1 FROM orion.PublicPermissionProfileEntry
  WHERE ProfileCode='HOSPITALITY_PUBLIC' AND ProfileVersion=5
    AND PermissionState='GRANT' AND PermissionName='VIEW DEFINITION'
    AND SecurableClass='OBJECT' AND SchemaName='orion'
    AND ObjectName='HospitalityScopePolicy'
)
  THROW 52769,'El perfil Hospitality v5 no contiene la visibilidad mínima de su política.',1;
IF NOT EXISTS
(
  SELECT 1 FROM orion.PublicPermissionProfileEntry
  WHERE ProfileCode='RESTAURANT_PUBLIC' AND ProfileVersion=5
    AND PermissionState='GRANT' AND PermissionName='VIEW DEFINITION'
    AND SecurableClass='OBJECT' AND SchemaName='orion'
    AND ObjectName='PublicIdentityScopePolicy'
)
  THROW 52770,'El perfil Restaurant v5 perdió la visibilidad mínima de su política.',1;

IF EXISTS
(
  SELECT 1 FROM orion.PublicSqlPrincipalBinding binding
  WHERE binding.IsActive=1
    AND binding.PermissionProfile IN('HOSPITALITY_PUBLIC','RESTAURANT_PUBLIC')
    AND DATABASE_PRINCIPAL_ID(binding.PrincipalName) IS NULL
)
  THROW 52771,'Un binding público activo no tiene principal de base.',1;

DECLARE @PrincipalName sysname,@ModuleCode varchar(20),@GrantSql nvarchar(max);
DECLARE principal_cursor CURSOR LOCAL FAST_FORWARD FOR
  SELECT binding.PrincipalName,publicSite.ModuleCode
  FROM orion.PublicSqlPrincipalBinding binding
  JOIN orion.PublicSite publicSite ON publicSite.PublicSiteId=binding.PublicSiteId
  WHERE binding.IsActive=1
    AND binding.PermissionProfile IN('HOSPITALITY_PUBLIC','RESTAURANT_PUBLIC');
OPEN principal_cursor;
FETCH NEXT FROM principal_cursor INTO @PrincipalName,@ModuleCode;
WHILE @@FETCH_STATUS=0
BEGIN
  SET @GrantSql=CASE @ModuleCode
    WHEN 'HOSPITALITY' THEN N'GRANT VIEW DEFINITION ON OBJECT::[orion].[HospitalityScopePolicy] TO '
    WHEN 'RESTAURANT' THEN N'GRANT VIEW DEFINITION ON OBJECT::[orion].[PublicIdentityScopePolicy] TO '
    ELSE NULL END;
  IF @GrantSql IS NULL THROW 52772,'El binding público tiene un módulo no soportado.',1;
  SET @GrantSql+=QUOTENAME(@PrincipalName)+N';';
  EXEC sys.sp_executesql @GrantSql;
  FETCH NEXT FROM principal_cursor INTO @PrincipalName,@ModuleCode;
END;
CLOSE principal_cursor;
DEALLOCATE principal_cursor;

UPDATE binding
SET PermissionVersion=5,UpdatedAtUtc=SYSUTCDATETIME(),UpdatedBy=ORIGINAL_LOGIN()
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
    (SELECT COUNT(*) FROM orion.PublicPermissionProfileEntry WHERE ProfileVersion=5) ProfileEntries,
    @UpdatedBindings UpdatedBindings;
END
ELSE
BEGIN
  SELECT N'PREVIEW' Estado,@MigrationId MigrationId,
    (SELECT COUNT(*) FROM orion.PublicPermissionProfileEntry WHERE ProfileVersion=5) ProfileEntries,
    @UpdatedBindings UpdatedBindings;
  ROLLBACK TRANSACTION;
END;
