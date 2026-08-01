/*
    Bruno's membership email-confirmation test reset

    Purpose
      Removes only the configured disposable test registrations, including
      confirmed accounts, so the same email addresses can be registered again
      through brunosgarden.com.

    Safe operating procedure
      1. Keep @Execute = 0 and run the script. Review both preview result sets.
      2. Confirm every row is a disposable email-confirmation test account.
      3. Set @Execute = 1 and set @Confirmation to the exact phrase below.
      4. Run the complete script again.

    Safety guarantees
      - Runs only against the grupocarpio database.
      - Targets only the email addresses listed in @TestEmails.
      - Allows pending or confirmed/active test members, including their
        confirmation-generated consents and QR tokens.
      - Refuses to delete points, ledger entries, closure requests, restaurant
        orders, redemptions, or other membership states.
      - Creates timestamped recovery copies in codex_recovery before deleting.
      - Performs all deletes in one transaction.

    This script does not send or resend confirmation email. It only removes
    disposable test registrations so the registration flow can be repeated.
*/

USE [grupocarpio];

SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET ARITHABORT ON;
SET NUMERIC_ROUNDABORT OFF;

DECLARE @Execute bit = 0;
DECLARE @Confirmation nvarchar(100) =  N'RESET BRUNO TEST MEMBERS';
DECLARE @RequiredConfirmation nvarchar(100) = N'RESET BRUNO TEST MEMBERS';

DECLARE @TestEmails table
(
    NormalizedEmail nvarchar(256) NOT NULL PRIMARY KEY
);

-- Add or remove disposable test addresses here. Always use uppercase.
INSERT INTO @TestEmails (NormalizedEmail)
VALUES
    (N'JC_CARPIO@HOTMAIL.COM'),
    (N'SPHERENETCOM@GMAIL.COM'),
    (N'RECEPCION@BONHOMIASUITES.COM');

IF DB_NAME() <> N'grupocarpio'
BEGIN
    THROW 51000, 'Safety check failed: this script may run only in grupocarpio.', 1;
END;

IF OBJECT_ID(N'brunos_auth.AspNetUsers', N'U') IS NULL
   OR OBJECT_ID(N'fidelidad.MemberAccount', N'U') IS NULL
BEGIN
    THROW 51000, 'Safety check failed: the Bruno identity or membership tables are missing.', 1;
END;

-- SSMS keeps local temporary tables alive for the lifetime of the query
-- connection. Clear leftovers so previewing and rerunning this script in the
-- same window is safe.
DROP TABLE IF EXISTS #TargetMembers;
DROP TABLE IF EXISTS #TargetUsers;

CREATE TABLE #TargetUsers
(
    UserId nvarchar(450) NOT NULL PRIMARY KEY,
    Email nvarchar(256) NULL,
    NormalizedEmail nvarchar(256) NULL,
    EmailConfirmed bit NOT NULL
);

CREATE TABLE #TargetMembers
(
    MemberId uniqueidentifier NOT NULL PRIMARY KEY,
    IdentityUserId nvarchar(450) NOT NULL,
    MembershipNumber varchar(20) NOT NULL,
    NormalizedEmail nvarchar(256) NOT NULL,
    Status varchar(30) NOT NULL,
    PointsBalance int NOT NULL,
    EmailVerified bit NOT NULL
);

INSERT INTO #TargetUsers (UserId, Email, NormalizedEmail, EmailConfirmed)
SELECT u.Id, u.Email, u.NormalizedEmail, u.EmailConfirmed
FROM brunos_auth.AspNetUsers AS u
INNER JOIN @TestEmails AS e
    ON e.NormalizedEmail = u.NormalizedEmail;

INSERT INTO #TargetMembers
(
    MemberId,
    IdentityUserId,
    MembershipNumber,
    NormalizedEmail,
    Status,
    PointsBalance,
    EmailVerified
)
SELECT
    m.Id,
    m.IdentityUserId,
    m.MembershipNumber,
    m.NormalizedEmail,
    m.Status,
    m.PointsBalance,
    m.EmailVerified
FROM fidelidad.MemberAccount AS m
WHERE EXISTS
(
    SELECT 1
    FROM @TestEmails AS e
    WHERE e.NormalizedEmail = m.NormalizedEmail
)
OR EXISTS
(
    SELECT 1
    FROM #TargetUsers AS u
    WHERE u.UserId = m.IdentityUserId
);

-- Include the linked identity if a membership matched by email first.
INSERT INTO #TargetUsers (UserId, Email, NormalizedEmail, EmailConfirmed)
SELECT u.Id, u.Email, u.NormalizedEmail, u.EmailConfirmed
FROM brunos_auth.AspNetUsers AS u
INNER JOIN #TargetMembers AS m
    ON m.IdentityUserId = u.Id
WHERE NOT EXISTS
(
    SELECT 1
    FROM #TargetUsers AS existing
    WHERE existing.UserId = u.Id
);

DECLARE @TargetUserCount int = (SELECT COUNT(*) FROM #TargetUsers);
DECLARE @TargetMemberCount int = (SELECT COUNT(*) FROM #TargetMembers);

SELECT
    u.Email,
    u.NormalizedEmail,
    u.EmailConfirmed,
    m.MembershipNumber,
    m.Status,
    m.PointsBalance,
    m.EmailVerified,
    (SELECT COUNT(*) FROM fidelidad.MemberConsent AS c WHERE c.MemberId = m.MemberId) AS ConsentCount,
    (SELECT COUNT(*) FROM fidelidad.MemberQrToken AS q WHERE q.MemberId = m.MemberId) AS QrTokenCount,
    (SELECT COUNT(*) FROM fidelidad.MemberClosureRequest AS c WHERE c.MemberId = m.MemberId) AS ClosureRequestCount,
    (SELECT COUNT(*) FROM fidelidad.PointLedger AS l WHERE l.MemberId = m.MemberId) AS LedgerEntryCount,
    (SELECT COUNT(*) FROM restaurante.[Order] AS o WHERE o.MemberId = m.MemberId) AS RestaurantOrderCount,
    (SELECT COUNT(*) FROM restaurante.PromotionRedemption AS r WHERE r.MemberId = m.MemberId) AS RedemptionCount
FROM #TargetUsers AS u
LEFT JOIN #TargetMembers AS m
    ON m.IdentityUserId = u.UserId
ORDER BY u.NormalizedEmail;

SELECT
    @TargetUserCount AS TargetUserCount,
    @TargetMemberCount AS TargetMemberCount,
    CASE WHEN @Execute = 1 THEN N'EXECUTE' ELSE N'PREVIEW ONLY' END AS RequestedMode;

IF EXISTS
(
    SELECT 1
    FROM #TargetMembers
    WHERE Status NOT IN ('PendingVerification', 'Active')
       OR PointsBalance <> 0
)
BEGIN
    THROW 51000, 'Safety check failed: at least one target membership is not pending/active or has points.', 1;
END;

IF EXISTS
(
    SELECT 1
    FROM #TargetMembers AS m
    WHERE EXISTS (SELECT 1 FROM fidelidad.PointLedger AS l WHERE l.MemberId = m.MemberId)
       OR EXISTS (SELECT 1 FROM fidelidad.MemberClosureRequest AS c WHERE c.MemberId = m.MemberId)
       OR EXISTS (SELECT 1 FROM restaurante.[Order] AS o WHERE o.MemberId = m.MemberId)
       OR EXISTS (SELECT 1 FROM restaurante.PromotionRedemption AS r WHERE r.MemberId = m.MemberId)
)
BEGIN
    THROW 51000, 'Safety check failed: at least one target membership has operational history.', 1;
END;

IF @TargetUserCount = 0 AND @TargetMemberCount = 0
BEGIN
    PRINT 'No matching Bruno test registrations were found. Nothing was changed.';
    RETURN;
END;

IF @Execute = 0
BEGIN
    PRINT 'Preview complete. Nothing was changed.';
    PRINT 'To delete these rows, set @Execute = 1 and @Confirmation = N''RESET BRUNO TEST MEMBERS''.';
    RETURN;
END;

IF @Confirmation <> @RequiredConfirmation
BEGIN
    THROW 51000, 'Safety check failed: the execution confirmation phrase is missing or incorrect.', 1;
END;

IF SCHEMA_ID(N'codex_recovery') IS NULL
BEGIN
    EXEC(N'CREATE SCHEMA codex_recovery AUTHORIZATION dbo;');
END;

DECLARE @UtcSuffix char(15) =
    CONVERT(char(8), SYSUTCDATETIME(), 112)
    + '_'
    + REPLACE(CONVERT(char(8), SYSUTCDATETIME(), 108), ':', '');
DECLARE @RecoveryPrefix sysname = N'BrunoMembershipTestReset_' + @UtcSuffix + N'_';
DECLARE @BackupSql nvarchar(max);

IF OBJECT_ID(N'codex_recovery.' + @RecoveryPrefix + N'AspNetUsers', N'U') IS NOT NULL
BEGIN
    THROW 51000, 'Recovery table name collision. Wait one second and run the script again.', 1;
END;

SET @BackupSql = N'
SELECT source.*
INTO codex_recovery.' + QUOTENAME(@RecoveryPrefix + N'AspNetUsers') + N'
FROM brunos_auth.AspNetUsers AS source
WHERE EXISTS (SELECT 1 FROM #TargetUsers AS target WHERE target.UserId = source.Id);

SELECT source.*
INTO codex_recovery.' + QUOTENAME(@RecoveryPrefix + N'AspNetUserClaims') + N'
FROM brunos_auth.AspNetUserClaims AS source
WHERE EXISTS (SELECT 1 FROM #TargetUsers AS target WHERE target.UserId = source.UserId);

SELECT source.*
INTO codex_recovery.' + QUOTENAME(@RecoveryPrefix + N'AspNetUserLogins') + N'
FROM brunos_auth.AspNetUserLogins AS source
WHERE EXISTS (SELECT 1 FROM #TargetUsers AS target WHERE target.UserId = source.UserId);

SELECT source.*
INTO codex_recovery.' + QUOTENAME(@RecoveryPrefix + N'AspNetUserRoles') + N'
FROM brunos_auth.AspNetUserRoles AS source
WHERE EXISTS (SELECT 1 FROM #TargetUsers AS target WHERE target.UserId = source.UserId);

SELECT source.*
INTO codex_recovery.' + QUOTENAME(@RecoveryPrefix + N'AspNetUserTokens') + N'
FROM brunos_auth.AspNetUserTokens AS source
WHERE EXISTS (SELECT 1 FROM #TargetUsers AS target WHERE target.UserId = source.UserId);

SELECT source.*
INTO codex_recovery.' + QUOTENAME(@RecoveryPrefix + N'MemberAccount') + N'
FROM fidelidad.MemberAccount AS source
WHERE EXISTS (SELECT 1 FROM #TargetMembers AS target WHERE target.MemberId = source.Id);

SELECT source.*
INTO codex_recovery.' + QUOTENAME(@RecoveryPrefix + N'MemberConsent') + N'
FROM fidelidad.MemberConsent AS source
WHERE EXISTS (SELECT 1 FROM #TargetMembers AS target WHERE target.MemberId = source.MemberId);

SELECT source.*
INTO codex_recovery.' + QUOTENAME(@RecoveryPrefix + N'MemberQrToken') + N'
FROM fidelidad.MemberQrToken AS source
WHERE EXISTS (SELECT 1 FROM #TargetMembers AS target WHERE target.MemberId = source.MemberId);

SELECT source.*
INTO codex_recovery.' + QUOTENAME(@RecoveryPrefix + N'MemberClosureRequest') + N'
FROM fidelidad.MemberClosureRequest AS source
WHERE EXISTS (SELECT 1 FROM #TargetMembers AS target WHERE target.MemberId = source.MemberId);

SELECT source.*
INTO codex_recovery.' + QUOTENAME(@RecoveryPrefix + N'PointLedger') + N'
FROM fidelidad.PointLedger AS source
WHERE EXISTS (SELECT 1 FROM #TargetMembers AS target WHERE target.MemberId = source.MemberId);';

EXEC sys.sp_executesql @BackupSql;

BEGIN TRY
    BEGIN TRANSACTION;

    DELETE source
    FROM fidelidad.MemberClosureRequest AS source
    INNER JOIN #TargetMembers AS target ON target.MemberId = source.MemberId;

    DELETE source
    FROM fidelidad.MemberQrToken AS source
    INNER JOIN #TargetMembers AS target ON target.MemberId = source.MemberId;

    DELETE source
    FROM fidelidad.PointLedger AS source
    INNER JOIN #TargetMembers AS target ON target.MemberId = source.MemberId;

    DELETE source
    FROM fidelidad.MemberConsent AS source
    INNER JOIN #TargetMembers AS target ON target.MemberId = source.MemberId;

    DELETE source
    FROM fidelidad.MemberAccount AS source
    INNER JOIN #TargetMembers AS target ON target.MemberId = source.Id;

    DELETE source
    FROM brunos_auth.AspNetUserClaims AS source
    INNER JOIN #TargetUsers AS target ON target.UserId = source.UserId;

    DELETE source
    FROM brunos_auth.AspNetUserLogins AS source
    INNER JOIN #TargetUsers AS target ON target.UserId = source.UserId;

    DELETE source
    FROM brunos_auth.AspNetUserRoles AS source
    INNER JOIN #TargetUsers AS target ON target.UserId = source.UserId;

    DELETE source
    FROM brunos_auth.AspNetUserTokens AS source
    INNER JOIN #TargetUsers AS target ON target.UserId = source.UserId;

    DELETE source
    FROM brunos_auth.AspNetUsers AS source
    INNER JOIN #TargetUsers AS target ON target.UserId = source.Id;

    IF EXISTS
    (
        SELECT 1
        FROM brunos_auth.AspNetUsers AS source
        INNER JOIN #TargetUsers AS target ON target.UserId = source.Id
    )
    OR EXISTS
    (
        SELECT 1
        FROM fidelidad.MemberAccount AS source
        INNER JOIN #TargetMembers AS target ON target.MemberId = source.Id
    )
    BEGIN
        THROW 51000, 'Verification failed: at least one target row remains.', 1;
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0
    BEGIN
        ROLLBACK TRANSACTION;
    END;

    THROW;
END CATCH;

SELECT
    @TargetUserCount AS DeletedUserCount,
    @TargetMemberCount AS DeletedMemberCount,
    N'codex_recovery.' + @RecoveryPrefix + N'*' AS RecoveryTables,
    (SELECT COUNT(*) FROM brunos_auth.AspNetUsers) AS RemainingBrunoUsers,
    (SELECT COUNT(*) FROM fidelidad.MemberAccount) AS RemainingMemberships;
