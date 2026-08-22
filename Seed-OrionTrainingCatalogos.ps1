#requires -Version 7.0
<#
.SYNOPSIS
  Seeds the reviewed reference and synthetic catalogs into an existing
  Orion_Training database, without running a full reset.

.DESCRIPTION
  Sanitize-OrionTraining.ps1 already runs
  20260821_orion_training_catalogos.sql as part of the guarded reset. This
  script exists for the one case that reset does not cover: a Training database
  that was sanitized before the catalog seed existed, and is therefore attested
  but missing every dropdown catalog.

  Re-running the attestation afterwards is neither required nor possible.
  TrainingDatabaseSafetyVerifier gates startup on the capacitacion.EntornoSeguridad
  flags and the schema version only; it performs no row-count or manifest check,
  and nothing but the sanitizer clears that attestation. Running
  20260817_orion_training_attest.sql standalone throws 51752, because it requires
  the untrusted pre-attestation state.

  Preview is the default: the seed runs inside a transaction that is rolled back,
  so you see whether it succeeds without changing anything. Note that a rollback
  does not restore identity counters, so a preview leaves an identity gap. That
  is harmless while the existing attestation stands, but the next full reset is
  what must produce an attestable database, and that reset reseeds every identity
  anyway. To avoid the gap entirely, skip the preview on a database you have a
  backup of.

.EXAMPLE
  $env:ORION_TRAINING_SANITIZER_CONNECTION_STRING = '<sysadmin conn to Orion_Training;Encrypt=True>'
  .\Seed-OrionTrainingCatalogos.ps1
  .\Seed-OrionTrainingCatalogos.ps1 -Apply -ConfirmDatabase Orion_Training
  Remove-Item Env:ORION_TRAINING_SANITIZER_CONNECTION_STRING
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
  [Parameter()]
  [string]$ConnectionString,

  [Parameter()]
  [switch]$Apply,

  [Parameter()]
  [ValidateSet('Orion_Training')]
  [string]$ConfirmDatabase
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$requiredDatabase = 'Orion_Training'
$catalogSeedGuard = '20260821-v1'
$connectionEnvironmentVariable = 'ORION_TRAINING_SANITIZER_CONNECTION_STRING'
$catalogSeedScript = Join-Path $PSScriptRoot 'src\OrionERP.Infrastructure\Features\Capacitacion\Sql\20260821_orion_training_catalogos.sql'

# The tables the seed is responsible for. Counted before and after so the
# operator sees what changed without this script ever printing a row.
$seededTables = @(
  'dbo.Formas_Pago', 'dbo.CuentasContables', 'dbo.Actividad', 'dbo.Compra',
  'dbo.Servicios', 'dbo.Proveedores', 'dbo.CfdiPolizaCuentaDefault',
  'dbo.PlantillaContable', 'dbo.PlantillaContableLinea',
  'dbo.PARAMETROS_CONFIGURACION', 'dbo.OrdenTrabajoCategoria', 'dbo.Extra',
  'dbo.ExperienceProvider', 'dbo.Experience', 'dbo.ExperiencePackage',
  'dbo.ExperienceAddOn', 'dbo.BusinessPartner', 'dbo.BusinessPartnerRfcScope',
  'dbo.BusinessPartnerRole', 'dbo.SatRfcProfile', 'bancos.Cuentas_Banco',
  'logistica.UnitOfMeasure', 'logistica.UnitConversion', 'logistica.Allergen',
  'logistica.MaterialCategory', 'logistica.Material', 'rh.Holiday',
  'restaurante.Site', 'restaurante.DiningTable', 'restaurante.KitchenStation',
  'restaurante.CashRegister', 'restaurante.ExternalProvider',
  'restaurante.AccountingConfiguration', 'restaurante.ProductCard',
  'restaurante.Product', 'restaurante.ModifierGroup', 'restaurante.ModifierOption',
  'restaurante.Menu', 'restaurante.MenuSection', 'restaurante.MenuItem',
  'restaurante.MenuSchedule'
)

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
    throw 'Blocked: the catalog seed connection must declare Server or Data Source.'
  }
  if (-not [string]::IsNullOrWhiteSpace([string]$builder['AttachDBFilename'])) {
    throw 'Blocked: AttachDBFilename is not supported by the training catalog seed.'
  }
  if (-not [Convert]::ToBoolean($builder['Encrypt'])) {
    throw 'Blocked: the catalog seed administrative connection must use Encrypt=True.'
  }
  $builder['Persist Security Info'] = $false

  # This script runs once and exits, and its preflight refuses to seed while any
  # other session holds Orion_Training. A pooled connection stays physically open
  # after Close(), so pooling would leave this script's own connection behind and
  # make the next attempt in the same PowerShell window block on itself.
  $builder['Pooling'] = $false

  return $builder.ConnectionString
}

function Get-SqlBatches {
  param(
    [Parameter(Mandatory)]
    [string]$Path
  )

  if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
    throw "SQL script not found: $Path"
  }

  return [Text.RegularExpressions.Regex]::Split(
      [IO.File]::ReadAllText($Path),
      '(?im)^\s*GO\s*(?:--[^\r\n]*)?\r?$') |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
}

function Invoke-SqlFile {
  param(
    [Parameter(Mandatory)]
    [System.Data.SqlClient.SqlConnection]$Connection,

    [Parameter(Mandatory)]
    [string]$Path
  )

  foreach ($batch in (Get-SqlBatches -Path $Path)) {
    $command = $Connection.CreateCommand()
    try {
      $command.CommandTimeout = 0
      $command.CommandText = $batch
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

function Invoke-Scalar {
  param(
    [Parameter(Mandatory)]
    [System.Data.SqlClient.SqlConnection]$Connection,

    [Parameter(Mandatory)]
    [string]$Sql
  )

  $command = $Connection.CreateCommand()
  try {
    $command.CommandText = $Sql
    return $command.ExecuteScalar()
  }
  finally {
    $command.Dispose()
  }
}

function Assert-CatalogSeedSafety {
  param([System.Data.SqlClient.SqlConnection]$Connection)

  $command = $Connection.CreateCommand()
  try {
    $command.CommandText = @'
IF DB_NAME() COLLATE Latin1_General_100_BIN2 <> N'Orion_Training' COLLATE Latin1_General_100_BIN2
  THROW 51950, 'PREFLIGHT BLOCKED: the connected catalog is not exactly Orion_Training.', 1;
IF ISNULL(IS_SRVROLEMEMBER(N'sysadmin'), 0) <> 1
  THROW 51951, 'PREFLIGHT BLOCKED: the guarded catalog seed requires a sysadmin maintenance connection.', 1;
/*
  Deliberately no HAS_DBACCESS('grupocarpio') check here. That rule belongs to
  the runtime login, which TrainingDatabaseSafetyVerifier holds to it at startup.
  A maintenance connection is sysadmin by requirement, and sysadmin can reach
  every database on the instance -- including Orion_Training and grupocarpio when
  they are hosted together, which is the normal deployment. Asserting it here
  would make this script unrunnable in exactly the environment it exists for.
  What keeps this batch off production is the Initial Catalog pin in
  Get-RequiredConnectionString and the DB_NAME() guard below.
*/
IF EXISTS
(
  SELECT 1
  FROM sys.dm_exec_sessions
  WHERE is_user_process = 1
    AND database_id = DB_ID()
    AND session_id <> @@SPID
)
  THROW 51953, 'PREFLIGHT BLOCKED: stop OrionERP.Training and close every other Orion_Training session before seeding.', 1;
'@
    [void]$command.ExecuteNonQuery()
  }
  finally {
    $command.Dispose()
  }
}

function Get-CatalogInventory {
  param([System.Data.SqlClient.SqlConnection]$Connection)

  $inventory = [ordered]@{}
  foreach ($table in $seededTables) {
    $parts = $table.Split('.')
    $quoted = "[{0}].[{1}]" -f $parts[0], $parts[1]
    $exists = Invoke-Scalar -Connection $Connection -Sql "SELECT CASE WHEN OBJECT_ID(N'$table', N'U') IS NULL THEN 0 ELSE 1 END;"
    if ([int]$exists -eq 0) {
      $inventory[$table] = $null
    }
    else {
      $inventory[$table] = [int](Invoke-Scalar -Connection $Connection -Sql "SELECT COUNT(*) FROM $quoted;")
    }
  }
  return $inventory
}

function Write-CatalogInventory {
  param($Before, $After)

  Write-Host ''
  Write-Host 'Catalog row counts:'
  foreach ($table in $seededTables) {
    $from = $Before[$table]
    $to = $After[$table]
    if ($null -eq $from) {
      Write-Host ("  {0,-42} (table missing)" -f $table)
      continue
    }
    $marker = if ($to -gt $from) { '  <-- seeded' } else { '' }
    Write-Host ("  {0,-42} {1,6} -> {2,-6}{3}" -f $table, $from, $to, $marker)
  }
  Write-Host ''
}

$resolvedConnectionString = Get-RequiredConnectionString -ExplicitConnectionString $ConnectionString

if ($Apply -and $ConfirmDatabase -ne $requiredDatabase) {
  throw "Blocked: -Apply requires -ConfirmDatabase $requiredDatabase."
}

$connection = [System.Data.SqlClient.SqlConnection]::new($resolvedConnectionString)
$connection.Open()
try {
  # Name the blocking sessions before the SQL guard throws. "Close every other
  # session" is not actionable when the operator cannot see which ones they are,
  # and the answer is usually a forgotten SSMS tab.
  $blockers = $connection.CreateCommand()
  try {
    $blockers.CommandText = @'
SELECT session_id, login_name, ISNULL(host_name, N''), ISNULL(program_name, N''),
       DATEDIFF(minute, last_request_end_time, SYSDATETIME())
FROM sys.dm_exec_sessions
WHERE is_user_process = 1
  AND database_id = DB_ID()
  AND session_id <> @@SPID
ORDER BY session_id;
'@
    $reader = $blockers.ExecuteReader()
    $found = @()
    while ($reader.Read()) {
      $found += [pscustomobject]@{
        SPID     = $reader.GetInt16(0)
        Login    = $reader.GetString(1)
        Host     = $reader.GetString(2)
        Programa = $reader.GetString(3)
        InactivaMin = $reader.GetInt32(4)
      }
    }
    $reader.Close()

    if ($found.Count -gt 0) {
      Write-Host ''
      Write-Host "Orion_Training tiene $($found.Count) sesion(es) abierta(s). Ciérralas antes de sembrar:"
      $found | Format-Table -AutoSize | Out-String | Write-Host
      throw "Hay $($found.Count) sesion(es) usando Orion_Training. Detén OrionERP.Training y cierra las ventanas listadas arriba (en SSMS basta con cerrar la pestaña de consulta)."
    }
  }
  finally {
    $blockers.Dispose()
  }

  Assert-CatalogSeedSafety -Connection $connection

  # The seed refuses to run without this key, which is what keeps it from being
  # executed by an ordinary application connection.
  $guard = $connection.CreateCommand()
  try {
    $guard.CommandText = 'EXEC sys.sp_set_session_context @key = N''OrionTrainingCatalogSeedApply'', @value = @value, @read_only = 1;'
    [void]$guard.Parameters.AddWithValue('@value', $catalogSeedGuard)
    [void]$guard.ExecuteNonQuery()
  }
  finally {
    $guard.Dispose()
  }

  $before = Get-CatalogInventory -Connection $connection

  if (-not $Apply) {
    # Preview: the seed's own COMMIT is nested inside this transaction, so it
    # decrements the count without committing, and the rollback below discards
    # everything.
    $begin = $connection.CreateCommand()
    try {
      $begin.CommandText = 'BEGIN TRANSACTION;'
      [void]$begin.ExecuteNonQuery()
    }
    finally {
      $begin.Dispose()
    }

    try {
      Invoke-SqlFile -Connection $connection -Path $catalogSeedScript
      $after = Get-CatalogInventory -Connection $connection
      Write-CatalogInventory -Before $before -After $after
    }
    finally {
      $rollback = $connection.CreateCommand()
      try {
        $rollback.CommandText = 'IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;'
        [void]$rollback.ExecuteNonQuery()
      }
      finally {
        $rollback.Dispose()
      }
    }

    Write-Host 'Preview only. No data was changed; the seed succeeded inside a rolled-back transaction.'
    Write-Host "Use -Apply -ConfirmDatabase $requiredDatabase to commit."
    Write-Warning 'A rollback does not restore identity counters, so this preview left an identity gap. It is harmless while the current attestation stands, and the next full reset reseeds every identity.'
    return
  }

  if (-not $PSCmdlet.ShouldProcess($requiredDatabase, 'Seed reviewed reference and synthetic catalogs')) {
    return
  }

  Invoke-SqlFile -Connection $connection -Path $catalogSeedScript
  $after = Get-CatalogInventory -Connection $connection
  Write-CatalogInventory -Before $before -After $after

  Write-Host "Catalog seed applied to $requiredDatabase."
  Write-Host 'The existing data attestation is unchanged and remains valid; OrionERP.Training can be started again.'
}
finally {
  $connection.Close()
  $connection.Dispose()
}
