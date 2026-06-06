import "dotenv/config";
import crypto from "node:crypto";
import { createReadStream } from "node:fs";
import fs from "node:fs/promises";
import path from "node:path";
import OpenAI, { toFile } from "openai";
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
const visualDesignSystemPath = "tools/marketing/docs/visual-design-system.md";
const playbookPath = "tools/marketing/knowledge/playbook.md";
const defaultLessonInboxFile = "bonhomia-media-generation-quality.md";
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
    family: "business",
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
    family: "experience",
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
    family: "destination",
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
  const learningContext = await loadLearningContext();
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
      backgroundSize: config.backgroundSize,
      photoMode: config.photoMode,
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
    learning: {
      playbook: {
        path: playbookPath,
        hash: learningContext.playbookHash,
        acceptedRulesUsed: learningContext.playbookRules.length
      },
      designSystem: {
        path: visualDesignSystemPath,
        hash: learningContext.designSystemHash,
        acceptedRulesUsed: learningContext.designRules.length
      },
      lessonArtifactPath: null,
      lessonInboxPath: null,
      lessonInboxStatus: null
    },
    factualImagePolicy: brand.assets?.factualImagePolicy || {},
    designSystem: {
      rules: visualDesignSystemPath,
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
      learningContext,
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
  const lessonArtifactPath = path.join(mediaRoot, "lesson-proposals.md");
  await writeText(lessonArtifactPath, renderLessonProposals(manifest));
  manifest.learning.lessonArtifactPath = repoRelative(lessonArtifactPath);

  if (!hasFlag("--no-lesson-inbox", args)) {
    const lessonInboxResult = await writeLessonInboxProposal(manifest);
    manifest.learning.lessonInboxPath = lessonInboxResult.relativePath;
    manifest.learning.lessonInboxStatus = lessonInboxResult.status;
  } else {
    manifest.learning.lessonInboxStatus = "skipped";
  }

  await writeJson(path.join(mediaRoot, "media-manifest.json"), manifest);
  await writeText(path.join(mediaRoot, "media-generation-report.md"), renderMediaReport(manifest));

  return {
    outputRoot,
    mediaRoot,
    manifest
  };
}

async function generateImageAsset({ asset, brand, catalog, config, imageRoot, tempRoot, intelligence, learningContext }) {
  const decision = decideAssetTreatment(asset);
  const template = selectEditorialTemplate(asset, decision);
  const eventReview = reviewEventClaims(asset, intelligence);
  const sourceSuitePhoto = decision.usesSuite
    ? selectSuitePhoto(asset, catalog, intelligence)
    : null;
  const sourceHeroPhoto = decision.usesHeroPhoto
    ? selectHeroPhoto(asset, catalog, intelligence, decision)
    : null;
  const sourceLogo = repoPath(brand.assets?.logoPath || brand.assets?.repoImages?.logo);
  const copy = buildEditorialCopy(asset, template, sourceHeroPhoto || sourceSuitePhoto, eventReview, decision);
  const creativeFamily = getCreativeFamily(template, decision);
  const prompt = decision.usesHeroPhoto
    ? buildPhotoLedImagePrompt(asset, decision, eventReview, template, copy, learningContext, sourceHeroPhoto)
    : buildOpenAiImagePrompt(asset, decision, eventReview, template, copy, learningContext);
  const outputPath = path.join(imageRoot, `${safeFileName(asset.id || asset.type)}.png`);
  const candidateRoot = path.join(tempRoot, safeFileName(asset.id || asset.type));
  await ensureDir(candidateRoot);

  const candidates = [];
  const minimumCandidates = Math.max(1, config.candidatesPerAsset);
  const maxCandidates = Math.max(minimumCandidates, config.maxAttempts);

  for (let candidateIndex = 1; candidateIndex <= maxCandidates; candidateIndex += 1) {
    const candidatePrompt = buildCandidatePrompt(prompt, candidateIndex);
    const generatedLayer = decision.usesHeroPhoto
      ? await generatePhotoHeroLayer({
          asset,
          config,
          context: { candidateIndex, template },
          copy,
          decision,
          prompt: candidatePrompt,
          sourceHeroPhoto,
          template
        })
      : await generateCampaignLayer(
          candidatePrompt,
          config,
          { candidateIndex, template }
        );
    const imageBase = buildImageBaseEvidence(generatedLayer, sourceHeroPhoto);
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
      sourceHeroPhoto,
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
      learningContext,
      sourceHeroPhoto,
      sourceSuitePhoto,
      template,
      deterministicChecks: composed.deterministicChecks,
      width: composed.width,
      height: composed.height
    });
    candidates.push({
      candidateIndex,
      score: review.score,
      accepted: review.accepted,
      reviewer: review.reviewer,
      reviewerMode: review.reviewerMode,
      imageBase,
      sourceHeroPhoto: sourceHeroPhoto ? repoRelative(sourceHeroPhoto.path) : null,
      deterministicChecks: review.deterministicChecks,
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
        imageBase: acceptable.imageBase,
        learningContext,
        metadata,
        outputPath,
        prompt,
        selectedCandidate: acceptable,
        sourceLogo,
        sourceHeroPhoto,
        sourceSuitePhoto,
        template,
        creativeFamily
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
    creativeFamily,
    outputPath: null,
    sourceLogo: repoRelative(sourceLogo),
    sourceHeroPhoto: sourceHeroPhoto ? repoRelative(sourceHeroPhoto.path) : null,
    sourceHeroKind: sourceHeroPhoto?.kind || null,
    sourceSuitePhoto: sourceSuitePhoto ? repoRelative(sourceSuitePhoto.path) : null,
    sourceSuiteName: sourceSuitePhoto?.suiteName || null,
    openAi: {
      prompt,
      promptPolicy: buildPromptPolicy(decision, config, sourceHeroPhoto, true)
    },
    quality: {
      minimumScore: config.minimumScore,
      selectedCandidate: null,
      bestCandidate,
      rejectedCandidates: candidates.filter((candidate) => !candidate.accepted),
      acceptedCandidates: candidates.filter((candidate) => candidate.accepted),
      candidates
    },
    learning: buildAssetLearningEvidence(learningContext),
    factualPhotoPolicy: buildFactualPhotoPolicy(decision, config, sourceHeroPhoto),
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
  creativeFamily,
  imageBase,
  learningContext,
  metadata,
  outputPath,
  prompt,
  selectedCandidate,
  sourceLogo,
  sourceHeroPhoto,
  sourceSuitePhoto,
  template
}) {
  return {
    id: asset.id,
    type: asset.type,
    status: "generated",
    assetDecision: decision.name,
    template: template.id,
    creativeFamily,
    outputPath: repoRelative(outputPath),
    sourceLogo: repoRelative(sourceLogo),
    sourceHeroPhoto: sourceHeroPhoto ? repoRelative(sourceHeroPhoto.path) : null,
    sourceHeroKind: sourceHeroPhoto?.kind || null,
    sourceSuitePhoto: sourceSuitePhoto ? repoRelative(sourceSuitePhoto.path) : null,
    sourceSuiteName: sourceSuitePhoto?.suiteName || null,
    dimensions: {
      width: metadata.width,
      height: metadata.height
    },
    publicCopy: copy,
    openAi: {
      model: imageBase?.model || null,
      fallbackUsed: Boolean(imageBase?.fallbackUsed),
      requestedSize: imageBase?.requestedSize || null,
      sourceMode: imageBase?.sourceMode || null,
      prompt,
      promptPolicy: buildPromptPolicy(decision, config, sourceHeroPhoto, false)
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
    learning: buildAssetLearningEvidence(learningContext),
    factualPhotoPolicy: buildFactualPhotoPolicy(decision, config, sourceHeroPhoto),
    reviewNotes: [
      ...eventReview.reviewNotes,
      ...decision.reviewNotes,
      ...(sourceHeroPhoto
        ? [`Photo-led poster used a locked repo ${sourceHeroPhoto.kind || "property"} photo as the main canvas.`]
        : []),
      ...(sourceSuitePhoto ? [] : ["No suite photo module was used because the strategy did not require a separate factual suite card."])
    ]
  };
}

async function generatePhotoHeroLayer({ asset, config, context, copy, decision, prompt, sourceHeroPhoto, template }) {
  if (!sourceHeroPhoto?.path) {
    throw new Error(`Photo-led creative '${asset.id || asset.type}' could not find a repo source photo.`);
  }

  if (config.photoMode === "reference-edit" && !config.mock) {
    return generateReferenceEditLayer({
      config,
      copy,
      decision,
      prompt,
      sourceHeroPhoto,
      template
    });
  }

  const variant = getPhotoPosterVariant(context?.candidateIndex || 1);
  const buffer = await buildDeterministicPhotoLayer(sourceHeroPhoto, config, variant);
  return {
    buffer,
    model: config.mock ? "mock-photo-led" : "deterministic-photo-led",
    requestedSize: `${config.width}x${config.height}`,
    fallbackUsed: false,
    sourceMode: "deterministic-photo-led",
    sourcePhoto: sourceHeroPhoto.path
  };
}

async function buildDeterministicPhotoLayer(sourceHeroPhoto, config, variant) {
  return sharp(sourceHeroPhoto.path)
    .resize(config.width, config.height, { fit: "cover", position: variant.position })
    .modulate({
      brightness: variant.brightness,
      saturation: variant.saturation
    })
    .linear(variant.contrast, variant.offset)
    .sharpen({ sigma: 0.6 })
    .png()
    .toBuffer();
}

async function generateReferenceEditLayer({ config, copy, decision, prompt, sourceHeroPhoto, template }) {
  if (!process.env.OPENAI_API_KEY) {
    throw new Error("Missing OPENAI_API_KEY. Set it to use MARKETING_IMAGE_PHOTO_MODE=reference-edit.");
  }

  const client = new OpenAI({ apiKey: process.env.OPENAI_API_KEY });
  const sourceUpload = await toFile(
    createReadStream(sourceHeroPhoto.path),
    path.basename(sourceHeroPhoto.path),
    { type: imageMimeType(sourceHeroPhoto.path) }
  );
  const requestedSize = resolveOpenAiImageSize(config.model, config.backgroundSize);
  const result = await client.images.edit({
    model: config.model,
    image: sourceUpload,
    prompt: [
      prompt,
      "",
      "Reference-edit guardrails:",
      "Preserve the source photo's real Bonhomia room/property structure, layout, windows, furniture, doors, floors, walls, facade, and visible objects.",
      "Do not add, remove, replace, or rearrange factual suite/property features.",
      "Do not create readable text, logos, badges, UI, signage, or a complete advertisement layout.",
      `Leave clean space for deterministic overlay copy: ${copy.headline}; ${copy.subhead}; ${copy.cta}.`,
      `Template: ${template.id}; decision: ${decision.name}.`
    ].join("\n"),
    size: requestedSize,
    quality: config.quality,
    n: 1
  });
  const image = await readOpenAiImageResult(result);
  return {
    buffer: image.buffer,
    model: config.model,
    requestedSize,
    fallbackUsed: false,
    sourceMode: "openai-reference-edit",
    sourcePhoto: sourceHeroPhoto.path
  };
}

async function generateCampaignLayer(prompt, config, context) {
  if (config.mock) {
    return {
      buffer: await buildMockCampaignLayer(config.width, config.height, context),
      model: "mock-openai",
      requestedSize: `${config.width}x${config.height}`,
      fallbackUsed: false,
      sourceMode: "openai-generated-background"
    };
  }

  if (!process.env.OPENAI_API_KEY) {
    throw new Error("Missing OPENAI_API_KEY. Set it to generate marketing images, or run tests with --mock-openai.");
  }

  const client = new OpenAI({ apiKey: process.env.OPENAI_API_KEY });
  try {
    const generated = await callOpenAiImage(client, config.model, prompt, config);
    return {
      buffer: generated.buffer,
      model: config.model,
      requestedSize: generated.requestedSize,
      fallbackUsed: false,
      sourceMode: "openai-generated-background"
    };
  } catch (error) {
    if (!config.fallbackModel || config.fallbackModel === config.model) {
      throw error;
    }

    const generated = await callOpenAiImage(client, config.fallbackModel, prompt, config);
    return {
      buffer: generated.buffer,
      model: config.fallbackModel,
      requestedSize: generated.requestedSize,
      fallbackUsed: true,
      sourceMode: "openai-generated-background"
    };
  }
}

async function callOpenAiImage(client, model, prompt, config) {
  const requestedSize = resolveOpenAiImageSize(model, config.backgroundSize);
  const result = await client.images.generate({
    model,
    prompt,
    size: requestedSize,
    quality: config.quality,
    n: 1
  });
  return readOpenAiImageResult(result, requestedSize);
}

async function readOpenAiImageResult(result, requestedSize = null) {
  const item = result.data?.[0];
  if (item?.b64_json) {
    return {
      buffer: Buffer.from(item.b64_json, "base64"),
      requestedSize
    };
  }

  if (item?.url) {
    const response = await fetch(item.url);
    if (!response.ok) {
      throw new Error(`OpenAI image URL download failed with ${response.status}.`);
    }

    return {
      buffer: Buffer.from(await response.arrayBuffer()),
      requestedSize
    };
  }

  throw new Error("OpenAI image response did not include b64_json or url.");
}

function buildImageBaseEvidence(generatedLayer, sourceHeroPhoto) {
  return {
    model: generatedLayer.model,
    requestedSize: generatedLayer.requestedSize,
    fallbackUsed: Boolean(generatedLayer.fallbackUsed),
    sourceMode: generatedLayer.sourceMode || "unknown",
    sourcePhoto: sourceHeroPhoto ? repoRelative(sourceHeroPhoto.path) : null
  };
}

function buildPromptPolicy(decision, config, sourceHeroPhoto, failed) {
  if (decision.usesHeroPhoto) {
    if (config.photoMode === "reference-edit") {
      return failed
        ? "OpenAI reference-edit candidates used a real repo photo as image input; no candidate passed the strict quality gate."
        : "OpenAI reference-edit mode used a real repo photo as image input; logo, final text, CTA, and publishable layout were deterministic locked layers.";
    }

    return failed
      ? "Candidates used deterministic real-photo poster bases from the repo; no candidate passed the strict quality gate."
      : "The final poster used a deterministic real-photo base from the repo; logo, final text, CTA, and layout were deterministic locked layers.";
  }

  return failed
    ? "OpenAI generated only candidate campaign/background layers; no candidate passed the strict quality gate."
    : "OpenAI generated only the campaign/background layer; suite photos, logo, final text, and layout were deterministic locked layers.";
}

function buildFactualPhotoPolicy(decision, config, sourceHeroPhoto) {
  if (!decision.usesHeroPhoto) {
    return "Suite photos were not sent to OpenAI and were only cropped/resized/lightly enhanced as deterministic modules.";
  }

  if (config.photoMode === "reference-edit") {
    return `Reference-edit mode sent ${repoRelative(sourceHeroPhoto.path)} to OpenAI; production review must reject any altered room/property facts before publishing.`;
  }

  return `Photo-led mode used ${repoRelative(sourceHeroPhoto.path)} as a deterministic full-bleed base with crop, resize, color, contrast, and sharpen adjustments only.`;
}

function resolveOpenAiImageSize(model, backgroundSize) {
  if (String(model || "").startsWith("gpt-image-2")) {
    return backgroundSize;
  }

  return /x/u.test(String(backgroundSize || "")) && String(backgroundSize) !== "1280x1600"
    ? backgroundSize
    : "1024x1536";
}

function getPhotoPosterVariant(candidateIndex) {
  const variants = [
    { position: "attention", brightness: 0.88, saturation: 1.08, contrast: 1.05, offset: -5 },
    { position: "center", brightness: 0.94, saturation: 1.04, contrast: 1.04, offset: -4 },
    { position: "entropy", brightness: 0.9, saturation: 1.12, contrast: 1.07, offset: -7 },
    { position: "north", brightness: 0.92, saturation: 1.02, contrast: 1.06, offset: -6 },
    { position: "south", brightness: 0.89, saturation: 1.08, contrast: 1.08, offset: -8 },
    { position: "attention", brightness: 0.96, saturation: 1.0, contrast: 1.03, offset: -3 }
  ];
  return variants[(candidateIndex - 1) % variants.length];
}

function imageMimeType(filePath) {
  const ext = path.extname(filePath).toLowerCase();
  if (ext === ".jpg" || ext === ".jpeg") return "image/jpeg";
  if (ext === ".webp") return "image/webp";
  return "image/png";
}

async function composeSocialImage({
  brand,
  copy,
  decision,
  eventReview,
  generatedLayer,
  outputPath,
  sourceLogo,
  sourceHeroPhoto,
  sourceSuitePhoto,
  template,
  width,
  height
}) {
  const suiteModule = sourceSuitePhoto
    ? await buildSuiteModule(sourceSuitePhoto, template, copy, decision)
    : null;
  const logoPlacement = getLogoPlacement(template, decision);
  let logoMeta = null;
  const basePipeline = sharp(generatedLayer.buffer)
    .resize(width, height, { fit: "cover", position: "attention" });
  const base = await (decision.usesHeroPhoto
    ? basePipeline
    : basePipeline.modulate({ brightness: 0.92, saturation: 0.92 }))
    .png()
    .toBuffer();

  const overlays = [
    { input: Buffer.from(buildEditorialOverlaySvg(template, copy, decision, width, height)), top: 0, left: 0 }
  ];

  if (suiteModule) {
    overlays.push({
      input: suiteModule.buffer,
      top: suiteModule.top,
      left: suiteModule.left
    });
  }

  const sourceLogoExists = await fileExists(sourceLogo);
  if (sourceLogoExists && decision.usesHeroPhoto) {
    logoMeta = { width: 360, height: 112 };
  } else if (sourceLogoExists) {
    const logoWidth = decision.usesHeroPhoto
      ? 180
      : template.id === "destination_brand_awareness" ? 150 : 124;
    const logoPipeline = sharp(sourceLogo)
      .resize({ width: logoWidth, withoutEnlargement: true });
    const logo = await (decision.usesHeroPhoto
      ? logoPipeline.tint("#fffaf3")
      : logoPipeline)
      .png()
      .toBuffer();
    logoMeta = await sharp(logo).metadata();
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

  const metadata = await sharp(outputPath).metadata();
  return {
    ...metadata,
    deterministicChecks: buildDeterministicChecks({
      copy,
      decision,
      eventReview,
      height,
      logoMeta,
      logoPlacement,
      sourceLogoExists,
      sourceHeroPhoto,
      sourceSuitePhoto,
      suiteModule,
      template,
      width
    })
  };
}

async function buildSuiteModule(sourceSuitePhoto, template, copy, decision) {
  const isBusiness = template.id === "business_direct_booking";
  const isExperience = template.id === "experience_event_hook";
  const cardWidth = isBusiness ? 900 : 920;
  const cardHeight = isBusiness ? 438 : 300;
  const photoWidth = isBusiness ? cardWidth : 338;
  const photoHeight = isBusiness ? cardHeight : 246;
  const top = isBusiness ? 64 : isExperience ? 820 : 875;
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
    ? `<rect x="0" y="0" width="${cardWidth}" height="${cardHeight}" fill="#fff7e8"/>
       <rect x="0" y="${cardHeight - 86}" width="${cardWidth}" height="86" fill="#fff7e8" fill-opacity="0.93"/>
       <rect x="0" y="${cardHeight - 86}" width="12" height="86" fill="${template.palette.accent}"/>
       <text x="34" y="${cardHeight - 48}" font-family="Arial, Helvetica, sans-serif" font-size="28" font-weight="900" fill="#1f1a16">${escapeXml(label)}</text>
       <text x="34" y="${cardHeight - 19}" font-family="Arial, Helvetica, sans-serif" font-size="20" font-weight="800" fill="${template.palette.dark}">${escapeXml(subLabel)}</text>`
    : `<rect x="0" y="0" width="${cardWidth}" height="${cardHeight}" fill="#fff7e8"/>
       <rect x="${photoWidth + 34}" y="46" width="5" height="${cardHeight - 92}" fill="${template.palette.accent}"/>
       <text x="${photoWidth + 70}" y="112" font-family="Arial, Helvetica, sans-serif" font-size="38" font-weight="900" fill="#1f1a16">${escapeXml(label)}</text>
       <text x="${photoWidth + 70}" y="162" font-family="Arial, Helvetica, sans-serif" font-size="28" font-weight="700" fill="#40382f">${escapeXml(subLabel)}</text>
       <text x="${photoWidth + 70}" y="218" font-family="Arial, Helvetica, sans-serif" font-size="24" font-weight="700" fill="${template.palette.dark}">Bonhomia Suites</text>`}
</svg>`);
  const suitePhotoPlacement = isBusiness
    ? { top: 0, left: 0 }
    : { top: 27, left: 28 };
  const labelOverlay = isBusiness
    ? Buffer.from(`
<svg width="${cardWidth}" height="${cardHeight}" xmlns="http://www.w3.org/2000/svg">
  <rect x="0" y="${cardHeight - 86}" width="${cardWidth}" height="86" fill="#fff7e8" fill-opacity="0.93"/>
  <rect x="0" y="${cardHeight - 86}" width="12" height="86" fill="${template.palette.accent}"/>
  <text x="34" y="${cardHeight - 48}" font-family="Arial, Helvetica, sans-serif" font-size="28" font-weight="900" fill="#1f1a16">${escapeXml(label)}</text>
  <text x="34" y="${cardHeight - 19}" font-family="Arial, Helvetica, sans-serif" font-size="20" font-weight="800" fill="${template.palette.dark}">${escapeXml(subLabel)}</text>
</svg>`)
    : null;
  const composites = [
    { input: suitePhoto, ...suitePhotoPlacement },
    ...(labelOverlay ? [{ input: labelOverlay, top: 0, left: 0 }] : [])
  ];

  return {
    buffer: await sharp(shell)
      .composite(composites)
      .png()
      .toBuffer(),
    width: cardWidth,
    height: cardHeight,
    top,
    left,
    family: isBusiness ? "suite-proof-top-card" : isExperience ? "event-suite-handoff-card" : decision.name
  };
}

function buildDeterministicChecks({
  copy,
  decision,
  eventReview,
  height,
  logoMeta,
  logoPlacement,
  sourceLogoExists,
  sourceHeroPhoto,
  sourceSuitePhoto,
  suiteModule,
  template,
  width
}) {
  const checks = [];
  const add = (id, passed, severity, detail) => {
    checks.push({ id, passed, severity, detail });
  };

  add(
    "dimensions",
    width === 1080 && height === 1350,
    "critical",
    `Final export is ${width}x${height}; expected 1080x1350.`
  );
  add(
    "logo-present",
    sourceLogoExists === true,
    "critical",
    "Bonhomia logo source asset must be present and deterministic."
  );

  if (decision.usesHeroPhoto) {
    add(
      "hero-photo-present",
      Boolean(sourceHeroPhoto?.path),
      "critical",
      "Photo-led creative must use a real repo hero photo as the main canvas."
    );
    add(
      "hero-photo-kind-safe",
      ["property", "suite"].includes(sourceHeroPhoto?.kind),
      "warning",
      `Hero photo kind is ${sourceHeroPhoto?.kind || "unknown"}; expected property or suite.`
    );
  }

  if (sourceLogoExists && logoMeta) {
    const logoBox = {
      left: logoPlacement.logoLeft,
      top: logoPlacement.logoTop,
      width: logoMeta.width || 0,
      height: logoMeta.height || 0
    };
    add(
      "logo-safe-area",
      isBoxInside(logoBox, width, height, 32),
      "critical",
      `Logo box ${formatBox(logoBox)} must stay inside the 32px safe area.`
    );
    add(
      "logo-readable-size",
      logoBox.width >= 110 && logoBox.height >= 90,
      "warning",
      `Logo rendered at ${logoBox.width}x${logoBox.height}; expected at least 110x90.`
    );
  }

  if (decision.usesSuite) {
    add(
      "suite-module-required",
      Boolean(sourceSuitePhoto && suiteModule),
      "critical",
      "A suite-proof creative must include a real locked suite-photo module."
    );

    if (suiteModule) {
      const suiteBox = {
        left: suiteModule.left,
        top: suiteModule.top,
        width: suiteModule.width,
        height: suiteModule.height
      };
      add(
        "suite-module-bounds",
        isBoxInside(suiteBox, width, height, 42),
        "critical",
        `Suite module ${formatBox(suiteBox)} must stay inside the 42px safe area.`
      );
      add(
        "suite-module-scale",
        suiteModule.width >= 840 && suiteModule.height >= 280,
        "warning",
        "Suite proof module should be large enough to read as intentional lodging proof."
      );
    }
  }

  for (const check of buildTextFitChecks(template, decision, copy, width, height)) {
    checks.push(check);
  }

  const contrastChecks = [
    {
      id: "headline-contrast",
      foreground: template.palette.ink,
      background: template.palette.dark,
      minimum: 3
    },
    {
      id: "footer-contrast",
      foreground: template.palette.dark,
      background: template.palette.cream,
      minimum: 4.5
    },
    {
      id: "cta-contrast",
      foreground: template.id === "experience_event_hook" ? "#1f1a16" : template.palette.cream,
      background: template.palette.accent,
      minimum: 3
    }
  ];

  for (const check of contrastChecks) {
    const ratio = contrastRatio(check.foreground, check.background);
    add(
      check.id,
      ratio >= check.minimum,
      ratio >= check.minimum ? "info" : "warning",
      `${check.foreground} on ${check.background} contrast is ${ratio.toFixed(2)}; minimum ${check.minimum}.`
    );
  }

  add(
    "no-internal-dates",
    !/\d{4}-\d{2}-\d{2}/u.test(`${copy.headline} ${copy.subhead} ${copy.cta}`),
    "critical",
    "Public image copy must not include internal ISO date text."
  );

  add(
    "public-event-claim-safe",
    !(eventReview.mentionsEvent && !eventReview.verified && copyContainsSpecificEvent(copy)),
    "critical",
    "Specific event claims must be verified before appearing in final image copy."
  );

  const criticalFailures = checks
    .filter((check) => !check.passed && check.severity === "critical")
    .map((check) => check.detail);
  const warnings = checks
    .filter((check) => !check.passed && check.severity !== "critical")
    .map((check) => check.detail);

  return {
    passed: criticalFailures.length === 0,
    checks,
    criticalFailures,
    warnings
  };
}

function buildTextFitChecks(template, decision, copy, width, height) {
  const checks = [];
  const add = (id, passed, severity, detail) => {
    checks.push({ id, passed, severity, detail });
  };
  const headline = splitHeadline(copy.headline, decision.usesHeroPhoto
    ? decision.heroPhotoKind === "suite" ? 13 : 12
    : template.id === "experience_event_hook" ? 14 : 12);
  const spec = getTextLayoutSpec(template, decision, width, height);

  add(
    "headline-line-count",
    headline.length <= spec.maxHeadlineLines,
    "critical",
    `Headline splits to ${headline.length} lines; maximum ${spec.maxHeadlineLines}.`
  );

  for (const line of headline) {
    const estimated = estimateTextWidth(line, spec.headlineFontSize, 0.58);
    add(
      "headline-fit",
      estimated <= spec.headlineMaxWidth,
      "critical",
      `Headline line '${line}' estimates ${Math.round(estimated)}px; max ${spec.headlineMaxWidth}px.`
    );
  }

  add(
    "subhead-fit",
    estimateTextWidth(copy.subhead, spec.subheadFontSize, 0.54) <= spec.subheadMaxWidth,
    "warning",
    `Subhead estimates ${Math.round(estimateTextWidth(copy.subhead, spec.subheadFontSize, 0.54))}px; max ${spec.subheadMaxWidth}px.`
  );
  add(
    "cta-fit",
    estimateTextWidth(copy.cta, spec.ctaFontSize, 0.56) <= spec.ctaMaxWidth,
    "critical",
    `CTA estimates ${Math.round(estimateTextWidth(copy.cta, spec.ctaFontSize, 0.56))}px; max ${spec.ctaMaxWidth}px.`
  );
  add(
    "text-safe-area",
    spec.textLeft >= 48 && spec.textRight <= width - 48,
    "critical",
    `Primary text area from ${spec.textLeft}px to ${spec.textRight}px must stay in safe area.`
  );

  return checks;
}

function getTextLayoutSpec(template, decision, width, height) {
  if (decision.usesHeroPhoto) {
    return {
      headlineFontSize: decision.heroPhotoKind === "suite" ? 78 : 92,
      headlineMaxWidth: 720,
      maxHeadlineLines: 3,
      subheadFontSize: 37,
      subheadMaxWidth: 720,
      ctaFontSize: 33,
      ctaMaxWidth: 500,
      textLeft: 72,
      textRight: 792
    };
  }

  if (template.id === "destination_brand_awareness") {
    return {
      headlineFontSize: 82,
      headlineMaxWidth: 630,
      maxHeadlineLines: 3,
      subheadFontSize: 40,
      subheadMaxWidth: 650,
      ctaFontSize: 22,
      ctaMaxWidth: 220,
      textLeft: 72,
      textRight: 720
    };
  }

  if (template.id === "experience_event_hook") {
    return {
      headlineFontSize: 88,
      headlineMaxWidth: width - 140,
      maxHeadlineLines: 3,
      subheadFontSize: 34,
      subheadMaxWidth: width - 140,
      ctaFontSize: 33,
      ctaMaxWidth: width - 180,
      textLeft: 70,
      textRight: width - 70
    };
  }

  if (decision.usesSuite) {
    return {
      headlineFontSize: 80,
      headlineMaxWidth: width - 144,
      maxHeadlineLines: 3,
      subheadFontSize: 34,
      subheadMaxWidth: width - 144,
      ctaFontSize: 34,
      ctaMaxWidth: width - 220,
      textLeft: 72,
      textRight: width - 72
    };
  }

  return {
    headlineFontSize: 96,
    headlineMaxWidth: 665,
    maxHeadlineLines: 3,
    subheadFontSize: 37,
    subheadMaxWidth: 700,
    ctaFontSize: 35,
    ctaMaxWidth: 560,
    textLeft: 72,
    textRight: 742
  };
}

function estimateTextWidth(value, fontSize, multiplier) {
  return String(value || "").length * fontSize * multiplier;
}

function isBoxInside(box, width, height, margin) {
  return box.left >= margin
    && box.top >= margin
    && box.left + box.width <= width - margin
    && box.top + box.height <= height - margin;
}

function formatBox(box) {
  return `${box.left},${box.top},${box.width}x${box.height}`;
}

async function reviewCandidate({
  asset,
  candidateIndex,
  candidatePath,
  config,
  copy,
  decision,
  eventReview,
  deterministicChecks,
  sourceHeroPhoto,
  sourceSuitePhoto,
  template,
  learningContext,
  width,
  height
}) {
  if (config.mockReview) {
    return {
      ...buildMockReview(candidateIndex, config),
      deterministicChecks,
      reviewerMode: "mock"
    };
  }

  const heuristic = buildHeuristicReview({
    asset,
    copy,
    decision,
    deterministicChecks,
    eventReview,
    sourceHeroPhoto,
    sourceSuitePhoto,
    template,
    width,
    height,
    minimumScore: config.minimumScore
  });

  if (config.mock) {
    return {
      ...heuristic,
      deterministicChecks,
      reviewerMode: "heuristic-mock-image"
    };
  }

  if (!process.env.OPENAI_API_KEY || !config.reviewModel) {
    if (!config.allowHeuristicFinal) {
      throw new Error("OpenAI vision review is required for non-mock final media. Set MARKETING_ALLOW_HEURISTIC_FINAL=1 only for an emergency offline override.");
    }

    return {
      ...heuristic,
      deterministicChecks,
      reviewer: "heuristic-override",
      reviewerMode: "heuristic-override",
      issues: [
        ...heuristic.issues,
        "Accepted by explicit MARKETING_ALLOW_HEURISTIC_FINAL override; human review required before publishing."
      ]
    };
  }

  try {
    const vision = await reviewCandidateWithOpenAi({
      candidatePath,
      config,
      copy,
      decision,
      eventReview,
      asset,
      learningContext,
      sourceHeroPhoto,
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
      reviewerMode: "openai-vision",
      deterministicChecks,
      criticalFailures,
      issues,
      strengths: uniqueValues([...heuristic.strengths, ...vision.strengths])
    };
  } catch (error) {
    if (!config.allowHeuristicFinal) {
      throw error;
    }

    return {
      ...heuristic,
      deterministicChecks,
      reviewer: "heuristic-override",
      reviewerMode: "heuristic-override",
      issues: [
        ...heuristic.issues,
        `OpenAI vision review unavailable: ${error.message}`,
        "Accepted by explicit MARKETING_ALLOW_HEURISTIC_FINAL override; human review required before publishing."
      ]
    };
  }
}

async function reviewCandidateWithOpenAi({ candidatePath, config, copy, decision, eventReview, asset, learningContext, sourceHeroPhoto, template }) {
  const client = new OpenAI({ apiKey: process.env.OPENAI_API_KEY });
  const base64 = await fs.readFile(candidatePath, { encoding: "base64" });
  const learningRules = formatLearningRules(learningContext, 10);
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
- Asset decision: ${decision.name} (${decision.usesHeroPhoto ? `photo-led poster using a real ${sourceHeroPhoto?.kind || "repo"} photo` : decision.name === "logo_only" ? "no suite photo; brand/destination poster is allowed" : "see factual suite policy"})
- Audience: ${asset.audience || "not specified"}
- Headline: ${copy.headline}
- Subhead: ${copy.subhead}
- CTA: ${copy.cta}
- Event claim verified: ${eventReview.verified}
- Suite photo policy: real repo photos and logos are factual assets; generated/reference visuals must not imply false suite/property features or alter visible room/property facts.
- Photo mode: ${config.photoMode}
- Audience policy: use the asset audience and explicit user campaign as the primary audience. Bonhomia's default business-traveler priority is a planning default, not a critical rejection reason when this run explicitly requests event, family, tourist, or gastronomy creative.
- Accepted Bonhomia rules:
${learningRules}

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

function buildHeuristicReview({ copy, decision, deterministicChecks, eventReview, sourceHeroPhoto, sourceSuitePhoto, template, width, height, minimumScore }) {
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

  if (decision.usesHeroPhoto) {
    if (!sourceHeroPhoto) {
      criticalFailures.push("Photo-led strategy requires a real repo hero photo.");
      score -= 35;
    } else {
      strengths.push("Uses a real Bonhomia repo photo as the primary promotional canvas.");
    }
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

  if (deterministicChecks) {
    criticalFailures.push(...deterministicChecks.criticalFailures);
    issues.push(...deterministicChecks.warnings);
    score -= deterministicChecks.criticalFailures.length * 18;
    score -= deterministicChecks.warnings.length * 4;
    if (deterministicChecks.passed) {
      strengths.push("Passed deterministic dimensions, safe-area, contrast, and public-claim checks.");
    }
  }

  const accepted = score >= minimumScore && criticalFailures.length === 0;
  return {
    score,
    accepted,
    reviewer: "heuristic",
    reviewerMode: "heuristic",
    deterministicChecks,
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
  const intent = classifyCreativeIntent(asset);
  const {
    explicitlyLogoOnly,
    explicitlyNoPhoto,
    explicitlyNoSuitePhoto,
    hasBusiness,
    hasDirectBookingIntent,
    hasEvent,
    hasLodgingPush,
    hasSuiteSpecificIntent,
    namedSuite
  } = intent;

  if ((explicitlyLogoOnly || explicitlyNoPhoto) && !namedSuite) {
    return {
      name: "logo_only",
      usesSuite: false,
      usesHeroPhoto: false,
      reviewNotes: ["Logo-only treatment selected because the asset direction explicitly avoids photography."]
    };
  }

  if (explicitlyNoSuitePhoto && !namedSuite) {
    return hasBusiness
      ? {
          name: "photo_led_poster",
          usesSuite: false,
          usesHeroPhoto: true,
          heroPhotoKind: "property",
          reviewNotes: ["Business direct-booking creative avoids forced room photography and uses a real property photo poster instead."]
        }
      : {
          name: "logo_only",
          usesSuite: false,
          usesHeroPhoto: false,
          reviewNotes: ["Logo-only treatment selected because the asset direction explicitly avoids suite photography."]
        };
  }

  if (hasBusiness && !namedSuite && !hasEvent) {
    return {
      name: "photo_led_poster",
      usesSuite: false,
      usesHeroPhoto: true,
      heroPhotoKind: "property",
      reviewNotes: ["Business direct-booking creative uses a real property photo poster because no named business-specific suite photo was requested."]
    };
  }

  if (hasEvent && (hasSuiteSpecificIntent || namedSuite)) {
    return {
      name: "logo_and_suite_card",
      usesSuite: true,
      usesHeroPhoto: false,
      reviewNotes: ["Generated event/campaign visual is separated from the real suite-photo lodging module."]
    };
  }

  if (namedSuite || (hasSuiteSpecificIntent && !hasBusiness)) {
    return {
      name: "photo_led_poster",
      usesSuite: false,
      usesHeroPhoto: true,
      heroPhotoKind: "suite",
      reviewNotes: ["Real suite photo is used as the full-bleed factual poster canvas."]
    };
  }

  if (hasBusiness || hasDirectBookingIntent) {
    return {
      name: "photo_led_poster",
      usesSuite: false,
      usesHeroPhoto: true,
      heroPhotoKind: "property",
      reviewNotes: [hasBusiness
        ? "Business/direct-booking creative uses a real property photo instead of forcing room imagery."
        : "Direct-booking CTA is treated as destination/brand creative with a real Bonhomia property photo."]
    };
  }

  if (hasLodgingPush) {
    return {
      name: "photo_led_poster",
      usesSuite: false,
      usesHeroPhoto: true,
      heroPhotoKind: "property",
      reviewNotes: ["Generic lodging creative uses a real Bonhomia property photo unless a specific suite is named."]
    };
  }

  return {
    name: "photo_led_poster",
    usesSuite: false,
    usesHeroPhoto: true,
    heroPhotoKind: "property",
    reviewNotes: ["Destination/brand creative uses a real Bonhomia property photo as the main promotional canvas."]
  };
}

function selectEditorialTemplate(asset, decision) {
  const intent = classifyCreativeIntent(asset);

  if (intent.hasEvent) {
    return templateDefinitions.experience_event_hook;
  }

  if (intent.hasBusiness) {
    return templateDefinitions.business_direct_booking;
  }

  if (!decision.usesSuite) {
    return templateDefinitions.destination_brand_awareness;
  }

  return templateDefinitions.business_direct_booking;
}

function getCreativeFamily(template, decision) {
  if (decision.usesHeroPhoto) {
    return decision.heroPhotoKind === "suite"
      ? "photo_led_suite_poster"
      : "photo_led_property_poster";
  }

  if (template.id === "business_direct_booking" && decision.usesSuite) {
    return "business_suite_proof";
  }

  if (template.id === "business_direct_booking") {
    return "business_brand_rail";
  }

  if (template.id === "experience_event_hook" && decision.usesSuite) {
    return "event_suite_handoff";
  }

  if (template.id === "experience_event_hook") {
    return "event_local_poster";
  }

  return "destination_brand_lockup";
}

function classifyCreativeIntent(asset) {
  const strategyText = normalizeForDecision([
    asset.hook,
    asset.concept,
    asset.visualDirection,
    asset.caption,
    asset.audience
  ].filter(Boolean).join(" "));
  const fullText = normalizeForDecision([
    strategyText,
    asset.cta
  ].filter(Boolean).join(" "));
  const roomSpecificText = fullText.replace(/\bbonhomia suites\b/gu, "bonhomia");

  return {
    namedSuite: [...suiteNameSlugs.keys()].find((name) => fullText.includes(name)),
    explicitlyLogoOnly: /\b(logo only|solo logo|logo solo)\b/u.test(fullText),
    explicitlyNoPhoto: /\b(sin foto|no photo|without photo|sin imagen|no image)\b/u.test(fullText),
    explicitlyNoSuitePhoto: /\b(sin suite|no suite photo|no room photo|no forced room|sin foto de suite)\b/u.test(fullText),
    hasEvent: /\b(event|evento|feria|festival|arte|art|luciernaga|luciernagas|show|experiencia|temporada)\b/u.test(strategyText),
    hasBusiness: /\b(trabajo|business|empresa|empresas|corporativo|compania|companias|viajas por trabajo|viajero de negocio)\b/u.test(strategyText),
    hasDirectBookingIntent: /\b(direct-booking|direct booking|reserva directa|reservar directo|booking directo)\b/u.test(strategyText),
    hasSuiteSpecificIntent: /\b(real suite photo|suite photo|foto de suite|foto de habitacion|habitacion|habitaciones|room photo|room photos|penthouse|berlin|grecia|london|manhattan|moscu|paris|seul)\b/u.test(roomSpecificText),
    hasLodgingPush: /\b(hosped|hospedate|hospedaje|stay|lodging|duerme|dormir|descansa|descanso|estancia|quedate|comodidad|comodo|room|habitacion)\b/u.test(strategyText)
  };
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

function buildEditorialCopy(asset, template, sourcePhoto, eventReview, decision = {}) {
  const cta = sanitizeCta(asset.cta || "Reserva directo");
  if (decision.usesHeroPhoto) {
    return buildPhotoLedCopy(asset, template, sourcePhoto, eventReview, decision, cta);
  }

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
      suiteSubhead: sourcePhoto ? "Hospedaje práctico" : "Reserva directa"
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

function buildPhotoLedCopy(asset, template, sourcePhoto, eventReview, decision, cta) {
  const intent = classifyCreativeIntent(asset);
  if (template.id === "experience_event_hook") {
    const isFoodHook = classifyCreativeIntent(asset)
      && /\b(pulque|barbacoa|maguey|gastronomia|gastronómica|sabor)\b/u.test(normalizeForDecision([asset.hook, asset.concept, asset.caption].filter(Boolean).join(" ")));
    return {
      eyebrow: eventReview.verified ? "EXPERIENCIA LOCAL" : "PLAN LOCAL",
      headline: eventReview.verified ? headlineFromEvent(asset) : "PLAN LOCAL",
      subhead: isFoodHook ? "Después de la feria, descansa aquí." : "Descansa cerca de la feria.",
      cta,
      suiteSubhead: "Ideal para quedarte cerca",
      benefits: ["DESCANSO", "UBICACIÓN", "RESERVA"],
      location: "Calpulalpan, Tlaxcala"
    };
  }

  if (intent.hasBusiness) {
    return {
      eyebrow: "BONHOMIA SUITES",
      headline: "VIAJE DE TRABAJO",
      subhead: "Llega, descansa y reserva directo.",
      cta,
      suiteSubhead: sourcePhoto?.suiteName ? `Suite ${titleCase(sourcePhoto.suiteName)}` : "Hospedaje práctico",
      benefits: ["DESCANSO", "UBICACIÓN", "RESERVA"],
      location: "Calpulalpan, Tlaxcala"
    };
  }

  if (decision.heroPhotoKind === "suite") {
    return {
      eyebrow: "BONHOMIA SUITES",
      headline: "TU ESTANCIA TE ESPERA",
      subhead: "Reserva directamente.",
      cta,
      suiteSubhead: sourcePhoto?.suiteName ? `Suite ${titleCase(sourcePhoto.suiteName)}` : "Hospedaje recomendado",
      benefits: ["DESCANSO", "CONFORT", "RESERVA"],
      location: "Calpulalpan, Tlaxcala"
    };
  }

  return {
    eyebrow: "BONHOMIA SUITES",
    headline: "RELÁJATE, YA ESTÁS EN CASA",
    subhead: "Reserva directamente.",
    cta,
    suiteSubhead: "Hospedaje recomendado",
    benefits: ["DESCANSO", "UBICACIÓN", "RESERVA"],
    location: "Calpulalpan, Tlaxcala"
  };
}

function headlineFromEvent(asset) {
  const text = normalizeText([asset.hook, asset.concept, asset.caption].filter(Boolean).join(" "));
  if (text.includes("pulque") || text.includes("barbacoa") || text.includes("maguey") || text.includes("gastronomia")) {
    return "SABOR TLAXCALTECA";
  }

  if (text.includes("san antonio")) {
    return "FERIA DE SAN ANTONIO";
  }

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

function buildPhotoLedImagePrompt(asset, decision, eventReview, template, copy, learningContext, sourceHeroPhoto) {
  const learningRules = formatLearningRules(learningContext, 10);
  const sourceKind = sourceHeroPhoto?.kind === "suite" ? "suite interior/exterior" : "property/exterior";
  const claimGuidance = eventReview.verified
    ? "If reference-edit mode is enabled, the edit may support the verified local hook with subtle atmosphere only; no readable event details."
    : "Do not add specific event details, dates, venue names, or factual claims.";

  return [
    "Create a premium vertical Bonhomia Suites promotional poster using the provided real Bonhomia photo as the factual hero canvas.",
    `Template: ${template.id}.`,
    `Creative family: ${getCreativeFamily(template, decision)}.`,
    `Source photo role: ${sourceKind}.`,
    `Headline intent: ${copy.headline}.`,
    `Campaign concept: ${asset.concept || asset.hook || "warm, practical boutique lodging marketing"}.`,
    learningRules,
    claimGuidance,
    "Preserve the real source photo's visible property facts. Do not invent furniture, amenities, views, decor, objects, buildings, balconies, signs, or room layouts.",
    "Create only premium lighting/color/composition support around the factual source photo; do not create the final text, logo, CTA button, icons, or readable typography.",
    "Leave clean space for deterministic Bonhomia text/logo overlays in a refined editorial hospitality style.",
    "Avoid generic dark text-card layouts, stock ad templates, childish blocks, and fake Airbnb or third-party brand marks."
  ].join("\n");
}

function buildOpenAiImagePrompt(asset, decision, eventReview, template, copy, learningContext) {
  const claimGuidance = eventReview.verified
    ? "The campaign may visually suggest the verified event/experience, but do not render readable event details."
    : "Use generic local-culture/destination atmosphere only; do not include specific dates, venue names, or factual event claims.";
  const templateGuidance = template.id === "business_direct_booking"
    ? "Business creative direction: abstract direct-booking/business-travel signals only, such as route rhythm, check-in flow, calendar geometry, refined paper texture, and premium architectural spacing. No office, desk, meeting room, hotel room, or person."
    : template.id === "experience_event_hook"
      ? "Experience creative direction: make the local hook the hero through abstract nature/event atmosphere, with room left for a factual suite module below."
      : "Destination creative direction: brand-led awareness with strong graphic composition and calm premium negative space.";
  const learningRules = formatLearningRules(learningContext, 10);

  return [
    "Create a premium vertical editorial poster background for Bonhomia Suites in Calpulalpan.",
    `Template: ${template.id}.`,
    `Creative family: ${getCreativeFamily(template, decision)}.`,
    `Headline intent: ${copy.headline}.`,
    `Campaign concept: ${asset.concept || asset.hook || "warm, practical, premium lodging marketing"}.`,
    learningRules,
    claimGuidance,
    templateGuidance,
    "Use bold graphic composition, intentional negative space, high-contrast palette, subtle Mexican highland atmosphere, and editorial poster energy.",
    "Do not copy Airbnb, Airbnb logos, exact reference layouts, or any brand marks.",
    "Important: create an abstract/editorial campaign poster layer, not a realistic property photo.",
    "Do not create rooms, beds, furniture, windows, balconies, terraces, buildings, churches, hotel exteriors, interior design, amenities, signs, logos, readable text, people, or anything that could be mistaken for a Bonhomia suite or property feature.",
    "Do not create diagonal stripe overlays, watermark-like patterns, stock mockup texture, placeholder bars, or draft-layout artifacts.",
    "Leave clean negative space for deterministic headline, CTA, logo, and optional real-suite photo module.",
    decision.usesSuite
      ? "The real suite photo will be added separately by the composer; do not depict any suite interior."
      : "The Bonhomia logo will be added separately by the composer; do not draw or imitate the logo.",
    "Style: bold editorial travel poster, premium boutique hospitality, confident geometry, no clutter, no generic wallpaper."
  ].join("\n");
}

function buildCandidatePrompt(prompt, candidateIndex) {
  const variations = [
    "Variation: asymmetrical arc composition with a large calm negative-space area.",
    "Variation: bold circular crop energy, deep color contrast, and clean editorial rhythm.",
    "Variation: minimal geometric poster with premium texture and strong focal contrast.",
    "Variation: asymmetric Swiss-inspired grid, warm premium palette, and one clear focal device.",
    "Variation: editorial travel-poster texture with confident empty space and integrated brand rail.",
    "Variation: refined collage-like abstract shapes, high contrast, and clean room for deterministic type."
  ];
  return `${prompt}\n${variations[(candidateIndex - 1) % variations.length]}`;
}

async function buildBonhomiaAssetCatalog(brand) {
  const suiteRoot = repoPath(brand.assets?.suiteImageRoot || "src/OrionERP.Bonhomia.Web/wwwroot/Images/Bonhomia/suites");
  const propertyRoot = repoPath(brand.assets?.propertyImageRoot || "src/OrionERP.Bonhomia.Web/wwwroot/Images/Bonhomia");
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

  const propertyPhotos = await collectPropertyPhotos(brand, propertyRoot);

  return {
    suiteRoot,
    propertyRoot,
    logoPath,
    editorialSuitePhotos,
    propertyPhotos,
    suites
  };
}

async function collectPropertyPhotos(brand, propertyRoot) {
  const explicit = uniqueValues([
    brand.assets?.repoImages?.landing,
    brand.assets?.repoImages?.hero,
    brand.assets?.repoImages?.services
  ].filter(Boolean))
    .map((relativePath) => repoPath(relativePath));
  const discovered = [];
  const directories = [
    propertyRoot,
    path.join(propertyRoot, "building")
  ];

  for (const directory of directories) {
    if (!(await fileExists(directory))) {
      continue;
    }

    const entries = await fs.readdir(directory, { withFileTypes: true });
    for (const entry of entries) {
      if (!entry.isFile() || !/\.(jpe?g|png|webp)$/iu.test(entry.name)) {
        continue;
      }

      const fullPath = path.join(directory, entry.name);
      if (isApprovedPropertyPhoto(fullPath)) {
        discovered.push(fullPath);
      }
    }
  }

  return uniqueValues([...explicit, ...discovered])
    .filter((filePath) => isApprovedPropertyPhoto(filePath))
    .map((filePath) => ({
      kind: "property",
      path: filePath,
      score: scorePropertyPhotoName(filePath)
    }))
    .sort((a, b) => b.score - a.score || a.path.localeCompare(b.path));
}

function isApprovedPropertyPhoto(filePath) {
  const name = normalizeForDecision(path.basename(filePath));
  return /\.(jpe?g|png|webp)$/iu.test(filePath)
    && !/\b(catalog|floorplan|planta|plano|render|logo|letterhead)\b/u.test(name)
    && !/\b(gallery-lounge|gallery-kitchen)\b/u.test(name);
}

function scorePropertyPhotoName(filePath) {
  const name = normalizeForDecision(toForwardSlash(filePath));
  let score = 0;
  if (name.includes("hero")) score += 80;
  if (name.includes("terrace") || name.includes("lounge")) score += 72;
  if (name.includes("kitchen")) score += 52;
  if (name.includes("exterior-main")) score += 44;
  if (name.includes("building")) score += 34;
  if (name.includes("exterior-vertical")) score -= 20;
  if (name.includes("exterior") && !name.includes("exterior-vertical")) score += 28;
  if (name.includes("wine")) score += 8;
  return score;
}

function selectHeroPhoto(asset, catalog, intelligence, decision) {
  if (decision.heroPhotoKind === "suite") {
    const suitePhoto = selectSuitePhoto(asset, catalog, intelligence);
    return suitePhoto
      ? {
          ...suitePhoto,
          kind: "suite"
        }
      : null;
  }

  const text = normalizeForDecision([asset.hook, asset.concept, asset.caption, asset.visualDirection].filter(Boolean).join(" "));
  const prefersInterior = /\b(interior|cocina|kitchen|lounge|terraza|terrace)\b/u.test(text);
  const requiresExterior = /\b(exterior only|fachada|foto exterior|exterior photo)\b/u.test(text);
  const propertyPhoto = requiresExterior
    ? catalog.propertyPhotos.find((photo) => /exterior-main|building/u.test(normalizeForDecision(photo.path)))
      || catalog.propertyPhotos[0]
    : /\b(cocina|kitchen|gastronomia|gastronómica|sabor|pulque|barbacoa)\b/u.test(text)
      ? catalog.propertyPhotos.find((photo) => /kitchen/u.test(normalizeForDecision(photo.path)))
        || catalog.propertyPhotos.find((photo) => /gallery|hero|terrace|lounge/u.test(normalizeForDecision(photo.path)))
        || catalog.propertyPhotos[0]
    : /\b(terraza|terrace|living|lounge)\b/u.test(text)
      ? catalog.propertyPhotos.find((photo) => /terrace|lounge/u.test(normalizeForDecision(photo.path)))
        || catalog.propertyPhotos.find((photo) => /gallery|hero|kitchen/u.test(normalizeForDecision(photo.path)))
        || catalog.propertyPhotos[0]
    : prefersInterior
    ? catalog.propertyPhotos.find((photo) => /gallery|hero|kitchen|terrace|lounge/u.test(normalizeForDecision(photo.path)))
      || catalog.propertyPhotos[0]
    : catalog.propertyPhotos[0];

  if (propertyPhoto) {
    return propertyPhoto;
  }

  const suitePhoto = selectSuitePhoto(asset, catalog, intelligence);
  return suitePhoto
    ? {
        ...suitePhoto,
        kind: "suite"
      }
    : null;
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

async function loadLearningContext() {
  const playbook = await readTextIfExists(repoPath(playbookPath));
  const designSystem = await readTextIfExists(repoPath(visualDesignSystemPath));
  return {
    playbookHash: hashText(playbook),
    designSystemHash: hashText(designSystem),
    playbookRules: extractRelevantRules(playbook, [
      "image",
      "suite",
      "logo",
      "candidate",
      "event",
      "claim",
      "copy",
      "quality",
      "text"
    ]),
    designRules: extractRelevantRules(designSystem, [
      "poster",
      "hierarchy",
      "negative space",
      "logo",
      "suite",
      "candidate",
      "quality",
      "text",
      "event",
      "mobile"
    ])
  };
}

async function readTextIfExists(filePath) {
  if (!(await fileExists(filePath))) {
    return "";
  }

  return fs.readFile(filePath, "utf8");
}

function hashText(value) {
  return crypto
    .createHash("sha256")
    .update(String(value || ""), "utf8")
    .digest("hex")
    .slice(0, 16);
}

function extractRelevantRules(markdown, keywords) {
  const normalizedKeywords = keywords.map((keyword) => normalizeText(keyword));
  return uniqueValues(String(markdown || "")
    .split(/\r?\n/u)
    .map((line) => line.trim())
    .filter((line) => line.startsWith("- "))
    .map((line) => line.replace(/^- /u, "").trim())
    .filter((line) => {
      const normalized = normalizeText(line);
      return normalizedKeywords.some((keyword) => normalized.includes(keyword));
    }))
    .slice(0, 18);
}

function formatLearningRules(learningContext, limit) {
  const rules = uniqueValues([
    ...(learningContext?.playbookRules || []),
    ...(learningContext?.designRules || [])
  ]).slice(0, limit);

  if (rules.length === 0) {
    return "- Follow the Bonhomia visual design system: one idea, short copy, strong hierarchy, integrated logo, factual suite modules.";
  }

  return rules
    .map((rule) => `- ${rule.slice(0, 220)}`)
    .join("\n");
}

function buildAssetLearningEvidence(learningContext) {
  return {
    playbookHash: learningContext?.playbookHash || null,
    designSystemHash: learningContext?.designSystemHash || null,
    acceptedRulesUsed: (learningContext?.playbookRules?.length || 0)
      + (learningContext?.designRules?.length || 0)
  };
}

async function writeLessonInboxProposal(manifest) {
  const relativePath = path.join(
    "knowledge",
    "lesson-inbox",
    safeFileName(manifest.brand?.id || "brand") === "bonhomia"
      ? defaultLessonInboxFile
      : `${safeFileName(manifest.brand?.id || "brand")}-media-generation-quality.md`
  );
  const fullPath = path.join(toolRoot, relativePath);
  const content = renderLessonInboxProposal(manifest);
  const existed = await fileExists(fullPath);
  const current = existed ? await fs.readFile(fullPath, "utf8") : null;

  if (current === content) {
    return {
      relativePath: repoRelative(fullPath),
      status: "unchanged"
    };
  }

  await writeText(fullPath, content);
  return {
    relativePath: repoRelative(fullPath),
    status: existed ? "updated" : "written"
  };
}

function resolveImageConfig(brand, args, options) {
  const provider = brand.providers?.image?.openai || {};
  const review = brand.providers?.image?.review || {};
  const output = brand.providers?.image?.output || {};
  const backgroundSize = process.env.MARKETING_IMAGE_BACKGROUND_SIZE || provider.backgroundSize || "1280x1600";
  validateImageSize(backgroundSize);
  return {
    model: process.env.MARKETING_IMAGE_MODEL || provider.model || "gpt-image-2",
    fallbackModel: process.env.MARKETING_IMAGE_FALLBACK_MODEL || provider.fallbackModel || "gpt-image-1",
    quality: process.env.MARKETING_IMAGE_QUALITY || provider.quality || "high",
    photoMode: normalizePhotoMode(getArgValue("--photo-mode", args)
      || (hasFlag("--reference-edit", args) ? "reference-edit" : null)
      || process.env.MARKETING_IMAGE_PHOTO_MODE
      || provider.photoMode
      || "deterministic"),
    backgroundSize,
    reviewModel: process.env.MARKETING_REVIEW_MODEL || review.model || "gpt-5-mini",
    minimumScore: Number(firstEnv("MARKETING_REVIEW_MIN_SCORE", "MARKETING_MIN_IMAGE_SCORE") || review.minimumScore || 82),
    maxAttempts: Number(firstEnv("MARKETING_REVIEW_MAX_ATTEMPTS", "MARKETING_IMAGE_MAX_ATTEMPTS") || review.maxAttempts || 6),
    candidatesPerAsset: Number(firstEnv("MARKETING_REVIEW_CANDIDATES", "MARKETING_IMAGE_CANDIDATES") || review.candidatesPerAsset || 4),
    strictReview: firstEnv("MARKETING_REVIEW_STRICT", "MARKETING_IMAGE_STRICT_REVIEW")
      ? firstEnv("MARKETING_REVIEW_STRICT", "MARKETING_IMAGE_STRICT_REVIEW") !== "0"
      : review.strict !== false,
    fallbackToHeuristicWhenUnavailable: review.fallbackToHeuristicWhenUnavailable !== false,
    allowHeuristicFinal: options.allowHeuristicFinal
      || hasFlag("--allow-heuristic-final", args)
      || process.env.MARKETING_ALLOW_HEURISTIC_FINAL === "1",
    width: Number(process.env.MARKETING_IMAGE_WIDTH || output.width || 1080),
    height: Number(process.env.MARKETING_IMAGE_HEIGHT || output.height || 1350),
    mock: options.mock || hasFlag("--mock-openai", args) || process.env.MARKETING_IMAGE_MOCK === "1",
    mockReview: options.mockReview || hasFlag("--mock-review", args) || process.env.MARKETING_REVIEW_MOCK === "1"
  };
}

function normalizePhotoMode(value) {
  const normalized = String(value || "deterministic")
    .trim()
    .toLowerCase()
    .replace(/_/gu, "-");
  if (["deterministic", "reference-edit"].includes(normalized)) {
    return normalized;
  }

  throw new Error(`Invalid MARKETING_IMAGE_PHOTO_MODE '${value}'. Use deterministic or reference-edit.`);
}

function validateImageSize(value) {
  if (value === "auto") {
    return;
  }

  const match = String(value || "").match(/^(\d+)x(\d+)$/u);
  if (!match) {
    throw new Error(`Invalid MARKETING_IMAGE_BACKGROUND_SIZE '${value}'. Use WIDTHxHEIGHT.`);
  }

  const width = Number(match[1]);
  const height = Number(match[2]);
  const longEdge = Math.max(width, height);
  const shortEdge = Math.min(width, height);
  const totalPixels = width * height;
  if (
    width % 16 !== 0
    || height % 16 !== 0
    || longEdge > 3840
    || longEdge / shortEdge > 3
    || totalPixels < 655360
    || totalPixels > 8294400
  ) {
    throw new Error(`Invalid GPT Image 2 size '${value}'. Edges must be multiples of 16, max edge 3840, ratio <= 3:1, and pixels between 655360 and 8294400.`);
  }
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
  const deterministicLines = manifest.assets
    .flatMap((asset) => (asset.quality?.candidates || [])
      .filter((candidate) => candidate.deterministicChecks && !candidate.deterministicChecks.passed)
      .map((candidate) => `- ${asset.id} candidate ${candidate.candidateIndex}: ${candidate.deterministicChecks.criticalFailures.concat(candidate.deterministicChecks.warnings).join("; ")}`));

  return `# Media Generation Report

Brand: ${manifest.brand.name}
Week: ${manifest.week.id}
Provider: ${manifest.provider.image}
Quality target: ${manifest.qualityGate.target}
Lesson inbox: ${manifest.learning?.lessonInboxPath || "n/a"} (${manifest.learning?.lessonInboxStatus || "n/a"})

## Generated Images

${imageLines}

## Rejected Candidates

${rejectionLines.length > 0 ? rejectionLines.join("\n") : "- No candidates were rejected."}

## Deterministic Review Evidence

${deterministicLines.length > 0 ? deterministicLines.join("\n") : "- All reviewed candidates passed deterministic critical checks."}

## Unsupported V1 Assets

${unsupportedLines}

## Factual Suite Photo Policy

- Real Bonhomia photos are locked factual assets from the OrionERP Bonhomia repo.
- Default photo-led mode uses real photos as deterministic poster canvases with crop, color, contrast, and sharpen adjustments only.
- OpenAI abstract mode generates only campaign/background layers when a real-photo poster is not the right treatment.
- Reference-edit mode is opt-in; it sends a repo photo to OpenAI and must be rejected if visible property facts change.
- Bonhomia brand lockup/logo was composed as a deterministic layer; the checked-in logo source was verified before composition.

## Review Notes

${manifest.assets.flatMap((asset) => asset.reviewNotes || []).map((note) => `- ${note}`).join("\n") || "- No additional review notes."}
`;
}

function renderAssetReportLine(asset) {
  if (asset.status !== "generated") {
    return `- ${asset.id}: ${asset.status}, ${asset.template}/${asset.creativeFamily}, no final image written.`;
  }

  return [
    `- ${asset.id}: generated, ${asset.template}/${asset.creativeFamily}, score ${asset.quality?.score ?? "n/a"},`,
    `candidate ${asset.quality?.selectedCandidate ?? "n/a"},`,
    `reviewer ${asset.quality?.reviewer ?? "n/a"},`,
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
- Prefer real Bonhomia photo-led posters for normal promotional images when the repo has a strong factual source photo.
- Generate multiple candidates and keep only the best candidate that passes the quality gate.
- Reject generic text-card layouts, fake property visuals, clipped text, and disconnected logo badges.
- Do not force suite photos into generic business/direct-booking ads when the creative does not name a suite.
- Preserve unsupported TikTok video concepts instead of silently dropping them.

## Evidence From This Run

- Images processed: ${manifest.assets.length}
- Unsupported video assets: ${manifest.unsupported.length}
- Candidate decisions: ${manifest.assets.map((asset) => `${asset.id}=${asset.status}/${asset.template}/${asset.creativeFamily}/score:${asset.quality?.score ?? "n/a"}`).join(", ") || "none"}
- Playbook hash: ${manifest.learning?.playbook?.hash || "n/a"}
- Design system hash: ${manifest.learning?.designSystem?.hash || "n/a"}
- Lesson inbox: ${manifest.learning?.lessonInboxPath || "n/a"} (${manifest.learning?.lessonInboxStatus || "n/a"})

Review these lessons before promoting them into the playbook.
`;
}

function renderLessonInboxProposal(manifest) {
  return `# Lesson Proposal: Bonhomia Media Generation Quality

## Proposed Durable Lessons

- Keep final social images on the editorial-poster standard: one dominant idea, short public copy, strong hierarchy, and intentional negative space.
- Prefer deterministic photo-led promotional posters for normal Bonhomia sales images when a strong real repo photo exists; use generated abstract backgrounds only for concepts that need non-factual campaign art.
- Treat deterministic QA as part of creative quality: dimensions, safe areas, text fit, contrast, logo placement, suite-module bounds, and claim safety must pass before publishing.
- Use OpenAI for high-quality campaign/background layers or opt-in reference edits, but keep Bonhomia logo, public text, CTA, and publishable layout as locked composer layers.
- Prefer destination or brand-led layouts when the asset has only a generic reservation CTA; use business layouts only when the audience or concept is business-travel specific.
- Fail closed when OpenAI vision review is unavailable for production media unless an explicit emergency override is set and human review is planned.

## Review Status

Proposed. This file is intentionally stable so repeated media runs do not create duplicate lesson proposals. Run-specific evidence is stored in ignored media artifacts.
`;
}

function buildEditorialOverlaySvg(template, copy, decision, width, height) {
  if (decision.usesHeroPhoto) {
    return buildPhotoLedPosterOverlaySvg(template, copy, decision, width, height);
  }

  if (template.id === "experience_event_hook") {
    return buildExperienceOverlaySvg(template, copy, decision, width, height);
  }

  if (template.id === "destination_brand_awareness") {
    return buildDestinationOverlaySvg(template, copy, width, height);
  }

  return buildBusinessOverlaySvg(template, copy, decision, width, height);
}

function buildPhotoLedPosterOverlaySvg(template, copy, decision, width, height) {
  const headline = splitPhotoLedHeadline(copy.headline, decision);
  const textX = 72;
  const headlineY = decision.heroPhotoKind === "suite" ? 560 : 430;
  const lineGap = decision.heroPhotoKind === "suite" ? 88 : 96;
  const headlineSize = decision.heroPhotoKind === "suite" ? 76 : 88;
  const subheadY = headlineY + headline.length * lineGap + 42;
  const benefitLine = (copy.benefits || []).slice(0, 3).join("  |  ");
  const benefitY = subheadY + 64;
  const ctaY = benefitY + 92;
  const ctaWidth = ctaButtonWidth(copy.cta, width, textX, 520);
  return `
<svg width="${width}" height="${height}" xmlns="http://www.w3.org/2000/svg">
  <defs>
    <linearGradient id="photoLeftShade" x1="0" y1="0" x2="1" y2="0">
      <stop offset="0" stop-color="#071715" stop-opacity="0.72"/>
      <stop offset="0.42" stop-color="#071715" stop-opacity="0.34"/>
      <stop offset="0.76" stop-color="#071715" stop-opacity="0.08"/>
      <stop offset="1" stop-color="#071715" stop-opacity="0.02"/>
    </linearGradient>
    <linearGradient id="photoBottomShade" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stop-color="#071715" stop-opacity="0"/>
      <stop offset="0.52" stop-color="#071715" stop-opacity="0.34"/>
      <stop offset="1" stop-color="#071715" stop-opacity="0.78"/>
    </linearGradient>
    <linearGradient id="ctaGold" x1="0" y1="0" x2="1" y2="1">
      <stop offset="0" stop-color="#f4c98a"/>
      <stop offset="1" stop-color="#d39b52"/>
    </linearGradient>
  </defs>
  <rect width="${width}" height="${height}" fill="#071715" fill-opacity="0.08"/>
  <rect width="${width}" height="${height}" fill="url(#photoLeftShade)"/>
  <rect x="0" y="690" width="${width}" height="${height - 690}" fill="url(#photoBottomShade)"/>
  <rect x="54" y="58" width="5" height="1060" fill="#f4c98a" fill-opacity="0.78"/>
  <text x="72" y="105" font-family="Georgia, 'Times New Roman', serif" font-size="54" font-weight="500" fill="#fffaf3">BONHOMIA</text>
  <text x="146" y="146" font-family="Arial, Helvetica, sans-serif" font-size="22" font-weight="800" letter-spacing="12" fill="#f4c98a">SUITES</text>
  <rect x="72" y="172" width="178" height="3" fill="#f4c98a" fill-opacity="0.82"/>
  ${headline.map((line, index) => `<text x="${textX}" y="${headlineY + index * lineGap}" font-family="Georgia, 'Times New Roman', serif" font-size="${headlineSize}" font-weight="700" fill="#fffaf3">${escapeXml(line)}</text>`).join("")}
  <text x="${textX}" y="${subheadY}" font-family="Arial, Helvetica, sans-serif" font-size="40" font-weight="800" fill="#fffaf3">${escapeXml(copy.subhead.replace(/[.]$/u, ""))}</text>
  <text x="${textX}" y="${benefitY}" font-family="Arial, Helvetica, sans-serif" font-size="24" font-weight="900" letter-spacing="3" fill="#f4c98a">${escapeXml(benefitLine)}</text>
  <rect x="${textX}" y="${ctaY}" width="${ctaWidth}" height="92" rx="6" fill="url(#ctaGold)"/>
  <text x="${textX + 42}" y="${ctaY + 59}" font-family="Arial, Helvetica, sans-serif" font-size="34" font-weight="900" letter-spacing="4" fill="#17110c">${escapeXml(copy.cta.toUpperCase())}</text>
  <path d="M${textX + ctaWidth - 86} ${ctaY + 46} H${textX + ctaWidth - 42} M${textX + ctaWidth - 58} ${ctaY + 28} L${textX + ctaWidth - 39} ${ctaY + 46} L${textX + ctaWidth - 58} ${ctaY + 64}" fill="none" stroke="#17110c" stroke-width="5" stroke-linecap="round" stroke-linejoin="round"/>
  <text x="${textX}" y="${ctaY + 154}" font-family="Arial, Helvetica, sans-serif" font-size="34" font-weight="900" letter-spacing="2" fill="#fffaf3">bonhomiasuites.com</text>
</svg>`;
}

function splitPhotoLedHeadline(value, decision) {
  const normalized = String(value || "").trim().toUpperCase();
  if (/^RELÁJATE,\s+YA\s+ESTÁS\s+EN\s+CASA$/u.test(normalized)) {
    return ["RELÁJATE,", "YA ESTÁS", "EN CASA"];
  }

  return splitHeadline(normalized, decision.heroPhotoKind === "suite" ? 13 : 12);
}

function buildBenefitItemSvg(label, index) {
  const x = index * 302;
  const safeLabel = escapeXml(label);
  return `
  <g transform="translate(${x} 0)" fill="none" stroke="#f4c98a" stroke-width="3.5" stroke-linecap="round" stroke-linejoin="round">
    ${benefitIconPath(index)}
    <text x="0" y="84" font-family="Arial, Helvetica, sans-serif" font-size="19" font-weight="900" letter-spacing="1" fill="#fffaf3" stroke="none">${safeLabel}</text>
  </g>`;
}

function benefitIconPath(index) {
  const icons = [
    '<path d="M4 34 H58 V58 H4 Z"/><path d="M10 34 V22 C10 15 15 10 22 10 H42 C49 10 54 15 54 22 V34"/><path d="M16 58 V66 M52 58 V66"/>',
    '<path d="M32 66 C32 66 54 42 54 24 C54 12 45 4 32 4 C19 4 10 12 10 24 C10 42 32 66 32 66 Z"/><circle cx="32" cy="24" r="8"/>',
    '<path d="M10 18 H58 V62 H10 Z"/><path d="M10 30 H58"/><path d="M22 8 V22 M46 8 V22"/><path d="M24 44 H44"/>',
    '<path d="M32 6 L56 16 V34 C56 48 46 60 32 66 C18 60 8 48 8 34 V16 Z"/><path d="M22 34 L30 42 L46 24"/>'
  ];
  return icons[index % icons.length];
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
  <rect x="0" y="${height - 190}" width="${width}" height="190" fill="${template.palette.cream}" fill-opacity="0.98"/>
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
  </defs>
  <rect width="${width}" height="${height}" fill="url(#businessShade)"/>
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
  <rect x="0" y="${height - 190}" width="${width}" height="190" fill="${template.palette.cream}" fill-opacity="0.98"/>
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
  const textX = 72;
  const railX = 786;
  const accentX = 748;
  const railButtonX = 812;
  const railButtonWidth = 236;
  return `
<svg width="${width}" height="${height}" xmlns="http://www.w3.org/2000/svg">
  <defs>
    <linearGradient id="destinationShade" x1="0" y1="0" x2="1" y2="1">
      <stop offset="0" stop-color="${template.palette.dark}" stop-opacity="0.58"/>
      <stop offset="0.55" stop-color="${template.palette.dark}" stop-opacity="0.70"/>
      <stop offset="1" stop-color="${template.palette.blue}" stop-opacity="0.62"/>
    </linearGradient>
  </defs>
  <rect width="${width}" height="${height}" fill="url(#destinationShade)"/>
  <circle cx="190" cy="1010" r="340" fill="${template.palette.accent}" fill-opacity="0.10"/>
  <path d="M72 188 C236 126, 466 144, 692 78" fill="none" stroke="${template.palette.cream}" stroke-width="8" stroke-opacity="0.24"/>
  <rect x="${accentX}" y="0" width="${railX - accentX}" height="${height}" fill="${template.palette.accent}"/>
  <rect x="${railX}" y="0" width="${width - railX}" height="${height}" fill="${template.palette.cream}" fill-opacity="0.97"/>
  <text x="${textX}" y="214" font-family="Arial, Helvetica, sans-serif" font-size="26" font-weight="800" letter-spacing="6" fill="${template.palette.cream}">${escapeXml(copy.eyebrow)}</text>
  <rect x="${textX}" y="252" width="158" height="13" fill="${template.palette.accent}"/>
  ${headline.map((line, index) => `<text x="${textX}" y="${390 + index * 96}" font-family="Arial Black, Arial, Helvetica, sans-serif" font-size="82" font-weight="900" fill="${template.palette.ink}">${escapeXml(line)}</text>`).join("")}
  <text x="${textX}" y="718" font-family="Arial, Helvetica, sans-serif" font-size="39" font-weight="800" fill="${template.palette.cream}">${escapeXml(copy.subhead)}</text>
  <rect x="${railButtonX}" y="798" width="${railButtonWidth}" height="82" fill="${template.palette.accent}"/>
  <text x="${railButtonX + 16}" y="851" font-family="Arial, Helvetica, sans-serif" font-size="22" font-weight="900" fill="${template.palette.cream}">${escapeXml(copy.cta)}</text>
  <text x="814" y="1110" font-family="Arial, Helvetica, sans-serif" font-size="22" font-weight="900" fill="${template.palette.dark}">bonhomiasuites.com</text>
  <rect x="814" y="1170" width="110" height="8" fill="${template.palette.accent}"/>
</svg>`;
}

function getLogoPlacement(template, decision) {
  if (decision.usesHeroPhoto) {
    return {
      panelTop: 0,
      panelLeft: 0,
      panelWidth: 0,
      panelHeight: 0,
      logoTop: 60,
      logoLeft: 72
    };
  }

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
      logoTop: 88,
      logoLeft: 858
    };
  }

  return {
    panelTop: 0,
    panelLeft: 0,
    panelWidth: 0,
    panelHeight: 0,
    logoTop: 1170,
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
  </defs>
  <rect width="${width}" height="${height}" fill="url(#bg)"/>
  <circle cx="${width - 180}" cy="240" r="240" fill="#fffaf3" fill-opacity="0.16"/>
  <circle cx="190" cy="${height - 260}" r="310" fill="#fffaf3" fill-opacity="0.10"/>
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

function contrastRatio(foreground, background) {
  const first = relativeLuminance(hexToRgb(foreground));
  const second = relativeLuminance(hexToRgb(background));
  const lighter = Math.max(first, second);
  const darker = Math.min(first, second);
  return (lighter + 0.05) / (darker + 0.05);
}

function relativeLuminance(rgb) {
  const [r, g, b] = rgb.map((channel) => {
    const normalized = channel / 255;
    return normalized <= 0.03928
      ? normalized / 12.92
      : ((normalized + 0.055) / 1.055) ** 2.4;
  });
  return 0.2126 * r + 0.7152 * g + 0.0722 * b;
}

function hexToRgb(value) {
  const normalized = String(value || "")
    .replace("#", "")
    .trim();
  if (!/^[0-9a-f]{6}$/iu.test(normalized)) {
    return [0, 0, 0];
  }

  return [
    Number.parseInt(normalized.slice(0, 2), 16),
    Number.parseInt(normalized.slice(2, 4), 16),
    Number.parseInt(normalized.slice(4, 6), 16)
  ];
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

export const __mediaTestHooks = {
  buildDeterministicChecks,
  classifyCreativeIntent,
  decideAssetTreatment,
  getCreativeFamily,
  reviewCandidate,
  resolveImageConfig,
  selectEditorialTemplate
};

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
