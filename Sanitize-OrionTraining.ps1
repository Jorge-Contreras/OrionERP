#requires -Version 7.0

[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
  [Parameter()]
  [string]$ConnectionString,

  [Parameter()]
  [switch]$Apply,

  [Parameter()]
  [ValidateSet('Orion_Training')]
  [string]$ConfirmDatabase,

  [Parameter()]
  [Security.SecureString]$InstructorPassword,

  [Parameter()]
  [Security.SecureString]$Trainee01Password,

  [Parameter()]
  [Security.SecureString]$Trainee02Password,

  [Parameter()]
  [Security.SecureString]$AuditorPassword,

  [Parameter()]
  [ValidateLength(1, 256)]
  [string]$ReviewedBy = 'Sanitize-OrionTraining.ps1/v1'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$requiredDatabase = 'Orion_Training'
$sessionGuard = '20260817-v1'
$connectionEnvironmentVariable = 'ORION_TRAINING_SANITIZER_CONNECTION_STRING'
$capacitacionInstaller = Join-Path $PSScriptRoot 'src\OrionERP.Infrastructure\Features\Capacitacion\Sql\20260817_capacitacion_v1.sql'
$curriculumInstaller = Join-Path $PSScriptRoot 'src\OrionERP.Infrastructure\Features\Capacitacion\Sql\20260819_capacitacion_curriculum_v2.sql'
$sanitizeScript = Join-Path $PSScriptRoot 'src\OrionERP.Infrastructure\Features\Capacitacion\Sql\20260817_orion_training_sanitize.sql'
$provisionScript = Join-Path $PSScriptRoot 'src\OrionERP.Infrastructure\Features\Capacitacion\Sql\20260817_orion_training_provision.sql'
$scenarioScript = Join-Path $PSScriptRoot 'src\OrionERP.Infrastructure\Features\Capacitacion\Sql\20260817_orion_training_scenarios.sql'
$reviewedTriggerScript = Join-Path $PSScriptRoot 'src\OrionERP.Infrastructure\Features\Capacitacion\Sql\20260817_orion_training_reviewed_triggers.sql'
$attestScript = Join-Path $PSScriptRoot 'src\OrionERP.Infrastructure\Features\Capacitacion\Sql\20260817_orion_training_attest.sql'
$neutralizeCloneScript = Join-Path $PSScriptRoot 'src\OrionERP.Infrastructure\Features\Capacitacion\Sql\20260818_orion_training_neutralize_clone.sql'
$trainingCfdiParserScript = Join-Path $PSScriptRoot 'src\OrionERP.Infrastructure\Features\Capacitacion\Sql\20260818_orion_training_cfdi_fixture_parser.sql'

function Get-RequiredConnectionString {
  param([string]$ExplicitConnectionString)

  $candidate = $ExplicitConnectionString
  if ([string]::IsNullOrWhiteSpace($candidate)) {
    $candidate = [Environment]::GetEnvironmentVariable($connectionEnvironmentVariable, 'Process')
  }

  if ([string]::IsNullOrWhiteSpace($candidate)) {
    throw "Provide -ConnectionString or set the process-only $connectionEnvironmentVariable environment variable. The workflow never imports or rewrites another environment's connection string."
  }

  $builder = [System.Data.SqlClient.SqlConnectionStringBuilder]::new($candidate)
  if (-not [string]::Equals(
      [string]$builder['Initial Catalog'],
      $requiredDatabase,
      [StringComparison]::Ordinal)) {
    throw "Blocked: Initial Catalog must be exactly $requiredDatabase."
  }
  if ([string]::IsNullOrWhiteSpace([string]$builder['Data Source'])) {
    throw 'Blocked: the sanitizer connection must declare Server or Data Source.'
  }
  if (-not [string]::IsNullOrWhiteSpace([string]$builder['AttachDBFilename'])) {
    throw 'Blocked: AttachDBFilename is not supported by the training sanitizer.'
  }
  if (-not [Convert]::ToBoolean($builder['Encrypt'])) {
    throw 'Blocked: the sanitizer administrative connection must use Encrypt=True.'
  }
  $builder['Persist Security Info'] = $false

  return $builder.ConnectionString
}

function Assert-TrainingPreProvisionSafety {
  param([System.Data.SqlClient.SqlConnection]$Connection)

  $command = $Connection.CreateCommand()
  try {
    $command.CommandText = @'
IF DB_NAME() COLLATE Latin1_General_100_BIN2 <> N'Orion_Training' COLLATE Latin1_General_100_BIN2
  THROW 51790, 'PREFLIGHT BLOCKED: the connected catalog is not exactly Orion_Training.', 1;
IF ISNULL(IS_SRVROLEMEMBER(N'sysadmin'), 0) <> 1
  THROW 51802, 'PREFLIGHT BLOCKED: the guarded reset requires a sysadmin maintenance connection.', 1;
IF EXISTS
(
  SELECT 1
  FROM sys.dm_exec_sessions
  WHERE is_user_process = 1
    AND database_id = DB_ID()
    AND session_id <> @@SPID
)
  THROW 51791, 'PREFLIGHT BLOCKED: close every other Orion_Training session before schema or seed writes.', 1;
IF EXISTS
(
  SELECT 1
  FROM sys.databases
  WHERE database_id = DB_ID()
    AND (containment <> 0
         OR is_trustworthy_on = 1
         OR is_db_chaining_on = 1
         OR is_cdc_enabled = 1
         OR is_broker_enabled = 1
         OR is_published = 1
         OR is_subscribed = 1
         OR is_merge_published = 1
         OR is_distributor = 1)
)
  THROW 51792, 'PREFLIGHT BLOCKED: containment, trust/chaining, CDC, Broker, or replication is enabled.', 1;
IF EXISTS (SELECT 1 FROM sys.change_tracking_databases WHERE database_id = DB_ID())
  THROW 51793, 'PREFLIGHT BLOCKED: SQL Change Tracking requires a reviewed purge strategy.', 1;
IF EXISTS
(
  SELECT 1
  FROM sys.tables
  WHERE is_ms_shipped = 0
    AND (is_replicated = 1 OR is_merge_published = 1 OR is_sync_tran_subscribed = 1)
)
   OR EXISTS (SELECT 1 FROM sys.columns WHERE is_filestream = 1)
   OR EXISTS (SELECT 1 FROM sys.transmission_queue)
   OR EXISTS (SELECT 1 FROM sys.conversation_endpoints)
   OR EXISTS (SELECT 1 FROM sys.service_queues WHERE is_ms_shipped = 0)
  THROW 51799, 'PREFLIGHT BLOCKED: replication, FILESTREAM, or retained Service Broker state exists.', 1;
IF EXISTS (SELECT 1 FROM sys.triggers WHERE parent_class = 0 AND is_ms_shipped = 0)
   OR EXISTS (SELECT 1 FROM sys.security_policies)
   OR EXISTS (SELECT 1 FROM sys.security_predicates)
  THROW 51794, 'PREFLIGHT BLOCKED: a database DDL trigger or row-level security policy exists.', 1;
DECLARE @AllowedSynonyms table
(
  SynonymSchema sysname NOT NULL,
  SynonymName sysname NOT NULL,
  TargetSchema sysname NOT NULL,
  TargetName sysname NOT NULL,
  PRIMARY KEY (SynonymSchema, SynonymName)
);
INSERT @AllowedSynonyms (SynonymSchema, SynonymName, TargetSchema, TargetName)
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

IF (SELECT COUNT(*) FROM sys.synonyms) <> (SELECT COUNT(*) FROM @AllowedSynonyms)
   OR EXISTS
   (
     SELECT 1
     FROM sys.synonyms synonymInfo
     JOIN sys.schemas synonymSchema ON synonymSchema.schema_id = synonymInfo.schema_id
     LEFT JOIN @AllowedSynonyms allowed
       ON allowed.SynonymSchema COLLATE Latin1_General_100_BIN2 = synonymSchema.name COLLATE Latin1_General_100_BIN2
      AND allowed.SynonymName COLLATE Latin1_General_100_BIN2 = synonymInfo.name COLLATE Latin1_General_100_BIN2
     WHERE allowed.SynonymName IS NULL
        OR PARSENAME(synonymInfo.base_object_name, 4) IS NOT NULL
        OR PARSENAME(synonymInfo.base_object_name, 3) IS NOT NULL
        OR ISNULL(PARSENAME(synonymInfo.base_object_name, 2), N'') COLLATE Latin1_General_100_BIN2
             <> allowed.TargetSchema COLLATE Latin1_General_100_BIN2
        OR ISNULL(PARSENAME(synonymInfo.base_object_name, 1), N'') COLLATE Latin1_General_100_BIN2
             <> allowed.TargetName COLLATE Latin1_General_100_BIN2
        OR OBJECT_ID(QUOTENAME(allowed.TargetSchema) + N'.' + QUOTENAME(allowed.TargetName), N'U') IS NULL
   )
   OR EXISTS
   (
     SELECT 1
     FROM @AllowedSynonyms allowed
     WHERE NOT EXISTS
     (
       SELECT 1
       FROM sys.synonyms synonymInfo
       JOIN sys.schemas synonymSchema ON synonymSchema.schema_id = synonymInfo.schema_id
       WHERE synonymSchema.name COLLATE Latin1_General_100_BIN2 = allowed.SynonymSchema COLLATE Latin1_General_100_BIN2
         AND synonymInfo.name COLLATE Latin1_General_100_BIN2 = allowed.SynonymName COLLATE Latin1_General_100_BIN2
     )
   )
  THROW 51810, 'PREFLIGHT BLOCKED: the exact reviewed local-synonym manifest is not present.', 1;

IF EXISTS (SELECT 1 FROM sys.external_tables)
   OR EXISTS (SELECT 1 FROM sys.external_data_sources)
   OR EXISTS (SELECT 1 FROM sys.database_scoped_credentials)
   OR EXISTS (SELECT 1 FROM sys.assemblies WHERE is_user_defined = 1)
   OR EXISTS (SELECT 1 FROM sys.assembly_modules)
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
  THROW 51795, 'PREFLIGHT BLOCKED: external, CLR, key, full-text, indexed-view, or credential objects exist.', 1;

IF EXISTS
(
  SELECT 1
  FROM sys.database_principals principalInfo
  WHERE principalInfo.principal_id > 4
    AND principalInfo.is_fixed_role = 0
    AND
    (
      principalInfo.type IN (N'A', N'C', N'K', N'U', N'G', N'E', N'X')
      OR (principalInfo.type = N'S' AND principalInfo.authentication_type <> 1)
    )
)
   OR EXISTS (SELECT 1 FROM sys.crypt_properties)
   OR EXISTS
      (SELECT 1 FROM sys.sql_modules
       WHERE execute_as_principal_id IS NOT NULL)
  THROW 51800, 'PREFLIGHT BLOCKED: contained/external/certificate principals, module signatures, or EXECUTE AS modules exist.', 1;

-- Surviving public/guest/fixed-role grants become runtime permissions through
-- implicit public membership or the required reader/writer roles. The guarded
-- sanitizer normalizes database-level grants; more granular grants require an
-- explicit operator review because their securable ownership may be material.
IF EXISTS
(
  SELECT 1
  FROM sys.database_permissions permissionInfo
  JOIN sys.database_principals grantee
    ON grantee.principal_id = permissionInfo.grantee_principal_id
  WHERE (grantee.name IN (N'public', N'guest') OR grantee.is_fixed_role = 1)
    AND permissionInfo.class NOT IN (0, 1)
)
  THROW 51803, 'PREFLIGHT BLOCKED: public, guest, or a fixed role has an explicit permission class the sanitizer cannot normalize.', 1;

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
  THROW 51801, 'PREFLIGHT BLOCKED: an encrypted module or cross-database dependency exists.', 1;

DECLARE @AllowedTriggers table
(
  TriggerSchema sysname NOT NULL,
  TriggerName sysname NOT NULL,
  ParentSchema sysname NOT NULL,
  ParentTable sysname NOT NULL,
  PRIMARY KEY (TriggerSchema, TriggerName)
);
INSERT @AllowedTriggers (TriggerSchema, TriggerName, ParentSchema, ParentTable)
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
  (N'capacitacion', N'TR_PracticaPaso_VersionPublicadaInmutable', N'capacitacion', N'PracticaPaso');

IF EXISTS
(
  SELECT 1
  FROM sys.triggers triggerInfo
  JOIN sys.objects triggerObject ON triggerObject.object_id = triggerInfo.object_id
  JOIN sys.objects parentInfo ON parentInfo.object_id = triggerInfo.parent_id
  JOIN sys.schemas triggerSchema ON triggerSchema.schema_id = triggerObject.schema_id
  JOIN sys.schemas parentSchema ON parentSchema.schema_id = parentInfo.schema_id
  LEFT JOIN @AllowedTriggers allowed
    ON allowed.TriggerSchema = triggerSchema.name
   AND allowed.TriggerName = triggerInfo.name
   AND allowed.ParentSchema = parentSchema.name
   AND allowed.ParentTable = parentInfo.name
  WHERE triggerInfo.parent_class = 1
    AND triggerInfo.is_ms_shipped = 0
    AND allowed.TriggerName IS NULL
)
  THROW 51796, 'PREFLIGHT BLOCKED: an unreviewed DML trigger could execute during synthetic provisioning.', 1;

IF EXISTS
(
  SELECT 1
  FROM sys.sql_modules moduleInfo
  WHERE moduleInfo.definition LIKE N'%grupocarpio%'
     OR moduleInfo.definition LIKE N'%Orion_Sandbox%'
     OR moduleInfo.definition LIKE N'%sp_send_dbmail%'
     OR moduleInfo.definition LIKE N'%xp_cmdshell%'
     OR moduleInfo.definition LIKE N'%sp_OA%'
     OR moduleInfo.definition LIKE N'%sp_execute_external_script%'
     OR moduleInfo.definition LIKE N'%OPENROWSET%'
     OR moduleInfo.definition LIKE N'%OPENDATASOURCE%'
     OR moduleInfo.definition LIKE N'%OPENQUERY%'
     OR moduleInfo.definition LIKE N'%BULK INSERT%'
     OR moduleInfo.definition LIKE N'%EXECUTE% AT %'
     OR moduleInfo.definition LIKE N'%EXEC% AT %'
)
  THROW 51797, 'PREFLIGHT BLOCKED: a SQL module contains a cross-catalog or external-effect primitive.', 1;
'@
    [void]$command.ExecuteNonQuery()
  }
  finally {
    $command.Dispose()
  }
}

function Set-SanitizerSessionGuard {
  param([System.Data.SqlClient.SqlConnection]$Connection)

  $command = $Connection.CreateCommand()
  try {
    $command.CommandText = @'
IF DB_NAME() COLLATE Latin1_General_100_BIN2 <> N'Orion_Training' COLLATE Latin1_General_100_BIN2
  THROW 51780, 'Blocked: the connected catalog is not exactly Orion_Training.', 1;
EXEC sys.sp_set_session_context
  @key = N'OrionTrainingSanitizerApply',
  @value = N'20260817-v1',
  @read_only = 1;
-- Local server time on purpose. This marker is compared only against
-- sys.dm_db_stats_properties.last_updated, which SQL Server records in local
-- server time. A UTC marker sits ahead of every local timestamp on any server
-- behind UTC, so the FULLSCAN attestation guard could never be satisfied.
-- Both values are produced by the same server clock, so they are comparable.
DECLARE @SanitizerStartedAt datetime = CONVERT(datetime, GETDATE());
EXEC sys.sp_set_session_context
  @key = N'OrionTrainingSanitizerStartedAt',
  @value = @SanitizerStartedAt,
  @read_only = 1;
'@
    [void]$command.ExecuteNonQuery()
  }
  finally {
    $command.Dispose()
  }
}

function Get-TrainingInventory {
  param([System.Data.SqlClient.SqlConnection]$Connection)

  $command = $Connection.CreateCommand()
  try {
    $command.CommandText = @'
IF DB_NAME() COLLATE Latin1_General_100_BIN2 <> N'Orion_Training' COLLATE Latin1_General_100_BIN2
  THROW 51781, 'Blocked: the connected catalog is not exactly Orion_Training.', 1;

SELECT
  DB_NAME() AS DatabaseName,
  (SELECT COUNT_BIG(*) FROM sys.tables WHERE is_ms_shipped = 0) AS UserTableCount,
  (SELECT COUNT_BIG(*)
   FROM sys.tables tableInfo
   WHERE tableInfo.is_ms_shipped = 0
     AND EXISTS
       (SELECT 1 FROM sys.partitions partitionInfo
        WHERE partitionInfo.object_id = tableInfo.object_id
          AND partitionInfo.index_id IN (0, 1)
          AND partitionInfo.rows > 0)) AS NonEmptyUserTableCount,
  (SELECT COALESCE(SUM(CONVERT(bigint, partitionInfo.rows)), 0)
   FROM sys.tables tableInfo
   JOIN sys.partitions partitionInfo ON partitionInfo.object_id = tableInfo.object_id
   WHERE tableInfo.is_ms_shipped = 0
     AND partitionInfo.index_id IN (0, 1)) AS ApproximateRowCount,
  CASE
    WHEN OBJECT_ID(N'capacitacion.EntornoSeguridad', N'U') IS NULL THEN N'MISSING'
    WHEN EXISTS
      (SELECT 1 FROM capacitacion.EntornoSeguridad
       WHERE EntornoSeguridadId = 1 AND Entorno = N'Training'
         AND DatosSanitizados = 1 AND DatosSinteticos = 1
         AND RevisadoEn IS NOT NULL AND NULLIF(LTRIM(RTRIM(RevisadoPor)), N'') IS NOT NULL)
      THEN N'ATTESTED'
    ELSE N'BLOCKED'
  END AS DataAttestation;
'@
    $reader = $command.ExecuteReader()
    try {
      if (-not $reader.Read()) {
        throw 'The training inventory query returned no result.'
      }
      return [pscustomobject]@{
        DatabaseName = $reader.GetString(0)
        UserTableCount = $reader.GetInt64(1)
        NonEmptyUserTableCount = $reader.GetInt64(2)
        ApproximateRowCount = $reader.GetInt64(3)
        DataAttestation = $reader.GetString(4)
      }
    }
    finally {
      $reader.Dispose()
    }
  }
  finally {
    $command.Dispose()
  }
}

function Get-SqlBatches {
  param(
    [Parameter(Mandatory)]
    [string]$Path,

    [Parameter()]
    [hashtable]$SqlCmdVariables = @{}
  )

  if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
    throw "SQL script not found: $Path"
  }

  $text = [IO.File]::ReadAllText($Path)
  foreach ($entry in $SqlCmdVariables.GetEnumerator()) {
    $text = $text.Replace('$(' + $entry.Key + ')', [string]$entry.Value)
  }

  $text = [Text.RegularExpressions.Regex]::Replace(
    $text,
    '(?im)^\s*:(?:ON\s+ERROR\s+EXIT|setvar\s+\w+\s+"[^"]*")\s*\r?\n?',
    '')

  return [Text.RegularExpressions.Regex]::Split($text, '(?im)^\s*GO\s*(?:--[^\r\n]*)?\r?$') |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
}

function Invoke-SqlFile {
  param(
    [Parameter(Mandatory)]
    [System.Data.SqlClient.SqlConnection]$Connection,

    [Parameter(Mandatory)]
    [string]$Path,

    [Parameter()]
    [hashtable]$SqlCmdVariables = @{},

    [Parameter()]
    [hashtable]$Parameters = @{}
  )

  foreach ($batch in (Get-SqlBatches -Path $Path -SqlCmdVariables $SqlCmdVariables)) {
    $command = $Connection.CreateCommand()
    try {
      $command.CommandTimeout = 0
      $command.CommandText = $batch
      foreach ($entry in $Parameters.GetEnumerator()) {
        $parameter = $command.Parameters.Add($entry.Key, [System.Data.SqlDbType]::NVarChar, $entry.Value.Size)
        $parameter.Value = $entry.Value.Value
      }
      [void]$command.ExecuteNonQuery()
    }
    catch {
      $rollback = $Connection.CreateCommand()
      try {
        $rollback.CommandText = 'IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;'
        [void]$rollback.ExecuteNonQuery()
      }
      finally {
        $rollback.Dispose()
      }
      throw
    }
    finally {
      $command.Dispose()
    }
  }
}

function Set-UInt32BigEndian {
  param(
    [byte[]]$Buffer,
    [int]$Offset,
    [uint32]$Value
  )

  $Buffer[$Offset] = [byte](($Value -shr 24) -band 0xff)
  $Buffer[$Offset + 1] = [byte](($Value -shr 16) -band 0xff)
  $Buffer[$Offset + 2] = [byte](($Value -shr 8) -band 0xff)
  $Buffer[$Offset + 3] = [byte]($Value -band 0xff)
}

function New-AspNetIdentityV3PasswordHash {
  param([Parameter(Mandatory)][string]$Password)

  $salt = [byte[]]::new(16)
  [Security.Cryptography.RandomNumberGenerator]::Fill($salt)
  $passwordBytes = [Text.Encoding]::UTF8.GetBytes($Password)
  $deriveBytes = [Security.Cryptography.Rfc2898DeriveBytes]::new(
    $passwordBytes,
    $salt,
    100000,
    [Security.Cryptography.HashAlgorithmName]::SHA256)
  try {
    $subkey = $deriveBytes.GetBytes(32)
    $payload = [byte[]]::new(13 + $salt.Length + $subkey.Length)
    $payload[0] = 0x01
    Set-UInt32BigEndian -Buffer $payload -Offset 1 -Value 1
    Set-UInt32BigEndian -Buffer $payload -Offset 5 -Value 100000
    Set-UInt32BigEndian -Buffer $payload -Offset 9 -Value ([uint32]$salt.Length)
    [Array]::Copy($salt, 0, $payload, 13, $salt.Length)
    [Array]::Copy($subkey, 0, $payload, 13 + $salt.Length, $subkey.Length)
    return [Convert]::ToBase64String($payload)
  }
  finally {
    $deriveBytes.Dispose()
    [Array]::Clear($passwordBytes, 0, $passwordBytes.Length)
    [Array]::Clear($salt, 0, $salt.Length)
  }
}

function ConvertFrom-TrainingSecureString {
  param([Parameter(Mandatory)][Security.SecureString]$Value)

  $pointer = [IntPtr]::Zero
  try {
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Value)
    return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
  }
  finally {
    if ($pointer -ne [IntPtr]::Zero) {
      [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
    }
  }
}

function New-TrainingCredentialMaterial {
  param(
    [Parameter(Mandatory)][Security.SecureString]$SecureValue,
    [Parameter(Mandatory)][string]$AccountLabel
  )

  $plainText = ConvertFrom-TrainingSecureString -Value $SecureValue
  $passwordBytes = $null
  try {
    if ($plainText.Length -lt 12 -or
        $plainText -cnotmatch '[a-z]' -or
        $plainText -cnotmatch '[A-Z]' -or
        $plainText -notmatch '[0-9]' -or
        $plainText -notmatch '[^a-zA-Z0-9]') {
      throw "The $AccountLabel password must contain at least 12 characters, lowercase, uppercase, a number, and a symbol."
    }

    $passwordBytes = [Text.Encoding]::UTF8.GetBytes($plainText)
    return [pscustomobject]@{
      Fingerprint = [Convert]::ToBase64String([Security.Cryptography.SHA256]::HashData($passwordBytes))
      PasswordHash = New-AspNetIdentityV3PasswordHash -Password $plainText
    }
  }
  finally {
    if ($null -ne $passwordBytes) {
      [Array]::Clear($passwordBytes, 0, $passwordBytes.Length)
    }
    $plainText = $null
  }
}

function Set-TrainingDmlTriggersEnabled {
  param(
    [System.Data.SqlClient.SqlConnection]$Connection,
    [bool]$Enabled
  )

  $command = $Connection.CreateCommand()
  try {
    $verb = if ($Enabled) { 'ENABLE' } else { 'DISABLE' }
    $command.CommandText = @"
IF DB_NAME() COLLATE Latin1_General_100_BIN2 <> N'Orion_Training' COLLATE Latin1_General_100_BIN2
  THROW 51798, 'TRIGGER GUARD BLOCKED: the connected catalog is not exactly Orion_Training.', 1;
DECLARE @QualifiedName nvarchar(517);
DECLARE @Statement nvarchar(max);
DECLARE TriggerCursor CURSOR LOCAL FAST_FORWARD FOR
  SELECT QUOTENAME(schemaInfo.name) + N'.' + QUOTENAME(tableInfo.name)
  FROM sys.tables tableInfo
  JOIN sys.schemas schemaInfo ON schemaInfo.schema_id = tableInfo.schema_id
  WHERE tableInfo.is_ms_shipped = 0
  ORDER BY schemaInfo.name, tableInfo.name;
OPEN TriggerCursor;
FETCH NEXT FROM TriggerCursor INTO @QualifiedName;
WHILE @@FETCH_STATUS = 0
BEGIN
  SET @Statement = N'$verb TRIGGER ALL ON ' + @QualifiedName + N';';
  EXEC sys.sp_executesql @Statement;
  FETCH NEXT FROM TriggerCursor INTO @QualifiedName;
END;
CLOSE TriggerCursor;
DEALLOCATE TriggerCursor;
"@
    [void]$command.ExecuteNonQuery()
  }
  finally {
    $command.Dispose()
  }
}

function Update-TrainingStatisticsFullScan {
  param([System.Data.SqlClient.SqlConnection]$Connection)

  $command = $Connection.CreateCommand()
  try {
    $command.CommandTimeout = 0
    $command.CommandText = @'
IF DB_NAME() COLLATE Latin1_General_100_BIN2 <> N'Orion_Training' COLLATE Latin1_General_100_BIN2
  THROW 51804, 'STATISTICS GUARD BLOCKED: the connected catalog is not exactly Orion_Training.', 1;
DECLARE @QualifiedName nvarchar(517);
DECLARE @Statement nvarchar(max);
DECLARE StatisticsCursor CURSOR LOCAL FAST_FORWARD FOR
  SELECT QUOTENAME(schemaInfo.name) + N'.' + QUOTENAME(tableInfo.name)
  FROM sys.tables tableInfo
  JOIN sys.schemas schemaInfo ON schemaInfo.schema_id = tableInfo.schema_id
  WHERE tableInfo.is_ms_shipped = 0
  ORDER BY schemaInfo.name, tableInfo.name;
OPEN StatisticsCursor;
FETCH NEXT FROM StatisticsCursor INTO @QualifiedName;
WHILE @@FETCH_STATUS = 0
BEGIN
  SET @Statement = N'UPDATE STATISTICS ' + @QualifiedName + N' WITH FULLSCAN;';
  EXEC sys.sp_executesql @Statement;
  FETCH NEXT FROM StatisticsCursor INTO @QualifiedName;
END;
CLOSE StatisticsCursor;
DEALLOCATE StatisticsCursor;
'@
    [void]$command.ExecuteNonQuery()
  }
  finally {
    $command.Dispose()
  }
}

$effectiveConnectionString = Get-RequiredConnectionString -ExplicitConnectionString $ConnectionString
$connection = [System.Data.SqlClient.SqlConnection]::new($effectiveConnectionString)

try {
  $connection.Open()
  $inventory = Get-TrainingInventory -Connection $connection
  Write-Output $inventory

  # Preview the exact legacy-object neutralization inside an outer transaction.
  # The included script may use its own nested transaction, but the outer
  # transaction guarantees that Preview never persists a schema change.
  Set-SanitizerSessionGuard -Connection $connection
  $previewTransaction = $connection.CreateCommand()
  try {
    $previewTransaction.CommandText = 'BEGIN TRANSACTION;'
    [void]$previewTransaction.ExecuteNonQuery()
    Invoke-SqlFile -Connection $connection -Path $neutralizeCloneScript
    Assert-TrainingPreProvisionSafety -Connection $connection
  }
  finally {
    $previewTransaction.CommandText = 'IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;'
    [void]$previewTransaction.ExecuteNonQuery()
    $previewTransaction.Dispose()
  }

  if (-not $Apply) {
    Write-Host 'Preview only. No data was changed. Exact clone neutralization and strict preflight succeeded inside a rolled-back transaction; no schema was changed.'
    Write-Host 'Use -Apply -ConfirmDatabase Orion_Training after reviewing the inventory.'
    return
  }

  if (-not [string]::Equals($ConfirmDatabase, $requiredDatabase, [StringComparison]::Ordinal)) {
    throw 'Blocked: -Apply also requires -ConfirmDatabase Orion_Training.'
  }
  if ($null -eq $InstructorPassword -or
      $null -eq $Trainee01Password -or
      $null -eq $Trainee02Password -or
      $null -eq $AuditorPassword) {
    throw 'Blocked: four distinct SecureString passwords are required for instructor, trainee01, trainee02, and auditor.'
  }
  if (-not $PSCmdlet.ShouldProcess(
      'Orion_Training only',
      'erase every cloned operational/personal row, install synthetic scenarios, and attest the result')) {
    return
  }

  $credentialMaterials = @(
    New-TrainingCredentialMaterial -SecureValue $InstructorPassword -AccountLabel 'instructor'
    New-TrainingCredentialMaterial -SecureValue $Trainee01Password -AccountLabel 'trainee01'
    New-TrainingCredentialMaterial -SecureValue $Trainee02Password -AccountLabel 'trainee02'
    New-TrainingCredentialMaterial -SecureValue $AuditorPassword -AccountLabel 'auditor'
  )
  if (@($credentialMaterials.Fingerprint | Select-Object -Unique).Count -ne 4) {
    throw 'Blocked: every synthetic account must have a different plaintext password.'
  }
  try {
    $passwordHashes = @{
      '@InstructorPasswordHash' = @{ Size = -1; Value = $credentialMaterials[0].PasswordHash }
      '@Trainee01PasswordHash' = @{ Size = -1; Value = $credentialMaterials[1].PasswordHash }
      '@Trainee02PasswordHash' = @{ Size = -1; Value = $credentialMaterials[2].PasswordHash }
      '@AuditorPasswordHash' = @{ Size = -1; Value = $credentialMaterials[3].PasswordHash }
    }

    Invoke-SqlFile -Connection $connection -Path $neutralizeCloneScript
    Assert-TrainingPreProvisionSafety -Connection $connection
    Invoke-SqlFile -Connection $connection -Path $sanitizeScript

    $safeToEnableTriggers = $false
    try {
      # Existing cloned triggers are untrusted until the reviewed installer has
      # replaced its own definitions. Suppress them before the installer's first
      # seed write, then suppress again because CREATE OR ALTER TRIGGER enables
      # the reviewed capacitacion triggers near the end of that script.
      Set-TrainingDmlTriggersEnabled -Connection $connection -Enabled $false
      Invoke-SqlFile -Connection $connection -Path $capacitacionInstaller -SqlCmdVariables @{ ExpectedDatabase = $requiredDatabase }
      # The full module curriculum is authored on top of the reviewed schema and
      # publishes each new course version only after its content exists.
      Invoke-SqlFile -Connection $connection -Path $curriculumInstaller -SqlCmdVariables @{ ExpectedDatabase = $requiredDatabase }
      Invoke-SqlFile -Connection $connection -Path $reviewedTriggerScript
      Invoke-SqlFile -Connection $connection -Path $trainingCfdiParserScript
      Set-TrainingDmlTriggersEnabled -Connection $connection -Enabled $false
      Assert-TrainingPreProvisionSafety -Connection $connection
      Invoke-SqlFile -Connection $connection -Path $provisionScript -Parameters $passwordHashes
      Invoke-SqlFile -Connection $connection -Path $scenarioScript
      Update-TrainingStatisticsFullScan -Connection $connection
      $safeToEnableTriggers = $true
    }
    finally {
      if ($safeToEnableTriggers) {
        Set-TrainingDmlTriggersEnabled -Connection $connection -Enabled $true
      }
      else {
        Write-Warning 'Provisioning did not complete; DML triggers remain disabled and OrionERP.Training must stay stopped pending a fresh reviewed reset.'
      }
    }
    Assert-TrainingPreProvisionSafety -Connection $connection
    Invoke-SqlFile -Connection $connection -Path $attestScript -Parameters @{
      '@ReviewedBy' = @{ Size = 256; Value = $ReviewedBy }
    }
    Assert-TrainingPreProvisionSafety -Connection $connection
  }
  finally {
    $passwordHashes = $null
    $credentialMaterials = $null
  }

  $finalInventory = Get-TrainingInventory -Connection $connection
  if ($finalInventory.DataAttestation -ne 'ATTESTED') {
    throw 'The final data attestation could not be read back. OrionERP.Training must remain stopped.'
  }

  Write-Output $finalInventory
  Write-Host 'Orion_Training now contains only the reviewed reference rows and fictional v1 cohort/scenarios.'
  Write-Host 'Synthetic accounts: instructor, trainee01, trainee02, and auditor @training.orion.local.'
  Write-Host 'The application runtime SQL principal is intentionally provisioned by the separate least-privilege workflow.'
}
finally {
  $connection.Dispose()
}
