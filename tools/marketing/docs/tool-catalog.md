# Marketing Tool Catalog

This catalog explains the active `tools/marketing` commands, what each command reads, what it writes, and why it exists.

## `npm run intelligence`

Exports aggregate Bonhomia marketing intelligence from OrionERP.

- Reads: `ASPNETCORE_ConnectionStrings__OrionDb`, Bonhomia brand config, Salud Financiera stored procedure, public experience tables.
- Writes: `artifacts/intelligence/<week>/marketing-data.json` and `research-checklist.md`.
- Use when: starting a weekly strategy run or refreshing sales/occupancy context.
- Safety: aggregate metrics only; no customer PII or credentials.

## `npm run brief`

Creates the weekly strategy brief and media plan.

- Reads: intelligence export, Bonhomia strategy config, public experiences, known demand signals.
- Writes: `weekly-brief.md`, `media-plan.json`, and `review-checklist.md`.
- Use when: deciding what market to target and what media to create.
- Rule: strategy before media.

## `npm run media`

Generates supported media assets from the media plan.

- Reads: `media-plan.json`, Bonhomia logo and suite/property images from `src/OrionERP.Bonhomia.Web/wwwroot/Images/Bonhomia`, OpenAI image config, `docs/visual-design-system.md`, and `docs/art-direction-references.md`.
- Writes: `media/images/*.png`, `media-manifest.json`, `media-generation-report.md`, and `lesson-proposals.md`.
- Use when: the weekly plan asks for Facebook/Instagram image assets.
- V1 support: Facebook/Instagram feed images.
- V1 limitation: TikTok/Reels video is reported as `unsupported_v1`.

Quality workflow:

- The generator produces multiple candidates per image.
- A vision-capable review model scores hierarchy, readability, contrast, brand fit, composition, suite truthfulness, and whether the image feels amateur.
- Candidates below `MARKETING_REVIEW_MIN_SCORE` or with critical failures are rejected.
- The report lists selected candidates, scores, rejected candidates, and reasons.
- Use `--mock-openai --mock-review` for offline tests without API calls.

Image policy:

- Codex decides whether each image should be logo-only, suite-card, or logo-and-suite-card.
- Logo-only is valid when the strategy is event, destination, awareness, or brand focused.
- Business/direct-booking assets may use a brand-led poster when no specific suite is named.
- Suite photos are used when the strategy needs a lodging proof point, suite recommendation, or room-specific push that a real photo supports.
- If no suite is named, the tool prefers approved editorial suite photos over automatically choosing a low-quality room photo from financial need alone.
- OpenAI creates the campaign/background layer only.
- Suite photos and logos are deterministic locked layers composed with `sharp`.
- Suite photos must not be generatively edited or used in a way that implies false amenities, furniture, views, decor, objects, or layout.
- Specific event names, dates, venues, history, or claims require user-provided details or cited research. When unverified, final image copy is generic and the report flags the claim for review.

## `npm run lessons`

Lists lesson proposals or promotes a reviewed lesson into the playbook.

- Reads: `knowledge/lesson-inbox/*.md` or the file passed with `--promote`.
- Writes: `knowledge/playbook.md` only when `--promote` is used.
- Use when: a media or strategy run teaches something durable that should affect future work.
- Default: inbox first, promote after review.

## `npm run validate`

Checks workspace configuration and secret hygiene.

- Reads: brand config, schema, docs, scripts, playbook, and checked-in asset paths.
- Writes: nothing.
- Use when: validating the marketing workspace after edits.

## `npm run test`

Runs offline tests for the marketing workspace.

- Reads: checked-in scripts/config.
- Writes: ignored test artifacts under `artifacts/`.
- Uses: mock OpenAI image output and mock visual review scoring.
- Use when: verifying media generation without spending API calls or needing SQL Server.
