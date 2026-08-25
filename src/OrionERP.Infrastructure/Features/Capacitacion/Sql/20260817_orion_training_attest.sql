/*
  Final, fail-closed verification for Orion_Training sanitization v1.

  This is the only batch in the workflow that may mark EntornoSeguridad ready.
  It is intentionally strict: every non-empty table must be explicitly allowed,
  and every allowed operational row must carry a recognizable synthetic marker.

  Required ADO.NET parameter: @ReviewedBy nvarchar(256)
*/

SET NOCOUNT ON;
SET XACT_ABORT ON;

IF DB_NAME() COLLATE Latin1_General_100_BIN2 <> N'Orion_Training' COLLATE Latin1_General_100_BIN2
  THROW 51740, 'ATTESTATION BLOCKED: the active database is not exactly Orion_Training.', 1;

IF ISNULL(TRY_CONVERT(nvarchar(64), SESSION_CONTEXT(N'OrionTrainingSanitizerApply')), N'') <> N'20260817-v1'
  THROW 51741, 'ATTESTATION BLOCKED: the guarded PowerShell workflow did not authorize this session.', 1;

DECLARE @SanitizerStartedAt datetime =
  TRY_CONVERT(datetime, SESSION_CONTEXT(N'OrionTrainingSanitizerStartedAt'));
IF @SanitizerStartedAt IS NULL
  THROW 51844, 'ATTESTATION BLOCKED: the guarded reset start marker is absent.', 1;

IF NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(256), @ReviewedBy))), N'') IS NULL
  THROW 51742, 'ATTESTATION BLOCKED: ReviewedBy is required.', 1;

IF ISNULL(IS_SRVROLEMEMBER(N'sysadmin'), 0) <> 1
  THROW 51830, 'ATTESTATION BLOCKED: the final verification requires the guarded sysadmin maintenance session.', 1;

IF EXISTS
(
  SELECT 1
  FROM sys.dm_exec_sessions sessionInfo
  WHERE sessionInfo.database_id = DB_ID()
    AND sessionInfo.session_id <> @@SPID
    AND sessionInfo.is_user_process = 1
)
  THROW 51765, 'ATTESTATION BLOCKED: another Orion_Training session appeared during the workflow.', 1;

IF EXISTS
(
  SELECT 1
  FROM sys.databases databaseInfo
  WHERE databaseInfo.database_id = DB_ID()
    AND (databaseInfo.owner_sid <> 0x01
         OR databaseInfo.containment <> 0
         OR databaseInfo.is_trustworthy_on = 1
         OR databaseInfo.is_db_chaining_on = 1
         OR databaseInfo.is_cdc_enabled = 1
         OR databaseInfo.is_broker_enabled = 1
         OR databaseInfo.is_published = 1
         OR databaseInfo.is_subscribed = 1
         OR databaseInfo.is_merge_published = 1
         OR databaseInfo.is_distributor = 1)
)
  THROW 51743, 'ATTESTATION BLOCKED: owner, containment, TRUSTWORTHY, chaining, CDC, Broker, or replication is unsafe.', 1;

IF EXISTS (SELECT 1 FROM sys.change_tracking_databases WHERE database_id = DB_ID())
  THROW 51766, 'ATTESTATION BLOCKED: SQL Change Tracking is enabled.', 1;

IF EXISTS
(
  SELECT 1
  FROM sys.tables
  WHERE is_ms_shipped = 0
    AND (temporal_type <> 0
         OR is_memory_optimized = 1
         OR is_filetable = 1
         OR is_replicated = 1
         OR is_merge_published = 1
         OR is_sync_tran_subscribed = 1
         OR is_tracked_by_cdc = 1)
)
   OR EXISTS (SELECT 1 FROM sys.columns WHERE is_filestream = 1)
   OR EXISTS (SELECT 1 FROM sys.transmission_queue)
   OR EXISTS (SELECT 1 FROM sys.conversation_endpoints)
   OR EXISTS (SELECT 1 FROM sys.service_queues WHERE is_ms_shipped = 0)
  THROW 51831, 'ATTESTATION BLOCKED: temporal/FILESTREAM/replication/CDC or retained Service Broker state exists.', 1;

IF EXISTS
(
  SELECT 1
  FROM sys.database_query_store_options
  WHERE actual_state <> 0 OR desired_state <> 0
)
   OR EXISTS (SELECT 1 FROM sys.query_store_query_text)
  THROW 51832, 'ATTESTATION BLOCKED: Query Store is not disabled and empty.', 1;

IF EXISTS
(
  SELECT 1
  FROM sys.database_principals
  WHERE principal_id > 4
    AND is_fixed_role = 0
)
   OR EXISTS
      (
        -- dbo belongs to db_owner in every SQL Server database and cannot be
        -- removed, so only cloned members can be verified gone. Scoped exactly
        -- like the sanitizer's membership sweep.
        SELECT 1
        FROM sys.database_role_members membership
        WHERE membership.member_principal_id > 4
      )
  THROW 51833, 'ATTESTATION BLOCKED: a cloned database principal or role membership survived.', 1;

IF EXISTS
(
  SELECT 1
  FROM sys.database_permissions permissionInfo
  JOIN sys.database_principals grantee
    ON grantee.principal_id = permissionInfo.grantee_principal_id
  -- Every database ships built-in GRANTs to public on system objects (negative
  -- major_id). The sanitizer never revokes them, so verifying against them would
  -- block attestation on a clean database too.
  WHERE (grantee.name IN (N'public', N'guest') OR grantee.is_fixed_role = 1)
    AND (permissionInfo.class = 0 OR permissionInfo.major_id > 0)
)
  THROW 51834, 'ATTESTATION BLOCKED: public, guest, or a fixed role retained an explicit permission.', 1;

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
  THROW 51845, 'ATTESTATION BLOCKED: a user schema or object has a non-dbo owner.', 1;

IF EXISTS (SELECT 1 FROM sys.triggers WHERE parent_class = 0 AND is_ms_shipped = 0)
   OR EXISTS (SELECT 1 FROM sys.security_policies)
   OR EXISTS (SELECT 1 FROM sys.security_predicates)
   OR EXISTS (SELECT 1 FROM sys.external_tables)
   OR EXISTS (SELECT 1 FROM sys.external_data_sources)
   OR EXISTS (SELECT 1 FROM sys.database_scoped_credentials)
   OR EXISTS (SELECT 1 FROM sys.assemblies WHERE is_user_defined = 1)
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
  THROW 51767, 'ATTESTATION BLOCKED: a DDL/RLS/external/CLR/key/full-text/indexed-view/credential object exists.', 1;

IF (SELECT COUNT(*) FROM sys.synonyms) <> 12
   OR EXISTS
   (
     SELECT 1
     FROM sys.synonyms synonymInfo
     JOIN sys.schemas synonymSchema ON synonymSchema.schema_id = synonymInfo.schema_id
     LEFT JOIN
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
     ) expected(SynonymSchema, SynonymName, TargetSchema, TargetName)
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
   OR EXISTS
   (
     SELECT 1
     FROM
     (
       VALUES
         (N'dbo', N'Comprobante'), (N'dbo', N'Concepto'),
         (N'dbo', N'Conceptos'), (N'dbo', N'Emisor'),
         (N'dbo', N'Impuestos'), (N'dbo', N'InformacionGlobal'), (N'dbo', N'Receptor'),
         (N'dbo', N'retencion'), (N'dbo', N'Retenciones'),
         (N'dbo', N'TimbreFiscalDigital'), (N'dbo', N'traslado'), (N'dbo', N'Traslados')
     ) expected(SynonymSchema, SynonymName)
     WHERE NOT EXISTS
     (
       SELECT 1
       FROM sys.synonyms synonymInfo
       JOIN sys.schemas synonymSchema ON synonymSchema.schema_id = synonymInfo.schema_id
       WHERE synonymSchema.name COLLATE Latin1_General_100_BIN2 = expected.SynonymSchema COLLATE Latin1_General_100_BIN2
         AND synonymInfo.name COLLATE Latin1_General_100_BIN2 = expected.SynonymName COLLATE Latin1_General_100_BIN2
     )
   )
  THROW 51744, 'ATTESTATION BLOCKED: the exact 12-local-synonym manifest is missing, remote, retargeted, or has extras.', 1;

IF EXISTS
(
  SELECT 1
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
     OR definition LIKE N'%EXEC% AT %'
)
  THROW 51745, 'ATTESTATION BLOCKED: a module has a cross-catalog or external-effect primitive.', 1;

IF EXISTS
(
  SELECT 1
  FROM sys.sql_modules moduleInfo
  JOIN sys.objects objectInfo ON objectInfo.object_id = moduleInfo.object_id
  WHERE objectInfo.is_ms_shipped = 0
    AND moduleInfo.definition IS NULL
)
   OR EXISTS
      (SELECT 1 FROM sys.sql_expression_dependencies
       WHERE referenced_server_name IS NOT NULL OR referenced_database_name IS NOT NULL)
   OR EXISTS (SELECT 1 FROM sys.crypt_properties)
   OR EXISTS
      (SELECT 1 FROM sys.sql_modules
       WHERE execute_as_principal_id IS NOT NULL)
  THROW 51835, 'ATTESTATION BLOCKED: encrypted/cross-database/signed/EXECUTE AS module state exists.', 1;

IF EXISTS
(
  SELECT 1
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
    )
)
  THROW 51768, 'ATTESTATION BLOCKED: an unreviewed DML trigger exists.', 1;

IF OBJECT_ID(N'capacitacion.EntornoSeguridad', N'U') IS NULL
   OR OBJECT_ID(N'capacitacion.EsquemaVersion', N'U') IS NULL
  THROW 51750, 'ATTESTATION BLOCKED: the training safety schema is missing.', 1;

IF NOT EXISTS (SELECT 1 FROM capacitacion.EsquemaVersion WHERE Version = 1)
  THROW 51751, 'ATTESTATION BLOCKED: capacitacion schema version 1 is missing.', 1;

IF (SELECT COUNT(*) FROM capacitacion.EntornoSeguridad) <> 1
   OR NOT EXISTS
      (
        SELECT 1
        FROM capacitacion.EntornoSeguridad
        WHERE EntornoSeguridadId = 1
          AND Entorno = N'Training'
          AND DatosSanitizados = 0
          AND DatosSinteticos = 0
          AND RevisadoEn IS NULL
          AND RevisadoPor IS NULL
          AND VersionEsquema = 1
      )
  THROW 51752, 'ATTESTATION BLOCKED: EntornoSeguridad is not in its required untrusted state.', 1;

IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE is_disabled = 1 OR is_not_trusted = 1)
   OR EXISTS (SELECT 1 FROM sys.check_constraints WHERE is_disabled = 1 OR is_not_trusted = 1)
   OR EXISTS (SELECT 1 FROM sys.triggers WHERE parent_class = 1 AND is_disabled = 1)
  THROW 51762, 'ATTESTATION BLOCKED: every foreign key, check constraint, and DML trigger must be enabled and trusted.', 1;

IF OBJECT_ID(N'dbo.trg_Transacciones_Audit', N'TR') IS NULL
   OR OBJECT_ID(N'dbo.trg_Registro_Contable_Audit', N'TR') IS NULL
   OR OBJECT_ID(N'dbo.TR_Transaccion_Comprobante_BlockPago20Direct', N'TR') IS NULL
   OR OBJECT_ID(N'capacitacion.TR_EntornoSeguridad_MaintenanceOnly', N'TR') IS NULL
   OR OBJECT_ID(N'capacitacion.TR_EsquemaVersion_MaintenanceOnly', N'TR') IS NULL
   OR OBJECT_ID(N'capacitacion.TR_Finalizacion_AppendOnly', N'TR') IS NULL
   OR OBJECT_ID(N'capacitacion.TR_EventoAuditoria_AppendOnly', N'TR') IS NULL
   OR OBJECT_ID(N'capacitacion.TR_CursoVersion_PublicadaInmutable', N'TR') IS NULL
   OR OBJECT_ID(N'capacitacion.TR_FirmaInstructor_AppendOnly', N'TR') IS NULL
   OR OBJECT_ID(N'capacitacion.TR_Leccion_VersionPublicadaInmutable', N'TR') IS NULL
   OR OBJECT_ID(N'capacitacion.TR_BloqueContenido_VersionPublicadaInmutable', N'TR') IS NULL
   OR OBJECT_ID(N'capacitacion.TR_Recurso_VersionPublicadaInmutable', N'TR') IS NULL
   OR OBJECT_ID(N'capacitacion.TR_Evaluacion_VersionPublicadaInmutable', N'TR') IS NULL
   OR OBJECT_ID(N'capacitacion.TR_Pregunta_VersionPublicadaInmutable', N'TR') IS NULL
   OR OBJECT_ID(N'capacitacion.TR_OpcionPregunta_VersionPublicadaInmutable', N'TR') IS NULL
   OR OBJECT_ID(N'capacitacion.TR_Practica_VersionPublicadaInmutable', N'TR') IS NULL
   OR OBJECT_ID(N'capacitacion.TR_PracticaPaso_VersionPublicadaInmutable', N'TR') IS NULL
  THROW 51764, 'ATTESTATION BLOCKED: one or more reviewed operational or training triggers are missing.', 1;

SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;
BEGIN TRANSACTION;

-- A table variable cannot carry a named constraint; only the inline, unnamed
-- form parses. The key itself is what this manifest needs.
DECLARE @AllowedNonEmpty TABLE
(
  SchemaName sysname NOT NULL,
  TableName sysname NOT NULL,
  PRIMARY KEY (SchemaName, TableName)
);

INSERT @AllowedNonEmpty (SchemaName, TableName)
VALUES
  (N'dbo', N'__EFMigrationsHistory'),
  (N'dbo', N'DateDimension'),
  (N'dbo', N'RazonSocial'),
  (N'dbo', N'Capital_Humano'),
  (N'dbo', N'Clientes'),
  (N'dbo', N'ROOM'),
  (N'dbo', N'ROOM_CALENDAR'),
  (N'dbo', N'RESERVATION'),
  (N'dbo', N'RESERVATION_DETAIL'),
  (N'auth', N'AspNetRoles'),
  (N'auth', N'AspNetUsers'),
  (N'auth', N'AspNetUserClaims'),
  (N'auth', N'AspNetUserRoles'),
  (N'auth', N'AspNetUserCompanies'),
  (N'auth', N'AspNetUserCompanyRoles'),
  (N'orion', N'Company'),
  (N'logistica', N'UnitOfMeasure'),
  (N'logistica', N'MaterialCategory'),
  (N'logistica', N'Location'),
  (N'logistica', N'Material'),
  (N'logistica', N'StockBalance'),
  (N'rh', N'WorkSite'),
  (N'rh', N'ScheduleTemplate'),
  (N'rh', N'ScheduleDay'),
  (N'rh', N'AttendancePolicy'),
  (N'rh', N'PayGroup'),
  (N'rh', N'EmployeeWorkAssignment'),
  (N'rh', N'LeaveType'),
  (N'rh', N'LeavePolicy'),
  (N'rh', N'LeaveEnrollment'),
  (N'rh', N'LeaveBalanceLedger'),
  (N'rh', N'PrivacyNotice'),
  (N'capacitacion', N'EsquemaVersion'),
  (N'capacitacion', N'EntornoSeguridad'),
  (N'capacitacion', N'Curso'),
  (N'capacitacion', N'CursoVersion'),
  (N'capacitacion', N'Leccion'),
  (N'capacitacion', N'BloqueContenido'),
  (N'capacitacion', N'Recurso'),
  (N'capacitacion', N'Evaluacion'),
  (N'capacitacion', N'Pregunta'),
  (N'capacitacion', N'OpcionPregunta'),
  (N'capacitacion', N'Practica'),
  (N'capacitacion', N'PracticaPaso'),
  (N'capacitacion', N'RutaAprendizaje'),
  (N'capacitacion', N'RutaCurso'),
  (N'capacitacion', N'Asignacion'),
  -- The reviewed reference catalog preserved by the sanitizer. Its contents are
  -- pinned to the canonical SAT manifest by guard 51769 below.
  (N'dbo', N'Formas_Pago'),
  -- Synthetic catalogs written by 20260821_orion_training_catalogos.sql. Every
  -- table the seed touches must appear here, or the sweep below throws 51753.
  -- A unit test diffs the seed's targets against this list so the two cannot
  -- drift apart.
  (N'dbo', N'CuentasContables'),
  (N'dbo', N'CfdiPolizaCuentaDefault'),
  (N'dbo', N'PlantillaContable'),
  (N'dbo', N'PlantillaContableLinea'),
  (N'dbo', N'Actividad'),
  (N'dbo', N'Compra'),
  (N'dbo', N'Servicios'),
  (N'dbo', N'Proveedores'),
  (N'dbo', N'PARAMETROS_CONFIGURACION'),
  (N'dbo', N'OrdenTrabajoCategoria'),
  (N'dbo', N'Extra'),
  (N'dbo', N'ExperienceProvider'),
  (N'dbo', N'Experience'),
  (N'dbo', N'ExperiencePackage'),
  (N'dbo', N'ExperienceAddOn'),
  (N'dbo', N'BusinessPartner'),
  (N'dbo', N'BusinessPartnerRfcScope'),
  (N'dbo', N'BusinessPartnerRole'),
  (N'dbo', N'SatRfcProfile'),
  (N'bancos', N'Cuentas_Banco'),
  (N'logistica', N'Allergen'),
  (N'logistica', N'UnitConversion'),
  (N'rh', N'Holiday'),
  -- Restaurant configuration only. The transactional tables of this schema
  -- (Order, OrderLine, Payment, CashShift, CashMovement, QuickPin,
  -- SupervisorAuthorization, EventOutbox, DailySequence) stay off this list on
  -- purpose: a trainee creates those by working, and their emptiness is what
  -- makes this manifest worth checking.
  (N'restaurante', N'Site'),
  (N'restaurante', N'SiteLocationPriority'),
  (N'restaurante', N'DiningTable'),
  (N'restaurante', N'KitchenStation'),
  (N'restaurante', N'CashRegister'),
  (N'restaurante', N'ExternalProvider'),
  (N'restaurante', N'AccountingConfiguration'),
  (N'restaurante', N'ProductCard'),
  (N'restaurante', N'Product'),
  (N'restaurante', N'ModifierGroup'),
  (N'restaurante', N'ModifierOption'),
  (N'restaurante', N'ProductModifierGroup'),
  (N'restaurante', N'Menu'),
  (N'restaurante', N'MenuSection'),
  (N'restaurante', N'MenuItem'),
  (N'restaurante', N'MenuSchedule');

DECLARE @QualifiedName nvarchar(517);
DECLARE @HasRows bit = 0;
DECLARE @Statement nvarchar(max);
DECLARE @UnexpectedTable nvarchar(517) = NULL;

DECLARE UnexpectedTableCursor CURSOR LOCAL FAST_FORWARD FOR
  SELECT QUOTENAME(schemaInfo.name) + N'.' + QUOTENAME(tableInfo.name)
  FROM sys.tables tableInfo
  JOIN sys.schemas schemaInfo ON schemaInfo.schema_id = tableInfo.schema_id
  LEFT JOIN @AllowedNonEmpty allowed
    ON allowed.SchemaName = schemaInfo.name AND allowed.TableName = tableInfo.name
  WHERE tableInfo.is_ms_shipped = 0
    AND allowed.TableName IS NULL
  ORDER BY schemaInfo.name, tableInfo.name;

OPEN UnexpectedTableCursor;
FETCH NEXT FROM UnexpectedTableCursor INTO @QualifiedName;
WHILE @@FETCH_STATUS = 0 AND @HasRows = 0
BEGIN
  SET @Statement = N'SELECT @Found = CONVERT(bit, CASE WHEN EXISTS (SELECT 1 FROM '
                 + @QualifiedName + N') THEN 1 ELSE 0 END);';
  EXEC sys.sp_executesql @Statement, N'@Found bit OUTPUT', @Found = @HasRows OUTPUT;
  IF @HasRows = 1 SET @UnexpectedTable = @QualifiedName;
  FETCH NEXT FROM UnexpectedTableCursor INTO @QualifiedName;
END;
CLOSE UnexpectedTableCursor;
DEALLOCATE UnexpectedTableCursor;

IF @HasRows = 1
  THROW 51753, 'ATTESTATION BLOCKED: a table outside the synthetic manifest contains rows.', 1;

IF (SELECT COUNT(*) FROM dbo.RazonSocial) <> 1
   OR EXISTS (SELECT 1 FROM dbo.RazonSocial WHERE Nombre <> N'ORION TRAINING · ORGANIZACIÓN FICTICIA')
  THROW 51754, 'ATTESTATION BLOCKED: the fictional company manifest does not match.', 1;

IF (SELECT COUNT(*) FROM dbo.Capital_Humano) <> 4
   OR EXISTS
      (
        SELECT 1
        FROM dbo.Capital_Humano employeeInfo
        WHERE employeeInfo.ID NOT IN (990001, 990002, 990003, 990004)
           OR employeeInfo.RFC <> 'XAXX010101000'
           OR employeeInfo.[Status] <> N'ACTIVO'
           OR employeeInfo.RFC_Capital_Humano NOT LIKE N'TRAINING-%'
           OR employeeInfo.Usuario_Acceso NOT LIKE '%@training.orion.local'
           OR employeeInfo.Contrasena_Acceso <> 'NO-ES-CREDENCIAL'
           OR employeeInfo.CURP IS NOT NULL
           OR employeeInfo.Fecha_Nacimiento IS NOT NULL
           OR employeeInfo.Seguro_Social IS NOT NULL
           OR employeeInfo.Calle IS NOT NULL
           OR employeeInfo.Colonia IS NOT NULL
           OR employeeInfo.Comunidad IS NOT NULL
           OR employeeInfo.Ciudad IS NOT NULL
           OR employeeInfo.Estado IS NOT NULL
           OR employeeInfo.Telefono IS NOT NULL
           OR employeeInfo.Numero_Emergencia IS NOT NULL
           OR employeeInfo.Sueldo_Mensual IS NOT NULL
           OR employeeInfo.Fotografia IS NOT NULL
      )
  THROW 51755, 'ATTESTATION BLOCKED: the fictional employee manifest does not match or contains personal fields.', 1;

IF (SELECT COUNT(*) FROM auth.AspNetUsers) <> 4
   OR EXISTS
      (
        SELECT 1
        FROM auth.AspNetUsers userInfo
        WHERE userInfo.Id NOT IN
          (N'training-user-instructor-v1', N'training-user-trainee01-v1', N'training-user-trainee02-v1', N'training-user-auditor-v1')
           OR userInfo.EmployeeId NOT IN (990001, 990002, 990003, 990004)
           OR userInfo.Email NOT LIKE N'%@training.orion.local'
           OR userInfo.PhoneNumber IS NOT NULL
           OR userInfo.ArrendadorProveedorId IS NOT NULL
           OR NULLIF(userInfo.PasswordHash, N'') IS NULL
      )
   OR (SELECT COUNT(*) FROM auth.AspNetRoles) <> 12
   OR EXISTS
      (
        SELECT 1 FROM auth.AspNetRoles
        WHERE Name NOT IN
          (N'CapacitacionAdmin', N'CapacitacionInstructor', N'CapacitacionAuditor', N'Lectura', N'SatOperator', N'Logistica',
           N'Administrador', N'Arrendadores', N'OrdenTrabajoSupervisor', N'APOperator', N'CapitalHumanoAdmin', N'RestauranteSupervisor')
      )
   OR EXISTS
      (
        SELECT 1 FROM auth.AspNetRoles
        WHERE [Scope] <> CASE WHEN Name IN (N'Administrador',N'Arrendadores') THEN 'Global' ELSE 'Company' END
      )
   OR (SELECT COUNT(*) FROM auth.AspNetUserClaims WHERE ClaimType = N'rfc' AND ClaimValue = N'XAXX010101000') <> 4
   OR (SELECT COUNT(*) FROM auth.AspNetUserRoles) <> 22
   OR (SELECT COUNT(*) FROM orion.Company WHERE Rfc=N'XAXX010101000' AND IsActive=1) <> 1
   OR (SELECT COUNT(*) FROM auth.AspNetUserCompanies WHERE Rfc=N'XAXX010101000' AND IsActive=1 AND EmployeeId IS NOT NULL) <> 4
   OR (SELECT COUNT(*) FROM auth.AspNetUserCompanyRoles WHERE Rfc=N'XAXX010101000') <> 19
   OR EXISTS
      (
        SELECT trainee.UserId, expectedRole.RoleName
        FROM
          (VALUES (N'training-user-trainee01-v1'), (N'training-user-trainee02-v1')) trainee(UserId)
        CROSS JOIN
          (
            VALUES (N'Lectura'), (N'SatOperator'), (N'Logistica'), (N'Arrendadores'),
                   (N'OrdenTrabajoSupervisor'), (N'APOperator'), (N'CapitalHumanoAdmin'), (N'RestauranteSupervisor')
          ) expectedRole(RoleName)
        WHERE NOT EXISTS
        (
          SELECT 1
          FROM auth.AspNetUserRoles membership
          JOIN auth.AspNetRoles roleInfo ON roleInfo.Id = membership.RoleId
          WHERE membership.UserId = trainee.UserId
            AND roleInfo.Name = expectedRole.RoleName
        )
      )
   OR EXISTS
      (
        SELECT 1
        FROM auth.AspNetUserRoles membership
        JOIN auth.AspNetRoles roleInfo ON roleInfo.Id = membership.RoleId
        -- Los participantes practican todos los módulos, pero nunca administran
        -- el entorno: Administrador y CapacitacionAdmin quedan fuera de su
        -- alcance, igual que la nómina.
        WHERE membership.UserId IN (N'training-user-trainee01-v1', N'training-user-trainee02-v1')
          AND roleInfo.Name IN (N'Administrador', N'CapacitacionAdmin', N'CapitalHumanoNomina', N'Conteo')
      )
   OR EXISTS
      (
        SELECT 1
        FROM auth.AspNetUserRoles membership
        JOIN auth.AspNetRoles roleInfo ON roleInfo.Id = membership.RoleId
        WHERE membership.UserId = N'training-user-instructor-v1'
          AND roleInfo.Name IN
            (N'CapitalHumanoNomina', N'CapitalHumanoSupervisor', N'Conteo')
      )
  THROW 51756, 'ATTESTATION BLOCKED: the fictional Identity manifest does not match.', 1;

IF EXISTS
(
  SELECT expectedMembership.UserId, expectedMembership.RoleName
  FROM
  (
    VALUES
      (N'training-user-instructor-v1', N'CapacitacionAdmin'),
      (N'training-user-instructor-v1', N'CapacitacionInstructor'),
      (N'training-user-instructor-v1', N'SatOperator'),
      (N'training-user-instructor-v1', N'Logistica'),
      (N'training-user-instructor-v1', N'Administrador'),
      (N'training-user-trainee01-v1', N'Lectura'),
      (N'training-user-trainee01-v1', N'SatOperator'),
      (N'training-user-trainee01-v1', N'Logistica'),
      (N'training-user-trainee01-v1', N'Arrendadores'),
      (N'training-user-trainee01-v1', N'OrdenTrabajoSupervisor'),
      (N'training-user-trainee01-v1', N'APOperator'),
      (N'training-user-trainee01-v1', N'CapitalHumanoAdmin'),
      (N'training-user-trainee01-v1', N'RestauranteSupervisor'),
      (N'training-user-trainee02-v1', N'Lectura'),
      (N'training-user-trainee02-v1', N'SatOperator'),
      (N'training-user-trainee02-v1', N'Logistica'),
      (N'training-user-trainee02-v1', N'Arrendadores'),
      (N'training-user-trainee02-v1', N'OrdenTrabajoSupervisor'),
      (N'training-user-trainee02-v1', N'APOperator'),
      (N'training-user-trainee02-v1', N'CapitalHumanoAdmin'),
      (N'training-user-trainee02-v1', N'RestauranteSupervisor'),
      (N'training-user-auditor-v1', N'CapacitacionAuditor')
  ) expectedMembership(UserId, RoleName)
  WHERE NOT EXISTS
  (
    SELECT 1
    FROM auth.AspNetUserRoles membership
    JOIN auth.AspNetRoles roleInfo ON roleInfo.Id = membership.RoleId
    WHERE membership.UserId = expectedMembership.UserId
      AND roleInfo.Name = expectedMembership.RoleName
  )
)
  THROW 51766, 'ATTESTATION BLOCKED: an expected least-privilege cohort role assignment is missing.', 1;

IF (SELECT COUNT(*) FROM dbo.Clientes) <> 1
   OR EXISTS
      (
        SELECT 1 FROM dbo.Clientes
        WHERE Nombre <> N'HUÉSPED FICTICIO · CAPACITACIÓN'
           OR Email <> 'guest@training.orion.local'
           OR Password IS NOT NULL
      )
   OR (SELECT COUNT(*) FROM dbo.ROOM) <> 2
   OR EXISTS (SELECT 1 FROM dbo.ROOM WHERE ROOM_NAME NOT LIKE 'TRN-%' OR NOTES <> 'DATOS SINTÉTICOS')
   OR (SELECT COUNT(*) FROM dbo.ROOM_CALENDAR) <> 120
   OR EXISTS (SELECT 1 FROM dbo.ROOM_CALENDAR WHERE ROOM NOT LIKE 'TRN-%' OR NOTES <> 'DATOS SINTÉTICOS')
   OR (SELECT COUNT(*) FROM dbo.RESERVATION) <> 1
   OR EXISTS (SELECT 1 FROM dbo.RESERVATION WHERE RFC <> 'XAXX010101000' OR NOTES NOT LIKE '%FICTICIA%')
   OR (SELECT COUNT(*) FROM dbo.RESERVATION_DETAIL) <> 1
   OR EXISTS (SELECT 1 FROM dbo.RESERVATION_DETAIL WHERE NOTES <> 'DATOS SINTÉTICOS')
  THROW 51757, 'ATTESTATION BLOCKED: the fictional reservations scenario does not match.', 1;

/*
  Provisioning creates two units, two categories, two locations, three
  housekeeping materials and six stock balances. The catalog seed then adds four
  units (GR/KG/ML/LT), one ALIMENTOS category and three made-to-order menu
  materials, because restaurante.Product requires a material per product and the
  housekeeping rows cannot double as menu items.

  Only the counts move. Every naming predicate below still holds, which is the
  point: the seed keeps the TRAINING and TRN- prefixes precisely so this guard
  stays as strict as it was. StockBalance stays at six because a made-to-order
  finished good gets no stock row.
*/
IF (SELECT COUNT(*) FROM logistica.UnitOfMeasure) <> 6
   OR EXISTS (SELECT 1 FROM logistica.UnitOfMeasure WHERE UnitName NOT LIKE 'TRAINING %')
   OR (SELECT COUNT(*) FROM logistica.MaterialCategory) <> 3
   OR EXISTS (SELECT 1 FROM logistica.MaterialCategory WHERE Rfc <> 'XAXX010101000' OR CategoryName NOT LIKE 'TRAINING %')
   OR (SELECT COUNT(*) FROM logistica.Location) <> 2
   OR EXISTS (SELECT 1 FROM logistica.Location WHERE Rfc <> 'XAXX010101000' OR LocationCode NOT LIKE 'TRN-%')
   OR (SELECT COUNT(*) FROM logistica.Material) <> 6
   OR EXISTS (SELECT 1 FROM logistica.Material WHERE Rfc <> 'XAXX010101000' OR MaterialCode NOT LIKE 'TRN-%')
   OR (SELECT COUNT(*) FROM logistica.StockBalance) <> 6
   OR EXISTS (SELECT 1 FROM logistica.StockBalance WHERE Rfc <> 'XAXX010101000' OR Notes <> 'DATOS SINTÉTICOS')
  THROW 51758, 'ATTESTATION BLOCKED: the fictional logistics scenario does not match.', 1;

/*
  The preserved reference catalog, verified at the end of the run.

  dbo.Formas_Pago is the one table whose rows may survive the erase, so this is
  the guard that makes that exception auditable: exactly the twenty-two SAT
  c_FormaPago claves, nothing more. It is the attestation-side mirror of the
  clamp in 20260817_orion_training_sanitize.sql and of the MERGE in
  20260821_orion_training_catalogos.sql; a unit test compares all three lists so
  they cannot drift apart and quietly widen what survives.

  The description length bound is the second half of the check. Even a canonical
  clave must not carry text longer than SAT wording, which is what a smuggled
  production value would look like.
*/
IF (SELECT COUNT(*) FROM dbo.Formas_Pago) <> 22
   OR EXISTS
      (
        SELECT 1
        FROM dbo.Formas_Pago
        WHERE Clave COLLATE Latin1_General_100_BIN2 NOT IN
          (N'01', N'02', N'03', N'04', N'05', N'06', N'08', N'12', N'13', N'14',
           N'15', N'17', N'23', N'24', N'25', N'26', N'27', N'28', N'29', N'30',
           N'31', N'99')
      )
   OR EXISTS (SELECT 1 FROM dbo.Formas_Pago WHERE LEN(ISNULL(Descripcion, N'')) > 100)
  THROW 51769, 'ATTESTATION BLOCKED: the preserved reference catalog is outside its canonical SAT manifest.', 1;

/*
  The synthetic catalogs. Every RFC-bearing table the catalog seed writes must
  belong wholly to the fictional tenant, and the restaurant configuration must
  carry its TRN- markers. A cloned row surviving inside one of these catalogs
  would be invisible to the emptiness sweep above, because these tables are now
  allowed to be non-empty; this is the guard that closes that gap.
*/
IF EXISTS (SELECT 1 FROM dbo.CuentasContables WHERE RFC <> 'XAXX010101000')
   OR EXISTS (SELECT 1 FROM dbo.Actividad WHERE RFC <> 'XAXX010101000')
   OR EXISTS (SELECT 1 FROM dbo.Compra WHERE RFC <> 'XAXX010101000')
   OR EXISTS (SELECT 1 FROM dbo.Servicios WHERE RFC <> 'XAXX010101000')
   OR EXISTS (SELECT 1 FROM dbo.CfdiPolizaCuentaDefault WHERE Rfc <> 'XAXX010101000')
   OR EXISTS (SELECT 1 FROM dbo.PlantillaContable WHERE RFC <> 'XAXX010101000')
   OR EXISTS (SELECT 1 FROM bancos.Cuentas_Banco WHERE RFC <> 'XAXX010101000')
   OR EXISTS (SELECT 1 FROM dbo.BusinessPartnerRfcScope WHERE Rfc <> 'XAXX010101000')
   OR EXISTS (SELECT 1 FROM rh.Holiday WHERE Rfc <> 'XAXX010101000')
   OR EXISTS (SELECT 1 FROM dbo.SatRfcProfile WHERE Rfc <> 'XAXX010101000')
   OR EXISTS
      (
        SELECT 1 FROM dbo.SatRfcProfile
        WHERE SATFielCertificate IS NOT NULL
           OR SATFielKey IS NOT NULL
           OR SATFielPfx IS NOT NULL
           OR SATFielPasswordEnc IS NOT NULL
      )
   OR EXISTS (SELECT 1 FROM restaurante.Site WHERE Rfc <> 'XAXX010101000' OR SiteCode NOT LIKE 'TRN-%')
   OR EXISTS (SELECT 1 FROM restaurante.DiningTable WHERE Rfc <> 'XAXX010101000')
   OR EXISTS (SELECT 1 FROM restaurante.KitchenStation WHERE Rfc <> 'XAXX010101000')
   OR EXISTS (SELECT 1 FROM restaurante.CashRegister WHERE Rfc <> 'XAXX010101000' OR DeviceKeyHash IS NOT NULL)
   OR EXISTS (SELECT 1 FROM restaurante.Menu WHERE Rfc <> 'XAXX010101000' OR MenuCode NOT LIKE 'TRN-%')
   OR EXISTS (SELECT 1 FROM restaurante.Product WHERE Rfc <> 'XAXX010101000' OR Sku NOT LIKE 'TRN-%')
  THROW 51770, 'ATTESTATION BLOCKED: the synthetic catalog manifest is not wholly fictional.', 1;

IF (SELECT COUNT(*) FROM rh.WorkSite) <> 1
   OR EXISTS
       (SELECT 1 FROM rh.WorkSite
        WHERE Rfc <> 'XAXX010101000' OR Code <> 'TRN-SITE-01'
           OR [Name] <> N'TRAINING · SITIO FICTICIO'
           OR TimeZoneId <> 'America/Mexico_City'
           OR Latitude <> 0 OR Longitude <> 0
           OR RadiusMeters <> 100 OR MaxAccuracyMeters <> 100
           OR IsActive <> 1 OR CreatedBy <> N'OrionERP Training scenario v1')
   OR (SELECT COUNT(*) FROM rh.ScheduleTemplate) <> 1
   OR EXISTS
       (SELECT 1 FROM rh.ScheduleTemplate
        WHERE Rfc <> 'XAXX010101000' OR Code <> 'TRN-DIARIO'
           OR [Name] <> N'TRAINING · HORARIO FICTICIO DIARIO'
           OR IsActive <> 1 OR CreatedBy <> N'OrionERP Training scenario v1')
   OR (SELECT COUNT(*) FROM rh.ScheduleDay) <> 7
   OR (SELECT COUNT(DISTINCT DayOfWeek) FROM rh.ScheduleDay) <> 7
   OR EXISTS
      (SELECT 1
       FROM rh.ScheduleDay dayInfo
       JOIN rh.ScheduleTemplate templateInfo ON templateInfo.Id = dayInfo.ScheduleTemplateId
        WHERE templateInfo.Rfc <> 'XAXX010101000'
           OR templateInfo.Code <> 'TRN-DIARIO'
           OR dayInfo.DayOfWeek NOT BETWEEN 0 AND 6
           OR dayInfo.IsWorkingDay <> 1
           OR dayInfo.StartTime IS NULL OR dayInfo.StartTime <> CONVERT(time(0), '09:00:00')
           OR dayInfo.EndTime IS NULL OR dayInfo.EndTime <> CONVERT(time(0), '17:00:00')
           OR dayInfo.UnpaidBreakMinutes <> 60)
   OR (SELECT COUNT(*) FROM rh.AttendancePolicy) <> 1
   OR EXISTS
       (SELECT 1 FROM rh.AttendancePolicy
        WHERE Rfc <> 'XAXX010101000' OR Code <> 'TRN-ASISTENCIA'
           OR [Name] <> N'TRAINING · ASISTENCIA FICTICIA'
           OR EffectiveFrom <> CONVERT(date, '2026-01-01') OR EffectiveTo IS NOT NULL
           OR WeeklyOrdinaryMinutes <> 2880 OR WeeklyDoubleOvertimeMinutes <> 540
           OR WeeklyTripleOvertimeMinutes <> 240 OR GraceMinutes <> 5 OR RoundingMinutes <> 1
           OR LocationRequired <> 0 OR IsActive <> 1 OR RequiresReview <> 1
           OR CreatedBy <> N'OrionERP Training scenario v1')
   OR (SELECT COUNT(*) FROM rh.PayGroup) <> 1
   OR EXISTS
       (SELECT 1 FROM rh.PayGroup
        WHERE Rfc <> 'XAXX010101000' OR Code <> 'TRN-SEMANAL'
           OR [Name] <> N'TRAINING · GRUPO FICTICIO' OR Frequency <> 'WEEKLY'
           OR IsActive <> 1 OR CreatedBy <> N'OrionERP Training scenario v1')
   OR (SELECT COUNT(*) FROM rh.EmployeeWorkAssignment) <> 2
   OR EXISTS
      (SELECT 1
       FROM rh.EmployeeWorkAssignment assignmentInfo
       JOIN rh.WorkSite siteInfo ON siteInfo.Id = assignmentInfo.SiteId
       JOIN rh.ScheduleTemplate templateInfo ON templateInfo.Id = assignmentInfo.ScheduleTemplateId
       JOIN rh.AttendancePolicy attendanceInfo ON attendanceInfo.Id = assignmentInfo.AttendancePolicyId
       JOIN rh.PayGroup payGroupInfo ON payGroupInfo.Id = assignmentInfo.PayGroupId
       WHERE assignmentInfo.Rfc <> 'XAXX010101000'
          OR assignmentInfo.EmployeeId NOT IN (990002, 990003)
          OR siteInfo.Code <> 'TRN-SITE-01'
           OR templateInfo.Code <> 'TRN-DIARIO'
           OR attendanceInfo.Code <> 'TRN-ASISTENCIA'
           OR payGroupInfo.Code <> 'TRN-SEMANAL'
           OR assignmentInfo.EffectiveFrom <> CONVERT(date, '2026-01-05')
           OR assignmentInfo.EffectiveTo IS NOT NULL
           OR assignmentInfo.CreatedBy <> N'OrionERP Training scenario v1')
   OR EXISTS
      (
        SELECT 1
        FROM (VALUES (990002), (990003)) expected(EmployeeId)
        WHERE (SELECT COUNT(*) FROM rh.EmployeeWorkAssignment actual
               WHERE actual.Rfc = 'XAXX010101000'
                 AND actual.EmployeeId = expected.EmployeeId) <> 1
      )
  THROW 51836, 'ATTESTATION BLOCKED: the fictional RH attendance manifest does not match.', 1;

IF (SELECT COUNT(*) FROM rh.LeaveType) <> 2
   OR EXISTS
       (SELECT 1 FROM rh.LeaveType
        WHERE Rfc <> 'XAXX010101000' OR Code NOT IN ('TRN-PERSONAL', 'TRN-SIN-GOCE')
           OR [Name] <> CASE Code
                WHEN 'TRN-PERSONAL' THEN N'TRAINING · PERMISO PERSONAL FICTICIO'
                WHEN 'TRN-SIN-GOCE' THEN N'TRAINING · PERMISO SIN GOCE FICTICIO'
              END
           OR IsPaid <> CASE Code WHEN 'TRN-PERSONAL' THEN 1 ELSE 0 END
           OR RequiresBalance <> CASE Code WHEN 'TRN-PERSONAL' THEN 1 ELSE 0 END
           OR IsActive <> 1)
   OR (SELECT COUNT(*) FROM rh.LeavePolicy) <> 2
   OR EXISTS
      (SELECT 1
       FROM rh.LeavePolicy policyInfo
       JOIN rh.LeaveType typeInfo ON typeInfo.Id = policyInfo.LeaveTypeId
        WHERE policyInfo.Rfc <> 'XAXX010101000'
           OR policyInfo.Code NOT IN ('TRN-PERSONAL-POL', 'TRN-SIN-GOCE-POL')
           OR typeInfo.Code <> CASE policyInfo.Code
                WHEN 'TRN-PERSONAL-POL' THEN 'TRN-PERSONAL'
                WHEN 'TRN-SIN-GOCE-POL' THEN 'TRN-SIN-GOCE'
              END
           OR policyInfo.[Name] <> CASE policyInfo.Code
                WHEN 'TRN-PERSONAL-POL' THEN N'TRAINING · POLÍTICA PERSONAL FICTICIA'
                WHEN 'TRN-SIN-GOCE-POL' THEN N'TRAINING · POLÍTICA SIN GOCE FICTICIA'
              END
           OR policyInfo.EffectiveFrom <> CONVERT(date, '2026-01-01')
           OR policyInfo.EffectiveTo IS NOT NULL OR policyInfo.AccrualMethod <> 'NONE'
           OR policyInfo.AnnualDays IS NOT NULL OR policyInfo.AllowPartialDay <> 1
           OR policyInfo.RequiresReview <> 1 OR policyInfo.IsActive <> 1
           OR policyInfo.CreatedBy <> N'OrionERP Training scenario v1')
   OR (SELECT COUNT(*) FROM rh.LeaveEnrollment) <> 4
   OR EXISTS
      (SELECT 1
       FROM rh.LeaveEnrollment enrollmentInfo
       JOIN rh.LeavePolicy policyInfo ON policyInfo.Id = enrollmentInfo.LeavePolicyId
        WHERE enrollmentInfo.Rfc <> 'XAXX010101000'
           OR enrollmentInfo.EmployeeId NOT IN (990002, 990003)
           OR policyInfo.Code NOT IN ('TRN-PERSONAL-POL', 'TRN-SIN-GOCE-POL')
           OR enrollmentInfo.EffectiveFrom <> CONVERT(date, '2026-01-05')
           OR enrollmentInfo.EffectiveTo IS NOT NULL
           OR enrollmentInfo.CreatedBy <> N'OrionERP Training scenario v1')
   OR EXISTS
      (
        SELECT 1
        FROM (VALUES (990002), (990003)) expectedEmployee(EmployeeId)
        CROSS JOIN (VALUES ('TRN-PERSONAL-POL'), ('TRN-SIN-GOCE-POL')) expectedPolicy(Code)
        WHERE
        (
          SELECT COUNT(*)
          FROM rh.LeaveEnrollment actual
          JOIN rh.LeavePolicy actualPolicy ON actualPolicy.Id = actual.LeavePolicyId
          WHERE actual.Rfc = 'XAXX010101000'
            AND actual.EmployeeId = expectedEmployee.EmployeeId
            AND actualPolicy.Code = expectedPolicy.Code
        ) <> 1
      )
   OR (SELECT COUNT(*) FROM rh.LeaveBalanceLedger) <> 2
   OR EXISTS
      (SELECT 1
       FROM rh.LeaveBalanceLedger ledgerInfo
       JOIN rh.LeaveType typeInfo ON typeInfo.Id = ledgerInfo.LeaveTypeId
       WHERE ledgerInfo.Rfc <> 'XAXX010101000'
          OR ledgerInfo.EmployeeId NOT IN (990002, 990003)
           OR ledgerInfo.Days <> 5
           OR typeInfo.Code <> 'TRN-PERSONAL'
           OR ledgerInfo.TransactionDate <> CONVERT(date, '2026-01-05')
           OR ledgerInfo.TransactionType <> 'TRAINING_INITIAL'
           OR ledgerInfo.Reason <> N'Saldo ficticio exclusivamente para práctica'
           OR ledgerInfo.CreatedBy <> N'OrionERP Training scenario v1'
           OR ledgerInfo.SourceKey <> CASE ledgerInfo.EmployeeId
               WHEN 990002 THEN 'training:initial:trainee01'
               WHEN 990003 THEN 'training:initial:trainee02'
             END)
   OR (SELECT COUNT(*) FROM rh.PrivacyNotice) <> 1
   OR EXISTS
       (SELECT 1 FROM rh.PrivacyNotice
        WHERE Rfc <> 'XAXX010101000' OR Version <> 'TRN-v1'
           OR Title <> N'Aviso ficticio del entorno de capacitación'
           OR NoticeText <> N'Este aviso pertenece únicamente a Orion_Training. No captures ubicación, documentos ni datos personales reales. La evidencia de este ejercicio debe ser ficticia y el entorno puede reiniciarse sin conservar tus prácticas.'
           OR EffectiveFrom <> CONVERT(date, '2026-01-01') OR IsActive <> 1
           OR CreatedBy <> N'OrionERP Training scenario v1')
  THROW 51837, 'ATTESTATION BLOCKED: the fictional RH leave/privacy manifest does not match.', 1;

-- Reviewed curriculum manifest: the five pilot courses authored by the schema
-- installer plus one course per OrionERP module authored by the curriculum
-- script. A table variable cannot carry a named constraint; only the inline,
-- unnamed form parses.
DECLARE @CursoManifiesto TABLE
(
  Clave nvarchar(64) NOT NULL,
  Autor nvarchar(256) NOT NULL,
  PRIMARY KEY (Clave)
);

INSERT @CursoManifiesto (Clave, Autor)
VALUES
  (N'ORION-FUNDAMENTOS', N'OrionERP.Capacitacion.Seed.v1'),
  (N'RES-END-TO-END', N'OrionERP.Capacitacion.Seed.v1'),
  (N'CFDI-CONTABILIDAD', N'OrionERP.Capacitacion.Seed.v1'),
  (N'LOGISTICA-OPERACION', N'OrionERP.Capacitacion.Seed.v1'),
  (N'RH-CAPITAL-HUMANO', N'OrionERP.Capacitacion.Seed.v1'),
  (N'CAPACITACION-MODULO', N'OrionERP.Capacitacion.Curriculum.v2'),
  (N'RESERVAS-CALENDARIO', N'OrionERP.Capacitacion.Curriculum.v2'),
  (N'ARRENDADORES-ESTADO', N'OrionERP.Capacitacion.Curriculum.v2'),
  (N'OT-OPERACION', N'OrionERP.Capacitacion.Curriculum.v2'),
  (N'CFDI-SAT-OPERACION', N'OrionERP.Capacitacion.Curriculum.v2'),
  (N'CFDI-DECLARACION-PREVIA', N'OrionERP.Capacitacion.Curriculum.v2'),
  (N'CONTA-POLIZAS', N'OrionERP.Capacitacion.Curriculum.v2'),
  (N'BANCOS-CONCILIACION', N'OrionERP.Capacitacion.Curriculum.v2'),
  (N'CXP-RECURRENTES', N'OrionERP.Capacitacion.Curriculum.v2'),
  (N'REPORTES-FINANCIEROS', N'OrionERP.Capacitacion.Curriculum.v2'),
  (N'LOGISTICA-COMPRAS', N'OrionERP.Capacitacion.Curriculum.v2'),
  (N'LOGISTICA-INVENTARIO', N'OrionERP.Capacitacion.Curriculum.v2'),
  (N'REST-POS-SERVICIO', N'OrionERP.Capacitacion.Curriculum.v2'),
  (N'REST-COCINA-PRODUCCION', N'OrionERP.Capacitacion.Curriculum.v2'),
  (N'REST-INVENTARIO-TURNOS', N'OrionERP.Capacitacion.Curriculum.v2'),
  (N'REST-CATALOGO-CONFIG', N'OrionERP.Capacitacion.Curriculum.v2'),
  (N'REST-COMERCIAL', N'OrionERP.Capacitacion.Curriculum.v2'),
  (N'RH-ASISTENCIA', N'OrionERP.Capacitacion.Curriculum.v2'),
  (N'RH-CONFIG-TIEMPO', N'OrionERP.Capacitacion.Curriculum.v2'),
  (N'RH-AUSENCIAS', N'OrionERP.Capacitacion.Curriculum.v2'),
  (N'RH-PRENOMINA', N'OrionERP.Capacitacion.Curriculum.v2'),
  (N'RH-EXPEDIENTES', N'OrionERP.Capacitacion.Curriculum.v2'),
  (N'AJUSTES-PLANTILLAS', N'OrionERP.Capacitacion.Curriculum.v2'),
  (N'ADMIN-SEGURIDAD', N'OrionERP.Capacitacion.Curriculum.v2');

DECLARE @CursosEsperados int = (SELECT COUNT(*) FROM @CursoManifiesto);

IF (SELECT COUNT(*) FROM capacitacion.Curso) <> @CursosEsperados
   OR EXISTS
      (
        SELECT 1
        FROM capacitacion.Curso courseInfo
        LEFT JOIN @CursoManifiesto manifest ON manifest.Clave = courseInfo.Clave
        WHERE courseInfo.Rfc <> N'*'
           OR manifest.Clave IS NULL
           OR courseInfo.CreadoPor <> manifest.Autor
      )
   OR (SELECT COUNT(*) FROM capacitacion.CursoVersion) <> @CursosEsperados
   OR EXISTS
      (
        SELECT 1
        FROM capacitacion.CursoVersion versionInfo
        JOIN capacitacion.Curso courseInfo ON courseInfo.CursoId = versionInfo.CursoId
        LEFT JOIN @CursoManifiesto manifest ON manifest.Clave = courseInfo.Clave
        WHERE versionInfo.NumeroVersion <> 1
           OR versionInfo.Estado <> N'PUBLICADA'
           OR manifest.Clave IS NULL
           OR versionInfo.CreadaPor <> manifest.Autor
           OR versionInfo.PublicadaPor <> manifest.Autor
      )
   OR (SELECT COUNT(*) FROM capacitacion.RutaAprendizaje) <> 1
   OR NOT EXISTS
      (
        SELECT 1 FROM capacitacion.RutaAprendizaje
        WHERE Rfc = N'*'
          AND Clave = N'ORION-EXPERTO'
          AND Activa = 1
          AND CreadaPor = N'OrionERP.Capacitacion.Curriculum.v2'
      )
   OR (SELECT COUNT(*) FROM capacitacion.RutaCurso) <> @CursosEsperados
   -- trainee01 carries the complete program and trainee02 the five pilot courses.
   OR (SELECT COUNT(*) FROM capacitacion.Asignacion) <> (@CursosEsperados + 5)
   OR (SELECT COUNT(*) FROM capacitacion.Asignacion WHERE EmployeeId = 990002) <> @CursosEsperados
   OR (SELECT COUNT(*) FROM capacitacion.Asignacion WHERE EmployeeId = 990003) <> 5
   OR EXISTS
      (
        SELECT 1 FROM capacitacion.Asignacion
        WHERE Rfc <> N'XAXX010101000'
           OR EmployeeId NOT IN (990002, 990003)
           OR InstructorEmployeeId <> 990001
           OR AsignadaPorEmployeeId <> 990001
           OR AsignadaPor <> N'instructor@training.orion.local'
      )
  THROW 51759, 'ATTESTATION BLOCKED: the fictional training catalog or assignment manifest does not match.', 1;

IF EXISTS
(
  SELECT 1
  FROM capacitacion.Leccion lesson
  JOIN capacitacion.CursoVersion versionInfo ON versionInfo.CursoVersionId = lesson.CursoVersionId
  JOIN capacitacion.Curso courseInfo ON courseInfo.CursoId = versionInfo.CursoId
  WHERE courseInfo.Rfc <> N'*'
     OR NOT EXISTS (SELECT 1 FROM @CursoManifiesto manifest WHERE manifest.Clave = courseInfo.Clave)
)
   OR EXISTS
   (
     SELECT 1
     FROM capacitacion.BloqueContenido blockInfo
     JOIN capacitacion.Leccion lesson ON lesson.LeccionId = blockInfo.LeccionId
     JOIN capacitacion.CursoVersion versionInfo ON versionInfo.CursoVersionId = lesson.CursoVersionId
     JOIN capacitacion.Curso courseInfo ON courseInfo.CursoId = versionInfo.CursoId
     WHERE courseInfo.Rfc <> N'*'
        OR NOT EXISTS (SELECT 1 FROM @CursoManifiesto manifest WHERE manifest.Clave = courseInfo.Clave)
   )
   OR EXISTS
   (
     SELECT 1
     FROM capacitacion.Evaluacion assessment
     JOIN capacitacion.CursoVersion versionInfo ON versionInfo.CursoVersionId = assessment.CursoVersionId
     JOIN capacitacion.Curso courseInfo ON courseInfo.CursoId = versionInfo.CursoId
     WHERE courseInfo.Rfc <> N'*'
        OR NOT EXISTS (SELECT 1 FROM @CursoManifiesto manifest WHERE manifest.Clave = courseInfo.Clave)
   )
   OR EXISTS
   (
     SELECT 1
     FROM capacitacion.Practica practiceInfo
     JOIN capacitacion.CursoVersion versionInfo ON versionInfo.CursoVersionId = practiceInfo.CursoVersionId
     JOIN capacitacion.Curso courseInfo ON courseInfo.CursoId = versionInfo.CursoId
     WHERE courseInfo.Rfc <> N'*'
        OR NOT EXISTS (SELECT 1 FROM @CursoManifiesto manifest WHERE manifest.Clave = courseInfo.Clave)
   )
   OR EXISTS
   (
     SELECT 1
     FROM capacitacion.RutaCurso pathCourse
     JOIN capacitacion.CursoVersion versionInfo ON versionInfo.CursoVersionId = pathCourse.CursoVersionId
     JOIN capacitacion.Curso courseInfo ON courseInfo.CursoId = versionInfo.CursoId
     WHERE courseInfo.Rfc <> N'*'
        OR NOT EXISTS (SELECT 1 FROM @CursoManifiesto manifest WHERE manifest.Clave = courseInfo.Clave)
   )
  THROW 51763, 'ATTESTATION BLOCKED: authored training content is not wholly owned by the reviewed seed courses.', 1;

IF EXISTS
(
  SELECT 1
  FROM capacitacion.Recurso
  WHERE Ruta LIKE N'http:%'
     OR Ruta LIKE N'https:%'
     OR Ruta LIKE N'//%'
)
  THROW 51760, 'ATTESTATION BLOCKED: seeded visual aids must be local training assets.', 1;

-- Identity/sequence state is part of the clone's data surface. Verify the
-- deterministic reset rather than accepting production-derived high-water
-- marks on otherwise empty synthetic tables.
DECLARE @IdentityQualifiedName nvarchar(517);
DECLARE @IdentitySeed decimal(38, 0);
DECLARE @IdentityIncrement decimal(38, 0);
DECLARE @IdentityLastValue decimal(38, 0);
DECLARE @IdentityRowCount bigint;
DECLARE @IdentityExpected decimal(38, 0);
DECLARE IdentityAttestationCursor CURSOR LOCAL FAST_FORWARD FOR
  SELECT
    QUOTENAME(schemaInfo.name) + N'.' + QUOTENAME(tableInfo.name),
    CONVERT(decimal(38, 0), identityInfo.seed_value),
    CONVERT(decimal(38, 0), identityInfo.increment_value),
    CONVERT(decimal(38, 0), identityInfo.last_value)
  FROM sys.identity_columns identityInfo
  JOIN sys.tables tableInfo ON tableInfo.object_id = identityInfo.object_id
  JOIN sys.schemas schemaInfo ON schemaInfo.schema_id = tableInfo.schema_id
  WHERE tableInfo.is_ms_shipped = 0
    AND tableInfo.object_id NOT IN
      (OBJECT_ID(N'dbo.__EFMigrationsHistory'), OBJECT_ID(N'dbo.DateDimension'))
  ORDER BY schemaInfo.name, tableInfo.name;

OPEN IdentityAttestationCursor;
FETCH NEXT FROM IdentityAttestationCursor
  INTO @IdentityQualifiedName, @IdentitySeed, @IdentityIncrement, @IdentityLastValue;
WHILE @@FETCH_STATUS = 0
BEGIN
  SET @IdentityRowCount = 0;
  SET @Statement = N'SELECT @Rows = COUNT_BIG(*) FROM ' + @IdentityQualifiedName + N';';
  EXEC sys.sp_executesql @Statement, N'@Rows bigint OUTPUT', @Rows = @IdentityRowCount OUTPUT;

  IF @IdentityRowCount = 0
  BEGIN
    IF @IdentityLastValue IS NOT NULL
       AND @IdentityLastValue <> @IdentitySeed - @IdentityIncrement
      THROW 51838, 'ATTESTATION BLOCKED: an empty identity table retained a non-deterministic counter.', 1;
  END
  ELSE
  BEGIN
    SET @IdentityExpected = @IdentitySeed
      + (CONVERT(decimal(38, 0), @IdentityRowCount) - 1) * @IdentityIncrement;
    IF @IdentityLastValue IS NULL OR @IdentityLastValue <> @IdentityExpected
      THROW 51839, 'ATTESTATION BLOCKED: a synthetic identity table has an unexpected counter.', 1;
  END;

  FETCH NEXT FROM IdentityAttestationCursor
    INTO @IdentityQualifiedName, @IdentitySeed, @IdentityIncrement, @IdentityLastValue;
END;
CLOSE IdentityAttestationCursor;
DEALLOCATE IdentityAttestationCursor;

IF EXISTS
(
  SELECT 1
  FROM sys.sequences
  WHERE is_exhausted = 1
     OR current_value IS NULL
     OR CONVERT(decimal(38, 0), current_value)
        <> CONVERT(decimal(38, 0), start_value)
)
  THROW 51843, 'ATTESTATION BLOCKED: a sequence retained a production-derived current value.', 1;

IF EXISTS
(
  SELECT 1
  FROM sys.stats statsInfo
  JOIN sys.tables tableInfo ON tableInfo.object_id = statsInfo.object_id
  OUTER APPLY sys.dm_db_stats_properties(statsInfo.object_id, statsInfo.stats_id) propertiesInfo
  WHERE tableInfo.is_ms_shipped = 0
    AND
    (
      propertiesInfo.last_updated < @SanitizerStartedAt
      OR ISNULL(propertiesInfo.modification_counter, 0) <> 0
      OR ISNULL(propertiesInfo.rows_sampled, 0) <> ISNULL(propertiesInfo.rows, 0)
    )
)
  THROW 51846, 'ATTESTATION BLOCKED: a user-table statistic was not rebuilt by the guarded FULLSCAN.', 1;

UPDATE capacitacion.EntornoSeguridad
SET DatosSanitizados = 1,
    DatosSinteticos = 1,
    RevisadoEn = SYSUTCDATETIME(),
    RevisadoPor = LEFT(LTRIM(RTRIM(CONVERT(nvarchar(256), @ReviewedBy))), 256),
    VersionEsquema = 1
WHERE EntornoSeguridadId = 1
  AND Entorno = N'Training'
  AND DatosSanitizados = 0
  AND DatosSinteticos = 0;

IF @@ROWCOUNT <> 1
BEGIN
  ROLLBACK TRANSACTION;
  THROW 51761, 'ATTESTATION BLOCKED: the safety row changed during verification.', 1;
END;
COMMIT TRANSACTION;
