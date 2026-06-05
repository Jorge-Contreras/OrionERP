import fs from "node:fs/promises";
import path from "node:path";
import {
  cleanDir,
  copyIfExists,
  ensureDir,
  isDirectRun,
  loadCampaignContext,
  repoPath,
  toForwardSlash,
  writeJson
} from "./lib.mjs";

export async function prepareAssets(args = process.argv.slice(2)) {
  const context = await loadCampaignContext(args);
  const { storyboard, brand } = context;
  const publicRoot = context.artifacts.public;
  await cleanDir(publicRoot);

  const assets = {};
  const repoImages = brand.assets?.repoImages || {};
  for (const [key, relativeSource] of Object.entries(repoImages)) {
    const extension = path.extname(relativeSource);
    const destinationRelative = `images/${key}${extension}`;
    const copied = await copyIfExists(
      repoPath(relativeSource),
      path.join(publicRoot, destinationRelative)
    );
    if (copied) {
      assets[key] = toForwardSlash(destinationRelative);
    }
  }

  const captureManifest = await readCaptureManifest(context);
  const captureAliases = storyboard.campaign?.captureAliases || {};
  if (captureManifest) {
    for (const [sceneCaptureKey, screenshotKey] of Object.entries(captureAliases)) {
      const screenshot = captureManifest.screenshots?.[screenshotKey];
      if (!screenshot?.file) {
        continue;
      }

      const source = path.join(context.artifacts.captures, screenshot.file);
      const destinationRelative = `captures/${sceneCaptureKey}.png`;
      const copied = await copyIfExists(source, path.join(publicRoot, destinationRelative));
      if (copied) {
        assets[`capture:${sceneCaptureKey}`] = toForwardSlash(destinationRelative);
      }
    }
  }

  const audioRelative = "audio/narration.mp3";
  const hasNarration = await copyIfExists(
    path.join(context.artifacts.audio, "narration.mp3"),
    path.join(publicRoot, audioRelative)
  );

  const musicManifest = await readAudioManifest(context, "music-manifest.json");
  const musicFile = musicManifest?.output || "house-bed.wav";
  const musicRelative = `audio/${musicFile}`;
  const hasMusic = await copyIfExists(
    path.join(context.artifacts.audio, musicFile),
    path.join(publicRoot, musicRelative)
  );

  const scenes = storyboard.scenes.map((scene) => ({
    ...scene,
    asset: assets[`capture:${scene.captureKey}`] || assets[scene.assetKey] || assets.hero,
    source: assets[`capture:${scene.captureKey}`] ? "capture" : "repo"
  }));

  const props = {
    ...storyboard.format,
    campaignId: context.campaignId,
    brandId: context.brandId,
    brand: {
      name: brand.name,
      publicBaseUrl: brand.publicBaseUrl,
      disclosures: brand.disclosures || {}
    },
    metadata: {
      ...storyboard.metadata,
      voiceDisclosure: storyboard.metadata?.voiceDisclosure || brand.disclosures?.aiVoice
    },
    scenes,
    logo: assets.logo,
    narrationAudio: hasNarration ? audioRelative : null,
    musicAudio: hasMusic ? musicRelative : null,
    musicStatus: musicManifest?.status || "missing",
    paymentOutcome: captureManifest?.paymentOutcome || "no_capture",
    captureNotes: captureManifest?.notes || []
  };

  await ensureDir(context.artifacts.root);
  await writeJson(path.join(context.artifacts.root, "render-props.json"), props);
  return { context, props };
}

async function readCaptureManifest(context) {
  try {
    const raw = await fs.readFile(path.join(context.artifacts.captures, "manifest.json"), "utf8");
    return JSON.parse(raw);
  } catch {
    return null;
  }
}

async function readAudioManifest(context, fileName) {
  try {
    const raw = await fs.readFile(path.join(context.artifacts.audio, fileName), "utf8");
    return JSON.parse(raw);
  } catch {
    return null;
  }
}

if (isDirectRun(import.meta.url)) {
  const { context, props } = await prepareAssets();
  console.log(`Prepared ${props.scenes.length} scenes for ${context.campaignId} in ${context.artifacts.public}`);
}
