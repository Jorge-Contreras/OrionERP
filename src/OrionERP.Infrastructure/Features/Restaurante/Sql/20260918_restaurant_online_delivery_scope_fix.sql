/*
  La sesion del worker usa el SiteId global de orion.Site, no el SiteId local de
  restaurante.Site. Resuelve el PublicSite con el alcance de plataforma y usa
  ese identificador para la retencion, sin reabrir el permiso al sitio publico.
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
  IF @ApplyInput NOT IN(N'0',N'1') THROW 54160,'ApplyChanges debe ser 0 o 1.',1;
  SET @ApplyChanges=CONVERT(bit,@ApplyInput);
END;
IF @ExpectedDatabase LIKE N'$'+N'(%'
   OR @ExpectedDatabase NOT IN(N'Orion_Sandbox',N'grupocarpio')
  THROW 54161,'Base esperada no autorizada para la correccion de alcance.',1;
IF DB_NAME()<>@ExpectedDatabase THROW 54162,'La conexion no apunta a la base declarada.',1;
IF @MigrationId<>N'20260918_restaurant_online_delivery_scope_fix'
  THROW 54163,'MigrationId incorrecto.',1;
IF @MigrationChecksum LIKE '$'+N'(%' OR LEN(@MigrationChecksum)<>64
   OR @MigrationChecksum LIKE '%[^0-9A-F]%' COLLATE Latin1_General_100_BIN2
  THROW 54164,'MigrationChecksum invalido.',1;
IF @AppVersionInput NOT LIKE N'$'+N'(%'
  SET @AppVersion=NULLIF(LTRIM(RTRIM(@AppVersionInput)),N'');

IF OBJECT_ID(N'orion.SchemaMigration',N'U') IS NULL
   OR OBJECT_ID(N'orion.PublicSite',N'U') IS NULL
   OR OBJECT_ID(N'orion.PublicSqlPrincipalBinding',N'U') IS NULL
   OR OBJECT_ID(N'restaurante.OnlineOrderingSettings',N'U') IS NULL
   OR OBJECT_ID(N'restaurante.OnlineCheckoutFacade',N'U') IS NULL
   OR OBJECT_ID(N'restaurante.DeliveryEvidence',N'U') IS NULL
   OR OBJECT_ID(N'restaurante.OnlineDeliveryEvidencePurge',N'P') IS NULL
  THROW 54165,'Faltan objetos requeridos para corregir el alcance de purga.',1;
IF NOT EXISTS
(
  SELECT 1 FROM orion.PublicPermissionProfile
  WHERE ProfileCode='RESTAURANT_PUBLIC' AND ProfileVersion=13 AND ProfileChecksum IS NOT NULL
)
  THROW 54166,'Falta el perfil publico Restaurant v13.',1;

DECLARE @ExistingChecksum char(64)=
  (SELECT Checksum FROM orion.SchemaMigration WHERE MigrationId=@MigrationId);
IF @ExistingChecksum IS NOT NULL AND UPPER(@ExistingChecksum)<>UPPER(@MigrationChecksum)
  THROW 54167,'El mismo MigrationId ya existe con otro checksum.',1;
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
    @Resource=N'OrionERP:Restaurant:OnlineDeliveryScopeFix',
    @LockMode=N'Exclusive',@LockOwner=N'Transaction',@LockTimeout=30000;
  IF @LockResult<0 THROW 54168,'No fue posible obtener el bloqueo de la migracion.',1;

  EXEC(N'CREATE OR ALTER PROCEDURE restaurante.OnlineDeliveryEvidencePurge AS
  BEGIN
    SET NOCOUNT ON;
    DECLARE @SessionPublicSiteId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N''OrionERP.PublicSiteId''));
    DECLARE @CompanyId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N''OrionERP.CompanyId''));
    DECLARE @PlatformSiteId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N''OrionERP.SiteId''));
    DECLARE @Rfc varchar(50)=CONVERT(varchar(50),SESSION_CONTEXT(N''OrionRfc''));
    DECLARE @ResolvedPublicSiteId bigint,@PublicSiteCount bigint,@SettingsCount bigint;
    IF @SessionPublicSiteId IS NOT NULL
      THROW 54123,''La purga de evidencias solo admite el worker interno.'',1;
    IF @CompanyId IS NULL OR @PlatformSiteId IS NULL OR NULLIF(@Rfc,'''') IS NULL
      THROW 54124,''No existe contexto interno para purga.'',1;
    SELECT @ResolvedPublicSiteId=MIN(PublicSiteId),@PublicSiteCount=COUNT_BIG(*)
      FROM orion.PublicSite
      WHERE CompanyId=@CompanyId AND SiteId=@PlatformSiteId
        AND ModuleCode=''RESTAURANT'' AND IsActive=1;
    IF @PublicSiteCount<>1 OR @ResolvedPublicSiteId IS NULL
      THROW 54125,''La purga no resolvio exactamente un sitio publico.'',1;
    SELECT @SettingsCount=COUNT_BIG(*)
      FROM restaurante.OnlineOrderingSettings
      WHERE PublicSiteId=@ResolvedPublicSiteId AND Rfc=@Rfc;
    IF @SettingsCount<>1
      THROW 54126,''El sitio publico no tiene una configuracion unica de pedidos.'',1;
    DELETE facade
      FROM restaurante.OnlineCheckoutFacade facade
      JOIN restaurante.OnlineCheckoutAttempt attempt
        ON attempt.PublicSiteId=facade.PublicSiteId AND attempt.Rfc=facade.Rfc
       AND attempt.SiteId=facade.SiteId AND attempt.Id=facade.CheckoutAttemptId
      WHERE facade.PublicSiteId=@ResolvedPublicSiteId AND facade.Rfc=@Rfc
        AND facade.UpdatedAtUtc<DATEADD(HOUR,-24,SYSUTCDATETIME())
        AND attempt.[State] IN(''Quoted'',''PaymentDenied'',''RequoteRequired'',''Expired'',''Failed'');
    UPDATE evidence
      SET Content=NULL,Thumbnail=NULL,PurgedAtUtc=SYSUTCDATETIME(),PurgedBy=N''system:retention-90-days''
      FROM restaurante.DeliveryEvidence evidence
      JOIN restaurante.[Order] orderInfo
        ON orderInfo.PublicSiteId=evidence.PublicSiteId AND orderInfo.Rfc=evidence.Rfc
       AND orderInfo.SiteId=evidence.SiteId AND orderInfo.Id=evidence.OrderId
      WHERE evidence.PublicSiteId=@ResolvedPublicSiteId AND evidence.Rfc=@Rfc
        AND evidence.PurgedAtUtc IS NULL
        AND ((orderInfo.CompletedAt IS NOT NULL AND orderInfo.CompletedAt<DATEADD(DAY,-90,SYSUTCDATETIME()))
          OR (orderInfo.CancelledAt IS NOT NULL AND orderInfo.CancelledAt<DATEADD(DAY,-90,SYSUTCDATETIME())));
  END;');

  IF EXISTS
  (
    SELECT 1
    FROM orion.PublicSqlPrincipalBinding binding
    JOIN orion.PublicSite publicSite ON publicSite.PublicSiteId=binding.PublicSiteId
    JOIN sys.database_permissions permissionInfo
      ON permissionInfo.grantee_principal_id=DATABASE_PRINCIPAL_ID(binding.PrincipalName)
     AND permissionInfo.class=1
     AND permissionInfo.major_id=OBJECT_ID(N'restaurante.OnlineDeliveryEvidencePurge')
     AND permissionInfo.permission_name='EXECUTE' AND permissionInfo.state IN('G','W')
    WHERE binding.IsActive=1 AND binding.PermissionProfile='RESTAURANT_PUBLIC'
      AND publicSite.ModuleCode='RESTAURANT'
  )
    THROW 54169,'La purga sigue expuesta al principal publico.',1;

  SELECT N'REVISION' Estado,@MigrationId MigrationId,
    CONVERT(bit,1) PlatformScopeResolution,CONVERT(bit,1) PublicExecuteRemoved;

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
