/*
  Promociones, membresía y sitio público de Bruno's.

  Uso:
    sqlcmd ... -f 65001 -v ExpectedDatabase="Orion_Sandbox" ApplyChanges="0" -i 20260730_bruno_promotions_loyalty.sql
    sqlcmd ... -f 65001 -v ExpectedDatabase="Orion_Sandbox" ApplyChanges="1" -i 20260730_bruno_promotions_loyalty.sql

  ApplyChanges=0 ejecuta y valida todo dentro de una transacción que se revierte.
  ApplyChanges=1 confirma. Producción requiere respaldo y autorización explícita.
  -f 65001 es obligatorio para conservar correctamente los literales Unicode del archivo UTF-8.
*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET XACT_ABORT ON;
SET NOCOUNT ON;

DECLARE @ExpectedDatabase sysname = N'$(ExpectedDatabase)';
DECLARE @ApplyChanges bit = TRY_CONVERT(bit, N'$(ApplyChanges)');
DECLARE @BrunoRfc varchar(50) = 'BRUNOS260707L26';
DECLARE @LockResult int;

IF @ExpectedDatabase NOT IN (N'Orion_Sandbox', N'Orion_SandBox', N'grupocarpio')
  THROW 51200, 'ExpectedDatabase debe ser Orion_Sandbox o grupocarpio.', 1;
IF DB_NAME() <> @ExpectedDatabase
  THROW 51201, 'La base conectada no coincide con ExpectedDatabase.', 1;
IF @ApplyChanges IS NULL
  THROW 51202, 'ApplyChanges debe ser 0 o 1.', 1;
IF SESSION_CONTEXT(N'OrionRfc') IS NOT NULL
  THROW 51203, 'La migración requiere SESSION_CONTEXT OrionRfc en NULL.', 1;
IF NOT EXISTS (SELECT 1 FROM restaurante.Site WHERE Rfc=@BrunoRfc AND SiteCode='BRUNOS-01')
  THROW 51204, 'No existe la sede BRUNOS-01 para el RFC interno de Bruno.', 1;

SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;

BEGIN TRY
  BEGIN TRANSACTION;

  EXEC @LockResult = sys.sp_getapplock
    @Resource = N'OrionERP:Bruno:PromotionsLoyalty:20260730',
    @LockMode = N'Exclusive',
    @LockOwner = N'Transaction',
    @LockTimeout = 15000;
  IF @LockResult < 0
    THROW 51205, 'No fue posible obtener el bloqueo exclusivo de migración.', 1;

  IF SCHEMA_ID('fidelidad') IS NULL EXEC('CREATE SCHEMA fidelidad');
  IF SCHEMA_ID('brunos_auth') IS NULL EXEC('CREATE SCHEMA brunos_auth');

  /* Identidad de clientes; permanece separada de auth (OrionERP administrativo). */
  IF OBJECT_ID('brunos_auth.AspNetRoles','U') IS NULL
  BEGIN
    CREATE TABLE brunos_auth.AspNetRoles
    (
      Id nvarchar(450) NOT NULL CONSTRAINT PK_BrunoAspNetRoles PRIMARY KEY,
      [Name] nvarchar(256) NULL,
      NormalizedName nvarchar(256) NULL,
      ConcurrencyStamp nvarchar(max) NULL
    );
    CREATE UNIQUE INDEX RoleNameIndex_Bruno ON brunos_auth.AspNetRoles(NormalizedName) WHERE NormalizedName IS NOT NULL;
  END;

  IF OBJECT_ID('brunos_auth.AspNetUsers','U') IS NULL
  BEGIN
    CREATE TABLE brunos_auth.AspNetUsers
    (
      Id nvarchar(450) NOT NULL CONSTRAINT PK_BrunoAspNetUsers PRIMARY KEY,
      UserName nvarchar(256) NULL,
      NormalizedUserName nvarchar(256) NULL,
      Email nvarchar(256) NULL,
      NormalizedEmail nvarchar(256) NULL,
      EmailConfirmed bit NOT NULL,
      PasswordHash nvarchar(max) NULL,
      SecurityStamp nvarchar(max) NULL,
      ConcurrencyStamp nvarchar(max) NULL,
      PhoneNumber nvarchar(max) NULL,
      PhoneNumberConfirmed bit NOT NULL,
      TwoFactorEnabled bit NOT NULL,
      LockoutEnd datetimeoffset NULL,
      LockoutEnabled bit NOT NULL,
      AccessFailedCount int NOT NULL,
      FirstName nvarchar(100) NOT NULL,
      LastName nvarchar(100) NOT NULL,
      CreatedAt datetime2(0) NOT NULL CONSTRAINT DF_BrunoUser_Created DEFAULT SYSUTCDATETIME(),
      ClosedAt datetime2(0) NULL
    );
    CREATE INDEX EmailIndex_Bruno ON brunos_auth.AspNetUsers(NormalizedEmail);
    CREATE UNIQUE INDEX UserNameIndex_Bruno ON brunos_auth.AspNetUsers(NormalizedUserName) WHERE NormalizedUserName IS NOT NULL;
  END;

  IF OBJECT_ID('brunos_auth.AspNetRoleClaims','U') IS NULL
  BEGIN
    CREATE TABLE brunos_auth.AspNetRoleClaims
    (
      Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_BrunoAspNetRoleClaims PRIMARY KEY,
      RoleId nvarchar(450) NOT NULL,
      ClaimType nvarchar(max) NULL,
      ClaimValue nvarchar(max) NULL,
      CONSTRAINT FK_BrunoRoleClaims_Roles FOREIGN KEY(RoleId) REFERENCES brunos_auth.AspNetRoles(Id) ON DELETE CASCADE
    );
    CREATE INDEX IX_BrunoRoleClaims_RoleId ON brunos_auth.AspNetRoleClaims(RoleId);
  END;

  IF OBJECT_ID('brunos_auth.AspNetUserClaims','U') IS NULL
  BEGIN
    CREATE TABLE brunos_auth.AspNetUserClaims
    (
      Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_BrunoAspNetUserClaims PRIMARY KEY,
      UserId nvarchar(450) NOT NULL,
      ClaimType nvarchar(max) NULL,
      ClaimValue nvarchar(max) NULL,
      CONSTRAINT FK_BrunoUserClaims_Users FOREIGN KEY(UserId) REFERENCES brunos_auth.AspNetUsers(Id) ON DELETE CASCADE
    );
    CREATE INDEX IX_BrunoUserClaims_UserId ON brunos_auth.AspNetUserClaims(UserId);
  END;

  IF OBJECT_ID('brunos_auth.AspNetUserLogins','U') IS NULL
  BEGIN
    CREATE TABLE brunos_auth.AspNetUserLogins
    (
      LoginProvider nvarchar(450) NOT NULL,
      ProviderKey nvarchar(450) NOT NULL,
      ProviderDisplayName nvarchar(max) NULL,
      UserId nvarchar(450) NOT NULL,
      CONSTRAINT PK_BrunoAspNetUserLogins PRIMARY KEY(LoginProvider,ProviderKey),
      CONSTRAINT FK_BrunoUserLogins_Users FOREIGN KEY(UserId) REFERENCES brunos_auth.AspNetUsers(Id) ON DELETE CASCADE
    );
    CREATE INDEX IX_BrunoUserLogins_UserId ON brunos_auth.AspNetUserLogins(UserId);
  END;

  IF OBJECT_ID('brunos_auth.AspNetUserRoles','U') IS NULL
  BEGIN
    CREATE TABLE brunos_auth.AspNetUserRoles
    (
      UserId nvarchar(450) NOT NULL,
      RoleId nvarchar(450) NOT NULL,
      CONSTRAINT PK_BrunoAspNetUserRoles PRIMARY KEY(UserId,RoleId),
      CONSTRAINT FK_BrunoUserRoles_Users FOREIGN KEY(UserId) REFERENCES brunos_auth.AspNetUsers(Id) ON DELETE CASCADE,
      CONSTRAINT FK_BrunoUserRoles_Roles FOREIGN KEY(RoleId) REFERENCES brunos_auth.AspNetRoles(Id) ON DELETE CASCADE
    );
    CREATE INDEX IX_BrunoUserRoles_RoleId ON brunos_auth.AspNetUserRoles(RoleId);
  END;

  IF OBJECT_ID('brunos_auth.AspNetUserTokens','U') IS NULL
  BEGIN
    CREATE TABLE brunos_auth.AspNetUserTokens
    (
      UserId nvarchar(450) NOT NULL,
      LoginProvider nvarchar(450) NOT NULL,
      [Name] nvarchar(450) NOT NULL,
      [Value] nvarchar(max) NULL,
      CONSTRAINT PK_BrunoAspNetUserTokens PRIMARY KEY(UserId,LoginProvider,[Name]),
      CONSTRAINT FK_BrunoUserTokens_Users FOREIGN KEY(UserId) REFERENCES brunos_auth.AspNetUsers(Id) ON DELETE CASCADE
    );
  END;

  /* Fidelidad */
  IF OBJECT_ID('fidelidad.MemberAccount','U') IS NULL
  BEGIN
    CREATE TABLE fidelidad.MemberAccount
    (
      Id uniqueidentifier NOT NULL CONSTRAINT PK_LoyaltyMember PRIMARY KEY,
      Rfc varchar(50) NOT NULL,
      IdentityUserId nvarchar(450) NOT NULL,
      MembershipNumber varchar(20) NOT NULL,
      FirstName nvarchar(100) NOT NULL,
      LastName nvarchar(100) NOT NULL,
      NormalizedEmail nvarchar(256) NOT NULL,
      NormalizedPhone varchar(30) NOT NULL,
      EmailVerified bit NOT NULL CONSTRAINT DF_LoyaltyMember_EmailVerified DEFAULT 0,
      PhoneVerified bit NOT NULL CONSTRAINT DF_LoyaltyMember_PhoneVerified DEFAULT 0,
      [Status] varchar(30) NOT NULL,
      PointsBalance int NOT NULL CONSTRAINT DF_LoyaltyMember_Balance DEFAULT 0,
      IsAdultConfirmed bit NOT NULL,
      CreatedAt datetime2(0) NOT NULL CONSTRAINT DF_LoyaltyMember_Created DEFAULT SYSUTCDATETIME(),
      UpdatedAt datetime2(0) NOT NULL CONSTRAINT DF_LoyaltyMember_Updated DEFAULT SYSUTCDATETIME(),
      ClosedAt datetime2(0) NULL,
      RowVersion rowversion NOT NULL,
      CONSTRAINT CK_LoyaltyMember_Balance CHECK(PointsBalance >= 0),
      CONSTRAINT FK_LoyaltyMember_Identity FOREIGN KEY(IdentityUserId) REFERENCES brunos_auth.AspNetUsers(Id),
      CONSTRAINT UX_LoyaltyMember_RfcIdentity UNIQUE(Rfc,IdentityUserId),
      CONSTRAINT UX_LoyaltyMember_RfcNumber UNIQUE(Rfc,MembershipNumber),
      CONSTRAINT UX_LoyaltyMember_RfcEmail UNIQUE(Rfc,NormalizedEmail),
      CONSTRAINT UX_LoyaltyMember_RfcPhone UNIQUE(Rfc,NormalizedPhone)
    );
    CREATE UNIQUE INDEX UX_LoyaltyMember_RfcId ON fidelidad.MemberAccount(Rfc,Id);
    CREATE INDEX IX_LoyaltyMember_Lookup ON fidelidad.MemberAccount(Rfc,[Status],NormalizedPhone,NormalizedEmail);
  END;

  IF OBJECT_ID('fidelidad.MemberConsent','U') IS NULL
  BEGIN
    CREATE TABLE fidelidad.MemberConsent
    (
      Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_MemberConsent PRIMARY KEY,
      Rfc varchar(50) NOT NULL,
      MemberId uniqueidentifier NOT NULL,
      ConsentType varchar(40) NOT NULL,
      DocumentVersion varchar(30) NOT NULL,
      IsGranted bit NOT NULL,
      Source varchar(30) NOT NULL,
      RecordedAt datetime2(0) NOT NULL CONSTRAINT DF_MemberConsent_Recorded DEFAULT SYSUTCDATETIME(),
      CONSTRAINT FK_MemberConsent_Member FOREIGN KEY(Rfc,MemberId) REFERENCES fidelidad.MemberAccount(Rfc,Id)
    );
    CREATE INDEX IX_MemberConsent_Current ON fidelidad.MemberConsent(Rfc,MemberId,ConsentType,RecordedAt DESC);
  END;

  IF OBJECT_ID('fidelidad.PointLedger','U') IS NULL
  BEGIN
    CREATE TABLE fidelidad.PointLedger
    (
      Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_PointLedger PRIMARY KEY,
      Rfc varchar(50) NOT NULL,
      MemberId uniqueidentifier NOT NULL,
      EntryType varchar(30) NOT NULL,
      PointsDelta int NOT NULL,
      BalanceAfter int NOT NULL,
      EligibleMerchandiseAmount decimal(18,2) NULL,
      OrderId uniqueidentifier NULL,
      RefundId uniqueidentifier NULL,
      SourceKey varchar(120) NOT NULL,
      Reason nvarchar(500) NULL,
      CreatedBy nvarchar(256) NULL,
      OccurredAt datetime2(0) NOT NULL CONSTRAINT DF_PointLedger_Occurred DEFAULT SYSUTCDATETIME(),
      CONSTRAINT CK_PointLedger_Balance CHECK(BalanceAfter >= 0),
      CONSTRAINT FK_PointLedger_Member FOREIGN KEY(Rfc,MemberId) REFERENCES fidelidad.MemberAccount(Rfc,Id),
      CONSTRAINT UX_PointLedger_Source UNIQUE(Rfc,SourceKey)
    );
    CREATE INDEX IX_PointLedger_Member ON fidelidad.PointLedger(Rfc,MemberId,OccurredAt DESC,Id DESC);
  END;

  IF OBJECT_ID('fidelidad.MemberQrToken','U') IS NULL
  BEGIN
    CREATE TABLE fidelidad.MemberQrToken
    (
      Id uniqueidentifier NOT NULL CONSTRAINT PK_MemberQrToken PRIMARY KEY,
      Rfc varchar(50) NOT NULL,
      MemberId uniqueidentifier NOT NULL,
      TokenHash char(64) NOT NULL,
      ExpiresAt datetime2(0) NOT NULL,
      UsedAt datetime2(0) NULL,
      CreatedAt datetime2(0) NOT NULL CONSTRAINT DF_MemberQrToken_Created DEFAULT SYSUTCDATETIME(),
      CONSTRAINT FK_MemberQrToken_Member FOREIGN KEY(Rfc,MemberId) REFERENCES fidelidad.MemberAccount(Rfc,Id),
      CONSTRAINT UX_MemberQrToken_Hash UNIQUE(Rfc,TokenHash)
    );
    CREATE INDEX IX_MemberQrToken_Expiry ON fidelidad.MemberQrToken(Rfc,ExpiresAt);
  END;

  IF OBJECT_ID('fidelidad.MemberClosureRequest','U') IS NULL
  BEGIN
    CREATE TABLE fidelidad.MemberClosureRequest
    (
      Id uniqueidentifier NOT NULL CONSTRAINT PK_MemberClosureRequest PRIMARY KEY,
      Rfc varchar(50) NOT NULL,
      MemberId uniqueidentifier NOT NULL,
      Reason nvarchar(500) NOT NULL,
      [Status] varchar(30) NOT NULL,
      RequestedAt datetime2(0) NOT NULL CONSTRAINT DF_MemberClosure_Requested DEFAULT SYSUTCDATETIME(),
      CompletedAt datetime2(0) NULL,
      CompletedBy nvarchar(256) NULL,
      CONSTRAINT FK_MemberClosure_Member FOREIGN KEY(Rfc,MemberId) REFERENCES fidelidad.MemberAccount(Rfc,Id)
    );
    CREATE INDEX IX_MemberClosure_Status ON fidelidad.MemberClosureRequest(Rfc,[Status],RequestedAt);
  END;

  IF OBJECT_ID('fidelidad.ProgramSettings','U') IS NULL
  BEGIN
    CREATE TABLE fidelidad.ProgramSettings
    (
      Rfc varchar(50) NOT NULL CONSTRAINT PK_LoyaltyProgramSettings PRIMARY KEY,
      PesosPerPoint decimal(18,2) NOT NULL,
      IsAccrualEnabled bit NOT NULL,
      PointsExpire bit NOT NULL,
      UpdatedAt datetime2(0) NOT NULL CONSTRAINT DF_LoyaltySettings_Updated DEFAULT SYSUTCDATETIME(),
      UpdatedBy nvarchar(256) NULL,
      CONSTRAINT CK_LoyaltySettings_Rate CHECK(PesosPerPoint > 0)
    );
  END;

  /* Promociones */
  IF OBJECT_ID('restaurante.Promotion','U') IS NULL
  BEGIN
    CREATE TABLE restaurante.Promotion
    (
      Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_RestaurantPromotion PRIMARY KEY,
      Rfc varchar(50) NOT NULL,
      SiteId int NULL,
      [Name] nvarchar(160) NOT NULL,
      PublicDescription nvarchar(800) NOT NULL,
      PublicTerms nvarchar(2000) NOT NULL,
      [Status] varchar(30) NOT NULL,
      RuleType varchar(30) NOT NULL,
      Priority int NOT NULL CONSTRAINT DF_Promotion_Priority DEFAULT 0,
      ValidFromLocal datetime2(0) NULL,
      ValidToLocal datetime2(0) NULL,
      PosEnabled bit NOT NULL,
      WebEnabled bit NOT NULL,
      MemberOnly bit NOT NULL,
      CodeRequired bit NOT NULL,
      IsCombinable bit NOT NULL,
      IsPublic bit NOT NULL,
      BuyQuantity decimal(18,4) NOT NULL CONSTRAINT DF_Promotion_Buy DEFAULT 0,
      PayQuantity decimal(18,4) NOT NULL CONSTRAINT DF_Promotion_Pay DEFAULT 0,
      PercentOff decimal(9,4) NOT NULL CONSTRAINT DF_Promotion_Percent DEFAULT 0,
      FixedAmount decimal(18,2) NOT NULL CONSTRAINT DF_Promotion_Fixed DEFAULT 0,
      BundlePrice decimal(18,2) NOT NULL CONSTRAINT DF_Promotion_Bundle DEFAULT 0,
      MinimumQuantity decimal(18,4) NOT NULL CONSTRAINT DF_Promotion_MinQty DEFAULT 0,
      MinimumSubtotal decimal(18,2) NOT NULL CONSTRAINT DF_Promotion_MinSubtotal DEFAULT 0,
      GlobalLimit int NULL,
      RedemptionCount int NOT NULL CONSTRAINT DF_Promotion_Redemptions DEFAULT 0,
      CreatedAt datetime2(0) NOT NULL CONSTRAINT DF_Promotion_Created DEFAULT SYSUTCDATETIME(),
      CreatedBy nvarchar(256) NULL,
      UpdatedAt datetime2(0) NOT NULL CONSTRAINT DF_Promotion_Updated DEFAULT SYSUTCDATETIME(),
      UpdatedBy nvarchar(256) NULL,
      RowVersion rowversion NOT NULL,
      CONSTRAINT FK_Promotion_Site FOREIGN KEY(Rfc,SiteId) REFERENCES restaurante.Site(Rfc,Id),
      CONSTRAINT CK_Promotion_Status CHECK([Status] IN('Draft','Scheduled','Active','Paused','Expired')),
      CONSTRAINT CK_Promotion_RuleType CHECK(RuleType IN('BuyXPayY','PercentOff','FixedAmountOff','FixedBundlePrice')),
      CONSTRAINT CK_Promotion_Validity CHECK(ValidToLocal IS NULL OR ValidFromLocal IS NULL OR ValidToLocal>ValidFromLocal),
      CONSTRAINT CK_Promotion_Limit CHECK(GlobalLimit IS NULL OR GlobalLimit>0)
    );
    CREATE UNIQUE INDEX UX_Promotion_RfcId ON restaurante.Promotion(Rfc,Id);
    CREATE INDEX IX_Promotion_Eligibility ON restaurante.Promotion(Rfc,SiteId,[Status],ValidFromLocal,ValidToLocal,Priority);
  END;

  IF OBJECT_ID('restaurante.PromotionSchedule','U') IS NULL
  BEGIN
    CREATE TABLE restaurante.PromotionSchedule
    (
      Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_PromotionSchedule PRIMARY KEY,
      Rfc varchar(50) NOT NULL,
      PromotionId bigint NOT NULL,
      DayOfWeek tinyint NOT NULL,
      StartsAt time(0) NOT NULL,
      EndsAt time(0) NOT NULL,
      CONSTRAINT FK_PromotionSchedule_Promotion FOREIGN KEY(Rfc,PromotionId) REFERENCES restaurante.Promotion(Rfc,Id) ON DELETE CASCADE,
      CONSTRAINT CK_PromotionSchedule_Day CHECK(DayOfWeek BETWEEN 0 AND 6),
      CONSTRAINT UX_PromotionSchedule UNIQUE(Rfc,PromotionId,DayOfWeek,StartsAt,EndsAt)
    );
  END;

  IF OBJECT_ID('restaurante.PromotionProduct','U') IS NULL
  BEGIN
    CREATE TABLE restaurante.PromotionProduct
    (
      Rfc varchar(50) NOT NULL,
      PromotionId bigint NOT NULL,
      ProductId bigint NOT NULL,
      CONSTRAINT PK_PromotionProduct PRIMARY KEY(Rfc,PromotionId,ProductId),
      CONSTRAINT FK_PromotionProduct_Promotion FOREIGN KEY(Rfc,PromotionId) REFERENCES restaurante.Promotion(Rfc,Id) ON DELETE CASCADE,
      CONSTRAINT FK_PromotionProduct_Product FOREIGN KEY(Rfc,ProductId) REFERENCES restaurante.Product(Rfc,Id)
    );
  END;

  IF OBJECT_ID('restaurante.PromotionMaterialCategory','U') IS NULL
  BEGIN
    CREATE TABLE restaurante.PromotionMaterialCategory
    (
      Rfc varchar(50) NOT NULL,
      PromotionId bigint NOT NULL,
      MaterialCategoryId int NOT NULL,
      CONSTRAINT PK_PromotionMaterialCategory PRIMARY KEY(Rfc,PromotionId,MaterialCategoryId),
      CONSTRAINT FK_PromotionCategory_Promotion FOREIGN KEY(Rfc,PromotionId) REFERENCES restaurante.Promotion(Rfc,Id) ON DELETE CASCADE,
      CONSTRAINT FK_PromotionCategory_Category FOREIGN KEY(Rfc,MaterialCategoryId) REFERENCES logistica.MaterialCategory(Rfc,Id)
    );
  END;

  IF OBJECT_ID('restaurante.PromotionCode','U') IS NULL
  BEGIN
    CREATE TABLE restaurante.PromotionCode
    (
      Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_PromotionCode PRIMARY KEY,
      Rfc varchar(50) NOT NULL,
      PromotionId bigint NOT NULL,
      Code varchar(32) NOT NULL,
      GlobalLimit int NULL,
      PerMemberLimit int NULL,
      RedemptionCount int NOT NULL CONSTRAINT DF_PromotionCode_Redemptions DEFAULT 0,
      IsActive bit NOT NULL,
      CONSTRAINT FK_PromotionCode_Promotion FOREIGN KEY(Rfc,PromotionId) REFERENCES restaurante.Promotion(Rfc,Id) ON DELETE CASCADE,
      CONSTRAINT CK_PromotionCode_Limits CHECK((GlobalLimit IS NULL OR GlobalLimit>0) AND (PerMemberLimit IS NULL OR PerMemberLimit>0)),
      CONSTRAINT UX_PromotionCode_RfcCode UNIQUE(Rfc,Code)
    );
    CREATE UNIQUE INDEX UX_PromotionCode_RfcId ON restaurante.PromotionCode(Rfc,Id);
  END;

  IF OBJECT_ID('restaurante.OrderPromotion','U') IS NULL
  BEGIN
    CREATE TABLE restaurante.OrderPromotion
    (
      Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_OrderPromotion PRIMARY KEY,
      Rfc varchar(50) NOT NULL,
      OrderId uniqueidentifier NOT NULL,
      PromotionId bigint NOT NULL,
      PromotionNameSnapshot nvarchar(160) NOT NULL,
      RuleTypeSnapshot varchar(30) NOT NULL,
      CodeId bigint NULL,
      CodeSnapshot varchar(32) NULL,
      DiscountAmount decimal(18,2) NOT NULL,
      CreatedAt datetime2(0) NOT NULL CONSTRAINT DF_OrderPromotion_Created DEFAULT SYSUTCDATETIME(),
      CONSTRAINT FK_OrderPromotion_Order FOREIGN KEY(Rfc,OrderId) REFERENCES restaurante.[Order](Rfc,Id),
      CONSTRAINT FK_OrderPromotion_Promotion FOREIGN KEY(Rfc,PromotionId) REFERENCES restaurante.Promotion(Rfc,Id),
      CONSTRAINT FK_OrderPromotion_Code FOREIGN KEY(Rfc,CodeId) REFERENCES restaurante.PromotionCode(Rfc,Id),
      CONSTRAINT CK_OrderPromotion_Discount CHECK(DiscountAmount>0)
    );
    CREATE UNIQUE INDEX UX_OrderPromotion_RfcId ON restaurante.OrderPromotion(Rfc,Id);
    CREATE INDEX IX_OrderPromotion_Report ON restaurante.OrderPromotion(Rfc,PromotionId,CreatedAt,OrderId);
  END;

  IF OBJECT_ID('restaurante.OrderLinePromotion','U') IS NULL
  BEGIN
    CREATE TABLE restaurante.OrderLinePromotion
    (
      Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_OrderLinePromotion PRIMARY KEY,
      Rfc varchar(50) NOT NULL,
      OrderPromotionId bigint NOT NULL,
      OrderLineId bigint NOT NULL,
      AppliedQuantity decimal(18,4) NOT NULL,
      DiscountAmount decimal(18,2) NOT NULL,
      CONSTRAINT FK_OrderLinePromotion_OrderPromotion FOREIGN KEY(Rfc,OrderPromotionId) REFERENCES restaurante.OrderPromotion(Rfc,Id),
      CONSTRAINT FK_OrderLinePromotion_OrderLine FOREIGN KEY(Rfc,OrderLineId) REFERENCES restaurante.OrderLine(Rfc,Id),
      CONSTRAINT CK_OrderLinePromotion_Amounts CHECK(AppliedQuantity>0 AND DiscountAmount>0)
    );
  END;

  IF OBJECT_ID('restaurante.PromotionRedemption','U') IS NULL
  BEGIN
    CREATE TABLE restaurante.PromotionRedemption
    (
      Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_PromotionRedemption PRIMARY KEY,
      Rfc varchar(50) NOT NULL,
      PromotionId bigint NOT NULL,
      CodeId bigint NULL,
      OrderId uniqueidentifier NOT NULL,
      MemberId uniqueidentifier NULL,
      DiscountAmount decimal(18,2) NOT NULL,
      RedeemedAt datetime2(0) NOT NULL CONSTRAINT DF_PromotionRedemption_At DEFAULT SYSUTCDATETIME(),
      CONSTRAINT FK_PromotionRedemption_Promotion FOREIGN KEY(Rfc,PromotionId) REFERENCES restaurante.Promotion(Rfc,Id),
      CONSTRAINT FK_PromotionRedemption_Code FOREIGN KEY(Rfc,CodeId) REFERENCES restaurante.PromotionCode(Rfc,Id),
      CONSTRAINT FK_PromotionRedemption_Order FOREIGN KEY(Rfc,OrderId) REFERENCES restaurante.[Order](Rfc,Id),
      CONSTRAINT FK_PromotionRedemption_Member FOREIGN KEY(Rfc,MemberId) REFERENCES fidelidad.MemberAccount(Rfc,Id),
      CONSTRAINT UX_PromotionRedemption_OrderPromotion UNIQUE(Rfc,OrderId,PromotionId)
    );
    CREATE INDEX IX_PromotionRedemption_Member ON restaurante.PromotionRedemption(Rfc,PromotionId,MemberId,RedeemedAt);
  END;

  IF OBJECT_ID('restaurante.PublicSiteSettings','U') IS NULL
  BEGIN
    CREATE TABLE restaurante.PublicSiteSettings
    (
      Rfc varchar(50) NOT NULL,
      SiteId int NOT NULL,
      LegalName nvarchar(200) NOT NULL,
      PublicName nvarchar(160) NOT NULL,
      HeroEyebrow nvarchar(160) NOT NULL,
      HeroTitle nvarchar(240) NOT NULL,
      HeroDescription nvarchar(800) NOT NULL,
      AddressLine nvarchar(300) NOT NULL,
      Neighborhood nvarchar(160) NOT NULL,
      PostalCode varchar(10) NOT NULL,
      City nvarchar(120) NOT NULL,
      StateName nvarchar(120) NOT NULL,
      CountryName nvarchar(120) NOT NULL,
      WhatsAppPhone varchar(30) NOT NULL,
      WhatsAppDisplay nvarchar(40) NOT NULL,
      MapsUrl nvarchar(1000) NOT NULL,
      FacebookUrl nvarchar(1000) NULL,
      InstagramUrl nvarchar(1000) NULL,
      TikTokUrl nvarchar(1000) NULL,
      OpeningHoursJson nvarchar(2000) NOT NULL,
      SeoDescription nvarchar(500) NOT NULL,
      IsWebsiteEnabled bit NOT NULL CONSTRAINT DF_PublicSite_WebsiteEnabled DEFAULT 0,
      IsMembershipEnabled bit NOT NULL CONSTRAINT DF_PublicSite_MembershipEnabled DEFAULT 0,
      IsLoyaltyAccrualEnabled bit NOT NULL CONSTRAINT DF_PublicSite_LoyaltyEnabled DEFAULT 0,
      IsPromotionsEnabled bit NOT NULL CONSTRAINT DF_PublicSite_PromotionsEnabled DEFAULT 0,
      UpdatedAt datetime2(0) NOT NULL CONSTRAINT DF_PublicSite_Updated DEFAULT SYSUTCDATETIME(),
      UpdatedBy nvarchar(256) NULL,
      RowVersion rowversion NOT NULL,
      CONSTRAINT PK_PublicSiteSettings PRIMARY KEY(Rfc,SiteId),
      CONSTRAINT FK_PublicSiteSettings_Site FOREIGN KEY(Rfc,SiteId) REFERENCES restaurante.Site(Rfc,Id),
      CONSTRAINT CK_PublicSite_HoursJson CHECK(ISJSON(OpeningHoursJson)=1)
    );
  END;

  IF OBJECT_ID('restaurante.ProductDietaryTag','U') IS NULL
  BEGIN
    CREATE TABLE restaurante.ProductDietaryTag
    (
      Rfc varchar(50) NOT NULL,
      ProductId bigint NOT NULL,
      Tag nvarchar(60) NOT NULL,
      CONSTRAINT PK_ProductDietaryTag PRIMARY KEY(Rfc,ProductId,Tag),
      CONSTRAINT FK_ProductDietaryTag_Product FOREIGN KEY(Rfc,ProductId)
        REFERENCES restaurante.Product(Rfc,Id) ON DELETE CASCADE
    );
  END;

  IF COL_LENGTH('restaurante.Order','MemberId') IS NULL
    ALTER TABLE restaurante.[Order] ADD MemberId uniqueidentifier NULL;
  IF COL_LENGTH('restaurante.Order','MembershipNumberSnapshot') IS NULL
    ALTER TABLE restaurante.[Order] ADD MembershipNumberSnapshot varchar(20) NULL;
  IF COL_LENGTH('restaurante.Order','PromotionDiscountTotal') IS NULL
    ALTER TABLE restaurante.[Order] ADD PromotionDiscountTotal decimal(18,2) NOT NULL
      CONSTRAINT DF_RestaurantOrder_PromotionDiscount DEFAULT 0 WITH VALUES;
  IF COL_LENGTH('restaurante.Order','EligibleMerchandiseTotal') IS NULL
    ALTER TABLE restaurante.[Order] ADD EligibleMerchandiseTotal decimal(18,2) NOT NULL
      CONSTRAINT DF_RestaurantOrder_EligibleMerchandise DEFAULT 0 WITH VALUES;
  IF COL_LENGTH('restaurante.Order','PointsEarned') IS NULL
    ALTER TABLE restaurante.[Order] ADD PointsEarned int NOT NULL
      CONSTRAINT DF_RestaurantOrder_Points DEFAULT 0 WITH VALUES;

  IF NOT EXISTS
  (
    SELECT 1 FROM sys.foreign_keys
    WHERE parent_object_id=OBJECT_ID('restaurante.[Order]')
      AND [name]='FK_RestaurantOrder_LoyaltyMember'
  )
    ALTER TABLE restaurante.[Order] WITH CHECK ADD CONSTRAINT FK_RestaurantOrder_LoyaltyMember
      FOREIGN KEY(Rfc,MemberId) REFERENCES fidelidad.MemberAccount(Rfc,Id);

  IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID('restaurante.[Order]') AND [name]='IX_RestaurantOrder_Member')
    CREATE INDEX IX_RestaurantOrder_Member ON restaurante.[Order](Rfc,MemberId,PaidAt);

  /* Datos operativos iniciales, siempre deshabilitados para despliegue gradual. */
  IF NOT EXISTS(SELECT 1 FROM fidelidad.ProgramSettings WHERE Rfc=@BrunoRfc)
    INSERT fidelidad.ProgramSettings(Rfc,PesosPerPoint,IsAccrualEnabled,PointsExpire,UpdatedBy)
    VALUES(@BrunoRfc,10,0,0,N'20260730_bruno_promotions_loyalty');

  DECLARE @BrunoSiteId int =
    (SELECT Id FROM restaurante.Site WHERE Rfc=@BrunoRfc AND SiteCode='BRUNOS-01');

  IF NOT EXISTS(SELECT 1 FROM restaurante.PublicSiteSettings WHERE Rfc=@BrunoRfc AND SiteId=@BrunoSiteId)
    INSERT restaurante.PublicSiteSettings
    (
      Rfc,SiteId,LegalName,PublicName,HeroEyebrow,HeroTitle,HeroDescription,
      AddressLine,Neighborhood,PostalCode,City,StateName,CountryName,
      WhatsAppPhone,WhatsAppDisplay,MapsUrl,FacebookUrl,OpeningHoursJson,
      SeoDescription,UpdatedBy
    )
    VALUES
    (
      @BrunoRfc,@BrunoSiteId,N'Bruno''s Garden & Snacks S.A. de C.V.',
      N'Bruno''s Garden & Snacks',N'Jardín pet friendly en Calpulalpan',
      N'Snacks, jardín y buenos momentos',
      N'Un espacio familiar para disfrutar chicken fingers, hamburguesas, bebidas y tiempo con tu mascota.',
      N'Calle Camino Nacional #5',N'Colonia Francisco Sarabia','90207',
      N'Calpulalpan',N'Tlaxcala',N'México','527491103026',N'+52 749 110 3026',
      N'https://maps.app.goo.gl/tC4YHTcM2ED13MwC8',
      N'https://www.facebook.com/profile.php?id=61592059341008',
      N'{"Monday":[],"Tuesday":[{"opens":"09:00","closes":"22:00"}],"Wednesday":[{"opens":"09:00","closes":"22:00"}],"Thursday":[{"opens":"09:00","closes":"22:00"}],"Friday":[{"opens":"09:00","closes":"22:00"}],"Saturday":[{"opens":"09:00","closes":"22:00"}],"Sunday":[{"opens":"09:00","closes":"22:00"}]}',
      N'Bruno''s Garden & Snacks: jardín pet friendly, ambiente familiar, chicken fingers, hamburguesas, snacks y bebidas en Calpulalpan.',
      N'20260730_bruno_promotions_loyalty'
    );

  DECLARE @ChilaquilesProductId bigint =
    (SELECT TOP(1) Id FROM restaurante.Product WHERE Rfc=@BrunoRfc AND Sku='BRUNOS-CHIL');
  DECLARE @ChickenFingersProductId bigint =
    (SELECT TOP(1) Id FROM restaurante.Product WHERE Rfc=@BrunoRfc AND Sku='BR-CF');

  IF @ChilaquilesProductId IS NULL OR @ChickenFingersProductId IS NULL
    THROW 51206, 'No se encontraron los SKU esperados para crear las promociones borrador.', 1;

  IF NOT EXISTS(SELECT 1 FROM restaurante.Promotion WHERE Rfc=@BrunoRfc AND [Name]=N'Chilaquiles 2x1 · martes y miércoles')
  BEGIN
    INSERT restaurante.Promotion
    (
      Rfc,SiteId,[Name],PublicDescription,PublicTerms,[Status],RuleType,Priority,
      PosEnabled,WebEnabled,MemberOnly,CodeRequired,IsCombinable,IsPublic,
      BuyQuantity,PayQuantity,CreatedBy,UpdatedBy
    )
    VALUES
    (
      @BrunoRfc,@BrunoSiteId,N'Chilaquiles 2x1 · martes y miércoles',
      N'Compra dos chilaquiles y paga uno.',
      N'Promoción en borrador. Al publicarse aplicará martes y miércoles de 10:00 a 12:00, sujeta a disponibilidad y no acumulable sobre las mismas unidades.',
      'Draft','BuyXPayY',100,1,1,0,0,0,1,2,1,
      N'20260730_bruno_promotions_loyalty',N'20260730_bruno_promotions_loyalty'
    );
    DECLARE @ChilaquilesPromotionId bigint=SCOPE_IDENTITY();
    INSERT restaurante.PromotionProduct(Rfc,PromotionId,ProductId)
      VALUES(@BrunoRfc,@ChilaquilesPromotionId,@ChilaquilesProductId);
    INSERT restaurante.PromotionSchedule(Rfc,PromotionId,DayOfWeek,StartsAt,EndsAt)
      VALUES(@BrunoRfc,@ChilaquilesPromotionId,2,'10:00','12:00'),
            (@BrunoRfc,@ChilaquilesPromotionId,3,'10:00','12:00');
  END;

  IF NOT EXISTS(SELECT 1 FROM restaurante.Promotion WHERE Rfc=@BrunoRfc AND [Name]=N'Chicken fingers 3x1')
  BEGIN
    INSERT restaurante.Promotion
    (
      Rfc,SiteId,[Name],PublicDescription,PublicTerms,[Status],RuleType,Priority,
      PosEnabled,WebEnabled,MemberOnly,CodeRequired,IsCombinable,IsPublic,
      BuyQuantity,PayQuantity,CreatedBy,UpdatedBy
    )
    VALUES
    (
      @BrunoRfc,@BrunoSiteId,N'Chicken fingers 3x1',
      N'Compra tres órdenes elegibles de chicken fingers y paga una.',
      N'Promoción en borrador. Requiere definir vigencia y horario antes de publicarse. Sujeta a disponibilidad y no acumulable sobre las mismas unidades.',
      'Draft','BuyXPayY',90,1,1,0,0,0,1,3,1,
      N'20260730_bruno_promotions_loyalty',N'20260730_bruno_promotions_loyalty'
    );
    DECLARE @ChickenPromotionId bigint=SCOPE_IDENTITY();
    INSERT restaurante.PromotionProduct(Rfc,PromotionId,ProductId)
      VALUES(@BrunoRfc,@ChickenPromotionId,@ChickenFingersProductId);
  END;

  /* Incorporar tablas nuevas a la política RLS compartida. */
  IF EXISTS
  (
    SELECT 1 FROM sys.security_policies
    WHERE [name]='RfcSecurityPolicy' AND schema_id=SCHEMA_ID('logistica')
  )
  BEGIN
    DECLARE @RlsSchema sysname;
    DECLARE @RlsTable sysname;
    DECLARE @RlsSql nvarchar(max);
    DECLARE RlsCursor CURSOR LOCAL FAST_FORWARD FOR
    SELECT schemaInfo.[name],tableInfo.[name]
    FROM sys.tables tableInfo
    JOIN sys.schemas schemaInfo ON schemaInfo.schema_id=tableInfo.schema_id
    WHERE schemaInfo.[name] IN('restaurante','fidelidad')
      AND EXISTS
      (
        SELECT 1 FROM sys.columns columnInfo
        WHERE columnInfo.object_id=tableInfo.object_id AND columnInfo.[name]='Rfc'
      )
      AND NOT EXISTS
      (
        SELECT 1 FROM sys.security_predicates predicateInfo
        WHERE predicateInfo.object_id=OBJECT_ID('logistica.RfcSecurityPolicy')
          AND predicateInfo.target_object_id=tableInfo.object_id
      );

    OPEN RlsCursor;
    FETCH NEXT FROM RlsCursor INTO @RlsSchema,@RlsTable;
    WHILE @@FETCH_STATUS=0
    BEGIN
      SET @RlsSql=N'ALTER SECURITY POLICY logistica.RfcSecurityPolicy
        ADD FILTER PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON '+QUOTENAME(@RlsSchema)+N'.'+QUOTENAME(@RlsTable)+N',
        ADD BLOCK PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON '+QUOTENAME(@RlsSchema)+N'.'+QUOTENAME(@RlsTable)+N' AFTER INSERT,
        ADD BLOCK PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON '+QUOTENAME(@RlsSchema)+N'.'+QUOTENAME(@RlsTable)+N' AFTER UPDATE;';
      EXEC sys.sp_executesql @RlsSql;
      FETCH NEXT FROM RlsCursor INTO @RlsSchema,@RlsTable;
    END;
    CLOSE RlsCursor;
    DEALLOCATE RlsCursor;
  END;

  IF NOT EXISTS(SELECT 1 FROM restaurante.Promotion WHERE Rfc=@BrunoRfc AND [Status]='Draft')
    THROW 51207, 'No se crearon o conservaron promociones borrador.', 1;
  IF NOT EXISTS(SELECT 1 FROM fidelidad.ProgramSettings WHERE Rfc=@BrunoRfc AND PesosPerPoint=10)
    THROW 51208, 'No quedó configurada la tasa inicial de fidelidad.', 1;
  IF NOT EXISTS(SELECT 1 FROM restaurante.PublicSiteSettings WHERE Rfc=@BrunoRfc AND SiteId=@BrunoSiteId)
    THROW 51209, 'No quedó configurado el contenido inicial del sitio.', 1;

  SELECT
    DB_NAME() AS DatabaseName,
    @ApplyChanges AS ApplyChanges,
    (SELECT COUNT(*) FROM restaurante.Promotion WHERE Rfc=@BrunoRfc) AS PromotionCount,
    (SELECT COUNT(*) FROM fidelidad.MemberAccount WHERE Rfc=@BrunoRfc) AS MemberCount,
    (SELECT IsWebsiteEnabled FROM restaurante.PublicSiteSettings WHERE Rfc=@BrunoRfc AND SiteId=@BrunoSiteId) AS WebsiteEnabled,
    (SELECT IsAccrualEnabled FROM fidelidad.ProgramSettings WHERE Rfc=@BrunoRfc) AS LoyaltyEnabled;

  IF @ApplyChanges=1
    COMMIT TRANSACTION;
  ELSE
  BEGIN
    ROLLBACK TRANSACTION;
    PRINT 'SIMULACIÓN COMPLETA: todos los cambios fueron revertidos.';
  END;
END TRY
BEGIN CATCH
  IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
  THROW;
END CATCH;
