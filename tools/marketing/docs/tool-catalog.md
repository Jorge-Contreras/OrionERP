# Legacy Marketing Command Catalog

This catalog documents the old `tools/marketing` npm commands. They are retained as reference helpers while the project transitions to a Codex-led workflow.

Default production workflow: use Codex directly. Do not run `npm run media` or the project OpenAI image pipeline unless the user explicitly asks to inspect, test, or revive the legacy automation.

For the active project workflow, see `docs/codex-workflow.md`.

## `npm run intelligence`

Legacy helper that exports aggregate Bonhomia marketing intelligence from OrionERP.

- Reads: `ASPNETCORE_ConnectionStrings__OrionDb`, Bonhomia brand config, Salud Financiera stored procedure, public experience tables.
- Writes: `artifacts/intelligence/<week>/marketing-data.json` and `research-checklist.md`.
- Safe use: refreshing aggregate sales/occupancy context when Codex needs a machine-readable data export.
- Safety: aggregate metrics only; no customer PII or credentials.

## `npm run brief`

Legacy helper that creates a weekly strategy brief and media plan.

- Reads: intelligence export, Bonhomia strategy config, public experiences, known demand signals.
- Writes: `weekly-brief.md`, `media-plan.json`, and `review-checklist.md`.
- Safe use: reference only. Codex should still own the actual strategy recommendation.
- Rule: strategy before media.

## `npm run media`

Deprecated production path. This command was an experiment in automated Facebook/Instagram image generation.

- Reads: `media-plan.json`, Bonhomia logo and suite/property images, OpenAI image config, `docs/visual-design-system.md`, and `docs/art-direction-references.md`.
- Writes: generated images, media manifest, generation report, rejected candidate evidence, and lesson proposals.
- Former support: Facebook/Instagram feed images.
- Former limitation: TikTok/Reels video reported as unsupported.

Do not use this command for normal creative production. The current preferred path is a Codex creative pass:

1. Research and plan.
2. Choose distinct creative directions.
3. Use local Bonhomia assets and current event sources.
4. Compose/generate assets directly.
5. Visually inspect the outputs.
6. Report final paths, previews, sources, and quality notes.

The old quality rules remain useful as review criteria: mobile readability, strong hierarchy, integrated logo, source-safe public copy, real factual suite/property imagery, no fake amenities, and no amateur template layouts.

## `npm run lessons`

Legacy helper that lists lesson proposals or promotes a reviewed lesson into the playbook.

- Reads: `knowledge/lesson-inbox/*.md` or the file passed with `--promote`.
- Writes: `knowledge/playbook.md` only when `--promote` is used.
- Safe use: optional. Codex may also edit lessons directly when the user asks.
- Default: inbox first, promote after review.

## `npm run validate`

Legacy workspace validation.

- Reads: brand config, docs, scripts, playbook, and checked-in asset paths.
- Writes: nothing.
- Use when: validating that the checked-in marketing workspace still has required files and secret hygiene.

## `npm test`

Legacy offline tests for the old tool workspace.

- Reads: checked-in scripts/config.
- Writes: ignored test artifacts under `artifacts/`.
- Uses: mock OpenAI image output and mock visual review scoring.
- Use when: maintaining the legacy scripts, not for normal Codex creative production.
