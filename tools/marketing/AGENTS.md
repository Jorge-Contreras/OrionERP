# Marketing Agent Notes

## Scope

This folder is the Bonhomia Suites marketing intelligence and generation workspace. Bonhomia is the only active brand for now; OrionERP and Grupo Orion marketing are paused until the user explicitly resumes them.

The main business goal is to raise Bonhomia's overall occupancy rate to 50%.

## Default Workflow

When the user asks for marketing strategy or marketing material, work in this order:

1. Inspect aggregate sales, occupancy, ADR, RevPAR, suite performance, cash flow, and data quality.
2. Inspect public Bonhomia experiences and configured seasonal demand signals.
3. Research or request confirmation for local demand drivers before making event-specific claims.
4. Recommend strategy before proposing media.
5. Produce media concepts, specs, captions, hooks, and a review checklist.
6. Generate final images/videos only when the requested capability exists and the strategy is clear.

## Data Sources

Use Salud Financiera as the official financial source:

- UI: `src/OrionERP.Web/Features/ReportesFinancieros/SaludEmpresa/SaludEmpresaPage.razor`
- Service: `IReportesFinancierosService.GetSaludEmpresaAsync`
- Stored procedure: `reporteFinanciero.Reporte_Salud_Empresa`

Use public experiences from `IReservacionExperiencesService.GetPublicExperienceCatalogAsync`.

Production aggregate data is allowed for marketing intelligence when the configured OrionDb connection points to production.

## Privacy

Never write customer PII, reservation-level rows, payment references, SQL credentials, API keys, PayPal secrets, or connection strings to marketing artifacts. Export aggregate metrics only.

Generated reports, media, captures, audio, and private assets belong in ignored artifact/private folders.

## Learning

Keep durable marketing lessons in `knowledge/playbook.md`. Store new lesson proposals in `knowledge/lesson-inbox/` until accepted.

Preserve these standing lessons:

- Avoid over-zoomed screenshots.
- Favor readable phone-frame previews.
- Treat PayPal iframe capture as a handoff/explanation unless sandbox access makes completion reliable.
- Bonhomia Spanish copy should feel relaxed, fast, warm, and practical.
- Synthetic music is placeholder quality unless explicitly approved.

## Image Generation

Use `npm run media` for supported image generation. The command may use OpenAI to create campaign/event/destination visuals, but Bonhomia suite photos and logos must be deterministic locked layers from `src/OrionERP.Bonhomia.Web/wwwroot/Images/Bonhomia`.

Use `docs/visual-design-system.md` as the art-direction source for Bonhomia images. Default toward bold editorial poster quality: one idea, short hook, strong type hierarchy, purposeful negative space, integrated logo placement, and strict mobile readability.

Codex should choose the image treatment from the strategy:

- Use logo-only when a suite photo is not strategically necessary.
- Use a real suite-photo card when the strategy needs lodging proof, a suite recommendation, or a suite-specific push.
- For generic business/direct-booking creative, use a brand-led editorial poster when no suite is named or when the available room photo does not prove the business-travel claim.
- Use event/campaign visuals around suite-photo cards when the image connects a local event to staying at Bonhomia.

Financial need can guide which rooms deserve promotion, but it must not automatically force a weak room photo into final creative. If a campaign does not name a specific suite, choose an approved editorial suite photo or omit room imagery.

Do not imply that generated event props, artwork, furniture, amenities, views, decor, objects, or room layouts exist inside a suite unless they are visible in the real source photo.

Specific event names, dates, venues, history, or claims need prompt-provided details or cited research before publishing. If not verified, keep the copy generic and flag it for review.

Generate multiple candidates and reject weak outputs before saving finals. Critical rejection reasons include fake suite/property features, clipped or unreadable text, disconnected logo badges, generic dark text-card layouts, public claims without a source, and anything that feels amateur or childish.
