using System.Data;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrionERP.Application.Common;
using OrionERP.Application.Features.Cfdi.Facturama;
using OrionERP.Application.Features.Contabilidad.Transacciones;
using OrionERP.Application.Features.Reservaciones;
using OrionERP.Application.Features.Reservaciones.Cfdi;
using OrionERP.Application.Features.Reservaciones.ListaReservaciones;
using OrionERP.Application.Features.Rfcs.Contracts;
using OrionERP.Infrastructure.Features.Cfdi.Facturama;
using OrionERP.Infrastructure.Features.Contabilidad.Transacciones.Services;
using OrionERP.Infrastructure.Features.Reservaciones.Cfdi;

namespace OrionERP.UnitTests.Reservaciones;

public class HospitalityFiscalScopeTests
{
  [Fact]
  public async Task ReservationContext_RejectsAnotherIssuerBeforeLoadingReservationOrCallingExternalServices()
  {
    var service = CreateReservationService(new FixedHospitalityScope());
    await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetContextAsync(11, "OTHER010101AA1"));
  }

  [Fact]
  public async Task Stamp_RejectsAnotherIssuerBeforeAnyDatabaseOrExternalCall()
  {
    var service = CreateReservationService(new FixedHospitalityScope());
    var result = await service.CreateCfdiAsync(new ReservationCfdiCreateRequest
    {
      ReservationId = 11,
      IssuerRfc = "OTHER010101AA1"
    });
    Assert.False(result.Success);
    Assert.Contains("RFC emisor", result.Message);
  }

  [Fact]
  public async Task Airbnb_RejectsAnotherIssuerBeforeAnyDatabaseOrAccountingWrite()
  {
    var service = CreateReservationService(new FixedHospitalityScope());
    var result = await service.ApplyAirbnbAccountingAsync(new ReservationAirbnbAccountingRequest
    {
      ReservationId = 11,
      TransaccionId = 42,
      IssuerRfc = "OTHER010101AA1"
    });
    Assert.False(result.Success);
    Assert.Contains("RFC emisor", result.Message);
  }

  [Fact]
  public async Task CustomerSearch_WithoutScopeFailsBeforeDatabase()
    => await Assert.ThrowsAsync<InvalidOperationException>(() => CreateReservationService().SearchCustomersAsync("guest"));

  [Fact]
  public async Task BillingStatus_WithoutScopeFailsBeforeDatabase()
    => await Assert.ThrowsAsync<InvalidOperationException>(() => CreateReservationService().GetFacturacionStatusAsync(11));

  [Fact]
  public async Task ReceiverValidation_WithoutScopeDoesNotContactFiscalProvider()
    => await Assert.ThrowsAsync<InvalidOperationException>(() => CreateReservationService().ValidateReceiverAsync(new()));

  [Theory]
  [InlineData("header")]
  [InlineData("attachment")]
  [InlineData("movements")]
  [InlineData("cfdi")]
  [InlineData("delete")]
  [InlineData("reservation-search")]
  public async Task Accounting_WithoutContextFailsClosedBeforeDatabase(string operation)
  {
    var service = CreateTransactionService();
    await Assert.ThrowsAsync<InvalidOperationException>(async () =>
    {
      switch (operation)
      {
        case "header": await service.GetHeaderAsync(42); break;
        case "attachment": await service.GetAttachmentContentAsync(42); break;
        case "movements": await service.GetMovimientosAsync(42); break;
        case "cfdi": await service.GetCfdiPolizaLinkingWorkspaceAsync(42, "OWNER010101AA1", new()); break;
        case "delete": await service.DeleteTransaccionAsync(42); break;
        case "reservation-search": await service.SearchReservacionesAsync("guest"); break;
      }
    });
  }

  [Fact]
  public async Task AccountingCandidates_RejectRequestedForeignCompanyBeforeDatabase()
  {
    var service = CreateTransactionService(new FixedCompany());
    await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetCfdiCandidatesAsync(new() { Rfc = "OTHER010101AA1" }));
  }

  [Fact]
  public async Task AccountingCreation_RejectsRequestedForeignCompanyBeforeDatabase()
  {
    var service = CreateTransactionService(new FixedCompany());
    await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.CreateTransaccionAsync(new() { Rfc = "OTHER010101AA1" }));
  }

  private static ReservationCfdiService CreateReservationService(IHospitalityScopeAccessor? scope = null)
    => new(new ForbiddenConnectionFactory(), Configuration(), Unused<IListaReservacionesService>(),
      Unused<ITransaccionService>(), Unused<IFacturamaApiClient>(), Unused<ISatRfcProfileRepository>(),
      Unused<ICfdiStampingService>(), NullLogger<ReservationCfdiService>.Instance, scope);

  private static TransaccionService CreateTransactionService(ICurrentCompanyContext? company = null)
    => new(Configuration(), Unused<IFacturamaApiClient>(), Unused<ISatRfcProfileRepository>(),
      Unused<ICfdiStampingService>(), NullLogger<TransaccionService>.Instance, companyContext: company);

  private static IConfiguration Configuration() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
  {
    ["ConnectionStrings:OrionDb"] = "Server=127.0.0.1,1;Database=Orion_Sandbox;Integrated Security=True;Connect Timeout=1"
  }).Build();

  private static T Unused<T>() where T : class => DispatchProxy.Create<T, ForbiddenDependency>();

  public class ForbiddenDependency : DispatchProxy
  {
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
      => throw new Xunit.Sdk.XunitException($"Unexpected dependency invocation: {targetMethod?.Name}");
  }

  private sealed class ForbiddenConnectionFactory : IDbConnectionFactory
  {
    public IDbConnection Create() => throw new Xunit.Sdk.XunitException("Scope rejection must happen before opening SQL.");
  }

  private sealed class FixedHospitalityScope : IHospitalityScopeAccessor
  {
    public Task<HospitalityScope> ResolveRequiredAsync(CancellationToken ct = default)
      => Task.FromResult(new HospitalityScope(10, 20, "OWNER010101AA1"));
  }

  private sealed class FixedCompany : ICurrentCompanyContext
  {
    public string CurrentRfc => "OWNER010101AA1";
    public string DisplayName => "Owner";
    public int? EmployeeId => null;
    public string RequireRfc() => CurrentRfc;
    public void EnsureRfc(string rfc)
    {
      if (!string.Equals(CurrentRfc, rfc, StringComparison.OrdinalIgnoreCase))
        throw new UnauthorizedAccessException("Foreign company.");
    }
    public Task<long> RequireCompanyIdAsync(CancellationToken ct = default) => Task.FromResult(1L);
  }
}
