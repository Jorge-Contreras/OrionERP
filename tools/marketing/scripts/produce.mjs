import { spawn } from "node:child_process";
import path from "node:path";
import { toolRoot } from "./lib.mjs";

const args = process.argv.slice(2);
const steps = [
  ["capture", "scripts/capture-flow.mjs"],
  ["voice", "scripts/generate-voice.mjs"],
  ["music", "scripts/generate-music.mjs"],
  ["render", "scripts/render.mjs"],
  ["review", "scripts/review.mjs"]
];

for (const [label, script] of steps) {
  console.log(`\n== ${label} ==`);
  await runNode(path.join(toolRoot, script), args);
}

console.log("\nMarketing production pipeline completed.");

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
