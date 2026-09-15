/*
  Removes direct public access to the unfiltered online-checkout status view.

  Public callers must use restaurante.OnlineCheckoutStatusGet, which requires
  both the verified PublicSite/RFC session context and a tracking-token hash.
  RESTAURANT_PUBLIC v9 also adds an explicit object DENY as defense in depth.
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @ExpectedDatabase sysname=N'$(ExpectedDatabase)';
DECLARE @ApplyInput nvarchar(20)=N'$(ApplyChanges)';
DECLARE @ApplyChanges bit=0;
DECLARE @MigrationId nvarchar(200)=N'$(MigrationId)';
DECLARE @MigrationChecksum varchar(128)='$(MigrationChecksum)';
DECLARE @AppVersionInput nvarchar(64)=N'$(AppVersion)';
DECLARE @AppVersion nvarchar(64)=NULL;

IF @ApplyInput NOT LIKE N'$'+N'(%'
BEGIN
  IF @ApplyInput NOT IN(N'0',N'1') THROW 53770,'ApplyChanges debe ser 0 o 1.',1;
  SET @ApplyChanges=CONVERT(bit,@ApplyInput);
END;
IF @ExpectedDatabase LIKE N'$'+N'(%'
   OR @ExpectedDatabase NOT IN(N'Orion_Sandbox',N'grupocarpio',N'Orion_CutoverValidation_20260908')
  THROW 53771,'Base esperada no autorizada para el hotfix de permisos de online ordering.',1;
IF DB_NAME()<>@ExpectedDatabase
  THROW 53772,'La conexion no apunta a la base declarada.',1;
IF @MigrationId<>N'20260914_restaurant_online_ordering_status_view_permission_hotfix'
  THROW 53773,'MigrationId incorrecto.',1;
IF @MigrationChecksum LIKE '$'+N'(%' OR LEN(@MigrationChecksum)<>64
   OR @MigrationChecksum LIKE '%[^0-9A-F]%' COLLATE Latin1_General_100_BIN2
  THROW 53774,'MigrationChecksum invalido.',1;
IF @AppVersionInput NOT LIKE N'$'+N'(%'
  SET @AppVersion=NULLIF(LTRIM(RTRIM(@AppVersionInput)),N'');

IF OBJECT_ID(N'orion.SchemaMigration',N'U') IS NULL
   OR OBJECT_ID(N'orion.PublicSite',N'U') IS NULL
   OR OBJECT_ID(N'orion.PublicSqlPrincipalBinding',N'U') IS NULL
   OR OBJECT_ID(N'orion.PublicPermissionProfile',N'U') IS NULL
   OR OBJECT_ID(N'orion.PublicPermissionProfileEntry',N'U') IS NULL
   OR OBJECT_ID(N'orion.ApplyPublicPermissionProfile',N'P') IS NULL
   OR OBJECT_ID(N'restaurante.vw_PublicOnlineCheckoutStatus',N'V') IS NULL
   OR OBJECT_ID(N'restaurante.OnlineCheckoutStatusGet',N'P') IS NULL
  THROW 53775,'Faltan objetos requeridos para cerrar el acceso al estado de checkout.',1;

IF NOT EXISTS
(
  SELECT 1 FROM orion.SchemaMigration
  WHERE MigrationId=N'20260914_restaurant_online_ordering'
    AND UPPER(Checksum)='EC2449FE88F159C3941ED9B14A42450DA7E6FB57511543918D70A1CC96D4137A'
)
  THROW 53776,'La migracion base de online ordering no coincide con la version revisada.',1;
IF NOT EXISTS
(
  SELECT 1 FROM orion.SchemaMigration
  WHERE MigrationId=N'20260914_restaurant_online_ordering_runtime_corrections'
    AND UPPER(Checksum)='77204A1874353A6DA27A739D80F7B67478EC5527FAE7DF04A76009CB06C3747E'
)
  THROW 53777,'La correccion de online ordering no coincide con la version revisada.',1;
IF NOT EXISTS
(
  SELECT 1 FROM orion.PublicPermissionProfile
  WHERE ProfileCode='RESTAURANT_PUBLIC' AND ProfileVersion=8 AND ProfileChecksum IS NOT NULL
)
  THROW 53778,'Falta el perfil publico Restaurant v8.',1;

DECLARE @ExistingChecksum char(64)=
  (SELECT Checksum FROM orion.SchemaMigration WHERE MigrationId=@MigrationId);
IF @ExistingChecksum IS NOT NULL AND UPPER(@ExistingChecksum)<>UPPER(@MigrationChecksum)
  THROW 53779,'El mismo MigrationId ya existe con otro checksum.',1;
IF @ExistingChecksum IS NOT NULL
BEGIN
  SELECT N'YA_APLICADO' Estado,@MigrationId MigrationId,@ExistingChecksum Checksum;
  RETURN;
END;

SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;
BEGIN TRY
  BEGIN TRANSACTION;

  DECLARE @LockResult int;
  EXEC @LockResult=sys.sp_getapplock
    @Resource=N'OrionERP:Restaurant:OnlineOrdering:StatusViewPermissionHotfix',
    @LockMode=N'Exclusive',@LockOwner=N'Transaction',@LockTimeout=30000;
  IF @LockResult<0 THROW 53780,'No fue posible obtener el bloqueo del hotfix.',1;

  IF EXISTS
  (
    SELECT 1 FROM orion.PublicPermissionProfile
    WHERE ProfileCode='RESTAURANT_PUBLIC' AND ProfileVersion=9
  )
    THROW 53781,'Existe un perfil Restaurant v9 sin registro de esta migracion.',1;

  INSERT orion.PublicPermissionProfile(ProfileCode,ProfileVersion,ModuleCode)
  VALUES('RESTAURANT_PUBLIC',9,'RESTAURANT');

  INSERT orion.PublicPermissionProfileEntry
    (ProfileCode,ProfileVersion,PermissionState,PermissionName,SecurableClass,SchemaName,ObjectName)
  SELECT source.ProfileCode,9,source.PermissionState,source.PermissionName,
    source.SecurableClass,source.SchemaName,source.ObjectName
  FROM orion.PublicPermissionProfileEntry source
  WHERE source.ProfileCode='RESTAURANT_PUBLIC' AND source.ProfileVersion=8;

  DELETE entryInfo
  FROM orion.PublicPermissionProfileEntry entryInfo
  WHERE entryInfo.ProfileCode='RESTAURANT_PUBLIC'
    AND entryInfo.ProfileVersion=9
    AND entryInfo.PermissionName='SELECT'
    AND entryInfo.SecurableClass='OBJECT'
    AND entryInfo.SchemaName='restaurante'
    AND entryInfo.ObjectName='vw_PublicOnlineCheckoutStatus';

  INSERT orion.PublicPermissionProfileEntry
    (ProfileCode,ProfileVersion,PermissionState,PermissionName,SecurableClass,SchemaName,ObjectName)
  VALUES
    ('RESTAURANT_PUBLIC',9,'DENY','SELECT','OBJECT','restaurante','vw_PublicOnlineCheckoutStatus');

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
  WHERE profileInfo.ProfileCode='RESTAURANT_PUBLIC' AND profileInfo.ProfileVersion=9;

  IF EXISTS
  (
    SELECT 1
    FROM orion.PublicSqlPrincipalBinding binding
    JOIN orion.PublicSite publicSite ON publicSite.PublicSiteId=binding.PublicSiteId
    WHERE binding.IsActive=1 AND binding.PermissionProfile='RESTAURANT_PUBLIC'
      AND publicSite.ModuleCode='RESTAURANT'
      AND DATABASE_PRINCIPAL_ID(binding.PrincipalName) IS NULL
  )
    THROW 53782,'Un binding Restaurant activo no tiene principal de base.',1;

  DECLARE @PublicSiteKey varchar(100);
  DECLARE @PrincipalName sysname;
  DECLARE @AppliedBindings int=0;
  DECLARE @ProfileApplicationReview TABLE
  (
    ApplyChanges bit NOT NULL,
    PublicSiteId bigint NOT NULL,
    ProfileCode varchar(40) NOT NULL,
    ProfileVersion int NOT NULL,
    Estado varchar(5) NOT NULL,
    Permiso varchar(20) NOT NULL,
    Securable nvarchar(1100) NOT NULL,
    YaExiste bit NOT NULL
  );
  DECLARE public_principal_cursor CURSOR LOCAL FAST_FORWARD FOR
    SELECT publicSite.PublicSiteKey,binding.PrincipalName
    FROM orion.PublicSqlPrincipalBinding binding
    JOIN orion.PublicSite publicSite ON publicSite.PublicSiteId=binding.PublicSiteId
    WHERE binding.IsActive=1 AND binding.PermissionProfile='RESTAURANT_PUBLIC'
      AND publicSite.ModuleCode='RESTAURANT';
  OPEN public_principal_cursor;
  FETCH NEXT FROM public_principal_cursor INTO @PublicSiteKey,@PrincipalName;
  WHILE @@FETCH_STATUS=0
  BEGIN
    INSERT @ProfileApplicationReview
      (ApplyChanges,PublicSiteId,ProfileCode,ProfileVersion,Estado,Permiso,Securable,YaExiste)
    EXEC orion.ApplyPublicPermissionProfile
      @PublicSiteKey=@PublicSiteKey,
      @PrincipalName=@PrincipalName,
      @ApplyChanges=1;
    SET @AppliedBindings=@AppliedBindings+1;
    FETCH NEXT FROM public_principal_cursor INTO @PublicSiteKey,@PrincipalName;
  END;
  CLOSE public_principal_cursor;
  DEALLOCATE public_principal_cursor;

  IF @AppliedBindings=0
    THROW 53783,'No existen bindings Restaurant activos para aplicar el hotfix.',1;
  IF EXISTS
  (
    SELECT 1
    FROM orion.PublicSqlPrincipalBinding binding
    JOIN orion.PublicSite publicSite ON publicSite.PublicSiteId=binding.PublicSiteId
    WHERE binding.IsActive=1 AND binding.PermissionProfile='RESTAURANT_PUBLIC'
      AND publicSite.ModuleCode='RESTAURANT'
      AND binding.PermissionVersion<>9
  )
    THROW 53784,'No todos los bindings Restaurant recibieron el perfil v9.',1;
  IF EXISTS
  (
    SELECT 1 FROM orion.PublicPermissionProfileEntry entryInfo
    WHERE entryInfo.ProfileCode='RESTAURANT_PUBLIC' AND entryInfo.ProfileVersion=9
      AND entryInfo.PermissionState='GRANT' AND entryInfo.PermissionName='SELECT'
      AND entryInfo.SecurableClass='OBJECT' AND entryInfo.SchemaName='restaurante'
      AND entryInfo.ObjectName='vw_PublicOnlineCheckoutStatus'
  ) OR NOT EXISTS
  (
    SELECT 1 FROM orion.PublicPermissionProfileEntry entryInfo
    WHERE entryInfo.ProfileCode='RESTAURANT_PUBLIC' AND entryInfo.ProfileVersion=9
      AND entryInfo.PermissionState='DENY' AND entryInfo.PermissionName='SELECT'
      AND entryInfo.SecurableClass='OBJECT' AND entryInfo.SchemaName='restaurante'
      AND entryInfo.ObjectName='vw_PublicOnlineCheckoutStatus'
  )
    THROW 53785,'El perfil Restaurant v9 no cerro el acceso directo al view.',1;

  IF EXISTS
  (
    SELECT 1
    FROM orion.PublicSqlPrincipalBinding binding
    JOIN orion.PublicSite publicSite ON publicSite.PublicSiteId=binding.PublicSiteId
    JOIN sys.database_permissions permissionInfo
      ON permissionInfo.grantee_principal_id=DATABASE_PRINCIPAL_ID(binding.PrincipalName)
    WHERE binding.IsActive=1 AND binding.PermissionProfile='RESTAURANT_PUBLIC'
      AND publicSite.ModuleCode='RESTAURANT'
      AND permissionInfo.class=1
      AND permissionInfo.major_id=OBJECT_ID(N'restaurante.vw_PublicOnlineCheckoutStatus')
      AND permissionInfo.permission_name='SELECT'
      AND permissionInfo.state IN('G','W')
  ) OR EXISTS
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
          AND permissionInfo.class=1
          AND permissionInfo.major_id=OBJECT_ID(N'restaurante.vw_PublicOnlineCheckoutStatus')
          AND permissionInfo.permission_name='SELECT' AND permissionInfo.state='D'
      )
  ) OR EXISTS
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
          AND permissionInfo.class=1
          AND permissionInfo.major_id=OBJECT_ID(N'restaurante.OnlineCheckoutStatusGet')
          AND permissionInfo.permission_name='EXECUTE' AND permissionInfo.state IN('G','W')
      )
  )
    THROW 53786,'Los permisos efectivos de estado de checkout no quedaron cerrados.',1;

  SELECT DB_NAME() DatabaseName,@ApplyChanges ApplyChanges,
    9 RestaurantPublicPermissionVersion,@AppliedBindings UpdatedPublicBindings,
    CONVERT(bit,1) DirectStatusViewDenied,
    CONVERT(bit,1) ScopedStatusProcedureRetained;

  IF @ApplyChanges=1
  BEGIN
    INSERT orion.SchemaMigration(MigrationId,Checksum,AppliedBy,AppVersion,DatabaseName)
    VALUES
    (
      @MigrationId,@MigrationChecksum,
      COALESCE(CONVERT(nvarchar(256),SESSION_CONTEXT(N'OrionERP.UserName')),
        CONVERT(nvarchar(256),ORIGINAL_LOGIN())),
      @AppVersion,DB_NAME()
    );
    COMMIT TRANSACTION;
    SELECT N'APLICADO' Estado,@MigrationId MigrationId;
  END
  ELSE
  BEGIN
    ROLLBACK TRANSACTION;
    SELECT N'PREVIEW_VALIDADO_SIN_CAMBIOS' Estado,@MigrationId MigrationId;
  END;
END TRY
BEGIN CATCH
  IF CURSOR_STATUS('local','public_principal_cursor')>=-1
  BEGIN
    IF CURSOR_STATUS('local','public_principal_cursor')>-1 CLOSE public_principal_cursor;
    DEALLOCATE public_principal_cursor;
  END;
  IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
  THROW;
END CATCH;
