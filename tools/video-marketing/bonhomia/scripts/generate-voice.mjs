import "dotenv/config";
import fs from "node:fs/promises";
import path from "node:path";
import OpenAI from "openai";
import { artifactRoot, ensureDir, readJson, writeJson } from "./lib.mjs";

const storyboard = await readJson("config/storyboard.json");
const audioRoot = path.join(artifactRoot, "audio");
await ensureDir(audioRoot);

const narration = storyboard.scenes
  .map((scene) => scene.voiceover)
  .join("\n\n");

await fs.writeFile(path.join(audioRoot, "narration.txt"), `${narration}\n`, "utf8");

if (!process.env.OPENAI_API_KEY) {
  await writeJson(path.join(audioRoot, "voice-manifest.json"), {
    status: "skipped",
    reason: "OPENAI_API_KEY is not set.",
    narrationText: "narration.txt"
  });
  console.warn("OPENAI_API_KEY is not set. Wrote narration.txt and skipped TTS generation.");
  process.exit(0);
}

const client = new OpenAI({ apiKey: process.env.OPENAI_API_KEY });
const voice = process.env.BONHOMIA_TTS_VOICE || "nova";
const speed = Number(process.env.BONHOMIA_TTS_SPEED || "1.08");

const response = await client.audio.speech.create({
  model: "gpt-4o-mini-tts",
  voice,
  input: narration,
  instructions: "Voz femenina en espanol mexicano, natural y relajada, como una amiga mostrando una app en un Reel. Ritmo rapido, energia amable, cero tono corporativo. Usa pausas cortas, sonrisa en la voz y una cadencia humana.",
  response_format: "mp3",
  speed
});

const audioBuffer = Buffer.from(await response.arrayBuffer());
const audioPath = path.join(audioRoot, "narration.mp3");
await fs.writeFile(audioPath, audioBuffer);
await writeJson(path.join(audioRoot, "voice-manifest.json"), {
  status: "generated",
  model: "gpt-4o-mini-tts",
  voice,
  speed,
  output: "narration.mp3",
  disclosure: storyboard.metadata.voiceDisclosure
});

console.log(`Voiceover written to ${audioPath}`);
