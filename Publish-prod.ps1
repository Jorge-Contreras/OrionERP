# publish-prod.ps1
$ErrorActionPreference = 'Stop'

# Go to the folder where this script is located
Set-Location $PSScriptRoot

# Service name (as registered in Windows Services)
$serviceName = "OrionERP"

# Paths
$project = "src\OrionERP.Web\OrionERP.Web.csproj"
$outDir  = "C:\Users\Orion\Grupo Carpio Dropbox\Grupo Orion\Software\GitHubs\Production\OrionERP"

function Stop-OrionService {
    param([string]$Name)

    $svc = Get-Service -Name $Name -ErrorAction SilentlyContinue
    if (-not $svc) {
        Write-Warning "Service '$Name' not found. Skipping stop."
        return
    }

    if ($svc.Status -ne 'Stopped') {
        Write-Host "=== Stopping service: $Name ==="
        Stop-Service -Name $Name -Force
        $svc.WaitForStatus('Stopped','00:00:30')
        Write-Host "Service '$Name' stopped."
    } else {
        Write-Host "Service '$Name' is already stopped."
    }
}

function Start-OrionService {
    param([string]$Name)

    $svc = Get-Service -Name $Name -ErrorAction SilentlyContinue
    if (-not $svc) {
        Write-Warning "Service '$Name' not found. Skipping start."
        return
    }

    Write-Host "=== Starting service: $Name ==="
    Start-Service -Name $Name
    $svc.Refresh()
    $svc.WaitForStatus('Running','00:00:30')
    Write-Host "Service '$Name' is running."
}

# --- Stop service before publish ---
Stop-OrionService -Name $serviceName

Write-Host "=== Cleaning project ==="
dotnet clean $project

Write-Host "=== Publishing project ==="
dotnet publish $project `
    -c Release `
    -r win-x64 `
    --self-contained false `
    -o $outDir

# --- Start service after publish ---
Start-OrionService -Name $serviceName

Write-Host "=== Done ==="
