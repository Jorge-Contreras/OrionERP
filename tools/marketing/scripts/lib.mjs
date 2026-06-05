import fs from "node:fs/promises";
import path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

export const toolRoot = path.resolve(fileURLToPath(new URL("..", import.meta.url)));
export const repoRoot = path.resolve(toolRoot, "..", "..");
export const artifactRoot = path.join(toolRoot, "artifacts");
export const defaultCampaignId = "bonhomia-reservation-video";

export async function ensureDir(dir) {
  await fs.mkdir(dir, { recursive: true });
}

export async function readJson(filePath) {
  return JSON.parse(await fs.readFile(filePath, "utf8"));
}

export async function writeJson(filePath, value) {
  await ensureDir(path.dirname(filePath));
  await fs.writeFile(filePath, `${JSON.stringify(value, null, 2)}\n`, "utf8");
}

export async function writeText(filePath, value) {
  await ensureDir(path.dirname(filePath));
  await fs.writeFile(filePath, value, "utf8");
}

export async function copyIfExists(source, destination) {
  try {
    await fs.access(source);
  } catch {
    return false;
  }

  await ensureDir(path.dirname(destination));
  await fs.copyFile(source, destination);
  return true;
}

export async function cleanDir(dir) {
  await fs.rm(dir, { recursive: true, force: true });
  await ensureDir(dir);
}

export function toForwardSlash(value) {
  return value.split(path.sep).join("/");
}

export function isDirectRun(metaUrl) {
  return process.argv[1]
    && pathToFileURL(path.resolve(process.argv[1])).href === metaUrl;
}

export function formatDate(date) {
  const year = date.getFullYear();
  const month = String(date.getMonth() + 1).padStart(2, "0");
  const day = String(date.getDate()).padStart(2, "0");
  return `${year}-${month}-${day}`;
}

export function addDays(date, days) {
  const next = new Date(date);
  next.setDate(next.getDate() + days);
  return next;
}

export async function fileExists(filePath) {
  try {
    await fs.access(filePath);
    return true;
  } catch {
    return false;
  }
}

export function getArgValue(name, args = process.argv.slice(2)) {
  const equalsPrefix = `${name}=`;
  const equalsMatch = args.find((arg) => arg.startsWith(equalsPrefix));
  if (equalsMatch) {
    return equalsMatch.slice(equalsPrefix.length);
  }

  const index = args.indexOf(name);
  if (index >= 0 && args[index + 1] && !args[index + 1].startsWith("--")) {
    return args[index + 1];
  }

  return null;
}

export function getCampaignId(args = process.argv.slice(2)) {
  return getArgValue("--campaign", args)
    || process.env.MARKETING_CAMPAIGN
    || defaultCampaignId;
}

export async function loadCampaignContext(args = process.argv.slice(2)) {
  const campaignId = getCampaignId(args);
  const campaignRoot = path.join(toolRoot, "campaigns", campaignId);
  const storyboardPath = path.join(campaignRoot, "storyboard.json");
  const scenarioPath = path.join(campaignRoot, "scenario.json");
  const storyboard = await readJson(storyboardPath);
  const scenario = await readJson(scenarioPath);
  const brandId = storyboard.brandId || scenario.brandId || storyboard.campaign?.brandId;
  if (!brandId) {
    throw new Error(`Campaign ${campaignId} must declare brandId.`);
  }

  const brandRoot = path.join(toolRoot, "brands", brandId);
  const brand = await readJson(path.join(brandRoot, "brand.json"));
  const campaignArtifactRoot = path.join(artifactRoot, campaignId);
  const publicRoot = path.join(campaignArtifactRoot, "public");

  return {
    campaignId,
    campaignRoot,
    storyboardPath,
    scenarioPath,
    storyboard,
    scenario,
    brandId,
    brandRoot,
    brand,
    artifacts: {
      root: campaignArtifactRoot,
      captures: path.join(campaignArtifactRoot, "captures"),
      audio: path.join(campaignArtifactRoot, "audio"),
      final: path.join(campaignArtifactRoot, "final"),
      public: publicRoot,
      review: path.join(campaignArtifactRoot, "review"),
      bundle: path.join(campaignArtifactRoot, "remotion-bundle")
    }
  };
}

export function repoPath(relativePath) {
  return path.join(repoRoot, relativePath);
}

export function campaignOutputName(context) {
  return context.storyboard.campaign?.outputName || `${context.campaignId}.mp4`;
}

export function campaignCompositionId(context) {
  return context.storyboard.campaign?.compositionId || "BonhomiaPromo";
}

export function narrationFromStoryboard(storyboard) {
  return storyboard.scenes
    .map((scene) => scene.voiceover)
    .filter(Boolean)
    .join("\n\n");
}

export function hasFlag(name, args = process.argv.slice(2)) {
  return args.includes(name);
}
