import fs from "node:fs/promises";
import path from "node:path";
import { spawn } from "node:child_process";
import sharp from "sharp";
import {
  artifactRoot,
  fileExists,
  readJson,
  repoRoot,
  resolveWeek,
  toolRoot,
  writeJson
} from "./lib.mjs";

await runNode(path.join(toolRoot, "scripts", "validate.mjs"), process.argv.slice(2));

const errors = [];
const packageJson = JSON.parse(await fs.readFile(path.join(toolRoot, "package.json"), "utf8"));
const requiredDependencies = ["dotenv", "mssql", "openai", "sharp"];
for (const dependency of requiredDependencies) {
  if (!packageJson.dependencies?.[dependency]) {
    errors.push(`package.json is missing dependency '${dependency}'.`);
  }
}

const week = resolveWeek(["--week", "2026-W24"]);
if (week.id !== "2026-W24" || week.startDate !== "2026-06-08" || week.endDate !== "2026-06-14") {
  errors.push(`ISO week resolver returned an unexpected result: ${JSON.stringify(week)}`);
}

const mediaSchema = await readJson(path.join(toolRoot, "schemas", "media-plan.schema.json"));
if (!mediaSchema.required?.includes("assets")) {
  errors.push("Media plan schema must require assets.");
}

if (!mediaSchema.properties?.assets?.items?.properties?.assetDecision) {
  errors.push("Media plan schema must support generated assetDecision metadata.");
}

if (await fileExists(path.join(toolRoot, "campaigns", "bonhomia-reservation-video"))) {
  errors.push("Reservation video campaign should not be active.");
}

const syntaxScripts = [
  "lib.mjs",
  "intelligence.mjs",
  "brief.mjs",
  "media.mjs",
  "lessons.mjs",
  "validate.mjs",
  "test.mjs"
];

for (const script of syntaxScripts) {
  await runNode(process.execPath, ["--check", path.join(toolRoot, "scripts", script)], true);
}

await writeMediaFixture(week);
await runNode(
  path.join(toolRoot, "scripts", "media.mjs"),
  ["--brand", "bonhomia", "--week", week.id, "--use-existing", "--mock-openai", "--mock-review", "--media", "2 Facebook images, 1 TikTok video"]
);
await verifyMediaFixture(week, errors);

if (errors.length > 0) {
  for (const error of errors) {
    console.error(`ERROR: ${error}`);
  }
  process.exit(1);
}

console.log("Marketing intelligence tests passed.");

function runNode(scriptPath, scriptArgs, directExecutable = false) {
  return new Promise((resolve, reject) => {
    const command = directExecutable ? scriptPath : process.execPath;
    const args = directExecutable ? scriptArgs : [scriptPath, ...scriptArgs];
    const child = spawn(command, args, {
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

async function writeMediaFixture(week) {
  const root = path.join(artifactRoot, "intelligence", week.id);
  await writeJson(path.join(root, "marketing-data.json"), {
    brand: {
      id: "bonhomia",
      name: "Bonhomia Suites"
    },
    week,
    goals: {
      occupancyTargetPct: 50
    },
    publicExperiences: [],
    upcomingExperiences: [],
    saludFinanciera: {
      suitePerformance: [
        {
          roomName: "PENTHOUSE",
          occupancyPct: 0,
          roomRevenue: 0
        },
        {
          roomName: "PARIS",
          occupancyPct: 20,
          roomRevenue: 5000
        }
      ]
    }
  });
  await writeJson(path.join(root, "media-plan.json"), {
    schemaVersion: "test",
    brandId: "bonhomia",
    week,
    requestedMedia: "2 Facebook images, 1 TikTok video",
    strategy: {
      objective: "Test image generation.",
      occupancyTargetPct: 50,
      primaryAudience: "Business travelers / companies",
      rationale: "Offline fixture."
    },
    assets: [
      {
        id: "fb-local-art-awareness",
        type: "facebook_image",
        platforms: ["Facebook", "Instagram"],
        size: "1080x1350",
        audience: "BnB travelers and tourists",
        hook: "Calpulalpan tiene plan este fin.",
        concept: "Local culture awareness image for Bonhomia.",
        visualDirection: "Generated destination visual with Bonhomia logo only.",
        caption: "Un plan local, una estancia comoda y reserva directa.",
        cta: "Conoce Bonhomia"
      },
      {
        id: "fb-business-penthouse",
        type: "facebook_image",
        platforms: ["Facebook", "Instagram"],
        size: "1080x1350",
        audience: "Business travelers / companies",
        hook: "Viajas por trabajo a Calpulalpan?",
        concept: "Business lodging with Suite Penthouse as the stay option.",
        visualDirection: "Use a real suite photo card and do not invent suite amenities.",
        caption: "Penthouse es una opcion practica para llegar, trabajar y descansar.",
        cta: "Reserva directo"
      },
      {
        id: "fb-business-brand-poster",
        type: "facebook_image",
        platforms: ["Facebook", "Instagram"],
        size: "1080x1350",
        audience: "Business travelers / companies",
        hook: "Viajas por trabajo a Calpulalpan?",
        concept: "Direct-booking business poster without a named suite.",
        visualDirection: "Brand-led editorial grid, no forced room photo.",
        caption: "Reserva directo para una estancia practica de trabajo.",
        cta: "Reserva directo"
      },
      {
        id: "tiktok-fixture",
        type: "tiktok_video",
        platforms: ["TikTok"],
        size: "1080x1920",
        audience: "Business travelers / companies",
        hook: "Reserva Bonhomia rapido.",
        concept: "Short booking video concept.",
        caption: "Video futuro.",
        cta: "Reserva directo",
        scenes: ["Hook", "Suite", "CTA"]
      }
    ],
    reviewChecklist: []
  });
}

async function verifyMediaFixture(week, errors) {
  const mediaRoot = path.join(artifactRoot, "intelligence", week.id, "media");
  const manifest = await readJson(path.join(mediaRoot, "media-manifest.json"));
  if (manifest.assets.length !== 3) {
    errors.push(`Expected 3 generated image assets, got ${manifest.assets.length}.`);
  }

  if (manifest.unsupported.length !== 1 || manifest.unsupported[0].status !== "unsupported_v1") {
    errors.push("Expected one unsupported_v1 TikTok asset.");
  }

  const logoOnly = manifest.assets.find((asset) => asset.id === "fb-local-art-awareness");
  if (logoOnly?.assetDecision !== "logo_only" || logoOnly.sourceSuitePhoto !== null) {
    errors.push("Logo-only fixture should not include a suite photo.");
  }

  const suiteCard = manifest.assets.find((asset) => asset.id === "fb-business-penthouse");
  if (!suiteCard?.sourceSuitePhoto || suiteCard.assetDecision !== "suite_card") {
    errors.push("Suite-card fixture should include a real suite photo.");
  }

  const businessPoster = manifest.assets.find((asset) => asset.id === "fb-business-brand-poster");
  if (businessPoster?.assetDecision !== "business_brand_poster" || businessPoster.sourceSuitePhoto !== null) {
    errors.push("Business poster fixture should not force a suite photo when no suite is named.");
  }

  if (businessPoster?.template !== "business_direct_booking") {
    errors.push("Business poster fixture should still use the business_direct_booking template.");
  }

  for (const asset of manifest.assets) {
    if (asset.status !== "generated") {
      errors.push(`Expected ${asset.id} to pass the mock quality gate, got ${asset.status}.`);
    }

    if (typeof asset.quality?.score !== "number" || asset.quality.score < asset.quality.minimumScore) {
      errors.push(`Expected ${asset.id} to record an accepted quality score.`);
    }

    if (!Array.isArray(asset.quality?.rejectedCandidates) || asset.quality.rejectedCandidates.length === 0) {
      errors.push(`Expected ${asset.id} to record at least one rejected candidate.`);
    }

    if (!asset.quality?.rejectedCandidates?.some((candidate) => /kindergarten/iu.test(candidate.criticalFailures?.join(" ") || ""))) {
      errors.push(`Expected ${asset.id} to record the mock amateur-layout rejection reason.`);
    }

    const outputPath = path.join(repoRoot, asset.outputPath);
    if (!(await fileExists(outputPath))) {
      errors.push(`Generated image is missing: ${asset.outputPath}`);
      continue;
    }

    const metadata = await sharp(outputPath).metadata();
    if (metadata.width !== 1080 || metadata.height !== 1350) {
      errors.push(`Generated image ${asset.id} is ${metadata.width}x${metadata.height}, expected 1080x1350.`);
    }
  }

  const lessonPath = path.join(mediaRoot, "lesson-proposals.md");
  if (!(await fileExists(lessonPath))) {
    errors.push("Media run should write lesson-proposals.md.");
  }

  const report = await fs.readFile(path.join(mediaRoot, "media-generation-report.md"), "utf8");
  if (!/Rejected Candidates/iu.test(report) || !/candidate 1/iu.test(report)) {
    errors.push("Media report should include candidate rejection details.");
  }
}
