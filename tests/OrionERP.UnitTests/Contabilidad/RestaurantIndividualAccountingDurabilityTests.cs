using OrionERP.UnitTests.Common;

namespace OrionERP.UnitTests.Contabilidad;

public sealed class RestaurantIndividualAccountingDurabilityTests
{
  [Fact]
  public void IndividualAndLateReversal_HaveSeparateStableIdentities()
  {
    var type = typeof(OrionERP.Infrastructure.Features.Restaurante.RestaurantAccountingService);
    var individualMethod = type.GetMethod(
      "IndividualCfdiOperationKey",
      System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
      ?? throw new InvalidOperationException("Falta IndividualCfdiOperationKey.");
    var reversalMethod = type.GetMethod(
      "LateCfdiReversalOperationKey",
      System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
      ?? throw new InvalidOperationException("Falta LateCfdiReversalOperationKey.");
    var orderId = Guid.Parse("12345678-1234-1234-1234-1234567890ab");

    var individual = (string)individualMethod.Invoke(null, [4, orderId])!;
    var retry = (string)individualMethod.Invoke(null, [4, orderId])!;
    var reversal = (string)reversalMethod.Invoke(null, [4, orderId])!;

    Assert.Equal(individual, retry);
    Assert.NotEqual(individual, reversal);
    Assert.Contains(orderId.ToString("D"), individual, StringComparison.Ordinal);
  }

  [Fact]
  public void IndividualCfdi_RecordsEveryPolicyBeforeItsRecoverableLink()
  {
    var service = RepoFile.Read(
      "src/OrionERP.Infrastructure/Features/Restaurante/RestaurantAccountingService.cs");
    var block = Between(service, "GenerateIndividualCfdiPolicyAsync", "private async Task<int> CreateClosedTransactionAsync(");

    Assert.Contains("IndividualCfdiOperationKey", block, StringComparison.Ordinal);
    Assert.Contains("LateCfdiReversalOperationKey", block, StringComparison.Ordinal);
    Assert.Contains("_outbox.RecordPolicyAsync(individualOperation.Id", block, StringComparison.Ordinal);
    Assert.Contains("_outbox.RecordPolicyAsync(operation.Id", block, StringComparison.Ordinal);
    Assert.Contains("FailOutboxQuietlyAsync(individualOperation.Id", block, StringComparison.Ordinal);
    Assert.Contains("FailOutboxQuietlyAsync(operation.Id", block, StringComparison.Ordinal);
  }

  [Fact]
  public void CfdiAndRestaurantLinks_AreIdempotentOnRetry()
  {
    var service = RepoFile.Read(
      "src/OrionERP.Infrastructure/Features/Restaurante/RestaurantAccountingService.cs");
    var block = Between(service, "GenerateIndividualCfdiPolicyAsync", "private async Task<int> CreateClosedTransactionAsync(");

    Assert.Contains("IsComprobanteLinkedToTransaccionAsync", block, StringComparison.Ordinal);
    Assert.Contains("WHERE Rfc=@Rfc AND OrderId=@OrderId AND LinkType='LateCfdiReversal'", block, StringComparison.Ordinal);
    Assert.Contains("WHERE Rfc=@Rfc AND OrderId=@OrderId AND LinkType='IndividualCfdi'", block, StringComparison.Ordinal);
    Assert.Contains("IF NOT EXISTS", block, StringComparison.Ordinal);
    Assert.Contains("_outbox.CompleteAsync(individualOperation.Id", block, StringComparison.Ordinal);
    Assert.Contains("_outbox.CompleteAsync(operation.Id", block, StringComparison.Ordinal);
  }

  [Fact]
  public void LinkFailures_NoLongerDeleteTrackedIndividualOrReversalPolicies()
  {
    var service = RepoFile.Read(
      "src/OrionERP.Infrastructure/Features/Restaurante/RestaurantAccountingService.cs");
    var method = Between(service, "GenerateIndividualCfdiPolicyAsync", "internal static string IndividualCfdiOperationKey");
    var individualLink = method.IndexOf("InsertTransaccionComprobanteAsync", StringComparison.Ordinal);
    var finalCatch = method.LastIndexOf("catch (Exception ex)", StringComparison.Ordinal);
    var failurePath = method[finalCatch..];

    Assert.True(individualLink > 0);
    Assert.DoesNotContain("DeleteTransaccionAsync", failurePath, StringComparison.Ordinal);
    Assert.Contains("se conserva y el reintento la retomará", failurePath, StringComparison.Ordinal);
  }

  private static string Between(string source, string start, string end)
  {
    var from = source.IndexOf(start, StringComparison.Ordinal);
    var to = source.IndexOf(end, from + start.Length, StringComparison.Ordinal);
    return source[from..(to < 0 ? source.Length : to)];
  }
}
