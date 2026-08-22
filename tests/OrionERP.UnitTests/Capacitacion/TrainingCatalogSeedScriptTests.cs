using System.Text.RegularExpressions;

namespace OrionERP.UnitTests.Capacitacion;

/// <summary>
/// The catalog seed restores the reference and synthetic data that the
/// sanitizer erases. Three files have to agree about it -- the sanitizer's
/// preserved-catalog clamp, the seed itself, and the attestation manifest --
/// and nothing but a test keeps them from drifting apart. A drift here does not
/// fail loudly: it either widens what survives a production clone, or leaves a
/// seeded table outside the attestation allowlist so the next reset aborts.
/// </summary>
public sealed class TrainingCatalogSeedScriptTests
{
  private static readonly string CatalogSeedSql = ReadRepoFile(
    "src/OrionERP.Infrastructure/Features/Capacitacion/Sql/20260821_orion_training_catalogos.sql");
  private static readonly string SanitizeSql = ReadRepoFile(
    "src/OrionERP.Infrastructure/Features/Capacitacion/Sql/20260817_orion_training_sanitize.sql");
  private static readonly string AttestSql = ReadRepoFile(
    "src/OrionERP.Infrastructure/Features/Capacitacion/Sql/20260817_orion_training_attest.sql");
  private static readonly string ProvisionSql = ReadRepoFile(
    "src/OrionERP.Infrastructure/Features/Capacitacion/Sql/20260817_orion_training_provision.sql");
  private static readonly string PowerShell = ReadRepoFile("Sanitize-OrionTraining.ps1");
  private static readonly string StandaloneSeedPowerShell = ReadRepoFile("Seed-OrionTrainingCatalogos.ps1");

  [Fact]
  public void CatalogSeed_GuardsExactCatalogAndSessionBeforeAnyWrite()
  {
    var catalogGuard = CatalogSeedSql.IndexOf(
      "DB_NAME() COLLATE Latin1_General_100_BIN2 <> N'Orion_Training'",
      StringComparison.Ordinal);
    var sessionGuard = CatalogSeedSql.IndexOf(
      "SESSION_CONTEXT(N'OrionTrainingSanitizerApply')",
      StringComparison.Ordinal);
    var firstWrite = FirstWriteIndex(CatalogSeedSql);

    Assert.True(catalogGuard >= 0, "The seed must refuse to run outside Orion_Training.");
    Assert.True(sessionGuard > catalogGuard);
    Assert.True(firstWrite > sessionGuard, "No row may be written before both guards have run.");
  }

  [Fact]
  public void CatalogSeed_RejectsAMissingSessionContextInsteadOfAcceptingSqlNull()
  {
    var guardLines = CatalogSeedSql.Split('\n')
      .Where(line => line.Contains("SESSION_CONTEXT(", StringComparison.Ordinal))
      .ToArray();

    Assert.NotEmpty(guardLines);
    Assert.All(guardLines, line => Assert.Contains(
      "ISNULL(TRY_CONVERT(nvarchar(64), SESSION_CONTEXT(",
      line,
      StringComparison.Ordinal));
  }

  [Fact]
  public void CatalogSeed_AvoidsConstructsThatWouldBreakTheIdentityOrTraineeContract()
  {
    // An explicit identity write or a wholesale empty breaks the attestation's
    // counter check (51839). A source-driven MERGE delete would erase catalog
    // rows a trainee added through /ajustes/catalogos.
    Assert.DoesNotContain("IDENTITY_INSERT", CatalogSeedSql, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("TRUNCATE TABLE", CatalogSeedSql, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("NOT MATCHED BY SOURCE", CatalogSeedSql, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("DROP TABLE", CatalogSeedSql, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("USE [", CatalogSeedSql, StringComparison.OrdinalIgnoreCase);

    // Get-SqlBatches splits on GO, which would break the single-transaction
    // contract this script depends on.
    Assert.DoesNotContain("\nGO", CatalogSeedSql, StringComparison.Ordinal);
  }

  [Fact]
  public void CatalogSeed_CannotIssueAPositiveDataAttestation()
  {
    Assert.DoesNotContain("DatosSanitizados = 1", CatalogSeedSql, StringComparison.Ordinal);
    Assert.DoesNotContain("DatosSinteticos = 1", CatalogSeedSql, StringComparison.Ordinal);
  }

  [Fact]
  public void CatalogSeed_UsesRecognizableSyntheticMarkers()
  {
    Assert.Contains("XAXX010101000", CatalogSeedSql, StringComparison.Ordinal);
    Assert.Contains("DATOS SINTÉTICOS", CatalogSeedSql, StringComparison.Ordinal);
    Assert.Contains("FICTICI", CatalogSeedSql, StringComparison.Ordinal);
    Assert.Contains("TRN-", CatalogSeedSql, StringComparison.Ordinal);
  }

  /// <summary>
  /// The preserved reference catalog is the single place where a row cloned from
  /// production may survive the erase. Three files bound it, and they must bound
  /// it identically: the sanitizer's clamp deletes anything outside the list, the
  /// seed restates the list, and the attestation verifies it at the end of the
  /// run. If they ever disagree, the widest of the three silently wins.
  /// </summary>
  [Fact]
  public void CanonicalSatPaymentClaves_AreIdenticalInSanitizerSeedAndAttestation()
  {
    var inSanitizer = ExtractClaveManifest(SanitizeSql, "SANITIZATION BLOCKED: a preserved reference catalog row");
    var inSeed = ExtractClaveManifest(CatalogSeedSql, "CATALOG SEED FAILED: dbo.Formas_Pago does not match");
    var inAttestation = ExtractClaveManifest(AttestSql, "ATTESTATION BLOCKED: the preserved reference catalog");

    Assert.Equal(22, inSanitizer.Count);
    Assert.Equal(inSanitizer, inSeed);
    Assert.Equal(inSanitizer, inAttestation);

    // The published SAT c_FormaPago key set. Spelled out so a silent edit to all
    // three lists at once still fails.
    Assert.Equal(
      new[]
      {
        "01", "02", "03", "04", "05", "06", "08", "12", "13", "14", "15",
        "17", "23", "24", "25", "26", "27", "28", "29", "30", "31", "99"
      },
      inSanitizer.OrderBy(clave => clave, StringComparer.Ordinal).ToArray());
  }

  /// <summary>
  /// The attestation sweeps every user table and throws 51753 when one outside
  /// the manifest holds rows. A table the seed writes to but the manifest omits
  /// therefore aborts the reset -- after the erase has already happened. This
  /// test is what makes that contract self-checking.
  /// </summary>
  [Fact]
  public void EveryTableTheCatalogSeedWritesTo_IsAllowedToBeNonEmptyByTheAttestation()
  {
    var written = ExtractWrittenTables(CatalogSeedSql);
    var allowed = ExtractAttestationManifest(AttestSql);

    Assert.NotEmpty(written);
    var missing = written.Except(allowed, StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal).ToArray();

    Assert.True(
      missing.Length == 0,
      "These tables are seeded but are not in @AllowedNonEmpty, so the attestation will throw 51753: "
        + string.Join(", ", missing));
  }

  /// <summary>
  /// Provisioning throws 51726 if any table holds rows after sanitization, with
  /// its own hard-coded exemption list. A table the sanitizer preserves but that
  /// guard does not exempt kills the reset immediately after a successful erase.
  /// </summary>
  [Fact]
  public void EveryPreservedTable_IsExemptedFromTheProvisioningEmptinessGuard()
  {
    var preserved = Regex.Matches(SanitizeSql, @"OBJECT_ID\(N'dbo\.(?<table>[A-Za-z_]+)'\)\)")
      .Select(match => match.Groups["table"].Value)
      .ToHashSet(StringComparer.Ordinal);

    Assert.Contains("__EFMigrationsHistory", preserved);
    Assert.Contains("DateDimension", preserved);
    Assert.Contains("Formas_Pago", preserved);

    var exemption = Regex.Match(
      ProvisionSql,
      @"schemaInfo\.name = N'dbo' AND tableInfo\.name IN \((?<list>[^)]*)\)");
    Assert.True(exemption.Success, "The 51726 exemption list was not found in the provisioning script.");

    foreach (var table in preserved)
    {
      Assert.Contains($"N'{table}'", exemption.Groups["list"].Value, StringComparison.Ordinal);
    }
  }

  /// <summary>
  /// dbo.SatRfcProfile is the one table that can hold a real FIEL certificate and
  /// its encrypted password. It is never preserved from a clone, and the row the
  /// seed writes must carry no credential at all.
  /// </summary>
  [Fact]
  public void SatRfcProfile_IsSeededWithoutAnyFielCredential()
  {
    Assert.DoesNotContain("SatRfcProfile", PreservedBlock(SanitizeSql), StringComparison.Ordinal);

    Assert.Contains(
      "THROW 51931, 'CATALOG SEED FAILED: a SAT FIEL credential is present",
      CatalogSeedSql,
      StringComparison.Ordinal);

    foreach (var column in new[] { "SATFielCertificate", "SATFielKey", "SATFielPfx", "SATFielPasswordEnc" })
    {
      Assert.Contains($"{column} IS NOT NULL", CatalogSeedSql, StringComparison.Ordinal);
      Assert.Contains($"{column} IS NOT NULL", AttestSql, StringComparison.Ordinal);
    }
  }

  /// <summary>
  /// The seed depends on provisioning and the scenarios for its parent rows, and
  /// the attestation requires every statistic to postdate the reset with a zero
  /// modification counter. That leaves exactly one valid slot for it.
  /// </summary>
  [Fact]
  public void Orchestrator_RunsTheCatalogSeedAfterScenariosAndBeforeTheStatisticsRebuild()
  {
    var scenarios = PowerShell.IndexOf("-Path $scenarioScript", StringComparison.Ordinal);
    var catalogSeed = PowerShell.IndexOf("-Path $catalogSeedScript", StringComparison.Ordinal);
    var statistics = PowerShell.IndexOf("Update-TrainingStatisticsFullScan -Connection $connection", StringComparison.Ordinal);
    var attest = PowerShell.IndexOf("-Path $attestScript", StringComparison.Ordinal);

    Assert.True(scenarios >= 0);
    Assert.True(catalogSeed > scenarios, "The catalog seed needs the rows provisioning and the scenarios create.");
    Assert.True(statistics > catalogSeed, "Seeding after the FULLSCAN fails attestation guard 51846.");
    Assert.True(attest > statistics);
  }

  [Fact]
  public void StandaloneSeedLauncher_IsPinnedToTrainingAndPreviewsByDefault()
  {
    Assert.Contains("[ValidateSet('Orion_Training')]", StandaloneSeedPowerShell, StringComparison.Ordinal);
    Assert.Contains("OrionTrainingCatalogSeedApply", StandaloneSeedPowerShell, StringComparison.Ordinal);
    Assert.Contains("Encrypt=True", StandaloneSeedPowerShell, StringComparison.Ordinal);

    // Applying must be a deliberate, named act.
    Assert.Contains("-Apply requires -ConfirmDatabase", StandaloneSeedPowerShell, StringComparison.Ordinal);

    // What keeps this off production is the catalog pin, not a database-access
    // probe: the maintenance connection is sysadmin by requirement and can
    // therefore reach every database on the instance.
    Assert.Contains("Initial Catalog must be exactly", StandaloneSeedPowerShell, StringComparison.Ordinal);
    Assert.Contains(
      "DB_NAME() COLLATE Latin1_General_100_BIN2 <> N'Orion_Training'",
      StandaloneSeedPowerShell,
      StringComparison.Ordinal);

    // The repair path must never try to re-issue the attestation.
    Assert.DoesNotContain("DatosSanitizados", StandaloneSeedPowerShell, StringComparison.Ordinal);
    Assert.DoesNotContain("attestScript", StandaloneSeedPowerShell, StringComparison.Ordinal);
  }

  private static int FirstWriteIndex(string sql)
  {
    var candidates = new[] { "\n  MERGE ", "\n  INSERT ", "\n  UPDATE ", "\n  DELETE " }
      .Select(token => sql.IndexOf(token, StringComparison.Ordinal))
      .Where(index => index >= 0)
      .ToArray();

    Assert.NotEmpty(candidates);
    return candidates.Min();
  }

  private static string PreservedBlock(string sanitizeSql)
  {
    var start = sanitizeSql.IndexOf("INSERT @Preserved (ObjectId)", StringComparison.Ordinal);
    Assert.True(start >= 0, "The @Preserved list was not found in the sanitizer.");
    var end = sanitizeSql.IndexOf(';', start);
    return sanitizeSql[start..end];
  }

  /// <summary>
  /// Pulls the SAT clave list out of the IN (...) clause that precedes the given
  /// error message, so each file is read the way SQL Server reads it rather than
  /// by trusting a hand-maintained copy.
  /// </summary>
  private static HashSet<string> ExtractClaveManifest(string sql, string trailingMessageFragment)
  {
    var anchor = sql.IndexOf(trailingMessageFragment, StringComparison.Ordinal);
    Assert.True(anchor >= 0, $"Could not find the manifest anchored by: {trailingMessageFragment}");

    var listEnd = sql.LastIndexOf("N'99')", anchor, StringComparison.Ordinal);
    Assert.True(listEnd >= 0, $"Could not find the end of the clave list before: {trailingMessageFragment}");

    var listStart = sql.LastIndexOf("(N'01'", listEnd, StringComparison.Ordinal);
    Assert.True(listStart >= 0, $"Could not find the start of the clave list before: {trailingMessageFragment}");

    return Regex.Matches(sql[listStart..(listEnd + "N'99')".Length)], @"N'(?<clave>\d{2})'")
      .Select(match => match.Groups["clave"].Value)
      .ToHashSet(StringComparer.Ordinal);
  }

  private static HashSet<string> ExtractWrittenTables(string seedSql)
  {
    // Only real statement targets: a schema-qualified name after INSERT, MERGE or
    // UPDATE. Table variables (@Name) are excluded by the schema requirement.
    return Regex.Matches(
        seedSql,
        @"(?:INSERT|MERGE|UPDATE)\s+(?<schema>dbo|bancos|logistica|rh|restaurante|capacitacion|auth)\.(?<table>[A-Za-z_]+)")
      .Select(match => $"{match.Groups["schema"].Value}.{match.Groups["table"].Value}")
      .ToHashSet(StringComparer.Ordinal);
  }

  private static HashSet<string> ExtractAttestationManifest(string attestSql)
  {
    var start = attestSql.IndexOf("INSERT @AllowedNonEmpty (SchemaName, TableName)", StringComparison.Ordinal);
    Assert.True(start >= 0, "The @AllowedNonEmpty manifest was not found in the attestation script.");
    var end = attestSql.IndexOf("DECLARE @QualifiedName", start, StringComparison.Ordinal);
    Assert.True(end > start);

    return Regex.Matches(attestSql[start..end], @"\(N'(?<schema>[A-Za-z_]+)',\s*N'(?<table>[A-Za-z_]+)'\)")
      .Select(match => $"{match.Groups["schema"].Value}.{match.Groups["table"].Value}")
      .ToHashSet(StringComparer.Ordinal);
  }

  private static string ReadRepoFile(string relativePath)
  {
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null)
    {
      var candidate = Path.Combine(directory.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
      if (File.Exists(candidate)) return File.ReadAllText(candidate);
      directory = directory.Parent;
    }

    throw new FileNotFoundException($"No se encontró {relativePath} desde {AppContext.BaseDirectory}.");
  }
}
