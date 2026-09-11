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
    # Probe a readiness endpoint rather than "/", which redirects to the login page.
    # Rendering that form over plain HTTP fails once antiforgery cookies require a
    # secure request, failing the health check and rolling back a good deployment.
    [string]$HealthCheckUrl = "http://127.0.0.1:5000/readyz",
    [int]$HealthCheckAttempts = 15,
    [int]$HealthCheckDelaySeconds = 2,
    [scriptblock]$HealthCheckValidator = $null,
    [string[]]$AdditionalPublishArguments = @(),
    # Non-secret, target-specific settings materialized after the staged copy.
    # Environment variables remain higher precedence at runtime.
    [object]$InstanceSettings = $null,
    [string]$InstanceSettingsPath = "",
    [string]$InstanceProfileValidatorAssembly = "",
    [switch]$SkipServiceControl,
    [switch]$AllowNonMain,
    [switch]$AllowDirty,
    [switch]$RollbackToPreviousRelease,
    [switch]$ValidateOnly
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
        [int]$DelaySeconds,
        [scriptblock]$Validator
    )

    Write-Step "Verifying application health"
    $lastFailure = $null

    for ($attempt = 1; $attempt -le $Attempts; $attempt++) {
        try {
            $response = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 5
            if ([int]$response.StatusCode -ge 200 -and [int]$response.StatusCode -lt 400) {
                if ($null -ne $Validator) {
                    $isValid = & $Validator $response
                    if (-not $isValid) {
                        throw "The health response did not satisfy the application-specific safety contract."
                    }
                }

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

function Assert-LoopbackHealthCheckUrl {
    param([string]$Url)

    try {
        $uri = [Uri]::new($Url, [UriKind]::Absolute)
    }
    catch {
        throw "HealthCheckUrl must be an absolute HTTP URL on loopback."
    }

    $address = $null
    $isLoopbackHost = $uri.Host.Equals("localhost", [StringComparison]::OrdinalIgnoreCase) -or
        ([Net.IPAddress]::TryParse($uri.Host, [ref]$address) -and [Net.IPAddress]::IsLoopback($address))
    if ($uri.Scheme -notin @("http", "https") -or -not $isLoopbackHost) {
        throw "HealthCheckUrl must use HTTP or HTTPS on a loopback host; production readiness may not be delegated to an external endpoint."
    }
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

function Write-InstanceSettings {
    param(
        [string]$Destination,
        [object]$Settings
    )

    if ($null -eq $Settings) {
        return
    }

    $destinationRoot = [System.IO.Path]::GetFullPath($Destination).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    $settingsPath = [System.IO.Path]::GetFullPath(
        (Join-Path -Path $destinationRoot -ChildPath "appsettings.Instance.json"))
    if (-not $settingsPath.StartsWith(
            $destinationRoot + [System.IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Instance settings path resolved outside the deployment directory."
    }

    $json = $Settings | ConvertTo-Json -Depth 20
    [System.IO.File]::WriteAllText(
        $settingsPath,
        $json,
        [System.Text.UTF8Encoding]::new($false))
    Write-Host "Wrote explicit target configuration: appsettings.Instance.json"
}

function Assert-ObjectSchema {
    param(
        [object]$Value,
        [string]$Path,
        [string[]]$Allowed,
        [string[]]$Required = @()
    )

    if ($null -eq $Value -or $Value -is [string] -or $Value -is [array] -or $Value.GetType().IsValueType) {
        throw "$Path must be a JSON object."
    }

    $names = @($Value.PSObject.Properties.Name)
    foreach ($name in $names) {
        if ($Allowed -inotcontains $name) {
            throw "$Path contains the unsupported property '$name'. Instance profiles use a closed, public-only schema."
        }
    }
    foreach ($name in $Required) {
        if ($names -inotcontains $name) {
            throw "$Path is missing the required property '$name'."
        }
    }
}

function Assert-ArrayItemSchema {
    param(
        [object]$Value,
        [string]$Path,
        [string[]]$Allowed,
        [string[]]$Required = $Allowed
    )

    if ($null -eq $Value -or $Value -isnot [array]) {
        throw "$Path must be a JSON array."
    }

    $index = 0
    foreach ($item in @($Value)) {
        Assert-ObjectSchema -Value $item -Path "$Path[$index]" -Allowed $Allowed -Required $Required
        $index++
    }
}

function Assert-InstanceSettings {
    param([object]$Settings)

    if ($null -eq $Settings) {
        return
    }

    $publicWebsiteFields = @(
        "PublicSiteKey", "ExpectedCompanyRfc", "SiteKey", "ModuleCode", "CanonicalHost", "LoopbackPort"
    )
    $presentationFields = @(
        "PublicSiteKey", "BrandingVersion", "ContentVersion", "PublicName", "ShortName",
        "MembershipProgramName", "LegalName", "LocationName", "Tagline", "FooterSummary",
        "SeoDescription", "Locale", "PublicEmail", "PhoneE164", "PhoneDisplay", "WhatsAppE164",
        "WhatsAppDisplay", "OperatingAddress", "FiscalAddress", "PrivacyEmail", "PrivacyVersion",
        "PrivacyUpdatedDisplay", "TermsVersion", "TermsUpdatedDisplay", "PrimaryColor",
        "PrimaryDarkColor", "AccentColor", "Assets"
    )

    Assert-ObjectSchema `
        -Value $Settings.PublicWebsite `
        -Path "PublicWebsite" `
        -Allowed $publicWebsiteFields `
        -Required $publicWebsiteFields
    Assert-ObjectSchema `
        -Value $Settings.PublicWebsitePresentation `
        -Path "PublicWebsitePresentation" `
        -Allowed $presentationFields `
        -Required $presentationFields

    $moduleCode = [string]$Settings.PublicWebsite.ModuleCode
    $rootFields = switch -CaseSensitive ($moduleCode) {
        "HOSPITALITY" { @("PublicWebsite", "PublicWebsitePresentation", "HospitalityWebsite", "PublicIntegrations:Mail") }
        "RESTAURANT" { @("PublicWebsite", "PublicWebsitePresentation", "PublicIntegrations:Mail") }
        default { throw "The instance settings profile has an unsupported PublicWebsite.ModuleCode '$moduleCode'." }
    }
    Assert-ObjectSchema -Value $Settings -Path "Instance settings" -Allowed $rootFields -Required $rootFields

    if ([string]::IsNullOrWhiteSpace([string]$Settings.PublicWebsite.PublicSiteKey) -or
        [string]::IsNullOrWhiteSpace([string]$Settings.PublicWebsite.ExpectedCompanyRfc) -or
        [string]::IsNullOrWhiteSpace([string]$Settings.PublicWebsite.SiteKey) -or
        [string]::IsNullOrWhiteSpace([string]$Settings.PublicWebsite.CanonicalHost) -or
        [int]$Settings.PublicWebsite.LoopbackPort -le 0) {
        throw "The instance settings profile has an incomplete PublicWebsite identity."
    }
    if ([string]$Settings.PublicWebsite.PublicSiteKey -cne [string]$Settings.PublicWebsitePresentation.PublicSiteKey -or
        [long]$Settings.PublicWebsitePresentation.BrandingVersion -le 0 -or
        [long]$Settings.PublicWebsitePresentation.ContentVersion -le 0) {
        throw "The presentation identity or versions do not match the public website."
    }

    $assets = $Settings.PublicWebsitePresentation.Assets
    if ($null -eq $assets -or $assets -is [string] -or $assets -is [array] -or $assets.GetType().IsValueType) {
        throw "PublicWebsitePresentation.Assets must be a JSON object."
    }
    foreach ($asset in $assets.PSObject.Properties) {
        if ($asset.Value -isnot [string]) {
            throw "PublicWebsitePresentation.Assets.$($asset.Name) must be a string path."
        }
    }

    if ($moduleCode -ceq "HOSPITALITY") {
        $hospitalityFields = @(
            "ReservationSourceLabel", "AccountingAccount", "PdfFilePrefix", "CheckInDisplay",
            "CheckOutDisplay", "LateCheckoutWindowDisplay", "LateCheckoutFeeDisplay",
            "CancellationAdvanceDays", "RefundPercent", "LostPropertyRetentionDays",
            "HomeGalleryAssetKeys", "BuildingGalleryAssetKeys", "GuestProfiles", "Services",
            "FeaturedExtras", "Faqs", "Rooms"
        )
        Assert-ObjectSchema `
            -Value $Settings.HospitalityWebsite `
            -Path "HospitalityWebsite" `
            -Allowed $hospitalityFields `
            -Required $hospitalityFields
        Assert-ArrayItemSchema `
            -Value $Settings.HospitalityWebsite.GuestProfiles `
            -Path "HospitalityWebsite.GuestProfiles" `
            -Allowed @("Title", "Text", "Icon")
        Assert-ArrayItemSchema `
            -Value $Settings.HospitalityWebsite.Services `
            -Path "HospitalityWebsite.Services" `
            -Allowed @("Title", "Text", "Icon")
        Assert-ArrayItemSchema `
            -Value $Settings.HospitalityWebsite.FeaturedExtras `
            -Path "HospitalityWebsite.FeaturedExtras" `
            -Allowed @("Code", "Aliases", "Name", "Detail", "MaxQuantity", "Icon")
        Assert-ArrayItemSchema `
            -Value $Settings.HospitalityWebsite.Faqs `
            -Path "HospitalityWebsite.Faqs" `
            -Allowed @("Category", "Question", "Answer")
        Assert-ArrayItemSchema `
            -Value $Settings.HospitalityWebsite.Rooms `
            -Path "HospitalityWebsite.Rooms" `
            -Allowed @(
                "RoomCode", "Aliases", "Tag", "Ideal", "Capacity", "Bedrooms", "Bathrooms",
                "PrimaryAssetKey", "GalleryAssetKeys"
            )
    }

    Assert-ObjectSchema `
        -Value $Settings.'PublicIntegrations:Mail' `
        -Path "PublicIntegrations:Mail" `
        -Allowed @("SenderAddress") `
        -Required @("SenderAddress")
}

function Read-InstanceSettings {
    param([string]$Path)

    $profilePath = Resolve-ScriptPath -Path $Path
    $repositoryRoot = [System.IO.Path]::GetFullPath($PSScriptRoot).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    if (-not $profilePath.StartsWith(
            $repositoryRoot + [System.IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "The instance settings profile resolved outside the repository."
    }
    if (-not (Test-Path -LiteralPath $profilePath -PathType Leaf)) {
        throw "Instance settings profile '$profilePath' does not exist."
    }

    $raw = Get-Content -LiteralPath $profilePath -Raw
    if ($raw -match '"(?:ConnectionStrings|Password|ClientSecret|SecretKey|ApiKey|TenantId|ClientId|PayPalClientId)"\s*:') {
        throw "Instance settings profiles may contain public configuration only; a secret-bearing field was found."
    }

    try {
        $settings = $raw | ConvertFrom-Json
    }
    catch {
        throw "Instance settings profile '$profilePath' is not valid JSON: $($_.Exception.Message)"
    }

    return $settings
}

function Test-InstanceProfileArtifact {
    param(
        [string]$PublishDirectory,
        [string]$ValidatorAssembly
    )

    $assemblyPath = Join-Path -Path $PublishDirectory -ChildPath $ValidatorAssembly
    if (-not (Test-Path -LiteralPath $assemblyPath -PathType Leaf)) {
        throw "Instance profile validator assembly '$ValidatorAssembly' was not produced by dotnet publish."
    }

    Write-Step "Validating the complete instance profile before touching production"

    # The publisher itself may run inside a development host that exports
    # ASPNETCORE_URLS or instance-specific configuration. Those values must not
    # mask the staged appsettings.Instance.json during preflight. Snapshot only
    # relevant process variables, isolate the child validation, then restore
    # every value without logging it (some Graph overrides may be secrets).
    $overridePattern = '^(?:(?:ASPNETCORE_|DOTNET_)?(?:PublicWebsite|PublicWebsitePresentation|HospitalityWebsite|HospitalityCheckout|BonhomiaCheckout|PublicIntegrations|BrunoGraphMail|BonhomiaGraphMail)(?:__.*)?|(?:ASPNETCORE_|DOTNET_)?(?:Urls|HTTP_PORTS|HTTPS_PORTS|Kestrel)(?:__.*)?)$'
    $regexOptions = [Text.RegularExpressions.RegexOptions]::IgnoreCase -bor
        [Text.RegularExpressions.RegexOptions]::CultureInvariant
    $processEnvironment = [Environment]::GetEnvironmentVariables(
        [EnvironmentVariableTarget]::Process)
    $presentNames = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    $affectedNames = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)

    foreach ($key in $processEnvironment.Keys) {
        $name = [string]$key
        [void]$presentNames.Add($name)
        if ([Text.RegularExpressions.Regex]::IsMatch($name, $overridePattern, $regexOptions)) {
            [void]$affectedNames.Add($name)
        }
    }
    [void]$affectedNames.Add("ASPNETCORE_ENVIRONMENT")
    [void]$affectedNames.Add("DOTNET_ENVIRONMENT")

    $environmentSnapshot = @($affectedNames | ForEach-Object {
        [pscustomobject]@{
            Name = $_
            WasPresent = $presentNames.Contains($_)
            Value = [Environment]::GetEnvironmentVariable(
                $_,
                [EnvironmentVariableTarget]::Process)
        }
    })

    Push-Location -LiteralPath $PublishDirectory
    try {
        foreach ($name in $affectedNames) {
            [Environment]::SetEnvironmentVariable(
                $name,
                [System.Management.Automation.Language.NullString]::Value,
                [EnvironmentVariableTarget]::Process)
        }
        [Environment]::SetEnvironmentVariable(
            "ASPNETCORE_ENVIRONMENT",
            "Production",
            [EnvironmentVariableTarget]::Process)
        [Environment]::SetEnvironmentVariable(
            "DOTNET_ENVIRONMENT",
            "Production",
            [EnvironmentVariableTarget]::Process)

        Invoke-NativeCommand `
            -FilePath "dotnet" `
            -ArgumentList @($assemblyPath, "--validate-instance-profile=true")
    }
    finally {
        foreach ($entry in $environmentSnapshot) {
            if ($entry.WasPresent) {
                [Environment]::SetEnvironmentVariable(
                    $entry.Name,
                    $entry.Value,
                    [EnvironmentVariableTarget]::Process)
            }
            else {
                [Environment]::SetEnvironmentVariable(
                    $entry.Name,
                    [System.Management.Automation.Language.NullString]::Value,
                    [EnvironmentVariableTarget]::Process)
            }
        }
        Pop-Location
    }
}

function Save-PreviousRelease {
    param(
        [string]$Source,
        [string]$Destination,
        [string]$Name,
        [string[]]$PreservedDirectories,
        [int]$RetryCount,
        [int]$RetryWaitSeconds
    )

    Write-Step "Retaining the previous release for the presentation rollback window"
    Copy-Directory `
        -Source $Source `
        -Destination $Destination `
        -ExcludeFiles @("appsettings*.json", "rollback-manifest.json") `
        -ExcludeDirectories $PreservedDirectories `
        -RetryCount $RetryCount `
        -RetryWaitSeconds $RetryWaitSeconds `
        -Mirror

    $sourceInstance = Join-Path -Path $Source -ChildPath "appsettings.Instance.json"
    $retainedInstance = Join-Path -Path $Destination -ChildPath "appsettings.Instance.json"
    $hasInstanceSettings = Test-Path -LiteralPath $sourceInstance -PathType Leaf
    if ($hasInstanceSettings) {
        Copy-Item -LiteralPath $sourceInstance -Destination $retainedInstance -Force
    }
    elseif (Test-Path -LiteralPath $retainedInstance -PathType Leaf) {
        Remove-Item -LiteralPath $retainedInstance -Force
    }

    $manifest = [ordered]@{
        Version = 1
        ServiceName = $Name
        CapturedAtUtc = [DateTime]::UtcNow.ToString("O")
        HasInstanceSettings = $hasInstanceSettings
    }
    $manifestPath = Join-Path -Path $Destination -ChildPath "rollback-manifest.json"
    [System.IO.File]::WriteAllText(
        $manifestPath,
        ($manifest | ConvertTo-Json -Depth 5),
        [System.Text.UTF8Encoding]::new($false))
    Write-Host "Retained one previous release. Keep it until the database presentation transition is finalized."
}

if ($null -ne $InstanceSettings -and -not [string]::IsNullOrWhiteSpace($InstanceSettingsPath)) {
    throw "Use InstanceSettings or InstanceSettingsPath, not both."
}
if ($RollbackToPreviousRelease -and $ValidateOnly) {
    throw "RollbackToPreviousRelease and ValidateOnly cannot be used together."
}
if (-not $ValidateOnly -and -not $SkipServiceControl) {
    if ([string]::IsNullOrWhiteSpace($HealthCheckUrl)) {
        throw "A loopback HealthCheckUrl is required when the script controls the production service."
    }
    Assert-LoopbackHealthCheckUrl -Url $HealthCheckUrl
}
if (-not $RollbackToPreviousRelease) {
    if (-not [string]::IsNullOrWhiteSpace($InstanceSettingsPath)) {
        $InstanceSettings = Read-InstanceSettings -Path $InstanceSettingsPath
    }
    elseif ($null -ne $InstanceSettings) {
        try {
            $InstanceSettings = $InstanceSettings | ConvertTo-Json -Depth 20 | ConvertFrom-Json
        }
        catch {
            throw "InstanceSettings could not be normalized as JSON: $($_.Exception.Message)"
        }
    }
    Assert-InstanceSettings -Settings $InstanceSettings
}

$projectFullPath = Resolve-ScriptPath -Path $ProjectPath
$allowedOutputRootFullPath = [System.IO.Path]::GetFullPath(
    "C:\Users\Orion\Grupo Carpio Dropbox\Grupo Orion\Software\GitHubs\Production")
$outputFullPath = Resolve-OrionSafeChildDirectory `
    -Path (Resolve-ScriptPath -Path $OutputDirectory) `
    -AllowedRoot $allowedOutputRootFullPath `
    -Label "OutputDirectory"
if ($ServiceName -notmatch '^[A-Za-z0-9._-]{1,100}$') {
    throw "ServiceName contains characters that are unsafe for a release directory."
}
$rollbackFullPath = Resolve-OrionSafeChildDirectory `
    -Path (Join-Path -Path $allowedOutputRootFullPath -ChildPath "_rollback\$ServiceName\previous") `
    -AllowedRoot $allowedOutputRootFullPath `
    -Label "Rollback release directory"
$tempRoot = [System.IO.Path]::GetFullPath($env:TEMP)
$stagingRoot = Resolve-OrionSafeChildDirectory `
    -Path (Join-Path -Path $tempRoot -ChildPath ("OrionERP-publish-" + [Guid]::NewGuid().ToString("N"))) `
    -AllowedRoot $tempRoot `
    -Label "Temporary staging directory"
$stagingOutputPath = Join-Path -Path $stagingRoot -ChildPath "publish"
$backupOutputPath = Join-Path -Path $stagingRoot -ChildPath "backup"

$service = if ($SkipServiceControl -or $ValidateOnly) { $null } else { Get-OrionService -Name $ServiceName }
$serviceWasRunning = $service -and $service.Status -ne "Stopped"
$serviceStoppedByScript = $false
$backupCreated = $false
$productionCopyStarted = $false

try {
    Assert-CommandExists -CommandName "dotnet"
    if (-not $ValidateOnly) {
        Assert-CommandExists -CommandName "robocopy"
    }

    if ($RollbackToPreviousRelease -and $SkipServiceControl) {
        throw "RollbackToPreviousRelease requires service control so the retained release can be restored and verified atomically."
    }
    if (-not $RollbackToPreviousRelease) {
        Assert-CommandExists -CommandName "git"
        Assert-OrionProductionGitState `
            -RepositoryRoot $PSScriptRoot `
            -AllowNonMain:$AllowNonMain `
            -AllowDirty:$AllowDirty
        if (-not (Test-Path -LiteralPath $projectFullPath -PathType Leaf)) {
            throw "Project file '$projectFullPath' does not exist."
        }
    }

    if (-not $ValidateOnly -and -not $SkipServiceControl -and -not $service) {
        throw "Required Windows service '$ServiceName' was not found. Use -SkipServiceControl only when service lifecycle and validation are handled separately."
    }
    if (-not $ValidateOnly -and -not $SkipServiceControl -and -not (Test-IsAdministrator)) {
        throw "This PowerShell session is not elevated. Run it as Administrator to stop/start service '$ServiceName', or rerun with -SkipServiceControl only if you will handle the service manually."
    }
    if (-not $ValidateOnly -and -not $SkipServiceControl -and -not $serviceWasRunning) {
        throw "Coordinated publishing requires service '$ServiceName' to be running so the new release can be restarted and verified. Use -SkipServiceControl only for an intentionally external lifecycle."
    }

    if ($RollbackToPreviousRelease) {
        if (-not $serviceWasRunning) {
            throw "RollbackToPreviousRelease requires service '$ServiceName' to be running so readiness can be verified."
        }
        $manifestPath = Join-Path -Path $rollbackFullPath -ChildPath "rollback-manifest.json"
        if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
            throw "No retained previous release exists for service '$ServiceName'."
        }
        $rollbackManifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        if ([int]$rollbackManifest.Version -ne 1 -or
            [string]$rollbackManifest.ServiceName -cne $ServiceName) {
            throw "The retained release manifest does not belong to service '$ServiceName'."
        }
        if (-not (Test-Path -LiteralPath $outputFullPath -PathType Container)) {
            throw "The current deployment directory does not exist; there is nothing to roll back safely."
        }

        New-Item -ItemType Directory -Path $backupOutputPath -Force | Out-Null
        Write-Step "Backing up the current release before rollback"
        Copy-Directory `
            -Source $outputFullPath `
            -Destination $backupOutputPath `
            -RetryCount $CopyRetries `
            -RetryWaitSeconds $CopyRetryWaitSeconds `
            -Mirror
        $backupCreated = $true

        $serviceStoppedByScript = Stop-OrionService -Name $ServiceName
        Write-Step "Restoring the retained previous release"
        $productionCopyStarted = $true
        Copy-Directory `
            -Source $rollbackFullPath `
            -Destination $outputFullPath `
            -ExcludeFiles @("appsettings*.json", "rollback-manifest.json") `
            -ExcludeDirectories $PreserveDirectoryPatterns `
            -RetryCount $CopyRetries `
            -RetryWaitSeconds $CopyRetryWaitSeconds `
            -Mirror

        $retainedInstance = Join-Path -Path $rollbackFullPath -ChildPath "appsettings.Instance.json"
        $targetInstance = Join-Path -Path $outputFullPath -ChildPath "appsettings.Instance.json"
        if ([bool]$rollbackManifest.HasInstanceSettings) {
            if (-not (Test-Path -LiteralPath $retainedInstance -PathType Leaf)) {
                throw "The retained release manifest requires appsettings.Instance.json, but the file is missing."
            }
            Copy-Item -LiteralPath $retainedInstance -Destination $targetInstance -Force
        }
        elseif (Test-Path -LiteralPath $targetInstance -PathType Leaf) {
            Remove-Item -LiteralPath $targetInstance -Force
        }

        Start-OrionService -Name $ServiceName
        Wait-ApplicationHealth `
            -Url $HealthCheckUrl `
            -Attempts $HealthCheckAttempts `
            -DelaySeconds $HealthCheckDelaySeconds `
            -Validator $HealthCheckValidator
        Write-Step "Previous release restored and ready"
        Write-Host "Now confirm the presentation rollback in OrionERP before its database fallback window expires."
        return
    }

    New-Item -ItemType Directory -Path $stagingOutputPath -Force | Out-Null

    Write-Step "Publishing project to staging"
    $publishArguments = @(
        "publish",
        $projectFullPath,
        "-c", "Release",
        "-r", $Runtime,
        "--self-contained", "false",
        "--nologo",
        "--verbosity", "minimal",
        "-o", $stagingOutputPath
    )
    $publishArguments += $AdditionalPublishArguments
    Invoke-NativeCommand -FilePath "dotnet" -ArgumentList $publishArguments

    if ($null -ne $InstanceSettings) {
        Write-InstanceSettings -Destination $stagingOutputPath -Settings $InstanceSettings
        $validatorAssembly = if ([string]::IsNullOrWhiteSpace($InstanceProfileValidatorAssembly)) {
            [System.IO.Path]::GetFileNameWithoutExtension($projectFullPath) + ".dll"
        }
        else {
            $InstanceProfileValidatorAssembly
        }
        if ([System.IO.Path]::GetFileName($validatorAssembly) -cne $validatorAssembly -or
            -not $validatorAssembly.EndsWith(".dll", [StringComparison]::OrdinalIgnoreCase)) {
            throw "InstanceProfileValidatorAssembly must be one DLL file name produced in the publish root."
        }
        Test-InstanceProfileArtifact `
            -PublishDirectory $stagingOutputPath `
            -ValidatorAssembly $validatorAssembly
    }

    if ($ValidateOnly) {
        Write-Step "Publish and instance-profile validation completed without touching production"
        return
    }

    if (-not $SkipServiceControl) {
        if ($serviceWasRunning) {
            $serviceStoppedByScript = Stop-OrionService -Name $ServiceName
        }
        elseif ($service) {
            Write-Host "Service '$ServiceName' is already stopped."
        }
    }

    if (Test-Path -LiteralPath $outputFullPath -PathType Container) {
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

    Write-InstanceSettings -Destination $outputFullPath -Settings $InstanceSettings

    if ($serviceStoppedByScript) {
        Start-OrionService -Name $ServiceName
    }

    if (-not [string]::IsNullOrWhiteSpace($HealthCheckUrl)) {
        if ($SkipServiceControl) {
            Write-Warning "Skipping health verification because -SkipServiceControl was used."
        }
        elseif ($serviceWasRunning) {
            Wait-ApplicationHealth `
                -Url $HealthCheckUrl `
                -Attempts $HealthCheckAttempts `
                -DelaySeconds $HealthCheckDelaySeconds `
                -Validator $HealthCheckValidator
        }
        else {
            Write-Warning "Skipping health verification because service '$ServiceName' was not running before deployment."
        }
    }

    if ($backupCreated) {
        Save-PreviousRelease `
            -Source $backupOutputPath `
            -Destination $rollbackFullPath `
            -Name $ServiceName `
            -PreservedDirectories $PreserveDirectoryPatterns `
            -RetryCount $CopyRetries `
            -RetryWaitSeconds $CopyRetryWaitSeconds
    }
    else {
        Write-Warning "This was a first deployment, so no previous release could be retained for rollback."
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
    $validatedStagingRoot = Resolve-OrionSafeChildDirectory `
        -Path $stagingRoot `
        -AllowedRoot $tempRoot `
        -Label "Temporary staging directory"
    Remove-Item -LiteralPath $validatedStagingRoot -Recurse -Force -ErrorAction SilentlyContinue
}
