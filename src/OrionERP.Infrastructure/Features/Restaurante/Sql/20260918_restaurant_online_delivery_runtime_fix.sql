/*
  Corrige el contexto del proceso interno de retencion de evidencias y retira
  ese procedimiento del principal publico de Bruno.

  RESTAURANT_PUBLIC v13 = v12 sin EXECUTE sobre OnlineDeliveryEvidencePurge.
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
  IF @ApplyInput NOT IN(N'0',N'1') THROW 54140,'ApplyChanges debe ser 0 o 1.',1;
  SET @ApplyChanges=CONVERT(bit,@ApplyInput);
END;
IF @ExpectedDatabase LIKE N'$'+N'(%'
   OR @ExpectedDatabase NOT IN(N'Orion_Sandbox',N'grupocarpio')
  THROW 54141,'Base esperada no autorizada para la correccion de entrega.',1;
IF DB_NAME()<>@ExpectedDatabase
  THROW 54142,'La conexion no apunta a la base declarada.',1;
IF @MigrationId<>N'20260918_restaurant_online_delivery_runtime_fix'
  THROW 54143,'MigrationId incorrecto.',1;
IF @MigrationChecksum LIKE '$'+N'(%' OR LEN(@MigrationChecksum)<>64
   OR @MigrationChecksum LIKE '%[^0-9A-F]%' COLLATE Latin1_General_100_BIN2
  THROW 54144,'MigrationChecksum invalido.',1;
IF @AppVersionInput NOT LIKE N'$'+N'(%'
  SET @AppVersion=NULLIF(LTRIM(RTRIM(@AppVersionInput)),N'');

IF OBJECT_ID(N'orion.SchemaMigration',N'U') IS NULL
   OR OBJECT_ID(N'orion.PublicSite',N'U') IS NULL
   OR OBJECT_ID(N'orion.PublicSqlPrincipalBinding',N'U') IS NULL
   OR OBJECT_ID(N'orion.PublicPermissionProfile',N'U') IS NULL
   OR OBJECT_ID(N'orion.PublicPermissionProfileEntry',N'U') IS NULL
   OR OBJECT_ID(N'orion.ApplyPublicPermissionProfile',N'P') IS NULL
   OR OBJECT_ID(N'restaurante.OnlineOrderingSettings',N'U') IS NULL
   OR OBJECT_ID(N'restaurante.OnlineCheckoutFacade',N'U') IS NULL
   OR OBJECT_ID(N'restaurante.DeliveryEvidence',N'U') IS NULL
   OR OBJECT_ID(N'restaurante.OnlineDeliveryEvidencePurge',N'P') IS NULL
  THROW 54145,'Faltan objetos requeridos para corregir la retencion de evidencias.',1;
IF NOT EXISTS
(
  SELECT 1 FROM orion.PublicPermissionProfile
  WHERE ProfileCode='RESTAURANT_PUBLIC' AND ProfileVersion=12 AND ProfileChecksum IS NOT NULL
)
  THROW 54146,'Falta el perfil publico Restaurant v12.',1;

DECLARE @ExistingChecksum char(64)=
  (SELECT Checksum FROM orion.SchemaMigration WHERE MigrationId=@MigrationId);
IF @ExistingChecksum IS NOT NULL AND UPPER(@ExistingChecksum)<>UPPER(@MigrationChecksum)
  THROW 54147,'El mismo MigrationId ya existe con otro checksum.',1;
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
    @Resource=N'OrionERP:Restaurant:OnlineDeliveryRuntimeFix',
    @LockMode=N'Exclusive',@LockOwner=N'Transaction',@LockTimeout=30000;
  IF @LockResult<0 THROW 54148,'No fue posible obtener el bloqueo de la migracion.',1;

  EXEC(N'CREATE OR ALTER PROCEDURE restaurante.OnlineDeliveryEvidencePurge AS
  BEGIN
    SET NOCOUNT ON;
    DECLARE @SessionPublicSiteId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N''OrionERP.PublicSiteId''));
    DECLARE @CompanyId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N''OrionERP.CompanyId''));
    DECLARE @SiteId int=TRY_CONVERT(int,SESSION_CONTEXT(N''OrionERP.SiteId''));
    DECLARE @Rfc varchar(50)=CONVERT(varchar(50),SESSION_CONTEXT(N''OrionRfc''));
    DECLARE @ResolvedPublicSiteId bigint,@SettingsCount bigint;
    IF @SessionPublicSiteId IS NOT NULL
      THROW 54123,''La purga de evidencias solo admite el worker interno.'',1;
    IF @CompanyId IS NULL OR @SiteId IS NULL OR NULLIF(@Rfc,'''') IS NULL
      THROW 54124,''No existe contexto interno para purga.'',1;
    SELECT @ResolvedPublicSiteId=MIN(PublicSiteId),@SettingsCount=COUNT_BIG(*)
      FROM restaurante.OnlineOrderingSettings
      WHERE Rfc=@Rfc AND SiteId=@SiteId;
    IF @SettingsCount<>1 OR @ResolvedPublicSiteId IS NULL
      THROW 54125,''La purga no resolvio exactamente un sitio publico.'',1;
    DELETE facade
      FROM restaurante.OnlineCheckoutFacade facade
      JOIN restaurante.OnlineCheckoutAttempt attempt
        ON attempt.PublicSiteId=facade.PublicSiteId AND attempt.Rfc=facade.Rfc
       AND attempt.SiteId=facade.SiteId AND attempt.Id=facade.CheckoutAttemptId
      WHERE facade.PublicSiteId=@ResolvedPublicSiteId AND facade.Rfc=@Rfc AND facade.SiteId=@SiteId
        AND facade.UpdatedAtUtc<DATEADD(HOUR,-24,SYSUTCDATETIME())
        AND attempt.[State] IN(''Quoted'',''PaymentDenied'',''RequoteRequired'',''Expired'',''Failed'');
    UPDATE evidence
      SET Content=NULL,Thumbnail=NULL,PurgedAtUtc=SYSUTCDATETIME(),PurgedBy=N''system:retention-90-days''
      FROM restaurante.DeliveryEvidence evidence
      JOIN restaurante.[Order] orderInfo
        ON orderInfo.PublicSiteId=evidence.PublicSiteId AND orderInfo.Rfc=evidence.Rfc
       AND orderInfo.SiteId=evidence.SiteId AND orderInfo.Id=evidence.OrderId
      WHERE evidence.PublicSiteId=@ResolvedPublicSiteId AND evidence.Rfc=@Rfc AND evidence.SiteId=@SiteId
        AND evidence.PurgedAtUtc IS NULL
        AND ((orderInfo.CompletedAt IS NOT NULL AND orderInfo.CompletedAt<DATEADD(DAY,-90,SYSUTCDATETIME()))
          OR (orderInfo.CancelledAt IS NOT NULL AND orderInfo.CancelledAt<DATEADD(DAY,-90,SYSUTCDATETIME())));
  END;');

  IF EXISTS
  (
    SELECT 1 FROM orion.PublicPermissionProfile
    WHERE ProfileCode='RESTAURANT_PUBLIC' AND ProfileVersion=13
  )
    THROW 54149,'Existe un perfil Restaurant v13 sin registro de esta migracion.',1;

  INSERT orion.PublicPermissionProfile(ProfileCode,ProfileVersion,ModuleCode)
  VALUES('RESTAURANT_PUBLIC',13,'RESTAURANT');
  INSERT orion.PublicPermissionProfileEntry
    (ProfileCode,ProfileVersion,PermissionState,PermissionName,SecurableClass,SchemaName,ObjectName)
  SELECT ProfileCode,13,PermissionState,PermissionName,SecurableClass,SchemaName,ObjectName
  FROM orion.PublicPermissionProfileEntry
  WHERE ProfileCode='RESTAURANT_PUBLIC' AND ProfileVersion=12
    AND NOT(PermissionState='GRANT' AND PermissionName='EXECUTE'
      AND SecurableClass='OBJECT' AND SchemaName='restaurante'
      AND ObjectName='OnlineDeliveryEvidencePurge');

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
  WHERE profileInfo.ProfileCode='RESTAURANT_PUBLIC' AND profileInfo.ProfileVersion=13;

  IF EXISTS
  (
    SELECT 1
    FROM orion.PublicSqlPrincipalBinding binding
    JOIN orion.PublicSite publicSite ON publicSite.PublicSiteId=binding.PublicSiteId
    WHERE binding.IsActive=1 AND binding.PermissionProfile='RESTAURANT_PUBLIC'
      AND publicSite.ModuleCode='RESTAURANT'
      AND DATABASE_PRINCIPAL_ID(binding.PrincipalName) IS NULL
  )
    THROW 54150,'Un binding Restaurant activo no tiene principal de base.',1;

  DECLARE @PublicSiteKey varchar(100),@PrincipalName sysname,@AppliedBindings int=0;
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
      @PublicSiteKey=@PublicSiteKey,@PrincipalName=@PrincipalName,@ApplyChanges=1;
    SET @AppliedBindings+=1;
    FETCH NEXT FROM public_principal_cursor INTO @PublicSiteKey,@PrincipalName;
  END;
  CLOSE public_principal_cursor;
  DEALLOCATE public_principal_cursor;
  IF @AppliedBindings=0 THROW 54151,'No existen bindings Restaurant activos.',1;
  IF EXISTS
  (
    SELECT 1
    FROM orion.PublicSqlPrincipalBinding binding
    JOIN orion.PublicSite publicSite ON publicSite.PublicSiteId=binding.PublicSiteId
    WHERE binding.IsActive=1 AND binding.PermissionProfile='RESTAURANT_PUBLIC'
      AND publicSite.ModuleCode='RESTAURANT'
      AND (binding.PermissionVersion<>13 OR EXISTS
      (
        SELECT 1 FROM sys.database_permissions permissionInfo
        WHERE permissionInfo.grantee_principal_id=DATABASE_PRINCIPAL_ID(binding.PrincipalName)
          AND permissionInfo.class=1
          AND permissionInfo.major_id=OBJECT_ID(N'restaurante.OnlineDeliveryEvidencePurge')
          AND permissionInfo.permission_name='EXECUTE' AND permissionInfo.state IN('G','W')
      ))
  )
    THROW 54152,'La purga sigue expuesta al principal publico.',1;

  SELECT N'REVISION' Estado,@MigrationId MigrationId,
    13 RestaurantPublicPermissionVersion,@AppliedBindings UpdatedPublicBindings,
    CONVERT(bit,1) InternalPurgeContext;

  IF @ApplyChanges=1
  BEGIN
    INSERT orion.SchemaMigration(MigrationId,Checksum,AppliedBy,AppVersion,DatabaseName)
    VALUES(@MigrationId,@MigrationChecksum,
      COALESCE(CONVERT(nvarchar(256),SESSION_CONTEXT(N'OrionERP.UserName')),
        CONVERT(nvarchar(256),ORIGINAL_LOGIN())),@AppVersion,DB_NAME());
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
  IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
  THROW;
END CATCH;
