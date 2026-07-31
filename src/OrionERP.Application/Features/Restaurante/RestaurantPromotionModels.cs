using System.ComponentModel.DataAnnotations;

namespace OrionERP.Application.Features.Restaurante;

public static class RestaurantPromotionStatuses
{
  public const string Draft = "Draft";
  public const string Scheduled = "Scheduled";
  public const string Active = "Active";
  public const string Paused = "Paused";
  public const string Expired = "Expired";

  public static readonly IReadOnlySet<string> All = new HashSet<string>(
    [Draft, Scheduled, Active, Paused, Expired],
    StringComparer.OrdinalIgnoreCase);
}

public static class RestaurantPromotionRuleTypes
{
  public const string BuyXPayY = "BuyXPayY";
  public const string PercentOff = "PercentOff";
  public const string FixedAmountOff = "FixedAmountOff";
  public const string FixedBundlePrice = "FixedBundlePrice";

  public static readonly IReadOnlySet<string> All = new HashSet<string>(
    [BuyXPayY, PercentOff, FixedAmountOff, FixedBundlePrice],
    StringComparer.OrdinalIgnoreCase);
}

public static class RestaurantSalesChannels
{
  public const string Pos = "POS";
  public const string Web = "Web";
}

public sealed class RestaurantPromotionScheduleDto
{
  public long Id { get; set; }
  public byte DayOfWeek { get; set; }
  public TimeSpan StartsAt { get; set; }
  public TimeSpan EndsAt { get; set; }
}

public sealed class RestaurantPromotionCodeDto
{
  public long Id { get; set; }
  public string Code { get; set; } = string.Empty;
  public int? GlobalLimit { get; set; }
  public int? PerMemberLimit { get; set; }
  public int RedemptionCount { get; set; }
  public int MemberRedemptionCount { get; set; }
  public bool IsActive { get; set; }
}

public sealed class RestaurantPromotionDto
{
  public long Id { get; set; }
  public string Rfc { get; set; } = string.Empty;
  public int? SiteId { get; set; }
  public string? SiteName { get; set; }
  public string Name { get; set; } = string.Empty;
  public string PublicDescription { get; set; } = string.Empty;
  public string PublicTerms { get; set; } = string.Empty;
  public string Status { get; set; } = RestaurantPromotionStatuses.Draft;
  public string RuleType { get; set; } = RestaurantPromotionRuleTypes.BuyXPayY;
  public int Priority { get; set; }
  public DateTime? ValidFromLocal { get; set; }
  public DateTime? ValidToLocal { get; set; }
  public bool PosEnabled { get; set; } = true;
  public bool WebEnabled { get; set; }
  public bool MemberOnly { get; set; }
  public bool CodeRequired { get; set; }
  public bool IsCombinable { get; set; }
  public bool IsPublic { get; set; } = true;
  public decimal BuyQuantity { get; set; }
  public decimal PayQuantity { get; set; }
  public decimal PercentOff { get; set; }
  public decimal FixedAmount { get; set; }
  public decimal BundlePrice { get; set; }
  public decimal MinimumQuantity { get; set; }
  public decimal MinimumSubtotal { get; set; }
  public int? GlobalLimit { get; set; }
  public int RedemptionCount { get; set; }
  public IReadOnlyList<long> ProductIds { get; set; } = Array.Empty<long>();
  public IReadOnlyList<int> MaterialCategoryIds { get; set; } = Array.Empty<int>();
  public IReadOnlyList<RestaurantPromotionScheduleDto> Schedules { get; set; } = Array.Empty<RestaurantPromotionScheduleDto>();
  public IReadOnlyList<RestaurantPromotionCodeDto> Codes { get; set; } = Array.Empty<RestaurantPromotionCodeDto>();
  public DateTime CreatedAt { get; set; }
  public DateTime UpdatedAt { get; set; }
}

public sealed class RestaurantPromotionSaveRequest
{
  [Required] public string Rfc { get; set; } = string.Empty;
  public long? Id { get; set; }
  public int? SiteId { get; set; }
  [Required, StringLength(160)] public string Name { get; set; } = string.Empty;
  [Required, StringLength(800)] public string PublicDescription { get; set; } = string.Empty;
  [Required, StringLength(2000)] public string PublicTerms { get; set; } = string.Empty;
  [Required] public string Status { get; set; } = RestaurantPromotionStatuses.Draft;
  [Required] public string RuleType { get; set; } = RestaurantPromotionRuleTypes.BuyXPayY;
  [Range(-100000, 100000)] public int Priority { get; set; }
  public DateTime? ValidFromLocal { get; set; }
  public DateTime? ValidToLocal { get; set; }
  public bool PosEnabled { get; set; } = true;
  public bool WebEnabled { get; set; }
  public bool MemberOnly { get; set; }
  public bool CodeRequired { get; set; }
  public bool IsCombinable { get; set; }
  public bool IsPublic { get; set; } = true;
  [Range(typeof(decimal), "0", "999999")] public decimal BuyQuantity { get; set; }
  [Range(typeof(decimal), "0", "999999")] public decimal PayQuantity { get; set; }
  [Range(typeof(decimal), "0", "100")] public decimal PercentOff { get; set; }
  [Range(typeof(decimal), "0", "999999999")] public decimal FixedAmount { get; set; }
  [Range(typeof(decimal), "0", "999999999")] public decimal BundlePrice { get; set; }
  [Range(typeof(decimal), "0", "999999")] public decimal MinimumQuantity { get; set; }
  [Range(typeof(decimal), "0", "999999999")] public decimal MinimumSubtotal { get; set; }
  [Range(1, int.MaxValue)] public int? GlobalLimit { get; set; }
  public List<long> ProductIds { get; set; } = [];
  public List<int> MaterialCategoryIds { get; set; } = [];
  public List<RestaurantPromotionScheduleSaveRequest> Schedules { get; set; } = [];
  public List<RestaurantPromotionCodeSaveRequest> Codes { get; set; } = [];
}

public sealed class RestaurantPromotionScheduleSaveRequest
{
  [Range(0, 6)] public byte DayOfWeek { get; set; }
  public TimeSpan StartsAt { get; set; }
  public TimeSpan EndsAt { get; set; }
}

public sealed class RestaurantPromotionCodeSaveRequest
{
  [Required, RegularExpression("^[A-Za-z0-9-]{3,32}$")] public string Code { get; set; } = string.Empty;
  [Range(1, int.MaxValue)] public int? GlobalLimit { get; set; }
  [Range(1, int.MaxValue)] public int? PerMemberLimit { get; set; }
  public bool IsActive { get; set; } = true;
}

public sealed class RestaurantPromotionQuoteRequest
{
  [Required] public string Rfc { get; set; } = string.Empty;
  public int SiteId { get; set; }
  public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
  public string Channel { get; set; } = RestaurantSalesChannels.Pos;
  public string OrderType { get; set; } = "Pickup";
  public Guid? MemberId { get; set; }
  public string? Code { get; set; }
  public List<RestaurantPromotionQuoteLineRequest> Lines { get; set; } = [];
}

public sealed class RestaurantPromotionQuoteLineRequest
{
  [Required] public string LineKey { get; set; } = string.Empty;
  public long? ProductId { get; set; }
  public int? MaterialCategoryId { get; set; }
  public decimal Quantity { get; set; }
  public decimal UnitPrice { get; set; }
  public decimal ManualDiscountAmount { get; set; }
  public bool IsCustom { get; set; }
}

public sealed class RestaurantPromotionQuoteDto
{
  public DateTimeOffset EvaluatedAt { get; set; }
  public string? NormalizedCode { get; set; }
  public decimal MerchandiseSubtotal { get; set; }
  public decimal ManualDiscountTotal { get; set; }
  public decimal PromotionDiscountTotal { get; set; }
  public decimal DiscountedMerchandise { get; set; }
  public bool CodeAccepted { get; set; }
  public string? Message { get; set; }
  public IReadOnlyList<RestaurantPromotionAdjustmentDto> Adjustments { get; set; } = Array.Empty<RestaurantPromotionAdjustmentDto>();
  public IReadOnlyList<RestaurantPromotionLineAdjustmentDto> LineAdjustments { get; set; } = Array.Empty<RestaurantPromotionLineAdjustmentDto>();
}

public sealed class RestaurantPromotionAdjustmentDto
{
  public long PromotionId { get; set; }
  public string PromotionName { get; set; } = string.Empty;
  public string RuleType { get; set; } = string.Empty;
  public string? Code { get; set; }
  public decimal DiscountAmount { get; set; }
  public bool IsCombinable { get; set; }
}

public sealed class RestaurantPromotionLineAdjustmentDto
{
  public string LineKey { get; set; } = string.Empty;
  public long PromotionId { get; set; }
  public string PromotionName { get; set; } = string.Empty;
  public decimal DiscountAmount { get; set; }
  public decimal AppliedQuantity { get; set; }
}

public sealed class RestaurantPromotionReportDto
{
  public DateTime From { get; set; }
  public DateTime To { get; set; }
  public decimal GrossSales { get; set; }
  public decimal PromotionDiscount { get; set; }
  public decimal NetSales { get; set; }
  public int OrderCount { get; set; }
  public IReadOnlyList<RestaurantPromotionPerformanceDto> Promotions { get; set; } = Array.Empty<RestaurantPromotionPerformanceDto>();
}

public sealed class RestaurantPromotionPerformanceDto
{
  public long PromotionId { get; set; }
  public string PromotionName { get; set; } = string.Empty;
  public string? Code { get; set; }
  public int RedemptionCount { get; set; }
  public int OrderCount { get; set; }
  public decimal GrossSales { get; set; }
  public decimal DiscountAmount { get; set; }
  public decimal NetSales { get; set; }
}

public sealed class RestaurantPromotionDefinition
{
  public long Id { get; set; }
  public string Name { get; set; } = string.Empty;
  public string Status { get; set; } = string.Empty;
  public string RuleType { get; set; } = string.Empty;
  public int Priority { get; set; }
  public DateTime? ValidFromLocal { get; set; }
  public DateTime? ValidToLocal { get; set; }
  public bool PosEnabled { get; set; }
  public bool WebEnabled { get; set; }
  public bool MemberOnly { get; set; }
  public bool CodeRequired { get; set; }
  public bool IsCombinable { get; set; }
  public decimal BuyQuantity { get; set; }
  public decimal PayQuantity { get; set; }
  public decimal PercentOff { get; set; }
  public decimal FixedAmount { get; set; }
  public decimal BundlePrice { get; set; }
  public decimal MinimumQuantity { get; set; }
  public decimal MinimumSubtotal { get; set; }
  public int? GlobalLimit { get; set; }
  public int RedemptionCount { get; set; }
  public int MemberRedemptionCount { get; set; }
  public IReadOnlySet<long> ProductIds { get; set; } = new HashSet<long>();
  public IReadOnlySet<int> MaterialCategoryIds { get; set; } = new HashSet<int>();
  public IReadOnlyList<RestaurantPromotionScheduleDto> Schedules { get; set; } = Array.Empty<RestaurantPromotionScheduleDto>();
  public IReadOnlyList<RestaurantPromotionCodeDto> Codes { get; set; } = Array.Empty<RestaurantPromotionCodeDto>();
}
