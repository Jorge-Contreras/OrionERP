/*
  Durable, public-site-scoped checkout boundary for Restaurant online ordering.

  Safe deployment defaults:
    - Bruno's public site is disabled for online ordering;
    - the independent schedule is copied from its public opening-hours JSON;
    - the maximum order total is MXN 2,500;
    - every existing product is seeded explicitly as not orderable;
    - public SQL principals receive procedure/view access, never direct DML on
      checkout, POS, payment, inventory, loyalty, or outbox tables.
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;

DECLARE @ExpectedDatabase sysname=N'$(ExpectedDatabase)';
DECLARE @ApplyInput nvarchar(20)=N'$(ApplyChanges)';
DECLARE @ApplyChanges bit=0;
DECLARE @MigrationId nvarchar(200)=N'$(MigrationId)';
DECLARE @MigrationChecksum varchar(128)='$(MigrationChecksum)';
DECLARE @AppVersionInput nvarchar(64)=N'$(AppVersion)';
DECLARE @AppVersion nvarchar(64)=NULL;

IF @ApplyInput NOT LIKE N'$'+N'(%'
BEGIN
  IF @ApplyInput NOT IN(N'0',N'1') THROW 53600,'ApplyChanges debe ser 0 o 1.',1;
  SET @ApplyChanges=CONVERT(bit,@ApplyInput);
END;
IF @ExpectedDatabase LIKE N'$'+N'(%'
   OR @ExpectedDatabase NOT IN(N'Orion_Sandbox',N'grupocarpio',N'Orion_CutoverValidation_20260908')
  THROW 53601,'Base esperada no autorizada para online ordering.',1;
IF DB_NAME()<>@ExpectedDatabase
  THROW 53602,'La conexion no apunta a la base declarada.',1;
IF @MigrationId<>N'20260914_restaurant_online_ordering'
  THROW 53603,'MigrationId incorrecto.',1;
IF @MigrationChecksum LIKE '$'+N'(%' OR LEN(@MigrationChecksum)<>64
   OR @MigrationChecksum LIKE '%[^0-9A-F]%' COLLATE Latin1_General_100_BIN2
  THROW 53604,'MigrationChecksum invalido.',1;
IF @AppVersionInput NOT LIKE N'$'+N'(%'
  SET @AppVersion=NULLIF(LTRIM(RTRIM(@AppVersionInput)),N'');

IF OBJECT_ID(N'orion.SchemaMigration',N'U') IS NULL
   OR OBJECT_ID(N'orion.PublicSite',N'U') IS NULL
   OR OBJECT_ID(N'orion.PublicSqlPrincipalBinding',N'U') IS NULL
   OR OBJECT_ID(N'orion.PublicPermissionProfile',N'U') IS NULL
   OR OBJECT_ID(N'orion.PublicPermissionProfileEntry',N'U') IS NULL
   OR OBJECT_ID(N'restaurante.Site',N'U') IS NULL
   OR OBJECT_ID(N'restaurante.PublicSiteSettings',N'U') IS NULL
   OR OBJECT_ID(N'restaurante.Product',N'U') IS NULL
   OR OBJECT_ID(N'restaurante.[Order]',N'U') IS NULL
   OR OBJECT_ID(N'restaurante.Payment',N'U') IS NULL
   OR OBJECT_ID(N'restaurante.PaymentRefund',N'U') IS NULL
   OR OBJECT_ID(N'fidelidad.MemberAccount',N'U') IS NULL
   OR OBJECT_ID(N'logistica.RfcSecurityPolicy',N'SP') IS NULL
   OR OBJECT_ID(N'logistica.fn_RfcAccessPredicate',N'IF') IS NULL
  THROW 53605,'Faltan objetos base requeridos por online ordering.',1;
IF COL_LENGTH(N'restaurante.Site',N'OrionCompanyId') IS NULL
   OR COL_LENGTH(N'restaurante.Site',N'OrionSiteId') IS NULL
   OR COL_LENGTH(N'restaurante.PublicSiteSettings',N'PublicSiteId') IS NULL
   OR COL_LENGTH(N'fidelidad.MemberAccount',N'PublicSiteId') IS NULL
  THROW 53606,'Falta el binding central de Restaurant/PublicSite.',1;
IF NOT EXISTS
(
  SELECT 1 FROM orion.PublicPermissionProfile
  WHERE ProfileCode='RESTAURANT_PUBLIC' AND ProfileVersion=6
)
  THROW 53607,'Falta el perfil publico Restaurant v6.',1;

DECLARE @ExistingChecksum char(64)=
  (SELECT Checksum FROM orion.SchemaMigration WHERE MigrationId=@MigrationId);
IF @ExistingChecksum IS NOT NULL AND UPPER(@ExistingChecksum)<>UPPER(@MigrationChecksum)
  THROW 53608,'El mismo MigrationId ya existe con otro checksum.',1;
IF @ExistingChecksum IS NOT NULL
BEGIN
  SELECT N'YA_APLICADO' Estado,@MigrationId MigrationId,@ExistingChecksum Checksum;
  RETURN;
END;

IF OBJECT_ID(N'restaurante.OnlineOrderingSettings',N'U') IS NOT NULL
   OR OBJECT_ID(N'restaurante.OnlineOrderProduct',N'U') IS NOT NULL
   OR OBJECT_ID(N'restaurante.OnlineCheckoutAttempt',N'U') IS NOT NULL
   OR OBJECT_ID(N'restaurante.PaymentGatewayTransaction',N'U') IS NOT NULL
   OR OBJECT_ID(N'restaurante.PaymentGatewayRefund',N'U') IS NOT NULL
   OR OBJECT_ID(N'restaurante.PaymentGatewayEvent',N'U') IS NOT NULL
   OR OBJECT_ID(N'restaurante.OnlineOrderNotification',N'U') IS NOT NULL
   OR OBJECT_ID(N'restaurante.OnlineOrderingScopePolicy',N'SP') IS NOT NULL
  THROW 53609,'Hay objetos parciales de online ordering sin ledger; requiere revision manual.',1;

SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;
BEGIN TRANSACTION;

DECLARE @LockResult int;
EXEC @LockResult=sys.sp_getapplock
  @Resource=N'OrionERP:Restaurant:OnlineOrdering:20260914',
  @LockMode=N'Exclusive',@LockOwner=N'Transaction',@LockTimeout=30000;
IF @LockResult<0 THROW 53610,'No fue posible obtener el bloqueo de migracion.',1;

CREATE TABLE #RestaurantOnlineOrderingMigrationState
(
  ApplyChanges bit NOT NULL,
  MigrationId nvarchar(200) NOT NULL,
  MigrationChecksum varchar(128) NOT NULL,
  AppVersion nvarchar(64) NULL
);
INSERT #RestaurantOnlineOrderingMigrationState
  (ApplyChanges,MigrationId,MigrationChecksum,AppVersion)
VALUES(@ApplyChanges,@MigrationId,@MigrationChecksum,@AppVersion);

IF NOT EXISTS
(
  SELECT 1 FROM sys.indexes
  WHERE object_id=OBJECT_ID(N'orion.PublicSite')
    AND name=N'UX_PublicSite_IdCompanySite'
)
  CREATE UNIQUE INDEX UX_PublicSite_IdCompanySite
    ON orion.PublicSite(PublicSiteId,CompanyId,SiteId);

IF NOT EXISTS
(
  SELECT 1 FROM sys.indexes
  WHERE object_id=OBJECT_ID(N'fidelidad.MemberAccount')
    AND name=N'UX_LoyaltyMember_PublicSiteRfcId'
)
  CREATE UNIQUE INDEX UX_LoyaltyMember_PublicSiteRfcId
    ON fidelidad.MemberAccount(PublicSiteId,Rfc,Id);

IF NOT EXISTS
(
  SELECT 1 FROM sys.indexes
  WHERE object_id=OBJECT_ID(N'restaurante.PaymentRefund')
    AND name=N'UX_RestaurantPaymentRefund_RfcId'
)
  CREATE UNIQUE INDEX UX_RestaurantPaymentRefund_RfcId
    ON restaurante.PaymentRefund(Rfc,Id);

CREATE TABLE restaurante.OnlineOrderingSettings
(
  PublicSiteId bigint NOT NULL CONSTRAINT PK_OnlineOrderingSettings PRIMARY KEY,
  Rfc varchar(50) NOT NULL,
  SiteId int NOT NULL,
  CompanyId bigint NOT NULL,
  OrionSiteId bigint NOT NULL,
  IsEnabled bit NOT NULL CONSTRAINT DF_OnlineOrderingSettings_Enabled DEFAULT(0),
  IsPaused bit NOT NULL CONSTRAINT DF_OnlineOrderingSettings_Paused DEFAULT(0),
  PauseMessage nvarchar(300) NULL,
  WeeklyScheduleJson nvarchar(4000) NOT NULL,
  MinimumOrderTotal decimal(18,2) NOT NULL CONSTRAINT DF_OnlineOrderingSettings_Minimum DEFAULT(0),
  MaximumOrderTotal decimal(18,2) NOT NULL CONSTRAINT DF_OnlineOrderingSettings_Maximum DEFAULT(2500),
  GuestCheckoutEnabled bit NOT NULL CONSTRAINT DF_OnlineOrderingSettings_Guest DEFAULT(1),
  PickupEnabled bit NOT NULL CONSTRAINT DF_OnlineOrderingSettings_Pickup DEFAULT(1),
  TermsVersion varchar(40) NOT NULL CONSTRAINT DF_OnlineOrderingSettings_Terms DEFAULT('online-orders-v1'),
  PrivacyVersion varchar(40) NOT NULL CONSTRAINT DF_OnlineOrderingSettings_Privacy DEFAULT('online-orders-v1'),
  ActiveMerchantProfileKey varchar(80) NOT NULL CONSTRAINT DF_OnlineOrderingSettings_Merchant DEFAULT('shared-paypal-v1'),
  GatewayEnvironment varchar(20) NOT NULL CONSTRAINT DF_OnlineOrderingSettings_GatewayEnvironment DEFAULT('Unknown'),
  GatewayCredentialsConfigured bit NOT NULL CONSTRAINT DF_OnlineOrderingSettings_GatewayCredentials DEFAULT(0),
  GatewayWebhookConfigured bit NOT NULL CONSTRAINT DF_OnlineOrderingSettings_GatewayWebhook DEFAULT(0),
  GatewayReadinessAtUtc datetime2(3) NULL,
  ProcessorHeartbeatAtUtc datetime2(3) NULL,
  ConfigurationVersion bigint NOT NULL CONSTRAINT DF_OnlineOrderingSettings_Version DEFAULT(1),
  UpdatedAtUtc datetime2(3) NOT NULL CONSTRAINT DF_OnlineOrderingSettings_Updated DEFAULT(SYSUTCDATETIME()),
  UpdatedBy nvarchar(256) NULL,
  RowVersion rowversion NOT NULL,
  CONSTRAINT UX_OnlineOrderingSettings_Scope UNIQUE(PublicSiteId,Rfc,SiteId),
  CONSTRAINT FK_OnlineOrderingSettings_PublicSite FOREIGN KEY(PublicSiteId,CompanyId,OrionSiteId)
    REFERENCES orion.PublicSite(PublicSiteId,CompanyId,SiteId),
  CONSTRAINT FK_OnlineOrderingSettings_RestaurantSite FOREIGN KEY(Rfc,SiteId)
    REFERENCES restaurante.Site(Rfc,Id),
  CONSTRAINT CK_OnlineOrderingSettings_ScheduleJson CHECK(ISJSON(WeeklyScheduleJson)=1),
  CONSTRAINT CK_OnlineOrderingSettings_Amounts CHECK
    (MinimumOrderTotal>=0 AND MaximumOrderTotal>0 AND MaximumOrderTotal>=MinimumOrderTotal),
  CONSTRAINT CK_OnlineOrderingSettings_PauseMessage CHECK
    (IsPaused=0 OR NULLIF(LTRIM(RTRIM(PauseMessage)),N'') IS NOT NULL),
  CONSTRAINT CK_OnlineOrderingSettings_Legal CHECK
    (NULLIF(LTRIM(RTRIM(TermsVersion)),'') IS NOT NULL
     AND NULLIF(LTRIM(RTRIM(PrivacyVersion)),'') IS NOT NULL),
  CONSTRAINT CK_OnlineOrderingSettings_Merchant CHECK
    (NULLIF(LTRIM(RTRIM(ActiveMerchantProfileKey)),'') IS NOT NULL),
  CONSTRAINT CK_OnlineOrderingSettings_GatewayEnvironment CHECK
    (GatewayEnvironment IN('Unknown','Sandbox','Live')),
  CONSTRAINT CK_OnlineOrderingSettings_Version CHECK(ConfigurationVersion>0)
);

CREATE TABLE restaurante.OnlineOrderProduct
(
  PublicSiteId bigint NOT NULL,
  Rfc varchar(50) NOT NULL,
  SiteId int NOT NULL,
  ProductId bigint NOT NULL,
  IsEnabled bit NOT NULL CONSTRAINT DF_OnlineOrderProduct_Enabled DEFAULT(0),
  UpdatedAtUtc datetime2(3) NOT NULL CONSTRAINT DF_OnlineOrderProduct_Updated DEFAULT(SYSUTCDATETIME()),
  UpdatedBy nvarchar(256) NULL,
  RowVersion rowversion NOT NULL,
  CONSTRAINT PK_OnlineOrderProduct PRIMARY KEY(PublicSiteId,ProductId),
  CONSTRAINT FK_OnlineOrderProduct_Settings FOREIGN KEY(PublicSiteId,Rfc,SiteId)
    REFERENCES restaurante.OnlineOrderingSettings(PublicSiteId,Rfc,SiteId),
  CONSTRAINT FK_OnlineOrderProduct_Product FOREIGN KEY(Rfc,ProductId)
    REFERENCES restaurante.Product(Rfc,Id)
);

IF COL_LENGTH(N'restaurante.[Order]',N'PublicSiteId') IS NULL
  ALTER TABLE restaurante.[Order] ADD PublicSiteId bigint NULL;
IF COL_LENGTH(N'restaurante.[Order]',N'OnlineCheckoutAttemptId') IS NULL
  ALTER TABLE restaurante.[Order] ADD OnlineCheckoutAttemptId uniqueidentifier NULL;
IF COL_LENGTH(N'restaurante.[Order]',N'CustomerEmail') IS NULL
  ALTER TABLE restaurante.[Order] ADD CustomerEmail nvarchar(320) NULL;
IF COL_LENGTH(N'restaurante.[Order]',N'SalesChannel') IS NULL
  ALTER TABLE restaurante.[Order] ADD SalesChannel varchar(20) NOT NULL
    CONSTRAINT DF_RestaurantOrder_SalesChannel DEFAULT('POS') WITH VALUES;
GO

IF OBJECT_ID(N'tempdb..#RestaurantOnlineOrderingMigrationState',N'U') IS NULL
BEGIN
  IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
  THROW 53614,'Se perdio el estado de migracion al extender Restaurant Order.',1;
END;

DECLARE @MigrationId nvarchar(200)=
  (SELECT MigrationId FROM #RestaurantOnlineOrderingMigrationState);

IF NOT EXISTS
(
  SELECT 1 FROM sys.check_constraints
  WHERE parent_object_id=OBJECT_ID(N'restaurante.[Order]')
    AND name=N'CK_RestaurantOrder_SalesChannel'
)
  ALTER TABLE restaurante.[Order] WITH CHECK ADD CONSTRAINT CK_RestaurantOrder_SalesChannel
    CHECK(SalesChannel IN('POS','Web'));
IF NOT EXISTS
(
  SELECT 1 FROM sys.check_constraints
  WHERE parent_object_id=OBJECT_ID(N'restaurante.[Order]')
    AND name=N'CK_RestaurantOrder_OnlineScope'
)
  ALTER TABLE restaurante.[Order] WITH CHECK ADD CONSTRAINT CK_RestaurantOrder_OnlineScope
    CHECK
    (
      (SalesChannel='POS' AND OnlineCheckoutAttemptId IS NULL)
      OR
      (SalesChannel='Web' AND PublicSiteId IS NOT NULL AND OnlineCheckoutAttemptId IS NOT NULL)
    );
IF NOT EXISTS
(
  SELECT 1 FROM sys.foreign_keys
  WHERE parent_object_id=OBJECT_ID(N'restaurante.[Order]')
    AND name=N'FK_RestaurantOrder_PublicSite'
)
  ALTER TABLE restaurante.[Order] WITH CHECK ADD CONSTRAINT FK_RestaurantOrder_PublicSite
    FOREIGN KEY(PublicSiteId) REFERENCES orion.PublicSite(PublicSiteId);
IF NOT EXISTS
(
  SELECT 1 FROM sys.indexes
  WHERE object_id=OBJECT_ID(N'restaurante.[Order]')
    AND name=N'UX_RestaurantOrder_PublicScopeId'
)
  CREATE UNIQUE INDEX UX_RestaurantOrder_PublicScopeId
    ON restaurante.[Order](PublicSiteId,Rfc,SiteId,Id);

CREATE TABLE restaurante.OnlineCheckoutAttempt
(
  Id uniqueidentifier NOT NULL CONSTRAINT PK_OnlineCheckoutAttempt PRIMARY KEY,
  PublicSiteId bigint NOT NULL,
  Rfc varchar(50) NOT NULL,
  SiteId int NOT NULL,
  ClientAttemptId uniqueidentifier NOT NULL,
  MemberId uniqueidentifier NULL,
  CustomerName nvarchar(150) NOT NULL,
  CustomerEmail nvarchar(320) NOT NULL,
  CustomerPhone varchar(30) NOT NULL,
  QuoteFingerprint char(64) NOT NULL,
  QuoteExpiresAtUtc datetime2(3) NOT NULL,
  CartSnapshotJson nvarchar(max) NOT NULL,
  PromotionCode varchar(80) NULL,
  Subtotal decimal(18,2) NOT NULL,
  PromotionDiscountTotal decimal(18,2) NOT NULL,
  TaxTotal decimal(18,2) NOT NULL,
  Total decimal(18,2) NOT NULL,
  CurrencyCode char(3) NOT NULL,
  PrivacyVersion varchar(40) NOT NULL,
  TermsVersion varchar(40) NOT NULL,
  LegalAcceptedAtUtc datetime2(3) NOT NULL,
  MerchantProfileKey varchar(80) NOT NULL,
  PayPalCreateRequestId varchar(100) NULL,
  PayPalOrderId varchar(64) NULL,
  PayPalCaptureId varchar(64) NULL,
  [State] varchar(30) NOT NULL CONSTRAINT DF_OnlineCheckoutAttempt_State DEFAULT('Quoted'),
  RestaurantOrderId uniqueidentifier NULL,
  ImportAttempts int NOT NULL CONSTRAINT DF_OnlineCheckoutAttempt_ImportAttempts DEFAULT(0),
  ImportLeaseId uniqueidentifier NULL,
  ImportLeaseExpiresAtUtc datetime2(3) NULL,
  RecoveryAttempts int NOT NULL CONSTRAINT DF_OnlineCheckoutAttempt_RecoveryAttempts DEFAULT(0),
  RecoveryLeaseId uniqueidentifier NULL,
  RecoveryLeaseExpiresAtUtc datetime2(3) NULL,
  NextRetryAtUtc datetime2(3) NULL,
  FailureCode varchar(80) NULL,
  FailureMessage nvarchar(500) NULL,
  TrackingTokenHash binary(32) NOT NULL,
  CreatedAtUtc datetime2(3) NOT NULL CONSTRAINT DF_OnlineCheckoutAttempt_Created DEFAULT(SYSUTCDATETIME()),
  UpdatedAtUtc datetime2(3) NOT NULL CONSTRAINT DF_OnlineCheckoutAttempt_Updated DEFAULT(SYSUTCDATETIME()),
  CapturedAtUtc datetime2(3) NULL,
  PosCreatedAtUtc datetime2(3) NULL,
  RowVersion rowversion NOT NULL,
  CONSTRAINT UX_OnlineCheckoutAttempt_ScopeId UNIQUE(PublicSiteId,Rfc,SiteId,Id),
  CONSTRAINT FK_OnlineCheckoutAttempt_Settings FOREIGN KEY(PublicSiteId,Rfc,SiteId)
    REFERENCES restaurante.OnlineOrderingSettings(PublicSiteId,Rfc,SiteId),
  CONSTRAINT FK_OnlineCheckoutAttempt_Member FOREIGN KEY(PublicSiteId,Rfc,MemberId)
    REFERENCES fidelidad.MemberAccount(PublicSiteId,Rfc,Id),
  CONSTRAINT FK_OnlineCheckoutAttempt_Order FOREIGN KEY(PublicSiteId,Rfc,SiteId,RestaurantOrderId)
    REFERENCES restaurante.[Order](PublicSiteId,Rfc,SiteId,Id),
  CONSTRAINT CK_OnlineCheckoutAttempt_State CHECK([State] IN
    ('Quoted','PayPalCreated','CapturePending','Captured','PosCreated','RequoteRequired',
     'PaymentDenied','CapturedNeedsOrder','RefundRequested','RefundPending','Refunded','Expired','Failed')),
  CONSTRAINT CK_OnlineCheckoutAttempt_CartJson CHECK(ISJSON(CartSnapshotJson)=1),
  CONSTRAINT CK_OnlineCheckoutAttempt_Amounts CHECK
    (Subtotal>=0 AND PromotionDiscountTotal>=0 AND PromotionDiscountTotal<=Subtotal
     AND TaxTotal>=0 AND Total>0),
  CONSTRAINT CK_OnlineCheckoutAttempt_Currency CHECK(CurrencyCode=UPPER(CurrencyCode)),
  CONSTRAINT CK_OnlineCheckoutAttempt_Fingerprint CHECK
    (LEN(QuoteFingerprint)=64 AND QuoteFingerprint NOT LIKE '%[^0-9A-F]%' COLLATE Latin1_General_100_BIN2),
  CONSTRAINT CK_OnlineCheckoutAttempt_Contact CHECK
    (NULLIF(LTRIM(RTRIM(CustomerName)),N'') IS NOT NULL
     AND NULLIF(LTRIM(RTRIM(CustomerEmail)),N'') IS NOT NULL
     AND NULLIF(LTRIM(RTRIM(CustomerPhone)),'') IS NOT NULL),
  CONSTRAINT CK_OnlineCheckoutAttempt_Legal CHECK
    (NULLIF(LTRIM(RTRIM(PrivacyVersion)),'') IS NOT NULL
     AND NULLIF(LTRIM(RTRIM(TermsVersion)),'') IS NOT NULL),
  CONSTRAINT CK_OnlineCheckoutAttempt_ImportLease CHECK
    ((ImportLeaseId IS NULL AND ImportLeaseExpiresAtUtc IS NULL)
     OR (ImportLeaseId IS NOT NULL AND ImportLeaseExpiresAtUtc IS NOT NULL)),
  CONSTRAINT CK_OnlineCheckoutAttempt_RecoveryLease CHECK
    ((RecoveryLeaseId IS NULL AND RecoveryLeaseExpiresAtUtc IS NULL)
     OR (RecoveryLeaseId IS NOT NULL AND RecoveryLeaseExpiresAtUtc IS NOT NULL)),
  CONSTRAINT CK_OnlineCheckoutAttempt_ProviderIdentity CHECK
    ((PayPalOrderId IS NULL AND PayPalCreateRequestId IS NULL)
     OR (PayPalOrderId IS NOT NULL AND PayPalCreateRequestId IS NOT NULL))
);

CREATE UNIQUE INDEX UX_OnlineCheckoutAttempt_ClientAttempt
  ON restaurante.OnlineCheckoutAttempt(PublicSiteId,ClientAttemptId);
CREATE UNIQUE INDEX UX_OnlineCheckoutAttempt_TrackingToken
  ON restaurante.OnlineCheckoutAttempt(TrackingTokenHash);
CREATE UNIQUE INDEX UX_OnlineCheckoutAttempt_PayPalOrder
  ON restaurante.OnlineCheckoutAttempt(MerchantProfileKey,PayPalOrderId)
  WHERE PayPalOrderId IS NOT NULL;
CREATE UNIQUE INDEX UX_OnlineCheckoutAttempt_PayPalCapture
  ON restaurante.OnlineCheckoutAttempt(MerchantProfileKey,PayPalCaptureId)
  WHERE PayPalCaptureId IS NOT NULL;
CREATE UNIQUE INDEX UX_OnlineCheckoutAttempt_RestaurantOrder
  ON restaurante.OnlineCheckoutAttempt(PublicSiteId,RestaurantOrderId)
  WHERE RestaurantOrderId IS NOT NULL;
CREATE INDEX IX_OnlineCheckoutAttempt_Recovery
  ON restaurante.OnlineCheckoutAttempt(PublicSiteId,[State],NextRetryAtUtc,CreatedAtUtc)
  INCLUDE(Id,PayPalOrderId,PayPalCaptureId,ImportAttempts,ImportLeaseExpiresAtUtc);

ALTER TABLE restaurante.[Order] WITH CHECK ADD CONSTRAINT FK_RestaurantOrder_OnlineCheckout
  FOREIGN KEY(PublicSiteId,Rfc,SiteId,OnlineCheckoutAttemptId)
  REFERENCES restaurante.OnlineCheckoutAttempt(PublicSiteId,Rfc,SiteId,Id);
CREATE UNIQUE INDEX UX_RestaurantOrder_OnlineCheckout
  ON restaurante.[Order](PublicSiteId,OnlineCheckoutAttemptId)
  WHERE OnlineCheckoutAttemptId IS NOT NULL;

CREATE TABLE restaurante.PaymentGatewayTransaction
(
  Id uniqueidentifier NOT NULL CONSTRAINT PK_PaymentGatewayTransaction PRIMARY KEY,
  PublicSiteId bigint NOT NULL,
  Rfc varchar(50) NOT NULL,
  SiteId int NOT NULL,
  CheckoutAttemptId uniqueidentifier NOT NULL,
  Provider varchar(30) NOT NULL,
  MerchantProfileKey varchar(80) NOT NULL,
  ProviderOrderId varchar(64) NOT NULL,
  ProviderCaptureId varchar(64) NOT NULL,
  [Status] varchar(30) NOT NULL,
  GrossAmount decimal(18,2) NOT NULL,
  FeeAmount decimal(18,2) NULL,
  NetAmount decimal(18,2) NULL,
  CurrencyCode char(3) NOT NULL,
  LocalPaymentId uniqueidentifier NULL,
  IdempotencyKey varchar(100) NOT NULL,
  CapturedAtUtc datetime2(3) NULL,
  CreatedAtUtc datetime2(3) NOT NULL CONSTRAINT DF_PaymentGatewayTransaction_Created DEFAULT(SYSUTCDATETIME()),
  UpdatedAtUtc datetime2(3) NOT NULL CONSTRAINT DF_PaymentGatewayTransaction_Updated DEFAULT(SYSUTCDATETIME()),
  RowVersion rowversion NOT NULL,
  CONSTRAINT UX_PaymentGatewayTransaction_ScopeId UNIQUE(PublicSiteId,Rfc,SiteId,Id),
  CONSTRAINT FK_PaymentGatewayTransaction_Attempt FOREIGN KEY(PublicSiteId,Rfc,SiteId,CheckoutAttemptId)
    REFERENCES restaurante.OnlineCheckoutAttempt(PublicSiteId,Rfc,SiteId,Id),
  CONSTRAINT FK_PaymentGatewayTransaction_Payment FOREIGN KEY(Rfc,LocalPaymentId)
    REFERENCES restaurante.Payment(Rfc,Id),
  CONSTRAINT CK_PaymentGatewayTransaction_Provider CHECK(Provider='PayPal'),
  CONSTRAINT CK_PaymentGatewayTransaction_Status CHECK([Status] IN
    ('Pending','Completed','Denied','Reversed','PartiallyRefunded','Refunded')),
  CONSTRAINT CK_PaymentGatewayTransaction_Amounts CHECK
    (GrossAmount>0 AND (FeeAmount IS NULL OR FeeAmount>=0)
     AND (NetAmount IS NULL OR NetAmount>=0)
     AND (FeeAmount IS NULL OR NetAmount IS NULL OR GrossAmount=FeeAmount+NetAmount)),
  CONSTRAINT CK_PaymentGatewayTransaction_Currency CHECK(CurrencyCode=UPPER(CurrencyCode))
);
CREATE UNIQUE INDEX UX_PaymentGatewayTransaction_Capture
  ON restaurante.PaymentGatewayTransaction(MerchantProfileKey,ProviderCaptureId);
CREATE UNIQUE INDEX UX_PaymentGatewayTransaction_Idempotency
  ON restaurante.PaymentGatewayTransaction(PublicSiteId,IdempotencyKey);
CREATE UNIQUE INDEX UX_PaymentGatewayTransaction_Checkout
  ON restaurante.PaymentGatewayTransaction(PublicSiteId,CheckoutAttemptId);
CREATE UNIQUE INDEX UX_PaymentGatewayTransaction_LocalPayment
  ON restaurante.PaymentGatewayTransaction(Rfc,LocalPaymentId)
  WHERE LocalPaymentId IS NOT NULL;

CREATE TABLE restaurante.PaymentGatewayRefund
(
  Id uniqueidentifier NOT NULL CONSTRAINT PK_PaymentGatewayRefund PRIMARY KEY,
  PublicSiteId bigint NOT NULL,
  Rfc varchar(50) NOT NULL,
  SiteId int NOT NULL,
  GatewayTransactionId uniqueidentifier NOT NULL,
  LocalRefundId uniqueidentifier NULL,
  ProviderRefundId varchar(64) NULL,
  Amount decimal(18,2) NOT NULL,
  CurrencyCode char(3) NOT NULL,
  Reason nvarchar(500) NOT NULL,
  [Status] varchar(30) NOT NULL CONSTRAINT DF_PaymentGatewayRefund_Status DEFAULT('Requested'),
  IdempotencyKey varchar(100) NOT NULL,
  RequestedBy nvarchar(256) NOT NULL,
  AuthorizedBy nvarchar(256) NOT NULL,
  Attempts int NOT NULL CONSTRAINT DF_PaymentGatewayRefund_Attempts DEFAULT(0),
  LeaseId uniqueidentifier NULL,
  LeaseExpiresAtUtc datetime2(3) NULL,
  NextRetryAtUtc datetime2(3) NULL,
  FailureCode varchar(80) NULL,
  FailureMessage nvarchar(500) NULL,
  RequestedAtUtc datetime2(3) NOT NULL CONSTRAINT DF_PaymentGatewayRefund_Requested DEFAULT(SYSUTCDATETIME()),
  CompletedAtUtc datetime2(3) NULL,
  UpdatedAtUtc datetime2(3) NOT NULL CONSTRAINT DF_PaymentGatewayRefund_Updated DEFAULT(SYSUTCDATETIME()),
  RowVersion rowversion NOT NULL,
  CONSTRAINT UX_PaymentGatewayRefund_ScopeId UNIQUE(PublicSiteId,Rfc,SiteId,Id),
  CONSTRAINT FK_PaymentGatewayRefund_Transaction FOREIGN KEY(PublicSiteId,Rfc,SiteId,GatewayTransactionId)
    REFERENCES restaurante.PaymentGatewayTransaction(PublicSiteId,Rfc,SiteId,Id),
  CONSTRAINT FK_PaymentGatewayRefund_LocalRefund FOREIGN KEY(Rfc,LocalRefundId)
    REFERENCES restaurante.PaymentRefund(Rfc,Id),
  CONSTRAINT CK_PaymentGatewayRefund_Status CHECK([Status] IN('Requested','Processing','Pending','Completed','Failed')),
  CONSTRAINT CK_PaymentGatewayRefund_Amount CHECK(Amount>0),
  CONSTRAINT CK_PaymentGatewayRefund_Currency CHECK(CurrencyCode=UPPER(CurrencyCode)),
  CONSTRAINT CK_PaymentGatewayRefund_Reason CHECK(NULLIF(LTRIM(RTRIM(Reason)),N'') IS NOT NULL),
  CONSTRAINT CK_PaymentGatewayRefund_Lease CHECK
    ((LeaseId IS NULL AND LeaseExpiresAtUtc IS NULL)
     OR (LeaseId IS NOT NULL AND LeaseExpiresAtUtc IS NOT NULL))
);
CREATE UNIQUE INDEX UX_PaymentGatewayRefund_Idempotency
  ON restaurante.PaymentGatewayRefund(PublicSiteId,IdempotencyKey);
CREATE UNIQUE INDEX UX_PaymentGatewayRefund_ProviderRefund
  ON restaurante.PaymentGatewayRefund(PublicSiteId,ProviderRefundId)
  WHERE ProviderRefundId IS NOT NULL;
CREATE UNIQUE INDEX UX_PaymentGatewayRefund_LocalRefund
  ON restaurante.PaymentGatewayRefund(Rfc,LocalRefundId)
  WHERE LocalRefundId IS NOT NULL;
CREATE INDEX IX_PaymentGatewayRefund_Work
  ON restaurante.PaymentGatewayRefund(PublicSiteId,[Status],NextRetryAtUtc,LeaseExpiresAtUtc);

CREATE TABLE restaurante.PaymentGatewayEvent
(
  Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_PaymentGatewayEvent PRIMARY KEY,
  PublicSiteId bigint NOT NULL,
  Rfc varchar(50) NOT NULL,
  SiteId int NOT NULL,
  Provider varchar(30) NOT NULL,
  MerchantProfileKey varchar(80) NOT NULL,
  ProviderEventId varchar(100) NOT NULL,
  EventType varchar(100) NOT NULL,
  ResourceType varchar(80) NULL,
  ResourceId varchar(100) NULL,
  CheckoutAttemptId uniqueidentifier NOT NULL,
  PayloadHash char(64) NOT NULL,
  VerificationStatus varchar(20) NOT NULL,
  ProcessingStatus varchar(20) NOT NULL CONSTRAINT DF_PaymentGatewayEvent_Processing DEFAULT('Pending'),
  Attempts int NOT NULL CONSTRAINT DF_PaymentGatewayEvent_Attempts DEFAULT(0),
  LeaseId uniqueidentifier NULL,
  LeaseExpiresAtUtc datetime2(3) NULL,
  NextRetryAtUtc datetime2(3) NULL,
  FailureCode varchar(80) NULL,
  FailureMessage nvarchar(500) NULL,
  ReceivedAtUtc datetime2(3) NOT NULL CONSTRAINT DF_PaymentGatewayEvent_Received DEFAULT(SYSUTCDATETIME()),
  ProcessedAtUtc datetime2(3) NULL,
  UpdatedAtUtc datetime2(3) NOT NULL CONSTRAINT DF_PaymentGatewayEvent_Updated DEFAULT(SYSUTCDATETIME()),
  RowVersion rowversion NOT NULL,
  CONSTRAINT FK_PaymentGatewayEvent_Settings FOREIGN KEY(PublicSiteId,Rfc,SiteId)
    REFERENCES restaurante.OnlineOrderingSettings(PublicSiteId,Rfc,SiteId),
  CONSTRAINT FK_PaymentGatewayEvent_Attempt FOREIGN KEY(PublicSiteId,Rfc,SiteId,CheckoutAttemptId)
    REFERENCES restaurante.OnlineCheckoutAttempt(PublicSiteId,Rfc,SiteId,Id),
  CONSTRAINT CK_PaymentGatewayEvent_Provider CHECK(Provider='PayPal'),
  CONSTRAINT CK_PaymentGatewayEvent_PayloadHash CHECK
    (LEN(PayloadHash)=64 AND PayloadHash NOT LIKE '%[^0-9A-F]%' COLLATE Latin1_General_100_BIN2),
  CONSTRAINT CK_PaymentGatewayEvent_Verification CHECK(VerificationStatus IN('Verified','Rejected')),
  CONSTRAINT CK_PaymentGatewayEvent_Processing CHECK(ProcessingStatus IN('Pending','Processing','Processed','Ignored','Failed')),
  CONSTRAINT CK_PaymentGatewayEvent_Lease CHECK
    ((LeaseId IS NULL AND LeaseExpiresAtUtc IS NULL)
     OR (LeaseId IS NOT NULL AND LeaseExpiresAtUtc IS NOT NULL))
);
CREATE UNIQUE INDEX UX_PaymentGatewayEvent_ProviderEvent
  ON restaurante.PaymentGatewayEvent(MerchantProfileKey,ProviderEventId);
CREATE INDEX IX_PaymentGatewayEvent_Work
  ON restaurante.PaymentGatewayEvent(PublicSiteId,ProcessingStatus,NextRetryAtUtc,LeaseExpiresAtUtc,ReceivedAtUtc);

CREATE TABLE restaurante.OnlineOrderNotification
(
  Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_OnlineOrderNotification PRIMARY KEY,
  PublicSiteId bigint NOT NULL,
  Rfc varchar(50) NOT NULL,
  SiteId int NOT NULL,
  CheckoutAttemptId uniqueidentifier NOT NULL,
  RestaurantOrderId uniqueidentifier NOT NULL,
  NotificationType varchar(30) NOT NULL,
  RecipientEmail nvarchar(320) NOT NULL,
  [Status] varchar(20) NOT NULL CONSTRAINT DF_OnlineOrderNotification_Status DEFAULT('Pending'),
  IdempotencyKey varchar(100) NOT NULL,
  Attempts int NOT NULL CONSTRAINT DF_OnlineOrderNotification_Attempts DEFAULT(0),
  LeaseId uniqueidentifier NULL,
  LeaseExpiresAtUtc datetime2(3) NULL,
  NextRetryAtUtc datetime2(3) NULL,
  FailureCode varchar(80) NULL,
  FailureMessage nvarchar(500) NULL,
  CreatedAtUtc datetime2(3) NOT NULL CONSTRAINT DF_OnlineOrderNotification_Created DEFAULT(SYSUTCDATETIME()),
  SentAtUtc datetime2(3) NULL,
  UpdatedAtUtc datetime2(3) NOT NULL CONSTRAINT DF_OnlineOrderNotification_Updated DEFAULT(SYSUTCDATETIME()),
  RowVersion rowversion NOT NULL,
  CONSTRAINT FK_OnlineOrderNotification_Attempt FOREIGN KEY(PublicSiteId,Rfc,SiteId,CheckoutAttemptId)
    REFERENCES restaurante.OnlineCheckoutAttempt(PublicSiteId,Rfc,SiteId,Id),
  CONSTRAINT FK_OnlineOrderNotification_Order FOREIGN KEY(PublicSiteId,Rfc,SiteId,RestaurantOrderId)
    REFERENCES restaurante.[Order](PublicSiteId,Rfc,SiteId,Id),
  CONSTRAINT CK_OnlineOrderNotification_Type CHECK(NotificationType IN('Confirmation','Ready')),
  CONSTRAINT CK_OnlineOrderNotification_Status CHECK([Status] IN('Pending','Processing','Sent','Failed')),
  CONSTRAINT CK_OnlineOrderNotification_Email CHECK(NULLIF(LTRIM(RTRIM(RecipientEmail)),N'') IS NOT NULL),
  CONSTRAINT CK_OnlineOrderNotification_Lease CHECK
    ((LeaseId IS NULL AND LeaseExpiresAtUtc IS NULL)
     OR (LeaseId IS NOT NULL AND LeaseExpiresAtUtc IS NOT NULL))
);
CREATE UNIQUE INDEX UX_OnlineOrderNotification_Idempotency
  ON restaurante.OnlineOrderNotification(PublicSiteId,IdempotencyKey);
CREATE UNIQUE INDEX UX_OnlineOrderNotification_Type
  ON restaurante.OnlineOrderNotification(PublicSiteId,CheckoutAttemptId,NotificationType);
CREATE INDEX IX_OnlineOrderNotification_Work
  ON restaurante.OnlineOrderNotification(PublicSiteId,[Status],NextRetryAtUtc,LeaseExpiresAtUtc);

/* Bind Bruno's exact central PublicSite to its legacy Restaurant site. */
DECLARE @TargetPublicSiteId bigint;
DECLARE @TargetCompanyId bigint;
DECLARE @TargetOrionSiteId bigint;
DECLARE @TargetRfc varchar(50)='BRUNOS260707L26';

SELECT @TargetPublicSiteId=publicSite.PublicSiteId,
  @TargetCompanyId=publicSite.CompanyId,@TargetOrionSiteId=publicSite.SiteId
FROM orion.PublicSite publicSite
JOIN orion.Company companyInfo ON companyInfo.CompanyId=publicSite.CompanyId
JOIN orion.Site platformSite
  ON platformSite.CompanyId=publicSite.CompanyId AND platformSite.SiteId=publicSite.SiteId
WHERE publicSite.PublicSiteKey='brunos-main'
  AND publicSite.ModuleCode='RESTAURANT'
  AND companyInfo.Rfc=@TargetRfc
  AND platformSite.SiteKey='brunos-01'
  AND publicSite.IsActive=1 AND companyInfo.IsActive=1 AND platformSite.IsActive=1;

IF @TargetPublicSiteId IS NULL
  THROW 53611,'No existe el binding central exacto brunos-main/BRUNOS260707L26/brunos-01.',1;

EXEC sys.sp_set_session_context @key=N'OrionRfc',@value=@TargetRfc;
EXEC sys.sp_set_session_context @key=N'OrionERP.CompanyId',@value=@TargetCompanyId;
EXEC sys.sp_set_session_context @key=N'OrionERP.SiteId',@value=@TargetOrionSiteId;
EXEC sys.sp_set_session_context @key=N'OrionERP.PublicSiteId',@value=@TargetPublicSiteId;

IF NOT EXISTS
(
  SELECT 1
  FROM orion.PublicSite publicSite
  JOIN orion.Company companyInfo ON companyInfo.CompanyId=publicSite.CompanyId
  LEFT JOIN restaurante.Site restaurantSite
    ON restaurantSite.OrionCompanyId=publicSite.CompanyId
   AND restaurantSite.OrionSiteId=publicSite.SiteId
   AND restaurantSite.Rfc=companyInfo.Rfc
  LEFT JOIN restaurante.PublicSiteSettings publicSettings
    ON publicSettings.PublicSiteId=publicSite.PublicSiteId
   AND publicSettings.Rfc=restaurantSite.Rfc
   AND publicSettings.SiteId=restaurantSite.Id
  WHERE publicSite.PublicSiteId=@TargetPublicSiteId
    AND publicSite.ModuleCode='RESTAURANT'
    AND restaurantSite.Id IS NOT NULL AND publicSettings.PublicSiteId IS NOT NULL
)
  THROW 53611,'Un PublicSite Restaurant no tiene binding legacy/configuracion exactos.',1;

INSERT restaurante.OnlineOrderingSettings
(
  PublicSiteId,Rfc,SiteId,CompanyId,OrionSiteId,IsEnabled,IsPaused,PauseMessage,
  WeeklyScheduleJson,MinimumOrderTotal,MaximumOrderTotal,GuestCheckoutEnabled,
  PickupEnabled,TermsVersion,PrivacyVersion,ActiveMerchantProfileKey,
  GatewayEnvironment,GatewayCredentialsConfigured,GatewayWebhookConfigured,
  GatewayReadinessAtUtc,ProcessorHeartbeatAtUtc,ConfigurationVersion,UpdatedBy
)
SELECT publicSite.PublicSiteId,restaurantSite.Rfc,restaurantSite.Id,
       publicSite.CompanyId,publicSite.SiteId,0,0,NULL,
       publicSettings.OpeningHoursJson,0,2500,1,1,
       'online-orders-v1','online-orders-v1','shared-paypal-v1',
       'Unknown',0,0,NULL,NULL,1,
       N'20260914_restaurant_online_ordering'
FROM orion.PublicSite publicSite
JOIN orion.Company companyInfo ON companyInfo.CompanyId=publicSite.CompanyId
JOIN restaurante.Site restaurantSite
  ON restaurantSite.OrionCompanyId=publicSite.CompanyId
 AND restaurantSite.OrionSiteId=publicSite.SiteId
 AND restaurantSite.Rfc=companyInfo.Rfc
JOIN restaurante.PublicSiteSettings publicSettings
  ON publicSettings.PublicSiteId=publicSite.PublicSiteId
 AND publicSettings.Rfc=restaurantSite.Rfc
 AND publicSettings.SiteId=restaurantSite.Id
WHERE publicSite.PublicSiteId=@TargetPublicSiteId AND publicSite.ModuleCode='RESTAURANT';

INSERT restaurante.OnlineOrderProduct
  (PublicSiteId,Rfc,SiteId,ProductId,IsEnabled,UpdatedBy)
SELECT settings.PublicSiteId,settings.Rfc,settings.SiteId,product.Id,0,
       N'20260914_restaurant_online_ordering'
FROM restaurante.OnlineOrderingSettings settings
JOIN restaurante.Product product ON product.Rfc=settings.Rfc;

IF EXISTS(SELECT 1 FROM restaurante.OnlineOrderingSettings WHERE IsEnabled<>0 OR MaximumOrderTotal<>2500)
  THROW 53612,'Online ordering no quedo sembrado apagado con maximo MXN 2500.',1;
IF EXISTS(SELECT 1 FROM restaurante.OnlineOrderProduct WHERE IsEnabled<>0)
  THROW 53613,'Un producto quedo habilitado durante la migracion.',1;

IF OBJECT_ID(N'orion.TenantTableClassification',N'U') IS NOT NULL
BEGIN
  INSERT orion.TenantTableClassification
    (SchemaName,TableName,Classification,OwnerColumn,ReviewedInMigrationId,Notes)
  SELECT N'restaurante',source.TableName,'TENANT_OWNED',N'Rfc',@MigrationId,
         N'Online ordering aislado por RFC y PublicSite mediante dos politicas RLS.'
  FROM(VALUES
    (N'OnlineOrderingSettings'),(N'OnlineOrderProduct'),(N'OnlineCheckoutAttempt'),
    (N'PaymentGatewayTransaction'),(N'PaymentGatewayRefund'),
    (N'PaymentGatewayEvent'),(N'OnlineOrderNotification')) source(TableName)
  WHERE NOT EXISTS
  (
    SELECT 1 FROM orion.TenantTableClassification target
    WHERE target.SchemaName=N'restaurante' AND target.TableName=source.TableName
  );
END;
GO

CREATE FUNCTION restaurante.fn_OnlineOrderingScopePredicate
(
  @PublicSiteId bigint,
  @Rfc varchar(50)
)
RETURNS TABLE WITH SCHEMABINDING AS RETURN
  SELECT 1 AS Allowed
  WHERE @Rfc=CONVERT(varchar(50),SESSION_CONTEXT(N'OrionRfc'))
    AND EXISTS
    (
      SELECT 1
      FROM orion.PublicSite publicSite
      JOIN orion.Company companyInfo ON companyInfo.CompanyId=publicSite.CompanyId
      WHERE publicSite.PublicSiteId=@PublicSiteId
        AND publicSite.ModuleCode='RESTAURANT'
        AND publicSite.IsActive=CONVERT(bit,1)
        AND companyInfo.IsActive=CONVERT(bit,1)
        AND companyInfo.Rfc=@Rfc
        AND
        (
          EXISTS
          (
            SELECT 1 FROM orion.PublicSqlPrincipalBinding binding
            WHERE binding.PrincipalName=USER_NAME()
              AND binding.PublicSiteId=@PublicSiteId
              AND binding.IsActive=CONVERT(bit,1)
              AND @PublicSiteId=TRY_CONVERT(bigint,SESSION_CONTEXT(N'OrionERP.PublicSiteId'))
          )
          OR
          (
            NOT EXISTS
              (SELECT 1 FROM orion.PublicSqlPrincipalBinding binding WHERE binding.PrincipalName=USER_NAME())
            AND publicSite.CompanyId=TRY_CONVERT(bigint,SESSION_CONTEXT(N'OrionERP.CompanyId'))
            AND
            (
              SESSION_CONTEXT(N'OrionERP.SiteId') IS NULL
              OR publicSite.SiteId=TRY_CONVERT(bigint,SESSION_CONTEXT(N'OrionERP.SiteId'))
            )
          )
        )
    );
GO

DECLARE @MigrationId nvarchar(200)=
  (SELECT MigrationId FROM #RestaurantOnlineOrderingMigrationState);

CREATE SECURITY POLICY restaurante.OnlineOrderingScopePolicy
  ADD FILTER PREDICATE restaurante.fn_OnlineOrderingScopePredicate(PublicSiteId,Rfc)
    ON restaurante.OnlineOrderingSettings,
  ADD BLOCK PREDICATE restaurante.fn_OnlineOrderingScopePredicate(PublicSiteId,Rfc)
    ON restaurante.OnlineOrderingSettings AFTER INSERT,
  ADD BLOCK PREDICATE restaurante.fn_OnlineOrderingScopePredicate(PublicSiteId,Rfc)
    ON restaurante.OnlineOrderingSettings AFTER UPDATE,
  ADD FILTER PREDICATE restaurante.fn_OnlineOrderingScopePredicate(PublicSiteId,Rfc)
    ON restaurante.OnlineOrderProduct,
  ADD BLOCK PREDICATE restaurante.fn_OnlineOrderingScopePredicate(PublicSiteId,Rfc)
    ON restaurante.OnlineOrderProduct AFTER INSERT,
  ADD BLOCK PREDICATE restaurante.fn_OnlineOrderingScopePredicate(PublicSiteId,Rfc)
    ON restaurante.OnlineOrderProduct AFTER UPDATE,
  ADD FILTER PREDICATE restaurante.fn_OnlineOrderingScopePredicate(PublicSiteId,Rfc)
    ON restaurante.OnlineCheckoutAttempt,
  ADD BLOCK PREDICATE restaurante.fn_OnlineOrderingScopePredicate(PublicSiteId,Rfc)
    ON restaurante.OnlineCheckoutAttempt AFTER INSERT,
  ADD BLOCK PREDICATE restaurante.fn_OnlineOrderingScopePredicate(PublicSiteId,Rfc)
    ON restaurante.OnlineCheckoutAttempt AFTER UPDATE,
  ADD FILTER PREDICATE restaurante.fn_OnlineOrderingScopePredicate(PublicSiteId,Rfc)
    ON restaurante.PaymentGatewayTransaction,
  ADD BLOCK PREDICATE restaurante.fn_OnlineOrderingScopePredicate(PublicSiteId,Rfc)
    ON restaurante.PaymentGatewayTransaction AFTER INSERT,
  ADD BLOCK PREDICATE restaurante.fn_OnlineOrderingScopePredicate(PublicSiteId,Rfc)
    ON restaurante.PaymentGatewayTransaction AFTER UPDATE,
  ADD FILTER PREDICATE restaurante.fn_OnlineOrderingScopePredicate(PublicSiteId,Rfc)
    ON restaurante.PaymentGatewayRefund,
  ADD BLOCK PREDICATE restaurante.fn_OnlineOrderingScopePredicate(PublicSiteId,Rfc)
    ON restaurante.PaymentGatewayRefund AFTER INSERT,
  ADD BLOCK PREDICATE restaurante.fn_OnlineOrderingScopePredicate(PublicSiteId,Rfc)
    ON restaurante.PaymentGatewayRefund AFTER UPDATE,
  ADD FILTER PREDICATE restaurante.fn_OnlineOrderingScopePredicate(PublicSiteId,Rfc)
    ON restaurante.PaymentGatewayEvent,
  ADD BLOCK PREDICATE restaurante.fn_OnlineOrderingScopePredicate(PublicSiteId,Rfc)
    ON restaurante.PaymentGatewayEvent AFTER INSERT,
  ADD BLOCK PREDICATE restaurante.fn_OnlineOrderingScopePredicate(PublicSiteId,Rfc)
    ON restaurante.PaymentGatewayEvent AFTER UPDATE,
  ADD FILTER PREDICATE restaurante.fn_OnlineOrderingScopePredicate(PublicSiteId,Rfc)
    ON restaurante.OnlineOrderNotification,
  ADD BLOCK PREDICATE restaurante.fn_OnlineOrderingScopePredicate(PublicSiteId,Rfc)
    ON restaurante.OnlineOrderNotification AFTER INSERT,
  ADD BLOCK PREDICATE restaurante.fn_OnlineOrderingScopePredicate(PublicSiteId,Rfc)
    ON restaurante.OnlineOrderNotification AFTER UPDATE
  WITH(STATE=ON,SCHEMABINDING=ON);
GO

CREATE OR ALTER VIEW restaurante.vw_PublicOnlineOrderingConfiguration
AS
SELECT
  settings.PublicSiteId,
  publicSite.PublicSiteKey,
  publicSite.CanonicalHost,
  settings.Rfc,
  settings.SiteId,
  settings.CompanyId,
  settings.OrionSiteId,
  restaurantSite.TimeZoneId,
  settings.IsEnabled,
  settings.IsPaused,
  settings.PauseMessage,
  settings.WeeklyScheduleJson,
  settings.MinimumOrderTotal,
  settings.MaximumOrderTotal,
  settings.GuestCheckoutEnabled,
  settings.PickupEnabled,
  settings.TermsVersion,
  settings.PrivacyVersion,
  settings.ActiveMerchantProfileKey,
  settings.GatewayEnvironment,
  settings.GatewayCredentialsConfigured,
  settings.GatewayWebhookConfigured,
  settings.GatewayReadinessAtUtc,
  settings.ProcessorHeartbeatAtUtc,
  settings.ConfigurationVersion,
  settings.UpdatedAtUtc,
  settings.RowVersion
FROM restaurante.OnlineOrderingSettings settings
JOIN orion.PublicSite publicSite
  ON publicSite.PublicSiteId=settings.PublicSiteId
JOIN restaurante.Site restaurantSite
  ON restaurantSite.Rfc=settings.Rfc
 AND restaurantSite.Id=settings.SiteId
WHERE publicSite.ModuleCode='RESTAURANT';
GO

CREATE OR ALTER VIEW restaurante.vw_PublicOnlineCheckoutStatus
AS
SELECT
  attempt.PublicSiteId,
  attempt.Id CheckoutAttemptId,
  attempt.[State],
  CONVERT(bit,CASE WHEN attempt.[State] IN
    ('Captured','CapturedNeedsOrder','PosCreated','RefundRequested','RefundPending','Refunded')
    THEN 1 ELSE 0 END) IsPaymentReceived,
  CONVERT(bit,CASE WHEN attempt.[State] IN
    ('CapturePending','Captured','CapturedNeedsOrder','RefundRequested','RefundPending')
    THEN 1 ELSE 0 END) IsPendingRecovery,
  attempt.RestaurantOrderId,
  orderInfo.Folio,
  orderInfo.[Status] RestaurantOrderStatus,
  orderInfo.PaymentStatus,
  attempt.FailureCode,
  attempt.CreatedAtUtc,
  attempt.UpdatedAtUtc,
  attempt.CapturedAtUtc,
  attempt.PosCreatedAtUtc,
  orderInfo.SentToKitchenAt,
  orderInfo.ReadyAt,
  orderInfo.CompletedAt,
  orderInfo.CancelledAt
FROM restaurante.OnlineCheckoutAttempt attempt
LEFT JOIN restaurante.[Order] orderInfo
  ON orderInfo.PublicSiteId=attempt.PublicSiteId
 AND orderInfo.Rfc=attempt.Rfc
 AND orderInfo.SiteId=attempt.SiteId
 AND orderInfo.Id=attempt.RestaurantOrderId;
GO

CREATE OR ALTER PROCEDURE restaurante.OnlineOrderingBootstrapGet
AS
BEGIN
  SET NOCOUNT ON;

  DECLARE @PublicSiteId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N'OrionERP.PublicSiteId'));
  DECLARE @Rfc varchar(50)=CONVERT(varchar(50),SESSION_CONTEXT(N'OrionRfc'));

  IF @PublicSiteId IS NULL OR NULLIF(@Rfc,'') IS NULL
    THROW 53620,'No existe un contexto publico de restaurante verificado.',1;

  IF NOT EXISTS
  (
    SELECT 1
    FROM restaurante.OnlineOrderingSettings
    WHERE PublicSiteId=@PublicSiteId AND Rfc=@Rfc
  )
    THROW 53621,'El sitio publico no tiene configuracion de pedidos.',1;

  SELECT
    PublicSiteId,PublicSiteKey,CanonicalHost,Rfc,SiteId,CompanyId,OrionSiteId,
    TimeZoneId,IsEnabled,IsPaused,PauseMessage,WeeklyScheduleJson,
    MinimumOrderTotal,MaximumOrderTotal,GuestCheckoutEnabled,PickupEnabled,
    TermsVersion,PrivacyVersion,ActiveMerchantProfileKey,GatewayEnvironment,
    GatewayCredentialsConfigured,GatewayWebhookConfigured,GatewayReadinessAtUtc,
    ProcessorHeartbeatAtUtc,ConfigurationVersion,UpdatedAtUtc,RowVersion
  FROM restaurante.vw_PublicOnlineOrderingConfiguration
  WHERE PublicSiteId=@PublicSiteId AND Rfc=@Rfc;

  SELECT ProductId
  FROM restaurante.OnlineOrderProduct
  WHERE PublicSiteId=@PublicSiteId AND Rfc=@Rfc AND IsEnabled=1
  ORDER BY ProductId;
END;
GO

CREATE OR ALTER PROCEDURE restaurante.OnlineCheckoutAttemptCreate
  @Id uniqueidentifier,
  @ClientAttemptId uniqueidentifier,
  @MemberId uniqueidentifier=NULL,
  @CustomerName nvarchar(150),
  @CustomerEmail nvarchar(320),
  @CustomerPhone varchar(30),
  @QuoteFingerprint char(64),
  @QuoteExpiresAtUtc datetime2(3),
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

  DECLARE @PublicSiteId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N'OrionERP.PublicSiteId'));
  DECLARE @Rfc varchar(50)=CONVERT(varchar(50),SESSION_CONTEXT(N'OrionRfc'));
  DECLARE @SiteId int;
  DECLARE @ExistingId uniqueidentifier;
  DECLARE @WasCreated bit=0;

  IF @PublicSiteId IS NULL OR NULLIF(@Rfc,'') IS NULL
    THROW 53622,'No existe un contexto publico de restaurante verificado.',1;
  IF @Id IS NULL OR @ClientAttemptId IS NULL
    THROW 53623,'Los identificadores del checkout son obligatorios.',1;
  IF @QuoteExpiresAtUtc<=SYSUTCDATETIME()
    THROW 53624,'La cotizacion ya vencio.',1;
  IF @LegalAcceptedAtUtc>DATEADD(MINUTE,5,SYSUTCDATETIME())
    THROW 53625,'La aceptacion legal tiene una fecha invalida.',1;
  IF ISJSON(@CartSnapshotJson)<>1
    THROW 53626,'El carrito normalizado no es JSON valido.',1;
  IF @CurrencyCode<>UPPER(@CurrencyCode)
     OR LEN(@QuoteFingerprint)<>64
     OR @QuoteFingerprint LIKE '%[^0-9A-F]%' COLLATE Latin1_General_100_BIN2
    THROW 53627,'La moneda o huella de cotizacion es invalida.',1;

  BEGIN TRANSACTION;

  SELECT @SiteId=settings.SiteId
  FROM restaurante.OnlineOrderingSettings settings WITH(UPDLOCK,HOLDLOCK)
  WHERE settings.PublicSiteId=@PublicSiteId
    AND settings.Rfc=@Rfc
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
    THROW 53628,'El sitio no acepta este checkout en este momento.',1;
  END;

  IF @MemberId IS NOT NULL AND NOT EXISTS
  (
    SELECT 1 FROM fidelidad.MemberAccount member
    WHERE member.PublicSiteId=@PublicSiteId AND member.Rfc=@Rfc
      AND member.Id=@MemberId AND member.[Status]='Active'
  )
  BEGIN
    ROLLBACK TRANSACTION;
    THROW 53629,'La membresia no pertenece al sitio publico.',1;
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
        AND attempt.MerchantProfileKey=@MerchantProfileKey
        AND attempt.CurrencyCode=@CurrencyCode
        AND attempt.Total=@Total
        AND ((attempt.MemberId=@MemberId) OR (attempt.MemberId IS NULL AND @MemberId IS NULL))
    )
    BEGIN
      ROLLBACK TRANSACTION;
      THROW 53630,'ClientAttemptId ya se uso con otro checkout.',1;
    END;
    SET @Id=@ExistingId;
  END
  ELSE
  BEGIN
    INSERT restaurante.OnlineCheckoutAttempt
    (
      Id,PublicSiteId,Rfc,SiteId,ClientAttemptId,MemberId,
      CustomerName,CustomerEmail,CustomerPhone,QuoteFingerprint,QuoteExpiresAtUtc,
      CartSnapshotJson,PromotionCode,Subtotal,PromotionDiscountTotal,TaxTotal,Total,
      CurrencyCode,PrivacyVersion,TermsVersion,LegalAcceptedAtUtc,
      MerchantProfileKey,TrackingTokenHash
    )
    VALUES
    (
      @Id,@PublicSiteId,@Rfc,@SiteId,@ClientAttemptId,@MemberId,
      LTRIM(RTRIM(@CustomerName)),LOWER(LTRIM(RTRIM(@CustomerEmail))),LTRIM(RTRIM(@CustomerPhone)),
      @QuoteFingerprint,@QuoteExpiresAtUtc,@CartSnapshotJson,NULLIF(LTRIM(RTRIM(@PromotionCode)),''),
      @Subtotal,@PromotionDiscountTotal,@TaxTotal,@Total,@CurrencyCode,
      @PrivacyVersion,@TermsVersion,@LegalAcceptedAtUtc,@MerchantProfileKey,@TrackingTokenHash
    );
    SET @WasCreated=1;
  END;

  COMMIT TRANSACTION;

  SELECT @WasCreated WasCreated,
    attempt.Id,attempt.PublicSiteId,attempt.Rfc,attempt.SiteId,attempt.ClientAttemptId,
    attempt.MemberId,attempt.CustomerName,attempt.CustomerEmail,attempt.CustomerPhone,
    attempt.QuoteFingerprint,attempt.QuoteExpiresAtUtc,attempt.CartSnapshotJson,
    attempt.PromotionCode,attempt.Subtotal,attempt.PromotionDiscountTotal,attempt.TaxTotal,
    attempt.Total,attempt.CurrencyCode,attempt.PrivacyVersion,attempt.TermsVersion,
    attempt.LegalAcceptedAtUtc,attempt.MerchantProfileKey,attempt.TrackingTokenHash,attempt.PayPalCreateRequestId,
    attempt.PayPalOrderId,attempt.PayPalCaptureId,attempt.[State],attempt.RestaurantOrderId,
    attempt.ImportAttempts,attempt.NextRetryAtUtc,attempt.FailureCode,attempt.FailureMessage,
    attempt.CreatedAtUtc,attempt.UpdatedAtUtc,attempt.CapturedAtUtc,attempt.PosCreatedAtUtc,
    attempt.RowVersion
  FROM restaurante.OnlineCheckoutAttempt attempt
  WHERE attempt.Id=@Id AND attempt.PublicSiteId=@PublicSiteId AND attempt.Rfc=@Rfc;
END;
GO

CREATE OR ALTER PROCEDURE restaurante.OnlineCheckoutAttemptGet
  @Id uniqueidentifier=NULL,
  @ClientAttemptId uniqueidentifier=NULL,
  @TrackingTokenHash binary(32)=NULL,
  @PayPalOrderId varchar(64)=NULL,
  @PayPalCaptureId varchar(64)=NULL,
  @MerchantProfileKey varchar(80)=NULL
AS
BEGIN
  SET NOCOUNT ON;

  DECLARE @PublicSiteId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N'OrionERP.PublicSiteId'));
  DECLARE @Rfc varchar(50)=CONVERT(varchar(50),SESSION_CONTEXT(N'OrionRfc'));
  DECLARE @Selectors int=
    CASE WHEN @Id IS NULL THEN 0 ELSE 1 END+
    CASE WHEN @ClientAttemptId IS NULL THEN 0 ELSE 1 END+
    CASE WHEN @TrackingTokenHash IS NULL THEN 0 ELSE 1 END+
    CASE WHEN @PayPalOrderId IS NULL AND @PayPalCaptureId IS NULL THEN 0 ELSE 1 END;

  IF @PublicSiteId IS NULL OR NULLIF(@Rfc,'') IS NULL
    THROW 53631,'No existe un contexto publico de restaurante verificado.',1;
  IF @Selectors<>1
    THROW 53632,'Debe especificarse exactamente un selector de checkout.',1;
  IF (@PayPalOrderId IS NOT NULL OR @PayPalCaptureId IS NOT NULL)
     AND NULLIF(@MerchantProfileKey,'') IS NULL
    THROW 53633,'MerchantProfileKey es obligatorio para buscar IDs de PayPal.',1;

  SELECT
    attempt.Id,attempt.PublicSiteId,attempt.Rfc,attempt.SiteId,attempt.ClientAttemptId,
    attempt.MemberId,attempt.CustomerName,attempt.CustomerEmail,attempt.CustomerPhone,
    attempt.QuoteFingerprint,attempt.QuoteExpiresAtUtc,attempt.CartSnapshotJson,
    attempt.PromotionCode,attempt.Subtotal,attempt.PromotionDiscountTotal,attempt.TaxTotal,
    attempt.Total,attempt.CurrencyCode,attempt.PrivacyVersion,attempt.TermsVersion,
    attempt.LegalAcceptedAtUtc,attempt.MerchantProfileKey,attempt.TrackingTokenHash,attempt.PayPalCreateRequestId,
    attempt.PayPalOrderId,attempt.PayPalCaptureId,attempt.[State],attempt.RestaurantOrderId,
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
          (@PayPalOrderId IS NOT NULL AND attempt.PayPalOrderId=@PayPalOrderId)
          OR (@PayPalCaptureId IS NOT NULL AND attempt.PayPalCaptureId=@PayPalCaptureId)
        )
      )
    );
END;
GO

CREATE OR ALTER PROCEDURE restaurante.OnlineCheckoutPayPalOrderRecord
  @Id uniqueidentifier,
  @PayPalCreateRequestId varchar(100),
  @PayPalOrderId varchar(64)
AS
BEGIN
  SET NOCOUNT ON;
  SET XACT_ABORT ON;

  DECLARE @PublicSiteId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N'OrionERP.PublicSiteId'));
  DECLARE @Rfc varchar(50)=CONVERT(varchar(50),SESSION_CONTEXT(N'OrionRfc'));

  IF @PublicSiteId IS NULL OR NULLIF(@Rfc,'') IS NULL
    THROW 53634,'No existe un contexto publico de restaurante verificado.',1;
  IF NULLIF(LTRIM(RTRIM(@PayPalCreateRequestId)),'') IS NULL
     OR NULLIF(LTRIM(RTRIM(@PayPalOrderId)),'') IS NULL
    THROW 53635,'Los identificadores de PayPal son obligatorios.',1;

  BEGIN TRANSACTION;

  IF NOT EXISTS
  (
    SELECT 1 FROM restaurante.OnlineCheckoutAttempt WITH(UPDLOCK,HOLDLOCK)
    WHERE Id=@Id AND PublicSiteId=@PublicSiteId AND Rfc=@Rfc
  )
  BEGIN
    ROLLBACK TRANSACTION;
    THROW 53636,'No existe el checkout en el sitio publico.',1;
  END;

  IF EXISTS
  (
    SELECT 1 FROM restaurante.OnlineCheckoutAttempt
    WHERE Id=@Id
      AND
      (
        (PayPalCreateRequestId IS NOT NULL AND PayPalCreateRequestId<>@PayPalCreateRequestId)
        OR (PayPalOrderId IS NOT NULL AND PayPalOrderId<>@PayPalOrderId)
      )
  )
  BEGIN
    ROLLBACK TRANSACTION;
    THROW 53637,'El checkout ya esta ligado a otro pedido PayPal.',1;
  END;

  IF EXISTS
  (
    SELECT 1 FROM restaurante.OnlineCheckoutAttempt
    WHERE Id=@Id AND [State] NOT IN('Quoted','PayPalCreated','RequoteRequired')
  )
  BEGIN
    ROLLBACK TRANSACTION;
    THROW 53638,'El checkout no admite registrar un pedido PayPal.',1;
  END;

  UPDATE restaurante.OnlineCheckoutAttempt
  SET PayPalCreateRequestId=COALESCE(PayPalCreateRequestId,@PayPalCreateRequestId),
      PayPalOrderId=COALESCE(PayPalOrderId,@PayPalOrderId),
      [State]=CASE WHEN [State]='Quoted' THEN 'PayPalCreated' ELSE [State] END,
      UpdatedAtUtc=SYSUTCDATETIME()
  WHERE Id=@Id AND PublicSiteId=@PublicSiteId AND Rfc=@Rfc;

  COMMIT TRANSACTION;

  SELECT
    attempt.Id,attempt.PublicSiteId,attempt.Rfc,attempt.SiteId,attempt.ClientAttemptId,
    attempt.MemberId,attempt.CustomerName,attempt.CustomerEmail,attempt.CustomerPhone,
    attempt.QuoteFingerprint,attempt.QuoteExpiresAtUtc,attempt.CartSnapshotJson,
    attempt.PromotionCode,attempt.Subtotal,attempt.PromotionDiscountTotal,attempt.TaxTotal,
    attempt.Total,attempt.CurrencyCode,attempt.PrivacyVersion,attempt.TermsVersion,
    attempt.LegalAcceptedAtUtc,attempt.MerchantProfileKey,attempt.TrackingTokenHash,attempt.PayPalCreateRequestId,
    attempt.PayPalOrderId,attempt.PayPalCaptureId,attempt.[State],attempt.RestaurantOrderId,
    attempt.ImportAttempts,attempt.NextRetryAtUtc,attempt.FailureCode,attempt.FailureMessage,
    attempt.CreatedAtUtc,attempt.UpdatedAtUtc,attempt.CapturedAtUtc,attempt.PosCreatedAtUtc,
    orderInfo.Folio OrderFolio,attempt.RowVersion
  FROM restaurante.OnlineCheckoutAttempt attempt
  LEFT JOIN restaurante.[Order] orderInfo
    ON orderInfo.PublicSiteId=attempt.PublicSiteId AND orderInfo.Rfc=attempt.Rfc
   AND orderInfo.SiteId=attempt.SiteId AND orderInfo.Id=attempt.RestaurantOrderId
  WHERE attempt.Id=@Id AND attempt.PublicSiteId=@PublicSiteId AND attempt.Rfc=@Rfc;
END;
GO

CREATE OR ALTER PROCEDURE restaurante.OnlineCheckoutCaptureRecord
  @Id uniqueidentifier,
  @PayPalOrderId varchar(64),
  @PayPalCaptureId varchar(64)=NULL,
  @Status varchar(30),
  @GrossAmount decimal(18,2),
  @FeeAmount decimal(18,2)=NULL,
  @NetAmount decimal(18,2)=NULL,
  @CurrencyCode char(3),
  @IdempotencyKey varchar(100),
  @CapturedAtUtc datetime2(3)=NULL
AS
BEGIN
  SET NOCOUNT ON;
  SET XACT_ABORT ON;

  DECLARE @PublicSiteId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N'OrionERP.PublicSiteId'));
  DECLARE @Rfc varchar(50)=CONVERT(varchar(50),SESSION_CONTEXT(N'OrionRfc'));
  DECLARE @SiteId int;
  DECLARE @MerchantProfileKey varchar(80);
  DECLARE @ExpectedAmount decimal(18,2);
  DECLARE @ExpectedCurrency char(3);
  DECLARE @CurrentState varchar(30);
  DECLARE @TransactionId uniqueidentifier;

  IF @PublicSiteId IS NULL OR NULLIF(@Rfc,'') IS NULL
    THROW 53639,'No existe un contexto publico de restaurante verificado.',1;
  IF @Status NOT IN('Pending','Completed','Denied')
    THROW 53640,'El estado de captura PayPal no es valido.',1;
  IF @Status IN('Pending','Completed') AND NULLIF(LTRIM(RTRIM(@PayPalCaptureId)),'') IS NULL
    THROW 53641,'La captura PayPal es obligatoria para este estado.',1;
  IF NULLIF(LTRIM(RTRIM(@IdempotencyKey)),'') IS NULL
    THROW 53642,'La idempotencia de captura es obligatoria.',1;

  BEGIN TRANSACTION;

  SELECT
    @SiteId=attempt.SiteId,
    @MerchantProfileKey=attempt.MerchantProfileKey,
    @ExpectedAmount=attempt.Total,
    @ExpectedCurrency=attempt.CurrencyCode,
    @CurrentState=attempt.[State]
  FROM restaurante.OnlineCheckoutAttempt attempt WITH(UPDLOCK,HOLDLOCK)
  WHERE attempt.Id=@Id
    AND attempt.PublicSiteId=@PublicSiteId
    AND attempt.Rfc=@Rfc
    AND attempt.PayPalOrderId=@PayPalOrderId;

  IF @SiteId IS NULL
  BEGIN
    ROLLBACK TRANSACTION;
    THROW 53643,'El pedido PayPal no pertenece al checkout publico.',1;
  END;
  IF @GrossAmount<>@ExpectedAmount OR @CurrencyCode<>@ExpectedCurrency
  BEGIN
    ROLLBACK TRANSACTION;
    THROW 53644,'La captura no coincide con el importe y moneda cotizados.',1;
  END;
  IF @CurrentState IN('PosCreated','RefundRequested','RefundPending','Refunded')
     AND @Status<>'Completed'
  BEGIN
    ROLLBACK TRANSACTION;
    THROW 53645,'Una captura confirmada no puede retroceder de estado.',1;
  END;

  IF @PayPalCaptureId IS NOT NULL
  BEGIN
    IF EXISTS
    (
      SELECT 1 FROM restaurante.OnlineCheckoutAttempt
      WHERE Id=@Id AND PayPalCaptureId IS NOT NULL AND PayPalCaptureId<>@PayPalCaptureId
    )
    BEGIN
      ROLLBACK TRANSACTION;
      THROW 53647,'El checkout ya esta ligado a otra captura PayPal.',1;
    END;

    SELECT @TransactionId=transactionInfo.Id
    FROM restaurante.PaymentGatewayTransaction transactionInfo WITH(UPDLOCK,HOLDLOCK)
    WHERE transactionInfo.PublicSiteId=@PublicSiteId
      AND transactionInfo.CheckoutAttemptId=@Id;

    IF @TransactionId IS NULL
    BEGIN
      SET @TransactionId=NEWID();
      INSERT restaurante.PaymentGatewayTransaction
      (
        Id,PublicSiteId,Rfc,SiteId,CheckoutAttemptId,Provider,MerchantProfileKey,
        ProviderOrderId,ProviderCaptureId,[Status],GrossAmount,FeeAmount,NetAmount,
        CurrencyCode,IdempotencyKey,CapturedAtUtc
      )
      VALUES
      (
        @TransactionId,@PublicSiteId,@Rfc,@SiteId,@Id,'PayPal',@MerchantProfileKey,
        @PayPalOrderId,@PayPalCaptureId,@Status,@GrossAmount,@FeeAmount,@NetAmount,
        @CurrencyCode,@IdempotencyKey,
        CASE WHEN @Status='Completed' THEN COALESCE(@CapturedAtUtc,SYSUTCDATETIME()) ELSE @CapturedAtUtc END
      );
    END
    ELSE
    BEGIN
      IF EXISTS
      (
        SELECT 1 FROM restaurante.PaymentGatewayTransaction
        WHERE Id=@TransactionId
          AND (ProviderOrderId<>@PayPalOrderId OR ProviderCaptureId<>@PayPalCaptureId
               OR GrossAmount<>@GrossAmount OR CurrencyCode<>@CurrencyCode
               OR IdempotencyKey<>@IdempotencyKey)
      )
      BEGIN
        ROLLBACK TRANSACTION;
        THROW 53648,'La captura repetida no coincide con la transaccion guardada.',1;
      END;

      UPDATE restaurante.PaymentGatewayTransaction
      SET [Status]=CASE
            WHEN [Status] IN('Refunded','PartiallyRefunded') THEN [Status]
            WHEN @Status='Completed' THEN 'Completed'
            WHEN [Status]='Completed' THEN [Status]
            ELSE @Status END,
          FeeAmount=COALESCE(@FeeAmount,FeeAmount),
          NetAmount=COALESCE(@NetAmount,NetAmount),
          CapturedAtUtc=CASE WHEN @Status='Completed'
            THEN COALESCE(CapturedAtUtc,@CapturedAtUtc,SYSUTCDATETIME()) ELSE CapturedAtUtc END,
          UpdatedAtUtc=SYSUTCDATETIME()
      WHERE Id=@TransactionId;
    END;
  END;

  UPDATE restaurante.OnlineCheckoutAttempt
  SET PayPalCaptureId=COALESCE(PayPalCaptureId,@PayPalCaptureId),
      [State]=CASE
        WHEN @Status='Completed' AND [State] NOT IN
          ('PosCreated','RefundRequested','RefundPending','Refunded') THEN 'Captured'
        WHEN @Status='Pending' AND [State] NOT IN
          ('Captured','CapturedNeedsOrder','PosCreated','RefundRequested','RefundPending','Refunded') THEN 'CapturePending'
        WHEN @Status='Denied' AND [State] NOT IN
          ('Captured','CapturedNeedsOrder','PosCreated','RefundRequested','RefundPending','Refunded') THEN 'PaymentDenied'
        ELSE [State] END,
      CapturedAtUtc=CASE WHEN @Status='Completed'
        THEN COALESCE(CapturedAtUtc,@CapturedAtUtc,SYSUTCDATETIME()) ELSE CapturedAtUtc END,
      FailureCode=CASE WHEN @Status='Denied' THEN 'PAYPAL_CAPTURE_DENIED' ELSE NULL END,
      FailureMessage=NULL,
      NextRetryAtUtc=CASE WHEN @Status='Pending' THEN DATEADD(SECOND,30,SYSUTCDATETIME()) ELSE NULL END,
      UpdatedAtUtc=SYSUTCDATETIME()
  WHERE Id=@Id AND PublicSiteId=@PublicSiteId AND Rfc=@Rfc;

  COMMIT TRANSACTION;

  SELECT
    attempt.Id,attempt.PublicSiteId,attempt.Rfc,attempt.SiteId,attempt.ClientAttemptId,
    attempt.MemberId,attempt.CustomerName,attempt.CustomerEmail,attempt.CustomerPhone,
    attempt.QuoteFingerprint,attempt.QuoteExpiresAtUtc,attempt.CartSnapshotJson,
    attempt.PromotionCode,attempt.Subtotal,attempt.PromotionDiscountTotal,attempt.TaxTotal,
    attempt.Total,attempt.CurrencyCode,attempt.PrivacyVersion,attempt.TermsVersion,
    attempt.LegalAcceptedAtUtc,attempt.MerchantProfileKey,attempt.PayPalCreateRequestId,
    attempt.PayPalOrderId,attempt.PayPalCaptureId,attempt.[State],attempt.RestaurantOrderId,
    attempt.ImportAttempts,attempt.NextRetryAtUtc,attempt.FailureCode,attempt.FailureMessage,
    attempt.CreatedAtUtc,attempt.UpdatedAtUtc,attempt.CapturedAtUtc,attempt.PosCreatedAtUtc,
    attempt.RowVersion
  FROM restaurante.OnlineCheckoutAttempt attempt
  WHERE attempt.Id=@Id AND attempt.PublicSiteId=@PublicSiteId AND attempt.Rfc=@Rfc;

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
END;
GO

CREATE OR ALTER PROCEDURE restaurante.OnlineCheckoutCaptureAuthorize
  @Id uniqueidentifier,
  @PayPalOrderId varchar(64)
AS
BEGIN
  SET NOCOUNT ON;
  SET XACT_ABORT ON;

  DECLARE @PublicSiteId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N'OrionERP.PublicSiteId'));
  DECLARE @Rfc varchar(50)=CONVERT(varchar(50),SESSION_CONTEXT(N'OrionRfc'));

  IF @PublicSiteId IS NULL OR NULLIF(@Rfc,'') IS NULL
    THROW 53646,'No existe un contexto publico de restaurante verificado.',1;

  BEGIN TRANSACTION;

  IF NOT EXISTS
  (
    SELECT 1
    FROM restaurante.OnlineCheckoutAttempt attempt WITH(UPDLOCK,HOLDLOCK)
    JOIN restaurante.OnlineOrderingSettings settings
      ON settings.PublicSiteId=attempt.PublicSiteId
     AND settings.Rfc=attempt.Rfc
     AND settings.SiteId=attempt.SiteId
    WHERE attempt.Id=@Id
      AND attempt.PublicSiteId=@PublicSiteId
      AND attempt.Rfc=@Rfc
      AND attempt.PayPalOrderId=@PayPalOrderId
      AND attempt.[State] IN('PayPalCreated','CapturePending')
      AND settings.IsEnabled=1
      AND settings.IsPaused=0
      AND settings.PickupEnabled=1
      AND settings.ProcessorHeartbeatAtUtc>=DATEADD(SECOND,-120,SYSUTCDATETIME())
  )
  BEGIN
    ROLLBACK TRANSACTION;
    THROW 53658,'La captura esta bloqueada por estado, configuracion o heartbeat.',1;
  END;

  UPDATE restaurante.OnlineCheckoutAttempt
  SET [State]='CapturePending',NextRetryAtUtc=DATEADD(SECOND,30,SYSUTCDATETIME()),
      FailureCode=NULL,FailureMessage=NULL,UpdatedAtUtc=SYSUTCDATETIME()
  WHERE Id=@Id AND PublicSiteId=@PublicSiteId AND Rfc=@Rfc;

  COMMIT TRANSACTION;

  SELECT attempt.Id,attempt.PayPalOrderId,attempt.MerchantProfileKey,
    attempt.QuoteFingerprint,attempt.Total,attempt.CurrencyCode,attempt.[State],
    attempt.UpdatedAtUtc,attempt.RowVersion
  FROM restaurante.OnlineCheckoutAttempt attempt
  WHERE attempt.Id=@Id AND attempt.PublicSiteId=@PublicSiteId AND attempt.Rfc=@Rfc;
END;
GO

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

  DECLARE @PublicSiteId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N'OrionERP.PublicSiteId'));
  DECLARE @Rfc varchar(50)=CONVERT(varchar(50),SESSION_CONTEXT(N'OrionRfc'));

  IF @PublicSiteId IS NULL OR NULLIF(@Rfc,'') IS NULL
    THROW 53649,'No existe un contexto publico de restaurante verificado.',1;
  IF @State NOT IN('RequoteRequired','CapturePending','PaymentDenied','Expired','Failed')
    THROW 53650,'El endpoint publico no puede asignar ese estado.',1;
  IF NULLIF(@ExpectedState,'') IS NULL
    THROW 53651,'ExpectedState es obligatorio.',1;
  IF @RestaurantOrderId IS NOT NULL
    THROW 53674,'El endpoint publico no puede ligar una orden POS.',1;

  UPDATE restaurante.OnlineCheckoutAttempt
  SET [State]=@State,
      FailureCode=NULLIF(LTRIM(RTRIM(@FailureCode)),''),
      FailureMessage=LEFT(NULLIF(LTRIM(RTRIM(@FailureMessage)),N''),500),
      NextRetryAtUtc=@NextRetryAtUtc,
      UpdatedAtUtc=SYSUTCDATETIME()
  WHERE Id=@Id AND PublicSiteId=@PublicSiteId AND Rfc=@Rfc
    AND [State]=@ExpectedState
    AND
    (
      ([State]='Quoted' AND @State IN('RequoteRequired','Expired','Failed'))
      OR ([State]='PayPalCreated' AND @State IN('RequoteRequired','CapturePending','PaymentDenied','Expired','Failed'))
      OR ([State]='CapturePending' AND @State IN('CapturePending','PaymentDenied','Failed'))
      OR ([State]='RequoteRequired' AND @State IN('RequoteRequired','Expired'))
    );

  IF @@ROWCOUNT=0
    THROW 53652,'El checkout cambio de estado o la transicion no es valida.',1;

  SELECT
    attempt.Id,attempt.PublicSiteId,attempt.Rfc,attempt.SiteId,attempt.ClientAttemptId,
    attempt.QuoteFingerprint,attempt.PayPalOrderId,attempt.PayPalCaptureId,
    attempt.[State],attempt.RestaurantOrderId,attempt.NextRetryAtUtc,
    attempt.FailureCode,attempt.FailureMessage,attempt.UpdatedAtUtc,attempt.RowVersion
  FROM restaurante.OnlineCheckoutAttempt attempt
  WHERE attempt.Id=@Id AND attempt.PublicSiteId=@PublicSiteId AND attempt.Rfc=@Rfc;
END;
GO

CREATE OR ALTER PROCEDURE restaurante.OnlineCheckoutStatusGet
  @TrackingTokenHash binary(32)
AS
BEGIN
  SET NOCOUNT ON;

  DECLARE @PublicSiteId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N'OrionERP.PublicSiteId'));
  DECLARE @Rfc varchar(50)=CONVERT(varchar(50),SESSION_CONTEXT(N'OrionRfc'));

  IF @PublicSiteId IS NULL OR NULLIF(@Rfc,'') IS NULL OR @TrackingTokenHash IS NULL
    THROW 53653,'El contexto o token de seguimiento es invalido.',1;

  SELECT
    attempt.[State] CheckoutStatus,
    CASE
      WHEN attempt.[State]='Refunded' THEN 'Refunded'
      WHEN attempt.[State] IN('Captured','CapturedNeedsOrder','PosCreated','RefundRequested','RefundPending') THEN 'Paid'
      WHEN attempt.[State]='CapturePending' THEN 'Pending'
      WHEN attempt.[State]='PaymentDenied' THEN 'Denied'
      ELSE 'NotPaid' END PaymentStatus,
    orderInfo.[Status] OrderStatus,
    orderInfo.Folio OrderFolio,
    attempt.Total,
    attempt.CurrencyCode Currency,
    CONVERT(bit,CASE
      WHEN attempt.[State] IN('Refunded','PaymentDenied','Expired','Failed')
        OR orderInfo.[Status] IN('Completed','Cancelled') THEN 1 ELSE 0 END) IsTerminal,
    CONVERT(nvarchar(240),CASE
      WHEN attempt.[State]='PosCreated' AND orderInfo.[Status]='Ready' THEN N'Tu pedido esta listo para recoger.'
      WHEN attempt.[State]='PosCreated' THEN N'Tu pedido esta confirmado. Te enviaremos un correo cuando este listo.'
      WHEN attempt.[State] IN('Captured','CapturedNeedsOrder') THEN N'Pago recibido. Estamos confirmando tu pedido.'
      WHEN attempt.[State] IN('RefundRequested','RefundPending') THEN N'Estamos procesando el reembolso de tu pago.'
      WHEN attempt.[State]='Refunded' THEN N'Tu pago fue reembolsado.'
      WHEN attempt.[State]='PaymentDenied' THEN N'PayPal no completo el cobro.'
      WHEN attempt.[State]='RequoteRequired' THEN N'El menu o el total cambio. Revisa nuevamente tu pedido.'
      WHEN attempt.[State]='CapturePending' THEN N'Estamos confirmando el pago con PayPal.'
      ELSE N'El pedido esta pendiente.' END) [Message],
    attempt.UpdatedAtUtc
  FROM restaurante.OnlineCheckoutAttempt attempt
  LEFT JOIN restaurante.[Order] orderInfo
    ON orderInfo.PublicSiteId=attempt.PublicSiteId
   AND orderInfo.Rfc=attempt.Rfc
   AND orderInfo.SiteId=attempt.SiteId
   AND orderInfo.Id=attempt.RestaurantOrderId
  WHERE attempt.PublicSiteId=@PublicSiteId
    AND attempt.Rfc=@Rfc
    AND attempt.TrackingTokenHash=@TrackingTokenHash;
END;
GO

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
  @VerificationStatus varchar(20)='Verified'
AS
BEGIN
  SET NOCOUNT ON;
  SET XACT_ABORT ON;

  DECLARE @PublicSiteId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N'OrionERP.PublicSiteId'));
  DECLARE @Rfc varchar(50)=CONVERT(varchar(50),SESSION_CONTEXT(N'OrionRfc'));
  DECLARE @SiteId int;
  DECLARE @CheckoutAttemptId uniqueidentifier;
  DECLARE @EventId bigint;
  DECLARE @WasInserted bit=0;

  IF @PublicSiteId IS NULL OR NULLIF(@Rfc,'') IS NULL
    THROW 53654,'No existe un contexto publico de restaurante verificado.',1;
  IF @VerificationStatus<>'Verified'
    THROW 53655,'Solo se persisten webhooks verificados.',1;
  IF NULLIF(LTRIM(RTRIM(@ProviderEventId)),'') IS NULL
     OR NULLIF(LTRIM(RTRIM(@EventType)),'') IS NULL
     OR NULLIF(LTRIM(RTRIM(@MerchantProfileKey)),'') IS NULL
     OR LEN(@PayloadHash)<>64
     OR @PayloadHash LIKE '%[^0-9A-F]%' COLLATE Latin1_General_100_BIN2
    THROW 53656,'Los metadatos del webhook son invalidos.',1;

  SELECT TOP(1)
    @CheckoutAttemptId=attempt.Id,
    @SiteId=attempt.SiteId
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
      OR (@RelatedCaptureId IS NOT NULL AND attempt.PayPalCaptureId=@RelatedCaptureId)
      OR (@RelatedRefundId IS NOT NULL AND refundInfo.ProviderRefundId=@RelatedRefundId)
      OR (@ResourceId IS NOT NULL AND
          (attempt.PayPalOrderId=@ResourceId OR attempt.PayPalCaptureId=@ResourceId
           OR refundInfo.ProviderRefundId=@ResourceId))
    );

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
      EventType,ResourceType,ResourceId,CheckoutAttemptId,PayloadHash,VerificationStatus
    )
    VALUES
    (
      @PublicSiteId,@Rfc,@SiteId,'PayPal',@MerchantProfileKey,@ProviderEventId,
      @EventType,@ResourceType,@ResourceId,@CheckoutAttemptId,@PayloadHash,@VerificationStatus
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
    THROW 53657,'El event ID de PayPal se repitio con otro payload.',1;
  END;

  COMMIT TRANSACTION;

  SELECT CONVERT(bit,1) WasMatched,@WasInserted WasInserted,
    eventInfo.Id EventId,@CheckoutAttemptId CheckoutAttemptId,eventInfo.ProcessingStatus
  FROM restaurante.PaymentGatewayEvent eventInfo
  WHERE eventInfo.Id=@EventId;
END;
GO

CREATE OR ALTER PROCEDURE restaurante.OnlineOrderingProcessorHeartbeatSet
AS
BEGIN
  SET NOCOUNT ON;

  DECLARE @PublicSiteId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N'OrionERP.PublicSiteId'));
  DECLARE @Rfc varchar(50)=CONVERT(varchar(50),SESSION_CONTEXT(N'OrionRfc'));
  DECLARE @CompanyId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N'OrionERP.CompanyId'));
  DECLARE @OrionSiteId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N'OrionERP.SiteId'));

  UPDATE settings
  SET ProcessorHeartbeatAtUtc=SYSUTCDATETIME(),UpdatedAtUtc=SYSUTCDATETIME()
  FROM restaurante.OnlineOrderingSettings settings
  WHERE settings.Rfc=@Rfc
    AND
    (
      (@PublicSiteId IS NOT NULL AND settings.PublicSiteId=@PublicSiteId)
      OR (@PublicSiteId IS NULL AND settings.CompanyId=@CompanyId
          AND (@OrionSiteId IS NULL OR settings.OrionSiteId=@OrionSiteId))
    );

  IF @@ROWCOUNT<>1
    THROW 53659,'El heartbeat no resolvio exactamente un sitio de pedidos.',1;

  SELECT PublicSiteId,ProcessorHeartbeatAtUtc
  FROM restaurante.OnlineOrderingSettings
  WHERE Rfc=@Rfc
    AND
    (
      (@PublicSiteId IS NOT NULL AND PublicSiteId=@PublicSiteId)
      OR (@PublicSiteId IS NULL AND CompanyId=@CompanyId AND OrionSiteId=@OrionSiteId)
    );
END;
GO

CREATE OR ALTER PROCEDURE restaurante.OnlineOrderingRuntimeReadinessSet
  @GatewayEnvironment varchar(20),
  @GatewayCredentialsConfigured bit,
  @GatewayWebhookConfigured bit,
  @MerchantProfileKey varchar(80)
AS
BEGIN
  SET NOCOUNT ON;

  DECLARE @PublicSiteId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N'OrionERP.PublicSiteId'));
  DECLARE @Rfc varchar(50)=CONVERT(varchar(50),SESSION_CONTEXT(N'OrionRfc'));
  DECLARE @RequiredEnvironment varchar(20)=CASE WHEN DB_NAME()='grupocarpio' THEN 'Live' ELSE 'Sandbox' END;

  IF @PublicSiteId IS NULL OR NULLIF(@Rfc,'') IS NULL
    THROW 53671,'No existe un contexto publico de restaurante verificado.',1;
  IF @GatewayEnvironment NOT IN('Sandbox','Live')
     OR NULLIF(LTRIM(RTRIM(@MerchantProfileKey)),'') IS NULL
    THROW 53672,'La declaracion de runtime PayPal es invalida.',1;

  UPDATE restaurante.OnlineOrderingSettings
  SET GatewayEnvironment=@GatewayEnvironment,
      GatewayCredentialsConfigured=@GatewayCredentialsConfigured,
      GatewayWebhookConfigured=@GatewayWebhookConfigured,
      ActiveMerchantProfileKey=@MerchantProfileKey,
      GatewayReadinessAtUtc=CASE
        WHEN @GatewayEnvironment=@RequiredEnvironment
         AND @GatewayCredentialsConfigured=1 AND @GatewayWebhookConfigured=1
        THEN SYSUTCDATETIME() ELSE NULL END,
      UpdatedAtUtc=SYSUTCDATETIME()
  WHERE PublicSiteId=@PublicSiteId AND Rfc=@Rfc;

  IF @@ROWCOUNT<>1
    THROW 53673,'La declaracion de runtime no resolvio el sitio publico.',1;

  SELECT PublicSiteId,GatewayEnvironment,GatewayCredentialsConfigured,
    GatewayWebhookConfigured,GatewayReadinessAtUtc,ActiveMerchantProfileKey,
    CONVERT(bit,CASE
      WHEN GatewayEnvironment=@RequiredEnvironment
       AND GatewayCredentialsConfigured=1 AND GatewayWebhookConfigured=1
       AND GatewayReadinessAtUtc>=DATEADD(MINUTE,-5,SYSUTCDATETIME())
      THEN 1 ELSE 0 END) IsGatewayReady
  FROM restaurante.OnlineOrderingSettings
  WHERE PublicSiteId=@PublicSiteId AND Rfc=@Rfc;
END;
GO

CREATE OR ALTER PROCEDURE restaurante.OnlineOrderImportClaim
  @LeaseId uniqueidentifier,
  @LeaseSeconds int=90
AS
BEGIN
  SET NOCOUNT ON;
  SET XACT_ABORT ON;

  IF @LeaseId IS NULL OR @LeaseSeconds NOT BETWEEN 15 AND 600
    THROW 53660,'La concesion del importador es invalida.',1;

  DECLARE @PublicSiteId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N'OrionERP.PublicSiteId'));
  DECLARE @Rfc varchar(50)=CONVERT(varchar(50),SESSION_CONTEXT(N'OrionRfc'));
  DECLARE @CompanyId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N'OrionERP.CompanyId'));
  DECLARE @OrionSiteId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N'OrionERP.SiteId'));
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
    THROW 53661,'El importador no pudo resolver el sitio de pedidos.',1;

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
      AND attempt.[State] IN('Captured','CapturedNeedsOrder')
      AND transactionInfo.[Status] IN('Completed','PartiallyRefunded')
      AND (attempt.NextRetryAtUtc IS NULL OR attempt.NextRetryAtUtc<=SYSUTCDATETIME())
      AND (attempt.ImportLeaseExpiresAtUtc IS NULL OR attempt.ImportLeaseExpiresAtUtc<SYSUTCDATETIME())
    ORDER BY COALESCE(attempt.CapturedAtUtc,attempt.CreatedAtUtc),attempt.Id
  )
  UPDATE nextAttempt
  SET [State]='CapturedNeedsOrder',
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
    attempt.CurrencyCode,attempt.MerchantProfileKey,attempt.PayPalOrderId,
    attempt.PayPalCaptureId,attempt.[State],attempt.ImportAttempts,attempt.ImportLeaseId,
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
END;
GO

CREATE OR ALTER PROCEDURE restaurante.OnlineOrderImportComplete
  @Id uniqueidentifier,
  @LeaseId uniqueidentifier,
  @RestaurantOrderId uniqueidentifier,
  @LocalPaymentId uniqueidentifier
AS
BEGIN
  SET NOCOUNT ON;
  SET XACT_ABORT ON;

  DECLARE @PublicSiteId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N'OrionERP.PublicSiteId'));
  DECLARE @Rfc varchar(50)=CONVERT(varchar(50),SESSION_CONTEXT(N'OrionRfc'));
  DECLARE @CompanyId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N'OrionERP.CompanyId'));
  DECLARE @OrionSiteId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N'OrionERP.SiteId'));
  DECLARE @ResolvedPublicSiteId bigint;
  DECLARE @SiteId int;
  DECLARE @CustomerEmail nvarchar(320);

  SELECT @ResolvedPublicSiteId=settings.PublicSiteId,@SiteId=settings.SiteId
  FROM restaurante.[Order] scopedOrder
  JOIN restaurante.OnlineOrderingSettings settings
    ON settings.PublicSiteId=scopedOrder.PublicSiteId
   AND settings.Rfc=scopedOrder.Rfc
   AND settings.SiteId=scopedOrder.SiteId
  WHERE scopedOrder.Rfc=@Rfc AND scopedOrder.Id=@RestaurantOrderId
    AND
    (
      (@PublicSiteId IS NOT NULL AND settings.PublicSiteId=@PublicSiteId)
      OR (@PublicSiteId IS NULL AND settings.CompanyId=@CompanyId
          AND (@OrionSiteId IS NULL OR settings.OrionSiteId=@OrionSiteId))
    );

  IF @ResolvedPublicSiteId IS NULL
    THROW 53662,'No se resolvio el sitio del importador.',1;

  BEGIN TRANSACTION;

  SELECT @CustomerEmail=attempt.CustomerEmail
  FROM restaurante.OnlineCheckoutAttempt attempt WITH(UPDLOCK,HOLDLOCK)
  WHERE attempt.Id=@Id AND attempt.PublicSiteId=@ResolvedPublicSiteId AND attempt.Rfc=@Rfc
    AND
    (
      (attempt.[State]='CapturedNeedsOrder' AND attempt.ImportLeaseId=@LeaseId
       AND attempt.ImportLeaseExpiresAtUtc>=SYSUTCDATETIME())
      OR (attempt.[State]='PosCreated' AND attempt.RestaurantOrderId=@RestaurantOrderId)
    );

  IF @CustomerEmail IS NULL
  BEGIN
    ROLLBACK TRANSACTION;
    THROW 53663,'El checkout no tiene una concesion valida o ya cambio.',1;
  END;

  IF NOT EXISTS
  (
    SELECT 1 FROM restaurante.[Order] orderInfo
    WHERE orderInfo.Id=@RestaurantOrderId
      AND orderInfo.PublicSiteId=@ResolvedPublicSiteId
      AND orderInfo.Rfc=@Rfc
      AND orderInfo.SiteId=@SiteId
      AND orderInfo.OnlineCheckoutAttemptId=@Id
      AND orderInfo.SalesChannel='Web'
      AND orderInfo.OrderType='Pickup'
  )
  BEGIN
    ROLLBACK TRANSACTION;
    THROW 53664,'La orden POS no coincide con el checkout Web.',1;
  END;

  IF NOT EXISTS
  (
    SELECT 1
    FROM restaurante.Payment paymentInfo
    JOIN restaurante.PaymentGatewayTransaction transactionInfo
      ON transactionInfo.PublicSiteId=@ResolvedPublicSiteId
     AND transactionInfo.Rfc=paymentInfo.Rfc
     AND transactionInfo.CheckoutAttemptId=@Id
     AND transactionInfo.ProviderCaptureId=paymentInfo.ExternalReference
    WHERE paymentInfo.Rfc=@Rfc AND paymentInfo.Id=@LocalPaymentId
      AND paymentInfo.OrderId=@RestaurantOrderId
      AND paymentInfo.PaymentMethod='Platform'
      AND paymentInfo.[Status] IN('Paid','PartiallyRefunded','Refunded')
  )
  BEGIN
    ROLLBACK TRANSACTION;
    THROW 53665,'El pago POS no coincide con la captura PayPal.',1;
  END;

  UPDATE restaurante.OnlineCheckoutAttempt
  SET [State]='PosCreated',RestaurantOrderId=@RestaurantOrderId,
      PosCreatedAtUtc=COALESCE(PosCreatedAtUtc,SYSUTCDATETIME()),
      ImportLeaseId=NULL,ImportLeaseExpiresAtUtc=NULL,NextRetryAtUtc=NULL,
      FailureCode=NULL,FailureMessage=NULL,UpdatedAtUtc=SYSUTCDATETIME()
  WHERE Id=@Id AND PublicSiteId=@ResolvedPublicSiteId AND Rfc=@Rfc;

  UPDATE restaurante.PaymentGatewayTransaction
  SET LocalPaymentId=COALESCE(LocalPaymentId,@LocalPaymentId),UpdatedAtUtc=SYSUTCDATETIME()
  WHERE PublicSiteId=@ResolvedPublicSiteId AND Rfc=@Rfc AND CheckoutAttemptId=@Id
    AND (LocalPaymentId IS NULL OR LocalPaymentId=@LocalPaymentId);
  IF @@ROWCOUNT<>1
  BEGIN
    ROLLBACK TRANSACTION;
    THROW 53666,'No se pudo ligar el pago local a PayPal.',1;
  END;

  IF NOT EXISTS
  (
    SELECT 1 FROM restaurante.OnlineOrderNotification WITH(UPDLOCK,HOLDLOCK)
    WHERE PublicSiteId=@ResolvedPublicSiteId
      AND CheckoutAttemptId=@Id AND NotificationType='Confirmation'
  )
    INSERT restaurante.OnlineOrderNotification
    (
      PublicSiteId,Rfc,SiteId,CheckoutAttemptId,RestaurantOrderId,
      NotificationType,RecipientEmail,IdempotencyKey
    )
    VALUES
    (
      @ResolvedPublicSiteId,@Rfc,@SiteId,@Id,@RestaurantOrderId,
      'Confirmation',@CustomerEmail,CONCAT('email-confirmation-',CONVERT(varchar(36),@Id))
    );

  COMMIT TRANSACTION;

  SELECT attempt.Id CheckoutAttemptId,attempt.[State],attempt.RestaurantOrderId,
    attempt.PosCreatedAtUtc,attempt.UpdatedAtUtc
  FROM restaurante.OnlineCheckoutAttempt attempt
  WHERE attempt.Id=@Id AND attempt.PublicSiteId=@ResolvedPublicSiteId AND attempt.Rfc=@Rfc;
END;
GO

CREATE OR ALTER PROCEDURE restaurante.OnlineOrderImportFail
  @Id uniqueidentifier,
  @LeaseId uniqueidentifier,
  @IsDeterministic bit,
  @FailureCode varchar(80),
  @FailureMessage nvarchar(500),
  @NextRetryAtUtc datetime2(3)=NULL
AS
BEGIN
  SET NOCOUNT ON;
  SET XACT_ABORT ON;

  DECLARE @PublicSiteId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N'OrionERP.PublicSiteId'));
  DECLARE @Rfc varchar(50)=CONVERT(varchar(50),SESSION_CONTEXT(N'OrionRfc'));
  DECLARE @CompanyId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N'OrionERP.CompanyId'));
  DECLARE @OrionSiteId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N'OrionERP.SiteId'));
  DECLARE @ResolvedPublicSiteId bigint;
  DECLARE @SiteId int;
  DECLARE @TransactionId uniqueidentifier;
  DECLARE @Amount decimal(18,2);
  DECLARE @CurrencyCode char(3);

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
    THROW 53667,'No se resolvio el sitio del importador.',1;
  IF NULLIF(LTRIM(RTRIM(@FailureCode)),'') IS NULL
    THROW 53668,'El codigo de falla es obligatorio.',1;

  BEGIN TRANSACTION;

  IF NOT EXISTS
  (
    SELECT 1 FROM restaurante.OnlineCheckoutAttempt WITH(UPDLOCK,HOLDLOCK)
    WHERE Id=@Id AND PublicSiteId=@ResolvedPublicSiteId AND Rfc=@Rfc
      AND [State]='CapturedNeedsOrder' AND ImportLeaseId=@LeaseId
  )
  BEGIN
    ROLLBACK TRANSACTION;
    THROW 53669,'La concesion del checkout ya no es valida.',1;
  END;

  IF @IsDeterministic=1
  BEGIN
    SELECT @TransactionId=transactionInfo.Id,@Amount=transactionInfo.GrossAmount,
      @CurrencyCode=transactionInfo.CurrencyCode
    FROM restaurante.PaymentGatewayTransaction transactionInfo WITH(UPDLOCK,HOLDLOCK)
    WHERE transactionInfo.PublicSiteId=@ResolvedPublicSiteId
      AND transactionInfo.Rfc=@Rfc
      AND transactionInfo.CheckoutAttemptId=@Id
      AND transactionInfo.[Status] IN('Completed','PartiallyRefunded');

    IF @TransactionId IS NULL
    BEGIN
      ROLLBACK TRANSACTION;
      THROW 53670,'No existe una captura completada para reembolsar.',1;
    END;

    IF NOT EXISTS
    (
      SELECT 1 FROM restaurante.PaymentGatewayRefund WITH(UPDLOCK,HOLDLOCK)
      WHERE PublicSiteId=@ResolvedPublicSiteId
        AND IdempotencyKey=CONCAT('refund-auto-',CONVERT(varchar(36),@Id))
    )
      INSERT restaurante.PaymentGatewayRefund
      (
        Id,PublicSiteId,Rfc,SiteId,GatewayTransactionId,Amount,CurrencyCode,
        Reason,[Status],IdempotencyKey,RequestedBy,AuthorizedBy
      )
      VALUES
      (
        NEWID(),@ResolvedPublicSiteId,@Rfc,@SiteId,@TransactionId,@Amount,@CurrencyCode,
        LEFT(COALESCE(NULLIF(LTRIM(RTRIM(@FailureMessage)),N''),N'Bruno no pudo surtir el pedido.'),500),
        'Requested',CONCAT('refund-auto-',CONVERT(varchar(36),@Id)),
        N'system:online-order-import',N'system:automatic-fulfillment-refund'
      );

    UPDATE restaurante.OnlineCheckoutAttempt
    SET [State]='RefundRequested',ImportLeaseId=NULL,ImportLeaseExpiresAtUtc=NULL,
        NextRetryAtUtc=NULL,FailureCode=@FailureCode,
        FailureMessage=LEFT(NULLIF(LTRIM(RTRIM(@FailureMessage)),N''),500),
        UpdatedAtUtc=SYSUTCDATETIME()
    WHERE Id=@Id AND PublicSiteId=@ResolvedPublicSiteId AND Rfc=@Rfc;
  END
  ELSE
  BEGIN
    UPDATE restaurante.OnlineCheckoutAttempt
    SET [State]='CapturedNeedsOrder',ImportLeaseId=NULL,ImportLeaseExpiresAtUtc=NULL,
        NextRetryAtUtc=COALESCE(@NextRetryAtUtc,DATEADD(MINUTE,1,SYSUTCDATETIME())),
        FailureCode=@FailureCode,
        FailureMessage=LEFT(NULLIF(LTRIM(RTRIM(@FailureMessage)),N''),500),
        UpdatedAtUtc=SYSUTCDATETIME()
    WHERE Id=@Id AND PublicSiteId=@ResolvedPublicSiteId AND Rfc=@Rfc;
  END;

  COMMIT TRANSACTION;

  SELECT Id CheckoutAttemptId,[State],ImportAttempts,NextRetryAtUtc,
    FailureCode,FailureMessage,UpdatedAtUtc
  FROM restaurante.OnlineCheckoutAttempt
  WHERE Id=@Id AND PublicSiteId=@ResolvedPublicSiteId AND Rfc=@Rfc;
END;
GO

CREATE OR ALTER PROCEDURE restaurante.OnlineOrderingAdminGet
AS
BEGIN
  SET NOCOUNT ON;

  DECLARE @PublicSiteId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N'OrionERP.PublicSiteId'));
  DECLARE @Rfc varchar(50)=CONVERT(varchar(50),SESSION_CONTEXT(N'OrionRfc'));
  DECLARE @CompanyId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N'OrionERP.CompanyId'));
  DECLARE @OrionSiteId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N'OrionERP.SiteId'));
  DECLARE @ResolvedPublicSiteId bigint;
  DECLARE @RequiredEnvironment varchar(20)=CASE WHEN DB_NAME()='grupocarpio' THEN 'Live' ELSE 'Sandbox' END;

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
    THROW 53675,'No se resolvio el sitio para administrar pedidos online.',1;

  SELECT
    settings.PublicSiteId,publicSite.PublicSiteKey,settings.Rfc,settings.SiteId,
    settings.IsEnabled,settings.IsPaused,settings.PauseMessage,
    settings.GuestCheckoutEnabled,settings.PickupEnabled,
    settings.MinimumOrderTotal,settings.MaximumOrderTotal,
    settings.WeeklyScheduleJson,settings.TermsVersion,settings.PrivacyVersion,
    settings.ActiveMerchantProfileKey,settings.GatewayEnvironment,
    settings.GatewayCredentialsConfigured,settings.GatewayWebhookConfigured,
    settings.GatewayReadinessAtUtc,settings.ProcessorHeartbeatAtUtc,
    @RequiredEnvironment RequiredGatewayEnvironment,
    CONVERT(bit,CASE
      WHEN settings.GatewayEnvironment=@RequiredEnvironment
       AND settings.GatewayCredentialsConfigured=1
       AND settings.GatewayWebhookConfigured=1
       AND settings.GatewayReadinessAtUtc>=DATEADD(MINUTE,-5,SYSUTCDATETIME())
       AND settings.ProcessorHeartbeatAtUtc>=DATEADD(SECOND,-120,SYSUTCDATETIME())
       AND EXISTS
         (SELECT 1 FROM restaurante.OnlineOrderProduct productFlag
          WHERE productFlag.PublicSiteId=settings.PublicSiteId AND productFlag.IsEnabled=1)
      THEN 1 ELSE 0 END) IsReady,
    settings.ConfigurationVersion,settings.UpdatedAtUtc,settings.UpdatedBy,settings.RowVersion
  FROM restaurante.OnlineOrderingSettings settings
  JOIN orion.PublicSite publicSite ON publicSite.PublicSiteId=settings.PublicSiteId
  WHERE settings.PublicSiteId=@ResolvedPublicSiteId AND settings.Rfc=@Rfc;

  SELECT product.Id ProductId,
    CASE WHEN NULLIF(LTRIM(RTRIM(product.VariantName)),'') IS NULL
      THEN card.[Name] ELSE CONCAT(card.[Name],N' · ',product.VariantName) END ProductName,
    product.Sku,product.Price,product.IsActive,product.SoldOutOverride IsSoldOut,
    productFlag.IsEnabled IsOnlineEnabled
  FROM restaurante.OnlineOrderProduct productFlag
  JOIN restaurante.Product product
    ON product.Rfc=productFlag.Rfc AND product.Id=productFlag.ProductId
  JOIN restaurante.ProductCard card
    ON card.Rfc=product.Rfc AND card.Id=product.ProductCardId
  WHERE productFlag.PublicSiteId=@ResolvedPublicSiteId AND productFlag.Rfc=@Rfc
  ORDER BY card.[Name],product.VariantName,product.Id;
END;
GO

CREATE OR ALTER PROCEDURE restaurante.OnlineOrderingAdminSave
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
  @ExpectedRowVersion binary(8)=NULL,
  @UpdatedBy nvarchar(256)=NULL
AS
BEGIN
  SET NOCOUNT ON;
  SET XACT_ABORT ON;

  DECLARE @PublicSiteId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N'OrionERP.PublicSiteId'));
  DECLARE @Rfc varchar(50)=CONVERT(varchar(50),SESSION_CONTEXT(N'OrionRfc'));
  DECLARE @CompanyId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N'OrionERP.CompanyId'));
  DECLARE @OrionSiteId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N'OrionERP.SiteId'));
  DECLARE @ResolvedPublicSiteId bigint;
  DECLARE @SiteId int;
  DECLARE @RequiredEnvironment varchar(20)=CASE WHEN DB_NAME()='grupocarpio' THEN 'Live' ELSE 'Sandbox' END;
  DECLARE @Now datetime2(3)=SYSUTCDATETIME();
  DECLARE @EnabledProducts TABLE(ProductId bigint NOT NULL PRIMARY KEY);

  IF ISJSON(@WeeklyScheduleJson)<>1 OR LEFT(LTRIM(@WeeklyScheduleJson),1)<>'{'
    THROW 53676,'El horario online no es un objeto JSON valido.',1;
  IF ISJSON(@EnabledProductIdsJson)<>1 OR LEFT(LTRIM(@EnabledProductIdsJson),1)<>'['
    THROW 53677,'La seleccion de productos no es un arreglo JSON valido.',1;
  IF @MaximumOrderTotal<=0
    THROW 53678,'El maximo por pedido debe ser mayor que cero.',1;
  IF NULLIF(LTRIM(RTRIM(@TermsVersion)),'') IS NULL
     OR NULLIF(LTRIM(RTRIM(@PrivacyVersion)),'') IS NULL
    THROW 53679,'Las versiones legales son obligatorias.',1;
  IF @IsPaused=1 AND NULLIF(LTRIM(RTRIM(@PauseMessage)),N'') IS NULL
    THROW 53680,'El mensaje de pausa es obligatorio.',1;

  IF EXISTS
  (
    SELECT 1
    FROM OPENJSON(@WeeklyScheduleJson) dayInfo
    CROSS APPLY OPENJSON(dayInfo.[value]) slotInfo
    WHERE dayInfo.[type]<>4
       OR slotInfo.[type]<>5
       OR TRY_CONVERT(time(0),JSON_VALUE(slotInfo.[value],'$.opens')) IS NULL
       OR TRY_CONVERT(time(0),JSON_VALUE(slotInfo.[value],'$.closes')) IS NULL
  )
    THROW 53681,'Un intervalo del horario online es invalido.',1;

  INSERT @EnabledProducts(ProductId)
  SELECT TRY_CONVERT(bigint,[value])
  FROM OPENJSON(@EnabledProductIdsJson)
  WHERE TRY_CONVERT(bigint,[value]) IS NOT NULL AND TRY_CONVERT(bigint,[value])>0
  GROUP BY TRY_CONVERT(bigint,[value]);

  IF (SELECT COUNT_BIG(*) FROM OPENJSON(@EnabledProductIdsJson))<>
     (SELECT COUNT_BIG(*) FROM @EnabledProducts)
    THROW 53682,'La seleccion de productos contiene IDs invalidos o repetidos.',1;

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
    THROW 53683,'No se resolvio el sitio para administrar pedidos online.',1;

  IF EXISTS
  (
    SELECT 1 FROM @EnabledProducts enabled
    LEFT JOIN restaurante.Product product
      ON product.Rfc=@Rfc AND product.Id=enabled.ProductId AND product.IsActive=1
    WHERE product.Id IS NULL
  )
    THROW 53684,'Solo se pueden habilitar productos activos del RFC.',1;

  BEGIN TRANSACTION;

  IF @ExpectedRowVersion IS NOT NULL AND NOT EXISTS
  (
    SELECT 1 FROM restaurante.OnlineOrderingSettings WITH(UPDLOCK,HOLDLOCK)
    WHERE PublicSiteId=@ResolvedPublicSiteId AND Rfc=@Rfc AND RowVersion=@ExpectedRowVersion
  )
  BEGIN
    ROLLBACK TRANSACTION;
    THROW 53685,'La configuracion cambio; recargue antes de guardar.',1;
  END;

  INSERT restaurante.OnlineOrderProduct(PublicSiteId,Rfc,SiteId,ProductId,IsEnabled,UpdatedBy)
  SELECT @ResolvedPublicSiteId,@Rfc,@SiteId,product.Id,0,
    COALESCE(NULLIF(LTRIM(RTRIM(@UpdatedBy)),N''),CONVERT(nvarchar(256),ORIGINAL_LOGIN()))
  FROM restaurante.Product product
  WHERE product.Rfc=@Rfc
    AND NOT EXISTS
    (
      SELECT 1 FROM restaurante.OnlineOrderProduct existing
      WHERE existing.PublicSiteId=@ResolvedPublicSiteId AND existing.ProductId=product.Id
    );

  UPDATE productFlag
  SET IsEnabled=CONVERT(bit,CASE WHEN enabled.ProductId IS NULL THEN 0 ELSE 1 END),
      UpdatedAtUtc=@Now,
      UpdatedBy=COALESCE(NULLIF(LTRIM(RTRIM(@UpdatedBy)),N''),CONVERT(nvarchar(256),ORIGINAL_LOGIN()))
  FROM restaurante.OnlineOrderProduct productFlag
  LEFT JOIN @EnabledProducts enabled ON enabled.ProductId=productFlag.ProductId
  WHERE productFlag.PublicSiteId=@ResolvedPublicSiteId AND productFlag.Rfc=@Rfc;

  IF @IsEnabled=1 AND
  (
    @PickupEnabled=0
    OR NOT EXISTS(SELECT 1 FROM @EnabledProducts)
    OR NOT EXISTS
      (SELECT 1 FROM OPENJSON(@WeeklyScheduleJson) dayInfo
       CROSS APPLY OPENJSON(dayInfo.[value]) slotInfo WHERE dayInfo.[type]=4 AND slotInfo.[type]=5)
    OR EXISTS
      (SELECT 1 FROM restaurante.OnlineOrderingSettings settings
       WHERE settings.PublicSiteId=@ResolvedPublicSiteId AND settings.Rfc=@Rfc
         AND
         (
           settings.GatewayEnvironment<>@RequiredEnvironment
           OR settings.GatewayCredentialsConfigured=0
           OR settings.GatewayWebhookConfigured=0
           OR settings.GatewayReadinessAtUtc<DATEADD(MINUTE,-5,@Now)
           OR settings.ProcessorHeartbeatAtUtc<DATEADD(SECOND,-120,@Now)
           OR NULLIF(LTRIM(RTRIM(settings.ActiveMerchantProfileKey)),'') IS NULL
         ))
  )
  BEGIN
    ROLLBACK TRANSACTION;
    THROW 53686,'No se puede habilitar: falta horario, productos, PayPal o heartbeat.',1;
  END;

  UPDATE restaurante.OnlineOrderingSettings
  SET IsEnabled=@IsEnabled,IsPaused=@IsPaused,
      PauseMessage=CASE WHEN @IsPaused=1 THEN LTRIM(RTRIM(@PauseMessage)) ELSE NULL END,
      WeeklyScheduleJson=@WeeklyScheduleJson,
      MinimumOrderTotal=0,MaximumOrderTotal=@MaximumOrderTotal,
      GuestCheckoutEnabled=@GuestCheckoutEnabled,PickupEnabled=@PickupEnabled,
      TermsVersion=LTRIM(RTRIM(@TermsVersion)),PrivacyVersion=LTRIM(RTRIM(@PrivacyVersion)),
      ConfigurationVersion=ConfigurationVersion+1,UpdatedAtUtc=@Now,
      UpdatedBy=COALESCE(NULLIF(LTRIM(RTRIM(@UpdatedBy)),N''),CONVERT(nvarchar(256),ORIGINAL_LOGIN()))
  WHERE PublicSiteId=@ResolvedPublicSiteId AND Rfc=@Rfc;

  COMMIT TRANSACTION;

  SELECT PublicSiteId,IsEnabled,IsPaused,MaximumOrderTotal,WeeklyScheduleJson,
    TermsVersion,PrivacyVersion,ConfigurationVersion,UpdatedAtUtc,RowVersion
  FROM restaurante.OnlineOrderingSettings
  WHERE PublicSiteId=@ResolvedPublicSiteId AND Rfc=@Rfc;
END;
GO

CREATE OR ALTER PROCEDURE restaurante.PayPalRecoveryClaim
  @LeaseId uniqueidentifier,
  @LeaseSeconds int=90
AS
BEGIN
  SET NOCOUNT ON;
  SET XACT_ABORT ON;

  DECLARE @PublicSiteId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N'OrionERP.PublicSiteId'));
  DECLARE @Rfc varchar(50)=CONVERT(varchar(50),SESSION_CONTEXT(N'OrionRfc'));
  DECLARE @ClaimedAttemptId uniqueidentifier;
  DECLARE @ClaimedEventId bigint;
  DECLARE @EventClaim TABLE(EventId bigint NOT NULL,CheckoutAttemptId uniqueidentifier NOT NULL);
  DECLARE @AttemptClaim TABLE(CheckoutAttemptId uniqueidentifier NOT NULL);

  IF @PublicSiteId IS NULL OR NULLIF(@Rfc,'') IS NULL
    THROW 53687,'No existe un contexto publico de restaurante verificado.',1;
  IF @LeaseId IS NULL OR @LeaseSeconds NOT BETWEEN 15 AND 600
    THROW 53688,'La concesion de recuperacion es invalida.',1;

  BEGIN TRANSACTION;

  ;WITH nextEvent AS
  (
    SELECT TOP(1) eventInfo.*
    FROM restaurante.PaymentGatewayEvent eventInfo WITH(UPDLOCK,READPAST,ROWLOCK)
    WHERE eventInfo.PublicSiteId=@PublicSiteId AND eventInfo.Rfc=@Rfc
      AND eventInfo.VerificationStatus='Verified'
      AND eventInfo.ProcessingStatus IN('Pending','Failed')
      AND (eventInfo.NextRetryAtUtc IS NULL OR eventInfo.NextRetryAtUtc<=SYSUTCDATETIME())
      AND (eventInfo.LeaseExpiresAtUtc IS NULL OR eventInfo.LeaseExpiresAtUtc<SYSUTCDATETIME())
    ORDER BY eventInfo.ReceivedAtUtc,eventInfo.Id
  )
  UPDATE nextEvent
  SET ProcessingStatus='Processing',Attempts=Attempts+1,
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
        AND attempt.[State]='CapturePending'
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
    CONVERT(varchar(20),CASE WHEN @ClaimedEventId IS NULL THEN 'Capture' ELSE 'Event' END) WorkType,
    attempt.Id CheckoutAttemptId,eventInfo.Id EventId,eventInfo.EventType,
    eventInfo.ResourceType,eventInfo.ResourceId,attempt.PayPalOrderId,attempt.PayPalCaptureId,
    attempt.MerchantProfileKey,attempt.QuoteFingerprint,attempt.Total,attempt.CurrencyCode,
    attempt.[State],attempt.RecoveryAttempts,@LeaseId LeaseId,
    COALESCE(eventInfo.LeaseExpiresAtUtc,attempt.RecoveryLeaseExpiresAtUtc) LeaseExpiresAtUtc
  FROM restaurante.OnlineCheckoutAttempt attempt
  LEFT JOIN restaurante.PaymentGatewayEvent eventInfo
    ON eventInfo.Id=@ClaimedEventId AND eventInfo.CheckoutAttemptId=attempt.Id
  WHERE attempt.PublicSiteId=@PublicSiteId AND attempt.Rfc=@Rfc
    AND attempt.Id=@ClaimedAttemptId;
END;
GO

CREATE OR ALTER PROCEDURE restaurante.PayPalRecoveryResult
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

  DECLARE @PublicSiteId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N'OrionERP.PublicSiteId'));
  DECLARE @Rfc varchar(50)=CONVERT(varchar(50),SESSION_CONTEXT(N'OrionRfc'));

  IF @PublicSiteId IS NULL OR NULLIF(@Rfc,'') IS NULL
    THROW 53689,'No existe un contexto publico de restaurante verificado.',1;
  IF @WorkType NOT IN('Capture','Event') OR @Outcome NOT IN('Processed','Pending','Ignored','Failed')
    THROW 53690,'El resultado de recuperacion es invalido.',1;
  IF @WorkType='Capture' AND @EventId IS NOT NULL
    THROW 53691,'Una recuperacion de captura no acepta EventId.',1;
  IF @WorkType='Event' AND @EventId IS NULL
    THROW 53692,'EventId es obligatorio para un webhook.',1;

  BEGIN TRANSACTION;

  IF @WorkType='Event'
  BEGIN
    UPDATE restaurante.PaymentGatewayEvent
    SET ProcessingStatus=CASE @Outcome
          WHEN 'Processed' THEN 'Processed'
          WHEN 'Ignored' THEN 'Ignored'
          WHEN 'Pending' THEN 'Pending'
          ELSE 'Failed' END,
        LeaseId=NULL,LeaseExpiresAtUtc=NULL,
        NextRetryAtUtc=CASE WHEN @Outcome IN('Pending','Failed')
          THEN COALESCE(@NextRetryAtUtc,DATEADD(MINUTE,1,SYSUTCDATETIME())) ELSE NULL END,
        FailureCode=CASE WHEN @Outcome IN('Pending','Failed') THEN NULLIF(LTRIM(RTRIM(@FailureCode)),'') ELSE NULL END,
        FailureMessage=CASE WHEN @Outcome IN('Pending','Failed')
          THEN LEFT(NULLIF(LTRIM(RTRIM(@FailureMessage)),N''),500) ELSE NULL END,
        ProcessedAtUtc=CASE WHEN @Outcome IN('Processed','Ignored') THEN SYSUTCDATETIME() ELSE NULL END,
        UpdatedAtUtc=SYSUTCDATETIME()
    WHERE Id=@EventId AND PublicSiteId=@PublicSiteId AND Rfc=@Rfc
      AND CheckoutAttemptId=@CheckoutAttemptId
      AND ProcessingStatus='Processing' AND LeaseId=@LeaseId;
  END
  ELSE
  BEGIN
    UPDATE restaurante.OnlineCheckoutAttempt
    SET RecoveryLeaseId=NULL,RecoveryLeaseExpiresAtUtc=NULL,
        NextRetryAtUtc=CASE WHEN @Outcome IN('Pending','Failed') AND [State]='CapturePending'
          THEN COALESCE(@NextRetryAtUtc,DATEADD(MINUTE,1,SYSUTCDATETIME())) ELSE NextRetryAtUtc END,
        FailureCode=CASE WHEN @Outcome='Failed' AND [State]='CapturePending'
          THEN NULLIF(LTRIM(RTRIM(@FailureCode)),'') ELSE FailureCode END,
        FailureMessage=CASE WHEN @Outcome='Failed' AND [State]='CapturePending'
          THEN LEFT(NULLIF(LTRIM(RTRIM(@FailureMessage)),N''),500) ELSE FailureMessage END,
        UpdatedAtUtc=SYSUTCDATETIME()
    WHERE Id=@CheckoutAttemptId AND PublicSiteId=@PublicSiteId AND Rfc=@Rfc
      AND RecoveryLeaseId=@LeaseId;
  END;

  IF @@ROWCOUNT<>1
  BEGIN
    ROLLBACK TRANSACTION;
    THROW 53693,'La concesion de recuperacion ya no es valida.',1;
  END;

  COMMIT TRANSACTION;

  SELECT @WorkType WorkType,@CheckoutAttemptId CheckoutAttemptId,@EventId EventId,
    @Outcome Outcome,SYSUTCDATETIME() UpdatedAtUtc;
END;
GO

CREATE OR ALTER PROCEDURE restaurante.PaymentGatewayRefundClaim
  @LeaseId uniqueidentifier,
  @LeaseSeconds int=90
AS
BEGIN
  SET NOCOUNT ON;
  SET XACT_ABORT ON;

  DECLARE @PublicSiteId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N'OrionERP.PublicSiteId'));
  DECLARE @Rfc varchar(50)=CONVERT(varchar(50),SESSION_CONTEXT(N'OrionRfc'));
  DECLARE @Claimed TABLE(Id uniqueidentifier NOT NULL);

  IF @PublicSiteId IS NULL OR NULLIF(@Rfc,'') IS NULL
    THROW 53694,'No existe un contexto publico de restaurante verificado.',1;
  IF @LeaseId IS NULL OR @LeaseSeconds NOT BETWEEN 15 AND 600
    THROW 53695,'La concesion de reembolso es invalida.',1;

  BEGIN TRANSACTION;
  ;WITH nextRefund AS
  (
    SELECT TOP(1) refundInfo.*
    FROM restaurante.PaymentGatewayRefund refundInfo WITH(UPDLOCK,READPAST,ROWLOCK)
    WHERE refundInfo.PublicSiteId=@PublicSiteId AND refundInfo.Rfc=@Rfc
      AND refundInfo.[Status] IN('Requested','Pending','Failed')
      AND (refundInfo.NextRetryAtUtc IS NULL OR refundInfo.NextRetryAtUtc<=SYSUTCDATETIME())
      AND (refundInfo.LeaseExpiresAtUtc IS NULL OR refundInfo.LeaseExpiresAtUtc<SYSUTCDATETIME())
    ORDER BY refundInfo.RequestedAtUtc,refundInfo.Id
  )
  UPDATE nextRefund
  SET [Status]='Processing',Attempts=Attempts+1,LeaseId=@LeaseId,
      LeaseExpiresAtUtc=DATEADD(SECOND,@LeaseSeconds,SYSUTCDATETIME()),
      NextRetryAtUtc=NULL,UpdatedAtUtc=SYSUTCDATETIME()
  OUTPUT inserted.Id INTO @Claimed(Id);

  UPDATE attempt
  SET [State]='RefundPending',UpdatedAtUtc=SYSUTCDATETIME()
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
END;
GO

CREATE OR ALTER PROCEDURE restaurante.PaymentGatewayRefundResult
  @Id uniqueidentifier,
  @LeaseId uniqueidentifier,
  @Outcome varchar(20),
  @ProviderRefundId varchar(64)=NULL,
  @FailureCode varchar(80)=NULL,
  @FailureMessage nvarchar(500)=NULL,
  @NextRetryAtUtc datetime2(3)=NULL
AS
BEGIN
  SET NOCOUNT ON;
  SET XACT_ABORT ON;

  DECLARE @PublicSiteId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N'OrionERP.PublicSiteId'));
  DECLARE @Rfc varchar(50)=CONVERT(varchar(50),SESSION_CONTEXT(N'OrionRfc'));
  DECLARE @TransactionId uniqueidentifier;
  DECLARE @CheckoutAttemptId uniqueidentifier;
  DECLARE @GrossAmount decimal(18,2);
  DECLARE @LocalPaymentId uniqueidentifier;
  DECLARE @CompletedAmount decimal(18,2);

  IF @PublicSiteId IS NULL OR NULLIF(@Rfc,'') IS NULL
    THROW 53696,'No existe un contexto publico de restaurante verificado.',1;
  IF @Outcome NOT IN('Completed','Pending','Failed')
    THROW 53697,'El resultado del reembolso es invalido.',1;
  IF @Outcome IN('Completed','Pending') AND NULLIF(LTRIM(RTRIM(@ProviderRefundId)),'') IS NULL
    THROW 53698,'ProviderRefundId es obligatorio para un reembolso aceptado por PayPal.',1;

  BEGIN TRANSACTION;

  SELECT @TransactionId=refundInfo.GatewayTransactionId,
    @CheckoutAttemptId=transactionInfo.CheckoutAttemptId,
    @GrossAmount=transactionInfo.GrossAmount,
    @LocalPaymentId=transactionInfo.LocalPaymentId
  FROM restaurante.PaymentGatewayRefund refundInfo WITH(UPDLOCK,HOLDLOCK)
  JOIN restaurante.PaymentGatewayTransaction transactionInfo
    ON transactionInfo.PublicSiteId=refundInfo.PublicSiteId
   AND transactionInfo.Rfc=refundInfo.Rfc
   AND transactionInfo.SiteId=refundInfo.SiteId
   AND transactionInfo.Id=refundInfo.GatewayTransactionId
  WHERE refundInfo.Id=@Id AND refundInfo.PublicSiteId=@PublicSiteId AND refundInfo.Rfc=@Rfc
    AND refundInfo.[Status]='Processing' AND refundInfo.LeaseId=@LeaseId;

  IF @TransactionId IS NULL
  BEGIN
    ROLLBACK TRANSACTION;
    THROW 53699,'La concesion del reembolso ya no es valida.',1;
  END;

  IF EXISTS
  (
    SELECT 1 FROM restaurante.PaymentGatewayRefund
    WHERE Id=@Id AND ProviderRefundId IS NOT NULL AND ProviderRefundId<>@ProviderRefundId
  )
  BEGIN
    ROLLBACK TRANSACTION;
    THROW 53700,'El reembolso ya esta ligado a otro ID de PayPal.',1;
  END;

  UPDATE restaurante.PaymentGatewayRefund
  SET [Status]=@Outcome,
      ProviderRefundId=CASE WHEN @Outcome IN('Completed','Pending')
        THEN COALESCE(ProviderRefundId,@ProviderRefundId) ELSE ProviderRefundId END,
      LeaseId=NULL,LeaseExpiresAtUtc=NULL,
      NextRetryAtUtc=CASE WHEN @Outcome IN('Pending','Failed')
        THEN COALESCE(@NextRetryAtUtc,DATEADD(MINUTE,1,SYSUTCDATETIME())) ELSE NULL END,
      FailureCode=CASE WHEN @Outcome='Failed' THEN NULLIF(LTRIM(RTRIM(@FailureCode)),'') ELSE NULL END,
      FailureMessage=CASE WHEN @Outcome='Failed'
        THEN LEFT(NULLIF(LTRIM(RTRIM(@FailureMessage)),N''),500) ELSE NULL END,
      CompletedAtUtc=CASE WHEN @Outcome='Completed' THEN COALESCE(CompletedAtUtc,SYSUTCDATETIME()) ELSE CompletedAtUtc END,
      UpdatedAtUtc=SYSUTCDATETIME()
  WHERE Id=@Id AND PublicSiteId=@PublicSiteId AND Rfc=@Rfc;

  SELECT @CompletedAmount=COALESCE(SUM(Amount),0)
  FROM restaurante.PaymentGatewayRefund
  WHERE PublicSiteId=@PublicSiteId AND Rfc=@Rfc
    AND GatewayTransactionId=@TransactionId AND [Status]='Completed';

  UPDATE restaurante.PaymentGatewayTransaction
  SET [Status]=CASE
      WHEN @CompletedAmount>=GrossAmount THEN 'Refunded'
      WHEN @CompletedAmount>0 THEN 'PartiallyRefunded'
      ELSE [Status] END,
      UpdatedAtUtc=SYSUTCDATETIME()
  WHERE Id=@TransactionId AND PublicSiteId=@PublicSiteId AND Rfc=@Rfc;

  UPDATE restaurante.OnlineCheckoutAttempt
  SET [State]=CASE
        WHEN @Outcome='Completed' AND @LocalPaymentId IS NULL AND @CompletedAmount>=@GrossAmount
          THEN 'Refunded'
        WHEN @Outcome IN('Completed','Pending','Failed') THEN 'RefundPending'
        ELSE [State] END,
      FailureCode=CASE WHEN @Outcome='Failed' THEN NULLIF(LTRIM(RTRIM(@FailureCode)),'') ELSE FailureCode END,
      FailureMessage=CASE WHEN @Outcome='Failed'
        THEN LEFT(NULLIF(LTRIM(RTRIM(@FailureMessage)),N''),500) ELSE FailureMessage END,
      UpdatedAtUtc=SYSUTCDATETIME()
  WHERE Id=@CheckoutAttemptId AND PublicSiteId=@PublicSiteId AND Rfc=@Rfc;

  COMMIT TRANSACTION;

  SELECT refundInfo.Id,refundInfo.[Status],refundInfo.ProviderRefundId,
    refundInfo.LocalRefundId,refundInfo.CompletedAtUtc,refundInfo.NextRetryAtUtc,
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
END;
GO

CREATE OR ALTER PROCEDURE restaurante.OnlineOrderNotificationClaim
  @LeaseId uniqueidentifier,
  @BatchSize int=10,
  @LeaseSeconds int=90
AS
BEGIN
  SET NOCOUNT ON;
  SET XACT_ABORT ON;

  DECLARE @PublicSiteId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N'OrionERP.PublicSiteId'));
  DECLARE @Rfc varchar(50)=CONVERT(varchar(50),SESSION_CONTEXT(N'OrionRfc'));
  DECLARE @Claimed TABLE(Id bigint NOT NULL);

  IF @PublicSiteId IS NULL OR NULLIF(@Rfc,'') IS NULL
    THROW 53701,'No existe un contexto publico de restaurante verificado.',1;
  IF @LeaseId IS NULL OR @LeaseSeconds NOT BETWEEN 15 AND 600 OR @BatchSize NOT BETWEEN 1 AND 50
    THROW 53702,'La concesion de correo es invalida.',1;

  BEGIN TRANSACTION;
  ;WITH nextNotification AS
  (
    SELECT TOP(@BatchSize) notification.*
    FROM restaurante.OnlineOrderNotification notification WITH(UPDLOCK,READPAST,ROWLOCK)
    WHERE notification.PublicSiteId=@PublicSiteId AND notification.Rfc=@Rfc
      AND notification.[Status] IN('Pending','Failed')
      AND (notification.NextRetryAtUtc IS NULL OR notification.NextRetryAtUtc<=SYSUTCDATETIME())
      AND (notification.LeaseExpiresAtUtc IS NULL OR notification.LeaseExpiresAtUtc<SYSUTCDATETIME())
    ORDER BY notification.CreatedAtUtc,notification.Id
  )
  UPDATE nextNotification
  SET [Status]='Processing',Attempts=Attempts+1,LeaseId=@LeaseId,
      LeaseExpiresAtUtc=DATEADD(SECOND,@LeaseSeconds,SYSUTCDATETIME()),
      NextRetryAtUtc=NULL,UpdatedAtUtc=SYSUTCDATETIME()
  OUTPUT inserted.Id INTO @Claimed(Id);
  COMMIT TRANSACTION;

  SELECT notification.Id,notification.CheckoutAttemptId,notification.RestaurantOrderId,
    notification.NotificationType,notification.RecipientEmail,notification.IdempotencyKey,
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
END;
GO

CREATE OR ALTER PROCEDURE restaurante.OnlineOrderNotificationComplete
  @Id bigint,
  @LeaseId uniqueidentifier
AS
BEGIN
  SET NOCOUNT ON;

  DECLARE @PublicSiteId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N'OrionERP.PublicSiteId'));
  DECLARE @Rfc varchar(50)=CONVERT(varchar(50),SESSION_CONTEXT(N'OrionRfc'));
  IF @PublicSiteId IS NULL OR NULLIF(@Rfc,'') IS NULL
    THROW 53705,'No existe un contexto publico de restaurante verificado.',1;

  UPDATE restaurante.OnlineOrderNotification
  SET [Status]='Sent',LeaseId=NULL,LeaseExpiresAtUtc=NULL,NextRetryAtUtc=NULL,
      FailureCode=NULL,FailureMessage=NULL,
      SentAtUtc=COALESCE(SentAtUtc,SYSUTCDATETIME()),UpdatedAtUtc=SYSUTCDATETIME()
  WHERE Id=@Id AND PublicSiteId=@PublicSiteId AND Rfc=@Rfc
    AND [Status]='Processing' AND LeaseId=@LeaseId;
  IF @@ROWCOUNT<>1
    THROW 53706,'La concesion de correo ya no es valida.',1;

  SELECT Id,[Status],Attempts,SentAtUtc,UpdatedAtUtc
  FROM restaurante.OnlineOrderNotification
  WHERE Id=@Id AND PublicSiteId=@PublicSiteId AND Rfc=@Rfc;
END;
GO

CREATE OR ALTER PROCEDURE restaurante.OnlineOrderNotificationFail
  @Id bigint,
  @LeaseId uniqueidentifier,
  @FailureCode varchar(80),
  @FailureMessage nvarchar(500)=NULL,
  @NextRetryAtUtc datetime2(3)=NULL
AS
BEGIN
  SET NOCOUNT ON;

  DECLARE @PublicSiteId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N'OrionERP.PublicSiteId'));
  DECLARE @Rfc varchar(50)=CONVERT(varchar(50),SESSION_CONTEXT(N'OrionRfc'));
  IF @PublicSiteId IS NULL OR NULLIF(@Rfc,'') IS NULL
    THROW 53707,'No existe un contexto publico de restaurante verificado.',1;
  IF NULLIF(LTRIM(RTRIM(@FailureCode)),'') IS NULL
    THROW 53708,'El codigo de falla de correo es obligatorio.',1;

  UPDATE restaurante.OnlineOrderNotification
  SET [Status]='Failed',LeaseId=NULL,LeaseExpiresAtUtc=NULL,
      NextRetryAtUtc=COALESCE(@NextRetryAtUtc,DATEADD(MINUTE,2,SYSUTCDATETIME())),
      FailureCode=LEFT(LTRIM(RTRIM(@FailureCode)),80),
      FailureMessage=LEFT(NULLIF(LTRIM(RTRIM(@FailureMessage)),N''),500),
      UpdatedAtUtc=SYSUTCDATETIME()
  WHERE Id=@Id AND PublicSiteId=@PublicSiteId AND Rfc=@Rfc
    AND [Status]='Processing' AND LeaseId=@LeaseId;
  IF @@ROWCOUNT<>1
    THROW 53709,'La concesion de correo ya no es valida.',1;

  SELECT Id,[Status],Attempts,NextRetryAtUtc,FailureCode,UpdatedAtUtc
  FROM restaurante.OnlineOrderNotification
  WHERE Id=@Id AND PublicSiteId=@PublicSiteId AND Rfc=@Rfc;
END;
GO

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

  DECLARE @PublicSiteId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N'OrionERP.PublicSiteId'));
  DECLARE @Rfc varchar(50)=CONVERT(varchar(50),SESSION_CONTEXT(N'OrionRfc'));

  IF @PublicSiteId IS NULL OR NULLIF(@Rfc,'') IS NULL
    THROW 53703,'No existe un contexto publico de restaurante verificado.',1;

  UPDATE restaurante.OnlineOrderNotification
  SET [Status]=CASE WHEN @Succeeded=1 THEN 'Sent' ELSE 'Failed' END,
      LeaseId=NULL,LeaseExpiresAtUtc=NULL,
      NextRetryAtUtc=CASE WHEN @Succeeded=0
        THEN COALESCE(@NextRetryAtUtc,DATEADD(MINUTE,2,SYSUTCDATETIME())) ELSE NULL END,
      FailureCode=CASE WHEN @Succeeded=0 THEN NULLIF(LTRIM(RTRIM(@FailureCode)),'') ELSE NULL END,
      FailureMessage=CASE WHEN @Succeeded=0
        THEN LEFT(NULLIF(LTRIM(RTRIM(@FailureMessage)),N''),500) ELSE NULL END,
      SentAtUtc=CASE WHEN @Succeeded=1 THEN COALESCE(SentAtUtc,SYSUTCDATETIME()) ELSE SentAtUtc END,
      UpdatedAtUtc=SYSUTCDATETIME()
  WHERE Id=@Id AND PublicSiteId=@PublicSiteId AND Rfc=@Rfc
    AND [Status]='Processing' AND LeaseId=@LeaseId;

  IF @@ROWCOUNT<>1
    THROW 53704,'La concesion de correo ya no es valida.',1;

  SELECT Id,[Status],Attempts,SentAtUtc,NextRetryAtUtc,UpdatedAtUtc
  FROM restaurante.OnlineOrderNotification
  WHERE Id=@Id AND PublicSiteId=@PublicSiteId AND Rfc=@Rfc;
END;
GO

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

  DECLARE @PublicSiteId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N'OrionERP.PublicSiteId'));
  DECLARE @Rfc varchar(50)=CONVERT(varchar(50),SESSION_CONTEXT(N'OrionRfc'));
  DECLARE @CompanyId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N'OrionERP.CompanyId'));
  DECLARE @OrionSiteId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N'OrionERP.SiteId'));
  DECLARE @ResolvedPublicSiteId bigint;
  DECLARE @SiteId int;
  DECLARE @TransactionId uniqueidentifier;
  DECLARE @CurrencyCode char(3);
  DECLARE @GrossAmount decimal(18,2);
  DECLARE @ExistingOrReserved decimal(18,2);
  DECLARE @RefundId uniqueidentifier;
  DECLARE @WasCreated bit=0;

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
    THROW 53710,'No se resolvio el sitio del reembolso.',1;
  IF @Amount<=0 OR NULLIF(LTRIM(RTRIM(@Reason)),N'') IS NULL
     OR NULLIF(LTRIM(RTRIM(@IdempotencyKey)),'') IS NULL
     OR NULLIF(LTRIM(RTRIM(@RequestedBy)),N'') IS NULL
     OR NULLIF(LTRIM(RTRIM(@AuthorizedBy)),N'') IS NULL
    THROW 53711,'La solicitud de reembolso es invalida.',1;

  BEGIN TRANSACTION;

  SELECT @TransactionId=transactionInfo.Id,@CurrencyCode=transactionInfo.CurrencyCode,
    @GrossAmount=transactionInfo.GrossAmount
  FROM restaurante.OnlineCheckoutAttempt attempt WITH(UPDLOCK,HOLDLOCK)
  JOIN restaurante.PaymentGatewayTransaction transactionInfo
    ON transactionInfo.PublicSiteId=attempt.PublicSiteId
   AND transactionInfo.Rfc=attempt.Rfc
   AND transactionInfo.SiteId=attempt.SiteId
   AND transactionInfo.CheckoutAttemptId=attempt.Id
  WHERE attempt.PublicSiteId=@ResolvedPublicSiteId AND attempt.Rfc=@Rfc
    AND attempt.SiteId=@SiteId AND attempt.RestaurantOrderId=@RestaurantOrderId
    AND transactionInfo.LocalPaymentId IS NOT NULL
    AND transactionInfo.[Status] IN('Completed','PartiallyRefunded');

  IF @TransactionId IS NULL
  BEGIN
    ROLLBACK TRANSACTION;
    THROW 53712,'La orden no tiene un pago PayPal reembolsable.',1;
  END;

  SELECT @RefundId=Id
  FROM restaurante.PaymentGatewayRefund WITH(UPDLOCK,HOLDLOCK)
  WHERE PublicSiteId=@ResolvedPublicSiteId AND IdempotencyKey=@IdempotencyKey;

  IF @RefundId IS NULL
  BEGIN
    SELECT @ExistingOrReserved=COALESCE(SUM(Amount),0)
    FROM restaurante.PaymentGatewayRefund
    WHERE PublicSiteId=@ResolvedPublicSiteId AND Rfc=@Rfc
      AND GatewayTransactionId=@TransactionId AND [Status]<>'Failed';

    IF @ExistingOrReserved+@Amount>@GrossAmount
    BEGIN
      ROLLBACK TRANSACTION;
      THROW 53713,'El reembolso excede el saldo capturado disponible.',1;
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
      LEFT(LTRIM(RTRIM(@Reason)),500),'Requested',@IdempotencyKey,
      LEFT(LTRIM(RTRIM(@RequestedBy)),256),LEFT(LTRIM(RTRIM(@AuthorizedBy)),256)
    );
    SET @WasCreated=1;
  END
  ELSE IF NOT EXISTS
  (
    SELECT 1 FROM restaurante.PaymentGatewayRefund
    WHERE Id=@RefundId AND GatewayTransactionId=@TransactionId
      AND Amount=@Amount AND Reason=LEFT(LTRIM(RTRIM(@Reason)),500)
  )
  BEGIN
    ROLLBACK TRANSACTION;
    THROW 53714,'La idempotencia ya se uso con otro reembolso.',1;
  END;

  UPDATE attempt
  SET [State]=CASE WHEN attempt.[State]='Refunded' THEN attempt.[State] ELSE 'RefundRequested' END,
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
END;
GO

CREATE OR ALTER PROCEDURE restaurante.PaymentGatewayRefundLocalComplete
  @Id uniqueidentifier,
  @LocalRefundId uniqueidentifier
AS
BEGIN
  SET NOCOUNT ON;
  SET XACT_ABORT ON;

  DECLARE @PublicSiteId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N'OrionERP.PublicSiteId'));
  DECLARE @Rfc varchar(50)=CONVERT(varchar(50),SESSION_CONTEXT(N'OrionRfc'));
  DECLARE @CompanyId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N'OrionERP.CompanyId'));
  DECLARE @OrionSiteId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N'OrionERP.SiteId'));
  DECLARE @ResolvedPublicSiteId bigint;
  DECLARE @TransactionId uniqueidentifier;
  DECLARE @CheckoutAttemptId uniqueidentifier;
  DECLARE @GrossAmount decimal(18,2);
  DECLARE @LinkedAmount decimal(18,2);

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
    THROW 53715,'No se resolvio el sitio del reembolso local.',1;

  BEGIN TRANSACTION;

  SELECT @TransactionId=refundInfo.GatewayTransactionId,
    @CheckoutAttemptId=transactionInfo.CheckoutAttemptId,@GrossAmount=transactionInfo.GrossAmount
  FROM restaurante.PaymentGatewayRefund refundInfo WITH(UPDLOCK,HOLDLOCK)
  JOIN restaurante.PaymentGatewayTransaction transactionInfo
    ON transactionInfo.PublicSiteId=refundInfo.PublicSiteId
   AND transactionInfo.Rfc=refundInfo.Rfc
   AND transactionInfo.SiteId=refundInfo.SiteId
   AND transactionInfo.Id=refundInfo.GatewayTransactionId
  JOIN restaurante.PaymentRefund localRefund
    ON localRefund.Rfc=refundInfo.Rfc AND localRefund.Id=@LocalRefundId
   AND localRefund.PaymentId=transactionInfo.LocalPaymentId
   AND localRefund.Amount=refundInfo.Amount
  WHERE refundInfo.Id=@Id AND refundInfo.PublicSiteId=@ResolvedPublicSiteId
    AND refundInfo.Rfc=@Rfc AND refundInfo.[Status]='Completed'
    AND (refundInfo.LocalRefundId IS NULL OR refundInfo.LocalRefundId=@LocalRefundId);

  IF @TransactionId IS NULL
  BEGIN
    ROLLBACK TRANSACTION;
    THROW 53716,'El reembolso local no coincide con el exito del proveedor.',1;
  END;

  UPDATE restaurante.PaymentGatewayRefund
  SET LocalRefundId=@LocalRefundId,UpdatedAtUtc=SYSUTCDATETIME()
  WHERE Id=@Id AND PublicSiteId=@ResolvedPublicSiteId AND Rfc=@Rfc;

  SELECT @LinkedAmount=COALESCE(SUM(Amount),0)
  FROM restaurante.PaymentGatewayRefund
  WHERE PublicSiteId=@ResolvedPublicSiteId AND Rfc=@Rfc
    AND GatewayTransactionId=@TransactionId AND [Status]='Completed' AND LocalRefundId IS NOT NULL;

  UPDATE restaurante.OnlineCheckoutAttempt
  SET [State]=CASE WHEN @LinkedAmount>=@GrossAmount THEN 'Refunded' ELSE 'PosCreated' END,
      UpdatedAtUtc=SYSUTCDATETIME()
  WHERE Id=@CheckoutAttemptId AND PublicSiteId=@ResolvedPublicSiteId AND Rfc=@Rfc;

  COMMIT TRANSACTION;

  SELECT @Id Id,@LocalRefundId LocalRefundId,@LinkedAmount LinkedRefundAmount,
    @GrossAmount GrossAmount,
    CONVERT(bit,CASE WHEN @LinkedAmount>=@GrossAmount THEN 1 ELSE 0 END) IsFullyRefunded;
END;
GO

CREATE OR ALTER PROCEDURE restaurante.OnlineOrderNotificationEnqueue
  @RestaurantOrderId uniqueidentifier,
  @NotificationType varchar(30)
AS
BEGIN
  SET NOCOUNT ON;
  SET XACT_ABORT ON;

  DECLARE @PublicSiteId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N'OrionERP.PublicSiteId'));
  DECLARE @Rfc varchar(50)=CONVERT(varchar(50),SESSION_CONTEXT(N'OrionRfc'));
  DECLARE @CompanyId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N'OrionERP.CompanyId'));
  DECLARE @OrionSiteId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N'OrionERP.SiteId'));
  DECLARE @ResolvedPublicSiteId bigint;
  DECLARE @SiteId int;
  DECLARE @CheckoutAttemptId uniqueidentifier;
  DECLARE @RecipientEmail nvarchar(320);

  IF @NotificationType NOT IN('Confirmation','Ready')
    THROW 53717,'El tipo de notificacion online es invalido.',1;

  SELECT @ResolvedPublicSiteId=settings.PublicSiteId,@SiteId=settings.SiteId
  FROM restaurante.[Order] scopedOrder
  JOIN restaurante.OnlineOrderingSettings settings
    ON settings.PublicSiteId=scopedOrder.PublicSiteId
   AND settings.Rfc=scopedOrder.Rfc
   AND settings.SiteId=scopedOrder.SiteId
  WHERE scopedOrder.Rfc=@Rfc AND scopedOrder.Id=@RestaurantOrderId
    AND
    (
      (@PublicSiteId IS NOT NULL AND settings.PublicSiteId=@PublicSiteId)
      OR (@PublicSiteId IS NULL AND settings.CompanyId=@CompanyId
          AND (@OrionSiteId IS NULL OR settings.OrionSiteId=@OrionSiteId))
    );

  SELECT @CheckoutAttemptId=attempt.Id,@RecipientEmail=attempt.CustomerEmail
  FROM restaurante.[Order] orderInfo
  JOIN restaurante.OnlineCheckoutAttempt attempt
    ON attempt.PublicSiteId=orderInfo.PublicSiteId AND attempt.Rfc=orderInfo.Rfc
   AND attempt.SiteId=orderInfo.SiteId AND attempt.Id=orderInfo.OnlineCheckoutAttemptId
  WHERE orderInfo.PublicSiteId=@ResolvedPublicSiteId AND orderInfo.Rfc=@Rfc
    AND orderInfo.SiteId=@SiteId AND orderInfo.Id=@RestaurantOrderId
    AND orderInfo.SalesChannel='Web'
    AND (@NotificationType='Confirmation' OR orderInfo.[Status] IN('Ready','Completed'));

  IF @CheckoutAttemptId IS NULL
    THROW 53718,'La orden Web no esta lista para esta notificacion.',1;

  BEGIN TRANSACTION;
  IF NOT EXISTS
  (
    SELECT 1 FROM restaurante.OnlineOrderNotification WITH(UPDLOCK,HOLDLOCK)
    WHERE PublicSiteId=@ResolvedPublicSiteId AND CheckoutAttemptId=@CheckoutAttemptId
      AND NotificationType=@NotificationType
  )
    INSERT restaurante.OnlineOrderNotification
    (
      PublicSiteId,Rfc,SiteId,CheckoutAttemptId,RestaurantOrderId,
      NotificationType,RecipientEmail,IdempotencyKey
    )
    VALUES
    (
      @ResolvedPublicSiteId,@Rfc,@SiteId,@CheckoutAttemptId,@RestaurantOrderId,
      @NotificationType,@RecipientEmail,
      CONCAT('email-',LOWER(@NotificationType),'-',CONVERT(varchar(36),@CheckoutAttemptId))
    );
  COMMIT TRANSACTION;

  SELECT Id,CheckoutAttemptId,RestaurantOrderId,NotificationType,[Status],CreatedAtUtc
  FROM restaurante.OnlineOrderNotification
  WHERE PublicSiteId=@ResolvedPublicSiteId AND CheckoutAttemptId=@CheckoutAttemptId
    AND NotificationType=@NotificationType;
END;
GO

CREATE OR ALTER PROCEDURE restaurante.OnlineOrderingRecoveryList
  @Take int=100
AS
BEGIN
  SET NOCOUNT ON;

  DECLARE @PublicSiteId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N'OrionERP.PublicSiteId'));
  DECLARE @Rfc varchar(50)=CONVERT(varchar(50),SESSION_CONTEXT(N'OrionRfc'));
  DECLARE @CompanyId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N'OrionERP.CompanyId'));
  DECLARE @OrionSiteId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N'OrionERP.SiteId'));
  DECLARE @ResolvedPublicSiteId bigint;

  IF @Take NOT BETWEEN 1 AND 500
    THROW 53719,'El limite de recuperacion es invalido.',1;

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
    THROW 53720,'No se resolvio el sitio de recuperacion.',1;

  SELECT TOP(@Take)
    attempt.Id CheckoutAttemptId,attempt.[State] CheckoutStatus,
    attempt.PayPalOrderId,attempt.PayPalCaptureId,orderInfo.Folio OrderFolio,
    attempt.Total,attempt.CurrencyCode Currency,
    attempt.ImportAttempts+attempt.RecoveryAttempts RetryCount,
    COALESCE(refundFailure.FailureCode,eventFailure.FailureCode,attempt.FailureCode) LastErrorCode,
    COALESCE(refundFailure.FailureMessage,eventFailure.FailureMessage,attempt.FailureMessage) LastErrorMessage,
    attempt.UpdatedAtUtc
  FROM restaurante.OnlineCheckoutAttempt attempt
  LEFT JOIN restaurante.[Order] orderInfo
    ON orderInfo.PublicSiteId=attempt.PublicSiteId AND orderInfo.Rfc=attempt.Rfc
   AND orderInfo.SiteId=attempt.SiteId AND orderInfo.Id=attempt.RestaurantOrderId
  OUTER APPLY
  (
    SELECT TOP(1) refundInfo.FailureCode,refundInfo.FailureMessage
    FROM restaurante.PaymentGatewayTransaction transactionInfo
    JOIN restaurante.PaymentGatewayRefund refundInfo
      ON refundInfo.PublicSiteId=transactionInfo.PublicSiteId
     AND refundInfo.Rfc=transactionInfo.Rfc AND refundInfo.SiteId=transactionInfo.SiteId
     AND refundInfo.GatewayTransactionId=transactionInfo.Id
    WHERE transactionInfo.PublicSiteId=attempt.PublicSiteId
      AND transactionInfo.CheckoutAttemptId=attempt.Id
      AND refundInfo.[Status] IN('Failed','Pending','Processing')
    ORDER BY refundInfo.UpdatedAtUtc DESC
  ) refundFailure
  OUTER APPLY
  (
    SELECT TOP(1) eventInfo.FailureCode,eventInfo.FailureMessage
    FROM restaurante.PaymentGatewayEvent eventInfo
    WHERE eventInfo.PublicSiteId=attempt.PublicSiteId
      AND eventInfo.CheckoutAttemptId=attempt.Id
      AND eventInfo.ProcessingStatus='Failed'
    ORDER BY eventInfo.UpdatedAtUtc DESC
  ) eventFailure
  WHERE attempt.PublicSiteId=@ResolvedPublicSiteId AND attempt.Rfc=@Rfc
    AND
    (
      attempt.[State] IN('CapturePending','Captured','CapturedNeedsOrder','RefundRequested','RefundPending','Failed')
      OR refundFailure.FailureCode IS NOT NULL OR eventFailure.FailureCode IS NOT NULL
    )
  ORDER BY attempt.UpdatedAtUtc DESC,attempt.Id;
END;
GO

IF OBJECT_ID(N'tempdb..#RestaurantOnlineOrderingMigrationState',N'U') IS NULL
BEGIN
  IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
  THROW 53721,'Se perdio el estado de migracion entre batches.',1;
END;
IF @@TRANCOUNT<>1 OR XACT_STATE()<>1
BEGIN
  IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
  IF OBJECT_ID(N'tempdb..#RestaurantOnlineOrderingMigrationState',N'U') IS NOT NULL
    DROP TABLE #RestaurantOnlineOrderingMigrationState;
  THROW 53722,'La transaccion de migracion no sobrevivio entre batches.',1;
END;

DECLARE @ApplyChanges bit;
DECLARE @MigrationId nvarchar(200);
DECLARE @MigrationChecksum varchar(128);
DECLARE @AppVersion nvarchar(64);
SELECT @ApplyChanges=ApplyChanges,@MigrationId=MigrationId,
  @MigrationChecksum=MigrationChecksum,@AppVersion=AppVersion
FROM #RestaurantOnlineOrderingMigrationState;

BEGIN TRY
  INSERT orion.PublicPermissionProfile(ProfileCode,ProfileVersion,ModuleCode)
  SELECT 'RESTAURANT_PUBLIC',7,'RESTAURANT'
  WHERE NOT EXISTS
  (
    SELECT 1 FROM orion.PublicPermissionProfile
    WHERE ProfileCode='RESTAURANT_PUBLIC' AND ProfileVersion=7
  );

  INSERT orion.PublicPermissionProfileEntry
    (ProfileCode,ProfileVersion,PermissionState,PermissionName,SecurableClass,SchemaName,ObjectName)
  SELECT source.ProfileCode,7,source.PermissionState,source.PermissionName,
    source.SecurableClass,source.SchemaName,source.ObjectName
  FROM orion.PublicPermissionProfileEntry source
  WHERE source.ProfileCode='RESTAURANT_PUBLIC' AND source.ProfileVersion=6
    AND NOT EXISTS
    (
      SELECT 1 FROM orion.PublicPermissionProfileEntry target
      WHERE target.ProfileCode='RESTAURANT_PUBLIC' AND target.ProfileVersion=7
        AND target.PermissionState=source.PermissionState
        AND target.PermissionName=source.PermissionName
        AND target.SecurableClass=source.SecurableClass
        AND target.SchemaName=source.SchemaName
        AND target.ObjectName=source.ObjectName
    );

  DECLARE @OnlinePermissions TABLE
  (
    PermissionState varchar(5) NOT NULL,
    PermissionName varchar(20) NOT NULL,
    SchemaName sysname NOT NULL,
    ObjectName sysname NOT NULL,
    PRIMARY KEY(PermissionState,PermissionName,SchemaName,ObjectName)
  );

  INSERT @OnlinePermissions(PermissionState,PermissionName,SchemaName,ObjectName)
  SELECT 'DENY',permissionInfo.PermissionName,'restaurante',objectInfo.ObjectName
  FROM (VALUES('SELECT'),('INSERT'),('UPDATE'),('DELETE')) permissionInfo(PermissionName)
  CROSS JOIN
  (VALUES
    ('OnlineOrderingSettings'),('OnlineOrderProduct'),('OnlineCheckoutAttempt'),
    ('PaymentGatewayTransaction'),('PaymentGatewayRefund'),
    ('PaymentGatewayEvent'),('OnlineOrderNotification')
  ) objectInfo(ObjectName);

  INSERT @OnlinePermissions VALUES
    ('GRANT','SELECT','restaurante','vw_PublicOnlineCheckoutStatus'),
    ('GRANT','EXECUTE','restaurante','OnlineOrderingBootstrapGet'),
    ('GRANT','EXECUTE','restaurante','OnlineCheckoutAttemptCreate'),
    ('GRANT','EXECUTE','restaurante','OnlineCheckoutAttemptGet'),
    ('GRANT','EXECUTE','restaurante','OnlineCheckoutPayPalOrderRecord'),
    ('GRANT','EXECUTE','restaurante','OnlineCheckoutCaptureAuthorize'),
    ('GRANT','EXECUTE','restaurante','OnlineCheckoutCaptureRecord'),
    ('GRANT','EXECUTE','restaurante','OnlineCheckoutStateSet'),
    ('GRANT','EXECUTE','restaurante','OnlineCheckoutStatusGet'),
    ('GRANT','EXECUTE','restaurante','PaymentGatewayEventRecord'),
    ('GRANT','EXECUTE','restaurante','OnlineOrderingRuntimeReadinessSet'),
    ('GRANT','EXECUTE','restaurante','PayPalRecoveryClaim'),
    ('GRANT','EXECUTE','restaurante','PayPalRecoveryResult'),
    ('GRANT','EXECUTE','restaurante','PaymentGatewayRefundClaim'),
    ('GRANT','EXECUTE','restaurante','PaymentGatewayRefundResult'),
    ('GRANT','EXECUTE','restaurante','OnlineOrderNotificationClaim'),
    ('GRANT','EXECUTE','restaurante','OnlineOrderNotificationComplete'),
    ('GRANT','EXECUTE','restaurante','OnlineOrderNotificationFail');

  INSERT orion.PublicPermissionProfileEntry
    (ProfileCode,ProfileVersion,PermissionState,PermissionName,SecurableClass,SchemaName,ObjectName)
  SELECT 'RESTAURANT_PUBLIC',7,permissionInfo.PermissionState,
    permissionInfo.PermissionName,'OBJECT',permissionInfo.SchemaName,permissionInfo.ObjectName
  FROM @OnlinePermissions permissionInfo
  WHERE NOT EXISTS
  (
    SELECT 1 FROM orion.PublicPermissionProfileEntry existing
    WHERE existing.ProfileCode='RESTAURANT_PUBLIC' AND existing.ProfileVersion=7
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
  WHERE profileInfo.ProfileCode='RESTAURANT_PUBLIC' AND profileInfo.ProfileVersion=7;

  IF EXISTS
  (
    SELECT 1
    FROM orion.PublicSqlPrincipalBinding binding
    JOIN orion.PublicSite publicSite ON publicSite.PublicSiteId=binding.PublicSiteId
    WHERE binding.IsActive=1 AND binding.PermissionProfile='RESTAURANT_PUBLIC'
      AND publicSite.ModuleCode='RESTAURANT'
      AND DATABASE_PRINCIPAL_ID(binding.PrincipalName) IS NULL
  )
    THROW 53723,'Un binding Restaurant activo no tiene principal de base.',1;

  DECLARE @PrincipalName sysname;
  DECLARE @PermissionState varchar(5);
  DECLARE @PermissionName varchar(20);
  DECLARE @SchemaName sysname;
  DECLARE @ObjectName sysname;
  DECLARE @PermissionSql nvarchar(max);

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
    DECLARE permission_cursor CURSOR LOCAL FAST_FORWARD FOR
      SELECT PermissionState,PermissionName,SchemaName,ObjectName FROM @OnlinePermissions;
    OPEN permission_cursor;
    FETCH NEXT FROM permission_cursor
      INTO @PermissionState,@PermissionName,@SchemaName,@ObjectName;
    WHILE @@FETCH_STATUS=0
    BEGIN
      SET @PermissionSql=@PermissionState+N' '+@PermissionName+N' ON OBJECT::'+
        QUOTENAME(@SchemaName)+N'.'+QUOTENAME(@ObjectName)+N' TO '+QUOTENAME(@PrincipalName)+N';';
      EXEC sys.sp_executesql @PermissionSql;
      FETCH NEXT FROM permission_cursor
        INTO @PermissionState,@PermissionName,@SchemaName,@ObjectName;
    END;
    CLOSE permission_cursor;
    DEALLOCATE permission_cursor;

    FETCH NEXT FROM principal_cursor INTO @PrincipalName;
  END;
  CLOSE principal_cursor;
  DEALLOCATE principal_cursor;

  UPDATE binding
  SET PermissionVersion=7,UpdatedAtUtc=SYSUTCDATETIME(),UpdatedBy=ORIGINAL_LOGIN()
  FROM orion.PublicSqlPrincipalBinding binding
  JOIN orion.PublicSite publicSite ON publicSite.PublicSiteId=binding.PublicSiteId
  WHERE binding.IsActive=1 AND binding.PermissionProfile='RESTAURANT_PUBLIC'
    AND publicSite.ModuleCode='RESTAURANT';
  DECLARE @UpdatedBindings int=@@ROWCOUNT;

  IF EXISTS
  (
    SELECT 1 FROM @OnlinePermissions expected
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
    THROW 53724,'No quedaron aplicados todos los permisos de checkout.',1;

  IF EXISTS(SELECT 1 FROM restaurante.OnlineOrderingSettings WHERE IsEnabled<>0)
     OR EXISTS(SELECT 1 FROM restaurante.OnlineOrderProduct WHERE IsEnabled<>0)
    THROW 53725,'La migracion intento habilitar pedidos o productos.',1;

  IF NOT EXISTS
  (
    SELECT 1 FROM sys.security_policies
    WHERE name='OnlineOrderingScopePolicy' AND schema_id=SCHEMA_ID('restaurante') AND is_enabled=1
  )
    THROW 53726,'La politica PublicSite/RFC no quedo activa.',1;

  SELECT DB_NAME() DatabaseName,@ApplyChanges ApplyChanges,
    (SELECT COUNT_BIG(*) FROM restaurante.OnlineOrderingSettings) ConfiguredPublicSites,
    (SELECT COUNT_BIG(*) FROM restaurante.OnlineOrderProduct) ProductEligibilityRows,
    @UpdatedBindings UpdatedPublicBindings,
    CONVERT(bit,CASE WHEN DB_NAME()='grupocarpio' THEN 1 ELSE 0 END) RequiresLiveGateway;

  EXEC sys.sp_set_session_context @key=N'OrionERP.PublicSiteId',@value=NULL;
  EXEC sys.sp_set_session_context @key=N'OrionERP.SiteId',@value=NULL;
  EXEC sys.sp_set_session_context @key=N'OrionERP.CompanyId',@value=NULL;
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
    SELECT N'APLICADO_DESHABILITADO' Estado,@MigrationId MigrationId;
  END
  ELSE
  BEGIN
    ROLLBACK TRANSACTION;
    SELECT N'PREVIEW_VALIDADO_SIN_CAMBIOS' Estado,@MigrationId MigrationId;
  END;

  IF OBJECT_ID(N'tempdb..#RestaurantOnlineOrderingMigrationState',N'U') IS NOT NULL
    DROP TABLE #RestaurantOnlineOrderingMigrationState;
END TRY
BEGIN CATCH
  IF CURSOR_STATUS('local','permission_cursor')>=-1
  BEGIN
    IF CURSOR_STATUS('local','permission_cursor')> -1 CLOSE permission_cursor;
    DEALLOCATE permission_cursor;
  END;
  IF CURSOR_STATUS('local','principal_cursor')>=-1
  BEGIN
    IF CURSOR_STATUS('local','principal_cursor')> -1 CLOSE principal_cursor;
    DEALLOCATE principal_cursor;
  END;
  IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
  EXEC sys.sp_set_session_context @key=N'OrionERP.PublicSiteId',@value=NULL;
  EXEC sys.sp_set_session_context @key=N'OrionERP.SiteId',@value=NULL;
  EXEC sys.sp_set_session_context @key=N'OrionERP.CompanyId',@value=NULL;
  EXEC sys.sp_set_session_context @key=N'OrionRfc',@value=NULL;
  IF OBJECT_ID(N'tempdb..#RestaurantOnlineOrderingMigrationState',N'U') IS NOT NULL
    DROP TABLE #RestaurantOnlineOrderingMigrationState;
  THROW;
END CATCH;
