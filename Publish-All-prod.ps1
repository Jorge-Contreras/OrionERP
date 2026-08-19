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

function Assert-GitState {
    $insideWorkTree = (& git -C $PSScriptRoot rev-parse --is-inside-work-tree 2>$null).Trim()
    if ($LASTEXITCODE -ne 0 -or $insideWorkTree -ne "true") {
        throw "The publish script must run from the OrionERP Git working tree."
    }

    $branch = (& git -C $PSScriptRoot branch --show-current).Trim()
    if (-not $AllowNonMain -and $branch -ne "main") {
        throw "Production publishing requires branch 'main'. Current branch: '$branch'. Use -AllowNonMain only for an intentional production smoke test."
    }

    $changes = @(& git -C $PSScriptRoot status --porcelain)
    if (-not $AllowDirty -and $changes.Count -gt 0) {
        throw "Production publishing requires a clean working tree. Commit or stash the current changes first."
    }

    if (-not $AllowNonMain) {
        Invoke-NativeCommand -FilePath "git" -ArgumentList @("-C", $PSScriptRoot, "fetch", "origin", "main", "--quiet")
        $head = (& git -C $PSScriptRoot rev-parse HEAD).Trim()
        $originMain = (& git -C $PSScriptRoot rev-parse origin/main).Trim()
        if ($LASTEXITCODE -ne 0 -or $head -ne $originMain) {
            throw "Local main is not identical to origin/main. Pull the latest main before publishing."
        }
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
    }
    Bonhomia = [pscustomobject]@{
        DisplayName = "Bonhomia public website"
        ServiceName = "OrionERP.Bonhomia"
        ProjectPath = "src\OrionERP.Bonhomia.Web\OrionERP.Bonhomia.Web.csproj"
        OutputDirectory = "C:\Users\Orion\Grupo Carpio Dropbox\Grupo Orion\Software\GitHubs\Production\OrionERP.Bonhomia.Web"
        HealthCheckUrl = "http://127.0.0.1:5010/healthz"
    }
    Bruno = [pscustomobject]@{
        DisplayName = "Bruno's public website"
        ServiceName = "OrionERP.Bruno"
        ProjectPath = "src\OrionERP.Bruno.Web\OrionERP.Bruno.Web.csproj"
        OutputDirectory = "C:\Users\Orion\Grupo Carpio Dropbox\Grupo Orion\Software\GitHubs\Production\OrionERP.Bruno.Web"
        HealthCheckUrl = "http://127.0.0.1:5020/readyz"
    }
}

$selectedTargets = @($Applications | Select-Object -Unique | ForEach-Object { $targets[$_] })
if ($selectedTargets.Count -eq 0) {
    throw "Select at least one application to publish."
}

Assert-GitState

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

if ($ValidateOnly) {
    $tempRoot = [System.IO.Path]::GetFullPath($env:TEMP).TrimEnd([System.IO.Path]::DirectorySeparatorChar)
    $validationRoot = [System.IO.Path]::GetFullPath((Join-Path $tempRoot ("OrionERP-full-publish-validation-" + [Guid]::NewGuid().ToString("N"))))
    if (-not $validationRoot.StartsWith($tempRoot + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "The validation directory resolved outside the system temporary directory."
    }

    try {
        foreach ($target in $selectedTargets) {
            Write-Step "Validating $($target.DisplayName)"
            $targetOutput = Join-Path $validationRoot $target.ServiceName
            Invoke-NativeCommand -FilePath "dotnet" -ArgumentList @(
                "publish",
                (Join-Path $PSScriptRoot $target.ProjectPath),
                "-c", "Release",
                "-r", $Runtime,
                "--self-contained", "false",
                "--nologo",
                "--verbosity", "minimal",
                "-o", $targetOutput
            )
        }

        Write-Step "Validation completed successfully"
    }
    finally {
        Remove-Item -LiteralPath $validationRoot -Recurse -Force -ErrorAction SilentlyContinue
    }

    exit 0
}

foreach ($target in $selectedTargets) {
    if (-not (Get-Service -Name $target.ServiceName -ErrorAction SilentlyContinue)) {
        throw "Required Windows service '$($target.ServiceName)' was not found. Complete the service setup before publishing $($target.DisplayName)."
    }
}

$publishWorker = Join-Path $PSScriptRoot "Publish-prod.ps1"
foreach ($target in $selectedTargets) {
    Write-Step "Publishing $($target.DisplayName)"
    & $publishWorker `
        -ServiceName $target.ServiceName `
        -ProjectPath $target.ProjectPath `
        -OutputDirectory $target.OutputDirectory `
        -Runtime $Runtime `
        -HealthCheckUrl $target.HealthCheckUrl
}

Write-Step "Full production publish completed successfully"
