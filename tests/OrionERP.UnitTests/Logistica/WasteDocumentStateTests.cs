using OrionERP.Application.Features.Logistica.Stock;

namespace OrionERP.UnitTests.Logistica;

public sealed class WasteDocumentStateTests
{
  private static WasteDocumentDto Document(string status, long? reversedBy = null, long? reversalOf = null)
    => new() { Id = 7, Status = status, ReversedByAdjustmentId = reversedBy, ReversalOfAdjustmentId = reversalOf };

  [Theory]
  [InlineData(WasteStatuses.PendingReview, true)]
  [InlineData(WasteStatuses.Approved, false)]
  [InlineData(WasteStatuses.Reversed, false)]
  public void OnlyAPendingDocument_WaitsForReview(string status, bool expected)
    => Assert.Equal(expected, Document(status).CanReview);

  [Theory]
  [InlineData(WasteStatuses.PendingReview, true)]
  [InlineData(WasteStatuses.Approved, true)]
  [InlineData(WasteStatuses.Reversed, false)]
  [InlineData(WasteStatuses.Draft, false)]
  public void ALiveDocument_CanStillBeCorrected(string status, bool expected)
    => Assert.Equal(expected, Document(status).CanReverse);

  [Fact]
  public void AnAlreadyReversedDocument_IsNotReversedTwice()
    => Assert.False(Document(WasteStatuses.Approved, reversedBy: 9).CanReverse);

  [Fact]
  public void AReversal_IsNeverItselfReversed()
  {
    var reversal = Document(WasteStatuses.Approved, reversalOf: 4);

    Assert.True(reversal.IsReversal);
    Assert.False(reversal.CanReverse);
  }

  [Theory]
  [InlineData(WasteStatuses.PendingReview, "Pendiente de revisión")]
  [InlineData(WasteStatuses.Approved, "Aprobada")]
  [InlineData(WasteStatuses.Reversed, "Reversada")]
  public void Statuses_ReachTheOperatorInSpanish(string status, string expected)
    => Assert.Equal(expected, WasteStatuses.LabelFor(status));

  [Fact]
  public void Lines_ShowQuantityAndCostInPositiveEvenThoughTheDeltaIsNegative()
  {
    var line = new WasteDocumentLineDto { QuantityDelta = -2.5m, FrozenUnitCost = 12m };

    Assert.Equal(2.5m, line.Quantity);
    Assert.Equal(30m, line.Cost);
  }
}
