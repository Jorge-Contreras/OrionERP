import fs from "node:fs/promises";
import path from "node:path";
import { spawn } from "node:child_process";
import sharp from "sharp";
import { __mediaTestHooks } from "./media.mjs";
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
await verifyReviewFallbackPolicy(errors);

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

  if (manifest.provider?.quality !== "high" || manifest.provider?.backgroundSize !== "1280x1600") {
    errors.push("Media manifest should record high quality and the 1280x1600 production layer size.");
  }

  if (manifest.provider?.photoMode !== "deterministic") {
    errors.push(`Media manifest should record deterministic photo mode by default, got ${manifest.provider?.photoMode}.`);
  }

  if (manifest.qualityGate?.candidatesPerAsset < 4 || manifest.qualityGate?.maxAttempts < 6) {
    errors.push("Media manifest should record at least 4 candidates and 6 max attempts.");
  }

  if (!manifest.learning?.playbook?.hash || !manifest.learning?.designSystem?.hash) {
    errors.push("Media manifest should record playbook and design-system hashes.");
  }

  if (!manifest.learning?.lessonArtifactPath || !manifest.learning?.lessonInboxPath) {
    errors.push("Media manifest should record artifact and lesson-inbox proposal paths.");
  }

  if (manifest.unsupported.length !== 1 || manifest.unsupported[0].status !== "unsupported_v1") {
    errors.push("Expected one unsupported_v1 TikTok asset.");
  }

  const logoOnly = manifest.assets.find((asset) => asset.id === "fb-local-art-awareness");
  if (logoOnly?.assetDecision !== "logo_only" || logoOnly.sourceSuitePhoto !== null) {
    errors.push("Logo-only fixture should not include a suite photo.");
  }

  if (logoOnly?.template !== "destination_brand_awareness") {
    errors.push("Logo-only local awareness fixture should use the destination_brand_awareness template, not a business template.");
  }

  const suitePoster = manifest.assets.find((asset) => asset.id === "fb-business-penthouse");
  if (!suitePoster?.sourceHeroPhoto || suitePoster.assetDecision !== "photo_led_poster" || suitePoster.sourceHeroKind !== "suite") {
    errors.push("Named-suite fixture should use a photo-led poster with a real suite hero photo.");
  }

  const businessPoster = manifest.assets.find((asset) => asset.id === "fb-business-brand-poster");
  if (businessPoster?.assetDecision !== "photo_led_poster" || !businessPoster.sourceHeroPhoto || businessPoster.sourceHeroKind !== "property") {
    errors.push("Business poster fixture should use a real property hero photo instead of forcing a suite module.");
  }

  if (businessPoster?.template !== "business_direct_booking") {
    errors.push("Business poster fixture should still use the business_direct_booking template.");
  }

  for (const asset of manifest.assets) {
    if (!asset.creativeFamily) {
      errors.push(`Expected ${asset.id} to record a creativeFamily.`);
    }

    if (asset.assetDecision === "photo_led_poster" && !asset.openAi?.sourceMode) {
      errors.push(`Expected ${asset.id} to record the image source mode.`);
    }

    if (!asset.learning?.playbookHash || !asset.learning?.designSystemHash) {
      errors.push(`Expected ${asset.id} to record learning hashes.`);
    }

    if (asset.status !== "generated") {
      errors.push(`Expected ${asset.id} to pass the mock quality gate, got ${asset.status}.`);
    }

    if (typeof asset.quality?.score !== "number" || asset.quality.score < asset.quality.minimumScore) {
      errors.push(`Expected ${asset.id} to record an accepted quality score.`);
    }

    if (!Array.isArray(asset.quality?.rejectedCandidates) || asset.quality.rejectedCandidates.length === 0) {
      errors.push(`Expected ${asset.id} to record at least one rejected candidate.`);
    }

    if (!Array.isArray(asset.quality?.candidates) || asset.quality.candidates.length < 4) {
      errors.push(`Expected ${asset.id} to review at least 4 candidates.`);
    }

    if (!asset.quality?.candidates?.every((candidate) => candidate.reviewerMode && candidate.deterministicChecks && candidate.imageBase)) {
      errors.push(`Expected ${asset.id} candidates to record reviewerMode, deterministicChecks, and imageBase evidence.`);
    }

    const selected = asset.quality?.candidates?.find((candidate) => candidate.candidateIndex === asset.quality.selectedCandidate);
    if (!selected?.deterministicChecks?.passed) {
      errors.push(`Expected selected candidate for ${asset.id} to pass deterministic checks.`);
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

  const lessonInboxPath = path.join(repoRoot, manifest.learning.lessonInboxPath);
  if (!(await fileExists(lessonInboxPath))) {
    errors.push("Media run should write a deduplicated lesson-inbox proposal by default.");
  }

  const report = await fs.readFile(path.join(mediaRoot, "media-generation-report.md"), "utf8");
  if (!/Rejected Candidates/iu.test(report) || !/candidate 1/iu.test(report) || !/Deterministic Review Evidence/iu.test(report)) {
    errors.push("Media report should include candidate rejection details.");
  }
}

async function verifyReviewFallbackPolicy(errors) {
  const localAwarenessAsset = {
    id: "unit-local-awareness",
    type: "facebook_image",
    audience: "BnB travelers and tourists",
    hook: "Calpulalpan tiene plan este fin.",
    concept: "Local culture awareness image for Bonhomia.",
    visualDirection: "Generated destination visual with Bonhomia logo only.",
    caption: "Un plan local, una estancia comoda y reserva directa.",
    cta: "Conoce Bonhomia"
  };
  const decision = __mediaTestHooks.decideAssetTreatment(localAwarenessAsset);
  const template = __mediaTestHooks.selectEditorialTemplate(localAwarenessAsset, decision);

  if (decision.name !== "logo_only") {
    errors.push(`Expected generic destination CTA asset to remain logo_only, got ${decision.name}.`);
  }

  if (template.id !== "destination_brand_awareness") {
    errors.push(`Expected generic destination CTA asset to use destination template, got ${template.id}.`);
  }

  const deterministicChecks = {
    passed: true,
    checks: [],
    criticalFailures: [],
    warnings: []
  };
  const reviewArgs = {
    asset: localAwarenessAsset,
    candidateIndex: 1,
    candidatePath: "unused.png",
    config: {
      mock: false,
      mockReview: false,
      reviewModel: null,
      minimumScore: 82,
      allowHeuristicFinal: false
    },
    copy: {
      headline: "CALPULALPAN",
      subhead: "Tu estancia empieza aqui.",
      cta: "Conoce Bonhomia"
    },
    decision,
    deterministicChecks,
    eventReview: {
      mentionsEvent: false,
      verified: false
    },
    sourceSuitePhoto: null,
    template,
    learningContext: {
      playbookRules: [],
      designRules: []
    },
    width: 1080,
    height: 1350
  };

  try {
    await __mediaTestHooks.reviewCandidate(reviewArgs);
    errors.push("Expected production review to fail closed without OpenAI vision review.");
  } catch (error) {
    if (!/vision review is required/iu.test(error.message)) {
      errors.push(`Unexpected fail-closed review error: ${error.message}`);
    }
  }

  const overrideReview = await __mediaTestHooks.reviewCandidate({
    ...reviewArgs,
    config: {
      ...reviewArgs.config,
      allowHeuristicFinal: true
    }
  });
  if (overrideReview.reviewerMode !== "heuristic-override" || overrideReview.accepted !== true) {
    errors.push("Expected MARKETING_ALLOW_HEURISTIC_FINAL override path to use heuristic-override and accept the structurally valid fixture.");
  }
}
