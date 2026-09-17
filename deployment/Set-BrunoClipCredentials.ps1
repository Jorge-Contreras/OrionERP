<#
.SYNOPSIS
  Instala el perfil Live de Clip en el bloque Environment del servicio
  OrionERP.Bruno.

.DESCRIPTION
  En produccion la seccion RestaurantCheckout no existe en ningun archivo: el
  publicado preserva appsettings*.json (el desplegado es de julio y no la trae) y
  la plantilla deployment\public-sites\brunos-main.json tampoco. Todo tiene que
  venir del bloque Environment del servicio, asi que ademas de las credenciales
  este script instala PublicBaseUrl -sin la cual la validacion de produccion
  impide el arranque- y las versiones legales vigentes, que por omision dirian
  online-orders-v1 y no coincidirian con lo que el sitio le muestra al cliente.

  Las llaves se piden por consola como SecureString: no se escriben en el
  historial, no se muestran en pantalla y no quedan en el repositorio.

  El script es aditivo e idempotente: conserva las demas variables del servicio y
  vuelve a ejecutarse sin efectos secundarios.

.EXAMPLE
  .\deployment\Set-BrunoClipCredentials.ps1

.EXAMPLE
  .\deployment\Set-BrunoClipCredentials.ps1 -RemovePayPal -RestartService
#>
[CmdletBinding()]
param(
  # Quita las tres variables de PayPal, que el codigo nuevo ya no lee.
  [switch]$RemovePayPal,

  # Reinicia el servicio al terminar. No hace falta si vas a publicar enseguida:
  # el publicado lo reinicia de todos modos.
  [switch]$RestartService,

  [string]$ServiceName = 'OrionERP.Bruno',

  # Solo para ensayar la fusion contra una copia en HKCU sin tocar el servicio.
  # Por omision apunta al bloque Environment real, que vive en HKLM.
  [string]$ServiceKeyPath,

  [string]$PublicBaseUrl = 'https://brunosgarden.com',
  [string]$TermsVersion = '2026-09-14',
  [string]$PrivacyVersion = '2026-09-14'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$serviceKey = $ServiceKeyPath
if ([string]::IsNullOrWhiteSpace($serviceKey)) {
  $serviceKey = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
}

# La elevacion hace falta por HKLM, no por el script: un ensayo contra HKCU no la
# necesita y exigirla ahi solo impediria probar la fusion antes de usarla.
if ($serviceKey -like 'HKLM:*') {
  $identity = [Security.Principal.WindowsPrincipal]::new(
    [Security.Principal.WindowsIdentity]::GetCurrent())
  if (-not $identity.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Escribir en HKLM exige una consola elevada. Abre PowerShell como Administrador.'
  }
}

if (-not (Test-Path $serviceKey)) {
  throw "No existe la clave de registro '$serviceKey'."
}

function Read-Plain {
  param([string]$Prompt)
  $secure = Read-Host -Prompt $Prompt -AsSecureString
  $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
  try { [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) }
  finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
}

Write-Host ''
Write-Host 'Perfil Live de Clip para Bruno''s' -ForegroundColor Cyan
Write-Host 'Pegalas del Panel de Desarrollador. No se muestran al teclear.'
Write-Host ''

$apiKey = (Read-Plain -Prompt 'Clip API key (produccion)').Trim()
$apiSecret = (Read-Plain -Prompt 'Clip API secret (produccion)').Trim()

if ([string]::IsNullOrWhiteSpace($apiKey)) { throw 'La API key viene vacia.' }
if ([string]::IsNullOrWhiteSpace($apiSecret)) { throw 'El API secret viene vacio.' }

# El servicio declara Environment=Live. Una llave test_ ahi no cobraria nada y la
# validacion de arranque la rechaza, asi que se detecta antes de escribirla.
if ($apiKey.StartsWith('test_', [StringComparison]::Ordinal)) {
  throw 'Esa es una llave de sandbox (prefijo test_) y el servicio corre en Live.'
}
if ($apiSecret.StartsWith('test_', [StringComparison]::Ordinal)) {
  throw 'Ese es un secreto de sandbox (prefijo test_) y el servicio corre en Live.'
}
if ($apiKey -eq $apiSecret) {
  throw 'La llave y el secreto son iguales: revisa cual pegaste en cada campo.'
}

$desired = [ordered]@{
  'ASPNETCORE_RestaurantCheckout__Environment'    = 'Live'
  'ASPNETCORE_RestaurantCheckout__ClipApiKey'     = $apiKey
  'ASPNETCORE_RestaurantCheckout__ClipApiSecret'  = $apiSecret
  'ASPNETCORE_RestaurantCheckout__PublicBaseUrl'  = $PublicBaseUrl
  'ASPNETCORE_RestaurantCheckout__TermsVersion'   = $TermsVersion
  'ASPNETCORE_RestaurantCheckout__PrivacyVersion' = $PrivacyVersion
}

$obsolete = @(
  'ASPNETCORE_RestaurantCheckout__PayPalClientId',
  'ASPNETCORE_RestaurantCheckout__PayPalClientSecret',
  'ASPNETCORE_RestaurantCheckout__PayPalWebhookId'
)

$existing = @()
$current = Get-ItemProperty -Path $serviceKey -Name Environment -ErrorAction SilentlyContinue
if ($null -ne $current -and $null -ne $current.Environment) {
  $existing = @($current.Environment)
}

# Se conserva el orden original y solo se reemplaza el valor de las variables
# tocadas; el resto del bloque (cadena de conexion, Graph, puertos) no se mueve.
$result = [System.Collections.Generic.List[string]]::new()
$seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($entry in $existing) {
  $name = ($entry -split '=', 2)[0]
  if ($RemovePayPal -and $obsolete -contains $name) { continue }
  if ($desired.Contains($name)) {
    [void]$result.Add("$name=$($desired[$name])")
    [void]$seen.Add($name)
    continue
  }
  [void]$result.Add($entry)
}
foreach ($name in $desired.Keys) {
  if (-not $seen.Contains($name)) { [void]$result.Add("$name=$($desired[$name])") }
}

Set-ItemProperty -Path $serviceKey -Name Environment -Value ([string[]]$result.ToArray()) -Type MultiString

# Se relee del registro en vez de confiar en la variable en memoria: lo que vale
# es lo que el servicio va a leer al arrancar.
$verify = @((Get-ItemProperty -Path $serviceKey -Name Environment).Environment)
Write-Host ''
Write-Host 'Bloque Environment del servicio:' -ForegroundColor Cyan
foreach ($entry in $verify) {
  $parts = $entry -split '=', 2
  $name = $parts[0]
  $value = ''
  if ($parts.Count -gt 1) { $value = $parts[1] }
  if ($name -match 'Secret|ApiKey|Password|ClientId') {
    $shown = "<$($value.Length) caracteres>"
  } else {
    $shown = $value
  }
  $color = 'Gray'
  if ($desired.Contains($name)) { $color = 'Green' }
  Write-Host ("  {0,-52} {1}" -f $name, $shown) -ForegroundColor $color
}

$missing = @($desired.Keys | Where-Object { $verify -notcontains "$_=$($desired[$_])" })
if ($missing.Count -gt 0) {
  throw "No quedaron escritas: $($missing -join ', ')"
}
Write-Host ''
Write-Host 'Las seis variables de RestaurantCheckout quedaron instaladas.' -ForegroundColor Green

if ($RemovePayPal) {
  Write-Host 'Las tres variables de PayPal fueron retiradas.' -ForegroundColor Green
} else {
  $stale = @($verify | Where-Object { ($_ -split '=', 2)[0] -in $obsolete })
  if ($stale.Count -gt 0) {
    Write-Host ''
    Write-Host ("Siguen {0} variables de PayPal que el codigo nuevo ya no lee." -f $stale.Count) -ForegroundColor Yellow
    Write-Host 'Vuelve a correr con -RemovePayPal para quitarlas.' -ForegroundColor Yellow
  }
}

if ($RestartService) {
  Write-Host ''
  Write-Host "Reiniciando $ServiceName ..." -ForegroundColor Cyan
  Restart-Service -Name $ServiceName -Force
  Write-Host ("Estado: {0}" -f (Get-Service -Name $ServiceName).Status) -ForegroundColor Green
} else {
  Write-Host ''
  Write-Host 'El servicio NO se reinicio: el bloque Environment se lee al arrancar,' -ForegroundColor Yellow
  Write-Host 'asi que esto surte efecto con el publicado (que ya lo reinicia) o con' -ForegroundColor Yellow
  Write-Host '-RestartService.' -ForegroundColor Yellow
}
