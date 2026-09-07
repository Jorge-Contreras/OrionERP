using System.Globalization;

namespace OrionERP.Application.Features.Restaurante;

public static class RestaurantPromotionEngine
{
  private static readonly CultureInfo MexicanCulture = CultureInfo.GetCultureInfo("es-MX");

  public static RestaurantPromotionQuoteDto Quote(
    RestaurantPromotionQuoteRequest request,
    IReadOnlyList<RestaurantPromotionDefinition> definitions,
    DateTimeOffset localAt)
  {
    ArgumentNullException.ThrowIfNull(request);
    ArgumentNullException.ThrowIfNull(definitions);

    var normalizedCode = NormalizeCode(request.Code);
    var states = BuildStates(request);
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
    var rejections = string.IsNullOrWhiteSpace(normalizedCode) || codeAccepted
      ? Array.Empty<RestaurantPromotionCodeRejectionDto>()
      : DiagnoseCode(request, definitions, localAt, normalizedCode!, states, adjustments);

    var quote = new RestaurantPromotionQuoteDto
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
          : null,
      Adjustments = adjustments,
      LineAdjustments = lineAdjustments
    };
    return rejections.Count == 0 ? quote : DescribeRejections(quote, rejections);
  }

  /// <summary>
  /// Vuelca el diagnóstico del código en la cotización: motivo principal, detalle,
  /// sugerencia y el mensaje corto que ve la caja.
  /// </summary>
  public static RestaurantPromotionQuoteDto DescribeRejections(
    RestaurantPromotionQuoteDto quote,
    IReadOnlyList<RestaurantPromotionCodeRejectionDto> rejections)
  {
    ArgumentNullException.ThrowIfNull(quote);
    ArgumentNullException.ThrowIfNull(rejections);
    var headline = rejections.Count == 0 ? null : rejections[0];
    quote.CodeRejections = rejections;
    quote.CodeRejectionReason = headline?.Reason;
    quote.CodeRejectionDetail = headline?.Detail;
    quote.CodeRejectionFix = headline?.Fix;
    quote.Message = headline is null
      ? quote.Message
      : string.Join(
          ' ',
          new[]
          {
            $"El código {quote.NormalizedCode} no se aplicó.",
            headline.Detail,
            headline.Fix
          }.Where(part => !string.IsNullOrWhiteSpace(part)));
    return quote;
  }

  public static string? NormalizeCode(string? value)
    => string.IsNullOrWhiteSpace(value)
      ? null
      : value.Trim().ToUpperInvariant();

  private static IReadOnlyList<RestaurantPromotionCodeRejectionDto> DiagnoseCode(
    RestaurantPromotionQuoteRequest request,
    IReadOnlyList<RestaurantPromotionDefinition> definitions,
    DateTimeOffset localAt,
    string normalizedCode,
    IReadOnlyDictionary<string, LineState> remaining,
    IReadOnlyList<RestaurantPromotionAdjustmentDto> adjustments)
  {
    var owners = definitions
      .Where(definition => definition.Codes.Any(code =>
        string.Equals(code.Code, normalizedCode, StringComparison.OrdinalIgnoreCase)))
      .ToList();
    if (owners.Count == 0)
    {
      return
      [
        new RestaurantPromotionCodeRejectionDto
        {
          Reason = RestaurantPromotionRejectionReasons.UnknownCode,
          Detail = $"Ninguna promoción de esta sede usa el código {normalizedCode}.",
          Fix = "Revisa cómo se escribió o pide al cliente el código vigente."
        }
      ];
    }

    var pristine = BuildStates(request);
    return owners
      .OrderByDescending(owner => owner.Priority)
      .ThenBy(owner => owner.Id)
      .Select(owner => Diagnose(owner, request, localAt, normalizedCode, pristine, remaining, adjustments))
      .OrderByDescending(rejection => Nearness(rejection.Reason))
      .ToList();
  }

  private static RestaurantPromotionCodeRejectionDto Diagnose(
    RestaurantPromotionDefinition definition,
    RestaurantPromotionQuoteRequest request,
    DateTimeOffset localAt,
    string normalizedCode,
    IReadOnlyDictionary<string, LineState> pristine,
    IReadOnlyDictionary<string, LineState> remaining,
    IReadOnlyList<RestaurantPromotionAdjustmentDto> adjustments)
  {
    var name = definition.Name;
    var code = definition.Codes.First(item =>
      string.Equals(item.Code, normalizedCode, StringComparison.OrdinalIgnoreCase));

    if (adjustments.Any(adjustment => adjustment.PromotionId == definition.Id))
    {
      return Rejection(definition, RestaurantPromotionRejectionReasons.AlreadyApplied,
        $"«{name}» ya se aplicó sola a esta orden y el código no agrega nada.",
        "Borra el código: el descuento ya está en el total.");
    }
    if (!code.IsActive)
    {
      return Rejection(definition, RestaurantPromotionRejectionReasons.CodeInactive,
        $"El código {normalizedCode} de «{name}» está desactivado.",
        "Un supervisor debe reactivarlo en Restaurante ▸ Promociones.");
    }
    if (definition.Status is not (RestaurantPromotionStatuses.Active or RestaurantPromotionStatuses.Scheduled))
    {
      return Rejection(definition, RestaurantPromotionRejectionReasons.PromotionNotActive,
        $"«{name}» {DescribeStatus(definition.Status)}.",
        "Un supervisor puede publicarla desde Restaurante ▸ Promociones.");
    }
    if (definition.ValidFromLocal.HasValue && localAt.DateTime < definition.ValidFromLocal.Value)
    {
      return Rejection(definition, RestaurantPromotionRejectionReasons.NotStarted,
        $"«{name}» empieza el {DateText(definition.ValidFromLocal.Value)}, dentro de {DescribeSpan(definition.ValidFromLocal.Value - localAt.DateTime)}.",
        "Aplícalo a partir de esa fecha.");
    }
    if (definition.ValidToLocal.HasValue && localAt.DateTime >= definition.ValidToLocal.Value)
    {
      return Rejection(definition, RestaurantPromotionRejectionReasons.Expired,
        $"«{name}» venció el {DateText(definition.ValidToLocal.Value)}, hace {DescribeSpan(localAt.DateTime - definition.ValidToLocal.Value)}.",
        "Avísale al cliente que la vigencia ya terminó.");
    }
    if (definition.GlobalLimit.HasValue && definition.RedemptionCount >= definition.GlobalLimit.Value)
    {
      return Rejection(definition, RestaurantPromotionRejectionReasons.PromotionLimitReached,
        $"«{name}» llegó a su tope de {definition.GlobalLimit.Value} canjes ({definition.RedemptionCount} registrados).",
        "La promoción se agotó; ofrécele al cliente otra vigente.");
    }
    if (string.Equals(request.Channel, RestaurantSalesChannels.Pos, StringComparison.OrdinalIgnoreCase) && !definition.PosEnabled)
    {
      return Rejection(definition, RestaurantPromotionRejectionReasons.ChannelDisabled,
        $"«{name}» no está habilitada para el punto de venta{(definition.WebEnabled ? "; solo aplica en pedidos por el sitio web" : string.Empty)}.",
        "Un supervisor puede habilitarla para mostrador en Restaurante ▸ Promociones.");
    }
    if (string.Equals(request.Channel, RestaurantSalesChannels.Web, StringComparison.OrdinalIgnoreCase) && !definition.WebEnabled)
    {
      return Rejection(definition, RestaurantPromotionRejectionReasons.ChannelDisabled,
        $"«{name}» no está habilitada para pedidos en línea{(definition.PosEnabled ? "; solo aplica en el punto de venta" : string.Empty)}.",
        "Un supervisor puede habilitarla para el sitio web en Restaurante ▸ Promociones.");
    }
    if (definition.MemberOnly && !request.MemberId.HasValue)
    {
      return Rejection(definition, RestaurantPromotionRejectionReasons.MemberRequired,
        $"«{name}» es exclusiva de socios y esta orden no tiene membresía vinculada.",
        "Vincula la membresía del cliente y vuelve a aplicar el código.");
    }
    if (definition.Schedules.Count > 0 && !definition.Schedules.Any(schedule => MatchesSchedule(schedule, localAt)))
    {
      var next = NextScheduleStart(definition.Schedules, localAt);
      return Rejection(definition, RestaurantPromotionRejectionReasons.OutsideSchedule,
        $"«{name}» solo aplica {DescribeSchedules(definition.Schedules)}, y ahora es {DayName((int)localAt.DayOfWeek)} {TimeText(localAt.TimeOfDay)}.",
        next is null
          ? "Cóbrala dentro del horario publicado."
          : $"El siguiente horario válido es el {DayName((int)next.Value.DayOfWeek)} a las {TimeText(next.Value.TimeOfDay)} ({DateText(next.Value.DateTime)}).");
    }
    if (code.GlobalLimit.HasValue && code.RedemptionCount >= code.GlobalLimit.Value)
    {
      return Rejection(definition, RestaurantPromotionRejectionReasons.CodeLimitReached,
        $"El código {normalizedCode} llegó a su tope de {code.GlobalLimit.Value} usos ({code.RedemptionCount} registrados).",
        "Ese código ya no puede volver a canjearse.");
    }
    if (code.PerMemberLimit.HasValue && !request.MemberId.HasValue)
    {
      return Rejection(definition, RestaurantPromotionRejectionReasons.MemberRequired,
        $"El código {normalizedCode} permite {code.PerMemberLimit.Value} canje(s) por socio y esta orden no tiene membresía vinculada.",
        "Vincula la membresía del cliente y vuelve a aplicar el código.");
    }
    if (code.PerMemberLimit.HasValue && code.MemberRedemptionCount >= code.PerMemberLimit.Value)
    {
      return Rejection(definition, RestaurantPromotionRejectionReasons.MemberLimitReached,
        $"Este socio ya canjeó {normalizedCode} {code.MemberRedemptionCount} de {code.PerMemberLimit.Value} vez(veces) permitida(s).",
        "El código sigue vigente para otros socios, pero no para este.");
    }

    var eligible = pristine.Values
      .Where(state => state.AvailableQuantity > 0 && state.AvailableAmount > 0)
      .Where(state => MatchesScope(definition, state.Request))
      .ToList();
    if (eligible.Count == 0)
    {
      var onlyCustom = pristine.Count > 0 &&
        pristine.Values.All(state => state.Request.IsCustom || !state.Request.ProductId.HasValue);
      return Rejection(definition, RestaurantPromotionRejectionReasons.NoEligibleItems,
        pristine.Count == 0
          ? "La orden todavía no tiene productos."
          : onlyCustom
            ? "Las partidas personalizadas no participan en promociones y la orden solo tiene partidas de ese tipo."
            : $"Ningún producto de esta orden participa en «{name}».",
        "Agrega un producto participante y vuelve a aplicar el código.");
    }

    var quantity = eligible.Sum(state => state.AvailableQuantity);
    var amount = Round(eligible.Sum(state => state.AvailableAmount));
    if (quantity < definition.MinimumQuantity)
    {
      return Rejection(definition, RestaurantPromotionRejectionReasons.MinimumQuantity,
        $"«{name}» pide mínimo {QuantityText(definition.MinimumQuantity)} piezas participantes y la orden lleva {QuantityText(quantity)}.",
        $"Faltan {QuantityText(definition.MinimumQuantity - quantity)} pieza(s) participante(s).");
    }
    if (amount < definition.MinimumSubtotal)
    {
      return Rejection(definition, RestaurantPromotionRejectionReasons.MinimumSubtotal,
        $"«{name}» pide un consumo mínimo de {Money(definition.MinimumSubtotal)} en productos participantes y la orden lleva {Money(amount)}.",
        $"Faltan {Money(definition.MinimumSubtotal - amount)} en productos participantes.");
    }

    var candidate = CalculateCandidate(definition, pristine, normalizedCode);
    if (candidate is null)
    {
      return DescribeRuleFailure(definition, eligible);
    }
    if (adjustments.Count > 0)
    {
      var blocked = !definition.IsCombinable &&
        definition.RuleType is not (RestaurantPromotionRuleTypes.BuyXPayY or RestaurantPromotionRuleTypes.FixedBundlePrice);
      var live = CalculateCandidate(definition, remaining, normalizedCode);
      if (blocked || live is null)
      {
        var others = string.Join(
          ", ",
          adjustments.Select(adjustment => $"«{adjustment.PromotionName}»").Distinct(StringComparer.Ordinal));
        return Rejection(definition, RestaurantPromotionRejectionReasons.NotCombinable,
          blocked
            ? $"«{name}» no se acumula con otras promociones y {others} ya se aplicó a esta orden."
            : $"{others} ya descontó los productos que «{name}» necesita.",
          "Cobra los productos participantes en una orden aparte o quita la otra promoción.");
      }
    }
    return Rejection(definition, RestaurantPromotionRejectionReasons.RuleNotSatisfied,
      $"«{name}» no generó descuento con los productos de esta orden.",
      "Revisa las condiciones de la promoción.");
  }

  private static RestaurantPromotionCodeRejectionDto DescribeRuleFailure(
    RestaurantPromotionDefinition definition,
    IReadOnlyList<LineState> eligible)
  {
    var name = definition.Name;
    var units = eligible.Sum(state => (int)decimal.Truncate(state.AvailableQuantity));
    var amount = Round(eligible.Sum(state => state.AvailableAmount));
    switch (definition.RuleType)
    {
      case RestaurantPromotionRuleTypes.BuyXPayY:
      {
        var buy = (int)decimal.Truncate(definition.BuyQuantity);
        var pay = (int)decimal.Truncate(definition.PayQuantity);
        return buy <= 0 || pay < 0 || pay >= buy
          ? Rejection(definition, RestaurantPromotionRejectionReasons.RuleMisconfigured,
              $"«{name}» está mal configurada: compra {buy} y paga {pay}.",
              "Un supervisor debe corregirla en Restaurante ▸ Promociones.")
          : Rejection(definition, RestaurantPromotionRejectionReasons.RuleNotSatisfied,
              $"«{name}» es {buy}x{pay}: necesita {buy} piezas participantes completas y la orden tiene {units}.",
              $"Agrega {buy - units} pieza(s) participante(s) para completar el {buy}x{pay}.");
      }
      case RestaurantPromotionRuleTypes.PercentOff:
        return definition.PercentOff <= 0 || definition.PercentOff > 100
          ? Rejection(definition, RestaurantPromotionRejectionReasons.RuleMisconfigured,
              $"«{name}» tiene un porcentaje inválido ({QuantityText(definition.PercentOff)}%).",
              "Un supervisor debe corregirla en Restaurante ▸ Promociones.")
          : Rejection(definition, RestaurantPromotionRejectionReasons.NoSavings,
              $"«{name}» descuenta {QuantityText(definition.PercentOff)}%, pero {Money(amount)} de productos participantes no alcanzan ni un centavo.",
              "Agrega más productos participantes.");
      case RestaurantPromotionRuleTypes.FixedAmountOff:
        return definition.FixedAmount <= 0
          ? Rejection(definition, RestaurantPromotionRejectionReasons.RuleMisconfigured,
              $"«{name}» tiene un descuento fijo de {Money(definition.FixedAmount)}.",
              "Un supervisor debe corregirla en Restaurante ▸ Promociones.")
          : Rejection(definition, RestaurantPromotionRejectionReasons.NoSavings,
              $"«{name}» descuenta {Money(definition.FixedAmount)}, pero los productos participantes suman {Money(amount)}.",
              "Agrega más productos participantes.");
      case RestaurantPromotionRuleTypes.FixedBundlePrice:
      {
        var buy = (int)decimal.Truncate(definition.BuyQuantity);
        if (buy <= 0 || definition.BundlePrice < 0)
        {
          return Rejection(definition, RestaurantPromotionRejectionReasons.RuleMisconfigured,
            $"«{name}» tiene un paquete mal configurado ({buy} piezas a {Money(definition.BundlePrice)}).",
            "Un supervisor debe corregirla en Restaurante ▸ Promociones.");
        }
        return units < buy
          ? Rejection(definition, RestaurantPromotionRejectionReasons.RuleNotSatisfied,
              $"«{name}» arma paquetes de {buy} piezas y la orden tiene {units} participantes.",
              $"Agrega {buy - units} pieza(s) participante(s) para completar el paquete.")
          : Rejection(definition, RestaurantPromotionRejectionReasons.NoSavings,
              $"El paquete de «{name}» cuesta {Money(definition.BundlePrice)} y las piezas participantes no lo superan, así que no hay descuento.",
              "El precio normal ya es igual o mejor que el del paquete.");
      }
      default:
        return Rejection(definition, RestaurantPromotionRejectionReasons.RuleMisconfigured,
          $"«{name}» usa un tipo de regla desconocido ({definition.RuleType}).",
          "Un supervisor debe corregirla en Restaurante ▸ Promociones.");
    }
  }

  private static RestaurantPromotionCodeRejectionDto Rejection(
    RestaurantPromotionDefinition definition,
    string reason,
    string detail,
    string? fix)
    => new()
    {
      Reason = reason,
      PromotionId = definition.Id,
      PromotionName = definition.Name,
      Detail = detail,
      Fix = fix
    };

  private static int Nearness(string reason) => reason switch
  {
    RestaurantPromotionRejectionReasons.AlreadyApplied => 11,
    RestaurantPromotionRejectionReasons.NotCombinable => 10,
    RestaurantPromotionRejectionReasons.NoSavings => 9,
    RestaurantPromotionRejectionReasons.RuleNotSatisfied => 8,
    RestaurantPromotionRejectionReasons.MinimumSubtotal => 7,
    RestaurantPromotionRejectionReasons.MinimumQuantity => 7,
    RestaurantPromotionRejectionReasons.NoEligibleItems => 6,
    RestaurantPromotionRejectionReasons.OutsideSchedule => 5,
    RestaurantPromotionRejectionReasons.MemberRequired => 4,
    RestaurantPromotionRejectionReasons.MemberLimitReached => 4,
    RestaurantPromotionRejectionReasons.ChannelDisabled => 3,
    RestaurantPromotionRejectionReasons.CodeLimitReached => 2,
    RestaurantPromotionRejectionReasons.PromotionLimitReached => 2,
    RestaurantPromotionRejectionReasons.NotStarted => 1,
    RestaurantPromotionRejectionReasons.Expired => 1,
    _ => 0
  };

  private static string DescribeStatus(string status) => status switch
  {
    RestaurantPromotionStatuses.Draft => "sigue en borrador y todavía no se publica",
    RestaurantPromotionStatuses.Paused => "está pausada",
    RestaurantPromotionStatuses.Expired => "está marcada como terminada",
    _ => $"tiene el estado {status} y no puede aplicarse"
  };

  private static string DescribeSchedules(IReadOnlyList<RestaurantPromotionScheduleDto> schedules)
  {
    var listed = schedules
      .OrderBy(schedule => schedule.DayOfWeek)
      .ThenBy(schedule => schedule.StartsAt)
      .Take(4)
      .Select(schedule => schedule.StartsAt == schedule.EndsAt
        ? $"{DayName(schedule.DayOfWeek)} todo el día"
        : $"{DayName(schedule.DayOfWeek)} de {TimeText(schedule.StartsAt)} a {TimeText(schedule.EndsAt)}");
    var text = string.Join("; ", listed);
    return schedules.Count > 4 ? $"{text} (+{schedules.Count - 4} horarios más)" : text;
  }

  private static DateTimeOffset? NextScheduleStart(
    IReadOnlyList<RestaurantPromotionScheduleDto> schedules,
    DateTimeOffset localAt)
  {
    DateTimeOffset? best = null;
    for (var offset = 0; offset <= 7; offset++)
    {
      var day = localAt.Date.AddDays(offset);
      foreach (var schedule in schedules.Where(item => item.DayOfWeek == (byte)day.DayOfWeek))
      {
        var start = new DateTimeOffset(day.Add(schedule.StartsAt), localAt.Offset);
        if (start > localAt && (best is null || start < best.Value))
        {
          best = start;
        }
      }
    }
    return best;
  }

  private static string DescribeSpan(TimeSpan span)
  {
    var total = span.Duration();
    if (total.TotalHours < 1)
    {
      var minutes = Math.Max(1, (int)total.TotalMinutes);
      return minutes == 1 ? "1 minuto" : $"{minutes} minutos";
    }
    if (total.TotalDays < 1)
    {
      var hours = (int)total.TotalHours;
      return hours == 1 ? "1 hora" : $"{hours} horas";
    }
    var days = (int)total.TotalDays;
    return days == 1 ? "1 día" : $"{days} días";
  }

  private static string Money(decimal value) => value.ToString("C2", MexicanCulture);

  private static string QuantityText(decimal value) => value.ToString("0.##", MexicanCulture);

  private static string DateText(DateTime value) => value.ToString("dd/MM/yyyy HH:mm", MexicanCulture);

  private static string TimeText(TimeSpan value) => $"{(int)value.TotalHours:00}:{value.Minutes:00}";

  private static string DayName(int day) => MexicanCulture.DateTimeFormat.DayNames[day % 7];

  private static Dictionary<string, LineState> BuildStates(RestaurantPromotionQuoteRequest request)
    => request.Lines
      .Where(line => line.Quantity > 0 && line.UnitPrice >= 0)
      .Select(line => new LineState(line))
      .ToDictionary(line => line.Request.LineKey, StringComparer.Ordinal);

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
