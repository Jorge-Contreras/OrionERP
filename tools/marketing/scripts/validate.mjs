import fs from "node:fs/promises";
import path from "node:path";
import {
  fileExists,
  loadCampaignContext,
  repoPath,
  toolRoot
} from "./lib.mjs";

const context = await loadCampaignContext();
const { storyboard, brand } = context;
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

if (context.brandId !== brand.id) {
  errors.push(`Campaign brandId (${context.brandId}) does not match brand profile id (${brand.id}).`);
}

for (const scene of storyboard.scenes) {
  if (!scene.id || !scene.title || !scene.voiceover || !scene.assetKey) {
    errors.push(`Scene ${scene.id || "<missing>"} is missing required copy or asset metadata.`);
  }
}

for (const [assetKey, relativePath] of Object.entries(brand.assets?.repoImages || {})) {
  if (!(await fileExists(repoPath(relativePath)))) {
    errors.push(`Brand asset '${assetKey}' does not exist: ${relativePath}`);
  }
}

if (!(await fileExists(path.join(toolRoot, "knowledge", "playbook.md")))) {
  errors.push("Marketing knowledge playbook is missing.");
}

const scannedFiles = await collectTextFiles(toolRoot);
for (const filePath of scannedFiles) {
  const value = await fs.readFile(filePath, "utf8");
  if (containsSecretLikeValue(value)) {
    errors.push(`${path.relative(toolRoot, filePath)} appears to contain a secret-like value.`);
  }
}

if (errors.length > 0) {
  for (const error of errors) {
    console.error(`ERROR: ${error}`);
  }
  process.exit(1);
}

console.log(`Campaign ${context.campaignId} valid: ${duration}s, ${storyboard.scenes.length} scenes, ${storyboard.format.width}x${storyboard.format.height}.`);

async function collectTextFiles(root) {
  const collected = [];
  await visit(root, collected);
  return collected;
}

async function visit(dir, collected) {
  const entries = await fs.readdir(dir, { withFileTypes: true });
  for (const entry of entries) {
    if (entry.name === "node_modules" || entry.name === "artifacts" || entry.name === ".git") {
      continue;
    }

    const fullPath = path.join(dir, entry.name);
    if (path.relative(toolRoot, fullPath) === path.join("scripts", "validate.mjs")) {
      continue;
    }

    if (entry.isDirectory()) {
      await visit(fullPath, collected);
      continue;
    }

    if (/\.(json|md|mjs|jsx|example|txt)$/i.test(entry.name) || entry.name === ".env.example") {
      collected.push(fullPath);
    }
  }
}

function containsSecretLikeValue(value) {
  if (/(sk_live_|sk-proj-|xox[baprs]-|PAYPAL-[A-Z0-9]{10,})/im.test(value)) {
    return true;
  }

  if (/Password=(?!<redacted>|$)[^;\s]+/im.test(value)) {
    return true;
  }

  const assignmentKeys = [
    "OPENAI_API_KEY",
    "ELEVENLABS_API_KEY",
    "PAYPAL_SANDBOX_BUYER_PASSWORD",
    "ASPNETCORE_BonhomiaCheckout__PayPalClientSecret"
  ];

  for (const line of value.split(/\r?\n/u)) {
    for (const key of assignmentKeys) {
      const match = line.match(new RegExp(`${escapeRegex(key)}\\s*=\\s*(.+)$`, "i"));
      if (!match) {
        continue;
      }

      const assignedValue = match[1].trim().replace(/^["']|["']$/g, "");
      if (assignedValue && !/^<[^>]+>$/u.test(assignedValue)) {
        return true;
      }
    }
  }

  return false;
}

function escapeRegex(value) {
  return value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}
