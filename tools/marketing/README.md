# OrionERP Marketing Intelligence

This workspace is the permanent marketing strategy surface for Bonhomia Suites inside OrionERP.

The normal workflow is strategy first:

1. Export aggregate Bonhomia performance from Salud Financiera.
2. Export public experiences and upcoming seasonal demand signals.
3. Build a weekly strategy brief aimed at raising occupancy to 50%.
4. Propose media concepts, captions, hooks, specs, and a review checklist.
5. Generate supported Facebook/Instagram images and report unsupported video assets.

Generated reports and media stay in `tools/marketing/artifacts/`, which is ignored by git. Keep SQL passwords, API keys, PayPal credentials, and private assets out of this repo.

## Setup

```powershell
cd tools/marketing
npm install
copy .env.example .env
```

Fill `.env` locally. For production aggregate intelligence, set:

```powershell
$env:ASPNETCORE_ConnectionStrings__OrionDb = "Server=bonhomia.ddns.net,1433;Database=grupocarpio;User Id=orion;Password=<redacted>;TrustServerCertificate=True;Encrypt=True;"
```

Use `MARKETING_RFC` only when you need to override the Bonhomia default RFC from `BonhomiaCheckout:AccountingRfc`.

## Weekly Commands

```powershell
npm run intelligence -- --brand bonhomia --week current
npm run brief -- --brand bonhomia --week current
npm run media -- --brand bonhomia --week current --media "2 Facebook images, 1 TikTok video"
```

Outputs are written to:

```text
tools/marketing/artifacts/intelligence/<week>/
```

Expected files:

- `marketing-data.json`: aggregate-only Salud Financiera and public experience export.
- `research-checklist.md`: manual research checklist for Google, Facebook, Instagram, and local event confirmation.
- `weekly-brief.md`: strategy recommendation before media.
- `media-plan.json`: structured asset concepts and generation inputs.
- `review-checklist.md`: publishing and data-safety checks.
- `media/images/*.png`: generated Facebook/Instagram image assets.
- `media/media-manifest.json`: generated asset sources, OpenAI model metadata, quality scores, rejected candidates, and unsupported-video entries.
- `media/media-generation-report.md`: human-readable summary of generated images, selected candidates, rejection reasons, and v1 limitations.

## Prompt Pattern

In the future Codex Project, ask from the OrionERP repo root:

```text
Today we will work on this week's marketing strategy plan and media generation.
Check how sales are going, research what's coming this week, and tell me what market we can hit.
Create this marketing material [2 Facebook images, 1 TikTok video].
```

V1 generates the strategy brief, media plan, and Facebook/Instagram feed images. TikTok/Reels video generation is not implemented yet; the tool preserves the video concept and reports it as `unsupported_v1`.

## Data Sources

- Official financial source: `IReportesFinancierosService.GetSaludEmpresaAsync`.
- Official UI: `src/OrionERP.Web/Features/ReportesFinancieros/SaludEmpresa/SaludEmpresaPage.razor`.
- Stored procedure: `reporteFinanciero.Reporte_Salud_Empresa`.
- Public experiences source: `IReservacionExperiencesService.GetPublicExperienceCatalogAsync`.
- Default Bonhomia RFC: `BonhomiaCheckout:AccountingRfc`.

Artifacts must stay aggregate-only. Do not write customer PII, reservation details, payment references, SQL credentials, API keys, or connection strings.

## Music Direction

Preferred v1 music flow is a local licensed MP3/WAV library:

```powershell
$env:MARKETING_MUSIC_LIBRARY_ROOT = "C:\ApprovedMarketingMusic"
```

Synthetic music is review-only placeholder quality. Use licensed local tracks now; evaluate low-cost APIs or AI music providers later when commercial rights and consistency are clear.

## Image Generation Direction

Image generation uses OpenAI for campaign/background visuals and `sharp` for deterministic composition. Configure:

```powershell
$env:OPENAI_API_KEY = "<openai-api-key>"
$env:MARKETING_IMAGE_MODEL = "gpt-image-2"
$env:MARKETING_IMAGE_FALLBACK_MODEL = "gpt-image-1"
$env:MARKETING_REVIEW_MODEL = "gpt-5-mini"
$env:MARKETING_REVIEW_MIN_SCORE = "82"
```

Codex chooses per asset whether a suite photo is useful. Logo-only is valid for awareness, destination, and event-style creatives. Use real suite-photo cards when the image needs lodging proof, a business-stay angle, or a specific suite recommendation.

Suite photos and logos come from `src/OrionERP.Bonhomia.Web/wwwroot/Images/Bonhomia`. Suite photos may be cropped and lightly enhanced, but not generatively edited or used in a way that implies false suite features.

The media tool uses `docs/visual-design-system.md` as the Bonhomia art-direction layer. It creates multiple candidates, reviews them with a vision-capable model, rejects weak/amateur outputs, and saves only accepted finals. Rejected candidates are not kept as final media, but their scores and reasons are written to the report.

## Learning System

- `knowledge/playbook.md` contains accepted durable rules.
- `knowledge/lesson-inbox/` stores proposed lessons from runs.
- Promote only reusable guidance into the playbook.
- `docs/tool-catalog.md` explains what every marketing command does.
- `npm run lessons` lists proposals; `npm run lessons -- --promote <file>` promotes a reviewed lesson.

The old Bonhomia reservation walkthrough was archived because it was a one-time campaign. Its useful lessons remain in the playbook.
