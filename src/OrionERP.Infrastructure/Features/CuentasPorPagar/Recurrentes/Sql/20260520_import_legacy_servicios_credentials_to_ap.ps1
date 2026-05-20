[CmdletBinding()]
param(
  [string]$ConnectionString = "",
  [string]$KeyPath = "",
  [switch]$Overwrite
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Get-ConfiguredConnectionString {
  param([string]$Value)

  if (-not [string]::IsNullOrWhiteSpace($Value)) {
    return $Value
  }

  $candidateNames = @(
    "ORIONERP_CONNECTION_STRING",
    "ConnectionStrings__OrionDb",
    "ConnectionStrings__DefaultConnection",
    "ASPNETCORE_ConnectionStrings__OrionDb",
    "ASPNETCORE_ConnectionStrings__DefaultConnection"
  )

  foreach ($name in $candidateNames) {
    $candidate = [Environment]::GetEnvironmentVariable($name)
    if (-not [string]::IsNullOrWhiteSpace($candidate)) {
      return $candidate
    }
  }

  throw "Pass -ConnectionString or set one of: $($candidateNames -join ', ')."
}

function Get-RepositoryRoot {
  $directory = Get-Item -LiteralPath (Get-Location)

  while ($null -ne $directory) {
    if (Test-Path -LiteralPath (Join-Path $directory.FullName "OrionERP.sln")) {
      return $directory.FullName
    }

    $directory = $directory.Parent
  }

  return (Get-Location).Path
}

function Get-ConfiguredKeyPath {
  param([string]$Value)

  if (-not [string]::IsNullOrWhiteSpace($Value)) {
    $resolved = Resolve-Path -LiteralPath $Value -ErrorAction Stop
    return $resolved.ProviderPath
  }

  $repoRoot = Get-RepositoryRoot
  $candidates = @(
    (Join-Path $repoRoot "src\OrionERP.Web\App_Data\rfc-register.aes.key"),
    (Join-Path $repoRoot "App_Data\rfc-register.aes.key"),
    (Join-Path $PSScriptRoot "..\..\..\..\..\OrionERP.Web\App_Data\rfc-register.aes.key")
  )

  foreach ($candidate in $candidates) {
    if (Test-Path -LiteralPath $candidate) {
      $resolved = Resolve-Path -LiteralPath $candidate -ErrorAction Stop
      return $resolved.ProviderPath
    }
  }

  throw "AES-GCM key file was not found. Pass -KeyPath with the rfc-register.aes.key path."
}

function Get-NullableString {
  param(
    [System.Data.IDataRecord]$Reader,
    [string]$Name
  )

  $ordinal = $Reader.GetOrdinal($Name)
  if ($Reader.IsDBNull($ordinal)) {
    return $null
  }

  $value = [string]$Reader.GetValue($ordinal)
  if ([string]::IsNullOrWhiteSpace($value)) {
    return $null
  }

  return $value.Trim()
}

function Protect-RecurringPayablePassword {
  param(
    [string]$PlainText,
    [byte[]]$Key
  )

  $nonce = [byte[]]::new(12)
  [System.Security.Cryptography.RandomNumberGenerator]::Fill($nonce)

  $plainBytes = [System.Text.Encoding]::UTF8.GetBytes($PlainText)
  $cipherBytes = [byte[]]::new($plainBytes.Length)
  $tag = [byte[]]::new(16)

  $aes = [System.Security.Cryptography.AesGcm]::new($Key, 16)
  try {
    $aes.Encrypt($nonce, $plainBytes, $cipherBytes, $tag)
  }
  finally {
    $aes.Dispose()
    [Array]::Clear($plainBytes, 0, $plainBytes.Length)
  }

  $payload = [byte[]]::new($nonce.Length + $tag.Length + $cipherBytes.Length)
  [Array]::Copy($nonce, 0, $payload, 0, $nonce.Length)
  [Array]::Copy($tag, 0, $payload, $nonce.Length, $tag.Length)
  [Array]::Copy($cipherBytes, 0, $payload, $nonce.Length + $tag.Length, $cipherBytes.Length)
  [Array]::Clear($cipherBytes, 0, $cipherBytes.Length)

  return ,$payload
}

function Add-NullableNVarCharParameter {
  param(
    [System.Data.SqlClient.SqlCommand]$Command,
    [string]$Name,
    [int]$Size,
    [string]$Value
  )

  $parameter = $Command.Parameters.Add($Name, [System.Data.SqlDbType]::NVarChar, $Size)
  if ($null -eq $Value) {
    $parameter.Value = [DBNull]::Value
  }
  else {
    $parameter.Value = $Value
  }
}

$resolvedConnectionString = Get-ConfiguredConnectionString -Value $ConnectionString
$resolvedKeyPath = Get-ConfiguredKeyPath -Value $KeyPath
$key = [System.IO.File]::ReadAllBytes($resolvedKeyPath)

if ($key.Length -ne 32) {
  throw "AES-GCM key must be exactly 32 bytes. Found $($key.Length) bytes at $resolvedKeyPath."
}

$connection = [System.Data.SqlClient.SqlConnection]::new($resolvedConnectionString)
$rows = [System.Collections.Generic.List[object]]::new()
$updatedRows = 0
$passwordsEncrypted = 0
$websiteUserRowsUpdated = 0

try {
  $connection.Open()

  $select = $connection.CreateCommand()
  $select.CommandText = @"
SELECT
    rp.Id,
    NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(500), rp.Website))), N'') AS CurrentWebsite,
    NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(200), rp.UserName))), N'') AS CurrentUserName,
    CASE WHEN rp.PasswordEnc IS NULL OR DATALENGTH(rp.PasswordEnc) = 0 THEN CONVERT(bit, 0) ELSE CONVERT(bit, 1) END AS HasPassword,
    LEFT(NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(4000), s.Pagina_Web))), N''), 500) AS LegacyWebsite,
    LEFT(NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(4000), s.Usuario))), N''), 200) AS LegacyUserName,
    NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(4000), s.Contrasena))), N'') AS LegacyPassword
FROM AP.RecurringPayable rp
JOIN dbo.Servicios s
  ON s.id = rp.LegacyServicioId
WHERE rp.LegacyServicioId IS NOT NULL
  AND (
      NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(4000), s.Pagina_Web))), N'') IS NOT NULL
      OR NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(4000), s.Usuario))), N'') IS NOT NULL
      OR NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(4000), s.Contrasena))), N'') IS NOT NULL
  );
"@

  $reader = $select.ExecuteReader()
  try {
    while ($reader.Read()) {
      $rows.Add([pscustomobject]@{
          Id = [int]$reader["Id"]
          CurrentWebsite = Get-NullableString -Reader $reader -Name "CurrentWebsite"
          CurrentUserName = Get-NullableString -Reader $reader -Name "CurrentUserName"
          HasPassword = [bool]$reader["HasPassword"]
          LegacyWebsite = Get-NullableString -Reader $reader -Name "LegacyWebsite"
          LegacyUserName = Get-NullableString -Reader $reader -Name "LegacyUserName"
          LegacyPassword = Get-NullableString -Reader $reader -Name "LegacyPassword"
        })
    }
  }
  finally {
    $reader.Dispose()
  }

  foreach ($row in $rows) {
    $nextWebsite = $row.CurrentWebsite
    $nextUserName = $row.CurrentUserName
    $nextPasswordEnc = $null
    $shouldUpdate = $false
    $portalFieldsChanged = $false
    $passwordChanged = $false

    if (-not [string]::IsNullOrWhiteSpace($row.LegacyWebsite) -and ($Overwrite -or [string]::IsNullOrWhiteSpace($row.CurrentWebsite))) {
      $nextWebsite = $row.LegacyWebsite
      $shouldUpdate = $true
      $portalFieldsChanged = $true
    }

    if (-not [string]::IsNullOrWhiteSpace($row.LegacyUserName) -and ($Overwrite -or [string]::IsNullOrWhiteSpace($row.CurrentUserName))) {
      $nextUserName = $row.LegacyUserName
      $shouldUpdate = $true
      $portalFieldsChanged = $true
    }

    if (-not [string]::IsNullOrWhiteSpace($row.LegacyPassword) -and ($Overwrite -or -not $row.HasPassword)) {
      $nextPasswordEnc = Protect-RecurringPayablePassword -PlainText $row.LegacyPassword -Key $key
      $shouldUpdate = $true
      $passwordChanged = $true
    }

    if (-not $shouldUpdate) {
      continue
    }

    $update = $connection.CreateCommand()
    $update.CommandText = @"
UPDATE AP.RecurringPayable
SET
    Website = @Website,
    UserName = @UserName,
    PasswordEnc = CASE WHEN @PasswordEnc IS NULL THEN PasswordEnc ELSE @PasswordEnc END,
    UpdatedAt = SYSUTCDATETIME(),
    UpdatedBy = N'LegacyServiciosCredentialImport'
WHERE Id = @Id;
"@

    [void]$update.Parameters.Add("@Id", [System.Data.SqlDbType]::Int)
    $update.Parameters["@Id"].Value = $row.Id
    Add-NullableNVarCharParameter -Command $update -Name "@Website" -Size 500 -Value $nextWebsite
    Add-NullableNVarCharParameter -Command $update -Name "@UserName" -Size 200 -Value $nextUserName
    $passwordParameter = $update.Parameters.Add("@PasswordEnc", [System.Data.SqlDbType]::VarBinary, -1)
    if ($null -eq $nextPasswordEnc) {
      $passwordParameter.Value = [DBNull]::Value
    }
    else {
      $passwordParameter.Value = $nextPasswordEnc
    }

    [void]$update.ExecuteNonQuery()
    $updatedRows++

    if ($portalFieldsChanged) {
      $websiteUserRowsUpdated++
    }

    if ($passwordChanged) {
      $passwordsEncrypted++
      [Array]::Clear($nextPasswordEnc, 0, $nextPasswordEnc.Length)
    }
  }
}
finally {
  if ($null -ne $key) {
    [Array]::Clear($key, 0, $key.Length)
  }

  $connection.Dispose()
}

Write-Host "Legacy Servicios credential import complete."
Write-Host "Rows scanned: $($rows.Count)"
Write-Host "Rows updated: $updatedRows"
Write-Host "Rows with Website/UserName updates: $websiteUserRowsUpdated"
Write-Host "Passwords encrypted into AP.RecurringPayable.PasswordEnc: $passwordsEncrypted"
if (-not $Overwrite) {
  Write-Host "Existing AP credential values were preserved. Use -Overwrite to replace them from dbo.Servicios."
}
