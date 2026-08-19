/*
  Invoked only by Provision-TrainingRuntimeSqlLogin.ps1 through a parameterized
  SqlCommand. Required parameters:

    @ExpectedDatabase nvarchar(128)
    @RuntimeLogin     nvarchar(128)
    @RuntimePassword  nvarchar(128)

  This batch may create or rotate the fixed SQL-auth login and may change only
  its user and permissions inside Orion_Training. It only reads the production
  and development catalogs to prove that the login has no mapped user there.
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF DB_NAME() COLLATE Latin1_General_100_BIN2 <> N'master'
  THROW 51630, 'La conexión administrativa debe permanecer exactamente en master.', 1;
IF ISNULL(IS_SRVROLEMEMBER(N'sysadmin'), 0) <> 1
  THROW 51650, 'El aprovisionamiento runtime requiere una conexión administrativa sysadmin separada.', 1;
IF @ExpectedDatabase COLLATE Latin1_General_100_BIN2 <> N'Orion_Training'
  THROW 51631, 'Solo se permite aprovisionar Orion_Training.', 1;
IF @RuntimeLogin COLLATE Latin1_General_100_BIN2 <> N'orion_training_runtime'
  THROW 51632, 'El login runtime no coincide con el principal fijo de Training.', 1;
IF DB_ID(N'Orion_Training') IS NULL
   OR DB_ID(N'grupocarpio') IS NULL
   OR DB_ID(N'Orion_Sandbox') IS NULL
  THROW 51633, 'No se pueden verificar los tres límites de catálogo requeridos.', 1;
IF CONVERT(int, SERVERPROPERTY(N'IsIntegratedSecurityOnly')) = 1
  THROW 51634, 'La instancia no admite el login SQL dedicado requerido por Training.', 1;
IF LEN(@RuntimePassword) < 16 OR LEN(@RuntimePassword) > 128
  THROW 51635, 'La contraseña runtime debe tener entre 16 y 128 caracteres.', 1;

IF EXISTS
(
  SELECT 1
  FROM sys.dm_exec_sessions
  WHERE is_user_process = 1
    AND database_id = DB_ID(N'Orion_Training')
    AND session_id <> @@SPID
)
  THROW 51651, 'Detenga OrionERP.Training y cierre toda sesión de Orion_Training antes de aprovisionar el runtime.', 1;

CREATE TABLE #ExpectedTrainingSynonyms
(
  SynonymSchema sysname NOT NULL,
  SynonymName sysname NOT NULL,
  TargetSchema sysname NOT NULL,
  TargetName sysname NOT NULL,
  PRIMARY KEY (SynonymSchema, SynonymName)
);
INSERT #ExpectedTrainingSynonyms (SynonymSchema, SynonymName, TargetSchema, TargetName)
VALUES
  (N'dbo', N'Comprobante', N'cfdi', N'Comprobante'),
  (N'dbo', N'Concepto', N'cfdi', N'Concepto'),
  (N'dbo', N'Conceptos', N'cfdi', N'Conceptos'),
  (N'dbo', N'Emisor', N'cfdi', N'Emisor'),
  (N'dbo', N'Impuestos', N'cfdi', N'Impuestos'),
  (N'dbo', N'InformacionGlobal', N'cfdi', N'InformacionGlobal'),
  (N'dbo', N'Receptor', N'cfdi', N'Receptor'),
  (N'dbo', N'retencion', N'cfdi', N'retencion'),
  (N'dbo', N'Retenciones', N'cfdi', N'Retenciones'),
  (N'dbo', N'TimbreFiscalDigital', N'cfdi', N'TimbreFiscalDigital'),
  (N'dbo', N'traslado', N'cfdi', N'traslado'),
  (N'dbo', N'Traslados', N'cfdi', N'Traslados');

DECLARE @TrainingPreflight nvarchar(max) = N'
  USE [Orion_Training];
  IF EXISTS
  (
    SELECT 1 FROM sys.databases
    WHERE database_id = DB_ID()
      AND (owner_sid <> 0x01 OR containment <> 0 OR is_trustworthy_on = 1
           OR is_db_chaining_on = 1 OR is_cdc_enabled = 1 OR is_broker_enabled = 1
           OR is_published = 1 OR is_subscribed = 1 OR is_merge_published = 1
           OR is_distributor = 1)
  )
    THROW 51652, ''La base Training no conserva propietario, containment y opciones aisladas.'', 1;

  IF OBJECT_ID(N''capacitacion.EntornoSeguridad'', N''U'') IS NULL
     OR OBJECT_ID(N''capacitacion.EsquemaVersion'', N''U'') IS NULL
     OR (SELECT COUNT(*) FROM capacitacion.EntornoSeguridad) <> 1
     OR NOT EXISTS
        (SELECT 1 FROM capacitacion.EntornoSeguridad
         WHERE EntornoSeguridadId = 1 AND Entorno = N''Training''
           AND DatosSanitizados = 1 AND DatosSinteticos = 1
           AND VersionEsquema = 1 AND RevisadoEn IS NOT NULL
           AND NULLIF(LTRIM(RTRIM(RevisadoPor)), N'''') IS NOT NULL)
     OR NOT EXISTS (SELECT 1 FROM capacitacion.EsquemaVersion WHERE Version = 1)
    THROW 51653, ''La atestación positiva de datos sintéticos/sanitizados v1 no existe.'', 1;

  IF EXISTS (SELECT 1 FROM sys.database_query_store_options WHERE actual_state <> 0 OR desired_state <> 0)
     OR EXISTS (SELECT 1 FROM sys.query_store_query_text)
     OR EXISTS (SELECT 1 FROM sys.change_tracking_databases WHERE database_id = DB_ID())
     OR EXISTS (SELECT 1 FROM sys.transmission_queue)
     OR EXISTS (SELECT 1 FROM sys.conversation_endpoints)
     OR EXISTS (SELECT 1 FROM sys.service_queues WHERE is_ms_shipped = 0)
    THROW 51654, ''Query Store, Change Tracking o Service Broker retuvo estado no permitido.'', 1;

  IF EXISTS
  (
    SELECT 1 FROM sys.database_principals principalInfo
    WHERE principalInfo.principal_id > 4
      AND principalInfo.is_fixed_role = 0
      AND
      (
        principalInfo.name <> N''orion_training_runtime''
        OR principalInfo.type <> N''S''
        OR principalInfo.authentication_type <> 1
        OR principalInfo.sid <> SUSER_SID(N''orion_training_runtime'')
      )
  )
    THROW 51655, ''Un principal no canónico sobrevivió en Orion_Training.'', 1;

  IF EXISTS
  (
    SELECT 1
    FROM sys.database_role_members membership
    JOIN sys.database_principals roleInfo ON roleInfo.principal_id = membership.role_principal_id
    JOIN sys.database_principals memberInfo ON memberInfo.principal_id = membership.member_principal_id
    -- dbo is a member of db_owner in every SQL Server database and cannot be
    -- removed. Principal ids 1-4 are the built-in dbo/guest/INFORMATION_SCHEMA/sys
    -- entries, so only cloned memberships are canonical-checked here.
    WHERE membership.member_principal_id > 4
      AND
      (
        memberInfo.name <> N''orion_training_runtime''
        OR roleInfo.name NOT IN (N''db_datareader'', N''db_datawriter'')
      )
  )
    THROW 51656, ''Una membresía de rol no canónica sobrevivió en Orion_Training.'', 1;

  IF EXISTS
  (
    SELECT 1
    FROM sys.database_permissions permissionInfo
    JOIN sys.database_principals grantee ON grantee.principal_id = permissionInfo.grantee_principal_id
    -- Every SQL Server database ships built-in GRANTs to public on system objects
    -- (negative major_id). The sanitizer never revokes them, so counting them as
    -- clone residue would block provisioning on any catalog.
    WHERE (grantee.name IN (N''public'', N''guest'') OR grantee.is_fixed_role = 1)
      AND (permissionInfo.class = 0 OR permissionInfo.major_id > 0)
  )
    THROW 51657, ''public, guest o un rol fijo conserva permisos explícitos clonados.'', 1;
  IF EXISTS
  (
    SELECT 1 FROM sys.schemas
    WHERE name NOT IN (N''dbo'', N''guest'', N''INFORMATION_SCHEMA'', N''sys'')
      AND principal_id <> USER_ID(N''dbo'')
  )
     OR EXISTS
        (SELECT 1 FROM sys.objects
         WHERE is_ms_shipped = 0 AND principal_id IS NOT NULL
           AND principal_id <> USER_ID(N''dbo''))
    THROW 51661, ''Un esquema u objeto de usuario conserva propietario distinto de dbo.'', 1;

  IF EXISTS (SELECT 1 FROM sys.security_policies)
     OR EXISTS (SELECT 1 FROM sys.security_predicates)
     OR EXISTS (SELECT 1 FROM sys.sql_modules WHERE execute_as_principal_id IS NOT NULL)
     OR EXISTS
        (SELECT 1 FROM sys.sql_expression_dependencies
         WHERE referenced_server_name IS NOT NULL OR referenced_database_name IS NOT NULL)
     OR EXISTS (SELECT 1 FROM sys.certificates)
     OR EXISTS (SELECT 1 FROM sys.asymmetric_keys)
     OR EXISTS (SELECT 1 FROM sys.symmetric_keys)
     OR EXISTS (SELECT 1 FROM sys.column_master_keys)
     OR EXISTS (SELECT 1 FROM sys.column_encryption_keys)
     OR EXISTS
        (SELECT 1 FROM sys.views viewInfo
         JOIN sys.indexes indexInfo ON indexInfo.object_id = viewInfo.object_id
         WHERE indexInfo.index_id > 0)
    THROW 51662, ''RLS, EXECUTE AS, dependencias entre catálogos, claves o vistas indexadas no están permitidos en Orion_Training.'', 1;

  IF (SELECT COUNT(*) FROM sys.synonyms) <> (SELECT COUNT(*) FROM #ExpectedTrainingSynonyms)
     OR EXISTS
        (
          SELECT 1
          FROM sys.synonyms synonymInfo
          JOIN sys.schemas synonymSchema ON synonymSchema.schema_id = synonymInfo.schema_id
          LEFT JOIN #ExpectedTrainingSynonyms expected
            ON expected.SynonymSchema COLLATE Latin1_General_100_BIN2 = synonymSchema.name COLLATE Latin1_General_100_BIN2
           AND expected.SynonymName COLLATE Latin1_General_100_BIN2 = synonymInfo.name COLLATE Latin1_General_100_BIN2
          WHERE expected.SynonymName IS NULL
             OR PARSENAME(synonymInfo.base_object_name, 4) IS NOT NULL
             OR PARSENAME(synonymInfo.base_object_name, 3) IS NOT NULL
             OR ISNULL(PARSENAME(synonymInfo.base_object_name, 2), N'''') COLLATE Latin1_General_100_BIN2
                  <> expected.TargetSchema COLLATE Latin1_General_100_BIN2
             OR ISNULL(PARSENAME(synonymInfo.base_object_name, 1), N'''') COLLATE Latin1_General_100_BIN2
                  <> expected.TargetName COLLATE Latin1_General_100_BIN2
             OR OBJECT_ID(QUOTENAME(expected.TargetSchema) + N''.'' + QUOTENAME(expected.TargetName), N''U'') IS NULL
        )
     OR EXISTS
        (
          SELECT 1 FROM #ExpectedTrainingSynonyms expected
          WHERE NOT EXISTS
          (
            SELECT 1
            FROM sys.synonyms synonymInfo
            JOIN sys.schemas synonymSchema ON synonymSchema.schema_id = synonymInfo.schema_id
            WHERE synonymSchema.name COLLATE Latin1_General_100_BIN2 = expected.SynonymSchema COLLATE Latin1_General_100_BIN2
              AND synonymInfo.name COLLATE Latin1_General_100_BIN2 = expected.SynonymName COLLATE Latin1_General_100_BIN2
          )
        )
    THROW 51666, ''El manifiesto exacto de 12 sinónimos locales falta, apunta fuera del catálogo o tiene extras.'', 1;

  IF OBJECT_ID(N''capacitacion.TR_EntornoSeguridad_MaintenanceOnly'', N''TR'') IS NULL
     OR OBJECT_ID(N''capacitacion.TR_EsquemaVersion_MaintenanceOnly'', N''TR'') IS NULL
     OR EXISTS
        (SELECT 1 FROM sys.triggers
         WHERE object_id IN
           (OBJECT_ID(N''capacitacion.TR_EntornoSeguridad_MaintenanceOnly''),
            OBJECT_ID(N''capacitacion.TR_EsquemaVersion_MaintenanceOnly''))
           AND is_disabled = 1)
    THROW 51664, ''Los triggers de mantenimiento de la atestación no están presentes y habilitados.'', 1;';
EXEC sys.sp_executesql @TrainingPreflight;

DECLARE @RuntimeSid varbinary(85) = SUSER_SID(@RuntimeLogin);
IF @RuntimeSid IS NOT NULL
BEGIN
  IF NOT EXISTS
  (
    SELECT 1
    FROM master.sys.server_principals
    WHERE sid = @RuntimeSid
      AND name = @RuntimeLogin
      AND type = N'S'
  )
    THROW 51636, 'Ya existe un principal incompatible con el nombre runtime reservado.', 1;

  IF EXISTS
  (
    SELECT 1
    FROM master.sys.server_role_members membership
    JOIN master.sys.server_principals memberInfo
      ON memberInfo.principal_id = membership.member_principal_id
    WHERE memberInfo.sid = @RuntimeSid
  )
    THROW 51637, 'El login runtime pertenece a un rol de servidor; retire esa membresía antes de continuar.', 1;

  IF EXISTS
  (
    SELECT 1
    FROM master.sys.server_permissions permissionInfo
    JOIN master.sys.server_principals principalInfo
      ON principalInfo.principal_id = permissionInfo.grantee_principal_id
    WHERE principalInfo.sid = @RuntimeSid
      AND permissionInfo.state IN (N'G', N'W')
      AND permissionInfo.permission_name <> N'CONNECT SQL'
  )
    THROW 51638, 'El login runtime conserva permisos explícitos de servidor no permitidos.', 1;

  IF EXISTS (SELECT 1 FROM sys.databases WHERE owner_sid = @RuntimeSid)
    THROW 51639, 'El login runtime no puede ser propietario de ninguna base de datos.', 1;

  IF EXISTS
  (
    SELECT 1
    FROM sys.dm_exec_sessions
    WHERE is_user_process = 1
      AND session_id <> @@SPID
      AND security_id = @RuntimeSid
  )
    THROW 51640, 'Detenga OrionERP.Training y cierre sus conexiones runtime antes de rotar el login.', 1;
END;

DECLARE @BoundarySql nvarchar(max) = N'
  IF EXISTS
  (
    SELECT 1
    FROM [grupocarpio].sys.database_principals
    WHERE sid = SUSER_SID(N''orion_training_runtime'')
       OR name = N''orion_training_runtime''
  )
    THROW 51641, ''El login runtime tiene un usuario en grupocarpio; no se modificó producción.'', 1;

  IF EXISTS
  (
    SELECT 1
    FROM [Orion_Sandbox].sys.database_principals
    WHERE sid = SUSER_SID(N''orion_training_runtime'')
       OR name = N''orion_training_runtime''
  )
    THROW 51642, ''El login runtime tiene un usuario en Orion_Sandbox; no se modificó desarrollo.'', 1;';
EXEC sys.sp_executesql @BoundarySql;

DECLARE @QuotedPassword nvarchar(258) = QUOTENAME(@RuntimePassword, N'''');
IF @QuotedPassword IS NULL
  THROW 51643, 'No se pudo preparar la contraseña runtime.', 1;

DECLARE @LoginDdl nvarchar(max);
IF SUSER_ID(@RuntimeLogin) IS NULL
  SET @LoginDdl = N'CREATE LOGIN ' + QUOTENAME(@RuntimeLogin)
    + N' WITH PASSWORD = ' + @QuotedPassword
    + N', CHECK_POLICY = ON, CHECK_EXPIRATION = ON, DEFAULT_DATABASE = [Orion_Training];';
ELSE
  SET @LoginDdl = N'ALTER LOGIN ' + QUOTENAME(@RuntimeLogin)
    + N' WITH PASSWORD = ' + @QuotedPassword
    + N', CHECK_POLICY = ON, CHECK_EXPIRATION = ON, DEFAULT_DATABASE = [Orion_Training];';
EXEC sys.sp_executesql @LoginDdl;

SET @RuntimeSid = SUSER_SID(@RuntimeLogin);
IF @RuntimeSid IS NULL
  THROW 51644, 'No se creó el login runtime.', 1;

DECLARE @TrainingSql nvarchar(max) = N'
  USE [Orion_Training];

  DECLARE @ExistingUser sysname =
  (
    SELECT TOP (1) name
    FROM sys.database_principals
    WHERE sid = SUSER_SID(N''orion_training_runtime'')
       OR name = N''orion_training_runtime''
    ORDER BY CASE WHEN name = N''orion_training_runtime'' THEN 0 ELSE 1 END
  );

  IF @ExistingUser IS NOT NULL
  BEGIN
    IF EXISTS
    (
      SELECT 1
      FROM sys.schemas
      WHERE principal_id = USER_ID(@ExistingUser)
    )
       OR EXISTS
       (
         SELECT 1
         FROM sys.database_principals
         WHERE owning_principal_id = USER_ID(@ExistingUser)
       )
      THROW 51645, ''El usuario runtime existente posee objetos o roles; remédielo manualmente.'', 1;

    -- EXECUTE() only concatenates string literals and variables; a function call
    -- such as QUOTENAME() inside it is a syntax error. Build the DDL first, then
    -- run it through sp_executesql.
    DECLARE @DropExistingUserDdl nvarchar(max) =
      N''DROP USER '' + QUOTENAME(@ExistingUser) + N'';'';
    EXEC sys.sp_executesql @DropExistingUserDdl;
  END;

  CREATE USER [orion_training_runtime] FOR LOGIN [orion_training_runtime];
  ALTER ROLE [db_datareader] ADD MEMBER [orion_training_runtime];
  ALTER ROLE [db_datawriter] ADD MEMBER [orion_training_runtime];
  GRANT CONNECT TO [orion_training_runtime];
  GRANT EXECUTE TO [orion_training_runtime];
  GRANT VIEW DEFINITION TO [orion_training_runtime];
  GRANT VIEW DATABASE STATE TO [orion_training_runtime];

  DENY INSERT, UPDATE, DELETE
    ON OBJECT::capacitacion.EntornoSeguridad
    TO [orion_training_runtime];
  DENY INSERT, UPDATE, DELETE
    ON OBJECT::capacitacion.EsquemaVersion
    TO [orion_training_runtime];

  IF EXISTS
  (
    SELECT 1
    FROM sys.database_role_members membership
    JOIN sys.database_principals roleInfo
      ON roleInfo.principal_id = membership.role_principal_id
    JOIN sys.database_principals memberInfo
      ON memberInfo.principal_id = membership.member_principal_id
    WHERE memberInfo.name = N''orion_training_runtime''
      AND roleInfo.name NOT IN (N''db_datareader'', N''db_datawriter'')
  )
    THROW 51646, ''El usuario runtime conserva roles de base no permitidos.'', 1;

  IF IS_ROLEMEMBER(N''db_datareader'', N''orion_training_runtime'') <> 1
     OR IS_ROLEMEMBER(N''db_datawriter'', N''orion_training_runtime'') <> 1
     OR IS_ROLEMEMBER(N''db_owner'', N''orion_training_runtime'') <> 0
     OR IS_ROLEMEMBER(N''db_ddladmin'', N''orion_training_runtime'') <> 0
     OR IS_ROLEMEMBER(N''db_securityadmin'', N''orion_training_runtime'') <> 0
    THROW 51647, ''Los roles runtime no coinciden con el contrato mínimo de Training.'', 1;

  IF EXISTS
  (
    SELECT 1 FROM sys.database_principals principalInfo
    WHERE principalInfo.principal_id > 4
      AND principalInfo.is_fixed_role = 0
      AND
      (
        principalInfo.name <> N''orion_training_runtime''
        OR principalInfo.type <> N''S''
        OR principalInfo.authentication_type <> 1
        OR principalInfo.sid <> SUSER_SID(N''orion_training_runtime'')
      )
  )
     -- Count and inspect only non-built-in memberships. dbo belongs to db_owner in
     -- every SQL Server database, so an unscoped count sees three rows here, not
     -- the two runtime memberships this manifest actually describes.
     OR (SELECT COUNT(*) FROM sys.database_role_members
         WHERE member_principal_id > 4) <> 2
     OR EXISTS
        (
          SELECT 1
          FROM sys.database_role_members membership
          JOIN sys.database_principals roleInfo ON roleInfo.principal_id = membership.role_principal_id
          JOIN sys.database_principals memberInfo ON memberInfo.principal_id = membership.member_principal_id
          WHERE membership.member_principal_id > 4
            AND
            (
              memberInfo.name <> N''orion_training_runtime''
              OR roleInfo.name NOT IN (N''db_datareader'', N''db_datawriter'')
            )
        )
    THROW 51658, ''El manifiesto final de principal/membresía runtime no es exacto.'', 1;

  IF EXISTS
  (
    SELECT 1
    FROM sys.database_permissions permissionInfo
    JOIN sys.database_principals grantee ON grantee.principal_id = permissionInfo.grantee_principal_id
    -- Built-in system-object GRANTs to public (negative major_id) are never
    -- revoked by the sanitizer, so they are not residue from the runtime alta.
    WHERE (grantee.name IN (N''public'', N''guest'') OR grantee.is_fixed_role = 1)
      AND (permissionInfo.class = 0 OR permissionInfo.major_id > 0)
  )
    THROW 51659, ''public, guest o un rol fijo conserva permisos explícitos después del alta runtime.'', 1;

  IF (SELECT COUNT(*) FROM sys.database_permissions permissionInfo
      JOIN sys.database_principals grantee ON grantee.principal_id = permissionInfo.grantee_principal_id
      WHERE grantee.name = N''orion_training_runtime'') <> 10
     OR EXISTS
        (
          SELECT 1
          FROM sys.database_permissions permissionInfo
          JOIN sys.database_principals grantee ON grantee.principal_id = permissionInfo.grantee_principal_id
          WHERE grantee.name = N''orion_training_runtime''
            AND NOT
            (
              permissionInfo.class = 0
              AND permissionInfo.major_id = 0
              AND permissionInfo.minor_id = 0
              AND permissionInfo.state = N''G''
              AND permissionInfo.permission_name IN
                (N''CONNECT'', N''EXECUTE'', N''VIEW DEFINITION'', N''VIEW DATABASE STATE'')
              OR permissionInfo.class = 1
              AND permissionInfo.major_id IN
                (OBJECT_ID(N''capacitacion.EntornoSeguridad''), OBJECT_ID(N''capacitacion.EsquemaVersion''))
              AND permissionInfo.minor_id = 0
              AND permissionInfo.state = N''D''
              AND permissionInfo.permission_name IN (N''INSERT'', N''UPDATE'', N''DELETE'')
            )
        )
    THROW 51660, ''Los permisos explícitos runtime no coinciden con el manifiesto de diez filas.'', 1;

  IF EXISTS
  (
    SELECT 1 FROM sys.schemas
    WHERE name NOT IN (N''dbo'', N''guest'', N''INFORMATION_SCHEMA'', N''sys'')
      AND principal_id <> USER_ID(N''dbo'')
  )
     OR EXISTS
        (SELECT 1 FROM sys.objects
         WHERE is_ms_shipped = 0 AND principal_id IS NOT NULL
           AND principal_id <> USER_ID(N''dbo''))
     OR EXISTS (SELECT 1 FROM sys.security_policies)
     OR EXISTS (SELECT 1 FROM sys.security_predicates)
     OR EXISTS (SELECT 1 FROM sys.sql_modules WHERE execute_as_principal_id IS NOT NULL)
     OR EXISTS
        (SELECT 1 FROM sys.sql_expression_dependencies
         WHERE referenced_server_name IS NOT NULL OR referenced_database_name IS NOT NULL)
     OR EXISTS (SELECT 1 FROM sys.certificates)
     OR EXISTS (SELECT 1 FROM sys.asymmetric_keys)
     OR EXISTS (SELECT 1 FROM sys.symmetric_keys)
     OR EXISTS (SELECT 1 FROM sys.column_master_keys)
     OR EXISTS (SELECT 1 FROM sys.column_encryption_keys)
     OR EXISTS
        (SELECT 1 FROM sys.views viewInfo
         JOIN sys.indexes indexInfo ON indexInfo.object_id = viewInfo.object_id
         WHERE indexInfo.index_id > 0)
    THROW 51663, ''Ownership, RLS, EXECUTE AS, dependencias entre catálogos, claves o vistas indexadas se apartaron del manifiesto final.'', 1;

  IF (SELECT COUNT(*) FROM sys.synonyms) <> (SELECT COUNT(*) FROM #ExpectedTrainingSynonyms)
     OR EXISTS
        (
          SELECT 1
          FROM sys.synonyms synonymInfo
          JOIN sys.schemas synonymSchema ON synonymSchema.schema_id = synonymInfo.schema_id
          LEFT JOIN #ExpectedTrainingSynonyms expected
            ON expected.SynonymSchema COLLATE Latin1_General_100_BIN2 = synonymSchema.name COLLATE Latin1_General_100_BIN2
           AND expected.SynonymName COLLATE Latin1_General_100_BIN2 = synonymInfo.name COLLATE Latin1_General_100_BIN2
          WHERE expected.SynonymName IS NULL
             OR PARSENAME(synonymInfo.base_object_name, 4) IS NOT NULL
             OR PARSENAME(synonymInfo.base_object_name, 3) IS NOT NULL
             OR ISNULL(PARSENAME(synonymInfo.base_object_name, 2), N'''') COLLATE Latin1_General_100_BIN2
                  <> expected.TargetSchema COLLATE Latin1_General_100_BIN2
             OR ISNULL(PARSENAME(synonymInfo.base_object_name, 1), N'''') COLLATE Latin1_General_100_BIN2
                  <> expected.TargetName COLLATE Latin1_General_100_BIN2
             OR OBJECT_ID(QUOTENAME(expected.TargetSchema) + N''.'' + QUOTENAME(expected.TargetName), N''U'') IS NULL
        )
     OR EXISTS
        (
          SELECT 1 FROM #ExpectedTrainingSynonyms expected
          WHERE NOT EXISTS
          (
            SELECT 1
            FROM sys.synonyms synonymInfo
            JOIN sys.schemas synonymSchema ON synonymSchema.schema_id = synonymInfo.schema_id
            WHERE synonymSchema.name COLLATE Latin1_General_100_BIN2 = expected.SynonymSchema COLLATE Latin1_General_100_BIN2
              AND synonymInfo.name COLLATE Latin1_General_100_BIN2 = expected.SynonymName COLLATE Latin1_General_100_BIN2
          )
        )
    THROW 51667, ''El manifiesto de sinónimos locales cambió durante el aprovisionamiento runtime.'', 1;

  IF OBJECT_ID(N''capacitacion.TR_EntornoSeguridad_MaintenanceOnly'', N''TR'') IS NULL
     OR OBJECT_ID(N''capacitacion.TR_EsquemaVersion_MaintenanceOnly'', N''TR'') IS NULL
     OR EXISTS
        (SELECT 1 FROM sys.triggers
         WHERE object_id IN
           (OBJECT_ID(N''capacitacion.TR_EntornoSeguridad_MaintenanceOnly''),
            OBJECT_ID(N''capacitacion.TR_EsquemaVersion_MaintenanceOnly''))
           AND is_disabled = 1)
    THROW 51665, ''Los triggers protectores cambiaron durante el aprovisionamiento runtime.'', 1;';
EXEC sys.sp_executesql @TrainingSql;

IF NOT EXISTS
(
  SELECT 1
  FROM master.sys.sql_logins
  WHERE sid = @RuntimeSid
    AND name = N'orion_training_runtime'
    AND is_disabled = 0
    AND is_policy_checked = 1
    AND is_expiration_checked = 1
    AND default_database_name = N'Orion_Training'
)
  THROW 51648, 'La política o el catálogo predeterminado del login runtime no son seguros.', 1;

-- This impersonation check is only an early fail-closed check. The PowerShell
-- workflow must additionally open real SQL-auth connections with the supplied
-- credential before it reports success.
CREATE TABLE #RuntimeBoundary
(
  ActiveCatalog sysname NULL,
  CurrentLogin sysname NULL,
  CanAccessProduction int NULL,
  CanAccessDevelopment int NULL,
  IsSysadmin int NULL,
  IsTrainingOwner int NULL,
  CanRewriteAttestation int NULL,
  CanControlTraining int NULL,
  CanAlterUsers int NULL,
  CanAlterRoles int NULL,
  UnexpectedServerPermissions int NULL
);

DECLARE @VerifySql nvarchar(max) = N'
  EXECUTE AS LOGIN = N''orion_training_runtime'';
  BEGIN TRY
  INSERT #RuntimeBoundary
  SELECT
    DB_NAME(),
    SUSER_SNAME(),
    CONVERT(int, HAS_DBACCESS(N''grupocarpio'')),
    CONVERT(int, HAS_DBACCESS(N''Orion_Sandbox'')),
    CONVERT(int, ISNULL(IS_SRVROLEMEMBER(N''sysadmin''), 0)),
    CONVERT(int, ISNULL(IS_ROLEMEMBER(N''db_owner''), 0)),
    CONVERT(int, ISNULL(HAS_PERMS_BY_NAME(
      N''capacitacion.EntornoSeguridad'', N''OBJECT'', N''UPDATE''), 0)),
    CONVERT(int, ISNULL(HAS_PERMS_BY_NAME(DB_NAME(), N''DATABASE'', N''CONTROL''), 0)),
    CONVERT(int, ISNULL(HAS_PERMS_BY_NAME(DB_NAME(), N''DATABASE'', N''ALTER ANY USER''), 0)),
    CONVERT(int, ISNULL(HAS_PERMS_BY_NAME(DB_NAME(), N''DATABASE'', N''ALTER ANY ROLE''), 0)),
    CONVERT(int,
      (SELECT COUNT(*) FROM sys.fn_my_permissions(NULL, N''SERVER'')
       WHERE permission_name NOT IN (N''CONNECT SQL'', N''VIEW ANY DATABASE'')));
  END TRY
  BEGIN CATCH
    -- Without an explicit REVERT on the failure path this session stays
    -- impersonated as a login that cannot reach Orion_Training. Every later
    -- statement then fails under the wrong security context, and reusing that
    -- pooled session surfaces as "the session is in the kill state".
    IF SUSER_SNAME() <> ORIGINAL_LOGIN() REVERT;
    THROW;
  END CATCH;
  REVERT;';
-- Run in the Orion_Training context so EXECUTE AS and REVERT occur in the same
-- database. SQL Server rejects REVERT when the database has changed since the
-- impersonation began, so switching with USE inside the block cannot work; the
-- local #RuntimeBoundary table stays visible because temp tables are session
-- scoped rather than database scoped.
EXEC Orion_Training.sys.sp_executesql @VerifySql;

IF EXISTS
(
  SELECT 1
  FROM #RuntimeBoundary
  WHERE ActiveCatalog COLLATE Latin1_General_100_BIN2 <> N'Orion_Training'
     OR CurrentLogin COLLATE Latin1_General_100_BIN2 <> N'orion_training_runtime'
     OR CanAccessProduction <> 0
     OR CanAccessDevelopment <> 0
     OR IsSysadmin <> 0
     OR IsTrainingOwner <> 0
     OR CanRewriteAttestation <> 0
     OR CanControlTraining <> 0
     OR CanAlterUsers <> 0
     OR CanAlterRoles <> 0
     OR UnexpectedServerPermissions <> 0
)
  THROW 51649, 'El principal runtime no cumple el límite de seguridad de Training.', 1;

SELECT
  N'Orion_Training' AS DatabaseName,
  N'orion_training_runtime' AS RuntimeAccount,
  N'provisioned; real credential verification required' AS SafetyStatus;
