[CmdletBinding()]
param(
    [string]$Runtime = "win-x64",
    [switch]$SkipServiceControl,
    [switch]$AllowNonMain,
    [switch]$AllowDirty,
    [switch]$RollbackToPreviousRelease
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

Set-Location -Path $PSScriptRoot
. (Join-Path $PSScriptRoot "deployment\Publish-Safety.ps1")

if (-not $RollbackToPreviousRelease) {
    Assert-OrionProductionGitState `
        -RepositoryRoot $PSScriptRoot `
        -AllowNonMain:$AllowNonMain `
        -AllowDirty:$AllowDirty
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

if (-not $SkipServiceControl -and -not (Test-IsAdministrator)) {
    $powerShellExe = (Get-Process -Id $PID).Path
    if ([string]::IsNullOrWhiteSpace($powerShellExe)) {
        $powerShellExe = "powershell.exe"
    }
    $escapedScriptPath = $PSCommandPath.Replace("'", "''")
    $escapedRuntime = $Runtime.Replace("'", "''")
    $elevatedCommand = "& '$escapedScriptPath' -Runtime '$escapedRuntime'"
    if ($AllowNonMain) { $elevatedCommand += " -AllowNonMain" }
    if ($AllowDirty) { $elevatedCommand += " -AllowDirty" }
    if ($RollbackToPreviousRelease) { $elevatedCommand += " -RollbackToPreviousRelease" }
    $encodedCommand = ConvertTo-EncodedPowerShellCommand -Command $elevatedCommand
    Write-Host "This publish needs Administrator rights to restart the OrionERP.Bruno service."
    $elevatedProcess = Start-Process `
        -FilePath $powerShellExe `
        -ArgumentList @("-NoProfile", "-ExecutionPolicy", "Bypass", "-EncodedCommand", $encodedCommand) `
        -WorkingDirectory $PSScriptRoot `
        -Verb RunAs `
        -Wait `
        -PassThru
    exit $elevatedProcess.ExitCode
}

$arguments = @{
    ServiceName = "OrionERP.Bruno"
    ProjectPath = "src\OrionERP.Bruno.Web\OrionERP.Bruno.Web.csproj"
    OutputDirectory = "C:\Users\Orion\Grupo Carpio Dropbox\Grupo Orion\Software\GitHubs\Production\OrionERP.Bruno.Web"
    Runtime = $Runtime
    HealthCheckUrl = "http://127.0.0.1:5020/readyz"
    InstanceSettingsPath = "deployment\public-sites\brunos-main.json"
    InstanceProfileValidatorAssembly = "OrionERP.Bruno.Web.dll"
}
if ($SkipServiceControl) {
    $arguments.SkipServiceControl = $true
}
if ($AllowNonMain) {
    $arguments.AllowNonMain = $true
}
if ($AllowDirty) {
    $arguments.AllowDirty = $true
}
if ($RollbackToPreviousRelease) {
    $arguments.RollbackToPreviousRelease = $true
}

& (Join-Path $PSScriptRoot "Publish-prod.ps1") @arguments
