/*
  Otorga al principal público de Restaurante la lectura de logistica.MaterialCategory.

  Sin este permiso, /api/restaurant/checkout/quote lanzaba 500 en cuanto el cliente
  escribía un código de promoción que el motor rechazaba por alcance: al explicar el
  rechazo, RestaurantPromotionService.LoadScopeLabelsAsync lee las categorías que
  participan en la promoción y chocaba con

    The SELECT permission was denied on the object 'MaterialCategory',
    database 'grupocarpio', schema 'logistica'.

  El perfil ya concedía restaurante.PromotionMaterialCategory (la tabla puente) pero
  nunca la tabla de categorías a la que esa puente hace JOIN, así que la promoción por
  categoría era la única ruta del catálogo público que quedaba fuera de la matriz.

  RESTAURANT_PUBLIC v10 = v9 + GRANT SELECT sobre logistica.MaterialCategory.
  Es lectura de catálogo, del mismo nivel que logistica.Material, que ya estaba dada.
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
  IF @ApplyInput NOT IN(N'0',N'1') THROW 53800,'ApplyChanges debe ser 0 o 1.',1;
  SET @ApplyChanges=CONVERT(bit,@ApplyInput);
END;
IF @ExpectedDatabase LIKE N'$'+N'(%'
   OR @ExpectedDatabase NOT IN(N'Orion_Sandbox',N'grupocarpio',N'Orion_CutoverValidation_20260908')
  THROW 53801,'Base esperada no autorizada para el permiso de categorias de material.',1;
IF DB_NAME()<>@ExpectedDatabase
  THROW 53802,'La conexion no apunta a la base declarada.',1;
IF @MigrationId<>N'20260916_restaurant_public_material_category_permission'
  THROW 53803,'MigrationId incorrecto.',1;
IF @MigrationChecksum LIKE '$'+N'(%' OR LEN(@MigrationChecksum)<>64
   OR @MigrationChecksum LIKE '%[^0-9A-F]%' COLLATE Latin1_General_100_BIN2
  THROW 53804,'MigrationChecksum invalido.',1;
IF @AppVersionInput NOT LIKE N'$'+N'(%'
  SET @AppVersion=NULLIF(LTRIM(RTRIM(@AppVersionInput)),N'');

IF OBJECT_ID(N'orion.SchemaMigration',N'U') IS NULL
   OR OBJECT_ID(N'orion.PublicSite',N'U') IS NULL
   OR OBJECT_ID(N'orion.PublicSqlPrincipalBinding',N'U') IS NULL
   OR OBJECT_ID(N'orion.PublicPermissionProfile',N'U') IS NULL
   OR OBJECT_ID(N'orion.PublicPermissionProfileEntry',N'U') IS NULL
   OR OBJECT_ID(N'orion.ApplyPublicPermissionProfile',N'P') IS NULL
   OR OBJECT_ID(N'logistica.MaterialCategory',N'U') IS NULL
   OR OBJECT_ID(N'restaurante.PromotionMaterialCategory',N'U') IS NULL
  THROW 53805,'Faltan objetos requeridos para otorgar la lectura de categorias.',1;

IF NOT EXISTS
(
  SELECT 1 FROM orion.PublicPermissionProfile
  WHERE ProfileCode='RESTAURANT_PUBLIC' AND ProfileVersion=9 AND ProfileChecksum IS NOT NULL
)
  THROW 53806,'Falta el perfil publico Restaurant v9.',1;

DECLARE @ExistingChecksum char(64)=
  (SELECT Checksum FROM orion.SchemaMigration WHERE MigrationId=@MigrationId);
IF @ExistingChecksum IS NOT NULL AND UPPER(@ExistingChecksum)<>UPPER(@MigrationChecksum)
  THROW 53807,'El mismo MigrationId ya existe con otro checksum.',1;
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
    @Resource=N'OrionERP:Restaurant:Public:MaterialCategoryPermission',
    @LockMode=N'Exclusive',@LockOwner=N'Transaction',@LockTimeout=30000;
  IF @LockResult<0 THROW 53808,'No fue posible obtener el bloqueo de la migracion.',1;

  IF EXISTS
  (
    SELECT 1 FROM orion.PublicPermissionProfile
    WHERE ProfileCode='RESTAURANT_PUBLIC' AND ProfileVersion=10
  )
    THROW 53809,'Existe un perfil Restaurant v10 sin registro de esta migracion.',1;

  INSERT orion.PublicPermissionProfile(ProfileCode,ProfileVersion,ModuleCode)
  VALUES('RESTAURANT_PUBLIC',10,'RESTAURANT');

  INSERT orion.PublicPermissionProfileEntry
    (ProfileCode,ProfileVersion,PermissionState,PermissionName,SecurableClass,SchemaName,ObjectName)
  SELECT source.ProfileCode,10,source.PermissionState,source.PermissionName,
    source.SecurableClass,source.SchemaName,source.ObjectName
  FROM orion.PublicPermissionProfileEntry source
  WHERE source.ProfileCode='RESTAURANT_PUBLIC' AND source.ProfileVersion=9;

  INSERT orion.PublicPermissionProfileEntry
    (ProfileCode,ProfileVersion,PermissionState,PermissionName,SecurableClass,SchemaName,ObjectName)
  SELECT 'RESTAURANT_PUBLIC',10,'GRANT','SELECT','OBJECT','logistica','MaterialCategory'
  WHERE NOT EXISTS
  (
    SELECT 1 FROM orion.PublicPermissionProfileEntry
    WHERE ProfileCode='RESTAURANT_PUBLIC' AND ProfileVersion=10
      AND PermissionState='GRANT' AND PermissionName='SELECT'
      AND SecurableClass='OBJECT' AND SchemaName='logistica' AND ObjectName='MaterialCategory'
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
  WHERE profileInfo.ProfileCode='RESTAURANT_PUBLIC' AND profileInfo.ProfileVersion=10;

  IF EXISTS
  (
    SELECT 1
    FROM orion.PublicSqlPrincipalBinding binding
    JOIN orion.PublicSite publicSite ON publicSite.PublicSiteId=binding.PublicSiteId
    WHERE binding.IsActive=1 AND binding.PermissionProfile='RESTAURANT_PUBLIC'
      AND publicSite.ModuleCode='RESTAURANT'
      AND DATABASE_PRINCIPAL_ID(binding.PrincipalName) IS NULL
  )
    THROW 53810,'Un binding Restaurant activo no tiene principal de base.',1;

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
    THROW 53811,'No existen bindings Restaurant activos para aplicar el permiso.',1;
  IF EXISTS
  (
    SELECT 1
    FROM orion.PublicSqlPrincipalBinding binding
    JOIN orion.PublicSite publicSite ON publicSite.PublicSiteId=binding.PublicSiteId
    WHERE binding.IsActive=1 AND binding.PermissionProfile='RESTAURANT_PUBLIC'
      AND publicSite.ModuleCode='RESTAURANT'
      AND binding.PermissionVersion<>10
  )
    THROW 53812,'No todos los bindings Restaurant recibieron el perfil v10.',1;

  /* El permiso efectivo es lo unico que evita que el checkout vuelva a lanzar 500. */
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
          AND permissionInfo.class=1
          AND permissionInfo.major_id=OBJECT_ID(N'logistica.MaterialCategory')
          AND permissionInfo.permission_name='SELECT' AND permissionInfo.state IN('G','W')
      )
  )
    THROW 53813,'La lectura de logistica.MaterialCategory no quedo otorgada.',1;

  SELECT DB_NAME() DatabaseName,@ApplyChanges ApplyChanges,
    10 RestaurantPublicPermissionVersion,@AppliedBindings UpdatedPublicBindings,
    CONVERT(bit,1) MaterialCategoryReadable;

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
