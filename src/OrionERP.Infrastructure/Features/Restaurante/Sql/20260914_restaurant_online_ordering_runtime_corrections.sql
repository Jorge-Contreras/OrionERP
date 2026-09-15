/*
  Runtime corrections found during the first Orion_Sandbox smoke test:
    - legal versions are declared by the verified public host together with
      PayPal readiness;
    - administrative optimistic concurrency uses ConfigurationVersion, so
      heartbeat/readiness telemetry cannot invalidate an operator's edit;
    - restaurant emails persist an Outlook immutable draft id before delivery,
      allowing expired worker leases to recover without sending a second copy;
    - expired payment worker leases remain reclaimable and ambiguous refunds
      remain reserved/idempotently recoverable;
    - Club Bruno membership writes cross narrowly scoped procedures and the
      RESTAURANT_PUBLIC v8 profile removes broad direct data modification.
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
  IF @ApplyInput NOT IN(N'0',N'1') THROW 53730,'ApplyChanges debe ser 0 o 1.',1;
  SET @ApplyChanges=CONVERT(bit,@ApplyInput);
END;
IF @ExpectedDatabase LIKE N'$'+N'(%'
   OR @ExpectedDatabase NOT IN(N'Orion_Sandbox',N'grupocarpio',N'Orion_CutoverValidation_20260908')
  THROW 53731,'Base esperada no autorizada para las correcciones de online ordering.',1;
IF DB_NAME()<>@ExpectedDatabase
  THROW 53732,'La conexion no apunta a la base declarada.',1;
IF @MigrationId<>N'20260914_restaurant_online_ordering_runtime_corrections'
  THROW 53733,'MigrationId incorrecto.',1;
IF @MigrationChecksum LIKE '$'+N'(%' OR LEN(@MigrationChecksum)<>64
   OR @MigrationChecksum LIKE '%[^0-9A-F]%' COLLATE Latin1_General_100_BIN2
  THROW 53734,'MigrationChecksum invalido.',1;
IF @AppVersionInput NOT LIKE N'$'+N'(%'
  SET @AppVersion=NULLIF(LTRIM(RTRIM(@AppVersionInput)),N'');

IF OBJECT_ID(N'orion.SchemaMigration',N'U') IS NULL
   OR OBJECT_ID(N'orion.PublicSite',N'U') IS NULL
   OR OBJECT_ID(N'orion.Company',N'U') IS NULL
   OR OBJECT_ID(N'orion.PublicSqlPrincipalBinding',N'U') IS NULL
   OR OBJECT_ID(N'orion.PublicPermissionProfile',N'U') IS NULL
   OR OBJECT_ID(N'orion.PublicPermissionProfileEntry',N'U') IS NULL
   OR OBJECT_ID(N'orion.ApplyPublicPermissionProfile',N'P') IS NULL
   OR OBJECT_ID(N'public_identity.AspNetUsers',N'U') IS NULL
   OR OBJECT_ID(N'fidelidad.MemberAccount',N'U') IS NULL
   OR OBJECT_ID(N'fidelidad.MemberQrToken',N'U') IS NULL
   OR OBJECT_ID(N'fidelidad.MemberConsent',N'U') IS NULL
   OR OBJECT_ID(N'fidelidad.MemberClosureRequest',N'U') IS NULL
   OR OBJECT_ID(N'fidelidad.PointLedger',N'U') IS NULL
   OR OBJECT_ID(N'fidelidad.ProgramSettings',N'U') IS NULL
   OR OBJECT_ID(N'restaurante.[Order]',N'U') IS NULL
   OR OBJECT_ID(N'restaurante.OnlineOrderingSettings',N'U') IS NULL
   OR OBJECT_ID(N'restaurante.OnlineCheckoutStatusGet',N'P') IS NULL
   OR OBJECT_ID(N'restaurante.PayPalRecoveryClaim',N'P') IS NULL
   OR OBJECT_ID(N'restaurante.PaymentGatewayRefundRequest',N'P') IS NULL
   OR OBJECT_ID(N'restaurante.PaymentGatewayRefundClaim',N'P') IS NULL
   OR OBJECT_ID(N'restaurante.PaymentGatewayRefundResult',N'P') IS NULL
   OR OBJECT_ID(N'restaurante.OnlineOrderingRecoveryList',N'P') IS NULL
   OR OBJECT_ID(N'restaurante.OnlineOrderNotification',N'U') IS NULL
   OR OBJECT_ID(N'restaurante.OnlineOrderingRuntimeReadinessSet',N'P') IS NULL
   OR OBJECT_ID(N'restaurante.OnlineOrderingAdminSave',N'P') IS NULL
  THROW 53735,'Falta la migracion base de online ordering.',1;
IF NOT EXISTS
(
  SELECT 1 FROM orion.SchemaMigration
  WHERE MigrationId=N'20260914_restaurant_online_ordering'
)
  THROW 53736,'La migracion base no esta registrada.',1;
IF NOT EXISTS
(
  SELECT 1 FROM orion.PublicPermissionProfile
  WHERE ProfileCode='RESTAURANT_PUBLIC' AND ProfileVersion=7
)
  THROW 53740,'Falta el perfil publico Restaurant v7.',1;
IF
(
  SELECT COUNT(DISTINCT schemaInfo.principal_id)
  FROM sys.schemas schemaInfo
  WHERE schemaInfo.name IN('orion','public_identity','fidelidad','restaurante')
)<>1
  THROW 53757,'Los procedimientos publicos requieren una cadena de propiedad comun entre esquemas.',1;

DECLARE @ExistingChecksum char(64)=
  (SELECT Checksum FROM orion.SchemaMigration WHERE MigrationId=@MigrationId);
IF @ExistingChecksum IS NOT NULL AND UPPER(@ExistingChecksum)<>UPPER(@MigrationChecksum)
  THROW 53737,'El mismo MigrationId ya existe con otro checksum.',1;
IF @ExistingChecksum IS NOT NULL
BEGIN
  SELECT N'YA_APLICADO' Estado,@MigrationId MigrationId,@ExistingChecksum Checksum;
  RETURN;
END;

BEGIN TRY
  BEGIN TRANSACTION;

  IF COL_LENGTH(N'restaurante.PaymentGatewayRefund',N'ProviderGrossAmount') IS NULL
    ALTER TABLE restaurante.PaymentGatewayRefund ADD ProviderGrossAmount decimal(18,2) NULL;
  IF COL_LENGTH(N'restaurante.PaymentGatewayRefund',N'ProviderFeeAmount') IS NULL
    ALTER TABLE restaurante.PaymentGatewayRefund ADD ProviderFeeAmount decimal(18,2) NULL;
  IF COL_LENGTH(N'restaurante.PaymentGatewayRefund',N'ProviderNetAmount') IS NULL
    ALTER TABLE restaurante.PaymentGatewayRefund ADD ProviderNetAmount decimal(18,2) NULL;
  IF COL_LENGTH(N'restaurante.PaymentGatewayRefund',N'ReconciledAtUtc') IS NULL
    ALTER TABLE restaurante.PaymentGatewayRefund ADD ReconciledAtUtc datetime2(3) NULL;

  IF NOT EXISTS
  (
    SELECT 1 FROM sys.check_constraints
    WHERE parent_object_id=OBJECT_ID(N'restaurante.PaymentGatewayRefund')
      AND name=N'CK_PaymentGatewayRefund_ProviderAmounts'
  )
    EXEC(N'
ALTER TABLE restaurante.PaymentGatewayRefund WITH CHECK
ADD CONSTRAINT CK_PaymentGatewayRefund_ProviderAmounts CHECK
(
  (ProviderGrossAmount IS NULL AND ProviderFeeAmount IS NULL
   AND ProviderNetAmount IS NULL AND ReconciledAtUtc IS NULL)
  OR
  (ProviderGrossAmount IS NOT NULL AND ProviderFeeAmount IS NOT NULL
   AND ProviderNetAmount IS NOT NULL AND ReconciledAtUtc IS NOT NULL
   AND ProviderGrossAmount=Amount
   AND ProviderGrossAmount=ProviderFeeAmount+ProviderNetAmount
   AND ProviderGrossAmount>0 AND ProviderFeeAmount>=0 AND ProviderNetAmount>=0)
);');

  IF EXISTS
  (
    SELECT refundInfo.GatewayTransactionId
    FROM restaurante.PaymentGatewayRefund refundInfo
    WHERE refundInfo.LocalRefundId IS NULL
    GROUP BY refundInfo.GatewayTransactionId
    HAVING COUNT_BIG(*)>1
  )
    THROW 53760,'Existen varios reembolsos sin resolver para una misma captura.',1;
  IF NOT EXISTS
  (
    SELECT 1 FROM sys.indexes
    WHERE object_id=OBJECT_ID(N'restaurante.PaymentGatewayRefund')
      AND name=N'UX_PaymentGatewayRefund_UnresolvedTransaction'
  )
    CREATE UNIQUE INDEX UX_PaymentGatewayRefund_UnresolvedTransaction
      ON restaurante.PaymentGatewayRefund(GatewayTransactionId)
      WHERE LocalRefundId IS NULL;

  IF COL_LENGTH(N'restaurante.PaymentGatewayEvent',N'RelatedOrderId') IS NULL
    ALTER TABLE restaurante.PaymentGatewayEvent ADD RelatedOrderId varchar(64) NULL;
  IF COL_LENGTH(N'restaurante.PaymentGatewayEvent',N'RelatedCaptureId') IS NULL
    ALTER TABLE restaurante.PaymentGatewayEvent ADD RelatedCaptureId varchar(64) NULL;
  IF COL_LENGTH(N'restaurante.PaymentGatewayEvent',N'RelatedRefundId') IS NULL
    ALTER TABLE restaurante.PaymentGatewayEvent ADD RelatedRefundId varchar(64) NULL;

  IF COL_LENGTH(N'restaurante.OnlineCheckoutAttempt',N'SettingsConfigurationVersion') IS NULL
    ALTER TABLE restaurante.OnlineCheckoutAttempt ADD SettingsConfigurationVersion bigint NULL;
  EXEC(N'
UPDATE attempt
SET SettingsConfigurationVersion=settings.ConfigurationVersion
FROM restaurante.OnlineCheckoutAttempt attempt
JOIN restaurante.OnlineOrderingSettings settings
  ON settings.PublicSiteId=attempt.PublicSiteId
 AND settings.Rfc=attempt.Rfc
 AND settings.SiteId=attempt.SiteId
WHERE attempt.SettingsConfigurationVersion IS NULL;
IF EXISTS
(
  SELECT 1 FROM restaurante.OnlineCheckoutAttempt
  WHERE SettingsConfigurationVersion IS NULL OR SettingsConfigurationVersion<=0
)
  THROW 53769,''No se pudo ligar cada checkout a una version de configuracion.'',1;
ALTER TABLE restaurante.OnlineCheckoutAttempt
  ALTER COLUMN SettingsConfigurationVersion bigint NOT NULL;');

  IF NOT EXISTS
  (
    SELECT 1 FROM sys.check_constraints
    WHERE parent_object_id=OBJECT_ID(N'restaurante.OnlineCheckoutAttempt')
      AND name=N'CK_OnlineCheckoutAttempt_SettingsVersion'
  )
    EXEC(N'
ALTER TABLE restaurante.OnlineCheckoutAttempt WITH CHECK
ADD CONSTRAINT CK_OnlineCheckoutAttempt_SettingsVersion
CHECK(SettingsConfigurationVersion>0);');

  EXEC(N'
CREATE OR ALTER PROCEDURE restaurante.OnlineOrderingRuntimeReadinessSet
  @GatewayEnvironment varchar(20),
  @GatewayCredentialsConfigured bit,
  @GatewayWebhookConfigured bit,
  @MerchantProfileKey varchar(80),
  @TermsVersion varchar(40),
  @PrivacyVersion varchar(40)
AS
BEGIN
  SET NOCOUNT ON;

  DECLARE @PublicSiteId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N''OrionERP.PublicSiteId''));
  DECLARE @Rfc varchar(50)=CONVERT(varchar(50),SESSION_CONTEXT(N''OrionRfc''));
  DECLARE @RequiredEnvironment varchar(20)=CASE WHEN DB_NAME()=''grupocarpio'' THEN ''Live'' ELSE ''Sandbox'' END;

  IF @PublicSiteId IS NULL OR NULLIF(@Rfc,'''') IS NULL
    THROW 53671,''No existe un contexto publico de restaurante verificado.'',1;
  IF @GatewayEnvironment NOT IN(''Sandbox'',''Live'')
     OR NULLIF(LTRIM(RTRIM(@MerchantProfileKey)),'''') IS NULL
     OR NULLIF(LTRIM(RTRIM(@TermsVersion)),'''') IS NULL
     OR NULLIF(LTRIM(RTRIM(@PrivacyVersion)),'''') IS NULL
    THROW 53672,''La declaracion de runtime PayPal o legal es invalida.'',1;

  UPDATE restaurante.OnlineOrderingSettings
  SET GatewayEnvironment=@GatewayEnvironment,
      GatewayCredentialsConfigured=@GatewayCredentialsConfigured,
      GatewayWebhookConfigured=@GatewayWebhookConfigured,
      ActiveMerchantProfileKey=LTRIM(RTRIM(@MerchantProfileKey)),
      TermsVersion=LTRIM(RTRIM(@TermsVersion)),
      PrivacyVersion=LTRIM(RTRIM(@PrivacyVersion)),
      GatewayReadinessAtUtc=CASE
        WHEN @GatewayEnvironment=@RequiredEnvironment
         AND @GatewayCredentialsConfigured=1 AND @GatewayWebhookConfigured=1
        THEN SYSUTCDATETIME() ELSE NULL END,
      ConfigurationVersion=ConfigurationVersion+
        CASE WHEN TermsVersion<>LTRIM(RTRIM(@TermsVersion))
               OR PrivacyVersion<>LTRIM(RTRIM(@PrivacyVersion)) THEN 1 ELSE 0 END,
      UpdatedAtUtc=SYSUTCDATETIME()
  WHERE PublicSiteId=@PublicSiteId AND Rfc=@Rfc;

  IF @@ROWCOUNT<>1
    THROW 53673,''La declaracion de runtime no resolvio el sitio publico.'',1;

  SELECT PublicSiteId,GatewayEnvironment,GatewayCredentialsConfigured,
    GatewayWebhookConfigured,GatewayReadinessAtUtc,ActiveMerchantProfileKey,
    TermsVersion,PrivacyVersion,ConfigurationVersion,
    CONVERT(bit,CASE
      WHEN GatewayEnvironment=@RequiredEnvironment
       AND GatewayCredentialsConfigured=1 AND GatewayWebhookConfigured=1
       AND GatewayReadinessAtUtc>=DATEADD(MINUTE,-5,SYSUTCDATETIME())
      THEN 1 ELSE 0 END) IsGatewayReady
  FROM restaurante.OnlineOrderingSettings
  WHERE PublicSiteId=@PublicSiteId AND Rfc=@Rfc;
END;');

  EXEC(N'
CREATE OR ALTER PROCEDURE restaurante.OnlineCheckoutStatusGet
  @TrackingTokenHash binary(32)
AS
BEGIN
  SET NOCOUNT ON;

  DECLARE @PublicSiteId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N''OrionERP.PublicSiteId''));
  DECLARE @Rfc varchar(50)=CONVERT(varchar(50),SESSION_CONTEXT(N''OrionRfc''));

  IF @PublicSiteId IS NULL OR NULLIF(@Rfc,'''') IS NULL OR @TrackingTokenHash IS NULL
    THROW 53653,''El contexto o token de seguimiento es invalido.'',1;

  SELECT
    attempt.[State] CheckoutStatus,
    CASE
      WHEN attempt.[State]=''Refunded'' OR transactionInfo.[Status]=''Refunded'' THEN ''Refunded''
      WHEN transactionInfo.[Status]=''PartiallyRefunded'' THEN ''PartiallyRefunded''
      WHEN attempt.[State] IN(''Captured'',''CapturedNeedsOrder'',''PosCreated'',''RefundRequested'',''RefundPending'') THEN ''Paid''
      WHEN attempt.[State]=''CapturePending'' THEN ''Pending''
      WHEN attempt.[State]=''PaymentDenied'' THEN ''Denied''
      ELSE ''NotPaid'' END PaymentStatus,
    orderInfo.[Status] OrderStatus,
    orderInfo.Folio OrderFolio,
    attempt.Total,
    attempt.CurrencyCode Currency,
    CONVERT(bit,CASE
      WHEN attempt.[State] IN(''Refunded'',''PaymentDenied'',''Expired'',''Failed'')
        OR orderInfo.[Status] IN(''Completed'',''Cancelled'') THEN 1 ELSE 0 END) IsTerminal,
    CONVERT(nvarchar(240),CASE
      WHEN attempt.[State]=''PosCreated'' AND orderInfo.[Status]=''Ready'' THEN N''Tu pedido esta listo para recoger.''
      WHEN attempt.[State]=''PosCreated'' THEN N''Tu pedido esta confirmado. Te enviaremos un correo cuando este listo.''
      WHEN attempt.[State] IN(''Captured'',''CapturedNeedsOrder'') THEN N''Pago recibido. Estamos confirmando tu pedido.''
      WHEN attempt.[State] IN(''RefundRequested'',''RefundPending'') THEN N''Estamos procesando el reembolso de tu pago.''
      WHEN attempt.[State]=''Refunded'' THEN N''Tu pago fue reembolsado.''
      WHEN attempt.[State]=''PaymentDenied'' THEN N''PayPal no completo el cobro.''
      WHEN attempt.[State]=''RequoteRequired'' THEN N''El menu o el total cambio. Revisa nuevamente tu pedido.''
      WHEN attempt.[State]=''CapturePending'' THEN N''Estamos confirmando el pago con PayPal.''
      ELSE N''El pedido esta pendiente.'' END) [Message],
    attempt.UpdatedAtUtc
  FROM restaurante.OnlineCheckoutAttempt attempt
  LEFT JOIN restaurante.PaymentGatewayTransaction transactionInfo
    ON transactionInfo.PublicSiteId=attempt.PublicSiteId
   AND transactionInfo.Rfc=attempt.Rfc
   AND transactionInfo.SiteId=attempt.SiteId
   AND transactionInfo.CheckoutAttemptId=attempt.Id
  LEFT JOIN restaurante.[Order] orderInfo
    ON orderInfo.PublicSiteId=attempt.PublicSiteId
   AND orderInfo.Rfc=attempt.Rfc
   AND orderInfo.SiteId=attempt.SiteId
   AND orderInfo.Id=attempt.RestaurantOrderId
  WHERE attempt.PublicSiteId=@PublicSiteId
    AND attempt.Rfc=@Rfc
    AND attempt.TrackingTokenHash=@TrackingTokenHash;
END;');

  EXEC(N'
CREATE OR ALTER PROCEDURE restaurante.OnlineOrderingAdminSaveV2
  @IsEnabled bit,
  @IsPaused bit,
  @PauseMessage nvarchar(300)=NULL,
  @GuestCheckoutEnabled bit,
  @PickupEnabled bit,
  @MaximumOrderTotal decimal(18,2),
  @WeeklyScheduleJson nvarchar(4000),
  @TermsVersion varchar(40),
  @PrivacyVersion varchar(40),
  @EnabledProductIdsJson nvarchar(max),
  @ExpectedConfigurationVersion bigint,
  @UpdatedBy nvarchar(256)=NULL
AS
BEGIN
  SET NOCOUNT ON;
  SET XACT_ABORT ON;

  DECLARE @PublicSiteId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N''OrionERP.PublicSiteId''));
  DECLARE @Rfc varchar(50)=CONVERT(varchar(50),SESSION_CONTEXT(N''OrionRfc''));
  DECLARE @CompanyId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N''OrionERP.CompanyId''));
  DECLARE @OrionSiteId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N''OrionERP.SiteId''));
  DECLARE @CurrentConfigurationVersion bigint;
  DECLARE @CurrentRowVersion binary(8);

  IF @ExpectedConfigurationVersion IS NULL OR @ExpectedConfigurationVersion<=0
    THROW 53685,''La configuracion cambio; recargue antes de guardar.'',1;

  BEGIN TRY
    BEGIN TRANSACTION;

    SELECT @CurrentConfigurationVersion=settings.ConfigurationVersion,
           @CurrentRowVersion=settings.RowVersion
    FROM restaurante.OnlineOrderingSettings settings WITH(UPDLOCK,HOLDLOCK)
    WHERE settings.Rfc=@Rfc
      AND
      (
        (@PublicSiteId IS NOT NULL AND settings.PublicSiteId=@PublicSiteId)
        OR (@PublicSiteId IS NULL AND settings.CompanyId=@CompanyId
            AND (@OrionSiteId IS NULL OR settings.OrionSiteId=@OrionSiteId))
      );

    IF @@ROWCOUNT<>1 OR @CurrentConfigurationVersion<>@ExpectedConfigurationVersion
    BEGIN
      ROLLBACK TRANSACTION;
      THROW 53685,''La configuracion cambio; recargue antes de guardar.'',1;
    END;

    EXEC restaurante.OnlineOrderingAdminSave
      @IsEnabled=@IsEnabled,
      @IsPaused=@IsPaused,
      @PauseMessage=@PauseMessage,
      @GuestCheckoutEnabled=@GuestCheckoutEnabled,
      @PickupEnabled=@PickupEnabled,
      @MaximumOrderTotal=@MaximumOrderTotal,
      @WeeklyScheduleJson=@WeeklyScheduleJson,
      @TermsVersion=@TermsVersion,
      @PrivacyVersion=@PrivacyVersion,
      @EnabledProductIdsJson=@EnabledProductIdsJson,
      @ExpectedRowVersion=@CurrentRowVersion,
      @UpdatedBy=@UpdatedBy;

    COMMIT TRANSACTION;
  END TRY
  BEGIN CATCH
    IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
    THROW;
  END CATCH;
END;');

  EXEC(N'
CREATE OR ALTER PROCEDURE restaurante.OnlineCheckoutAttemptCreate
  @Id uniqueidentifier,
  @ClientAttemptId uniqueidentifier,
  @MemberId uniqueidentifier=NULL,
  @CustomerName nvarchar(150),
  @CustomerEmail nvarchar(320),
  @CustomerPhone varchar(30),
  @QuoteFingerprint char(64),
  @QuoteExpiresAtUtc datetime2(3),
  @SettingsConfigurationVersion bigint,
  @CartSnapshotJson nvarchar(max),
  @PromotionCode varchar(80)=NULL,
  @Subtotal decimal(18,2),
  @PromotionDiscountTotal decimal(18,2),
  @TaxTotal decimal(18,2),
  @Total decimal(18,2),
  @CurrencyCode char(3),
  @PrivacyVersion varchar(40),
  @TermsVersion varchar(40),
  @LegalAcceptedAtUtc datetime2(3),
  @MerchantProfileKey varchar(80),
  @TrackingTokenHash binary(32)
AS
BEGIN
  SET NOCOUNT ON;
  SET XACT_ABORT ON;

  DECLARE @PublicSiteId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N''OrionERP.PublicSiteId''));
  DECLARE @Rfc varchar(50)=CONVERT(varchar(50),SESSION_CONTEXT(N''OrionRfc''));
  DECLARE @SiteId int;
  DECLARE @ExistingId uniqueidentifier;
  DECLARE @WasCreated bit=0;

  IF @PublicSiteId IS NULL OR NULLIF(@Rfc,'''') IS NULL
    THROW 53622,''No existe un contexto publico de restaurante verificado.'',1;
  IF @Id IS NULL OR @ClientAttemptId IS NULL OR @SettingsConfigurationVersion<=0
    THROW 53623,''Los identificadores y version del checkout son obligatorios.'',1;
  IF @QuoteExpiresAtUtc<=SYSUTCDATETIME()
    THROW 53624,''La cotizacion ya vencio.'',1;
  IF @LegalAcceptedAtUtc>DATEADD(MINUTE,5,SYSUTCDATETIME())
    THROW 53625,''La aceptacion legal tiene una fecha invalida.'',1;
  IF ISJSON(@CartSnapshotJson)<>1
    THROW 53626,''El carrito normalizado no es JSON valido.'',1;
  IF @CurrencyCode<>UPPER(@CurrencyCode)
     OR LEN(@QuoteFingerprint)<>64
     OR @QuoteFingerprint LIKE ''%[^0-9A-F]%'' COLLATE Latin1_General_100_BIN2
    THROW 53627,''La moneda o huella de cotizacion es invalida.'',1;

  BEGIN TRANSACTION;

  SELECT @SiteId=settings.SiteId
  FROM restaurante.OnlineOrderingSettings settings WITH(UPDLOCK,HOLDLOCK)
  WHERE settings.PublicSiteId=@PublicSiteId
    AND settings.Rfc=@Rfc
    AND settings.ConfigurationVersion=@SettingsConfigurationVersion
    AND settings.IsEnabled=1
    AND settings.IsPaused=0
    AND settings.PickupEnabled=1
    AND (@MemberId IS NOT NULL OR settings.GuestCheckoutEnabled=1)
    AND settings.TermsVersion=@TermsVersion
    AND settings.PrivacyVersion=@PrivacyVersion
    AND settings.ActiveMerchantProfileKey=@MerchantProfileKey
    AND @Total BETWEEN settings.MinimumOrderTotal AND settings.MaximumOrderTotal;

  IF @SiteId IS NULL
  BEGIN
    ROLLBACK TRANSACTION;
    THROW 53628,''El sitio o la version de configuracion ya no acepta este checkout.'',1;
  END;

  IF @MemberId IS NOT NULL AND NOT EXISTS
  (
    SELECT 1 FROM fidelidad.MemberAccount member
    WHERE member.PublicSiteId=@PublicSiteId AND member.Rfc=@Rfc
      AND member.Id=@MemberId AND member.[Status]=''Active''
  )
  BEGIN
    ROLLBACK TRANSACTION;
    THROW 53629,''La membresia no pertenece al sitio publico.'',1;
  END;

  SELECT @ExistingId=attempt.Id
  FROM restaurante.OnlineCheckoutAttempt attempt WITH(UPDLOCK,HOLDLOCK)
  WHERE attempt.PublicSiteId=@PublicSiteId
    AND attempt.Rfc=@Rfc
    AND attempt.ClientAttemptId=@ClientAttemptId;

  IF @ExistingId IS NOT NULL
  BEGIN
    IF NOT EXISTS
    (
      SELECT 1 FROM restaurante.OnlineCheckoutAttempt attempt
      WHERE attempt.Id=@ExistingId
        AND attempt.QuoteFingerprint=@QuoteFingerprint
        AND attempt.SettingsConfigurationVersion=@SettingsConfigurationVersion
        AND attempt.MerchantProfileKey=@MerchantProfileKey
        AND attempt.CurrencyCode=@CurrencyCode
        AND attempt.Total=@Total
        AND ((attempt.MemberId=@MemberId) OR (attempt.MemberId IS NULL AND @MemberId IS NULL))
    )
    BEGIN
      ROLLBACK TRANSACTION;
      THROW 53630,''ClientAttemptId ya se uso con otro checkout.'',1;
    END;
    SET @Id=@ExistingId;
  END
  ELSE
  BEGIN
    INSERT restaurante.OnlineCheckoutAttempt
    (
      Id,PublicSiteId,Rfc,SiteId,ClientAttemptId,MemberId,
      CustomerName,CustomerEmail,CustomerPhone,QuoteFingerprint,QuoteExpiresAtUtc,
      SettingsConfigurationVersion,CartSnapshotJson,PromotionCode,
      Subtotal,PromotionDiscountTotal,TaxTotal,Total,
      CurrencyCode,PrivacyVersion,TermsVersion,LegalAcceptedAtUtc,
      MerchantProfileKey,TrackingTokenHash
    )
    VALUES
    (
      @Id,@PublicSiteId,@Rfc,@SiteId,@ClientAttemptId,@MemberId,
      LTRIM(RTRIM(@CustomerName)),LOWER(LTRIM(RTRIM(@CustomerEmail))),LTRIM(RTRIM(@CustomerPhone)),
      @QuoteFingerprint,@QuoteExpiresAtUtc,@SettingsConfigurationVersion,@CartSnapshotJson,
      NULLIF(LTRIM(RTRIM(@PromotionCode)),''''),
      @Subtotal,@PromotionDiscountTotal,@TaxTotal,@Total,@CurrencyCode,
      @PrivacyVersion,@TermsVersion,@LegalAcceptedAtUtc,@MerchantProfileKey,@TrackingTokenHash
    );
    SET @WasCreated=1;
  END;

  COMMIT TRANSACTION;

  SELECT @WasCreated WasCreated,
    attempt.Id,attempt.PublicSiteId,attempt.Rfc,attempt.SiteId,attempt.ClientAttemptId,
    attempt.MemberId,attempt.CustomerName,attempt.CustomerEmail,attempt.CustomerPhone,
    attempt.QuoteFingerprint,attempt.QuoteExpiresAtUtc,attempt.SettingsConfigurationVersion,
    attempt.CartSnapshotJson,attempt.PromotionCode,attempt.Subtotal,
    attempt.PromotionDiscountTotal,attempt.TaxTotal,attempt.Total,
    attempt.CurrencyCode,attempt.PrivacyVersion,attempt.TermsVersion,
    attempt.LegalAcceptedAtUtc,attempt.MerchantProfileKey,attempt.TrackingTokenHash,
    attempt.PayPalCreateRequestId,attempt.PayPalOrderId,attempt.PayPalCaptureId,
    attempt.[State],attempt.RestaurantOrderId,attempt.ImportAttempts,
    attempt.NextRetryAtUtc,attempt.FailureCode,attempt.FailureMessage,
    attempt.CreatedAtUtc,attempt.UpdatedAtUtc,attempt.CapturedAtUtc,
    attempt.PosCreatedAtUtc,attempt.RowVersion
  FROM restaurante.OnlineCheckoutAttempt attempt
  WHERE attempt.Id=@Id AND attempt.PublicSiteId=@PublicSiteId AND attempt.Rfc=@Rfc;
END;');

  EXEC(N'
CREATE OR ALTER PROCEDURE restaurante.OnlineCheckoutCaptureAuthorize
  @Id uniqueidentifier,
  @PayPalOrderId varchar(64),
  @ProcessorHeartbeatMaxAgeSeconds int
AS
BEGIN
  SET NOCOUNT ON;
  SET XACT_ABORT ON;

  DECLARE @PublicSiteId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N''OrionERP.PublicSiteId''));
  DECLARE @Rfc varchar(50)=CONVERT(varchar(50),SESSION_CONTEXT(N''OrionRfc''));
  DECLARE @Now datetime2(3)=SYSUTCDATETIME();
  DECLARE @RequiredEnvironment varchar(20)=CASE WHEN DB_NAME()=''grupocarpio'' THEN ''Live'' ELSE ''Sandbox'' END;
  DECLARE @AttemptFound bit=0;
  DECLARE @AttemptState varchar(30);
  DECLARE @QuoteExpiresAtUtc datetime2(3);
  DECLARE @AttemptConfigurationVersion bigint;
  DECLARE @CurrentConfigurationVersion bigint;
  DECLARE @AttemptTotal decimal(18,2);
  DECLARE @MinimumOrderTotal decimal(18,2);
  DECLARE @MaximumOrderTotal decimal(18,2);
  DECLARE @AttemptTermsVersion varchar(40);
  DECLARE @CurrentTermsVersion varchar(40);
  DECLARE @AttemptPrivacyVersion varchar(40);
  DECLARE @CurrentPrivacyVersion varchar(40);
  DECLARE @AttemptMerchantProfileKey varchar(80);
  DECLARE @CurrentMerchantProfileKey varchar(80);
  DECLARE @IsEnabled bit;
  DECLARE @IsPaused bit;
  DECLARE @PickupEnabled bit;
  DECLARE @GatewayEnvironment varchar(20);
  DECLARE @GatewayCredentialsConfigured bit;
  DECLARE @GatewayWebhookConfigured bit;
  DECLARE @GatewayReadinessAtUtc datetime2(3);
  DECLARE @ProcessorHeartbeatAtUtc datetime2(3);
  DECLARE @WeeklyScheduleJson nvarchar(4000);
  DECLARE @TimeZoneId nvarchar(100);
  DECLARE @LocalNow datetime2(0);
  DECLARE @LocalTime time(0);
  DECLARE @DayIndex int;
  DECLARE @TodayKey varchar(10);
  DECLARE @PreviousKey varchar(10);
  DECLARE @IsOpen bit=0;

  IF @PublicSiteId IS NULL OR NULLIF(@Rfc,'''') IS NULL
    THROW 53646,''No existe un contexto publico de restaurante verificado.'',1;
  IF @ProcessorHeartbeatMaxAgeSeconds NOT BETWEEN 15 AND 300
    THROW 53770,''La vigencia del heartbeat es invalida.'',1;

  BEGIN TRANSACTION;

  SELECT @AttemptFound=1,@AttemptState=attempt.[State],
    @QuoteExpiresAtUtc=attempt.QuoteExpiresAtUtc,
    @AttemptConfigurationVersion=attempt.SettingsConfigurationVersion,
    @CurrentConfigurationVersion=settings.ConfigurationVersion,
    @AttemptTotal=attempt.Total,@MinimumOrderTotal=settings.MinimumOrderTotal,
    @MaximumOrderTotal=settings.MaximumOrderTotal,
    @AttemptTermsVersion=attempt.TermsVersion,@CurrentTermsVersion=settings.TermsVersion,
    @AttemptPrivacyVersion=attempt.PrivacyVersion,@CurrentPrivacyVersion=settings.PrivacyVersion,
    @AttemptMerchantProfileKey=attempt.MerchantProfileKey,
    @CurrentMerchantProfileKey=settings.ActiveMerchantProfileKey,
    @IsEnabled=settings.IsEnabled,@IsPaused=settings.IsPaused,
    @PickupEnabled=settings.PickupEnabled,@GatewayEnvironment=settings.GatewayEnvironment,
    @GatewayCredentialsConfigured=settings.GatewayCredentialsConfigured,
    @GatewayWebhookConfigured=settings.GatewayWebhookConfigured,
    @GatewayReadinessAtUtc=settings.GatewayReadinessAtUtc,
    @ProcessorHeartbeatAtUtc=settings.ProcessorHeartbeatAtUtc,
    @WeeklyScheduleJson=settings.WeeklyScheduleJson,@TimeZoneId=restaurantSite.TimeZoneId
  FROM restaurante.OnlineCheckoutAttempt attempt WITH(UPDLOCK,HOLDLOCK)
  JOIN restaurante.OnlineOrderingSettings settings WITH(UPDLOCK,HOLDLOCK)
    ON settings.PublicSiteId=attempt.PublicSiteId
   AND settings.Rfc=attempt.Rfc
   AND settings.SiteId=attempt.SiteId
  JOIN restaurante.Site restaurantSite
    ON restaurantSite.Rfc=settings.Rfc AND restaurantSite.Id=settings.SiteId
  WHERE attempt.Id=@Id AND attempt.PublicSiteId=@PublicSiteId
    AND attempt.Rfc=@Rfc AND attempt.PayPalOrderId=@PayPalOrderId;

  IF @AttemptFound=0
     OR @AttemptState NOT IN(''PayPalCreated'',''CapturePending'')
     OR @QuoteExpiresAtUtc<=@Now
     OR @AttemptConfigurationVersion<>@CurrentConfigurationVersion
     OR @IsEnabled=0 OR @IsPaused=1 OR @PickupEnabled=0
     OR @AttemptTotal NOT BETWEEN @MinimumOrderTotal AND @MaximumOrderTotal
     OR @AttemptTermsVersion<>@CurrentTermsVersion
     OR @AttemptPrivacyVersion<>@CurrentPrivacyVersion
     OR @AttemptMerchantProfileKey<>@CurrentMerchantProfileKey
     OR @GatewayEnvironment<>@RequiredEnvironment
     OR @GatewayCredentialsConfigured=0 OR @GatewayWebhookConfigured=0
     OR @GatewayReadinessAtUtc IS NULL
     OR @GatewayReadinessAtUtc<DATEADD(MINUTE,-5,@Now)
     OR @ProcessorHeartbeatAtUtc IS NULL
     OR @ProcessorHeartbeatAtUtc<DATEADD(SECOND,-@ProcessorHeartbeatMaxAgeSeconds,@Now)
     OR NOT EXISTS
       (SELECT 1 FROM restaurante.OnlineOrderProduct productFlag
        WHERE productFlag.PublicSiteId=@PublicSiteId AND productFlag.Rfc=@Rfc
          AND productFlag.IsEnabled=1)
  BEGIN
    ROLLBACK TRANSACTION;
    THROW 53658,''La captura esta bloqueada por estado, vigencia, configuracion o readiness.'',1;
  END;

  BEGIN TRY
    SET @LocalNow=CONVERT(datetime2(0),(@Now AT TIME ZONE ''UTC'') AT TIME ZONE @TimeZoneId);
  END TRY
  BEGIN CATCH
    IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
    THROW 53658,''La zona horaria impide validar el horario de captura.'',1;
  END CATCH;
  SET @LocalTime=CONVERT(time(0),@LocalNow);
  SET @DayIndex=((DATEDIFF(DAY,CONVERT(date,''19000101''),CONVERT(date,@LocalNow))%7)+7)%7;
  SET @TodayKey=CASE @DayIndex
    WHEN 0 THEN ''Monday'' WHEN 1 THEN ''Tuesday'' WHEN 2 THEN ''Wednesday''
    WHEN 3 THEN ''Thursday'' WHEN 4 THEN ''Friday'' WHEN 5 THEN ''Saturday''
    ELSE ''Sunday'' END;
  SET @PreviousKey=CASE @DayIndex
    WHEN 0 THEN ''Sunday'' WHEN 1 THEN ''Monday'' WHEN 2 THEN ''Tuesday''
    WHEN 3 THEN ''Wednesday'' WHEN 4 THEN ''Thursday'' WHEN 5 THEN ''Friday''
    ELSE ''Saturday'' END;

  IF EXISTS
  (
    SELECT 1
    FROM OPENJSON(@WeeklyScheduleJson,CONCAT(''$.'',@TodayKey))
    WITH(Opens varchar(5) ''$.opens'',Closes varchar(5) ''$.closes'') slotInfo
    CROSS APPLY
    (
      SELECT TRY_CONVERT(time(0),slotInfo.Opens) OpensAt,
        TRY_CONVERT(time(0),slotInfo.Closes) ClosesAt
    ) parsed
    WHERE parsed.OpensAt IS NOT NULL AND parsed.ClosesAt IS NOT NULL
      AND
      (
        (parsed.OpensAt<parsed.ClosesAt
         AND @LocalTime>=parsed.OpensAt AND @LocalTime<parsed.ClosesAt)
        OR (parsed.OpensAt>parsed.ClosesAt AND @LocalTime>=parsed.OpensAt)
      )
  )
    SET @IsOpen=1;
  ELSE IF EXISTS
  (
    SELECT 1
    FROM OPENJSON(@WeeklyScheduleJson,CONCAT(''$.'',@PreviousKey))
    WITH(Opens varchar(5) ''$.opens'',Closes varchar(5) ''$.closes'') slotInfo
    CROSS APPLY
    (
      SELECT TRY_CONVERT(time(0),slotInfo.Opens) OpensAt,
        TRY_CONVERT(time(0),slotInfo.Closes) ClosesAt
    ) parsed
    WHERE parsed.OpensAt IS NOT NULL AND parsed.ClosesAt IS NOT NULL
      AND parsed.OpensAt>parsed.ClosesAt AND @LocalTime<parsed.ClosesAt
  )
    SET @IsOpen=1;

  IF @IsOpen=0
  BEGIN
    ROLLBACK TRANSACTION;
    THROW 53658,''La captura esta fuera del horario vigente de pedidos.'',1;
  END;

  UPDATE restaurante.OnlineCheckoutAttempt
  SET [State]=''CapturePending'',NextRetryAtUtc=DATEADD(SECOND,30,@Now),
      FailureCode=NULL,FailureMessage=NULL,UpdatedAtUtc=@Now
  WHERE Id=@Id AND PublicSiteId=@PublicSiteId AND Rfc=@Rfc;

  COMMIT TRANSACTION;

  SELECT attempt.Id,attempt.PayPalOrderId,attempt.MerchantProfileKey,
    attempt.QuoteFingerprint,attempt.Total,attempt.CurrencyCode,attempt.[State],
    attempt.SettingsConfigurationVersion,attempt.QuoteExpiresAtUtc,
    attempt.TermsVersion,attempt.PrivacyVersion,
    attempt.UpdatedAtUtc,attempt.RowVersion
  FROM restaurante.OnlineCheckoutAttempt attempt
  WHERE attempt.Id=@Id AND attempt.PublicSiteId=@PublicSiteId AND attempt.Rfc=@Rfc;
END;');

  EXEC(N'
CREATE OR ALTER PROCEDURE restaurante.PaymentGatewayEventRecord
  @MerchantProfileKey varchar(80),
  @ProviderEventId varchar(100),
  @EventType varchar(100),
  @ResourceType varchar(80)=NULL,
  @ResourceId varchar(100)=NULL,
  @RelatedOrderId varchar(64)=NULL,
  @RelatedCaptureId varchar(64)=NULL,
  @RelatedRefundId varchar(64)=NULL,
  @PayloadHash char(64),
  @VerificationStatus varchar(20)=''Verified''
AS
BEGIN
  SET NOCOUNT ON;
  SET XACT_ABORT ON;

  DECLARE @PublicSiteId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N''OrionERP.PublicSiteId''));
  DECLARE @Rfc varchar(50)=CONVERT(varchar(50),SESSION_CONTEXT(N''OrionRfc''));
  DECLARE @CheckoutAttemptId uniqueidentifier;
  DECLARE @SiteId int;
  DECLARE @EventId bigint;
  DECLARE @WasInserted bit=0;
  DECLARE @Matches TABLE
  (
    CheckoutAttemptId uniqueidentifier NOT NULL PRIMARY KEY,
    SiteId int NOT NULL
  );

  SET @MerchantProfileKey=NULLIF(LTRIM(RTRIM(@MerchantProfileKey)),'''');
  SET @ProviderEventId=NULLIF(LTRIM(RTRIM(@ProviderEventId)),'''');
  SET @EventType=NULLIF(LTRIM(RTRIM(@EventType)),'''');
  SET @ResourceType=NULLIF(LTRIM(RTRIM(@ResourceType)),'''');
  SET @ResourceId=NULLIF(LTRIM(RTRIM(@ResourceId)),'''');
  SET @RelatedOrderId=NULLIF(LTRIM(RTRIM(@RelatedOrderId)),'''');
  SET @RelatedCaptureId=NULLIF(LTRIM(RTRIM(@RelatedCaptureId)),'''');
  SET @RelatedRefundId=NULLIF(LTRIM(RTRIM(@RelatedRefundId)),'''');

  IF @PublicSiteId IS NULL OR NULLIF(@Rfc,'''') IS NULL
    THROW 53654,''No existe un contexto publico de restaurante verificado.'',1;
  IF @VerificationStatus<>''Verified''
    THROW 53655,''Solo se persisten webhooks verificados.'',1;
  IF @ProviderEventId IS NULL OR @EventType IS NULL OR @MerchantProfileKey IS NULL
     OR LEN(@PayloadHash)<>64
     OR @PayloadHash LIKE ''%[^0-9A-F]%'' COLLATE Latin1_General_100_BIN2
    THROW 53656,''Los metadatos del webhook son invalidos.'',1;

  INSERT @Matches(CheckoutAttemptId,SiteId)
  SELECT DISTINCT attempt.Id,attempt.SiteId
  FROM restaurante.OnlineCheckoutAttempt attempt
  LEFT JOIN restaurante.PaymentGatewayTransaction transactionInfo
    ON transactionInfo.PublicSiteId=attempt.PublicSiteId
   AND transactionInfo.Rfc=attempt.Rfc
   AND transactionInfo.SiteId=attempt.SiteId
   AND transactionInfo.CheckoutAttemptId=attempt.Id
  LEFT JOIN restaurante.PaymentGatewayRefund refundInfo
    ON refundInfo.PublicSiteId=transactionInfo.PublicSiteId
   AND refundInfo.Rfc=transactionInfo.Rfc
   AND refundInfo.SiteId=transactionInfo.SiteId
   AND refundInfo.GatewayTransactionId=transactionInfo.Id
  WHERE attempt.PublicSiteId=@PublicSiteId
    AND attempt.Rfc=@Rfc
    AND attempt.MerchantProfileKey=@MerchantProfileKey
    AND
    (
      (@RelatedOrderId IS NOT NULL AND attempt.PayPalOrderId=@RelatedOrderId)
      OR (@RelatedCaptureId IS NOT NULL AND transactionInfo.ProviderCaptureId=@RelatedCaptureId)
      OR (@RelatedRefundId IS NOT NULL AND refundInfo.ProviderRefundId=@RelatedRefundId)
      OR (@ResourceId IS NOT NULL AND
          (attempt.PayPalOrderId=@ResourceId OR transactionInfo.ProviderCaptureId=@ResourceId
           OR refundInfo.ProviderRefundId=@ResourceId))
    );

  IF (SELECT COUNT(*) FROM @Matches)>1
    THROW 53761,''El webhook coincide con mas de un checkout local.'',1;
  SELECT @CheckoutAttemptId=CheckoutAttemptId,@SiteId=SiteId FROM @Matches;
  IF @CheckoutAttemptId IS NULL
  BEGIN
    SELECT CONVERT(bit,0) WasMatched,CONVERT(bit,0) WasInserted,
      CONVERT(bigint,NULL) EventId,CONVERT(uniqueidentifier,NULL) CheckoutAttemptId,
      CONVERT(varchar(20),NULL) ProcessingStatus;
    RETURN;
  END;

  BEGIN TRANSACTION;

  SELECT @EventId=eventInfo.Id
  FROM restaurante.PaymentGatewayEvent eventInfo WITH(UPDLOCK,HOLDLOCK)
  WHERE eventInfo.MerchantProfileKey=@MerchantProfileKey
    AND eventInfo.ProviderEventId=@ProviderEventId;

  IF @EventId IS NULL
  BEGIN
    INSERT restaurante.PaymentGatewayEvent
    (
      PublicSiteId,Rfc,SiteId,Provider,MerchantProfileKey,ProviderEventId,
      EventType,ResourceType,ResourceId,RelatedOrderId,RelatedCaptureId,
      RelatedRefundId,CheckoutAttemptId,PayloadHash,VerificationStatus
    )
    VALUES
    (
      @PublicSiteId,@Rfc,@SiteId,''PayPal'',@MerchantProfileKey,@ProviderEventId,
      @EventType,@ResourceType,@ResourceId,@RelatedOrderId,@RelatedCaptureId,
      @RelatedRefundId,@CheckoutAttemptId,@PayloadHash,@VerificationStatus
    );
    SET @EventId=SCOPE_IDENTITY();
    SET @WasInserted=1;
  END
  ELSE IF EXISTS
  (
    SELECT 1 FROM restaurante.PaymentGatewayEvent
    WHERE Id=@EventId AND PayloadHash<>@PayloadHash
  )
  BEGIN
    ROLLBACK TRANSACTION;
    THROW 53657,''El event ID de PayPal se repitio con otro payload.'',1;
  END;

  COMMIT TRANSACTION;

  SELECT CONVERT(bit,1) WasMatched,@WasInserted WasInserted,
    eventInfo.Id EventId,@CheckoutAttemptId CheckoutAttemptId,eventInfo.ProcessingStatus
  FROM restaurante.PaymentGatewayEvent eventInfo
  WHERE eventInfo.Id=@EventId;
END;');

  EXEC(N'
CREATE OR ALTER PROCEDURE restaurante.PaymentGatewayRefundEventBind
  @EventId bigint,
  @LeaseId uniqueidentifier,
  @ProviderRefundId varchar(64),
  @ProviderCaptureId varchar(64),
  @Amount decimal(18,2),
  @CurrencyCode char(3)
AS
BEGIN
  SET NOCOUNT ON;
  SET XACT_ABORT ON;

  DECLARE @PublicSiteId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N''OrionERP.PublicSiteId''));
  DECLARE @Rfc varchar(50)=CONVERT(varchar(50),SESSION_CONTEXT(N''OrionRfc''));
  DECLARE @EventType varchar(100);
  DECLARE @ResourceType varchar(80);
  DECLARE @ResourceId varchar(100);
  DECLARE @RelatedOrderId varchar(64);
  DECLARE @RelatedCaptureId varchar(64);
  DECLARE @RelatedRefundId varchar(64);
  DECLARE @EventCheckoutAttemptId uniqueidentifier;
  DECLARE @RefundId uniqueidentifier;
  DECLARE @ExistingProviderRefundId varchar(64);
  DECLARE @RefundStatus varchar(30);
  DECLARE @KnownCheckoutAttemptId uniqueidentifier;
  DECLARE @KnownCaptureId varchar(64);
  DECLARE @KnownOrderId varchar(64);
  DECLARE @KnownAmount decimal(18,2);
  DECLARE @KnownCurrencyCode char(3);
  DECLARE @WasBound bit=0;
  DECLARE @CandidateCount int;
  DECLARE @Candidates TABLE
  (
    RefundId uniqueidentifier NOT NULL PRIMARY KEY,
    ProviderRefundId varchar(64) NULL,
    RefundStatus varchar(30) NOT NULL
  );

  SET @ProviderRefundId=NULLIF(LTRIM(RTRIM(@ProviderRefundId)),'''');
  SET @ProviderCaptureId=NULLIF(LTRIM(RTRIM(@ProviderCaptureId)),'''');
  SET @CurrencyCode=UPPER(@CurrencyCode);
  IF @PublicSiteId IS NULL OR NULLIF(@Rfc,'''') IS NULL
    THROW 53762,''No existe un contexto publico de restaurante verificado.'',1;
  IF @EventId IS NULL OR @LeaseId IS NULL OR @ProviderRefundId IS NULL
     OR @ProviderCaptureId IS NULL OR @Amount<=0
     OR LEN(@CurrencyCode)<>3 OR @CurrencyCode LIKE ''%[^A-Z]%'' COLLATE Latin1_General_100_BIN2
    THROW 53763,''Los datos verificados del reembolso son invalidos.'',1;

  BEGIN TRANSACTION;

  SELECT @EventType=eventInfo.EventType,@ResourceType=eventInfo.ResourceType,
    @ResourceId=eventInfo.ResourceId,@RelatedOrderId=eventInfo.RelatedOrderId,
    @RelatedCaptureId=eventInfo.RelatedCaptureId,@RelatedRefundId=eventInfo.RelatedRefundId,
    @EventCheckoutAttemptId=eventInfo.CheckoutAttemptId
  FROM restaurante.PaymentGatewayEvent eventInfo WITH(UPDLOCK,HOLDLOCK)
  WHERE eventInfo.Id=@EventId AND eventInfo.PublicSiteId=@PublicSiteId
    AND eventInfo.Rfc=@Rfc AND eventInfo.VerificationStatus=''Verified''
    AND eventInfo.ProcessingStatus=''Processing'' AND eventInfo.LeaseId=@LeaseId;

  IF @EventCheckoutAttemptId IS NULL
  BEGIN
    ROLLBACK TRANSACTION;
    THROW 53764,''La concesion del webhook ya no es valida.'',1;
  END;

  IF @EventType NOT LIKE ''PAYMENT.REFUND.%''
     OR LOWER(COALESCE(@ResourceType,''''))<>''refund''
     OR @RelatedCaptureId IS NULL
     OR (@RelatedRefundId IS NULL AND @ResourceId IS NULL)
  BEGIN
    COMMIT TRANSACTION;
    SELECT CONVERT(varchar(20),''Unsupported'') MatchOutcome,
      CONVERT(uniqueidentifier,NULL) RefundId,CONVERT(bit,0) WasBound,
      CONVERT(varchar(30),NULL) RefundStatus,CONVERT(varchar(64),NULL) ProviderRefundId;
    RETURN;
  END;

  IF @RelatedCaptureId<>@ProviderCaptureId
     OR COALESCE(@RelatedRefundId,@ResourceId)<>@ProviderRefundId
     OR (@RelatedRefundId IS NOT NULL AND @ResourceId IS NOT NULL
         AND @RelatedRefundId<>@ResourceId)
  BEGIN
    COMMIT TRANSACTION;
    SELECT CONVERT(varchar(20),''Conflict'') MatchOutcome,
      CONVERT(uniqueidentifier,NULL) RefundId,CONVERT(bit,0) WasBound,
      CONVERT(varchar(30),NULL) RefundStatus,CONVERT(varchar(64),NULL) ProviderRefundId;
    RETURN;
  END;

  SELECT @RefundId=refundInfo.Id,@RefundStatus=refundInfo.[Status],
    @KnownCheckoutAttemptId=transactionInfo.CheckoutAttemptId,
    @KnownCaptureId=transactionInfo.ProviderCaptureId,
    @KnownOrderId=transactionInfo.ProviderOrderId,
    @KnownAmount=refundInfo.Amount,@KnownCurrencyCode=refundInfo.CurrencyCode
  FROM restaurante.PaymentGatewayRefund refundInfo WITH(UPDLOCK,HOLDLOCK)
  JOIN restaurante.PaymentGatewayTransaction transactionInfo
    ON transactionInfo.PublicSiteId=refundInfo.PublicSiteId
   AND transactionInfo.Rfc=refundInfo.Rfc
   AND transactionInfo.SiteId=refundInfo.SiteId
   AND transactionInfo.Id=refundInfo.GatewayTransactionId
  WHERE refundInfo.PublicSiteId=@PublicSiteId AND refundInfo.Rfc=@Rfc
    AND refundInfo.ProviderRefundId=@ProviderRefundId;

  IF @RefundId IS NOT NULL
  BEGIN
    IF @KnownCheckoutAttemptId=@EventCheckoutAttemptId
       AND @KnownCaptureId=@ProviderCaptureId
       AND (@RelatedOrderId IS NULL OR @KnownOrderId=@RelatedOrderId)
       AND @KnownAmount=@Amount AND @KnownCurrencyCode=@CurrencyCode
    BEGIN
      COMMIT TRANSACTION;
      SELECT CONVERT(varchar(20),''Matched'') MatchOutcome,@RefundId RefundId,
        CONVERT(bit,0) WasBound,@RefundStatus RefundStatus,
        @ProviderRefundId ProviderRefundId;
      RETURN;
    END;

    COMMIT TRANSACTION;
    SELECT CONVERT(varchar(20),''Conflict'') MatchOutcome,@RefundId RefundId,
      CONVERT(bit,0) WasBound,@RefundStatus RefundStatus,
      @ProviderRefundId ProviderRefundId;
    RETURN;
  END;

  INSERT @Candidates(RefundId,ProviderRefundId,RefundStatus)
  SELECT refundInfo.Id,refundInfo.ProviderRefundId,refundInfo.[Status]
  FROM restaurante.PaymentGatewayRefund refundInfo WITH(UPDLOCK,HOLDLOCK)
  JOIN restaurante.PaymentGatewayTransaction transactionInfo
    ON transactionInfo.PublicSiteId=refundInfo.PublicSiteId
   AND transactionInfo.Rfc=refundInfo.Rfc
   AND transactionInfo.SiteId=refundInfo.SiteId
   AND transactionInfo.Id=refundInfo.GatewayTransactionId
  WHERE refundInfo.PublicSiteId=@PublicSiteId AND refundInfo.Rfc=@Rfc
    AND refundInfo.LocalRefundId IS NULL
    AND transactionInfo.CheckoutAttemptId=@EventCheckoutAttemptId
    AND transactionInfo.ProviderCaptureId=@ProviderCaptureId
    AND (@RelatedOrderId IS NULL OR transactionInfo.ProviderOrderId=@RelatedOrderId)
    AND refundInfo.Amount=@Amount AND refundInfo.CurrencyCode=@CurrencyCode;

  SELECT @CandidateCount=COUNT(*) FROM @Candidates;
  IF @CandidateCount=0
  BEGIN
    COMMIT TRANSACTION;
    SELECT CONVERT(varchar(20),''Unmatched'') MatchOutcome,
      CONVERT(uniqueidentifier,NULL) RefundId,CONVERT(bit,0) WasBound,
      CONVERT(varchar(30),NULL) RefundStatus,CONVERT(varchar(64),NULL) ProviderRefundId;
    RETURN;
  END;
  IF @CandidateCount>1
  BEGIN
    COMMIT TRANSACTION;
    SELECT CONVERT(varchar(20),''Ambiguous'') MatchOutcome,
      CONVERT(uniqueidentifier,NULL) RefundId,CONVERT(bit,0) WasBound,
      CONVERT(varchar(30),NULL) RefundStatus,CONVERT(varchar(64),NULL) ProviderRefundId;
    RETURN;
  END;

  SELECT @RefundId=RefundId,@ExistingProviderRefundId=ProviderRefundId,
    @RefundStatus=RefundStatus FROM @Candidates;
  IF (@ExistingProviderRefundId IS NOT NULL AND @ExistingProviderRefundId<>@ProviderRefundId)
     OR EXISTS
     (
       SELECT 1 FROM restaurante.PaymentGatewayRefund refundInfo WITH(UPDLOCK,HOLDLOCK)
       WHERE refundInfo.PublicSiteId=@PublicSiteId
         AND refundInfo.ProviderRefundId=@ProviderRefundId AND refundInfo.Id<>@RefundId
     )
  BEGIN
    COMMIT TRANSACTION;
    SELECT CONVERT(varchar(20),''Conflict'') MatchOutcome,
      @RefundId RefundId,CONVERT(bit,0) WasBound,@RefundStatus RefundStatus,
      @ExistingProviderRefundId ProviderRefundId;
    RETURN;
  END;

  IF @ExistingProviderRefundId IS NULL
  BEGIN
    UPDATE restaurante.PaymentGatewayRefund
    SET ProviderRefundId=@ProviderRefundId,
        NextRetryAtUtc=CASE WHEN [Status] IN(''Requested'',''Pending'',''Failed'')
          THEN SYSUTCDATETIME() ELSE NextRetryAtUtc END,
        UpdatedAtUtc=SYSUTCDATETIME()
    WHERE Id=@RefundId AND PublicSiteId=@PublicSiteId AND Rfc=@Rfc
      AND ProviderRefundId IS NULL AND LocalRefundId IS NULL;
    IF @@ROWCOUNT<>1
    BEGIN
      ROLLBACK TRANSACTION;
      THROW 53765,''El reembolso cambio durante la conciliacion del webhook.'',1;
    END;
    SET @WasBound=1;
  END;

  COMMIT TRANSACTION;

  SELECT CONVERT(varchar(20),''Matched'') MatchOutcome,@RefundId RefundId,
    @WasBound WasBound,refundInfo.[Status] RefundStatus,
    refundInfo.ProviderRefundId
  FROM restaurante.PaymentGatewayRefund refundInfo
  WHERE refundInfo.Id=@RefundId AND refundInfo.PublicSiteId=@PublicSiteId
    AND refundInfo.Rfc=@Rfc;
END;');

  EXEC(N'
CREATE OR ALTER PROCEDURE restaurante.PayPalRecoveryClaim
  @LeaseId uniqueidentifier,
  @LeaseSeconds int=90
AS
BEGIN
  SET NOCOUNT ON;
  SET XACT_ABORT ON;

  DECLARE @PublicSiteId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N''OrionERP.PublicSiteId''));
  DECLARE @Rfc varchar(50)=CONVERT(varchar(50),SESSION_CONTEXT(N''OrionRfc''));
  DECLARE @ClaimedAttemptId uniqueidentifier;
  DECLARE @ClaimedEventId bigint;
  DECLARE @EventClaim TABLE(EventId bigint NOT NULL,CheckoutAttemptId uniqueidentifier NOT NULL);
  DECLARE @AttemptClaim TABLE(CheckoutAttemptId uniqueidentifier NOT NULL);

  IF @PublicSiteId IS NULL OR NULLIF(@Rfc,'''') IS NULL
    THROW 53687,''No existe un contexto publico de restaurante verificado.'',1;
  IF @LeaseId IS NULL OR @LeaseSeconds NOT BETWEEN 15 AND 600
    THROW 53688,''La concesion de recuperacion es invalida.'',1;

  BEGIN TRANSACTION;

  ;WITH nextEvent AS
  (
    SELECT TOP(1) eventInfo.*
    FROM restaurante.PaymentGatewayEvent eventInfo WITH(UPDLOCK,READPAST,ROWLOCK)
    WHERE eventInfo.PublicSiteId=@PublicSiteId AND eventInfo.Rfc=@Rfc
      AND eventInfo.VerificationStatus=''Verified''
      AND
      (
        eventInfo.ProcessingStatus IN(''Pending'',''Failed'')
        OR
        (
          eventInfo.ProcessingStatus=''Processing''
          AND eventInfo.LeaseExpiresAtUtc<SYSUTCDATETIME()
        )
      )
      AND (eventInfo.NextRetryAtUtc IS NULL OR eventInfo.NextRetryAtUtc<=SYSUTCDATETIME())
      AND (eventInfo.LeaseExpiresAtUtc IS NULL OR eventInfo.LeaseExpiresAtUtc<SYSUTCDATETIME())
    ORDER BY eventInfo.ReceivedAtUtc,eventInfo.Id
  )
  UPDATE nextEvent
  SET ProcessingStatus=''Processing'',Attempts=Attempts+1,
      LeaseId=@LeaseId,LeaseExpiresAtUtc=DATEADD(SECOND,@LeaseSeconds,SYSUTCDATETIME()),
      NextRetryAtUtc=NULL,UpdatedAtUtc=SYSUTCDATETIME()
  OUTPUT inserted.Id,inserted.CheckoutAttemptId
    INTO @EventClaim(EventId,CheckoutAttemptId);

  SELECT TOP(1) @ClaimedEventId=EventId,@ClaimedAttemptId=CheckoutAttemptId
  FROM @EventClaim;

  IF @ClaimedEventId IS NULL
  BEGIN
    ;WITH nextCapture AS
    (
      SELECT TOP(1) attempt.*
      FROM restaurante.OnlineCheckoutAttempt attempt WITH(UPDLOCK,READPAST,ROWLOCK)
      WHERE attempt.PublicSiteId=@PublicSiteId AND attempt.Rfc=@Rfc
        AND attempt.[State]=''CapturePending''
        AND (attempt.NextRetryAtUtc IS NULL OR attempt.NextRetryAtUtc<=SYSUTCDATETIME())
        AND (attempt.RecoveryLeaseExpiresAtUtc IS NULL OR attempt.RecoveryLeaseExpiresAtUtc<SYSUTCDATETIME())
      ORDER BY attempt.UpdatedAtUtc,attempt.Id
    )
    UPDATE nextCapture
    SET RecoveryAttempts=RecoveryAttempts+1,RecoveryLeaseId=@LeaseId,
        RecoveryLeaseExpiresAtUtc=DATEADD(SECOND,@LeaseSeconds,SYSUTCDATETIME()),
        NextRetryAtUtc=NULL,UpdatedAtUtc=SYSUTCDATETIME()
    OUTPUT inserted.Id INTO @AttemptClaim(CheckoutAttemptId);

    SELECT TOP(1) @ClaimedAttemptId=CheckoutAttemptId FROM @AttemptClaim;
  END;

  COMMIT TRANSACTION;

  SELECT TOP(1)
    CONVERT(varchar(20),CASE WHEN @ClaimedEventId IS NULL THEN ''Capture'' ELSE ''Event'' END) WorkType,
    attempt.Id CheckoutAttemptId,eventInfo.Id EventId,eventInfo.EventType,
    eventInfo.ResourceType,eventInfo.ResourceId,eventInfo.RelatedOrderId,
    eventInfo.RelatedCaptureId,eventInfo.RelatedRefundId,
    attempt.PayPalOrderId,attempt.PayPalCaptureId,
    attempt.MerchantProfileKey,attempt.QuoteFingerprint,attempt.Total,attempt.CurrencyCode,
    attempt.[State],attempt.RecoveryAttempts,@LeaseId LeaseId,
    COALESCE(eventInfo.LeaseExpiresAtUtc,attempt.RecoveryLeaseExpiresAtUtc) LeaseExpiresAtUtc
  FROM restaurante.OnlineCheckoutAttempt attempt
  LEFT JOIN restaurante.PaymentGatewayEvent eventInfo
    ON eventInfo.Id=@ClaimedEventId AND eventInfo.CheckoutAttemptId=attempt.Id
  WHERE attempt.PublicSiteId=@PublicSiteId AND attempt.Rfc=@Rfc
    AND attempt.Id=@ClaimedAttemptId;
END;');

  EXEC(N'
CREATE OR ALTER PROCEDURE restaurante.PaymentGatewayRefundClaim
  @LeaseId uniqueidentifier,
  @LeaseSeconds int=90
AS
BEGIN
  SET NOCOUNT ON;
  SET XACT_ABORT ON;

  DECLARE @PublicSiteId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N''OrionERP.PublicSiteId''));
  DECLARE @Rfc varchar(50)=CONVERT(varchar(50),SESSION_CONTEXT(N''OrionRfc''));
  DECLARE @Claimed TABLE(Id uniqueidentifier NOT NULL);

  IF @PublicSiteId IS NULL OR NULLIF(@Rfc,'''') IS NULL
    THROW 53694,''No existe un contexto publico de restaurante verificado.'',1;
  IF @LeaseId IS NULL OR @LeaseSeconds NOT BETWEEN 15 AND 600
    THROW 53695,''La concesion de reembolso es invalida.'',1;

  BEGIN TRANSACTION;
  ;WITH nextRefund AS
  (
    SELECT TOP(1) refundInfo.*
    FROM restaurante.PaymentGatewayRefund refundInfo WITH(UPDLOCK,READPAST,ROWLOCK)
    WHERE refundInfo.PublicSiteId=@PublicSiteId AND refundInfo.Rfc=@Rfc
      AND
      (
        refundInfo.[Status] IN(''Requested'',''Pending'',''Failed'')
        OR
        (
          refundInfo.[Status]=''Processing''
          AND refundInfo.LeaseExpiresAtUtc<SYSUTCDATETIME()
        )
      )
      AND (refundInfo.NextRetryAtUtc IS NULL OR refundInfo.NextRetryAtUtc<=SYSUTCDATETIME())
      AND (refundInfo.LeaseExpiresAtUtc IS NULL OR refundInfo.LeaseExpiresAtUtc<SYSUTCDATETIME())
    ORDER BY refundInfo.RequestedAtUtc,refundInfo.Id
  )
  UPDATE nextRefund
  SET [Status]=''Processing'',Attempts=Attempts+1,LeaseId=@LeaseId,
      LeaseExpiresAtUtc=DATEADD(SECOND,@LeaseSeconds,SYSUTCDATETIME()),
      NextRetryAtUtc=NULL,UpdatedAtUtc=SYSUTCDATETIME()
  OUTPUT inserted.Id INTO @Claimed(Id);

  UPDATE attempt
  SET [State]=''RefundPending'',UpdatedAtUtc=SYSUTCDATETIME()
  FROM restaurante.OnlineCheckoutAttempt attempt
  JOIN restaurante.PaymentGatewayTransaction transactionInfo
    ON transactionInfo.PublicSiteId=attempt.PublicSiteId
   AND transactionInfo.Rfc=attempt.Rfc
   AND transactionInfo.SiteId=attempt.SiteId
   AND transactionInfo.CheckoutAttemptId=attempt.Id
  JOIN restaurante.PaymentGatewayRefund refundInfo
    ON refundInfo.PublicSiteId=transactionInfo.PublicSiteId
   AND refundInfo.Rfc=transactionInfo.Rfc
   AND refundInfo.SiteId=transactionInfo.SiteId
   AND refundInfo.GatewayTransactionId=transactionInfo.Id
  JOIN @Claimed claimed ON claimed.Id=refundInfo.Id;
  COMMIT TRANSACTION;

  SELECT refundInfo.Id,refundInfo.GatewayTransactionId,refundInfo.Amount,
    refundInfo.CurrencyCode,refundInfo.Reason,refundInfo.IdempotencyKey,
    refundInfo.ProviderRefundId,refundInfo.ProviderGrossAmount,
    refundInfo.ProviderFeeAmount,refundInfo.ProviderNetAmount,
    refundInfo.ReconciledAtUtc,
    refundInfo.RequestedBy,refundInfo.AuthorizedBy,refundInfo.Attempts,
    refundInfo.LeaseId,refundInfo.LeaseExpiresAtUtc,
    transactionInfo.ProviderCaptureId,transactionInfo.ProviderOrderId,
    transactionInfo.MerchantProfileKey,transactionInfo.GrossAmount,
    transactionInfo.LocalPaymentId,attempt.Id CheckoutAttemptId,
    attempt.RestaurantOrderId
  FROM @Claimed claimed
  JOIN restaurante.PaymentGatewayRefund refundInfo ON refundInfo.Id=claimed.Id
  JOIN restaurante.PaymentGatewayTransaction transactionInfo
    ON transactionInfo.PublicSiteId=refundInfo.PublicSiteId
   AND transactionInfo.Rfc=refundInfo.Rfc
   AND transactionInfo.SiteId=refundInfo.SiteId
   AND transactionInfo.Id=refundInfo.GatewayTransactionId
  JOIN restaurante.OnlineCheckoutAttempt attempt
    ON attempt.PublicSiteId=transactionInfo.PublicSiteId
   AND attempt.Rfc=transactionInfo.Rfc
   AND attempt.SiteId=transactionInfo.SiteId
   AND attempt.Id=transactionInfo.CheckoutAttemptId;
END;');

  EXEC(N'
CREATE OR ALTER PROCEDURE restaurante.PaymentGatewayRefundResult
  @Id uniqueidentifier,
  @LeaseId uniqueidentifier,
  @Outcome varchar(20),
  @ProviderRefundId varchar(64)=NULL,
  @ProviderGrossAmount decimal(18,2)=NULL,
  @ProviderFeeAmount decimal(18,2)=NULL,
  @ProviderNetAmount decimal(18,2)=NULL,
  @ReconciledAtUtc datetime2(3)=NULL,
  @FailureCode varchar(80)=NULL,
  @FailureMessage nvarchar(500)=NULL,
  @NextRetryAtUtc datetime2(3)=NULL
AS
BEGIN
  SET NOCOUNT ON;
  SET XACT_ABORT ON;

  DECLARE @PublicSiteId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N''OrionERP.PublicSiteId''));
  DECLARE @Rfc varchar(50)=CONVERT(varchar(50),SESSION_CONTEXT(N''OrionRfc''));
  DECLARE @TransactionId uniqueidentifier;
  DECLARE @CheckoutAttemptId uniqueidentifier;
  DECLARE @GrossAmount decimal(18,2);
  DECLARE @RefundAmount decimal(18,2);
  DECLARE @LocalPaymentId uniqueidentifier;
  DECLARE @CompletedAmount decimal(18,2);
  DECLARE @StoredProviderGrossAmount decimal(18,2);
  DECLARE @StoredProviderFeeAmount decimal(18,2);
  DECLARE @StoredProviderNetAmount decimal(18,2);

  SET @ProviderRefundId=NULLIF(LTRIM(RTRIM(@ProviderRefundId)),'''');
  IF @PublicSiteId IS NULL OR NULLIF(@Rfc,'''') IS NULL
    THROW 53696,''No existe un contexto publico de restaurante verificado.'',1;
  IF @Outcome NOT IN(''Completed'',''Pending'',''Failed'')
    THROW 53697,''El resultado del reembolso es invalido.'',1;
  IF @Outcome=''Completed'' AND @ProviderRefundId IS NULL
    THROW 53698,''ProviderRefundId es obligatorio para un reembolso completado por PayPal.'',1;
  IF
  (
    (@ProviderGrossAmount IS NULL AND
      (@ProviderFeeAmount IS NOT NULL OR @ProviderNetAmount IS NOT NULL OR @ReconciledAtUtc IS NOT NULL))
    OR (@ProviderGrossAmount IS NOT NULL AND
      (@ProviderFeeAmount IS NULL OR @ProviderNetAmount IS NULL OR @ReconciledAtUtc IS NULL))
    OR (@ProviderGrossAmount IS NOT NULL AND
      (@ProviderGrossAmount<=0 OR @ProviderFeeAmount<0 OR @ProviderNetAmount<0
       OR @ProviderGrossAmount<>@ProviderFeeAmount+@ProviderNetAmount))
  )
    THROW 53766,''El desglose financiero del reembolso es invalido.'',1;

  BEGIN TRANSACTION;

  SELECT @TransactionId=refundInfo.GatewayTransactionId,
    @CheckoutAttemptId=transactionInfo.CheckoutAttemptId,
    @GrossAmount=transactionInfo.GrossAmount,
    @RefundAmount=refundInfo.Amount,
    @LocalPaymentId=transactionInfo.LocalPaymentId,
    @StoredProviderGrossAmount=refundInfo.ProviderGrossAmount,
    @StoredProviderFeeAmount=refundInfo.ProviderFeeAmount,
    @StoredProviderNetAmount=refundInfo.ProviderNetAmount
  FROM restaurante.PaymentGatewayRefund refundInfo WITH(UPDLOCK,HOLDLOCK)
  JOIN restaurante.PaymentGatewayTransaction transactionInfo
    ON transactionInfo.PublicSiteId=refundInfo.PublicSiteId
   AND transactionInfo.Rfc=refundInfo.Rfc
   AND transactionInfo.SiteId=refundInfo.SiteId
   AND transactionInfo.Id=refundInfo.GatewayTransactionId
  WHERE refundInfo.Id=@Id AND refundInfo.PublicSiteId=@PublicSiteId AND refundInfo.Rfc=@Rfc
    AND refundInfo.[Status]=''Processing'' AND refundInfo.LeaseId=@LeaseId;

  IF @TransactionId IS NULL
  BEGIN
    ROLLBACK TRANSACTION;
    THROW 53699,''La concesion del reembolso ya no es valida.'',1;
  END;

  IF @ProviderRefundId IS NOT NULL AND EXISTS
  (
    SELECT 1 FROM restaurante.PaymentGatewayRefund
    WHERE Id=@Id AND ProviderRefundId IS NOT NULL AND ProviderRefundId<>@ProviderRefundId
  )
  BEGIN
    ROLLBACK TRANSACTION;
    THROW 53700,''El reembolso ya esta ligado a otro ID de PayPal.'',1;
  END;

  IF @ProviderGrossAmount IS NOT NULL
     AND
     (
       @ProviderGrossAmount<>@RefundAmount
       OR (@StoredProviderGrossAmount IS NOT NULL AND
          (@StoredProviderGrossAmount<>@ProviderGrossAmount
           OR @StoredProviderFeeAmount<>@ProviderFeeAmount
           OR @StoredProviderNetAmount<>@ProviderNetAmount))
     )
  BEGIN
    ROLLBACK TRANSACTION;
    THROW 53767,''El desglose de PayPal no coincide con el reembolso reservado.'',1;
  END;

  UPDATE restaurante.PaymentGatewayRefund
  SET [Status]=@Outcome,
      ProviderRefundId=CASE WHEN @ProviderRefundId IS NOT NULL
        THEN COALESCE(ProviderRefundId,@ProviderRefundId) ELSE ProviderRefundId END,
      ProviderGrossAmount=CASE WHEN @ProviderGrossAmount IS NOT NULL
        THEN COALESCE(ProviderGrossAmount,@ProviderGrossAmount) ELSE ProviderGrossAmount END,
      ProviderFeeAmount=CASE WHEN @ProviderGrossAmount IS NOT NULL
        THEN COALESCE(ProviderFeeAmount,@ProviderFeeAmount) ELSE ProviderFeeAmount END,
      ProviderNetAmount=CASE WHEN @ProviderGrossAmount IS NOT NULL
        THEN COALESCE(ProviderNetAmount,@ProviderNetAmount) ELSE ProviderNetAmount END,
      ReconciledAtUtc=CASE WHEN @ProviderGrossAmount IS NOT NULL
        THEN COALESCE(ReconciledAtUtc,@ReconciledAtUtc) ELSE ReconciledAtUtc END,
      LeaseId=NULL,LeaseExpiresAtUtc=NULL,
      NextRetryAtUtc=CASE WHEN @Outcome IN(''Pending'',''Failed'')
        THEN COALESCE(@NextRetryAtUtc,DATEADD(MINUTE,1,SYSUTCDATETIME())) ELSE NULL END,
      FailureCode=CASE WHEN @Outcome=''Failed'' THEN NULLIF(LTRIM(RTRIM(@FailureCode)),'''') ELSE NULL END,
      FailureMessage=CASE WHEN @Outcome=''Failed''
        THEN LEFT(NULLIF(LTRIM(RTRIM(@FailureMessage)),N''''),500) ELSE NULL END,
      CompletedAtUtc=CASE WHEN @Outcome=''Completed'' THEN COALESCE(CompletedAtUtc,SYSUTCDATETIME()) ELSE CompletedAtUtc END,
      UpdatedAtUtc=SYSUTCDATETIME()
  WHERE Id=@Id AND PublicSiteId=@PublicSiteId AND Rfc=@Rfc;

  SELECT @CompletedAmount=COALESCE(SUM(Amount),0)
  FROM restaurante.PaymentGatewayRefund
  WHERE PublicSiteId=@PublicSiteId AND Rfc=@Rfc
    AND GatewayTransactionId=@TransactionId AND [Status]=''Completed'';

  UPDATE restaurante.PaymentGatewayTransaction
  SET [Status]=CASE
      WHEN @CompletedAmount>=GrossAmount THEN ''Refunded''
      WHEN @CompletedAmount>0 THEN ''PartiallyRefunded''
      ELSE [Status] END,
      UpdatedAtUtc=SYSUTCDATETIME()
  WHERE Id=@TransactionId AND PublicSiteId=@PublicSiteId AND Rfc=@Rfc;

  UPDATE restaurante.OnlineCheckoutAttempt
  SET [State]=CASE
        WHEN @Outcome=''Completed'' AND @LocalPaymentId IS NULL AND @CompletedAmount>=@GrossAmount
          THEN ''Refunded''
        WHEN @Outcome IN(''Completed'',''Pending'',''Failed'') THEN ''RefundPending''
        ELSE [State] END,
      FailureCode=CASE WHEN @Outcome=''Failed'' THEN NULLIF(LTRIM(RTRIM(@FailureCode)),'''') ELSE FailureCode END,
      FailureMessage=CASE WHEN @Outcome=''Failed''
        THEN LEFT(NULLIF(LTRIM(RTRIM(@FailureMessage)),N''''),500) ELSE FailureMessage END,
      UpdatedAtUtc=SYSUTCDATETIME()
  WHERE Id=@CheckoutAttemptId AND PublicSiteId=@PublicSiteId AND Rfc=@Rfc;

  COMMIT TRANSACTION;

  SELECT refundInfo.Id,refundInfo.[Status],refundInfo.ProviderRefundId,
    refundInfo.LocalRefundId,refundInfo.CompletedAtUtc,refundInfo.NextRetryAtUtc,
    refundInfo.ProviderGrossAmount,refundInfo.ProviderFeeAmount,
    refundInfo.ProviderNetAmount,refundInfo.ReconciledAtUtc,
    transactionInfo.[Status] TransactionStatus,attempt.[State] CheckoutStatus
  FROM restaurante.PaymentGatewayRefund refundInfo
  JOIN restaurante.PaymentGatewayTransaction transactionInfo
    ON transactionInfo.PublicSiteId=refundInfo.PublicSiteId
   AND transactionInfo.Rfc=refundInfo.Rfc
   AND transactionInfo.SiteId=refundInfo.SiteId
   AND transactionInfo.Id=refundInfo.GatewayTransactionId
  JOIN restaurante.OnlineCheckoutAttempt attempt
    ON attempt.PublicSiteId=transactionInfo.PublicSiteId
   AND attempt.Rfc=transactionInfo.Rfc
   AND attempt.SiteId=transactionInfo.SiteId
   AND attempt.Id=transactionInfo.CheckoutAttemptId
  WHERE refundInfo.Id=@Id AND refundInfo.PublicSiteId=@PublicSiteId AND refundInfo.Rfc=@Rfc;
END;');

  EXEC(N'
CREATE OR ALTER PROCEDURE restaurante.PaymentGatewayRefundRequest
  @RestaurantOrderId uniqueidentifier,
  @Amount decimal(18,2),
  @Reason nvarchar(500),
  @IdempotencyKey varchar(100),
  @RequestedBy nvarchar(256),
  @AuthorizedBy nvarchar(256)
AS
BEGIN
  SET NOCOUNT ON;
  SET XACT_ABORT ON;

  DECLARE @PublicSiteId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N''OrionERP.PublicSiteId''));
  DECLARE @Rfc varchar(50)=CONVERT(varchar(50),SESSION_CONTEXT(N''OrionRfc''));
  DECLARE @CompanyId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N''OrionERP.CompanyId''));
  DECLARE @OrionSiteId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N''OrionERP.SiteId''));
  DECLARE @ResolvedPublicSiteId bigint;
  DECLARE @SiteId int;
  DECLARE @TransactionId uniqueidentifier;
  DECLARE @CurrencyCode char(3);
  DECLARE @GrossAmount decimal(18,2);
  DECLARE @ExistingOrReserved decimal(18,2);
  DECLARE @RefundId uniqueidentifier;
  DECLARE @WasCreated bit=0;

  SET @Reason=LEFT(LTRIM(RTRIM(@Reason)),500);
  SET @IdempotencyKey=NULLIF(LTRIM(RTRIM(@IdempotencyKey)),'''');

  SELECT @ResolvedPublicSiteId=settings.PublicSiteId,@SiteId=settings.SiteId
  FROM restaurante.OnlineOrderingSettings settings
  WHERE settings.Rfc=@Rfc
    AND
    (
      (@PublicSiteId IS NOT NULL AND settings.PublicSiteId=@PublicSiteId)
      OR (@PublicSiteId IS NULL AND settings.CompanyId=@CompanyId
          AND (@OrionSiteId IS NULL OR settings.OrionSiteId=@OrionSiteId))
    );
  IF @ResolvedPublicSiteId IS NULL
    THROW 53710,''No se resolvio el sitio del reembolso.'',1;
  IF @Amount<=0 OR NULLIF(@Reason,N'''') IS NULL
     OR @IdempotencyKey IS NULL
     OR NULLIF(LTRIM(RTRIM(@RequestedBy)),N'''') IS NULL
     OR NULLIF(LTRIM(RTRIM(@AuthorizedBy)),N'''') IS NULL
    THROW 53711,''La solicitud de reembolso es invalida.'',1;

  BEGIN TRANSACTION;

  SELECT @TransactionId=transactionInfo.Id,@CurrencyCode=transactionInfo.CurrencyCode,
    @GrossAmount=transactionInfo.GrossAmount
  FROM restaurante.OnlineCheckoutAttempt attempt WITH(UPDLOCK,HOLDLOCK)
  JOIN restaurante.PaymentGatewayTransaction transactionInfo WITH(UPDLOCK,HOLDLOCK)
    ON transactionInfo.PublicSiteId=attempt.PublicSiteId
   AND transactionInfo.Rfc=attempt.Rfc
   AND transactionInfo.SiteId=attempt.SiteId
   AND transactionInfo.CheckoutAttemptId=attempt.Id
  WHERE attempt.PublicSiteId=@ResolvedPublicSiteId AND attempt.Rfc=@Rfc
    AND attempt.SiteId=@SiteId AND attempt.RestaurantOrderId=@RestaurantOrderId
    AND transactionInfo.LocalPaymentId IS NOT NULL
    AND transactionInfo.[Status] IN(''Completed'',''PartiallyRefunded'');

  IF @TransactionId IS NULL
  BEGIN
    ROLLBACK TRANSACTION;
    THROW 53712,''La orden no tiene un pago PayPal reembolsable.'',1;
  END;

  SELECT @RefundId=Id
  FROM restaurante.PaymentGatewayRefund WITH(UPDLOCK,HOLDLOCK)
  WHERE PublicSiteId=@ResolvedPublicSiteId AND IdempotencyKey=@IdempotencyKey;

  IF @RefundId IS NULL
  BEGIN
    SELECT @RefundId=refundInfo.Id
    FROM restaurante.PaymentGatewayRefund refundInfo WITH
      (UPDLOCK,HOLDLOCK,INDEX(UX_PaymentGatewayRefund_UnresolvedTransaction))
    WHERE refundInfo.GatewayTransactionId=@TransactionId
      AND refundInfo.LocalRefundId IS NULL;

    IF @RefundId IS NOT NULL AND NOT EXISTS
    (
      SELECT 1 FROM restaurante.PaymentGatewayRefund refundInfo
      WHERE refundInfo.Id=@RefundId AND refundInfo.GatewayTransactionId=@TransactionId
        AND refundInfo.LocalRefundId IS NULL
        AND refundInfo.Amount=@Amount AND refundInfo.Reason=@Reason
    )
    BEGIN
      ROLLBACK TRANSACTION;
      THROW 53768,''Ya existe un reembolso sin resolver con otros datos para esta captura.'',1;
    END;

    IF @RefundId IS NULL
    BEGIN
      SELECT @ExistingOrReserved=COALESCE(SUM(Amount),0)
      FROM restaurante.PaymentGatewayRefund WITH(UPDLOCK,HOLDLOCK)
      WHERE PublicSiteId=@ResolvedPublicSiteId AND Rfc=@Rfc
        AND GatewayTransactionId=@TransactionId;

      IF @ExistingOrReserved+@Amount>@GrossAmount
      BEGIN
        ROLLBACK TRANSACTION;
        THROW 53713,''El reembolso excede el saldo capturado disponible.'',1;
      END;

      SET @RefundId=NEWID();
      INSERT restaurante.PaymentGatewayRefund
      (
        Id,PublicSiteId,Rfc,SiteId,GatewayTransactionId,Amount,CurrencyCode,
        Reason,[Status],IdempotencyKey,RequestedBy,AuthorizedBy
      )
      VALUES
      (
        @RefundId,@ResolvedPublicSiteId,@Rfc,@SiteId,@TransactionId,@Amount,@CurrencyCode,
        @Reason,''Requested'',@IdempotencyKey,
        LEFT(LTRIM(RTRIM(@RequestedBy)),256),LEFT(LTRIM(RTRIM(@AuthorizedBy)),256)
      );
      SET @WasCreated=1;
    END;
  END
  ELSE IF NOT EXISTS
  (
    SELECT 1 FROM restaurante.PaymentGatewayRefund
    WHERE Id=@RefundId AND GatewayTransactionId=@TransactionId
      AND Amount=@Amount AND Reason=@Reason
  )
  BEGIN
    ROLLBACK TRANSACTION;
    THROW 53714,''La idempotencia ya se uso con otro reembolso.'',1;
  END;

  UPDATE attempt
  SET [State]=CASE WHEN attempt.[State]=''Refunded'' THEN attempt.[State] ELSE ''RefundRequested'' END,
      UpdatedAtUtc=SYSUTCDATETIME()
  FROM restaurante.OnlineCheckoutAttempt attempt
  JOIN restaurante.PaymentGatewayTransaction transactionInfo
    ON transactionInfo.PublicSiteId=attempt.PublicSiteId
   AND transactionInfo.Rfc=attempt.Rfc
   AND transactionInfo.SiteId=attempt.SiteId
   AND transactionInfo.CheckoutAttemptId=attempt.Id
  WHERE transactionInfo.Id=@TransactionId;

  COMMIT TRANSACTION;

  SELECT @WasCreated WasCreated,refundInfo.Id,refundInfo.GatewayTransactionId,
    refundInfo.Amount,refundInfo.CurrencyCode,refundInfo.Reason,refundInfo.[Status],
    refundInfo.IdempotencyKey,refundInfo.ProviderRefundId,refundInfo.LocalRefundId,
    refundInfo.RequestedAtUtc,refundInfo.CompletedAtUtc
  FROM restaurante.PaymentGatewayRefund refundInfo
  WHERE refundInfo.Id=@RefundId;
END;');

  EXEC(N'
CREATE OR ALTER PROCEDURE restaurante.OnlineOrderingRecoveryList
  @Take int=100
AS
BEGIN
  SET NOCOUNT ON;

  DECLARE @PublicSiteId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N''OrionERP.PublicSiteId''));
  DECLARE @Rfc varchar(50)=CONVERT(varchar(50),SESSION_CONTEXT(N''OrionRfc''));
  DECLARE @CompanyId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N''OrionERP.CompanyId''));
  DECLARE @OrionSiteId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N''OrionERP.SiteId''));
  DECLARE @ResolvedPublicSiteId bigint;

  IF @Take NOT BETWEEN 1 AND 500
    THROW 53719,''El limite de recuperacion es invalido.'',1;

  SELECT @ResolvedPublicSiteId=settings.PublicSiteId
  FROM restaurante.OnlineOrderingSettings settings
  WHERE settings.Rfc=@Rfc
    AND
    (
      (@PublicSiteId IS NOT NULL AND settings.PublicSiteId=@PublicSiteId)
      OR (@PublicSiteId IS NULL AND settings.CompanyId=@CompanyId
          AND (@OrionSiteId IS NULL OR settings.OrionSiteId=@OrionSiteId))
    );
  IF @ResolvedPublicSiteId IS NULL
    THROW 53720,''No se resolvio el sitio de recuperacion.'',1;

  SELECT TOP(@Take)
    attempt.Id CheckoutAttemptId,attempt.[State] CheckoutStatus,
    attempt.PayPalOrderId,attempt.PayPalCaptureId,orderInfo.Folio OrderFolio,
    attempt.Total,attempt.CurrencyCode Currency,
    COALESCE(refundFailure.Attempts,eventFailure.Attempts,notificationFailure.Attempts,
      attempt.ImportAttempts+attempt.RecoveryAttempts) RetryCount,
    COALESCE(refundFailure.FailureCode,eventFailure.FailureCode,
      notificationFailure.FailureCode,attempt.FailureCode) LastErrorCode,
    COALESCE(refundFailure.FailureMessage,eventFailure.FailureMessage,
      notificationFailure.FailureMessage,attempt.FailureMessage) LastErrorMessage,
    attempt.UpdatedAtUtc
  FROM restaurante.OnlineCheckoutAttempt attempt
  LEFT JOIN restaurante.[Order] orderInfo
    ON orderInfo.PublicSiteId=attempt.PublicSiteId AND orderInfo.Rfc=attempt.Rfc
   AND orderInfo.SiteId=attempt.SiteId AND orderInfo.Id=attempt.RestaurantOrderId
  OUTER APPLY
  (
    SELECT TOP(1) refundInfo.Id RefundId,refundInfo.FailureCode,
      refundInfo.FailureMessage,refundInfo.Attempts
    FROM restaurante.PaymentGatewayTransaction transactionInfo
    JOIN restaurante.PaymentGatewayRefund refundInfo
      ON refundInfo.PublicSiteId=transactionInfo.PublicSiteId
     AND refundInfo.Rfc=transactionInfo.Rfc AND refundInfo.SiteId=transactionInfo.SiteId
     AND refundInfo.GatewayTransactionId=transactionInfo.Id
    WHERE transactionInfo.PublicSiteId=attempt.PublicSiteId
      AND transactionInfo.CheckoutAttemptId=attempt.Id
      AND refundInfo.[Status] IN(''Failed'',''Pending'',''Processing'')
    ORDER BY refundInfo.UpdatedAtUtc DESC
  ) refundFailure
  OUTER APPLY
  (
    SELECT TOP(1) eventInfo.Id EventId,eventInfo.FailureCode,
      eventInfo.FailureMessage,eventInfo.Attempts
    FROM restaurante.PaymentGatewayEvent eventInfo
    WHERE eventInfo.PublicSiteId=attempt.PublicSiteId
      AND eventInfo.CheckoutAttemptId=attempt.Id
      AND
      (
        eventInfo.ProcessingStatus=''Failed''
        OR
        (eventInfo.ProcessingStatus=''Processing''
         AND eventInfo.LeaseExpiresAtUtc<SYSUTCDATETIME())
      )
    ORDER BY eventInfo.UpdatedAtUtc DESC
  ) eventFailure
  OUTER APPLY
  (
    SELECT TOP(1) notification.Id NotificationId,notification.FailureCode,
      notification.FailureMessage,notification.Attempts
    FROM restaurante.OnlineOrderNotification notification
    WHERE notification.PublicSiteId=attempt.PublicSiteId
      AND notification.Rfc=attempt.Rfc
      AND notification.SiteId=attempt.SiteId
      AND notification.CheckoutAttemptId=attempt.Id
      AND
      (
        notification.[Status]=''Failed''
        OR
        (notification.[Status]=''Processing''
         AND notification.LeaseExpiresAtUtc<SYSUTCDATETIME())
      )
    ORDER BY notification.UpdatedAtUtc DESC
  ) notificationFailure
  WHERE attempt.PublicSiteId=@ResolvedPublicSiteId AND attempt.Rfc=@Rfc
    AND
    (
      attempt.[State] IN(''CapturePending'',''Captured'',''CapturedNeedsOrder'',''RefundRequested'',''RefundPending'',''Failed'')
      OR refundFailure.RefundId IS NOT NULL
      OR eventFailure.EventId IS NOT NULL
      OR notificationFailure.NotificationId IS NOT NULL
    )
  ORDER BY attempt.UpdatedAtUtc DESC,attempt.Id;
END;');

  IF COL_LENGTH(N'restaurante.OnlineOrderNotification',N'ProviderMessageId') IS NULL
    ALTER TABLE restaurante.OnlineOrderNotification
      ADD ProviderMessageId nvarchar(512) NULL;

  IF NOT EXISTS
  (
    SELECT 1 FROM sys.indexes
    WHERE object_id=OBJECT_ID(N'restaurante.OnlineOrderNotification')
      AND name=N'UX_OnlineOrderNotification_ProviderMessage'
  )
    EXEC(N'
      CREATE UNIQUE INDEX UX_OnlineOrderNotification_ProviderMessage
        ON restaurante.OnlineOrderNotification(PublicSiteId,ProviderMessageId)
        WHERE ProviderMessageId IS NOT NULL;');

  EXEC(N'
CREATE OR ALTER PROCEDURE restaurante.OnlineOrderNotificationClaim
  @LeaseId uniqueidentifier,
  @BatchSize int=10,
  @LeaseSeconds int=90
AS
BEGIN
  SET NOCOUNT ON;
  SET XACT_ABORT ON;

  DECLARE @PublicSiteId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N''OrionERP.PublicSiteId''));
  DECLARE @Rfc varchar(50)=CONVERT(varchar(50),SESSION_CONTEXT(N''OrionRfc''));
  DECLARE @Claimed TABLE(Id bigint NOT NULL);

  IF @PublicSiteId IS NULL OR NULLIF(@Rfc,'''') IS NULL
    THROW 53701,''No existe un contexto publico de restaurante verificado.'',1;
  IF @LeaseId IS NULL OR @LeaseSeconds NOT BETWEEN 15 AND 600 OR @BatchSize NOT BETWEEN 1 AND 50
    THROW 53702,''La concesion de correo es invalida.'',1;

  BEGIN TRANSACTION;
  ;WITH nextNotification AS
  (
    SELECT TOP(@BatchSize) notification.*
    FROM restaurante.OnlineOrderNotification notification WITH(UPDLOCK,READPAST,ROWLOCK)
    WHERE notification.PublicSiteId=@PublicSiteId AND notification.Rfc=@Rfc
      AND
      (
        notification.[Status] IN(''Pending'',''Failed'')
        OR
        (
          notification.[Status]=''Processing''
          AND notification.LeaseExpiresAtUtc<SYSUTCDATETIME()
        )
      )
      AND (notification.NextRetryAtUtc IS NULL OR notification.NextRetryAtUtc<=SYSUTCDATETIME())
      AND (notification.LeaseExpiresAtUtc IS NULL OR notification.LeaseExpiresAtUtc<SYSUTCDATETIME())
    ORDER BY notification.CreatedAtUtc,notification.Id
  )
  UPDATE nextNotification
  SET [Status]=''Processing'',Attempts=Attempts+1,LeaseId=@LeaseId,
      LeaseExpiresAtUtc=DATEADD(SECOND,@LeaseSeconds,SYSUTCDATETIME()),
      NextRetryAtUtc=NULL,UpdatedAtUtc=SYSUTCDATETIME()
  OUTPUT inserted.Id INTO @Claimed(Id);
  COMMIT TRANSACTION;

  SELECT notification.Id,notification.CheckoutAttemptId,notification.RestaurantOrderId,
    notification.NotificationType,notification.RecipientEmail,notification.IdempotencyKey,
    notification.ProviderMessageId,
    notification.Attempts,notification.LeaseId,notification.LeaseExpiresAtUtc,
    attempt.ClientAttemptId,attempt.CustomerName,orderInfo.Folio OrderFolio,orderInfo.[Status] OrderStatus,
    orderInfo.Total,attempt.CurrencyCode
  FROM @Claimed claimed
  JOIN restaurante.OnlineOrderNotification notification ON notification.Id=claimed.Id
  JOIN restaurante.OnlineCheckoutAttempt attempt
    ON attempt.PublicSiteId=notification.PublicSiteId
   AND attempt.Rfc=notification.Rfc
   AND attempt.SiteId=notification.SiteId
   AND attempt.Id=notification.CheckoutAttemptId
  JOIN restaurante.[Order] orderInfo
    ON orderInfo.PublicSiteId=notification.PublicSiteId
   AND orderInfo.Rfc=notification.Rfc
   AND orderInfo.SiteId=notification.SiteId
   AND orderInfo.Id=notification.RestaurantOrderId;
END;');

  EXEC(N'
CREATE OR ALTER PROCEDURE restaurante.OnlineOrderNotificationProviderMessageSet
  @Id bigint,
  @LeaseId uniqueidentifier,
  @ProviderMessageId nvarchar(512)
AS
BEGIN
  SET NOCOUNT ON;

  DECLARE @PublicSiteId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N''OrionERP.PublicSiteId''));
  DECLARE @Rfc varchar(50)=CONVERT(varchar(50),SESSION_CONTEXT(N''OrionRfc''));
  DECLARE @NormalizedProviderMessageId nvarchar(512)=NULLIF(LTRIM(RTRIM(@ProviderMessageId)),N'''');

  IF @PublicSiteId IS NULL OR NULLIF(@Rfc,'''') IS NULL
    THROW 53756,''No existe un contexto publico de restaurante verificado.'',1;
  IF @LeaseId IS NULL OR @NormalizedProviderMessageId IS NULL
    THROW 53757,''La identidad durable del correo es invalida.'',1;

  UPDATE restaurante.OnlineOrderNotification
  SET ProviderMessageId=COALESCE(ProviderMessageId,@NormalizedProviderMessageId),
      UpdatedAtUtc=SYSUTCDATETIME()
  WHERE Id=@Id AND PublicSiteId=@PublicSiteId AND Rfc=@Rfc
    AND [Status]=''Processing'' AND LeaseId=@LeaseId
    AND LeaseExpiresAtUtc>=SYSUTCDATETIME()
    AND (ProviderMessageId IS NULL OR ProviderMessageId=@NormalizedProviderMessageId);
  IF @@ROWCOUNT<>1
    THROW 53758,''La concesion o identidad durable del correo ya no es valida.'',1;

  SELECT Id,[Status],ProviderMessageId,Attempts,LeaseExpiresAtUtc,UpdatedAtUtc
  FROM restaurante.OnlineOrderNotification
  WHERE Id=@Id AND PublicSiteId=@PublicSiteId AND Rfc=@Rfc;
END;');

  EXEC(N'
CREATE OR ALTER PROCEDURE restaurante.OnlineOrderNotificationComplete
  @Id bigint,
  @LeaseId uniqueidentifier
AS
BEGIN
  SET NOCOUNT ON;

  DECLARE @PublicSiteId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N''OrionERP.PublicSiteId''));
  DECLARE @Rfc varchar(50)=CONVERT(varchar(50),SESSION_CONTEXT(N''OrionRfc''));
  IF @PublicSiteId IS NULL OR NULLIF(@Rfc,'''') IS NULL
    THROW 53705,''No existe un contexto publico de restaurante verificado.'',1;

  UPDATE restaurante.OnlineOrderNotification
  SET [Status]=''Sent'',LeaseId=NULL,LeaseExpiresAtUtc=NULL,NextRetryAtUtc=NULL,
      FailureCode=NULL,FailureMessage=NULL,
      SentAtUtc=COALESCE(SentAtUtc,SYSUTCDATETIME()),UpdatedAtUtc=SYSUTCDATETIME()
  WHERE Id=@Id AND PublicSiteId=@PublicSiteId AND Rfc=@Rfc
    AND [Status]=''Processing'' AND LeaseId=@LeaseId
    AND ProviderMessageId IS NOT NULL;
  IF @@ROWCOUNT<>1
    THROW 53706,''La concesion de correo ya no es valida.'',1;

  SELECT Id,[Status],ProviderMessageId,Attempts,SentAtUtc,UpdatedAtUtc
  FROM restaurante.OnlineOrderNotification
  WHERE Id=@Id AND PublicSiteId=@PublicSiteId AND Rfc=@Rfc;
END;');

  EXEC(N'
CREATE OR ALTER PROCEDURE restaurante.OnlineOrderNotificationResult
  @Id bigint,
  @LeaseId uniqueidentifier,
  @Succeeded bit,
  @FailureCode varchar(80)=NULL,
  @FailureMessage nvarchar(500)=NULL,
  @NextRetryAtUtc datetime2(3)=NULL
AS
BEGIN
  SET NOCOUNT ON;

  DECLARE @PublicSiteId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N''OrionERP.PublicSiteId''));
  DECLARE @Rfc varchar(50)=CONVERT(varchar(50),SESSION_CONTEXT(N''OrionRfc''));

  IF @PublicSiteId IS NULL OR NULLIF(@Rfc,'''') IS NULL
    THROW 53703,''No existe un contexto publico de restaurante verificado.'',1;

  UPDATE restaurante.OnlineOrderNotification
  SET [Status]=CASE WHEN @Succeeded=1 THEN ''Sent'' ELSE ''Failed'' END,
      LeaseId=NULL,LeaseExpiresAtUtc=NULL,
      NextRetryAtUtc=CASE WHEN @Succeeded=0
        THEN COALESCE(@NextRetryAtUtc,DATEADD(MINUTE,2,SYSUTCDATETIME())) ELSE NULL END,
      FailureCode=CASE WHEN @Succeeded=0 THEN NULLIF(LTRIM(RTRIM(@FailureCode)),'''') ELSE NULL END,
      FailureMessage=CASE WHEN @Succeeded=0
        THEN LEFT(NULLIF(LTRIM(RTRIM(@FailureMessage)),N''''),500) ELSE NULL END,
      SentAtUtc=CASE WHEN @Succeeded=1 THEN COALESCE(SentAtUtc,SYSUTCDATETIME()) ELSE SentAtUtc END,
      UpdatedAtUtc=SYSUTCDATETIME()
  WHERE Id=@Id AND PublicSiteId=@PublicSiteId AND Rfc=@Rfc
    AND [Status]=''Processing'' AND LeaseId=@LeaseId
    AND (@Succeeded=0 OR ProviderMessageId IS NOT NULL);

  IF @@ROWCOUNT<>1
    THROW 53704,''La concesion de correo ya no es valida.'',1;

  SELECT Id,[Status],ProviderMessageId,Attempts,SentAtUtc,NextRetryAtUtc,UpdatedAtUtc
  FROM restaurante.OnlineOrderNotification
  WHERE Id=@Id AND PublicSiteId=@PublicSiteId AND Rfc=@Rfc;
END;');

  EXEC(N'
CREATE OR ALTER PROCEDURE restaurante.PublicMemberCreate
  @IdentityUserId nvarchar(450),
  @FirstName nvarchar(100),
  @LastName nvarchar(100),
  @NormalizedEmail nvarchar(256),
  @NormalizedPhone varchar(30),
  @PrivacyVersion varchar(30),
  @TermsVersion varchar(30),
  @EmailMarketingConsent bit,
  @SmsMarketingConsent bit,
  @WhatsAppMarketingConsent bit
AS
BEGIN
  SET NOCOUNT ON;
  SET XACT_ABORT ON;

  DECLARE @ContextPublicSiteId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N''OrionERP.PublicSiteId''));
  DECLARE @ContextRfc varchar(50)=NULLIF(CONVERT(varchar(50),SESSION_CONTEXT(N''OrionRfc'')),'''');
  DECLARE @PublicSiteId bigint;
  DECLARE @Rfc varchar(50);
  DECLARE @MemberId uniqueidentifier=NEWID();
  DECLARE @MembershipNumber varchar(20);
  DECLARE @Candidate varchar(20);
  DECLARE @Attempt tinyint=0;
  DECLARE @IdentityEmail nvarchar(256);

  SELECT @PublicSiteId=binding.PublicSiteId,@Rfc=companyInfo.Rfc
  FROM orion.PublicSqlPrincipalBinding binding
  JOIN orion.PublicSite publicSite ON publicSite.PublicSiteId=binding.PublicSiteId
  JOIN orion.Company companyInfo ON companyInfo.CompanyId=publicSite.CompanyId
  WHERE binding.PrincipalName=USER_NAME()
    AND binding.PublicSiteId=@ContextPublicSiteId
    AND binding.IsActive=1
    AND binding.PermissionProfile=''RESTAURANT_PUBLIC''
    AND binding.PermissionVersion>=8
    AND publicSite.ModuleCode=''RESTAURANT''
    AND publicSite.IsActive=1
    AND companyInfo.IsActive=1
    AND companyInfo.Rfc=@ContextRfc;
  IF @PublicSiteId IS NULL OR @Rfc IS NULL
    THROW 53741,''No existe un contexto publico de restaurante verificado.'',1;

  SET @IdentityUserId=NULLIF(LTRIM(RTRIM(@IdentityUserId)),N'''');
  SET @FirstName=NULLIF(LTRIM(RTRIM(@FirstName)),N'''');
  SET @LastName=NULLIF(LTRIM(RTRIM(@LastName)),N'''');
  SET @NormalizedEmail=UPPER(NULLIF(LTRIM(RTRIM(@NormalizedEmail)),N''''));
  SET @NormalizedPhone=NULLIF(LTRIM(RTRIM(@NormalizedPhone)),'''');
  SET @PrivacyVersion=NULLIF(LTRIM(RTRIM(@PrivacyVersion)),'''');
  SET @TermsVersion=NULLIF(LTRIM(RTRIM(@TermsVersion)),'''');
  IF @IdentityUserId IS NULL OR @FirstName IS NULL OR @LastName IS NULL
     OR @NormalizedEmail IS NULL OR @NormalizedEmail NOT LIKE N''_%@_%._%''
     OR @NormalizedPhone IS NULL OR @NormalizedPhone LIKE ''%[^0-9+]%''
     OR @PrivacyVersion IS NULL OR @TermsVersion IS NULL
     OR @EmailMarketingConsent IS NULL OR @SmsMarketingConsent IS NULL
     OR @WhatsAppMarketingConsent IS NULL
    THROW 53743,''Los datos requeridos para crear la membresia son invalidos.'',1;

  BEGIN TRY
    BEGIN TRANSACTION;

    SELECT @IdentityEmail=identityUser.NormalizedEmail
    FROM public_identity.AspNetUsers identityUser WITH(UPDLOCK,HOLDLOCK)
    WHERE identityUser.Id=@IdentityUserId
      AND identityUser.PublicSiteId=@PublicSiteId
      AND identityUser.ClosedAt IS NULL;
    IF @@ROWCOUNT<>1 OR @IdentityEmail IS NULL OR @IdentityEmail<>@NormalizedEmail
      THROW 53744,''La cuenta de acceso no pertenece al sitio publico verificado.'',1;

    IF EXISTS
    (
      SELECT 1
      FROM fidelidad.MemberAccount member WITH(UPDLOCK,HOLDLOCK)
      WHERE member.Rfc=@Rfc
        AND
        (
          member.IdentityUserId=@IdentityUserId
          OR member.NormalizedEmail=@NormalizedEmail
          OR member.NormalizedPhone=@NormalizedPhone
        )
    )
      THROW 53742,''El correo o telefono ya pertenece a otra membresia.'',1;

    WHILE @MembershipNumber IS NULL AND @Attempt<20
    BEGIN
      SET @Attempt=@Attempt+1;
      SET @Candidate=CONCAT(''BG'',RIGHT(CONCAT(''00000000'',
        CONVERT(varchar(8),ABS(CONVERT(bigint,CHECKSUM(NEWID())))%100000000)),8));
      IF NOT EXISTS
      (
        SELECT 1 FROM fidelidad.MemberAccount WITH(UPDLOCK,HOLDLOCK)
        WHERE Rfc=@Rfc AND MembershipNumber=@Candidate
      )
        SET @MembershipNumber=@Candidate;
    END;
    IF @MembershipNumber IS NULL
      THROW 53745,''No fue posible generar un numero de membresia unico.'',1;

    INSERT fidelidad.MemberAccount
    (
      Id,Rfc,PublicSiteId,IdentityUserId,MembershipNumber,FirstName,LastName,
      NormalizedEmail,NormalizedPhone,[Status],IsAdultConfirmed
    )
    VALUES
    (
      @MemberId,@Rfc,@PublicSiteId,@IdentityUserId,@MembershipNumber,@FirstName,@LastName,
      @NormalizedEmail,@NormalizedPhone,''PendingVerification'',1
    );

    INSERT fidelidad.MemberConsent
      (Rfc,MemberId,ConsentType,DocumentVersion,IsGranted,Source)
    SELECT @Rfc,@MemberId,consentInfo.ConsentType,consentInfo.DocumentVersion,
      consentInfo.IsGranted,''Website''
    FROM
    (VALUES
      (''Privacy'',@PrivacyVersion,CONVERT(bit,1)),
      (''Terms'',@TermsVersion,CONVERT(bit,1)),
      (''EmailMarketing'',@TermsVersion,@EmailMarketingConsent),
      (''SmsMarketing'',@TermsVersion,@SmsMarketingConsent),
      (''WhatsAppMarketing'',@TermsVersion,@WhatsAppMarketingConsent)
    ) consentInfo(ConsentType,DocumentVersion,IsGranted);

    COMMIT TRANSACTION;
    SELECT @MemberId MemberId,@PublicSiteId PublicSiteId,@Rfc Rfc;
  END TRY
  BEGIN CATCH
    IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
    THROW;
  END CATCH;
END;');

  EXEC(N'
CREATE OR ALTER PROCEDURE restaurante.PublicMemberVerificationUpdate
  @MemberId uniqueidentifier,
  @EmailVerified bit,
  @PhoneVerified bit
AS
BEGIN
  SET NOCOUNT ON;

  DECLARE @ContextPublicSiteId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N''OrionERP.PublicSiteId''));
  DECLARE @ContextRfc varchar(50)=NULLIF(CONVERT(varchar(50),SESSION_CONTEXT(N''OrionRfc'')),'''');
  DECLARE @PublicSiteId bigint;
  DECLARE @Rfc varchar(50);

  SELECT @PublicSiteId=binding.PublicSiteId,@Rfc=companyInfo.Rfc
  FROM orion.PublicSqlPrincipalBinding binding
  JOIN orion.PublicSite publicSite ON publicSite.PublicSiteId=binding.PublicSiteId
  JOIN orion.Company companyInfo ON companyInfo.CompanyId=publicSite.CompanyId
  WHERE binding.PrincipalName=USER_NAME()
    AND binding.PublicSiteId=@ContextPublicSiteId
    AND binding.IsActive=1
    AND binding.PermissionProfile=''RESTAURANT_PUBLIC''
    AND binding.PermissionVersion>=8
    AND publicSite.ModuleCode=''RESTAURANT''
    AND publicSite.IsActive=1
    AND companyInfo.IsActive=1
    AND companyInfo.Rfc=@ContextRfc;
  IF @PublicSiteId IS NULL OR @Rfc IS NULL
    THROW 53741,''No existe un contexto publico de restaurante verificado.'',1;

  UPDATE fidelidad.MemberAccount
  SET EmailVerified=CASE WHEN @EmailVerified=1 THEN 1 ELSE EmailVerified END,
      PhoneVerified=CASE WHEN @PhoneVerified=1 THEN 1 ELSE PhoneVerified END,
      [Status]=CASE WHEN EmailVerified=1 OR @EmailVerified=1 THEN ''Active'' ELSE [Status] END,
      UpdatedAt=SYSUTCDATETIME()
  WHERE PublicSiteId=@PublicSiteId AND Rfc=@Rfc
    AND Id=@MemberId AND [Status]<>''Closed'';
  SELECT CONVERT(bit,CASE WHEN @@ROWCOUNT=1 THEN 1 ELSE 0 END) Succeeded;
END;');

  EXEC(N'
CREATE OR ALTER PROCEDURE restaurante.PublicMemberQrIssue
  @Id uniqueidentifier,
  @MemberId uniqueidentifier,
  @TokenHash char(64),
  @ExpiresAt datetime2(0)
AS
BEGIN
  SET NOCOUNT ON;
  SET XACT_ABORT ON;

  DECLARE @ContextPublicSiteId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N''OrionERP.PublicSiteId''));
  DECLARE @ContextRfc varchar(50)=NULLIF(CONVERT(varchar(50),SESSION_CONTEXT(N''OrionRfc'')),'''');
  DECLARE @PublicSiteId bigint;
  DECLARE @Rfc varchar(50);
  DECLARE @Created int;

  SELECT @PublicSiteId=binding.PublicSiteId,@Rfc=companyInfo.Rfc
  FROM orion.PublicSqlPrincipalBinding binding
  JOIN orion.PublicSite publicSite ON publicSite.PublicSiteId=binding.PublicSiteId
  JOIN orion.Company companyInfo ON companyInfo.CompanyId=publicSite.CompanyId
  WHERE binding.PrincipalName=USER_NAME()
    AND binding.PublicSiteId=@ContextPublicSiteId
    AND binding.IsActive=1
    AND binding.PermissionProfile=''RESTAURANT_PUBLIC''
    AND binding.PermissionVersion>=8
    AND publicSite.ModuleCode=''RESTAURANT''
    AND publicSite.IsActive=1
    AND companyInfo.IsActive=1
    AND companyInfo.Rfc=@ContextRfc;
  IF @PublicSiteId IS NULL OR @Rfc IS NULL
    THROW 53741,''No existe un contexto publico de restaurante verificado.'',1;
  SET @TokenHash=UPPER(@TokenHash);
  IF @Id IS NULL OR @MemberId IS NULL OR DATALENGTH(@TokenHash)<>64
     OR @TokenHash LIKE ''%[^0-9A-F]%'' COLLATE Latin1_General_100_BIN2
     OR @ExpiresAt<=SYSUTCDATETIME()
     OR @ExpiresAt>DATEADD(MINUTE,10,SYSUTCDATETIME())
    THROW 53746,''Los datos del QR de membresia son invalidos.'',1;

  BEGIN TRANSACTION;
  INSERT fidelidad.MemberQrToken(Id,Rfc,MemberId,TokenHash,ExpiresAt)
  SELECT @Id,@Rfc,@MemberId,@TokenHash,@ExpiresAt
  WHERE EXISTS
  (
    SELECT 1 FROM fidelidad.MemberAccount member WITH(UPDLOCK,HOLDLOCK)
    WHERE member.PublicSiteId=@PublicSiteId AND member.Rfc=@Rfc
      AND member.Id=@MemberId AND member.[Status]=''Active''
      AND member.EmailVerified=1
  );
  SET @Created=@@ROWCOUNT;

  IF @Created=1
  BEGIN
    DELETE tokenInfo
    FROM fidelidad.MemberQrToken tokenInfo
    JOIN fidelidad.MemberAccount member
      ON member.Rfc=tokenInfo.Rfc AND member.Id=tokenInfo.MemberId
    WHERE member.PublicSiteId=@PublicSiteId AND member.Rfc=@Rfc
      AND tokenInfo.ExpiresAt<DATEADD(HOUR,-1,SYSUTCDATETIME());
  END;
  COMMIT TRANSACTION;
  SELECT CONVERT(bit,CASE WHEN @Created=1 THEN 1 ELSE 0 END) Succeeded;
END;');

  EXEC(N'
CREATE OR ALTER PROCEDURE restaurante.PublicMemberConsentsUpdate
  @MemberId uniqueidentifier,
  @PrivacyVersion varchar(30),
  @TermsVersion varchar(30),
  @EmailMarketingConsent bit,
  @SmsMarketingConsent bit,
  @WhatsAppMarketingConsent bit
AS
BEGIN
  SET NOCOUNT ON;
  SET XACT_ABORT ON;

  DECLARE @ContextPublicSiteId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N''OrionERP.PublicSiteId''));
  DECLARE @ContextRfc varchar(50)=NULLIF(CONVERT(varchar(50),SESSION_CONTEXT(N''OrionRfc'')),'''');
  DECLARE @PublicSiteId bigint;
  DECLARE @Rfc varchar(50);

  SELECT @PublicSiteId=binding.PublicSiteId,@Rfc=companyInfo.Rfc
  FROM orion.PublicSqlPrincipalBinding binding
  JOIN orion.PublicSite publicSite ON publicSite.PublicSiteId=binding.PublicSiteId
  JOIN orion.Company companyInfo ON companyInfo.CompanyId=publicSite.CompanyId
  WHERE binding.PrincipalName=USER_NAME()
    AND binding.PublicSiteId=@ContextPublicSiteId
    AND binding.IsActive=1
    AND binding.PermissionProfile=''RESTAURANT_PUBLIC''
    AND binding.PermissionVersion>=8
    AND publicSite.ModuleCode=''RESTAURANT''
    AND publicSite.IsActive=1
    AND companyInfo.IsActive=1
    AND companyInfo.Rfc=@ContextRfc;
  IF @PublicSiteId IS NULL OR @Rfc IS NULL
    THROW 53741,''No existe un contexto publico de restaurante verificado.'',1;
  SET @PrivacyVersion=NULLIF(LTRIM(RTRIM(@PrivacyVersion)),'''');
  SET @TermsVersion=NULLIF(LTRIM(RTRIM(@TermsVersion)),'''');
  IF @MemberId IS NULL OR @PrivacyVersion IS NULL OR @TermsVersion IS NULL
     OR @EmailMarketingConsent IS NULL OR @SmsMarketingConsent IS NULL
     OR @WhatsAppMarketingConsent IS NULL
    THROW 53747,''Las versiones legales de membresia son invalidas.'',1;

  BEGIN TRANSACTION;
  IF NOT EXISTS
  (
    SELECT 1 FROM fidelidad.MemberAccount WITH(UPDLOCK,HOLDLOCK)
    WHERE PublicSiteId=@PublicSiteId AND Rfc=@Rfc
      AND Id=@MemberId AND [Status]<>''Closed''
  )
  BEGIN
    ROLLBACK TRANSACTION;
    SELECT CONVERT(bit,0) Succeeded;
    RETURN;
  END;

  INSERT fidelidad.MemberConsent
    (Rfc,MemberId,ConsentType,DocumentVersion,IsGranted,Source)
  SELECT @Rfc,@MemberId,consentInfo.ConsentType,consentInfo.DocumentVersion,
    consentInfo.IsGranted,''Website''
  FROM
  (VALUES
    (''Privacy'',@PrivacyVersion,CONVERT(bit,1)),
    (''Terms'',@TermsVersion,CONVERT(bit,1)),
    (''EmailMarketing'',@TermsVersion,@EmailMarketingConsent),
    (''SmsMarketing'',@TermsVersion,@SmsMarketingConsent),
    (''WhatsAppMarketing'',@TermsVersion,@WhatsAppMarketingConsent)
  ) consentInfo(ConsentType,DocumentVersion,IsGranted);
  COMMIT TRANSACTION;
  SELECT CONVERT(bit,1) Succeeded;
END;');

  EXEC(N'
CREATE OR ALTER PROCEDURE restaurante.PublicMemberClosureRequest
  @MemberId uniqueidentifier,
  @Reason nvarchar(500)
AS
BEGIN
  SET NOCOUNT ON;
  SET XACT_ABORT ON;

  DECLARE @ContextPublicSiteId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N''OrionERP.PublicSiteId''));
  DECLARE @ContextRfc varchar(50)=NULLIF(CONVERT(varchar(50),SESSION_CONTEXT(N''OrionRfc'')),'''');
  DECLARE @PublicSiteId bigint;
  DECLARE @Rfc varchar(50);
  DECLARE @IdentityUserId nvarchar(450);
  DECLARE @ClosedIdentityKey nvarchar(256)=CONCAT(N''closed-'',CONVERT(nvarchar(36),@MemberId));
  DECLARE @ClosedMemberKey varchar(30)=CONCAT(''C-'',LEFT(REPLACE(CONVERT(varchar(36),@MemberId),''-'',''''),28));

  SELECT @PublicSiteId=binding.PublicSiteId,@Rfc=companyInfo.Rfc
  FROM orion.PublicSqlPrincipalBinding binding
  JOIN orion.PublicSite publicSite ON publicSite.PublicSiteId=binding.PublicSiteId
  JOIN orion.Company companyInfo ON companyInfo.CompanyId=publicSite.CompanyId
  WHERE binding.PrincipalName=USER_NAME()
    AND binding.PublicSiteId=@ContextPublicSiteId
    AND binding.IsActive=1
    AND binding.PermissionProfile=''RESTAURANT_PUBLIC''
    AND binding.PermissionVersion>=8
    AND publicSite.ModuleCode=''RESTAURANT''
    AND publicSite.IsActive=1
    AND companyInfo.IsActive=1
    AND companyInfo.Rfc=@ContextRfc;
  IF @PublicSiteId IS NULL OR @Rfc IS NULL
    THROW 53741,''No existe un contexto publico de restaurante verificado.'',1;
  SET @Reason=NULLIF(LTRIM(RTRIM(@Reason)),N'''');
  IF @MemberId IS NULL OR @Reason IS NULL
    THROW 53748,''La solicitud de cierre es invalida.'',1;

  BEGIN TRY
    BEGIN TRANSACTION;
    SELECT @IdentityUserId=member.IdentityUserId
    FROM fidelidad.MemberAccount member WITH(UPDLOCK,HOLDLOCK)
    WHERE member.PublicSiteId=@PublicSiteId AND member.Rfc=@Rfc
      AND member.Id=@MemberId AND member.[Status]<>''Closed'';
    IF @IdentityUserId IS NULL
    BEGIN
      ROLLBACK TRANSACTION;
      SELECT CONVERT(bit,0) Succeeded;
      RETURN;
    END;

    INSERT fidelidad.MemberClosureRequest(Id,Rfc,MemberId,Reason,[Status])
    VALUES(NEWID(),@Rfc,@MemberId,@Reason,''Pending'');

    UPDATE fidelidad.MemberAccount
    SET [Status]=''Closed'',ClosedAt=SYSUTCDATETIME(),UpdatedAt=SYSUTCDATETIME(),
        FirstName=N''Miembro'',LastName=N''cerrado'',
        NormalizedEmail=@ClosedIdentityKey,NormalizedPhone=@ClosedMemberKey,
        EmailVerified=0,PhoneVerified=0
    WHERE PublicSiteId=@PublicSiteId AND Rfc=@Rfc
      AND Id=@MemberId AND [Status]<>''Closed'';
    IF @@ROWCOUNT<>1
      THROW 53749,''La membresia cambio durante la solicitud de cierre.'',1;

    INSERT fidelidad.MemberConsent(Rfc,MemberId,ConsentType,DocumentVersion,IsGranted,Source)
    SELECT @Rfc,@MemberId,consentType,''closure'',0,''MemberPortal''
    FROM (VALUES(''EmailMarketing''),(''SmsMarketing''),(''WhatsAppMarketing'')) valueInfo(consentType);

    UPDATE public_identity.AspNetUsers
    SET UserName=@ClosedIdentityKey,NormalizedUserName=UPPER(@ClosedIdentityKey),
        Email=NULL,NormalizedEmail=NULL,EmailConfirmed=0,
        PhoneNumber=NULL,PhoneNumberConfirmed=0,
        PasswordHash=NULL,SecurityStamp=CONVERT(nvarchar(36),NEWID()),
        ConcurrencyStamp=CONVERT(nvarchar(36),NEWID()),
        FirstName=N''Miembro'',LastName=N''cerrado'',
        ClosedAt=SYSUTCDATETIME(),LockoutEnd=''9999-12-31T23:59:59+00:00''
    WHERE Id=@IdentityUserId AND PublicSiteId=@PublicSiteId AND ClosedAt IS NULL;
    IF @@ROWCOUNT<>1
      THROW 53750,''La cuenta de acceso cambio durante la solicitud de cierre.'',1;

    COMMIT TRANSACTION;
    SELECT CONVERT(bit,1) Succeeded;
  END TRY
  BEGIN CATCH
    IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
    THROW;
  END CATCH;
END;');

  INSERT orion.PublicPermissionProfile(ProfileCode,ProfileVersion,ModuleCode)
  SELECT 'RESTAURANT_PUBLIC',8,'RESTAURANT'
  WHERE NOT EXISTS
  (
    SELECT 1 FROM orion.PublicPermissionProfile
    WHERE ProfileCode='RESTAURANT_PUBLIC' AND ProfileVersion=8
  );

  INSERT orion.PublicPermissionProfileEntry
    (ProfileCode,ProfileVersion,PermissionState,PermissionName,SecurableClass,SchemaName,ObjectName)
  SELECT source.ProfileCode,8,source.PermissionState,source.PermissionName,
    source.SecurableClass,source.SchemaName,source.ObjectName
  FROM orion.PublicPermissionProfileEntry source
  WHERE source.ProfileCode='RESTAURANT_PUBLIC' AND source.ProfileVersion=7
    AND NOT EXISTS
    (
      SELECT 1 FROM orion.PublicPermissionProfileEntry target
      WHERE target.ProfileCode='RESTAURANT_PUBLIC' AND target.ProfileVersion=8
        AND target.PermissionState=source.PermissionState
        AND target.PermissionName=source.PermissionName
        AND target.SecurableClass=source.SecurableClass
        AND target.SchemaName=source.SchemaName
        AND target.ObjectName=source.ObjectName
    );

  DELETE entryInfo
  FROM orion.PublicPermissionProfileEntry entryInfo
  WHERE entryInfo.ProfileCode='RESTAURANT_PUBLIC'
    AND entryInfo.ProfileVersion=8
    AND entryInfo.PermissionState='GRANT'
    AND entryInfo.PermissionName IN('INSERT','UPDATE','DELETE')
    AND
    (
      (entryInfo.SecurableClass='SCHEMA' AND entryInfo.SchemaName='public_identity')
      OR
      (entryInfo.SecurableClass='OBJECT' AND entryInfo.SchemaName='fidelidad'
       AND entryInfo.ObjectName IN
       ('MemberAccount','MemberQrToken','PointLedger','MemberClosureRequest','MemberConsent','ProgramSettings'))
      OR
      (entryInfo.SecurableClass='OBJECT' AND entryInfo.SchemaName='restaurante'
       AND entryInfo.ObjectName='Order' AND entryInfo.PermissionName='UPDATE')
    );

  DECLARE @BoundaryPermissions TABLE
  (
    PermissionState varchar(5) NOT NULL,
    PermissionName varchar(20) NOT NULL,
    SchemaName sysname NOT NULL,
    ObjectName sysname NOT NULL,
    PRIMARY KEY(PermissionState,PermissionName,SchemaName,ObjectName)
  );

  INSERT @BoundaryPermissions VALUES
    ('GRANT','INSERT','public_identity','AspNetUsers'),
    ('GRANT','UPDATE','public_identity','AspNetUsers'),
    ('GRANT','DELETE','public_identity','AspNetUsers'),
    ('GRANT','EXECUTE','restaurante','PublicMemberCreate'),
    ('GRANT','EXECUTE','restaurante','PublicMemberVerificationUpdate'),
    ('GRANT','EXECUTE','restaurante','PublicMemberQrIssue'),
    ('GRANT','EXECUTE','restaurante','PublicMemberConsentsUpdate'),
    ('GRANT','EXECUTE','restaurante','PublicMemberClosureRequest'),
    ('GRANT','EXECUTE','restaurante','OnlineOrderNotificationProviderMessageSet'),
    ('GRANT','EXECUTE','restaurante','PaymentGatewayRefundEventBind'),
    ('DENY','UPDATE','restaurante','Order');

  INSERT @BoundaryPermissions(PermissionState,PermissionName,SchemaName,ObjectName)
  SELECT 'DENY',permissionInfo.PermissionName,'fidelidad',objectInfo.ObjectName
  FROM (VALUES('INSERT'),('UPDATE'),('DELETE')) permissionInfo(PermissionName)
  CROSS JOIN
  (VALUES
    ('MemberAccount'),('MemberQrToken'),('PointLedger'),
    ('MemberClosureRequest'),('MemberConsent'),('ProgramSettings')
  ) objectInfo(ObjectName);

  IF EXISTS
  (
    SELECT 1 FROM @BoundaryPermissions permissionInfo
    WHERE OBJECT_ID(QUOTENAME(permissionInfo.SchemaName)+N'.'+QUOTENAME(permissionInfo.ObjectName)) IS NULL
  )
    THROW 53751,'El perfil Restaurant v8 menciona un objeto inexistente.',1;

  INSERT orion.PublicPermissionProfileEntry
    (ProfileCode,ProfileVersion,PermissionState,PermissionName,SecurableClass,SchemaName,ObjectName)
  SELECT 'RESTAURANT_PUBLIC',8,permissionInfo.PermissionState,
    permissionInfo.PermissionName,'OBJECT',permissionInfo.SchemaName,permissionInfo.ObjectName
  FROM @BoundaryPermissions permissionInfo
  WHERE NOT EXISTS
  (
    SELECT 1 FROM orion.PublicPermissionProfileEntry existing
    WHERE existing.ProfileCode='RESTAURANT_PUBLIC' AND existing.ProfileVersion=8
      AND existing.PermissionState=permissionInfo.PermissionState
      AND existing.PermissionName=permissionInfo.PermissionName
      AND existing.SecurableClass='OBJECT'
      AND existing.SchemaName=permissionInfo.SchemaName
      AND existing.ObjectName=permissionInfo.ObjectName
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
  WHERE profileInfo.ProfileCode='RESTAURANT_PUBLIC' AND profileInfo.ProfileVersion=8;

  IF EXISTS
  (
    SELECT 1
    FROM orion.PublicSqlPrincipalBinding binding
    JOIN orion.PublicSite publicSite ON publicSite.PublicSiteId=binding.PublicSiteId
    WHERE binding.IsActive=1 AND binding.PermissionProfile='RESTAURANT_PUBLIC'
      AND publicSite.ModuleCode='RESTAURANT'
      AND DATABASE_PRINCIPAL_ID(binding.PrincipalName) IS NULL
  )
    THROW 53752,'Un binding Restaurant activo no tiene principal de base.',1;

  DECLARE @BoundaryPublicSiteKey varchar(100);
  DECLARE @BoundaryPrincipalName sysname;
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
  DECLARE boundary_principal_cursor CURSOR LOCAL FAST_FORWARD FOR
    SELECT publicSite.PublicSiteKey,binding.PrincipalName
    FROM orion.PublicSqlPrincipalBinding binding
    JOIN orion.PublicSite publicSite ON publicSite.PublicSiteId=binding.PublicSiteId
    WHERE binding.IsActive=1 AND binding.PermissionProfile='RESTAURANT_PUBLIC'
      AND publicSite.ModuleCode='RESTAURANT';
  OPEN boundary_principal_cursor;
  FETCH NEXT FROM boundary_principal_cursor INTO @BoundaryPublicSiteKey,@BoundaryPrincipalName;
  WHILE @@FETCH_STATUS=0
  BEGIN
    INSERT @ProfileApplicationReview
      (ApplyChanges,PublicSiteId,ProfileCode,ProfileVersion,Estado,Permiso,Securable,YaExiste)
    EXEC orion.ApplyPublicPermissionProfile
      @PublicSiteKey=@BoundaryPublicSiteKey,
      @PrincipalName=@BoundaryPrincipalName,
      @ApplyChanges=1;
    SET @AppliedBindings=@AppliedBindings+1;
    FETCH NEXT FROM boundary_principal_cursor INTO @BoundaryPublicSiteKey,@BoundaryPrincipalName;
  END;
  CLOSE boundary_principal_cursor;
  DEALLOCATE boundary_principal_cursor;

  IF EXISTS
  (
    SELECT 1
    FROM orion.PublicSqlPrincipalBinding binding
    JOIN orion.PublicSite publicSite ON publicSite.PublicSiteId=binding.PublicSiteId
    WHERE binding.IsActive=1 AND binding.PermissionProfile='RESTAURANT_PUBLIC'
      AND publicSite.ModuleCode='RESTAURANT'
      AND binding.PermissionVersion<>8
  )
    THROW 53753,'No todos los bindings Restaurant recibieron el perfil v8.',1;

  IF EXISTS
  (
    SELECT 1
    FROM orion.PublicSqlPrincipalBinding binding
    JOIN orion.PublicSite publicSite ON publicSite.PublicSiteId=binding.PublicSiteId
    JOIN sys.database_permissions permissionInfo
      ON permissionInfo.grantee_principal_id=DATABASE_PRINCIPAL_ID(binding.PrincipalName)
    WHERE binding.IsActive=1 AND binding.PermissionProfile='RESTAURANT_PUBLIC'
      AND publicSite.ModuleCode='RESTAURANT'
      AND permissionInfo.state IN('G','W')
      AND permissionInfo.permission_name IN('INSERT','UPDATE','DELETE')
      AND
      (
        (permissionInfo.class=3 AND permissionInfo.major_id=SCHEMA_ID('public_identity'))
        OR
        (permissionInfo.class=1
         AND OBJECT_SCHEMA_NAME(permissionInfo.major_id)='fidelidad'
         AND OBJECT_NAME(permissionInfo.major_id) IN
           ('MemberAccount','MemberQrToken','PointLedger','MemberClosureRequest','MemberConsent','ProgramSettings'))
        OR
        (permissionInfo.class=1
         AND permissionInfo.major_id=OBJECT_ID(N'restaurante.[Order]')
         AND permissionInfo.permission_name='UPDATE')
      )
  )
    THROW 53754,'Persisten permisos publicos de escritura fuera de los procedimientos permitidos.',1;

  IF EXISTS
  (
    SELECT 1 FROM @BoundaryPermissions expected
    CROSS JOIN
    (
      SELECT binding.PrincipalName
      FROM orion.PublicSqlPrincipalBinding binding
      JOIN orion.PublicSite publicSite ON publicSite.PublicSiteId=binding.PublicSiteId
      WHERE binding.IsActive=1 AND binding.PermissionProfile='RESTAURANT_PUBLIC'
        AND publicSite.ModuleCode='RESTAURANT'
    ) principalInfo
    WHERE NOT EXISTS
    (
      SELECT 1 FROM sys.database_permissions actual
      WHERE actual.grantee_principal_id=DATABASE_PRINCIPAL_ID(principalInfo.PrincipalName)
        AND actual.class=1
        AND actual.major_id=OBJECT_ID(QUOTENAME(expected.SchemaName)+N'.'+QUOTENAME(expected.ObjectName))
        AND actual.permission_name COLLATE DATABASE_DEFAULT=expected.PermissionName
        AND actual.state=CASE expected.PermissionState WHEN 'GRANT' THEN 'G' ELSE 'D' END
    )
  )
    THROW 53755,'No quedaron aplicados todos los permisos del perfil Restaurant v8.',1;

  DECLARE @MigrationBrunoPublicSiteId bigint;
  DECLARE @MigrationBrunoCompanyId bigint;
  DECLARE @MigrationBrunoSiteId bigint;
  SELECT @MigrationBrunoPublicSiteId=publicSite.PublicSiteId,
    @MigrationBrunoCompanyId=publicSite.CompanyId,
    @MigrationBrunoSiteId=publicSite.SiteId
  FROM orion.PublicSite publicSite
  JOIN orion.Company companyInfo ON companyInfo.CompanyId=publicSite.CompanyId
  WHERE publicSite.PublicSiteKey='brunos-main'
    AND publicSite.ModuleCode='RESTAURANT'
    AND publicSite.IsActive=1
    AND companyInfo.Rfc='BRUNOS260707L26'
    AND companyInfo.IsActive=1;
  IF @MigrationBrunoPublicSiteId IS NULL
    THROW 53756,'No se resolvio el alcance central activo de Bruno.',1;

  EXEC sys.sp_set_session_context @key=N'OrionERP.PublicSiteId',@value=@MigrationBrunoPublicSiteId;
  EXEC sys.sp_set_session_context @key=N'OrionERP.CompanyId',@value=@MigrationBrunoCompanyId;
  EXEC sys.sp_set_session_context @key=N'OrionERP.SiteId',@value=@MigrationBrunoSiteId;
  EXEC sys.sp_set_session_context @key=N'OrionRfc',@value='BRUNOS260707L26';

  UPDATE settings
  SET TermsVersion='2026-09-14',PrivacyVersion='2026-09-14',
      ConfigurationVersion=settings.ConfigurationVersion+
        CASE WHEN settings.TermsVersion<>'2026-09-14'
               OR settings.PrivacyVersion<>'2026-09-14' THEN 1 ELSE 0 END,
      UpdatedAtUtc=SYSUTCDATETIME(),
      UpdatedBy=N'20260914_restaurant_online_ordering_runtime_corrections'
  FROM restaurante.OnlineOrderingSettings settings
  JOIN orion.PublicSite publicSite ON publicSite.PublicSiteId=settings.PublicSiteId
  WHERE publicSite.PublicSiteKey='brunos-main' AND settings.Rfc='BRUNOS260707L26';
  IF @@ROWCOUNT<>1
    THROW 53738,'No se resolvio exactamente la configuracion de Bruno.',1;

  IF NOT EXISTS
  (
    SELECT 1 FROM sys.parameters
    WHERE object_id=OBJECT_ID(N'restaurante.OnlineOrderingRuntimeReadinessSet')
      AND name=N'@TermsVersion'
  ) OR NOT EXISTS
  (
    SELECT 1 FROM sys.parameters
    WHERE object_id=OBJECT_ID(N'restaurante.OnlineOrderingAdminSaveV2')
      AND name=N'@ExpectedConfigurationVersion'
  ) OR NOT EXISTS
  (
    SELECT 1 FROM sys.parameters
    WHERE object_id=OBJECT_ID(N'restaurante.OnlineCheckoutAttemptCreate')
      AND name=N'@SettingsConfigurationVersion'
  ) OR NOT EXISTS
  (
    SELECT 1 FROM sys.parameters
    WHERE object_id=OBJECT_ID(N'restaurante.OnlineCheckoutCaptureAuthorize')
      AND name=N'@ProcessorHeartbeatMaxAgeSeconds'
  ) OR COL_LENGTH(N'restaurante.OnlineOrderNotification',N'ProviderMessageId') IS NULL
    OR COL_LENGTH(N'restaurante.PaymentGatewayRefund',N'ProviderGrossAmount') IS NULL
    OR COL_LENGTH(N'restaurante.PaymentGatewayRefund',N'ProviderFeeAmount') IS NULL
    OR COL_LENGTH(N'restaurante.PaymentGatewayRefund',N'ProviderNetAmount') IS NULL
    OR COL_LENGTH(N'restaurante.PaymentGatewayRefund',N'ReconciledAtUtc') IS NULL
    OR COL_LENGTH(N'restaurante.PaymentGatewayEvent',N'RelatedOrderId') IS NULL
    OR COL_LENGTH(N'restaurante.PaymentGatewayEvent',N'RelatedCaptureId') IS NULL
    OR COL_LENGTH(N'restaurante.PaymentGatewayEvent',N'RelatedRefundId') IS NULL
    OR COL_LENGTH(N'restaurante.OnlineCheckoutAttempt',N'SettingsConfigurationVersion') IS NULL
    OR OBJECT_ID(N'restaurante.OnlineOrderNotificationProviderMessageSet',N'P') IS NULL
    OR OBJECT_ID(N'restaurante.PaymentGatewayRefundEventBind',N'P') IS NULL
    OR OBJECT_ID(N'restaurante.PublicMemberCreate',N'P') IS NULL
    OR OBJECT_ID(N'restaurante.PublicMemberVerificationUpdate',N'P') IS NULL
    OR OBJECT_ID(N'restaurante.PublicMemberQrIssue',N'P') IS NULL
    OR OBJECT_ID(N'restaurante.PublicMemberConsentsUpdate',N'P') IS NULL
    OR OBJECT_ID(N'restaurante.PublicMemberClosureRequest',N'P') IS NULL
    OR NOT EXISTS
  (
    SELECT 1 FROM orion.PublicPermissionProfile
    WHERE ProfileCode='RESTAURANT_PUBLIC' AND ProfileVersion=8
      AND ProfileChecksum IS NOT NULL
  )
    OR NOT EXISTS
  (
    SELECT 1 FROM sys.columns
    WHERE object_id=OBJECT_ID(N'restaurante.OnlineOrderNotification')
      AND name=N'ProviderMessageId' AND max_length=1024
  )
    OR NOT EXISTS
  (
    SELECT 1 FROM sys.indexes
    WHERE object_id=OBJECT_ID(N'restaurante.PaymentGatewayRefund')
      AND name=N'UX_PaymentGatewayRefund_UnresolvedTransaction'
      AND is_unique=1 AND has_filter=1
  )
    OR NOT EXISTS
  (
    SELECT 1 FROM sys.parameters
    WHERE object_id=OBJECT_ID(N'restaurante.PaymentGatewayRefundResult')
      AND name=N'@ProviderGrossAmount'
  )
    OR CHARINDEX(N'eventInfo.ProcessingStatus=''Processing''',
      COALESCE(OBJECT_DEFINITION(OBJECT_ID(N'restaurante.PayPalRecoveryClaim')),N''))=0
    OR CHARINDEX(N'refundInfo.[Status]=''Processing''',
      COALESCE(OBJECT_DEFINITION(OBJECT_ID(N'restaurante.PaymentGatewayRefundClaim')),N''))=0
    OR CHARINDEX(N'IF @Outcome=''Completed'' AND @ProviderRefundId IS NULL',
      COALESCE(OBJECT_DEFINITION(OBJECT_ID(N'restaurante.PaymentGatewayRefundResult')),N''))=0
    OR CHARINDEX(N'CASE WHEN @ProviderRefundId IS NOT NULL',
      COALESCE(OBJECT_DEFINITION(OBJECT_ID(N'restaurante.PaymentGatewayRefundResult')),N''))=0
    OR CHARINDEX(N'ProviderGrossAmount=CASE WHEN @ProviderGrossAmount IS NOT NULL',
      COALESCE(OBJECT_DEFINITION(OBJECT_ID(N'restaurante.PaymentGatewayRefundResult')),N''))=0
    OR CHARINDEX(N'AND [Status]<>''Failed''',
      COALESCE(OBJECT_DEFINITION(OBJECT_ID(N'restaurante.PaymentGatewayRefundRequest')),N''))>0
    OR CHARINDEX(N'UX_PaymentGatewayRefund_UnresolvedTransaction',
      COALESCE(OBJECT_DEFINITION(OBJECT_ID(N'restaurante.PaymentGatewayRefundRequest')),N''))=0
    OR CHARINDEX(N'eventInfo.RelatedRefundId',
      COALESCE(OBJECT_DEFINITION(OBJECT_ID(N'restaurante.PayPalRecoveryClaim')),N''))=0
    OR CHARINDEX(N'PAYMENT.REFUND.%',
      COALESCE(OBJECT_DEFINITION(OBJECT_ID(N'restaurante.PaymentGatewayRefundEventBind')),N''))=0
    OR CHARINDEX(N'attempt.SettingsConfigurationVersion=@SettingsConfigurationVersion',
      COALESCE(OBJECT_DEFINITION(OBJECT_ID(N'restaurante.OnlineCheckoutAttemptCreate')),N''))=0
    OR CHARINDEX(N'@QuoteExpiresAtUtc<=@Now',
      COALESCE(OBJECT_DEFINITION(OBJECT_ID(N'restaurante.OnlineCheckoutCaptureAuthorize')),N''))=0
    OR CHARINDEX(N'@AttemptConfigurationVersion<>@CurrentConfigurationVersion',
      COALESCE(OBJECT_DEFINITION(OBJECT_ID(N'restaurante.OnlineCheckoutCaptureAuthorize')),N''))=0
    OR CHARINDEX(N'OPENJSON(@WeeklyScheduleJson',
      COALESCE(OBJECT_DEFINITION(OBJECT_ID(N'restaurante.OnlineCheckoutCaptureAuthorize')),N''))=0
    OR CHARINDEX(N'WHEN transactionInfo.[Status]=''PartiallyRefunded'' THEN ''PartiallyRefunded''',
      COALESCE(OBJECT_DEFINITION(OBJECT_ID(N'restaurante.OnlineCheckoutStatusGet')),N''))=0
    OR CHARINDEX(N'notificationFailure.NotificationId IS NOT NULL',
      COALESCE(OBJECT_DEFINITION(OBJECT_ID(N'restaurante.OnlineOrderingRecoveryList')),N''))=0
    OR EXISTS
  (
    SELECT 1 FROM orion.PublicPermissionProfileEntry entryInfo
    WHERE entryInfo.ProfileCode='RESTAURANT_PUBLIC' AND entryInfo.ProfileVersion=8
      AND entryInfo.PermissionState='GRANT'
      AND entryInfo.PermissionName IN('INSERT','UPDATE','DELETE')
      AND
      (
        (entryInfo.SecurableClass='SCHEMA' AND entryInfo.SchemaName='public_identity')
        OR (entryInfo.SecurableClass='OBJECT' AND entryInfo.SchemaName='fidelidad')
        OR (entryInfo.SecurableClass='OBJECT' AND entryInfo.SchemaName='restaurante'
            AND entryInfo.ObjectName='Order' AND entryInfo.PermissionName='UPDATE')
      )
  )
    THROW 53739,'No quedaron instalados los contratos corregidos.',1;

  SELECT DB_NAME() DatabaseName,@ApplyChanges ApplyChanges,
    settings.PublicSiteId,settings.TermsVersion,settings.PrivacyVersion,
    settings.ConfigurationVersion,
    OBJECT_ID(N'restaurante.OnlineOrderingAdminSaveV2') AdminSaveV2ObjectId,
    OBJECT_ID(N'restaurante.OnlineOrderNotificationProviderMessageSet') NotificationProviderSetObjectId,
    OBJECT_ID(N'restaurante.PublicMemberCreate') PublicMemberCreateObjectId,
    8 RestaurantPublicPermissionVersion,
    @AppliedBindings UpdatedPublicBindings
  FROM restaurante.OnlineOrderingSettings settings
  JOIN orion.PublicSite publicSite ON publicSite.PublicSiteId=settings.PublicSiteId
  WHERE publicSite.PublicSiteKey='brunos-main' AND settings.Rfc='BRUNOS260707L26';

  EXEC sys.sp_set_session_context @key=N'OrionERP.PublicSiteId',@value=NULL;
  EXEC sys.sp_set_session_context @key=N'OrionERP.CompanyId',@value=NULL;
  EXEC sys.sp_set_session_context @key=N'OrionERP.SiteId',@value=NULL;
  EXEC sys.sp_set_session_context @key=N'OrionRfc',@value=NULL;

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
  IF CURSOR_STATUS('local','boundary_principal_cursor')>=-1
  BEGIN
    IF CURSOR_STATUS('local','boundary_principal_cursor')>-1 CLOSE boundary_principal_cursor;
    DEALLOCATE boundary_principal_cursor;
  END;
  IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
  EXEC sys.sp_set_session_context @key=N'OrionERP.PublicSiteId',@value=NULL;
  EXEC sys.sp_set_session_context @key=N'OrionERP.CompanyId',@value=NULL;
  EXEC sys.sp_set_session_context @key=N'OrionERP.SiteId',@value=NULL;
  EXEC sys.sp_set_session_context @key=N'OrionRfc',@value=NULL;
  THROW;
END CATCH;
