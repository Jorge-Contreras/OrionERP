using OrionERP.UnitTests.Common;

namespace OrionERP.UnitTests.Platform;

public sealed class RfcTenantIsolationRemediationTests
{
  private static readonly string Migration = RepoFile.Read(
    "src/OrionERP.Infrastructure/Features/Platform/Sql/20260912_rfc_tenant_isolation_expand.sql");
  private static readonly string CompanyControls = RepoFile.Read(
    "src/OrionERP.Infrastructure/Features/Platform/Sql/20260912_rfc_tenant_isolation_company_controls.sql");
  private static readonly string FailClosedPrincipals = RepoFile.Read(
    "src/OrionERP.Infrastructure/Features/Platform/Sql/20260912_rfc_tenant_isolation_fail_closed_principals.sql");

  [Fact]
  public void Migration_IsPreviewableAuditedAndKeepsOriginalVendorsWithOhm()
  {
    Assert.Contains("$(ApplyChanges)", Migration, StringComparison.Ordinal);
    Assert.Contains("ROLLBACK TRANSACTION", Migration, StringComparison.Ordinal);
    Assert.Contains("TenantIsolationMigrationAudit", Migration, StringComparison.Ordinal);
    Assert.Contains("WHEN partner.Id IN(8,49,109,110,111) THEN @OhmRfc", Migration, StringComparison.Ordinal);
    Assert.Contains("INSERT #VendorMap(SourceId,TargetId) VALUES(8,112)", Migration, StringComparison.Ordinal);
    Assert.Contains("target.MaterialId=6933", Migration, StringComparison.Ordinal);
  }

  [Fact]
  public void Migration_EnforcesTenantAwareKeysAndFailClosedRls()
  {
    Assert.Contains("FK_MaterialVendor_OwnerPartner", Migration, StringComparison.Ordinal);
    Assert.Contains("FK_BusinessPartnerCfdiProfile_OwnerPartner", Migration, StringComparison.Ordinal);
    Assert.Contains("FK_Transacciones_Rfc_FormaPago", Migration, StringComparison.Ordinal);
    Assert.Contains("FK_PurchaseOrderRoomScope_CompanyRoom", Migration, StringComparison.Ordinal);
    Assert.Contains("FK_OrdenTrabajo_Rfc_Owner", Migration, StringComparison.Ordinal);
    Assert.Contains("ADD BLOCK PREDICATE logistica.fn_RfcAccessPredicate", Migration, StringComparison.Ordinal);
    Assert.Contains("DROP TABLE dbo.BusinessPartnerRfcScope", Migration, StringComparison.Ordinal);
    Assert.Contains("contabilidad.AccountingPeriod", CompanyControls, StringComparison.Ordinal);
    Assert.Contains("contabilidad.CompanyCycleActivation", CompanyControls, StringComparison.Ordinal);
    Assert.Contains("contabilidad.HospitalityAccountingMapping", CompanyControls, StringComparison.Ordinal);
    Assert.Contains("GLOBAL_PLATFORM_METADATA", CompanyControls, StringComparison.Ordinal);
    Assert.Contains("REMOVE_DBO_BYPASS", FailClosedPrincipals, StringComparison.Ordinal);
    Assert.Contains("NO_UNSCOPED_DBO_BYPASS", FailClosedPrincipals, StringComparison.Ordinal);
  }

  [Fact]
  public void Validation_FailsForUnknownTablesAndCrossCompanyRelationships()
  {
    var validation = RepoFile.Read("database/validation/validate-rfc-tenant-isolation.sql");
    Assert.Contains("tablas nuevas sin clasificación", validation, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("llave foránea tenant-aware", validation, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("policyInfo.is_enabled=1", validation, StringComparison.Ordinal);
    Assert.Contains("materiales ligados a proveedores de otra empresa", validation, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("RFC_TENANT_ISOLATION_OK", validation, StringComparison.Ordinal);
  }

  [Fact]
  public void CompanyFacingCatalogsAndPartners_DoNotUseGlobalCrudOrTheScopeBridge()
  {
    var catalogs = RepoFile.Read("src/OrionERP.Infrastructure/Features/Ajustes/Catalogos/CatalogoService.cs");
    var partners = RepoFile.Read("src/OrionERP.Infrastructure/Features/Logistica/BusinessPartners/BusinessPartnerService.cs");
    var partnerLifecycle = RepoFile.Read("src/OrionERP.Infrastructure/Features/Logistica/BusinessPartners/BusinessPartnerService.Lifecycle.cs");
    var materialModels = RepoFile.Read("src/OrionERP.Application/Features/Logistica/Materials/MaterialModels.cs");

    Assert.DoesNotContain("EsPorRfc = false", catalogs, StringComparison.Ordinal);
    Assert.Contains("AccountingConnectionFactory.InitializeAsync", catalogs, StringComparison.Ordinal);
    Assert.DoesNotContain("BusinessPartnerRfcScope", partners, StringComparison.Ordinal);
    Assert.DoesNotContain("BusinessPartnerRfcScope", partnerLifecycle, StringComparison.Ordinal);
    Assert.Contains("public string Rfc { get; set; }", materialModels, StringComparison.Ordinal);
  }

  [Fact]
  public void CompanyFacingPersistence_DoesNotIntroduceUnscopedSqlConnections()
  {
    var root = FindRepositoryRoot();
    var sourceRoot = Path.Combine(root, "src");
    var rawConnectionFiles = Directory.GetFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
      .Where(path => File.ReadAllText(path).Contains("new SqlConnection(", StringComparison.Ordinal))
      .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
      .OrderBy(path => path, StringComparer.Ordinal)
      .ToArray();

    var reviewedFiles = new HashSet<string>(StringComparer.Ordinal)
    {
      "src/OrionERP.DatabaseMigrator/MigrationRunner.cs",
      "src/OrionERP.Infrastructure/Features/Ajustes/Catalogos/CatalogoService.cs",
      "src/OrionERP.Infrastructure/Features/Cfdi/DescargaMasiva/Dapper/SqlConnectionFactory.cs",
      "src/OrionERP.Infrastructure/Features/Contabilidad/Transacciones/AccountingConnectionFactory.cs",
      "src/OrionERP.Infrastructure/Features/Contabilidad/Transacciones/Services/TransaccionService.cs",
      "src/OrionERP.Infrastructure/Features/Hospitality/PublicBooking/HospitalityPublicBookingService.cs",
      "src/OrionERP.Infrastructure/Features/Hospitality/PublicBooking/HospitalityPublicDataReader.cs",
      "src/OrionERP.Infrastructure/Features/Platform/CompanyIdentityResolver.cs",
      "src/OrionERP.Infrastructure/Features/Platform/OrionSqlSessionFactory.cs",
      "src/OrionERP.Infrastructure/Features/Platform/PublicSiteProvisioner.cs",
      "src/OrionERP.Infrastructure/Features/Platform/PublicWebsiteSqlConnectionFactory.cs",
      "src/OrionERP.Infrastructure/Features/Reservaciones/CalendarSync/OutlookRoomCalendarSyncRepository.cs",
      "src/OrionERP.Infrastructure/Features/Reservaciones/HospitalityAdministrationScopeAccessor.cs",
      "src/OrionERP.Web/Features/TrainingSafety/TrainingEndpoints.cs",
      "src/OrionERP.Web/Identity/CapitalHumanoImporter.cs",
      "src/OrionERP.Web/Identity/EmployeeCompanyClaimsPrincipalFactory.cs"
    };

    Assert.Empty(rawConnectionFiles.Except(reviewedFiles, StringComparer.Ordinal));

    var operationalFiles = rawConnectionFiles.Where(path =>
      !path.Contains("/Features/Platform/", StringComparison.Ordinal)
      && !path.Contains("/Identity/", StringComparison.Ordinal)
      && !path.Contains("/TrainingSafety/", StringComparison.Ordinal)
      && !path.Contains("DatabaseMigrator", StringComparison.Ordinal)
      && !path.EndsWith("HospitalityAdministrationScopeAccessor.cs", StringComparison.Ordinal)
      && !path.EndsWith("ConnectionFactory.cs", StringComparison.Ordinal));

    foreach (var relativePath in operationalFiles)
    {
      var source = File.ReadAllText(Path.Combine(root, relativePath));
      Assert.True(
        source.Contains("AccountingConnectionFactory.InitializeAsync", StringComparison.Ordinal)
        || source.Contains("HospitalityConnectionFactory.InitializeAsync", StringComparison.Ordinal)
        || source.Contains("OrionSqlSessionFactory.InitializeAsync", StringComparison.Ordinal),
        $"{relativePath} abre SqlConnection sin inicializar un alcance de empresa revisado.");
    }
  }

  [Fact]
  public void SaFullAccess_OnlyTheSaLoginSkipsTheCompanyContext()
  {
    const string SaBypass = "(SUSER_SID()=0x01 AND DATABASE_PRINCIPAL_ID()=1)";
    var core = RepoFile.Read("src/OrionERP.Infrastructure/Features/Platform/Sql/20260914_rls_sa_full_access.sql");
    var onlineOrdering = RepoFile.Read(
      "src/OrionERP.Infrastructure/Features/Platform/Sql/20260914_rls_sa_full_access_online_ordering.sql");
    var rebuilt = new (string Script, string Function)[]
    {
      (core, "contabilidad.fn_AccountingScopePredicate"),
      (core, "fiscal.fn_DeclarationScopePredicate"),
      (core, "logistica.fn_InventoryCoreScopePredicate"),
      (core, "logistica.fn_RfcAccessPredicate"),
      (core, "orion.fn_HospitalityPaymentScopePredicate"),
      (core, "orion.fn_HospitalityScopePredicate"),
      (core, "rh.fn_RfcAccessPredicate"),
      (core, "rh.fn_WorkforceScopePredicate"),
      (onlineOrdering, "restaurante.fn_OnlineOrderingScopePredicate")
    };

    foreach (var (script, function) in rebuilt)
    {
      var start = script.IndexOf("ALTER FUNCTION " + function, StringComparison.Ordinal);
      Assert.True(start >= 0, $"Falta ALTER FUNCTION {function}.");
      var body = script[start..script.IndexOf("';", start, StringComparison.Ordinal)];
      Assert.Contains(SaBypass, body, StringComparison.Ordinal);
      Assert.DoesNotContain("USER_NAME()=N''dbo''", body, StringComparison.Ordinal);
    }

    foreach (var script in new[] { core, onlineOrdering })
    {
      Assert.Contains("IF SUSER_SID()=0x01", script, StringComparison.Ordinal);
      Assert.Contains("EXECUTE AS LOGIN=N'sa'", script, StringComparison.Ordinal);
      Assert.DoesNotContain("IS_SRVROLEMEMBER", script, StringComparison.OrdinalIgnoreCase);
    }
  }

  private static string FindRepositoryRoot()
  {
    var current = new DirectoryInfo(AppContext.BaseDirectory);
    while (current is not null && !File.Exists(Path.Combine(current.FullName, "OrionERP.sln")))
      current = current.Parent;
    return current?.FullName ?? throw new InvalidOperationException("No se encontró la raíz del repositorio.");
  }
}
