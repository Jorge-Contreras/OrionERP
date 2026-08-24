import "dotenv/config";
import path from "node:path";
import sql from "mssql";
import {
  addDays,
  artifactRoot,
  ensureDir,
  formatDate,
  getConfiguredConnectionString,
  getMarketingRfc,
  isDirectRun,
  loadBrandContext,
  resolveWeek,
  writeJson,
  writeText
} from "./lib.mjs";

const outputFileName = "marketing-data.json";

export async function collectMarketingIntelligence(args = process.argv.slice(2)) {
  const brandContext = await loadBrandContext(args);
  const week = resolveWeek(args);
  const connectionString = getConfiguredConnectionString();
  const rfc = getMarketingRfc(brandContext.brand);

  if (!connectionString) {
    throw new Error(
      "Missing OrionDb connection string. Set ASPNETCORE_ConnectionStrings__OrionDb or MARKETING_ORIONDB_CONNECTION_STRING."
    );
  }

  const dbConfig = parseAdoConnectionString(connectionString);
  const outputRoot = path.join(artifactRoot, "intelligence", week.id);
  await ensureDir(outputRoot);

  const pool = new sql.ConnectionPool(dbConfig);
  await pool.connect();

  try {
    const salud = await readSaludEmpresa(pool, week, rfc);
    const publicExperiences = await readPublicExperiences(pool, week.startDate, week.endDateExclusive);
    const upcomingExperiences = await readPublicExperiences(
      pool,
      week.startDate,
      formatDate(addDays(new Date(`${week.startDate}T12:00:00`), 90))
    );

    const intelligence = buildIntelligence({
      brandContext,
      week,
      rfc,
      dbConfig,
      salud,
      publicExperiences,
      upcomingExperiences
    });

    await writeJson(path.join(outputRoot, outputFileName), intelligence);
    await writeText(path.join(outputRoot, "research-checklist.md"), buildResearchChecklist(intelligence));

    return {
      brandContext,
      week,
      outputRoot,
      intelligence
    };
  } finally {
    await pool.close();
  }
}

async function readSaludEmpresa(pool, week, rfc) {
  const request = pool.request();
  request.input("AnioInicio", sql.Int, week.financialScope.yearStart);
  request.input("MesInicio", sql.Int, week.financialScope.monthStart);
  request.input("AnioFin", sql.Int, week.financialScope.yearEnd);
  request.input("MesFin", sql.Int, week.financialScope.monthEnd);
  request.input("RFC", sql.NVarChar(20), rfc);
  request.input("IncluirHabitacionesNoRentables", sql.Bit, false);

  const result = await request.execute("reporteFinanciero.Reporte_Salud_Empresa");
  const recordsets = result.recordsets || [];

  return {
    executiveIndicators: (recordsets[0] || []).map(mapExecutiveIndicator),
    suitePerformance: (recordsets[1] || []).map(mapSuitePerformance),
    financialBreakdown: (recordsets[2] || []).map(mapFinancialBreakdown),
    cashFlow: (recordsets[3] || []).map(mapCashFlow),
    dataQuality: (recordsets[4] || []).map(mapDataQuality)
  };
}

async function readPublicExperiences(pool, startDate, endDateExclusive) {
  const tablesReady = await pool.request().query(`
SELECT CAST(CASE
    WHEN OBJECT_ID(N'dbo.ExperienceProvider', N'U') IS NOT NULL
     AND OBJECT_ID(N'dbo.Experience', N'U') IS NOT NULL
     AND OBJECT_ID(N'dbo.ExperiencePackage', N'U') IS NOT NULL
     AND OBJECT_ID(N'dbo.ExperienceAddOn', N'U') IS NOT NULL
    THEN 1 ELSE 0 END AS bit) AS TablesReady;
`);

  if (!tablesReady.recordset?.[0]?.TablesReady) {
    return [];
  }

  const request = pool.request();
  request.input("StartDate", sql.Date, new Date(`${startDate}T00:00:00`));
  request.input("EndDateExclusive", sql.Date, new Date(`${endDateExclusive}T00:00:00`));

  const result = await request.query(`
SELECT
    e.ExperienceID,
    e.Code,
    e.[Name],
    e.[Description],
    e.Category,
    ISNULL(p.[Name], '') AS ProviderName,
    e.SeasonStart,
    e.SeasonEnd,
    e.MinimumParticipants,
    e.MaximumParticipants,
    CAST(ISNULL(e.IsPublic, 0) AS bit) AS IsPublic,
    CAST(ISNULL(e.IsActive, 0) AS bit) AS IsActive
FROM dbo.Experience e
LEFT JOIN dbo.ExperienceProvider p
    ON p.ExperienceProviderID = e.ExperienceProviderID
WHERE e.IsActive = 1
  AND e.IsPublic = 1
  AND (
        (e.SeasonStart IS NULL OR e.SeasonStart < @EndDateExclusive)
        AND (e.SeasonEnd IS NULL OR e.SeasonEnd >= @StartDate)
      )
ORDER BY e.SeasonStart, e.[Name];

SELECT
    ep.ExperiencePackageID,
    ep.ExperienceID,
    ep.Code,
    ep.[Name],
    ep.[Description],
    ep.Includes,
    ep.ProviderPackageName,
    CAST(ISNULL(ep.UnitPrice, 0) AS decimal(18,2)) AS UnitPrice,
    ISNULL(ep.TaxMode, 'TaxableExclusive') AS TaxMode,
    CAST(ISNULL(ep.IsPublic, 0) AS bit) AS IsPublic,
    CAST(ISNULL(ep.IsActive, 0) AS bit) AS IsActive,
    ep.DisplayOrder
FROM dbo.ExperiencePackage ep
INNER JOIN dbo.Experience e
    ON e.ExperienceID = ep.ExperienceID
WHERE e.IsActive = 1
  AND e.IsPublic = 1
  AND ep.IsActive = 1
  AND ep.IsPublic = 1
ORDER BY ep.ExperienceID, ep.DisplayOrder, ep.[Name];

SELECT
    ea.ExperienceAddOnID,
    ea.ExperienceID,
    ea.Code,
    ea.[Name],
    ea.[Description],
    CAST(ISNULL(ea.UnitPrice, 0) AS decimal(18,2)) AS UnitPrice,
    CAST(ISNULL(ea.AppliesPerParticipant, 0) AS bit) AS AppliesPerParticipant,
    ISNULL(ea.TaxMode, 'TaxableExclusive') AS TaxMode,
    CAST(ISNULL(ea.IsPublic, 0) AS bit) AS IsPublic,
    CAST(ISNULL(ea.IsActive, 0) AS bit) AS IsActive,
    ea.DisplayOrder
FROM dbo.ExperienceAddOn ea
INNER JOIN dbo.Experience e
    ON e.ExperienceID = ea.ExperienceID
WHERE e.IsActive = 1
  AND e.IsPublic = 1
  AND ea.IsActive = 1
  AND ea.IsPublic = 1
ORDER BY ea.ExperienceID, ea.DisplayOrder, ea.[Name];
`);

  const experiences = result.recordsets?.[0] || [];
  const packagesByExperience = groupBy(result.recordsets?.[1] || [], "ExperienceID");
  const addOnsByExperience = groupBy(result.recordsets?.[2] || [], "ExperienceID");

  return experiences
    .map((row) => ({
      code: stringValue(row.Code),
      name: stringValue(row.Name),
      description: stringValue(row.Description),
      category: stringValue(row.Category),
      providerName: stringValue(row.ProviderName),
      seasonStart: dateOnly(row.SeasonStart),
      seasonEnd: dateOnly(row.SeasonEnd),
      minimumParticipants: numberValue(row.MinimumParticipants),
      maximumParticipants: numberValue(row.MaximumParticipants),
      packages: (packagesByExperience.get(row.ExperienceID) || []).map((item) => ({
        code: stringValue(item.Code),
        name: stringValue(item.Name),
        description: stringValue(item.Description),
        includes: stringValue(item.Includes),
        unitPrice: moneyValue(item.UnitPrice),
        taxMode: stringValue(item.TaxMode)
      })),
      addOns: (addOnsByExperience.get(row.ExperienceID) || []).map((item) => ({
        code: stringValue(item.Code),
        name: stringValue(item.Name),
        description: stringValue(item.Description),
        unitPrice: moneyValue(item.UnitPrice),
        appliesPerParticipant: Boolean(item.AppliesPerParticipant),
        taxMode: stringValue(item.TaxMode)
      }))
    }))
    .filter((experience) => experience.packages.length > 0);
}

function buildIntelligence({ brandContext, week, rfc, dbConfig, salud, publicExperiences, upcomingExperiences }) {
  const selectedPeriod = salud.executiveIndicators.find((row) => row.sortOrder === 1) || null;
  const previousPeriod = salud.executiveIndicators.find((row) => row.sortOrder === 2) || null;
  const samePeriodPreviousYear = salud.executiveIndicators.find((row) => row.sortOrder === 3) || null;
  const selectedFinancialBreakdown = salud.financialBreakdown.find((row) => row.sortOrder === 1) || null;
  const selectedCashFlow = salud.cashFlow.find((row) => row.sortOrder === 1) || null;
  const selectedSuites = salud.suitePerformance
    .filter((row) => row.sortOrder === 1)
    .sort((a, b) => (b.roomRevenue || 0) - (a.roomRevenue || 0));
  const targetPct = brandContext.brand.strategy?.primaryKpi?.targetPct ?? 50;
  const currentOccupancy = selectedPeriod?.occupancyPct ?? null;

  return {
    generatedAtUtc: new Date().toISOString(),
    brand: {
      id: brandContext.brand.id,
      name: brandContext.brand.name,
      publicBaseUrl: brandContext.brand.publicBaseUrl
    },
    privacy: {
      aggregateOnly: true,
      excludesCustomerPii: true,
      excludesCredentials: true,
      excludesConnectionStrings: true
    },
    source: {
      officialUi: brandContext.brand.strategy?.financialSource?.officialUi,
      officialService: brandContext.brand.strategy?.financialSource?.officialService,
      storedProcedure: brandContext.brand.strategy?.financialSource?.storedProcedure,
      publicExperienceService: brandContext.brand.strategy?.experienceSource?.officialService,
      databaseScope: classifyDatabase(dbConfig.database),
      rfc,
      financialGranularity: week.financialScope.granularity
    },
    week,
    goals: {
      primaryKpi: brandContext.brand.strategy?.primaryKpi,
      occupancyTargetPct: targetPct,
      occupancyGapPctPoints: currentOccupancy === null ? null : round(targetPct - currentOccupancy, 2)
    },
    audiencePriority: brandContext.brand.strategy?.audiencePriority || [],
    platforms: brandContext.brand.strategy?.platformPriority || [],
    knownDemandSignals: brandContext.brand.strategy?.knownDemandSignals || [],
    saludFinanciera: {
      occupancy: {
        currentPct: currentOccupancy,
        previousPct: previousPeriod?.occupancyPct ?? null,
        samePeriodPreviousYearPct: samePeriodPreviousYear?.occupancyPct ?? null,
        targetPct,
        gapPctPoints: currentOccupancy === null ? null : round(targetPct - currentOccupancy, 2),
        rentableSuites: selectedPeriod?.rentableSuites ?? null,
        availableNights: selectedPeriod?.availableNights ?? null,
        occupiedNights: selectedPeriod?.occupiedNights ?? null
      },
      revenue: selectedPeriod ? {
        roomRevenue: selectedPeriod.roomRevenue,
        adr: selectedPeriod.adr,
        revpar: selectedPeriod.revpar,
        reservationCount: selectedPeriod.reservationCount,
        reservationTotal: selectedPeriod.reservationTotal,
        postedCollections: selectedPeriod.postedCollections,
        collectionPct: selectedPeriod.collectionPct,
        outstandingCollections: selectedPeriod.outstandingCollections
      } : null,
      financialBreakdown: selectedFinancialBreakdown,
      cashFlow: selectedCashFlow,
      suitePerformance: selectedSuites,
      dataQuality: salud.dataQuality
        .filter((row) => row.sortOrder === 1)
        .map((row) => ({
          severity: row.severity,
          checkType: row.checkType,
          itemCount: row.itemCount,
          metricAmount: row.metricAmount,
          referenceAmount: row.referenceAmount,
          netEffect: row.netEffect
        }))
    },
    publicExperiences,
    upcomingExperiences,
    recommendedNextInputs: [
      "Search Google for Calpulalpan events and business travel demand for the requested week.",
      "Review Facebook and Instagram local event pages before committing the creative calendar.",
      "Confirm any paid campaign budget, promotion rules, and direct-booking offers before generation."
    ]
  };
}

function buildResearchChecklist(intelligence) {
  const experienceLines = intelligence.upcomingExperiences.length === 0
    ? "- No public Bonhomia experiences were found in the next 90 days."
    : intelligence.upcomingExperiences
      .slice(0, 8)
      .map((experience) => `- ${experience.name} (${experience.seasonStart || "sin inicio"} to ${experience.seasonEnd || "sin fin"})`)
      .join("\n");

  return `# Marketing Research Checklist

Brand: ${intelligence.brand.name}
Week: ${intelligence.week.id} (${intelligence.week.startDate} to ${intelligence.week.endDate})

## Data Already Exported

- Salud Financiera aggregate metrics from ${intelligence.source.officialService}.
- Public Bonhomia experiences from ${intelligence.source.publicExperienceService}.
- Occupancy gap to ${intelligence.goals.occupancyTargetPct}% target.

## Public Experiences To Consider

${experienceLines}

## Manual Research Before Publishing

- Google: search "Calpulalpan eventos esta semana", "Feria Calpulalpan", and "empresas cerca de Calpulalpan hospedaje".
- Facebook: check municipal, Feria, tourism, university, and company event pages.
- Instagram: check local hashtags and recent venue posts.
- Google Search intent: check whether demand looks business, tourism, family, couples, or event-driven.
- Confirm any offer or discount with operations before putting it in copy.

## Privacy Reminder

Use aggregate business metrics only. Do not paste customer names, reservation records, payment details, SQL credentials, or private account data into marketing artifacts.
`;
}

function mapExecutiveIndicator(row) {
  return {
    sortOrder: numberValue(row.SortOrder),
    periodLabel: stringValue(row.PeriodLabel),
    periodScope: stringValue(row.PeriodScope),
    periodStart: dateOnly(row.PeriodStart),
    periodEnd: dateOnly(row.PeriodEnd),
    rentableSuites: numberValue(row.RentableSuites),
    availableNights: numberValue(row.AvailableNights),
    occupiedNights: numberValue(row.OccupiedNights),
    occupancyPct: pctValue(row.OccupancyPct),
    roomRevenue: moneyValue(row.RoomRevenue),
    adr: moneyValue(row.ADR),
    revpar: moneyValue(row.RevPAR),
    reservationCount: numberValue(row.ReservationCount),
    reservationTotal: moneyValue(row.ReservationTotal),
    postedCollections: moneyValue(row.PostedCollections),
    collectionPct: pctValue(row.CollectionPct),
    outstandingCollections: moneyValue(row.OutstandingCollections),
    netAccountingIncome: moneyValue(row.NetAccountingIncome),
    operatingExpenses: moneyValue(row.OperatingExpenses),
    normalizedOperatingResult: moneyValue(row.NormalizedOperatingResult),
    netResult: moneyValue(row.NetResult),
    operatingMarginPct: pctValue(row.OperatingMarginPct),
    netMarginPct: pctValue(row.NetMarginPct),
    cashIn: moneyValue(row.CashIn),
    cashOut: moneyValue(row.CashOut),
    netCashflow: moneyValue(row.NetCashflow)
  };
}

function mapSuitePerformance(row) {
  return {
    sortOrder: numberValue(row.SortOrder),
    periodLabel: stringValue(row.PeriodLabel),
    periodScope: stringValue(row.PeriodScope),
    roomName: stringValue(row.RoomName),
    basePrice: moneyValue(row.BasePrice),
    availableNights: numberValue(row.AvailableNights),
    occupiedNights: numberValue(row.OccupiedNights),
    occupancyPct: pctValue(row.OccupancyPct),
    roomRevenue: moneyValue(row.RoomRevenue),
    adr: moneyValue(row.ADR),
    revpar: moneyValue(row.RevPAR)
  };
}

function mapFinancialBreakdown(row) {
  return {
    sortOrder: numberValue(row.SortOrder),
    periodLabel: stringValue(row.PeriodLabel),
    periodScope: stringValue(row.PeriodScope),
    periodStart: dateOnly(row.PeriodStart),
    periodEnd: dateOnly(row.PeriodEnd),
    grossIncome: moneyValue(row.GrossIncome401403 ?? row.GrossIncome401),
    salesReturns: moneyValue(row.SalesReturns402),
    netAccountingIncome: moneyValue(row.NetAccountingIncome),
    costOfSales: moneyValue(row.CostOfSales501504),
    grossProfit: moneyValue(row.GrossProfit),
    grossMarginPct: pctValue(row.GrossMarginPct),
    operatingExpenses: moneyValue(row.OperatingExpenses602605),
    financialExpenses: moneyValue(row.FinancialExpenses701),
    otherNet: moneyValue(row.OtherNet),
    taxes: moneyValue(row.Taxes611),
    normalizedOperatingResult: moneyValue(row.NormalizedOperatingResult),
    netResult: moneyValue(row.NetResult),
    operatingMarginPct: pctValue(row.OperatingMarginPct),
    netMarginPct: pctValue(row.NetMarginPct),
    pendingBankNetExcluded: moneyValue(row.PendingBankNetExcluded)
  };
}

function mapCashFlow(row) {
  return {
    sortOrder: numberValue(row.SortOrder),
    periodLabel: stringValue(row.PeriodLabel),
    periodScope: stringValue(row.PeriodScope),
    periodStart: dateOnly(row.PeriodStart),
    periodEnd: dateOnly(row.PeriodEnd),
    cashTransactionCount: numberValue(row.CashTransactionCount),
    openingCashBalance: moneyValue(row.OpeningCashBalance),
    cashIn: moneyValue(row.CashIn),
    cashOut: moneyValue(row.CashOut),
    netCashflow: moneyValue(row.NetCashflow),
    closingCashBalance: moneyValue(row.ClosingCashBalance)
  };
}

function mapDataQuality(row) {
  return {
    sortOrder: numberValue(row.SortOrder),
    periodLabel: stringValue(row.PeriodLabel),
    periodScope: stringValue(row.PeriodScope),
    severity: stringValue(row.Severity),
    checkType: stringValue(row.CheckType),
    itemCount: numberValue(row.ItemCount),
    metricAmount: moneyValue(row.MetricAmount),
    referenceAmount: moneyValue(row.ReferenceAmount),
    netEffect: moneyValue(row.NetEffect)
  };
}

function parseAdoConnectionString(value) {
  const parts = new Map();
  for (const segment of value.split(";")) {
    const [rawKey, ...rest] = segment.split("=");
    if (!rawKey || rest.length === 0) {
      continue;
    }

    parts.set(normalizeKey(rawKey), rest.join("=").trim());
  }

  const serverValue = getPart(parts, "server", "data source", "addr", "address", "network address");
  if (!serverValue) {
    throw new Error("OrionDb connection string is missing Server/Data Source.");
  }

  const { server, port } = parseServer(serverValue);
  const database = getPart(parts, "database", "initial catalog");
  const user = getPart(parts, "user id", "userid", "uid", "user");
  const password = getPart(parts, "password", "pwd");

  if (!database) {
    throw new Error("OrionDb connection string is missing Database/Initial Catalog.");
  }

  if (!user || !password) {
    throw new Error("OrionDb connection string must include SQL user and password for the marketing exporter.");
  }

  return {
    server,
    port,
    database,
    user,
    password,
    options: {
      encrypt: truthy(getPart(parts, "encrypt"), true),
      trustServerCertificate: truthy(getPart(parts, "trustservercertificate"), true)
    },
    connectionTimeout: 30000,
    requestTimeout: 60000
  };
}

function parseServer(value) {
  const cleaned = value.replace(/^tcp:/iu, "").trim();
  const [server, portText] = cleaned.split(",");
  return {
    server,
    port: portText ? Number(portText) : 1433
  };
}

function normalizeKey(value) {
  return value.trim().toLowerCase().replace(/\s+/gu, " ");
}

function getPart(parts, ...keys) {
  for (const key of keys) {
    if (parts.has(normalizeKey(key))) {
      return parts.get(normalizeKey(key));
    }
  }

  return null;
}

function truthy(value, defaultValue = false) {
  if (value === null || value === undefined || value === "") {
    return defaultValue;
  }

  return /^(true|yes|1)$/iu.test(String(value).trim());
}

function groupBy(rows, key) {
  const map = new Map();
  for (const row of rows) {
    const value = row[key];
    if (!map.has(value)) {
      map.set(value, []);
    }

    map.get(value).push(row);
  }

  return map;
}

function stringValue(value) {
  return value === null || value === undefined ? "" : String(value).trim();
}

function numberValue(value) {
  if (value === null || value === undefined || value === "") {
    return null;
  }

  const number = Number(value);
  return Number.isFinite(number) ? number : null;
}

function moneyValue(value) {
  const number = numberValue(value);
  return number === null ? null : round(number, 2);
}

function pctValue(value) {
  const number = numberValue(value);
  return number === null ? null : round(number, 2);
}

function dateOnly(value) {
  if (!value) {
    return null;
  }

  if (value instanceof Date) {
    return formatDate(value);
  }

  return String(value).slice(0, 10);
}

function round(value, digits) {
  const factor = 10 ** digits;
  return Math.round((Number(value) + Number.EPSILON) * factor) / factor;
}

function classifyDatabase(database) {
  const normalized = String(database || "").trim().toLowerCase();
  if (normalized === "grupocarpio") {
    return "production-aggregate";
  }

  if (normalized === "orion_sandbox") {
    return "sandbox-aggregate";
  }

  return "configured-aggregate";
}

if (isDirectRun(import.meta.url)) {
  collectMarketingIntelligence()
    .then(({ outputRoot, intelligence }) => {
      console.log(`Marketing intelligence written to ${path.join(outputRoot, outputFileName)}`);
      console.log(`Occupancy: ${intelligence.saludFinanciera.occupancy.currentPct ?? "n/a"}% / target ${intelligence.goals.occupancyTargetPct}%`);
    })
    .catch((error) => {
      console.error(error.message);
      process.exit(1);
    });
}
