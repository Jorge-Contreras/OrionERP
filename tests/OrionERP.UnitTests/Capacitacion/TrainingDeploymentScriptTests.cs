namespace OrionERP.UnitTests.Capacitacion;

public sealed class TrainingDeploymentScriptTests
{
  private static readonly string RuntimeSql = ReadRepoFile(
    "src/OrionERP.Infrastructure/Features/Capacitacion/Sql/20260817_training_runtime_login.sql");
  private static readonly string ProvisionRuntime = ReadRepoFile("Provision-TrainingRuntimeSqlLogin.ps1");
  private static readonly string Configure = ReadRepoFile("Configure-TrainingService.ps1");
  private static readonly string Publish = ReadRepoFile("Publish-Training.ps1");
  private static readonly string SanitizerLauncher = ReadRepoFile("Run-TrainingSanitizer.ps1");
  private static readonly string AttestSql = ReadRepoFile(
    "src/OrionERP.Infrastructure/Features/Capacitacion/Sql/20260817_orion_training_attest.sql");
  private static readonly string DatabaseSafetyVerifier = ReadRepoFile(
    "src/OrionERP.Web/Features/TrainingSafety/TrainingDatabaseSafetyVerifier.cs");
  private static readonly string TrainingSettings = ReadRepoFile(
    "src/OrionERP.Web/appsettings.Training.json");
  private static readonly string SanitizationDocs = ReadRepoFile(
    "docs/orion-training-sanitization.md");

  [Fact]
  public void SanitizerLauncher_ElevatesPreviewsAndConfirmsBeforeTheDestructiveReset()
  {
    Assert.Contains("IsInRole(", SanitizerLauncher, StringComparison.Ordinal);
    Assert.Contains("-Verb RunAs", SanitizerLauncher, StringComparison.Ordinal);

    // El servicio debe estar detenido antes de la vista previa: el preflight del
    // sanitizador rechaza cualquier otra sesión conectada al catálogo.
    var stopService = SanitizerLauncher.IndexOf("Stop-Service -Name $ServiceName", StringComparison.Ordinal);
    var sessionSweep = SanitizerLauncher.IndexOf(
      "$blocking = Get-TrainingBlockingSession",
      StringComparison.Ordinal);
    var preview = SanitizerLauncher.IndexOf(
      "& $sanitizer -ConnectionString $connectionString",
      StringComparison.Ordinal);

    // Detener el servicio no basta: una ventana ociosa de SSMS con el catálogo
    // seleccionado también dispara el bloqueo 51904 del neutralizador.
    Assert.InRange(sessionSweep, stopService + 1, preview - 1);
    Assert.Contains("DB_ID(@Database)", SanitizerLauncher, StringComparison.Ordinal);
    Assert.Contains("session_id <> @@SPID", SanitizerLauncher, StringComparison.Ordinal);

    // El diagnóstico y el cierre se hacen desde master para no contarse a sí
    // mismos, y KILL se arma con un entero ya validado contra el catálogo.
    Assert.Equal(2, CountOccurrences(SanitizerLauncher, "$builder['Initial Catalog'] = 'master'"));
    // EXECUTE (cadena) no admite una llamada a función dentro del paréntesis:
    // la sentencia se arma en una variable y se ejecuta con sp_executesql.
    Assert.Contains("EXEC sys.sp_executesql @KillStatement", SanitizerLauncher, StringComparison.Ordinal);
    Assert.DoesNotContain("EXEC(N'KILL", SanitizerLauncher, StringComparison.Ordinal);
    var confirmation = SanitizerLauncher.IndexOf("SANITIZAR", preview, StringComparison.Ordinal);
    var apply = SanitizerLauncher.IndexOf("-Apply `", StringComparison.Ordinal);

    Assert.InRange(stopService, 0, preview - 1);
    Assert.InRange(preview, stopService + 1, confirmation - 1);
    Assert.InRange(confirmation, preview + 1, apply - 1);
    Assert.Contains("-ConfirmDatabase $requiredDatabase", SanitizerLauncher, StringComparison.Ordinal);
    Assert.Contains("$requiredDatabase = 'Orion_Training'", SanitizerLauncher, StringComparison.Ordinal);

    // Nunca materializa credenciales: las contraseñas viajan como SecureString y
    // la cadena de conexión se arma en memoria, jamás se persiste.
    Assert.Contains("-AsSecureString", SanitizerLauncher, StringComparison.Ordinal);
    Assert.DoesNotContain("ConvertFrom-SecureString", SanitizerLauncher, StringComparison.Ordinal);
    Assert.DoesNotContain("PtrToStringBSTR", SanitizerLauncher, StringComparison.Ordinal);
    Assert.DoesNotContain("Set-Content", SanitizerLauncher, StringComparison.Ordinal);
    Assert.DoesNotContain("Password=", SanitizerLauncher, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("User Id=", SanitizerLauncher, StringComparison.OrdinalIgnoreCase);

    // Sólo recuerda el nombre del servidor, que no es un secreto, y nunca a nivel máquina.
    Assert.Contains("ORION_TRAINING_SANITIZER_SERVER", SanitizerLauncher, StringComparison.Ordinal);
    Assert.DoesNotContain("'Machine'", SanitizerLauncher, StringComparison.Ordinal);

    // Un reinicio incompleto deja el servicio abajo a propósito.
    var failureBranch = SanitizerLauncher.IndexOf("EL REINICIO NO SE COMPLETÓ", StringComparison.Ordinal);
    var keepStopped = SanitizerLauncher.IndexOf("queda DETENIDO", failureBranch, StringComparison.Ordinal);
    Assert.True(keepStopped > failureBranch);
  }

  [Fact]
  public void RuntimeProvisioning_UsesFixedParameterizedSqlAuthAndRealConnectionChecks()
  {
    Assert.Contains("[System.Security.SecureString]$RuntimePassword", ProvisionRuntime, StringComparison.Ordinal);
    Assert.Contains("[System.Data.SqlClient.SqlCredential]::new", ProvisionRuntime, StringComparison.Ordinal);
    Assert.Contains("Assert-BlockedCatalogConnection", ProvisionRuntime, StringComparison.Ordinal);
    Assert.Contains("$probe.Open()", ProvisionRuntime, StringComparison.Ordinal);
    Assert.Contains("$runtimeConnection.Open()", ProvisionRuntime, StringComparison.Ordinal);
    Assert.Contains("The administrative connection must explicitly target exactly master", ProvisionRuntime, StringComparison.Ordinal);
    Assert.DoesNotContain("sqlcmd", ProvisionRuntime, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("SQLCMDPASSWORD", ProvisionRuntime, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("RuntimePassword=$", ProvisionRuntime, StringComparison.OrdinalIgnoreCase);

    Assert.Contains("@RuntimePassword", RuntimeSql, StringComparison.Ordinal);
    Assert.Contains("N'orion_training_runtime'", RuntimeSql, StringComparison.Ordinal);
    Assert.Contains("CHECK_POLICY = ON", RuntimeSql, StringComparison.Ordinal);
    Assert.Contains("CHECK_EXPIRATION = ON", RuntimeSql, StringComparison.Ordinal);
    Assert.Contains("DENY INSERT, UPDATE, DELETE", RuntimeSql, StringComparison.Ordinal);
    Assert.Contains("GRANT CONNECT TO [orion_training_runtime]", RuntimeSql, StringComparison.Ordinal);
    Assert.Contains("GRANT VIEW DATABASE STATE TO [orion_training_runtime]", RuntimeSql, StringComparison.Ordinal);
    Assert.Contains("DatosSanitizados = 1 AND DatosSinteticos = 1", RuntimeSql, StringComparison.Ordinal);
    Assert.Contains("owner_sid <> 0x01", RuntimeSql, StringComparison.Ordinal);
    Assert.Contains("sys.fn_my_permissions(NULL, N''SERVER'')", RuntimeSql, StringComparison.Ordinal);
    Assert.Contains("IS_SRVROLEMEMBER(N'sysadmin')", RuntimeSql, StringComparison.Ordinal);
    Assert.Contains("sys.sql_modules WHERE execute_as_principal_id IS NOT NULL", RuntimeSql, StringComparison.Ordinal);
    Assert.Contains("referenced_server_name IS NOT NULL OR referenced_database_name IS NOT NULL", RuntimeSql, StringComparison.Ordinal);
    Assert.Contains("[grupocarpio].sys.database_principals", RuntimeSql, StringComparison.Ordinal);
    Assert.Contains("[Orion_Sandbox].sys.database_principals", RuntimeSql, StringComparison.Ordinal);
    Assert.DoesNotContain("NT SERVICE", RuntimeSql, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("USE [grupocarpio]", RuntimeSql, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("USE [Orion_Sandbox]", RuntimeSql, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("CREATE USER [orion_training_runtime] FOR LOGIN [orion_training_runtime];\n  DENY CONNECT", RuntimeSql, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public void Configure_ProtectsKeyTreeAndPinsRuntimeBoundary()
  {
    Assert.Contains("SetAccessRuleProtection($true, $false)", Configure, StringComparison.Ordinal);
    Assert.Contains("S-1-5-18", Configure, StringComparison.Ordinal);
    Assert.Contains("S-1-5-32-544", Configure, StringComparison.Ordinal);
    Assert.Contains("FileSystemRights]::Modify", Configure, StringComparison.Ordinal);
    Assert.Contains("Assert-DedicatedKeyItemAcl", Configure, StringComparison.Ordinal);
    Assert.Contains("three-principal effective-access contract", Configure, StringComparison.Ordinal);
    Assert.Contains("ReparsePoint", Configure, StringComparison.Ordinal);
    Assert.Contains("orion_training_runtime", Configure, StringComparison.Ordinal);
    Assert.Contains("Encrypt=True is required", Configure, StringComparison.Ordinal);
    Assert.Contains("AllowedHosts must include 127.0.0.1", Configure, StringComparison.Ordinal);
    Assert.Contains("PublicTrainingOrigin cannot use the OrionERP production origin", Configure, StringComparison.Ordinal);
    Assert.Contains("Protect-DedicatedServiceRegistryTree", Configure, StringComparison.Ordinal);
    Assert.Contains("RegistryRights]::FullControl", Configure, StringComparison.Ordinal);
    Assert.Contains("service registry effective-access contract failed", Configure, StringComparison.Ordinal);
  }

  [Fact]
  public void PublishSkip_IsRestrictedToTrueFirstInstall()
  {
    var guard = Publish.IndexOf(
      "$SkipServiceControl -and ($service -or $serviceRegistryExists)",
      StringComparison.Ordinal);
    var publishCall = Publish.IndexOf("Publish-prod.ps1", StringComparison.Ordinal);

    Assert.True(guard >= 0);
    Assert.True(publishCall > guard);
    Assert.Contains("true first-stage install", Publish, StringComparison.Ordinal);
  }

  [Fact]
  public void DatabaseBoundary_RequiresTheExactTwelveLocalCfdiSynonyms()
  {
    string[] synonymNames =
    [
      "Comprobante", "Concepto", "Conceptos", "Emisor", "Impuestos",
      "InformacionGlobal", "Receptor", "retencion", "Retenciones",
      "TimbreFiscalDigital", "traslado", "Traslados"
    ];

    foreach (var source in new[] { AttestSql, RuntimeSql, DatabaseSafetyVerifier })
    {
      Assert.Contains("sys.synonyms", source, StringComparison.Ordinal);
      Assert.Contains("PARSENAME", source, StringComparison.Ordinal);
      Assert.Contains("TargetSchema", source, StringComparison.Ordinal);
      Assert.Contains("OBJECT_ID(QUOTENAME(expected.TargetSchema)", source, StringComparison.Ordinal);
      foreach (var synonymName in synonymNames)
        Assert.Contains($"N'{synonymName}'", source, StringComparison.Ordinal);
    }

    Assert.Contains("(SELECT COUNT(*) FROM sys.synonyms) <> 12", AttestSql, StringComparison.Ordinal);
    Assert.DoesNotContain("CatalogoCuentas", AttestSql, StringComparison.Ordinal);
    Assert.DoesNotContain("CatalogoCuentas", RuntimeSql, StringComparison.Ordinal);
    Assert.DoesNotContain("CatalogoCuentas", DatabaseSafetyVerifier, StringComparison.Ordinal);
    Assert.DoesNotContain("synonyms are not allowed", AttestSql, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("(SELECT COUNT_BIG(*) FROM sys.synonyms)", DatabaseSafetyVerifier, StringComparison.Ordinal);
  }

  [Fact]
  public void TrainingHostnameDefaults_ArePinnedAcrossConfigurationAndRunbook()
  {
    const string host = "capacitacion.orion.land";
    const string origin = "https://capacitacion.orion.land";

    Assert.Contains(host, Configure, StringComparison.Ordinal);
    Assert.Contains(host, TrainingSettings, StringComparison.Ordinal);
    Assert.Contains(origin, TrainingSettings, StringComparison.Ordinal);
    Assert.Contains(host, SanitizationDocs, StringComparison.Ordinal);
    Assert.Contains(origin, SanitizationDocs, StringComparison.Ordinal);
  }

  private static int CountOccurrences(string value, string fragment)
  {
    var count = 0;
    var offset = 0;
    while ((offset = value.IndexOf(fragment, offset, StringComparison.Ordinal)) >= 0)
    {
      count++;
      offset += fragment.Length;
    }

    return count;
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
