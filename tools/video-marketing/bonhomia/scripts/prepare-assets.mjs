import fs from "node:fs/promises";
import path from "node:path";
import {
  artifactRoot,
  cleanDir,
  copyIfExists,
  ensureDir,
  isDirectRun,
  publicRoot,
  readJson,
  repoRoot,
  toForwardSlash,
  writeJson
} from "./lib.mjs";

const repoImages = {
  hero: "src/OrionERP.Bonhomia.Web/wwwroot/Images/Bonhomia/hero-penthouse.jpg",
  landing: "src/OrionERP.Bonhomia.Web/wwwroot/Images/Bonhomia/exterior-main.jpg",
  suites: "src/OrionERP.Bonhomia.Web/wwwroot/Images/Bonhomia/suites/manhattan/01.jpg",
  services: "src/OrionERP.Bonhomia.Web/wwwroot/Images/Bonhomia/gallery-kitchen.jpg",
  calendar: "src/OrionERP.Bonhomia.Web/wwwroot/Images/Bonhomia/catalog-booking.png",
  suite: "src/OrionERP.Bonhomia.Web/wwwroot/Images/Bonhomia/suites/penthouse/01.jpg",
  extras: "src/OrionERP.Bonhomia.Web/wwwroot/Images/Bonhomia/catalog-extras.png",
  payment: "src/OrionERP.Bonhomia.Web/wwwroot/Images/Bonhomia/catalog-rates.png",
  confirmation: "src/OrionERP.Bonhomia.Web/wwwroot/Images/Bonhomia/welcome-detail.png",
  logo: "src/OrionERP.Bonhomia.Web/wwwroot/Images/Bonhomia/logo-vertical.png"
};

const captureAliases = {
  landing: "landing",
  suites: "suites",
  services: "services",
  "reservation-dates": "reservation-dates-filled",
  "reservation-suite": "reservation-suite",
  "reservation-extras": "reservation-extras",
  payment: "payment",
  confirmation: "confirmation"
};

export async function prepareAssets() {
  const storyboard = await readJson("config/storyboard.json");
  await cleanDir(publicRoot);

  const assets = {};
  for (const [key, relativeSource] of Object.entries(repoImages)) {
    const extension = path.extname(relativeSource);
    const destinationRelative = `images/${key}${extension}`;
    const copied = await copyIfExists(
      path.join(repoRoot, relativeSource),
      path.join(publicRoot, destinationRelative)
    );
    if (copied) {
      assets[key] = toForwardSlash(destinationRelative);
    }
  }

  const captureManifest = await readCaptureManifest();
  if (captureManifest) {
    for (const [sceneCaptureKey, screenshotKey] of Object.entries(captureAliases)) {
      const screenshot = captureManifest.screenshots?.[screenshotKey];
      if (!screenshot?.file) {
        continue;
      }

      const source = path.join(artifactRoot, "captures", screenshot.file);
      const destinationRelative = `captures/${sceneCaptureKey}.png`;
      const copied = await copyIfExists(source, path.join(publicRoot, destinationRelative));
      if (copied) {
        assets[`capture:${sceneCaptureKey}`] = toForwardSlash(destinationRelative);
      }
    }
  }

  const audioRelative = "audio/narration.mp3";
  const hasNarration = await copyIfExists(
    path.join(artifactRoot, "audio", "narration.mp3"),
    path.join(publicRoot, audioRelative)
  );
  const musicRelative = "audio/house-bed.wav";
  const hasMusic = await copyIfExists(
    path.join(artifactRoot, "audio", "house-bed.wav"),
    path.join(publicRoot, musicRelative)
  );

  const scenes = storyboard.scenes.map((scene) => ({
    ...scene,
    asset: assets[`capture:${scene.captureKey}`] || assets[scene.assetKey] || assets.hero,
    source: assets[`capture:${scene.captureKey}`] ? "capture" : "repo"
  }));

  const props = {
    ...storyboard.format,
    metadata: storyboard.metadata,
    scenes,
    logo: assets.logo,
    narrationAudio: hasNarration ? audioRelative : null,
    musicAudio: hasMusic ? musicRelative : null,
    paymentOutcome: captureManifest?.paymentOutcome || "no_capture",
    captureNotes: captureManifest?.notes || []
  };

  await ensureDir(artifactRoot);
  await writeJson(path.join(artifactRoot, "render-props.json"), props);
  return props;
}

async function readCaptureManifest() {
  try {
    const raw = await fs.readFile(path.join(artifactRoot, "captures", "manifest.json"), "utf8");
    return JSON.parse(raw);
  } catch {
    return null;
  }
}

if (isDirectRun(import.meta.url)) {
  const props = await prepareAssets();
  console.log(`Prepared ${props.scenes.length} scenes in ${publicRoot}`);
}
