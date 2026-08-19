/*
  Orion_Training destructive sanitization, version 1.

  DO NOT run this file directly. Sanitize-OrionTraining.ps1 establishes the
  session guard after validating both the connection catalog and the explicit
  -Apply/-ConfirmDatabase switches.

  This batch intentionally does not create an attestation. A later, independent
  verification batch is the only code allowed to set DatosSanitizados and
  DatosSinteticos to 1.
*/

SET NOCOUNT ON;
SET XACT_ABORT ON;

IF DB_NAME() COLLATE Latin1_General_100_BIN2 <> N'Orion_Training' COLLATE Latin1_General_100_BIN2
  THROW 51700, 'SANITIZATION BLOCKED: the active database is not exactly Orion_Training.', 1;

IF ISNULL(TRY_CONVERT(nvarchar(64), SESSION_CONTEXT(N'OrionTrainingSanitizerApply')), N'') <> N'20260817-v1'
  THROW 51701, 'SANITIZATION BLOCKED: the guarded PowerShell workflow did not authorize this session.', 1;

IF DATABASEPROPERTYEX(DB_NAME(), 'Updateability') <> N'READ_WRITE'
  THROW 51702, 'SANITIZATION BLOCKED: Orion_Training is not writable.', 1;

IF ISNULL(IS_SRVROLEMEMBER(N'sysadmin'), 0) <> 1
  THROW 51713, 'SANITIZATION BLOCKED: principal cleanup requires a sysadmin maintenance connection.', 1;

IF EXISTS
(
  SELECT 1
  FROM sys.databases
  WHERE database_id = DB_ID()
    AND (containment <> 0
         OR is_broker_enabled = 1
         OR is_cdc_enabled = 1
         OR is_published = 1
         OR is_subscribed = 1
         OR is_merge_published = 1
         OR is_distributor = 1)
)
   OR EXISTS (SELECT 1 FROM sys.change_tracking_databases WHERE database_id = DB_ID())
  THROW 51714, 'SANITIZATION BLOCKED: containment, Broker, CDC, Change Tracking, or replication is enabled.', 1;

IF EXISTS
(
  SELECT 1
  FROM sys.dm_exec_sessions sessionInfo
  WHERE sessionInfo.database_id = DB_ID()
    AND sessionInfo.session_id <> @@SPID
    AND sessionInfo.is_user_process = 1
)
  THROW 51703, 'SANITIZATION BLOCKED: stop OrionERP.Training and close every other Orion_Training session first.', 1;

IF OBJECT_ID(N'dbo.Capital_Humano', N'U') IS NULL
   OR OBJECT_ID(N'auth.AspNetUsers', N'U') IS NULL
   OR OBJECT_ID(N'dbo.RESERVATION', N'U') IS NULL
   OR OBJECT_ID(N'cfdi.Comprobante', N'U') IS NULL
   OR OBJECT_ID(N'logistica.Material', N'U') IS NULL
  THROW 51704, 'SANITIZATION BLOCKED: the expected OrionERP schema anchors are missing.', 1;

IF (SELECT COUNT_BIG(*) FROM sys.tables WHERE is_ms_shipped = 0) < 250
  THROW 51705, 'SANITIZATION BLOCKED: the database does not match the expected OrionERP schema surface.', 1;

IF EXISTS
(
  SELECT 1
  FROM sys.tables
  WHERE is_ms_shipped = 0
    AND (temporal_type <> 0 OR is_memory_optimized = 1 OR is_filetable = 1)
)
  THROW 51711, 'SANITIZATION BLOCKED: a temporal, memory-optimized, or FileTable requires a reviewed erase strategy.', 1;

IF EXISTS (SELECT 1 FROM sys.security_policies)
   OR EXISTS (SELECT 1 FROM sys.security_predicates)
   OR EXISTS (SELECT 1 FROM sys.certificates)
   OR EXISTS (SELECT 1 FROM sys.asymmetric_keys)
   OR EXISTS (SELECT 1 FROM sys.symmetric_keys)
   OR EXISTS (SELECT 1 FROM sys.column_master_keys)
   OR EXISTS (SELECT 1 FROM sys.column_encryption_keys)
   OR EXISTS (SELECT 1 FROM sys.fulltext_indexes)
   OR EXISTS (SELECT 1 FROM sys.fulltext_catalogs)
   OR EXISTS
      (SELECT 1 FROM sys.views viewInfo
       JOIN sys.indexes indexInfo ON indexInfo.object_id = viewInfo.object_id
       WHERE indexInfo.index_id > 0)
  THROW 51724, 'SANITIZATION BLOCKED: RLS, database key, full-text, or indexed-view state requires a reviewed strategy.', 1;

IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE is_disabled = 1)
   OR EXISTS (SELECT 1 FROM sys.check_constraints WHERE is_disabled = 1)
  THROW 51712, 'SANITIZATION BLOCKED: disabled constraints would make state restoration ambiguous.', 1;

-- Disabled DML triggers are accepted only for recovery from a prior guarded
-- failure. PowerShell has already restricted their names/parents, this batch
-- never enables them before erasure, and the reviewed installers replace the
-- canonical bodies before the final attestation can enable the service.

IF EXISTS
(
  SELECT 1
  FROM sys.database_permissions permissionInfo
  JOIN sys.database_principals grantee
    ON grantee.principal_id = permissionInfo.grantee_principal_id
  WHERE (grantee.name IN (N'public', N'guest') OR grantee.is_fixed_role = 1)
    AND permissionInfo.class NOT IN (0, 1)
)
  THROW 51718, 'SANITIZATION BLOCKED: public, guest, or a fixed role has an unsupported explicit permission class.', 1;

-- These are the only cloned rows retained. Their schemas contain exclusively
-- migration identifiers or values derived from calendar dates.
IF OBJECT_ID(N'dbo.__EFMigrationsHistory', N'U') IS NULL
   OR (SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.__EFMigrationsHistory')) <> 2
   OR EXISTS
      (
        SELECT 1
        FROM sys.columns
        WHERE object_id = OBJECT_ID(N'dbo.__EFMigrationsHistory')
          AND name NOT IN (N'MigrationId', N'ProductVersion')
      )
  THROW 51706, 'SANITIZATION BLOCKED: __EFMigrationsHistory no longer has its expected safe shape.', 1;

IF OBJECT_ID(N'dbo.DateDimension', N'U') IS NULL
   OR (SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.DateDimension')) <> 34
   OR EXISTS
      (
        SELECT 1
        FROM sys.columns
        WHERE object_id = OBJECT_ID(N'dbo.DateDimension')
          AND name NOT IN
          (
            N'TheDate', N'TheDay', N'TheDaySuffix', N'TheDayName', N'TheDayOfWeek',
            N'TheDayOfWeekInMonth', N'TheDayOfYear', N'IsWeekend', N'TheWeek', N'TheISOweek',
            N'TheFirstOfWeek', N'TheLastOfWeek', N'TheWeekOfMonth', N'TheMonth', N'TheMonthName',
            N'TheFirstOfMonth', N'TheLastOfMonth', N'TheFirstOfNextMonth', N'TheLastOfNextMonth',
            N'TheQuarter', N'TheFirstOfQuarter', N'TheLastOfQuarter', N'TheYear', N'TheISOYear',
            N'TheFirstOfYear', N'TheLastOfYear', N'IsLeapYear', N'Has53Weeks', N'Has53ISOWeeks',
            N'MMYYYY', N'Style101', N'Style103', N'Style112', N'Style120'
          )
      )
  THROW 51707, 'SANITIZATION BLOCKED: DateDimension no longer has its expected safe shape.', 1;

DECLARE @ApplicationLockResult int;
EXEC @ApplicationLockResult = sys.sp_getapplock
  @Resource = N'OrionERP:Orion_Training:Sanitize:v1',
  @LockMode = N'Exclusive',
  @LockOwner = N'Session',
  @LockTimeout = 0;
IF @ApplicationLockResult < 0
  THROW 51708, 'SANITIZATION BLOCKED: another sanitization workflow holds the safety lock.', 1;

BEGIN TRY
  DECLARE @BuiltInOwnerName sysname = SUSER_SNAME(0x01);
  IF @BuiltInOwnerName IS NULL
    THROW 51715, 'SANITIZATION BLOCKED: the built-in SQL Server owner SID could not be resolved.', 1;

  IF NOT EXISTS
  (
    SELECT 1
    FROM sys.databases
    WHERE database_id = DB_ID()
      AND owner_sid = 0x01
  )
  BEGIN
    DECLARE @OwnerStatement nvarchar(max) =
      N'ALTER AUTHORIZATION ON DATABASE::[Orion_Training] TO ' + QUOTENAME(@BuiltInOwnerName) + N';';
    EXEC sys.sp_executesql @OwnerStatement;
  END;

  -- Query Store can retain production SQL text, literals, and execution history
  -- outside user tables. Clear it before the reset and keep it off in Training.
  ALTER DATABASE [Orion_Training] SET QUERY_STORE = ON (OPERATION_MODE = READ_WRITE);
  ALTER DATABASE [Orion_Training] SET QUERY_STORE CLEAR ALL;
  ALTER DATABASE [Orion_Training] SET QUERY_STORE = OFF;
END TRY
BEGIN CATCH
  EXEC sys.sp_releaseapplock
    @Resource = N'OrionERP:Orion_Training:Sanitize:v1',
    @LockOwner = N'Session';
  THROW;
END CATCH;

BEGIN TRY
  BEGIN TRANSACTION;

  -- Normalize every explicit database/object/column permission inherited
  -- through public/guest/fixed roles. Other securable classes were rejected.
  DECLARE @PermissionClass int;
  DECLARE @PermissionName sysname;
  DECLARE @PermissionGrantee sysname;
  DECLARE @PermissionObject nvarchar(517);
  DECLARE @PermissionColumn sysname;
  DECLARE @PermissionStatement nvarchar(max);
  DECLARE DatabasePermissionCursor CURSOR LOCAL FAST_FORWARD FOR
    SELECT DISTINCT
      permissionInfo.class,
      permissionInfo.permission_name,
      grantee.name,
      CASE WHEN permissionInfo.class = 1
        THEN QUOTENAME(schemaInfo.name) + N'.' + QUOTENAME(objectInfo.name)
        ELSE NULL END,
      columnInfo.name
    FROM sys.database_permissions permissionInfo
    JOIN sys.database_principals grantee
      ON grantee.principal_id = permissionInfo.grantee_principal_id
    LEFT JOIN sys.objects objectInfo
      ON permissionInfo.class = 1 AND objectInfo.object_id = permissionInfo.major_id
    LEFT JOIN sys.schemas schemaInfo ON schemaInfo.schema_id = objectInfo.schema_id
    LEFT JOIN sys.columns columnInfo
      ON columnInfo.object_id = permissionInfo.major_id
     AND columnInfo.column_id = permissionInfo.minor_id
    WHERE permissionInfo.class IN (0, 1)
      AND (grantee.name IN (N'public', N'guest') OR grantee.is_fixed_role = 1)
      -- Every SQL Server database ships built-in GRANTs to public on system
      -- objects, which carry a negative major_id and are absent from sys.objects.
      -- They are not clone artifacts, REVOKE ON OBJECT:: cannot name them, and
      -- removing them would damage the catalog. Restrict the sweep to
      -- database-level permissions (class 0, major_id 0) and user objects so the
      -- unresolved-target guard below stays meaningful.
      AND (permissionInfo.class = 0 OR permissionInfo.major_id > 0)
    ORDER BY permissionInfo.class DESC, grantee.name, permissionInfo.permission_name;
  OPEN DatabasePermissionCursor;
  FETCH NEXT FROM DatabasePermissionCursor
    INTO @PermissionClass, @PermissionName, @PermissionGrantee, @PermissionObject, @PermissionColumn;
  WHILE @@FETCH_STATUS = 0
  BEGIN
    IF @PermissionClass = 1 AND @PermissionObject IS NULL
      THROW 51723, 'SANITIZATION BLOCKED: an object permission target could not be resolved.', 1;
    SET @PermissionStatement = N'REVOKE ' + @PermissionName
      + CASE WHEN @PermissionClass = 1
          THEN N' ON OBJECT::' + @PermissionObject
            + CASE WHEN @PermissionColumn IS NULL THEN N'' ELSE N' (' + QUOTENAME(@PermissionColumn) + N')' END
          ELSE N'' END
      + N' FROM ' + QUOTENAME(@PermissionGrantee) + N' CASCADE;';
    EXEC sys.sp_executesql @PermissionStatement;
    FETCH NEXT FROM DatabasePermissionCursor
      INTO @PermissionClass, @PermissionName, @PermissionGrantee, @PermissionObject, @PermissionColumn;
  END;
  CLOSE DatabasePermissionCursor;
  DEALLOCATE DatabasePermissionCursor;

  -- Only instance-authenticated SQL users and custom roles can reach this point;
  -- the PowerShell preflight rejects contained, Windows/external, application,
  -- certificate/asymmetric-key, signed, and EXECUTE AS principals/modules.
  IF EXISTS
  (
    SELECT 1
    FROM sys.database_principals principalInfo
    WHERE principalInfo.principal_id > 4
      AND principalInfo.is_fixed_role = 0
      AND principalInfo.type NOT IN (N'S', N'R')
  )
     OR EXISTS
        (SELECT 1 FROM sys.database_principals
         WHERE principal_id > 4
           AND is_fixed_role = 0
           AND type = N'S'
           AND authentication_type <> 1)
    THROW 51716, 'SANITIZATION BLOCKED: an unsafe database principal survived preflight.', 1;

  -- Fixed database-role schemas and cloned user schemas can confer implicit
  -- CONTROL without an explicit permission row. Normalize every non-system
  -- schema owner to dbo before removing cloned principals.
  DECLARE @SchemaName sysname;
  DECLARE @OwnershipStatement nvarchar(max);
  DECLARE SchemaOwnershipCursor CURSOR LOCAL FAST_FORWARD FOR
    SELECT name
    FROM sys.schemas
    WHERE name NOT IN (N'dbo', N'guest', N'INFORMATION_SCHEMA', N'sys')
      AND principal_id <> USER_ID(N'dbo')
    ORDER BY name;
  OPEN SchemaOwnershipCursor;
  FETCH NEXT FROM SchemaOwnershipCursor INTO @SchemaName;
  WHILE @@FETCH_STATUS = 0
  BEGIN
    SET @OwnershipStatement = N'ALTER AUTHORIZATION ON SCHEMA::'
      + QUOTENAME(@SchemaName) + N' TO [dbo];';
    EXEC sys.sp_executesql @OwnershipStatement;
    FETCH NEXT FROM SchemaOwnershipCursor INTO @SchemaName;
  END;
  CLOSE SchemaOwnershipCursor;
  DEALLOCATE SchemaOwnershipCursor;

  IF EXISTS
  (
    SELECT 1
    FROM sys.objects
    WHERE is_ms_shipped = 0
      AND principal_id IS NOT NULL
      AND principal_id <> USER_ID(N'dbo')
  )
     OR EXISTS
        (SELECT 1 FROM sys.database_principals
         WHERE principal_id > 4
           AND is_fixed_role = 0
           AND owning_principal_id IS NOT NULL
           AND owning_principal_id <> USER_ID(N'dbo'))
    THROW 51725, 'SANITIZATION BLOCKED: a user object or role has a non-dbo owner.', 1;

  IF EXISTS
  (
    SELECT 1
    FROM sys.database_principals candidate
    WHERE candidate.principal_id > 4
      AND candidate.is_fixed_role = 0
      AND candidate.type IN (N'S', N'R')
      AND
      (
        EXISTS (SELECT 1 FROM sys.schemas WHERE principal_id = candidate.principal_id)
        OR EXISTS (SELECT 1 FROM sys.objects WHERE principal_id = candidate.principal_id)
        OR EXISTS
           (SELECT 1 FROM sys.database_principals owned
            WHERE owned.owning_principal_id = candidate.principal_id)
      )
  )
    THROW 51717, 'SANITIZATION BLOCKED: a cloned user or custom role owns a database securable.', 1;

  DECLARE @PrincipalName sysname;
  DECLARE @RoleName sysname;
  DECLARE @MemberName sysname;
  DECLARE @PrincipalStatement nvarchar(max);

  DECLARE UserCursor CURSOR LOCAL FAST_FORWARD FOR
    SELECT name
    FROM sys.database_principals
    WHERE principal_id > 4
      AND is_fixed_role = 0
      AND type = N'S'
      AND authentication_type = 1
    ORDER BY name;
  OPEN UserCursor;
  FETCH NEXT FROM UserCursor INTO @PrincipalName;
  WHILE @@FETCH_STATUS = 0
  BEGIN
    SET @PrincipalStatement = N'DROP USER ' + QUOTENAME(@PrincipalName) + N';';
    EXEC sys.sp_executesql @PrincipalStatement;
    FETCH NEXT FROM UserCursor INTO @PrincipalName;
  END;
  CLOSE UserCursor;
  DEALLOCATE UserCursor;

  DECLARE RoleMemberCursor CURSOR LOCAL FAST_FORWARD FOR
    SELECT roleInfo.name, memberInfo.name
    FROM sys.database_role_members membership
    JOIN sys.database_principals roleInfo
      ON roleInfo.principal_id = membership.role_principal_id
    JOIN sys.database_principals memberInfo
      ON memberInfo.principal_id = membership.member_principal_id
    -- dbo is a member of db_owner in every SQL Server database and cannot be
    -- removed (error 15405: "Cannot use the special principal 'dbo'"). Principal
    -- ids 1-4 are the built-in dbo/guest/INFORMATION_SCHEMA/sys entries, so this
    -- sweep is restricted to cloned members exactly like UserCursor above.
    WHERE (roleInfo.is_fixed_role = 0 OR memberInfo.is_fixed_role = 0)
      AND memberInfo.principal_id > 4
    ORDER BY roleInfo.name, memberInfo.name;
  OPEN RoleMemberCursor;
  FETCH NEXT FROM RoleMemberCursor INTO @RoleName, @MemberName;
  WHILE @@FETCH_STATUS = 0
  BEGIN
    SET @PrincipalStatement = N'ALTER ROLE ' + QUOTENAME(@RoleName)
      + N' DROP MEMBER ' + QUOTENAME(@MemberName) + N';';
    EXEC sys.sp_executesql @PrincipalStatement;
    FETCH NEXT FROM RoleMemberCursor INTO @RoleName, @MemberName;
  END;
  CLOSE RoleMemberCursor;
  DEALLOCATE RoleMemberCursor;

  DECLARE RoleCursor CURSOR LOCAL FAST_FORWARD FOR
    SELECT name
    FROM sys.database_principals
    WHERE principal_id > 4
      AND type = N'R'
      AND is_fixed_role = 0
    ORDER BY name;
  OPEN RoleCursor;
  FETCH NEXT FROM RoleCursor INTO @RoleName;
  WHILE @@FETCH_STATUS = 0
  BEGIN
    SET @PrincipalStatement = N'DROP ROLE ' + QUOTENAME(@RoleName) + N';';
    EXEC sys.sp_executesql @PrincipalStatement;
    FETCH NEXT FROM RoleCursor INTO @RoleName;
  END;
  CLOSE RoleCursor;
  DEALLOCATE RoleCursor;

  DECLARE @Preserved TABLE
  (
    ObjectId int NOT NULL PRIMARY KEY
  );

  INSERT @Preserved (ObjectId)
  VALUES (OBJECT_ID(N'dbo.__EFMigrationsHistory')),
         (OBJECT_ID(N'dbo.DateDimension'));

  IF EXISTS
  (
    SELECT 1
    FROM sys.foreign_keys foreignKeyInfo
    JOIN @Preserved kept ON kept.ObjectId = foreignKeyInfo.parent_object_id
    LEFT JOIN @Preserved referencedKept ON referencedKept.ObjectId = foreignKeyInfo.referenced_object_id
    WHERE referencedKept.ObjectId IS NULL
  )
    THROW 51709, 'SANITIZATION BLOCKED: a preserved table now references a table scheduled for erasure.', 1;

  DECLARE @QualifiedName nvarchar(517);
  DECLARE @Statement nvarchar(max);

  DECLARE DisableCursor CURSOR LOCAL FAST_FORWARD FOR
    SELECT QUOTENAME(schemaInfo.name) + N'.' + QUOTENAME(tableInfo.name)
    FROM sys.tables tableInfo
    JOIN sys.schemas schemaInfo ON schemaInfo.schema_id = tableInfo.schema_id
    WHERE tableInfo.is_ms_shipped = 0
    ORDER BY schemaInfo.name, tableInfo.name;

  OPEN DisableCursor;
  FETCH NEXT FROM DisableCursor INTO @QualifiedName;
  WHILE @@FETCH_STATUS = 0
  BEGIN
    SET @Statement = N'ALTER TABLE ' + @QualifiedName + N' NOCHECK CONSTRAINT ALL; '
                   + N'DISABLE TRIGGER ALL ON ' + @QualifiedName + N';';
    EXEC sys.sp_executesql @Statement;
    FETCH NEXT FROM DisableCursor INTO @QualifiedName;
  END;
  CLOSE DisableCursor;
  DEALLOCATE DisableCursor;

  -- An attestation must disappear before row erasure, but no cloned trigger
  -- body may observe this or any later user-table DML. If this transaction
  -- rolls back, both the prior attestation and trigger state are restored.
  IF OBJECT_ID(N'capacitacion.EntornoSeguridad', N'U') IS NOT NULL
  BEGIN
    UPDATE capacitacion.EntornoSeguridad
    SET DatosSanitizados = 0,
        DatosSinteticos = 0,
        RevisadoEn = NULL,
        RevisadoPor = NULL
    WHERE EntornoSeguridadId = 1;
  END;

  DECLARE EraseCursor CURSOR LOCAL FAST_FORWARD FOR
    SELECT QUOTENAME(schemaInfo.name) + N'.' + QUOTENAME(tableInfo.name)
    FROM sys.tables tableInfo
    JOIN sys.schemas schemaInfo ON schemaInfo.schema_id = tableInfo.schema_id
    LEFT JOIN @Preserved kept ON kept.ObjectId = tableInfo.object_id
    WHERE tableInfo.is_ms_shipped = 0
      AND kept.ObjectId IS NULL
    ORDER BY schemaInfo.name, tableInfo.name;

  OPEN EraseCursor;
  FETCH NEXT FROM EraseCursor INTO @QualifiedName;
  WHILE @@FETCH_STATUS = 0
  BEGIN
    SET @Statement = N'DELETE FROM ' + @QualifiedName + N';';
    EXEC sys.sp_executesql @Statement;
    FETCH NEXT FROM EraseCursor INTO @QualifiedName;
  END;
  CLOSE EraseCursor;
  DEALLOCATE EraseCursor;

  -- DELETE preserves production identity counters. Reseed every erased identity
  -- to seed-increment so its next implicit value is exactly the declared seed.
  DECLARE @IdentityResetValue nvarchar(100);
  DECLARE IdentityCursor CURSOR LOCAL FAST_FORWARD FOR
    SELECT
      QUOTENAME(schemaInfo.name) + N'.' + QUOTENAME(tableInfo.name),
      CONVERT(nvarchar(100),
        CONVERT(decimal(38, 0), identityInfo.seed_value)
        - CONVERT(decimal(38, 0), identityInfo.increment_value))
    FROM sys.identity_columns identityInfo
    JOIN sys.tables tableInfo ON tableInfo.object_id = identityInfo.object_id
    JOIN sys.schemas schemaInfo ON schemaInfo.schema_id = tableInfo.schema_id
    LEFT JOIN @Preserved kept ON kept.ObjectId = tableInfo.object_id
    WHERE tableInfo.is_ms_shipped = 0
      AND kept.ObjectId IS NULL
      -- A never-initialized identity already yields its declared seed on the
      -- first insert. DBCC RESEED has different empty-table semantics there,
      -- so leave NULL metadata untouched and only normalize cloned counters.
      AND identityInfo.last_value IS NOT NULL
    ORDER BY schemaInfo.name, tableInfo.name;

  OPEN IdentityCursor;
  FETCH NEXT FROM IdentityCursor INTO @QualifiedName, @IdentityResetValue;
  WHILE @@FETCH_STATUS = 0
  BEGIN
    SET @Statement = N'DBCC CHECKIDENT (N'''
      + REPLACE(@QualifiedName, N'''', N'''''')
      + N''', RESEED, ' + @IdentityResetValue + N') WITH NO_INFOMSGS;';
    EXEC sys.sp_executesql @Statement;
    FETCH NEXT FROM IdentityCursor INTO @QualifiedName, @IdentityResetValue;
  END;
  CLOSE IdentityCursor;
  DEALLOCATE IdentityCursor;

  -- Sequence state is metadata, not a row. Restart every sequence from its
  -- declared start value so no production-derived counter survives the clone.
  DECLARE @SequenceStartValue nvarchar(100);
  DECLARE SequenceCursor CURSOR LOCAL FAST_FORWARD FOR
    SELECT
      QUOTENAME(schemaInfo.name) + N'.' + QUOTENAME(sequenceInfo.name),
      CONVERT(nvarchar(100), CONVERT(decimal(38, 0), sequenceInfo.start_value))
    FROM sys.sequences sequenceInfo
    JOIN sys.schemas schemaInfo ON schemaInfo.schema_id = sequenceInfo.schema_id
    ORDER BY schemaInfo.name, sequenceInfo.name;

  OPEN SequenceCursor;
  FETCH NEXT FROM SequenceCursor INTO @QualifiedName, @SequenceStartValue;
  WHILE @@FETCH_STATUS = 0
  BEGIN
    SET @Statement = N'ALTER SEQUENCE ' + @QualifiedName
      + N' RESTART WITH ' + @SequenceStartValue + N';';
    EXEC sys.sp_executesql @Statement;
    FETCH NEXT FROM SequenceCursor INTO @QualifiedName, @SequenceStartValue;
  END;
  CLOSE SequenceCursor;
  DEALLOCATE SequenceCursor;

  -- Restore trusted constraints here, but deliberately leave every DML
  -- trigger disabled. The wrapper is the sole trigger enabler after it has
  -- installed reviewed bodies and completed all synthetic provisioning.
  DECLARE RestoreConstraintCursor CURSOR LOCAL FAST_FORWARD FOR
    SELECT QUOTENAME(schemaInfo.name) + N'.' + QUOTENAME(tableInfo.name)
    FROM sys.tables tableInfo
    JOIN sys.schemas schemaInfo ON schemaInfo.schema_id = tableInfo.schema_id
    WHERE tableInfo.is_ms_shipped = 0
    ORDER BY schemaInfo.name, tableInfo.name;

  OPEN RestoreConstraintCursor;
  FETCH NEXT FROM RestoreConstraintCursor INTO @QualifiedName;
  WHILE @@FETCH_STATUS = 0
  BEGIN
    SET @Statement = N'ALTER TABLE ' + @QualifiedName + N' WITH CHECK CHECK CONSTRAINT ALL;';
    EXEC sys.sp_executesql @Statement;
    FETCH NEXT FROM RestoreConstraintCursor INTO @QualifiedName;
  END;
  CLOSE RestoreConstraintCursor;
  DEALLOCATE RestoreConstraintCursor;

  DECLARE @UnexpectedRows bit = 0;
  DECLARE @UnexpectedTable nvarchar(517) = NULL;

  DECLARE VerifyEmptyCursor CURSOR LOCAL FAST_FORWARD FOR
    SELECT QUOTENAME(schemaInfo.name) + N'.' + QUOTENAME(tableInfo.name)
    FROM sys.tables tableInfo
    JOIN sys.schemas schemaInfo ON schemaInfo.schema_id = tableInfo.schema_id
    LEFT JOIN @Preserved kept ON kept.ObjectId = tableInfo.object_id
    WHERE tableInfo.is_ms_shipped = 0
      AND kept.ObjectId IS NULL
    ORDER BY schemaInfo.name, tableInfo.name;

  OPEN VerifyEmptyCursor;
  FETCH NEXT FROM VerifyEmptyCursor INTO @QualifiedName;
  WHILE @@FETCH_STATUS = 0 AND @UnexpectedRows = 0
  BEGIN
    SET @Statement = N'SELECT @HasRows = CONVERT(bit, CASE WHEN EXISTS (SELECT 1 FROM '
                   + @QualifiedName + N') THEN 1 ELSE 0 END);';
    EXEC sys.sp_executesql @Statement, N'@HasRows bit OUTPUT', @HasRows = @UnexpectedRows OUTPUT;
    IF @UnexpectedRows = 1 SET @UnexpectedTable = @QualifiedName;
    FETCH NEXT FROM VerifyEmptyCursor INTO @QualifiedName;
  END;
  CLOSE VerifyEmptyCursor;
  DEALLOCATE VerifyEmptyCursor;

  IF @UnexpectedRows = 1
    THROW 51710, 'SANITIZATION FAILED: at least one non-preserved table retained rows.', 1;

  IF EXISTS
  (
    SELECT 1
    FROM sys.databases
    WHERE database_id = DB_ID()
      AND (owner_sid <> 0x01 OR containment <> 0)
  )
    THROW 51719, 'SANITIZATION FAILED: database ownership or containment is outside the Training boundary.', 1;

  IF EXISTS
  (
    SELECT 1
    FROM sys.database_principals
    WHERE principal_id > 4
      AND is_fixed_role = 0
  )
     OR EXISTS
        (
          -- Scoped exactly like the membership cursor above: the built-in dbo
          -- membership in db_owner is permanent, so only cloned members are
          -- verified gone. Requiring an empty table would fail on any catalog.
          SELECT 1
          FROM sys.database_role_members membership
          WHERE membership.member_principal_id > 4
        )
    THROW 51720, 'SANITIZATION FAILED: a cloned database principal or role membership survived.', 1;

  IF EXISTS
  (
    SELECT 1
    FROM sys.database_permissions permissionInfo
    JOIN sys.database_principals grantee
      ON grantee.principal_id = permissionInfo.grantee_principal_id
    -- Scoped exactly like the revocation cursor above: the built-in system-object
    -- grants (negative major_id) are never revoked, so verifying against them
    -- would fail on a clean database too.
    WHERE (grantee.name IN (N'public', N'guest') OR grantee.is_fixed_role = 1)
      AND (permissionInfo.class = 0 OR permissionInfo.major_id > 0)
  )
    THROW 51721, 'SANITIZATION FAILED: public, guest, or a fixed role retained an explicit permission.', 1;

  IF EXISTS
  (
    SELECT 1 FROM sys.schemas
    WHERE name NOT IN (N'dbo', N'guest', N'INFORMATION_SCHEMA', N'sys')
      AND principal_id <> USER_ID(N'dbo')
  )
     OR EXISTS
        (SELECT 1 FROM sys.objects
         WHERE is_ms_shipped = 0
           AND principal_id IS NOT NULL
           AND principal_id <> USER_ID(N'dbo'))
    THROW 51726, 'SANITIZATION FAILED: non-dbo schema or object ownership survived.', 1;

  IF EXISTS
  (
    SELECT 1
    FROM sys.database_query_store_options
    WHERE actual_state <> 0 OR desired_state <> 0
  )
     OR EXISTS (SELECT 1 FROM sys.query_store_query_text)
    THROW 51722, 'SANITIZATION FAILED: Query Store is not disabled and empty.', 1;

  COMMIT TRANSACTION;
END TRY
BEGIN CATCH
  IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
  EXEC sys.sp_releaseapplock
    @Resource = N'OrionERP:Orion_Training:Sanitize:v1',
    @LockOwner = N'Session';
  THROW;
END CATCH;

EXEC sys.sp_releaseapplock
  @Resource = N'OrionERP:Orion_Training:Sanitize:v1',
  @LockOwner = N'Session';
