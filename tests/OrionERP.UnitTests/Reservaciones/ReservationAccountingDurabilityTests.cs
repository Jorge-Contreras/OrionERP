using OrionERP.UnitTests.Common;

namespace OrionERP.UnitTests.Reservaciones;

public sealed class ReservationAccountingDurabilityTests
{
  [Fact]
  public void OperationIdentity_IsCompanyScopedByTheOutboxAndStableBySiteAndReservation()
  {
    var method = typeof(OrionERP.Infrastructure.Features.Reservaciones.Accounting.ReservationAccountingService)
      .GetMethod("OperationKey", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
      ?? throw new InvalidOperationException("Falta OperationKey.");

    var first = (string)method.Invoke(null, [3L, 24258])!;
    var retry = (string)method.Invoke(null, [3L, 24258])!;
    var otherSite = (string)method.Invoke(null, [4L, 24258])!;

    Assert.Equal("RESERVATION:3:24258", first);
    Assert.Equal(first, retry);
    Assert.NotEqual(first, otherSite);
  }

  [Fact]
  public void ReservationPolicy_UsesExplicitMappingAndRecordsItBeforeLinking()
  {
    var service = RepoFile.Read(
      "src/OrionERP.Infrastructure/Features/Reservaciones/Accounting/ReservationAccountingService.cs");

    Assert.Contains("contabilidad.HospitalityAccountingMapping", service, StringComparison.Ordinal);
    Assert.Contains("WHERE CompanyId=@CompanyId AND SiteId=@SiteId AND IsEnabled=1", service, StringComparison.Ordinal);
    Assert.DoesNotContain("401.25.02", service, StringComparison.Ordinal);
    Assert.DoesNotContain("205.01.01", service, StringComparison.Ordinal);

    var record = service.IndexOf("_outbox.RecordPolicyAsync", StringComparison.Ordinal);
    var link = service.IndexOf("_transactions.UpsertReservacionLinkAsync", StringComparison.Ordinal);
    Assert.True(record >= 0 && link > record);
  }

  [Fact]
  public void RegularReservationPolicy_IsBalancedAndRetryable()
  {
    var service = RepoFile.Read(
      "src/OrionERP.Infrastructure/Features/Reservaciones/Accounting/ReservationAccountingService.cs");

    Assert.Contains("Movement(mapping.LeaseReceivable, concept, amount, 0m)", service, StringComparison.Ordinal);
    Assert.Contains("Movement(mapping.LeaseIncome, concept, 0m, amount)", service, StringComparison.Ordinal);
    Assert.Contains("operation.ResumesFromExistingPolicy", service, StringComparison.Ordinal);
    Assert.Contains("_outbox.FailAsync", service, StringComparison.Ordinal);
  }

  [Fact]
  public void ReservationPage_NoLongerCreatesAnUntrackedPolicy()
  {
    var page = RepoFile.Read(
      "src/OrionERP.Web/Features/Reservaciones/ListaReservaciones/ReservacionPage.razor.cs");
    var start = page.IndexOf("internal async Task CrearPolizaAsync()", StringComparison.Ordinal);
    var end = page.IndexOf("private async Task<bool> SaveReservationStateAsync", start, StringComparison.Ordinal);
    var block = page[start..end];

    Assert.Contains("ReservationAccountingService.GeneratePolicyAsync", block, StringComparison.Ordinal);
    Assert.DoesNotContain("CreateTransaccionAsync", block, StringComparison.Ordinal);
    Assert.DoesNotContain("UpsertReservacionLinkAsync", block, StringComparison.Ordinal);
  }
}
