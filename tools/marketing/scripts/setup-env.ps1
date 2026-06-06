param(
  [string]$OpenAiApiKey = $env:OPENAI_API_KEY,
  [string]$OrionDbConnectionString = $(if ($env:MARKETING_ORIONDB_CONNECTION_STRING) { $env:MARKETING_ORIONDB_CONNECTION_STRING } elseif ($env:ASPNETCORE_ConnectionStrings__OrionDb) { $env:ASPNETCORE_ConnectionStrings__OrionDb } else { $env:ConnectionStrings__OrionDb }),
  [switch]$Force
)

$ErrorActionPreference = "Stop"

$ToolRoot = Split-Path -Parent $PSScriptRoot
$EnvPath = Join-Path $ToolRoot ".env"

function Read-RequiredSecret {
  param(
    [string]$Prompt
  )

  $secure = Read-Host -Prompt $Prompt -AsSecureString
  if ($secure.Length -eq 0) {
    throw "Missing required value for $Prompt."
  }

  $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
  try {
    return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
  } finally {
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
  }
}

if ((Test-Path -LiteralPath $EnvPath) -and -not $Force) {
  throw ".env already exists at $EnvPath. Re-run with -Force to replace it."
}

if ([string]::IsNullOrWhiteSpace($OpenAiApiKey)) {
  $OpenAiApiKey = Read-RequiredSecret "OPENAI_API_KEY"
}

if ([string]::IsNullOrWhiteSpace($OrionDbConnectionString)) {
  $OrionDbConnectionString = Read-RequiredSecret "ASPNETCORE_ConnectionStrings__OrionDb"
}

$lines = @(
  "MARKETING_BRAND=bonhomia",
  "MARKETING_WEEK=current",
  "MARKETING_RFC=OHM191112Q26",
  "MARKETING_OCCUPANCY_TARGET=50",
  "",
  "# Local secret values. This file is ignored by git.",
  "ASPNETCORE_ConnectionStrings__OrionDb=$OrionDbConnectionString",
  "MARKETING_ORIONDB_CONNECTION_STRING=",
  "OPENAI_API_KEY=$OpenAiApiKey",
  "",
  "# Production image-generation defaults.",
  "MARKETING_IMAGE_MODEL=gpt-image-2",
  "MARKETING_IMAGE_FALLBACK_MODEL=gpt-image-1",
  "MARKETING_IMAGE_QUALITY=high",
  "MARKETING_IMAGE_PHOTO_MODE=deterministic",
  "MARKETING_IMAGE_BACKGROUND_SIZE=1280x1600",
  "MARKETING_IMAGE_WIDTH=1080",
  "MARKETING_IMAGE_HEIGHT=1350",
  "MARKETING_IMAGE_MOCK=0",
  "MARKETING_REVIEW_MODEL=gpt-5-mini",
  "MARKETING_REVIEW_MIN_SCORE=82",
  "MARKETING_REVIEW_MAX_ATTEMPTS=6",
  "MARKETING_REVIEW_CANDIDATES=4",
  "MARKETING_REVIEW_STRICT=1",
  "MARKETING_REVIEW_MOCK=0",
  "MARKETING_ALLOW_HEURISTIC_FINAL=0",
  "",
  "# Future media-generation provider slots.",
  "MARKETING_VOICE_PROVIDER=openai",
  "MARKETING_MUSIC_LIBRARY_ROOT=",
  "OPENAI_TTS_VOICE=nova",
  "OPENAI_TTS_SPEED=1.08",
  "ELEVENLABS_API_KEY=",
  "ELEVENLABS_VOICE_ID=",
  "ELEVENLABS_MODEL_ID=eleven_multilingual_v2",
  "ELEVENLABS_OUTPUT_FORMAT=mp3_44100_128"
)

Set-Content -LiteralPath $EnvPath -Value $lines -Encoding UTF8
Write-Host "Wrote local marketing .env at $EnvPath"
Write-Host "No secret values were printed. The file is ignored by git."
