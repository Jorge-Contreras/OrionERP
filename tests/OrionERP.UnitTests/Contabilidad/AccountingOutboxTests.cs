using OrionERP.Application.Features.Contabilidad.Transacciones;
using OrionERP.UnitTests.Common;

namespace OrionERP.UnitTests.Contabilidad;

public sealed class AccountingOutboxTests
{
  private static AccountingOperation Operation(string state, int? transaccionId)
    => new(1, 2, "BRUNOS260707L26", AccountingOutboxModules.Restaurant,
      "DAILY:1:2026-09-08", "{}", state, transaccionId, Attempts: 1);

  [Fact]
  public void TheDailyOperationKey_IsTheSameForARetryOfTheSameDay()
  {
    var first = InvokeDailyKey(1, new DateTime(2026, 9, 8, 23, 59, 0));
    var second = InvokeDailyKey(1, new DateTime(2026, 9, 8, 0, 0, 0));

    // Un reintento concurrente tiene que ser la misma operación, no dos.
    Assert.Equal(first, second);
  }

  [Theory]
  [InlineData(2, "2026-09-08")]
  [InlineData(1, "2026-09-09")]
  public void DifferentSiteOrDay_IsADifferentOperation(int siteId, string day)
  {
    var baseline = InvokeDailyKey(1, new DateTime(2026, 9, 8));

    Assert.NotEqual(baseline, InvokeDailyKey(siteId, DateTime.Parse(day)));
  }

  [Fact]
  public void ACompletedOperation_IsNotWorkedAgain()
  {
    var completed = Operation(AccountingOutboxStates.Completed, 77);

    Assert.True(completed.AlreadyCompleted);
    Assert.False(completed.ResumesFromExistingPolicy);
  }

  [Fact]
  public void APolicyCreatedButNotLinked_IsResumed()
  {
    var interrupted = Operation(AccountingOutboxStates.Claimed, 77);

    Assert.True(interrupted.ResumesFromExistingPolicy);
  }

  [Fact]
  public void AnOperationThatNeverReachedAPolicy_StartsFromScratch()
  {
    Assert.False(Operation(AccountingOutboxStates.Claimed, null).ResumesFromExistingPolicy);
    Assert.False(Operation(AccountingOutboxStates.Pending, null).ResumesFromExistingPolicy);
  }

  [Fact]
  public void TheFailurePathKeepsThePolicy_WhichIsWhatMakesItRecoverable()
  {
    var service = RepoFile.Read(
      "src/OrionERP.Infrastructure/Features/Restaurante/RestaurantAccountingService.cs");
    var linkBlock = Between(service, "GenerateDailyPolicyAsync", "GenerateIndividualCfdiPolicyAsync");

    // El rastro se graba antes del vínculo.
    Assert.Contains("await _outbox.RecordPolicyAsync(operation.Id, transactionId, ct);", linkBlock, StringComparison.Ordinal);
    // Y el catch ya no borra la póliza: la deja para el reintento.
    var catchBlock = linkBlock[linkBlock.LastIndexOf("catch (Exception ex)", StringComparison.Ordinal)..];
    Assert.DoesNotContain("DeleteTransaccionAsync", catchBlock, StringComparison.Ordinal);
    Assert.Contains("_outbox.FailAsync", catchBlock, StringComparison.Ordinal);
  }

  [Fact]
  public void TheDailyLink_CanBeRetriedWithoutCollidingWithItsOwnUniqueIndex()
  {
    var service = RepoFile.Read(
      "src/OrionERP.Infrastructure/Features/Restaurante/RestaurantAccountingService.cs");

    Assert.Contains("IF NOT EXISTS", service, StringComparison.Ordinal);
    Assert.Contains("FROM restaurante.AccountingLink WITH (UPDLOCK,HOLDLOCK)", service, StringComparison.Ordinal);
  }

  [Fact]
  public void TheOutboxIsItsOwn_AndDoesNotTouchTheSignalRQueue()
  {
    var outbox = RepoFile.Read(
      "src/OrionERP.Infrastructure/Features/Contabilidad/Transacciones/AccountingOutboxService.cs");

    Assert.Contains("contabilidad.AccountingOutbox", outbox, StringComparison.Ordinal);
    Assert.DoesNotContain("restaurante.EventOutbox", outbox, StringComparison.Ordinal);
  }

  [Fact]
  public void HospitalityShipsOff_BecauseItsMappingsAreEmpty()
  {
    var migration = RepoFile.Read(
      "src/OrionERP.Infrastructure/Features/Contabilidad/Transacciones/Sql/20260908_accounting_outbox_sandbox.sql");

    Assert.Contains("CREATE TABLE contabilidad.HospitalityAccountingMapping", migration, StringComparison.Ordinal);
    Assert.Contains("DF_HospitalityAccountingMapping_IsEnabled DEFAULT (0)", migration, StringComparison.Ordinal);
    Assert.Contains("La bandeja y los mappings deben entregarse vacios.", migration, StringComparison.Ordinal);
    // No se infiere ninguna cuenta ni categoría.
    Assert.DoesNotContain("INSERT contabilidad.HospitalityAccountingMapping", migration, StringComparison.Ordinal);
  }

  private static string InvokeDailyKey(int siteId, DateTime date)
  {
    var method = typeof(OrionERP.Infrastructure.Features.Restaurante.RestaurantAccountingService)
      .GetMethod("DailyOperationKey", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
      ?? throw new InvalidOperationException("Falta DailyOperationKey.");
    return (string)method.Invoke(null, [siteId, date])!;
  }

  private static string Between(string source, string start, string end)
  {
    var from = source.IndexOf(start, StringComparison.Ordinal);
    var to = source.IndexOf(end, from + start.Length, StringComparison.Ordinal);
    return source[from..(to < 0 ? source.Length : to)];
  }
}
