[CmdletBinding()]
param(
    [ValidateSet("OrionERP", "Bonhomia", "Bruno")]
    [string[]]$Applications = @("OrionERP", "Bonhomia", "Bruno"),
    [string]$Runtime = "win-x64",
    [switch]$ValidateOnly,
    [switch]$AllowNonMain,
    [switch]$AllowDirty
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
Set-Location -Path $PSScriptRoot

. (Join-Path $PSScriptRoot "deployment\Publish-Safety.ps1")

function Write-Step {
    param([string]$Message)

    Write-Host ""
    Write-Host "=== $Message ===" -ForegroundColor Cyan
}

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function ConvertTo-EncodedPowerShellCommand {
    param([string]$Command)

    return [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($Command))
}

function ConvertTo-SingleQuotedLiteral {
    param([string]$Value)

    return "'" + $Value.Replace("'", "''") + "'"
}

function Invoke-NativeCommand {
    param(
        [string]$FilePath,
        [string[]]$ArgumentList
    )

    & $FilePath @ArgumentList
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code ${LASTEXITCODE}: $FilePath"
    }
}

$targets = @{
    OrionERP = [pscustomobject]@{
        DisplayName = "OrionERP management console"
        ServiceName = "OrionERP"
        ProjectPath = "src\OrionERP.Web\OrionERP.Web.csproj"
        OutputDirectory = "C:\Users\Orion\Grupo Carpio Dropbox\Grupo Orion\Software\GitHubs\Production\OrionERP"
        # A dedicated readiness endpoint, matching Bonhomia and Bruno. Probing "/"
        # follows a redirect to the login page, and rendering that form over plain
        # HTTP fails once antiforgery cookies require a secure request, which would
        # fail the health check and roll back a perfectly good deployment.
        HealthCheckUrl = "http://127.0.0.1:5000/readyz"
        InstanceSettingsPath = ""
    }
    Bonhomia = [pscustomobject]@{
        DisplayName = "Bonhomia public website"
        ServiceName = "OrionERP.Bonhomia"
        ProjectPath = "src\OrionERP.Bonhomia.Web\OrionERP.Bonhomia.Web.csproj"
        OutputDirectory = "C:\Users\Orion\Grupo Carpio Dropbox\Grupo Orion\Software\GitHubs\Production\OrionERP.Bonhomia.Web"
        HealthCheckUrl = "http://127.0.0.1:5010/readyz"
        InstanceSettingsPath = "deployment\public-sites\bonhomia-main.json"
    }
    Bruno = [pscustomobject]@{
        DisplayName = "Bruno's public website"
        ServiceName = "OrionERP.Bruno"
        ProjectPath = "src\OrionERP.Bruno.Web\OrionERP.Bruno.Web.csproj"
        OutputDirectory = "C:\Users\Orion\Grupo Carpio Dropbox\Grupo Orion\Software\GitHubs\Production\OrionERP.Bruno.Web"
        HealthCheckUrl = "http://127.0.0.1:5020/readyz"
        InstanceSettingsPath = "deployment\public-sites\brunos-main.json"
    }
}

$selectedTargets = @($Applications | Select-Object -Unique | ForEach-Object { $targets[$_] })
if ($selectedTargets.Count -eq 0) {
    throw "Select at least one application to publish."
}

Assert-OrionProductionGitState `
    -RepositoryRoot $PSScriptRoot `
    -AllowNonMain:$AllowNonMain `
    -AllowDirty:$AllowDirty

if (-not $ValidateOnly -and -not (Test-IsAdministrator)) {
    $powerShellExe = (Get-Process -Id $PID).Path
    if ([string]::IsNullOrWhiteSpace($powerShellExe)) {
        $powerShellExe = "powershell.exe"
    }

    $applicationLiterals = ($Applications | Select-Object -Unique | ForEach-Object { ConvertTo-SingleQuotedLiteral $_ }) -join ","
    $elevatedCommand = "& $(ConvertTo-SingleQuotedLiteral $PSCommandPath) -Runtime $(ConvertTo-SingleQuotedLiteral $Runtime) -Applications @($applicationLiterals)"
    if ($AllowNonMain) { $elevatedCommand += " -AllowNonMain" }
    if ($AllowDirty) { $elevatedCommand += " -AllowDirty" }

    Write-Host "Opening one elevated PowerShell window for the complete production publish."
    $elevatedProcess = Start-Process `
        -FilePath $powerShellExe `
        -ArgumentList @("-NoProfile", "-ExecutionPolicy", "Bypass", "-EncodedCommand", (ConvertTo-EncodedPowerShellCommand $elevatedCommand)) `
        -WorkingDirectory $PSScriptRoot `
        -Verb RunAs `
        -Wait `
        -PassThru

    exit $elevatedProcess.ExitCode
}

$publishWorker = Join-Path $PSScriptRoot "Publish-prod.ps1"
if ($ValidateOnly) {
    foreach ($target in $selectedTargets) {
        Write-Step "Validating $($target.DisplayName)"
        & $publishWorker `
            -ServiceName $target.ServiceName `
            -ProjectPath $target.ProjectPath `
            -OutputDirectory $target.OutputDirectory `
            -Runtime $Runtime `
            -InstanceSettingsPath $target.InstanceSettingsPath `
            -AllowNonMain:$AllowNonMain `
            -AllowDirty:$AllowDirty `
            -ValidateOnly
    }

    Write-Step "Validation completed successfully"
    exit 0
}

foreach ($target in $selectedTargets) {
    if (-not (Get-Service -Name $target.ServiceName -ErrorAction SilentlyContinue)) {
        throw "Required Windows service '$($target.ServiceName)' was not found. Complete the service setup before publishing $($target.DisplayName)."
    }
}

foreach ($target in $selectedTargets) {
    Write-Step "Publishing $($target.DisplayName)"
    & $publishWorker `
        -ServiceName $target.ServiceName `
        -ProjectPath $target.ProjectPath `
        -OutputDirectory $target.OutputDirectory `
        -Runtime $Runtime `
        -HealthCheckUrl $target.HealthCheckUrl `
        -InstanceSettingsPath $target.InstanceSettingsPath `
        -AllowNonMain:$AllowNonMain `
        -AllowDirty:$AllowDirty
}

Write-Step "Full production publish completed successfully"
