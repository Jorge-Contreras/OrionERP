# Bonhomia Visual Design System

This document is the art direction layer for Bonhomia marketing images. It exists because prompt-only generation and the old npm media workflow created weak "layout script" assets: generic backgrounds, dark text boxes, disconnected logo badges, repeated templates, and suite tiles that felt pasted on.

## Quality Target

Aim first for bold editorial poster quality:

- One dominant idea per image.
- Short public-facing hook, not internal brief copy.
- Confident typography and strong negative space.
- Graphic shapes with intent, not decorative filler.
- Bonhomia logo integrated into the layout, not floating in a badge.
- Real Bonhomia photos as the default canvas for normal sales/promotional images when a strong factual photo exists.

## Extracted Reference Rules

The user's provided Airbnb-style references are used for design rules only. Do not copy their exact composition, brand marks, or wording.

Reference rule set:

- Use a limited palette with high contrast.
- Build around one strong visual device: large type, circular crop, collage grid, or central brand lockup.
- Use photo crops as graphic poster canvases or strong proof modules, not small disconnected thumbnails.
- Leave purposeful negative space.
- Keep text short enough to be read in under two seconds.
- Use geometric accents sparingly and consistently.
- Let the layout feel designed before it feels decorated.

## Professional Poster Rules

- Design for a two-second phone read: headline first, CTA second, proof/brand third.
- Prefer one confident focal device over several small decorations.
- Use a grid or rail so the logo, URL, headline, and CTA feel like one system.
- Treat real Bonhomia photos as factual hero canvases for sales promos or proof modules for event/destination handoffs.
- Do not use a weak suite photo just because a suite needs demand. If the creative does not name that suite, choose a stronger approved photo or omit the room.
- For business/direct-booking assets, prefer a real property photo-led poster when no specific suite is being sold.
- Use generated or illustrated layers as atmosphere, texture, or campaign art only when a photo-led poster is not the right treatment. Do not let generation produce final typography, logo, or unsourced factual Bonhomia property imagery.
- Generated event or campaign artwork may coexist with real Bonhomia photos in the same final asset when the real photo remains a factual locked module. Use clear poster composition devices such as split panels, collage grids, transparent graphic overlays, or top/bottom layouts so generated art reads as campaign art, not as an invented suite feature.

## Photo-Led Promotional Posters

- Start from a real Bonhomia property or suite photo when the asset is a general sales image, direct-booking push, suite recommendation, or broad hospitality promotion.
- Apply premium crop, exposure, contrast, saturation, and sharpening locally; keep visible property facts intact.
- Compose deterministic Bonhomia logo, headline, benefit row, CTA, URL, and location on top.
- Use short, warm Spanish copy such as a comfort promise plus direct reservation CTA.
- If any image-editing or generative tool touches a real Bonhomia photo, reject any candidate that changes room layout, furniture, exterior facts, decor, amenities, views, or objects.

## Bonhomia Editorial Templates

### `business_direct_booking`

- Purpose: capture business travelers and companies.
- Main idea: direct booking and practical rest.
- Preferred assets: real property/exterior photo-led poster when no specific suite is named; real suite photo-led poster or suite proof module only when a named suite or strong workspace-relevant photo supports the claim.
- Avoid: scenic fake property views, long weekly date ranges, generic beige wallpaper, generic dark text-card panels, forced room photos.

### `experience_event_hook`

- Purpose: connect a verified local experience or event to staying at Bonhomia.
- Main idea: event/experience first, suite as stay option second.
- Preferred assets: event-inspired generated visual with a real suite module when relevant; use photo-led only when the message is primarily lodging/sales rather than event artwork.
- Avoid: implying generated event objects or decor are inside the suite. Generated Feria, Luciernagas, destination, or seasonal artwork can sit above, below, beside, or transparently over a suite photo as poster graphics, but should not use shadows, reflections, perspective, or room-anchored placement that makes the artwork look physically present in the real suite.

### `destination_brand_awareness`

- Purpose: brand or destination awareness without a room-specific promise.
- Main idea: Calpulalpan + Bonhomia presence.
- Preferred assets: real property photo-led poster by default; logo-only treatment is valid when the prompt explicitly asks for logo-only or no photography.
- Avoid: forcing suite photos when the strategy does not need them.

### `collage_experience_preview`

- Purpose: later multi-experience social units.
- Status: planned, not default v1.
- Preferred assets: multiple real approved photos, consistent rounded crops, central brand lockup.

## Anti-Patterns

- Floating logo badge disconnected from the layout.
- Generic dark rounded text card over a wallpaper background.
- Public creative that includes internal week dates.
- Oversized paragraph copy inside the image.
- Disconnected suite tile that fights the main idea.
- Financially motivated but visually weak suite-photo selection when the asset does not name that suite.
- Fake rooms, balconies, property exteriors, amenities, furniture, views, or layouts generated or altered by any tool.
- Text clipping, unreadable logo, low contrast, or too many competing focal points.

## Quality Gate

Each final image should pass:

- Hierarchy: one dominant message.
- Readability: hook and CTA legible at mobile size.
- Composition: balanced, intentional, editorial.
- Brand fit: premium, warm, practical, not generic.
- Suite truth: no false suite/property implication.
- Generated art boundary: generated event/campaign visuals are visually separated from real suite facts, even when they share one poster.
- Public copy: short, source-safe, no private/internal details.
- Not amateur: no kindergarten-style block layout, generic dark text-card overlay, disconnected logo badge, or pasted-on suite tile.
- Manual checks: final dimensions, safe areas, text-fit risk, contrast, logo placement, suite-module bounds, and public-claim safety.
- Reviewer policy: Codex must visually inspect final images before delivery and iterate when the asset looks repetitive, generic, clipped, misleading, or amateur.

## Codex Review Loop

For each image asset:

1. Create at least two distinct directions when the first direction risks looking repetitive.
2. Compose or generate final headline, CTA, logo, and optional real photo canvas or suite module with deliberate layout choices.
3. Inspect each candidate visually for hierarchy, readability, contrast, brand fit, composition, suite truth, and amateur signals.
4. Reject candidates with critical failures, repeated-template feel, or weak public readability.
5. Save only accepted finals and write short quality notes beside the assets.

Codex creative work should consume accepted rules from `knowledge/playbook.md` plus this design system. New lessons belong in `knowledge/lesson-inbox/` until reviewed.

If a local-event hook is not verified, do not place the specific event claim in the image. Use generic local-experience copy and keep the claim as a review risk.

## Reference Docs

- Use `docs/art-direction-references.md` for the professional reference rules and source links that inform this system.
