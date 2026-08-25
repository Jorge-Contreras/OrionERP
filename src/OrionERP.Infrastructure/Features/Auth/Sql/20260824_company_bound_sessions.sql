/*
  OrionERP company-bound session foundation.

  Safe default: preview only. To apply on the same SQL connection first run:
    EXEC sys.sp_set_session_context @key=N'ApplyChanges', @value=1;
  The deployment wrapper sets this value and executes the full file in one
  connection. Without it, all changes are rolled back after the report.
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;

DECLARE @ApplyChanges bit = COALESCE(TRY_CONVERT(bit, SESSION_CONTEXT(N'ApplyChanges')), 0);

BEGIN TRANSACTION;

IF SCHEMA_ID(N'orion') IS NULL
  EXEC(N'CREATE SCHEMA orion AUTHORIZATION dbo;');

IF OBJECT_ID(N'orion.Company', N'U') IS NULL
BEGIN
  CREATE TABLE orion.Company
  (
    Rfc varchar(50) NOT NULL CONSTRAINT PK_orion_Company PRIMARY KEY,
    DisplayName nvarchar(200) NOT NULL,
    LegalName nvarchar(300) NULL,
    IsActive bit NOT NULL CONSTRAINT DF_orion_Company_IsActive DEFAULT (1),
    LogoBytes varbinary(max) NULL,
    LogoContentType varchar(50) NULL,
    BrandingVersion bigint NOT NULL CONSTRAINT DF_orion_Company_BrandingVersion DEFAULT (1),
    CreatedAtUtc datetime2(0) NOT NULL CONSTRAINT DF_orion_Company_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
    UpdatedAtUtc datetime2(0) NOT NULL CONSTRAINT DF_orion_Company_UpdatedAtUtc DEFAULT (SYSUTCDATETIME()),
    UpdatedBy nvarchar(450) NULL,
    RowVersion rowversion NOT NULL,
    -- Existing OrionERP company identifiers include Bruno's historical
    -- BRUNOS260707L26 value. Keep the directory strict but compatible.
    CONSTRAINT CK_orion_Company_Rfc CHECK (LEN(LTRIM(RTRIM(Rfc))) BETWEEN 12 AND 20),
    CONSTRAINT CK_orion_Company_Logo CHECK
    (
      (LogoBytes IS NULL AND LogoContentType IS NULL)
      OR (LogoBytes IS NOT NULL AND LogoContentType IN ('image/png','image/jpeg','image/webp'))
    )
  );
END;

IF COL_LENGTH(N'auth.AspNetRoles', N'Scope') IS NULL
BEGIN
  ALTER TABLE auth.AspNetRoles ADD [Scope] varchar(20) NOT NULL
    CONSTRAINT DF_AspNetRoles_Scope DEFAULT ('Company');
END;

EXEC(N'
UPDATE auth.AspNetRoles
SET [Scope] = CASE WHEN [Name] IN (N''Administrador'', N''Arrendadores'') THEN ''Global'' ELSE ''Company'' END
WHERE [Scope] IS NULL
   OR [Scope] NOT IN (''Global'',''Company'')
   OR ([Name] IN (N''Administrador'', N''Arrendadores'') AND [Scope] <> ''Global'');');

IF NOT EXISTS
(
  SELECT 1 FROM sys.check_constraints
  WHERE parent_object_id = OBJECT_ID(N'auth.AspNetRoles') AND [name] = N'CK_AspNetRoles_Scope'
)
  EXEC(N'ALTER TABLE auth.AspNetRoles WITH CHECK ADD CONSTRAINT CK_AspNetRoles_Scope CHECK ([Scope] IN (''Global'',''Company''));');

IF NOT EXISTS
(
  SELECT 1 FROM sys.indexes
  WHERE object_id = OBJECT_ID(N'dbo.Capital_Humano') AND [name] = N'UX_Capital_Humano_ID_RFC'
)
  CREATE UNIQUE INDEX UX_Capital_Humano_ID_RFC ON dbo.Capital_Humano(ID, RFC);

IF OBJECT_ID(N'auth.AspNetUserCompanies', N'U') IS NULL
BEGIN
  CREATE TABLE auth.AspNetUserCompanies
  (
    UserId nvarchar(450) NOT NULL,
    Rfc varchar(50) NOT NULL,
    EmployeeId int NULL,
    IsActive bit NOT NULL CONSTRAINT DF_AspNetUserCompanies_IsActive DEFAULT (1),
    AccessReviewRequired bit NOT NULL CONSTRAINT DF_AspNetUserCompanies_Review DEFAULT (0),
    AccessReviewedAtUtc datetime2(0) NULL,
    AccessReviewedBy nvarchar(450) NULL,
    CreatedAtUtc datetime2(0) NOT NULL CONSTRAINT DF_AspNetUserCompanies_Created DEFAULT (SYSUTCDATETIME()),
    UpdatedAtUtc datetime2(0) NOT NULL CONSTRAINT DF_AspNetUserCompanies_Updated DEFAULT (SYSUTCDATETIME()),
    UpdatedBy nvarchar(450) NULL,
    RowVersion rowversion NOT NULL,
    CONSTRAINT PK_AspNetUserCompanies PRIMARY KEY (UserId, Rfc),
    CONSTRAINT FK_AspNetUserCompanies_User FOREIGN KEY (UserId)
      REFERENCES auth.AspNetUsers(Id) ON DELETE CASCADE,
    CONSTRAINT FK_AspNetUserCompanies_Company FOREIGN KEY (Rfc)
      REFERENCES orion.Company(Rfc),
    CONSTRAINT FK_AspNetUserCompanies_EmployeeCompany FOREIGN KEY (EmployeeId, Rfc)
      REFERENCES dbo.Capital_Humano(ID, RFC)
  );

  CREATE UNIQUE INDEX UX_AspNetUserCompanies_EmployeeId
    ON auth.AspNetUserCompanies(EmployeeId) WHERE EmployeeId IS NOT NULL;
  CREATE INDEX IX_AspNetUserCompanies_RfcActive
    ON auth.AspNetUserCompanies(Rfc, IsActive) INCLUDE(UserId, EmployeeId, AccessReviewRequired);
END;

IF OBJECT_ID(N'auth.AspNetUserCompanyRoles', N'U') IS NULL
BEGIN
  CREATE TABLE auth.AspNetUserCompanyRoles
  (
    UserId nvarchar(450) NOT NULL,
    Rfc varchar(50) NOT NULL,
    RoleId nvarchar(450) NOT NULL,
    CONSTRAINT PK_AspNetUserCompanyRoles PRIMARY KEY(UserId, Rfc, RoleId),
    CONSTRAINT FK_AspNetUserCompanyRoles_Membership FOREIGN KEY(UserId, Rfc)
      REFERENCES auth.AspNetUserCompanies(UserId, Rfc) ON DELETE CASCADE,
    CONSTRAINT FK_AspNetUserCompanyRoles_Role FOREIGN KEY(RoleId)
      REFERENCES auth.AspNetRoles(Id) ON DELETE CASCADE
  );
  CREATE INDEX IX_AspNetUserCompanyRoles_RoleId ON auth.AspNetUserCompanyRoles(RoleId);
END;

IF OBJECT_ID(N'auth.CompanyAccessAudit', N'U') IS NULL
BEGIN
  CREATE TABLE auth.CompanyAccessAudit
  (
    Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_CompanyAccessAudit PRIMARY KEY,
    OccurredAtUtc datetime2(0) NOT NULL CONSTRAINT DF_CompanyAccessAudit_Occurred DEFAULT (SYSUTCDATETIME()),
    ActorUserId nvarchar(450) NOT NULL,
    Action varchar(80) NOT NULL,
    TargetUserId nvarchar(450) NULL,
    Rfc varchar(50) NULL,
    RoleId nvarchar(450) NULL,
    DetailJson nvarchar(max) NULL,
    CONSTRAINT CK_CompanyAccessAudit_DetailJson CHECK (DetailJson IS NULL OR ISJSON(DetailJson)=1)
  );
  CREATE INDEX IX_CompanyAccessAudit_TargetTime
    ON auth.CompanyAccessAudit(TargetUserId, OccurredAtUtc DESC);
  CREATE INDEX IX_CompanyAccessAudit_RfcTime
    ON auth.CompanyAccessAudit(Rfc, OccurredAtUtc DESC);
END;

-- Companies are durable directory records. They may be deactivated, but their
-- RFC identity must never be rewritten or removed.
EXEC(N'
CREATE OR ALTER TRIGGER orion.TR_Company_BlockDelete
ON orion.Company
INSTEAD OF DELETE
AS
BEGIN
  SET NOCOUNT ON;
  THROW 51001, ''Companies cannot be deleted. Deactivate the company instead.'', 1;
END;');

EXEC(N'
CREATE OR ALTER TRIGGER orion.TR_Company_ImmutableRfc
ON orion.Company
AFTER UPDATE
AS
BEGIN
  SET NOCOUNT ON;
  IF UPDATE(Rfc) AND EXISTS
  (
    SELECT 1
    FROM inserted currentRow
    FULL OUTER JOIN deleted previousRow ON previousRow.Rfc=currentRow.Rfc
    WHERE currentRow.Rfc IS NULL OR previousRow.Rfc IS NULL
  )
    THROW 51002, ''A company RFC is immutable.'', 1;
END;');

-- Access history is evidence, not mutable application data.
EXEC(N'
CREATE OR ALTER TRIGGER auth.TR_CompanyAccessAudit_AppendOnly
ON auth.CompanyAccessAudit
INSTEAD OF UPDATE, DELETE
AS
BEGIN
  SET NOCOUNT ON;
  THROW 51003, ''Company access audit records are append-only.'', 1;
END;');

;WITH CompanySource AS
(
  SELECT UPPER(LTRIM(RTRIM(ClaimValue))) Rfc, CAST(1 AS bit) IsActive
  FROM auth.AspNetUserClaims
  WHERE ClaimType=N'rfc' AND NULLIF(LTRIM(RTRIM(ClaimValue)), '') IS NOT NULL
  UNION
  SELECT UPPER(LTRIM(RTRIM(Rfc))), CAST(0 AS bit)
  FROM dbo.SatRfcProfile
  WHERE NULLIF(LTRIM(RTRIM(Rfc)), '') IS NOT NULL
), CompanyRollup AS
(
  SELECT Rfc, MAX(CONVERT(tinyint, IsActive)) IsActive
  FROM CompanySource GROUP BY Rfc
)
INSERT orion.Company(Rfc, DisplayName, LegalName, IsActive, UpdatedBy)
SELECT source.Rfc,
       COALESCE(NULLIF(LTRIM(RTRIM(profile.NombreComercial)), ''),
                CASE source.Rfc WHEN 'BRUNOS260707L26' THEN N'Bruno''s' ELSE NULL END,
                NULLIF(LTRIM(RTRIM(profile.RazonSocial)), ''), source.Rfc),
       NULLIF(LTRIM(RTRIM(profile.RazonSocial)), ''),
       source.IsActive,
       N'20260824_company_bound_sessions'
FROM CompanyRollup source
LEFT JOIN dbo.SatRfcProfile profile ON UPPER(LTRIM(RTRIM(profile.Rfc)))=source.Rfc
WHERE NOT EXISTS(SELECT 1 FROM orion.Company existing WHERE existing.Rfc=source.Rfc);

INSERT auth.AspNetUserCompanies(UserId, Rfc, IsActive, UpdatedBy)
SELECT DISTINCT claim.UserId, UPPER(LTRIM(RTRIM(claim.ClaimValue))), 1,
       N'20260824_company_bound_sessions'
FROM auth.AspNetUserClaims claim
JOIN orion.Company company ON company.Rfc=UPPER(LTRIM(RTRIM(claim.ClaimValue)))
WHERE claim.ClaimType=N'rfc'
  AND NULLIF(LTRIM(RTRIM(claim.ClaimValue)), '') IS NOT NULL
  AND NOT EXISTS
  (
    SELECT 1 FROM auth.AspNetUserCompanies existing
    WHERE existing.UserId=claim.UserId AND existing.Rfc=UPPER(LTRIM(RTRIM(claim.ClaimValue)))
  );

UPDATE membership
SET EmployeeId=[user].EmployeeId,
    UpdatedAtUtc=SYSUTCDATETIME(),
    UpdatedBy=N'20260824_company_bound_sessions'
FROM auth.AspNetUserCompanies membership
JOIN auth.AspNetUsers [user] ON [user].Id=membership.UserId
JOIN dbo.Capital_Humano employee ON employee.ID=[user].EmployeeId AND employee.RFC=membership.Rfc
WHERE membership.EmployeeId IS NULL
  AND NOT EXISTS
  (
    SELECT 1 FROM auth.AspNetUserCompanies other
    WHERE other.EmployeeId=[user].EmployeeId
  );

;WITH Candidate AS
(
  SELECT target.UserId,target.Rfc,MIN(employee.ID) EmployeeId,COUNT_BIG(*) CandidateCount
  FROM auth.AspNetUserCompanies target
  JOIN auth.AspNetUserCompanies linked ON linked.UserId=target.UserId AND linked.EmployeeId IS NOT NULL
  JOIN dbo.Capital_Humano sourceEmployee ON sourceEmployee.ID=linked.EmployeeId
  JOIN dbo.Capital_Humano employee
    ON employee.RFC=target.Rfc
   AND NULLIF(UPPER(LTRIM(RTRIM(employee.CURP))), '')=NULLIF(UPPER(LTRIM(RTRIM(sourceEmployee.CURP))), '')
   AND UPPER(LTRIM(RTRIM(ISNULL(employee.[Status], ''))))='ACTIVO'
  WHERE target.EmployeeId IS NULL
    AND NULLIF(LTRIM(RTRIM(sourceEmployee.CURP)), '') IS NOT NULL
    AND NOT EXISTS(SELECT 1 FROM auth.AspNetUserCompanies used WHERE used.EmployeeId=employee.ID)
  GROUP BY target.UserId,target.Rfc
)
UPDATE membership
SET EmployeeId=candidate.EmployeeId,
    UpdatedAtUtc=SYSUTCDATETIME(),
    UpdatedBy=N'20260824_company_bound_sessions:CURP'
FROM auth.AspNetUserCompanies membership
JOIN Candidate candidate ON candidate.UserId=membership.UserId AND candidate.Rfc=membership.Rfc
WHERE candidate.CandidateCount=1;

INSERT auth.AspNetUserCompanyRoles(UserId,Rfc,RoleId)
SELECT membership.UserId,membership.Rfc,userRole.RoleId
FROM auth.AspNetUserCompanies membership
JOIN auth.AspNetUserRoles userRole ON userRole.UserId=membership.UserId
JOIN auth.AspNetRoles roleInfo ON roleInfo.Id=userRole.RoleId AND roleInfo.[Scope]='Company'
WHERE NOT EXISTS
(
  SELECT 1 FROM auth.AspNetUserCompanyRoles existing
  WHERE existing.UserId=membership.UserId AND existing.Rfc=membership.Rfc AND existing.RoleId=userRole.RoleId
);

;WITH MultiCompany AS
(
  SELECT UserId FROM auth.AspNetUserCompanies WHERE IsActive=1 GROUP BY UserId HAVING COUNT(*)>1
)
UPDATE membership
SET AccessReviewRequired=1,
    UpdatedAtUtc=SYSUTCDATETIME(),
    UpdatedBy=N'20260824_company_bound_sessions'
FROM auth.AspNetUserCompanies membership
JOIN MultiCompany multi ON multi.UserId=membership.UserId
WHERE membership.AccessReviewedAtUtc IS NULL;

SELECT company.Rfc,company.DisplayName,company.LegalName,company.IsActive,
       (SELECT COUNT(*) FROM auth.AspNetUserCompanies membership WHERE membership.Rfc=company.Rfc) MemberCount,
       (SELECT COUNT(*) FROM dbo.Capital_Humano employee WHERE employee.RFC=company.Rfc) EmployeeCount,
       (SELECT COUNT(*) FROM auth.AspNetUserCompanies membership WHERE membership.Rfc=company.Rfc AND membership.AccessReviewRequired=1) ReviewCount
FROM orion.Company company
ORDER BY company.Rfc;

SELECT [user].Email,membership.Rfc,membership.EmployeeId,membership.AccessReviewRequired,
       COUNT(companyRole.RoleId) CompanyRoleCount
FROM auth.AspNetUserCompanies membership
JOIN auth.AspNetUsers [user] ON [user].Id=membership.UserId
LEFT JOIN auth.AspNetUserCompanyRoles companyRole
  ON companyRole.UserId=membership.UserId AND companyRole.Rfc=membership.Rfc
GROUP BY [user].Email,membership.Rfc,membership.EmployeeId,membership.AccessReviewRequired
ORDER BY [user].Email,membership.Rfc;

IF @ApplyChanges=1
BEGIN
  COMMIT TRANSACTION;
  PRINT 'Company-bound session migration applied.';
END
ELSE
BEGIN
  ROLLBACK TRANSACTION;
  PRINT 'Preview only: all company-bound session changes were rolled back.';
END;
