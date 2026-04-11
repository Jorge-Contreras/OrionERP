[CmdletBinding()]
param(
  [Parameter(Mandatory = $false)]
  [string]$ConnectionString = $env:ASPNETCORE_ConnectionStrings__OrionDb,

  [Parameter(Mandatory = $false)]
  [string]$DatabaseName = "Orion_SandBox"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($ConnectionString)) {
  throw "Provide -ConnectionString or set ASPNETCORE_ConnectionStrings__OrionDb."
}

if ([string]::IsNullOrWhiteSpace($DatabaseName)) {
  throw "Provide -DatabaseName."
}

Add-Type -AssemblyName System.Data

$resolvedConnectionString =
  if ($ConnectionString -match '(?i)(^|;)\s*Database\s*=') {
    $ConnectionString -replace '(?i)(^|;)\s*Database\s*=\s*[^;]*', "`$1Database=$DatabaseName"
  }
  elseif ($ConnectionString -match '(?i)(^|;)\s*Initial Catalog\s*=') {
    $ConnectionString -replace '(?i)(^|;)\s*Initial Catalog\s*=\s*[^;]*', "`$1Initial Catalog=$DatabaseName"
  }
  else {
    $ConnectionString.TrimEnd(';') + ";Database=$DatabaseName;"
  }

$sqlPath = Join-Path $PSScriptRoot "20260410_balance_order_anchor.sql"
if (-not (Test-Path -LiteralPath $sqlPath)) {
  throw "Could not find SQL script at $sqlPath."
}

function Invoke-DbBatch {
  param(
    [Parameter(Mandatory = $true)]
    [System.Data.SqlClient.SqlConnection]$Connection,
    [Parameter(Mandatory = $true)]
    [string]$Sql
  )

  $command = $Connection.CreateCommand()
  $command.CommandTimeout = 0
  $command.CommandText = $Sql
  [void]$command.ExecuteNonQuery()
}

$sql = Get-Content -LiteralPath $sqlPath -Raw
$batches = [regex]::Split($sql, '(?im)^\s*GO\s*;?\s*$')

$connection = New-Object System.Data.SqlClient.SqlConnection $resolvedConnectionString
$connection.Open()

try {
  foreach ($batch in $batches) {
    if ([string]::IsNullOrWhiteSpace($batch)) {
      continue
    }

    Invoke-DbBatch -Connection $connection -Sql $batch
  }

  Write-Host "Applied balance-order migration to $DatabaseName."
}
finally {
  $connection.Dispose()
}
