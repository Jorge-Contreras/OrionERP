<#
  Aplica las migraciones SQL de la Declaracion Mensual fiscal a una base.

  Requiere la variable de entorno ASPNETCORE_ConnectionStrings__OrionDb con una
  cadena de autenticacion SQL (usuario + password); no usa autenticacion
  integrada. La password se pasa a sqlcmd por $env:SQLCMDPASSWORD y nunca se
  imprime.

  Uso (desde la raiz del repo):

    # 1. Simulacro: no cambia nada. Los scripts con guarda corren con
    #    ApplyChanges=0 (validan y revierten); los de CREATE OR ALTER solo se
    #    compilan (SET NOEXEC ON).
    pwsh ./src/OrionERP.Infrastructure/Features/ReportesFinancieros/Sql/20260907_fiscal_declaracion_deploy.ps1

    # 2. Aplicar de verdad a produccion
    pwsh ./src/OrionERP.Infrastructure/Features/ReportesFinancieros/Sql/20260907_fiscal_declaracion_deploy.ps1 -Apply

  Parametros:
    -DatabaseName   base destino (default: grupocarpio)
    -Apply          ejecuta los cambios; sin el, es simulacro
    -Scripts        lista ordenada de rutas .sql a correr (default: el juego
                    fiscal: schema + objetos). Los scripts de limpieza de
                    cuentas duplicadas ya se aplicaron en grupocarpio; para
                    re-correrlos, pasalos aqui explicitamente.

  Que hace cada default:
    20260907_fiscal_declaracion_schema.sql   esquema fiscal.* (idempotente, con
                                             guarda ExpectedDatabase/ApplyChanges).
                                             Agrega fiscal.DeclaracionIsrProvisional
                                             y las columnas de cuentas de ISR en
                                             fiscal.EjercicioFiscal.
    20260907_fiscal_declaracion_objetos.sql  funciones y SPs (CREATE OR ALTER):
                                             - conciliacion 118-01 neta de notas
                                               de credito y de polizas de cierre
                                             - renglon de ISR contra la provision
                                             - hallazgo "Ingreso contable sin CFDI"
                                             - fn_Isr_Provisional (calculo unico)
                                             - Generar_Poliza_Isr + 7o conjunto de
                                               Rpt_Declaracion_Mensual (asiento de
                                               provision de ISR del mes)
    20260911_declaracion_previa_show_excluded.sql
                                             - conserva visibles los CFDI con X
                                               para poder incluirlos de nuevo
#>
[CmdletBinding()]
param(
  [string]$DatabaseName = 'grupocarpio',
  [switch]$Apply,
  [string[]]$Scripts
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$here = $PSScriptRoot
if (-not $Scripts -or $Scripts.Count -eq 0) {
  $Scripts = @(
    (Join-Path $here '20260907_fiscal_declaracion_schema.sql'),
    (Join-Path $here '20260907_fiscal_declaracion_objetos.sql'),
    (Join-Path $here '..\..\Cfdi\DeclaracionPrevia\Sql\20260911_declaracion_previa_show_excluded.sql')
  )
}

$raw = [Environment]::GetEnvironmentVariable('ASPNETCORE_ConnectionStrings__OrionDb', 'Process')
if ([string]::IsNullOrWhiteSpace($raw)) {
  $raw = [Environment]::GetEnvironmentVariable('ASPNETCORE_ConnectionStrings__OrionDb', 'Machine')
}
if ([string]::IsNullOrWhiteSpace($raw)) {
  throw 'Falta la variable de entorno ASPNETCORE_ConnectionStrings__OrionDb.'
}

$csb = [System.Data.SqlClient.SqlConnectionStringBuilder]::new($raw)
$server = $csb.DataSource
$user   = $csb.UserID
if ([string]::IsNullOrWhiteSpace($user)) {
  throw 'La cadena de conexion no trae usuario SQL; este script no usa autenticacion integrada.'
}
$env:SQLCMDPASSWORD = $csb.Password

$applyFlag = if ($Apply) { '1' } else { '0' }
$common = @('-S', $server, '-d', $DatabaseName, '-U', $user, '-N', '-C', '-b', '-f', '65001')

Write-Host ('Destino : {0} / {1}' -f $server, $DatabaseName)
Write-Host ('Modo    : {0}' -f $(if ($Apply) { 'APLICAR' } else { 'SIMULACRO (no cambia nada)' }))
Write-Host ''

foreach ($path in $Scripts) {
  $full = [System.IO.Path]::GetFullPath($path)
  if (-not (Test-Path -LiteralPath $full)) { throw "No existe el script: $full" }

  $name = Split-Path $full -Leaf
  $body = Get-Content -LiteralPath $full -Raw
  $guarded = $body -match '\$\(ExpectedDatabase\)'

  Write-Host "== $name =="

  if ($guarded) {
    # Script con guarda: el propio .sql valida DB_NAME() y revierte con
    # ApplyChanges=0. Se le pasan las dos variables por -v.
    & sqlcmd @common -v "ExpectedDatabase=$DatabaseName" "ApplyChanges=$applyFlag" -i $full
    if ($LASTEXITCODE -ne 0) { throw "sqlcmd fallo en $name (exit $LASTEXITCODE)." }
  }
  elseif ($Apply) {
    & sqlcmd @common -i $full
    if ($LASTEXITCODE -ne 0) { throw "sqlcmd fallo en $name (exit $LASTEXITCODE)." }
  }
  else {
    # CREATE OR ALTER sin guarda: en simulacro solo se compila (NOEXEC ON
    # persiste entre lotes en la misma conexion, asi que basta ponerlo una vez).
    #
    # Ojo: si este archivo depende de columnas/tablas que agrega un script con
    # guarda anterior de esta misma lista, el simulacro las revirtio y la
    # compilacion falla con "Invalid column name". Eso NO es un error real: se
    # resuelve al correr con -Apply, que si deja el esquema. Por eso aqui la
    # falla es solo aviso.
    $tmp = [System.IO.Path]::GetTempFileName() + '.sql'
    "SET NOEXEC ON;`r`nGO`r`n" + $body | Set-Content -LiteralPath $tmp -Encoding UTF8
    try {
      $out = & sqlcmd @common -i $tmp 2>&1
      if ($LASTEXITCODE -eq 0) {
        Write-Host '   compilado OK (se aplicara con -Apply)'
      }
      else {
        Write-Warning "compilacion en simulacro con avisos (normal si depende del esquema; corre con -Apply):"
        $out | ForEach-Object { Write-Host "   $_" }
      }
    }
    finally {
      Remove-Item -LiteralPath $tmp -ErrorAction SilentlyContinue
    }
  }
  Write-Host ''
}

Write-Host 'Listo.'
if (-not $Apply) {
  Write-Host 'Simulacro: no se cambio nada. Vuelve a correr con -Apply para aplicar.'
}
