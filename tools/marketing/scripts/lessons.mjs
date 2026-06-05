import fs from "node:fs/promises";
import path from "node:path";
import {
  ensureDir,
  getArgValue,
  isDirectRun,
  toolRoot,
  writeText
} from "./lib.mjs";

export async function runLessonsCommand(args = process.argv.slice(2)) {
  const promotePath = getArgValue("--promote", args);
  if (!promotePath) {
    return listLessonInbox();
  }

  const sourcePath = path.isAbsolute(promotePath)
    ? promotePath
    : path.resolve(process.cwd(), promotePath);
  const content = await fs.readFile(sourcePath, "utf8");
  const playbookPath = path.join(toolRoot, "knowledge", "playbook.md");
  const playbook = await fs.readFile(playbookPath, "utf8");
  const promoted = [
    playbook.trimEnd(),
    "",
    "## Promoted Lessons",
    "",
    `Source: ${path.relative(toolRoot, sourcePath).split(path.sep).join("/")}`,
    "",
    content.trim(),
    ""
  ].join("\n");

  await writeText(playbookPath, promoted);
  return {
    promoted: true,
    sourcePath,
    playbookPath
  };
}

async function listLessonInbox() {
  const inbox = path.join(toolRoot, "knowledge", "lesson-inbox");
  await ensureDir(inbox);
  const entries = (await fs.readdir(inbox, { withFileTypes: true }))
    .filter((entry) => entry.isFile() && entry.name.endsWith(".md"))
    .map((entry) => entry.name)
    .sort();

  return {
    promoted: false,
    entries
  };
}

if (isDirectRun(import.meta.url)) {
  runLessonsCommand()
    .then((result) => {
      if (result.promoted) {
        console.log(`Promoted lessons from ${result.sourcePath}`);
      } else if (result.entries.length === 0) {
        console.log("No lesson proposals found.");
      } else {
        console.log("Lesson proposals:");
        for (const entry of result.entries) {
          console.log(`- ${entry}`);
        }
      }
    })
    .catch((error) => {
      console.error(error.message);
      process.exit(1);
    });
}
