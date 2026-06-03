# Bonhomia Social Promo Video

This workspace produces a vertical 9:16 promotional video for the public Bonhomia Suites website and reservation flow in `src/OrionERP.Bonhomia.Web`.

It is intentionally self-contained: source scripts and storyboard live here, while generated screenshots, audio and MP4 files are written to `tools/video-marketing/bonhomia/artifacts/`, which is ignored by git.

## One-time setup

```powershell
cd tools/video-marketing/bonhomia
npm install
npm run install:browsers
```

## Run the local Bonhomia site

Start the public Bonhomia app from the repo root with development-safe settings. Keep real values in environment variables, user secrets or another private secret store.

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:ASPNETCORE_ConnectionStrings__OrionDb = "Server=bonhomia.ddns.net,1433;Database=Orion_Sandbox;User Id=orion;Password=<redacted>;TrustServerCertificate=True;Encrypt=True;"
$env:ASPNETCORE_BonhomiaCheckout__Environment = "Sandbox"
$env:ASPNETCORE_BonhomiaCheckout__PayPalClientId = "<paypal-sandbox-client-id>"
$env:ASPNETCORE_BonhomiaCheckout__PayPalClientSecret = "<paypal-sandbox-client-secret>"
dotnet run --project src/OrionERP.Bonhomia.Web/OrionERP.Bonhomia.Web.csproj --urls http://localhost:57474
```

## Produce the video

```powershell
cd tools/video-marketing/bonhomia
copy .env.example .env
# Fill OPENAI_API_KEY only if voiceover generation is desired.
# Optional: tune BONHOMIA_TTS_VOICE and BONHOMIA_TTS_SPEED for the delivery.
npm run capture
npm run voice
npm run music
npm run render
```

The rendered master is:

```text
tools/video-marketing/bonhomia/artifacts/final/bonhomia-social-promo-vertical.mp4
```

`npm run produce` runs capture, voice, music and render in sequence.

## Sandbox payment behavior

The capture script stops at the PayPal handoff unless these optional variables are set:

```powershell
$env:PAYPAL_SANDBOX_BUYER_EMAIL = "<sandbox-buyer-email>"
$env:PAYPAL_SANDBOX_BUYER_PASSWORD = "<sandbox-buyer-password>"
```

When the PayPal sandbox popup can be completed, the script captures the real confirmation panel. If PayPal blocks automation or buyer credentials are missing, the renderer keeps the payment scene as a secure handoff and uses the final CTA without exposing credentials.

## Validation

```powershell
npm run validate
```

Validation checks the storyboard duration, 1080x1920 format, required scene copy and obvious secret-like strings in committed config/docs.
