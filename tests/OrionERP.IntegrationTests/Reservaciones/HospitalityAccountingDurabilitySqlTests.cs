using System.Reflection;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrionERP.Application.Common;
using OrionERP.Application.Features.Cfdi.Facturama;
using OrionERP.Application.Features.Contabilidad.Transacciones;
using OrionERP.Application.Features.Reservaciones;
using OrionERP.Application.Features.Reservaciones.Cfdi;
using OrionERP.Infrastructure.Features.Contabilidad.Transacciones;
using OrionERP.Infrastructure.Features.Contabilidad.Transacciones.Services;
using OrionERP.Infrastructure.Features.Cfdi.Facturama;
using OrionERP.Infrastructure.Features.Reservaciones;
using OrionERP.Infrastructure.Features.Reservaciones.Accounting;
using OrionERP.Infrastructure.Features.Reservaciones.ListaReservaciones.Services;
using OrionERP.Application.Features.Rfcs.Contracts;

namespace OrionERP.IntegrationTests.Reservaciones;

public sealed class HospitalityAccountingDurabilitySqlTests
{
  [Fact]
  [Trait("Category", "SqlIntegration")]
  public async Task ConcurrentClaims_AllowOneConsumerAndKeepAnImmediateRetryOut()
  {
    if (Environment.GetEnvironmentVariable("ORION_RUN_SQL_INTEGRATION") != "1") return;
    var source = Environment.GetEnvironmentVariable("ASPNETCORE_ConnectionStrings__OrionDb")
      ?? throw new InvalidOperationException("Falta conexión Sandbox.");
    var connectionString = new SqlConnectionStringBuilder(source) { InitialCatalog = "Orion_Sandbox" }.ConnectionString;
    var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
      ["ConnectionStrings:OrionDb"] = connectionString
    }).Build();
    await using var bootstrap = new SqlConnection(connectionString);
    await bootstrap.OpenAsync();
    var scope = await bootstrap.QuerySingleAsync<ScopeRow>("""
      SELECT ps.CompanyId,ps.SiteId,company.Rfc AS CompanyRfc
      FROM orion.PublicSite ps JOIN orion.Company company ON company.CompanyId=ps.CompanyId
      WHERE ps.PublicSiteKey='bonhomia-main';
      """);
    var hospitalityScope = new HospitalityScope(scope.CompanyId, scope.SiteId, scope.CompanyRfc);
    var company = new FixedCompany(hospitalityScope);
    var firstOutbox = new AccountingOutboxService(new AccountingConnectionFactory(cfg, company), company);
    var secondOutbox = new AccountingOutboxService(new AccountingConnectionFactory(cfg, company), company);
    var operationKey = "CLAIM-INTEGRATION:" + Guid.NewGuid().ToString("N");

    try
    {
      var claims = await Task.WhenAll(
        firstOutbox.ClaimAsync(AccountingOutboxModules.Hospitality, operationKey, "{\"test\":true}"),
        secondOutbox.ClaimAsync(AccountingOutboxModules.Hospitality, operationKey, "{\"test\":true}"));

      Assert.Single(claims, item => item.WasClaimed);
      Assert.Single(claims, item => item.InProgress);
      await firstOutbox.FailAsync(claims[0].Id, "prueba de reintento");
      var retry = await firstOutbox.ClaimAsync(
        AccountingOutboxModules.Hospitality,
        operationKey,
        "{\"test\":true}");
      Assert.True(retry.WasClaimed);
      Assert.False(retry.InProgress);
    }
    finally
    {
      await bootstrap.ExecuteAsync("""
        EXEC sys.sp_set_session_context @key=N'OrionRfc',@value=@Rfc;
        EXEC sys.sp_set_session_context @key=N'OrionERP.CompanyId',@value=@CompanyId;
        DELETE FROM contabilidad.AccountingOutbox
        WHERE CompanyId=@CompanyId AND SourceModule='HOSPITALITY' AND OperationKey=@OperationKey;
        """, new { Rfc = scope.CompanyRfc, scope.CompanyId, OperationKey = operationKey });
    }
  }

  [Fact]
  [Trait("Category", "SqlIntegration")]
  public async Task AReservationRetry_ReusesOneBalancedMappedPolicy()
  {
    if (Environment.GetEnvironmentVariable("ORION_RUN_SQL_INTEGRATION") != "1") return;
    var source = Environment.GetEnvironmentVariable("ASPNETCORE_ConnectionStrings__OrionDb")
      ?? throw new InvalidOperationException("Falta conexión Sandbox.");
    var connectionString = new SqlConnectionStringBuilder(source) { InitialCatalog = "Orion_Sandbox" }.ConnectionString;
    var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
      ["ConnectionStrings:OrionDb"] = connectionString
    }).Build();

    await using var bootstrap = new SqlConnection(connectionString);
    await bootstrap.OpenAsync();
    Assert.Equal("Orion_Sandbox", await bootstrap.ExecuteScalarAsync<string>("SELECT DB_NAME();"), ignoreCase: true);
    var scope = await bootstrap.QuerySingleAsync<ScopeRow>("""
      SELECT ps.CompanyId,ps.SiteId,company.Rfc AS CompanyRfc
      FROM orion.PublicSite ps
      JOIN orion.Company company ON company.CompanyId=ps.CompanyId
      WHERE ps.PublicSiteKey='bonhomia-main';
      """);
    var hospitalityScope = new HospitalityScope(scope.CompanyId, scope.SiteId, scope.CompanyRfc);
    var fixedScope = new FixedScope(hospitalityScope);
    var company = new FixedCompany(hospitalityScope);
    var hospitalityConnections = new HospitalityConnectionFactory(cfg, fixedScope);
    var reservations = new ListaReservacionesService(cfg, NullLogger<ListaReservacionesService>.Instance, hospitalityConnections);
    var transactions = new TransaccionService(
      cfg,
      Unused<IFacturamaApiClient>(),
      Unused<ISatRfcProfileRepository>(),
      Unused<ICfdiStampingService>(),
      NullLogger<TransaccionService>.Instance,
      hospitalityScopeAccessor: fixedScope,
      companyContext: company);
    var outbox = new AccountingOutboxService(new AccountingConnectionFactory(cfg, company), company);
    var accounting = new ReservationAccountingService(
      reservations,
      fixedScope,
      hospitalityConnections,
      transactions,
      Unused<IReservationCfdiService>(),
      outbox,
      NullLogger<ReservationAccountingService>.Instance);

    var marker = "HOSPITALITY-ACCOUNTING-" + Guid.NewGuid().ToString("N");
    var customerId = 0;
    var reservationId = 0;
    var transactionId = 0;
    try
    {
      var customer = await reservations.CreateClienteAsync(marker);
      customerId = customer.Id;
      reservationId = await reservations.CreateReservationAsync(new() { ClienteId = customerId, Notes = marker });
      var extra = (await reservations.GetActiveExtraCatalogAsync()).First();
      var added = await reservations.AddExtraAsync(new()
      {
        ReservationId = reservationId,
        ExtraId = extra.ExtraId,
        UnitPrice = 100m,
        Quantity = 1,
        Notes = marker
      });
      Assert.True(added.Success, added.Message);

      var first = await accounting.GeneratePolicyAsync(reservationId);
      Assert.True(first.Success, first.Message);
      transactionId = first.TransaccionId!.Value;
      var retry = await accounting.GeneratePolicyAsync(reservationId);
      Assert.True(retry.Success, retry.Message);
      Assert.Equal(transactionId, retry.TransaccionId);

      await using var scoped = await hospitalityConnections.OpenAsync();
      var evidence = await scoped.QuerySingleAsync<EvidenceRow>("""
        SELECT
          (SELECT COUNT(*) FROM contabilidad.AccountingOutbox
           WHERE CompanyId=@CompanyId AND SourceModule='HOSPITALITY' AND OperationKey=@OperationKey AND [Status]='Completed') AS CompletedOperations,
          (SELECT COUNT(*) FROM dbo.Reservation_Transacciones
           WHERE ReservationID=@ReservationId AND TransaccionID=@TransactionId) AS ReservationLinks,
          (SELECT COUNT(*) FROM dbo.Registro_Contable WHERE TransaccionID=@TransactionId) AS MovementCount,
          (SELECT CAST(ISNULL(SUM(Debe),0) AS decimal(18,2)) FROM dbo.Registro_Contable WHERE TransaccionID=@TransactionId) AS Debit,
          (SELECT CAST(ISNULL(SUM(Haber),0) AS decimal(18,2)) FROM dbo.Registro_Contable WHERE TransaccionID=@TransactionId) AS Credit;
        """, new
      {
        scope.CompanyId,
        OperationKey = $"RESERVATION:{scope.SiteId}:{reservationId}",
        ReservationId = reservationId,
        TransactionId = transactionId
      });
      Assert.Equal(1, evidence.CompletedOperations);
      Assert.Equal(1, evidence.ReservationLinks);
      Assert.Equal(2, evidence.MovementCount);
      Assert.True(evidence.Debit > 0m);
      Assert.Equal(evidence.Debit, evidence.Credit);
    }
    finally
    {
      await using var cleanup = await hospitalityConnections.OpenAsync();
      await cleanup.ExecuteAsync("""
        DELETE FROM dbo.Reservation_Transacciones WHERE ReservationID=@ReservationId;
        DELETE FROM contabilidad.AccountingOutbox
        WHERE CompanyId=@CompanyId AND SourceModule='HOSPITALITY' AND OperationKey=@OperationKey;
        DELETE FROM dbo.Registro_Contable WHERE TransaccionID=@TransactionId;
        DELETE FROM dbo.Transacciones WHERE ID=@TransactionId AND Concepto LIKE 'PAGO DE LA RESERVACION#%';
        DELETE FROM dbo.Reservation_Extra WHERE ReservationID=@ReservationId;
        DELETE FROM dbo.RESERVATION WHERE ID=@ReservationId AND NOTES=@Marker;
        DELETE FROM orion.HospitalitySiteCustomer WHERE ClienteId=@CustomerId;
        DELETE FROM dbo.Clientes WHERE ID=@CustomerId AND Nombre=@Marker;
        """, new
      {
        ReservationId = reservationId,
        TransactionId = transactionId,
        scope.CompanyId,
        OperationKey = $"RESERVATION:{scope.SiteId}:{reservationId}",
        CustomerId = customerId,
        Marker = marker
      });
    }
  }

  private static T Unused<T>() where T : class => DispatchProxy.Create<T, ForbiddenExternalDependency>();

  public class ForbiddenExternalDependency : DispatchProxy
  {
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
      => throw new Xunit.Sdk.XunitException($"No external call is allowed: {targetMethod?.Name}");
  }

  private sealed class FixedScope(HospitalityScope scope) : IHospitalityScopeAccessor
  {
    public Task<HospitalityScope> ResolveRequiredAsync(CancellationToken ct = default) => Task.FromResult(scope);
  }

  private sealed class FixedCompany(HospitalityScope scope) : ICurrentCompanyContext
  {
    public string CurrentRfc => scope.CompanyRfc;
    public string DisplayName => scope.CompanyRfc;
    public int? EmployeeId => null;
    public string RequireRfc() => scope.CompanyRfc;
    public void EnsureRfc(string requestedRfc)
    {
      if (!string.Equals(scope.CompanyRfc, requestedRfc, StringComparison.OrdinalIgnoreCase))
        throw new UnauthorizedAccessException("Empresa ajena.");
    }
    public Task<long> RequireCompanyIdAsync(CancellationToken ct = default) => Task.FromResult(scope.CompanyId);
  }

  private sealed class ScopeRow
  {
    public long CompanyId { get; set; }
    public long SiteId { get; set; }
    public string CompanyRfc { get; set; } = string.Empty;
  }

  private sealed class EvidenceRow
  {
    public int CompletedOperations { get; set; }
    public int ReservationLinks { get; set; }
    public int MovementCount { get; set; }
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
  }
}
