[CmdletBinding()]
param(
    [string]$ServiceName = "OrionERP",
    [string]$ProjectPath = "src\OrionERP.Web\OrionERP.Web.csproj",
    [string]$OutputDirectory = "C:\Users\Orion\Grupo Carpio Dropbox\Grupo Orion\Software\GitHubs\Production\OrionERP",
    [string]$Runtime = "win-x64",
    [string[]]$PreserveFilePatterns = @("appsettings*.json"),
    [string[]]$PreserveDirectoryPatterns = @("App_Data"),
    [int]$CopyRetries = 5,
    [int]$CopyRetryWaitSeconds = 2,
    [string]$HealthCheckUrl = "http://127.0.0.1:5000/",
    [int]$HealthCheckAttempts = 15,
    [int]$HealthCheckDelaySeconds = 2,
    [switch]$SkipServiceControl
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

Set-Location -Path $PSScriptRoot

function Write-Step {
    param([string]$Message)

    Write-Host ""
    Write-Host "=== $Message ===" -ForegroundColor Cyan
}

function Resolve-ScriptPath {
    param([string]$Path)

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path -Path $PSScriptRoot -ChildPath $Path))
}

function Assert-CommandExists {
    param([string]$CommandName)

    if (-not (Get-Command -Name $CommandName -ErrorAction SilentlyContinue)) {
        throw "Required command '$CommandName' was not found in PATH."
    }
}

function Invoke-NativeCommand {
    param(
        [string]$FilePath,
        [string[]]$ArgumentList,
        [int[]]$SuccessExitCodes = @(0)
    )

    & $FilePath @ArgumentList

    if ($LASTEXITCODE -notin $SuccessExitCodes) {
        $commandText = ($ArgumentList | ForEach-Object {
            if ($_ -match "\s") { '"{0}"' -f $_ } else { $_ }
        }) -join " "

        throw ("Command failed with exit code {0}: {1} {2}" -f $LASTEXITCODE, $FilePath, $commandText)
    }
}

function Get-OrionService {
    param([string]$Name)

    return Get-Service -Name $Name -ErrorAction SilentlyContinue
}

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Stop-OrionService {
    param([string]$Name)

    $svc = Get-OrionService -Name $Name
    if (-not $svc) {
        Write-Warning "Service '$Name' not found. Skipping stop."
        return $false
    }

    if ($svc.Status -eq "Stopped") {
        Write-Host "Service '$Name' is already stopped."
        return $false
    }

    Write-Step "Stopping service: $Name"
    Stop-Service -Name $Name -Force
    $svc.WaitForStatus("Stopped", [TimeSpan]::FromSeconds(30))
    Write-Host "Service '$Name' stopped."
    return $true
}

function Start-OrionService {
    param([string]$Name)

    $svc = Get-OrionService -Name $Name
    if (-not $svc) {
        Write-Warning "Service '$Name' not found. Skipping start."
        return
    }

    $svc.Refresh()
    if ($svc.Status -eq "Running") {
        Write-Host "Service '$Name' is already running."
        return
    }

    Write-Step "Starting service: $Name"
    Start-Service -Name $Name
    $svc.WaitForStatus("Running", [TimeSpan]::FromSeconds(30))
    Write-Host "Service '$Name' is running."
}

function Copy-Directory {
    param(
        [string]$Source,
        [string]$Destination,
        [string[]]$ExcludeFiles = @(),
        [string[]]$ExcludeDirectories = @(),
        [int]$RetryCount = 5,
        [int]$RetryWaitSeconds = 2,
        [switch]$Mirror
    )

    if (-not (Test-Path -Path $Source -PathType Container)) {
        throw "Source directory '$Source' does not exist."
    }

    if (-not (Test-Path -Path $Destination -PathType Container)) {
        New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    }

    $arguments = @(
        $Source,
        $Destination,
        "*",
        "/COPY:DAT",
        "/DCOPY:DAT",
        "/R:$RetryCount",
        "/W:$RetryWaitSeconds",
        "/NFL",
        "/NDL",
        "/NJH",
        "/NJS",
        "/NP"
    )

    if ($Mirror) {
        $arguments += "/MIR"
    }
    else {
        $arguments += "/E"
    }

    if ($ExcludeFiles.Count -gt 0) {
        $arguments += "/XF"
        $arguments += $ExcludeFiles
    }

    if ($ExcludeDirectories.Count -gt 0) {
        $arguments += "/XD"
        $arguments += $ExcludeDirectories
    }

    Invoke-NativeCommand -FilePath "robocopy" -ArgumentList $arguments -SuccessExitCodes @(0, 1, 2, 3, 4, 5, 6, 7)
}

function Wait-ApplicationHealth {
    param(
        [string]$Url,
        [int]$Attempts,
        [int]$DelaySeconds
    )

    Write-Step "Verifying application health"
    $lastFailure = $null

    for ($attempt = 1; $attempt -le $Attempts; $attempt++) {
        try {
            $response = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 5
            if ([int]$response.StatusCode -ge 200 -and [int]$response.StatusCode -lt 400) {
                Write-Host ("Application health check passed: {0}" -f $Url)
                return
            }

            $lastFailure = "HTTP $([int]$response.StatusCode)"
        }
        catch {
            $lastFailure = $_.Exception.Message
        }

        if ($attempt -lt $Attempts) {
            Start-Sleep -Seconds $DelaySeconds
        }
    }

    throw ("Application health check failed after {0} attempts: {1}. Last failure: {2}" -f $Attempts, $Url, $lastFailure)
}

function Get-PreservePatternsToApply {
    param(
        [string]$Destination,
        [string[]]$Patterns
    )

    if (-not (Test-Path -Path $Destination -PathType Container)) {
        return @()
    }

    $preserve = New-Object System.Collections.Generic.List[string]

    foreach ($pattern in $Patterns) {
        $existingFiles = Get-ChildItem -Path $Destination -Filter $pattern -File -ErrorAction SilentlyContinue
        if ($existingFiles) {
            $preserve.Add($pattern)
        }
    }

    return $preserve.ToArray()
}

$projectFullPath = Resolve-ScriptPath -Path $ProjectPath
$outputFullPath = Resolve-ScriptPath -Path $OutputDirectory
$stagingRoot = Join-Path -Path $env:TEMP -ChildPath ("OrionERP-publish-" + [Guid]::NewGuid().ToString("N"))
$stagingOutputPath = Join-Path -Path $stagingRoot -ChildPath "publish"
$backupOutputPath = Join-Path -Path $stagingRoot -ChildPath "backup"

$service = if ($SkipServiceControl) { $null } else { Get-OrionService -Name $ServiceName }
$serviceWasRunning = $service -and $service.Status -ne "Stopped"
$serviceStoppedByScript = $false
$backupCreated = $false
$productionCopyStarted = $false

try {
    Assert-CommandExists -CommandName "dotnet"
    Assert-CommandExists -CommandName "robocopy"

    if (-not (Test-Path -Path $projectFullPath -PathType Leaf)) {
        throw "Project file '$projectFullPath' does not exist."
    }

    if (-not $SkipServiceControl -and $service -and -not (Test-IsAdministrator)) {
        throw "This PowerShell session is not elevated. Run it as Administrator to stop/start service '$ServiceName', or rerun with -SkipServiceControl only if you will handle the service manually."
    }

    New-Item -ItemType Directory -Path $stagingOutputPath -Force | Out-Null

    Write-Step "Publishing project to staging"
    Invoke-NativeCommand -FilePath "dotnet" -ArgumentList @(
        "publish",
        $projectFullPath,
        "-c", "Release",
        "-r", $Runtime,
        "--self-contained", "false",
        "--nologo",
        "--verbosity", "minimal",
        "-o", $stagingOutputPath
    )

    if (-not $SkipServiceControl) {
        if ($serviceWasRunning) {
            $serviceStoppedByScript = Stop-OrionService -Name $ServiceName
        }
        elseif ($service) {
            Write-Host "Service '$ServiceName' is already stopped."
        }
        else {
            Write-Warning "Service '$ServiceName' not found. Continuing without service control."
        }
    }

    if (Test-Path -Path $outputFullPath -PathType Container) {
        Write-Step "Backing up current deployment"
        Copy-Directory -Source $outputFullPath -Destination $backupOutputPath -RetryCount $CopyRetries -RetryWaitSeconds $CopyRetryWaitSeconds -Mirror
        $backupCreated = $true
    }
    else {
        New-Item -ItemType Directory -Path $outputFullPath -Force | Out-Null
    }

    $preservePatterns = @(Get-PreservePatternsToApply -Destination $outputFullPath -Patterns $PreserveFilePatterns)
    if ($preservePatterns.Count -gt 0) {
        Write-Host ("Preserving existing files matching: {0}" -f ($preservePatterns -join ", "))
    }

    Write-Step "Copying staged build to production"
    $productionCopyStarted = $true
    Copy-Directory `
        -Source $stagingOutputPath `
        -Destination $outputFullPath `
        -ExcludeFiles $preservePatterns `
        -ExcludeDirectories $PreserveDirectoryPatterns `
        -RetryCount $CopyRetries `
        -RetryWaitSeconds $CopyRetryWaitSeconds `
        -Mirror

    if ($serviceStoppedByScript) {
        Start-OrionService -Name $ServiceName
    }

    if (-not [string]::IsNullOrWhiteSpace($HealthCheckUrl)) {
        if ($SkipServiceControl) {
            Write-Warning "Skipping health verification because -SkipServiceControl was used."
        }
        elseif ($serviceWasRunning) {
            Wait-ApplicationHealth -Url $HealthCheckUrl -Attempts $HealthCheckAttempts -DelaySeconds $HealthCheckDelaySeconds
        }
        else {
            Write-Warning "Skipping health verification because service '$ServiceName' was not running before deployment."
        }
    }

    Write-Step "Deployment completed successfully"
}
catch {
    if ($productionCopyStarted -and $backupCreated) {
        Write-Warning "Deployment failed after production files were touched. Restoring the previous deployment."

        try {
            if (-not $SkipServiceControl) {
                $rollbackService = Get-OrionService -Name $ServiceName
                if ($rollbackService -and $rollbackService.Status -ne "Stopped") {
                    Stop-OrionService -Name $ServiceName | Out-Null
                }
            }

            Copy-Directory -Source $backupOutputPath -Destination $outputFullPath -RetryCount $CopyRetries -RetryWaitSeconds $CopyRetryWaitSeconds -Mirror
        }
        catch {
            Write-Warning ("Rollback failed: {0}" -f $_.Exception.Message)
        }
    }

    if ($serviceWasRunning -and -not $SkipServiceControl) {
        try {
            Start-OrionService -Name $ServiceName
        }
        catch {
            Write-Warning ("Service restart failed: {0}" -f $_.Exception.Message)
        }
    }

    throw
}
finally {
    Remove-Item -Path $stagingRoot -Recurse -Force -ErrorAction SilentlyContinue
}
