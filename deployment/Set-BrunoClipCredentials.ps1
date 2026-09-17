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

# Read-Host -AsSecureString lee tecla por tecla, y en esa lectura cruda Ctrl+V no
# llega como texto sino como un caracter de control: pegar deja un solo asterisco
# y ambos campos salen identicos. Un cuadro de dialogo de Windows si acepta pegar,
# y de paso deja revelar y contar lo pegado antes de escribirlo en el registro.
function Read-Secret {
  param([string]$Title, [string]$Prompt)

  if ([Threading.Thread]::CurrentThread.GetApartmentState() -ne 'STA') {
    Write-Warning 'Consola sin STA: se usara el prompt de texto. Ahi Ctrl+V no pega; usa clic derecho.'
    $secure = Read-Host -Prompt $Prompt -AsSecureString
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
  }

  Add-Type -AssemblyName System.Windows.Forms
  Add-Type -AssemblyName System.Drawing

  $form = [Windows.Forms.Form]::new()
  $form.Text = $Title
  $form.ClientSize = [Drawing.Size]::new(544, 162)
  $form.StartPosition = 'CenterScreen'
  $form.FormBorderStyle = 'FixedDialog'
  $form.MinimizeBox = $false
  $form.MaximizeBox = $false
  $form.TopMost = $true

  $label = [Windows.Forms.Label]::new()
  $label.Text = $Prompt
  $label.Location = [Drawing.Point]::new(16, 16)
  $label.Size = [Drawing.Size]::new(512, 20)
  $form.Controls.Add($label)

  # Los manejadores de eventos corren despues, asi que los controles que tocan se
  # declaran en el ambito del script para que sigan siendo visibles con StrictMode.
  $script:secretBox = [Windows.Forms.TextBox]::new()
  $script:secretBox.Location = [Drawing.Point]::new(16, 42)
  $script:secretBox.Size = [Drawing.Size]::new(512, 26)
  $script:secretBox.UseSystemPasswordChar = $true
  $form.Controls.Add($script:secretBox)

  $script:charCount = [Windows.Forms.Label]::new()
  $script:charCount.Location = [Drawing.Point]::new(300, 78)
  $script:charCount.Size = [Drawing.Size]::new(228, 20)
  $script:charCount.TextAlign = 'MiddleRight'
  $script:charCount.Text = '0 caracteres'
  $form.Controls.Add($script:charCount)

  $reveal = [Windows.Forms.CheckBox]::new()
  $reveal.Text = 'Mostrar lo pegado'
  $reveal.Location = [Drawing.Point]::new(16, 76)
  $reveal.Size = [Drawing.Size]::new(200, 22)
  $form.Controls.Add($reveal)

  $reveal.Add_CheckedChanged({ $script:secretBox.UseSystemPasswordChar = -not $this.Checked })
  $script:secretBox.Add_TextChanged({
    $script:charCount.Text = '{0} caracteres' -f $script:secretBox.Text.Length
  })

  $ok = [Windows.Forms.Button]::new()
  $ok.Text = 'Aceptar'
  $ok.DialogResult = [Windows.Forms.DialogResult]::OK
  $ok.Location = [Drawing.Point]::new(336, 112)
  $ok.Size = [Drawing.Size]::new(92, 30)
  $form.Controls.Add($ok)

  $cancelar = [Windows.Forms.Button]::new()
  $cancelar.Text = 'Cancelar'
  $cancelar.DialogResult = [Windows.Forms.DialogResult]::Cancel
  $cancelar.Location = [Drawing.Point]::new(436, 112)
  $cancelar.Size = [Drawing.Size]::new(92, 30)
  $form.Controls.Add($cancelar)

  $form.AcceptButton = $ok
  $form.CancelButton = $cancelar
  $form.Add_Shown({ $form.Activate(); $script:secretBox.Focus() })

  try {
    if ($form.ShowDialog() -ne [Windows.Forms.DialogResult]::OK) {
      throw 'Cancelado: no se escribio nada.'
    }
    return $script:secretBox.Text
  }
  finally {
    $script:secretBox.Clear()
    $form.Dispose()
  }
}

function Assert-PegadoValido {
  param([string]$Valor, [string]$Campo)
  if ([string]::IsNullOrWhiteSpace($Valor)) {
    throw "$Campo vino vacio."
  }
  # Un Ctrl+V sobre una lectura cruda de consola deja caracteres de control, no el
  # texto. Es la senal inequivoca de que no se pego lo que se creia.
  if ($Valor -match '[\x00-\x1F]') {
    throw "$Campo trae caracteres de control: no se pego el texto. Usa el cuadro de dialogo o pega con clic derecho."
  }
  if ($Valor.Length -lt 12) {
    Write-Warning "$Campo mide solo $($Valor.Length) caracteres. Verifica que se haya pegado completo."
  }
}

Write-Host ''
Write-Host 'Perfil Live de Clip para Bruno''s' -ForegroundColor Cyan
Write-Host 'Se abre un cuadro por cada credencial. Ahi si puedes pegar con Ctrl+V.'
Write-Host ''

$apiKey = (Read-Secret -Title 'Clip - API key de produccion' -Prompt 'Pega la Clip API key (produccion):').Trim()
$apiSecret = (Read-Secret -Title 'Clip - API secret de produccion' -Prompt 'Pega el Clip API secret (produccion):').Trim()

Assert-PegadoValido -Valor $apiKey -Campo 'La API key'
Assert-PegadoValido -Valor $apiSecret -Campo 'El API secret'

# El servicio declara Environment=Live. Una llave test_ ahi no cobraria nada y la
# validacion de arranque la rechaza, asi que se detecta antes de escribirla.
if ($apiKey.StartsWith('test_', [StringComparison]::Ordinal)) {
  throw 'Esa es una llave de sandbox (prefijo test_) y el servicio corre en Live.'
}
if ($apiSecret.StartsWith('test_', [StringComparison]::Ordinal)) {
  throw 'Ese es un secreto de sandbox (prefijo test_) y el servicio corre en Live.'
}
if ($apiKey -eq $apiSecret) {
  throw 'La llave y el secreto son identicos: o se pego lo mismo dos veces, o el pegado no entro.'
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
