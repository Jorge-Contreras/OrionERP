[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)]
    [ValidateSet("Orion_Training", "Orion_Sandbox", "grupocarpio")]
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
if (-not [string]::Equals($catalog, $ExpectedDatabase, [StringComparison]::Ordinal)) {
    throw "The connection-string catalog must exactly match -ExpectedDatabase."
}
if (-not $integrated -and ([string]::IsNullOrWhiteSpace($user) -or [string]::IsNullOrWhiteSpace($password))) {
    throw "The connection string must use Integrated Security or include User Id and Password."
}

$schemaScript = Join-Path $PSScriptRoot "src\OrionERP.Infrastructure\Features\Capacitacion\Sql\20260817_capacitacion_v1.sql"
if (-not (Test-Path -LiteralPath $schemaScript -PathType Leaf)) {
    throw "Capacitacion schema script was not found."
}

$sqlcmdArguments = @(
    "-S", $server,
    "-d", $ExpectedDatabase,
    "-N",
    "-C",
    "-b",
    "-f", "65001",
    "-v", "ExpectedDatabase=$ExpectedDatabase"
)
if ($integrated) {
    $sqlcmdArguments += "-E"
}
else {
    $sqlcmdArguments += @("-U", $user)
}

$temporaryDirectory = $null
$savedPassword = $env:SQLCMDPASSWORD
try {
    if (-not $integrated) {
        $env:SQLCMDPASSWORD = $password
    }

    if ($Apply) {
        if (-not $PSCmdlet.ShouldProcess($ExpectedDatabase, "Apply Capacitacion schema version 1")) {
            return
        }

        & sqlcmd @sqlcmdArguments -i $schemaScript
    }
    else {
        $temporaryDirectory = Join-Path ([IO.Path]::GetTempPath()) ("orion-capacitacion-preview-" + [Guid]::NewGuid().ToString("N"))
        New-Item -ItemType Directory -Path $temporaryDirectory | Out-Null
        $wrapperPath = Join-Path $temporaryDirectory "preview.sql"
        $escapedScriptPath = $schemaScript.Replace('"', '""')
        @"
:ON ERROR EXIT
BEGIN TRANSACTION;
GO
:r "$escapedScriptPath"
IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
GO
"@ | Set-Content -LiteralPath $wrapperPath -Encoding utf8NoBOM

        Write-Host "Validating the complete schema inside a transaction that will be rolled back."
        & sqlcmd @sqlcmdArguments -i $wrapperPath
    }

    if ($LASTEXITCODE -ne 0) {
        throw "sqlcmd failed with exit code $LASTEXITCODE."
    }

    if ($Apply) {
        Write-Host "Capacitacion schema applied successfully."
    }
    else {
        Write-Host "Capacitacion schema preview passed; no changes were committed."
    }
}
finally {
    $env:SQLCMDPASSWORD = $savedPassword
    if ($temporaryDirectory -and (Test-Path -LiteralPath $temporaryDirectory)) {
        Remove-Item -LiteralPath $temporaryDirectory -Recurse -Force
    }
}
