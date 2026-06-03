import fs from "node:fs/promises";
import path from "node:path";
import { readJson, toolRoot } from "./lib.mjs";

const storyboard = await readJson("config/storyboard.json");
const duration = storyboard.scenes.reduce((total, scene) => total + scene.duration, 0);
const errors = [];

if (storyboard.format.width !== 1080 || storyboard.format.height !== 1920) {
  errors.push("Storyboard must be 1080x1920 for the vertical social master.");
}

if (storyboard.format.durationSeconds > 90 || duration > 90) {
  errors.push(`Storyboard duration is too long: ${duration}s.`);
}

if (duration !== storyboard.format.durationSeconds) {
  errors.push(`Scene durations (${duration}s) do not match format.durationSeconds (${storyboard.format.durationSeconds}s).`);
}

for (const scene of storyboard.scenes) {
  if (!scene.id || !scene.title || !scene.voiceover || !scene.assetKey) {
    errors.push(`Scene ${scene.id || "<missing>"} is missing required copy or asset metadata.`);
  }
}

const scannedFiles = [
  "README.md",
  ".env.example",
  "config/scenario.json",
  "config/storyboard.json"
];

for (const relativePath of scannedFiles) {
  const value = await fs.readFile(path.join(toolRoot, relativePath), "utf8");
  if (/sk_live_|sk-proj-|PAYPAL-[A-Z0-9]{10,}|Password=(?!<redacted>|$)\S+/im.test(value)) {
    errors.push(`${relativePath} appears to contain a secret-like value.`);
  }
}

if (errors.length > 0) {
  for (const error of errors) {
    console.error(`ERROR: ${error}`);
  }
  process.exit(1);
}

console.log(`Storyboard valid: ${duration}s, ${storyboard.scenes.length} scenes, ${storyboard.format.width}x${storyboard.format.height}.`);
