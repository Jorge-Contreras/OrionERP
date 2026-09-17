/*
  Moves the Restaurant online checkout from PayPal to Clip.

  PayPal could never complete a card payment on this site: the shared REST app
  reports card.guestEnabled=false at the account level, so the card button drew
  but no card payment could finish. restaurante.PaymentGatewayTransaction is
  empty in production, which means no PayPal payment was ever captured and this
  can be a clean replacement instead of a two-provider coexistence.

  Two things change shape, not just names:

  1. Clip has no "create order" step. PayPal handed out an order id before the
     customer paid; Clip creates nothing until the charge. The provider columns
     stop being PayPal-specific and are only filled at charge time, both with the
     same Clip payment id.

  2. POST /payments accepts no idempotency key, so a retried charge would charge
     twice. OnlineCheckoutChargeBegin makes the Quoted -> ChargePending
     transition the exclusive right to charge: whoever loses it never calls Clip,
     and a charge that leaves no usable answer lands in ChargeUnknown for the
     recovery worker to resolve by lookup instead of by charging again.
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
  IF @ApplyInput NOT IN(N'0',N'1') THROW 54010,'ApplyChanges debe ser 0 o 1.',1;
  SET @ApplyChanges=CONVERT(bit,@ApplyInput);
END;
IF @ExpectedDatabase LIKE N'$'+N'(%'
   OR @ExpectedDatabase NOT IN(N'Orion_Sandbox',N'grupocarpio')
  THROW 54011,'Base esperada no autorizada para la migracion a Clip.',1;
IF DB_NAME()<>@ExpectedDatabase
  THROW 54012,'La conexion no apunta a la base declarada.',1;
IF @MigrationId<>N'20260916_restaurant_online_ordering_clip'
  THROW 54013,'MigrationId incorrecto.',1;
IF @MigrationChecksum LIKE '$'+N'(%' OR LEN(@MigrationChecksum)<>64
   OR @MigrationChecksum LIKE '%[^0-9A-F]%' COLLATE Latin1_General_100_BIN2
  THROW 54014,'MigrationChecksum invalido.',1;
IF @AppVersionInput NOT LIKE N'$'+N'(%'
  SET @AppVersion=NULLIF(LTRIM(RTRIM(@AppVersionInput)),N'');

IF OBJECT_ID(N'orion.SchemaMigration',N'U') IS NULL
   OR OBJECT_ID(N'restaurante.OnlineCheckoutAttempt',N'U') IS NULL
   OR OBJECT_ID(N'restaurante.PaymentGatewayTransaction',N'U') IS NULL
   OR OBJECT_ID(N'restaurante.PaymentGatewayEvent',N'U') IS NULL
   OR OBJECT_ID(N'restaurante.OnlineOrderingSettings',N'U') IS NULL
  THROW 54015,'Faltan objetos requeridos para migrar el checkout a Clip.',1;

IF NOT EXISTS
(
  SELECT 1 FROM orion.SchemaMigration
  WHERE MigrationId=N'20260914_restaurant_online_ordering_tracking_ready_email_status'
    AND UPPER(Checksum)='8F54CFA571AAB178A24D21CBB91198DB2183EF9375813DC4E0D638748D087E6B'
)
  THROW 54016,'El checkout en linea no esta en la version revisada previa a Clip.',1;

-- El perfil publico v11 de esta migracion parte de v10, asi que el permiso de
-- logistica.MaterialCategory tiene que estar aplicado antes.
IF NOT EXISTS
(
  SELECT 1 FROM orion.PublicPermissionProfile
  WHERE ProfileCode='RESTAURANT_PUBLIC' AND ProfileVersion=10 AND ProfileChecksum IS NOT NULL
)
  THROW 54009,'Falta el perfil publico Restaurant v10.',1;

-- Un pago capturado con PayPal tendria que seguir siendo reembolsable con las
-- credenciales de PayPal, y esta migracion retira esa ruta. Solo procede si de
-- verdad no hay dinero cobrado con el proveedor anterior.
IF EXISTS(SELECT 1 FROM restaurante.PaymentGatewayTransaction WHERE Provider=N'PayPal')
  THROW 54017,'Existen capturas PayPal: la migracion a Clip requiere conservar su ruta de reembolso.',1;

DECLARE @ExistingChecksum char(64)=
  (SELECT Checksum FROM orion.SchemaMigration WHERE MigrationId=@MigrationId);
IF @ExistingChecksum IS NOT NULL AND UPPER(@ExistingChecksum)<>UPPER(@MigrationChecksum)
  THROW 54018,'El mismo MigrationId ya existe con otro checksum.',1;
IF @ExistingChecksum IS NOT NULL
BEGIN
  SELECT N'YA_APLICADO' Estado,@MigrationId MigrationId,@ExistingChecksum Checksum;
  RETURN;
END;

BEGIN TRY
  BEGIN TRANSACTION;

  DECLARE @LockResult int;
  EXEC @LockResult=sys.sp_getapplock
    @Resource=N'OrionERP:Restaurant:OnlineOrdering:ClipMigration',
    @LockMode=N'Exclusive',@LockOwner=N'Transaction',@LockTimeout=30000;
  IF @LockResult<0 THROW 54019,'No fue posible obtener el bloqueo de la migracion.',1;

  /* ---------- 1. Columnas neutrales de proveedor ---------- */

  -- Los indices filtrados y los CHECK guardan el nombre de la columna como
  -- texto, asi que se retiran antes del rename y se reconstruyen despues.
  IF EXISTS(SELECT 1 FROM sys.indexes
            WHERE object_id=OBJECT_ID(N'restaurante.OnlineCheckoutAttempt')
              AND name=N'UX_OnlineCheckoutAttempt_PayPalOrder')
    DROP INDEX UX_OnlineCheckoutAttempt_PayPalOrder ON restaurante.OnlineCheckoutAttempt;
  IF EXISTS(SELECT 1 FROM sys.indexes
            WHERE object_id=OBJECT_ID(N'restaurante.OnlineCheckoutAttempt')
              AND name=N'UX_OnlineCheckoutAttempt_PayPalCapture')
    DROP INDEX UX_OnlineCheckoutAttempt_PayPalCapture ON restaurante.OnlineCheckoutAttempt;
  IF EXISTS(SELECT 1 FROM sys.indexes
            WHERE object_id=OBJECT_ID(N'restaurante.OnlineCheckoutAttempt')
              AND name=N'IX_OnlineCheckoutAttempt_Recovery')
    DROP INDEX IX_OnlineCheckoutAttempt_Recovery ON restaurante.OnlineCheckoutAttempt;
  IF OBJECT_ID(N'restaurante.CK_OnlineCheckoutAttempt_ProviderIdentity',N'C') IS NOT NULL
    ALTER TABLE restaurante.OnlineCheckoutAttempt
      DROP CONSTRAINT CK_OnlineCheckoutAttempt_ProviderIdentity;
  IF OBJECT_ID(N'restaurante.CK_OnlineCheckoutAttempt_State',N'C') IS NOT NULL
    ALTER TABLE restaurante.OnlineCheckoutAttempt
      DROP CONSTRAINT CK_OnlineCheckoutAttempt_State;

  IF COL_LENGTH(N'restaurante.OnlineCheckoutAttempt',N'PayPalCreateRequestId') IS NOT NULL
    EXEC sys.sp_rename
      @objname=N'restaurante.OnlineCheckoutAttempt.PayPalCreateRequestId',
      @newname=N'ProviderRequestId',@objtype=N'COLUMN';
  IF COL_LENGTH(N'restaurante.OnlineCheckoutAttempt',N'PayPalOrderId') IS NOT NULL
    EXEC sys.sp_rename
      @objname=N'restaurante.OnlineCheckoutAttempt.PayPalOrderId',
      @newname=N'ProviderOrderId',@objtype=N'COLUMN';
  IF COL_LENGTH(N'restaurante.OnlineCheckoutAttempt',N'PayPalCaptureId') IS NOT NULL
    EXEC sys.sp_rename
      @objname=N'restaurante.OnlineCheckoutAttempt.PayPalCaptureId',
      @newname=N'ProviderCaptureId',@objtype=N'COLUMN';

  IF COL_LENGTH(N'restaurante.OnlineCheckoutAttempt',N'Provider') IS NULL
  BEGIN
    ALTER TABLE restaurante.OnlineCheckoutAttempt
      ADD Provider varchar(20) NOT NULL
        CONSTRAINT DF_OnlineCheckoutAttempt_Provider DEFAULT(N'Clip');
  END;

  -- A partir de aqui todo lo que nombre una columna nueva o renombrada va por
  -- EXEC: SQL Server compila el lote completo antes de ejecutarlo, asi que una
  -- referencia directa fallaria con "Invalid column name" pese a que el ALTER de
  -- arriba ya corrio.
  -- Los intentos que ya existian son de la era PayPal, sin importar que nunca
  -- llegaran a cobrar.
  EXEC(N'
UPDATE restaurante.OnlineCheckoutAttempt
SET Provider=N''PayPal''
WHERE Provider=N''Clip''
  AND (ProviderOrderId IS NOT NULL OR [State]=N''PayPalCreated'' OR [State]=N''CapturePending'');');

  -- Un ChargeRequestId sella quien gano el derecho exclusivo a cobrar. Existe
  -- para que un cargo perdido se pueda distinguir de uno que nunca arranco.
  IF COL_LENGTH(N'restaurante.OnlineCheckoutAttempt',N'ChargeRequestId') IS NULL
    ALTER TABLE restaurante.OnlineCheckoutAttempt ADD ChargeRequestId uniqueidentifier NULL;
  IF COL_LENGTH(N'restaurante.OnlineCheckoutAttempt',N'ChargeStartedAtUtc') IS NULL
    ALTER TABLE restaurante.OnlineCheckoutAttempt ADD ChargeStartedAtUtc datetime2(3) NULL;

  /* ---------- 2. Estados del nuevo ciclo de cobro ---------- */

  -- PayPalCreated y CapturePending se conservan permitidos para que las filas
  -- historicas sigan validando; el codigo nuevo ya no los escribe.
  ALTER TABLE restaurante.OnlineCheckoutAttempt WITH CHECK
    ADD CONSTRAINT CK_OnlineCheckoutAttempt_State CHECK([State] IN
      (N'Quoted',N'ChargePending',N'Authenticating3ds',N'ChargeUnknown',
       N'Captured',N'PosCreated',N'RequoteRequired',N'PaymentDenied',
       N'CapturedNeedsOrder',N'RefundRequested',N'RefundPending',N'Refunded',
       N'Expired',N'Failed',
       N'PayPalCreated',N'CapturePending'));

  EXEC(N'
ALTER TABLE restaurante.OnlineCheckoutAttempt WITH CHECK
  ADD CONSTRAINT CK_OnlineCheckoutAttempt_Provider CHECK(Provider IN(N''Clip'',N''PayPal''));');

  -- En Clip el identificador del pago llega hasta el cargo, y orden y captura
  -- son el mismo valor. Lo que importa es que el sello de la solicitud y el
  -- identificador aparezcan juntos.
  EXEC(N'
ALTER TABLE restaurante.OnlineCheckoutAttempt WITH CHECK
  ADD CONSTRAINT CK_OnlineCheckoutAttempt_ProviderIdentity CHECK
    ((ProviderOrderId IS NULL AND ProviderRequestId IS NULL)
     OR (ProviderOrderId IS NOT NULL AND ProviderRequestId IS NOT NULL));');

  EXEC(N'
CREATE UNIQUE INDEX UX_OnlineCheckoutAttempt_ProviderOrder
  ON restaurante.OnlineCheckoutAttempt(MerchantProfileKey,ProviderOrderId)
  WHERE ProviderOrderId IS NOT NULL;');
  EXEC(N'
CREATE UNIQUE INDEX UX_OnlineCheckoutAttempt_ProviderCapture
  ON restaurante.OnlineCheckoutAttempt(MerchantProfileKey,ProviderCaptureId)
  WHERE ProviderCaptureId IS NOT NULL;');
  EXEC(N'
CREATE INDEX IX_OnlineCheckoutAttempt_Recovery
  ON restaurante.OnlineCheckoutAttempt(PublicSiteId,[State],NextRetryAtUtc,CreatedAtUtc)
  INCLUDE(Id,Provider,ProviderOrderId,ProviderCaptureId,ImportAttempts,ImportLeaseExpiresAtUtc);');

  /* ---------- 3. Clip como proveedor de cobro ---------- */

  IF OBJECT_ID(N'restaurante.CK_PaymentGatewayTransaction_Provider',N'C') IS NOT NULL
    ALTER TABLE restaurante.PaymentGatewayTransaction
      DROP CONSTRAINT CK_PaymentGatewayTransaction_Provider;
  ALTER TABLE restaurante.PaymentGatewayTransaction WITH CHECK
    ADD CONSTRAINT CK_PaymentGatewayTransaction_Provider CHECK(Provider IN(N'Clip',N'PayPal'));

  /* ---------- 4. Cerrar los intentos de la era PayPal ---------- */

  -- Ninguno puede prosperar: sus ordenes de PayPal quedaron abandonadas y el
  -- cobro con tarjeta nunca fue posible en esa cuenta.
  EXEC(N'
UPDATE restaurante.OnlineCheckoutAttempt
SET [State]=N''Expired'',
    FailureCode=N''PROVIDER_MIGRATED'',
    FailureMessage=N''El intento quedo abierto con PayPal, que no podia cobrar tarjetas en esta cuenta.'',
    NextRetryAtUtc=NULL,
    RecoveryLeaseId=NULL,
    RecoveryLeaseExpiresAtUtc=NULL,
    UpdatedAtUtc=SYSUTCDATETIME()
WHERE Provider=N''PayPal''
  AND [State] IN(N''Quoted'',N''PayPalCreated'',N''CapturePending'',N''RequoteRequired'');');

  /* ---------- 5. Reserva del intento ---------- */

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
  @TrackingTokenHash binary(32),
  @Provider varchar(20)=''Clip''
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
  IF @Provider<>''Clip''
    THROW 54020,''Los intentos nuevos solo se cobran con Clip.'',1;

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
        AND attempt.Provider=@Provider
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
      MerchantProfileKey,TrackingTokenHash,Provider
    )
    VALUES
    (
      @Id,@PublicSiteId,@Rfc,@SiteId,@ClientAttemptId,@MemberId,
      LTRIM(RTRIM(@CustomerName)),LOWER(LTRIM(RTRIM(@CustomerEmail))),LTRIM(RTRIM(@CustomerPhone)),
      @QuoteFingerprint,@QuoteExpiresAtUtc,@SettingsConfigurationVersion,@CartSnapshotJson,
      NULLIF(LTRIM(RTRIM(@PromotionCode)),''''),
      @Subtotal,@PromotionDiscountTotal,@TaxTotal,@Total,@CurrencyCode,
      @PrivacyVersion,@TermsVersion,@LegalAcceptedAtUtc,@MerchantProfileKey,
      @TrackingTokenHash,@Provider
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
    attempt.Provider,attempt.ProviderRequestId,attempt.ProviderOrderId,attempt.ProviderCaptureId,
    attempt.ChargeRequestId,attempt.ChargeStartedAtUtc,
    attempt.[State],attempt.RestaurantOrderId,attempt.ImportAttempts,
    attempt.NextRetryAtUtc,attempt.FailureCode,attempt.FailureMessage,
    attempt.CreatedAtUtc,attempt.UpdatedAtUtc,attempt.CapturedAtUtc,
    attempt.PosCreatedAtUtc,attempt.RowVersion
  FROM restaurante.OnlineCheckoutAttempt attempt
  WHERE attempt.Id=@Id AND attempt.PublicSiteId=@PublicSiteId AND attempt.Rfc=@Rfc;
END;');

  EXEC(N'
CREATE OR ALTER PROCEDURE restaurante.OnlineCheckoutAttemptGet
  @Id uniqueidentifier=NULL,
  @ClientAttemptId uniqueidentifier=NULL,
  @TrackingTokenHash binary(32)=NULL,
  @ProviderOrderId varchar(64)=NULL,
  @ProviderCaptureId varchar(64)=NULL,
  @MerchantProfileKey varchar(80)=NULL
AS
BEGIN
  SET NOCOUNT ON;

  DECLARE @PublicSiteId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N''OrionERP.PublicSiteId''));
  DECLARE @Rfc varchar(50)=CONVERT(varchar(50),SESSION_CONTEXT(N''OrionRfc''));
  DECLARE @Selectors int=
    CASE WHEN @Id IS NULL THEN 0 ELSE 1 END+
    CASE WHEN @ClientAttemptId IS NULL THEN 0 ELSE 1 END+
    CASE WHEN @TrackingTokenHash IS NULL THEN 0 ELSE 1 END+
    CASE WHEN @ProviderOrderId IS NULL AND @ProviderCaptureId IS NULL THEN 0 ELSE 1 END;

  IF @PublicSiteId IS NULL OR NULLIF(@Rfc,'''') IS NULL
    THROW 53631,''No existe un contexto publico de restaurante verificado.'',1;
  IF @Selectors<>1
    THROW 53632,''Debe especificarse exactamente un selector de checkout.'',1;
  IF (@ProviderOrderId IS NOT NULL OR @ProviderCaptureId IS NOT NULL)
     AND NULLIF(@MerchantProfileKey,'''') IS NULL
    THROW 53633,''MerchantProfileKey es obligatorio para buscar IDs del proveedor.'',1;

  SELECT
    attempt.Id,attempt.PublicSiteId,attempt.Rfc,attempt.SiteId,attempt.ClientAttemptId,
    attempt.MemberId,attempt.CustomerName,attempt.CustomerEmail,attempt.CustomerPhone,
    attempt.QuoteFingerprint,attempt.QuoteExpiresAtUtc,attempt.CartSnapshotJson,
    attempt.PromotionCode,attempt.Subtotal,attempt.PromotionDiscountTotal,attempt.TaxTotal,
    attempt.Total,attempt.CurrencyCode,attempt.PrivacyVersion,attempt.TermsVersion,
    attempt.LegalAcceptedAtUtc,attempt.MerchantProfileKey,attempt.TrackingTokenHash,
    attempt.Provider,attempt.ProviderRequestId,attempt.ProviderOrderId,attempt.ProviderCaptureId,
    attempt.ChargeRequestId,attempt.ChargeStartedAtUtc,
    attempt.[State],attempt.RestaurantOrderId,
    attempt.ImportAttempts,attempt.ImportLeaseId,attempt.ImportLeaseExpiresAtUtc,
    attempt.NextRetryAtUtc,attempt.FailureCode,attempt.FailureMessage,
    attempt.CreatedAtUtc,attempt.UpdatedAtUtc,attempt.CapturedAtUtc,attempt.PosCreatedAtUtc,
    orderInfo.Folio OrderFolio,attempt.RowVersion
  FROM restaurante.OnlineCheckoutAttempt attempt
  LEFT JOIN restaurante.[Order] orderInfo
    ON orderInfo.PublicSiteId=attempt.PublicSiteId AND orderInfo.Rfc=attempt.Rfc
   AND orderInfo.SiteId=attempt.SiteId AND orderInfo.Id=attempt.RestaurantOrderId
  WHERE attempt.PublicSiteId=@PublicSiteId AND attempt.Rfc=@Rfc
    AND
    (
      (@Id IS NOT NULL AND attempt.Id=@Id)
      OR (@ClientAttemptId IS NOT NULL AND attempt.ClientAttemptId=@ClientAttemptId)
      OR (@TrackingTokenHash IS NOT NULL AND attempt.TrackingTokenHash=@TrackingTokenHash)
      OR
      (
        @MerchantProfileKey IS NOT NULL
        AND attempt.MerchantProfileKey=@MerchantProfileKey
        AND
        (
          (@ProviderOrderId IS NOT NULL AND attempt.ProviderOrderId=@ProviderOrderId)
          OR (@ProviderCaptureId IS NOT NULL AND attempt.ProviderCaptureId=@ProviderCaptureId)
        )
      )
    );
END;');

  /* ---------- 6. Derecho exclusivo a cobrar ---------- */

  -- Clip no acepta llave de idempotencia en POST /payments, asi que la
  -- exclusividad la da la base: solo la transicion a ChargePending habilita un
  -- cargo. Un intento que ya esta cobrando o autenticando 3DS no se puede volver
  -- a cobrar, porque el pago anterior aun podria prosperar y cobrarian los dos.
  EXEC(N'
CREATE OR ALTER PROCEDURE restaurante.OnlineCheckoutChargeBegin
  @Id uniqueidentifier,
  @ChargeRequestId uniqueidentifier,
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
  DECLARE @AttemptProvider varchar(20);
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
  IF @ChargeRequestId IS NULL
    THROW 54029,''El sello del cargo es obligatorio.'',1;
  IF @ProcessorHeartbeatMaxAgeSeconds NOT BETWEEN 15 AND 300
    THROW 53770,''La vigencia del heartbeat es invalida.'',1;

  BEGIN TRANSACTION;

  SELECT @AttemptFound=1,@AttemptState=attempt.[State],@AttemptProvider=attempt.Provider,
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
  WHERE attempt.Id=@Id AND attempt.PublicSiteId=@PublicSiteId AND attempt.Rfc=@Rfc;

  -- El estado se evalua aparte para que el servicio distinga un cargo ya en
  -- vuelo de un bloqueo por configuracion, y no invite a reintentar el primero.
  IF @AttemptFound=0 OR @AttemptProvider<>''Clip''
     OR @AttemptState NOT IN(''Quoted'',''PaymentDenied'')
  BEGIN
    ROLLBACK TRANSACTION;
    THROW 54030,''El intento no admite un cargo nuevo.'',1;
  END;

  IF @QuoteExpiresAtUtc<=@Now
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
    THROW 54031,''El cargo esta bloqueado por vigencia, configuracion o readiness.'',1;
  END;

  BEGIN TRY
    SET @LocalNow=CONVERT(datetime2(0),(@Now AT TIME ZONE ''UTC'') AT TIME ZONE @TimeZoneId);
  END TRY
  BEGIN CATCH
    IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
    THROW 54031,''La zona horaria impide validar el horario del cargo.'',1;
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
    THROW 54031,''El cargo esta fuera del horario vigente de pedidos.'',1;
  END;

  -- Un reintento tras un rechazo definitivo estrena pago en Clip, asi que los
  -- identificadores del intento anterior se liberan; el rechazo queda en la
  -- bitacora de eventos, no en la fila del intento.
  UPDATE restaurante.OnlineCheckoutAttempt
  SET [State]=''ChargePending'',
      ChargeRequestId=@ChargeRequestId,
      ChargeStartedAtUtc=@Now,
      ProviderRequestId=NULL,
      ProviderOrderId=NULL,
      ProviderCaptureId=NULL,
      -- La recuperacion no debe tocar un cargo que aun esta en vuelo: esta
      -- ventana tiene que cubrir el timeout HTTP del cobro con holgura.
      NextRetryAtUtc=DATEADD(SECOND,120,@Now),
      FailureCode=NULL,FailureMessage=NULL,UpdatedAtUtc=@Now
  WHERE Id=@Id AND PublicSiteId=@PublicSiteId AND Rfc=@Rfc
    AND [State] IN(''Quoted'',''PaymentDenied'');

  IF @@ROWCOUNT<>1
  BEGIN
    ROLLBACK TRANSACTION;
    THROW 54030,''Otro cargo tomo este intento primero.'',1;
  END;

  COMMIT TRANSACTION;

  SELECT attempt.Id,attempt.Provider,attempt.MerchantProfileKey,
    attempt.ChargeRequestId,attempt.ChargeStartedAtUtc,
    attempt.QuoteFingerprint,attempt.Total,attempt.CurrencyCode,attempt.[State],
    attempt.CustomerName,attempt.CustomerEmail,attempt.CustomerPhone,
    attempt.SettingsConfigurationVersion,attempt.QuoteExpiresAtUtc,
    attempt.TermsVersion,attempt.PrivacyVersion,
    attempt.UpdatedAtUtc,attempt.RowVersion
  FROM restaurante.OnlineCheckoutAttempt attempt
  WHERE attempt.Id=@Id AND attempt.PublicSiteId=@PublicSiteId AND attempt.Rfc=@Rfc;
END;');

  /* ---------- 7. Desenlace del cargo ---------- */

  EXEC(N'
CREATE OR ALTER PROCEDURE restaurante.OnlineCheckoutChargeResult
  @Id uniqueidentifier,
  @ChargeRequestId uniqueidentifier,
  @Outcome varchar(20),
  @ProviderPaymentId varchar(64)=NULL,
  @ExternalReference varchar(100)=NULL,
  @GrossAmount decimal(18,2)=NULL,
  @CurrencyCode char(3)=NULL,
  @FailureCode varchar(80)=NULL,
  @FailureMessage nvarchar(500)=NULL,
  @CapturedAtUtc datetime2(3)=NULL
AS
BEGIN
  SET NOCOUNT ON;
  SET XACT_ABORT ON;

  DECLARE @PublicSiteId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N''OrionERP.PublicSiteId''));
  DECLARE @Rfc varchar(50)=CONVERT(varchar(50),SESSION_CONTEXT(N''OrionRfc''));
  DECLARE @Now datetime2(3)=SYSUTCDATETIME();
  DECLARE @SiteId int;
  DECLARE @MerchantProfileKey varchar(80);
  DECLARE @ExpectedAmount decimal(18,2);
  DECLARE @ExpectedCurrency char(3);
  DECLARE @CurrentState varchar(30);
  DECLARE @CurrentChargeRequestId uniqueidentifier;
  DECLARE @CurrentPaymentId varchar(64);
  DECLARE @TransactionId uniqueidentifier;
  DECLARE @AlreadyConfirmed bit=0;

  IF @PublicSiteId IS NULL OR NULLIF(@Rfc,'''') IS NULL
    THROW 54040,''No existe un contexto publico de restaurante verificado.'',1;
  IF @Outcome NOT IN(''Approved'',''Pending3ds'',''Denied'',''Unknown'')
    THROW 54041,''El desenlace del cargo no es valido.'',1;
  IF @Outcome IN(''Approved'',''Pending3ds'') AND NULLIF(LTRIM(RTRIM(@ProviderPaymentId)),'''') IS NULL
    THROW 54042,''El identificador del pago es obligatorio para este desenlace.'',1;
  IF @Outcome=''Approved'' AND (@GrossAmount IS NULL OR @CurrencyCode IS NULL)
    THROW 54043,''El importe y la moneda son obligatorios en un cargo aprobado.'',1;

  BEGIN TRANSACTION;

  SELECT @SiteId=attempt.SiteId,@MerchantProfileKey=attempt.MerchantProfileKey,
    @ExpectedAmount=attempt.Total,@ExpectedCurrency=attempt.CurrencyCode,
    @CurrentState=attempt.[State],@CurrentChargeRequestId=attempt.ChargeRequestId,
    @CurrentPaymentId=attempt.ProviderOrderId
  FROM restaurante.OnlineCheckoutAttempt attempt WITH(UPDLOCK,HOLDLOCK)
  WHERE attempt.Id=@Id AND attempt.PublicSiteId=@PublicSiteId AND attempt.Rfc=@Rfc
    AND attempt.Provider=''Clip'';

  IF @SiteId IS NULL
  BEGIN
    ROLLBACK TRANSACTION;
    THROW 54044,''El cargo no pertenece a un checkout Clip del sitio publico.'',1;
  END;

  -- Una respuesta tardia de un cargo ya superado no puede escribir sobre el
  -- cargo vigente.
  IF @CurrentChargeRequestId IS NULL OR @CurrentChargeRequestId<>@ChargeRequestId
  BEGIN
    ROLLBACK TRANSACTION;
    THROW 54045,''El sello del cargo no corresponde al intento vigente.'',1;
  END;

  -- Un desenlace repetido sobre un cargo ya confirmado no cambia nada.
  IF @CurrentState IN(''Captured'',''CapturedNeedsOrder'',''PosCreated'',
                      ''RefundRequested'',''RefundPending'',''Refunded'')
  BEGIN
    IF @Outcome<>''Approved''
       OR (@ProviderPaymentId IS NOT NULL AND @CurrentPaymentId IS NOT NULL
           AND @CurrentPaymentId<>@ProviderPaymentId)
    BEGIN
      ROLLBACK TRANSACTION;
      THROW 54046,''Un cargo confirmado no puede retroceder de estado.'',1;
    END;
    SET @AlreadyConfirmed=1;
  END;

  IF @AlreadyConfirmed=0
     AND @CurrentState NOT IN(''ChargePending'',''Authenticating3ds'',''ChargeUnknown'')
  BEGIN
    ROLLBACK TRANSACTION;
    THROW 54047,''El intento no esta esperando el desenlace de un cargo.'',1;
  END;

  IF @AlreadyConfirmed=0 AND @Outcome=''Approved''
     AND (@GrossAmount<>@ExpectedAmount OR @CurrencyCode<>@ExpectedCurrency)
  BEGIN
    ROLLBACK TRANSACTION;
    THROW 54048,''El cargo no coincide con el importe y moneda cotizados.'',1;
  END;

  IF @AlreadyConfirmed=0 AND @Outcome=''Approved''
  BEGIN
    SELECT @TransactionId=transactionInfo.Id
    FROM restaurante.PaymentGatewayTransaction transactionInfo WITH(UPDLOCK,HOLDLOCK)
    WHERE transactionInfo.PublicSiteId=@PublicSiteId
      AND transactionInfo.CheckoutAttemptId=@Id;

    IF @TransactionId IS NULL
    BEGIN
      SET @TransactionId=NEWID();
      -- Clip no desglosa comision en el objeto de pago, asi que FeeAmount y
      -- NetAmount quedan nulos hasta conciliar con la API de depositos.
      INSERT restaurante.PaymentGatewayTransaction
      (
        Id,PublicSiteId,Rfc,SiteId,CheckoutAttemptId,Provider,MerchantProfileKey,
        ProviderOrderId,ProviderCaptureId,[Status],GrossAmount,FeeAmount,NetAmount,
        CurrencyCode,IdempotencyKey,CapturedAtUtc
      )
      VALUES
      (
        @TransactionId,@PublicSiteId,@Rfc,@SiteId,@Id,''Clip'',@MerchantProfileKey,
        @ProviderPaymentId,@ProviderPaymentId,''Completed'',@GrossAmount,NULL,NULL,
        @CurrencyCode,CONVERT(varchar(100),@ChargeRequestId),
        COALESCE(@CapturedAtUtc,@Now)
      );
    END
    ELSE
    BEGIN
      IF EXISTS
      (
        SELECT 1 FROM restaurante.PaymentGatewayTransaction
        WHERE Id=@TransactionId
          AND (ProviderCaptureId<>@ProviderPaymentId OR GrossAmount<>@GrossAmount
               OR CurrencyCode<>@CurrencyCode)
      )
      BEGIN
        ROLLBACK TRANSACTION;
        THROW 54049,''El cargo repetido no coincide con la transaccion guardada.'',1;
      END;
      UPDATE restaurante.PaymentGatewayTransaction
      SET [Status]=CASE WHEN [Status] IN(''Refunded'',''PartiallyRefunded'')
            THEN [Status] ELSE ''Completed'' END,
          CapturedAtUtc=COALESCE(CapturedAtUtc,@CapturedAtUtc,@Now),
          UpdatedAtUtc=@Now
      WHERE Id=@TransactionId;
    END;
  END;

  IF @AlreadyConfirmed=1
  BEGIN
    SELECT @TransactionId=transactionInfo.Id
    FROM restaurante.PaymentGatewayTransaction transactionInfo
    WHERE transactionInfo.PublicSiteId=@PublicSiteId
      AND transactionInfo.CheckoutAttemptId=@Id;
  END
  ELSE
  UPDATE restaurante.OnlineCheckoutAttempt
  SET [State]=CASE @Outcome
        WHEN ''Approved'' THEN ''Captured''
        WHEN ''Pending3ds'' THEN ''Authenticating3ds''
        WHEN ''Denied'' THEN ''PaymentDenied''
        ELSE ''ChargeUnknown'' END,
      ProviderOrderId=CASE WHEN @Outcome=''Denied'' THEN NULL
        ELSE COALESCE(@ProviderPaymentId,ProviderOrderId) END,
      ProviderRequestId=CASE WHEN @Outcome=''Denied'' THEN NULL
        WHEN @ProviderPaymentId IS NULL THEN ProviderRequestId
        ELSE COALESCE(NULLIF(LTRIM(RTRIM(@ExternalReference)),''''),
                      CONVERT(varchar(100),@ChargeRequestId)) END,
      ProviderCaptureId=CASE WHEN @Outcome=''Approved'' THEN @ProviderPaymentId
        WHEN @Outcome=''Denied'' THEN NULL ELSE ProviderCaptureId END,
      CapturedAtUtc=CASE WHEN @Outcome=''Approved''
        THEN COALESCE(CapturedAtUtc,@CapturedAtUtc,@Now) ELSE CapturedAtUtc END,
      FailureCode=CASE WHEN @Outcome IN(''Denied'',''Unknown'')
        THEN NULLIF(LTRIM(RTRIM(@FailureCode)),'''') ELSE NULL END,
      FailureMessage=CASE WHEN @Outcome IN(''Denied'',''Unknown'')
        THEN LEFT(NULLIF(LTRIM(RTRIM(@FailureMessage)),N''''),500) ELSE NULL END,
      NextRetryAtUtc=CASE @Outcome
        WHEN ''Approved'' THEN NULL
        WHEN ''Denied'' THEN NULL
        WHEN ''Pending3ds'' THEN DATEADD(SECOND,90,@Now)
        ELSE DATEADD(SECOND,30,@Now) END,
      UpdatedAtUtc=@Now
  WHERE Id=@Id AND PublicSiteId=@PublicSiteId AND Rfc=@Rfc;

  COMMIT TRANSACTION;

  EXEC restaurante.OnlineCheckoutAttemptGet @Id=@Id;

  SELECT
    transactionInfo.Id,transactionInfo.CheckoutAttemptId,transactionInfo.Provider,
    transactionInfo.MerchantProfileKey,transactionInfo.ProviderOrderId,
    transactionInfo.ProviderCaptureId,transactionInfo.[Status],transactionInfo.GrossAmount,
    transactionInfo.FeeAmount,transactionInfo.NetAmount,transactionInfo.CurrencyCode,
    transactionInfo.LocalPaymentId,transactionInfo.IdempotencyKey,
    transactionInfo.CapturedAtUtc,transactionInfo.CreatedAtUtc,transactionInfo.UpdatedAtUtc,
    transactionInfo.RowVersion
  FROM restaurante.PaymentGatewayTransaction transactionInfo
  WHERE transactionInfo.Id=@TransactionId;
END;');

  /* ---------- 8. Estados que el endpoint publico puede fijar ---------- */

  EXEC(N'
CREATE OR ALTER PROCEDURE restaurante.OnlineCheckoutStateSet
  @Id uniqueidentifier,
  @ExpectedState varchar(30),
  @State varchar(30),
  @FailureCode varchar(80)=NULL,
  @FailureMessage nvarchar(500)=NULL,
  @NextRetryAtUtc datetime2(3)=NULL,
  @RestaurantOrderId uniqueidentifier=NULL
AS
BEGIN
  SET NOCOUNT ON;

  DECLARE @PublicSiteId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N''OrionERP.PublicSiteId''));
  DECLARE @Rfc varchar(50)=CONVERT(varchar(50),SESSION_CONTEXT(N''OrionRfc''));

  IF @PublicSiteId IS NULL OR NULLIF(@Rfc,'''') IS NULL
    THROW 53649,''No existe un contexto publico de restaurante verificado.'',1;
  IF @State NOT IN(''RequoteRequired'',''PaymentDenied'',''ChargeUnknown'',''Expired'',''Failed'')
    THROW 53650,''El endpoint publico no puede asignar ese estado.'',1;
  IF NULLIF(@ExpectedState,'''') IS NULL
    THROW 53651,''ExpectedState es obligatorio.'',1;
  IF @RestaurantOrderId IS NOT NULL
    THROW 53674,''El endpoint publico no puede ligar una orden POS.'',1;

  -- Un cargo en vuelo no se puede abandonar desde el endpoint publico: mientras
  -- Clip pueda cobrarlo, solo la recuperacion decide su desenlace.
  UPDATE restaurante.OnlineCheckoutAttempt
  SET [State]=@State,
      FailureCode=NULLIF(LTRIM(RTRIM(@FailureCode)),''''),
      FailureMessage=LEFT(NULLIF(LTRIM(RTRIM(@FailureMessage)),N''''),500),
      NextRetryAtUtc=@NextRetryAtUtc,
      UpdatedAtUtc=SYSUTCDATETIME()
  WHERE Id=@Id AND PublicSiteId=@PublicSiteId AND Rfc=@Rfc
    AND [State]=@ExpectedState
    AND
    (
      ([State]=''Quoted'' AND @State IN(''RequoteRequired'',''Expired'',''Failed''))
      OR ([State]=''PaymentDenied'' AND @State IN(''RequoteRequired'',''Expired'',''Failed''))
      OR ([State]=''ChargeUnknown'' AND @State IN(''ChargeUnknown'',''Failed''))
      OR ([State]=''RequoteRequired'' AND @State IN(''RequoteRequired'',''Expired''))
    );

  IF @@ROWCOUNT=0
    THROW 53652,''El checkout cambio de estado o la transicion no es valida.'',1;

  SELECT
    attempt.Id,attempt.PublicSiteId,attempt.Rfc,attempt.SiteId,attempt.ClientAttemptId,
    attempt.QuoteFingerprint,attempt.Provider,attempt.ProviderOrderId,attempt.ProviderCaptureId,
    attempt.[State],attempt.RestaurantOrderId,attempt.NextRetryAtUtc,
    attempt.FailureCode,attempt.FailureMessage,attempt.UpdatedAtUtc,attempt.RowVersion
  FROM restaurante.OnlineCheckoutAttempt attempt
  WHERE attempt.Id=@Id AND attempt.PublicSiteId=@PublicSiteId AND attempt.Rfc=@Rfc;
END;');

  /* ---------- 9. Estado publico de seguimiento ---------- */

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
      WHEN attempt.[State] IN(''ChargePending'',''Authenticating3ds'',''ChargeUnknown'') THEN ''Pending''
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
      WHEN attempt.[State]=''PaymentDenied'' THEN N''El banco no autorizo el cobro. Puedes intentar con otra tarjeta.''
      WHEN attempt.[State]=''RequoteRequired'' THEN N''El menu o el total cambio. Revisa nuevamente tu pedido.''
      WHEN attempt.[State]=''Authenticating3ds'' THEN N''Tu banco esta verificando el pago. No vuelvas a pagar.''
      WHEN attempt.[State] IN(''ChargePending'',''ChargeUnknown'') THEN N''Estamos confirmando el pago. No vuelvas a pagar.''
      ELSE N''El pedido esta pendiente.'' END) [Message],
    attempt.UpdatedAtUtc,
    readyNotification.[Status] ReadyNotificationStatus
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
  OUTER APPLY
  (
    SELECT TOP(1) notification.[Status]
    FROM restaurante.OnlineOrderNotification notification
    WHERE notification.PublicSiteId=attempt.PublicSiteId
      AND notification.Rfc=attempt.Rfc
      AND notification.CheckoutAttemptId=attempt.Id
      AND notification.NotificationType=''Ready''
    ORDER BY notification.Id DESC
  ) readyNotification
  WHERE attempt.PublicSiteId=@PublicSiteId
    AND attempt.Rfc=@Rfc
    AND attempt.TrackingTokenHash=@TrackingTokenHash;
END;');

  /* ---------- 10. Reclamo de trabajo de recuperacion ---------- */

  -- Un cargo en ChargePending, Authenticating3ds o ChargeUnknown solo lo resuelve
  -- una consulta a Clip. El NextRetryAtUtc que sello ChargeBegin protege al cargo
  -- que todavia esta en vuelo de que la recuperacion lo tome antes de tiempo.
  EXEC(N'
CREATE OR ALTER PROCEDURE restaurante.PaymentRecoveryClaim
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
    ;WITH nextCharge AS
    (
      SELECT TOP(1) attempt.*
      FROM restaurante.OnlineCheckoutAttempt attempt WITH(UPDLOCK,READPAST,ROWLOCK)
      WHERE attempt.PublicSiteId=@PublicSiteId AND attempt.Rfc=@Rfc
        AND attempt.Provider=''Clip''
        AND attempt.[State] IN(''ChargePending'',''Authenticating3ds'',''ChargeUnknown'')
        AND (attempt.NextRetryAtUtc IS NULL OR attempt.NextRetryAtUtc<=SYSUTCDATETIME())
        AND (attempt.RecoveryLeaseExpiresAtUtc IS NULL OR attempt.RecoveryLeaseExpiresAtUtc<SYSUTCDATETIME())
      ORDER BY attempt.UpdatedAtUtc,attempt.Id
    )
    UPDATE nextCharge
    SET RecoveryAttempts=RecoveryAttempts+1,RecoveryLeaseId=@LeaseId,
        RecoveryLeaseExpiresAtUtc=DATEADD(SECOND,@LeaseSeconds,SYSUTCDATETIME()),
        NextRetryAtUtc=NULL,UpdatedAtUtc=SYSUTCDATETIME()
    OUTPUT inserted.Id INTO @AttemptClaim(CheckoutAttemptId);

    SELECT TOP(1) @ClaimedAttemptId=CheckoutAttemptId FROM @AttemptClaim;
  END;

  COMMIT TRANSACTION;

  SELECT TOP(1)
    CONVERT(varchar(20),CASE WHEN @ClaimedEventId IS NULL THEN ''Charge'' ELSE ''Event'' END) WorkType,
    attempt.Id CheckoutAttemptId,eventInfo.Id EventId,eventInfo.EventType,
    eventInfo.ResourceType,eventInfo.ResourceId,eventInfo.RelatedOrderId,
    eventInfo.RelatedCaptureId,eventInfo.RelatedRefundId,
    attempt.Provider,attempt.ProviderOrderId,attempt.ProviderCaptureId,
    attempt.ChargeRequestId,attempt.ChargeStartedAtUtc,attempt.CreatedAtUtc,
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
CREATE OR ALTER PROCEDURE restaurante.PaymentRecoveryResult
  @WorkType varchar(20),
  @CheckoutAttemptId uniqueidentifier,
  @EventId bigint=NULL,
  @LeaseId uniqueidentifier,
  @Outcome varchar(20),
  @FailureCode varchar(80)=NULL,
  @FailureMessage nvarchar(500)=NULL,
  @NextRetryAtUtc datetime2(3)=NULL
AS
BEGIN
  SET NOCOUNT ON;
  SET XACT_ABORT ON;

  DECLARE @PublicSiteId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N''OrionERP.PublicSiteId''));
  DECLARE @Rfc varchar(50)=CONVERT(varchar(50),SESSION_CONTEXT(N''OrionRfc''));

  IF @PublicSiteId IS NULL OR NULLIF(@Rfc,'''') IS NULL
    THROW 53689,''No existe un contexto publico de restaurante verificado.'',1;
  IF @WorkType NOT IN(''Charge'',''Event'') OR @Outcome NOT IN(''Processed'',''Pending'',''Ignored'',''Failed'')
    THROW 53690,''El resultado de recuperacion es invalido.'',1;
  IF @WorkType=''Charge'' AND @EventId IS NOT NULL
    THROW 53691,''Una recuperacion de cargo no acepta EventId.'',1;
  IF @WorkType=''Event'' AND @EventId IS NULL
    THROW 53692,''EventId es obligatorio para un webhook.'',1;

  BEGIN TRANSACTION;

  IF @WorkType=''Event''
  BEGIN
    UPDATE restaurante.PaymentGatewayEvent
    SET ProcessingStatus=CASE @Outcome
          WHEN ''Processed'' THEN ''Processed''
          WHEN ''Ignored'' THEN ''Ignored''
          WHEN ''Pending'' THEN ''Pending''
          ELSE ''Failed'' END,
        LeaseId=NULL,LeaseExpiresAtUtc=NULL,
        NextRetryAtUtc=CASE WHEN @Outcome IN(''Pending'',''Failed'')
          THEN COALESCE(@NextRetryAtUtc,DATEADD(MINUTE,1,SYSUTCDATETIME())) ELSE NULL END,
        FailureCode=CASE WHEN @Outcome IN(''Pending'',''Failed'') THEN NULLIF(LTRIM(RTRIM(@FailureCode)),'''') ELSE NULL END,
        FailureMessage=CASE WHEN @Outcome IN(''Pending'',''Failed'')
          THEN LEFT(NULLIF(LTRIM(RTRIM(@FailureMessage)),N''''),500) ELSE NULL END,
        ProcessedAtUtc=CASE WHEN @Outcome IN(''Processed'',''Ignored'') THEN SYSUTCDATETIME() ELSE NULL END,
        UpdatedAtUtc=SYSUTCDATETIME()
    WHERE Id=@EventId AND PublicSiteId=@PublicSiteId AND Rfc=@Rfc
      AND CheckoutAttemptId=@CheckoutAttemptId
      AND ProcessingStatus=''Processing'' AND LeaseId=@LeaseId;
  END
  ELSE
  BEGIN
    UPDATE restaurante.OnlineCheckoutAttempt
    SET RecoveryLeaseId=NULL,RecoveryLeaseExpiresAtUtc=NULL,
        NextRetryAtUtc=CASE WHEN @Outcome IN(''Pending'',''Failed'')
          AND [State] IN(''ChargePending'',''Authenticating3ds'',''ChargeUnknown'')
          THEN COALESCE(@NextRetryAtUtc,DATEADD(MINUTE,1,SYSUTCDATETIME())) ELSE NextRetryAtUtc END,
        FailureCode=CASE WHEN @Outcome=''Failed''
          AND [State] IN(''ChargePending'',''Authenticating3ds'',''ChargeUnknown'')
          THEN NULLIF(LTRIM(RTRIM(@FailureCode)),'''') ELSE FailureCode END,
        FailureMessage=CASE WHEN @Outcome=''Failed''
          AND [State] IN(''ChargePending'',''Authenticating3ds'',''ChargeUnknown'')
          THEN LEFT(NULLIF(LTRIM(RTRIM(@FailureMessage)),N''''),500) ELSE FailureMessage END,
        UpdatedAtUtc=SYSUTCDATETIME()
    WHERE Id=@CheckoutAttemptId AND PublicSiteId=@PublicSiteId AND Rfc=@Rfc
      AND RecoveryLeaseId=@LeaseId;
  END;

  IF @@ROWCOUNT<>1
  BEGIN
    ROLLBACK TRANSACTION;
    THROW 53693,''La concesion de recuperacion ya no es valida.'',1;
  END;

  COMMIT TRANSACTION;

  SELECT @WorkType WorkType,@CheckoutAttemptId CheckoutAttemptId,@EventId EventId,
    @Outcome Outcome,SYSUTCDATETIME() UpdatedAtUtc;
END;');

  /* ---------- 11. Reclamo del importador al POS ---------- */
  EXEC(N'
CREATE OR ALTER PROCEDURE restaurante.OnlineOrderImportClaim
  @LeaseId uniqueidentifier,
  @LeaseSeconds int=90
AS
BEGIN
  SET NOCOUNT ON;
  SET XACT_ABORT ON;

  IF @LeaseId IS NULL OR @LeaseSeconds NOT BETWEEN 15 AND 600
    THROW 53660,''La concesion del importador es invalida.'',1;

  DECLARE @PublicSiteId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N''OrionERP.PublicSiteId''));
  DECLARE @Rfc varchar(50)=CONVERT(varchar(50),SESSION_CONTEXT(N''OrionRfc''));
  DECLARE @CompanyId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N''OrionERP.CompanyId''));
  DECLARE @OrionSiteId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N''OrionERP.SiteId''));
  DECLARE @ResolvedPublicSiteId bigint;

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
    THROW 53661,''El importador no pudo resolver el sitio de pedidos.'',1;

  DECLARE @Claimed TABLE(Id uniqueidentifier NOT NULL);

  BEGIN TRANSACTION;
  ;WITH nextAttempt AS
  (
    SELECT TOP(1) attempt.*
    FROM restaurante.OnlineCheckoutAttempt attempt WITH(UPDLOCK,READPAST,ROWLOCK)
    JOIN restaurante.PaymentGatewayTransaction transactionInfo
      ON transactionInfo.PublicSiteId=attempt.PublicSiteId
     AND transactionInfo.Rfc=attempt.Rfc
     AND transactionInfo.SiteId=attempt.SiteId
     AND transactionInfo.CheckoutAttemptId=attempt.Id
    WHERE attempt.PublicSiteId=@ResolvedPublicSiteId
      AND attempt.Rfc=@Rfc
      AND attempt.[State] IN(''Captured'',''CapturedNeedsOrder'')
      AND transactionInfo.[Status] IN(''Completed'',''PartiallyRefunded'')
      AND (attempt.NextRetryAtUtc IS NULL OR attempt.NextRetryAtUtc<=SYSUTCDATETIME())
      AND (attempt.ImportLeaseExpiresAtUtc IS NULL OR attempt.ImportLeaseExpiresAtUtc<SYSUTCDATETIME())
    ORDER BY COALESCE(attempt.CapturedAtUtc,attempt.CreatedAtUtc),attempt.Id
  )
  UPDATE nextAttempt
  SET [State]=''CapturedNeedsOrder'',
      ImportAttempts=ImportAttempts+1,
      ImportLeaseId=@LeaseId,
      ImportLeaseExpiresAtUtc=DATEADD(SECOND,@LeaseSeconds,SYSUTCDATETIME()),
      NextRetryAtUtc=NULL,
      UpdatedAtUtc=SYSUTCDATETIME()
  OUTPUT inserted.Id INTO @Claimed(Id);
  COMMIT TRANSACTION;

  SELECT
    attempt.Id,attempt.PublicSiteId,attempt.Rfc,attempt.SiteId,attempt.ClientAttemptId,
    attempt.MemberId,attempt.CustomerName,attempt.CustomerEmail,attempt.CustomerPhone,
    attempt.QuoteFingerprint,attempt.CartSnapshotJson,attempt.PromotionCode,
    attempt.Subtotal,attempt.PromotionDiscountTotal,attempt.TaxTotal,attempt.Total,
    attempt.CurrencyCode,attempt.MerchantProfileKey,attempt.Provider,attempt.ProviderOrderId,
    attempt.ProviderCaptureId,attempt.[State],attempt.ImportAttempts,attempt.ImportLeaseId,
    attempt.ImportLeaseExpiresAtUtc,attempt.CapturedAtUtc,
    transactionInfo.Id GatewayTransactionId,transactionInfo.GrossAmount,
    transactionInfo.FeeAmount,transactionInfo.NetAmount,transactionInfo.IdempotencyKey CaptureIdempotencyKey
  FROM @Claimed claimed
  JOIN restaurante.OnlineCheckoutAttempt attempt ON attempt.Id=claimed.Id
  JOIN restaurante.PaymentGatewayTransaction transactionInfo
    ON transactionInfo.PublicSiteId=attempt.PublicSiteId
   AND transactionInfo.Rfc=attempt.Rfc
   AND transactionInfo.SiteId=attempt.SiteId
   AND transactionInfo.CheckoutAttemptId=attempt.Id;
END;');

  /* ---------- 12. Bandeja de recuperacion del panel ---------- */
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
    attempt.ProviderOrderId,attempt.ProviderCaptureId,orderInfo.Folio OrderFolio,
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
      attempt.[State] IN(''ChargePending'',''Authenticating3ds'',''ChargeUnknown'',''Captured'',''CapturedNeedsOrder'',''RefundRequested'',''RefundPending'',''Failed'')
      OR refundFailure.RefundId IS NOT NULL
      OR eventFailure.EventId IS NOT NULL
      OR notificationFailure.NotificationId IS NOT NULL
    )
  ORDER BY attempt.UpdatedAtUtc DESC,attempt.Id;
END;');

  /* ---------- 13. Registro del aviso de Clip ---------- */

  -- El aviso de Clip llega sin firma y solo trae el identificador del pago, asi
  -- que "verificado" aqui significa que ese pago pertenece a un intento nuestro.
  -- Un aviso repetido reabre el evento en vez de descartarse: la unica accion que
  -- dispara es consultar el pago, y esa consulta es idempotente, asi que perder
  -- un cambio de estatus cuesta mas que una consulta extra.
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
  @VerificationStatus varchar(20)=''Verified'',
  @Provider varchar(20)=''Clip''
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
    THROW 53655,''Solo se persisten avisos ligados a un checkout propio.'',1;
  IF @Provider NOT IN(''Clip'',''PayPal'')
    THROW 54050,''El proveedor del aviso no es valido.'',1;
  IF @ProviderEventId IS NULL OR @EventType IS NULL OR @MerchantProfileKey IS NULL
     OR LEN(@PayloadHash)<>64
     OR @PayloadHash LIKE ''%[^0-9A-F]%'' COLLATE Latin1_General_100_BIN2
    THROW 53656,''Los metadatos del aviso son invalidos.'',1;

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
      (@RelatedOrderId IS NOT NULL AND attempt.ProviderOrderId=@RelatedOrderId)
      OR (@RelatedCaptureId IS NOT NULL AND transactionInfo.ProviderCaptureId=@RelatedCaptureId)
      OR (@RelatedRefundId IS NOT NULL AND refundInfo.ProviderRefundId=@RelatedRefundId)
      OR (@ResourceId IS NOT NULL AND
          (attempt.ProviderOrderId=@ResourceId OR transactionInfo.ProviderCaptureId=@ResourceId
           OR refundInfo.ProviderRefundId=@ResourceId))
    );

  IF (SELECT COUNT(*) FROM @Matches)>1
    THROW 53761,''El aviso coincide con mas de un checkout local.'',1;
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
      @PublicSiteId,@Rfc,@SiteId,@Provider,@MerchantProfileKey,@ProviderEventId,
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
    THROW 53657,''El identificador del aviso se repitio con otro payload.'',1;
  END
  ELSE
  BEGIN
    -- Mismo aviso otra vez: se reabre para volver a consultar el pago, porque un
    -- cambio de estatus posterior llega con el mismo cuerpo exacto.
    UPDATE restaurante.PaymentGatewayEvent
    SET ProcessingStatus=''Pending'',
        LeaseId=NULL,LeaseExpiresAtUtc=NULL,
        NextRetryAtUtc=NULL,ProcessedAtUtc=NULL,
        UpdatedAtUtc=SYSUTCDATETIME()
    WHERE Id=@EventId
      AND ProcessingStatus IN(''Processed'',''Ignored'')
      AND (LeaseExpiresAtUtc IS NULL OR LeaseExpiresAtUtc<SYSUTCDATETIME());
  END;

  COMMIT TRANSACTION;

  SELECT CONVERT(bit,1) WasMatched,@WasInserted WasInserted,
    eventInfo.Id EventId,@CheckoutAttemptId CheckoutAttemptId,eventInfo.ProcessingStatus
  FROM restaurante.PaymentGatewayEvent eventInfo
  WHERE eventInfo.Id=@EventId;
END;');

  /* ---------- 14. Retirar el ciclo de cobro de PayPal ---------- */

  IF OBJECT_ID(N'restaurante.OnlineCheckoutPayPalOrderRecord',N'P') IS NOT NULL
    DROP PROCEDURE restaurante.OnlineCheckoutPayPalOrderRecord;
  IF OBJECT_ID(N'restaurante.OnlineCheckoutCaptureAuthorize',N'P') IS NOT NULL
    DROP PROCEDURE restaurante.OnlineCheckoutCaptureAuthorize;
  IF OBJECT_ID(N'restaurante.OnlineCheckoutCaptureRecord',N'P') IS NOT NULL
    DROP PROCEDURE restaurante.OnlineCheckoutCaptureRecord;
  IF OBJECT_ID(N'restaurante.PayPalRecoveryClaim',N'P') IS NOT NULL
    DROP PROCEDURE restaurante.PayPalRecoveryClaim;
  IF OBJECT_ID(N'restaurante.PayPalRecoveryResult',N'P') IS NOT NULL
    DROP PROCEDURE restaurante.PayPalRecoveryResult;

  /* ---------- 15. Perfil publico v11 ---------- */

  -- Los procedimientos nuevos no heredan permisos, y los que se eliminaron no
  -- pueden seguir declarados: sin este perfil el checkout responde 500.
  IF EXISTS
  (
    SELECT 1 FROM orion.PublicPermissionProfile
    WHERE ProfileCode='RESTAURANT_PUBLIC' AND ProfileVersion=11
  )
    THROW 54051,'Existe un perfil Restaurant v11 sin registro de esta migracion.',1;

  INSERT orion.PublicPermissionProfile(ProfileCode,ProfileVersion,ModuleCode)
  VALUES('RESTAURANT_PUBLIC',11,'RESTAURANT');

  INSERT orion.PublicPermissionProfileEntry
    (ProfileCode,ProfileVersion,PermissionState,PermissionName,SecurableClass,SchemaName,ObjectName)
  SELECT source.ProfileCode,11,source.PermissionState,source.PermissionName,
    source.SecurableClass,source.SchemaName,source.ObjectName
  FROM orion.PublicPermissionProfileEntry source
  WHERE source.ProfileCode='RESTAURANT_PUBLIC' AND source.ProfileVersion=10
    AND NOT
    (
      source.SchemaName='restaurante'
      AND source.ObjectName IN
      (
        'OnlineCheckoutPayPalOrderRecord','OnlineCheckoutCaptureAuthorize',
        'OnlineCheckoutCaptureRecord','PayPalRecoveryClaim','PayPalRecoveryResult'
      )
    );

  INSERT orion.PublicPermissionProfileEntry
    (ProfileCode,ProfileVersion,PermissionState,PermissionName,SecurableClass,SchemaName,ObjectName)
  SELECT 'RESTAURANT_PUBLIC',11,'GRANT','EXECUTE','OBJECT','restaurante',newProcedure.ObjectName
  FROM (VALUES
    ('OnlineCheckoutChargeBegin'),
    ('OnlineCheckoutChargeResult'),
    ('PaymentRecoveryClaim'),
    ('PaymentRecoveryResult')
  ) newProcedure(ObjectName)
  WHERE NOT EXISTS
  (
    SELECT 1 FROM orion.PublicPermissionProfileEntry existing
    WHERE existing.ProfileCode='RESTAURANT_PUBLIC' AND existing.ProfileVersion=11
      AND existing.PermissionState='GRANT' AND existing.PermissionName='EXECUTE'
      AND existing.SecurableClass='OBJECT' AND existing.SchemaName='restaurante'
      AND existing.ObjectName=newProcedure.ObjectName
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
  WHERE profileInfo.ProfileCode='RESTAURANT_PUBLIC' AND profileInfo.ProfileVersion=11;

  IF EXISTS
  (
    SELECT 1
    FROM orion.PublicSqlPrincipalBinding binding
    JOIN orion.PublicSite publicSite ON publicSite.PublicSiteId=binding.PublicSiteId
    WHERE binding.IsActive=1 AND binding.PermissionProfile='RESTAURANT_PUBLIC'
      AND publicSite.ModuleCode='RESTAURANT'
      AND DATABASE_PRINCIPAL_ID(binding.PrincipalName) IS NULL
  )
    THROW 54052,'Un binding Restaurant activo no tiene principal de base.',1;

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
    THROW 54053,'No existen bindings Restaurant activos para aplicar el perfil v11.',1;
  IF EXISTS
  (
    SELECT 1
    FROM orion.PublicSqlPrincipalBinding binding
    JOIN orion.PublicSite publicSite ON publicSite.PublicSiteId=binding.PublicSiteId
    WHERE binding.IsActive=1 AND binding.PermissionProfile='RESTAURANT_PUBLIC'
      AND publicSite.ModuleCode='RESTAURANT'
      AND binding.PermissionVersion<>11
  )
    THROW 54054,'No todos los bindings Restaurant recibieron el perfil v11.',1;

  /* ---------- 16. Validaciones ---------- */

  IF COL_LENGTH(N'restaurante.OnlineCheckoutAttempt',N'ProviderOrderId') IS NULL
     OR COL_LENGTH(N'restaurante.OnlineCheckoutAttempt',N'ProviderCaptureId') IS NULL
     OR COL_LENGTH(N'restaurante.OnlineCheckoutAttempt',N'ProviderRequestId') IS NULL
     OR COL_LENGTH(N'restaurante.OnlineCheckoutAttempt',N'Provider') IS NULL
     OR COL_LENGTH(N'restaurante.OnlineCheckoutAttempt',N'ChargeRequestId') IS NULL
    THROW 54060,'Las columnas neutrales de proveedor no quedaron en su lugar.',1;
  IF COL_LENGTH(N'restaurante.OnlineCheckoutAttempt',N'PayPalOrderId') IS NOT NULL
     OR COL_LENGTH(N'restaurante.OnlineCheckoutAttempt',N'PayPalCaptureId') IS NOT NULL
     OR COL_LENGTH(N'restaurante.OnlineCheckoutAttempt',N'PayPalCreateRequestId') IS NOT NULL
    THROW 54061,'Sobrevivio alguna columna con nombre de PayPal.',1;

  IF OBJECT_ID(N'restaurante.OnlineCheckoutChargeBegin',N'P') IS NULL
     OR OBJECT_ID(N'restaurante.OnlineCheckoutChargeResult',N'P') IS NULL
     OR OBJECT_ID(N'restaurante.PaymentRecoveryClaim',N'P') IS NULL
     OR OBJECT_ID(N'restaurante.PaymentRecoveryResult',N'P') IS NULL
    THROW 54062,'Faltan los procedimientos del ciclo de cobro de Clip.',1;
  IF OBJECT_ID(N'restaurante.OnlineCheckoutPayPalOrderRecord',N'P') IS NOT NULL
     OR OBJECT_ID(N'restaurante.OnlineCheckoutCaptureAuthorize',N'P') IS NOT NULL
     OR OBJECT_ID(N'restaurante.OnlineCheckoutCaptureRecord',N'P') IS NOT NULL
     OR OBJECT_ID(N'restaurante.PayPalRecoveryClaim',N'P') IS NOT NULL
     OR OBJECT_ID(N'restaurante.PayPalRecoveryResult',N'P') IS NOT NULL
    THROW 54063,'Sobrevivio algun procedimiento del ciclo de PayPal.',1;

  -- Un procedimiento que aun mencione una columna renombrada compila pero falla
  -- en la primera ejecucion, asi que la comprobacion es sobre la definicion viva.
  IF EXISTS
  (
    SELECT 1
    FROM sys.sql_modules moduleInfo
    JOIN sys.objects objectInfo ON objectInfo.object_id=moduleInfo.object_id
    WHERE OBJECT_SCHEMA_NAME(objectInfo.object_id)=N'restaurante'
      AND objectInfo.type=N'P'
      AND
      (
        moduleInfo.definition LIKE N'%PayPalOrderId%'
        OR moduleInfo.definition LIKE N'%PayPalCaptureId%'
        OR moduleInfo.definition LIKE N'%PayPalCreateRequestId%'
      )
  )
    THROW 54064,'Algun procedimiento sigue referenciando columnas de PayPal.',1;

  IF NOT EXISTS
  (
    SELECT 1 FROM sys.check_constraints
    WHERE parent_object_id=OBJECT_ID(N'restaurante.OnlineCheckoutAttempt')
      AND name=N'CK_OnlineCheckoutAttempt_State'
      AND definition LIKE N'%ChargePending%'
      AND definition LIKE N'%Authenticating3ds%'
      AND definition LIKE N'%ChargeUnknown%'
  )
    THROW 54065,'El CHECK de estados no admite el ciclo de cobro de Clip.',1;

  IF NOT EXISTS
  (
    SELECT 1 FROM sys.check_constraints
    WHERE parent_object_id=OBJECT_ID(N'restaurante.PaymentGatewayTransaction')
      AND name=N'CK_PaymentGatewayTransaction_Provider'
      AND definition LIKE N'%Clip%'
  )
    THROW 54066,'PaymentGatewayTransaction no admite el proveedor Clip.',1;

  -- Las columnas nuevas siguen sin existir para el compilador de este lote, asi
  -- que los conteos se leen con sp_executesql y un parametro de salida.
  DECLARE @OpenLegacyAttempts int;
  DECLARE @LegacyAttempts int;
  EXEC sys.sp_executesql
    N'SELECT @Open=COUNT(*) FROM restaurante.OnlineCheckoutAttempt
      WHERE Provider=N''PayPal''
        AND [State] IN(N''Quoted'',N''PayPalCreated'',N''CapturePending'',N''RequoteRequired'');',
    N'@Open int OUTPUT',@Open=@OpenLegacyAttempts OUTPUT;
  IF @OpenLegacyAttempts>0
    THROW 54067,'Quedaron intentos de PayPal sin cerrar.',1;

  EXEC sys.sp_executesql
    N'SELECT @Total=COUNT(*) FROM restaurante.OnlineCheckoutAttempt WHERE Provider=N''PayPal'';',
    N'@Total int OUTPUT',@Total=@LegacyAttempts OUTPUT;

  SELECT DB_NAME() DatabaseName,@ApplyChanges ApplyChanges,
    CONVERT(bit,1) ProviderColumnsRenamed,
    CONVERT(bit,1) ChargeLifecycleInstalled,
    CONVERT(bit,1) PublicProfileV11Applied,
    @LegacyAttempts LegacyAttempts;

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
  IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
  THROW;
END CATCH;

/*
  Reversa manual (solo si nunca hubo un cargo con Clip):
    1. Restaurar el respaldo previo a esta migracion. Es la unica reversa segura
       una vez que existe una transaccion Clip, porque el dinero ya se movio.
    2. Sin cargos Clip: revertir el perfil publico a v10 aplicando
       orion.ApplyPublicPermissionProfile con esa version, volver a crear los
       cinco procedimientos de PayPal desde
       20260914_restaurant_online_ordering[_runtime_corrections].sql, renombrar
       las tres columnas a su nombre anterior con sys.sp_rename y reconstruir
       UX_OnlineCheckoutAttempt_PayPalOrder, UX_OnlineCheckoutAttempt_PayPalCapture
       e IX_OnlineCheckoutAttempt_Recovery.
*/
