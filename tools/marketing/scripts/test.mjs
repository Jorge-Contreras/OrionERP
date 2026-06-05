import fs from "node:fs/promises";
import path from "node:path";
import { spawn } from "node:child_process";
import {
  fileExists,
  loadCampaignContext,
  toolRoot
} from "./lib.mjs";

await runNode(path.join(toolRoot, "scripts", "validate.mjs"), process.argv.slice(2));

const context = await loadCampaignContext();
const errors = [];
const packageJson = JSON.parse(await fs.readFile(path.join(toolRoot, "package.json"), "utf8"));
const requiredScripts = ["capture", "voice", "music", "render", "review", "produce", "validate", "test"];
for (const script of requiredScripts) {
  if (!packageJson.scripts?.[script]) {
    errors.push(`package.json is missing npm script '${script}'.`);
  }
}

if (!context.brand.providers?.voice?.openai) {
  errors.push("Brand voice provider config is missing OpenAI settings.");
}

if (!context.brand.providers?.voice?.elevenlabs) {
  errors.push("Brand voice provider config is missing ElevenLabs settings.");
}

if (context.brand.providers?.music?.strategy !== "curated-library-first") {
  errors.push("Brand music strategy should be curated-library-first.");
}

if (!context.storyboard.campaign?.captureAliases?.payment) {
  errors.push("Campaign capture aliases must include payment.");
}

if (!context.storyboard.campaign?.outputName?.endsWith(".mp4")) {
  errors.push("Campaign outputName must be an mp4 file.");
}

if (!(await fileExists(path.join(toolRoot, "knowledge", "lesson-inbox")))) {
  errors.push("knowledge/lesson-inbox is missing.");
}

if (errors.length > 0) {
  for (const error of errors) {
    console.error(`ERROR: ${error}`);
  }
  process.exit(1);
}

console.log(`Marketing platform tests passed for ${context.campaignId}.`);

function runNode(scriptPath, scriptArgs) {
  return new Promise((resolve, reject) => {
    const child = spawn(process.execPath, [scriptPath, ...scriptArgs], {
      cwd: toolRoot,
      stdio: "inherit",
      env: process.env
    });

    child.on("error", reject);
    child.on("exit", (code) => {
      if (code === 0) {
        resolve();
      } else {
        reject(new Error(`${path.basename(scriptPath)} failed with exit code ${code}`));
      }
    });
  });
}
