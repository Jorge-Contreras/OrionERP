# OrionERP Marketing Project

This workspace is the permanent Bonhomia Suites marketing strategy and creative production surface inside OrionERP. It is now a regular Codex project, not primarily an npm media-generation tool.

The business goal is to raise Bonhomia's overall occupancy rate to 50%.

## How To Use This Project

Start a Codex chat from the OrionERP repo root or from `tools/marketing`. In Plan Mode, give Codex a marketing prompt such as:

```text
Check how we are doing in sales and create a plan for today's marketing strategies or posts.
```

Codex should then:

1. Inspect aggregate Bonhomia performance when sales strategy is requested.
2. Research current local demand drivers such as Feria de Calpulalpan, Luciernagas season, business travel, tourism, and configured public experiences.
3. Recommend the strategy before making creative.
4. Propose the media mix, captions, hooks, specs, and review risks.
5. Create approved assets directly as a Codex creative pass.
6. Save deliverables under ignored `tools/marketing/artifacts/` folders.
7. Show strategy, final paths, previews when possible, cited sources, and quality notes.

The detailed operating guide lives in `docs/codex-workflow.md`.

## Core Context

- Active brand: Bonhomia Suites.
- Main KPI: 50% overall occupancy.
- Audience priority: business travelers/companies, BnB travelers/tourists, families, couples, event visitors.
- Public website: `https://bonhomiasuites.com`.
- Bonhomia assets: `src/OrionERP.Bonhomia.Web/wwwroot/Images/Bonhomia`.
- Art direction: `docs/visual-design-system.md`.
- Durable lessons: `knowledge/playbook.md`.

Generated reports, images, captures, audio, and private assets belong in ignored `tools/marketing/artifacts/` folders. Keep SQL passwords, API keys, PayPal credentials, customer PII, payment references, and connection strings out of tracked files.

## Official Data Sources

Use Salud Financiera as the official financial source:

- UI: `src/OrionERP.Web/Features/ReportesFinancieros/SaludEmpresa/SaludEmpresaPage.razor`.
- Service: `IReportesFinancierosService.GetSaludEmpresaAsync`.
- Stored procedure: `reporteFinanciero.Reporte_Salud_Empresa`.

Use public Bonhomia experiences from:

- `IReservacionExperiencesService.GetPublicExperienceCatalogAsync`.

Production aggregate data is allowed for marketing intelligence when the configured OrionDb connection points to production. Export aggregate metrics only: occupancy, ADR, RevPAR, room revenue, suite performance, cash flow, financial breakdown, and data-quality notes.

## Local Environment

No npm setup is required for the default Codex workflow.

When live aggregate data is needed, provide the OrionDb connection through environment variables, user secrets, deployment configuration, or a local ignored `.env`. Never commit real credentials.

```powershell
$env:ASPNETCORE_ConnectionStrings__OrionDb = "Server=bonhomia.ddns.net,1433;Database=grupocarpio;User Id=orion;Password=<redacted>;TrustServerCertificate=True;Encrypt=True;"
```

Use `MARKETING_RFC` only when you need to override the Bonhomia default RFC from `BonhomiaCheckout:AccountingRfc`.

## Codex Creative Direction

Codex should create assets directly instead of defaulting to `npm run media`.

For images:

- Default Facebook/Instagram feed size: `1080x1350`.
- Use one dominant idea, short Spanish copy, clear CTA, and integrated Bonhomia branding.
- Use real Bonhomia photos and logos from the repo when factual lodging proof matters.
- Use event-first editorial art when the event is the hook and a room photo is secondary.
- Avoid repeating the same picture with different text.
- Inspect files visually before delivery.
- Cite sources for event dates, venues, history, and seasonal claims.

For TikTok/Reels:

- Strategy, hook, script, shot list, captions, and posting notes are supported.
- Video creation is a Codex creative task when the available environment has the needed capability.
- Synthetic music remains placeholder quality unless explicitly approved.

## Learning System

- `knowledge/playbook.md` contains accepted durable rules.
- `knowledge/lesson-inbox/` stores proposed lessons that need review.
- Promote only reusable guidance into the playbook.
- Run-specific notes can stay in ignored artifact reports.

## Legacy Automation

The old npm scripts are still checked in as legacy/reference helpers while the project transitions:

```powershell
npm run intelligence
npm run brief
npm run media
npm run lessons
npm run validate
npm test
```

Do not use `npm run media` as the production creative path unless the user explicitly asks to inspect, test, or revive the legacy automation. See `docs/tool-catalog.md` for legacy command notes.
