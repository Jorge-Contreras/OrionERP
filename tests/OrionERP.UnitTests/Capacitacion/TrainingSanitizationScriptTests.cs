using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using OrionERP.Infrastructure.Auth;

namespace OrionERP.UnitTests.Capacitacion;

public sealed class TrainingSanitizationScriptTests
{
  private static readonly string SanitizeSql = ReadRepoFile(
    "src/OrionERP.Infrastructure/Features/Capacitacion/Sql/20260817_orion_training_sanitize.sql");
  private static readonly string ProvisionSql = ReadRepoFile(
    "src/OrionERP.Infrastructure/Features/Capacitacion/Sql/20260817_orion_training_provision.sql");
  private static readonly string AttestSql = ReadRepoFile(
    "src/OrionERP.Infrastructure/Features/Capacitacion/Sql/20260817_orion_training_attest.sql");
  private static readonly string ReviewedTriggersSql = ReadRepoFile(
    "src/OrionERP.Infrastructure/Features/Capacitacion/Sql/20260817_orion_training_reviewed_triggers.sql");
  private static readonly string NeutralizeCloneSql = ReadRepoFile(
    "src/OrionERP.Infrastructure/Features/Capacitacion/Sql/20260818_orion_training_neutralize_clone.sql");
  private static readonly string TrainingCfdiParserSql = ReadRepoFile(
    "src/OrionERP.Infrastructure/Features/Capacitacion/Sql/20260818_orion_training_cfdi_fixture_parser.sql");
  private static readonly string ScenarioSql = ReadRepoFile(
    "src/OrionERP.Infrastructure/Features/Capacitacion/Sql/20260817_orion_training_scenarios.sql");
  private static readonly string PowerShell = ReadRepoFile("Sanitize-OrionTraining.ps1");
  private static readonly string DatabaseSafetyVerifier = ReadRepoFile(
    "src/OrionERP.Web/Features/TrainingSafety/TrainingDatabaseSafetyVerifier.cs");

  [Fact]
  public void Sanitizer_GuardsExactCatalogAndSessionBeforeAnyDelete()
  {
    var catalogGuard = SanitizeSql.IndexOf(
      "DB_NAME() COLLATE Latin1_General_100_BIN2 <> N'Orion_Training'",
      StringComparison.Ordinal);
    var sessionGuard = SanitizeSql.IndexOf(
      "SESSION_CONTEXT(N'OrionTrainingSanitizerApply')",
      StringComparison.Ordinal);
    var delete = SanitizeSql.IndexOf("DELETE FROM", StringComparison.Ordinal);

    Assert.True(catalogGuard >= 0);
    Assert.True(sessionGuard > catalogGuard);
    Assert.True(delete > sessionGuard);
    Assert.DoesNotContain("USE [", SanitizeSql, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("TRUNCATE TABLE", SanitizeSql, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("DROP TABLE", SanitizeSql, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public void Sanitizer_PreservesOnlyReviewedSchemaAndCalendarReferences()
  {
    Assert.Contains("VALUES (OBJECT_ID(N'dbo.__EFMigrationsHistory'))", SanitizeSql, StringComparison.Ordinal);
    Assert.Contains("(OBJECT_ID(N'dbo.DateDimension'))", SanitizeSql, StringComparison.Ordinal);
    Assert.Contains("COUNT(*) FROM sys.columns", SanitizeSql, StringComparison.Ordinal);
    Assert.Contains("WITH CHECK CHECK CONSTRAINT ALL", SanitizeSql, StringComparison.Ordinal);
    Assert.Contains("VerifyEmptyCursor", SanitizeSql, StringComparison.Ordinal);
  }

  [Fact]
  public void PermissionSweep_ExcludesBuiltInSystemObjectGrantsFromBothRevokeAndVerify()
  {
    // Every SQL Server database ships GRANTs to public on system objects. They
    // carry a negative major_id, are absent from sys.objects, and REVOKE
    // ON OBJECT:: cannot name them, so sweeping them made the reset fail against
    // any catalog. The revoke cursor and the final verifier must stay in the same
    // scope, otherwise one of them blocks on rows the other never removes.
    const string systemObjectScope = "(permissionInfo.class = 0 OR permissionInfo.major_id > 0)";

    var occurrences = 0;
    for (var index = SanitizeSql.IndexOf(systemObjectScope, StringComparison.Ordinal);
         index >= 0;
         index = SanitizeSql.IndexOf(systemObjectScope, index + systemObjectScope.Length, StringComparison.Ordinal))
    {
      occurrences++;
    }

    Assert.Equal(2, occurrences);

    var revokeScope = SanitizeSql.IndexOf(systemObjectScope, StringComparison.Ordinal);
    var revokeStatement = SanitizeSql.IndexOf("N'REVOKE '", StringComparison.Ordinal);
    var verifyScope = SanitizeSql.LastIndexOf(systemObjectScope, StringComparison.Ordinal);
    var retainedPermissionGuard = SanitizeSql.IndexOf(
      "SANITIZATION FAILED: public, guest, or a fixed role retained an explicit permission.",
      StringComparison.Ordinal);

    Assert.True(revokeScope >= 0);
    Assert.True(revokeScope < revokeStatement);
    Assert.True(verifyScope > revokeStatement);
    Assert.True(verifyScope < retainedPermissionGuard);
  }

  [Fact]
  public void RoleMembershipSweep_ExcludesTheBuiltInDboMembershipFromBothDropAndVerify()
  {
    // dbo is a member of db_owner in every SQL Server database and cannot be
    // dropped (error 15405), so neither the sweep nor the verifier may include it.
    // Requiring sys.database_role_members to be empty would fail on any catalog.
    Assert.DoesNotContain(
      "OR EXISTS (SELECT 1 FROM sys.database_role_members)",
      SanitizeSql,
      StringComparison.Ordinal);

    var sweepScope = SanitizeSql.IndexOf("AND memberInfo.principal_id > 4", StringComparison.Ordinal);
    var dropMember = SanitizeSql.IndexOf("N' DROP MEMBER '", StringComparison.Ordinal);
    var verifyScope = SanitizeSql.IndexOf(
      "WHERE membership.member_principal_id > 4",
      StringComparison.Ordinal);
    var survivedGuard = SanitizeSql.IndexOf(
      "SANITIZATION FAILED: a cloned database principal or role membership survived.",
      StringComparison.Ordinal);

    Assert.True(sweepScope >= 0);
    Assert.True(sweepScope < dropMember);
    Assert.True(verifyScope > dropMember);
    Assert.True(verifyScope < survivedGuard);
  }

  [Fact]
  public void Sanitizer_DisablesAllDmlTriggersBeforeItsFirstUserTableWriteAndNeverReenablesThem()
  {
    var disableTriggers = SanitizeSql.IndexOf(
      "DISABLE TRIGGER ALL ON",
      StringComparison.Ordinal);
    var clearAttestation = SanitizeSql.IndexOf(
      "UPDATE capacitacion.EntornoSeguridad",
      StringComparison.Ordinal);
    var eraseRows = SanitizeSql.IndexOf(
      "N'DELETE FROM ' + @QualifiedName",
      StringComparison.Ordinal);

    Assert.True(disableTriggers >= 0);
    Assert.True(clearAttestation > disableTriggers);
    Assert.True(eraseRows > clearAttestation);
    Assert.DoesNotContain("ENABLE TRIGGER ALL", SanitizeSql, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("RestoreConstraintCursor", SanitizeSql, StringComparison.Ordinal);
  }

  [Fact]
  public void TriggerSchemaQueries_ResolveSchemaThroughTheTriggerObject()
  {
    foreach (var source in new[] { PowerShell, AttestSql, DatabaseSafetyVerifier })
    {
      Assert.DoesNotContain("triggerInfo.schema_id", source, StringComparison.OrdinalIgnoreCase);
      Assert.Contains(
        "JOIN sys.objects triggerObject ON triggerObject.object_id = triggerInfo.object_id",
        source,
        StringComparison.Ordinal);
      Assert.Contains(
        "JOIN sys.schemas triggerSchema ON triggerSchema.schema_id = triggerObject.schema_id",
        source,
        StringComparison.Ordinal);
    }
  }

  [Fact]
  public void GuardedTrainingSql_RejectsAMissingSessionContextInsteadOfAcceptingSqlNull()
  {
    foreach (var source in new[]
             {
               SanitizeSql, ProvisionSql, AttestSql, ScenarioSql, ReviewedTriggersSql,
               NeutralizeCloneSql, TrainingCfdiParserSql
             })
    {
      var guardLines = source.Split('\n')
        .Where(line => line.Contains("SESSION_CONTEXT(", StringComparison.Ordinal)
                       && line.Contains("OrionTrainingSanitizerApply", StringComparison.Ordinal))
        .ToArray();
      Assert.NotEmpty(guardLines);
      Assert.All(guardLines, line => Assert.Contains(
        "ISNULL(TRY_CONVERT(nvarchar(64), SESSION_CONTEXT(",
        line,
        StringComparison.Ordinal));
    }
  }

  [Fact]
  public void CloneNeutralizer_RejectsOtherTrainingSessionsBeforeItsFirstTransactionAndDrop()
  {
    var sessionCheck = NeutralizeCloneSql.IndexOf("FROM sys.dm_exec_sessions", StringComparison.Ordinal);
    var transaction = NeutralizeCloneSql.IndexOf("BEGIN TRANSACTION", StringComparison.Ordinal);
    var firstDrop = NeutralizeCloneSql.IndexOf("DROP SECURITY POLICY", StringComparison.Ordinal);

    Assert.True(sessionCheck >= 0);
    Assert.True(transaction > sessionCheck);
    Assert.True(firstDrop > transaction);
    Assert.Contains("sessionInfo.database_id = DB_ID()", NeutralizeCloneSql, StringComparison.Ordinal);
    Assert.Contains("sessionInfo.session_id <> @@SPID", NeutralizeCloneSql, StringComparison.Ordinal);
    Assert.Contains("ISNULL(IS_SRVROLEMEMBER(N'sysadmin'), 0) <> 1", NeutralizeCloneSql, StringComparison.Ordinal);
  }

  [Fact]
  public void OnlyFinalVerifierCanIssuePositiveDataAttestation()
  {
    Assert.DoesNotContain("DatosSanitizados = 1", SanitizeSql, StringComparison.Ordinal);
    Assert.DoesNotContain("DatosSinteticos = 1", SanitizeSql, StringComparison.Ordinal);
    Assert.DoesNotContain("DatosSanitizados = 1", ProvisionSql, StringComparison.Ordinal);
    Assert.DoesNotContain("DatosSinteticos = 1", ProvisionSql, StringComparison.Ordinal);

    Assert.Contains("DatosSanitizados = 1", AttestSql, StringComparison.Ordinal);
    Assert.Contains("DatosSinteticos = 1", AttestSql, StringComparison.Ordinal);
    Assert.Contains("@AllowedNonEmpty", AttestSql, StringComparison.Ordinal);
    Assert.Contains("UnexpectedTableCursor", AttestSql, StringComparison.Ordinal);
  }

  [Fact]
  public void Provisioner_UsesRecognizableSyntheticPeopleAndScenarios()
  {
    Assert.Contains("@training.orion.local", ProvisionSql, StringComparison.Ordinal);
    Assert.Contains("XAXX010101000", ProvisionSql, StringComparison.Ordinal);
    Assert.Contains("NO-ES-CREDENCIAL", ProvisionSql, StringComparison.Ordinal);
    Assert.Contains("DATOS SINTÉTICOS", ProvisionSql, StringComparison.Ordinal);
    Assert.Contains("TRN-SUITE-01", ProvisionSql, StringComparison.Ordinal);
    Assert.Contains("TRN-MAT-001", ProvisionSql, StringComparison.Ordinal);
    Assert.Contains("N'LOGISTICA-OPERACION'", ProvisionSql, StringComparison.Ordinal);
    Assert.Contains("N'RH-CAPITAL-HUMANO'", ProvisionSql, StringComparison.Ordinal);
    Assert.Contains(") <> 5", ProvisionSql, StringComparison.Ordinal);

    // trainee01 is assigned the complete module program through the reviewed
    // learning path; trainee02 keeps the five-course pilot cohort. The
    // attestation counts both shapes instead of a single fixed total.
    Assert.Contains("N'ORION-EXPERTO'", ProvisionSql, StringComparison.Ordinal);
    Assert.Contains("@CursoManifiesto", AttestSql, StringComparison.Ordinal);
    Assert.Contains("@CursosEsperados int = (SELECT COUNT(*) FROM @CursoManifiesto)", AttestSql, StringComparison.Ordinal);
    Assert.Contains(
      "(SELECT COUNT(*) FROM capacitacion.Asignacion) <> (@CursosEsperados + 5)",
      AttestSql,
      StringComparison.Ordinal);
    Assert.Contains(
      "(SELECT COUNT(*) FROM capacitacion.Asignacion WHERE EmployeeId = 990002) <> @CursosEsperados",
      AttestSql,
      StringComparison.Ordinal);
    Assert.Contains(
      "(SELECT COUNT(*) FROM capacitacion.Asignacion WHERE EmployeeId = 990003) <> 5",
      AttestSql,
      StringComparison.Ordinal);
    Assert.Contains("N'SatOperator'", ProvisionSql, StringComparison.Ordinal);
    Assert.Contains("N'Logistica'", ProvisionSql, StringComparison.Ordinal);
    Assert.Contains("N'Lectura'", ProvisionSql, StringComparison.Ordinal);
    Assert.Contains(
      "(N'training-user-instructor-v1', N'training-role-sat-operator')",
      ProvisionSql,
      StringComparison.Ordinal);
    Assert.Contains(
      "(N'training-user-instructor-v1', N'training-role-logistica')",
      ProvisionSql,
      StringComparison.Ordinal);
    Assert.Contains("AspNetUserRoles) <> 22", AttestSql, StringComparison.Ordinal);

    // Los participantes reciben los roles de módulo necesarios para practicar
    // todo el currículo, pero Administrador queda reservado al instructor: es la
    // única cuenta que administra el entorno y la que abre el portal de
    // seguridad. La nómina sigue fuera del alcance de la capacitación.
    Assert.Contains(
      "(N'training-user-instructor-v1', N'training-role-administrador')",
      ProvisionSql,
      StringComparison.Ordinal);
    foreach (var trainee in new[] { "trainee01", "trainee02" })
    {
      Assert.DoesNotContain(
        $"(N'training-user-{trainee}-v1', N'training-role-administrador')",
        ProvisionSql,
        StringComparison.Ordinal);
      Assert.DoesNotContain(
        $"(N'training-user-{trainee}-v1', N'training-role-cap-admin')",
        ProvisionSql,
        StringComparison.Ordinal);
    }

    Assert.DoesNotContain("N'CapitalHumanoNomina'", ProvisionSql, StringComparison.Ordinal);
    Assert.DoesNotContain("N'Conteo'", ProvisionSql, StringComparison.Ordinal);
    Assert.DoesNotContain("INSERT cfdi.", ProvisionSql, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("INSERT bancos.", ProvisionSql, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("CREATE LOGIN", ProvisionSql, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("ALTER LOGIN", ProvisionSql, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("orion_training_runtime", ProvisionSql, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public void CohortPasswordHash_UsesAnAspNetIdentityV3ShapeAcceptedByTheApplication()
  {
    const string password = "Training-Test1!";
    byte[] salt = Enumerable.Range(1, 16).Select(value => (byte)value).ToArray();
    byte[] subkey = Rfc2898DeriveBytes.Pbkdf2(
      Encoding.UTF8.GetBytes(password),
      salt,
      100_000,
      HashAlgorithmName.SHA256,
      32);
    byte[] payload = new byte[13 + salt.Length + subkey.Length];
    payload[0] = 0x01;
    BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(1, 4), 1);
    BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(5, 4), 100_000);
    BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(9, 4), (uint)salt.Length);
    salt.CopyTo(payload, 13);
    subkey.CopyTo(payload, 13 + salt.Length);

    var encoded = Convert.ToBase64String(payload);
    var result = new PasswordHasher<ApplicationUser>().VerifyHashedPassword(
      new ApplicationUser(), encoded, password);

    Assert.True(result is PasswordVerificationResult.Success or PasswordVerificationResult.SuccessRehashNeeded);
    Assert.Contains("$payload[0] = 0x01", PowerShell, StringComparison.Ordinal);
    Assert.Contains("-Offset 1 -Value 1", PowerShell, StringComparison.Ordinal);
    Assert.Contains("-Offset 5 -Value 100000", PowerShell, StringComparison.Ordinal);
    Assert.Contains("-Offset 9 -Value ([uint32]$salt.Length)", PowerShell, StringComparison.Ordinal);
    Assert.Contains("HashAlgorithmName]::SHA256", PowerShell, StringComparison.Ordinal);
  }

  [Fact]
  public void PowerShell_IsPreviewFirstAndNeverRewritesTheCatalog()
  {
    Assert.Contains("[switch]$Apply", PowerShell, StringComparison.Ordinal);
    Assert.Contains("[ValidateSet('Orion_Training')]", PowerShell, StringComparison.Ordinal);
    Assert.Contains("-Apply also requires -ConfirmDatabase Orion_Training", PowerShell, StringComparison.Ordinal);
    Assert.Contains("Initial Catalog must be exactly", PowerShell, StringComparison.Ordinal);
    Assert.Contains("Preview only. No data was changed.", PowerShell, StringComparison.Ordinal);
    Assert.DoesNotContain(".InitialCatalog =", PowerShell, StringComparison.Ordinal);
    Assert.DoesNotContain("ChangeDatabase", PowerShell, StringComparison.Ordinal);
  }

  [Fact]
  public void PowerShell_RequiresEncryptedTransportAndPreflightsExternalEffectsBeforeWrites()
  {
    Assert.Contains("sanitizer administrative connection must use Encrypt=True", PowerShell, StringComparison.Ordinal);
    Assert.Contains("PREFLIGHT BLOCKED", PowerShell, StringComparison.Ordinal);
    Assert.Contains("sys.external_data_sources", PowerShell, StringComparison.Ordinal);
    Assert.Contains("sys.database_scoped_credentials", PowerShell, StringComparison.Ordinal);
    Assert.Contains("unreviewed DML trigger", PowerShell, StringComparison.Ordinal);

    var firstPreflightCall = PowerShell.IndexOf(
      "Assert-TrainingPreProvisionSafety -Connection $connection",
      PowerShell.IndexOf("$connection.Open()", StringComparison.Ordinal),
      StringComparison.Ordinal);
    var firstInstallerCall = PowerShell.IndexOf(
      "Invoke-SqlFile -Connection $connection -Path $capacitacionInstaller",
      StringComparison.Ordinal);
    var provisionCall = PowerShell.IndexOf(
      "Invoke-SqlFile -Connection $connection -Path $provisionScript",
      StringComparison.Ordinal);
    var secondPreflightCall = PowerShell.IndexOf(
      "Assert-TrainingPreProvisionSafety -Connection $connection",
      firstInstallerCall + 1,
      StringComparison.Ordinal);

    Assert.InRange(firstPreflightCall, 0, firstInstallerCall - 1);
    Assert.InRange(secondPreflightCall, firstInstallerCall + 1, provisionCall - 1);
  }

  [Fact]
  public void CloneNeutralization_IsExactGuardedAndPreviewedInsideARollbackOnlyTransaction()
  {
    Assert.Contains(
      "DB_NAME() COLLATE Latin1_General_100_BIN2 <> N'Orion_Training'",
      NeutralizeCloneSql,
      StringComparison.Ordinal);
    Assert.Contains("IS_SRVROLEMEMBER(N'sysadmin')", NeutralizeCloneSql, StringComparison.Ordinal);
    Assert.Contains(
      "SESSION_CONTEXT(N'OrionTrainingSanitizerApply')",
      NeutralizeCloneSql,
      StringComparison.Ordinal);
    Assert.DoesNotContain("\nGO", NeutralizeCloneSql, StringComparison.OrdinalIgnoreCase);
    Assert.Equal(
      1,
      NeutralizeCloneSql.Split("DROP SYNONYM", StringSplitOptions.None).Length - 1);
    Assert.Contains(
      "DROP SYNONYM [dbo].[CatalogoCuentas]",
      NeutralizeCloneSql,
      StringComparison.Ordinal);
    Assert.Contains("RfcSecurityPolicy", NeutralizeCloneSql, StringComparison.Ordinal);
    Assert.Contains("fn_RfcAccessPredicate", NeutralizeCloneSql, StringComparison.Ordinal);
    Assert.Contains(
      "CB84A46AA3F82101055CAB282775330FD8DA0C7EC0BA443BCD1A7A2A70AC552F",
      NeutralizeCloneSql,
      StringComparison.Ordinal);
    Assert.Contains(
      "7F8EEA45D2446B144BB477A244E6B266C261FB5718DC2766ED0B452B7AC717D9",
      NeutralizeCloneSql,
      StringComparison.Ordinal);
    Assert.Contains("CatalogoCuentas", NeutralizeCloneSql, StringComparison.Ordinal);
    Assert.Contains("TimbreFiscalDigital", NeutralizeCloneSql, StringComparison.Ordinal);

    var open = PowerShell.IndexOf("$connection.Open()", StringComparison.Ordinal);
    var begin = PowerShell.IndexOf("'BEGIN TRANSACTION;'", open, StringComparison.Ordinal);
    var previewNeutralize = PowerShell.IndexOf(
      "Invoke-SqlFile -Connection $connection -Path $neutralizeCloneScript",
      begin,
      StringComparison.Ordinal);
    var previewPreflight = PowerShell.IndexOf(
      "Assert-TrainingPreProvisionSafety -Connection $connection",
      previewNeutralize,
      StringComparison.Ordinal);
    var rollback = PowerShell.IndexOf(
      "IF XACT_STATE() <> 0 ROLLBACK TRANSACTION",
      previewPreflight,
      StringComparison.Ordinal);
    var applyGate = PowerShell.IndexOf("if (-not $Apply)", rollback, StringComparison.Ordinal);
    var appliedNeutralize = PowerShell.IndexOf(
      "Invoke-SqlFile -Connection $connection -Path $neutralizeCloneScript",
      previewNeutralize + 1,
      StringComparison.Ordinal);
    var sanitize = PowerShell.IndexOf(
      "Invoke-SqlFile -Connection $connection -Path $sanitizeScript",
      StringComparison.Ordinal);

    Assert.InRange(begin, open + 1, previewNeutralize - 1);
    Assert.InRange(previewPreflight, previewNeutralize + 1, rollback - 1);
    Assert.InRange(applyGate, rollback + 1, appliedNeutralize - 1);
    Assert.InRange(appliedNeutralize, applyGate + 1, sanitize - 1);
  }

  [Fact]
  public void AppliedReset_InstallsTheTrainingOnlyCfdiParserBeforeSyntheticProvisioning()
  {
    var installer = PowerShell.IndexOf(
      "Invoke-SqlFile -Connection $connection -Path $capacitacionInstaller",
      StringComparison.Ordinal);
    var parser = PowerShell.IndexOf(
      "Invoke-SqlFile -Connection $connection -Path $trainingCfdiParserScript",
      StringComparison.Ordinal);
    var preflight = PowerShell.IndexOf(
      "Assert-TrainingPreProvisionSafety -Connection $connection",
      parser,
      StringComparison.Ordinal);
    var provision = PowerShell.IndexOf(
      "Invoke-SqlFile -Connection $connection -Path $provisionScript",
      StringComparison.Ordinal);

    Assert.InRange(parser, installer + 1, preflight - 1);
    Assert.InRange(preflight, parser + 1, provision - 1);
    Assert.Contains(
      "OrionERP.Training.CfdiFixtureParser.v1:6B5863304AA8E607EBE20A274A2AF84042EB7001906AB0C505E9B4AB2E71040B",
      TrainingCfdiParserSql,
      StringComparison.Ordinal);
  }

  [Fact]
  public void AppliedReset_RestoresTheRuntimeDatabaseUserOnlyAfterAPositiveAttestation()
  {
    var attest = PowerShell.IndexOf(
      "Invoke-SqlFile -Connection $connection -Path $attestScript",
      StringComparison.Ordinal);
    var attestationGate = PowerShell.IndexOf(
      "$finalInventory.DataAttestation -ne 'ATTESTED'",
      StringComparison.Ordinal);
    var restore = PowerShell.IndexOf(
      "Restore-TrainingRuntimeDatabaseUser -Connection $connection",
      StringComparison.Ordinal);

    // A reset that ended halfway must leave the environment unreachable rather
    // than handing a runtime principal to half-sanitized data.
    Assert.InRange(attestationGate, attest + 1, restore - 1);

    // The sanitizer drops every SQL database user, so the runtime mapping has to
    // come back or OrionERP.Training cannot open Orion_Training (SQL error 4060).
    // This manifest must stay identical to 20260817_training_runtime_login.sql,
    // because TrainingDatabaseSafetyVerifier refuses to boot when it drifts.
    Assert.Contains(
      "CREATE USER [orion_training_runtime] FOR LOGIN [orion_training_runtime];",
      PowerShell,
      StringComparison.Ordinal);
    Assert.Contains(
      "ALTER ROLE [db_datareader] ADD MEMBER [orion_training_runtime];",
      PowerShell,
      StringComparison.Ordinal);
    Assert.Contains(
      "ALTER ROLE [db_datawriter] ADD MEMBER [orion_training_runtime];",
      PowerShell,
      StringComparison.Ordinal);
    Assert.Contains(
      "GRANT VIEW DATABASE STATE TO [orion_training_runtime];",
      PowerShell,
      StringComparison.Ordinal);
    Assert.Contains(
      "ON OBJECT::capacitacion.EntornoSeguridad",
      PowerShell,
      StringComparison.Ordinal);
    Assert.Contains(
      "ON OBJECT::capacitacion.EsquemaVersion",
      PowerShell,
      StringComparison.Ordinal);

    // Restoring the mapping needs no secret, so the login and its password stay
    // owned by the separate least-privilege provisioning workflow.
    Assert.DoesNotContain("CREATE LOGIN", PowerShell, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("ALTER LOGIN", PowerShell, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("WITH PASSWORD", PowerShell, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public void AppliedReset_SuppressesOldTriggersAndRebuildsSensitiveMetadataBeforeAttestation()
  {
    var firstDisable = PowerShell.IndexOf(
      "Set-TrainingDmlTriggersEnabled -Connection $connection -Enabled $false",
      StringComparison.Ordinal);
    var installer = PowerShell.IndexOf(
      "Invoke-SqlFile -Connection $connection -Path $capacitacionInstaller",
      StringComparison.Ordinal);
    var reviewedTriggers = PowerShell.IndexOf(
      "Invoke-SqlFile -Connection $connection -Path $reviewedTriggerScript",
      StringComparison.Ordinal);
    var secondDisable = PowerShell.IndexOf(
      "Set-TrainingDmlTriggersEnabled -Connection $connection -Enabled $false",
      firstDisable + 1,
      StringComparison.Ordinal);
    var provision = PowerShell.IndexOf(
      "Invoke-SqlFile -Connection $connection -Path $provisionScript",
      StringComparison.Ordinal);
    var statistics = PowerShell.IndexOf(
      "Update-TrainingStatisticsFullScan -Connection $connection",
      StringComparison.Ordinal);
    var attest = PowerShell.IndexOf(
      "Invoke-SqlFile -Connection $connection -Path $attestScript",
      StringComparison.Ordinal);

    Assert.InRange(firstDisable, 0, installer - 1);
    Assert.InRange(reviewedTriggers, installer + 1, secondDisable - 1);
    Assert.InRange(secondDisable, reviewedTriggers + 1, provision - 1);
    Assert.InRange(statistics, provision + 1, attest - 1);
    Assert.Contains("UPDATE STATISTICS", PowerShell, StringComparison.Ordinal);
    Assert.Contains("WITH FULLSCAN", PowerShell, StringComparison.Ordinal);
    Assert.Contains("DML triggers remain disabled", PowerShell, StringComparison.Ordinal);

    Assert.Contains("SESSION_CONTEXT(N'OrionTrainingSanitizerApply')", ReviewedTriggersSql, StringComparison.Ordinal);
    Assert.DoesNotContain("\nGO", ReviewedTriggersSql, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("EXEC sys.sp_executesql N'CREATE OR ALTER TRIGGER", ReviewedTriggersSql, StringComparison.Ordinal);
    Assert.Contains("TR_EntornoSeguridad_MaintenanceOnly", ReviewedTriggersSql, StringComparison.Ordinal);
    Assert.Contains("TR_EsquemaVersion_MaintenanceOnly", ReviewedTriggersSql, StringComparison.Ordinal);
    Assert.Contains("ORIGINAL_LOGIN() = N''orion_training_runtime''", ReviewedTriggersSql, StringComparison.Ordinal);
  }

  [Fact]
  public void ResetAndAttestation_RemoveCloneEscapeAndMetadataSurfaces()
  {
    Assert.Contains("DBCC CHECKIDENT", SanitizeSql, StringComparison.Ordinal);
    Assert.Contains("ALTER SEQUENCE", SanitizeSql, StringComparison.Ordinal);
    Assert.Contains("QUERY_STORE CLEAR ALL", SanitizeSql, StringComparison.Ordinal);
    Assert.Contains("sys.security_policies", AttestSql, StringComparison.Ordinal);
    Assert.Contains("sys.database_query_store_options", AttestSql, StringComparison.Ordinal);
    Assert.Contains("sys.database_principals", AttestSql, StringComparison.Ordinal);
    Assert.Contains("sys.database_permissions", AttestSql, StringComparison.Ordinal);
    Assert.Contains("sys.certificates", AttestSql, StringComparison.Ordinal);
    Assert.Contains("sys.dm_db_stats_properties", AttestSql, StringComparison.Ordinal);
    Assert.Contains("owner_sid <> 0x01", AttestSql, StringComparison.Ordinal);
  }

  [Fact]
  public void SyntheticAccounts_RequireFourDistinctSecurePasswords()
  {
    Assert.Contains("[Security.SecureString]$InstructorPassword", PowerShell, StringComparison.Ordinal);
    Assert.Contains("[Security.SecureString]$Trainee01Password", PowerShell, StringComparison.Ordinal);
    Assert.Contains("[Security.SecureString]$Trainee02Password", PowerShell, StringComparison.Ordinal);
    Assert.Contains("[Security.SecureString]$AuditorPassword", PowerShell, StringComparison.Ordinal);
    Assert.Contains("Select-Object -Unique", PowerShell, StringComparison.Ordinal);
    Assert.DoesNotContain("$InitialPassword", PowerShell, StringComparison.Ordinal);
  }

  [Fact]
  public void ReviewedSql_AvoidsReservedAliasesAndNamedTableVariableConstraints()
  {
    // ROWCOUNT is a reserved T-SQL keyword, so it cannot be used as an unquoted
    // column alias: "Incorrect syntax near the keyword 'RowCount'".
    Assert.DoesNotContain("AS RowCount", ProvisionSql, StringComparison.Ordinal);
    Assert.DoesNotContain("rowInfo.RowCount", ProvisionSql, StringComparison.Ordinal);
    Assert.Contains("AS TableRowCount", ProvisionSql, StringComparison.Ordinal);
    Assert.Contains("rowInfo.TableRowCount", ProvisionSql, StringComparison.Ordinal);

    // A table variable cannot carry a named constraint; only the inline unnamed
    // form parses. This one gates the final positive attestation.
    Assert.DoesNotContain("CONSTRAINT PK_AllowedTrainingNonEmpty", AttestSql, StringComparison.Ordinal);
    Assert.Contains("PRIMARY KEY (SchemaName, TableName)", AttestSql, StringComparison.Ordinal);
  }

  [Fact]
  public void Attestation_UsesTheSameBuiltInStateScopesAsTheSanitizer()
  {
    // The attestation guards duplicated the sanitizer's original defects: they
    // treated permanent built-in catalog state (the dbo membership in db_owner,
    // and the system-object GRANTs to public) as contamination, which made a
    // positive attestation impossible against any real database.
    Assert.DoesNotContain(
      "OR EXISTS (SELECT 1 FROM sys.database_role_members)",
      AttestSql,
      StringComparison.Ordinal);

    var membershipScope = AttestSql.IndexOf(
      "WHERE membership.member_principal_id > 4",
      StringComparison.Ordinal);
    var membershipGuard = AttestSql.IndexOf(
      "ATTESTATION BLOCKED: a cloned database principal or role membership survived.",
      StringComparison.Ordinal);
    var permissionScope = AttestSql.IndexOf(
      "AND (permissionInfo.class = 0 OR permissionInfo.major_id > 0)",
      StringComparison.Ordinal);
    var permissionGuard = AttestSql.IndexOf(
      "ATTESTATION BLOCKED: public, guest, or a fixed role retained an explicit permission.",
      StringComparison.Ordinal);

    Assert.True(membershipScope >= 0);
    Assert.True(membershipScope < membershipGuard);
    Assert.True(permissionScope > membershipGuard);
    Assert.True(permissionScope < permissionGuard);
  }

  [Fact]
  public void CloneNeutralizer_RecognisesTheTrainingParserByItsStoredDefinitionHash()
  {
    // SQL Server records a CREATE OR ALTER module with "OR ALTER" blanked out, and
    // this parser body ships inside a dynamic-SQL literal whose escaped '' quotes
    // collapse on execution. A hash taken over the raw installer text therefore
    // never matches sys.sql_modules, which left the "already the Training parser"
    // recovery branch unreachable and blocked every re-run after a partial reset.
    Assert.DoesNotContain(
      "3F652AB747ABB522A31DFA5A555D323DB90CA9570EBD70992B660CAEE83F29FC",
      NeutralizeCloneSql,
      StringComparison.OrdinalIgnoreCase);
    Assert.Contains(
      "0x0078FB57089DE8378CA77BFF968AEF21A7D338D5C086FB61F8262998E16925A9",
      NeutralizeCloneSql,
      StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public void ResetStartMarker_UsesLocalServerTimeToMatchStatisticsTimestamps()
  {
    // sys.dm_db_stats_properties.last_updated is recorded in local server time.
    // A UTC marker sits ahead of every local timestamp on any server behind UTC,
    // so the FULLSCAN attestation guard could never be satisfied. Both values are
    // produced by the same server clock, so they must share the same basis.
    Assert.DoesNotContain(
      "DECLARE @SanitizerStartedAt datetime = CONVERT(datetime, GETUTCDATE());",
      PowerShell,
      StringComparison.Ordinal);
    Assert.Contains(
      "DECLARE @SanitizerStartedAt datetime = CONVERT(datetime, GETDATE());",
      PowerShell,
      StringComparison.Ordinal);

    // The marker is only meaningful because the FULLSCAN runs before attestation.
    var fullScan = PowerShell.IndexOf("Update-TrainingStatisticsFullScan -Connection", StringComparison.Ordinal);
    var attestCall = PowerShell.IndexOf("-Path $attestScript", StringComparison.Ordinal);
    Assert.True(fullScan >= 0);
    Assert.True(attestCall > fullScan);
    Assert.Contains("propertiesInfo.last_updated < @SanitizerStartedAt", AttestSql, StringComparison.Ordinal);
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
