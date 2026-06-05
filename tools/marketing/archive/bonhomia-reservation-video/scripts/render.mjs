import path from "node:path";
import { bundle } from "@remotion/bundler";
import { renderMedia, selectComposition } from "@remotion/renderer";
import {
  campaignCompositionId,
  campaignOutputName,
  ensureDir,
  toolRoot
} from "./lib.mjs";
import { prepareAssets } from "./prepare-assets.mjs";

const { context, props } = await prepareAssets();
const outputRoot = context.artifacts.final;
await ensureDir(outputRoot);

const outputLocation = path.join(outputRoot, campaignOutputName(context));
const entryPoint = path.join(toolRoot, "src", "remotion", "index.jsx");
const bundleDir = context.artifacts.bundle;

console.log("Bundling Remotion composition...");
const serveUrl = await bundle({
  entryPoint,
  outDir: bundleDir,
  publicDir: context.artifacts.public
});

const composition = await selectComposition({
  serveUrl,
  id: campaignCompositionId(context),
  inputProps: props
});

console.log(`Rendering ${outputLocation}...`);
await renderMedia({
  composition,
  serveUrl,
  codec: "h264",
  outputLocation,
  inputProps: props,
  overwrite: true,
  chromiumOptions: {
    ignoreCertificateErrors: true
  },
  onProgress: ({ progress }) => {
    const percent = Math.round(progress * 100);
    if (percent % 10 === 0) {
      process.stdout.write(`\r${percent}%`);
    }
  }
});

console.log(`\nRendered ${outputLocation}`);
