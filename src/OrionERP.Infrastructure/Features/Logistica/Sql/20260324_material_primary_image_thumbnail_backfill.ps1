param(
  [string]$ConnectionString,
  [int]$MaxPixels = 240,
  [int]$JpegQuality = 85
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Get-DefaultConnectionString {
  $scriptDirectory = $PSScriptRoot
  $appSettingsPath = Join-Path $scriptDirectory "..\..\..\..\OrionERP.Web\appsettings.json"
  $resolvedPath = Resolve-Path $appSettingsPath
  $appSettings = Get-Content -Path $resolvedPath -Raw | ConvertFrom-Json
  return [string]$appSettings.ConnectionStrings.OrionDb
}

function Get-JpegCodec {
  return [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() |
    Where-Object { $_.MimeType -eq "image/jpeg" } |
    Select-Object -First 1
}

function New-ThumbnailBytes {
  param(
    [byte[]]$ImageBytes,
    [int]$MaxPixels,
    [int]$JpegQuality
  )

  $inputStream = $null
  $image = $null
  $bitmap = $null
  $graphics = $null
  $outputStream = $null
  $encoderParameters = $null

  try {
    $inputStream = New-Object System.IO.MemoryStream(, $ImageBytes)
    $image = [System.Drawing.Image]::FromStream($inputStream, $true, $true)

    $scale = [Math]::Min($MaxPixels / [double]$image.Width, $MaxPixels / [double]$image.Height)
    if ($scale -gt 1.0) {
      $scale = 1.0
    }

    $targetWidth = [Math]::Max([int][Math]::Round($image.Width * $scale), 1)
    $targetHeight = [Math]::Max([int][Math]::Round($image.Height * $scale), 1)

    $bitmap = New-Object System.Drawing.Bitmap($targetWidth, $targetHeight)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.Clear([System.Drawing.Color]::White)
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $graphics.DrawImage($image, 0, 0, $targetWidth, $targetHeight)

    $jpegCodec = Get-JpegCodec
    if ($null -eq $jpegCodec) {
      throw "No se encontro el codec JPEG en System.Drawing."
    }

    $encoderParameters = New-Object System.Drawing.Imaging.EncoderParameters(1)
    $encoderParameters.Param[0] = New-Object System.Drawing.Imaging.EncoderParameter(
      [System.Drawing.Imaging.Encoder]::Quality,
      [long]$JpegQuality)

    $outputStream = New-Object System.IO.MemoryStream
    $bitmap.Save($outputStream, $jpegCodec, $encoderParameters)
    return $outputStream.ToArray()
  }
  finally {
    if ($encoderParameters) { $encoderParameters.Dispose() }
    if ($outputStream) { $outputStream.Dispose() }
    if ($graphics) { $graphics.Dispose() }
    if ($bitmap) { $bitmap.Dispose() }
    if ($image) { $image.Dispose() }
    if ($inputStream) { $inputStream.Dispose() }
  }
}

Add-Type -AssemblyName System.Drawing

if ([string]::IsNullOrWhiteSpace($ConnectionString)) {
  $ConnectionString = Get-DefaultConnectionString
}

if ([string]::IsNullOrWhiteSpace($ConnectionString)) {
  throw "No se encontro una cadena de conexion valida."
}

$connection = New-Object System.Data.SqlClient.SqlConnection $ConnectionString

try {
  $connection.Open()

  $selectCommand = $connection.CreateCommand()
  $selectCommand.CommandText = @"
SELECT
    Id,
    MaterialCode,
    PrimaryImage
FROM logistica.Material
WHERE PrimaryImage IS NOT NULL
  AND PrimaryImageThumbnail IS NULL
ORDER BY Id;
"@

  $adapter = New-Object System.Data.SqlClient.SqlDataAdapter $selectCommand
  $table = New-Object System.Data.DataTable
  [void]$adapter.Fill($table)

  Write-Host ("Materiales pendientes de miniatura: {0}" -f $table.Rows.Count)

  $updatedCount = 0
  foreach ($row in $table.Rows) {
    $materialId = [int]$row["Id"]
    $materialCode = [string]$row["MaterialCode"]
    $imageBytes = [byte[]]$row["PrimaryImage"]

    try {
      [byte[]]$thumbnailBytes = New-ThumbnailBytes -ImageBytes $imageBytes -MaxPixels $MaxPixels -JpegQuality $JpegQuality

      $updateCommand = $connection.CreateCommand()
      $updateCommand.CommandText = @"
UPDATE logistica.Material
SET PrimaryImageThumbnail = @ThumbnailBytes,
    PrimaryImageThumbnailContentType = 'image/jpeg'
WHERE Id = @MaterialId;
"@
      [void]$updateCommand.Parameters.Add("@ThumbnailBytes", [System.Data.SqlDbType]::VarBinary, -1)
      $updateCommand.Parameters["@ThumbnailBytes"].Value = $thumbnailBytes
      [void]$updateCommand.Parameters.Add("@MaterialId", [System.Data.SqlDbType]::Int)
      $updateCommand.Parameters["@MaterialId"].Value = $materialId
      [void]$updateCommand.ExecuteNonQuery()

      $updatedCount++
      Write-Host ("[{0}/{1}] Miniatura generada para {2} (Id {3})" -f $updatedCount, $table.Rows.Count, $materialCode, $materialId)
    }
    catch {
      Write-Warning ("No se pudo generar la miniatura de {0} (Id {1}). {2}" -f $materialCode, $materialId, $_.Exception.Message)
    }
  }

  Write-Host ("Miniaturas actualizadas: {0}" -f $updatedCount)
}
finally {
  if ($connection.State -ne [System.Data.ConnectionState]::Closed) {
    $connection.Close()
  }

  $connection.Dispose()
}
