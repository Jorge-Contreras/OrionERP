import "dotenv/config";
import fs from "node:fs/promises";
import path from "node:path";
import OpenAI from "openai";
import sharp from "sharp";
import {
  artifactRoot,
  cleanDir,
  ensureDir,
  fileExists,
  getArgValue,
  hasFlag,
  isDirectRun,
  loadBrandContext,
  readJson,
  repoPath,
  repoRoot,
  resolveWeek,
  toolRoot,
  toForwardSlash,
  writeJson,
  writeText
} from "./lib.mjs";
import { buildWeeklyBrief } from "./brief.mjs";

const imageTypes = new Set(["facebook_image", "instagram_image", "story_image"]);
const videoTypes = new Set(["tiktok_video", "reel_video"]);
const suiteNameSlugs = new Map([
  ["berlin", "berlin"],
  ["grecia", "grecia"],
  ["london", "london"],
  ["manhattan", "manhattan"],
  ["moscu", "moscu"],
  ["paris", "paris"],
  ["penthouse", "penthouse"],
  ["seul", "seul"]
]);

const templateDefinitions = {
  business_direct_booking: {
    id: "business_direct_booking",
    palette: {
      ink: "#fffaf3",
      dark: "#063f34",
      accent: "#c8503f",
      cream: "#fff7e8",
      olive: "#63724a"
    }
  },
  experience_event_hook: {
    id: "experience_event_hook",
    palette: {
      ink: "#fffaf3",
      dark: "#052d2b",
      accent: "#e3b554",
      cream: "#fff7e8",
      coral: "#bf493e"
    }
  },
  destination_brand_awareness: {
    id: "destination_brand_awareness",
    palette: {
      ink: "#fffaf3",
      dark: "#053f37",
      accent: "#d95d43",
      cream: "#fff7e8",
      blue: "#164d60"
    }
  }
};

export async function buildMediaPackage(args = process.argv.slice(2), options = {}) {
  const brandContext = await loadBrandContext(args);
  const brand = brandContext.brand;
  const week = resolveWeek(args);
  const outputRoot = path.join(artifactRoot, "intelligence", week.id);
  const mediaPlanPath = path.join(outputRoot, "media-plan.json");
  const intelligencePath = path.join(outputRoot, "marketing-data.json");
  const useExisting = hasFlag("--use-existing", args);

  if (!(await fileExists(mediaPlanPath))) {
    if (useExisting) {
      throw new Error(`Missing media plan at ${mediaPlanPath}. Run npm run brief first.`);
    }

    await buildWeeklyBrief(args);
  }

  const mediaPlan = await readJson(mediaPlanPath);
  const intelligence = await fileExists(intelligencePath)
    ? await readJson(intelligencePath)
    : null;

  const mediaRoot = path.join(outputRoot, "media");
  const imageRoot = path.join(mediaRoot, "images");
  const tempRoot = path.join(mediaRoot, ".tmp-candidates");
  await cleanDir(imageRoot);
  await cleanDir(tempRoot);
  await ensureDir(mediaRoot);

  const config = resolveImageConfig(brand, args, options);
  const catalog = await buildBonhomiaAssetCatalog(brand);
  const manifest = {
    generatedAtUtc: new Date().toISOString(),
    brand: {
      id: brand.id,
      name: brand.name
    },
    week,
    requestedMedia: mediaPlan.requestedMedia || getArgValue("--media", args) || null,
    output: {
      root: repoRelative(mediaRoot),
      images: repoRelative(imageRoot)
    },
    provider: {
      image: config.mock ? "mock-openai" : "openai",
      model: config.model,
      fallbackModel: config.fallbackModel,
      quality: config.quality,
      reviewModel: config.reviewModel,
      deterministicComposer: "sharp"
    },
    qualityGate: {
      target: "editorial_poster",
      strict: config.strictReview,
      minimumScore: config.minimumScore,
      candidatesPerAsset: config.candidatesPerAsset,
      maxAttempts: config.maxAttempts
    },
    factualImagePolicy: brand.assets?.factualImagePolicy || {},
    designSystem: {
      rules: "tools/marketing/docs/visual-design-system.md",
      referenceUse: "design-rules-only"
    },
    assets: [],
    unsupported: []
  };

  const imageAssets = (mediaPlan.assets || []).filter((asset) => imageTypes.has(asset.type));
  const videoAssets = (mediaPlan.assets || []).filter((asset) => videoTypes.has(asset.type));

  for (const asset of imageAssets) {
    const generated = await generateImageAsset({
      asset,
      brand,
      catalog,
      config,
      imageRoot,
      tempRoot,
      intelligence,
      mediaPlan
    });
    manifest.assets.push(generated);
  }

  for (const asset of videoAssets) {
    manifest.unsupported.push(buildUnsupportedVideoEntry(asset));
  }

  const cleanupWarning = await removeDirectoryWithRetry(tempRoot);
  if (cleanupWarning) {
    manifest.cleanupWarnings = [cleanupWarning];
  }
  await writeJson(path.join(mediaRoot, "media-manifest.json"), manifest);
  await writeText(path.join(mediaRoot, "media-generation-report.md"), renderMediaReport(manifest));
  await writeText(path.join(mediaRoot, "lesson-proposals.md"), renderLessonProposals(manifest));

  if (hasFlag("--write-lesson-inbox", args)) {
    const lessonPath = path.join(
      "knowledge",
      "lesson-inbox",
      `${new Date().toISOString().slice(0, 10)}-${brand.id}-media-generation.md`
    );
    await writeText(path.join(toolRoot, lessonPath), renderLessonProposals(manifest));
  }

  return {
    outputRoot,
    mediaRoot,
    manifest
  };
}

async function generateImageAsset({ asset, brand, catalog, config, imageRoot, tempRoot, intelligence }) {
  const decision = decideAssetTreatment(asset);
  const template = selectEditorialTemplate(asset, decision);
  const eventReview = reviewEventClaims(asset, intelligence);
  const sourceSuitePhoto = decision.usesSuite
    ? selectSuitePhoto(asset, catalog, intelligence)
    : null;
  const sourceLogo = repoPath(brand.assets?.logoPath || brand.assets?.repoImages?.logo);
  const copy = buildEditorialCopy(asset, template, sourceSuitePhoto, eventReview);
  const prompt = buildOpenAiImagePrompt(asset, decision, eventReview, template, copy);
  const outputPath = path.join(imageRoot, `${safeFileName(asset.id || asset.type)}.png`);
  const candidateRoot = path.join(tempRoot, safeFileName(asset.id || asset.type));
  await ensureDir(candidateRoot);

  const candidates = [];
  const minimumCandidates = Math.max(1, config.candidatesPerAsset);
  const maxCandidates = Math.max(minimumCandidates, config.maxAttempts);

  for (let candidateIndex = 1; candidateIndex <= maxCandidates; candidateIndex += 1) {
    const generatedLayer = await generateCampaignLayer(
      buildCandidatePrompt(prompt, candidateIndex),
      config,
      { candidateIndex, template }
    );
    const candidatePath = path.join(candidateRoot, `candidate-${candidateIndex}.png`);
    const composed = await composeSocialImage({
      asset,
      brand,
      copy,
      decision,
      eventReview,
      generatedLayer,
      outputPath: candidatePath,
      sourceLogo,
      sourceSuitePhoto,
      template,
      width: config.width,
      height: config.height
    });
    const review = await reviewCandidate({
      asset,
      candidateIndex,
      candidatePath,
      config,
      copy,
      decision,
      eventReview,
      sourceSuitePhoto,
      template,
      width: composed.width,
      height: composed.height
    });
    candidates.push({
      candidateIndex,
      score: review.score,
      accepted: review.accepted,
      reviewer: review.reviewer,
      criticalFailures: review.criticalFailures,
      issues: review.issues,
      strengths: review.strengths
    });

    const acceptable = candidates
      .filter((candidate) => candidate.accepted)
      .sort((a, b) => b.score - a.score)[0];

    if (candidateIndex >= minimumCandidates && acceptable) {
      await fs.copyFile(path.join(candidateRoot, `candidate-${acceptable.candidateIndex}.png`), outputPath);
      const metadata = await sharp(outputPath).metadata();
      return buildGeneratedAssetResult({
        asset,
        candidates,
        config,
        copy,
        decision,
        eventReview,
        generatedLayer,
        metadata,
        outputPath,
        prompt,
        selectedCandidate: acceptable,
        sourceLogo,
        sourceSuitePhoto,
        template
      });
    }
  }

  const bestCandidate = candidates.slice().sort((a, b) => b.score - a.score)[0] || null;
  return {
    id: asset.id,
    type: asset.type,
    status: "failed_quality_gate",
    assetDecision: decision.name,
    template: template.id,
    outputPath: null,
    sourceLogo: repoRelative(sourceLogo),
    sourceSuitePhoto: sourceSuitePhoto ? repoRelative(sourceSuitePhoto.path) : null,
    sourceSuiteName: sourceSuitePhoto?.suiteName || null,
    openAi: {
      prompt,
      promptPolicy: "OpenAI generated only candidate campaign/background layers; no candidate passed the strict quality gate."
    },
    quality: {
      minimumScore: config.minimumScore,
      selectedCandidate: null,
      bestCandidate,
      rejectedCandidates: candidates.filter((candidate) => !candidate.accepted),
      acceptedCandidates: candidates.filter((candidate) => candidate.accepted),
      candidates
    },
    factualPhotoPolicy: "Suite photos were not sent to OpenAI and were only cropped/resized/lightly enhanced.",
    reviewNotes: [
      "No final image was written because all candidates failed the strict quality gate.",
      ...eventReview.reviewNotes,
      ...decision.reviewNotes
    ]
  };
}

function buildGeneratedAssetResult({
  asset,
  candidates,
  config,
  copy,
  decision,
  eventReview,
  generatedLayer,
  metadata,
  outputPath,
  prompt,
  selectedCandidate,
  sourceLogo,
  sourceSuitePhoto,
  template
}) {
  return {
    id: asset.id,
    type: asset.type,
    status: "generated",
    assetDecision: decision.name,
    template: template.id,
    outputPath: repoRelative(outputPath),
    sourceLogo: repoRelative(sourceLogo),
    sourceSuitePhoto: sourceSuitePhoto ? repoRelative(sourceSuitePhoto.path) : null,
    sourceSuiteName: sourceSuitePhoto?.suiteName || null,
    dimensions: {
      width: metadata.width,
      height: metadata.height
    },
    publicCopy: copy,
    openAi: {
      model: generatedLayer.model,
      fallbackUsed: generatedLayer.fallbackUsed,
      prompt,
      promptPolicy: "OpenAI generated only the campaign/background layer; suite photos, logo, final text, and layout were deterministic locked layers."
    },
    quality: {
      minimumScore: config.minimumScore,
      selectedCandidate: selectedCandidate.candidateIndex,
      score: selectedCandidate.score,
      reviewer: selectedCandidate.reviewer,
      rejectedCandidates: candidates.filter((candidate) => !candidate.accepted),
      acceptedCandidates: candidates.filter((candidate) => candidate.accepted),
      bestCandidate: candidates.slice().sort((a, b) => b.score - a.score)[0] || null,
      candidates
    },
    factualPhotoPolicy: "Suite photos were not sent to OpenAI and were only cropped/resized/lightly enhanced.",
    reviewNotes: [
      ...eventReview.reviewNotes,
      ...decision.reviewNotes,
      ...(sourceSuitePhoto ? [] : ["No suite photo was used because the strategy did not require a factual suite module."])
    ]
  };
}

async function generateCampaignLayer(prompt, config, context) {
  if (config.mock) {
    return {
      buffer: await buildMockCampaignLayer(config.width, config.height, context),
      model: "mock-openai",
      fallbackUsed: false
    };
  }

  if (!process.env.OPENAI_API_KEY) {
    throw new Error("Missing OPENAI_API_KEY. Set it to generate marketing images, or run tests with --mock-openai.");
  }

  const client = new OpenAI({ apiKey: process.env.OPENAI_API_KEY });
  try {
    const buffer = await callOpenAiImage(client, config.model, prompt, config);
    return {
      buffer,
      model: config.model,
      fallbackUsed: false
    };
  } catch (error) {
    if (!config.fallbackModel || config.fallbackModel === config.model) {
      throw error;
    }

    const buffer = await callOpenAiImage(client, config.fallbackModel, prompt, config);
    return {
      buffer,
      model: config.fallbackModel,
      fallbackUsed: true
    };
  }
}

async function callOpenAiImage(client, model, prompt, config) {
  const result = await client.images.generate({
    model,
    prompt,
    size: config.backgroundSize,
    quality: config.quality,
    n: 1
  });
  const item = result.data?.[0];
  if (item?.b64_json) {
    return Buffer.from(item.b64_json, "base64");
  }

  if (item?.url) {
    const response = await fetch(item.url);
    if (!response.ok) {
      throw new Error(`OpenAI image URL download failed with ${response.status}.`);
    }

    return Buffer.from(await response.arrayBuffer());
  }

  throw new Error("OpenAI image response did not include b64_json or url.");
}

async function composeSocialImage({
  brand,
  copy,
  decision,
  generatedLayer,
  outputPath,
  sourceLogo,
  sourceSuitePhoto,
  template,
  width,
  height
}) {
  const base = await sharp(generatedLayer.buffer)
    .resize(width, height, { fit: "cover" })
    .modulate({ brightness: 0.92, saturation: 0.92 })
    .png()
    .toBuffer();

  const overlays = [
    { input: Buffer.from(buildEditorialOverlaySvg(template, copy, decision, width, height)), top: 0, left: 0 }
  ];

  if (sourceSuitePhoto) {
    const module = await buildSuiteModule(sourceSuitePhoto, template, copy);
    overlays.push({
      input: module.buffer,
      top: module.top,
      left: module.left
    });
  }

  if (await fileExists(sourceLogo)) {
    const logo = await sharp(sourceLogo)
      .resize({ width: template.id === "destination_brand_awareness" ? 150 : 124, withoutEnlargement: true })
      .png()
      .toBuffer();
    const logoPlacement = getLogoPlacement(template, decision);
    if (logoPlacement.panelWidth > 0 && logoPlacement.panelHeight > 0) {
      overlays.push({
        input: Buffer.from(buildBrandPanelSvg(template, logoPlacement)),
        top: logoPlacement.panelTop,
        left: logoPlacement.panelLeft
      });
    }
    overlays.push({
      input: logo,
      top: logoPlacement.logoTop,
      left: logoPlacement.logoLeft
    });
  }

  await sharp(base)
    .composite(overlays)
    .png()
    .toFile(outputPath);

  return sharp(outputPath).metadata();
}

async function buildSuiteModule(sourceSuitePhoto, template, copy) {
  const isBusiness = template.id === "business_direct_booking";
  const cardWidth = isBusiness ? 940 : 920;
  const cardHeight = isBusiness ? 500 : 300;
  const photoWidth = isBusiness ? cardWidth : 338;
  const photoHeight = isBusiness ? cardHeight : 246;
  const top = isBusiness ? 54 : 875;
  const left = isBusiness ? 70 : 80;
  const suitePhoto = await sharp(sourceSuitePhoto.path)
    .resize(photoWidth, photoHeight, { fit: "cover", position: "attention" })
    .modulate({ brightness: 1.03, saturation: 1.01 })
    .sharpen()
    .png()
    .toBuffer();
  const label = sourceSuitePhoto.suiteName
    ? `Suite ${titleCase(sourceSuitePhoto.suiteName)}`
    : "Bonhomia Suites";
  const subLabel = copy.suiteSubhead || "Hospedaje recomendado";
  const shell = Buffer.from(`
<svg width="${cardWidth}" height="${cardHeight}" xmlns="http://www.w3.org/2000/svg">
  ${isBusiness
    ? `<rect x="0" y="0" width="${cardWidth}" height="${cardHeight}" fill="#fff7e8"/>`
    : `<rect x="0" y="0" width="${cardWidth}" height="${cardHeight}" fill="#fff7e8"/>
       <rect x="${photoWidth + 34}" y="46" width="5" height="${cardHeight - 92}" fill="${template.palette.accent}"/>
       <text x="${photoWidth + 70}" y="112" font-family="Arial, Helvetica, sans-serif" font-size="38" font-weight="900" fill="#1f1a16">${escapeXml(label)}</text>
       <text x="${photoWidth + 70}" y="162" font-family="Arial, Helvetica, sans-serif" font-size="28" font-weight="700" fill="#40382f">${escapeXml(subLabel)}</text>
       <text x="${photoWidth + 70}" y="218" font-family="Arial, Helvetica, sans-serif" font-size="24" font-weight="700" fill="${template.palette.dark}">Bonhomia Suites</text>`}
</svg>`);
  const suitePhotoPlacement = isBusiness
    ? { top: 0, left: 0 }
    : { top: 27, left: 28 };
  const labelOverlay = null;
  const composites = [
    { input: suitePhoto, ...suitePhotoPlacement },
    ...(labelOverlay ? [{ input: labelOverlay, top: 0, left: 0 }] : [])
  ];

  return {
    buffer: await sharp(shell)
      .composite(composites)
      .png()
      .toBuffer(),
    top,
    left
  };
}

async function reviewCandidate({
  asset,
  candidateIndex,
  candidatePath,
  config,
  copy,
  decision,
  eventReview,
  sourceSuitePhoto,
  template,
  width,
  height
}) {
  if (config.mockReview) {
    return buildMockReview(candidateIndex, config);
  }

  const heuristic = buildHeuristicReview({
    asset,
    copy,
    decision,
    eventReview,
    sourceSuitePhoto,
    template,
    width,
    height,
    minimumScore: config.minimumScore
  });

  if (config.mock || !process.env.OPENAI_API_KEY || !config.reviewModel) {
    return heuristic;
  }

  try {
    const vision = await reviewCandidateWithOpenAi({
      candidatePath,
      config,
      copy,
      decision,
      eventReview,
      template
    });
    const criticalFailures = uniqueValues([
      ...heuristic.criticalFailures,
      ...vision.criticalFailures
    ]);
    const issues = uniqueValues([
      ...heuristic.issues,
      ...vision.issues
    ]);
    const score = Math.min(heuristic.score, vision.score);
    return {
      score,
      accepted: score >= config.minimumScore && criticalFailures.length === 0,
      reviewer: vision.reviewer,
      criticalFailures,
      issues,
      strengths: uniqueValues([...heuristic.strengths, ...vision.strengths])
    };
  } catch (error) {
    if (!config.fallbackToHeuristicWhenUnavailable) {
      throw error;
    }

    return {
      ...heuristic,
      reviewer: "heuristic-fallback",
      issues: [
        ...heuristic.issues,
        `OpenAI vision review unavailable: ${error.message}`
      ]
    };
  }
}

async function reviewCandidateWithOpenAi({ candidatePath, config, copy, decision, eventReview, template }) {
  const client = new OpenAI({ apiKey: process.env.OPENAI_API_KEY });
  const base64 = await fs.readFile(candidatePath, { encoding: "base64" });
  const prompt = `You are a strict senior social media art director reviewing a Bonhomia Suites ad.

Return only JSON with:
{
  "score": number 0-100,
  "criticalFailures": string[],
  "issues": string[],
  "strengths": string[]
}

Reject harshly for kindergarten/amateur layout, generic dark text cards, floating disconnected logo badges, clipped text, weak hierarchy, poor contrast, misleading suite/property features, fake rooms/buildings/balconies/views, overcrowded copy, or public claims without source.

Context:
- Template: ${template.id}
- Asset decision: ${decision.name}
- Headline: ${copy.headline}
- Subhead: ${copy.subhead}
- CTA: ${copy.cta}
- Event claim verified: ${eventReview.verified}
- Suite photo policy: suite photos and logos are deterministic layers; generated visuals must not imply false suite/property features.

An acceptable score is ${config.minimumScore}+ and should look like a bold editorial poster rather than a layout-script draft.`;

  const response = await client.responses.create({
    model: config.reviewModel,
    input: [
      {
        role: "user",
        content: [
          { type: "input_text", text: prompt },
          { type: "input_image", image_url: `data:image/png;base64,${base64}` }
        ]
      }
    ]
  });
  const text = response.output_text || extractResponseText(response);
  const parsed = parseJsonObject(text);
  const score = clampNumber(parsed.score, 0, 100, 0);
  const criticalFailures = stringArray(parsed.criticalFailures);
  return {
    score,
    accepted: score >= config.minimumScore && criticalFailures.length === 0,
    reviewer: `openai:${config.reviewModel}`,
    criticalFailures,
    issues: stringArray(parsed.issues),
    strengths: stringArray(parsed.strengths)
  };
}

function buildHeuristicReview({ copy, decision, eventReview, sourceSuitePhoto, template, width, height, minimumScore }) {
  let score = 91;
  const criticalFailures = [];
  const issues = [];
  const strengths = ["Uses deterministic editorial layout and short public copy."];

  if (width !== 1080 || height !== 1350) {
    criticalFailures.push(`Output dimensions are ${width}x${height}, expected 1080x1350.`);
    score -= 30;
  }

  if (/\d{4}-\d{2}-\d{2}/u.test(`${copy.headline} ${copy.subhead} ${copy.cta}`)) {
    criticalFailures.push("Public image copy includes internal date text.");
    score -= 30;
  }

  if (copy.headline.length > 34) {
    issues.push("Headline is longer than the editorial target.");
    score -= 6;
  }

  if (copy.subhead.length > 58) {
    issues.push("Subhead is longer than the editorial target.");
    score -= 5;
  }

  if (decision.usesSuite && !sourceSuitePhoto) {
    criticalFailures.push("Strategy requires a suite module but no real suite photo was selected.");
    score -= 35;
  }

  if (eventReview.mentionsEvent && !eventReview.verified) {
    if (copyContainsSpecificEvent(copy)) {
      criticalFailures.push("Specific event claim is not verified.");
      score -= 25;
    } else {
      issues.push("Original asset mentioned an unverified event; final image copy was made generic and needs review before publishing.");
      score -= 3;
    }
  }

  if (template.id === "business_direct_booking" || template.id === "experience_event_hook") {
    strengths.push("Uses an editorial template instead of a generic text-card layout.");
  }

  const accepted = score >= minimumScore && criticalFailures.length === 0;
  return {
    score,
    accepted,
    reviewer: "heuristic",
    criticalFailures,
    issues,
    strengths
  };
}

function buildMockReview(candidateIndex, config) {
  if (candidateIndex === 1) {
    return {
      score: 55,
      accepted: false,
      reviewer: "mock-review",
      criticalFailures: ["Mock rejection: kindergarten-style block layout."],
      issues: ["Mock rejection verifies candidate regeneration."],
      strengths: []
    };
  }

  return {
    score: Math.max(config.minimumScore + 4, 88),
    accepted: true,
    reviewer: "mock-review",
    criticalFailures: [],
    issues: [],
    strengths: ["Mock accepted candidate after regeneration."]
  };
}

function decideAssetTreatment(asset) {
  const text = normalizeForDecision([
    asset.hook,
    asset.concept,
    asset.visualDirection,
    asset.caption,
    asset.audience,
    asset.cta
  ].filter(Boolean).join(" "));
  const namedSuite = [...suiteNameSlugs.keys()].find((name) => text.includes(name));
  const explicitlyLogoOnly = /\b(logo only|solo logo|logo solo)\b/u.test(text);
  const explicitlyNoSuitePhoto = /\b(sin foto|sin suite|no suite photo|no room photo|no forced room|sin foto de suite)\b/u.test(text);
  const hasEvent = /\b(event|evento|feria|festival|arte|art|luciernaga|luciernagas|show|experiencia|temporada)\b/u.test(text);
  const hasBusiness = /\b(trabajo|business|empresa|empresas|corporativo|compania|companias|viajas por trabajo|viajero de negocio)\b/u.test(text);
  const hasDirectBooking = /\b(reserva|reservar|directo|booking|bonhomiasuites)\b/u.test(text);
  const hasLodgingPush = /\b(hosped|hospedate|hospedaje|stay|lodging|duerme|dormir|descansa|descanso|estancia|quedate|comodidad|comodo|room|habitacion)\b/u.test(text);

  if (explicitlyLogoOnly && !namedSuite) {
    return {
      name: "logo_only",
      usesSuite: false,
      reviewNotes: ["Logo-only treatment selected because the asset direction explicitly avoids suite photography."]
    };
  }

  if (explicitlyNoSuitePhoto && !namedSuite) {
    return hasBusiness
      ? {
          name: "business_brand_poster",
          usesSuite: false,
          reviewNotes: ["Business direct-booking creative explicitly avoids forced room photography."]
        }
      : {
          name: "logo_only",
          usesSuite: false,
          reviewNotes: ["Logo-only treatment selected because the asset direction explicitly avoids suite photography."]
        };
  }

  if (hasBusiness && !namedSuite && !hasEvent) {
    return {
      name: "business_brand_poster",
      usesSuite: false,
      reviewNotes: ["Business direct-booking creative uses a brand-led poster because no named business-specific suite photo was requested."]
    };
  }

  if (hasEvent && (hasLodgingPush || namedSuite)) {
    return {
      name: "logo_and_suite_card",
      usesSuite: true,
      reviewNotes: ["Generated event/campaign visual is separated from the real suite-photo lodging module."]
    };
  }

  if (namedSuite || (hasLodgingPush && !hasBusiness)) {
    return {
      name: "suite_card",
      usesSuite: true,
      reviewNotes: ["Real suite photo is used as a factual lodging module."]
    };
  }

  if (hasBusiness || hasDirectBooking) {
    return {
      name: "business_brand_poster",
      usesSuite: false,
      reviewNotes: ["Business/direct-booking creative does not force room imagery when the claim is not room-specific."]
    };
  }

  return {
    name: "logo_only",
    usesSuite: false,
    reviewNotes: ["Logo-only treatment selected because the concept does not require a suite photo."]
  };
}

function selectEditorialTemplate(asset, decision) {
  const text = normalizeForDecision([
    asset.hook,
    asset.concept,
    asset.visualDirection,
    asset.caption,
    asset.audience
  ].filter(Boolean).join(" "));

  if (/\b(luciernaga|luciernagas|experiencia|evento|feria|festival|arte|art|show)\b/u.test(text)) {
    return templateDefinitions.experience_event_hook;
  }

  if (/\b(trabajo|business|empresa|empresas|directo|reserva|viajas por trabajo)\b/u.test(text)) {
    return templateDefinitions.business_direct_booking;
  }

  if (!decision.usesSuite) {
    return templateDefinitions.destination_brand_awareness;
  }

  return templateDefinitions.business_direct_booking;
}

function reviewEventClaims(asset, intelligence) {
  const text = normalizeText([asset.hook, asset.concept, asset.caption].filter(Boolean).join(" "));
  const verifiedNames = [
    ...(intelligence?.publicExperiences || []),
    ...(intelligence?.upcomingExperiences || [])
  ].map((item) => normalizeText(item.name || item.code || ""));
  const mentionsEvent = /\b(feria|festival|evento|arte|art|luciernaga|luciernagas|show|san antonio)\b/u.test(text);
  const verified = verifiedNames.some((name) => name && text.includes(name));

  if (!mentionsEvent || verified) {
    return {
      mentionsEvent,
      verified,
      reviewNotes: verified ? ["Specific experience claim is backed by the public experiences export."] : []
    };
  }

  return {
    mentionsEvent,
    verified: false,
    reviewNotes: ["Specific event claim needs user-provided details or public research before publishing."]
  };
}

function buildEditorialCopy(asset, template, sourceSuitePhoto, eventReview) {
  const cta = sanitizeCta(asset.cta || "Reserva directo");
  if (template.id === "experience_event_hook") {
    const headline = eventReview.verified
      ? headlineFromEvent(asset)
      : "PLAN LOCAL";
    return {
      eyebrow: "EXPERIENCIA LOCAL",
      headline,
      subhead: eventReview.verified
        ? "Vive la experiencia. Duerme cómodo."
        : "Calpulalpan se disfruta descansando bien.",
      cta,
      suiteSubhead: "Ideal para quedarte cerca"
    };
  }

  if (template.id === "business_direct_booking") {
    return {
      eyebrow: "BONHOMIA SUITES",
      headline: "VIAJE DE TRABAJO",
      subhead: "Llega, descansa y reserva directo.",
      cta,
      suiteSubhead: sourceSuitePhoto ? "Hospedaje práctico" : "Reserva directa"
    };
  }

  return {
    eyebrow: "BONHOMIA SUITES",
    headline: "CALPULALPAN",
    subhead: "Tu estancia empieza aquí.",
    cta,
    suiteSubhead: "Hospedaje recomendado"
  };
}

function headlineFromEvent(asset) {
  const text = normalizeText([asset.hook, asset.concept, asset.caption].filter(Boolean).join(" "));
  if (text.includes("luciernaga")) {
    return "LUCIERNAGAS";
  }

  if (text.includes("feria")) {
    return "FERIA LOCAL";
  }

  if (text.includes("arte") || text.includes("art")) {
    return "ARTE EN CALPULALPAN";
  }

  return compactHeadline(asset.hook || asset.concept || "EXPERIENCIA LOCAL");
}

function compactHeadline(value) {
  const cleaned = String(value || "")
    .replace(/\([^)]*\)/gu, "")
    .replace(/\d{4}-\d{2}-\d{2}/gu, "")
    .replace(/\s+to\s+/giu, " ")
    .trim();
  const words = cleaned.split(/\s+/u).filter(Boolean).slice(0, 4);
  return (words.join(" ") || "BONHOMIA").toUpperCase();
}

function sanitizeCta(value) {
  const cleaned = String(value || "Reserva directo")
    .replace(/por\s+bonhomiasuites\.com/giu, "")
    .replace(/\s+/gu, " ")
    .trim();
  return cleaned.length > 24 ? "Reserva directo" : cleaned;
}

function copyContainsSpecificEvent(copy) {
  const value = normalizeText(`${copy.headline || ""} ${copy.subhead || ""} ${copy.cta || ""}`);
  return /\b(luciernaga|luciernagas|feria|san antonio|festival|avistamiento)\b/u.test(value);
}

function buildOpenAiImagePrompt(asset, decision, eventReview, template, copy) {
  const claimGuidance = eventReview.verified
    ? "The campaign may visually suggest the verified event/experience, but do not render readable event details."
    : "Use generic local-culture/destination atmosphere only; do not include specific dates, venue names, or factual event claims.";
  const templateGuidance = template.id === "business_direct_booking" && !decision.usesSuite
    ? "Business creative direction: abstract direct-booking/business-travel signals only, such as route rhythm, check-in flow, calendar geometry, refined paper texture, and premium architectural spacing. No office, desk, meeting room, hotel room, or person."
    : template.id === "experience_event_hook"
      ? "Experience creative direction: make the local hook the hero through abstract nature/event atmosphere, with room left for a factual suite module below."
      : "Destination creative direction: brand-led awareness with strong graphic composition and calm premium negative space.";

  return [
    "Create a premium vertical editorial poster background for Bonhomia Suites in Calpulalpan.",
    `Template: ${template.id}.`,
    `Headline intent: ${copy.headline}.`,
    `Campaign concept: ${asset.concept || asset.hook || "warm, practical, premium lodging marketing"}.`,
    claimGuidance,
    templateGuidance,
    "Use bold graphic composition, intentional negative space, high-contrast palette, subtle Mexican highland atmosphere, and editorial poster energy.",
    "Do not copy Airbnb, Airbnb logos, exact reference layouts, or any brand marks.",
    "Important: create an abstract/editorial campaign poster layer, not a realistic property photo.",
    "Do not create rooms, beds, furniture, windows, balconies, terraces, buildings, churches, hotel exteriors, interior design, amenities, signs, logos, readable text, people, or anything that could be mistaken for a Bonhomia suite or property feature.",
    "Leave clean negative space for deterministic headline, CTA, logo, and optional real-suite photo module.",
    decision.usesSuite
      ? "The real suite photo will be added separately by the composer; do not depict any suite interior."
      : "The Bonhomia logo will be added separately by the composer; do not draw or imitate the logo.",
    "Style: bold editorial travel poster, premium boutique hospitality, confident geometry, no clutter, no generic wallpaper."
  ].join("\n");
}

function buildCandidatePrompt(prompt, candidateIndex) {
  const variations = [
    "Variation: strong diagonal composition with a large calm negative-space area.",
    "Variation: bold circular crop energy, deep color contrast, and clean editorial rhythm.",
    "Variation: minimal geometric poster with premium texture and strong focal contrast."
  ];
  return `${prompt}\n${variations[(candidateIndex - 1) % variations.length]}`;
}

async function buildBonhomiaAssetCatalog(brand) {
  const suiteRoot = repoPath(brand.assets?.suiteImageRoot || "src/OrionERP.Bonhomia.Web/wwwroot/Images/Bonhomia/suites");
  const logoPath = repoPath(brand.assets?.logoPath || brand.assets?.repoImages?.logo);
  const suites = [];
  const entries = await fs.readdir(suiteRoot, { withFileTypes: true });
  for (const entry of entries) {
    if (!entry.isDirectory()) {
      continue;
    }

    const suiteName = entry.name;
    const dir = path.join(suiteRoot, entry.name);
    const files = (await fs.readdir(dir, { withFileTypes: true }))
      .filter((item) => item.isFile())
      .map((item) => item.name)
      .filter((file) => /\.(jpe?g|webp|png)$/iu.test(file))
      .filter((file) => !/(render|floor|planta|plano)/iu.test(file))
      .sort((a, b) => scoreSuitePhotoName(a) - scoreSuitePhotoName(b));

    if (files.length > 0) {
      suites.push({
        suiteName,
        slug: normalizeSuiteSlug(suiteName),
        files: files.map((file) => path.join(dir, file))
      });
    }
  }

  const editorialSuitePhotos = [];
  for (const relativePath of brand.assets?.editorialSuitePhotos || []) {
    const fullPath = repoPath(relativePath);
    if (!(await fileExists(fullPath))) {
      continue;
    }

    const suite = suites.find((item) => item.files.some((file) => path.resolve(file) === path.resolve(fullPath)));
    editorialSuitePhotos.push({
      suiteName: suite?.suiteName || path.basename(path.dirname(fullPath)),
      path: fullPath
    });
  }

  return {
    suiteRoot,
    logoPath,
    editorialSuitePhotos,
    suites
  };
}

function selectSuitePhoto(asset, catalog, intelligence) {
  const text = normalizeForDecision([asset.hook, asset.concept, asset.caption, asset.visualDirection].filter(Boolean).join(" "));
  const namedSuite = [...suiteNameSlugs.keys()].find((name) => text.includes(name));
  const preferredSlug = namedSuite ? suiteNameSlugs.get(namedSuite) : null;
  const selected = catalog.suites.find((suite) => suite.slug === preferredSlug)
    || null;

  if (selected) {
    return {
      suiteName: selected.suiteName,
      path: selected.files[0]
    };
  }

  const editorialPhoto = catalog.editorialSuitePhotos?.[0];
  if (editorialPhoto) {
    return editorialPhoto;
  }

  const performanceSuite = selectSuiteFromPerformance(intelligence, catalog);
  const performanceSelected = catalog.suites.find((suite) => suite.slug === performanceSuite)
    || catalog.suites.find((suite) => suite.slug === "penthouse")
    || catalog.suites[0];

  if (!performanceSelected) {
    return null;
  }

  return {
    suiteName: performanceSelected.suiteName,
    path: performanceSelected.files[0]
  };
}

function selectSuiteFromPerformance(intelligence, catalog) {
  const rows = (intelligence?.saludFinanciera?.suitePerformance || [])
    .filter((row) => typeof row.occupancyPct === "number")
    .map((row) => ({
      slug: normalizeSuiteSlug(row.roomName || ""),
      occupancyPct: row.occupancyPct,
      roomRevenue: row.roomRevenue || 0
    }))
    .filter((row) => catalog.suites.some((suite) => suite.slug === row.slug))
    .sort((a, b) => a.occupancyPct - b.occupancyPct || b.roomRevenue - a.roomRevenue);

  return rows[0]?.slug || null;
}

function resolveImageConfig(brand, args, options) {
  const provider = brand.providers?.image?.openai || {};
  const review = brand.providers?.image?.review || {};
  const output = brand.providers?.image?.output || {};
  return {
    model: process.env.MARKETING_IMAGE_MODEL || provider.model || "gpt-image-2",
    fallbackModel: process.env.MARKETING_IMAGE_FALLBACK_MODEL || provider.fallbackModel || "gpt-image-1",
    quality: process.env.MARKETING_IMAGE_QUALITY || provider.quality || "medium",
    backgroundSize: process.env.MARKETING_IMAGE_BACKGROUND_SIZE || provider.backgroundSize || "1024x1536",
    reviewModel: process.env.MARKETING_REVIEW_MODEL || review.model || "gpt-5-mini",
    minimumScore: Number(firstEnv("MARKETING_REVIEW_MIN_SCORE", "MARKETING_MIN_IMAGE_SCORE") || review.minimumScore || 82),
    maxAttempts: Number(firstEnv("MARKETING_REVIEW_MAX_ATTEMPTS", "MARKETING_IMAGE_MAX_ATTEMPTS") || review.maxAttempts || 3),
    candidatesPerAsset: Number(firstEnv("MARKETING_REVIEW_CANDIDATES", "MARKETING_IMAGE_CANDIDATES") || review.candidatesPerAsset || 2),
    strictReview: firstEnv("MARKETING_REVIEW_STRICT", "MARKETING_IMAGE_STRICT_REVIEW")
      ? firstEnv("MARKETING_REVIEW_STRICT", "MARKETING_IMAGE_STRICT_REVIEW") !== "0"
      : review.strict !== false,
    fallbackToHeuristicWhenUnavailable: review.fallbackToHeuristicWhenUnavailable !== false,
    width: Number(process.env.MARKETING_IMAGE_WIDTH || output.width || 1080),
    height: Number(process.env.MARKETING_IMAGE_HEIGHT || output.height || 1350),
    mock: options.mock || hasFlag("--mock-openai", args) || process.env.MARKETING_IMAGE_MOCK === "1",
    mockReview: options.mockReview || hasFlag("--mock-review", args) || process.env.MARKETING_REVIEW_MOCK === "1"
  };
}

function firstEnv(...names) {
  for (const name of names) {
    if (process.env[name] !== undefined && process.env[name] !== "") {
      return process.env[name];
    }
  }

  return null;
}

function buildUnsupportedVideoEntry(asset) {
  return {
    id: asset.id,
    type: asset.type,
    status: "unsupported_v1",
    message: "TikTok/Reels video generation is not implemented in this version. The concept, scenes, caption, and music direction are preserved for a future video generator.",
    concept: asset.concept || null,
    hook: asset.hook || null,
    scenes: asset.scenes || [],
    caption: asset.caption || null,
    futureTools: ["Sora/OpenAI video API", "Remotion", "ElevenLabs/OpenAI TTS", "licensed music library"]
  };
}

async function removeDirectoryWithRetry(dirPath) {
  for (let attempt = 1; attempt <= 5; attempt += 1) {
    try {
      await fs.rm(dirPath, { recursive: true, force: true });
      return null;
    } catch (error) {
      if (!["EBUSY", "ENOTEMPTY", "EPERM"].includes(error.code) || attempt === 5) {
        return `Temporary candidate cleanup skipped for ${repoRelative(dirPath)}: ${error.message}`;
      }

      await sleep(150 * attempt);
    }
  }

  return null;
}

function sleep(ms) {
  return new Promise((resolve) => {
    setTimeout(resolve, ms);
  });
}

function renderMediaReport(manifest) {
  const imageLines = manifest.assets.length === 0
    ? "- No supported image assets were found in the media plan."
    : manifest.assets
      .map((asset) => renderAssetReportLine(asset))
      .join("\n");
  const unsupportedLines = manifest.unsupported.length === 0
    ? "- No unsupported video assets were requested."
    : manifest.unsupported
      .map((asset) => `- ${asset.id}: ${asset.status}. ${asset.message}`)
      .join("\n");
  const rejectionLines = manifest.assets
    .flatMap((asset) => (asset.quality?.candidates || [])
      .filter((candidate) => !candidate.accepted)
      .map((candidate) => `- ${asset.id} candidate ${candidate.candidateIndex}: score ${candidate.score}; ${candidate.criticalFailures.concat(candidate.issues).join("; ") || "below threshold"}`));

  return `# Media Generation Report

Brand: ${manifest.brand.name}
Week: ${manifest.week.id}
Provider: ${manifest.provider.image}
Quality target: ${manifest.qualityGate.target}

## Generated Images

${imageLines}

## Rejected Candidates

${rejectionLines.length > 0 ? rejectionLines.join("\n") : "- No candidates were rejected."}

## Unsupported V1 Assets

${unsupportedLines}

## Factual Suite Photo Policy

- Suite photos are locked factual modules from the OrionERP Bonhomia repo.
- OpenAI generated only campaign/background layers.
- Real suite photos were not sent through generative edits.
- Logo was placed from the checked-in Bonhomia asset, not redrawn.

## Review Notes

${manifest.assets.flatMap((asset) => asset.reviewNotes || []).map((note) => `- ${note}`).join("\n") || "- No additional review notes."}
`;
}

function renderAssetReportLine(asset) {
  if (asset.status !== "generated") {
    return `- ${asset.id}: ${asset.status}, ${asset.template}, no final image written.`;
  }

  return [
    `- ${asset.id}: generated, ${asset.template}, score ${asset.quality?.score ?? "n/a"},`,
    `candidate ${asset.quality?.selectedCandidate ?? "n/a"},`,
    `${asset.outputPath}`
  ].join(" ");
}

function renderLessonProposals(manifest) {
  return `# Lesson Proposals: ${manifest.brand.name} Media Generation

Generated: ${manifest.generatedAtUtc}
Week: ${manifest.week.id}

## Proposed Lessons

- Treat image quality as an art-direction problem: template, typography, negative space, and QA matter as much as the model.
- Use reference images as design-rule sources, not as layouts to copy.
- Generate multiple candidates and keep only the best candidate that passes the quality gate.
- Reject generic text-card layouts, fake property visuals, clipped text, and disconnected logo badges.
- Do not force suite photos into generic business/direct-booking ads when the creative does not name a suite.
- Preserve unsupported TikTok video concepts instead of silently dropping them.

## Evidence From This Run

- Images processed: ${manifest.assets.length}
- Unsupported video assets: ${manifest.unsupported.length}
- Candidate decisions: ${manifest.assets.map((asset) => `${asset.id}=${asset.status}/${asset.template}/score:${asset.quality?.score ?? "n/a"}`).join(", ") || "none"}

Review these lessons before promoting them into the playbook.
`;
}

function buildEditorialOverlaySvg(template, copy, decision, width, height) {
  if (template.id === "experience_event_hook") {
    return buildExperienceOverlaySvg(template, copy, decision, width, height);
  }

  if (template.id === "destination_brand_awareness") {
    return buildDestinationOverlaySvg(template, copy, width, height);
  }

  return buildBusinessOverlaySvg(template, copy, decision, width, height);
}

function buildBusinessOverlaySvg(template, copy, decision, width, height) {
  if (!decision.usesSuite) {
    return buildBusinessBrandPosterSvg(template, copy, width, height);
  }

  const headline = splitHeadline(copy.headline, 12);
  const textX = 72;
  const textY = 690;
  const panelTop = 570;
  const ctaWidth = ctaButtonWidth(copy.cta, width, textX, 520);
  return `
<svg width="${width}" height="${height}" xmlns="http://www.w3.org/2000/svg">
  <rect width="${width}" height="${height}" fill="${template.palette.dark}" fill-opacity="0.28"/>
  <rect x="0" y="${panelTop}" width="${width}" height="${height - panelTop}" fill="${template.palette.dark}" fill-opacity="0.96"/>
  <rect x="0" y="${height - 150}" width="${width}" height="150" fill="${template.palette.cream}" fill-opacity="0.98"/>
  <rect x="${textX}" y="${textY - 108}" width="148" height="12" fill="${template.palette.accent}"/>
  <text x="${textX}" y="${textY - 64}" font-family="Arial, Helvetica, sans-serif" font-size="24" font-weight="700" letter-spacing="5" fill="${template.palette.cream}">${escapeXml(copy.eyebrow)}</text>
  ${headline.map((line, index) => `<text x="${textX}" y="${textY + index * 98}" font-family="Arial Black, Arial, Helvetica, sans-serif" font-size="82" font-weight="900" letter-spacing="0" fill="${template.palette.ink}">${escapeXml(line)}</text>`).join("")}
  <text x="${textX}" y="${textY + headline.length * 98 + 34}" font-family="Arial, Helvetica, sans-serif" font-size="35" font-weight="600" fill="${template.palette.cream}">${escapeXml(copy.subhead)}</text>
  <rect x="${textX}" y="${height - 286}" width="${ctaWidth}" height="92" fill="${template.palette.accent}"/>
  <text x="${textX + 36}" y="${height - 227}" font-family="Arial, Helvetica, sans-serif" font-size="36" font-weight="900" fill="${template.palette.cream}">${escapeXml(copy.cta)}</text>
  <text x="${textX}" y="${height - 84}" font-family="Arial, Helvetica, sans-serif" font-size="31" font-weight="800" fill="${template.palette.dark}">bonhomiasuites.com</text>
</svg>`;
}

function buildBusinessBrandPosterSvg(template, copy, width, height) {
  const headline = splitHeadline(copy.headline, 12);
  const textX = 72;
  const headlineTop = 365;
  const ctaWidth = ctaButtonWidth(copy.cta, width, textX, 600);
  const benefitTop = 760;
  return `
<svg width="${width}" height="${height}" xmlns="http://www.w3.org/2000/svg">
  <defs>
    <linearGradient id="businessShade" x1="0" y1="0" x2="1" y2="1">
      <stop offset="0" stop-color="${template.palette.dark}" stop-opacity="0.54"/>
      <stop offset="0.58" stop-color="${template.palette.dark}" stop-opacity="0.74"/>
      <stop offset="1" stop-color="${template.palette.dark}" stop-opacity="0.92"/>
    </linearGradient>
    <pattern id="businessGrid" width="64" height="64" patternUnits="userSpaceOnUse">
      <path d="M64 0 L0 64" stroke="${template.palette.cream}" stroke-width="2" stroke-opacity="0.09"/>
    </pattern>
  </defs>
  <rect width="${width}" height="${height}" fill="url(#businessShade)"/>
  <rect width="${width}" height="${height}" fill="url(#businessGrid)"/>
  <circle cx="112" cy="180" r="220" fill="${template.palette.cream}" fill-opacity="0.10"/>
  <circle cx="760" cy="1040" r="360" fill="${template.palette.accent}" fill-opacity="0.16"/>
  <path d="M72 194 C270 132, 470 164, 712 94" fill="none" stroke="${template.palette.cream}" stroke-width="10" stroke-opacity="0.38"/>
  <rect x="808" y="0" width="272" height="${height}" fill="${template.palette.cream}" fill-opacity="0.96"/>
  <rect x="774" y="0" width="34" height="${height}" fill="${template.palette.accent}"/>
  <text x="${textX}" y="178" font-family="Arial, Helvetica, sans-serif" font-size="25" font-weight="800" letter-spacing="6" fill="${template.palette.cream}">${escapeXml(copy.eyebrow)}</text>
  <rect x="${textX}" y="220" width="154" height="13" fill="${template.palette.accent}"/>
  ${headline.map((line, index) => `<text x="${textX}" y="${headlineTop + index * 108}" font-family="Arial Black, Arial, Helvetica, sans-serif" font-size="96" font-weight="900" letter-spacing="0" fill="${template.palette.ink}">${escapeXml(line)}</text>`).join("")}
  <text x="${textX}" y="${headlineTop + headline.length * 108 + 42}" font-family="Arial, Helvetica, sans-serif" font-size="37" font-weight="700" fill="${template.palette.cream}">${escapeXml(copy.subhead)}</text>
  <g transform="translate(${textX} ${benefitTop})">
    <rect x="0" y="0" width="560" height="2" fill="${template.palette.cream}" fill-opacity="0.38"/>
    <text x="0" y="76" font-family="Arial, Helvetica, sans-serif" font-size="31" font-weight="900" fill="${template.palette.cream}">RESERVA DIRECTA</text>
    <text x="0" y="128" font-family="Arial, Helvetica, sans-serif" font-size="31" font-weight="900" fill="${template.palette.cream}">ESTANCIA PRACTICA</text>
    <text x="0" y="180" font-family="Arial, Helvetica, sans-serif" font-size="31" font-weight="900" fill="${template.palette.cream}">CALPULALPAN</text>
  </g>
  <rect x="${textX}" y="1052" width="${ctaWidth}" height="96" fill="${template.palette.accent}"/>
  <text x="${textX + 36}" y="1114" font-family="Arial, Helvetica, sans-serif" font-size="35" font-weight="900" fill="${template.palette.cream}">${escapeXml(copy.cta)}</text>
  <text x="${textX}" y="1234" font-family="Arial, Helvetica, sans-serif" font-size="33" font-weight="900" fill="${template.palette.cream}">bonhomiasuites.com</text>
  <text x="872" y="970" font-family="Arial, Helvetica, sans-serif" font-size="22" font-weight="900" letter-spacing="5" fill="${template.palette.dark}" transform="rotate(90 872 970)">BONHOMIA SUITES</text>
</svg>`;
}

function buildExperienceOverlaySvg(template, copy, decision, width, height) {
  const headline = splitHeadline(copy.headline, 14);
  const textX = 70;
  const textY = 222;
  const ctaWidth = ctaButtonWidth(copy.cta, width, textX, 560);
  return `
<svg width="${width}" height="${height}" xmlns="http://www.w3.org/2000/svg">
  <rect width="${width}" height="${height}" fill="${template.palette.dark}" fill-opacity="0.55"/>
  <rect x="0" y="0" width="${width}" height="820" fill="${template.palette.dark}" fill-opacity="0.58"/>
  <rect x="0" y="${height - 150}" width="${width}" height="150" fill="${template.palette.cream}" fill-opacity="0.98"/>
  <rect x="${textX}" y="${textY - 108}" width="148" height="12" fill="${template.palette.accent}"/>
  <text x="${textX}" y="${textY - 64}" font-family="Arial, Helvetica, sans-serif" font-size="24" font-weight="700" letter-spacing="5" fill="${template.palette.cream}">${escapeXml(copy.eyebrow)}</text>
  ${headline.map((line, index) => `<text x="${textX}" y="${textY + index * 106}" font-family="Arial Black, Arial, Helvetica, sans-serif" font-size="88" font-weight="900" letter-spacing="0" fill="${template.palette.ink}">${escapeXml(line)}</text>`).join("")}
  <text x="${textX}" y="${textY + headline.length * 106 + 38}" font-family="Arial, Helvetica, sans-serif" font-size="34" font-weight="700" fill="${template.palette.cream}">${escapeXml(copy.subhead)}</text>
  <rect x="${textX}" y="720" width="${ctaWidth}" height="86" fill="${template.palette.accent}"/>
  <text x="${textX + 34}" y="776" font-family="Arial, Helvetica, sans-serif" font-size="33" font-weight="900" fill="#1f1a16">${escapeXml(copy.cta)}</text>
  <text x="${textX}" y="${height - 84}" font-family="Arial, Helvetica, sans-serif" font-size="31" font-weight="800" fill="${template.palette.dark}">bonhomiasuites.com</text>
</svg>`;
}

function buildDestinationOverlaySvg(template, copy, width, height) {
  const headline = splitHeadline(copy.headline, 13);
  return `
<svg width="${width}" height="${height}" xmlns="http://www.w3.org/2000/svg">
  <rect width="${width}" height="${height}" fill="${template.palette.dark}" fill-opacity="0.82"/>
  <rect x="0" y="${height - 270}" width="${width}" height="270" fill="${template.palette.cream}" fill-opacity="0.96"/>
  <rect x="58" y="96" width="150" height="14" fill="${template.palette.cream}"/>
  <text x="72" y="320" font-family="Arial, Helvetica, sans-serif" font-size="24" font-weight="700" letter-spacing="5" fill="${template.palette.cream}">${escapeXml(copy.eyebrow)}</text>
  ${headline.map((line, index) => `<text x="70" y="${440 + index * 112}" font-family="Arial Black, Arial, Helvetica, sans-serif" font-size="96" font-weight="900" fill="${template.palette.ink}">${escapeXml(line)}</text>`).join("")}
  <text x="76" y="${height - 160}" font-family="Arial, Helvetica, sans-serif" font-size="40" font-weight="700" fill="#1f1a16">${escapeXml(copy.subhead)}</text>
  <text x="76" y="${height - 96}" font-family="Arial, Helvetica, sans-serif" font-size="30" font-weight="800" fill="${template.palette.dark}">${escapeXml(copy.cta)}</text>
</svg>`;
}

function getLogoPlacement(template, decision) {
  if (template.id === "business_direct_booking" && !decision.usesSuite) {
    return {
      panelTop: 0,
      panelLeft: 0,
      panelWidth: 0,
      panelHeight: 0,
      logoTop: 84,
      logoLeft: 882
    };
  }

  if (template.id === "destination_brand_awareness") {
    return {
      panelTop: 0,
      panelLeft: 0,
      panelWidth: 0,
      panelHeight: 0,
      logoTop: 1124,
      logoLeft: 846
    };
  }

  return {
    panelTop: 0,
    panelLeft: 0,
    panelWidth: 0,
    panelHeight: 0,
    logoTop: 1204,
    logoLeft: 858
  };
}

function buildBrandPanelSvg(template, placement) {
  return `
<svg width="${placement.panelWidth}" height="${placement.panelHeight}" xmlns="http://www.w3.org/2000/svg">
  <rect x="0" y="0" width="${placement.panelWidth}" height="${placement.panelHeight}" fill="${template.palette.cream}" fill-opacity="0.96"/>
  <rect x="10" y="10" width="${placement.panelWidth - 20}" height="${placement.panelHeight - 20}" fill="none" stroke="${template.palette.accent}" stroke-width="3"/>
</svg>`;
}

function ctaButtonWidth(value, width, left, minimum) {
  const estimated = String(value || "").length * 23 + 92;
  return Math.min(width - left * 2, Math.max(minimum, estimated));
}

async function buildMockCampaignLayer(width, height, context) {
  const colors = context?.candidateIndex === 1
    ? ["#d2b28b", "#cfc5b3", "#8a6b50"]
    : ["#073f36", "#c94d3d", "#fff2d8"];
  const svg = `
<svg width="${width}" height="${height}" xmlns="http://www.w3.org/2000/svg">
  <defs>
    <linearGradient id="bg" x1="0" y1="0" x2="1" y2="1">
      <stop offset="0" stop-color="${colors[0]}"/>
      <stop offset="0.52" stop-color="${colors[1]}"/>
      <stop offset="1" stop-color="${colors[2]}"/>
    </linearGradient>
    <pattern id="lines" width="56" height="56" patternUnits="userSpaceOnUse">
      <path d="M0 56 L56 0" stroke="#fffaf3" stroke-width="3" stroke-opacity="0.12"/>
    </pattern>
  </defs>
  <rect width="${width}" height="${height}" fill="url(#bg)"/>
  <rect width="${width}" height="${height}" fill="url(#lines)"/>
  <circle cx="${width - 180}" cy="240" r="240" fill="#fffaf3" fill-opacity="0.16"/>
</svg>`;
  return sharp(Buffer.from(svg)).png().toBuffer();
}

function splitHeadline(value, maxChars) {
  const words = String(value || "").toUpperCase().split(/\s+/u).filter(Boolean);
  const lines = [];
  let current = "";
  for (const word of words) {
    const next = current ? `${current} ${word}` : word;
    if (next.length > maxChars && current) {
      lines.push(current);
      current = word;
    } else {
      current = next;
    }
  }

  if (current) {
    lines.push(current);
  }

  return lines.slice(0, 3);
}

function scoreSuitePhotoName(fileName) {
  const match = fileName.match(/^(\d+)/u);
  return match ? Number(match[1]) : 100;
}

function normalizeSuiteSlug(value) {
  return normalizeText(value)
    .replace(/\s+/gu, "-")
    .replace(/[^a-z0-9-]/gu, "");
}

function normalizeText(value) {
  return String(value || "")
    .normalize("NFD")
    .replace(/\p{Diacritic}/gu, "")
    .toLowerCase();
}

function normalizeForDecision(value) {
  return normalizeText(value)
    .replace(/bonhomia suites/gu, "bonhomia")
    .replace(/[^a-z0-9\s-]/gu, " ");
}

function safeFileName(value) {
  return String(value || "asset")
    .toLowerCase()
    .replace(/[^a-z0-9]+/gu, "-")
    .replace(/^-|-$/gu, "")
    .slice(0, 80) || "asset";
}

function titleCase(value) {
  return String(value || "")
    .split(/[-_\s]+/u)
    .filter(Boolean)
    .map((part) => part.slice(0, 1).toUpperCase() + part.slice(1).toLowerCase())
    .join(" ");
}

function repoRelative(filePath) {
  return toForwardSlash(path.relative(repoRoot, filePath));
}

function escapeXml(value) {
  return String(value || "")
    .replace(/&/gu, "&amp;")
    .replace(/</gu, "&lt;")
    .replace(/>/gu, "&gt;")
    .replace(/"/gu, "&quot;");
}

function clampNumber(value, min, max, fallback) {
  const numeric = Number(value);
  if (!Number.isFinite(numeric)) {
    return fallback;
  }

  return Math.max(min, Math.min(max, numeric));
}

function stringArray(value) {
  return Array.isArray(value)
    ? value.map((item) => String(item)).filter(Boolean)
    : [];
}

function uniqueValues(values) {
  return [...new Set(values.filter(Boolean))];
}

function parseJsonObject(text) {
  const value = String(text || "").trim();
  try {
    return JSON.parse(value);
  } catch {
    const match = value.match(/\{[\s\S]*\}/u);
    if (match) {
      return JSON.parse(match[0]);
    }
  }

  throw new Error("OpenAI vision review did not return parseable JSON.");
}

function extractResponseText(response) {
  return (response.output || [])
    .flatMap((item) => item.content || [])
    .map((content) => content.text || "")
    .join("\n")
    .trim();
}

if (isDirectRun(import.meta.url)) {
  buildMediaPackage()
    .then(({ mediaRoot, manifest }) => {
      const generated = manifest.assets.filter((asset) => asset.status === "generated").length;
      const failed = manifest.assets.filter((asset) => asset.status !== "generated").length;
      console.log(`Media package written to ${mediaRoot}`);
      console.log(`Generated images: ${generated}`);
      console.log(`Failed quality gate: ${failed}`);
      console.log(`Unsupported v1 assets: ${manifest.unsupported.length}`);
    })
    .catch((error) => {
      console.error(error.message);
      process.exit(1);
    });
}
