import fs from "node:fs/promises";
import path from "node:path";
import { artifactRoot, ensureDir, readJson, writeJson } from "./lib.mjs";

const storyboard = await readJson("config/storyboard.json");
const sampleRate = 44100;
const durationSeconds = storyboard.format.durationSeconds + 1;
const totalSamples = Math.floor(sampleRate * durationSeconds);
const bpm = 124;
const beatSeconds = 60 / bpm;
const audioRoot = path.join(artifactRoot, "audio");
await ensureDir(audioRoot);

const left = new Float32Array(totalSamples);
const right = new Float32Array(totalSamples);

for (let i = 0; i < totalSamples; i++) {
  const t = i / sampleRate;
  const beat = t / beatSeconds;
  const beatPhase = beat - Math.floor(beat);
  const barBeat = Math.floor(beat) % 4;

  const kick = Math.exp(-beatPhase * 18) * Math.sin(2 * Math.PI * (48 + 34 * Math.exp(-beatPhase * 12)) * t);
  const hatPhase = (beat * 2) % 1;
  const hat = (noise(i) * 2 - 1) * Math.exp(-hatPhase * 24) * 0.16;
  const clapPhase = barBeat === 1 || barBeat === 3 ? beatPhase : 1;
  const clap = (noise(i + 9000) * 2 - 1) * Math.exp(-clapPhase * 32) * 0.22;

  const chordIndex = Math.floor(beat / 4) % 4;
  const root = [49, 44, 46, 42][chordIndex];
  const pad = (
    softSaw(root, t) +
    softSaw(root + 7, t) * 0.72 +
    softSaw(root + 12, t) * 0.58
  ) * 0.055;
  const bass = Math.sin(2 * Math.PI * midiToHz(root - 12) * t) * envelope(beatPhase, 0.08, 0.42) * 0.18;
  const pluck = Math.sin(2 * Math.PI * midiToHz(root + 24 + (barBeat % 2) * 7) * t)
    * Math.exp(((beat * 4) % 1) * -9)
    * 0.07;

  const sidechain = 0.72 + 0.28 * Math.min(1, beatPhase * 3.2);
  const sample = kick * 0.42 + hat + clap + (pad + bass + pluck) * sidechain;
  left[i] = clamp(sample * 0.72);
  right[i] = clamp((sample + pad * 0.35 - hat * 0.12) * 0.72);
}

const wav = encodeWav(left, right, sampleRate);
const musicPath = path.join(audioRoot, "house-bed.wav");
await fs.writeFile(musicPath, wav);
await writeJson(path.join(audioRoot, "music-manifest.json"), {
  status: "generated",
  style: "synthetic house bed",
  bpm,
  output: "house-bed.wav",
  durationSeconds
});

console.log(`Music bed written to ${musicPath}`);

function softSaw(midiNote, t) {
  const hz = midiToHz(midiNote);
  const phase = (t * hz) % 1;
  return (2 * phase - 1) * 0.45 + Math.sin(2 * Math.PI * hz * t) * 0.55;
}

function midiToHz(note) {
  return 440 * 2 ** ((note - 69) / 12);
}

function envelope(phase, attack, release) {
  if (phase < attack) {
    return phase / attack;
  }

  return Math.max(0, 1 - (phase - attack) / release);
}

function noise(seed) {
  const value = Math.sin(seed * 12.9898) * 43758.5453;
  return value - Math.floor(value);
}

function clamp(value) {
  return Math.max(-0.98, Math.min(0.98, value));
}

function encodeWav(leftChannel, rightChannel, rate) {
  const bytesPerSample = 2;
  const channelCount = 2;
  const dataLength = leftChannel.length * channelCount * bytesPerSample;
  const buffer = Buffer.alloc(44 + dataLength);

  buffer.write("RIFF", 0);
  buffer.writeUInt32LE(36 + dataLength, 4);
  buffer.write("WAVE", 8);
  buffer.write("fmt ", 12);
  buffer.writeUInt32LE(16, 16);
  buffer.writeUInt16LE(1, 20);
  buffer.writeUInt16LE(channelCount, 22);
  buffer.writeUInt32LE(rate, 24);
  buffer.writeUInt32LE(rate * channelCount * bytesPerSample, 28);
  buffer.writeUInt16LE(channelCount * bytesPerSample, 32);
  buffer.writeUInt16LE(16, 34);
  buffer.write("data", 36);
  buffer.writeUInt32LE(dataLength, 40);

  let offset = 44;
  for (let i = 0; i < leftChannel.length; i++) {
    buffer.writeInt16LE(Math.round(leftChannel[i] * 32767), offset);
    buffer.writeInt16LE(Math.round(rightChannel[i] * 32767), offset + 2);
    offset += 4;
  }

  return buffer;
}
