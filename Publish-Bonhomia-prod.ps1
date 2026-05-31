[CmdletBinding()]
param(
    [string]$Runtime = "win-x64",
    [switch]$SkipServiceControl
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

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
    $encodedCommand = ConvertTo-EncodedPowerShellCommand -Command $elevatedCommand

    Write-Host "This publish needs Administrator rights to restart the OrionERP.Bonhomia service."
    Write-Host "Opening an elevated PowerShell window. Approve the UAC prompt to continue deployment."

    $elevatedProcess = Start-Process `
        -FilePath $powerShellExe `
        -ArgumentList @("-NoProfile", "-ExecutionPolicy", "Bypass", "-EncodedCommand", $encodedCommand) `
        -WorkingDirectory $PSScriptRoot `
        -Verb RunAs `
        -Wait `
        -PassThru

    exit $elevatedProcess.ExitCode
}

$publishScript = Join-Path -Path $PSScriptRoot -ChildPath "Publish-prod.ps1"

$arguments = @{
    ServiceName = "OrionERP.Bonhomia"
    ProjectPath = "src\OrionERP.Bonhomia.Web\OrionERP.Bonhomia.Web.csproj"
    OutputDirectory = "C:\Users\Orion\Grupo Carpio Dropbox\Grupo Orion\Software\GitHubs\Production\OrionERP.Bonhomia.Web"
    Runtime = $Runtime
}

if ($SkipServiceControl) {
    $arguments.SkipServiceControl = $true
}

& $publishScript @arguments
