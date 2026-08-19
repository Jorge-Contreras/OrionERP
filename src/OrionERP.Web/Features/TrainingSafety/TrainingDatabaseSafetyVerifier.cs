using Microsoft.Data.SqlClient;

namespace OrionERP.Web.Features.TrainingSafety;

public sealed record TrainingDatabaseSafetyAttestation(
  bool Verified,
  int SchemaVersion,
  bool DataSanitized,
  bool SyntheticDataOnly,
  bool RuntimeLoginIsolated)
{
  public static TrainingDatabaseSafetyAttestation NotApplicable { get; } =
    new(false, 0, false, false, false);
}

public static class TrainingDatabaseSafetyVerifier
{
  public const int RequiredSchemaVersion = 1;

  public static async Task<TrainingDatabaseSafetyAttestation> VerifyOrThrowAsync(
    string connectionString,
    CancellationToken cancellationToken = default)
  {
    if (string.IsNullOrWhiteSpace(connectionString))
      throw Blocked("the Training database connection is missing");

    await using var connection = new SqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);

    await using (var boundaryCommand = connection.CreateCommand())
    {
      boundaryCommand.CommandText = """
        SELECT
          DB_NAME(),
          ORIGINAL_LOGIN(),
          CONVERT(nvarchar(20), CONNECTIONPROPERTY('auth_scheme')),
          CONVERT(int, HAS_DBACCESS(N'grupocarpio')),
          CONVERT(int, HAS_DBACCESS(N'Orion_Sandbox')),
          CONVERT(int, ISNULL(IS_SRVROLEMEMBER(N'sysadmin'), 0)),
          CONVERT(int, ISNULL(IS_ROLEMEMBER(N'db_owner'), 0)),
          CONVERT(int, databaseInfo.is_trustworthy_on),
          CONVERT(int, databaseInfo.is_db_chaining_on),
          CONVERT(int, databaseInfo.is_cdc_enabled),
          CONVERT(int, ISNULL(IS_ROLEMEMBER(N'db_ddladmin'), 0)),
          CONVERT(int, ISNULL(IS_ROLEMEMBER(N'db_securityadmin'), 0)),
          CONVERT(int, ISNULL(IS_ROLEMEMBER(N'db_datareader'), 0)),
          CONVERT(int, ISNULL(IS_ROLEMEMBER(N'db_datawriter'), 0)),
          CONVERT(int, ISNULL(HAS_PERMS_BY_NAME(DB_NAME(), N'DATABASE', N'EXECUTE'), 0)),
          CONVERT(int, ISNULL(HAS_PERMS_BY_NAME(DB_NAME(), N'DATABASE', N'VIEW DEFINITION'), 0)),
          CONVERT(int, ISNULL(HAS_PERMS_BY_NAME(
            N'capacitacion.EntornoSeguridad', N'OBJECT', N'UPDATE'), 0)),
          CONVERT(int, ISNULL(HAS_PERMS_BY_NAME(
            N'capacitacion.EsquemaVersion', N'OBJECT', N'DELETE'), 0)),
          CONVERT(int, ISNULL(HAS_PERMS_BY_NAME(DB_NAME(), N'DATABASE', N'CONTROL'), 0)),
          CONVERT(int, ISNULL(HAS_PERMS_BY_NAME(DB_NAME(), N'DATABASE', N'ALTER ANY USER'), 0)),
          CONVERT(int, ISNULL(HAS_PERMS_BY_NAME(DB_NAME(), N'DATABASE', N'ALTER ANY ROLE'), 0)),
          CONVERT(int,
          (
            SELECT COUNT(*)
            FROM sys.database_role_members membership
            JOIN sys.database_principals roleInfo
              ON roleInfo.principal_id = membership.role_principal_id
            JOIN sys.database_principals memberInfo
              ON memberInfo.principal_id = membership.member_principal_id
            WHERE memberInfo.name = N'orion_training_runtime'
              AND roleInfo.name NOT IN (N'db_datareader', N'db_datawriter')
          )),
          CONVERT(int,
            (SELECT COUNT(*) FROM sys.fn_my_permissions(NULL, N'SERVER')
             WHERE permission_name NOT IN (N'CONNECT SQL', N'VIEW ANY DATABASE'))),
          CONVERT(int,
            CASE WHEN databaseInfo.owner_sid <> 0x01
                       OR databaseInfo.containment <> 0
                       OR databaseInfo.is_broker_enabled = 1
                       OR databaseInfo.is_published = 1
                       OR databaseInfo.is_subscribed = 1
                       OR databaseInfo.is_merge_published = 1
                       OR databaseInfo.is_distributor = 1
                 THEN 1 ELSE 0 END
            -- dbo is a member of db_owner in every SQL Server database, so only
            -- non-built-in members (principal ids above 4) are counted here.
            + CASE WHEN (SELECT COUNT(*) FROM sys.database_role_members
                         WHERE member_principal_id > 4) = 2 THEN 0 ELSE 1 END
            + (SELECT COUNT(*)
               FROM sys.database_principals principalInfo
               WHERE principalInfo.principal_id > 4
                 AND principalInfo.is_fixed_role = 0
                 AND
                 (
                   principalInfo.name <> N'orion_training_runtime'
                   OR principalInfo.type <> N'S'
                   OR principalInfo.authentication_type <> 1
                   OR principalInfo.sid <> SUSER_SID(N'orion_training_runtime')
                 ))
            -- Built-in GRANTs to public on system objects (negative major_id) ship
            -- with every database and are never revoked, so they are not residue.
            + (SELECT COUNT(*)
               FROM sys.database_permissions permissionInfo
               JOIN sys.database_principals grantee
                 ON grantee.principal_id = permissionInfo.grantee_principal_id
               WHERE (grantee.name IN (N'public', N'guest') OR grantee.is_fixed_role = 1)
                 AND (permissionInfo.class = 0 OR permissionInfo.major_id > 0))
            + CASE WHEN
                (SELECT COUNT(*)
                 FROM sys.database_permissions permissionInfo
                 JOIN sys.database_principals grantee
                   ON grantee.principal_id = permissionInfo.grantee_principal_id
                 WHERE grantee.name = N'orion_training_runtime') = 10
              THEN 0 ELSE 1 END
            + (SELECT COUNT(*)
               FROM sys.database_permissions permissionInfo
               JOIN sys.database_principals grantee
                 ON grantee.principal_id = permissionInfo.grantee_principal_id
               WHERE grantee.name = N'orion_training_runtime'
                 AND NOT
                 (
                   permissionInfo.class = 0
                   AND permissionInfo.major_id = 0
                   AND permissionInfo.minor_id = 0
                   AND permissionInfo.state = N'G'
                   AND permissionInfo.permission_name IN
                     (N'CONNECT', N'EXECUTE', N'VIEW DEFINITION', N'VIEW DATABASE STATE')
                   OR permissionInfo.class = 1
                   AND permissionInfo.major_id IN
                     (OBJECT_ID(N'capacitacion.EntornoSeguridad'), OBJECT_ID(N'capacitacion.EsquemaVersion'))
                   AND permissionInfo.minor_id = 0
                   AND permissionInfo.state = N'D'
                   AND permissionInfo.permission_name IN (N'INSERT', N'UPDATE', N'DELETE')
                 ))
            + CASE WHEN EXISTS
                (SELECT 1 FROM sys.database_query_store_options
                 WHERE actual_state <> 0 OR desired_state <> 0)
              THEN 1 ELSE 0 END)
            + (SELECT COUNT(*) FROM sys.query_store_query_text)
        FROM sys.databases databaseInfo
        WHERE databaseInfo.database_id = DB_ID();
        """;

      await using var reader = await boundaryCommand.ExecuteReaderAsync(cancellationToken);
      if (!await reader.ReadAsync(cancellationToken))
        throw Blocked("database identity could not be verified");

      var catalog = reader.GetString(0);
      if (!string.Equals(catalog, TrainingEnvironment.RequiredDatabaseCatalog, StringComparison.Ordinal))
        throw Blocked($"the active catalog is '{catalog}', not Orion_Training");
      if (!string.Equals(reader.GetString(1), TrainingEnvironment.RequiredRuntimeLogin, StringComparison.Ordinal)
          || !string.Equals(reader.GetString(2), "SQL", StringComparison.OrdinalIgnoreCase))
        throw Blocked("the active connection is not the fixed Training SQL-auth login");
      if (reader.GetInt32(3) != 0 || reader.GetInt32(4) != 0)
        throw Blocked("the runtime SQL login can access a production or development catalog");
      if (reader.GetInt32(5) != 0 || reader.GetInt32(6) != 0
          || reader.GetInt32(10) != 0 || reader.GetInt32(11) != 0)
        throw Blocked("the runtime SQL login is sysadmin or db_owner instead of least privilege");
      if (reader.GetInt32(7) != 0 || reader.GetInt32(8) != 0 || reader.GetInt32(9) != 0)
        throw Blocked("TRUSTWORTHY, database chaining, or CDC is enabled");
      if (reader.GetInt32(12) != 1 || reader.GetInt32(13) != 1
          || reader.GetInt32(14) != 1 || reader.GetInt32(15) != 1
          || reader.GetInt32(21) != 0)
        throw Blocked("the runtime SQL login roles and grants do not match the Training contract");
      if (reader.GetInt32(16) != 0 || reader.GetInt32(17) != 0)
        throw Blocked("the runtime SQL login can rewrite Training safety attestation data");
      if (reader.GetInt32(18) != 0 || reader.GetInt32(19) != 0 || reader.GetInt32(20) != 0)
        throw Blocked("the runtime SQL login can change Training database security or schema authority");
      if (reader.GetInt32(22) != 0)
        throw Blocked("the runtime SQL login inherited an unexpected server permission");
      if (reader.GetInt32(23) != 0)
        throw Blocked("the database owner, containment, principal, membership, explicit-permission, or Query Store manifest drifted");
    }

    await using (var crossDatabaseCommand = connection.CreateCommand())
    {
      crossDatabaseCommand.CommandText = """
        WITH ExpectedSynonyms AS
        (
          SELECT *
          FROM
          (
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
              (N'dbo', N'Traslados', N'cfdi', N'Traslados')
          ) manifest(SynonymSchema, SynonymName, TargetSchema, TargetName)
        )
        SELECT
          CASE WHEN
            (SELECT COUNT(*) FROM sys.synonyms) = (SELECT COUNT(*) FROM ExpectedSynonyms)
            AND NOT EXISTS
            (
              SELECT 1
              FROM sys.synonyms synonymInfo
              JOIN sys.schemas synonymSchema ON synonymSchema.schema_id = synonymInfo.schema_id
              LEFT JOIN ExpectedSynonyms expected
                ON expected.SynonymSchema COLLATE Latin1_General_100_BIN2 = synonymSchema.name COLLATE Latin1_General_100_BIN2
               AND expected.SynonymName COLLATE Latin1_General_100_BIN2 = synonymInfo.name COLLATE Latin1_General_100_BIN2
              WHERE expected.SynonymName IS NULL
                 OR PARSENAME(synonymInfo.base_object_name, 4) IS NOT NULL
                 OR PARSENAME(synonymInfo.base_object_name, 3) IS NOT NULL
                 OR ISNULL(PARSENAME(synonymInfo.base_object_name, 2), N'') COLLATE Latin1_General_100_BIN2
                      <> expected.TargetSchema COLLATE Latin1_General_100_BIN2
                 OR ISNULL(PARSENAME(synonymInfo.base_object_name, 1), N'') COLLATE Latin1_General_100_BIN2
                      <> expected.TargetName COLLATE Latin1_General_100_BIN2
                 OR OBJECT_ID(QUOTENAME(expected.TargetSchema) + N'.' + QUOTENAME(expected.TargetName), N'U') IS NULL
            )
            AND NOT EXISTS
            (
              SELECT 1 FROM ExpectedSynonyms expected
              WHERE NOT EXISTS
              (
                SELECT 1
                FROM sys.synonyms synonymInfo
                JOIN sys.schemas synonymSchema ON synonymSchema.schema_id = synonymInfo.schema_id
                WHERE synonymSchema.name COLLATE Latin1_General_100_BIN2 = expected.SynonymSchema COLLATE Latin1_General_100_BIN2
                  AND synonymInfo.name COLLATE Latin1_General_100_BIN2 = expected.SynonymName COLLATE Latin1_General_100_BIN2
              )
            )
          THEN 0 ELSE 1 END
          +
          (SELECT COUNT_BIG(*)
           FROM sys.sql_modules
           WHERE definition LIKE N'%grupocarpio%'
              OR definition LIKE N'%Orion_Sandbox%'
              OR definition LIKE N'%sp_send_dbmail%'
              OR definition LIKE N'%xp_cmdshell%'
              OR definition LIKE N'%sp_OA%'
              OR definition LIKE N'%sp_execute_external_script%'
              OR definition LIKE N'%OPENROWSET%'
              OR definition LIKE N'%OPENDATASOURCE%'
              OR definition LIKE N'%OPENQUERY%'
              OR definition LIKE N'%BULK INSERT%'
              OR definition LIKE N'%EXECUTE% AT %'
              OR definition LIKE N'%EXEC% AT %')
          + (SELECT COUNT_BIG(*) FROM sys.external_tables)
          + (SELECT COUNT_BIG(*) FROM sys.external_data_sources)
          + (SELECT COUNT_BIG(*) FROM sys.database_scoped_credentials)
          + (SELECT COUNT_BIG(*) FROM sys.assemblies WHERE is_user_defined = 1)
          + (SELECT COUNT_BIG(*) FROM sys.triggers WHERE parent_class = 0 AND is_ms_shipped = 0)
          + (SELECT COUNT_BIG(*) FROM sys.change_tracking_databases WHERE database_id = DB_ID())
          + (SELECT COUNT_BIG(*) FROM sys.security_policies)
          + (SELECT COUNT_BIG(*) FROM sys.security_predicates)
          + (SELECT COUNT_BIG(*) FROM sys.certificates)
          + (SELECT COUNT_BIG(*) FROM sys.asymmetric_keys)
          + (SELECT COUNT_BIG(*) FROM sys.symmetric_keys)
          + (SELECT COUNT_BIG(*) FROM sys.column_master_keys)
          + (SELECT COUNT_BIG(*) FROM sys.column_encryption_keys)
          + (SELECT COUNT_BIG(*) FROM sys.fulltext_indexes)
          + (SELECT COUNT_BIG(*) FROM sys.fulltext_catalogs)
          + (SELECT COUNT_BIG(*) FROM sys.views viewInfo
             JOIN sys.indexes indexInfo ON indexInfo.object_id = viewInfo.object_id
             WHERE indexInfo.index_id > 0)
          + (SELECT COUNT_BIG(*) FROM sys.service_queues WHERE is_ms_shipped = 0)
          + (SELECT COUNT_BIG(*) FROM sys.transmission_queue)
          + (SELECT COUNT_BIG(*) FROM sys.conversation_endpoints)
          + (SELECT COUNT_BIG(*) FROM sys.crypt_properties)
          + (SELECT COUNT_BIG(*) FROM sys.sql_modules WHERE execute_as_principal_id IS NOT NULL)
          + (SELECT COUNT_BIG(*)
             FROM sys.sql_modules moduleInfo
             JOIN sys.objects objectInfo ON objectInfo.object_id = moduleInfo.object_id
             WHERE objectInfo.is_ms_shipped = 0 AND moduleInfo.definition IS NULL)
          + (SELECT COUNT_BIG(*) FROM sys.sql_expression_dependencies
             WHERE referenced_server_name IS NOT NULL OR referenced_database_name IS NOT NULL)
          + (SELECT COUNT_BIG(*) FROM sys.tables
             WHERE is_ms_shipped = 0
               AND (temporal_type <> 0 OR is_memory_optimized = 1 OR is_filetable = 1
                    OR is_replicated = 1 OR is_merge_published = 1
                    OR is_sync_tran_subscribed = 1 OR is_tracked_by_cdc = 1))
          + (SELECT COUNT_BIG(*) FROM sys.columns WHERE is_filestream = 1)
          + (SELECT COUNT_BIG(*) FROM sys.triggers
             WHERE parent_class = 1 AND is_ms_shipped = 0 AND is_disabled = 1)
          + (SELECT COUNT_BIG(*) FROM sys.foreign_keys
             WHERE is_disabled = 1 OR is_not_trusted = 1)
          + (SELECT COUNT_BIG(*) FROM sys.check_constraints
             WHERE is_disabled = 1 OR is_not_trusted = 1)
          + (SELECT COUNT_BIG(*) FROM sys.schemas
             WHERE name NOT IN (N'dbo', N'guest', N'INFORMATION_SCHEMA', N'sys')
               AND principal_id <> USER_ID(N'dbo'))
          + (SELECT COUNT_BIG(*) FROM sys.objects
             WHERE is_ms_shipped = 0 AND principal_id IS NOT NULL
               AND principal_id <> USER_ID(N'dbo'))
          + CASE WHEN (SELECT COUNT(*) FROM sys.triggers
                       WHERE parent_class = 1 AND is_ms_shipped = 0) = 17
                 THEN 0 ELSE 1 END
          + (SELECT COUNT_BIG(*)
             FROM sys.triggers triggerInfo
             JOIN sys.objects triggerObject ON triggerObject.object_id = triggerInfo.object_id
             JOIN sys.objects parentInfo ON parentInfo.object_id = triggerInfo.parent_id
             JOIN sys.schemas triggerSchema ON triggerSchema.schema_id = triggerObject.schema_id
             JOIN sys.schemas parentSchema ON parentSchema.schema_id = parentInfo.schema_id
             WHERE triggerInfo.parent_class = 1
               AND triggerInfo.is_ms_shipped = 0
               AND NOT EXISTS
               (
                 SELECT 1
                 FROM
                 (
                   VALUES
                     (N'dbo', N'trg_Transacciones_Audit', N'dbo', N'Transacciones'),
                     (N'dbo', N'trg_Registro_Contable_Audit', N'dbo', N'Registro_Contable'),
                     (N'dbo', N'TR_Transaccion_Comprobante_BlockPago20Direct', N'dbo', N'Transaccion_Comprobante'),
                     (N'capacitacion', N'TR_EntornoSeguridad_MaintenanceOnly', N'capacitacion', N'EntornoSeguridad'),
                     (N'capacitacion', N'TR_EsquemaVersion_MaintenanceOnly', N'capacitacion', N'EsquemaVersion'),
                     (N'capacitacion', N'TR_Finalizacion_AppendOnly', N'capacitacion', N'Finalizacion'),
                     (N'capacitacion', N'TR_EventoAuditoria_AppendOnly', N'capacitacion', N'EventoAuditoria'),
                     (N'capacitacion', N'TR_CursoVersion_PublicadaInmutable', N'capacitacion', N'CursoVersion'),
                     (N'capacitacion', N'TR_FirmaInstructor_AppendOnly', N'capacitacion', N'FirmaInstructor'),
                     (N'capacitacion', N'TR_Leccion_VersionPublicadaInmutable', N'capacitacion', N'Leccion'),
                     (N'capacitacion', N'TR_BloqueContenido_VersionPublicadaInmutable', N'capacitacion', N'BloqueContenido'),
                     (N'capacitacion', N'TR_Recurso_VersionPublicadaInmutable', N'capacitacion', N'Recurso'),
                     (N'capacitacion', N'TR_Evaluacion_VersionPublicadaInmutable', N'capacitacion', N'Evaluacion'),
                     (N'capacitacion', N'TR_Pregunta_VersionPublicadaInmutable', N'capacitacion', N'Pregunta'),
                     (N'capacitacion', N'TR_OpcionPregunta_VersionPublicadaInmutable', N'capacitacion', N'OpcionPregunta'),
                     (N'capacitacion', N'TR_Practica_VersionPublicadaInmutable', N'capacitacion', N'Practica'),
                     (N'capacitacion', N'TR_PracticaPaso_VersionPublicadaInmutable', N'capacitacion', N'PracticaPaso')
                 ) allowed(TriggerSchema, TriggerName, ParentSchema, ParentTable)
                 WHERE allowed.TriggerSchema = triggerSchema.name
                   AND allowed.TriggerName = triggerInfo.name
                   AND allowed.ParentSchema = parentSchema.name
                   AND allowed.ParentTable = parentInfo.name
               ));
        """;
      var crossDatabaseReferences = Convert.ToInt64(
        await crossDatabaseCommand.ExecuteScalarAsync(cancellationToken));
      if (crossDatabaseReferences != 0)
        throw Blocked("the database contains a cross-catalog or external-effect SQL object");
    }

    await using (var schemaCommand = connection.CreateCommand())
    {
      schemaCommand.CommandText = """
        SELECT CASE WHEN
          OBJECT_ID(N'capacitacion.EsquemaVersion', N'U') IS NOT NULL
          AND OBJECT_ID(N'capacitacion.EntornoSeguridad', N'U') IS NOT NULL
          AND OBJECT_ID(N'capacitacion.Curso', N'U') IS NOT NULL
          AND OBJECT_ID(N'capacitacion.Asignacion', N'U') IS NOT NULL
          AND OBJECT_ID(N'capacitacion.Sesion', N'U') IS NOT NULL
          THEN 1 ELSE 0 END;
        """;
      if (Convert.ToInt32(await schemaCommand.ExecuteScalarAsync(cancellationToken)) != 1)
        throw Blocked("the required capacitacion schema is not installed");
    }

    await using var attestationCommand = connection.CreateCommand();
    attestationCommand.CommandText = """
      SELECT TOP (1)
        safety.VersionEsquema,
        CONVERT(int, safety.DatosSanitizados),
        CONVERT(int, safety.DatosSinteticos)
      FROM capacitacion.EntornoSeguridad safety
      WHERE safety.EntornoSeguridadId = 1
        AND safety.Entorno = N'Training'
        AND safety.RevisadoEn IS NOT NULL
        AND NULLIF(LTRIM(RTRIM(safety.RevisadoPor)), N'') IS NOT NULL;
      """;
    await using var attestationReader = await attestationCommand.ExecuteReaderAsync(cancellationToken);
    if (!await attestationReader.ReadAsync(cancellationToken))
      throw Blocked("the sanitized Training database attestation is absent");

    var schemaVersion = attestationReader.GetInt32(0);
    var dataSanitized = attestationReader.GetInt32(1) == 1;
    var syntheticOnly = attestationReader.GetInt32(2) == 1;
    if (schemaVersion < RequiredSchemaVersion || !dataSanitized || !syntheticOnly)
      throw Blocked("the Training database is not attested as sanitized synthetic data");

    return new TrainingDatabaseSafetyAttestation(
      Verified: true,
      SchemaVersion: schemaVersion,
      DataSanitized: true,
      SyntheticDataOnly: true,
      RuntimeLoginIsolated: true);
  }

  private static InvalidOperationException Blocked(string reason)
    => new($"Training startup blocked: {reason}.");
}
