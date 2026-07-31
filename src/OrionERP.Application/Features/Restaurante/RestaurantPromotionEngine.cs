namespace OrionERP.Application.Features.Restaurante;

public static class RestaurantPromotionEngine
{
  public static RestaurantPromotionQuoteDto Quote(
    RestaurantPromotionQuoteRequest request,
    IReadOnlyList<RestaurantPromotionDefinition> definitions,
    DateTimeOffset localAt)
  {
    ArgumentNullException.ThrowIfNull(request);
    ArgumentNullException.ThrowIfNull(definitions);

    var normalizedCode = NormalizeCode(request.Code);
    var states = request.Lines
      .Where(line => line.Quantity > 0 && line.UnitPrice >= 0)
      .Select(line => new LineState(line))
      .ToDictionary(line => line.Request.LineKey, StringComparer.Ordinal);
    var subtotal = Round(states.Values.Sum(line => line.Gross));
    var adjustments = new List<RestaurantPromotionAdjustmentDto>();
    var lineAdjustments = new List<RestaurantPromotionLineAdjustmentDto>();
    var appliedPromotionIds = new HashSet<long>();
    var codeAccepted = false;

    while (true)
    {
      var candidates = definitions
        .Where(definition => !appliedPromotionIds.Contains(definition.Id))
        .Where(definition => IsEligible(definition, request, localAt, normalizedCode))
        .Select(definition => CalculateCandidate(definition, states, normalizedCode))
        .Where(candidate => candidate is { Discount: > 0 })
        .Where(candidate =>
          adjustments.Count == 0 ||
          candidate!.Definition.IsCombinable ||
          candidate.Definition.RuleType == RestaurantPromotionRuleTypes.BuyXPayY ||
          candidate.Definition.RuleType == RestaurantPromotionRuleTypes.FixedBundlePrice)
        .OrderByDescending(candidate => candidate!.Discount)
        .ThenByDescending(candidate => candidate!.Definition.Priority)
        .ThenBy(candidate => candidate!.Definition.Id)
        .ToList();

      var selected = candidates.FirstOrDefault();
      if (selected is null)
      {
        break;
      }

      appliedPromotionIds.Add(selected.Definition.Id);
      ApplyCandidate(selected, states);
      var appliedCode = selected.Code?.Code;
      codeAccepted |= !string.IsNullOrWhiteSpace(appliedCode);
      adjustments.Add(new RestaurantPromotionAdjustmentDto
      {
        PromotionId = selected.Definition.Id,
        PromotionName = selected.Definition.Name,
        RuleType = selected.Definition.RuleType,
        Code = appliedCode,
        DiscountAmount = selected.Discount,
        IsCombinable = selected.Definition.IsCombinable
      });
      lineAdjustments.AddRange(selected.LineDiscounts
        .Where(pair => pair.Value > 0)
        .Select(pair => new RestaurantPromotionLineAdjustmentDto
        {
          LineKey = pair.Key,
          PromotionId = selected.Definition.Id,
          PromotionName = selected.Definition.Name,
          DiscountAmount = pair.Value,
          AppliedQuantity = selected.ConsumedQuantities.GetValueOrDefault(pair.Key)
        }));
    }

    var promotionDiscount = Round(adjustments.Sum(adjustment => adjustment.DiscountAmount));
    var manualDiscount = Round(states.Values.Sum(line => line.EffectiveManualDiscount));
    var hasKnownCode = string.IsNullOrWhiteSpace(normalizedCode) ||
      definitions.Any(definition => definition.Codes.Any(code =>
        code.IsActive &&
        string.Equals(code.Code, normalizedCode, StringComparison.OrdinalIgnoreCase)));

    return new RestaurantPromotionQuoteDto
    {
      EvaluatedAt = localAt,
      NormalizedCode = normalizedCode,
      MerchandiseSubtotal = subtotal,
      ManualDiscountTotal = manualDiscount,
      PromotionDiscountTotal = promotionDiscount,
      DiscountedMerchandise = Round(Math.Max(0, subtotal - manualDiscount - promotionDiscount)),
      CodeAccepted = string.IsNullOrWhiteSpace(normalizedCode) || codeAccepted,
      Message = string.IsNullOrWhiteSpace(normalizedCode)
        ? null
        : codeAccepted
          ? "Código aplicado."
          : hasKnownCode
            ? "El código no es elegible para esta orden o alcanzó su límite."
            : "El código no existe o está inactivo.",
      Adjustments = adjustments,
      LineAdjustments = lineAdjustments
    };
  }

  public static string? NormalizeCode(string? value)
    => string.IsNullOrWhiteSpace(value)
      ? null
      : value.Trim().ToUpperInvariant();

  private static bool IsEligible(
    RestaurantPromotionDefinition definition,
    RestaurantPromotionQuoteRequest request,
    DateTimeOffset localAt,
    string? normalizedCode)
  {
    if (definition.Status is not (RestaurantPromotionStatuses.Active or RestaurantPromotionStatuses.Scheduled))
    {
      return false;
    }
    if (definition.ValidFromLocal.HasValue && localAt.DateTime < definition.ValidFromLocal.Value ||
        definition.ValidToLocal.HasValue && localAt.DateTime >= definition.ValidToLocal.Value)
    {
      return false;
    }
    if (definition.GlobalLimit.HasValue && definition.RedemptionCount >= definition.GlobalLimit.Value)
    {
      return false;
    }
    if (string.Equals(request.Channel, RestaurantSalesChannels.Pos, StringComparison.OrdinalIgnoreCase) && !definition.PosEnabled ||
        string.Equals(request.Channel, RestaurantSalesChannels.Web, StringComparison.OrdinalIgnoreCase) && !definition.WebEnabled)
    {
      return false;
    }
    if (definition.MemberOnly && !request.MemberId.HasValue)
    {
      return false;
    }
    if (definition.Schedules.Count > 0 && !definition.Schedules.Any(schedule => MatchesSchedule(schedule, localAt)))
    {
      return false;
    }

    var eligibleCode = definition.Codes.FirstOrDefault(code =>
      code.IsActive &&
      !string.IsNullOrWhiteSpace(normalizedCode) &&
      string.Equals(code.Code, normalizedCode, StringComparison.OrdinalIgnoreCase) &&
      (!code.GlobalLimit.HasValue || code.RedemptionCount < code.GlobalLimit.Value) &&
      (!code.PerMemberLimit.HasValue ||
       request.MemberId.HasValue && code.MemberRedemptionCount < code.PerMemberLimit.Value));

    return !definition.CodeRequired || eligibleCode is not null;
  }

  private static bool MatchesSchedule(RestaurantPromotionScheduleDto schedule, DateTimeOffset localAt)
  {
    var day = (byte)localAt.DayOfWeek;
    var time = localAt.TimeOfDay;
    if (schedule.StartsAt < schedule.EndsAt)
    {
      return day == schedule.DayOfWeek && time >= schedule.StartsAt && time < schedule.EndsAt;
    }

    if (schedule.StartsAt > schedule.EndsAt)
    {
      var previousDay = (byte)(((int)localAt.DayOfWeek + 6) % 7);
      return day == schedule.DayOfWeek && time >= schedule.StartsAt ||
             previousDay == schedule.DayOfWeek && time < schedule.EndsAt;
    }

    return day == schedule.DayOfWeek;
  }

  private static PromotionCandidate? CalculateCandidate(
    RestaurantPromotionDefinition definition,
    IReadOnlyDictionary<string, LineState> states,
    string? normalizedCode)
  {
    var eligible = states.Values
      .Where(state => state.AvailableQuantity > 0 && state.AvailableAmount > 0)
      .Where(state => MatchesScope(definition, state.Request))
      .ToList();
    var quantity = eligible.Sum(state => state.AvailableQuantity);
    var amount = Round(eligible.Sum(state => state.AvailableAmount));
    if (eligible.Count == 0 ||
        quantity < definition.MinimumQuantity ||
        amount < definition.MinimumSubtotal)
    {
      return null;
    }

    var code = definition.Codes.FirstOrDefault(item =>
      item.IsActive &&
      !string.IsNullOrWhiteSpace(normalizedCode) &&
      string.Equals(item.Code, normalizedCode, StringComparison.OrdinalIgnoreCase));
    return definition.RuleType switch
    {
      RestaurantPromotionRuleTypes.BuyXPayY => CalculateBuyXPayY(definition, eligible, code),
      RestaurantPromotionRuleTypes.PercentOff => CalculatePercent(definition, eligible, code),
      RestaurantPromotionRuleTypes.FixedAmountOff => CalculateFixed(definition, eligible, code),
      RestaurantPromotionRuleTypes.FixedBundlePrice => CalculateBundle(definition, eligible, code),
      _ => null
    };
  }

  private static PromotionCandidate? CalculateBuyXPayY(
    RestaurantPromotionDefinition definition,
    IReadOnlyList<LineState> eligible,
    RestaurantPromotionCodeDto? code)
  {
    var buy = (int)decimal.Truncate(definition.BuyQuantity);
    var pay = (int)decimal.Truncate(definition.PayQuantity);
    if (buy <= 0 || pay < 0 || pay >= buy)
    {
      return null;
    }

    var units = ExpandWholeUnits(eligible)
      .OrderByDescending(unit => unit.NetUnitPrice)
      .ThenBy(unit => unit.LineKey, StringComparer.Ordinal)
      .ToList();
    var completeGroups = units.Count / buy;
    if (completeGroups == 0)
    {
      return null;
    }

    var lineDiscounts = new Dictionary<string, decimal>(StringComparer.Ordinal);
    var consumed = new Dictionary<string, decimal>(StringComparer.Ordinal);
    for (var groupIndex = 0; groupIndex < completeGroups; groupIndex++)
    {
      var group = units.Skip(groupIndex * buy).Take(buy).ToList();
      foreach (var unit in group)
      {
        consumed[unit.LineKey] = consumed.GetValueOrDefault(unit.LineKey) + 1;
      }
      foreach (var freeUnit in group.OrderBy(unit => unit.NetUnitPrice).Take(buy - pay))
      {
        lineDiscounts[freeUnit.LineKey] = Round(lineDiscounts.GetValueOrDefault(freeUnit.LineKey) + freeUnit.NetUnitPrice);
      }
    }

    return CreateCandidate(definition, code, lineDiscounts, consumed);
  }

  private static PromotionCandidate? CalculatePercent(
    RestaurantPromotionDefinition definition,
    IReadOnlyList<LineState> eligible,
    RestaurantPromotionCodeDto? code)
  {
    if (definition.PercentOff <= 0 || definition.PercentOff > 100)
    {
      return null;
    }
    var lineDiscounts = eligible.ToDictionary(
      state => state.Request.LineKey,
      state => Round(state.AvailableAmount * definition.PercentOff / 100m),
      StringComparer.Ordinal);
    var consumed = eligible.ToDictionary(
      state => state.Request.LineKey,
      state => state.AvailableQuantity,
      StringComparer.Ordinal);
    return CreateCandidate(definition, code, lineDiscounts, consumed);
  }

  private static PromotionCandidate? CalculateFixed(
    RestaurantPromotionDefinition definition,
    IReadOnlyList<LineState> eligible,
    RestaurantPromotionCodeDto? code)
  {
    var available = Round(eligible.Sum(state => state.AvailableAmount));
    var target = Round(Math.Min(definition.FixedAmount, available));
    if (target <= 0)
    {
      return null;
    }

    var lineDiscounts = AllocateAmount(eligible, target);
    var consumed = eligible.ToDictionary(
      state => state.Request.LineKey,
      state => state.AvailableQuantity,
      StringComparer.Ordinal);
    return CreateCandidate(definition, code, lineDiscounts, consumed);
  }

  private static PromotionCandidate? CalculateBundle(
    RestaurantPromotionDefinition definition,
    IReadOnlyList<LineState> eligible,
    RestaurantPromotionCodeDto? code)
  {
    var buy = (int)decimal.Truncate(definition.BuyQuantity);
    if (buy <= 0 || definition.BundlePrice < 0)
    {
      return null;
    }
    var units = ExpandWholeUnits(eligible)
      .OrderByDescending(unit => unit.NetUnitPrice)
      .ThenBy(unit => unit.LineKey, StringComparer.Ordinal)
      .ToList();
    var completeGroups = units.Count / buy;
    if (completeGroups == 0)
    {
      return null;
    }

    var lineDiscounts = new Dictionary<string, decimal>(StringComparer.Ordinal);
    var consumed = new Dictionary<string, decimal>(StringComparer.Ordinal);
    for (var groupIndex = 0; groupIndex < completeGroups; groupIndex++)
    {
      var group = units.Skip(groupIndex * buy).Take(buy).ToList();
      var groupAmount = Round(group.Sum(unit => unit.NetUnitPrice));
      var groupDiscount = Round(Math.Max(0, groupAmount - definition.BundlePrice));
      if (groupDiscount <= 0)
      {
        continue;
      }
      foreach (var unit in group)
      {
        consumed[unit.LineKey] = consumed.GetValueOrDefault(unit.LineKey) + 1;
      }
      var groupStates = group
        .GroupBy(unit => unit.LineKey, StringComparer.Ordinal)
        .Select(grouped => new AllocationState(
          grouped.Key,
          grouped.Count(),
          Round(grouped.Sum(unit => unit.NetUnitPrice))))
        .ToList();
      foreach (var pair in AllocateAmount(groupStates, groupDiscount))
      {
        lineDiscounts[pair.Key] = Round(lineDiscounts.GetValueOrDefault(pair.Key) + pair.Value);
      }
    }

    return CreateCandidate(definition, code, lineDiscounts, consumed);
  }

  private static PromotionCandidate? CreateCandidate(
    RestaurantPromotionDefinition definition,
    RestaurantPromotionCodeDto? code,
    IReadOnlyDictionary<string, decimal> lineDiscounts,
    IReadOnlyDictionary<string, decimal> consumed)
  {
    var discount = Round(lineDiscounts.Sum(pair => pair.Value));
    return discount <= 0
      ? null
      : new PromotionCandidate(definition, code, discount, lineDiscounts, consumed);
  }

  private static bool MatchesScope(RestaurantPromotionDefinition definition, RestaurantPromotionQuoteLineRequest line)
  {
    if (line.IsCustom || !line.ProductId.HasValue)
    {
      return false;
    }
    if (definition.ProductIds.Count == 0 && definition.MaterialCategoryIds.Count == 0)
    {
      return true;
    }
    return definition.ProductIds.Contains(line.ProductId.Value) ||
           line.MaterialCategoryId.HasValue && definition.MaterialCategoryIds.Contains(line.MaterialCategoryId.Value);
  }

  private static IReadOnlyList<UnitState> ExpandWholeUnits(IEnumerable<LineState> states)
  {
    var units = new List<UnitState>();
    foreach (var state in states)
    {
      var count = (int)decimal.Truncate(state.AvailableQuantity);
      if (count <= 0)
      {
        continue;
      }
      var unitPrice = Round(state.AvailableAmount / state.AvailableQuantity);
      for (var index = 0; index < count; index++)
      {
        units.Add(new UnitState(state.Request.LineKey, unitPrice));
      }
    }
    return units;
  }

  private static Dictionary<string, decimal> AllocateAmount(IReadOnlyList<LineState> states, decimal target)
    => AllocateAmount(
      states.Select(state => new AllocationState(
        state.Request.LineKey,
        state.AvailableQuantity,
        state.AvailableAmount)).ToList(),
      target);

  private static Dictionary<string, decimal> AllocateAmount(IReadOnlyList<AllocationState> states, decimal target)
  {
    var result = new Dictionary<string, decimal>(StringComparer.Ordinal);
    var total = Round(states.Sum(state => state.Amount));
    var remaining = target;
    for (var index = 0; index < states.Count; index++)
    {
      var state = states[index];
      var allocation = index == states.Count - 1
        ? remaining
        : Round(target * state.Amount / total);
      allocation = Round(Math.Clamp(allocation, 0, state.Amount));
      result[state.LineKey] = allocation;
      remaining = Round(remaining - allocation);
    }
    return result;
  }

  private static void ApplyCandidate(PromotionCandidate candidate, IReadOnlyDictionary<string, LineState> states)
  {
    foreach (var pair in candidate.ConsumedQuantities)
    {
      var state = states[pair.Key];
      var consumedQuantity = Math.Min(state.AvailableQuantity, pair.Value);
      var discount = Math.Min(state.AvailableAmount, candidate.LineDiscounts.GetValueOrDefault(pair.Key));
      var consumedAmountBeforeDiscount = state.AvailableQuantity <= 0
        ? 0
        : Round(state.AvailableAmount * consumedQuantity / state.AvailableQuantity);
      state.AvailableQuantity = Math.Max(0, state.AvailableQuantity - consumedQuantity);
      state.AvailableAmount = Round(Math.Max(0, state.AvailableAmount - consumedAmountBeforeDiscount));
      state.PromotionDiscount = Round(state.PromotionDiscount + discount);
    }
  }

  private static decimal Round(decimal value)
    => decimal.Round(value, 2, MidpointRounding.AwayFromZero);

  private sealed class LineState
  {
    public LineState(RestaurantPromotionQuoteLineRequest request)
    {
      Request = request;
      Gross = Round(request.UnitPrice * request.Quantity);
      RequestedManualDiscount = Round(Math.Clamp(request.ManualDiscountAmount, 0, Gross));
      AvailableQuantity = request.Quantity;
      AvailableAmount = Gross;
    }

    public RestaurantPromotionQuoteLineRequest Request { get; }
    public decimal Gross { get; }
    public decimal RequestedManualDiscount { get; }
    public decimal EffectiveManualDiscount => Round(Math.Min(RequestedManualDiscount, Math.Max(0, Gross - PromotionDiscount)));
    public decimal AvailableQuantity { get; set; }
    public decimal AvailableAmount { get; set; }
    public decimal PromotionDiscount { get; set; }
  }

  private sealed record UnitState(string LineKey, decimal NetUnitPrice);
  private sealed record AllocationState(string LineKey, decimal Quantity, decimal Amount);
  private sealed record PromotionCandidate(
    RestaurantPromotionDefinition Definition,
    RestaurantPromotionCodeDto? Code,
    decimal Discount,
    IReadOnlyDictionary<string, decimal> LineDiscounts,
    IReadOnlyDictionary<string, decimal> ConsumedQuantities);
}
