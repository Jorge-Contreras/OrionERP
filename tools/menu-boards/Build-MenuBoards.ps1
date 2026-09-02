<#
.SYNOPSIS
  Genera los tableros de señalización de un restaurante a partir del menú
  publicado en la base de datos.

.DESCRIPTION
  Lee restaurante.MenuItem / Product / ProductCard para el RFC indicado, extrae
  las fotografías almacenadas en varbinary, arma menu-data.js y renderiza cada
  plantilla HTML con Chrome en modo headless.

  Las plantillas se diseñan a 1920x1080 px CSS y se renderizan con
  --force-device-scale-factor=2, de modo que el PNG resultante es 3840x2160
  reales: el texto y las fotos quedan supermuestreados para una TV 4K de 50".

  Regenerar después de un cambio de precios es volver a ejecutar este script.

.EXAMPLE
  .\Build-MenuBoards.ps1
  .\Build-MenuBoards.ps1 -Rfc BRUNOS260707L26 -Database grupocarpio
#>
[CmdletBinding()]
param(
  [string] $Rfc = 'BRUNOS260707L26',
  [string] $MenuCode = 'MENU_PRINCIPAL',
  [string] $Database = 'Orion_Sandbox',
  [string] $OutputDirectory = (Join-Path $PSScriptRoot '..\..\tmp\menu-boards'),
  [switch] $SkipRender
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$connectionTemplate = $env:ASPNETCORE_ConnectionStrings__OrionDb
if ([string]::IsNullOrWhiteSpace($connectionTemplate)) {
  throw 'Falta ASPNETCORE_ConnectionStrings__OrionDb. Exporta la cadena de conexión antes de ejecutar.'
}
$connectionString = $connectionTemplate -replace 'Database=[^;]+', "Database=$Database"

$workDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
$photoDirectory = Join-Path $workDirectory 'photos'
New-Item -ItemType Directory -Force -Path $photoDirectory | Out-Null

# Los nombres de catálogo vienen del POS y traen erratas y sufijos operativos que
# no deben salir en una pantalla de sala. Este mapa solo afecta al tablero; el
# catálogo sigue siendo la fuente de verdad y las erratas se reportan al final.
$displayNames = @{
  'BRUN-SIR-01'                    = 'HAMBURGUESA DE SIRLOIN'
  'BR-CF'                          = 'CHICKEN FINGERS'
  'BRUNOS-PF'                      = 'PAPAS A LA FRANCESA'
  'BRUNOS-MGY'                     = 'MANGONADA'
  'NEG MOD'                        = 'NEGRA MODELO 355 ML'
  'BOING'                          = 'BOING DE LATA'
  'BRUNOS-MICH'                    = 'MICHELADA GRANDE 1 L'
  'BRUNOS-MICC'                    = 'MICHELADA CHICA 210 ML'
  'MCH MED'                        = 'MICHELADA MEDIANA'
  'MOD'                            = 'MODELO ESPECIAL 1 L'
  'MOD 710'                        = 'MODELO ESPECIAL 710 ML'
  'MOD 1LT'                        = 'MODELO NEGRA 1 L'
  'MD 210'                         = 'MODELITO 210 ML'
  'CZA 355 EXTRA'                  = 'CORONA EXTRA 355 ML'
  'VIC'                            = 'VICTORIA 355 ML'
  'CERVEZA MODELO ESPECIAL 355 ML' = 'MODELO ESPECIAL 355 ML'
  'BRUNOS-JRR'                     = 'JARRA DE AGUA DE FRUTA'
  'AG'                             = 'AGUA BONAFONT 600 ML'
  'TOPC'                           = 'AGUA MINERAL TOPO CHICO'
  'BRUNOS-CL355'                   = 'COCA COLA 355 ML'
  'SP'                             = 'SPRITE ZERO 300 ML'
  'PH'                             = 'PAN DE ELOTE CON HELADO'
  'WF'                             = 'WAFFLE CON HELADO'
  'HB'                             = "HELADO BRUNO'S"
  'CEBO'                           = 'AROS DE CEBOLLA'
  'PAPA'                           = 'PAPA GAJO'
  'N'                              = 'NACHOS CON TOCINO'
  'BRUNOS-ENCH'                    = 'ENCHILADAS SUIZAS'
}

# Las secciones de CIGARROS se excluyen: la Ley General para el Control del
# Tabaco (art. 23) prohíbe publicitar tabaco, y un tablero de sala es publicidad.
$excludedSections = @('CIGARROS')

Write-Host "Leyendo el menú publicado de $Rfc en $Database…" -ForegroundColor Cyan

Add-Type -AssemblyName System.Drawing
$connection = New-Object System.Data.SqlClient.SqlConnection($connectionString)
$connection.Open()

$command = $connection.CreateCommand()
$command.CommandText = @'
SELECT section.[Name] AS Section, section.SortOrder AS SectionOrder, item.SortOrder AS ItemOrder,
       product.Sku, card.[Name] AS ProductName, product.VariantName, product.Price,
       COALESCE(product.VariantImage, card.FamilyImage) AS Photo
FROM restaurante.MenuItem item
JOIN restaurante.MenuSection section ON section.Rfc = item.Rfc AND section.Id = item.MenuSectionId
JOIN restaurante.Menu menu ON menu.Rfc = section.Rfc AND menu.Id = section.MenuId
JOIN restaurante.Product product ON product.Rfc = item.Rfc AND product.Id = item.ProductId
JOIN restaurante.ProductCard card ON card.Rfc = product.Rfc AND card.Id = product.ProductCardId
WHERE item.Rfc = @Rfc AND menu.MenuCode = @MenuCode AND menu.IsPublished = 1 AND product.IsActive = 1
ORDER BY section.SortOrder, item.SortOrder, card.[Name];
'@
$command.Parameters.AddWithValue('@Rfc', $Rfc) | Out-Null
$command.Parameters.AddWithValue('@MenuCode', $MenuCode) | Out-Null

$items = [System.Collections.Generic.List[object]]::new()
$lowResolution = [System.Collections.Generic.List[object]]::new()
$reader = $command.ExecuteReader()
while ($reader.Read()) {
  $section = $reader['Section']
  if ($excludedSections -contains $section) { continue }

  $sku = [string]$reader['Sku']
  $rawName = [string]$reader['ProductName']
  $name = if ($displayNames.ContainsKey($sku)) { $displayNames[$sku] } else { $rawName }

  $photoFile = $null; $width = 0; $height = 0
  if (-not $reader.IsDBNull($reader.GetOrdinal('Photo'))) {
    $bytes = [byte[]]$reader['Photo']
    $safe = ($sku -replace '[^A-Za-z0-9]+', '-').Trim('-').ToLowerInvariant()
    $photoFile = "photos/$safe.jpg"
    [System.IO.File]::WriteAllBytes((Join-Path $workDirectory $photoFile), $bytes)
    try {
      $stream = New-Object System.IO.MemoryStream(, $bytes)
      $image = [System.Drawing.Image]::FromStream($stream)
      $width = $image.Width; $height = $image.Height
      $image.Dispose(); $stream.Dispose()
    } catch { }
  }

  # Un lado menor a 700 px se ve suave al ampliarlo en un tablero 4K; la
  # plantilla lo coloca en una tarjeta compacta en lugar de una principal.
  $tier = if ($null -eq $photoFile) { 'none' }
          elseif ([Math]::Min($width, $height) -ge 700) { 'hero' }
          elseif ([Math]::Min($width, $height) -ge 380) { 'compact' }
          else { 'text' }

  if ($tier -in @('text', 'none')) {
    $lowResolution.Add([pscustomobject]@{ Sku = $sku; Name = $rawName; Size = "$($width)x$($height)" })
  }

  $items.Add([pscustomobject]@{
    section = $section
    sectionOrder = [int]$reader['SectionOrder']
    itemOrder = [int]$reader['ItemOrder']
    sku = $sku
    name = $name
    rawName = $rawName
    price = [int][decimal]$reader['Price']
    photo = $photoFile
    width = $width
    height = $height
    tier = $tier
  })
}
$reader.Close()
$connection.Close()

Write-Host "  $($items.Count) productos en $(($items | Select-Object -ExpandProperty section -Unique).Count) secciones." -ForegroundColor Green

$payload = [pscustomobject]@{
  rfc = $Rfc
  menuCode = $MenuCode
  generatedAt = (Get-Date).ToString('yyyy-MM-dd HH:mm')
  items = $items
}
$json = $payload | ConvertTo-Json -Depth 6 -Compress
Set-Content -Path (Join-Path $workDirectory 'menu-data.js') -Value "window.MENU_DATA = $json;" -Encoding UTF8

foreach ($asset in @('board.css', 'board-comida.html', 'board-bebidas.html')) {
  Copy-Item (Join-Path $PSScriptRoot $asset) (Join-Path $workDirectory $asset) -Force
}
Copy-Item (Join-Path $PSScriptRoot 'fonts') (Join-Path $workDirectory 'fonts') -Recurse -Force
$logo = Join-Path $PSScriptRoot '..\..\src\OrionERP.Bruno.Web\wwwroot\Images\Brunos\brunos-logo.png'
if (Test-Path $logo) { Copy-Item $logo (Join-Path $workDirectory 'logo.png') -Force }

if ($SkipRender) {
  Write-Host "Datos listos en $workDirectory (render omitido)." -ForegroundColor Yellow
  return
}

$chrome = @(
  'C:\Program Files\Google\Chrome\Application\chrome.exe',
  'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe'
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $chrome) { throw 'No se encontró Chrome ni Edge para renderizar.' }

foreach ($board in @(
  @{ Template = 'board-comida.html';  Output = 'menu-principal.png' },
  @{ Template = 'board-bebidas.html'; Output = 'menu-bebidas.png' }
)) {
  $source = Join-Path $workDirectory $board.Template
  $target = Join-Path $workDirectory $board.Output
  if (Test-Path $target) { Remove-Item $target -Force }

  # --virtual-time-budget deja que Chrome termine de cargar fuentes e imágenes
  # antes de capturar; sin él la captura puede salir sin tipografía.
  & $chrome --headless --disable-gpu --hide-scrollbars --allow-file-access-from-files `
            --force-device-scale-factor=2 --window-size=1920,1080 `
            --virtual-time-budget=20000 --screenshot="$target" "file:///$($source -replace '\\','/')" 2>&1 | Out-Null

  if (-not (Test-Path $target)) { throw "El render de $($board.Template) no produjo salida." }
  $image = [System.Drawing.Image]::FromFile($target)
  "{0}: {1}x{2}  ({3} MB)" -f $board.Output, $image.Width, $image.Height, [math]::Round((Get-Item $target).Length / 1MB, 2)
  $image.Dispose()
}

if ($lowResolution.Count -gt 0) {
  Write-Host "`nFotografías por debajo de lo deseable para un tablero 4K:" -ForegroundColor Yellow
  $lowResolution | Sort-Object Name | Format-Table -AutoSize | Out-String | Write-Host
  Write-Host 'Sustituirlas en el catálogo y volver a ejecutar este script las incorpora sin cambiar el diseño.' -ForegroundColor Yellow
}

Write-Host "Tableros generados en $workDirectory" -ForegroundColor Green
