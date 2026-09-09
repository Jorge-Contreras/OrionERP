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
      "20260909_bsu_opening_balance_counterpart_sandbox"
    })
    {
      var sandboxOnlyMigration = Manifest.Migrations.Single(item => item.Id == sandboxOnlyId);
      Assert.Equal(["Orion_Sandbox"], sandboxOnlyMigration.AllowedDatabases);
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
