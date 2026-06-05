import fs from "node:fs/promises";
import path from "node:path";
import {
  fileExists,
  loadBrandContext,
  repoPath,
  toolRoot
} from "./lib.mjs";

const errors = [];
const context = await loadBrandContext();
const brand = context.brand;

if (brand.id !== "bonhomia") {
  errors.push("V1 marketing intelligence should be scoped to Bonhomia.");
}

if (brand.strategy?.primaryKpi?.targetPct !== 50) {
  errors.push("Bonhomia primary KPI must target 50% occupancy.");
}

if (brand.strategy?.audiencePriority?.[0]?.id !== "business-travelers-companies") {
  errors.push("Business travelers / companies must be the first audience priority.");
}

if (brand.strategy?.financialSource?.officialService !== "IReportesFinancierosService.GetSaludEmpresaAsync") {
  errors.push("Financial source must point to Salud Financiera's official service.");
}

if (brand.strategy?.financialSource?.storedProcedure !== "reporteFinanciero.Reporte_Salud_Empresa") {
  errors.push("Financial source must use the Salud Empresa stored procedure.");
}

if (brand.strategy?.financialSource?.useProductionData !== true) {
  errors.push("Brand strategy should explicitly allow aggregate production data for marketing intelligence.");
}

if (brand.providers?.image?.defaultProvider !== "openai") {
  errors.push("Brand image provider should default to OpenAI.");
}

if (!brand.providers?.image?.openai?.model || !brand.providers?.image?.openai?.fallbackModel) {
  errors.push("Brand image provider must declare OpenAI model and fallbackModel.");
}

if (brand.providers?.image?.output?.width !== 1080 || brand.providers?.image?.output?.height !== 1350) {
  errors.push("Brand image output must default to 1080x1350.");
}

const imageReview = brand.providers?.image?.review || {};
if (!imageReview.model) {
  errors.push("Brand image review provider must declare a vision-capable review model.");
}

if (typeof imageReview.minimumScore !== "number" || imageReview.minimumScore < 80) {
  errors.push("Brand image review minimumScore should enforce editorial quality at 80+.");
}

if (
  typeof imageReview.maxAttempts !== "number"
  || typeof imageReview.candidatesPerAsset !== "number"
  || imageReview.maxAttempts < imageReview.candidatesPerAsset
  || imageReview.candidatesPerAsset < 1
) {
  errors.push("Brand image review must generate at least one candidate and allow enough attempts for regeneration.");
}

if (!brand.assets?.suiteImageRoot || !(await fileExists(repoPath(brand.assets.suiteImageRoot)))) {
  errors.push("Brand suite image root is missing or invalid.");
}

if (!brand.assets?.logoPath || !(await fileExists(repoPath(brand.assets.logoPath)))) {
  errors.push("Brand logoPath is missing or invalid.");
}

if (brand.assets?.factualImagePolicy?.suitePhotosAreLocked !== true) {
  errors.push("Brand factual image policy must lock suite photos.");
}

if (!Array.isArray(brand.assets?.editorialSuitePhotos) || brand.assets.editorialSuitePhotos.length === 0) {
  errors.push("Brand assets should declare editorialSuitePhotos for quality-first suite modules.");
}

for (const relativePath of brand.assets?.editorialSuitePhotos || []) {
  if (!(await fileExists(repoPath(relativePath)))) {
    errors.push(`Brand editorial suite photo does not exist: ${relativePath}`);
  }
}

for (const [assetKey, relativePath] of Object.entries(brand.assets?.repoImages || {})) {
  if (!(await fileExists(repoPath(relativePath)))) {
    errors.push(`Brand asset '${assetKey}' does not exist: ${relativePath}`);
  }
}

const requiredFiles = [
  "README.md",
  "CODEX_PROJECT.md",
  path.join("knowledge", "playbook.md"),
  path.join("knowledge", "lesson-inbox", "README.md"),
  path.join("docs", "tool-catalog.md"),
  path.join("docs", "art-direction-references.md"),
  path.join("docs", "visual-design-system.md"),
  path.join("schemas", "media-plan.schema.json"),
  path.join("scripts", "intelligence.mjs"),
  path.join("scripts", "brief.mjs"),
  path.join("scripts", "media.mjs"),
  path.join("scripts", "lessons.mjs")
];

for (const relativePath of requiredFiles) {
  if (!(await fileExists(path.join(toolRoot, relativePath)))) {
    errors.push(`Required marketing file is missing: ${relativePath}`);
  }
}

if (await fileExists(path.join(toolRoot, "campaigns", "bonhomia-reservation-video"))) {
  errors.push("The fixed reservation video campaign must not remain in active campaigns/.");
}

if (!(await fileExists(path.join(toolRoot, "archive", "bonhomia-reservation-video")))) {
  errors.push("The fixed reservation video campaign should be archived for reference.");
}

const packageJson = JSON.parse(await fs.readFile(path.join(toolRoot, "package.json"), "utf8"));
const requiredScripts = ["intelligence", "brief", "media", "lessons", "validate", "test"];
for (const script of requiredScripts) {
  if (!packageJson.scripts?.[script]) {
    errors.push(`package.json is missing npm script '${script}'.`);
  }
}

const requiredDependencies = ["dotenv", "mssql", "openai", "sharp"];
for (const dependency of requiredDependencies) {
  if (!packageJson.dependencies?.[dependency]) {
    errors.push(`package.json is missing dependency '${dependency}'.`);
  }
}

const scannedFiles = await collectTextFiles(toolRoot);
for (const filePath of scannedFiles) {
  const value = await fs.readFile(filePath, "utf8");
  if (containsSecretLikeValue(value)) {
    errors.push(`${path.relative(toolRoot, filePath)} appears to contain a secret-like value.`);
  }
}

if (errors.length > 0) {
  for (const error of errors) {
    console.error(`ERROR: ${error}`);
  }
  process.exit(1);
}

console.log("Marketing intelligence workspace valid.");

async function collectTextFiles(root) {
  const collected = [];
  await visit(root, collected);
  return collected;
}

async function visit(dir, collected) {
  const entries = await fs.readdir(dir, { withFileTypes: true });
  for (const entry of entries) {
    if (entry.name === "node_modules" || entry.name === "artifacts" || entry.name === ".git") {
      continue;
    }

    const fullPath = path.join(dir, entry.name);
    if (path.relative(toolRoot, fullPath) === path.join("scripts", "validate.mjs")) {
      continue;
    }

    if (entry.isDirectory()) {
      await visit(fullPath, collected);
      continue;
    }

    if (/\.(json|md|mjs|jsx|example|txt)$/iu.test(entry.name) || entry.name === ".env.example") {
      collected.push(fullPath);
    }
  }
}

function containsSecretLikeValue(value) {
  if (/(sk_live_|sk-proj-|xox[baprs]-|PAYPAL-[A-Z0-9]{10,})/imu.test(value)) {
    return true;
  }

  if (/(Password|Pwd)=(?!<redacted>|$)[^;\s]+/imu.test(value)) {
    return true;
  }

  const assignmentKeys = [
    "OPENAI_API_KEY",
    "ELEVENLABS_API_KEY",
    "PAYPAL_SANDBOX_BUYER_PASSWORD",
    "ASPNETCORE_BonhomiaCheckout__PayPalClientSecret",
    "ASPNETCORE_ConnectionStrings__OrionDb",
    "MARKETING_ORIONDB_CONNECTION_STRING"
  ];

  for (const line of value.split(/\r?\n/u)) {
    for (const key of assignmentKeys) {
      const match = line.match(new RegExp(`${escapeRegex(key)}\\s*=\\s*(.+)$`, "iu"));
      if (!match) {
        continue;
      }

      const assignedValue = match[1].trim().replace(/^["']|["']$/gu, "");
      if (assignedValue && !isDocumentedPlaceholder(assignedValue)) {
        return true;
      }
    }
  }

  return false;
}

function escapeRegex(value) {
  return value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

function isDocumentedPlaceholder(value) {
  return /^<[^>]+>$/u.test(value)
    || value.includes("<redacted>")
    || value.includes("<production-or-sandbox-oriondb-connection-string>");
}
