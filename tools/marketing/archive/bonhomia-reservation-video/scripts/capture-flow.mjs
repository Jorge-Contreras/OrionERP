import "dotenv/config";
import fs from "node:fs/promises";
import http from "node:http";
import https from "node:https";
import path from "node:path";
import { chromium } from "playwright";
import {
  addDays,
  cleanDir,
  ensureDir,
  formatDate,
  hasFlag,
  loadCampaignContext,
  writeJson
} from "./lib.mjs";

const campaignContext = await loadCampaignContext();
const { scenario } = campaignContext;
const captureRoot = campaignContext.artifacts.captures;
const videoRoot = path.join(captureRoot, "video");
const screenshotRoot = path.join(captureRoot, "screenshots");

const baseUrl = await resolveBaseUrl(
  process.env.BONHOMIA_BASE_URL
    || process.env.MARKETING_BASE_URL
    || scenario.baseUrl
    || campaignContext.brand.publicBaseUrl
);
const headed = hasFlag("--headed");
const skipPayPal = hasFlag("--skip-paypal");

const checkIn = addDays(new Date(), scenario.stay.checkInOffsetDays);
const checkOut = addDays(checkIn, scenario.stay.nights);
const stay = {
  checkIn: formatDate(checkIn),
  checkOut: formatDate(checkOut),
  guests: scenario.stay.guests
};

await cleanDir(captureRoot);
await ensureDir(videoRoot);
await ensureDir(screenshotRoot);

const manifest = {
  campaignId: campaignContext.campaignId,
  brandId: campaignContext.brandId,
  baseUrl,
  capturedAtUtc: new Date().toISOString(),
  viewport: scenario.viewport,
  stay,
  screenshots: {},
  video: null,
  paymentOutcome: "not_attempted",
  notes: []
};

const browser = await chromium.launch({ headless: !headed });
const browserContext = await browser.newContext({
  viewport: {
    width: scenario.viewport.width,
    height: scenario.viewport.height
  },
  deviceScaleFactor: scenario.viewport.deviceScaleFactor || 2,
  isMobile: true,
  hasTouch: true,
  ignoreHTTPSErrors: true,
  recordVideo: {
    dir: videoRoot,
    size: {
      width: scenario.viewport.width,
      height: scenario.viewport.height
    }
  }
});

const page = await browserContext.newPage();
let captureError = null;

try {
  await visitAndCapture(page, "/", "landing", "Bonhomia landing");
  await visitAndCapture(page, "/suites", "suites", "Suites catalog");
  await visitAndCapture(page, "/servicios", "services", "Services page");
  await visitAndCapture(page, "/reservar", "reservation-dates", "Reservation dates");

  await setDate(page, 0, stay.checkIn);
  await setDate(page, 1, stay.checkOut);
  await setGuests(page, stay.guests);
  await capture(page, "reservation-dates-filled", "Dates and guests selected");

  await clickButton(page, "Continuar a suites");
  await page.waitForSelector(".bonhomia-room-picker", { timeout: 15000 });
  await chooseSuite(page, scenario.preferredSuiteNames);
  await capture(page, "reservation-suite", "Available suite selected");

  await clickButton(page, "Continuar a experiencias");
  await page.waitForSelector("text=Experiencias", { timeout: 15000 });
  await capture(page, "reservation-experiences", "Experiences step");

  await clickButton(page, "Continuar a extras");
  await page.waitForSelector(".bonhomia-extra-selector", { timeout: 15000 });
  await chooseExtras(page, scenario.extras);
  await capture(page, "reservation-extras", "Extras selected");

  await clickButton(page, "Continuar al pago");
  await page.waitForSelector(".bonhomia-summary", { timeout: 30000 });
  await capture(page, "payment-quote", "Quote ready");

  await fillPhoneAndAccept(page, scenario.customer.phone);
  await capture(page, "payment", "PayPal payment gate");

  if (!skipPayPal && scenario.paypal.attemptSandboxCompletion) {
    await attemptPayPalSandboxCompletion(page, browserContext, scenario.paypal);
  } else {
    manifest.paymentOutcome = "skipped";
    manifest.notes.push("PayPal completion skipped by flag or scenario.");
  }

  if (await page.locator("#bonhomia-payment-confirmation").count()) {
    await capture(page, "confirmation", "Reservation confirmation");
    manifest.paymentOutcome = "completed";
  } else if (manifest.paymentOutcome !== "completed") {
    manifest.paymentOutcome = manifest.paymentOutcome === "not_attempted"
      ? "handoff"
      : manifest.paymentOutcome;
    manifest.notes.push("No confirmation panel was captured. Renderer will use the PayPal handoff scene and final CTA.");
  }
} catch (error) {
  captureError = error;
  manifest.captureStatus = "failed";
  manifest.notes.push(`Capture failed: ${error.message}`);
} finally {
  const video = page.video();
  await browserContext.close();
  if (video) {
    try {
      const videoPath = path.join(videoRoot, "bonhomia-flow.webm");
      await video.saveAs(videoPath);
      manifest.video = path.relative(captureRoot, videoPath).split(path.sep).join("/");
    } catch (error) {
      manifest.notes.push(`Recorded video could not be saved: ${error.message}`);
    }
  }
  await browser.close();
  manifest.captureStatus ??= "completed";
  await writeJson(path.join(captureRoot, "manifest.json"), manifest);
  console.log(`Capture manifest written to ${path.join(captureRoot, "manifest.json")}`);
}

if (captureError) {
  throw captureError;
}

async function visitAndCapture(page, route, key, label) {
  await page.goto(`${baseUrl}${route}`, { waitUntil: "domcontentloaded", timeout: 30000 });
  await waitForBlazor(page);
  await capture(page, key, label);
}

async function waitForBlazor(page) {
  await page.waitForSelector("body", { timeout: 15000 });
  await page.waitForTimeout(900);
}

async function capture(page, key, label) {
  const filePath = path.join(screenshotRoot, `${key}.png`);
  await page.screenshot({ path: filePath, fullPage: false });
  manifest.screenshots[key] = {
    label,
    file: path.relative(captureRoot, filePath).split(path.sep).join("/")
  };
}

async function setDate(page, index, value) {
  const input = page.locator(".bonhomia-date-row input[type='date']").nth(index);
  await input.waitFor({ timeout: 15000 });
  await input.evaluate((element, nextValue) => {
    element.value = nextValue;
    element.dispatchEvent(new Event("change", { bubbles: true }));
  }, value);
  await page.waitForTimeout(350);
}

async function setGuests(page, guests) {
  const input = page.locator(".bonhomia-date-row input[type='number']").first();
  await input.evaluate((element, nextValue) => {
    element.value = String(nextValue);
    element.dispatchEvent(new Event("change", { bubbles: true }));
  }, guests);
  await page.waitForTimeout(350);
}

async function clickButton(page, text) {
  const button = page.getByRole("button", { name: new RegExp(escapeRegex(text), "i") }).first();
  await button.waitFor({ timeout: 15000 });
  await button.click();
  await waitForBlazor(page);
}

async function chooseSuite(page, preferredSuiteNames) {
  for (const suiteName of preferredSuiteNames) {
    const option = page.locator(".bonhomia-room-option:not(.bonhomia-room-option--unavailable) .bonhomia-room-option__select")
      .filter({ hasText: suiteName })
      .first();
    if (await option.count()) {
      await option.click();
      await waitForBlazor(page);
      manifest.selectedSuite = suiteName;
      return;
    }
  }

  const firstAvailable = page.locator(".bonhomia-room-option:not(.bonhomia-room-option--unavailable) .bonhomia-room-option__select").first();
  await firstAvailable.waitFor({ timeout: 15000 });
  manifest.selectedSuite = await firstAvailable.locator("strong").first().innerText().catch(() => "First available suite");
  await firstAvailable.click();
  await waitForBlazor(page);
}

async function chooseExtras(page, extras) {
  if (!extras || extras.length === 0) {
    return;
  }

  for (const extra of extras) {
    const card = page.locator(".bonhomia-extra-selector article")
      .filter({ hasText: new RegExp(escapeRegex(extra.nameContains), "i") })
      .first();
    if (!(await card.count())) {
      continue;
    }

    const select = card.locator("select").first();
    await select.selectOption(String(extra.quantity));
    await page.waitForTimeout(250);
  }
}

async function fillPhoneAndAccept(page, phone) {
  const phoneInput = page.locator(".bonhomia-phone-field input[type='tel']").first();
  await phoneInput.waitFor({ timeout: 15000 });
  await phoneInput.fill(phone);
  await page
    .locator(".bonhomia-checkout-requirement")
    .getByRole("button", { name: /Aceptar/i })
    .click();
  await page.waitForTimeout(1800);
}

async function attemptPayPalSandboxCompletion(page, context, paypalConfig) {
  const buyerEmail = process.env[paypalConfig.buyerEmailEnv];
  const buyerPassword = process.env[paypalConfig.buyerPasswordEnv];

  if (!buyerEmail || !buyerPassword) {
    manifest.paymentOutcome = "handoff";
    manifest.notes.push("PayPal sandbox buyer credentials are not set, so capture stopped at the PayPal handoff.");
    return;
  }

  try {
    const popupPromise = context.waitForEvent("page", { timeout: 15000 }).catch(() => null);
    const buttonFrame = page.frameLocator("iframe[title*='PayPal'], iframe[name*='paypal']").first();
    await buttonFrame.locator("body").click({ timeout: 15000 });
    const popup = await popupPromise;
    if (!popup) {
      manifest.paymentOutcome = "paypal_popup_not_found";
      manifest.notes.push("PayPal popup did not open during sandbox completion.");
      return;
    }

    await popup.waitForLoadState("domcontentloaded", { timeout: 30000 });
    await popup.locator("input[type='email'], input[name='login_email']").first().fill(buyerEmail, { timeout: 30000 });
    await popup.getByRole("button", { name: /Next|Siguiente|Continuar/i }).click({ timeout: 15000 }).catch(() => {});
    await popup.locator("input[type='password'], input[name='login_password']").first().fill(buyerPassword, { timeout: 30000 });
    await popup.getByRole("button", { name: /Log In|Iniciar sesion|Continuar|Pagar|Pay Now|Agree/i }).first().click({ timeout: 30000 });
    await popup.waitForTimeout(2500);

    const finalButton = popup.getByRole("button", { name: /Pay Now|Pagar ahora|Agree and Pay|Aceptar y pagar|Complete Purchase/i }).first();
    if (await finalButton.count()) {
      await finalButton.click({ timeout: 30000 });
    }

    await page.waitForSelector("#bonhomia-payment-confirmation", { timeout: 60000 });
    manifest.paymentOutcome = "completed";
  } catch (error) {
    manifest.paymentOutcome = "paypal_completion_failed";
    manifest.notes.push(`PayPal sandbox completion failed: ${error.message}`);
  }
}

async function resolveBaseUrl(preferredUrl) {
  const candidates = [
    preferredUrl,
    scenario.baseUrl,
    "http://localhost:57474",
    "https://localhost:57473"
  ]
    .filter(Boolean)
    .map(normalizeBaseUrl)
    .filter((url, index, values) => values.indexOf(url) === index);

  const failures = [];
  for (const candidate of candidates) {
    try {
      await checkHealth(candidate);
      return candidate;
    } catch (error) {
      failures.push(`${candidate}: ${error.message}`);
    }
  }

  throw new Error(
    "Bonhomia site is not reachable. Start OrionERP.Bonhomia.Web in Visual Studio or set BONHOMIA_BASE_URL. " +
    failures.join(" | ")
  );
}

async function checkHealth(url) {
  const endpoint = new URL("/healthz", url);
  const transport = endpoint.protocol === "https:" ? https : http;

  await new Promise((resolve, reject) => {
    const request = transport.request(
      endpoint,
      {
        method: "GET",
        timeout: 5000,
        rejectUnauthorized: false
      },
      (response) => {
        response.resume();
        response.on("end", () => {
          if (response.statusCode && response.statusCode >= 200 && response.statusCode < 300) {
            resolve();
          } else {
            reject(new Error(`Health check returned ${response.statusCode}`));
          }
        });
      });

    request.on("timeout", () => {
      request.destroy(new Error("Health check timed out"));
    });
    request.on("error", reject);
    request.end();
  });
}

function normalizeBaseUrl(url) {
  return url.replace(/\/+$/u, "");
}

function escapeRegex(value) {
  return value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}
