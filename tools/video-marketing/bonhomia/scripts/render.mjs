import path from "node:path";
import { bundle } from "@remotion/bundler";
import { renderMedia, selectComposition } from "@remotion/renderer";
import { artifactRoot, ensureDir, publicRoot, toolRoot } from "./lib.mjs";
import { prepareAssets } from "./prepare-assets.mjs";

const props = await prepareAssets();
const outputRoot = path.join(artifactRoot, "final");
await ensureDir(outputRoot);

const outputLocation = path.join(outputRoot, "bonhomia-social-promo-vertical.mp4");
const entryPoint = path.join(toolRoot, "src", "remotion", "index.jsx");
const bundleDir = path.join(artifactRoot, "remotion-bundle");

console.log("Bundling Remotion composition...");
const serveUrl = await bundle({
  entryPoint,
  outDir: bundleDir,
  publicDir: publicRoot
});

const composition = await selectComposition({
  serveUrl,
  id: "BonhomiaPromo",
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
