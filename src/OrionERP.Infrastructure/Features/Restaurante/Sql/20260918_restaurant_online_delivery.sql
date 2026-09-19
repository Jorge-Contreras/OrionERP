/* Bruno checkout delivery, private evidence and courier lifecycle. */
-- Navigation/training destination introduced by this release: N'/restaurante/entregas'.
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
  IF @ApplyInput NOT IN(N'0',N'1') THROW 54100,'ApplyChanges debe ser 0 o 1.',1;
  SET @ApplyChanges=CONVERT(bit,@ApplyInput);
END;
IF @ExpectedDatabase LIKE N'$'+N'(%' OR @ExpectedDatabase NOT IN(N'Orion_Sandbox',N'grupocarpio')
  THROW 54101,'Base esperada no autorizada para entregas.',1;
IF DB_NAME()<>@ExpectedDatabase THROW 54102,'La conexion no apunta a la base declarada.',1;
IF @MigrationId<>N'20260918_restaurant_online_delivery' THROW 54103,'MigrationId incorrecto.',1;
IF @MigrationChecksum LIKE '$'+N'(%' OR LEN(@MigrationChecksum)<>64
   OR @MigrationChecksum LIKE '%[^0-9A-F]%' COLLATE Latin1_General_100_BIN2
  THROW 54104,'MigrationChecksum invalido.',1;
IF @AppVersionInput NOT LIKE N'$'+N'(%' SET @AppVersion=NULLIF(LTRIM(RTRIM(@AppVersionInput)),N'');
IF OBJECT_ID(N'orion.SchemaMigration',N'U') IS NULL
   OR OBJECT_ID(N'restaurante.OnlineOrderingSettings',N'U') IS NULL
   OR OBJECT_ID(N'restaurante.Delivery',N'U') IS NULL
   OR OBJECT_ID(N'restaurante.OnlineCheckoutAttempt',N'U') IS NULL
  THROW 54105,'Falta la base del checkout en linea.',1;
IF NOT EXISTS(SELECT 1 FROM orion.SchemaMigration WHERE MigrationId=N'20260917_restaurant_clip_legacy_attempt_scope')
  THROW 54106,'Falta la migracion previa de Clip.',1;

DECLARE @ExistingChecksum char(64)=(SELECT Checksum FROM orion.SchemaMigration WHERE MigrationId=@MigrationId);
IF @ExistingChecksum IS NOT NULL AND UPPER(@ExistingChecksum)<>UPPER(@MigrationChecksum)
  THROW 54107,'El mismo MigrationId ya existe con otro checksum.',1;
IF @ExistingChecksum IS NOT NULL
BEGIN SELECT N'YA_APLICADO' Estado,@MigrationId MigrationId,@ExistingChecksum Checksum; RETURN; END;

IF @ApplyChanges=0
BEGIN
  SELECT N'REVISION' Estado,@MigrationId MigrationId,
    N'Agrega modalidades, zona y tarifa; fotos privadas; flujo de repartidor; emails y permisos publicos v12.' Resumen,
    (SELECT COUNT_BIG(*) FROM restaurante.OnlineCheckoutAttempt WHERE [State] NOT IN('PosCreated','Refunded','Expired','Failed')) IntentosAbiertos,
    (SELECT COUNT_BIG(*) FROM restaurante.[Order] WHERE OrderType='Delivery' AND [Status] NOT IN('Completed','Cancelled')) EntregasActivas;
  RETURN;
END;

BEGIN TRY
  BEGIN TRANSACTION;

  IF COL_LENGTH(N'restaurante.OnlineOrderingSettings',N'DeliveryEnabled') IS NULL
    ALTER TABLE restaurante.OnlineOrderingSettings ADD DeliveryEnabled bit NOT NULL
      CONSTRAINT DF_OnlineOrderingSettings_Delivery DEFAULT(0) WITH VALUES;
  IF COL_LENGTH(N'restaurante.OnlineOrderingSettings',N'DeliveryOriginLatitude') IS NULL
    ALTER TABLE restaurante.OnlineOrderingSettings ADD DeliveryOriginLatitude decimal(9,6) NULL;
  IF COL_LENGTH(N'restaurante.OnlineOrderingSettings',N'DeliveryOriginLongitude') IS NULL
    ALTER TABLE restaurante.OnlineOrderingSettings ADD DeliveryOriginLongitude decimal(10,6) NULL;
  IF COL_LENGTH(N'restaurante.OnlineOrderingSettings',N'DeliveryRadiusKm') IS NULL
    ALTER TABLE restaurante.OnlineOrderingSettings ADD DeliveryRadiusKm decimal(9,3) NULL;
  IF COL_LENGTH(N'restaurante.OnlineOrderingSettings',N'DeliveryFlatFee') IS NULL
    ALTER TABLE restaurante.OnlineOrderingSettings ADD DeliveryFlatFee decimal(18,2) NOT NULL
      CONSTRAINT DF_OnlineOrderingSettings_DeliveryFee DEFAULT(0) WITH VALUES;
  IF OBJECT_ID(N'restaurante.CK_OnlineOrderingSettings_Delivery',N'C') IS NULL
    EXEC(N'ALTER TABLE restaurante.OnlineOrderingSettings WITH CHECK ADD CONSTRAINT CK_OnlineOrderingSettings_Delivery CHECK
    (DeliveryFlatFee>=0 AND (DeliveryRadiusKm>0 OR DeliveryRadiusKm IS NULL));');
  DECLARE @DeliveryRfc varchar(50),@DeliveryCompanyId bigint,@DeliveryOrionSiteId bigint,@DeliveryPublicSiteId bigint;
  SELECT @DeliveryRfc=companyInfo.Rfc,@DeliveryCompanyId=publicSite.CompanyId,
    @DeliveryOrionSiteId=publicSite.SiteId,@DeliveryPublicSiteId=publicSite.PublicSiteId
  FROM orion.PublicSite publicSite JOIN orion.Company companyInfo ON companyInfo.CompanyId=publicSite.CompanyId
  WHERE publicSite.PublicSiteKey='brunos-main' AND publicSite.ModuleCode='RESTAURANT';
  IF @DeliveryRfc IS NULL THROW 54108,'No se encontro el binding publico del restaurante.',1;
  EXEC sys.sp_set_session_context @key=N'OrionRfc',@value=@DeliveryRfc,@read_only=0;
  EXEC sys.sp_set_session_context @key=N'OrionERP.CompanyId',@value=@DeliveryCompanyId,@read_only=0;
  EXEC sys.sp_set_session_context @key=N'OrionERP.SiteId',@value=@DeliveryOrionSiteId,@read_only=0;
  EXEC sys.sp_set_session_context @key=N'OrionERP.PublicSiteId',@value=@DeliveryPublicSiteId,@read_only=0;
  EXEC(N'UPDATE restaurante.OnlineOrderingSettings
    SET TermsVersion=''2026-09-18'',PrivacyVersion=''2026-09-18'',ConfigurationVersion=ConfigurationVersion+1,
      UpdatedAtUtc=SYSUTCDATETIME(),UpdatedBy=N''migration:online-delivery''
    WHERE TermsVersion<>''2026-09-18'' OR PrivacyVersion<>''2026-09-18'';');
  EXEC sys.sp_set_session_context @key=N'OrionERP.PublicSiteId',@value=NULL,@read_only=0;
  EXEC sys.sp_set_session_context @key=N'OrionERP.SiteId',@value=NULL,@read_only=0;
  EXEC sys.sp_set_session_context @key=N'OrionERP.CompanyId',@value=NULL,@read_only=0;
  EXEC sys.sp_set_session_context @key=N'OrionRfc',@value=NULL,@read_only=0;

  IF COL_LENGTH(N'restaurante.Delivery',N'AddressComplement') IS NULL ALTER TABLE restaurante.Delivery ADD AddressComplement nvarchar(180) NULL;
  IF COL_LENGTH(N'restaurante.Delivery',N'Latitude') IS NULL ALTER TABLE restaurante.Delivery ADD Latitude decimal(9,6) NULL;
  IF COL_LENGTH(N'restaurante.Delivery',N'Longitude') IS NULL ALTER TABLE restaurante.Delivery ADD Longitude decimal(10,6) NULL;
  IF COL_LENGTH(N'restaurante.Delivery',N'GooglePlaceId') IS NULL ALTER TABLE restaurante.Delivery ADD GooglePlaceId varchar(255) NULL;
  IF COL_LENGTH(N'restaurante.Delivery',N'AddressVerificationStatus') IS NULL
    ALTER TABLE restaurante.Delivery ADD AddressVerificationStatus varchar(30) NOT NULL CONSTRAINT DF_Delivery_Verification DEFAULT('ManualUnverified') WITH VALUES;
  IF COL_LENGTH(N'restaurante.Delivery',N'DropoffPreference') IS NULL
    ALTER TABLE restaurante.Delivery ADD DropoffPreference varchar(30) NOT NULL CONSTRAINT DF_Delivery_Dropoff DEFAULT('MeetAtDoor') WITH VALUES;
  IF COL_LENGTH(N'restaurante.Delivery',N'AssignedCourierUserName') IS NULL ALTER TABLE restaurante.Delivery ADD AssignedCourierUserName nvarchar(256) NULL;
  IF COL_LENGTH(N'restaurante.Delivery',N'AssignedAt') IS NULL ALTER TABLE restaurante.Delivery ADD AssignedAt datetime2(3) NULL;
  IF COL_LENGTH(N'restaurante.Delivery',N'AddressConfirmedAt') IS NULL ALTER TABLE restaurante.Delivery ADD AddressConfirmedAt datetime2(3) NULL;
  IF COL_LENGTH(N'restaurante.Delivery',N'AddressConfirmedBy') IS NULL ALTER TABLE restaurante.Delivery ADD AddressConfirmedBy nvarchar(256) NULL;
  IF COL_LENGTH(N'restaurante.Delivery',N'RowVersion') IS NULL ALTER TABLE restaurante.Delivery ADD RowVersion rowversion;
  IF OBJECT_ID(N'restaurante.CK_Delivery_Verification',N'C') IS NULL
    EXEC(N'ALTER TABLE restaurante.Delivery WITH CHECK ADD CONSTRAINT CK_Delivery_Verification CHECK(AddressVerificationStatus IN(''Validated'',''PinSelected'',''ManualUnverified'',''StaffConfirmed''));');
  IF OBJECT_ID(N'restaurante.CK_Delivery_Dropoff',N'C') IS NULL
    EXEC(N'ALTER TABLE restaurante.Delivery WITH CHECK ADD CONSTRAINT CK_Delivery_Dropoff CHECK(DropoffPreference IN(''LeaveAtDoor'',''MeetAtDoor'',''MeetOutside''));');

  IF OBJECT_ID(N'restaurante.OnlineCheckoutFacade',N'U') IS NULL
  BEGIN
    CREATE TABLE restaurante.OnlineCheckoutFacade
    (
      CheckoutAttemptId uniqueidentifier NOT NULL CONSTRAINT PK_OnlineCheckoutFacade PRIMARY KEY,
      PublicSiteId bigint NOT NULL,Rfc varchar(50) NOT NULL,SiteId int NOT NULL,
      FileName nvarchar(255) NULL,ContentType varchar(80) NOT NULL,ByteLength int NOT NULL,
      Width int NOT NULL,Height int NOT NULL,Content varbinary(max) NOT NULL,Thumbnail varbinary(max) NOT NULL,
      ContentHash binary(32) NOT NULL,CreatedAtUtc datetime2(3) NOT NULL CONSTRAINT DF_OnlineCheckoutFacade_Created DEFAULT(SYSUTCDATETIME()),
      UpdatedAtUtc datetime2(3) NOT NULL CONSTRAINT DF_OnlineCheckoutFacade_Updated DEFAULT(SYSUTCDATETIME()),
      CONSTRAINT FK_OnlineCheckoutFacade_Attempt FOREIGN KEY(PublicSiteId,Rfc,SiteId,CheckoutAttemptId)
        REFERENCES restaurante.OnlineCheckoutAttempt(PublicSiteId,Rfc,SiteId,Id),
      CONSTRAINT CK_OnlineCheckoutFacade_Size CHECK(ByteLength BETWEEN 1 AND 2097152 AND Width BETWEEN 1 AND 1600 AND Height BETWEEN 1 AND 1600)
    );
  END;

  IF OBJECT_ID(N'restaurante.DeliveryEvidence',N'U') IS NULL
  BEGIN
    CREATE TABLE restaurante.DeliveryEvidence
    (
      Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_DeliveryEvidence PRIMARY KEY,
      PublicSiteId bigint NOT NULL,Rfc varchar(50) NOT NULL,SiteId int NOT NULL,OrderId uniqueidentifier NOT NULL,
      EvidenceType varchar(30) NOT NULL,ContentType varchar(80) NOT NULL,ByteLength int NOT NULL,
      Width int NOT NULL,Height int NOT NULL,Content varbinary(max) NULL,Thumbnail varbinary(max) NULL,
      ContentHash binary(32) NOT NULL,Source varchar(30) NOT NULL,CreatedBy nvarchar(256) NULL,
      CreatedAtUtc datetime2(3) NOT NULL CONSTRAINT DF_DeliveryEvidence_Created DEFAULT(SYSUTCDATETIME()),
      PurgedAtUtc datetime2(3) NULL,PurgedBy nvarchar(256) NULL,
      CONSTRAINT FK_DeliveryEvidence_Order FOREIGN KEY(PublicSiteId,Rfc,SiteId,OrderId)
        REFERENCES restaurante.[Order](PublicSiteId,Rfc,SiteId,Id),
      CONSTRAINT CK_DeliveryEvidence_Type CHECK(EvidenceType IN('Facade','DropoffProof')),
      CONSTRAINT CK_DeliveryEvidence_Source CHECK(Source IN('CustomerCheckout','Courier','Supervisor')),
      CONSTRAINT CK_DeliveryEvidence_Size CHECK(ByteLength BETWEEN 1 AND 2097152 AND Width BETWEEN 1 AND 1600 AND Height BETWEEN 1 AND 1600),
      CONSTRAINT CK_DeliveryEvidence_Purge CHECK((PurgedAtUtc IS NULL AND Content IS NOT NULL AND Thumbnail IS NOT NULL) OR (PurgedAtUtc IS NOT NULL AND Content IS NULL AND Thumbnail IS NULL))
    );
    CREATE INDEX IX_DeliveryEvidence_Order ON restaurante.DeliveryEvidence(Rfc,OrderId,EvidenceType,CreatedAtUtc DESC);
  END;

  IF NOT EXISTS(SELECT 1 FROM orion.TenantTableClassification WHERE SchemaName=N'restaurante' AND TableName=N'OnlineCheckoutFacade')
    INSERT orion.TenantTableClassification(SchemaName,TableName,Classification,OwnerColumn,ReviewedInMigrationId,Notes)
      VALUES(N'restaurante',N'OnlineCheckoutFacade','TENANT_OWNED',N'Rfc',@MigrationId,N'Foto temporal de fachada, 24 horas si se abandona.');
  IF NOT EXISTS(SELECT 1 FROM orion.TenantTableClassification WHERE SchemaName=N'restaurante' AND TableName=N'DeliveryEvidence')
    INSERT orion.TenantTableClassification(SchemaName,TableName,Classification,OwnerColumn,ReviewedInMigrationId,Notes)
      VALUES(N'restaurante',N'DeliveryEvidence','TENANT_OWNED',N'Rfc',@MigrationId,N'Evidencia privada; bytes 90 dias.');

  EXEC(N'ALTER SECURITY POLICY restaurante.OnlineOrderingScopePolicy
    ADD FILTER PREDICATE restaurante.fn_OnlineOrderingScopePredicate(PublicSiteId,Rfc) ON restaurante.OnlineCheckoutFacade,
    ADD BLOCK PREDICATE restaurante.fn_OnlineOrderingScopePredicate(PublicSiteId,Rfc) ON restaurante.OnlineCheckoutFacade AFTER INSERT,
    ADD BLOCK PREDICATE restaurante.fn_OnlineOrderingScopePredicate(PublicSiteId,Rfc) ON restaurante.OnlineCheckoutFacade AFTER UPDATE,
    ADD FILTER PREDICATE restaurante.fn_OnlineOrderingScopePredicate(PublicSiteId,Rfc) ON restaurante.DeliveryEvidence,
    ADD BLOCK PREDICATE restaurante.fn_OnlineOrderingScopePredicate(PublicSiteId,Rfc) ON restaurante.DeliveryEvidence AFTER INSERT,
    ADD BLOCK PREDICATE restaurante.fn_OnlineOrderingScopePredicate(PublicSiteId,Rfc) ON restaurante.DeliveryEvidence AFTER UPDATE;');

  IF OBJECT_ID(N'restaurante.CK_OnlineOrderNotification_Type',N'C') IS NOT NULL
    ALTER TABLE restaurante.OnlineOrderNotification DROP CONSTRAINT CK_OnlineOrderNotification_Type;
  ALTER TABLE restaurante.OnlineOrderNotification WITH CHECK ADD CONSTRAINT CK_OnlineOrderNotification_Type
    CHECK(NotificationType IN('Confirmation','Ready','OutForDelivery','Delivered'));

  EXEC(N'CREATE OR ALTER VIEW restaurante.vw_PublicOnlineOrderingConfiguration AS
  SELECT settings.PublicSiteId,publicSite.PublicSiteKey,publicSite.CanonicalHost,settings.Rfc,settings.SiteId,
    settings.CompanyId,settings.OrionSiteId,restaurantSite.TimeZoneId,settings.IsEnabled,settings.IsPaused,
    settings.PauseMessage,settings.WeeklyScheduleJson,settings.MinimumOrderTotal,settings.MaximumOrderTotal,
    settings.GuestCheckoutEnabled,settings.PickupEnabled,settings.DeliveryEnabled,settings.DeliveryOriginLatitude,
    settings.DeliveryOriginLongitude,settings.DeliveryRadiusKm,settings.DeliveryFlatFee,settings.TermsVersion,
    settings.PrivacyVersion,settings.ActiveMerchantProfileKey,settings.GatewayEnvironment,
    settings.GatewayCredentialsConfigured,settings.GatewayWebhookConfigured,settings.GatewayReadinessAtUtc,
    settings.ProcessorHeartbeatAtUtc,settings.ConfigurationVersion,settings.UpdatedAtUtc,settings.RowVersion
  FROM restaurante.OnlineOrderingSettings settings
  JOIN orion.PublicSite publicSite ON publicSite.PublicSiteId=settings.PublicSiteId
  JOIN restaurante.Site restaurantSite ON restaurantSite.Rfc=settings.Rfc AND restaurantSite.Id=settings.SiteId
  WHERE publicSite.ModuleCode=''RESTAURANT'';');

  EXEC(N'CREATE OR ALTER PROCEDURE restaurante.OnlineOrderingBootstrapGet AS
  BEGIN SET NOCOUNT ON;
    DECLARE @PublicSiteId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N''OrionERP.PublicSiteId''));
    DECLARE @Rfc varchar(50)=CONVERT(varchar(50),SESSION_CONTEXT(N''OrionRfc''));
    IF @PublicSiteId IS NULL OR NULLIF(@Rfc,'''') IS NULL THROW 53620,''No existe un contexto publico de restaurante verificado.'',1;
    SELECT * FROM restaurante.vw_PublicOnlineOrderingConfiguration WHERE PublicSiteId=@PublicSiteId AND Rfc=@Rfc;
    SELECT ProductId FROM restaurante.OnlineOrderProduct WHERE PublicSiteId=@PublicSiteId AND Rfc=@Rfc AND IsEnabled=1 ORDER BY ProductId;
  END;');

  EXEC(N'CREATE OR ALTER PROCEDURE restaurante.OnlineOrderingAdminGet AS
  BEGIN SET NOCOUNT ON;
    DECLARE @PublicSiteId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N''OrionERP.PublicSiteId''));
    DECLARE @Rfc varchar(50)=CONVERT(varchar(50),SESSION_CONTEXT(N''OrionRfc''));
    DECLARE @CompanyId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N''OrionERP.CompanyId''));
    DECLARE @OrionSiteId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N''OrionERP.SiteId''));
    DECLARE @ResolvedPublicSiteId bigint;
    DECLARE @RequiredEnvironment varchar(20)=CASE WHEN DB_NAME()=''grupocarpio'' THEN ''Live'' ELSE ''Sandbox'' END;
    SELECT @ResolvedPublicSiteId=PublicSiteId FROM restaurante.OnlineOrderingSettings WHERE Rfc=@Rfc AND
      ((@PublicSiteId IS NOT NULL AND PublicSiteId=@PublicSiteId) OR (@PublicSiteId IS NULL AND CompanyId=@CompanyId AND (@OrionSiteId IS NULL OR OrionSiteId=@OrionSiteId)));
    IF @ResolvedPublicSiteId IS NULL THROW 53675,''No se resolvio el sitio para administrar pedidos online.'',1;
    SELECT settings.PublicSiteId,publicSite.PublicSiteKey,settings.Rfc,settings.SiteId,settings.IsEnabled,settings.IsPaused,
      settings.PauseMessage,settings.GuestCheckoutEnabled,settings.PickupEnabled,settings.DeliveryEnabled,
      settings.DeliveryOriginLatitude,settings.DeliveryOriginLongitude,settings.DeliveryRadiusKm,settings.DeliveryFlatFee,
      settings.MinimumOrderTotal,settings.MaximumOrderTotal,settings.WeeklyScheduleJson,settings.TermsVersion,settings.PrivacyVersion,
      settings.ActiveMerchantProfileKey,settings.GatewayEnvironment,settings.GatewayCredentialsConfigured,
      settings.GatewayWebhookConfigured,settings.GatewayReadinessAtUtc,settings.ProcessorHeartbeatAtUtc,
      @RequiredEnvironment RequiredGatewayEnvironment,settings.ConfigurationVersion,settings.UpdatedAtUtc,settings.UpdatedBy,settings.RowVersion
    FROM restaurante.OnlineOrderingSettings settings JOIN orion.PublicSite publicSite ON publicSite.PublicSiteId=settings.PublicSiteId
    WHERE settings.PublicSiteId=@ResolvedPublicSiteId AND settings.Rfc=@Rfc;
    SELECT product.Id ProductId,CASE WHEN NULLIF(LTRIM(RTRIM(product.VariantName)),'''') IS NULL THEN card.[Name] ELSE CONCAT(card.[Name],N'' · '',product.VariantName) END ProductName,
      product.Sku,product.Price,product.IsActive,product.SoldOutOverride IsSoldOut,productFlag.IsEnabled IsOnlineEnabled
    FROM restaurante.OnlineOrderProduct productFlag JOIN restaurante.Product product ON product.Rfc=productFlag.Rfc AND product.Id=productFlag.ProductId
    JOIN restaurante.ProductCard card ON card.Rfc=product.Rfc AND card.Id=product.ProductCardId
    WHERE productFlag.PublicSiteId=@ResolvedPublicSiteId AND productFlag.Rfc=@Rfc ORDER BY card.[Name],product.VariantName,product.Id;
  END;');

  EXEC(N'CREATE OR ALTER PROCEDURE restaurante.OnlineOrderingAdminSaveV3
    @IsEnabled bit,@IsPaused bit,@PauseMessage nvarchar(300)=NULL,@GuestCheckoutEnabled bit,@PickupEnabled bit,
    @DeliveryEnabled bit,@DeliveryOriginLatitude decimal(9,6)=NULL,@DeliveryOriginLongitude decimal(10,6)=NULL,
    @DeliveryRadiusKm decimal(9,3)=NULL,@DeliveryFlatFee decimal(18,2),@MaximumOrderTotal decimal(18,2),
    @WeeklyScheduleJson nvarchar(4000),@TermsVersion varchar(40),@PrivacyVersion varchar(40),
    @EnabledProductIdsJson nvarchar(max),@ExpectedConfigurationVersion bigint,@UpdatedBy nvarchar(256)=NULL
  AS
  BEGIN SET NOCOUNT ON; SET XACT_ABORT ON;
    DECLARE @PublicSiteId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N''OrionERP.PublicSiteId''));
    DECLARE @Rfc varchar(50)=CONVERT(varchar(50),SESSION_CONTEXT(N''OrionRfc''));
    DECLARE @CompanyId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N''OrionERP.CompanyId''));
    DECLARE @OrionSiteId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N''OrionERP.SiteId''));
    DECLARE @ResolvedPublicSiteId bigint,@SiteId int,@Now datetime2(3)=SYSUTCDATETIME();
    DECLARE @RequiredEnvironment varchar(20)=CASE WHEN DB_NAME()=''grupocarpio'' THEN ''Live'' ELSE ''Sandbox'' END;
    DECLARE @EnabledProducts TABLE(ProductId bigint NOT NULL PRIMARY KEY);
    IF @ExpectedConfigurationVersion<=0 THROW 53685,''La configuracion cambio; recargue antes de guardar.'',1;
    IF @PickupEnabled=0 AND @DeliveryEnabled=0 THROW 53686,''Debe habilitar recoger o entrega.'',1;
    IF @DeliveryFlatFee<0 OR (@DeliveryEnabled=1 AND (@DeliveryOriginLatitude NOT BETWEEN -90 AND 90 OR @DeliveryOriginLongitude NOT BETWEEN -180 AND 180 OR @DeliveryRadiusKm<=0))
      THROW 53686,''La configuracion de entrega no es valida.'',1;
    IF @MaximumOrderTotal<=0 THROW 53678,''El maximo por pedido debe ser mayor que cero.'',1;
    IF ISJSON(@WeeklyScheduleJson)<>1 OR LEFT(LTRIM(@WeeklyScheduleJson),1)<>''{'' THROW 53676,''El horario online no es valido.'',1;
    IF ISJSON(@EnabledProductIdsJson)<>1 OR LEFT(LTRIM(@EnabledProductIdsJson),1)<>''['' THROW 53677,''La seleccion de productos no es valida.'',1;
    IF NULLIF(LTRIM(RTRIM(@TermsVersion)),'''') IS NULL OR NULLIF(LTRIM(RTRIM(@PrivacyVersion)),'''') IS NULL THROW 53679,''Las versiones legales son obligatorias.'',1;
    IF @IsPaused=1 AND NULLIF(LTRIM(RTRIM(@PauseMessage)),N'''') IS NULL THROW 53680,''El mensaje de pausa es obligatorio.'',1;
    INSERT @EnabledProducts SELECT TRY_CONVERT(bigint,[value]) FROM OPENJSON(@EnabledProductIdsJson)
      WHERE TRY_CONVERT(bigint,[value])>0 GROUP BY TRY_CONVERT(bigint,[value]);
    IF (SELECT COUNT_BIG(*) FROM OPENJSON(@EnabledProductIdsJson))<>(SELECT COUNT_BIG(*) FROM @EnabledProducts)
      THROW 53682,''La seleccion contiene IDs invalidos o repetidos.'',1;
    SELECT @ResolvedPublicSiteId=PublicSiteId,@SiteId=SiteId FROM restaurante.OnlineOrderingSettings
      WHERE Rfc=@Rfc AND ((@PublicSiteId IS NOT NULL AND PublicSiteId=@PublicSiteId) OR (@PublicSiteId IS NULL AND CompanyId=@CompanyId AND (@OrionSiteId IS NULL OR OrionSiteId=@OrionSiteId)));
    IF @ResolvedPublicSiteId IS NULL THROW 53683,''No se resolvio el sitio.'',1;
    IF EXISTS(SELECT 1 FROM @EnabledProducts e LEFT JOIN restaurante.Product p ON p.Rfc=@Rfc AND p.Id=e.ProductId AND p.IsActive=1 WHERE p.Id IS NULL)
      THROW 53684,''Solo se pueden habilitar productos activos.'',1;
    BEGIN TRANSACTION;
    IF NOT EXISTS(SELECT 1 FROM restaurante.OnlineOrderingSettings WITH(UPDLOCK,HOLDLOCK) WHERE PublicSiteId=@ResolvedPublicSiteId AND Rfc=@Rfc AND ConfigurationVersion=@ExpectedConfigurationVersion)
    BEGIN ROLLBACK; THROW 53685,''La configuracion cambio; recargue antes de guardar.'',1; END;
    INSERT restaurante.OnlineOrderProduct(PublicSiteId,Rfc,SiteId,ProductId,IsEnabled,UpdatedBy)
      SELECT @ResolvedPublicSiteId,@Rfc,@SiteId,p.Id,0,COALESCE(NULLIF(LTRIM(RTRIM(@UpdatedBy)),N''''),ORIGINAL_LOGIN())
      FROM restaurante.Product p WHERE p.Rfc=@Rfc AND NOT EXISTS(SELECT 1 FROM restaurante.OnlineOrderProduct x WHERE x.PublicSiteId=@ResolvedPublicSiteId AND x.ProductId=p.Id);
    UPDATE f SET IsEnabled=CONVERT(bit,CASE WHEN e.ProductId IS NULL THEN 0 ELSE 1 END),UpdatedAtUtc=@Now,
      UpdatedBy=COALESCE(NULLIF(LTRIM(RTRIM(@UpdatedBy)),N''''),ORIGINAL_LOGIN())
      FROM restaurante.OnlineOrderProduct f LEFT JOIN @EnabledProducts e ON e.ProductId=f.ProductId
      WHERE f.PublicSiteId=@ResolvedPublicSiteId AND f.Rfc=@Rfc;
    IF @IsEnabled=1 AND (NOT EXISTS(SELECT 1 FROM @EnabledProducts) OR NOT EXISTS(SELECT 1 FROM OPENJSON(@WeeklyScheduleJson) d CROSS APPLY OPENJSON(d.[value]) s)
      OR EXISTS(SELECT 1 FROM restaurante.OnlineOrderingSettings s WHERE s.PublicSiteId=@ResolvedPublicSiteId AND
        (s.GatewayEnvironment<>@RequiredEnvironment OR s.GatewayCredentialsConfigured=0 OR s.GatewayWebhookConfigured=0
         OR s.GatewayReadinessAtUtc<DATEADD(MINUTE,-5,@Now) OR s.ProcessorHeartbeatAtUtc<DATEADD(SECOND,-120,@Now))))
    BEGIN ROLLBACK; THROW 53686,''No se puede habilitar: falta horario, productos, Clip o procesador.'',1; END;
    UPDATE restaurante.OnlineOrderingSettings SET IsEnabled=@IsEnabled,IsPaused=@IsPaused,
      PauseMessage=CASE WHEN @IsPaused=1 THEN LTRIM(RTRIM(@PauseMessage)) ELSE NULL END,
      GuestCheckoutEnabled=@GuestCheckoutEnabled,PickupEnabled=@PickupEnabled,DeliveryEnabled=@DeliveryEnabled,
      DeliveryOriginLatitude=@DeliveryOriginLatitude,DeliveryOriginLongitude=@DeliveryOriginLongitude,
      DeliveryRadiusKm=@DeliveryRadiusKm,DeliveryFlatFee=@DeliveryFlatFee,MaximumOrderTotal=@MaximumOrderTotal,
      WeeklyScheduleJson=@WeeklyScheduleJson,TermsVersion=LTRIM(RTRIM(@TermsVersion)),PrivacyVersion=LTRIM(RTRIM(@PrivacyVersion)),
      ConfigurationVersion=ConfigurationVersion+1,UpdatedAtUtc=@Now,UpdatedBy=COALESCE(NULLIF(LTRIM(RTRIM(@UpdatedBy)),N''''),ORIGINAL_LOGIN())
      WHERE PublicSiteId=@ResolvedPublicSiteId AND Rfc=@Rfc;
    COMMIT;
    SELECT PublicSiteId,IsEnabled FROM restaurante.OnlineOrderingSettings WHERE PublicSiteId=@ResolvedPublicSiteId AND Rfc=@Rfc;
  END;');

  EXEC(N'CREATE OR ALTER PROCEDURE restaurante.OnlineCheckoutAttemptCreateV2
    @Id uniqueidentifier,@ClientAttemptId uniqueidentifier,@MemberId uniqueidentifier=NULL,@CustomerName nvarchar(150),
    @CustomerEmail nvarchar(320),@CustomerPhone varchar(30),@QuoteFingerprint char(64),@QuoteExpiresAtUtc datetime2(3),
    @SettingsConfigurationVersion bigint,@CartSnapshotJson nvarchar(max),@PromotionCode varchar(80)=NULL,
    @Subtotal decimal(18,2),@PromotionDiscountTotal decimal(18,2),@TaxTotal decimal(18,2),@Total decimal(18,2),
    @CurrencyCode char(3),@PrivacyVersion varchar(40),@TermsVersion varchar(40),@LegalAcceptedAtUtc datetime2(3),
    @MerchantProfileKey varchar(80),@TrackingTokenHash binary(32),@Provider varchar(20)=''Clip''
  AS
  BEGIN SET NOCOUNT ON; SET XACT_ABORT ON;
    DECLARE @PublicSiteId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N''OrionERP.PublicSiteId''));
    DECLARE @Rfc varchar(50)=CONVERT(varchar(50),SESSION_CONTEXT(N''OrionRfc''));
    DECLARE @SiteId int,@ExistingId uniqueidentifier,@WasCreated bit=0;
    DECLARE @FulfillmentType varchar(20)=JSON_VALUE(@CartSnapshotJson,''$.request.fulfillment.type'');
    DECLARE @Verification varchar(30)=JSON_VALUE(@CartSnapshotJson,''$.request.fulfillment.addressVerificationStatus'');
    DECLARE @ManualAcknowledged bit=TRY_CONVERT(bit,JSON_VALUE(@CartSnapshotJson,''$.request.fulfillment.manualAddressAcknowledged''));
    DECLARE @DeliveryFee decimal(18,2)=TRY_CONVERT(decimal(18,2),JSON_VALUE(@CartSnapshotJson,''$.deliveryFee''));
    IF @PublicSiteId IS NULL OR NULLIF(@Rfc,'''') IS NULL THROW 53622,''No existe contexto publico.'',1;
    IF @Id IS NULL OR @ClientAttemptId IS NULL OR @SettingsConfigurationVersion<=0 THROW 53623,''Identificadores invalidos.'',1;
    IF @QuoteExpiresAtUtc<=SYSUTCDATETIME() THROW 53624,''La cotizacion vencio.'',1;
    IF ISJSON(@CartSnapshotJson)<>1 OR @FulfillmentType NOT IN(''Pickup'',''Delivery'') THROW 53626,''La cotizacion no es valida.'',1;
    IF @Provider<>''Clip'' THROW 54020,''Los intentos nuevos solo se cobran con Clip.'',1;
    IF @FulfillmentType=''Delivery'' AND (NULLIF(LTRIM(RTRIM(JSON_VALUE(@CartSnapshotJson,''$.request.fulfillment.addressLine''))),'''') IS NULL
      OR JSON_VALUE(@CartSnapshotJson,''$.request.fulfillment.dropoffPreference'') NOT IN(''LeaveAtDoor'',''MeetAtDoor'',''MeetOutside'')
      OR (@Verification=''ManualUnverified'' AND ISNULL(@ManualAcknowledged,0)=0)) THROW 54120,''Faltan datos obligatorios de entrega.'',1;
    BEGIN TRANSACTION;
    SELECT @SiteId=settings.SiteId FROM restaurante.OnlineOrderingSettings settings WITH(UPDLOCK,HOLDLOCK)
      WHERE settings.PublicSiteId=@PublicSiteId AND settings.Rfc=@Rfc AND settings.ConfigurationVersion=@SettingsConfigurationVersion
        AND settings.IsEnabled=1 AND settings.IsPaused=0 AND (@MemberId IS NOT NULL OR settings.GuestCheckoutEnabled=1)
        AND settings.TermsVersion=@TermsVersion AND settings.PrivacyVersion=@PrivacyVersion
        AND settings.ActiveMerchantProfileKey=@MerchantProfileKey AND @Total BETWEEN settings.MinimumOrderTotal AND settings.MaximumOrderTotal
        AND ((@FulfillmentType=''Pickup'' AND settings.PickupEnabled=1 AND ISNULL(@DeliveryFee,0)=0)
          OR (@FulfillmentType=''Delivery'' AND settings.DeliveryEnabled=1 AND settings.DeliveryOriginLatitude IS NOT NULL
            AND settings.DeliveryOriginLongitude IS NOT NULL AND settings.DeliveryRadiusKm>0 AND @DeliveryFee=settings.DeliveryFlatFee));
    IF @SiteId IS NULL BEGIN ROLLBACK; THROW 53628,''La configuracion ya no acepta este checkout.'',1; END;
    SELECT @ExistingId=Id FROM restaurante.OnlineCheckoutAttempt WITH(UPDLOCK,HOLDLOCK)
      WHERE PublicSiteId=@PublicSiteId AND Rfc=@Rfc AND ClientAttemptId=@ClientAttemptId;
    IF @ExistingId IS NOT NULL
    BEGIN
      IF NOT EXISTS(SELECT 1 FROM restaurante.OnlineCheckoutAttempt WHERE Id=@ExistingId AND QuoteFingerprint=@QuoteFingerprint
        AND SettingsConfigurationVersion=@SettingsConfigurationVersion AND MerchantProfileKey=@MerchantProfileKey AND CurrencyCode=@CurrencyCode
        AND Total=@Total AND Provider=@Provider AND ((MemberId=@MemberId) OR (MemberId IS NULL AND @MemberId IS NULL)))
      BEGIN ROLLBACK; THROW 53630,''ClientAttemptId ya se uso con otro checkout.'',1; END;
      SET @Id=@ExistingId;
    END
    ELSE
    BEGIN
      INSERT restaurante.OnlineCheckoutAttempt(Id,PublicSiteId,Rfc,SiteId,ClientAttemptId,MemberId,CustomerName,CustomerEmail,CustomerPhone,
        QuoteFingerprint,QuoteExpiresAtUtc,SettingsConfigurationVersion,CartSnapshotJson,PromotionCode,Subtotal,PromotionDiscountTotal,TaxTotal,Total,
        CurrencyCode,PrivacyVersion,TermsVersion,LegalAcceptedAtUtc,MerchantProfileKey,TrackingTokenHash,Provider)
      VALUES(@Id,@PublicSiteId,@Rfc,@SiteId,@ClientAttemptId,@MemberId,LTRIM(RTRIM(@CustomerName)),LOWER(LTRIM(RTRIM(@CustomerEmail))),LTRIM(RTRIM(@CustomerPhone)),
        @QuoteFingerprint,@QuoteExpiresAtUtc,@SettingsConfigurationVersion,@CartSnapshotJson,NULLIF(LTRIM(RTRIM(@PromotionCode)),''''),@Subtotal,
        @PromotionDiscountTotal,@TaxTotal,@Total,@CurrencyCode,@PrivacyVersion,@TermsVersion,@LegalAcceptedAtUtc,@MerchantProfileKey,@TrackingTokenHash,@Provider);
      SET @WasCreated=1;
    END;
    COMMIT;
    SELECT @WasCreated WasCreated,attempt.* FROM restaurante.OnlineCheckoutAttempt attempt WHERE attempt.Id=@Id AND attempt.PublicSiteId=@PublicSiteId AND attempt.Rfc=@Rfc;
  END;');

  EXEC(N'CREATE OR ALTER PROCEDURE restaurante.OnlineCheckoutChargeBeginV2
    @Id uniqueidentifier,@ChargeRequestId uniqueidentifier,@ProcessorHeartbeatMaxAgeSeconds int
  AS
  BEGIN SET NOCOUNT ON; SET XACT_ABORT ON;
    DECLARE @PublicSiteId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N''OrionERP.PublicSiteId''));
    DECLARE @Rfc varchar(50)=CONVERT(varchar(50),SESSION_CONTEXT(N''OrionRfc''));
    DECLARE @Now datetime2(3)=SYSUTCDATETIME(),@RequiredEnvironment varchar(20)=CASE WHEN DB_NAME()=''grupocarpio'' THEN ''Live'' ELSE ''Sandbox'' END;
    DECLARE @Found bit=0,@State varchar(30),@Provider varchar(20),@FulfillmentType varchar(20),@QuoteExpires datetime2(3),
      @AttemptVersion bigint,@CurrentVersion bigint,@Total decimal(18,2),@Minimum decimal(18,2),@Maximum decimal(18,2),
      @AttemptTerms varchar(40),@Terms varchar(40),@AttemptPrivacy varchar(40),@Privacy varchar(40),
      @AttemptMerchant varchar(80),@Merchant varchar(80),@Enabled bit,@Paused bit,@Pickup bit,@Delivery bit,
      @GatewayEnvironment varchar(20),@Credentials bit,@Webhook bit,@GatewayReady datetime2(3),@Heartbeat datetime2(3);
    IF @PublicSiteId IS NULL OR NULLIF(@Rfc,'''') IS NULL THROW 53646,''No existe contexto publico.'',1;
    IF @ChargeRequestId IS NULL OR @ProcessorHeartbeatMaxAgeSeconds NOT BETWEEN 15 AND 300 THROW 54029,''El sello del cargo no es valido.'',1;
    BEGIN TRANSACTION;
    SELECT @Found=1,@State=a.[State],@Provider=a.Provider,@FulfillmentType=JSON_VALUE(a.CartSnapshotJson,''$.request.fulfillment.type''),
      @QuoteExpires=a.QuoteExpiresAtUtc,@AttemptVersion=a.SettingsConfigurationVersion,@CurrentVersion=s.ConfigurationVersion,
      @Total=a.Total,@Minimum=s.MinimumOrderTotal,@Maximum=s.MaximumOrderTotal,@AttemptTerms=a.TermsVersion,@Terms=s.TermsVersion,
      @AttemptPrivacy=a.PrivacyVersion,@Privacy=s.PrivacyVersion,@AttemptMerchant=a.MerchantProfileKey,@Merchant=s.ActiveMerchantProfileKey,
      @Enabled=s.IsEnabled,@Paused=s.IsPaused,@Pickup=s.PickupEnabled,@Delivery=s.DeliveryEnabled,@GatewayEnvironment=s.GatewayEnvironment,
      @Credentials=s.GatewayCredentialsConfigured,@Webhook=s.GatewayWebhookConfigured,@GatewayReady=s.GatewayReadinessAtUtc,@Heartbeat=s.ProcessorHeartbeatAtUtc
    FROM restaurante.OnlineCheckoutAttempt a WITH(UPDLOCK,HOLDLOCK) JOIN restaurante.OnlineOrderingSettings s WITH(UPDLOCK,HOLDLOCK)
      ON s.PublicSiteId=a.PublicSiteId AND s.Rfc=a.Rfc AND s.SiteId=a.SiteId
    WHERE a.Id=@Id AND a.PublicSiteId=@PublicSiteId AND a.Rfc=@Rfc;
    IF @Found=0 OR @Provider<>''Clip'' OR @State NOT IN(''Quoted'',''PaymentDenied'')
    BEGIN ROLLBACK; THROW 54030,''El intento no admite un cargo nuevo.'',1; END;
    IF @QuoteExpires<=@Now OR @AttemptVersion<>@CurrentVersion OR @Enabled=0 OR @Paused=1
      OR (@FulfillmentType=''Pickup'' AND @Pickup=0) OR (@FulfillmentType=''Delivery'' AND @Delivery=0)
      OR @FulfillmentType NOT IN(''Pickup'',''Delivery'') OR @Total NOT BETWEEN @Minimum AND @Maximum
      OR @AttemptTerms<>@Terms OR @AttemptPrivacy<>@Privacy OR @AttemptMerchant<>@Merchant
      OR @GatewayEnvironment<>@RequiredEnvironment OR @Credentials=0 OR @Webhook=0
      OR @GatewayReady<DATEADD(MINUTE,-5,@Now) OR @Heartbeat<DATEADD(SECOND,-@ProcessorHeartbeatMaxAgeSeconds,@Now)
      OR NOT EXISTS(SELECT 1 FROM restaurante.OnlineOrderProduct WHERE PublicSiteId=@PublicSiteId AND Rfc=@Rfc AND IsEnabled=1)
    BEGIN ROLLBACK; THROW 54031,''El cargo esta bloqueado por vigencia o configuracion.'',1; END;
    UPDATE restaurante.OnlineCheckoutAttempt SET [State]=''ChargePending'',ChargeRequestId=@ChargeRequestId,ChargeStartedAtUtc=@Now,
      ProviderRequestId=NULL,ProviderOrderId=NULL,ProviderCaptureId=NULL,NextRetryAtUtc=DATEADD(SECOND,120,@Now),
      FailureCode=NULL,FailureMessage=NULL,UpdatedAtUtc=@Now
      WHERE Id=@Id AND PublicSiteId=@PublicSiteId AND Rfc=@Rfc AND [State] IN(''Quoted'',''PaymentDenied'');
    IF @@ROWCOUNT<>1 BEGIN ROLLBACK; THROW 54030,''Otro cargo tomo este intento primero.'',1; END;
    COMMIT;
    SELECT Id,Provider,MerchantProfileKey,ChargeRequestId,ChargeStartedAtUtc,QuoteFingerprint,Total,CurrencyCode,[State],
      CustomerName,CustomerEmail,CustomerPhone,SettingsConfigurationVersion,QuoteExpiresAtUtc,TermsVersion,PrivacyVersion,UpdatedAtUtc,RowVersion
      FROM restaurante.OnlineCheckoutAttempt WHERE Id=@Id AND PublicSiteId=@PublicSiteId AND Rfc=@Rfc;
  END;');

  EXEC(N'CREATE OR ALTER PROCEDURE restaurante.OnlineCheckoutFacadeUpsert
    @CheckoutAttemptId uniqueidentifier,@FileName nvarchar(255)=NULL,@ContentType varchar(80),@Content varbinary(max),
    @Thumbnail varbinary(max),@ByteLength int,@Width int,@Height int,@ContentHash binary(32)
  AS
  BEGIN SET NOCOUNT ON; SET XACT_ABORT ON;
    DECLARE @PublicSiteId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N''OrionERP.PublicSiteId''));
    DECLARE @Rfc varchar(50)=CONVERT(varchar(50),SESSION_CONTEXT(N''OrionRfc'')); DECLARE @SiteId int;
    SELECT @SiteId=SiteId FROM restaurante.OnlineCheckoutAttempt WITH(UPDLOCK,HOLDLOCK)
      WHERE Id=@CheckoutAttemptId AND PublicSiteId=@PublicSiteId AND Rfc=@Rfc AND [State] IN(''Quoted'',''PaymentDenied'')
        AND JSON_VALUE(CartSnapshotJson,''$.request.fulfillment.type'')=''Delivery'';
    IF @SiteId IS NULL THROW 54121,''El intento no admite foto de fachada.'',1;
    IF @ContentType<>''image/jpeg'' OR @ByteLength<>DATALENGTH(@Content) OR @ByteLength NOT BETWEEN 1 AND 2097152
      OR @Width NOT BETWEEN 1 AND 1600 OR @Height NOT BETWEEN 1 AND 1600 OR DATALENGTH(@Thumbnail)=0
      THROW 54122,''La imagen normalizada no es valida.'',1;
    UPDATE restaurante.OnlineCheckoutFacade SET FileName=LEFT(@FileName,255),ContentType=@ContentType,ByteLength=@ByteLength,
      Width=@Width,Height=@Height,Content=@Content,Thumbnail=@Thumbnail,ContentHash=@ContentHash,UpdatedAtUtc=SYSUTCDATETIME()
      WHERE CheckoutAttemptId=@CheckoutAttemptId AND PublicSiteId=@PublicSiteId AND Rfc=@Rfc;
    IF @@ROWCOUNT=0 INSERT restaurante.OnlineCheckoutFacade(CheckoutAttemptId,PublicSiteId,Rfc,SiteId,FileName,ContentType,ByteLength,Width,Height,Content,Thumbnail,ContentHash)
      VALUES(@CheckoutAttemptId,@PublicSiteId,@Rfc,@SiteId,LEFT(@FileName,255),@ContentType,@ByteLength,@Width,@Height,@Content,@Thumbnail,@ContentHash);
  END;');

  EXEC(N'CREATE OR ALTER PROCEDURE restaurante.OnlineCheckoutStatusGet @TrackingTokenHash binary(32) AS
  BEGIN SET NOCOUNT ON;
    DECLARE @PublicSiteId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N''OrionERP.PublicSiteId''));
    DECLARE @Rfc varchar(50)=CONVERT(varchar(50),SESSION_CONTEXT(N''OrionRfc''));
    IF @PublicSiteId IS NULL OR NULLIF(@Rfc,'''') IS NULL OR @TrackingTokenHash IS NULL THROW 53653,''El contexto o token es invalido.'',1;
    SELECT a.[State] CheckoutStatus,
      CASE WHEN a.[State]=''Refunded'' OR t.[Status]=''Refunded'' THEN ''Refunded'' WHEN t.[Status]=''PartiallyRefunded'' THEN ''PartiallyRefunded''
        WHEN a.[State] IN(''Captured'',''CapturedNeedsOrder'',''PosCreated'',''RefundRequested'',''RefundPending'') THEN ''Paid''
        WHEN a.[State] IN(''ChargePending'',''Authenticating3ds'',''ChargeUnknown'') THEN ''Pending'' WHEN a.[State]=''PaymentDenied'' THEN ''Denied'' ELSE ''NotPaid'' END PaymentStatus,
      o.[Status] OrderStatus,o.Folio OrderFolio,o.OrderType FulfillmentType,a.Total,a.CurrencyCode Currency,
      CONVERT(bit,CASE WHEN a.[State] IN(''Refunded'',''PaymentDenied'',''Expired'',''Failed'') OR o.[Status] IN(''Completed'',''Cancelled'') THEN 1 ELSE 0 END) IsTerminal,
      CONVERT(nvarchar(240),CASE
        WHEN a.[State]=''PosCreated'' AND o.OrderType=''Delivery'' AND o.[Status]=''Ready'' THEN N''Tu pedido esta listo y espera repartidor.''
        WHEN a.[State]=''PosCreated'' AND o.OrderType=''Delivery'' AND o.[Status]=''Dispatched'' THEN N''Tu pedido va en camino.''
        WHEN a.[State]=''PosCreated'' AND o.OrderType=''Delivery'' AND o.[Status] IN(''Delivered'',''Completed'') THEN N''Tu pedido fue entregado.''
        WHEN a.[State]=''PosCreated'' AND o.[Status]=''Ready'' THEN N''Tu pedido esta listo para recoger.''
        WHEN a.[State]=''PosCreated'' THEN N''Tu pedido esta confirmado.''
        WHEN a.[State] IN(''Captured'',''CapturedNeedsOrder'') THEN N''Pago recibido. Estamos confirmando tu pedido.''
        WHEN a.[State]=''Refunded'' THEN N''Tu pago fue reembolsado.'' WHEN a.[State]=''PaymentDenied'' THEN N''El banco no autorizo el cobro.''
        WHEN a.[State] IN(''ChargePending'',''Authenticating3ds'',''ChargeUnknown'') THEN N''Estamos confirmando el pago. No vuelvas a pagar.'' ELSE N''El pedido esta pendiente.'' END) [Message],
      a.UpdatedAtUtc,readyNotice.[Status] ReadyNotificationStatus
    FROM restaurante.OnlineCheckoutAttempt a LEFT JOIN restaurante.PaymentGatewayTransaction t
      ON t.PublicSiteId=a.PublicSiteId AND t.Rfc=a.Rfc AND t.SiteId=a.SiteId AND t.CheckoutAttemptId=a.Id
    LEFT JOIN restaurante.[Order] o ON o.PublicSiteId=a.PublicSiteId AND o.Rfc=a.Rfc AND o.SiteId=a.SiteId AND o.Id=a.RestaurantOrderId
    OUTER APPLY(SELECT TOP(1) n.[Status] FROM restaurante.OnlineOrderNotification n WHERE n.PublicSiteId=a.PublicSiteId AND n.Rfc=a.Rfc AND n.CheckoutAttemptId=a.Id AND n.NotificationType=''Ready'' ORDER BY n.Id DESC) readyNotice
    WHERE a.PublicSiteId=@PublicSiteId AND a.Rfc=@Rfc AND a.TrackingTokenHash=@TrackingTokenHash;
  END;');

  EXEC(N'CREATE OR ALTER PROCEDURE restaurante.OnlineOrderImportCompleteV2
    @Id uniqueidentifier,@LeaseId uniqueidentifier,@RestaurantOrderId uniqueidentifier,@LocalPaymentId uniqueidentifier
  AS
  BEGIN SET NOCOUNT ON; SET XACT_ABORT ON;
    DECLARE @PublicSiteId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N''OrionERP.PublicSiteId''));
    DECLARE @Rfc varchar(50)=CONVERT(varchar(50),SESSION_CONTEXT(N''OrionRfc''));
    DECLARE @CompanyId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N''OrionERP.CompanyId''));
    DECLARE @OrionSiteId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N''OrionERP.SiteId''));
    DECLARE @ResolvedPublicSiteId bigint,@SiteId int,@CustomerEmail nvarchar(320),@FulfillmentType varchar(20);
    SELECT @ResolvedPublicSiteId=s.PublicSiteId,@SiteId=s.SiteId FROM restaurante.[Order] o
      JOIN restaurante.OnlineOrderingSettings s ON s.PublicSiteId=o.PublicSiteId AND s.Rfc=o.Rfc AND s.SiteId=o.SiteId
      WHERE o.Rfc=@Rfc AND o.Id=@RestaurantOrderId AND ((@PublicSiteId IS NOT NULL AND s.PublicSiteId=@PublicSiteId)
        OR (@PublicSiteId IS NULL AND s.CompanyId=@CompanyId AND (@OrionSiteId IS NULL OR s.OrionSiteId=@OrionSiteId)));
    IF @ResolvedPublicSiteId IS NULL THROW 53662,''No se resolvio el sitio del importador.'',1;
    BEGIN TRANSACTION;
    SELECT @CustomerEmail=CustomerEmail,@FulfillmentType=JSON_VALUE(CartSnapshotJson,''$.request.fulfillment.type'')
      FROM restaurante.OnlineCheckoutAttempt WITH(UPDLOCK,HOLDLOCK)
      WHERE Id=@Id AND PublicSiteId=@ResolvedPublicSiteId AND Rfc=@Rfc AND
        (([State]=''CapturedNeedsOrder'' AND ImportLeaseId=@LeaseId AND ImportLeaseExpiresAtUtc>=SYSUTCDATETIME()) OR ([State]=''PosCreated'' AND RestaurantOrderId=@RestaurantOrderId));
    IF @CustomerEmail IS NULL BEGIN ROLLBACK; THROW 53663,''El checkout no tiene una concesion valida.'',1; END;
    IF NOT EXISTS(SELECT 1 FROM restaurante.[Order] WHERE Id=@RestaurantOrderId AND PublicSiteId=@ResolvedPublicSiteId AND Rfc=@Rfc
      AND SiteId=@SiteId AND OnlineCheckoutAttemptId=@Id AND SalesChannel=''Web'' AND OrderType=@FulfillmentType)
    BEGIN ROLLBACK; THROW 53664,''La orden POS no coincide con el checkout Web.'',1; END;
    IF NOT EXISTS(SELECT 1 FROM restaurante.Payment p JOIN restaurante.PaymentGatewayTransaction t
      ON t.PublicSiteId=@ResolvedPublicSiteId AND t.Rfc=p.Rfc AND t.CheckoutAttemptId=@Id AND t.ProviderCaptureId=p.ExternalReference
      WHERE p.Rfc=@Rfc AND p.Id=@LocalPaymentId AND p.OrderId=@RestaurantOrderId AND p.PaymentMethod=''Platform'' AND p.[Status] IN(''Paid'',''PartiallyRefunded'',''Refunded''))
    BEGIN ROLLBACK; THROW 53665,''El pago POS no coincide con la captura Clip.'',1; END;
    UPDATE restaurante.OnlineCheckoutAttempt SET [State]=''PosCreated'',RestaurantOrderId=@RestaurantOrderId,
      PosCreatedAtUtc=COALESCE(PosCreatedAtUtc,SYSUTCDATETIME()),ImportLeaseId=NULL,ImportLeaseExpiresAtUtc=NULL,NextRetryAtUtc=NULL,
      FailureCode=NULL,FailureMessage=NULL,UpdatedAtUtc=SYSUTCDATETIME()
      WHERE Id=@Id AND PublicSiteId=@ResolvedPublicSiteId AND Rfc=@Rfc;
    UPDATE restaurante.PaymentGatewayTransaction SET LocalPaymentId=COALESCE(LocalPaymentId,@LocalPaymentId),UpdatedAtUtc=SYSUTCDATETIME()
      WHERE PublicSiteId=@ResolvedPublicSiteId AND Rfc=@Rfc AND CheckoutAttemptId=@Id AND (LocalPaymentId IS NULL OR LocalPaymentId=@LocalPaymentId);
    IF @@ROWCOUNT<>1 BEGIN ROLLBACK; THROW 53666,''No se pudo ligar el pago local a Clip.'',1; END;
    IF @FulfillmentType=''Delivery'' AND EXISTS(SELECT 1 FROM restaurante.OnlineCheckoutFacade WHERE CheckoutAttemptId=@Id AND PublicSiteId=@ResolvedPublicSiteId AND Rfc=@Rfc)
      AND NOT EXISTS(SELECT 1 FROM restaurante.DeliveryEvidence WHERE PublicSiteId=@ResolvedPublicSiteId AND Rfc=@Rfc AND OrderId=@RestaurantOrderId AND EvidenceType=''Facade'')
      INSERT restaurante.DeliveryEvidence(PublicSiteId,Rfc,SiteId,OrderId,EvidenceType,ContentType,ByteLength,Width,Height,Content,Thumbnail,ContentHash,Source,CreatedBy,CreatedAtUtc)
        SELECT PublicSiteId,Rfc,SiteId,@RestaurantOrderId,''Facade'',ContentType,ByteLength,Width,Height,Content,Thumbnail,ContentHash,''CustomerCheckout'',''customer'',CreatedAtUtc
        FROM restaurante.OnlineCheckoutFacade WHERE CheckoutAttemptId=@Id AND PublicSiteId=@ResolvedPublicSiteId AND Rfc=@Rfc;
    DELETE restaurante.OnlineCheckoutFacade WHERE CheckoutAttemptId=@Id AND PublicSiteId=@ResolvedPublicSiteId AND Rfc=@Rfc;
    IF NOT EXISTS(SELECT 1 FROM restaurante.OnlineOrderNotification WITH(UPDLOCK,HOLDLOCK) WHERE PublicSiteId=@ResolvedPublicSiteId AND CheckoutAttemptId=@Id AND NotificationType=''Confirmation'')
      INSERT restaurante.OnlineOrderNotification(PublicSiteId,Rfc,SiteId,CheckoutAttemptId,RestaurantOrderId,NotificationType,RecipientEmail,IdempotencyKey)
      VALUES(@ResolvedPublicSiteId,@Rfc,@SiteId,@Id,@RestaurantOrderId,''Confirmation'',@CustomerEmail,CONCAT(''email-confirmation-'',CONVERT(varchar(36),@Id)));
    COMMIT;
    SELECT Id CheckoutAttemptId,[State],RestaurantOrderId,PosCreatedAtUtc,UpdatedAtUtc FROM restaurante.OnlineCheckoutAttempt
      WHERE Id=@Id AND PublicSiteId=@ResolvedPublicSiteId AND Rfc=@Rfc;
  END;');

  EXEC(N'CREATE OR ALTER PROCEDURE restaurante.OnlineDeliveryEvidencePurge AS
  BEGIN SET NOCOUNT ON;
    DECLARE @PublicSiteId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N''OrionERP.PublicSiteId''));
    DECLARE @Rfc varchar(50)=CONVERT(varchar(50),SESSION_CONTEXT(N''OrionRfc''));
    IF @PublicSiteId IS NULL OR NULLIF(@Rfc,'''') IS NULL THROW 54123,''No existe contexto para purga.'',1;
    DELETE facade FROM restaurante.OnlineCheckoutFacade facade JOIN restaurante.OnlineCheckoutAttempt a
      ON a.PublicSiteId=facade.PublicSiteId AND a.Rfc=facade.Rfc AND a.SiteId=facade.SiteId AND a.Id=facade.CheckoutAttemptId
      WHERE facade.PublicSiteId=@PublicSiteId AND facade.Rfc=@Rfc AND facade.UpdatedAtUtc<DATEADD(HOUR,-24,SYSUTCDATETIME())
        AND a.[State] IN(''Quoted'',''PaymentDenied'',''RequoteRequired'',''Expired'',''Failed'');
    UPDATE e SET Content=NULL,Thumbnail=NULL,PurgedAtUtc=SYSUTCDATETIME(),PurgedBy=N''system:retention-90-days''
      FROM restaurante.DeliveryEvidence e JOIN restaurante.[Order] o ON o.PublicSiteId=e.PublicSiteId AND o.Rfc=e.Rfc AND o.SiteId=e.SiteId AND o.Id=e.OrderId
      WHERE e.PublicSiteId=@PublicSiteId AND e.Rfc=@Rfc AND e.PurgedAtUtc IS NULL
        AND ((o.CompletedAt IS NOT NULL AND o.CompletedAt<DATEADD(DAY,-90,SYSUTCDATETIME())) OR (o.CancelledAt IS NOT NULL AND o.CancelledAt<DATEADD(DAY,-90,SYSUTCDATETIME())));
  END;');

  EXEC(N'CREATE OR ALTER PROCEDURE restaurante.OnlineOrderNotificationClaim
    @LeaseId uniqueidentifier,@BatchSize int=10,@LeaseSeconds int=90
  AS
  BEGIN SET NOCOUNT ON; SET XACT_ABORT ON;
    DECLARE @PublicSiteId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N''OrionERP.PublicSiteId''));
    DECLARE @Rfc varchar(50)=CONVERT(varchar(50),SESSION_CONTEXT(N''OrionRfc'')); DECLARE @Claimed TABLE(Id bigint NOT NULL);
    IF @PublicSiteId IS NULL OR NULLIF(@Rfc,'''') IS NULL THROW 53701,''No existe contexto publico.'',1;
    IF @LeaseId IS NULL OR @LeaseSeconds NOT BETWEEN 15 AND 600 OR @BatchSize NOT BETWEEN 1 AND 50 THROW 53702,''La concesion de correo es invalida.'',1;
    BEGIN TRANSACTION;
    ;WITH nextNotification AS
    (SELECT TOP(@BatchSize) n.* FROM restaurante.OnlineOrderNotification n WITH(UPDLOCK,READPAST,ROWLOCK)
      WHERE n.PublicSiteId=@PublicSiteId AND n.Rfc=@Rfc AND (n.[Status] IN(''Pending'',''Failed'') OR (n.[Status]=''Processing'' AND n.LeaseExpiresAtUtc<SYSUTCDATETIME()))
        AND (n.NextRetryAtUtc IS NULL OR n.NextRetryAtUtc<=SYSUTCDATETIME()) AND (n.LeaseExpiresAtUtc IS NULL OR n.LeaseExpiresAtUtc<SYSUTCDATETIME())
      ORDER BY n.CreatedAtUtc,n.Id)
    UPDATE nextNotification SET [Status]=''Processing'',Attempts=Attempts+1,LeaseId=@LeaseId,
      LeaseExpiresAtUtc=DATEADD(SECOND,@LeaseSeconds,SYSUTCDATETIME()),NextRetryAtUtc=NULL,UpdatedAtUtc=SYSUTCDATETIME()
      OUTPUT inserted.Id INTO @Claimed(Id);
    COMMIT;
    SELECT n.Id,n.CheckoutAttemptId,n.RestaurantOrderId,n.NotificationType,n.RecipientEmail,n.IdempotencyKey,n.ProviderMessageId,
      n.Attempts,n.LeaseId,n.LeaseExpiresAtUtc,a.ClientAttemptId,a.CustomerName,o.Folio OrderFolio,o.[Status] OrderStatus,o.OrderType FulfillmentType,
      o.Total,a.CurrencyCode FROM @Claimed c JOIN restaurante.OnlineOrderNotification n ON n.Id=c.Id
      JOIN restaurante.OnlineCheckoutAttempt a ON a.PublicSiteId=n.PublicSiteId AND a.Rfc=n.Rfc AND a.SiteId=n.SiteId AND a.Id=n.CheckoutAttemptId
      JOIN restaurante.[Order] o ON o.PublicSiteId=n.PublicSiteId AND o.Rfc=n.Rfc AND o.SiteId=n.SiteId AND o.Id=n.RestaurantOrderId;
  END;');

  IF EXISTS(SELECT 1 FROM orion.PublicPermissionProfile WHERE ProfileCode='RESTAURANT_PUBLIC' AND ProfileVersion=12)
    THROW 54130,'Existe un perfil Restaurant v12 sin esta migracion.',1;
  INSERT orion.PublicPermissionProfile(ProfileCode,ProfileVersion,ModuleCode) VALUES('RESTAURANT_PUBLIC',12,'RESTAURANT');
  INSERT orion.PublicPermissionProfileEntry(ProfileCode,ProfileVersion,PermissionState,PermissionName,SecurableClass,SchemaName,ObjectName)
    SELECT ProfileCode,12,PermissionState,PermissionName,SecurableClass,SchemaName,ObjectName
    FROM orion.PublicPermissionProfileEntry WHERE ProfileCode='RESTAURANT_PUBLIC' AND ProfileVersion=11;
  INSERT orion.PublicPermissionProfileEntry(ProfileCode,ProfileVersion,PermissionState,PermissionName,SecurableClass,SchemaName,ObjectName)
    SELECT 'RESTAURANT_PUBLIC',12,'GRANT','EXECUTE','OBJECT','restaurante',v.ObjectName
    FROM (VALUES('OnlineCheckoutAttemptCreateV2'),('OnlineCheckoutChargeBeginV2'),('OnlineCheckoutFacadeUpsert'),('OnlineOrderImportCompleteV2'),('OnlineDeliveryEvidencePurge')) v(ObjectName);
  UPDATE p SET ProfileChecksum=x.ProfileChecksum,UpdatedAtUtc=SYSUTCDATETIME()
    FROM orion.PublicPermissionProfile p CROSS APPLY
    (SELECT HASHBYTES('SHA2_256',STRING_AGG(CONVERT(nvarchar(max),e.PermissionState+N'|'+e.PermissionName+N'|'+e.SecurableClass+N'|'+e.SchemaName+N'|'+e.ObjectName),N';')
      WITHIN GROUP(ORDER BY e.PermissionState,e.PermissionName,e.SecurableClass,e.SchemaName,e.ObjectName)) ProfileChecksum
      FROM orion.PublicPermissionProfileEntry e WHERE e.ProfileCode=p.ProfileCode AND e.ProfileVersion=p.ProfileVersion) x
    WHERE p.ProfileCode='RESTAURANT_PUBLIC' AND p.ProfileVersion=12;

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
    SELECT ps.PublicSiteKey,b.PrincipalName FROM orion.PublicSqlPrincipalBinding b JOIN orion.PublicSite ps ON ps.PublicSiteId=b.PublicSiteId
    WHERE b.IsActive=1 AND b.PermissionProfile='RESTAURANT_PUBLIC' AND ps.ModuleCode='RESTAURANT';
  OPEN public_principal_cursor; FETCH NEXT FROM public_principal_cursor INTO @PublicSiteKey,@PrincipalName;
  WHILE @@FETCH_STATUS=0
  BEGIN
    INSERT @ProfileApplicationReview
      (ApplyChanges,PublicSiteId,ProfileCode,ProfileVersion,Estado,Permiso,Securable,YaExiste)
    EXEC orion.ApplyPublicPermissionProfile @PublicSiteKey=@PublicSiteKey,@PrincipalName=@PrincipalName,@ApplyChanges=1;
    SET @AppliedBindings+=1; FETCH NEXT FROM public_principal_cursor INTO @PublicSiteKey,@PrincipalName;
  END;
  CLOSE public_principal_cursor; DEALLOCATE public_principal_cursor;
  IF @AppliedBindings=0 THROW 54131,'No existen bindings Restaurant activos.',1;

  INSERT orion.SchemaMigration(MigrationId,Checksum,AppliedBy,AppVersion,DatabaseName)
    VALUES(@MigrationId,@MigrationChecksum,ORIGINAL_LOGIN(),@AppVersion,DB_NAME());
  COMMIT TRANSACTION;
  SELECT N'APLICADO' Estado,@MigrationId MigrationId,@MigrationChecksum Checksum;
END TRY
BEGIN CATCH
  IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
  THROW;
END CATCH;
