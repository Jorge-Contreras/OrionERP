/* Deterministic, data-only acceptance fixture for a dual-module company in Orion_Sandbox. */
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
DECLARE @AppVersion nvarchar(64)=CASE WHEN @AppVersionInput LIKE N'$'+N'(%' THEN NULL ELSE NULLIF(LTRIM(RTRIM(@AppVersionInput)),N'') END;

IF @ApplyInput NOT LIKE N'$'+N'(%'
BEGIN
  IF @ApplyInput NOT IN(N'0',N'1') THROW 52900,'ApplyChanges debe ser 0 o 1.',1;
  SET @ApplyChanges=CONVERT(bit,@ApplyInput);
END;
IF @ExpectedDatabase LIKE N'$'+N'(%' OR @ExpectedDatabase<>N'Orion_Sandbox'
  THROW 52901,'Este fixture solo puede ejecutarse en Orion_Sandbox.',1;
IF DB_NAME()<>N'Orion_Sandbox' OR DB_NAME()<>@ExpectedDatabase
  THROW 52902,'La conexión no apunta a Orion_Sandbox.',1;
IF @MigrationId<>N'20260911_synthetic_dual_company' THROW 52903,'MigrationId incorrecto.',1;
IF @MigrationChecksum LIKE '$'+'(%' OR LEN(@MigrationChecksum)<>64
   OR @MigrationChecksum LIKE '%[^0-9A-F]%' COLLATE Latin1_General_100_BIN2
  THROW 52904,'MigrationChecksum inválido.',1;
IF OBJECT_ID(N'orion.SchemaMigration',N'U') IS NULL
   OR OBJECT_ID(N'orion.ApplyPublicPermissionProfile',N'P') IS NULL
   OR OBJECT_ID(N'orion.SetPublicIdentityBridgeMode',N'P') IS NULL
   OR OBJECT_ID(N'public_identity.AspNetUsers',N'U') IS NULL
   OR OBJECT_ID(N'orion.PublicSqlPrincipalBinding',N'U') IS NULL
  THROW 52905,'Faltan las migraciones neutrales requeridas.',1;

DECLARE @ExistingChecksum char(64)=(SELECT Checksum FROM orion.SchemaMigration WHERE MigrationId=@MigrationId);
IF @ExistingChecksum IS NOT NULL AND UPPER(@ExistingChecksum)<>UPPER(@MigrationChecksum)
  THROW 52906,'El MigrationId ya existe con otro checksum.',1;
IF @ExistingChecksum IS NOT NULL
BEGIN
  SELECT N'YA_APLICADO' Estado,@MigrationId MigrationId,@ExistingChecksum Checksum;
  RETURN;
END;

BEGIN TRY
  BEGIN TRANSACTION;
  DECLARE @LockResult int;
  EXEC @LockResult=sys.sp_getapplock
    @Resource=N'OrionERP:Platform:SyntheticDual:20260911',
    @LockMode=N'Exclusive',@LockOwner=N'Transaction',@LockTimeout=15000;
  IF @LockResult<0 THROW 52907,'No fue posible obtener el candado del fixture.',1;

  DECLARE @Rfc varchar(50)='TST260910DUAL01';
  DECLARE @OperationId uniqueidentifier='26091000-0000-0000-0000-000000000001';
  DECLARE @HospitalityKey varchar(100)='synthetic-hospitality-main';
  DECLARE @RestaurantKey varchar(100)='synthetic-restaurant-main';
  DECLARE @HospitalityPrincipal sysname=N'orion_public_synthetic_h_01';
  DECLARE @RestaurantPrincipal sysname=N'orion_public_synthetic_r_01';

  IF EXISTS(SELECT 1 FROM orion.Company WHERE LegacyTenantKey='synthetic-dual-01' AND Rfc<>@Rfc)
    THROW 52908,'LegacyTenantKey sintético pertenece a otra empresa.',1;
  IF EXISTS(SELECT 1 FROM orion.Company WHERE Rfc=@Rfc AND ISNULL(LegacyTenantKey,'') NOT IN('','synthetic-dual-01'))
    THROW 52909,'El RFC sintético pertenece a otro tenant.',1;

  IF NOT EXISTS(SELECT 1 FROM orion.Company WHERE Rfc=@Rfc)
    INSERT orion.Company(Rfc,DisplayName,LegalName,IsActive,TaxRfc,LegacyTenantKey,UpdatedBy)
    VALUES(@Rfc,N'Empresa dual sintética',N'Empresa dual sintética de validación',1,NULL,'synthetic-dual-01',N'20260911_synthetic_dual_company');
  ELSE
    UPDATE orion.Company SET DisplayName=N'Empresa dual sintética',LegalName=N'Empresa dual sintética de validación',
      IsActive=1,TaxRfc=NULL,LegacyTenantKey='synthetic-dual-01',UpdatedAtUtc=SYSUTCDATETIME(),
      UpdatedBy=N'20260911_synthetic_dual_company' WHERE Rfc=@Rfc;

  DECLARE @CompanyId bigint=(SELECT CompanyId FROM orion.Company WHERE Rfc=@Rfc);
  IF @CompanyId IS NULL THROW 52910,'No se resolvió CompanyId.',1;

  UPDATE orion.Site SET DisplayName=N'Campus dual',TimeZoneId=N'Central Standard Time (Mexico)',IsActive=1,
    UpdatedAtUtc=SYSUTCDATETIME(),UpdatedBy=N'20260911_synthetic_dual_company'
  WHERE CompanyId=@CompanyId AND SiteKey='dual-campus';
  IF @@ROWCOUNT=0
    INSERT orion.Site(CompanyId,SiteKey,DisplayName,TimeZoneId,IsActive,UpdatedBy)
    VALUES(@CompanyId,'dual-campus',N'Campus dual',N'Central Standard Time (Mexico)',1,N'20260911_synthetic_dual_company');

  UPDATE orion.Site SET DisplayName=N'Control dual',TimeZoneId=N'Central Standard Time (Mexico)',IsActive=1,
    UpdatedAtUtc=SYSUTCDATETIME(),UpdatedBy=N'20260911_synthetic_dual_company'
  WHERE CompanyId=@CompanyId AND SiteKey='dual-control';
  IF @@ROWCOUNT=0
    INSERT orion.Site(CompanyId,SiteKey,DisplayName,TimeZoneId,IsActive,UpdatedBy)
    VALUES(@CompanyId,'dual-control',N'Control dual',N'Central Standard Time (Mexico)',1,N'20260911_synthetic_dual_company');

  DECLARE @CampusSiteId bigint=(SELECT SiteId FROM orion.Site WHERE CompanyId=@CompanyId AND SiteKey='dual-campus');
  DECLARE @ControlSiteId bigint=(SELECT SiteId FROM orion.Site WHERE CompanyId=@CompanyId AND SiteKey='dual-control');
  IF @CampusSiteId IS NULL OR @ControlSiteId IS NULL THROW 52911,'No se resolvieron las sedes sintéticas.',1;
  IF NOT EXISTS(SELECT 1 FROM orion.Module WHERE ModuleCode='HOSPITALITY' AND IsActive=1)
     OR NOT EXISTS(SELECT 1 FROM orion.Module WHERE ModuleCode='RESTAURANT' AND IsActive=1)
    THROW 52912,'El catálogo de módulos públicos está incompleto.',1;

  MERGE orion.CompanyModule AS target
  USING(VALUES(@CompanyId,'HOSPITALITY'),(@CompanyId,'RESTAURANT')) source(CompanyId,ModuleCode)
    ON target.CompanyId=source.CompanyId AND target.ModuleCode=source.ModuleCode
  WHEN MATCHED THEN UPDATE SET [Status]='Enabled',EffectiveFromUtc=NULL,EffectiveToUtc=NULL,
    ConfigurationVersion=target.ConfigurationVersion+1,UpdatedAtUtc=SYSUTCDATETIME(),UpdatedBy=N'20260911_synthetic_dual_company'
  WHEN NOT MATCHED THEN INSERT(CompanyId,ModuleCode,[Status],UpdatedBy)
    VALUES(source.CompanyId,source.ModuleCode,'Enabled',N'20260911_synthetic_dual_company');

  MERGE orion.SiteCapability AS target
  USING(VALUES
    (@CompanyId,@CampusSiteId,'HOSPITALITY',CONVERT(bit,1)),
    (@CompanyId,@CampusSiteId,'RESTAURANT',CONVERT(bit,1)),
    (@CompanyId,@ControlSiteId,'HOSPITALITY',CONVERT(bit,0)),
    (@CompanyId,@ControlSiteId,'RESTAURANT',CONVERT(bit,0))
  ) source(CompanyId,SiteId,ModuleCode,IsEnabled)
    ON target.CompanyId=source.CompanyId AND target.SiteId=source.SiteId AND target.ModuleCode=source.ModuleCode
  WHEN MATCHED THEN UPDATE SET IsEnabled=source.IsEnabled,UpdatedAtUtc=SYSUTCDATETIME(),UpdatedBy=N'20260911_synthetic_dual_company'
  WHEN NOT MATCHED THEN INSERT(CompanyId,SiteId,ModuleCode,IsEnabled,UpdatedBy)
    VALUES(source.CompanyId,source.SiteId,source.ModuleCode,source.IsEnabled,N'20260911_synthetic_dual_company');

  MERGE restaurante.Site AS target
  USING(VALUES
    (@Rfc,'dual-campus','Campus dual',CONVERT(bit,1),@CompanyId,@CampusSiteId),
    (@Rfc,'dual-control','Control dual',CONVERT(bit,0),@CompanyId,@ControlSiteId)
  ) source(Rfc,SiteCode,[Name],IsEnabled,OrionCompanyId,OrionSiteId)
    ON target.OrionCompanyId=source.OrionCompanyId AND target.OrionSiteId=source.OrionSiteId
  WHEN MATCHED THEN UPDATE SET Rfc=source.Rfc,SiteCode=source.SiteCode,[Name]=source.[Name],
    TimeZoneId='Central Standard Time (Mexico)',IsEnabled=source.IsEnabled,UpdatedAt=SYSUTCDATETIME()
  WHEN NOT MATCHED THEN INSERT(Rfc,SiteCode,[Name],TimeZoneId,IsEnabled,OrionCompanyId,OrionSiteId)
    VALUES(source.Rfc,source.SiteCode,source.[Name],'Central Standard Time (Mexico)',source.IsEnabled,source.OrionCompanyId,source.OrionSiteId);

  IF EXISTS
  (
    SELECT 1 FROM orion.PublicSite
    WHERE PublicSiteKey IN(@HospitalityKey,@RestaurantKey) AND CompanyId<>@CompanyId
  ) THROW 52913,'Una clave pública sintética pertenece a otra empresa.',1;

  MERGE orion.PublicSite AS target
  USING(VALUES
    (@HospitalityKey,@CompanyId,@CampusSiteId,'HOSPITALITY','synthetic-hospitality-main.test'),
    (@RestaurantKey,@CompanyId,@CampusSiteId,'RESTAURANT','synthetic-restaurant-main.test')
  ) source(PublicSiteKey,CompanyId,SiteId,ModuleCode,CanonicalHost)
    ON target.PublicSiteKey=source.PublicSiteKey
  WHEN MATCHED THEN UPDATE SET CompanyId=source.CompanyId,SiteId=source.SiteId,ModuleCode=source.ModuleCode,
    CanonicalHost=source.CanonicalHost,IsActive=1,ConfigurationVersion=target.ConfigurationVersion+1,
    UpdatedAtUtc=SYSUTCDATETIME(),UpdatedBy=N'20260911_synthetic_dual_company'
  WHEN NOT MATCHED THEN INSERT(PublicSiteKey,CompanyId,SiteId,ModuleCode,CanonicalHost,IsActive,UpdatedBy)
    VALUES(source.PublicSiteKey,source.CompanyId,source.SiteId,source.ModuleCode,source.CanonicalHost,1,N'20260911_synthetic_dual_company');

  DECLARE @HospitalityPublicSiteId bigint=(SELECT PublicSiteId FROM orion.PublicSite WHERE PublicSiteKey=@HospitalityKey);
  DECLARE @RestaurantPublicSiteId bigint=(SELECT PublicSiteId FROM orion.PublicSite WHERE PublicSiteKey=@RestaurantKey);
  DECLARE @RestaurantLocalSiteId int=(SELECT Id FROM restaurante.Site WHERE OrionCompanyId=@CompanyId AND OrionSiteId=@CampusSiteId);

  IF NOT EXISTS(SELECT 1 FROM restaurante.PublicSiteSettings WHERE PublicSiteId=@RestaurantPublicSiteId)
    INSERT restaurante.PublicSiteSettings
    (PublicSiteId,Rfc,SiteId,LegalName,PublicName,HeroEyebrow,HeroTitle,HeroDescription,AddressLine,
     Neighborhood,PostalCode,City,StateName,CountryName,WhatsAppPhone,WhatsAppDisplay,MapsUrl,
     OpeningHoursJson,SeoDescription,IsWebsiteEnabled,IsMembershipEnabled,IsLoyaltyAccrualEnabled,
     IsPromotionsEnabled,UpdatedBy)
    VALUES(@RestaurantPublicSiteId,@Rfc,@RestaurantLocalSiteId,N'Empresa dual sintética de validación',
      N'Restaurante sintético',N'Validación dual',N'Experiencia de restaurante sintética',
      N'Contenido exclusivo para pruebas automatizadas.',N'Avenida de Prueba 100',N'Entorno local','00000',
      N'Ciudad de Prueba',N'Estado de Prueba',N'México','','',N'https://synthetic-restaurant-main.test/map',
      N'[]',N'Sitio sintético de validación Restaurant.',1,1,1,1,N'20260911_synthetic_dual_company');
  ELSE
    UPDATE restaurante.PublicSiteSettings SET Rfc=@Rfc,SiteId=@RestaurantLocalSiteId,
      LegalName=N'Empresa dual sintética de validación',PublicName=N'Restaurante sintético',
      IsWebsiteEnabled=1,IsMembershipEnabled=1,IsLoyaltyAccrualEnabled=1,IsPromotionsEnabled=1,
      UpdatedAt=SYSUTCDATETIME(),UpdatedBy=N'20260911_synthetic_dual_company'
    WHERE PublicSiteId=@RestaurantPublicSiteId;

  MERGE orion.IntegrationBinding AS target
  USING(VALUES
    (@CompanyId,@CampusSiteId,'HOSPITALITY',@HospitalityPublicSiteId,'CALENDAR',N'secrets://synthetic-hospitality/calendar',N'{"enabled":false}'),
    (@CompanyId,@CampusSiteId,'RESTAURANT',@RestaurantPublicSiteId,'MAIL',N'secrets://synthetic-restaurant/mail',N'{"enabled":false}')
  ) source(CompanyId,SiteId,ModuleCode,PublicSiteId,IntegrationKind,SecretReference,ConfigurationJson)
    ON target.CompanyId=source.CompanyId AND target.SiteId=source.SiteId
   AND target.ModuleCode=source.ModuleCode AND target.PublicSiteId=source.PublicSiteId
   AND target.IntegrationKind=source.IntegrationKind
  WHEN MATCHED THEN UPDATE SET SecretReference=source.SecretReference,ConfigurationJson=source.ConfigurationJson,
    IsEnabled=0,ConfigurationVersion=target.ConfigurationVersion+1,UpdatedAtUtc=SYSUTCDATETIME(),UpdatedBy=N'20260911_synthetic_dual_company'
  WHEN NOT MATCHED THEN INSERT(CompanyId,SiteId,ModuleCode,PublicSiteId,IntegrationKind,SecretReference,
    ConfigurationJson,IsEnabled,UpdatedBy)
    VALUES(source.CompanyId,source.SiteId,source.ModuleCode,source.PublicSiteId,source.IntegrationKind,
      source.SecretReference,source.ConfigurationJson,0,N'20260911_synthetic_dual_company');

  /* Three console identities: company administrator, Hospitality operator and Restaurant operator. */
  DECLARE @ConsoleUsers TABLE(UserId nvarchar(450) PRIMARY KEY,Email nvarchar(256));
  INSERT @ConsoleUsers VALUES
    (N'synthetic-company-admin-01',N'company-admin@synthetic-dual.invalid'),
    (N'synthetic-hospitality-operator-01',N'hospitality-operator@synthetic-dual.invalid'),
    (N'synthetic-restaurant-operator-01',N'restaurant-operator@synthetic-dual.invalid');
  INSERT auth.AspNetUsers
    (Id,UserName,NormalizedUserName,Email,NormalizedEmail,EmailConfirmed,SecurityStamp,ConcurrencyStamp,
     PhoneNumberConfirmed,TwoFactorEnabled,LockoutEnabled,AccessFailedCount)
  SELECT UserId,Email,UPPER(Email),Email,UPPER(Email),1,CONVERT(nvarchar(36),NEWID()),CONVERT(nvarchar(36),NEWID()),0,0,1,0
  FROM @ConsoleUsers source WHERE NOT EXISTS(SELECT 1 FROM auth.AspNetUsers target WHERE target.Id=source.UserId);
  INSERT auth.AspNetUserCompanies(UserId,Rfc,IsActive,AccessReviewRequired,UpdatedBy,CompanyId)
  SELECT UserId,@Rfc,1,0,N'20260911_synthetic_dual_company',@CompanyId FROM @ConsoleUsers source
  WHERE NOT EXISTS(SELECT 1 FROM auth.AspNetUserCompanies target WHERE target.UserId=source.UserId AND target.Rfc=@Rfc);

  DECLARE @OperatorRoleId nvarchar(450)=(SELECT Id FROM auth.AspNetRoles WHERE NormalizedName=N'OPERADOR' AND Scope='Company');
  DECLARE @RestaurantAdminRoleId nvarchar(450)=(SELECT Id FROM auth.AspNetRoles WHERE NormalizedName=N'RESTAURANTEADMIN' AND Scope='Company');
  DECLARE @RestaurantCashierRoleId nvarchar(450)=(SELECT Id FROM auth.AspNetRoles WHERE NormalizedName=N'RESTAURANTECAJA' AND Scope='Company');
  IF @OperatorRoleId IS NULL OR @RestaurantAdminRoleId IS NULL OR @RestaurantCashierRoleId IS NULL
    THROW 52914,'Faltan roles de consola requeridos por el fixture.',1;
  DECLARE @ConsoleRoles TABLE(UserId nvarchar(450),RoleId nvarchar(450),PRIMARY KEY(UserId,RoleId));
  INSERT @ConsoleRoles VALUES
    (N'synthetic-company-admin-01',@OperatorRoleId),
    (N'synthetic-company-admin-01',@RestaurantAdminRoleId),
    (N'synthetic-hospitality-operator-01',@OperatorRoleId),
    (N'synthetic-restaurant-operator-01',@RestaurantCashierRoleId);
  INSERT auth.AspNetUserCompanyRoles(UserId,Rfc,RoleId,CompanyId)
  SELECT source.UserId,@Rfc,source.RoleId,@CompanyId FROM @ConsoleRoles source
  WHERE NOT EXISTS
    (SELECT 1 FROM auth.AspNetUserCompanyRoles target WHERE target.UserId=source.UserId AND target.Rfc=@Rfc AND target.RoleId=source.RoleId);

  DECLARE @CreatePrincipalSql nvarchar(1000);
  IF DATABASE_PRINCIPAL_ID(@HospitalityPrincipal) IS NULL
  BEGIN
    SET @CreatePrincipalSql=N'CREATE USER '+QUOTENAME(@HospitalityPrincipal)+N' WITHOUT LOGIN;';
    EXEC sys.sp_executesql @CreatePrincipalSql;
  END;
  IF DATABASE_PRINCIPAL_ID(@RestaurantPrincipal) IS NULL
  BEGIN
    SET @CreatePrincipalSql=N'CREATE USER '+QUOTENAME(@RestaurantPrincipal)+N' WITHOUT LOGIN;';
    EXEC sys.sp_executesql @CreatePrincipalSql;
  END;
  IF EXISTS
  (
    SELECT 1 FROM sys.database_role_members
    WHERE member_principal_id IN(DATABASE_PRINCIPAL_ID(@HospitalityPrincipal),DATABASE_PRINCIPAL_ID(@RestaurantPrincipal))
  ) THROW 52915,'Los principales públicos sintéticos no pueden pertenecer a roles.',1;

  /* Exercise permission Preview and Apply twice to prove drift-free idempotence. */
  PRINT N'Fixture: permission profiles';
  CREATE TABLE #PermissionResult
  (ApplyChanges bit,PublicSiteId bigint,ProfileCode varchar(40),ProfileVersion int,
   Estado varchar(5),Permiso varchar(20),Securable nvarchar(800),YaExiste bit);
  INSERT #PermissionResult EXEC orion.ApplyPublicPermissionProfile @HospitalityKey,@HospitalityPrincipal,0;
  DELETE #PermissionResult;
  INSERT #PermissionResult EXEC orion.ApplyPublicPermissionProfile @HospitalityKey,@HospitalityPrincipal,0;
  DELETE #PermissionResult;
  INSERT #PermissionResult EXEC orion.ApplyPublicPermissionProfile @HospitalityKey,@HospitalityPrincipal,1;
  DELETE #PermissionResult;
  INSERT #PermissionResult EXEC orion.ApplyPublicPermissionProfile @HospitalityKey,@HospitalityPrincipal,1;
  DELETE #PermissionResult;
  INSERT #PermissionResult EXEC orion.ApplyPublicPermissionProfile @RestaurantKey,@RestaurantPrincipal,0;
  DELETE #PermissionResult;
  INSERT #PermissionResult EXEC orion.ApplyPublicPermissionProfile @RestaurantKey,@RestaurantPrincipal,0;
  DELETE #PermissionResult;
  INSERT #PermissionResult EXEC orion.ApplyPublicPermissionProfile @RestaurantKey,@RestaurantPrincipal,1;
  DELETE #PermissionResult;
  INSERT #PermissionResult EXEC orion.ApplyPublicPermissionProfile @RestaurantKey,@RestaurantPrincipal,1;

  IF NOT EXISTS(SELECT 1 FROM orion.PublicSqlPrincipalBinding WHERE PrincipalName=@HospitalityPrincipal AND PublicSiteId=@HospitalityPublicSiteId AND PermissionProfile='HOSPITALITY_PUBLIC' AND PermissionVersion=2 AND IsActive=1)
     OR NOT EXISTS(SELECT 1 FROM orion.PublicSqlPrincipalBinding WHERE PrincipalName=@RestaurantPrincipal AND PublicSiteId=@RestaurantPublicSiteId AND PermissionProfile='RESTAURANT_PUBLIC' AND PermissionVersion=2 AND IsActive=1)
    THROW 52916,'Los perfiles públicos no quedaron enlazados a sus PublicSites.',1;

  /* Hospitality fixture data under an explicit, complete execution scope. */
  PRINT N'Fixture: Hospitality data';
  EXEC sys.sp_set_session_context @key=N'OrionERP.CompanyId',@value=@CompanyId;
  EXEC sys.sp_set_session_context @key=N'OrionRfc',@value=@Rfc;
  EXEC sys.sp_set_session_context @key=N'OrionERP.SiteId',@value=@CampusSiteId;
  EXEC sys.sp_set_session_context @key=N'OrionERP.ModuleCode',@value=N'HOSPITALITY';
  EXEC sys.sp_set_session_context @key=N'OrionERP.PublicSiteId',@value=@HospitalityPublicSiteId;
  EXEC sys.sp_set_session_context @key=N'OrionERP.HospitalityCompanyId',@value=@CompanyId;
  EXEC sys.sp_set_session_context @key=N'OrionERP.HospitalitySiteId',@value=@CampusSiteId;

  DECLARE @CustomerId int=(SELECT TOP(1) ID FROM dbo.Clientes WHERE Email='guest@synthetic-dual.invalid' ORDER BY ID);
  IF @CustomerId IS NULL
  BEGIN
    INSERT dbo.Clientes(Nombre,Email,Notas) VALUES(N'Huésped sintético',N'guest@synthetic-dual.invalid',N'Fixture dual determinista');
    SET @CustomerId=SCOPE_IDENTITY();
  END;
  IF NOT EXISTS(SELECT 1 FROM orion.HospitalitySiteCustomer WHERE CompanyId=@CompanyId AND SiteId=@CampusSiteId AND ClienteId=@CustomerId)
    INSERT orion.HospitalitySiteCustomer(CompanyId,SiteId,ClienteId)
    VALUES(@CompanyId,@CampusSiteId,@CustomerId);

  DECLARE @RoomId int=(SELECT ID FROM dbo.ROOM WHERE OrionCompanyId=@CompanyId AND OrionSiteId=@CampusSiteId AND ROOM_NAME='SYNTHETIC-ROOM-01');
  IF @RoomId IS NULL
  BEGIN
    INSERT dbo.ROOM(ROOM_NAME,ROOM_TYPE,ROOM_DESCRIPTION,BASE_PRICE,IsActive,IsRentable,OrionCompanyId,OrionSiteId)
    VALUES('SYNTHETIC-ROOM-01','SUITE','Habitación sintética de validación',1000,1,1,@CompanyId,@CampusSiteId);
    SET @RoomId=SCOPE_IDENTITY();
  END;
  DECLARE @ReservationId int=(SELECT ID FROM dbo.RESERVATION WHERE OrionCompanyId=@CompanyId AND OrionSiteId=@CampusSiteId AND NOTES='SYNTHETIC-DUAL-RESERVATION-01');
  IF @ReservationId IS NULL
  BEGIN
    INSERT dbo.RESERVATION(CLIENTE_ID,CHECKIN,CHECKOUT,TOTAL_PRICE,STATUS,NOTES,AIRBNB_UPDATED,RFC,
      OrionCompanyId,OrionSiteId,PrivacyVersionAccepted,TermsVersionAccepted,LegalAcceptedAtUtc)
    VALUES(@CustomerId,'2026-10-01','2026-10-02',1000,'ACTIVA','SYNTHETIC-DUAL-RESERVATION-01',0,@Rfc,
      @CompanyId,@CampusSiteId,N'synthetic-privacy-v1',N'synthetic-terms-v1',SYSUTCDATETIME());
    SET @ReservationId=SCOPE_IDENTITY();
  END;
  IF NOT EXISTS(SELECT 1 FROM dbo.RESERVATION_DETAIL WHERE RESERVATION_ID=@ReservationId AND ROOM_ID=@RoomId)
    INSERT dbo.RESERVATION_DETAIL(RESERVATION_ID,ROOM_ID,PRICE,DISCOUNTED_PRICE,DISCOUNT,NOTES,OrionCompanyId,OrionSiteId)
    VALUES(@ReservationId,@RoomId,1000,1000,0,'SYNTHETIC-DUAL-DETAIL-01',@CompanyId,@CampusSiteId);
  IF NOT EXISTS(SELECT 1 FROM dbo.ROOM_CALENDAR WHERE OrionCompanyId=@CompanyId AND OrionSiteId=@CampusSiteId AND ROOM_DATE='2026-10-01' AND RoomId=@RoomId)
    INSERT dbo.ROOM_CALENDAR(ROOM_DATE,ROOM,IS_LOCKED,LOCK_DESCRIPTION,PRECIO,STATUS,OrionCompanyId,OrionSiteId,RoomId,ReservationId)
    VALUES('2026-10-01','SYNTHETIC-ROOM-01',1,'SYNTHETIC-DUAL-RESERVATION-01',1000,'RESERVADA',@CompanyId,@CampusSiteId,@RoomId,@ReservationId);

  /* Reconcile all seven Identity tables, then make the neutral schema authoritative. */
  PRINT N'Fixture: Identity cutover';
  IF (SELECT COUNT_BIG(*) FROM public_identity.AspNetUsers)<>(SELECT COUNT_BIG(*) FROM brunos_auth.AspNetUsers)
     OR (SELECT COUNT_BIG(*) FROM public_identity.AspNetRoles)<>(SELECT COUNT_BIG(*) FROM brunos_auth.AspNetRoles)
     OR (SELECT COUNT_BIG(*) FROM public_identity.AspNetUserClaims)<>(SELECT COUNT_BIG(*) FROM brunos_auth.AspNetUserClaims)
     OR (SELECT COUNT_BIG(*) FROM public_identity.AspNetRoleClaims)<>(SELECT COUNT_BIG(*) FROM brunos_auth.AspNetRoleClaims)
     OR (SELECT COUNT_BIG(*) FROM public_identity.AspNetUserLogins)<>(SELECT COUNT_BIG(*) FROM brunos_auth.AspNetUserLogins)
     OR (SELECT COUNT_BIG(*) FROM public_identity.AspNetUserRoles)<>(SELECT COUNT_BIG(*) FROM brunos_auth.AspNetUserRoles)
     OR (SELECT COUNT_BIG(*) FROM public_identity.AspNetUserTokens)<>(SELECT COUNT_BIG(*) FROM brunos_auth.AspNetUserTokens)
    THROW 52917,'Los conteos de Identity divergen antes del cutover.',1;
  IF ISNULL((SELECT CHECKSUM_AGG(BINARY_CHECKSUM(Id)) FROM public_identity.AspNetUsers),0)
       <>ISNULL((SELECT CHECKSUM_AGG(BINARY_CHECKSUM(Id)) FROM brunos_auth.AspNetUsers),0)
     OR ISNULL((SELECT CHECKSUM_AGG(BINARY_CHECKSUM(Id)) FROM public_identity.AspNetRoles),0)
       <>ISNULL((SELECT CHECKSUM_AGG(BINARY_CHECKSUM(Id)) FROM brunos_auth.AspNetRoles),0)
    THROW 52918,'Los hashes de claves Identity divergen antes del cutover.',1;

  EXEC orion.SetPublicIdentityBridgeMode @BridgeMode='Reverse',@UpdatedBy=N'20260911_synthetic_dual_company';
  IF NOT EXISTS(SELECT 1 FROM orion.PublicIdentityCompatibilityState WHERE SingletonId=1 AND BridgeMode='Reverse')
    THROW 52919,'El bridge de identidad no cambió a Reverse.',1;

  DECLARE @SharedEmail nvarchar(256)=N'shared-member@synthetic-dual.invalid';
  PRINT N'Fixture: public users';
  IF NOT EXISTS(SELECT 1 FROM public_identity.AspNetUsers WHERE Id=N'synthetic-public-restaurant-01')
    INSERT public_identity.AspNetUsers
      (Id,PublicSiteId,UserName,NormalizedUserName,Email,NormalizedEmail,EmailConfirmed,PasswordHash,
       SecurityStamp,ConcurrencyStamp,PhoneNumber,PhoneNumberConfirmed,TwoFactorEnabled,LockoutEnabled,
       AccessFailedCount,FirstName,LastName,CreatedAt)
    VALUES(N'synthetic-public-restaurant-01',@RestaurantPublicSiteId,N'restaurant-member@synthetic-dual.invalid',
      N'RESTAURANT-MEMBER@SYNTHETIC-DUAL.INVALID',@SharedEmail,N'SHARED-MEMBER@SYNTHETIC-DUAL.INVALID',1,NULL,
      N'synthetic-security-restaurant-01',N'synthetic-concurrency-restaurant-01',NULL,0,0,1,0,N'Miembro',N'Restaurant',SYSUTCDATETIME());
  IF NOT EXISTS(SELECT 1 FROM brunos_auth.AspNetUsers WHERE Id=N'synthetic-public-restaurant-01')
    THROW 52920,'El bridge Reverse no conservó la cuenta Restaurant en el esquema de rollback.',1;

  /* The legacy schema is Restaurant-only. Pause the one-way bridge while proving that
     neutral Identity can safely hold the same email for another module/PublicSite. */
  EXEC orion.SetPublicIdentityBridgeMode @BridgeMode='Off',@UpdatedBy=N'20260911_synthetic_dual_company';
  IF NOT EXISTS(SELECT 1 FROM public_identity.AspNetUsers WHERE Id=N'synthetic-public-hospitality-01')
    INSERT public_identity.AspNetUsers
      (Id,PublicSiteId,UserName,NormalizedUserName,Email,NormalizedEmail,EmailConfirmed,PasswordHash,
       SecurityStamp,ConcurrencyStamp,PhoneNumber,PhoneNumberConfirmed,TwoFactorEnabled,LockoutEnabled,
       AccessFailedCount,FirstName,LastName,CreatedAt)
    VALUES(N'synthetic-public-hospitality-01',@HospitalityPublicSiteId,N'hospitality-member@synthetic-dual.invalid',
      N'HOSPITALITY-MEMBER@SYNTHETIC-DUAL.INVALID',@SharedEmail,N'SHARED-MEMBER@SYNTHETIC-DUAL.INVALID',1,NULL,
      N'synthetic-security-hospitality-01',N'synthetic-concurrency-hospitality-01',NULL,0,0,1,0,N'Miembro',N'Hospitality',SYSUTCDATETIME());
  EXEC orion.SetPublicIdentityBridgeMode @BridgeMode='Reverse',@UpdatedBy=N'20260911_synthetic_dual_company';

  EXEC sys.sp_set_session_context @key=N'OrionERP.ModuleCode',@value=N'RESTAURANT';
  EXEC sys.sp_set_session_context @key=N'OrionERP.PublicSiteId',@value=@RestaurantPublicSiteId;
  DECLARE @MemberId uniqueidentifier='26091000-0000-0000-0000-000000000101';
  IF NOT EXISTS(SELECT 1 FROM fidelidad.MemberAccount WHERE Id=@MemberId)
    INSERT fidelidad.MemberAccount
      (Id,Rfc,IdentityUserId,MembershipNumber,FirstName,LastName,NormalizedEmail,NormalizedPhone,
       EmailVerified,PhoneVerified,[Status],PointsBalance,IsAdultConfirmed,PublicSiteId)
    VALUES(@MemberId,@Rfc,N'synthetic-public-restaurant-01','SYN2609100001',N'Miembro',N'Restaurant',
      N'SHARED-MEMBER@SYNTHETIC-DUAL.INVALID','+520000000001',1,0,'Active',100,1,@RestaurantPublicSiteId);

  IF NOT EXISTS(SELECT 1 FROM restaurante.Menu WHERE Rfc=@Rfc AND MenuCode='SYNTHETIC-MAIN')
    INSERT restaurante.Menu(Rfc,MenuCode,[Name],IsPublished,IsActive)
    VALUES(@Rfc,'SYNTHETIC-MAIN','Menú sintético',1,1);
  IF NOT EXISTS(SELECT 1 FROM restaurante.Promotion WHERE Rfc=@Rfc AND [Name]=N'Promoción sintética 10%')
    INSERT restaurante.Promotion
      (Rfc,SiteId,[Name],PublicDescription,PublicTerms,[Status],RuleType,Priority,ValidFromLocal,ValidToLocal,
       PosEnabled,WebEnabled,MemberOnly,CodeRequired,IsCombinable,IsPublic,PercentOff,CreatedBy,UpdatedBy)
    VALUES(@Rfc,@RestaurantLocalSiteId,N'Promoción sintética 10%',N'Descuento de validación.',N'Solo datos sintéticos.',
      'Active','PercentOff',10,'2026-01-01','2027-01-01',1,1,0,0,0,1,10,N'fixture',N'fixture');

  DECLARE @OrderId uniqueidentifier='26091000-0000-0000-0000-000000000201';
  IF NOT EXISTS(SELECT 1 FROM restaurante.[Order] WHERE Id=@OrderId)
    INSERT restaurante.[Order]
      (Id,Rfc,SiteId,Folio,OperationalDate,OrderType,[Status],PaymentStatus,CustomerName,
       Subtotal,DiscountTotal,TaxTotal,TipTotal,Total,BalanceDue,TaxRateSnapshot,PricesIncludeTaxSnapshot,
       IdempotencyKey,CreatedBy,MemberId,MembershipNumberSnapshot,EligibleMerchandiseTotal,PointsEarned)
    VALUES(@OrderId,@Rfc,@RestaurantLocalSiteId,1,'2026-09-15','DineIn','Completed','Paid',
      'Cliente sintético',100,0,16,0,116,0,0.16,0,'synthetic-order-01','fixture',@MemberId,
      'SYN2609100001',100,10);
  IF NOT EXISTS(SELECT 1 FROM fidelidad.PointLedger WHERE Rfc=@Rfc AND SourceKey='synthetic-opening-01')
    INSERT fidelidad.PointLedger
      (Rfc,MemberId,EntryType,PointsDelta,BalanceAfter,EligibleMerchandiseAmount,OrderId,SourceKey,Reason,CreatedBy)
    VALUES(@Rfc,@MemberId,'Earned',100,100,100,@OrderId,'synthetic-opening-01',N'Saldo sintético de validación',N'fixture');

  /* Reproducible provisioning ledger with resumable stages. */
  MERGE orion.ProvisioningOperation AS target
  USING(SELECT @OperationId OperationId) source ON target.OperationId=source.OperationId
  WHEN MATCHED THEN UPDATE SET OperationKind='SYNTHETIC_DUAL',[Status]='Completed',CompanyId=@CompanyId,
    RequestJson=N'{"fixture":"synthetic-dual-01","version":1}',LastError=NULL,UpdatedAtUtc=SYSUTCDATETIME()
  WHEN NOT MATCHED THEN INSERT(OperationId,OperationKind,[Status],CompanyId,RequestJson,CreatedAtUtc,UpdatedAtUtc)
    VALUES(@OperationId,'SYNTHETIC_DUAL','Completed',@CompanyId,N'{"fixture":"synthetic-dual-01","version":1}',SYSUTCDATETIME(),SYSUTCDATETIME());
  MERGE orion.ProvisioningOperationStep AS target
  USING(VALUES
    (@OperationId,'company','Completed'),(@OperationId,'sites','Completed'),(@OperationId,'bindings','Completed'),
    (@OperationId,'public-sites','Completed'),(@OperationId,'sql-principals','Completed'),
    (@OperationId,'permissions','Completed'),(@OperationId,'health','Completed')
  ) source(OperationId,StepCode,[Status])
    ON target.OperationId=source.OperationId AND target.StepCode=source.StepCode
  WHEN MATCHED THEN UPDATE SET [Status]=source.[Status],DetailJson=N'{"synthetic":true}',UpdatedAtUtc=SYSUTCDATETIME()
  WHEN NOT MATCHED THEN INSERT(OperationId,StepCode,[Status],DetailJson)
    VALUES(source.OperationId,source.StepCode,source.[Status],N'{"synthetic":true}');

  IF (SELECT COUNT(*) FROM orion.PublicSite WHERE CompanyId=@CompanyId AND IsActive=1)<>2
     OR (SELECT COUNT(*) FROM orion.SiteCapability WHERE CompanyId=@CompanyId AND SiteId=@CampusSiteId AND IsEnabled=1)<>2
     OR (SELECT COUNT(*) FROM orion.SiteCapability WHERE CompanyId=@CompanyId AND SiteId=@ControlSiteId AND IsEnabled=0)<>2
    THROW 52921,'La topología dual no cumple el fixture esperado.',1;
  IF (SELECT COUNT(*) FROM public_identity.AspNetUsers WHERE NormalizedEmail=N'SHARED-MEMBER@SYNTHETIC-DUAL.INVALID')<>2
    THROW 52922,'Identity no permite el mismo correo en dos PublicSites aislados.',1;
  IF EXISTS
  (
    SELECT 1 FROM
    (
      SELECT CONVERT(nvarchar(max),DisplayName) Value FROM orion.Company WHERE CompanyId=@CompanyId
      UNION ALL SELECT CanonicalHost FROM orion.PublicSite WHERE CompanyId=@CompanyId
      UNION ALL SELECT Email FROM public_identity.AspNetUsers WHERE PublicSiteId IN(@HospitalityPublicSiteId,@RestaurantPublicSiteId)
    ) fixture
    WHERE fixture.Value LIKE N'%bonhomia%' OR fixture.Value LIKE N'%bruno%' OR fixture.Value LIKE N'%ohm%'
  ) THROW 52923,'El fixture contiene una identidad comercial real.',1;

  EXEC sys.sp_set_session_context @key=N'OrionERP.CompanyId',@value=NULL;
  EXEC sys.sp_set_session_context @key=N'OrionRfc',@value=NULL;
  EXEC sys.sp_set_session_context @key=N'OrionERP.SiteId',@value=NULL;
  EXEC sys.sp_set_session_context @key=N'OrionERP.ModuleCode',@value=NULL;
  EXEC sys.sp_set_session_context @key=N'OrionERP.PublicSiteId',@value=NULL;
  EXEC sys.sp_set_session_context @key=N'OrionERP.HospitalityCompanyId',@value=NULL;
  EXEC sys.sp_set_session_context @key=N'OrionERP.HospitalitySiteId',@value=NULL;

  IF @ApplyChanges=1
  BEGIN
    INSERT orion.SchemaMigration(MigrationId,Checksum,AppliedBy,AppVersion,DatabaseName)
    VALUES(@MigrationId,@MigrationChecksum,
      COALESCE(CONVERT(nvarchar(256),SESSION_CONTEXT(N'OrionERP.UserName')),CONVERT(nvarchar(256),ORIGINAL_LOGIN())),
      @AppVersion,DB_NAME());
    COMMIT TRANSACTION;
    SELECT N'APLICADO' Estado,@MigrationId MigrationId,@CompanyId CompanyId,
      @HospitalityPublicSiteId HospitalityPublicSiteId,@RestaurantPublicSiteId RestaurantPublicSiteId,
      N'Reverse' IdentityBridgeMode,2 SqlPrincipals,7 CompletedSteps;
  END
  ELSE
  BEGIN
    SELECT N'PREVIEW' Estado,@MigrationId MigrationId,@CompanyId CompanyId,
      @HospitalityPublicSiteId HospitalityPublicSiteId,@RestaurantPublicSiteId RestaurantPublicSiteId,
      N'Reverse' IdentityBridgeMode,2 SqlPrincipals,7 CompletedSteps;
    ROLLBACK TRANSACTION;
  END;
END TRY
BEGIN CATCH
  EXEC sys.sp_set_session_context @key=N'OrionERP.CompanyId',@value=NULL;
  EXEC sys.sp_set_session_context @key=N'OrionRfc',@value=NULL;
  EXEC sys.sp_set_session_context @key=N'OrionERP.SiteId',@value=NULL;
  EXEC sys.sp_set_session_context @key=N'OrionERP.ModuleCode',@value=NULL;
  EXEC sys.sp_set_session_context @key=N'OrionERP.PublicSiteId',@value=NULL;
  EXEC sys.sp_set_session_context @key=N'OrionERP.HospitalityCompanyId',@value=NULL;
  EXEC sys.sp_set_session_context @key=N'OrionERP.HospitalitySiteId',@value=NULL;
  IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
  THROW;
END CATCH;
