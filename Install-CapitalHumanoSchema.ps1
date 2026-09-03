<#
.SYNOPSIS
  Aplica el esquema rh de Capital Humano y su seguridad a nivel de fila.

.DESCRIPTION
  Los dos scripts son idempotentes y aceptan las variables SQLCMD ExpectedDatabase
  y ApplyChanges. Sin -Apply corren con ApplyChanges=0: validan todo y revierten la
  transaccion sin dejar rastro, que es el modo de revision que exige AGENTS.md
  antes de cualquier cambio en produccion.

  El orden importa: la politica de seguridad necesita que las tablas ya existan.

.EXAMPLE
  ./Install-CapitalHumanoSchema.ps1 -ExpectedDatabase Orion_Sandbox
  ./Install-CapitalHumanoSchema.ps1 -ExpectedDatabase Orion_Sandbox -Apply
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)]
    [ValidateSet("Orion_Sandbox", "Orion_Training", "grupocarpio")]
    [string]$ExpectedDatabase,

    [string]$ConnectionString = $env:ORION_SCHEMA_ConnectionString,

    [switch]$Apply
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
Set-Location -LiteralPath $PSScriptRoot

function Get-ConnectionValue {
    param(
        [string]$Text,
        [string[]]$Names
    )

    foreach ($name in $Names) {
        $escaped = [regex]::Escape($name)
        $match = [regex]::Match(
            $Text,
            "(?i)(?:^|;)\s*$escaped\s*=\s*(?:`"([^`"]*)`"|'([^']*)'|([^;]*))")
        if ($match.Success) {
            foreach ($index in 1..3) {
                if ($match.Groups[$index].Success) {
                    return $match.Groups[$index].Value.Trim()
                }
            }
        }
    }

    return ""
}

if ([string]::IsNullOrWhiteSpace($ConnectionString)) {
    $ConnectionString = $env:ASPNETCORE_ConnectionStrings__OrionDb
}
if ([string]::IsNullOrWhiteSpace($ConnectionString)) {
    throw "Set ORION_SCHEMA_ConnectionString or pass -ConnectionString. The value is never printed."
}

if (-not (Get-Command sqlcmd -ErrorAction SilentlyContinue)) {
    throw "sqlcmd is required and was not found in PATH."
}

$server = Get-ConnectionValue -Text $ConnectionString -Names @("Server", "Data Source")
$catalog = Get-ConnectionValue -Text $ConnectionString -Names @("Database", "Initial Catalog")
$user = Get-ConnectionValue -Text $ConnectionString -Names @("User Id", "UID")
$password = Get-ConnectionValue -Text $ConnectionString -Names @("Password", "PWD")
$integratedValue = Get-ConnectionValue -Text $ConnectionString -Names @("Integrated Security", "Trusted_Connection")
$integrated = $integratedValue -match "^(?i:true|yes|sspi)$"

if ([string]::IsNullOrWhiteSpace($server)) {
    throw "The connection string must include Server or Data Source."
}
if (-not [string]::Equals($catalog, $ExpectedDatabase, [StringComparison]::OrdinalIgnoreCase)) {
    throw "The connection-string catalog must match -ExpectedDatabase."
}
if (-not $integrated -and ([string]::IsNullOrWhiteSpace($user) -or [string]::IsNullOrWhiteSpace($password))) {
    throw "The connection string must use Integrated Security or include User Id and Password."
}

$schemaRoot = Join-Path $PSScriptRoot "src\OrionERP.Infrastructure\Features\CapitalHumano\Workforce\Sql"
$scriptsToApply = @(
    (Join-Path $schemaRoot "20260805_workforce_attendance_mvp.sql"),
    (Join-Path $schemaRoot "20260903_zz_rh_rls.sql")
)
foreach ($scriptPath in $scriptsToApply) {
    if (-not (Test-Path -LiteralPath $scriptPath -PathType Leaf)) {
        throw "Capital Humano script was not found: $scriptPath"
    }
}

$applyChanges = if ($Apply) { "1" } else { "0" }

$sqlcmdArguments = @(
    "-S", $server,
    "-d", $ExpectedDatabase,
    "-N",
    "-C",
    "-b",
    "-f", "65001",
    "-v", "ExpectedDatabase=$ExpectedDatabase",
    "-v", "ApplyChanges=$applyChanges"
)
if ($integrated) {
    $sqlcmdArguments += "-E"
}
else {
    $sqlcmdArguments += @("-U", $user)
}

$savedPassword = $env:SQLCMDPASSWORD
try {
    if (-not $integrated) {
        $env:SQLCMDPASSWORD = $password
    }

    if ($Apply -and -not $PSCmdlet.ShouldProcess($ExpectedDatabase, "Apply the Capital Humano rh schema and its row-level security")) {
        return
    }

    if (-not $Apply) {
        Write-Host "Validating the rh schema and its row-level security; every change is rolled back."
    }

    foreach ($scriptPath in $scriptsToApply) {
        Write-Host "-> $(Split-Path -Leaf $scriptPath)"
        & sqlcmd @sqlcmdArguments -i $scriptPath
        if ($LASTEXITCODE -ne 0) {
            throw "sqlcmd failed with exit code $LASTEXITCODE."
        }
    }

    if ($Apply) {
        Write-Host "Capital Humano schema and row-level security applied successfully."
    }
    else {
        Write-Host "Capital Humano preview passed; no changes were committed."
    }
}
finally {
    $env:SQLCMDPASSWORD = $savedPassword
}
