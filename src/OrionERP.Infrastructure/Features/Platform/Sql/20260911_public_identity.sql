/* Brand-neutral public Identity schema with a one-direction-at-a-time rollback bridge. */
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
DECLARE @AppVersion nvarchar(64)=NULL;
IF @ApplyInput NOT IN(N'0',N'1') THROW 52420,'ApplyChanges debe ser 0 o 1.',1;
SET @ApplyChanges=CONVERT(bit,@ApplyInput);
IF @ExpectedDatabase NOT IN(N'Orion_Sandbox',N'grupocarpio',N'Orion_CutoverValidation_20260908')
  THROW 52421,'Base esperada no permitida.',1;
IF DB_NAME()<>@ExpectedDatabase THROW 52422,'La conexión no apunta a la base declarada.',1;
IF @MigrationId<>N'20260911_public_identity' THROW 52423,'MigrationId incorrecto.',1;
IF @MigrationChecksum LIKE '$'+'(%' OR LEN(@MigrationChecksum)<>64
   OR @MigrationChecksum LIKE '%[^0-9A-F]%' COLLATE Latin1_General_100_BIN2
  THROW 52424,'MigrationChecksum inválido.',1;
IF @AppVersionInput NOT LIKE N'$'+N'(%' SET @AppVersion=NULLIF(LTRIM(RTRIM(@AppVersionInput)),N'');
IF OBJECT_ID(N'brunos_auth.AspNetUsers',N'U') IS NULL
   OR COL_LENGTH(N'brunos_auth.AspNetUsers',N'PublicSiteId') IS NULL
   OR OBJECT_ID(N'orion.PublicSite',N'U') IS NULL
  THROW 52425,'Falta la identidad pública heredada ya delimitada por PublicSite.',1;
IF NOT EXISTS(SELECT 1 FROM orion.SchemaMigration WHERE MigrationId=N'20260911_platform_execution_scope')
  THROW 52426,'Falta la migración de execution scope.',1;

DECLARE @ExistingChecksum char(64)=(SELECT Checksum FROM orion.SchemaMigration WHERE MigrationId=@MigrationId);
IF @ExistingChecksum IS NOT NULL AND UPPER(@ExistingChecksum)<>UPPER(@MigrationChecksum)
  THROW 52427,'El MigrationId ya existe con otro checksum.',1;
IF @ExistingChecksum IS NOT NULL BEGIN SELECT N'YA_APLICADO' Estado,@MigrationId MigrationId; RETURN; END;

BEGIN TRY
  BEGIN TRANSACTION;
  DECLARE @LockResult int;
  EXEC @LockResult=sys.sp_getapplock @Resource=N'OrionERP:PublicIdentity:20260911',
    @LockMode=N'Exclusive',@LockOwner=N'Transaction',@LockTimeout=15000;
  IF @LockResult<0 THROW 52428,'No se obtuvo el candado de migración.',1;

  IF SCHEMA_ID(N'public_identity') IS NULL EXEC(N'CREATE SCHEMA public_identity AUTHORIZATION dbo;');
  IF OBJECT_ID(N'public_identity.AspNetUsers',N'U') IS NOT NULL
     OR OBJECT_ID(N'public_identity.AspNetRoles',N'U') IS NOT NULL
    THROW 52429,'El esquema public_identity está parcialmente creado sin ledger.',1;

  CREATE TABLE public_identity.AspNetUsers
  (
    Id nvarchar(450) NOT NULL CONSTRAINT PK_PublicIdentityUsers PRIMARY KEY,
    PublicSiteId bigint NOT NULL,
    UserName nvarchar(256) NULL,NormalizedUserName nvarchar(256) NULL,
    Email nvarchar(256) NULL,NormalizedEmail nvarchar(256) NULL,EmailConfirmed bit NOT NULL,
    PasswordHash nvarchar(max) NULL,SecurityStamp nvarchar(max) NULL,ConcurrencyStamp nvarchar(max) NULL,
    PhoneNumber nvarchar(max) NULL,PhoneNumberConfirmed bit NOT NULL,TwoFactorEnabled bit NOT NULL,
    LockoutEnd datetimeoffset NULL,LockoutEnabled bit NOT NULL,AccessFailedCount int NOT NULL,
    FirstName nvarchar(100) NOT NULL,LastName nvarchar(100) NOT NULL,
    CreatedAt datetime2(0) NOT NULL,ClosedAt datetime2(0) NULL,
    CONSTRAINT FK_PublicIdentityUsers_PublicSite FOREIGN KEY(PublicSiteId) REFERENCES orion.PublicSite(PublicSiteId),
    CONSTRAINT UX_PublicIdentityUsers_IdPublicSite UNIQUE(Id,PublicSiteId)
  );
  CREATE UNIQUE INDEX UserNameIndex_PublicSite ON public_identity.AspNetUsers(PublicSiteId,NormalizedUserName) WHERE NormalizedUserName IS NOT NULL;
  CREATE UNIQUE INDEX EmailIndex_PublicSite ON public_identity.AspNetUsers(PublicSiteId,NormalizedEmail) WHERE NormalizedEmail IS NOT NULL;

  CREATE TABLE public_identity.AspNetRoles
  (
    Id nvarchar(450) NOT NULL CONSTRAINT PK_PublicIdentityRoles PRIMARY KEY,
    PublicSiteId bigint NOT NULL,[Name] nvarchar(256) NULL,NormalizedName nvarchar(256) NULL,ConcurrencyStamp nvarchar(max) NULL,
    CONSTRAINT FK_PublicIdentityRoles_PublicSite FOREIGN KEY(PublicSiteId) REFERENCES orion.PublicSite(PublicSiteId),
    CONSTRAINT UX_PublicIdentityRoles_IdPublicSite UNIQUE(Id,PublicSiteId)
  );
  CREATE UNIQUE INDEX RoleNameIndex_PublicSite ON public_identity.AspNetRoles(PublicSiteId,NormalizedName) WHERE NormalizedName IS NOT NULL;

  CREATE TABLE public_identity.AspNetUserClaims
  (
    Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_PublicIdentityUserClaims PRIMARY KEY,
    PublicSiteId bigint NOT NULL,UserId nvarchar(450) NOT NULL,ClaimType nvarchar(max) NULL,ClaimValue nvarchar(max) NULL,
    CONSTRAINT FK_PublicIdentityUserClaims_User FOREIGN KEY(UserId,PublicSiteId)
      REFERENCES public_identity.AspNetUsers(Id,PublicSiteId) ON DELETE CASCADE
  );
  CREATE INDEX IX_PublicIdentityUserClaims_User ON public_identity.AspNetUserClaims(PublicSiteId,UserId);

  CREATE TABLE public_identity.AspNetRoleClaims
  (
    Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_PublicIdentityRoleClaims PRIMARY KEY,
    PublicSiteId bigint NOT NULL,RoleId nvarchar(450) NOT NULL,ClaimType nvarchar(max) NULL,ClaimValue nvarchar(max) NULL,
    CONSTRAINT FK_PublicIdentityRoleClaims_Role FOREIGN KEY(RoleId,PublicSiteId)
      REFERENCES public_identity.AspNetRoles(Id,PublicSiteId) ON DELETE CASCADE
  );
  CREATE INDEX IX_PublicIdentityRoleClaims_Role ON public_identity.AspNetRoleClaims(PublicSiteId,RoleId);

  CREATE TABLE public_identity.AspNetUserLogins
  (
    PublicSiteId bigint NOT NULL,LoginProvider nvarchar(450) NOT NULL,ProviderKey nvarchar(450) NOT NULL,
    ProviderDisplayName nvarchar(max) NULL,UserId nvarchar(450) NOT NULL,
    CONSTRAINT PK_PublicIdentityUserLogins PRIMARY KEY(PublicSiteId,LoginProvider,ProviderKey),
    CONSTRAINT FK_PublicIdentityUserLogins_User FOREIGN KEY(UserId,PublicSiteId)
      REFERENCES public_identity.AspNetUsers(Id,PublicSiteId) ON DELETE CASCADE
  );
  CREATE INDEX IX_PublicIdentityUserLogins_User ON public_identity.AspNetUserLogins(PublicSiteId,UserId);

  CREATE TABLE public_identity.AspNetUserRoles
  (
    PublicSiteId bigint NOT NULL,UserId nvarchar(450) NOT NULL,RoleId nvarchar(450) NOT NULL,
    CONSTRAINT PK_PublicIdentityUserRoles PRIMARY KEY(PublicSiteId,UserId,RoleId),
    CONSTRAINT FK_PublicIdentityUserRoles_User FOREIGN KEY(UserId,PublicSiteId)
      REFERENCES public_identity.AspNetUsers(Id,PublicSiteId) ON DELETE CASCADE,
    CONSTRAINT FK_PublicIdentityUserRoles_Role FOREIGN KEY(RoleId,PublicSiteId)
      REFERENCES public_identity.AspNetRoles(Id,PublicSiteId)
  );

  CREATE TABLE public_identity.AspNetUserTokens
  (
    PublicSiteId bigint NOT NULL,UserId nvarchar(450) NOT NULL,LoginProvider nvarchar(450) NOT NULL,
    [Name] nvarchar(450) NOT NULL,[Value] nvarchar(max) NULL,
    CONSTRAINT PK_PublicIdentityUserTokens PRIMARY KEY(PublicSiteId,UserId,LoginProvider,[Name]),
    CONSTRAINT FK_PublicIdentityUserTokens_User FOREIGN KEY(UserId,PublicSiteId)
      REFERENCES public_identity.AspNetUsers(Id,PublicSiteId) ON DELETE CASCADE
  );

  INSERT public_identity.AspNetUsers
    (Id,PublicSiteId,UserName,NormalizedUserName,Email,NormalizedEmail,EmailConfirmed,PasswordHash,SecurityStamp,
     ConcurrencyStamp,PhoneNumber,PhoneNumberConfirmed,TwoFactorEnabled,LockoutEnd,LockoutEnabled,AccessFailedCount,
     FirstName,LastName,CreatedAt,ClosedAt)
  SELECT Id,PublicSiteId,UserName,NormalizedUserName,Email,NormalizedEmail,EmailConfirmed,PasswordHash,SecurityStamp,
     ConcurrencyStamp,PhoneNumber,PhoneNumberConfirmed,TwoFactorEnabled,LockoutEnd,LockoutEnabled,AccessFailedCount,
     FirstName,LastName,CreatedAt,ClosedAt
  FROM brunos_auth.AspNetUsers;

  IF EXISTS(SELECT 1 FROM brunos_auth.AspNetRoles)
  BEGIN
    IF (SELECT COUNT(*) FROM orion.PublicSite WHERE ModuleCode=N'RESTAURANT' AND IsActive=1)<>1
      THROW 52430,'Los roles heredados requieren un único PublicSite Restaurant para el backfill.',1;
    INSERT public_identity.AspNetRoles(Id,PublicSiteId,[Name],NormalizedName,ConcurrencyStamp)
    SELECT Id,(SELECT PublicSiteId FROM orion.PublicSite WHERE ModuleCode=N'RESTAURANT' AND IsActive=1),[Name],NormalizedName,ConcurrencyStamp
    FROM brunos_auth.AspNetRoles;
  END;

  SET IDENTITY_INSERT public_identity.AspNetUserClaims ON;
  INSERT public_identity.AspNetUserClaims(Id,PublicSiteId,UserId,ClaimType,ClaimValue)
  SELECT claimInfo.Id,identityUser.PublicSiteId,claimInfo.UserId,claimInfo.ClaimType,claimInfo.ClaimValue
  FROM brunos_auth.AspNetUserClaims claimInfo JOIN brunos_auth.AspNetUsers identityUser ON identityUser.Id=claimInfo.UserId;
  SET IDENTITY_INSERT public_identity.AspNetUserClaims OFF;

  SET IDENTITY_INSERT public_identity.AspNetRoleClaims ON;
  INSERT public_identity.AspNetRoleClaims(Id,PublicSiteId,RoleId,ClaimType,ClaimValue)
  SELECT claimInfo.Id,roleInfo.PublicSiteId,claimInfo.RoleId,claimInfo.ClaimType,claimInfo.ClaimValue
  FROM brunos_auth.AspNetRoleClaims claimInfo JOIN public_identity.AspNetRoles roleInfo ON roleInfo.Id=claimInfo.RoleId;
  SET IDENTITY_INSERT public_identity.AspNetRoleClaims OFF;

  INSERT public_identity.AspNetUserLogins(PublicSiteId,LoginProvider,ProviderKey,ProviderDisplayName,UserId)
  SELECT identityUser.PublicSiteId,loginInfo.LoginProvider,loginInfo.ProviderKey,loginInfo.ProviderDisplayName,loginInfo.UserId
  FROM brunos_auth.AspNetUserLogins loginInfo JOIN brunos_auth.AspNetUsers identityUser ON identityUser.Id=loginInfo.UserId;
  INSERT public_identity.AspNetUserRoles(PublicSiteId,UserId,RoleId)
  SELECT identityUser.PublicSiteId,roleLink.UserId,roleLink.RoleId
  FROM brunos_auth.AspNetUserRoles roleLink JOIN brunos_auth.AspNetUsers identityUser ON identityUser.Id=roleLink.UserId;
  INSERT public_identity.AspNetUserTokens(PublicSiteId,UserId,LoginProvider,[Name],[Value])
  SELECT identityUser.PublicSiteId,tokenInfo.UserId,tokenInfo.LoginProvider,tokenInfo.[Name],tokenInfo.[Value]
  FROM brunos_auth.AspNetUserTokens tokenInfo JOIN brunos_auth.AspNetUsers identityUser ON identityUser.Id=tokenInfo.UserId;

  IF NOT EXISTS(SELECT 1 FROM sys.foreign_keys WHERE parent_object_id=OBJECT_ID(N'fidelidad.MemberAccount') AND name=N'FK_LoyaltyMember_PublicIdentityScope')
    ALTER TABLE fidelidad.MemberAccount WITH CHECK ADD CONSTRAINT FK_LoyaltyMember_PublicIdentityScope
      FOREIGN KEY(IdentityUserId,PublicSiteId) REFERENCES public_identity.AspNetUsers(Id,PublicSiteId);

  CREATE TABLE orion.PublicIdentityCompatibilityState
  (
    SingletonId tinyint NOT NULL CONSTRAINT PK_PublicIdentityCompatibilityState PRIMARY KEY,
    BridgeMode varchar(10) NOT NULL,
    UpdatedAtUtc datetime2(3) NOT NULL CONSTRAINT DF_PublicIdentityCompatibilityState_Updated DEFAULT(SYSUTCDATETIME()),
    UpdatedBy nvarchar(256) NOT NULL,
    CONSTRAINT CK_PublicIdentityCompatibilityState_Singleton CHECK(SingletonId=1),
    CONSTRAINT CK_PublicIdentityCompatibilityState_Mode CHECK(BridgeMode IN('Forward','Reverse','Off'))
  );
  INSERT orion.PublicIdentityCompatibilityState(SingletonId,BridgeMode,UpdatedBy)
  VALUES(1,'Forward',N'20260911_public_identity');

  EXEC(N'
    CREATE OR ALTER FUNCTION orion.fn_PublicIdentityScopePredicate(@PublicSiteId bigint)
    RETURNS TABLE WITH SCHEMABINDING AS RETURN
      SELECT 1 Allowed
      WHERE @PublicSiteId=TRY_CONVERT(bigint,SESSION_CONTEXT(N''OrionERP.PublicSiteId''))
         OR USER_NAME()=N''dbo'';');

  EXEC(N'
    CREATE SECURITY POLICY orion.PublicIdentityScopePolicy
      ADD FILTER PREDICATE orion.fn_PublicIdentityScopePredicate(PublicSiteId) ON public_identity.AspNetUsers,
      ADD BLOCK PREDICATE orion.fn_PublicIdentityScopePredicate(PublicSiteId) ON public_identity.AspNetUsers AFTER INSERT,
      ADD BLOCK PREDICATE orion.fn_PublicIdentityScopePredicate(PublicSiteId) ON public_identity.AspNetUsers AFTER UPDATE,
      ADD FILTER PREDICATE orion.fn_PublicIdentityScopePredicate(PublicSiteId) ON public_identity.AspNetRoles,
      ADD BLOCK PREDICATE orion.fn_PublicIdentityScopePredicate(PublicSiteId) ON public_identity.AspNetRoles AFTER INSERT,
      ADD BLOCK PREDICATE orion.fn_PublicIdentityScopePredicate(PublicSiteId) ON public_identity.AspNetRoles AFTER UPDATE,
      ADD FILTER PREDICATE orion.fn_PublicIdentityScopePredicate(PublicSiteId) ON public_identity.AspNetUserClaims,
      ADD BLOCK PREDICATE orion.fn_PublicIdentityScopePredicate(PublicSiteId) ON public_identity.AspNetUserClaims AFTER INSERT,
      ADD BLOCK PREDICATE orion.fn_PublicIdentityScopePredicate(PublicSiteId) ON public_identity.AspNetUserClaims AFTER UPDATE,
      ADD FILTER PREDICATE orion.fn_PublicIdentityScopePredicate(PublicSiteId) ON public_identity.AspNetRoleClaims,
      ADD BLOCK PREDICATE orion.fn_PublicIdentityScopePredicate(PublicSiteId) ON public_identity.AspNetRoleClaims AFTER INSERT,
      ADD BLOCK PREDICATE orion.fn_PublicIdentityScopePredicate(PublicSiteId) ON public_identity.AspNetRoleClaims AFTER UPDATE,
      ADD FILTER PREDICATE orion.fn_PublicIdentityScopePredicate(PublicSiteId) ON public_identity.AspNetUserLogins,
      ADD BLOCK PREDICATE orion.fn_PublicIdentityScopePredicate(PublicSiteId) ON public_identity.AspNetUserLogins AFTER INSERT,
      ADD BLOCK PREDICATE orion.fn_PublicIdentityScopePredicate(PublicSiteId) ON public_identity.AspNetUserLogins AFTER UPDATE,
      ADD FILTER PREDICATE orion.fn_PublicIdentityScopePredicate(PublicSiteId) ON public_identity.AspNetUserRoles,
      ADD BLOCK PREDICATE orion.fn_PublicIdentityScopePredicate(PublicSiteId) ON public_identity.AspNetUserRoles AFTER INSERT,
      ADD BLOCK PREDICATE orion.fn_PublicIdentityScopePredicate(PublicSiteId) ON public_identity.AspNetUserRoles AFTER UPDATE,
      ADD FILTER PREDICATE orion.fn_PublicIdentityScopePredicate(PublicSiteId) ON public_identity.AspNetUserTokens,
      ADD BLOCK PREDICATE orion.fn_PublicIdentityScopePredicate(PublicSiteId) ON public_identity.AspNetUserTokens AFTER INSERT,
      ADD BLOCK PREDICATE orion.fn_PublicIdentityScopePredicate(PublicSiteId) ON public_identity.AspNetUserTokens AFTER UPDATE
      WITH(STATE=ON,SCHEMABINDING=ON);');

  EXEC(N'
    CREATE OR ALTER TRIGGER brunos_auth.TR_PublicIdentityBridge_UsersForward
    ON brunos_auth.AspNetUsers WITH EXECUTE AS OWNER AFTER INSERT,UPDATE,DELETE AS
    BEGIN
      SET NOCOUNT ON;
      IF NOT EXISTS(SELECT 1 FROM orion.PublicIdentityCompatibilityState WHERE SingletonId=1 AND BridgeMode=''Forward'') RETURN;
      MERGE public_identity.AspNetUsers AS target USING inserted AS source ON target.Id=source.Id
      WHEN MATCHED THEN UPDATE SET PublicSiteId=source.PublicSiteId,UserName=source.UserName,NormalizedUserName=source.NormalizedUserName,
        Email=source.Email,NormalizedEmail=source.NormalizedEmail,EmailConfirmed=source.EmailConfirmed,PasswordHash=source.PasswordHash,
        SecurityStamp=source.SecurityStamp,ConcurrencyStamp=source.ConcurrencyStamp,PhoneNumber=source.PhoneNumber,
        PhoneNumberConfirmed=source.PhoneNumberConfirmed,TwoFactorEnabled=source.TwoFactorEnabled,LockoutEnd=source.LockoutEnd,
        LockoutEnabled=source.LockoutEnabled,AccessFailedCount=source.AccessFailedCount,FirstName=source.FirstName,LastName=source.LastName,
        CreatedAt=source.CreatedAt,ClosedAt=source.ClosedAt
      WHEN NOT MATCHED THEN INSERT(Id,PublicSiteId,UserName,NormalizedUserName,Email,NormalizedEmail,EmailConfirmed,PasswordHash,
        SecurityStamp,ConcurrencyStamp,PhoneNumber,PhoneNumberConfirmed,TwoFactorEnabled,LockoutEnd,LockoutEnabled,AccessFailedCount,
        FirstName,LastName,CreatedAt,ClosedAt)
        VALUES(source.Id,source.PublicSiteId,source.UserName,source.NormalizedUserName,source.Email,source.NormalizedEmail,source.EmailConfirmed,
        source.PasswordHash,source.SecurityStamp,source.ConcurrencyStamp,source.PhoneNumber,source.PhoneNumberConfirmed,source.TwoFactorEnabled,
        source.LockoutEnd,source.LockoutEnabled,source.AccessFailedCount,source.FirstName,source.LastName,source.CreatedAt,source.ClosedAt);
      DELETE target FROM public_identity.AspNetUsers target JOIN deleted old ON old.Id=target.Id LEFT JOIN inserted currentRow ON currentRow.Id=old.Id WHERE currentRow.Id IS NULL;
    END;');

  EXEC(N'
    CREATE OR ALTER TRIGGER public_identity.TR_PublicIdentityBridge_UsersReverse
    ON public_identity.AspNetUsers WITH EXECUTE AS OWNER AFTER INSERT,UPDATE,DELETE AS
    BEGIN
      SET NOCOUNT ON;
      IF NOT EXISTS(SELECT 1 FROM orion.PublicIdentityCompatibilityState WHERE SingletonId=1 AND BridgeMode=''Reverse'') RETURN;
      MERGE brunos_auth.AspNetUsers AS target USING inserted AS source ON target.Id=source.Id
      WHEN MATCHED THEN UPDATE SET PublicSiteId=source.PublicSiteId,UserName=source.UserName,NormalizedUserName=source.NormalizedUserName,
        Email=source.Email,NormalizedEmail=source.NormalizedEmail,EmailConfirmed=source.EmailConfirmed,PasswordHash=source.PasswordHash,
        SecurityStamp=source.SecurityStamp,ConcurrencyStamp=source.ConcurrencyStamp,PhoneNumber=source.PhoneNumber,
        PhoneNumberConfirmed=source.PhoneNumberConfirmed,TwoFactorEnabled=source.TwoFactorEnabled,LockoutEnd=source.LockoutEnd,
        LockoutEnabled=source.LockoutEnabled,AccessFailedCount=source.AccessFailedCount,FirstName=source.FirstName,LastName=source.LastName,
        CreatedAt=source.CreatedAt,ClosedAt=source.ClosedAt
      WHEN NOT MATCHED THEN INSERT(Id,PublicSiteId,UserName,NormalizedUserName,Email,NormalizedEmail,EmailConfirmed,PasswordHash,
        SecurityStamp,ConcurrencyStamp,PhoneNumber,PhoneNumberConfirmed,TwoFactorEnabled,LockoutEnd,LockoutEnabled,AccessFailedCount,
        FirstName,LastName,CreatedAt,ClosedAt)
        VALUES(source.Id,source.PublicSiteId,source.UserName,source.NormalizedUserName,source.Email,source.NormalizedEmail,source.EmailConfirmed,
        source.PasswordHash,source.SecurityStamp,source.ConcurrencyStamp,source.PhoneNumber,source.PhoneNumberConfirmed,source.TwoFactorEnabled,
        source.LockoutEnd,source.LockoutEnabled,source.AccessFailedCount,source.FirstName,source.LastName,source.CreatedAt,source.ClosedAt);
      DELETE target FROM brunos_auth.AspNetUsers target JOIN deleted old ON old.Id=target.Id LEFT JOIN inserted currentRow ON currentRow.Id=old.Id WHERE currentRow.Id IS NULL;
    END;');

  EXEC(N'
    CREATE OR ALTER TRIGGER brunos_auth.TR_PublicIdentityBridge_RolesForward
    ON brunos_auth.AspNetRoles WITH EXECUTE AS OWNER AFTER INSERT,UPDATE,DELETE AS
    BEGIN
      SET NOCOUNT ON;
      IF NOT EXISTS(SELECT 1 FROM orion.PublicIdentityCompatibilityState WHERE SingletonId=1 AND BridgeMode=''Forward'') RETURN;
      DECLARE @PublicSiteId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N''OrionERP.PublicSiteId''));
      IF @PublicSiteId IS NULL AND EXISTS(SELECT 1 FROM inserted)
      BEGIN
        IF (SELECT COUNT(*) FROM orion.PublicSite WHERE ModuleCode=N''RESTAURANT'' AND IsActive=1)<>1 THROW 52434,''No se puede inferir el PublicSite de un rol heredado.'',1;
        SELECT @PublicSiteId=PublicSiteId FROM orion.PublicSite WHERE ModuleCode=N''RESTAURANT'' AND IsActive=1;
      END;
      MERGE public_identity.AspNetRoles target USING inserted source ON target.Id=source.Id
      WHEN MATCHED THEN UPDATE SET PublicSiteId=@PublicSiteId,[Name]=source.[Name],NormalizedName=source.NormalizedName,ConcurrencyStamp=source.ConcurrencyStamp
      WHEN NOT MATCHED THEN INSERT(Id,PublicSiteId,[Name],NormalizedName,ConcurrencyStamp) VALUES(source.Id,@PublicSiteId,source.[Name],source.NormalizedName,source.ConcurrencyStamp);
      DELETE target FROM public_identity.AspNetRoles target JOIN deleted old ON old.Id=target.Id LEFT JOIN inserted currentRow ON currentRow.Id=old.Id WHERE currentRow.Id IS NULL;
    END;');

  EXEC(N'
    CREATE OR ALTER TRIGGER public_identity.TR_PublicIdentityBridge_RolesReverse
    ON public_identity.AspNetRoles WITH EXECUTE AS OWNER AFTER INSERT,UPDATE,DELETE AS
    BEGIN
      SET NOCOUNT ON;
      IF NOT EXISTS(SELECT 1 FROM orion.PublicIdentityCompatibilityState WHERE SingletonId=1 AND BridgeMode=''Reverse'') RETURN;
      MERGE brunos_auth.AspNetRoles target USING inserted source ON target.Id=source.Id
      WHEN MATCHED THEN UPDATE SET [Name]=source.[Name],NormalizedName=source.NormalizedName,ConcurrencyStamp=source.ConcurrencyStamp
      WHEN NOT MATCHED THEN INSERT(Id,[Name],NormalizedName,ConcurrencyStamp) VALUES(source.Id,source.[Name],source.NormalizedName,source.ConcurrencyStamp);
      DELETE target FROM brunos_auth.AspNetRoles target JOIN deleted old ON old.Id=target.Id LEFT JOIN inserted currentRow ON currentRow.Id=old.Id WHERE currentRow.Id IS NULL;
    END;');

  EXEC(N'
    CREATE OR ALTER TRIGGER brunos_auth.TR_PublicIdentityBridge_UserClaimsForward
    ON brunos_auth.AspNetUserClaims WITH EXECUTE AS OWNER AFTER INSERT,UPDATE,DELETE AS
    BEGIN
      SET NOCOUNT ON;
      IF NOT EXISTS(SELECT 1 FROM orion.PublicIdentityCompatibilityState WHERE SingletonId=1 AND BridgeMode=''Forward'') RETURN;
      SET IDENTITY_INSERT public_identity.AspNetUserClaims ON;
      MERGE public_identity.AspNetUserClaims target
      USING (SELECT source.Id,identityUser.PublicSiteId,source.UserId,source.ClaimType,source.ClaimValue FROM inserted source JOIN brunos_auth.AspNetUsers identityUser ON identityUser.Id=source.UserId) source
      ON target.Id=source.Id
      WHEN MATCHED THEN UPDATE SET PublicSiteId=source.PublicSiteId,UserId=source.UserId,ClaimType=source.ClaimType,ClaimValue=source.ClaimValue
      WHEN NOT MATCHED THEN INSERT(Id,PublicSiteId,UserId,ClaimType,ClaimValue) VALUES(source.Id,source.PublicSiteId,source.UserId,source.ClaimType,source.ClaimValue);
      SET IDENTITY_INSERT public_identity.AspNetUserClaims OFF;
      DELETE target FROM public_identity.AspNetUserClaims target JOIN deleted old ON old.Id=target.Id LEFT JOIN inserted currentRow ON currentRow.Id=old.Id WHERE currentRow.Id IS NULL;
    END;');

  EXEC(N'
    CREATE OR ALTER TRIGGER public_identity.TR_PublicIdentityBridge_UserClaimsReverse
    ON public_identity.AspNetUserClaims WITH EXECUTE AS OWNER AFTER INSERT,UPDATE,DELETE AS
    BEGIN
      SET NOCOUNT ON;
      IF NOT EXISTS(SELECT 1 FROM orion.PublicIdentityCompatibilityState WHERE SingletonId=1 AND BridgeMode=''Reverse'') RETURN;
      SET IDENTITY_INSERT brunos_auth.AspNetUserClaims ON;
      MERGE brunos_auth.AspNetUserClaims target USING inserted source ON target.Id=source.Id
      WHEN MATCHED THEN UPDATE SET UserId=source.UserId,ClaimType=source.ClaimType,ClaimValue=source.ClaimValue
      WHEN NOT MATCHED THEN INSERT(Id,UserId,ClaimType,ClaimValue) VALUES(source.Id,source.UserId,source.ClaimType,source.ClaimValue);
      SET IDENTITY_INSERT brunos_auth.AspNetUserClaims OFF;
      DELETE target FROM brunos_auth.AspNetUserClaims target JOIN deleted old ON old.Id=target.Id LEFT JOIN inserted currentRow ON currentRow.Id=old.Id WHERE currentRow.Id IS NULL;
    END;');

  EXEC(N'
    CREATE OR ALTER TRIGGER brunos_auth.TR_PublicIdentityBridge_RoleClaimsForward
    ON brunos_auth.AspNetRoleClaims WITH EXECUTE AS OWNER AFTER INSERT,UPDATE,DELETE AS
    BEGIN
      SET NOCOUNT ON;
      IF NOT EXISTS(SELECT 1 FROM orion.PublicIdentityCompatibilityState WHERE SingletonId=1 AND BridgeMode=''Forward'') RETURN;
      SET IDENTITY_INSERT public_identity.AspNetRoleClaims ON;
      MERGE public_identity.AspNetRoleClaims target
      USING (SELECT source.Id,roleInfo.PublicSiteId,source.RoleId,source.ClaimType,source.ClaimValue FROM inserted source JOIN public_identity.AspNetRoles roleInfo ON roleInfo.Id=source.RoleId) source
      ON target.Id=source.Id
      WHEN MATCHED THEN UPDATE SET PublicSiteId=source.PublicSiteId,RoleId=source.RoleId,ClaimType=source.ClaimType,ClaimValue=source.ClaimValue
      WHEN NOT MATCHED THEN INSERT(Id,PublicSiteId,RoleId,ClaimType,ClaimValue) VALUES(source.Id,source.PublicSiteId,source.RoleId,source.ClaimType,source.ClaimValue);
      SET IDENTITY_INSERT public_identity.AspNetRoleClaims OFF;
      DELETE target FROM public_identity.AspNetRoleClaims target JOIN deleted old ON old.Id=target.Id LEFT JOIN inserted currentRow ON currentRow.Id=old.Id WHERE currentRow.Id IS NULL;
    END;');

  EXEC(N'
    CREATE OR ALTER TRIGGER public_identity.TR_PublicIdentityBridge_RoleClaimsReverse
    ON public_identity.AspNetRoleClaims WITH EXECUTE AS OWNER AFTER INSERT,UPDATE,DELETE AS
    BEGIN
      SET NOCOUNT ON;
      IF NOT EXISTS(SELECT 1 FROM orion.PublicIdentityCompatibilityState WHERE SingletonId=1 AND BridgeMode=''Reverse'') RETURN;
      SET IDENTITY_INSERT brunos_auth.AspNetRoleClaims ON;
      MERGE brunos_auth.AspNetRoleClaims target USING inserted source ON target.Id=source.Id
      WHEN MATCHED THEN UPDATE SET RoleId=source.RoleId,ClaimType=source.ClaimType,ClaimValue=source.ClaimValue
      WHEN NOT MATCHED THEN INSERT(Id,RoleId,ClaimType,ClaimValue) VALUES(source.Id,source.RoleId,source.ClaimType,source.ClaimValue);
      SET IDENTITY_INSERT brunos_auth.AspNetRoleClaims OFF;
      DELETE target FROM brunos_auth.AspNetRoleClaims target JOIN deleted old ON old.Id=target.Id LEFT JOIN inserted currentRow ON currentRow.Id=old.Id WHERE currentRow.Id IS NULL;
    END;');

  EXEC(N'
    CREATE OR ALTER TRIGGER brunos_auth.TR_PublicIdentityBridge_UserLoginsForward
    ON brunos_auth.AspNetUserLogins WITH EXECUTE AS OWNER AFTER INSERT,UPDATE,DELETE AS
    BEGIN
      SET NOCOUNT ON;
      IF NOT EXISTS(SELECT 1 FROM orion.PublicIdentityCompatibilityState WHERE SingletonId=1 AND BridgeMode=''Forward'') RETURN;
      MERGE public_identity.AspNetUserLogins target
      USING (SELECT identityUser.PublicSiteId,source.LoginProvider,source.ProviderKey,source.ProviderDisplayName,source.UserId FROM inserted source JOIN brunos_auth.AspNetUsers identityUser ON identityUser.Id=source.UserId) source
      ON target.PublicSiteId=source.PublicSiteId AND target.LoginProvider=source.LoginProvider AND target.ProviderKey=source.ProviderKey
      WHEN MATCHED THEN UPDATE SET ProviderDisplayName=source.ProviderDisplayName,UserId=source.UserId
      WHEN NOT MATCHED THEN INSERT(PublicSiteId,LoginProvider,ProviderKey,ProviderDisplayName,UserId) VALUES(source.PublicSiteId,source.LoginProvider,source.ProviderKey,source.ProviderDisplayName,source.UserId);
      DELETE target FROM public_identity.AspNetUserLogins target JOIN deleted old ON old.LoginProvider=target.LoginProvider AND old.ProviderKey=target.ProviderKey LEFT JOIN inserted currentRow ON currentRow.LoginProvider=old.LoginProvider AND currentRow.ProviderKey=old.ProviderKey WHERE currentRow.ProviderKey IS NULL;
    END;');

  EXEC(N'
    CREATE OR ALTER TRIGGER public_identity.TR_PublicIdentityBridge_UserLoginsReverse
    ON public_identity.AspNetUserLogins WITH EXECUTE AS OWNER AFTER INSERT,UPDATE,DELETE AS
    BEGIN
      SET NOCOUNT ON;
      IF NOT EXISTS(SELECT 1 FROM orion.PublicIdentityCompatibilityState WHERE SingletonId=1 AND BridgeMode=''Reverse'') RETURN;
      MERGE brunos_auth.AspNetUserLogins target USING inserted source ON target.LoginProvider=source.LoginProvider AND target.ProviderKey=source.ProviderKey
      WHEN MATCHED THEN UPDATE SET ProviderDisplayName=source.ProviderDisplayName,UserId=source.UserId
      WHEN NOT MATCHED THEN INSERT(LoginProvider,ProviderKey,ProviderDisplayName,UserId) VALUES(source.LoginProvider,source.ProviderKey,source.ProviderDisplayName,source.UserId);
      DELETE target FROM brunos_auth.AspNetUserLogins target JOIN deleted old ON old.LoginProvider=target.LoginProvider AND old.ProviderKey=target.ProviderKey LEFT JOIN inserted currentRow ON currentRow.PublicSiteId=old.PublicSiteId AND currentRow.LoginProvider=old.LoginProvider AND currentRow.ProviderKey=old.ProviderKey WHERE currentRow.ProviderKey IS NULL;
    END;');

  EXEC(N'
    CREATE OR ALTER TRIGGER brunos_auth.TR_PublicIdentityBridge_UserRolesForward
    ON brunos_auth.AspNetUserRoles WITH EXECUTE AS OWNER AFTER INSERT,UPDATE,DELETE AS
    BEGIN
      SET NOCOUNT ON;
      IF NOT EXISTS(SELECT 1 FROM orion.PublicIdentityCompatibilityState WHERE SingletonId=1 AND BridgeMode=''Forward'') RETURN;
      MERGE public_identity.AspNetUserRoles target
      USING (SELECT identityUser.PublicSiteId,source.UserId,source.RoleId FROM inserted source JOIN brunos_auth.AspNetUsers identityUser ON identityUser.Id=source.UserId) source
      ON target.PublicSiteId=source.PublicSiteId AND target.UserId=source.UserId AND target.RoleId=source.RoleId
      WHEN NOT MATCHED THEN INSERT(PublicSiteId,UserId,RoleId) VALUES(source.PublicSiteId,source.UserId,source.RoleId);
      DELETE target FROM public_identity.AspNetUserRoles target JOIN deleted old ON old.UserId=target.UserId AND old.RoleId=target.RoleId LEFT JOIN inserted currentRow ON currentRow.UserId=old.UserId AND currentRow.RoleId=old.RoleId WHERE currentRow.UserId IS NULL;
    END;');

  EXEC(N'
    CREATE OR ALTER TRIGGER public_identity.TR_PublicIdentityBridge_UserRolesReverse
    ON public_identity.AspNetUserRoles WITH EXECUTE AS OWNER AFTER INSERT,UPDATE,DELETE AS
    BEGIN
      SET NOCOUNT ON;
      IF NOT EXISTS(SELECT 1 FROM orion.PublicIdentityCompatibilityState WHERE SingletonId=1 AND BridgeMode=''Reverse'') RETURN;
      MERGE brunos_auth.AspNetUserRoles target USING inserted source ON target.UserId=source.UserId AND target.RoleId=source.RoleId
      WHEN NOT MATCHED THEN INSERT(UserId,RoleId) VALUES(source.UserId,source.RoleId);
      DELETE target FROM brunos_auth.AspNetUserRoles target JOIN deleted old ON old.UserId=target.UserId AND old.RoleId=target.RoleId LEFT JOIN inserted currentRow ON currentRow.PublicSiteId=old.PublicSiteId AND currentRow.UserId=old.UserId AND currentRow.RoleId=old.RoleId WHERE currentRow.UserId IS NULL;
    END;');

  EXEC(N'
    CREATE OR ALTER TRIGGER brunos_auth.TR_PublicIdentityBridge_UserTokensForward
    ON brunos_auth.AspNetUserTokens WITH EXECUTE AS OWNER AFTER INSERT,UPDATE,DELETE AS
    BEGIN
      SET NOCOUNT ON;
      IF NOT EXISTS(SELECT 1 FROM orion.PublicIdentityCompatibilityState WHERE SingletonId=1 AND BridgeMode=''Forward'') RETURN;
      MERGE public_identity.AspNetUserTokens target
      USING (SELECT identityUser.PublicSiteId,source.UserId,source.LoginProvider,source.[Name],source.[Value] FROM inserted source JOIN brunos_auth.AspNetUsers identityUser ON identityUser.Id=source.UserId) source
      ON target.PublicSiteId=source.PublicSiteId AND target.UserId=source.UserId AND target.LoginProvider=source.LoginProvider AND target.[Name]=source.[Name]
      WHEN MATCHED THEN UPDATE SET [Value]=source.[Value]
      WHEN NOT MATCHED THEN INSERT(PublicSiteId,UserId,LoginProvider,[Name],[Value]) VALUES(source.PublicSiteId,source.UserId,source.LoginProvider,source.[Name],source.[Value]);
      DELETE target FROM public_identity.AspNetUserTokens target JOIN deleted old ON old.UserId=target.UserId AND old.LoginProvider=target.LoginProvider AND old.[Name]=target.[Name] LEFT JOIN inserted currentRow ON currentRow.UserId=old.UserId AND currentRow.LoginProvider=old.LoginProvider AND currentRow.[Name]=old.[Name] WHERE currentRow.UserId IS NULL;
    END;');

  EXEC(N'
    CREATE OR ALTER TRIGGER public_identity.TR_PublicIdentityBridge_UserTokensReverse
    ON public_identity.AspNetUserTokens WITH EXECUTE AS OWNER AFTER INSERT,UPDATE,DELETE AS
    BEGIN
      SET NOCOUNT ON;
      IF NOT EXISTS(SELECT 1 FROM orion.PublicIdentityCompatibilityState WHERE SingletonId=1 AND BridgeMode=''Reverse'') RETURN;
      MERGE brunos_auth.AspNetUserTokens target USING inserted source ON target.UserId=source.UserId AND target.LoginProvider=source.LoginProvider AND target.[Name]=source.[Name]
      WHEN MATCHED THEN UPDATE SET [Value]=source.[Value]
      WHEN NOT MATCHED THEN INSERT(UserId,LoginProvider,[Name],[Value]) VALUES(source.UserId,source.LoginProvider,source.[Name],source.[Value]);
      DELETE target FROM brunos_auth.AspNetUserTokens target JOIN deleted old ON old.UserId=target.UserId AND old.LoginProvider=target.LoginProvider AND old.[Name]=target.[Name] LEFT JOIN inserted currentRow ON currentRow.PublicSiteId=old.PublicSiteId AND currentRow.UserId=old.UserId AND currentRow.LoginProvider=old.LoginProvider AND currentRow.[Name]=old.[Name] WHERE currentRow.UserId IS NULL;
    END;');

  EXEC(N'
    CREATE OR ALTER PROCEDURE orion.SetPublicIdentityBridgeMode
      @BridgeMode varchar(10),@UpdatedBy nvarchar(256)
    WITH EXECUTE AS OWNER AS
    BEGIN
      SET NOCOUNT ON; SET XACT_ABORT ON;
      IF @BridgeMode NOT IN(''Forward'',''Reverse'',''Off'') THROW 52431,''BridgeMode inválido.'',1;
      IF NULLIF(LTRIM(RTRIM(@UpdatedBy)),N'''') IS NULL THROW 52432,''UpdatedBy es obligatorio.'',1;
      BEGIN TRANSACTION;
      UPDATE orion.PublicIdentityCompatibilityState WITH(UPDLOCK,HOLDLOCK)
      SET BridgeMode=@BridgeMode,UpdatedAtUtc=SYSUTCDATETIME(),UpdatedBy=@UpdatedBy WHERE SingletonId=1;
      COMMIT TRANSACTION;
      SELECT BridgeMode,UpdatedAtUtc,UpdatedBy FROM orion.PublicIdentityCompatibilityState WHERE SingletonId=1;
    END;');

  IF (SELECT COUNT_BIG(*) FROM public_identity.AspNetUsers)<>(SELECT COUNT_BIG(*) FROM brunos_auth.AspNetUsers)
    THROW 52433,'El conteo de usuarios no coincide después de la copia.',1;

  IF @ApplyChanges=1
  BEGIN
    INSERT orion.SchemaMigration(MigrationId,Checksum,AppliedBy,AppVersion,DatabaseName)
    VALUES(@MigrationId,@MigrationChecksum,
      COALESCE(CONVERT(nvarchar(256),SESSION_CONTEXT(N'OrionERP.UserName')),CONVERT(nvarchar(256),ORIGINAL_LOGIN())),
      @AppVersion,DB_NAME());
    COMMIT TRANSACTION;
    SELECT N'APLICADO' Estado,@MigrationId MigrationId,(SELECT COUNT_BIG(*) FROM public_identity.AspNetUsers) UsersCopied,N'Forward' BridgeMode;
  END
  ELSE
  BEGIN
    SELECT N'PREVIEW' Estado,@MigrationId MigrationId,(SELECT COUNT_BIG(*) FROM public_identity.AspNetUsers) UsersCopied,N'Forward' BridgeMode;
    ROLLBACK TRANSACTION;
  END;
END TRY
BEGIN CATCH
  IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
  THROW;
END CATCH;
