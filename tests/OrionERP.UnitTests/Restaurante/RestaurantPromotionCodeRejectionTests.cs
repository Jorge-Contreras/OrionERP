using OrionERP.Application.Features.Restaurante;

namespace OrionERP.UnitTests.Restaurante;

public sealed class RestaurantPromotionCodeRejectionTests
{
  [Fact]
  public void UnknownCodeSaysNoPromotionUsesIt()
  {
    var promotion = Promotion(RestaurantPromotionRuleTypes.PercentOff);
    promotion.PercentOff = 10;
    promotion.Codes = [Code("MARTES25")];

    var result = Quote(promotion, "otro-codigo");

    Assert.False(result.CodeAccepted);
    Assert.Equal(RestaurantPromotionRejectionReasons.UnknownCode, result.CodeRejectionReason);
    Assert.Contains("OTRO-CODIGO", result.CodeRejectionDetail!);
    Assert.Contains("El código OTRO-CODIGO no se aplicó.", result.Message!);
    Assert.Contains(result.CodeRejectionFix!, result.Message!);
  }

  [Fact]
  public void InactiveCodeIsReportedApartFromAMissingOne()
  {
    var promotion = Promotion(RestaurantPromotionRuleTypes.PercentOff);
    promotion.PercentOff = 10;
    promotion.CodeRequired = true;
    var code = Code("MARTES25");
    code.IsActive = false;
    promotion.Codes = [code];

    var result = Quote(promotion, "MARTES25");

    Assert.Equal(RestaurantPromotionRejectionReasons.CodeInactive, result.CodeRejectionReason);
    Assert.Contains("desactivado", result.CodeRejectionDetail!);
  }

  [Fact]
  public void PausedPromotionExplainsTheStatus()
  {
    var promotion = Promotion(RestaurantPromotionRuleTypes.PercentOff);
    promotion.Status = RestaurantPromotionStatuses.Paused;
    promotion.PercentOff = 10;
    promotion.CodeRequired = true;
    promotion.Codes = [Code("MARTES25")];

    var result = Quote(promotion, "MARTES25");

    Assert.Equal(RestaurantPromotionRejectionReasons.PromotionNotActive, result.CodeRejectionReason);
    Assert.Contains("pausada", result.CodeRejectionDetail!);
    Assert.Contains("Promoción", result.CodeRejectionDetail!);
  }

  [Fact]
  public void ExpiredPromotionShowsWhenItEnded()
  {
    var promotion = Promotion(RestaurantPromotionRuleTypes.PercentOff);
    promotion.PercentOff = 10;
    promotion.CodeRequired = true;
    promotion.ValidToLocal = new DateTime(2026, 8, 1, 23, 0, 0);
    promotion.Codes = [Code("MARTES25")];

    var result = Quote(promotion, "MARTES25");

    Assert.Equal(RestaurantPromotionRejectionReasons.Expired, result.CodeRejectionReason);
    Assert.Contains("01/08/2026 23:00", result.CodeRejectionDetail!);
    Assert.Contains("2 días", result.CodeRejectionDetail!);
  }

  [Fact]
  public void OutsideScheduleNamesTheWindowAndTheNextOne()
  {
    var promotion = Promotion(RestaurantPromotionRuleTypes.PercentOff);
    promotion.PercentOff = 10;
    promotion.CodeRequired = true;
    promotion.Codes = [Code("MARTES25")];
    promotion.Schedules =
    [
      new RestaurantPromotionScheduleDto
      {
        DayOfWeek = (byte)DayOfWeek.Tuesday,
        StartsAt = new TimeSpan(10, 0, 0),
        EndsAt = new TimeSpan(12, 0, 0)
      }
    ];

    var result = Quote(promotion, "MARTES25", at: TuesdayAt(13));

    Assert.Equal(RestaurantPromotionRejectionReasons.OutsideSchedule, result.CodeRejectionReason);
    Assert.Contains("martes de 10:00 a 12:00", result.CodeRejectionDetail!);
    Assert.Contains("13:00", result.CodeRejectionDetail!);
    Assert.Contains("11/08/2026 10:00", result.CodeRejectionFix!);
  }

  [Fact]
  public void MemberOnlyPromotionAsksForTheMembership()
  {
    var promotion = Promotion(RestaurantPromotionRuleTypes.PercentOff);
    promotion.PercentOff = 10;
    promotion.MemberOnly = true;
    promotion.CodeRequired = true;
    promotion.Codes = [Code("MARTES25")];

    var result = Quote(promotion, "MARTES25");

    Assert.Equal(RestaurantPromotionRejectionReasons.MemberRequired, result.CodeRejectionReason);
    Assert.Contains("exclusiva de socios", result.CodeRejectionDetail!);
    Assert.Contains("Vincula la membresía", result.CodeRejectionFix!);
  }

  [Fact]
  public void ExhaustedCodeReportsItsCounters()
  {
    var promotion = Promotion(RestaurantPromotionRuleTypes.PercentOff);
    promotion.PercentOff = 10;
    promotion.CodeRequired = true;
    var code = Code("MARTES25");
    code.GlobalLimit = 50;
    code.RedemptionCount = 50;
    promotion.Codes = [code];

    var result = Quote(promotion, "MARTES25");

    Assert.Equal(RestaurantPromotionRejectionReasons.CodeLimitReached, result.CodeRejectionReason);
    Assert.Contains("50 usos", result.CodeRejectionDetail!);
  }

  [Fact]
  public void MemberThatAlreadyRedeemedGetsAPersonalExplanation()
  {
    var promotion = Promotion(RestaurantPromotionRuleTypes.FixedAmountOff);
    promotion.FixedAmount = 25;
    promotion.CodeRequired = true;
    var code = Code("MARTES25");
    code.PerMemberLimit = 1;
    code.MemberRedemptionCount = 1;
    promotion.Codes = [code];

    var result = Quote(promotion, "MARTES25", memberId: Guid.NewGuid());

    Assert.Equal(RestaurantPromotionRejectionReasons.MemberLimitReached, result.CodeRejectionReason);
    Assert.Contains("1 de 1", result.CodeRejectionDetail!);
    Assert.Contains("otros socios", result.CodeRejectionFix!);
  }

  [Fact]
  public void MinimumSubtotalSaysHowMuchIsMissing()
  {
    var promotion = Promotion(RestaurantPromotionRuleTypes.PercentOff);
    promotion.PercentOff = 10;
    promotion.CodeRequired = true;
    promotion.MinimumSubtotal = 300;
    promotion.Codes = [Code("MARTES25")];

    var result = Quote(promotion, "MARTES25");

    Assert.Equal(RestaurantPromotionRejectionReasons.MinimumSubtotal, result.CodeRejectionReason);
    Assert.Contains("300", result.CodeRejectionDetail!);
    Assert.Contains("100", result.CodeRejectionDetail!);
    Assert.Contains("200", result.CodeRejectionFix!);
  }

  [Fact]
  public void CartWithoutParticipatingProductsSaysSo()
  {
    var promotion = Promotion(RestaurantPromotionRuleTypes.PercentOff);
    promotion.PercentOff = 10;
    promotion.CodeRequired = true;
    promotion.ProductIds = new HashSet<long> { 999 };
    promotion.Codes = [Code("MARTES25")];

    var result = Quote(promotion, "MARTES25");

    Assert.Equal(RestaurantPromotionRejectionReasons.NoEligibleItems, result.CodeRejectionReason);
    Assert.Contains("Ningún producto de esta orden participa", result.CodeRejectionDetail!);
  }

  [Fact]
  public void BuyXPayYCountsTheMissingUnits()
  {
    var promotion = Promotion(RestaurantPromotionRuleTypes.BuyXPayY);
    promotion.BuyQuantity = 3;
    promotion.PayQuantity = 1;
    promotion.CodeRequired = true;
    promotion.Codes = [Code("MARTES25")];

    var result = Quote(promotion, "MARTES25", quantity: 2);

    Assert.Equal(RestaurantPromotionRejectionReasons.RuleNotSatisfied, result.CodeRejectionReason);
    Assert.Contains("3x1", result.CodeRejectionDetail!);
    Assert.Contains("la orden tiene 2", result.CodeRejectionDetail!);
    Assert.Contains("Agrega 1 pieza", result.CodeRejectionFix!);
  }

  [Fact]
  public void NonCombinablePromotionNamesTheOneThatWon()
  {
    var winner = Promotion(RestaurantPromotionRuleTypes.PercentOff, id: 1, name: "Mitad de precio");
    winner.PercentOff = 50;
    var coded = Promotion(RestaurantPromotionRuleTypes.PercentOff, id: 2, name: "Diez por ciento");
    coded.PercentOff = 10;
    coded.CodeRequired = true;
    coded.Codes = [Code("MARTES25")];

    var result = RestaurantPromotionEngine.Quote(
      Request("MARTES25", null, Line("line", 1, 1, 100)),
      [winner, coded],
      TuesdayAt(11));

    Assert.Equal(50m, result.PromotionDiscountTotal);
    Assert.Equal(RestaurantPromotionRejectionReasons.NotCombinable, result.CodeRejectionReason);
    Assert.Contains("Mitad de precio", result.CodeRejectionDetail!);
  }

  [Fact]
  public void PromotionThatAlreadyAppliedTellsTheCashierToClearTheCode()
  {
    var promotion = Promotion(RestaurantPromotionRuleTypes.PercentOff);
    promotion.PercentOff = 10;
    var code = Code("MARTES25");
    code.IsActive = false;
    promotion.Codes = [code];

    var result = Quote(promotion, "MARTES25");

    Assert.Equal(10m, result.PromotionDiscountTotal);
    Assert.Equal(RestaurantPromotionRejectionReasons.AlreadyApplied, result.CodeRejectionReason);
    Assert.Contains("Borra el código", result.CodeRejectionFix!);
  }

  private static RestaurantPromotionQuoteDto Quote(
    RestaurantPromotionDefinition promotion,
    string code,
    DateTimeOffset? at = null,
    Guid? memberId = null,
    decimal quantity = 1,
    decimal unitPrice = 100)
    => RestaurantPromotionEngine.Quote(
      Request(code, memberId, Line("line", 1, quantity, unitPrice)),
      [promotion],
      at ?? TuesdayAt(11));

  private static RestaurantPromotionQuoteRequest Request(
    string code,
    Guid? memberId,
    params RestaurantPromotionQuoteLineRequest[] lines)
    => new()
    {
      Rfc = "BRUNOS260707L26",
      SiteId = 1,
      Channel = RestaurantSalesChannels.Pos,
      OrderType = "Pickup",
      MemberId = memberId,
      Code = code,
      Lines = lines.ToList()
    };

  private static RestaurantPromotionQuoteLineRequest Line(
    string key,
    long productId,
    decimal quantity,
    decimal unitPrice)
    => new()
    {
      LineKey = key,
      ProductId = productId,
      Quantity = quantity,
      UnitPrice = unitPrice
    };

  private static RestaurantPromotionCodeDto Code(string value)
    => new() { Code = value, IsActive = true };

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
