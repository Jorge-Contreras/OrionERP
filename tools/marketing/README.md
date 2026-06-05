# OrionERP Marketing Platform

This workspace produces review-ready marketing assets for OrionERP brands while staying close to the repo assets, routes, and application behavior it promotes.

V1 ships one complete campaign: `bonhomia-reservation-video`, a vertical 9:16 Bonhomia Suites reservation walkthrough with browser captures, narration, background music, MP4 render, review stills, captions, metadata, run report, checklist, and lesson proposals.

Generated media lives in `tools/marketing/artifacts/`, which is ignored by git. Keep secrets in environment variables, user secrets, deployment configuration, or another private secret store.

## One-Time Setup

```powershell
cd tools/marketing
npm install
npm run install:browsers
```

## Run The Local Bonhomia Site

Start the public Bonhomia app from the repo root with development-safe settings.

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:ASPNETCORE_ConnectionStrings__OrionDb = "Server=bonhomia.ddns.net,1433;Database=Orion_Sandbox;User Id=orion;Password=<redacted>;TrustServerCertificate=True;Encrypt=True;"
$env:ASPNETCORE_BonhomiaCheckout__Environment = "Sandbox"
$env:ASPNETCORE_BonhomiaCheckout__PayPalClientId = "<paypal-sandbox-client-id>"
$env:ASPNETCORE_BonhomiaCheckout__PayPalClientSecret = "<paypal-sandbox-client-secret>"
dotnet run --project src/OrionERP.Bonhomia.Web/OrionERP.Bonhomia.Web.csproj --urls http://localhost:57474
```

## Produce A Review Package

```powershell
cd tools/marketing
copy .env.example .env
# Fill only the values you need; do not commit .env.
npm run produce -- --campaign bonhomia-reservation-video
```

The rendered master is written to:

```text
tools/marketing/artifacts/bonhomia-reservation-video/final/bonhomia-reservation-video.mp4
```

The review package is written to:

```text
tools/marketing/artifacts/bonhomia-reservation-video/review/
```

## Useful Commands

```powershell
npm run capture -- --campaign bonhomia-reservation-video
npm run voice -- --campaign bonhomia-reservation-video
npm run music -- --campaign bonhomia-reservation-video
npm run render -- --campaign bonhomia-reservation-video
npm run review -- --campaign bonhomia-reservation-video
npm run validate -- --campaign bonhomia-reservation-video
npm run test -- --campaign bonhomia-reservation-video
```

## Providers

OpenAI TTS is the default voice provider. ElevenLabs is available as an opt-in adapter:

```powershell
$env:MARKETING_VOICE_PROVIDER = "elevenlabs"
$env:ELEVENLABS_API_KEY = "<elevenlabs-api-key>"
$env:ELEVENLABS_VOICE_ID = "<voice-id>"
```

Music prefers a curated/licensed local library. Set `MARKETING_MUSIC_LIBRARY_ROOT` to a folder containing approved tracks. If no track is found, the tool creates a clearly marked synthetic placeholder so video timing can still be reviewed.

## Learning System

- `knowledge/playbook.md` contains accepted durable guidance.
- `knowledge/lesson-inbox/` contains proposed lessons awaiting review.
- Each review package includes `lesson-proposals.md`; promote only reusable guidance into the playbook.

## PayPal Sandbox Behavior

The capture script stops at the PayPal handoff unless these optional variables are set:

```powershell
$env:PAYPAL_SANDBOX_BUYER_EMAIL = "<sandbox-buyer-email>"
$env:PAYPAL_SANDBOX_BUYER_PASSWORD = "<sandbox-buyer-password>"
```

When PayPal blocks iframe or popup automation, the renderer keeps the secure handoff scene and the review checklist flags that the payment step must be explained rather than shown as completed.
