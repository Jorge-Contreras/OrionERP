# Codex Marketing Workflow

This workspace is now a regular Codex project for Bonhomia marketing strategy and creative production. It is not primarily an npm tool.

## Project Role

Codex is the marketing operator:

1. Read the user's prompt and clarify the business objective.
2. Inspect sales, occupancy, ADR, RevPAR, suite performance, cash flow, public experiences, and data quality when the task asks for strategy.
3. Research current local demand drivers before making public claims.
4. Recommend a strategy before producing media.
5. Create the approved images, captions, hooks, briefs, and review notes directly.
6. Save deliverables under ignored `artifacts/` folders and show final paths, previews when possible, sources, and quality notes.

The legacy npm scripts remain in the repository only as reference helpers. Do not run `npm run media` or the old project OpenAI image pipeline unless the user explicitly asks for legacy tool testing or maintenance.

## Normal Plan Mode Flow

When the user starts a planning chat with a prompt such as:

```text
Check how we are doing in sales and create a plan for today's marketing strategies or posts.
```

Codex should respond with a strategy plan, not immediate generic posts. The plan should usually include:

- Current performance read: occupancy, gap to the 50% target, week-over-week trend, year-over-year context when available, suite strengths/weaknesses, cash-flow sensitivity, and data-quality caveats.
- Demand context: researched local events, seasonal drivers, configured Bonhomia experiences, business-travel opportunities, tourism angles, and weather/calendar considerations when relevant.
- Recommended audience: choose from business travelers/companies, BnB travelers/tourists, families, couples, and event visitors.
- Recommended media mix: number of Facebook/Instagram images, story variants, captions, hooks, and TikTok/Reels concepts when useful.
- Rationale: explain why each proposed asset should exist and what booking behavior it should influence.
- Review risks: unverified event claims, date claims, image rights, offer/price accuracy, and anything needing human confirmation.

Example recommendation shape:

```text
Sales are below last week and below the same week last year, so we should push direct bookings. I found that Feria de Calpulalpan is active this week and Luciernagas season is about to begin. I recommend 3 Facebook/Instagram images: two Feria stay-at-Bonhomia posts and one Luciernagas early-season post, plus one short TikTok engagement concept.
```

## Creative Production Flow

After the strategy is clear and the user asks Codex to create the assets:

1. Use `docs/visual-design-system.md` and `knowledge/playbook.md` as the art-direction source.
2. Select real Bonhomia property, suite, logo, and public website assets from `src/OrionERP.Bonhomia.Web/wwwroot/Images/Bonhomia`.
3. Research event/date/history claims and cite sources in the final report.
4. Create visually distinct concepts, not the same layout with different text.
5. Compose final images at platform-appropriate sizes, usually `1080x1350` for Facebook/Instagram feed.
6. Inspect final files visually before delivery.
7. Save a lightweight quality report beside the assets.

Codex may use local image composition, browser screenshots, approved AI image-generation surfaces available in the Codex environment, or other creative tools. The important rule is that Codex owns the creative judgment, visual QA, factual safety, and final composition. The old npm media pipeline does not own production creative.

## Research Rules

- Specific event names, dates, venues, artist lineups, prices, historical claims, and official schedules need prompt-provided details or cited research.
- If a claim is not verified, keep public creative generic and flag the claim for review.
- For Feria de Calpulalpan / San Antonio de Padua, treat June 13 as the central patronal date, but verify each year's official dates before publishing.
- For Luciernagas season, verify current season dates, access rules, conservation restrictions, and travel messaging before publishing.

## Image Rules

- One dominant idea per image.
- Short Spanish hook, clear CTA, integrated Bonhomia brand, and mobile readability.
- Real Bonhomia photos are factual assets. They may be cropped, brightened, color-corrected, sharpened, or placed in a layout, but must not be generatively altered in a way that changes property facts.
- Do not imply generated event props, decorations, furniture, amenities, views, or room layouts exist inside a suite.
- Avoid generic dark text cards, pasted-on logos, weak suite tiles, clipped text, and repeated template layouts.
- Use event-first editorial art when the event is the hook; use real property/suite proof when the booking promise needs it.

## Deliverable Report

A completed Codex creative pass should usually return:

- Strategy summary.
- Final image/video/concept paths.
- Markdown previews for local images when possible.
- Captions/hooks and suggested posting notes.
- Sources used for event or seasonal claims.
- Quality report: dimensions, visual differences, readability, factual safety, and publishing caveats.

## Learning

Keep accepted durable lessons in `knowledge/playbook.md`. Put proposed or run-specific lessons in `knowledge/lesson-inbox/` when they should be reviewed before becoming permanent.
