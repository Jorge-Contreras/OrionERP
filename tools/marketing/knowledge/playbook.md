# OrionERP Marketing Playbook

This file stores accepted, durable rules for marketing video editing, graphic design, capture automation, and review quality. Keep it curated: run reports can propose lessons, but only promote guidance that should influence future campaigns.

## Production Defaults

- Keep marketing tooling inside this repo when campaigns depend on OrionERP or Bonhomia code, routes, data flows, or checked-in assets.
- Generated MP4s, captures, audio, review stills, and run reports belong in ignored `artifacts/` folders.
- Secrets stay in environment variables, user secrets, deployment configuration, or another private secret store.
- V1 output is local-with-review: generate a complete review package, then get human approval before publishing.
- Bonhomia is the active brand until the user explicitly resumes OrionERP or Grupo Orion marketing.
- The primary Bonhomia KPI is overall occupancy, with a target of 50%.
- This is now a Codex-led marketing project, not an npm media-generation tool. Codex should plan, research, create, inspect, and report directly.
- Use legacy npm scripts only when the user explicitly asks to test, inspect, or revive old automation.

## Marketing Intelligence Rules

- Strategy comes before media generation. Inspect performance, experiences, and demand drivers before recommending images or videos.
- Use Salud Financiera as the official financial source for Bonhomia marketing decisions.
- Export aggregate metrics only: occupancy, ADR, RevPAR, room revenue, suite performance, cash flow, financial breakdown, and data quality.
- Never write customer PII, reservation-level rows, payment details, SQL credentials, API keys, or connection strings into marketing artifacts.
- Audience priority is business travelers / companies first, then BnB travelers and tourists, families, couples, and event visitors.
- Use Google Search, Facebook, Instagram, and TikTok as the default platform set. TikTok is important because video generation has been the main missing capability.
- For local hooks such as Feria de Calpulalpan, San Antonio de Padua, or Luciernagas, research and verify public details before publishing specific claims.
- Production aggregate data is allowed for strategy when the configured OrionDb connection points to production.

## Video And Capture Rules

- For website walkthroughs, show real browser captures in a readable phone-frame layout unless a storyboard explicitly asks for a close crop.
- Do not over-zoom screenshots. If users cannot distinguish the site, the shot fails even if the crop looks energetic.
- Preserve capture manifests even after partial failures so render and review can explain what happened.
- If PayPal iframe or popup automation fails, capture the secure handoff and explain the payment step in narration/captions. Do not imply access to iframe internals.
- Use review stills from several timestamps to check crops, caption placement, UI readability, and brand polish before sharing a final MP4.

## Voice And Script Rules

- Bonhomia Spanish social videos should sound relaxed, warm, direct, and quick. Avoid formal corporate phrasing.
- Prefer a faster social-video cadence for short reels; the current OpenAI baseline is `nova` at speed `1.08`.
- Keep required AI voice disclosure visible in video metadata/captions and on-screen when applicable.

## Music Rules

- Treat synthetic generated music as placeholder quality unless explicitly approved for publication.
- Prefer curated/licensed tracks selected by mood tags such as `house`, `warm`, `premium`, or `calm`.
- Review music as part of the publish checklist, not as an afterthought.

## Image Generation Rules

- Treat image quality as art direction plus QA, not only model choice. The default target is bold editorial poster quality.
- Use `docs/visual-design-system.md` for Bonhomia image rules, templates, anti-patterns, and review gates.
- Codex owns image production. Do not default to `npm run media` for publishable creative.
- Final creative should have one dominant idea, a short public hook, clear hierarchy, strong negative space, and an integrated logo placement.
- Do not put internal week dates, long brief copy, or strategy notes inside final creative.
- Create distinct concepts and reject weak outputs before saving finals. Track quality notes and rejection reasons in the run report when useful.
- Avoid producing the same picture with different text. Change the visual idea, composition family, asset choice, or medium when the strategy calls for multiple posts.
- Reject amateur layouts: generic dark text cards, floating logo badges, disconnected suite tiles, clipped text, low contrast, or "kindergarten-style" block composition.
- Available creative tools can create campaign, event, destination, mood, and abstract marketing visuals around Bonhomia, but they must not redraw Bonhomia logos or invent suite interiors.
- Suite photos must come from the checked-in Bonhomia public site assets and remain factual locked modules.
- Suite photos may be cropped, resized, brightened, color-corrected, sharpened, or placed in a card, but not generatively changed.
- Codex may choose logo-only image treatments when the strategy does not need suite proof.
- Use real suite-photo cards when the image recommends a stay, a business-travel benefit, or a specific suite.
- Business/direct-booking ads do not have to show a room. If no suite is named and the available photo does not prove the business-travel claim, use a brand-led editorial poster instead.
- Financial need can identify which suites deserve attention, but it should not force a weak photo into a public ad. If the creative does not name a specific suite, choose the strongest approved editorial suite photo or omit the suite module.
- Use external references as design principles: single focal point, visual hierarchy, concise mobile copy, grid/rail discipline, brand integration, and clear CTA.
- If an image promotes an event, artwork, Feria, Luciernagas, or another local hook, generated visuals may represent that hook outside the suite-photo module.
- Do not imply generated objects, amenities, art, furniture, views, decorations, or layouts exist inside a suite unless the real suite photo shows them.
- Specific event claims require user-provided details or cited research before publishing; otherwise use generic local-event language and flag for review.

## Tooling Lessons

- If system `ffmpeg` or `ffprobe` is missing, use Remotion/Node metadata checks before blocking the workflow.
- Keep provider integrations behind adapters so OpenAI, ElevenLabs, music libraries, or future APIs can be swapped without rewriting campaigns.
- Treat provider integrations as helpers, not the creative owner. Codex remains responsible for strategy, facts, art direction, visual QA, and final delivery.
