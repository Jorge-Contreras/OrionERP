import "dotenv/config";
import fs from "node:fs/promises";
import path from "node:path";
import OpenAI from "openai";
import {
  ensureDir,
  loadCampaignContext,
  narrationFromStoryboard,
  writeJson
} from "./lib.mjs";

const context = await loadCampaignContext();
const audioRoot = context.artifacts.audio;
await ensureDir(audioRoot);

const narration = narrationFromStoryboard(context.storyboard);
await fs.writeFile(path.join(audioRoot, "narration.txt"), `${narration}\n`, "utf8");

const provider = (
  process.env.MARKETING_VOICE_PROVIDER
  || context.brand.providers?.voice?.defaultProvider
  || "openai"
).toLowerCase();

if (provider === "elevenlabs") {
  await generateElevenLabsVoice(context, narration);
} else if (provider === "openai") {
  await generateOpenAiVoice(context, narration);
} else {
  await writeJson(path.join(audioRoot, "voice-manifest.json"), {
    status: "skipped",
    provider,
    reason: `Unsupported voice provider: ${provider}`,
    narrationText: "narration.txt"
  });
  console.warn(`Unsupported voice provider '${provider}'. Wrote narration.txt and skipped TTS generation.`);
}

async function generateOpenAiVoice(currentContext, text) {
  const settings = currentContext.brand.providers?.voice?.openai || {};
  if (!process.env.OPENAI_API_KEY) {
    await writeJson(path.join(audioRoot, "voice-manifest.json"), {
      status: "skipped",
      provider: "openai",
      reason: "OPENAI_API_KEY is not set.",
      narrationText: "narration.txt"
    });
    console.warn("OPENAI_API_KEY is not set. Wrote narration.txt and skipped TTS generation.");
    return;
  }

  const client = new OpenAI({ apiKey: process.env.OPENAI_API_KEY });
  const voice = process.env.BONHOMIA_TTS_VOICE || process.env.OPENAI_TTS_VOICE || settings.voice || "nova";
  const speed = Number(process.env.BONHOMIA_TTS_SPEED || process.env.OPENAI_TTS_SPEED || settings.speed || "1.08");
  const model = process.env.OPENAI_TTS_MODEL || settings.model || "gpt-4o-mini-tts";

  const response = await client.audio.speech.create({
    model,
    voice,
    input: text,
    instructions: settings.instructions,
    response_format: "mp3",
    speed
  });

  const audioBuffer = Buffer.from(await response.arrayBuffer());
  const audioPath = path.join(audioRoot, "narration.mp3");
  await fs.writeFile(audioPath, audioBuffer);
  await writeJson(path.join(audioRoot, "voice-manifest.json"), {
    status: "generated",
    provider: "openai",
    model,
    voice,
    speed,
    output: "narration.mp3",
    disclosure: currentContext.storyboard.metadata?.voiceDisclosure || currentContext.brand.disclosures?.aiVoice
  });

  console.log(`Voiceover written to ${audioPath}`);
}

async function generateElevenLabsVoice(currentContext, text) {
  const settings = currentContext.brand.providers?.voice?.elevenlabs || {};
  const apiKey = process.env.ELEVENLABS_API_KEY;
  const voiceId = process.env.ELEVENLABS_VOICE_ID;
  if (!apiKey || !voiceId) {
    await writeJson(path.join(audioRoot, "voice-manifest.json"), {
      status: "skipped",
      provider: "elevenlabs",
      reason: "ELEVENLABS_API_KEY and ELEVENLABS_VOICE_ID are required.",
      narrationText: "narration.txt"
    });
    console.warn("ElevenLabs credentials are not set. Wrote narration.txt and skipped TTS generation.");
    return;
  }

  const outputFormat = process.env.ELEVENLABS_OUTPUT_FORMAT || settings.outputFormat || "mp3_44100_128";
  const endpoint = new URL(`https://api.elevenlabs.io/v1/text-to-speech/${encodeURIComponent(voiceId)}`);
  endpoint.searchParams.set("output_format", outputFormat);

  const response = await fetch(endpoint, {
    method: "POST",
    headers: {
      "xi-api-key": apiKey,
      "Content-Type": "application/json"
    },
    body: JSON.stringify({
      text,
      model_id: process.env.ELEVENLABS_MODEL_ID || settings.modelId || "eleven_multilingual_v2",
      language_code: process.env.ELEVENLABS_LANGUAGE_CODE || settings.languageCode || "es",
      voice_settings: settings.voiceSettings || undefined
    })
  });

  if (!response.ok) {
    const detail = await response.text().catch(() => "");
    throw new Error(`ElevenLabs TTS failed with ${response.status}: ${detail.slice(0, 500)}`);
  }

  const audioBuffer = Buffer.from(await response.arrayBuffer());
  const audioPath = path.join(audioRoot, "narration.mp3");
  await fs.writeFile(audioPath, audioBuffer);
  await writeJson(path.join(audioRoot, "voice-manifest.json"), {
    status: "generated",
    provider: "elevenlabs",
    model: process.env.ELEVENLABS_MODEL_ID || settings.modelId || "eleven_multilingual_v2",
    voiceId,
    outputFormat,
    output: "narration.mp3",
    disclosure: currentContext.storyboard.metadata?.voiceDisclosure || currentContext.brand.disclosures?.aiVoice
  });

  console.log(`ElevenLabs voiceover written to ${audioPath}`);
}
