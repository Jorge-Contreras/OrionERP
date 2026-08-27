using OrionERP.Application.Features.Restaurante;

namespace OrionERP.UnitTests.Restaurante;

public sealed class RestaurantRecipeScalingTests
{
  [Theory]
  [InlineData(2, 4, 10, 5)]
  [InlineData(0.25, 1, 3, 0.75)]
  [InlineData(12, 6, 3, 6)]
  public void ScaleQuantity_PreservesProportion(decimal quantity, decimal originalYield, decimal targetYield, decimal expected)
    => Assert.Equal(expected, RestaurantRecipeScaling.ScaleQuantity(quantity, originalYield, targetYield));

  [Theory]
  [InlineData(0)]
  [InlineData(-1)]
  public void ScaleQuantity_RejectsInvalidYields(decimal yield)
  {
    Assert.Throws<ArgumentOutOfRangeException>(() => RestaurantRecipeScaling.ScaleQuantity(1, yield, 1));
    Assert.Throws<ArgumentOutOfRangeException>(() => RestaurantRecipeScaling.ScaleQuantity(1, 1, yield));
  }
}
