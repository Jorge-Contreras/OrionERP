# Codex Project Handoff: Bonhomia Marketing Intelligence

Point the new Codex Project to the OrionERP repo root:

```text
C:\Users\Orion\Grupo Carpio Dropbox\Grupo Orion\Software\GitHubs\Development\OrionERP
```

Do not point the project only to `tools/marketing`; the agent needs the OrionERP code, Bonhomia public site assets, Salud Financiera source, and SQL model context.

## Mission

Build a permanent marketing intelligence and generation platform for Bonhomia Suites. The business goal is to raise overall occupancy to 50%.

V1 is strategy-first. It reads aggregate production data, understands public experiences, recommends the weekly marketing strategy, produces media plans, and generates Facebook/Instagram feed images. TikTok/Reels video is still reported as unsupported in v1.

## Current Capabilities

- Bonhomia is the active brand in `tools/marketing/brands/bonhomia/brand.json`.
- The primary KPI is 50% overall occupancy.
- Audience priority is:
  1. Business travelers / companies.
  2. BnB travelers and tourists.
  3. Families.
  4. Couples.
  5. Event visitors.
- `npm run intelligence` exports aggregate-only Salud Financiera metrics and public experiences.
- `npm run brief` produces a weekly strategy brief, media plan, and review checklist.
- `npm run media` generates supported Facebook/Instagram images, reviews multiple candidates, saves accepted finals, and reports unsupported video assets.
- `docs/visual-design-system.md` defines the editorial poster quality target, extracted reference rules, templates, anti-patterns, and quality gates.
- `docs/art-direction-references.md` stores design-rule references and source links for professional hierarchy, mobile creative, grid discipline, and brand-safe composition.
- `knowledge/playbook.md` preserves durable marketing lessons from prior video work.
- The fixed Bonhomia reservation video campaign is archived, not active.

## How To Run

From the repo root:

```powershell
cd tools/marketing
npm install
npm run validate
npm run test
npm run intelligence -- --brand bonhomia --week current
npm run brief -- --brand bonhomia --week current
npm run media -- --brand bonhomia --week current --media "2 Facebook images, 1 TikTok video"
```

Artifacts are written to:

```text
tools/marketing/artifacts/intelligence/<week>/
```

## Required Local Environment

Use environment variables or a local `.env`; never commit secrets.

```powershell
$env:ASPNETCORE_ConnectionStrings__OrionDb = "Server=bonhomia.ddns.net,1433;Database=grupocarpio;User Id=orion;Password=<redacted>;TrustServerCertificate=True;Encrypt=True;"
$env:MARKETING_BRAND = "bonhomia"
$env:MARKETING_WEEK = "current"
```

`MARKETING_RFC` can override the default Bonhomia RFC when needed. The default comes from `BonhomiaCheckout:AccountingRfc`.

## Official Data Sources

- Financial UI: `src/OrionERP.Web/Features/ReportesFinancieros/SaludEmpresa/SaludEmpresaPage.razor`.
- Financial service: `IReportesFinancierosService.GetSaludEmpresaAsync`.
- Financial stored procedure: `reporteFinanciero.Reporte_Salud_Empresa`.
- Experience service: `IReservacionExperiencesService.GetPublicExperienceCatalogAsync`.

Only aggregate marketing metrics may be exported. Do not write customer names, reservation-level rows, payment details, SQL credentials, API keys, or connection strings.

## Recommended Prompt For The New Project

```text
Today we will work on this week's marketing strategy plan and media generation.
Check how our sales are going, research what's coming this week, and tell me what market we can hit.
Create this marketing material [2 Facebook images, 1 TikTok video].
```

Expected V1 response:

- Explain occupancy status and the gap to 50%.
- Identify the priority audience for the week.
- Reference active or upcoming experiences such as Luciernagas when relevant.
- Ask for or perform public research on Google/Facebook/Instagram before event-specific claims.
- Recommend strategy before media.
- Produce concepts, specs, captions, hooks, review checklist, and supported Facebook/Instagram image files.
- Report TikTok/Reels video as unsupported in v1 while preserving the concept for future generation.

## Image Generation Rules

- Use OpenAI for campaign/event/background visuals.
- Use `sharp` to compose final images with deterministic text, logo, and optional suite-photo cards.
- Generate multiple candidates per image and reject outputs that fail hierarchy, readability, contrast, brand fit, composition, suite truth, or the "not amateur" check.
- Keep public image hooks short. Do not place internal week dates or long strategy copy in the creative.
- Logo-only is valid when the strategy does not need suite imagery.
- Suite photos are optional strategy assets, not mandatory decoration.
- Business/direct-booking images can be brand-led posters when no suite is named or the available room photo does not support the claim.
- Financial need may suggest what to promote, but visual quality and factual fit decide whether a room photo belongs in the ad.
- When used, suite photos must come from `src/OrionERP.Bonhomia.Web/wwwroot/Images/Bonhomia`.
- Suite photos may be cropped or lightly enhanced, but must not be generatively edited.
- Generated visuals may appear around suite photos, but must not imply false suite amenities, objects, artwork, views, furniture, decor, or room layout.
- Specific event claims require prompt-provided details or cited research before publishing.
- When an event hook is not verified, keep final image copy generic and preserve the specific claim as a review risk in the report.

## What Remains To Build

- Google/Facebook/Instagram/TikTok research connectors or approved manual research workflow.
- More advanced image variants, platform-specific crops, and automated A/B creative testing.
- Flexible TikTok/Reels video generator that can build different concepts, not repeat one fixed walkthrough.
- Licensed music library selection and metadata management.
- ElevenLabs voice provider production workflow.
- Social publishing integrations after human approval.
- Marketing analytics ingestion from Meta, TikTok, Google Search, and website conversions.
- Budget recommendation logic based on occupancy gap, ADR, RevPAR, and cash flow.

## Lessons Already Preserved

- Do not over-zoom website screenshots; readability beats energetic crop.
- Use phone-frame previews for browser captures.
- PayPal iframe automation is unreliable; explain the handoff instead of implying access.
- Bonhomia Spanish voice should be relaxed, faster, and less formal.
- Synthetic music is placeholder quality unless explicitly approved.
- Keep generated media and private assets out of git.
- Preserve run/capture manifests after partial failures.
