#requires -Version 7.0
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$ServiceName = "OrionERP.Training",
    [string]$ExecutablePath = "C:\Users\Orion\Grupo Carpio Dropbox\Grupo Orion\Software\GitHubs\Production\OrionERP.Training\OrionERP.Web.exe",
    [string]$ConnectionString = $env:ORION_TRAINING_ConnectionStrings__OrionDb,
    [string]$WindowsServiceUrl = "http://localhost:5030",
    [string]$AllowedHosts = "localhost;127.0.0.1;capacitacion.orion.land",
    [string]$PublicTrainingOrigin = "https://capacitacion.orion.land",
    [string]$DataProtectionKeyDirectory = "$env:ProgramData\Grupo Orion\OrionERP.Training\DataProtectionKeys",
    [string]$ProductionDataProtectionKeyDirectory = "C:\Users\Orion\Grupo Carpio Dropbox\Grupo Orion\Software\GitHubs\Production\OrionERP\App_Data\keys",
    [string]$LogPath,
    [switch]$Restart
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

if (-not [string]::IsNullOrWhiteSpace($LogPath)) {
    Start-Transcript -LiteralPath ([IO.Path]::GetFullPath($LogPath)) -Force | Out-Null
}

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    return ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Wait-TrainingReadiness {
    param([Uri]$ServiceUri, [int]$Attempts = 30)

    $healthUrl = "http://127.0.0.1:$($ServiceUri.Port)/readyz"
    $lastFailure = "no response"
    foreach ($attempt in 1..$Attempts) {
        try {
            $ready = Invoke-RestMethod -Uri $healthUrl -TimeoutSec 10
            if ($ready.status -eq "ready" `
                -and $ready.environment -eq "Training" `
                -and $ready.database.catalog -eq "Orion_Training" `
                -and $ready.database.reachable -eq $true `
                -and $ready.training.mode -eq "production_clone" `
                -and $ready.training.existingUsersPreserved -eq $true) {
                Write-Host "OrionERP Training is ready on $healthUrl."
                return
            }
            $lastFailure = "readiness payload did not match the production-clone contract"
        }
        catch {
            $lastFailure = $_.Exception.Message
        }

        if ($attempt -lt $Attempts) { Start-Sleep -Seconds 2 }
    }

    throw "Training service failed readiness validation. Last failure: $lastFailure"
}

if (-not (Test-IsAdministrator)) {
    throw "Run this script from an elevated PowerShell session."
}
if (-not [string]::Equals($ServiceName, "OrionERP.Training", [StringComparison]::Ordinal)) {
    throw "This configuration is pinned to OrionERP.Training."
}
if ([string]::IsNullOrWhiteSpace($ConnectionString)) {
    # Reuse the normal OrionERP SQL credential without printing or copying it to
    # a repository file. Only the catalog is changed, so a production restore
    # keeps the same login SID and requires no post-clone provisioning.
    $productionServiceRegistryPath = "HKLM:\SYSTEM\CurrentControlSet\Services\OrionERP"
    $productionServiceProperties = Get-ItemProperty `
        -LiteralPath $productionServiceRegistryPath `
        -Name Environment `
        -ErrorAction SilentlyContinue
    $productionEnvironment = if (
        $productionServiceProperties `
            -and $productionServiceProperties.PSObject.Properties["Environment"]
    ) {
        $productionServiceProperties.Environment
    }
    else {
        @()
    }
    $productionConnectionEntry = @($productionEnvironment) |
        Where-Object {
            $_ -like "ASPNETCORE_ConnectionStrings__OrionDb=*" `
                -or $_ -like "DOTNET_ConnectionStrings__OrionDb=*"
        } |
        Select-Object -First 1
    $machineConnection = @(
        [Environment]::GetEnvironmentVariable(
            "ASPNETCORE_ConnectionStrings__OrionDb",
            [EnvironmentVariableTarget]::Machine),
        [Environment]::GetEnvironmentVariable(
            "DOTNET_ConnectionStrings__OrionDb",
            [EnvironmentVariableTarget]::Machine)
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        Select-Object -First 1
    $productionConnection = if ($productionConnectionEntry) {
        $productionConnectionEntry.Substring($productionConnectionEntry.IndexOf("=") + 1)
    }
    elseif ($machineConnection) {
        [string]$machineConnection
    }
    else {
        # Older OrionERP installations keep the same setting in the deployed
        # appsettings.json instead of the per-service Environment value.
        $productionSettingsPath = Join-Path `
            (Split-Path -Parent $ExecutablePath) `
            "..\OrionERP\appsettings.json"
        $productionSettingsPath = [IO.Path]::GetFullPath($productionSettingsPath)
        if (-not (Test-Path -LiteralPath $productionSettingsPath -PathType Leaf)) {
            throw "No OrionERP production connection configuration was found. Set ORION_TRAINING_ConnectionStrings__OrionDb or pass -ConnectionString."
        }

        $productionSettings = Get-Content -LiteralPath $productionSettingsPath -Raw |
            ConvertFrom-Json
        [string]$productionSettings.ConnectionStrings.OrionDb
    }
    if ([string]::IsNullOrWhiteSpace($productionConnection)) {
        throw "No OrionERP production connection configuration was found. Set ORION_TRAINING_ConnectionStrings__OrionDb or pass -ConnectionString."
    }

    $productionBuilder = [System.Data.SqlClient.SqlConnectionStringBuilder]::new(
        $productionConnection)
    $productionBuilder["Initial Catalog"] = "Orion_Training"
    $ConnectionString = $productionBuilder.ConnectionString
    $productionConnection = $null
    Write-Host "Using the normal OrionERP SQL credential with catalog Orion_Training."
}

try {
    $connectionBuilder = [System.Data.SqlClient.SqlConnectionStringBuilder]::new($ConnectionString)
}
catch [ArgumentException] {
    throw "Training service configuration rejected: the connection string is invalid."
}
if ([string]::IsNullOrWhiteSpace([string]$connectionBuilder["Data Source"])) {
    throw "Training service configuration rejected: Server or Data Source is required."
}
if (-not [string]::Equals(
    ([string]$connectionBuilder["Initial Catalog"]).Trim(),
    "Orion_Training",
    [StringComparison]::OrdinalIgnoreCase)) {
    throw "Training service configuration rejected: the database must be exactly Orion_Training."
}
if (-not [string]::IsNullOrWhiteSpace([string]$connectionBuilder["AttachDBFilename"])) {
    throw "Training service configuration rejected: AttachDBFilename is not allowed."
}
if (-not [Convert]::ToBoolean($connectionBuilder["Encrypt"])) {
    throw "Training service configuration rejected: Encrypt=True is required."
}
$connectionBuilder["Persist Security Info"] = $false
$connectionBuilder["Application Name"] = "OrionERP.Training"
$ConnectionString = $connectionBuilder.ConnectionString

$serviceUri = $null
if (-not [Uri]::TryCreate($WindowsServiceUrl, [UriKind]::Absolute, [ref]$serviceUri) `
    -or -not $serviceUri.IsLoopback `
    -or $serviceUri.Scheme -ne "http" `
    -or $serviceUri.AbsolutePath -ne "/" `
    -or $serviceUri.Port -le 0 `
    -or $serviceUri.Port -in @(5000, 5010, 5020)) {
    throw "WindowsServiceUrl must be a dedicated loopback HTTP origin."
}

$publicUri = $null
if (-not [Uri]::TryCreate($PublicTrainingOrigin, [UriKind]::Absolute, [ref]$publicUri) `
    -or $publicUri.Scheme -ne "https" `
    -or $publicUri.AbsolutePath -ne "/" `
    -or [string]::Equals($publicUri.Host, "orionerp.orion.land", [StringComparison]::OrdinalIgnoreCase)) {
    throw "PublicTrainingOrigin must be a dedicated HTTPS origin."
}
if (-not (($AllowedHosts -split "[;,]") -contains "127.0.0.1")) {
    throw "AllowedHosts must include 127.0.0.1 for readiness."
}

$resolvedExecutablePath = [IO.Path]::GetFullPath($ExecutablePath)
if (-not (Test-Path -LiteralPath $resolvedExecutablePath -PathType Leaf)) {
    throw "Training executable was not found at '$resolvedExecutablePath'. Publish it first."
}

# The clone contains values encrypted by the live application. Copying the same
# Data Protection key material lets Training read those copied values while its
# own cookies remain separate through their configured names.
$resolvedTrainingKeyDirectory = [IO.Path]::GetFullPath(
    [Environment]::ExpandEnvironmentVariables($DataProtectionKeyDirectory))
$resolvedProductionKeyDirectory = [IO.Path]::GetFullPath($ProductionDataProtectionKeyDirectory)
if (-not (Test-Path -LiteralPath $resolvedProductionKeyDirectory -PathType Container)) {
    throw "Production Data Protection keys were not found at '$resolvedProductionKeyDirectory'."
}
New-Item -ItemType Directory -Path $resolvedTrainingKeyDirectory -Force | Out-Null
Get-ChildItem -LiteralPath $resolvedProductionKeyDirectory -Filter "key-*.xml" -File |
    Copy-Item -Destination $resolvedTrainingKeyDirectory -Force

# Verify the cloned database before touching the service configuration. This
# accepts the same SQL login used by production; no special Training login or
# post-restore user provisioning is required.
$probe = [System.Data.SqlClient.SqlConnection]::new($ConnectionString)
try {
    $probe.Open()
    $command = $probe.CreateCommand()
    try {
        $command.CommandText = "SELECT DB_NAME();"
        $activeCatalog = [string]$command.ExecuteScalar()
        if (-not [string]::Equals($activeCatalog, "Orion_Training", [StringComparison]::OrdinalIgnoreCase)) {
            throw "The connection opened '$activeCatalog' instead of Orion_Training."
        }
    }
    finally { $command.Dispose() }
}
finally { $probe.Dispose() }

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if (-not $service) {
    if ($PSCmdlet.ShouldProcess($ServiceName, "Create Training Windows service")) {
        $service = New-Service `
            -Name $ServiceName `
            -BinaryPathName ('"{0}"' -f $resolvedExecutablePath) `
            -DisplayName "OrionERP Training" `
            -Description "OrionERP practice environment backed by an Orion_Training production clone" `
            -StartupType Automatic
    }
}
else {
    $serviceConfiguration = Get-CimInstance -ClassName Win32_Service -Filter "Name='$ServiceName'"
    $configuredExecutable = if ($serviceConfiguration.PathName -match '^"([^"]+)"') { $Matches[1] } else { $serviceConfiguration.PathName }
    if (-not [string]::Equals(
        [IO.Path]::GetFullPath($configuredExecutable),
        $resolvedExecutablePath,
        [StringComparison]::OrdinalIgnoreCase)) {
        throw "Existing service '$ServiceName' points to a different executable."
    }
}

$serviceRegistryPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
$serviceEnvironment = @(
    "ASPNETCORE_ENVIRONMENT=Training",
    "DOTNET_ENVIRONMENT=Training",
    "ORION_TRAINING_SERVICE=1",
    "ORION_TRAINING_AllowedHosts=$AllowedHosts",
    "ORION_TRAINING_ConnectionStrings__OrionDb=$ConnectionString",
    "ORION_TRAINING_Hosting__WindowsServiceUrl=$WindowsServiceUrl",
    "ORION_TRAINING_PlatformIsolation__DataProtectionApplicationName=OrionERP",
    "ORION_TRAINING_PlatformIsolation__DataProtectionKeyPath=$resolvedTrainingKeyDirectory",
    "ORION_TRAINING_Capacitacion__SandboxBaseUrl=$($publicUri.GetLeftPart([UriPartial]::Authority))"
)

if ($PSCmdlet.ShouldProcess($ServiceName, "Store Training service configuration")) {
    New-ItemProperty `
        -LiteralPath $serviceRegistryPath `
        -Name "Environment" `
        -PropertyType MultiString `
        -Value $serviceEnvironment `
        -Force | Out-Null

    Set-Service -Name $ServiceName -StartupType Automatic
    & sc.exe config $ServiceName start= delayed-auto depend= 'MSSQL$SQLEXPRESS' | Out-Null
    & sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/restart/120000 | Out-Null
    & sc.exe failureflag $ServiceName 1 | Out-Null
}

Write-Host "Configured $ServiceName for $([string]$connectionBuilder['Data Source']) / Orion_Training on port $($serviceUri.Port)."
Write-Host "The connection secret was stored without being displayed."

if ($Restart -and $PSCmdlet.ShouldProcess($ServiceName, "Restart Training service")) {
    $service = Get-Service -Name $ServiceName
    if ($service.Status -ne "Stopped") {
        Stop-Service -Name $ServiceName -Force
        $service.WaitForStatus("Stopped", [TimeSpan]::FromSeconds(30))
    }
    Start-Service -Name $ServiceName
    $service.WaitForStatus("Running", [TimeSpan]::FromSeconds(45))
    Wait-TrainingReadiness -ServiceUri $serviceUri
}
