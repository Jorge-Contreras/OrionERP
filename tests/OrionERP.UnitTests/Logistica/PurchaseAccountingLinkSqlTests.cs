using OrionERP.UnitTests.Common;

namespace OrionERP.UnitTests.Logistica;

public sealed class PurchaseAccountingLinkSqlTests
{
  private static readonly string Migration = RepoFile.Read(
    "src/OrionERP.Infrastructure/Features/Logistica/Sql/20260912_logistics_purchase_accounting_link.sql");
  private static readonly string Service = RepoFile.Read(
    "src/OrionERP.Infrastructure/Features/Logistica/Purchasing/PurchaseAccountingService.cs");

  [Fact]
  public void Migration_KeysLinksToTheCompanyPurchaseOrderAndToAnExistingPolicy()
  {
    Assert.Contains("CREATE TABLE logistica.PurchaseAccountingLink", Migration, StringComparison.Ordinal);
    Assert.Contains("FOREIGN KEY (Rfc, PurchaseOrderId) REFERENCES logistica.PurchaseOrder (Rfc, Id)", Migration, StringComparison.Ordinal);
    Assert.Contains("FOREIGN KEY (TransaccionId) REFERENCES dbo.Transacciones (ID)", Migration, StringComparison.Ordinal);
    Assert.Contains("CHECK (MontoAsignado > 0)", Migration, StringComparison.Ordinal);
    Assert.Contains("CHECK (Origin IN ('Generated', 'Manual'))", Migration, StringComparison.Ordinal);
    Assert.Contains("ON logistica.PurchaseAccountingLink (PurchaseOrderId, TransaccionId)", Migration, StringComparison.Ordinal);
  }

  [Fact]
  public void Migration_EnrollsInFailClosedRlsAndTenantClassification()
  {
    // Sin estas piezas la tabla queda fuera del aislamiento por RFC y el validador
    // database/validation/validate-rfc-tenant-isolation.sql rechaza el esquema.
    Assert.Contains("ADD FILTER PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.PurchaseAccountingLink", Migration, StringComparison.Ordinal);
    Assert.Contains("ADD BLOCK PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.PurchaseAccountingLink AFTER INSERT", Migration, StringComparison.Ordinal);
    Assert.Contains("ADD BLOCK PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.PurchaseAccountingLink AFTER UPDATE", Migration, StringComparison.Ordinal);
    Assert.Contains("VALUES ('logistica', 'PurchaseAccountingLink', 'TENANT_OWNED', 'Rfc'", Migration, StringComparison.Ordinal);
  }

  [Fact]
  public void Service_ScopesEveryConnectionToTheAuthorizedCompanyAndSite()
  {
    // La RLS por RFC no filtra al login de la aplicación: el aislamiento lo hace el servicio.
    Assert.Contains("LogisticsLocationScope.OpenAsync", Service, StringComparison.Ordinal);
    Assert.Contains("LogisticsLocationScope.EnsureRfcAsync", Service, StringComparison.Ordinal);
    Assert.DoesNotContain("_connectionFactory.Create()", Service, StringComparison.Ordinal);
    Assert.Contains("PurchaseOrderService.OrderVisibilitySql", Service, StringComparison.Ordinal);
  }

  [Fact]
  public void Service_NeverDeletesPoliciesAndResumesThroughTheOutbox()
  {
    // DELETE sobre dbo.Transacciones siempre falla; un intento a medias se retoma.
    Assert.DoesNotContain("DeleteTransaccionAsync", Service, StringComparison.Ordinal);
    Assert.Contains("AccountingOutboxModules.Purchasing", Service, StringComparison.Ordinal);
    Assert.Contains("RecordPolicyAsync", Service, StringComparison.Ordinal);
    Assert.Contains("AccountingCycleSql.PeriodClosedForDateSql", Service, StringComparison.Ordinal);
  }

  [Fact]
  public void DeletingAPolicy_ChecksPurchaseLinksFirst()
  {
    var transacciones = RepoFile.Read(
      "src/OrionERP.Infrastructure/Features/Contabilidad/Transacciones/Services/TransaccionService.cs");
    Assert.Contains("EXISTS (SELECT 1 FROM logistica.PurchaseAccountingLink WHERE TransaccionId = @TransaccionId)", transacciones, StringComparison.Ordinal);
  }
}
