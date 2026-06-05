import fs from "node:fs/promises";
import path from "node:path";
import { bundle } from "@remotion/bundler";
import {
  getVideoMetadata,
  renderStill,
  selectComposition
} from "@remotion/renderer";
import {
  campaignCompositionId,
  campaignOutputName,
  ensureDir,
  fileExists,
  toolRoot,
  writeJson,
  writeText
} from "./lib.mjs";
import { prepareAssets } from "./prepare-assets.mjs";

const { context, props } = await prepareAssets();
const videoPath = path.join(context.artifacts.final, campaignOutputName(context));
if (!(await fileExists(videoPath))) {
  throw new Error(`Rendered MP4 not found: ${videoPath}. Run npm run render first.`);
}

await ensureDir(context.artifacts.review);
const stillRoot = path.join(context.artifacts.review, "stills");
await ensureDir(stillRoot);

const metadata = await getVideoMetadata(videoPath);
await writeJson(path.join(context.artifacts.review, "metadata.json"), {
  campaignId: context.campaignId,
  brandId: context.brandId,
  videoPath,
  ...metadata,
  generatedAtUtc: new Date().toISOString(),
  musicStatus: props.musicStatus,
  paymentOutcome: props.paymentOutcome
});

await renderReviewStills(context, props, stillRoot);
await writeText(path.join(context.artifacts.review, "captions.txt"), buildCaptions(context, props));
await writeText(path.join(context.artifacts.review, "review-checklist.md"), buildChecklist(context, props, metadata));
await writeText(path.join(context.artifacts.review, "run-report.md"), buildRunReport(context, props, metadata));
await writeText(path.join(context.artifacts.review, "lesson-proposals.md"), buildLessonProposals(context, props));

console.log(`Review package written to ${context.artifacts.review}`);

async function renderReviewStills(currentContext, inputProps, stillRootPath) {
  const entryPoint = path.join(toolRoot, "src", "remotion", "index.jsx");
  const serveUrl = await bundle({
    entryPoint,
    outDir: path.join(currentContext.artifacts.root, "remotion-review-bundle"),
    publicDir: currentContext.artifacts.public
  });

  const composition = await selectComposition({
    serveUrl,
    id: campaignCompositionId(currentContext),
    inputProps
  });

  const frames = currentContext.storyboard.scenes.map((scene) => ({
    id: scene.id,
    frame: Math.max(0, Math.round((scene.start + scene.duration / 2) * currentContext.storyboard.format.fps))
  }));

  for (const item of frames) {
    const output = path.join(stillRootPath, `${String(item.frame).padStart(4, "0")}-${item.id}.png`);
    await renderStill({
      composition,
      serveUrl,
      frame: item.frame,
      inputProps,
      output,
      imageFormat: "png",
      chromiumOptions: { ignoreCertificateErrors: true }
    });
  }
}

function buildCaptions(currentContext, inputProps) {
  const metadata = currentContext.storyboard.metadata || {};
  const hashtags = Array.isArray(metadata.hashtags) ? metadata.hashtags.join(" ") : "";
  return [
    metadata.caption || "",
    "",
    inputProps.brand?.publicBaseUrl ? `Sitio: ${inputProps.brand.publicBaseUrl}` : "",
    hashtags,
    "",
    inputProps.metadata?.voiceDisclosure || currentContext.brand.disclosures?.aiVoice || ""
  ].filter((line, index, lines) => line || lines[index - 1]).join("\n").trim() + "\n";
}

function buildChecklist(currentContext, inputProps, metadata) {
  const fallbackMusic = inputProps.musicStatus !== "curated";
  const paypalFallback = inputProps.paymentOutcome !== "completed";
  return `# Review Checklist - ${currentContext.campaignId}

- [ ] Watch the full MP4 on a phone-sized screen.
- [ ] Confirm dimensions are 1080x1920. Detected: ${metadata.width}x${metadata.height}.
- [ ] Confirm duration is under 90 seconds. Detected: ${metadata.durationInSeconds.toFixed(2)}s.
- [ ] Confirm captions are readable and do not cover critical UI.
- [ ] Confirm screenshots show the website clearly and are not over-zoomed.
- [ ] Confirm no secrets, credentials, private DB strings, or PayPal details are visible.
- [ ] Confirm AI voice disclosure is present: ${inputProps.metadata?.voiceDisclosure || currentContext.brand.disclosures?.aiVoice || "missing"}
- [ ] Confirm PayPal is represented as a secure handoff if iframe completion was unavailable. Payment outcome: ${inputProps.paymentOutcome}.
- [ ] Confirm music is publish-ready. Music status: ${inputProps.musicStatus}${fallbackMusic ? " (replace fallback music before publishing)" : ""}.
- [ ] Confirm final caption copy and hashtags are approved.

${paypalFallback ? "Note: PayPal completion was not captured; the video should explain the secure handoff instead of implying iframe access.\n" : ""}${fallbackMusic ? "Note: fallback synthetic music is acceptable for layout tests, not final publishing quality.\n" : ""}`;
}

function buildRunReport(currentContext, inputProps, metadata) {
  const sceneLines = currentContext.storyboard.scenes
    .map((scene) => `- ${scene.id}: ${scene.duration}s, source=${inputProps.scenes.find((item) => item.id === scene.id)?.source || "unknown"}`)
    .join("\n");

  return `# Run Report - ${currentContext.campaignId}

Generated: ${new Date().toISOString()}

## Output
- Video: ${path.join(currentContext.artifacts.final, campaignOutputName(currentContext))}
- Review folder: ${currentContext.artifacts.review}
- Dimensions: ${metadata.width}x${metadata.height}
- Duration: ${metadata.durationInSeconds.toFixed(2)}s
- Video codec: ${metadata.codec}
- Audio codec: ${metadata.audioCodec || "unknown"}

## Campaign
- Brand: ${currentContext.brand.name}
- Voice: ${inputProps.narrationAudio || "missing"}
- Music: ${inputProps.musicAudio || "missing"} (${inputProps.musicStatus})
- Payment outcome: ${inputProps.paymentOutcome}

## Scenes
${sceneLines}

## Capture Notes
${inputProps.captureNotes?.length ? inputProps.captureNotes.map((note) => `- ${note}`).join("\n") : "- None"}
`;
}

function buildLessonProposals(currentContext, inputProps) {
  const proposals = [
    "Keep browser captures inside a readable phone-frame layout unless the storyboard explicitly calls for a close crop.",
    "If PayPal iframe or popup automation fails, preserve the handoff screenshot and explain secure payment in narration/captions.",
    "Treat synthetic music as placeholder only; publish-ready runs should use curated/licensed tracks.",
    "Use the review stills to check captions, crops, and UI readability before sharing a final MP4."
  ];

  if (inputProps.musicStatus === "curated") {
    proposals.push("Record which curated music mood worked well so future Bonhomia videos can reuse the sonic direction.");
  }

  return `# Lesson Proposals - ${currentContext.campaignId}

These are proposed lessons from this run. Promote only durable, repeatedly useful guidance into \`knowledge/playbook.md\`.

${proposals.map((proposal) => `- ${proposal}`).join("\n")}
`;
}
