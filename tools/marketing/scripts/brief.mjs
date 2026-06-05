import "dotenv/config";
import path from "node:path";
import {
  artifactRoot,
  getArgValue,
  hasFlag,
  isDirectRun,
  readJson,
  resolveWeek,
  writeJson,
  writeText
} from "./lib.mjs";
import { collectMarketingIntelligence } from "./intelligence.mjs";

const money = new Intl.NumberFormat("es-MX", {
  style: "currency",
  currency: "MXN",
  maximumFractionDigits: 0
});

export async function buildWeeklyBrief(args = process.argv.slice(2)) {
  const week = resolveWeek(args);
  const outputRoot = path.join(artifactRoot, "intelligence", week.id);
  const shouldUseExisting = hasFlag("--use-existing", args);
  const intelligence = shouldUseExisting
    ? await readJson(path.join(outputRoot, "marketing-data.json"))
    : (await collectMarketingIntelligence(args)).intelligence;

  const strategy = recommendStrategy(intelligence);
  const mediaPlan = buildMediaPlan(intelligence, strategy, getRequestedMedia(args));
  const brief = renderBrief(intelligence, strategy, mediaPlan);
  const checklist = renderReviewChecklist(intelligence, mediaPlan);

  await writeText(path.join(outputRoot, "weekly-brief.md"), brief);
  await writeJson(path.join(outputRoot, "media-plan.json"), mediaPlan);
  await writeText(path.join(outputRoot, "review-checklist.md"), checklist);

  return {
    outputRoot,
    intelligence,
    strategy,
    mediaPlan
  };
}

function recommendStrategy(intelligence) {
  const occupancy = intelligence.saludFinanciera.occupancy;
  const currentOccupancy = occupancy.currentPct;
  const target = occupancy.targetPct;
  const gap = occupancy.gapPctPoints;
  const suites = intelligence.saludFinanciera.suitePerformance || [];
  const underTargetSuites = suites
    .filter((suite) => typeof suite.occupancyPct === "number" && suite.occupancyPct < target)
    .slice(-3);
  const demandSignals = relevantDemandSignals(intelligence);
  const hasExperiences = intelligence.publicExperiences.length > 0 || intelligence.upcomingExperiences.length > 0;
  const hasLuciernagas = [...intelligence.publicExperiences, ...intelligence.upcomingExperiences, ...demandSignals]
    .some((item) => containsAny(`${item.name || ""} ${item.code || ""} ${item.id || ""}`, ["luciernaga", "luciernagas"]));

  const primaryAudience = intelligence.audiencePriority[0]?.label || "Business travelers / companies";
  const secondaryAudience = hasLuciernagas
    ? "BnB travelers, tourists, families and couples for the Luciernagas season"
    : hasExperiences
      ? "Tourists and families around active Bonhomia experiences"
      : "Tourists and event visitors if local research confirms demand";

  let posture = "conversion";
  if (typeof currentOccupancy !== "number") {
    posture = "data-review";
  } else if (currentOccupancy >= target) {
    posture = "protect-rate";
  } else if (gap >= 20) {
    posture = "demand-recovery";
  }

  const actions = [];
  actions.push("Lead with direct booking and suite practicality for company stays: invoice-friendly, comfortable, well located, and easy to reserve.");
  if (hasLuciernagas) {
    actions.push("Use Luciernagas as the emotional hook, then connect the experience to sleeping comfortably at Bonhomia.");
  }
  if (demandSignals.some((signal) => signal.id === "feria-calpulalpan-san-antonio")) {
    actions.push("Research the Feria de Calpulalpan angle before publishing; if confirmed for the week, use local history/event context as the attention hook.");
  }
  if (underTargetSuites.length > 0) {
    actions.push(`Use suite-specific creative for slower rooms: ${underTargetSuites.map((suite) => suite.roomName).join(", ")}.`);
  }
  actions.push("Use Google Search for high-intent lodging demand, Facebook/Instagram for trust and local context, and TikTok for faster discovery.");

  return {
    posture,
    objective: `Move occupancy toward ${target}% while protecting brand quality and direct reservations.`,
    occupancyGap: gap,
    primaryAudience,
    secondaryAudience,
    demandSignals,
    underTargetSuites,
    actions,
    rationale: buildRationale(intelligence, posture)
  };
}

function buildMediaPlan(intelligence, strategy, requestedMedia) {
  const target = intelligence.goals.occupancyTargetPct;
  const signalNames = strategy.demandSignals.map((signal) => signal.name).join(", ");
  const experience = pickExperience(intelligence);
  const suite = strategy.underTargetSuites[0] || intelligence.saludFinanciera.suitePerformance?.[0] || null;

  return {
    schemaVersion: "2026-06-05",
    brandId: intelligence.brand.id,
    week: intelligence.week,
    promptPattern: "Create this marketing material [2 Facebook images, 1 TikTok video]",
    requestedMedia,
    strategy: {
      objective: strategy.objective,
      occupancyTargetPct: target,
      occupancyGapPctPoints: strategy.occupancyGap,
      primaryAudience: strategy.primaryAudience,
      secondaryAudience: strategy.secondaryAudience,
      rationale: strategy.rationale
    },
    assets: [
      {
        id: "fb-business-direct-booking",
        type: "facebook_image",
        platforms: ["Facebook", "Instagram"],
        size: "1080x1350",
        audience: strategy.primaryAudience,
        hook: "Viajas por trabajo a Calpulalpan? Llega, descansa y reserva directo.",
        concept: "Brand-led direct-booking poster for company travelers; use a suite photo only when a specific room or workspace-relevant photo is named.",
        visualDirection: "Bold editorial grid, one fast headline, integrated logo and URL, no generic dark text card, no forced room photo.",
        caption: "Para viajes de trabajo en Calpulalpan: reserva directo, llega tranquilo y descansa en Bonhomia Suites.",
        cta: "Reserva directo por bonhomiasuites.com",
        successMetric: "Direct booking clicks and qualified WhatsApp/company inquiries."
      },
      {
        id: "fb-seasonal-local-hook",
        type: "facebook_image",
        platforms: ["Facebook", "Instagram"],
        size: "1080x1350",
        audience: strategy.secondaryAudience,
        hook: experience
          ? `${experience.name}: vive la experiencia y duerme comodo en Bonhomia.`
          : signalNames
            ? `${signalNames}: ven a Calpulalpan y quedate en Bonhomia.`
            : "Calpulalpan se vive mejor descansando bien.",
        concept: "Local-demand creative that connects an event or experience with the comfort of staying at Bonhomia.",
        visualDirection: "Use real suite/property imagery plus one local-context element after research confirmation.",
        caption: experience
          ? `Si vienes por ${experience.name}, hazlo facil: experiencia, descanso y reserva directa en Bonhomia Suites.`
          : "Cuando Calpulalpan tiene movimiento, Bonhomia es una base practica para llegar, descansar y seguir.",
        cta: "Consulta disponibilidad",
        successMetric: "Engagement, saves, direct messages, and availability checks."
      },
      {
        id: "tiktok-fast-suite-experience",
        type: "tiktok_video",
        platforms: ["TikTok", "Instagram Reels", "YouTube Shorts"],
        size: "1080x1920",
        durationSeconds: "20-35",
        audience: `${strategy.primaryAudience}; secondary: ${strategy.secondaryAudience}`,
        hook: "No busques hotel a la carrera: reserva Bonhomia en menos de un minuto.",
        scenes: [
          "0-3s: exterior or suite detail, fast text hook.",
          "3-10s: show suite comfort and practical location.",
          "10-18s: show direct booking or WhatsApp/direct website CTA.",
          "18-28s: seasonal/local hook or business-travel benefit.",
          "28-35s: final CTA with logo and URL."
        ],
        voiceStyle: "Spanish, relaxed, fast, less formal than the first reservation-video attempt.",
        musicDirection: "Licensed house track from MARKETING_MUSIC_LIBRARY_ROOT; synthetic music is review-only placeholder.",
        caption: "Bonhomia Suites en Calpulalpan: reserva directo, descansa bien y aprovecha lo que esta pasando cerca.",
        cta: "Reserva directo"
      }
    ],
    searchAds: [
      {
        platform: "Google Search",
        audience: strategy.primaryAudience,
        keywordsToResearch: [
          "hotel en Calpulalpan",
          "suites en Calpulalpan",
          "hospedaje empresas Calpulalpan",
          "hotel cerca de Calpulalpan"
        ],
        headlineIdeas: [
          "Suites en Calpulalpan",
          "Reserva Directo Bonhomia",
          "Hospedaje Comodo Para Trabajo"
        ],
        descriptionIdea: "Suites comodas, reserva directa y una estancia practica para trabajo o descanso en Calpulalpan."
      }
    ],
    reviewChecklist: [
      "Strategy appears before asset production.",
      "Brief states occupancy gap to 50%.",
      "No customer PII, credentials, DB strings, or payment data.",
      "Local event claims are verified before publishing.",
      "Images are readable on mobile and not over-zoomed.",
      "TikTok/Reels voice is relaxed and faster paced.",
      "Music is licensed or marked as placeholder."
    ]
  };
}

function renderBrief(intelligence, strategy, mediaPlan) {
  const occupancy = intelligence.saludFinanciera.occupancy;
  const revenue = intelligence.saludFinanciera.revenue;
  const experiences = intelligence.publicExperiences;
  const upcoming = intelligence.upcomingExperiences;

  return `# Bonhomia Weekly Marketing Brief

Week: ${intelligence.week.id} (${intelligence.week.startDate} to ${intelligence.week.endDate})
Data source: ${intelligence.source.officialService} / ${intelligence.source.storedProcedure}

## Goal

Raise overall occupancy to ${intelligence.goals.occupancyTargetPct}%.
Current occupancy: ${formatPct(occupancy.currentPct)}.
Gap to goal: ${formatGap(occupancy.gapPctPoints)}.

## Strategy Recommendation

${strategy.objective}

Primary audience: ${strategy.primaryAudience}.
Secondary angle: ${strategy.secondaryAudience}.

Why this week: ${strategy.rationale}

Recommended actions:
${strategy.actions.map((action) => `- ${action}`).join("\n")}

## Sales And Occupancy Snapshot

- Available nights: ${valueOrNa(occupancy.availableNights)}
- Occupied nights: ${valueOrNa(occupancy.occupiedNights)}
- Room revenue: ${formatMoney(revenue?.roomRevenue)}
- ADR: ${formatMoney(revenue?.adr)}
- RevPAR: ${formatMoney(revenue?.revpar)}
- Reservation count: ${valueOrNa(revenue?.reservationCount)}
- Collection rate: ${formatPct(revenue?.collectionPct)}

## Suites To Watch

${renderSuites(strategy.underTargetSuites)}

## Experiences And Demand Signals

Current public experiences:
${renderExperiences(experiences)}

Upcoming public experiences:
${renderExperiences(upcoming.slice(0, 5))}

Known demand signals:
${renderSignals(strategy.demandSignals)}

## Media Plan

${mediaPlan.assets.map((asset) => `- ${asset.type} (${asset.size}): ${asset.hook}`).join("\n")}

Google Search: research business and direct-booking intent before spending.

## Human Review Before Publishing

- Confirm local event details with Google/Facebook/Instagram research.
- Confirm any offer, discount, or availability claim with operations.
- Keep all production data aggregate-only.
- Use licensed music for publishable video.
`;
}

function renderReviewChecklist(intelligence, mediaPlan) {
  return `# Marketing Review Checklist

Brand: ${intelligence.brand.name}
Week: ${intelligence.week.id}

${mediaPlan.reviewChecklist.map((item) => `- [ ] ${item}`).join("\n")}

## Data Safety

- [ ] No customer names, reservation IDs, payment references, DB strings, passwords, or API keys appear in captions, images, videos, or reports.
- [ ] Financial data is summarized as aggregate business metrics only.

## Approval

- [ ] Strategy approved.
- [ ] Copy approved.
- [ ] Creative direction approved.
- [ ] Local/event claims verified.
- [ ] Final assets reviewed on mobile.
`;
}

function getRequestedMedia(args) {
  const requested = getArgValue("--media", args);
  if (requested) {
    return requested;
  }

  return "2 Facebook images, 1 TikTok video";
}

function relevantDemandSignals(intelligence) {
  const start = new Date(`${intelligence.week.startDate}T12:00:00`);
  const end = new Date(`${intelligence.week.endDate}T12:00:00`);
  const signals = [];

  for (const signal of intelligence.knownDemandSignals || []) {
    if (signal.id === "feria-calpulalpan-san-antonio" && isNearMonthDay(start, end, 6, 13, 14)) {
      signals.push(signal);
    }

    if (signal.id === "luciernagas" && overlapsMonthDayRange(start, end, 6, 15, 8, 15, 21)) {
      signals.push(signal);
    }
  }

  return signals;
}

function isNearMonthDay(start, end, month, day, leadDays) {
  const year = start.getFullYear();
  const event = new Date(year, month - 1, day, 12);
  const windowStart = new Date(event);
  windowStart.setDate(event.getDate() - leadDays);
  const windowEnd = new Date(event);
  windowEnd.setDate(event.getDate() + leadDays);
  return start <= windowEnd && end >= windowStart;
}

function overlapsMonthDayRange(start, end, startMonth, startDay, endMonth, endDay, leadDays) {
  const year = start.getFullYear();
  const seasonStart = new Date(year, startMonth - 1, startDay, 12);
  seasonStart.setDate(seasonStart.getDate() - leadDays);
  const seasonEnd = new Date(year, endMonth - 1, endDay, 12);
  return start <= seasonEnd && end >= seasonStart;
}

function buildRationale(intelligence, posture) {
  const occupancy = intelligence.saludFinanciera.occupancy;
  const parts = [];

  if (typeof occupancy.currentPct === "number") {
    parts.push(`occupancy is ${formatPct(occupancy.currentPct)}, ${formatGap(occupancy.gapPctPoints)} from the 50% target`);
  } else {
    parts.push("occupancy is not available in the current export, so the first job is to verify data quality");
  }

  if (posture === "demand-recovery") {
    parts.push("the gap is large enough to prioritize demand capture over brand-only content");
  } else if (posture === "protect-rate") {
    parts.push("the target is already met or close, so messaging should protect rate and improve direct bookings");
  } else {
    parts.push("the gap supports a conversion-focused weekly plan");
  }

  if (intelligence.upcomingExperiences.length > 0) {
    parts.push(`there are ${intelligence.upcomingExperiences.length} public experience(s) in the next 90 days`);
  }

  return `${parts.join("; ")}.`;
}

function pickExperience(intelligence) {
  return intelligence.publicExperiences[0] || intelligence.upcomingExperiences[0] || null;
}

function renderSuites(suites) {
  if (!suites || suites.length === 0) {
    return "- No under-target suite callout is available from the current export.";
  }

  return suites
    .map((suite) => `- ${suite.roomName}: ${formatPct(suite.occupancyPct)} occupancy, ${formatMoney(suite.roomRevenue)} room revenue.`)
    .join("\n");
}

function renderExperiences(experiences) {
  if (!experiences || experiences.length === 0) {
    return "- None found in this window.";
  }

  return experiences
    .map((experience) => `- ${experience.name} (${experience.seasonStart || "sin inicio"} to ${experience.seasonEnd || "sin fin"})`)
    .join("\n");
}

function renderSignals(signals) {
  if (!signals || signals.length === 0) {
    return "- None triggered by the configured dates.";
  }

  return signals
    .map((signal) => `- ${signal.name}: ${signal.strategyUse}`)
    .join("\n");
}

function containsAny(value, needles) {
  const normalized = value.toLowerCase();
  return needles.some((needle) => normalized.includes(needle));
}

function formatMoney(value) {
  return typeof value === "number" ? money.format(value) : "n/a";
}

function formatPct(value) {
  return typeof value === "number" ? `${value.toFixed(2)}%` : "n/a";
}

function formatGap(value) {
  if (typeof value !== "number") {
    return "n/a";
  }

  if (value <= 0) {
    return `${Math.abs(value).toFixed(2)} pts above target`;
  }

  return `${value.toFixed(2)} pts below target`;
}

function valueOrNa(value) {
  return value === null || value === undefined ? "n/a" : value;
}

if (isDirectRun(import.meta.url)) {
  buildWeeklyBrief()
    .then(({ outputRoot, strategy }) => {
      console.log(`Weekly brief written to ${path.join(outputRoot, "weekly-brief.md")}`);
      console.log(`Recommended posture: ${strategy.posture}`);
    })
    .catch((error) => {
      console.error(error.message);
      process.exit(1);
    });
}
