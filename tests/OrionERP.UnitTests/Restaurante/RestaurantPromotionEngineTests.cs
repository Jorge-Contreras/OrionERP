using OrionERP.Application.Features.Restaurante;

namespace OrionERP.UnitTests.Restaurante;

public sealed class RestaurantPromotionEngineTests
{
  [Fact]
  public void BuyTwoPayOne_AppliesOnlyInsideConfiguredSchedule()
  {
    var promotion = Promotion(RestaurantPromotionRuleTypes.BuyXPayY);
    promotion.BuyQuantity = 2;
    promotion.PayQuantity = 1;
    promotion.ProductIds = new HashSet<long> { 26 };
    promotion.Schedules =
    [
      new RestaurantPromotionScheduleDto
      {
        DayOfWeek = (byte)DayOfWeek.Tuesday,
        StartsAt = new TimeSpan(10, 0, 0),
        EndsAt = new TimeSpan(12, 0, 0)
      }
    ];
    var request = QuoteRequest(
      new RestaurantPromotionQuoteLineRequest
      {
        LineKey = "chilaquiles",
        ProductId = 26,
        Quantity = 2,
        UnitPrice = 80
      });

    var inside = RestaurantPromotionEngine.Quote(
      request,
      [promotion],
      new DateTimeOffset(2026, 8, 4, 10, 0, 0, TimeSpan.FromHours(-6)));
    var atEnd = RestaurantPromotionEngine.Quote(
      request,
      [promotion],
      new DateTimeOffset(2026, 8, 4, 12, 0, 0, TimeSpan.FromHours(-6)));

    Assert.Equal(80m, inside.PromotionDiscountTotal);
    Assert.Equal(80m, inside.DiscountedMerchandise);
    Assert.Equal(0m, atEnd.PromotionDiscountTotal);
  }

  [Fact]
  public void BuyThreePayOne_DiscountsTwoCheapestEligibleUnits()
  {
    var promotion = Promotion(RestaurantPromotionRuleTypes.BuyXPayY);
    promotion.BuyQuantity = 3;
    promotion.PayQuantity = 1;
    var result = RestaurantPromotionEngine.Quote(
      QuoteRequest(
        Line("a", 1, 1, 100),
        Line("b", 2, 1, 80),
        Line("c", 3, 1, 60)),
      [promotion],
      TuesdayAt(11));

    Assert.Equal(140m, result.PromotionDiscountTotal);
    Assert.Equal(100m, result.DiscountedMerchandise);
  }

  [Fact]
  public void HighestSavingsWinsForSameUnits()
  {
    var percent = Promotion(RestaurantPromotionRuleTypes.PercentOff, id: 1, name: "Veinte");
    percent.PercentOff = 20;
    percent.ProductIds = new HashSet<long> { 26 };
    var twoForOne = Promotion(RestaurantPromotionRuleTypes.BuyXPayY, id: 2, name: "Dos por uno");
    twoForOne.BuyQuantity = 2;
    twoForOne.PayQuantity = 1;
    twoForOne.ProductIds = new HashSet<long> { 26 };

    var result = RestaurantPromotionEngine.Quote(
      QuoteRequest(Line("chilaquiles", 26, 2, 80)),
      [percent, twoForOne],
      TuesdayAt(11));

    var adjustment = Assert.Single(result.Adjustments);
    Assert.Equal(2, adjustment.PromotionId);
    Assert.Equal(80m, adjustment.DiscountAmount);
  }

  [Fact]
  public void ManualDiscountIsAppliedAfterPromotion()
  {
    var percent = Promotion(RestaurantPromotionRuleTypes.PercentOff);
    percent.PercentOff = 50;
    var line = Line("line", 1, 1, 100);
    line.ManualDiscountAmount = 20;

    var result = RestaurantPromotionEngine.Quote(
      QuoteRequest(line),
      [percent],
      TuesdayAt(11));

    Assert.Equal(20m, result.ManualDiscountTotal);
    Assert.Equal(50m, result.PromotionDiscountTotal);
    Assert.Equal(30m, result.DiscountedMerchandise);
  }

  [Fact]
  public void RequiredCodeIsNormalizedAndEnforcesMemberLimit()
  {
    var promotion = Promotion(RestaurantPromotionRuleTypes.FixedAmountOff);
    promotion.FixedAmount = 25;
    promotion.CodeRequired = true;
    promotion.Codes =
    [
      new RestaurantPromotionCodeDto
      {
        Code = "MARTES25",
        IsActive = true,
        PerMemberLimit = 1,
        MemberRedemptionCount = 1
      }
    ];
    var request = QuoteRequest(Line("line", 1, 1, 100));
    request.MemberId = Guid.NewGuid();
    request.Code = " martes25 ";

    var exhausted = RestaurantPromotionEngine.Quote(request, [promotion], TuesdayAt(11));
    promotion.Codes =
    [
      new RestaurantPromotionCodeDto
      {
        Code = "MARTES25",
        IsActive = true,
        PerMemberLimit = 1,
        MemberRedemptionCount = 0
      }
    ];
    var available = RestaurantPromotionEngine.Quote(request, [promotion], TuesdayAt(11));

    Assert.Equal(0m, exhausted.PromotionDiscountTotal);
    Assert.False(exhausted.CodeAccepted);
    Assert.Equal(25m, available.PromotionDiscountTotal);
    Assert.True(available.CodeAccepted);
    Assert.Equal("MARTES25", available.NormalizedCode);
  }

  [Fact]
  public void OvernightScheduleUsesPreviousDayForAfterMidnightPortion()
  {
    var promotion = Promotion(RestaurantPromotionRuleTypes.PercentOff);
    promotion.PercentOff = 10;
    promotion.Schedules =
    [
      new RestaurantPromotionScheduleDto
      {
        DayOfWeek = (byte)DayOfWeek.Tuesday,
        StartsAt = new TimeSpan(22, 0, 0),
        EndsAt = new TimeSpan(2, 0, 0)
      }
    ];

    var result = RestaurantPromotionEngine.Quote(
      QuoteRequest(Line("line", 1, 1, 100)),
      [promotion],
      new DateTimeOffset(2026, 8, 5, 1, 30, 0, TimeSpan.FromHours(-6)));

    Assert.Equal(10m, result.PromotionDiscountTotal);
  }

  private static RestaurantPromotionQuoteRequest QuoteRequest(params RestaurantPromotionQuoteLineRequest[] lines)
    => new()
    {
      Rfc = "BRUNOS260707L26",
      SiteId = 1,
      Channel = RestaurantSalesChannels.Pos,
      OrderType = "Pickup",
      Lines = lines.ToList()
    };

  private static RestaurantPromotionQuoteLineRequest Line(string key, long productId, decimal quantity, decimal unitPrice)
    => new()
    {
      LineKey = key,
      ProductId = productId,
      Quantity = quantity,
      UnitPrice = unitPrice
    };

  private static RestaurantPromotionDefinition Promotion(string type, long id = 1, string name = "Promoción")
    => new()
    {
      Id = id,
      Name = name,
      Status = RestaurantPromotionStatuses.Active,
      RuleType = type,
      PosEnabled = true
    };

  private static DateTimeOffset TuesdayAt(int hour)
    => new(2026, 8, 4, hour, 0, 0, TimeSpan.FromHours(-6));
}
