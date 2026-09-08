using System.Data;
using System.Reflection;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrionERP.Application.Common;
using OrionERP.Application.Features.Cfdi.Facturama;
using OrionERP.Application.Features.Contabilidad.Transacciones;
using OrionERP.Application.Features.OrdenesTrabajo;
using OrionERP.Application.Features.Reservaciones;
using OrionERP.Application.Features.Reservaciones.Cfdi;
using OrionERP.Application.Features.Reservaciones.ListaReservaciones;
using OrionERP.Application.Features.Reservaciones.Experiencias;
using OrionERP.Application.Features.Rfcs.Contracts;
using OrionERP.Infrastructure.Features.Cfdi.Facturama;
using OrionERP.Infrastructure.Features.Contabilidad.Transacciones.Services;
using OrionERP.Infrastructure.Features.Reservaciones;
using OrionERP.Infrastructure.Features.OrdenesTrabajo;
using OrionERP.Infrastructure.Features.Reservaciones.Cfdi;
using OrionERP.Infrastructure.Features.Reservaciones.Experiencias;
using OrionERP.Infrastructure.Features.Reservaciones.ListaReservaciones.Services;

namespace OrionERP.IntegrationTests.Reservaciones;

public sealed class HospitalityAdministrativeCrudTests
{
  [Fact]
  [Trait("Category", "SqlIntegration")]
  public async Task ScopedServices_IsolateCreatedCustomersReservationsDocumentsExperiencesAndFiscalProfiles()
  {
    if (Environment.GetEnvironmentVariable("ORION_RUN_SQL_INTEGRATION") != "1") return;
    var source = Environment.GetEnvironmentVariable("ASPNETCORE_ConnectionStrings__OrionDb")
      ?? throw new InvalidOperationException("Falta conexión Sandbox.");
    var connectionString = new SqlConnectionStringBuilder(source) { InitialCatalog = "Orion_Sandbox" }.ConnectionString;
    await using var bootstrap = new SqlConnection(connectionString);
    await bootstrap.OpenAsync();
    Assert.Equal("Orion_Sandbox", await bootstrap.ExecuteScalarAsync<string>("SELECT DB_NAME();"), ignoreCase: true);
    var scopes = (await bootstrap.QueryAsync<ScopeRow>("""
SELECT ps.PublicSiteKey, ps.CompanyId, ps.SiteId, company.Rfc AS CompanyRfc
FROM orion.PublicSite ps INNER JOIN orion.Company company ON company.CompanyId = ps.CompanyId
WHERE ps.PublicSiteKey IN ('bonhomia-main', 'brunos-main');
""")).ToList();
    Assert.Equal(2, scopes.Count);
    var scopeA = scopes.Single(row => row.PublicSiteKey == "bonhomia-main").ToScope();
    var scopeB = scopes.Single(row => row.PublicSiteKey == "brunos-main").ToScope();
    Assert.NotEqual(scopeA.CompanyId, scopeB.CompanyId);
    var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:OrionDb"] = connectionString }).Build();
    var factoryA = new HospitalityConnectionFactory(cfg, new FixedScope(scopeA));
    var factoryB = new HospitalityConnectionFactory(cfg, new FixedScope(scopeB));
    var serviceA = new ListaReservacionesService(cfg, NullLogger<ListaReservacionesService>.Instance, factoryA);
    var serviceB = new ListaReservacionesService(cfg, NullLogger<ListaReservacionesService>.Instance, factoryB);
    var experiencesA = new ReservacionExperiencesService(cfg, factoryA);
    var experiencesB = new ReservacionExperiencesService(cfg, factoryB);
    var fiscalA = FiscalService(cfg, connectionString, scopeA, serviceA);
    var fiscalB = FiscalService(cfg, connectionString, scopeB, serviceB);
    var marker = "SCOPED-CRUD-" + Guid.NewGuid().ToString("N");
    int customerId = 0, reservationId = 0, fiscalId = 0, transactionAId = 0, transactionBId = 0, workOrderId = 0, genericWorkOrderId = 0;
    try
    {
      var customer = await serviceA.CreateClienteAsync(marker);
      customerId = customer.Id;
      Assert.Equal(customer.Id, (await serviceA.ResolveClienteAsync(customer.Id, null))?.Id);
      Assert.Null(await serviceB.ResolveClienteAsync(customer.Id, marker));
      Assert.Empty(await serviceB.GetClientesAsync(marker));

      reservationId = await serviceA.CreateReservationAsync(new() { ClienteId = customer.Id, Notes = marker });
      Assert.Equal(reservationId, (await serviceA.GetReservacionDetailAsync(reservationId))?.Id);
      Assert.Null(await serviceB.GetReservacionDetailAsync(reservationId));
      await Assert.ThrowsAsync<SqlException>(() => serviceB.CreateReservationAsync(new() { ClienteId = customer.Id, Notes = marker }));

      var bytes = System.Text.Encoding.UTF8.GetBytes(marker);
      var attachment = await serviceA.AddAttachmentAsync(new() { ReservationId = reservationId, FileName = "scope.txt", Extension = "txt", Content = bytes });
      Assert.Equal(bytes, (await serviceA.GetAttachmentContentAsync(attachment.Id))?.Bytes);
      Assert.Null(await serviceB.GetAttachmentContentAsync(attachment.Id));
      await serviceB.DeleteAttachmentAsync(attachment.Id);
      Assert.NotNull(await serviceA.GetAttachmentContentAsync(attachment.Id));
      await Assert.ThrowsAsync<SqlException>(() => serviceB.AddAttachmentAsync(new() { ReservationId = reservationId, FileName = "cross.txt", Content = bytes }));

      var fiscalRequest = new ReservationCfdiCustomerUpsertRequest
      {
        DisplayName = marker, FiscalName = marker, Rfc = "TST" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant(),
        TaxZipCode = "44100", FiscalRegime = "601", CfdiUse = "G03"
      };
      var saved = await fiscalA.SaveCustomerAsync(fiscalRequest);
      Assert.True(saved.Success, saved.Message);
      fiscalId = saved.BusinessPartnerId!.Value;
      Assert.Contains(await fiscalA.SearchCustomersAsync(marker), row => row.BusinessPartnerId == fiscalId);
      Assert.Empty(await fiscalB.SearchCustomersAsync(marker));
      fiscalRequest.BusinessPartnerId = fiscalId;
      var rejected = await fiscalB.SaveCustomerAsync(fiscalRequest);
      Assert.False(rejected.Success);
      Assert.Contains("no pertenece", rejected.Message);
      Assert.Empty((await fiscalB.GetFacturacionStatusAsync(reservationId)).Payments);
      var extra = (await serviceA.GetActiveExtraCatalogAsync()).First();
      var extraResult = await serviceA.AddExtraAsync(new() { ReservationId = reservationId, ExtraId = extra.ExtraId, UnitPrice = 1m, Quantity = 1, Notes = marker });
      Assert.True(extraResult.Success, extraResult.Message);
      Assert.NotNull(await fiscalA.GetContextAsync(reservationId, scopeA.CompanyRfc));
      var transactionsA = TransactionService(cfg, scopeA);
      var transactionsB = TransactionService(cfg, scopeB);
      Assert.Contains(await transactionsA.SearchReservacionesAsync(reservationId.ToString()), row => row.ReservationId == reservationId);
      Assert.Empty(await transactionsB.SearchReservacionesAsync(reservationId.ToString()));
      var paymentA = await transactionsA.CreateTransaccionAsync(new() { Rfc = scopeA.CompanyRfc, Fecha = DateTime.Today, Concepto = marker, Monto = 1m, TipoPoliza = "INGRESO", FormaPago = "03" });
      Assert.True(paymentA.Success, paymentA.Message);
      transactionAId = paymentA.NewTransaccionId;
      var paymentB = await transactionsB.CreateTransaccionAsync(new() { Rfc = scopeB.CompanyRfc, Fecha = DateTime.Today, Concepto = marker, Monto = 1m, TipoPoliza = "INGRESO", FormaPago = "03" });
      Assert.True(paymentB.Success, paymentB.Message);
      transactionBId = paymentB.NewTransaccionId;
      var linked = await transactionsA.UpsertReservacionLinkAsync(new() { ReservationId = reservationId, TransaccionId = transactionAId, Amount = 1m });
      Assert.True(linked.Success, linked.Message);
      Assert.Single(await transactionsA.GetReservacionLinksAsync(transactionAId));
      Assert.False((await transactionsA.UpsertReservacionLinkAsync(new() { ReservationId = reservationId, TransaccionId = transactionBId, Amount = 1m })).Success);
      Assert.False((await transactionsB.UpsertReservacionLinkAsync(new() { ReservationId = reservationId, TransaccionId = transactionBId, Amount = 1m })).Success);
      await Assert.ThrowsAsync<UnauthorizedAccessException>(() => transactionsB.GetHeaderAsync(transactionAId));
      Assert.Single((await fiscalA.GetFacturacionStatusAsync(reservationId)).Payments);
      Assert.Empty((await fiscalB.GetFacturacionStatusAsync(reservationId)).Payments);
      Assert.False((await transactionsA.DeleteTransaccionAsync(transactionAId)).Success);
      Assert.NotNull(await transactionsA.GetHeaderAsync(transactionAId));

      var ordersA = new OrdenTrabajoService(new ConnectionFactory(connectionString), new FixedScope(scopeA), new FixedCompany(scopeA.CompanyRfc));
      var ordersB = new OrdenTrabajoService(new ConnectionFactory(connectionString), new FixedScope(scopeB), new FixedCompany(scopeB.CompanyRfc));
      var genericOrders = new OrdenTrabajoService(new ConnectionFactory(connectionString), companyContext: new FixedCompany(scopeA.CompanyRfc));
      var room = (await ordersA.GetRoomOptionsAsync()).First();
      var employeeId = await bootstrap.ExecuteScalarAsync<int>("SELECT TOP (1) ID FROM dbo.Capital_Humano WHERE RFC = @Rfc AND UPPER(LTRIM(RTRIM([Status]))) = 'ACTIVO' ORDER BY ID;", new { Rfc = scopeA.CompanyRfc });
      Assert.True(employeeId > 0);
      var order = await ordersA.CreateManualAsync(new() { Rfc = scopeA.CompanyRfc, Titulo = marker, OwnerEmployeeId = employeeId, RoomId = room.Id, ReservationId = reservationId, CreatedBy = "scope-integration" });
      Assert.True(order.Success, order.Message);
      workOrderId = order.EntityId!.Value;
      Assert.NotNull(await ordersA.GetWorkOrderDetailAsync(workOrderId));
      Assert.Null(await ordersB.GetWorkOrderDetailAsync(workOrderId));
      Assert.Null(await genericOrders.GetWorkOrderDetailAsync(workOrderId));
      Assert.DoesNotContain(await ordersB.SearchWorkOrdersAsync(new() { SearchText = marker }), row => row.Id == workOrderId);
      await Assert.ThrowsAsync<UnauthorizedAccessException>(() => ordersB.CancelWorkOrderAsync(workOrderId, "cross-site", "scope-integration"));
      await Assert.ThrowsAsync<UnauthorizedAccessException>(() => ordersB.CreateManualAsync(new() { Rfc = scopeB.CompanyRfc, Titulo = marker, OwnerEmployeeId = employeeId, RoomId = room.Id, CreatedBy = "scope-integration" }));
      var genericOrder = await genericOrders.CreateManualAsync(new() { Rfc = scopeA.CompanyRfc, Titulo = marker, OwnerEmployeeId = employeeId, CreatedBy = "scope-integration" });
      Assert.True(genericOrder.Success, genericOrder.Message);
      genericWorkOrderId = genericOrder.EntityId!.Value;
      Assert.NotNull(await genericOrders.GetWorkOrderDetailAsync(genericWorkOrderId));

      var catalog = await experiencesA.GetActiveExperienceCatalogAsync();
      var experience = catalog.First(item => item.Packages.Count > 0);
      var added = await experiencesA.AddExperienceAsync(new()
      {
        ReservationId = reservationId, ExperienceId = experience.ExperienceId,
        ExperiencePackageId = experience.Packages[0].ExperiencePackageId,
        ExperienceDate = (experience.SeasonStart ?? DateOnly.FromDateTime(DateTime.Today)).ToDateTime(TimeOnly.MinValue),
        AdultParticipants = Math.Max(1, experience.MinimumParticipants), Notes = marker
      });
      Assert.True(added.Success, added.Message);
      var line = Assert.Single(await experiencesA.GetExperiencesAsync(reservationId));
      Assert.Empty(await experiencesB.GetExperiencesAsync(reservationId));
      Assert.False((await experiencesB.DeleteExperienceAsync(line.Id)).Success);
      Assert.Single(await experiencesA.GetExperiencesAsync(reservationId));
      Assert.True((await experiencesA.DeleteExperienceAsync(line.Id)).Success);
    }
    finally
    {
      // Only IDs created by this run are removed. Both successful and failed tests leave existing data intact.
      await using var cleanup = await factoryA.OpenAsync();
      await cleanup.ExecuteAsync("""
DELETE FROM dbo.OrdenTrabajo WHERE Id IN (@WorkOrderId, @GenericWorkOrderId) AND Titulo = @Marker;
DELETE addon FROM dbo.Reservation_ExperienceAddOn addon
INNER JOIN dbo.Reservation_Experience experience ON experience.ReservationExperienceID = addon.ReservationExperienceID
WHERE experience.ReservationID = @ReservationId;
DELETE FROM dbo.Reservation_Experience WHERE ReservationID = @ReservationId;
DELETE FROM dbo.RESERVATION_ATTACHMENT WHERE ReservationID = @ReservationId;
DELETE FROM dbo.Reservation_Extra WHERE ReservationID = @ReservationId;
DELETE FROM dbo.Reservation_Transacciones WHERE ReservationID = @ReservationId;
DELETE FROM dbo.RESERVATION WHERE ID = @ReservationId AND NOTES = @Marker;
DELETE FROM dbo.Transacciones WHERE ID IN (@TransactionAId, @TransactionBId) AND Concepto = @Marker;
DELETE FROM orion.HospitalitySiteCustomer WHERE ClienteId = @CustomerId;
DELETE FROM dbo.Clientes WHERE ID = @CustomerId AND Nombre = @Marker;
DELETE FROM dbo.BusinessPartnerCfdiProfile WHERE BusinessPartnerId = @FiscalId;
DELETE FROM dbo.BusinessPartnerRole WHERE BusinessPartnerId = @FiscalId;
DELETE FROM orion.HospitalityFiscalCustomer WHERE BusinessPartnerId = @FiscalId;
DELETE FROM dbo.BusinessPartner WHERE Id = @FiscalId AND PartnerName = @Marker;
""", new { ReservationId = reservationId, CustomerId = customerId, FiscalId = fiscalId, TransactionAId = transactionAId, TransactionBId = transactionBId, WorkOrderId = workOrderId, GenericWorkOrderId = genericWorkOrderId, Marker = marker });
    }
  }

  private static TransaccionService TransactionService(IConfiguration cfg, HospitalityScope scope)
    => new(cfg, Unused<IFacturamaApiClient>(), Unused<ISatRfcProfileRepository>(), Unused<ICfdiStampingService>(),
      NullLogger<TransaccionService>.Instance, hospitalityScopeAccessor: new FixedScope(scope), companyContext: new FixedCompany(scope.CompanyRfc));

  private sealed class FixedCompany(string rfc) : ICurrentCompanyContext
  {
    public string CurrentRfc => rfc;
    public string DisplayName => rfc;
    public int? EmployeeId => null;
    public string RequireRfc() => rfc;
    public void EnsureRfc(string requestedRfc)
    {
      if (!string.Equals(rfc, requestedRfc, StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException("Empresa ajena.");
    }
  }

  private static ReservationCfdiService FiscalService(IConfiguration cfg, string cs, HospitalityScope scope, IListaReservacionesService reservations)
    => new(new ConnectionFactory(cs), cfg, reservations, Unused<ITransaccionService>(), Unused<IFacturamaApiClient>(),
      Unused<ISatRfcProfileRepository>(), Unused<ICfdiStampingService>(), NullLogger<ReservationCfdiService>.Instance, new FixedScope(scope));

  private static T Unused<T>() where T : class => DispatchProxy.Create<T, ForbiddenExternalDependency>();
  public class ForbiddenExternalDependency : DispatchProxy
  {
    protected override object? Invoke(MethodInfo? method, object?[]? args)
      => throw new Xunit.Sdk.XunitException($"No external call is allowed: {method?.Name}");
  }
  private sealed class ConnectionFactory(string cs) : IDbConnectionFactory { public IDbConnection Create() => new SqlConnection(cs); }
  private sealed class FixedScope(HospitalityScope scope) : IHospitalityScopeAccessor
  {
    public Task<HospitalityScope> ResolveRequiredAsync(CancellationToken ct = default) => Task.FromResult(scope);
  }
  private sealed class ScopeRow
  {
    public string PublicSiteKey { get; set; } = "";
    public long CompanyId { get; set; }
    public long SiteId { get; set; }
    public string CompanyRfc { get; set; } = "";
    public HospitalityScope ToScope() => new(CompanyId, SiteId, CompanyRfc);
  }
}
