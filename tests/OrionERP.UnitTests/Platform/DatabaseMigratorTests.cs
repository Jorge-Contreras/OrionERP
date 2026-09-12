using System.Text.Json;
using System.Text.RegularExpressions;
using OrionERP.DatabaseMigrator;
using OrionERP.UnitTests.Common;

namespace OrionERP.UnitTests.Platform;

public sealed class SqlBatchSplitterTests
{
  [Fact]
  public void Split_UsesOnlyStandaloneGoLinesAsSeparators()
  {
    var script = """
      SELECT N'GO' AS Literal;
      -- GO inside a comment is not a separator.
      GO -- separator
      SELECT 2 AS Value;
      go
      """;

    var batches = SqlBatchSplitter.Split(script);

    Assert.Equal(2, batches.Count);
    Assert.Contains("SELECT N'GO'", batches[0], StringComparison.Ordinal);
    Assert.Contains("-- GO inside a comment", batches[0], StringComparison.Ordinal);
    Assert.Equal("SELECT 2 AS Value;", batches[1]);
  }

  [Fact]
  public void Split_HonorsSqlCmdGoRepeatCounts()
  {
    var batches = SqlBatchSplitter.Split("SELECT 1;\r\nGO 3\r\nSELECT 2;");

    Assert.Equal(["SELECT 1;", "SELECT 1;", "SELECT 1;", "SELECT 2;"], batches);
  }

  [Theory]
  [InlineData(0)]
  [InlineData(101)]
  public void Split_RejectsUnsafeGoRepeatCounts(int repeat)
  {
    var error = Assert.Throws<InvalidOperationException>(
      () => SqlBatchSplitter.Split($"SELECT 1;{Environment.NewLine}GO {repeat}"));

    Assert.Contains("entre 1 y 100", error.Message, StringComparison.Ordinal);
  }
}

public sealed class SqlInventoryTests
{
  [Fact]
  public void LegacyLiteralDetection_DoesNotConfuseASeedAliasWithTenantData()
  {
    Assert.False(SqlInventory.ContainsLegacyTenantLiteral(
      "SELECT seed.ModuleCode FROM (VALUES ('ACCOUNTING_CORE')) seed(ModuleCode);"));
    Assert.True(SqlInventory.ContainsLegacyTenantLiteral(
      "SELECT * FROM orion.Company WHERE Rfc = 'SEED';"));
  }
}

public sealed class DatabaseMigratorCommandLineTests
{
  [Fact]
  public void Parse_UsesSafeInventoryDefaults()
  {
    var repositoryRoot = FindRepositoryRoot();

    var options = CommandLine.Parse(
      ["--mode", "inventory", "--repository-root", repositoryRoot]);

    Assert.Equal(MigrationMode.Inventory, options.Mode);
    Assert.Null(options.Database);
    Assert.Equal(
      Path.Combine(repositoryRoot, "database", "orion-migrations.json"),
      options.ManifestPath);
    Assert.Equal("ASPNETCORE_ConnectionStrings__OrionDb", options.ConnectionEnvironmentVariable);
    Assert.Equal(300, options.CommandTimeoutSeconds);
  }

  [Fact]
  public void Parse_RequiresDatabaseForEveryConnectedMode()
  {
    var repositoryRoot = FindRepositoryRoot();

    foreach (var mode in new[] { "plan", "preview", "apply", "verify" })
    {
      var error = Assert.Throws<ArgumentException>(() => CommandLine.Parse(
        ["--mode", mode, "--repository-root", repositoryRoot]));
      Assert.Contains("--database", error.Message, StringComparison.Ordinal);
    }
  }

  [Fact]
  public void Parse_RejectsAConnectionStringArgument()
  {
    var repositoryRoot = FindRepositoryRoot();

    var error = Assert.Throws<ArgumentException>(() => CommandLine.Parse(
    [
      "--mode", "inventory",
      "--repository-root", repositoryRoot,
      "--connection-string", "Server=localhost;Database=Orion_Sandbox;Integrated Security=true;"
    ]));

    Assert.Contains("connection-string", error.Message, StringComparison.OrdinalIgnoreCase);
  }

  private static string FindRepositoryRoot()
  {
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "OrionERP.sln")))
      directory = directory.Parent;

    return directory?.FullName
      ?? throw new InvalidOperationException("No se encontró la raíz del repositorio.");
  }
}

public sealed class DatabaseMigrationManifestTests
{
  private static readonly string RepositoryRoot = FindRepositoryRoot();
  private static readonly MigrationManifest Manifest = JsonSerializer.Deserialize<MigrationManifest>(
    RepoFile.Read("database/orion-migrations.json"),
    JsonOptions.Indented) ?? throw new InvalidOperationException("El manifiesto está vacío.");

  [Fact]
  public void Manifest_HasUniqueSafeScriptsAndAnExactDatabaseAllowlist()
  {
    Assert.Equal(1, Manifest.Version);
    Assert.NotEmpty(Manifest.Migrations);
    Assert.Equal(
      Manifest.Migrations.Count,
      Manifest.Migrations.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count());
    Assert.Equal(
      Manifest.Migrations.Count,
      Manifest.Migrations.Select(item => item.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count());

    foreach (var migration in Manifest.Migrations)
    {
      Assert.False(string.IsNullOrWhiteSpace(migration.Id));
      Assert.False(Path.IsPathRooted(migration.Path));

      var scriptPath = Path.GetFullPath(Path.Combine(RepositoryRoot, migration.Path));
      var relativePath = Path.GetRelativePath(RepositoryRoot, scriptPath);
      Assert.False(relativePath.StartsWith("..", StringComparison.Ordinal));
      Assert.False(Path.IsPathRooted(relativePath));
      Assert.True(File.Exists(scriptPath), $"No existe el script declarado: {migration.Path}");

      Assert.NotEmpty(migration.AllowedDatabases);
      Assert.All(migration.AllowedDatabases, database =>
        Assert.Contains(database, new[] { "Orion_Sandbox", "grupocarpio" }));
    }

    var foundation = Manifest.Migrations.Single(item => item.Id == "20260901_platform_foundation");
    Assert.Equal(
      ["Orion_Sandbox", "grupocarpio"],
      foundation.AllowedDatabases.Order(StringComparer.Ordinal).ToArray());
    var sandboxProvisioning = Manifest.Migrations.Single(item => item.Id == "20260902_platform_sandbox_bonhomia_bruno");
    Assert.Equal(["Orion_Sandbox"], sandboxProvisioning.AllowedDatabases);

    foreach (var sandboxOnlyId in new[]
    {
      "20260903_hospitality_public_scope_sandbox",
      "20260903_restaurant_public_identity_scope_sandbox",
      "20260904_public_site_presentation_transition_sandbox",
      "20260905_hospitality_legal_consent_sandbox",
      "20260908_hospitality_administration_scope_sandbox",
      "20260908_accounting_company_identity_sandbox",
      "20260908_accounting_cycle_sandbox",
      "20260908_accounting_outbox_sandbox",
      "20260908_published_reports_sandbox",
      "20260909_accounting_cycle_activation_sandbox",
      "20260909_bsu_opening_balance_counterpart_sandbox",
      "20260909_workforce_attendance_scope_sandbox",
      "20260909_calendar_owner_scope_sandbox",
      "20260909_hospitality_legacy_mechanisms_sandbox",
      "20260909_hospitality_legacy_transaction_guards_sandbox",
      "20260909_hospitality_payment_policy_switch_sandbox",
      "20260909_hospitality_payment_link_removal",
      "20260909_accounting_rls_scope_sandbox",
      "20260909_inventory_core_rls_scope_sandbox",
      "20260909_fiscal_rls_scope_sandbox"
    })
    {
      var sandboxOnlyMigration = Manifest.Migrations.Single(item => item.Id == sandboxOnlyId);
      Assert.Equal(["Orion_Sandbox"], sandboxOnlyMigration.AllowedDatabases);
    }
  }

  [Fact]
  public void Manifest_DeclaresChecksumVerifiedReplacementsForRestoredSandboxHistory()
  {
    var productionManifest = JsonSerializer.Deserialize<MigrationManifest>(
      RepoFile.Read("database/orion-production-migrations.json"),
      JsonOptions.Indented) ?? throw new InvalidOperationException("El manifiesto productivo está vacío.");
    var productionById = productionManifest.Migrations.ToDictionary(item => item.Id, StringComparer.Ordinal);
    var expected = new Dictionary<string, string>(StringComparer.Ordinal)
    {
      ["20260902_platform_sandbox_bonhomia_bruno"] = "20260908_production_public_site_bindings",
      ["20260903_hospitality_public_scope_sandbox"] = "20260908_production_hospitality_public_scope",
      ["20260903_restaurant_public_identity_scope_sandbox"] = "20260908_production_restaurant_public_identity_scope",
      ["20260904_public_site_presentation_transition_sandbox"] = "20260908_production_public_site_presentation_transition",
      ["20260905_hospitality_legal_consent_sandbox"] = "20260908_production_hospitality_legal_consent",
      ["20260908_hospitality_administration_scope_sandbox"] = "20260908_production_hospitality_administration_scope",
      ["20260908_accounting_company_identity_sandbox"] = "20260908_production_accounting_company_identity",
      ["20260908_accounting_cycle_sandbox"] = "20260908_production_accounting_cycle",
      ["20260908_accounting_outbox_sandbox"] = "20260908_production_accounting_outbox",
      ["20260908_published_reports_sandbox"] = "20260908_production_published_reports",
      ["20260909_accounting_cycle_activation_sandbox"] = "20260909_production_accounting_cycle_activation",
      ["20260909_bsu_opening_balance_counterpart_sandbox"] = "20260909_production_bsu_opening_balance_counterpart",
      ["20260909_workforce_attendance_scope_sandbox"] = "20260909_production_workforce_attendance_scope",
      ["20260909_calendar_owner_scope_sandbox"] = "20260909_production_calendar_owner_scope",
      ["20260909_hospitality_legacy_mechanisms_sandbox"] = "20260910_production_hospitality_legacy_mechanisms",
      ["20260909_hospitality_legacy_transaction_guards_sandbox"] = "20260910_production_hospitality_legacy_mechanisms",
      ["20260909_hospitality_payment_policy_switch_sandbox"] = "20260910_production_hospitality_legacy_mechanisms"
    };

    var actual = Manifest.Migrations
      .Where(item => item.SatisfiedBy is { Count: > 0 })
      .ToDictionary(item => item.Id, item => Assert.Single(item.SatisfiedBy!), StringComparer.Ordinal);

    Assert.Equal(expected.Keys.Order(), actual.Keys.Order());
    foreach (var (migrationId, replacementId) in expected)
    {
      var replacement = actual[migrationId];
      Assert.Equal(replacementId, replacement.Id);
      Assert.Equal(productionById[replacementId].Path, replacement.Path);
      Assert.True(File.Exists(Path.Combine(RepositoryRoot, replacement.Path)));
    }
  }

  [Fact]
  public void Manifest_TracksEveryManagedPlatformAndTenantIsolationScript()
  {
    var declared = Manifest.Migrations
      .Select(item => NormalizePath(item.Path))
      .ToHashSet(StringComparer.OrdinalIgnoreCase);
    var platformSqlRoot = Path.Combine(
      RepositoryRoot,
      "src", "OrionERP.Infrastructure", "Features", "Platform", "Sql");
    var actual = Directory.EnumerateFiles(platformSqlRoot, "*.sql", SearchOption.TopDirectoryOnly)
      .Select(path => NormalizePath(Path.GetRelativePath(RepositoryRoot, path)))
      .ToHashSet(StringComparer.OrdinalIgnoreCase);
    actual.Add(NormalizePath(
      "src/OrionERP.Infrastructure/Features/Bonhomia/PublicBooking/Sql/20260903_hospitality_public_scope_sandbox.sql"));
    actual.Add(NormalizePath(
      "src/OrionERP.Infrastructure/Features/Bonhomia/PublicBooking/Sql/20260905_hospitality_legal_consent_sandbox.sql"));
    actual.Add(NormalizePath(
      "src/OrionERP.Infrastructure/Features/Restaurante/Sql/20260903_restaurant_public_identity_scope_sandbox.sql"));

    actual.Add(NormalizePath("src/OrionERP.Infrastructure/Features/Reservaciones/Sql/20260908_hospitality_administration_scope_sandbox.sql"));
    actual.Add(NormalizePath("src/OrionERP.Infrastructure/Features/Contabilidad/Transacciones/Sql/20260908_accounting_company_identity_sandbox.sql"));
    actual.Add(NormalizePath("src/OrionERP.Infrastructure/Features/Contabilidad/Transacciones/Sql/20260908_accounting_cycle_sandbox.sql"));
    actual.Add(NormalizePath("src/OrionERP.Infrastructure/Features/Contabilidad/Transacciones/Sql/20260908_accounting_outbox_sandbox.sql"));
    actual.Add(NormalizePath("src/OrionERP.Infrastructure/Features/ReportesFinancieros/Sql/20260908_published_reports_sandbox.sql"));
    actual.Add(NormalizePath("src/OrionERP.Infrastructure/Features/Contabilidad/Transacciones/Sql/20260909_accounting_cycle_activation_sandbox.sql"));
    actual.Add(NormalizePath("src/OrionERP.Infrastructure/Features/Contabilidad/Transacciones/Sql/20260909_bsu_opening_balance_counterpart_sandbox.sql"));
    actual.Add(NormalizePath("src/OrionERP.Infrastructure/Features/CapitalHumano/Workforce/Sql/20260909_workforce_attendance_scope_sandbox.sql"));
    actual.Add(NormalizePath("src/OrionERP.Infrastructure/Features/Reservaciones/ListaReservaciones/Sql/20260909_calendar_owner_scope_sandbox.sql"));
    actual.Add(NormalizePath("src/OrionERP.Infrastructure/Features/Reservaciones/Sql/20260909_hospitality_legacy_mechanisms_sandbox.sql"));
    actual.Add(NormalizePath("src/OrionERP.Infrastructure/Features/Reservaciones/Sql/20260909_hospitality_legacy_transaction_guards_sandbox.sql"));
    actual.Add(NormalizePath("src/OrionERP.Infrastructure/Features/Reservaciones/Sql/20260909_hospitality_payment_policy_switch_sandbox.sql"));
    actual.Add(NormalizePath("src/OrionERP.Infrastructure/Features/Reservaciones/Sql/20260909_hospitality_payment_link_removal.sql"));
    actual.Add(NormalizePath("src/OrionERP.Infrastructure/Features/Contabilidad/Transacciones/Sql/20260909_accounting_rls_scope_sandbox.sql"));
    actual.Add(NormalizePath("src/OrionERP.Infrastructure/Features/Logistica/Sql/20260909_inventory_core_rls_scope_sandbox.sql"));
    actual.Add(NormalizePath("src/OrionERP.Infrastructure/Features/ReportesFinancieros/Sql/20260909_fiscal_rls_scope_sandbox.sql"));

    Assert.Equal(
      actual.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
      declared.Order(StringComparer.OrdinalIgnoreCase).ToArray());
  }

  [Fact]
  public void ManifestScripts_KeepDatabasePreviewAndRegistryGuards()
  {
    var allowedVariables = new HashSet<string>(
      ["ExpectedDatabase", "ApplyChanges", "MigrationId", "MigrationChecksum", "AppVersion"],
      StringComparer.Ordinal);

    foreach (var migration in Manifest.Migrations)
    {
      var sql = File.ReadAllText(Path.Combine(RepositoryRoot, migration.Path));
      Assert.Contains("$(ExpectedDatabase)", sql, StringComparison.Ordinal);
      Assert.Contains("$(ApplyChanges)", sql, StringComparison.Ordinal);
      Assert.Contains("$(MigrationId)", sql, StringComparison.Ordinal);
      Assert.Contains("$(MigrationChecksum)", sql, StringComparison.Ordinal);
      Assert.Contains("SET XACT_ABORT ON", sql, StringComparison.OrdinalIgnoreCase);
      Assert.Contains("BEGIN TRANSACTION", sql, StringComparison.OrdinalIgnoreCase);
      Assert.Contains("ROLLBACK TRANSACTION", sql, StringComparison.OrdinalIgnoreCase);
      switch (migration.Id)
      {
        case "20260901_platform_foundation":
          Assert.DoesNotContain("OHM191112Q26", sql, StringComparison.OrdinalIgnoreCase);
          Assert.DoesNotContain("BRUNOS260707L26", sql, StringComparison.OrdinalIgnoreCase);
          Assert.DoesNotContain("SIN_RFC", sql, StringComparison.OrdinalIgnoreCase);
          break;
        case "20260902_platform_sandbox_bonhomia_bruno":
          Assert.Contains("OHM191112Q26", sql, StringComparison.OrdinalIgnoreCase);
          Assert.Contains("BRUNOS260707L26", sql, StringComparison.OrdinalIgnoreCase);
          Assert.DoesNotContain("SIN_RFC", sql, StringComparison.OrdinalIgnoreCase);
          Assert.Equal(["Orion_Sandbox"], migration.AllowedDatabases);
          break;
        case "20260903_hospitality_public_scope_sandbox":
          Assert.Contains("OHM191112Q26", sql, StringComparison.OrdinalIgnoreCase);
          Assert.Contains("SIN_RFC", sql, StringComparison.OrdinalIgnoreCase);
          Assert.DoesNotContain("BRUNOS260707L26", sql, StringComparison.OrdinalIgnoreCase);
          Assert.Equal(["Orion_Sandbox"], migration.AllowedDatabases);
          break;
        case "20260903_restaurant_public_identity_scope_sandbox":
          Assert.Contains("BRUNOS260707L26", sql, StringComparison.OrdinalIgnoreCase);
          Assert.DoesNotContain("OHM191112Q26", sql, StringComparison.OrdinalIgnoreCase);
          Assert.DoesNotContain("SIN_RFC", sql, StringComparison.OrdinalIgnoreCase);
          Assert.Equal(["Orion_Sandbox"], migration.AllowedDatabases);
          break;
        case "20260904_public_site_presentation_transition_sandbox":
          Assert.DoesNotContain("OHM191112Q26", sql, StringComparison.OrdinalIgnoreCase);
          Assert.DoesNotContain("BRUNOS260707L26", sql, StringComparison.OrdinalIgnoreCase);
          Assert.DoesNotContain("SIN_RFC", sql, StringComparison.OrdinalIgnoreCase);
          Assert.Equal(["Orion_Sandbox"], migration.AllowedDatabases);
          break;
        case "20260905_hospitality_legal_consent_sandbox":
          Assert.DoesNotContain("OHM191112Q26", sql, StringComparison.OrdinalIgnoreCase);
          Assert.DoesNotContain("BRUNOS260707L26", sql, StringComparison.OrdinalIgnoreCase);
          Assert.DoesNotContain("SIN_RFC", sql, StringComparison.OrdinalIgnoreCase);
          Assert.Contains("PrivacyVersionAccepted", sql, StringComparison.Ordinal);
          Assert.Contains("TermsVersionAccepted", sql, StringComparison.Ordinal);
          Assert.Contains("LegalAcceptedAtUtc", sql, StringComparison.Ordinal);
          Assert.Equal(["Orion_Sandbox"], migration.AllowedDatabases);
          break;
        case "20260908_hospitality_administration_scope_sandbox":
          Assert.DoesNotContain("OHM191112Q26", sql, StringComparison.OrdinalIgnoreCase);
          Assert.DoesNotContain("BRUNOS260707L26", sql, StringComparison.OrdinalIgnoreCase);
          Assert.Contains("CREATE SECURITY POLICY orion.HospitalityScopePolicy", sql, StringComparison.Ordinal);
          Assert.Contains("ADD BLOCK PREDICATE", sql, StringComparison.Ordinal);
          Assert.Equal(["Orion_Sandbox"], migration.AllowedDatabases);
          break;
        case "20260908_accounting_company_identity_sandbox":
          // El vínculo se resuelve contra orion.Company, no contra un RFC escrito a mano.
          Assert.DoesNotContain("OHM191112Q26", sql, StringComparison.OrdinalIgnoreCase);
          Assert.DoesNotContain("BRUNOS260707L26", sql, StringComparison.OrdinalIgnoreCase);
          Assert.DoesNotContain("SIN_RFC", sql, StringComparison.OrdinalIgnoreCase);
          // Aditiva y nullable: ni NOT NULL ni RLS antes de acreditar a los escritores.
          Assert.Contains("ADD CompanyId bigint NULL", sql, StringComparison.Ordinal);
          Assert.DoesNotContain("CompanyId bigint NOT NULL", sql, StringComparison.Ordinal);
          Assert.DoesNotContain("ALTER COLUMN", sql, StringComparison.OrdinalIgnoreCase);
          Assert.DoesNotContain("CREATE SECURITY POLICY", sql, StringComparison.Ordinal);
          Assert.DoesNotContain("CREATE INDEX", sql, StringComparison.OrdinalIgnoreCase);
          Assert.Contains("ENABLE TRIGGER trg_Transacciones_Audit", sql, StringComparison.Ordinal);
          Assert.Contains("ENABLE TRIGGER trg_Registro_Contable_Audit", sql, StringComparison.Ordinal);
          Assert.Equal(["Orion_Sandbox"], migration.AllowedDatabases);
          break;
        case "20260908_accounting_cycle_sandbox":
          Assert.DoesNotContain("OHM191112Q26", sql, StringComparison.OrdinalIgnoreCase);
          Assert.DoesNotContain("BRUNOS260707L26", sql, StringComparison.OrdinalIgnoreCase);
          Assert.DoesNotContain("SIN_RFC", sql, StringComparison.OrdinalIgnoreCase);
          // Se entrega apagada: nada se activa ni se cierra al aplicar.
          Assert.Contains("DF_CompanyCycleActivation_IsEnabled DEFAULT (0)", sql, StringComparison.Ordinal);
          Assert.Contains("El ciclo debe entregarse apagado y sin periodos cerrados.", sql, StringComparison.Ordinal);
          Assert.Contains("Ninguna poliza historica debe entrar al ciclo al aplicar la migracion.", sql, StringComparison.Ordinal);
          // Inmutabilidad y reversa unica en SQL, no solo en el servicio.
          Assert.Contains("CREATE TRIGGER dbo.TR_Transacciones_CycleImmutability", sql, StringComparison.Ordinal);
          Assert.Contains("CREATE TRIGGER dbo.TR_Registro_Contable_CycleImmutability", sql, StringComparison.Ordinal);
          Assert.Contains("CREATE UNIQUE INDEX UX_Transacciones_ReversalOf", sql, StringComparison.Ordinal);
          // El Estatus legacy no se convierte ni se reinterpreta, y el cierre del libro
          // no se mezcla con el cierre declarativo de impuestos: el guion lo nombra en
          // un comentario para decir justamente que no lo lee ni lo escribe.
          Assert.DoesNotContain("UPDATE dbo.Transacciones", sql, StringComparison.Ordinal);
          foreach (var statement in new[] { "FROM fiscal.", "JOIN fiscal.", "UPDATE fiscal.", "INSERT fiscal.", "DELETE fiscal." })
            Assert.DoesNotContain(statement, sql, StringComparison.OrdinalIgnoreCase);
          Assert.Equal(["Orion_Sandbox"], migration.AllowedDatabases);
          break;
        case "20260908_accounting_outbox_sandbox":
          Assert.DoesNotContain("OHM191112Q26", sql, StringComparison.OrdinalIgnoreCase);
          Assert.DoesNotContain("BRUNOS260707L26", sql, StringComparison.OrdinalIgnoreCase);
          Assert.DoesNotContain("SIN_RFC", sql, StringComparison.OrdinalIgnoreCase);
          // La identidad única de operación es lo que impide una segunda contabilización.
          Assert.Contains("CREATE UNIQUE INDEX UX_AccountingOutbox_Operation", sql, StringComparison.Ordinal);
          // Hospedaje se entrega apagada, con sus mappings vacíos y sin inferir cuentas.
          Assert.Contains("DF_HospitalityAccountingMapping_IsEnabled DEFAULT (0)", sql, StringComparison.Ordinal);
          Assert.DoesNotContain("INSERT contabilidad.HospitalityAccountingMapping", sql, StringComparison.Ordinal);
          // Es una bandeja propia: la de SignalR se nombra en un comentario para decir
          // justamente que no se lee ni se escribe.
          foreach (var statement in new[] { "FROM restaurante.EventOutbox", "JOIN restaurante.EventOutbox", "INSERT restaurante.EventOutbox", "UPDATE restaurante.EventOutbox", "DELETE restaurante.EventOutbox" })
            Assert.DoesNotContain(statement, sql, StringComparison.OrdinalIgnoreCase);
          Assert.Equal(["Orion_Sandbox"], migration.AllowedDatabases);
          break;
        case "20260908_published_reports_sandbox":
          Assert.DoesNotContain("OHM191112Q26", sql, StringComparison.OrdinalIgnoreCase);
          Assert.DoesNotContain("BRUNOS260707L26", sql, StringComparison.OrdinalIgnoreCase);
          Assert.DoesNotContain("SIN_RFC", sql, StringComparison.OrdinalIgnoreCase);
          // El reporte vigente conserva su significado: el filtro es condicional.
          Assert.Contains("@SoloPublicadas BIT = 0", sql, StringComparison.Ordinal);
          Assert.Contains("@SoloPublicadas = 0 OR t.CycleState IN (''Posted'', ''Reversed'')", sql, StringComparison.Ordinal);
          // Los dos reportes se adaptan como unidad.
          Assert.Contains("[Rpt_BalanzaComprobacion]", sql, StringComparison.Ordinal);
          Assert.Contains("[ESTADO_PERDIDAS_GANANCIAS]", sql, StringComparison.Ordinal);
          foreach (var fiscalStatement in new[] { "FROM fiscal.", "JOIN fiscal.", "UPDATE fiscal.", "INSERT fiscal.", "DELETE fiscal.", "EXEC fiscal." })
            Assert.DoesNotContain(fiscalStatement, sql, StringComparison.OrdinalIgnoreCase);
          Assert.Equal(["Orion_Sandbox"], migration.AllowedDatabases);
          break;
        case "20260909_accounting_cycle_activation_sandbox":
          // Ésta SÍ nombra empresas: es el registro de a quién aprobó el usuario.
          Assert.Contains("BRUNOS260707L26", sql, StringComparison.Ordinal);
          Assert.Contains("OHM191112Q26", sql, StringComparison.Ordinal);
          Assert.DoesNotContain("SIN_RFC", sql, StringComparison.OrdinalIgnoreCase);
          // Un piloto, no una activación masiva, y sin cerrar periodos ni publicar historia.
          Assert.Contains("Debe quedar exactamente una empresa con el ciclo encendido.", sql, StringComparison.Ordinal);
          Assert.Contains("Ninguna poliza historica debe entrar al ciclo por esta activacion.", sql, StringComparison.Ordinal);
          Assert.Contains("Esta migracion no cierra periodos.", sql, StringComparison.Ordinal);
          Assert.DoesNotContain("UPDATE dbo.Transacciones", sql, StringComparison.Ordinal);
          Assert.Equal(["Orion_Sandbox"], migration.AllowedDatabases);
          break;
        case "20260909_bsu_opening_balance_counterpart_sandbox":
          // Nombra a BSU porque corrige una de sus pólizas, identificada por ID.
          Assert.Contains("BSU210121M77", sql, StringComparison.Ordinal);
          Assert.DoesNotContain("OHM191112Q26", sql, StringComparison.Ordinal);
          Assert.DoesNotContain("SIN_RFC", sql, StringComparison.OrdinalIgnoreCase);
          // Aditiva: agrega un renglón y no modifica ni borra ninguno.
          Assert.Contains("INSERT dbo.Registro_Contable", sql, StringComparison.Ordinal);
          Assert.DoesNotContain("UPDATE dbo.Registro_Contable", sql, StringComparison.Ordinal);
          Assert.DoesNotContain("DELETE dbo.Registro_Contable", sql, StringComparison.Ordinal);
          Assert.DoesNotContain("UPDATE dbo.Transacciones", sql, StringComparison.Ordinal);
          // Comprueba la forma antes de escribir y vuelve a leerla bajo candado.
          Assert.Contains("La poliza cambio despues del preview; no se aplica a ciegas.", sql, StringComparison.Ordinal);
          Assert.Contains("Esta correccion no mete la poliza al ciclo.", sql, StringComparison.Ordinal);
          Assert.Equal(["Orion_Sandbox"], migration.AllowedDatabases);
          break;
        case "20260909_workforce_attendance_scope_sandbox":
          Assert.DoesNotContain("OHM191112Q26", sql, StringComparison.OrdinalIgnoreCase);
          Assert.DoesNotContain("BRUNOS260707L26", sql, StringComparison.OrdinalIgnoreCase);
          Assert.DoesNotContain("SIN_RFC", sql, StringComparison.OrdinalIgnoreCase);
          // Fail-closed de verdad: el predicado nuevo no admite contexto nulo.
          Assert.Contains("CREATE FUNCTION rh.fn_WorkforceScopePredicate", sql, StringComparison.Ordinal);
          Assert.Contains("El predicado nuevo no debe admitir contexto nulo.", sql, StringComparison.Ordinal);
          // Un lote acotado: seis tablas fuera, y las demás conservan su política.
          Assert.Contains("La politica heredada de RH debe conservar sus otros cuarenta y ocho predicados.", sql, StringComparison.Ordinal);
          Assert.Contains("Una tabla del lote sigue en la politica heredada.", sql, StringComparison.Ordinal);
          // No toca reglas laborales, nómina, expedientes ni biométricos.
          foreach (var write in new[] { "INSERT rh.", "UPDATE rh.", "DELETE rh.", "INSERT INTO rh." })
            Assert.DoesNotContain(write, sql, StringComparison.OrdinalIgnoreCase);
          Assert.Equal(["Orion_Sandbox"], migration.AllowedDatabases);
          break;
        case "20260909_calendar_owner_scope_sandbox":
          // No nombra empresas: al dueño lo resuelve la identidad de la sesión, no el script.
          Assert.DoesNotContain("OHM191112Q26", sql, StringComparison.OrdinalIgnoreCase);
          Assert.DoesNotContain("BRUNOS260707L26", sql, StringComparison.OrdinalIgnoreCase);
          Assert.DoesNotContain("SIN_RFC", sql, StringComparison.OrdinalIgnoreCase);
          // El calendario de hoy no cambia: el parámetro nace opcional y el filtro es condicional.
          Assert.Contains("@OwnerId int = NULL", sql, StringComparison.Ordinal);
          Assert.Contains("(@OwnerId IS NULL OR r.OWNER_ID = @OwnerId)", sql, StringComparison.Ordinal);
          // Y acota los tres conjuntos de resultados, no sólo la lista de habitaciones.
          Assert.Contains("Los resultados dejaron de derivarse de #Resources", sql, StringComparison.Ordinal);
          // Versiona un procedimiento: no toca datos ni políticas de seguridad.
          foreach (var write in new[] { "INSERT dbo.", "UPDATE dbo.", "DELETE dbo.", "ALTER SECURITY POLICY" })
            Assert.DoesNotContain(write, sql, StringComparison.OrdinalIgnoreCase);
          Assert.Equal(["Orion_Sandbox"], migration.AllowedDatabases);
          break;
        case "20260909_hospitality_legacy_mechanisms_sandbox":
          // El único RFC escrito a mano corresponde a los cuatro mappings Outlook
          // cuya evidencia exacta fue revisada; los demás alcances se resuelven por contexto.
          Assert.Contains("OHM191112Q26", sql, StringComparison.Ordinal);
          Assert.DoesNotContain("BRUNOS260707L26", sql, StringComparison.OrdinalIgnoreCase);
          Assert.DoesNotContain("SIN_RFC", sql, StringComparison.OrdinalIgnoreCase);
          Assert.Contains("HospitalityPaymentCorrectionManifest", sql, StringComparison.Ordinal);
          Assert.Contains("HospitalityOutlookMappingQuarantine", sql, StringComparison.Ordinal);
          Assert.Contains("HospitalityActivityTemplateMapping", sql, StringComparison.Ordinal);
          Assert.Equal(["Orion_Sandbox"], migration.AllowedDatabases);
          break;
        case "20260909_hospitality_legacy_transaction_guards_sandbox":
          Assert.DoesNotContain("OHM191112Q26", sql, StringComparison.OrdinalIgnoreCase);
          Assert.DoesNotContain("BRUNOS260707L26", sql, StringComparison.OrdinalIgnoreCase);
          Assert.DoesNotContain("SIN_RFC", sql, StringComparison.OrdinalIgnoreCase);
          Assert.Contains("IF XACT_STATE()<>0 ROLLBACK TRANSACTION", sql, StringComparison.Ordinal);
          Assert.Contains("ReconcileHospitalityPaymentLinks_E7Core", sql, StringComparison.Ordinal);
          Assert.Equal(["Orion_Sandbox"], migration.AllowedDatabases);
          break;
        case "20260909_hospitality_payment_policy_switch_sandbox":
          Assert.DoesNotContain("OHM191112Q26", sql, StringComparison.OrdinalIgnoreCase);
          Assert.DoesNotContain("BRUNOS260707L26", sql, StringComparison.OrdinalIgnoreCase);
          Assert.DoesNotContain("SIN_RFC", sql, StringComparison.OrdinalIgnoreCase);
          Assert.Contains("fn_HospitalityPaymentScopePredicate", sql, StringComparison.Ordinal);
          Assert.Contains("ExpectedPreviewChecksum", sql, StringComparison.Ordinal);
          Assert.Equal(["Orion_Sandbox"], migration.AllowedDatabases);
          break;
        case "20260909_hospitality_payment_link_removal":
          // Este corrector nombra ambos RFC porque registra la decisión empresarial
          // exacta. Su única eliminación permitida es el vínculo cruzado revisado.
          Assert.Contains("OHM191112Q26", sql, StringComparison.Ordinal);
          Assert.Contains("BSU210121M77", sql, StringComparison.Ordinal);
          Assert.DoesNotContain("BRUNOS260707L26", sql, StringComparison.OrdinalIgnoreCase);
          Assert.DoesNotContain("SIN_RFC", sql, StringComparison.OrdinalIgnoreCase);
          Assert.Contains("CE25D7CFD35BCDD6554E2C4798A2FA51825E04B7849B3DBB5FAF99197862A40A", sql, StringComparison.Ordinal);
          Assert.Contains("HospitalityPaymentLinkRemovalAudit", sql, StringComparison.Ordinal);
          Assert.Contains("HAVING COUNT(*) NOT BETWEEN 1 AND 25", sql, StringComparison.Ordinal);
          Assert.Contains("DELETE link", sql, StringComparison.Ordinal);
          Assert.DoesNotContain("DELETE dbo.Transacciones", sql, StringComparison.OrdinalIgnoreCase);
          Assert.DoesNotContain("DELETE dbo.Registro_Contable", sql, StringComparison.OrdinalIgnoreCase);
          Assert.DoesNotContain("DELETE dbo.Transaccion_Comprobante", sql, StringComparison.OrdinalIgnoreCase);
          Assert.Equal(["Orion_Sandbox"], migration.AllowedDatabases);
          break;
        case "20260909_accounting_rls_scope_sandbox":
          Assert.DoesNotContain("OHM191112Q26", sql, StringComparison.OrdinalIgnoreCase);
          Assert.DoesNotContain("BRUNOS260707L26", sql, StringComparison.OrdinalIgnoreCase);
          Assert.DoesNotContain("SIN_RFC", sql, StringComparison.OrdinalIgnoreCase);
          Assert.Contains("CREATE SECURITY POLICY contabilidad.AccountingScopePolicy", sql, StringComparison.Ordinal);
          Assert.Contains("El predicado contable no debe admitir bypass por NULL.", sql, StringComparison.Ordinal);
          Assert.Equal(["Orion_Sandbox"], migration.AllowedDatabases);
          break;
        case "20260909_inventory_core_rls_scope_sandbox":
          Assert.DoesNotContain("OHM191112Q26", sql, StringComparison.OrdinalIgnoreCase);
          Assert.DoesNotContain("BRUNOS260707L26", sql, StringComparison.OrdinalIgnoreCase);
          Assert.DoesNotContain("SIN_RFC", sql, StringComparison.OrdinalIgnoreCase);
          Assert.Contains("CREATE SECURITY POLICY logistica.InventoryCoreScopePolicy", sql, StringComparison.Ordinal);
          Assert.Contains("El predicado E8b no debe admitir bypass por NULL.", sql, StringComparison.Ordinal);
          Assert.Equal(["Orion_Sandbox"], migration.AllowedDatabases);
          break;
        case "20260909_fiscal_rls_scope_sandbox":
          Assert.DoesNotContain("OHM191112Q26", sql, StringComparison.OrdinalIgnoreCase);
          Assert.DoesNotContain("BRUNOS260707L26", sql, StringComparison.OrdinalIgnoreCase);
          Assert.DoesNotContain("SIN_RFC", sql, StringComparison.OrdinalIgnoreCase);
          Assert.Contains("CREATE SECURITY POLICY fiscal.DeclarationScopePolicy", sql, StringComparison.Ordinal);
          Assert.Contains("El predicado E8d no debe admitir bypass por NULL.", sql, StringComparison.Ordinal);
          Assert.DoesNotContain("ALTER SECURITY POLICY fiscal.ComprobanteScopePolicy", sql, StringComparison.OrdinalIgnoreCase);
          Assert.Equal(["Orion_Sandbox"], migration.AllowedDatabases);
          break;
        case "20260911_platform_execution_scope":
          Assert.DoesNotContain("OHM191112Q26", sql, StringComparison.OrdinalIgnoreCase);
          Assert.DoesNotContain("BRUNOS260707L26", sql, StringComparison.OrdinalIgnoreCase);
          Assert.Contains("OrionCompanyId", sql, StringComparison.Ordinal);
          Assert.Contains("PublicSiteId", sql, StringComparison.Ordinal);
          Assert.Contains("PublicSqlPrincipalBinding", sql, StringComparison.Ordinal);
          Assert.Equal(["Orion_Sandbox"], migration.AllowedDatabases);
          break;
        case "20260911_public_identity":
          Assert.DoesNotContain("OHM191112Q26", sql, StringComparison.OrdinalIgnoreCase);
          Assert.DoesNotContain("BRUNOS260707L26", sql, StringComparison.OrdinalIgnoreCase);
          Assert.Contains("public_identity", sql, StringComparison.Ordinal);
          Assert.Contains("PublicIdentityScopePolicy", sql, StringComparison.Ordinal);
          Assert.Contains("PublicIdentityCompatibilityState", sql, StringComparison.Ordinal);
          Assert.Equal(["Orion_Sandbox"], migration.AllowedDatabases);
          break;
        case "20260911_public_provisioning_profiles":
          Assert.DoesNotContain("OHM191112Q26", sql, StringComparison.OrdinalIgnoreCase);
          Assert.DoesNotContain("BRUNOS260707L26", sql, StringComparison.OrdinalIgnoreCase);
          Assert.Contains("PublicPermissionProfileEntry", sql, StringComparison.Ordinal);
          Assert.Contains("CREATE OR ALTER PROCEDURE orion.ApplyPublicPermissionProfile", sql, StringComparison.Ordinal);
          Assert.Contains("@ApplyChanges bit=0", sql, StringComparison.Ordinal);
          Assert.Equal(["Orion_Sandbox"], migration.AllowedDatabases);
          break;
        case "20260911_public_principal_boundary":
          Assert.DoesNotContain("OHM191112Q26", sql, StringComparison.OrdinalIgnoreCase);
          Assert.DoesNotContain("BRUNOS260707L26", sql, StringComparison.OrdinalIgnoreCase);
          Assert.Contains("PublicSqlPrincipalBinding", sql, StringComparison.Ordinal);
          Assert.Contains("ProfileVersion=2", sql, StringComparison.Ordinal);
          Assert.Contains("SELECT @ProfileVersion=MAX(ProfileVersion)", sql, StringComparison.Ordinal);
          Assert.Equal(["Orion_Sandbox"], migration.AllowedDatabases);
          break;
        case "20260911_public_identity_readiness_permissions":
          Assert.DoesNotContain("OHM191112Q26", sql, StringComparison.OrdinalIgnoreCase);
          Assert.DoesNotContain("BRUNOS260707L26", sql, StringComparison.OrdinalIgnoreCase);
          Assert.Contains("PublicIdentityCompatibilityState", sql, StringComparison.Ordinal);
          Assert.Contains("ProfileVersion=3", sql, StringComparison.Ordinal);
          Assert.Equal(["Orion_Sandbox"], migration.AllowedDatabases);
          break;
        case "20260911_public_identity_policy_metadata_visibility":
          Assert.DoesNotContain("OHM191112Q26", sql, StringComparison.OrdinalIgnoreCase);
          Assert.DoesNotContain("BRUNOS260707L26", sql, StringComparison.OrdinalIgnoreCase);
          Assert.Contains("GRANT VIEW DEFINITION ON OBJECT::[orion].[PublicIdentityScopePolicy]", sql, StringComparison.Ordinal);
          Assert.Contains("ProfileVersion=4", sql, StringComparison.Ordinal);
          Assert.Equal(["Orion_Sandbox"], migration.AllowedDatabases);
          break;
        case "20260911_public_rls_policy_metadata_permissions":
          Assert.DoesNotContain("OHM191112Q26", sql, StringComparison.OrdinalIgnoreCase);
          Assert.DoesNotContain("BRUNOS260707L26", sql, StringComparison.OrdinalIgnoreCase);
          Assert.Contains("GRANT VIEW DEFINITION ON OBJECT::[orion].[HospitalityScopePolicy]", sql, StringComparison.Ordinal);
          Assert.Contains("GRANT VIEW DEFINITION ON OBJECT::[orion].[PublicIdentityScopePolicy]", sql, StringComparison.Ordinal);
          Assert.Contains("ProfileVersion=5", sql, StringComparison.Ordinal);
          Assert.Equal(["Orion_Sandbox"], migration.AllowedDatabases);
          break;
        case "20260911_public_rls_principal_binding":
          Assert.DoesNotContain("OHM191112Q26", sql, StringComparison.OrdinalIgnoreCase);
          Assert.DoesNotContain("BRUNOS260707L26", sql, StringComparison.OrdinalIgnoreCase);
          Assert.Contains("binding.PrincipalName=USER_NAME()", sql, StringComparison.Ordinal);
          Assert.Contains("SESSION_CONTEXT(N'OrionERP.PublicSiteId')", sql, StringComparison.Ordinal);
          Assert.Contains("ALTER FUNCTION orion.fn_PublicIdentityScopePredicate", sql, StringComparison.Ordinal);
          Assert.Contains("ALTER FUNCTION orion.fn_HospitalityScopePredicate", sql, StringComparison.Ordinal);
          Assert.Equal(["Orion_Sandbox"], migration.AllowedDatabases);
          break;
        case "20260911_sandbox_database_owner":
          Assert.DoesNotContain("OHM191112Q26", sql, StringComparison.OrdinalIgnoreCase);
          Assert.DoesNotContain("BRUNOS260707L26", sql, StringComparison.OrdinalIgnoreCase);
          Assert.Contains("ALTER AUTHORIZATION ON DATABASE::[Orion_Sandbox] TO [sa]", sql, StringComparison.Ordinal);
          Assert.Contains("EXECUTE AS USER=N'dbo'", sql, StringComparison.Ordinal);
          Assert.Equal(["Orion_Sandbox"], migration.AllowedDatabases);
          break;
        case "20260911_rfc_rls_fail_closed":
          Assert.DoesNotContain("OHM191112Q26", sql, StringComparison.OrdinalIgnoreCase);
          Assert.DoesNotContain("BRUNOS260707L26", sql, StringComparison.OrdinalIgnoreCase);
          Assert.Contains("PublicSqlPrincipalBinding", sql, StringComparison.Ordinal);
          Assert.Contains("SESSION_CONTEXT(N'OrionERP.CompanyId')", sql, StringComparison.Ordinal);
          Assert.Contains("SESSION_CONTEXT(N'OrionERP.PublicSiteId')", sql, StringComparison.Ordinal);
          Assert.Contains("WHERE USER_NAME()=N'dbo'", sql, StringComparison.Ordinal);
          Assert.Equal(["Orion_Sandbox"], migration.AllowedDatabases);
          break;
        case "20260911_synthetic_dual_company":
          Assert.DoesNotContain("OHM191112Q26", sql, StringComparison.OrdinalIgnoreCase);
          Assert.DoesNotContain("BRUNOS260707L26", sql, StringComparison.OrdinalIgnoreCase);
          Assert.Contains("TST260910DUAL01", sql, StringComparison.Ordinal);
          Assert.Contains("synthetic-hospitality-main", sql, StringComparison.Ordinal);
          Assert.Contains("synthetic-restaurant-main", sql, StringComparison.Ordinal);
          Assert.Contains("SetPublicIdentityBridgeMode", sql, StringComparison.Ordinal);
          Assert.Contains("ApplyPublicPermissionProfile", sql, StringComparison.Ordinal);
          Assert.Contains("@ExpectedDatabase<>N'Orion_Sandbox'", sql, StringComparison.Ordinal);
          Assert.Equal(["Orion_Sandbox"], migration.AllowedDatabases);
          break;
        case "20260911_legacy_write_compatibility":
          Assert.DoesNotContain("OHM191112Q26", sql, StringComparison.OrdinalIgnoreCase);
          Assert.DoesNotContain("BRUNOS260707L26", sql, StringComparison.OrdinalIgnoreCase);
          Assert.Contains("TR_Site_LegacyBinding", sql, StringComparison.Ordinal);
          Assert.Contains("TR_PublicSiteSettings_LegacyBinding", sql, StringComparison.Ordinal);
          Assert.Contains("WHERE OrionCompanyId IS NOT NULL AND OrionSiteId IS NOT NULL", sql, StringComparison.Ordinal);
          Assert.Contains("WHERE PublicSiteId IS NOT NULL", sql, StringComparison.Ordinal);
          Assert.Equal(["Orion_Sandbox"], migration.AllowedDatabases);
          break;
        default:
          throw new Xunit.Sdk.XunitException($"La migración {migration.Id} no tiene política explícita de literales heredados.");
      }

      var variables = Regex.Matches(sql, @"\$\((?<name>[^)]+)\)", RegexOptions.CultureInvariant)
        .Select(match => match.Groups["name"].Value)
        .ToHashSet(StringComparer.Ordinal);
      Assert.True(
        variables.SetEquals(allowedVariables),
        $"Variables SQLCMD inesperadas en {migration.Path}: {string.Join(", ", variables.Order())}");
    }
  }

  private static string NormalizePath(string path) => path.Replace('\\', '/');

  private static string FindRepositoryRoot()
  {
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "OrionERP.sln")))
      directory = directory.Parent;

    return directory?.FullName
      ?? throw new InvalidOperationException("No se encontró la raíz del repositorio.");
  }
}
