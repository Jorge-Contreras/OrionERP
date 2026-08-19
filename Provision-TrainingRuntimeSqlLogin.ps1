[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$AdminConnectionString = $env:ORION_TRAINING_ADMIN_ConnectionString,
    [Parameter(Mandatory)]
    [System.Security.SecureString]$RuntimePassword,
    [switch]$Apply
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
Set-Location -LiteralPath $PSScriptRoot

$runtimeLogin = "orion_training_runtime"
$trainingCatalog = "Orion_Training"
$blockedCatalogs = @("grupocarpio", "Orion_Sandbox")

function ConvertTo-TransientPlainText {
    param([System.Security.SecureString]$SecureValue)

    $pointer = [IntPtr]::Zero
    try {
        $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($SecureValue)
        return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
    }
    finally {
        if ($pointer -ne [IntPtr]::Zero) {
            [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
        }
    }
}

function Assert-BlockedCatalogConnection {
    param(
        [System.Data.SqlClient.SqlConnectionStringBuilder]$BaseBuilder,
        [System.Data.SqlClient.SqlCredential]$Credential,
        [string]$Catalog
    )

    $probeBuilder = [System.Data.SqlClient.SqlConnectionStringBuilder]::new($BaseBuilder.ConnectionString)
    $probeBuilder["Initial Catalog"] = $Catalog
    $probe = [System.Data.SqlClient.SqlConnection]::new(
        $probeBuilder.ConnectionString,
        $Credential)
    try {
        $probe.Open()
    }
    catch [System.Data.SqlClient.SqlException] {
        $sqlErrors = @($_.Exception.Errors)
        $accessDenied = @($sqlErrors | Where-Object { $_.Number -in @(4060, 916) })
        $unexpectedErrors = @($sqlErrors | Where-Object { $_.Number -notin @(4060, 916, 18456) })
        if ($accessDenied.Count -eq 0 -or $unexpectedErrors.Count -ne 0) {
            throw "Runtime boundary verification for '$Catalog' failed for a reason other than an explicit database-access denial."
        }
        return
    }
    finally {
        $probe.Dispose()
    }

    throw "Runtime boundary verification failed: the dedicated credential opened '$Catalog'."
}

if (-not $Apply) {
    throw "Provisioning is disabled by default. Review the SQL and pass -Apply explicitly."
}
if ([string]::IsNullOrWhiteSpace($AdminConnectionString)) {
    throw "Set ORION_TRAINING_ADMIN_ConnectionString for a SQL administrator; it is never stored or printed."
}
if ($RuntimePassword.Length -lt 16 -or $RuntimePassword.Length -gt 128) {
    throw "RuntimePassword must contain between 16 and 128 characters."
}

try {
    $adminBuilder = [System.Data.SqlClient.SqlConnectionStringBuilder]::new($AdminConnectionString)
}
catch [ArgumentException] {
    throw "AdminConnectionString is not a valid SQL Server connection string."
}

if ([string]::IsNullOrWhiteSpace([string]$adminBuilder["Data Source"])) {
    throw "AdminConnectionString must include Server or Data Source."
}
if (-not [string]::Equals(
    ([string]$adminBuilder["Initial Catalog"]).Trim(),
    "master",
    [StringComparison]::OrdinalIgnoreCase)) {
    throw "The administrative connection must explicitly target exactly master."
}
if (-not [string]::IsNullOrWhiteSpace([string]$adminBuilder["AttachDBFilename"])) {
    throw "AdminConnectionString cannot use AttachDBFilename."
}
if (-not [Convert]::ToBoolean($adminBuilder["Encrypt"])) {
    throw "AdminConnectionString must enable Encrypt=True."
}
if (-not [Convert]::ToBoolean($adminBuilder["Integrated Security"]) `
    -and ([string]::IsNullOrWhiteSpace([string]$adminBuilder["User ID"]) `
      -or [string]::IsNullOrWhiteSpace([string]$adminBuilder["Password"]))) {
    throw "AdminConnectionString must use Integrated Security or include a separate administrative User Id and Password."
}

$adminBuilder["Persist Security Info"] = $false
# Provisioning runs guarded DDL and impersonation probes. If an attempt aborts
# mid-batch, the underlying session can be left unusable or killed, and a pooled
# connection would hand that same poisoned session to the next attempt. Each run
# must start from a genuinely fresh session.
$adminBuilder["Pooling"] = $false
$scriptPath = Join-Path $PSScriptRoot "src\OrionERP.Infrastructure\Features\Capacitacion\Sql\20260817_training_runtime_login.sql"
if (-not (Test-Path -LiteralPath $scriptPath -PathType Leaf)) {
    throw "The reviewed runtime-login SQL script was not found."
}
if (-not $PSCmdlet.ShouldProcess(
    "$runtimeLogin on $trainingCatalog",
    "Create or rotate the fixed SQL-auth login and verify its real connection boundary")) {
    return
}

$adminConnection = $null
$provisionCommand = $null
$runtimeConnection = $null
$runtimePasswordCopy = $null
$runtimePasswordText = $null
try {
    $adminConnection = [System.Data.SqlClient.SqlConnection]::new($adminBuilder.ConnectionString)
    $adminConnection.Open()

    $catalogCommand = $adminConnection.CreateCommand()
    try {
        $catalogCommand.CommandText = "SELECT DB_NAME(), CONVERT(int, ISNULL(IS_SRVROLEMEMBER(N'sysadmin'), 0));"
        $catalogReader = $catalogCommand.ExecuteReader()
        try {
            if (-not $catalogReader.Read()) {
                throw "The administrative boundary query returned no result."
            }
            $activeAdminCatalog = $catalogReader.GetString(0)
            $adminIsSysadmin = $catalogReader.GetInt32(1) -eq 1
        }
        finally {
            $catalogReader.Dispose()
        }
    }
    finally {
        $catalogCommand.Dispose()
    }
    if (-not [string]::Equals($activeAdminCatalog, "master", [StringComparison]::Ordinal)) {
        throw "The opened administrative connection is not exactly master."
    }
    if (-not $adminIsSysadmin) {
        throw "The runtime provisioning administrator must be sysadmin."
    }

    $runtimePasswordText = ConvertTo-TransientPlainText -SecureValue $RuntimePassword
    $provisionCommand = $adminConnection.CreateCommand()
    $provisionCommand.CommandTimeout = 60
    $provisionCommand.CommandText = [IO.File]::ReadAllText($scriptPath)
    $null = $provisionCommand.Parameters.Add(
        "@ExpectedDatabase",
        [System.Data.SqlDbType]::NVarChar,
        128)
    $provisionCommand.Parameters["@ExpectedDatabase"].Value = $trainingCatalog
    $null = $provisionCommand.Parameters.Add(
        "@RuntimeLogin",
        [System.Data.SqlDbType]::NVarChar,
        128)
    $provisionCommand.Parameters["@RuntimeLogin"].Value = $runtimeLogin
    $null = $provisionCommand.Parameters.Add(
        "@RuntimePassword",
        [System.Data.SqlDbType]::NVarChar,
        128)
    $provisionCommand.Parameters["@RuntimePassword"].Value = $runtimePasswordText
    $null = $provisionCommand.ExecuteNonQuery()

    $provisionCommand.Dispose()
    $provisionCommand = $null
    $runtimePasswordText = $null

    $runtimeBuilder = [System.Data.SqlClient.SqlConnectionStringBuilder]::new()
    $runtimeBuilder["Data Source"] = [string]$adminBuilder["Data Source"]
    $runtimeBuilder["Initial Catalog"] = $trainingCatalog
    $runtimeBuilder["Integrated Security"] = $false
    $runtimeBuilder["Encrypt"] = $true
    $runtimeBuilder["TrustServerCertificate"] = [Convert]::ToBoolean($adminBuilder["TrustServerCertificate"])
    $runtimeBuilder["Persist Security Info"] = $false
    $runtimeBuilder["Application Name"] = "OrionERP.Training provisioning verification"
    $runtimeBuilder["Connect Timeout"] = 15
    # This connection exists to prove what a freshly created principal can and
    # cannot reach, so it must never reuse a pooled session established under
    # earlier permissions. The blocked-catalog probes inherit this builder.
    $runtimeBuilder["Pooling"] = $false

    $runtimePasswordCopy = $RuntimePassword.Copy()
    $runtimePasswordCopy.MakeReadOnly()
    $runtimeCredential = [System.Data.SqlClient.SqlCredential]::new(
        $runtimeLogin,
        $runtimePasswordCopy)
    $runtimeConnection = [System.Data.SqlClient.SqlConnection]::new(
        $runtimeBuilder.ConnectionString,
        $runtimeCredential)
    $runtimeConnection.Open()

    $verifyCommand = $runtimeConnection.CreateCommand()
    try {
        $verifyCommand.CommandText = @"
SELECT
  DB_NAME(),
  ORIGINAL_LOGIN(),
  CONVERT(nvarchar(20), CONNECTIONPROPERTY('auth_scheme')),
  CONVERT(int, ISNULL(HAS_DBACCESS(N'grupocarpio'), 0)),
  CONVERT(int, ISNULL(HAS_DBACCESS(N'Orion_Sandbox'), 0)),
  CONVERT(int, ISNULL(IS_SRVROLEMEMBER(N'sysadmin'), 0)),
  CONVERT(int, ISNULL(IS_ROLEMEMBER(N'db_owner'), 0)),
  CONVERT(int, ISNULL(IS_ROLEMEMBER(N'db_ddladmin'), 0)),
  CONVERT(int, ISNULL(IS_ROLEMEMBER(N'db_securityadmin'), 0)),
  CONVERT(int, ISNULL(IS_ROLEMEMBER(N'db_datareader'), 0)),
  CONVERT(int, ISNULL(IS_ROLEMEMBER(N'db_datawriter'), 0)),
  CONVERT(int, ISNULL(HAS_PERMS_BY_NAME(DB_NAME(), N'DATABASE', N'EXECUTE'), 0)),
  CONVERT(int, ISNULL(HAS_PERMS_BY_NAME(DB_NAME(), N'DATABASE', N'VIEW DEFINITION'), 0)),
  CONVERT(int, ISNULL(HAS_PERMS_BY_NAME(
    N'capacitacion.EntornoSeguridad', N'OBJECT', N'UPDATE'), 0)),
  CONVERT(int, ISNULL(HAS_PERMS_BY_NAME(
    N'capacitacion.EsquemaVersion', N'OBJECT', N'DELETE'), 0)),
  CONVERT(int, ISNULL(HAS_PERMS_BY_NAME(DB_NAME(), N'DATABASE', N'CONTROL'), 0)),
  CONVERT(int, ISNULL(HAS_PERMS_BY_NAME(DB_NAME(), N'DATABASE', N'ALTER ANY USER'), 0)),
  CONVERT(int, ISNULL(HAS_PERMS_BY_NAME(DB_NAME(), N'DATABASE', N'ALTER ANY ROLE'), 0)),
  CONVERT(int,
  (
    SELECT COUNT(*)
    FROM sys.database_role_members membership
    JOIN sys.database_principals roleInfo
      ON roleInfo.principal_id = membership.role_principal_id
    JOIN sys.database_principals memberInfo
      ON memberInfo.principal_id = membership.member_principal_id
    WHERE memberInfo.name = N'orion_training_runtime'
      AND roleInfo.name NOT IN (N'db_datareader', N'db_datawriter')
  )),
  CONVERT(int,
    (SELECT COUNT(*) FROM sys.fn_my_permissions(NULL, N'SERVER')
     WHERE permission_name NOT IN (N'CONNECT SQL', N'VIEW ANY DATABASE'))),
  CONVERT(int,
  (
    SELECT COUNT(*)
    FROM sys.database_principals principalInfo
    WHERE principalInfo.principal_id > 4
      AND principalInfo.is_fixed_role = 0
      AND
      (
        principalInfo.name <> N'orion_training_runtime'
        OR principalInfo.type <> N'S'
        OR principalInfo.authentication_type <> 1
        OR principalInfo.sid <> SUSER_SID(N'orion_training_runtime')
      )
  )
  -- dbo is a member of db_owner in every SQL Server database, so only non-built-in
  -- members are counted; and the built-in GRANTs to public on system objects
  -- (negative major_id) are never revoked, so they are not clone residue either.
  + CASE WHEN (SELECT COUNT(*) FROM sys.database_role_members
               WHERE member_principal_id > 4) = 2 THEN 0 ELSE 1 END
  + (SELECT COUNT(*)
     FROM sys.database_permissions permissionInfo
     JOIN sys.database_principals grantee ON grantee.principal_id = permissionInfo.grantee_principal_id
     WHERE (grantee.name IN (N'public', N'guest') OR grantee.is_fixed_role = 1)
       AND (permissionInfo.class = 0 OR permissionInfo.major_id > 0))
  + CASE WHEN EXISTS
      (SELECT 1 FROM sys.databases
       WHERE database_id = DB_ID() AND (owner_sid <> 0x01 OR containment <> 0))
    THEN 1 ELSE 0 END
  + (SELECT COUNT(*) FROM sys.schemas
     WHERE name NOT IN (N'dbo', N'guest', N'INFORMATION_SCHEMA', N'sys')
       AND principal_id <> USER_ID(N'dbo'))
  + (SELECT COUNT(*) FROM sys.objects
     WHERE is_ms_shipped = 0 AND principal_id IS NOT NULL
       AND principal_id <> USER_ID(N'dbo'))
  + (SELECT COUNT(*) FROM sys.security_policies)
  + (SELECT COUNT(*) FROM sys.security_predicates)
  + (SELECT COUNT(*) FROM sys.certificates)
  + (SELECT COUNT(*) FROM sys.asymmetric_keys)
  + (SELECT COUNT(*) FROM sys.symmetric_keys)
  + (SELECT COUNT(*) FROM sys.column_master_keys)
  + (SELECT COUNT(*) FROM sys.column_encryption_keys)
  + (SELECT COUNT(*) FROM sys.views viewInfo
     JOIN sys.indexes indexInfo ON indexInfo.object_id = viewInfo.object_id
     WHERE indexInfo.index_id > 0));
"@
        $reader = $verifyCommand.ExecuteReader()
        try {
            if (-not $reader.Read()) {
                throw "The runtime credential verification returned no result."
            }

            $valid = [string]::Equals($reader.GetString(0), $trainingCatalog, [StringComparison]::Ordinal) `
                -and [string]::Equals($reader.GetString(1), $runtimeLogin, [StringComparison]::Ordinal) `
                -and [string]::Equals($reader.GetString(2), "SQL", [StringComparison]::OrdinalIgnoreCase) `
                -and [Convert]::ToInt32($reader.GetValue(3)) -eq 0 `
                -and [Convert]::ToInt32($reader.GetValue(4)) -eq 0 `
                -and [Convert]::ToInt32($reader.GetValue(5)) -eq 0 `
                -and [Convert]::ToInt32($reader.GetValue(6)) -eq 0 `
                -and [Convert]::ToInt32($reader.GetValue(7)) -eq 0 `
                -and [Convert]::ToInt32($reader.GetValue(8)) -eq 0 `
                -and [Convert]::ToInt32($reader.GetValue(9)) -eq 1 `
                -and [Convert]::ToInt32($reader.GetValue(10)) -eq 1 `
                -and [Convert]::ToInt32($reader.GetValue(11)) -eq 1 `
                -and [Convert]::ToInt32($reader.GetValue(12)) -eq 1 `
                -and [Convert]::ToInt32($reader.GetValue(13)) -eq 0 `
                -and [Convert]::ToInt32($reader.GetValue(14)) -eq 0 `
                -and [Convert]::ToInt32($reader.GetValue(15)) -eq 0 `
                -and [Convert]::ToInt32($reader.GetValue(16)) -eq 0 `
                -and [Convert]::ToInt32($reader.GetValue(17)) -eq 0 `
                -and [Convert]::ToInt32($reader.GetValue(18)) -eq 0 `
                -and [Convert]::ToInt32($reader.GetValue(19)) -eq 0 `
                -and [Convert]::ToInt32($reader.GetValue(20)) -eq 0
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $verifyCommand.Dispose()
    }

    if (-not $valid) {
        throw "The real runtime SQL-auth connection did not satisfy the Training permission contract."
    }

    $runtimeConnection.Dispose()
    $runtimeConnection = $null

    foreach ($blockedCatalog in $blockedCatalogs) {
        Assert-BlockedCatalogConnection `
            -BaseBuilder $runtimeBuilder `
            -Credential $runtimeCredential `
            -Catalog $blockedCatalog
    }

    Write-Host "The dedicated Training SQL-auth login passed real connection and catalog-isolation checks."
}
finally {
    $runtimePasswordText = $null
    if ($provisionCommand) {
        $provisionCommand.Dispose()
    }
    if ($runtimeConnection) {
        $runtimeConnection.Dispose()
    }
    if ($runtimePasswordCopy) {
        $runtimePasswordCopy.Dispose()
    }
    if ($adminConnection) {
        $adminConnection.Dispose()
    }
}
