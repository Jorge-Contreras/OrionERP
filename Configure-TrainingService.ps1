[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$ServiceName = "OrionERP.Training",
    [string]$ExecutablePath = "C:\Users\Orion\Grupo Carpio Dropbox\Grupo Orion\Software\GitHubs\Production\OrionERP.Training\OrionERP.Web.exe",
    [string]$WindowsServiceUrl = "http://localhost:5030",
    [string]$AllowedHosts = "localhost;127.0.0.1;capacitacion.orion.land",
    [string]$PublicTrainingOrigin = "https://capacitacion.orion.land",
    [string]$DataProtectionKeyDirectory = "$env:ProgramData\Grupo Orion\OrionERP.Training\DataProtectionKeys",
    [string]$ConnectionString = $env:ORION_TRAINING_ConnectionStrings__OrionDb,
    [switch]$Restart
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

if ($ServiceName -notmatch '^[A-Za-z0-9_.-]+$') {
    throw "ServiceName contains unsupported characters."
}

$requiredServiceName = "OrionERP.Training"
$requiredExecutablePath = [IO.Path]::GetFullPath(
    "C:\Users\Orion\Grupo Carpio Dropbox\Grupo Orion\Software\GitHubs\Production\OrionERP.Training\OrionERP.Web.exe")
$requiredKeyDirectory = [IO.Path]::GetFullPath(
    (Join-Path $env:ProgramData "Grupo Orion\OrionERP.Training\DataProtectionKeys"))
if (-not [string]::Equals($ServiceName, $requiredServiceName, [StringComparison]::Ordinal)) {
    throw "Training configuration is pinned to service '$requiredServiceName'."
}

$allowedHostEntries = @($AllowedHosts -split '[;,]' |
    ForEach-Object { $_.Trim() } |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
if ($allowedHostEntries.Count -eq 0 -or $allowedHostEntries -contains "*" -or $allowedHostEntries -contains "+") {
    throw "AllowedHosts must list explicit local and/or Cloudflare hostnames; wildcards are forbidden."
}
if ($allowedHostEntries -notcontains "127.0.0.1") {
    throw "AllowedHosts must include 127.0.0.1 for the local readiness probe."
}
$AllowedHosts = $allowedHostEntries -join ";"

$normalizedPublicTrainingOrigin = ""
if (-not [string]::IsNullOrWhiteSpace($PublicTrainingOrigin)) {
    $publicOriginUri = $null
    if (-not [Uri]::TryCreate($PublicTrainingOrigin, [UriKind]::Absolute, [ref]$publicOriginUri) `
        -or [string]::IsNullOrWhiteSpace($publicOriginUri.Host) `
        -or -not [string]::IsNullOrEmpty($publicOriginUri.UserInfo) `
        -or $publicOriginUri.AbsolutePath -ne "/" `
        -or -not [string]::IsNullOrEmpty($publicOriginUri.Query) `
        -or -not [string]::IsNullOrEmpty($publicOriginUri.Fragment) `
        -or ($publicOriginUri.Scheme -ne "https" `
          -and -not ($publicOriginUri.IsLoopback -and $publicOriginUri.Scheme -eq "http"))) {
        throw "PublicTrainingOrigin must be an HTTPS public origin or a loopback HTTP origin without path, query, fragment, or user information."
    }
    if ([string]::Equals(
        $publicOriginUri.Host,
        "orionerp.orion.land",
        [StringComparison]::OrdinalIgnoreCase)) {
        throw "PublicTrainingOrigin cannot use the OrionERP production origin."
    }
    if ($allowedHostEntries -notcontains $publicOriginUri.Host) {
        throw "PublicTrainingOrigin host must be explicitly present in AllowedHosts."
    }

    $normalizedPublicTrainingOrigin = $publicOriginUri.GetLeftPart([UriPartial]::Authority)
}

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function New-FileSystemRule {
    param(
        [Security.Principal.SecurityIdentifier]$Identity,
        [Security.AccessControl.FileSystemRights]$Rights,
        [bool]$IsDirectory,
        [Security.AccessControl.AccessControlType]$AccessType =
            [Security.AccessControl.AccessControlType]::Allow
    )

    $inheritance = if ($IsDirectory) {
        [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor `
            [Security.AccessControl.InheritanceFlags]::ObjectInherit
    }
    else {
        [Security.AccessControl.InheritanceFlags]::None
    }

    return [Security.AccessControl.FileSystemAccessRule]::new(
        $Identity,
        $Rights,
        $inheritance,
        [Security.AccessControl.PropagationFlags]::None,
        $AccessType)
}

function Set-DedicatedKeyItemAcl {
    param(
        [System.IO.FileSystemInfo]$Item,
        [Security.Principal.SecurityIdentifier]$ServiceSid,
        [Security.Principal.SecurityIdentifier]$SystemSid,
        [Security.Principal.SecurityIdentifier]$AdministratorsSid
    )

    $acl = Get-Acl -LiteralPath $Item.FullName
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($existingRule in @($acl.Access)) {
        $null = $acl.RemoveAccessRuleAll($existingRule)
    }
    $acl.SetOwner($AdministratorsSid)
    $acl.AddAccessRule((New-FileSystemRule `
        -Identity $SystemSid `
        -Rights ([Security.AccessControl.FileSystemRights]::FullControl) `
        -IsDirectory $Item.PSIsContainer))
    $acl.AddAccessRule((New-FileSystemRule `
        -Identity $AdministratorsSid `
        -Rights ([Security.AccessControl.FileSystemRights]::FullControl) `
        -IsDirectory $Item.PSIsContainer))
    $acl.AddAccessRule((New-FileSystemRule `
        -Identity $ServiceSid `
        -Rights ([Security.AccessControl.FileSystemRights]::Modify) `
        -IsDirectory $Item.PSIsContainer))
    Set-Acl -LiteralPath $Item.FullName -AclObject $acl
}

function Assert-DedicatedKeyItemAcl {
    param(
        [System.IO.FileSystemInfo]$Item,
        [Security.Principal.SecurityIdentifier]$ServiceSid,
        [Security.Principal.SecurityIdentifier]$SystemSid,
        [Security.Principal.SecurityIdentifier]$AdministratorsSid
    )

    $acl = Get-Acl -LiteralPath $Item.FullName
    if (-not $acl.AreAccessRulesProtected) {
        throw "The Training key DACL is not protected from inherited access."
    }
    if ($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -ne $AdministratorsSid.Value) {
        throw "The Training key item owner is not BUILTIN\Administrators."
    }

    $expectedRights = @{
        $SystemSid.Value = [Security.AccessControl.FileSystemRights]::FullControl
        $AdministratorsSid.Value = [Security.AccessControl.FileSystemRights]::FullControl
        $ServiceSid.Value = [Security.AccessControl.FileSystemRights]::Modify -bor `
            [Security.AccessControl.FileSystemRights]::Synchronize
    }
    $expectedInheritance = if ($Item.PSIsContainer) {
        [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor `
            [Security.AccessControl.InheritanceFlags]::ObjectInherit
    }
    else {
        [Security.AccessControl.InheritanceFlags]::None
    }
    $rules = @($acl.GetAccessRules(
        $true,
        $true,
        [Security.Principal.SecurityIdentifier]))
    if ($rules.Count -ne $expectedRights.Count) {
        throw "The Training key DACL contains an unexpected number of access rules."
    }

    foreach ($rule in $rules) {
        $sid = $rule.IdentityReference.Value
        if (-not $expectedRights.ContainsKey($sid) `
            -or $rule.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow `
            -or $rule.IsInherited `
            -or $rule.FileSystemRights -ne $expectedRights[$sid] `
            -or $rule.InheritanceFlags -ne $expectedInheritance `
            -or $rule.PropagationFlags -ne [Security.AccessControl.PropagationFlags]::None) {
            throw "The Training key DACL does not match the three-principal effective-access contract."
        }
    }
}

function Protect-DedicatedKeyTree {
    param(
        [string]$RootPath,
        [Security.Principal.SecurityIdentifier]$ServiceSid
    )

    $rootItem = Get-Item -LiteralPath $RootPath -Force
    if (($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "The Training key directory cannot be a reparse point."
    }
    $items = @($rootItem) + @(Get-ChildItem -LiteralPath $RootPath -Force -Recurse)
    if ($items | Where-Object {
        ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
    }) {
        throw "The Training key tree cannot contain reparse points."
    }

    $systemSid = [Security.Principal.SecurityIdentifier]::new("S-1-5-18")
    $administratorsSid = [Security.Principal.SecurityIdentifier]::new("S-1-5-32-544")
    foreach ($item in $items) {
        Set-DedicatedKeyItemAcl `
            -Item $item `
            -ServiceSid $ServiceSid `
            -SystemSid $systemSid `
            -AdministratorsSid $administratorsSid
    }
    foreach ($item in $items) {
        Assert-DedicatedKeyItemAcl `
            -Item $item `
            -ServiceSid $ServiceSid `
            -SystemSid $systemSid `
            -AdministratorsSid $administratorsSid
    }
}

function Protect-DedicatedServiceRegistryTree {
    param([string]$RootPath)

    $systemSid = [Security.Principal.SecurityIdentifier]::new("S-1-5-18")
    $administratorsSid = [Security.Principal.SecurityIdentifier]::new("S-1-5-32-544")
    $expectedRights = @{
        $systemSid.Value = [Security.AccessControl.RegistryRights]::FullControl
        $administratorsSid.Value = [Security.AccessControl.RegistryRights]::FullControl
    }
    $items = @((Get-Item -LiteralPath $RootPath)) + @(Get-ChildItem -LiteralPath $RootPath -Recurse)

    foreach ($item in $items) {
        $acl = Get-Acl -LiteralPath $item.PSPath
        $acl.SetAccessRuleProtection($true, $false)
        foreach ($existingRule in @($acl.Access)) {
            $null = $acl.RemoveAccessRuleAll($existingRule)
        }
        $acl.SetOwner($administratorsSid)
        foreach ($sid in @($systemSid, $administratorsSid)) {
            $acl.AddAccessRule([Security.AccessControl.RegistryAccessRule]::new(
                $sid,
                [Security.AccessControl.RegistryRights]::FullControl,
                [Security.AccessControl.InheritanceFlags]::ContainerInherit,
                [Security.AccessControl.PropagationFlags]::None,
                [Security.AccessControl.AccessControlType]::Allow))
        }
        Set-Acl -LiteralPath $item.PSPath -AclObject $acl
    }

    foreach ($item in $items) {
        $acl = Get-Acl -LiteralPath $item.PSPath
        if (-not $acl.AreAccessRulesProtected `
            -or $acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -ne $administratorsSid.Value) {
            throw "The OrionERP.Training service registry key is not protected by Administrators."
        }
        $rules = @($acl.GetAccessRules(
            $true,
            $true,
            [Security.Principal.SecurityIdentifier]))
        if ($rules.Count -ne $expectedRights.Count) {
            throw "The OrionERP.Training service registry DACL contains an unexpected principal."
        }
        foreach ($rule in $rules) {
            $sidValue = $rule.IdentityReference.Value
            if (-not $expectedRights.ContainsKey($sidValue) `
                -or $rule.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow `
                -or $rule.IsInherited `
                -or $rule.RegistryRights -ne $expectedRights[$sidValue] `
                -or $rule.InheritanceFlags -ne [Security.AccessControl.InheritanceFlags]::ContainerInherit `
                -or $rule.PropagationFlags -ne [Security.AccessControl.PropagationFlags]::None) {
                throw "The OrionERP.Training service registry effective-access contract failed."
            }
        }
    }
}

function Wait-TrainingReadiness {
    # A first start has to extract the single-file bundle, JIT, and run the full
    # database safety verification before /readyz answers, which can take well over
    # a minute on a cold machine. The budget below only bounds how long a genuinely
    # broken start is tolerated; a healthy service still passes on the first probe.
    param(
        [Uri]$ServiceUri,
        [int]$Attempts = 30
    )

    $healthUrl = "http://127.0.0.1:$($ServiceUri.Port)/readyz"
    $lastFailure = "no response"
    foreach ($attempt in 1..$Attempts) {
        try {
            $ready = Invoke-RestMethod -Uri $healthUrl -TimeoutSec 20
            $valid = $ready.status -eq "ready" `
                -and $ready.environment -eq "Training" `
                -and $ready.database.catalog -eq "Orion_Training" `
                -and $ready.database.safetyVerified -eq $true `
                -and [int]$ready.database.schemaVersion -ge 1 `
                -and $ready.database.sanitized -eq $true `
                -and $ready.database.syntheticDataOnly -eq $true `
                -and $ready.database.runtimeLoginIsolated -eq $true `
                -and $ready.trainingSafety.externalEffectsBlocked -eq $true `
                -and $ready.trainingSafety.serverOutboundHttpBlocked -eq $true `
                -and $ready.trainingSafety.browserOutboundBlocked -eq $true
            if ($valid) {
                Write-Host "Training readiness and safety attestation passed."
                return
            }
            $lastFailure = "readiness payload did not match the Training safety contract"
        }
        catch {
            $lastFailure = $_.Exception.Message
        }

        if ($attempt -lt $Attempts) {
            Start-Sleep -Seconds 2
        }
    }

    throw "Training service failed readiness validation. Last failure: $lastFailure"
}

if (-not (Test-IsAdministrator)) {
    throw "Run this script from an elevated PowerShell session."
}

if ([string]::IsNullOrWhiteSpace($ConnectionString)) {
    throw "Set ORION_TRAINING_ConnectionStrings__OrionDb in this PowerShell session before running the script."
}

try {
    $connectionBuilder = [System.Data.SqlClient.SqlConnectionStringBuilder]::new($ConnectionString)
}
catch [ArgumentException] {
    throw "Training service configuration rejected: the connection string is not valid."
}
if ([string]::IsNullOrWhiteSpace([string]$connectionBuilder["Data Source"])) {
    throw "Training service configuration rejected: Server or Data Source is required."
}
if (-not [string]::Equals(
    ([string]$connectionBuilder["Initial Catalog"]).Trim(),
    "Orion_Training",
    [StringComparison]::OrdinalIgnoreCase)) {
    throw "Training service configuration rejected: the connection string must target exactly Orion_Training."
}
if (-not [string]::IsNullOrWhiteSpace([string]$connectionBuilder["AttachDBFilename"])) {
    throw "Training service configuration rejected: AttachDBFilename is forbidden."
}
if ([Convert]::ToBoolean($connectionBuilder["Integrated Security"]) `
    -or -not [string]::Equals(
      ([string]$connectionBuilder["User ID"]).Trim(),
      "orion_training_runtime",
      [StringComparison]::Ordinal) `
    -or [string]::IsNullOrWhiteSpace([string]$connectionBuilder["Password"])) {
    throw "Training service configuration rejected: use the fixed orion_training_runtime SQL-auth credential."
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
    -or -not [string]::IsNullOrEmpty($serviceUri.UserInfo) `
    -or $serviceUri.AbsolutePath -ne "/" `
    -or -not [string]::IsNullOrEmpty($serviceUri.Query) `
    -or $serviceUri.Port -le 0 `
    -or $serviceUri.Port -in @(5000, 5010, 5020)) {
    throw "WindowsServiceUrl must be a loopback HTTP origin on a port not reserved by production (5000, 5010, or 5020)."
}

$resolvedExecutablePath = [IO.Path]::GetFullPath($ExecutablePath)
if (-not [string]::Equals(
    $resolvedExecutablePath,
    $requiredExecutablePath,
    [StringComparison]::OrdinalIgnoreCase)) {
    throw "ExecutablePath must be the dedicated OrionERP.Training executable."
}
if (-not (Test-Path -LiteralPath $resolvedExecutablePath -PathType Leaf)) {
    throw "Training executable was not found at '$resolvedExecutablePath'. Publish it first."
}

$resolvedKeyDirectory = [IO.Path]::GetFullPath(
    [Environment]::ExpandEnvironmentVariables($DataProtectionKeyDirectory))
if (-not [string]::Equals(
    $resolvedKeyDirectory,
    $requiredKeyDirectory,
    [StringComparison]::OrdinalIgnoreCase)) {
    throw "DataProtectionKeyDirectory must be the dedicated non-synced OrionERP.Training ProgramData directory."
}
$serviceAccount = "NT SERVICE\$ServiceName"

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($service -and $service.Status -ne "Stopped") {
    if (-not $Restart) {
        throw "The existing Training service is running. Pass -Restart so the isolated configuration is applied immediately."
    }
    if ($PSCmdlet.ShouldProcess($ServiceName, "Stop Training service before changing its identity, secrets, and ACLs")) {
        Stop-Service -Name $ServiceName -Force
        $service.WaitForStatus("Stopped", [TimeSpan]::FromSeconds(30))
        $service = Get-Service -Name $ServiceName
    }
}
if (-not $service -and $PSCmdlet.ShouldProcess($ServiceName, "Create dedicated Training Windows service")) {
    $service = New-Service `
        -Name $ServiceName `
        -BinaryPathName ('"{0}"' -f $resolvedExecutablePath) `
        -DisplayName "OrionERP Training" `
        -Description "OrionERP isolated employee training environment" `
        -StartupType Automatic
}

if ($PSCmdlet.ShouldProcess($ServiceName, "Use the dedicated virtual service account $serviceAccount")) {
    $serviceConfiguration = Get-CimInstance -ClassName Win32_Service -Filter "Name='$ServiceName'"
    if (-not $serviceConfiguration) {
        throw "Windows service '$ServiceName' was not found after creation."
    }

    $configuredExecutable = if ($serviceConfiguration.PathName -match '^"([^"]+)"') {
        $Matches[1]
    }
    else {
        $serviceConfiguration.PathName
    }
    if (-not [string]::Equals(
        [IO.Path]::GetFullPath($configuredExecutable),
        $resolvedExecutablePath,
        [StringComparison]::OrdinalIgnoreCase)) {
        throw "Existing service '$ServiceName' points to a different executable; refusing to reconfigure it."
    }

    $changeResult = Invoke-CimMethod `
        -InputObject $serviceConfiguration `
        -MethodName Change `
        -Arguments @{ StartName = $serviceAccount; StartPassword = $null }
    if ($changeResult.ReturnValue -ne 0) {
        throw "Could not assign the dedicated virtual service account (Win32 error $($changeResult.ReturnValue))."
    }

    $publishDirectory = Split-Path -Parent $resolvedExecutablePath
    New-Item -ItemType Directory -Path $resolvedKeyDirectory -Force | Out-Null

    # The risk this guards against is a redirected publish directory pointing the
    # service at a different executable. Only name-surrogate reparse points
    # (symbolic links, junctions, mount points) redirect, and those expose a
    # LinkTarget. Cloud sync engines such as Dropbox and OneDrive also mark synced
    # directories with a reparse tag (0x9000e01a here) that resolves in place and
    # reports no LinkTarget, so rejecting every reparse point would make any
    # synced deployment root impossible while adding no protection.
    $publishDirectoryItem = Get-Item -LiteralPath $publishDirectory -Force
    if (($publishDirectoryItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 `
        -and $null -ne $publishDirectoryItem.LinkTarget) {
        throw "The dedicated Training publish directory cannot be a symbolic link, junction, or mount point."
    }
    try {
        $serviceSid = [Security.Principal.NTAccount]::new($serviceAccount).Translate(
            [Security.Principal.SecurityIdentifier])
    }
    catch {
        throw "The dedicated Training service SID could not be resolved after service creation."
    }

    $inheritance = [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor `
        [Security.AccessControl.InheritanceFlags]::ObjectInherit
    $propagation = [Security.AccessControl.PropagationFlags]::None
    $allow = [Security.AccessControl.AccessControlType]::Allow

    $publishAcl = Get-Acl -LiteralPath $publishDirectory
    $publishRule = [Security.AccessControl.FileSystemAccessRule]::new(
        $serviceSid,
        [Security.AccessControl.FileSystemRights]::ReadAndExecute,
        $inheritance,
        $propagation,
        $allow)
    $publishAcl.SetAccessRule($publishRule)
    Set-Acl -LiteralPath $publishDirectory -AclObject $publishAcl

    $verifiedPublishAcl = Get-Acl -LiteralPath $publishDirectory
    $publishServiceRules = @($verifiedPublishAcl.GetAccessRules(
        $true,
        $false,
        [Security.Principal.SecurityIdentifier]) | Where-Object {
            $_.IdentityReference.Value -eq $serviceSid.Value
        })
    $expectedPublishRights = [Security.AccessControl.FileSystemRights]::ReadAndExecute -bor `
        [Security.AccessControl.FileSystemRights]::Synchronize
    if ($publishServiceRules.Count -ne 1 `
        -or $publishServiceRules[0].AccessControlType -ne $allow `
        -or $publishServiceRules[0].FileSystemRights -ne $expectedPublishRights `
        -or $publishServiceRules[0].InheritanceFlags -ne $inheritance `
        -or $publishServiceRules[0].PropagationFlags -ne $propagation) {
        throw "The Training service publish-tree rule is not scoped to inherited ReadAndExecute access."
    }

    Protect-DedicatedKeyTree -RootPath $resolvedKeyDirectory -ServiceSid $serviceSid
}

$serviceRegistryPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
$serviceEnvironment = @(
    "ASPNETCORE_ENVIRONMENT=Training",
    "DOTNET_ENVIRONMENT=Training",
    "ORION_TRAINING_SERVICE=1",
    "ORION_TRAINING_AllowedHosts=$AllowedHosts",
    "ORION_TRAINING_ConnectionStrings__OrionDb=$ConnectionString",
    "ORION_TRAINING_Hosting__WindowsServiceUrl=$WindowsServiceUrl",
    "ORION_TRAINING_PlatformIsolation__DataProtectionKeyPath=$resolvedKeyDirectory",
    "ORION_TRAINING_Capacitacion__SandboxBaseUrl=$normalizedPublicTrainingOrigin"
)

if ($PSCmdlet.ShouldProcess($ServiceName, "Set isolated Training environment and connection configuration")) {
    Protect-DedicatedServiceRegistryTree -RootPath $serviceRegistryPath
    New-ItemProperty `
        -LiteralPath $serviceRegistryPath `
        -Name "Environment" `
        -PropertyType MultiString `
        -Value $serviceEnvironment `
        -Force | Out-Null

    Protect-DedicatedServiceRegistryTree -RootPath $serviceRegistryPath

    Write-Host "Training service configuration stored without displaying the connection string."
}

if ($Restart -and $PSCmdlet.ShouldProcess($ServiceName, "Restart Training service")) {
    $service = Get-Service -Name $ServiceName
    if ($service.Status -ne "Stopped") {
        Stop-Service -Name $ServiceName -Force
        $service.WaitForStatus("Stopped", [TimeSpan]::FromSeconds(30))
    }

    Start-Service -Name $ServiceName
    $service.WaitForStatus("Running", [TimeSpan]::FromSeconds(30))
    Wait-TrainingReadiness -ServiceUri $serviceUri
}
