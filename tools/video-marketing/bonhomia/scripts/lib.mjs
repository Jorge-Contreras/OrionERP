import fs from "node:fs/promises";
import path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

export const toolRoot = path.resolve(fileURLToPath(new URL("..", import.meta.url)));
export const repoRoot = path.resolve(toolRoot, "..", "..", "..");
export const artifactRoot = path.join(toolRoot, "artifacts");
export const publicRoot = path.join(artifactRoot, "public");

export async function ensureDir(dir) {
  await fs.mkdir(dir, { recursive: true });
}

export async function readJson(relativePath) {
  const filePath = path.join(toolRoot, relativePath);
  return JSON.parse(await fs.readFile(filePath, "utf8"));
}

export async function writeJson(filePath, value) {
  await ensureDir(path.dirname(filePath));
  await fs.writeFile(filePath, `${JSON.stringify(value, null, 2)}\n`, "utf8");
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
