[CmdletBinding()]
param(
    [string]$ServiceName = "OrionERP.Training",
    [string]$OutputDirectory = "C:\Users\Orion\Grupo Carpio Dropbox\Grupo Orion\Software\GitHubs\Production\OrionERP.Training",
    [string]$Runtime = "win-x64",
    [string]$WindowsServiceUrl = "http://localhost:5030",
    [switch]$SkipServiceControl
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
Set-Location -LiteralPath $PSScriptRoot

$requiredServiceName = "OrionERP.Training"
$requiredOutputDirectory = [IO.Path]::GetFullPath(
    "C:\Users\Orion\Grupo Carpio Dropbox\Grupo Orion\Software\GitHubs\Production\OrionERP.Training")
if (-not [string]::Equals($ServiceName, $requiredServiceName, [StringComparison]::Ordinal)) {
    throw "Training publish is pinned to service '$requiredServiceName'."
}
if (-not [string]::Equals(
    [IO.Path]::GetFullPath($OutputDirectory),
    $requiredOutputDirectory,
    [StringComparison]::OrdinalIgnoreCase)) {
    throw "Training publish is pinned to its dedicated OrionERP.Training output directory."
}

$serviceUri = $null
if (-not [Uri]::TryCreate($WindowsServiceUrl, [UriKind]::Absolute, [ref]$serviceUri) `
    -or -not $serviceUri.IsLoopback `
    -or $serviceUri.Scheme -ne "http" `
    -or -not [string]::IsNullOrEmpty($serviceUri.UserInfo) `
    -or $serviceUri.AbsolutePath -ne "/" `
    -or -not [string]::IsNullOrEmpty($serviceUri.Query) `
    -or $serviceUri.Port -le 0 `
    -or $serviceUri.Port -in @(5000, 5010, 5020)) {
    throw "WindowsServiceUrl must use a non-production loopback port."
}

$serviceRegistryPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
$serviceRegistryExists = Test-Path -LiteralPath $serviceRegistryPath
if ($SkipServiceControl -and ($service -or $serviceRegistryExists)) {
    throw "SkipServiceControl is permitted only for a true first-stage install before OrionERP.Training exists."
}
if ($serviceRegistryExists) {
    $serviceConfiguration = Get-CimInstance -ClassName Win32_Service -Filter "Name='$ServiceName'"
    $configuredExecutable = if ($serviceConfiguration.PathName -match '^"([^"]+)"') {
        $Matches[1]
    }
    else {
        $serviceConfiguration.PathName
    }
    $requiredExecutable = Join-Path $requiredOutputDirectory "OrionERP.Web.exe"
    if (-not [string]::Equals(
        [IO.Path]::GetFullPath($configuredExecutable),
        [IO.Path]::GetFullPath($requiredExecutable),
        [StringComparison]::OrdinalIgnoreCase)) {
        throw "Existing Training service points outside the dedicated Training deployment."
    }

    $configuredEnvironment = (Get-ItemProperty -LiteralPath $serviceRegistryPath -Name Environment -ErrorAction SilentlyContinue).Environment
    $configuredUrlEntry = @($configuredEnvironment) |
        Where-Object { $_ -like "ORION_TRAINING_Hosting__WindowsServiceUrl=*" } |
        Select-Object -First 1
    if ($configuredUrlEntry) {
        $configuredUrl = $configuredUrlEntry.Substring($configuredUrlEntry.IndexOf("=") + 1)
        if (-not [string]::Equals($configuredUrl.TrimEnd('/'), $WindowsServiceUrl.TrimEnd('/'), [StringComparison]::OrdinalIgnoreCase)) {
            throw "Publish port does not match the service-scoped Training configuration."
        }
    }
}

if (-not $SkipServiceControl) {
    if (-not $service) {
        throw "Training service is not configured. For first install, publish with -SkipServiceControl, then run Configure-TrainingService.ps1 -Restart."
    }
}

$healthCheckUrl = "http://127.0.0.1:$($serviceUri.Port)/readyz"
$trainingReadinessValidator = {
    param($response)

    $ready = $response.Content | ConvertFrom-Json
    return $ready.status -eq "ready" `
        -and $ready.environment -eq "Training" `
        -and $ready.database.catalog -eq "Orion_Training" `
        -and $ready.database.trainingCatalogAllowed -eq $true `
        -and $ready.database.reachable -eq $true `
        -and $ready.training.active -eq $true `
        -and $ready.training.mode -eq "production_clone" `
        -and $ready.training.existingUsersPreserved -eq $true
}

$publishParameters = @{
    ServiceName = $ServiceName
    ProjectPath = "src\OrionERP.Web\OrionERP.Web.csproj"
    OutputDirectory = $OutputDirectory
    Runtime = $Runtime
    PreserveFilePatterns = @()
    PreserveDirectoryPatterns = @()
    HealthCheckUrl = $healthCheckUrl
    HealthCheckValidator = $trainingReadinessValidator
    HealthCheckAttempts = 20
    HealthCheckDelaySeconds = 3
}

if ($SkipServiceControl) {
    $publishParameters.SkipServiceControl = $true
}

# Publish-prod.ps1 provides staging, backup, rollback, service control, and
# bounded health checks. Training uses a different service/output/port and does
# not preserve appsettings files; its database secret belongs only in the
# service-scoped ORION_TRAINING_ configuration installed by
# Configure-TrainingService.ps1. The shared RFC encryption key is intentionally
# published so copied credentials remain readable inside the cloned database.
& (Join-Path -Path $PSScriptRoot -ChildPath "Publish-prod.ps1") @publishParameters
