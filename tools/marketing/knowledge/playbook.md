# OrionERP Marketing Playbook

This file stores accepted, durable rules for marketing video editing, graphic design, capture automation, and review quality. Keep it curated: run reports can propose lessons, but only promote guidance that should influence future campaigns.

## Production Defaults

- Keep marketing tooling inside this repo when campaigns depend on OrionERP or Bonhomia code, routes, data flows, or checked-in assets.
- Generated MP4s, captures, audio, review stills, and run reports belong in ignored `artifacts/` folders.
- Secrets stay in environment variables, user secrets, deployment configuration, or another private secret store.
- V1 output is local-with-review: generate a complete review package, then get human approval before publishing.

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

## Tooling Lessons

- If system `ffmpeg` or `ffprobe` is missing, use Remotion/Node metadata checks before blocking the workflow.
- Keep provider integrations behind adapters so OpenAI, ElevenLabs, music libraries, or future APIs can be swapped without rewriting campaigns.
