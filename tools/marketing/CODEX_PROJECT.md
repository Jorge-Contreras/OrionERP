# Codex Project Handoff: Bonhomia Marketing

Point the Codex Project to the OrionERP repo root:

```text
C:\Users\Orion\Grupo Carpio Dropbox\Grupo Orion\Software\GitHubs\Development\OrionERP
```

Do not point the project only to `tools/marketing`; Codex needs the OrionERP code, Bonhomia public site assets, Salud Financiera source, and SQL model context.

## Mission

Run Bonhomia Suites marketing strategy and creative production as a Codex-led workflow. The business goal is to raise overall occupancy to 50%.

This project is no longer centered on the npm media tool. Codex should do the work: inspect data, research demand, recommend strategy, create images or video concepts, inspect outputs, and report sources and quality.

## Current Capability

- Bonhomia is the active brand in `tools/marketing/brands/bonhomia/brand.json`.
- The primary KPI is 50% overall occupancy.
- Audience priority is:
  1. Business travelers / companies.
  2. BnB travelers and tourists.
  3. Families.
  4. Couples.
  5. Event visitors.
- Codex knows the official financial source, public experience source, asset folders, image quality target, privacy boundaries, and learning workflow.
- `docs/codex-workflow.md` defines the normal Codex project flow.
- `docs/visual-design-system.md` defines the Bonhomia editorial poster quality target, templates, anti-patterns, and quality gates.
- `docs/art-direction-references.md` stores design-rule references and source links for hierarchy, mobile creative, grid discipline, and brand-safe composition.
- `knowledge/playbook.md` preserves durable marketing lessons.
- The old npm scripts remain only as legacy/reference helpers.

## Recommended Prompt

Use Plan Mode for strategy prompts:

```text
Check how we are doing in sales and create a plan for today's marketing strategies or posts.
```

Expected Codex plan:

- Explain occupancy status and the gap to 50%.
- Compare against recent weeks or last year when data is available.
- Identify the priority audience for the day/week.
- Research timely local demand such as Feria de Calpulalpan, San Antonio de Padua, Luciernagas season, business travel, tourism, or configured Bonhomia experiences.
- Recommend strategy before media.
- Recommend the number and type of Facebook/Instagram images, story variants, TikTok/Reels concepts, captions, hooks, and review checks.
- Flag public claims that need official confirmation before publishing.

After approving the plan or asking Codex to create the assets, Codex should produce the deliverables directly and save them under `tools/marketing/artifacts/`.

## Official Data Sources

- Financial UI: `src/OrionERP.Web/Features/ReportesFinancieros/SaludEmpresa/SaludEmpresaPage.razor`.
- Financial service: `IReportesFinancierosService.GetSaludEmpresaAsync`.
- Financial stored procedure: `reporteFinanciero.Reporte_Salud_Empresa`.
- Experience service: `IReservacionExperiencesService.GetPublicExperienceCatalogAsync`.

Only aggregate marketing metrics may be exported. Do not write customer names, reservation-level rows, payment details, SQL credentials, API keys, or connection strings.

## Local Environment

Use environment variables or a local ignored `.env` for database access. Never commit secrets.

```powershell
$env:ASPNETCORE_ConnectionStrings__OrionDb = "Server=bonhomia.ddns.net,1433;Database=grupocarpio;User Id=orion;Password=<redacted>;TrustServerCertificate=True;Encrypt=True;"
$env:MARKETING_BRAND = "bonhomia"
$env:MARKETING_WEEK = "current"
```

`MARKETING_RFC` can override the default Bonhomia RFC when needed. The default comes from `BonhomiaCheckout:AccountingRfc`.

## Creative Rules

- Use `tools/marketing/docs/visual-design-system.md` and `tools/marketing/knowledge/playbook.md`.
- Create visually distinct concepts, not the same layout with different text.
- Keep image hooks short and mobile readable.
- Do not place internal week dates or strategy notes inside public creative.
- Use real Bonhomia photos from `src/OrionERP.Bonhomia.Web/wwwroot/Images/Bonhomia` when lodging proof matters.
- Suite/property photos may be cropped, resized, brightened, color-corrected, sharpened, or placed in a layout, but must not be generatively changed in a way that alters facts.
- Event-first editorial art is valid when the event is the hook and suite proof is secondary.
- Generated visuals must not imply false amenities, objects, artwork, views, furniture, decor, room layouts, or property features.
- Specific event claims require prompt-provided details or cited research before publishing.
- Inspect final media visually before delivery.

## Legacy Scripts

The old commands remain checked in but are not the default workflow:

```powershell
cd tools/marketing
npm run intelligence
npm run brief
npm run media
npm run lessons
npm run validate
npm test
```

Use them only when the user explicitly asks to inspect, test, or revive the old automation.

## Lessons Already Preserved

- Do not over-zoom website screenshots; readability beats energetic crop.
- Use phone-frame previews for browser captures.
- PayPal iframe automation is unreliable; explain the handoff instead of implying access.
- Bonhomia Spanish voice should be relaxed, fast, warm, and practical.
- Synthetic music is placeholder quality unless explicitly approved.
- Keep generated media and private assets out of git.
- Preserve run/capture manifests after partial failures.
