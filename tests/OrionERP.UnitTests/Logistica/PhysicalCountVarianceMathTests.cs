using OrionERP.Application.Features.Logistica.PhysicalCounts;

namespace OrionERP.UnitTests.Logistica;

public sealed class PhysicalCountVarianceMathTests
{
  [Fact]
  public void PostingDelta_WhenNothingMoved_MatchesCountedMinusExpected()
  {
    // sistema == esperado (100); se contaron 98 -> delta -2
    Assert.Equal(-2m, PhysicalCountVarianceMath.PostingDelta(98m, 100m));
  }

  [Fact]
  public void PostingDelta_MeasuresAgainstCurrentSystemQuantity_NotTheOpeningSnapshot()
  {
    // Se abrió el conteo con 100 esperados, pero entró una compra de 50 durante el conteo:
    // existencia real ahora 150. Se contaron 150. El delta al kardex debe ser 0, no +50.
    Assert.Equal(0m, PhysicalCountVarianceMath.PostingDelta(150m, 150m));
  }

  [Fact]
  public void MovedDuringCount_DetectsDriftBetweenSnapshotAndCurrentQuantity()
  {
    Assert.True(PhysicalCountVarianceMath.MovedDuringCount(expectedAtOpen: 100m, systemQuantityNow: 150m));
    Assert.False(PhysicalCountVarianceMath.MovedDuringCount(expectedAtOpen: 100m, systemQuantityNow: 100m));
  }

  [Fact]
  public void MovedDuringCount_IgnoresSubEpsilonNoise()
  {
    Assert.False(PhysicalCountVarianceMath.MovedDuringCount(100m, 100.00005m));
  }
}
