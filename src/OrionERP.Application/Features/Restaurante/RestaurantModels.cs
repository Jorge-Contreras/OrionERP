using System.ComponentModel.DataAnnotations;
using OrionERP.Application.Features.Logistica.Materials;

namespace OrionERP.Application.Features.Restaurante;

public static class RestaurantOrderStatuses
{
  public const string Draft = "Draft";
  public const string AwaitingPayment = "AwaitingPayment";
  public const string Sent = "Sent";
  public const string Preparing = "Preparing";
  public const string Ready = "Ready";
  public const string Dispatched = "Dispatched";
  public const string Delivered = "Delivered";
  public const string Completed = "Completed";
  public const string Cancelled = "Cancelled";
}

public static class RestaurantPaymentStatuses
{
  public const string Pending = "Pending";
  public const string Partial = "Partial";
  public const string Paid = "Paid";
  public const string PartiallyRefunded = "PartiallyRefunded";
  public const string Refunded = "Refunded";
  public const string PendingSettlement = "PendingSettlement";
}

public static class RestaurantProductKinds
{
  public const string Standard = "Standard";
  public const string Combo = "Combo";

  public static bool IsValid(string? value)
    => string.Equals(value, Standard, StringComparison.OrdinalIgnoreCase)
       || string.Equals(value, Combo, StringComparison.OrdinalIgnoreCase);

  public static string Normalize(string? value)
    => string.Equals(value, Combo, StringComparison.OrdinalIgnoreCase) ? Combo : Standard;
}

public static class RestaurantOrderLineKinds
{
  public const string Standard = "Standard";
  public const string Combo = "Combo";
  public const string ComboComponent = "ComboComponent";
}

public static class RestaurantModifierEffectKinds
{
  public const string AddQuantity = "AddQuantity";
  public const string RemoveIngredient = "RemoveIngredient";
  public const string AdjustQuantity = "AdjustQuantity";

  public static bool IsValid(string? value)
    => string.Equals(value, AddQuantity, StringComparison.OrdinalIgnoreCase)
       || string.Equals(value, RemoveIngredient, StringComparison.OrdinalIgnoreCase)
       || string.Equals(value, AdjustQuantity, StringComparison.OrdinalIgnoreCase);

  public static string Normalize(string? value)
    => string.Equals(value, AddQuantity, StringComparison.OrdinalIgnoreCase)
      ? AddQuantity
      : string.Equals(value, RemoveIngredient, StringComparison.OrdinalIgnoreCase)
        ? RemoveIngredient
        : AdjustQuantity;
}

public sealed class RestaurantSiteDto
{
  public int Id { get; set; }
  public string Rfc { get; set; } = string.Empty;
  public string SiteCode { get; set; } = string.Empty;
  public string Name { get; set; } = string.Empty;
  public string TimeZoneId { get; set; } = string.Empty;
  public TimeSpan OperationalDayCutoff { get; set; }
  public decimal TaxRate { get; set; }
  public bool PricesIncludeTax { get; set; }
  public bool IsEnabled { get; set; }
  public bool AllowSupervisorDeficit { get; set; }
  public string CrossContaminationWarning { get; set; } = string.Empty;
}

public sealed class RestaurantSiteUpsertRequest
{
  [Required] public string Rfc { get; set; } = string.Empty;
  public int? Id { get; set; }
  [Required, StringLength(30)] public string SiteCode { get; set; } = string.Empty;
  [Required, StringLength(150)] public string Name { get; set; } = string.Empty;
  [Required, StringLength(100)] public string TimeZoneId { get; set; } = "Central Standard Time (Mexico)";
  public TimeSpan OperationalDayCutoff { get; set; } = new(4, 0, 0);
  [Range(0, 1)] public decimal TaxRate { get; set; } = 0.16m;
  public bool PricesIncludeTax { get; set; } = true;
  public bool IsEnabled { get; set; }
  public bool AllowSupervisorDeficit { get; set; }
  [Required, StringLength(300)] public string CrossContaminationWarning { get; set; } = "Puede existir contaminación cruzada. Consulte al personal si tiene alergias.";
}

public sealed class RestaurantProductDto
{
  public long Id { get; set; }
  public long ProductCardId { get; set; }
  public int? MaterialId { get; set; }
  public int? MaterialCategoryId { get; set; }
  public string ProductKind { get; set; } = RestaurantProductKinds.Standard;
  public string Sku { get; set; } = string.Empty;
  public string Name { get; set; } = string.Empty;
  public string? Description { get; set; }
  public string? VariantName { get; set; }
  public decimal Price { get; set; }
  public int? KitchenStationId { get; set; }
  public string? KitchenStationName { get; set; }
  public int? PreparationMinutes { get; set; }
  public bool IsActive { get; set; }
  public bool IsSoldOut { get; set; }
  public bool HasImage { get; set; }
  public bool HasVariantImage { get; set; }
  public string ProductType { get; set; } = string.Empty;
  public string FulfillmentMode { get; set; } = string.Empty;
  public decimal TheoreticalCost { get; set; }
  public IReadOnlyList<string> Allergens { get; set; } = Array.Empty<string>();
  public IReadOnlyList<string> DietaryTags { get; set; } = Array.Empty<string>();
  public IReadOnlyList<RestaurantModifierGroupDto> ModifierGroups { get; set; } = Array.Empty<RestaurantModifierGroupDto>();
  public IReadOnlyList<RestaurantComboSlotDto> ComboSlots { get; set; } = Array.Empty<RestaurantComboSlotDto>();
}

public sealed class RestaurantProductUpsertRequest
{
  [Required] public string Rfc { get; set; } = string.Empty;
  public long? Id { get; set; }
  public long? ProductCardId { get; set; }
  public int? MaterialId { get; set; }
  [Required, StringLength(20)] public string ProductKind { get; set; } = RestaurantProductKinds.Standard;
  /// <summary>
  /// Rol de producción del material. Sustituye a los antiguos campos independientes
  /// ProductType/FulfillmentMode, que podían quedar en combinaciones contradictorias.
  /// </summary>
  [Required, StringLength(40)] public string ProductionRole { get; set; } = MaterialProductionRoles.OnDemandFinishedGood;
  [Required, StringLength(50)] public string Sku { get; set; } = string.Empty;
  [Required, StringLength(160)] public string Name { get; set; } = string.Empty;
  [StringLength(800)] public string? Description { get; set; }
  [StringLength(120)] public string? VariantName { get; set; }
  [Range(0, 999999999)] public decimal Price { get; set; }
  public int? KitchenStationId { get; set; }
  [Range(0, 1440)] public int? PreparationMinutes { get; set; }
  public bool IsActive { get; set; } = true;
  public bool SoldOutOverride { get; set; }
  public byte[]? FamilyImage { get; set; }
  public byte[]? FamilyImageThumbnail { get; set; }
  public string? ImageFileName { get; set; }
  public string? ImageContentType { get; set; }
  public byte[]? VariantImage { get; set; }
  public byte[]? VariantImageThumbnail { get; set; }
  public string? VariantImageFileName { get; set; }
  public string? VariantImageContentType { get; set; }
  public List<string> DietaryTags { get; set; } = [];
}

public sealed class RestaurantModifierGroupDto
{
  public long Id { get; set; }
  public string Name { get; set; } = string.Empty;
  public int MinSelections { get; set; }
  public int MaxSelections { get; set; }
  public IReadOnlyList<RestaurantModifierOptionDto> Options { get; set; } = Array.Empty<RestaurantModifierOptionDto>();
}

public sealed class RestaurantModifierOptionDto
{
  public long Id { get; set; }
  public string Name { get; set; } = string.Empty;
  public decimal PriceDelta { get; set; }
  public IReadOnlyList<RestaurantModifierEffectDto> IngredientEffects { get; set; } = Array.Empty<RestaurantModifierEffectDto>();
}

public sealed class RestaurantModifierEffectDto
{
  public int MaterialId { get; set; }
  public string MaterialName { get; set; } = string.Empty;
  public string EffectKind { get; set; } = RestaurantModifierEffectKinds.AdjustQuantity;
  public decimal Quantity { get; set; }
  public int? UnitId { get; set; }
  public string? UnitName { get; set; }
}

public sealed class RestaurantComboSlotDto
{
  public long Id { get; set; }
  public string Name { get; set; } = string.Empty;
  public int MinSelections { get; set; }
  public int MaxSelections { get; set; }
  public int SortOrder { get; set; }
  public bool IsActive { get; set; }
  public IReadOnlyList<RestaurantComboSlotOptionDto> Options { get; set; } = Array.Empty<RestaurantComboSlotOptionDto>();
}

public sealed class RestaurantComboSlotOptionDto
{
  public long Id { get; set; }
  public long ComponentProductId { get; set; }
  public string ComponentProductName { get; set; } = string.Empty;
  public string ComponentSku { get; set; } = string.Empty;
  public decimal Quantity { get; set; } = 1;
  public decimal PriceDelta { get; set; }
  public bool IsDefault { get; set; }
  public int SortOrder { get; set; }
  public bool IsActive { get; set; }
  public bool IsSoldOut { get; set; }
  public IReadOnlyList<string> Allergens { get; set; } = Array.Empty<string>();
  public IReadOnlyList<RestaurantModifierGroupDto> ComponentModifierGroups { get; set; } = Array.Empty<RestaurantModifierGroupDto>();
  public RestaurantProductDto? ComponentProduct { get; set; }
  public IReadOnlyList<RestaurantComboOptionRouteDto> Routes { get; set; } = Array.Empty<RestaurantComboOptionRouteDto>();
}

public sealed class RestaurantComboOptionRouteDto
{
  public long MenuId { get; set; }
  public string MenuName { get; set; } = string.Empty;
  public long MenuSectionId { get; set; }
  public string MenuSectionName { get; set; } = string.Empty;
}

public sealed class RestaurantMenuSectionDto
{
  public long Id { get; set; }
  public string Name { get; set; } = string.Empty;
  public int SortOrder { get; set; }
  public IReadOnlyList<RestaurantProductDto> Products { get; set; } = Array.Empty<RestaurantProductDto>();
}

public sealed class RestaurantPosCatalogDto
{
  public RestaurantSiteDto Site { get; set; } = new();
  public string MenuName { get; set; } = string.Empty;
  public IReadOnlyList<RestaurantMenuSectionDto> Sections { get; set; } = Array.Empty<RestaurantMenuSectionDto>();
  public IReadOnlyList<RestaurantDiningTableDto> Tables { get; set; } = Array.Empty<RestaurantDiningTableDto>();
  public IReadOnlyList<RestaurantExternalProviderDto> ExternalProviders { get; set; } = Array.Empty<RestaurantExternalProviderDto>();
}

public sealed class RestaurantDiningTableDto
{
  public int Id { get; set; }
  public string Code { get; set; } = string.Empty;
  public string Name { get; set; } = string.Empty;
  public int? Capacity { get; set; }
  public bool IsActive { get; set; }
}

public sealed class RestaurantExternalProviderDto
{
  public int Id { get; set; }
  public string Code { get; set; } = string.Empty;
  public string Name { get; set; } = string.Empty;
  public decimal DefaultCommissionRate { get; set; }
  public bool IsActive { get; set; }
}

public sealed class RestaurantOrderCreateRequest
{
  [Required] public string Rfc { get; set; } = string.Empty;
  [Required] public int SiteId { get; set; }
  public int? CashRegisterId { get; set; }
  public Guid? CashShiftId { get; set; }
  [Required] public string IdempotencyKey { get; set; } = string.Empty;
  [Required] public string OrderType { get; set; } = "Pickup";
  public int? DiningTableId { get; set; }
  [Required, StringLength(150)] public string CustomerName { get; set; } = string.Empty;
  public string? CustomerPhone { get; set; }
  public string? Notes { get; set; }
  public string? DeliveryAddress { get; set; }
  public string? DeliveryReferences { get; set; }
  public int? ExternalProviderId { get; set; }
  public string? ExternalReference { get; set; }
  public decimal DeliveryCost { get; set; }
  public decimal CommissionAmount { get; set; }
  public decimal OrderDiscountAmount { get; set; }
  public string? DiscountReason { get; set; }
  public string? SupervisorAuthorizedBy { get; set; }
  public Guid? MemberId { get; set; }
  [Range(0, 1000000)] public int PointsToRedeem { get; set; }
  [StringLength(32)] public string? PromotionCode { get; set; }
  [Required, StringLength(10)] public string SalesChannel { get; set; } = RestaurantSalesChannels.Pos;
  public bool AllowInventoryDeficit { get; set; }
  [MinLength(1)] public List<RestaurantOrderLineCreateRequest> Lines { get; set; } = [];
  public List<RestaurantPaymentCreateRequest> Payments { get; set; } = [];
}

public sealed class RestaurantOrderLineCreateRequest
{
  public long? ProductId { get; set; }
  public long? MenuSectionId { get; set; }
  public bool IsCustom { get; set; }
  [StringLength(180)] public string? CustomName { get; set; }
  [Range(typeof(decimal), "0.01", "999999999")] public decimal? CustomUnitPrice { get; set; }
  [Range(typeof(decimal), "0.0001", "999999")] public decimal Quantity { get; set; } = 1;
  public decimal DiscountAmount { get; set; }
  [StringLength(500)] public string? Notes { get; set; }
  public List<long> ModifierOptionIds { get; set; } = [];
  public List<RestaurantComboSelectionCreateRequest> ComboSelections { get; set; } = [];
}

public sealed class RestaurantComboSelectionCreateRequest
{
  public long ComboSlotId { get; set; }
  public long ComboSlotOptionId { get; set; }
  public List<long> ModifierOptionIds { get; set; } = [];
  [StringLength(500)] public string? Notes { get; set; }
}

public sealed class RestaurantPaymentCreateRequest
{
  [Required] public string PaymentMethod { get; set; } = "Cash";
  [Range(typeof(decimal), "0", "999999999")] public decimal Amount { get; set; }
  public decimal TipAmount { get; set; }
  [Required] public string IdempotencyKey { get; set; } = string.Empty;
  public string? ExternalReference { get; set; }
}

public sealed class RestaurantPaymentDto
{
  public Guid Id { get; set; }
  public string PaymentMethod { get; set; } = string.Empty;
  public decimal Amount { get; set; }
  public decimal TipAmount { get; set; }
  public decimal RefundedAmount { get; set; }
  public string Status { get; set; } = string.Empty;
  public DateTime PaidAt { get; set; }
  public decimal RefundableAmount => Math.Max(0, Amount - RefundedAmount);
}

public sealed class RestaurantAdditionalPaymentRequest
{
  [Required] public string Rfc { get; set; } = string.Empty;
  public Guid OrderId { get; set; }
  public Guid? CashShiftId { get; set; }
  [Required] public string PaymentMethod { get; set; } = "Cash";
  [Range(typeof(decimal), "0.01", "999999999")] public decimal Amount { get; set; }
  [Range(typeof(decimal), "0", "999999999")] public decimal TipAmount { get; set; }
  [Required, StringLength(100)] public string IdempotencyKey { get; set; } = string.Empty;
  [StringLength(100)] public string? ExternalReference { get; set; }
  [Required, StringLength(500)] public string Reason { get; set; } = string.Empty;
  [Required, StringLength(256)] public string SupervisorUserName { get; set; } = string.Empty;
}

public sealed class RestaurantPaymentRefundRequest
{
  [Required] public string Rfc { get; set; } = string.Empty;
  public Guid PaymentId { get; set; }
  [Range(typeof(decimal), "0.01", "999999999")] public decimal Amount { get; set; }
  [Required, StringLength(100)] public string IdempotencyKey { get; set; } = string.Empty;
  [Required, StringLength(500)] public string Reason { get; set; } = string.Empty;
  [Required, StringLength(256)] public string SupervisorUserName { get; set; } = string.Empty;
}

public sealed class RestaurantQuickPinSetupRequest
{
  [Required] public string Rfc { get; set; } = string.Empty;
  public int SiteId { get; set; }
  public int CashRegisterId { get; set; }
  [Required, RegularExpression("^[0-9]{4,8}$")] public string Pin { get; set; } = string.Empty;
}

public sealed class RestaurantQuickPinVerifyRequest
{
  [Required] public string Rfc { get; set; } = string.Empty;
  public int CashRegisterId { get; set; }
  [Required, StringLength(256)] public string UserNameOrEmail { get; set; } = string.Empty;
  [Required, RegularExpression("^[0-9]{4,8}$")] public string Pin { get; set; } = string.Empty;
}

public sealed class RestaurantQuickPinResult
{
  public bool Success { get; init; }
  public string Message { get; init; } = string.Empty;
  public string? UserId { get; init; }
  public string? UserName { get; init; }
}

public sealed class RestaurantOrderResult
{
  public Guid OrderId { get; set; }
  public int Folio { get; set; }
  public string? CustomerName { get; set; }
  public DateTime OperationalDate { get; set; }
  public string Status { get; set; } = string.Empty;
  public string PaymentStatus { get; set; } = string.Empty;
  public decimal Total { get; set; }
  public decimal BalanceDue { get; set; }
  public decimal PromotionDiscountTotal { get; set; }
  public IReadOnlyList<RestaurantPromotionAdjustmentDto> AppliedPromotions { get; set; } = Array.Empty<RestaurantPromotionAdjustmentDto>();
  public string? MembershipNumber { get; set; }
  public int PointsEarned { get; set; }
  public int PointsRedeemed { get; set; }
  public decimal RedemptionValue { get; set; }
  public int? PointsBalance { get; set; }
  public bool WasDuplicate { get; set; }
}

public sealed class RestaurantOrderDto
{
  public Guid Id { get; set; }
  public int Folio { get; set; }
  public DateTime OperationalDate { get; set; }
  public string OrderType { get; set; } = string.Empty;
  public string Status { get; set; } = string.Empty;
  public string PaymentStatus { get; set; } = string.Empty;
  public string? CustomerName { get; set; }
  public string? TableName { get; set; }
  public string? Notes { get; set; }
  public decimal Total { get; set; }
  public decimal BalanceDue { get; set; }
  public decimal PromotionDiscountTotal { get; set; }
  public Guid? MemberId { get; set; }
  public string? MembershipNumber { get; set; }
  public int PointsEarned { get; set; }
  public int PointsRedeemed { get; set; }
  public decimal RedemptionValue { get; set; }
  public int? CashRegisterId { get; set; }
  public Guid? CashShiftId { get; set; }
  public byte Priority { get; set; }
  public string? PriorityReason { get; set; }
  public string? PrioritizedBy { get; set; }
  public DateTime CreatedAt { get; set; }
  public IReadOnlyList<RestaurantOrderLineDto> Lines { get; set; } = Array.Empty<RestaurantOrderLineDto>();
}

public sealed class RestaurantReceiptDto
{
  public Guid OrderId { get; set; }
  public int SiteId { get; set; }
  public string SiteName { get; set; } = string.Empty;
  public string SiteTimeZoneId { get; set; } = string.Empty;
  public int Folio { get; set; }
  public string OrderType { get; set; } = string.Empty;
  public string Status { get; set; } = string.Empty;
  public string PaymentStatus { get; set; } = string.Empty;
  public string? CustomerName { get; set; }
  public string? TableName { get; set; }
  public string? Notes { get; set; }
  public decimal DiscountTotal { get; set; }
  public decimal TaxTotal { get; set; }
  public decimal TipTotal { get; set; }
  public decimal Total { get; set; }
  public decimal BalanceDue { get; set; }
  public decimal TaxRate { get; set; }
  public bool PricesIncludeTax { get; set; }
  public decimal DeliveryCost { get; set; }
  public string? MembershipNumber { get; set; }
  public int PointsEarned { get; set; }
  public int PointsRedeemed { get; set; }
  public decimal RedemptionValue { get; set; }
  public int? PointsBalance { get; set; }
  public DateTime CreatedAt { get; set; }
  public IReadOnlyList<RestaurantReceiptLineDto> Lines { get; set; } = Array.Empty<RestaurantReceiptLineDto>();
  public IReadOnlyList<RestaurantPaymentDto> Payments { get; set; } = Array.Empty<RestaurantPaymentDto>();
  public IReadOnlyList<RestaurantPromotionAdjustmentDto> Promotions { get; set; } = Array.Empty<RestaurantPromotionAdjustmentDto>();
}

public sealed class RestaurantReceiptLineDto
{
  public long Id { get; set; }
  public long? ProductId { get; set; }
  public long? MenuSectionId { get; set; }
  public string? MenuSectionName { get; set; }
  public int? MenuSectionSortOrder { get; set; }
  public string ProductName { get; set; } = string.Empty;
  public bool IsCustom { get; set; }
  public string LineKind { get; set; } = RestaurantOrderLineKinds.Standard;
  public long? ParentOrderLineId { get; set; }
  public long? ComboSlotId { get; set; }
  public long? ComboSlotOptionId { get; set; }
  public string? ParentProductName { get; set; }
  public string? ComboSlotName { get; set; }
  public decimal Quantity { get; set; }
  public decimal UnitPrice { get; set; }
  public decimal BaseUnitPrice { get; set; }
  public decimal ChoicePriceDelta { get; set; }
  public decimal DiscountAmount { get; set; }
  public string? Notes { get; set; }
  public IReadOnlyList<string> Modifiers { get; set; } = Array.Empty<string>();
  public IReadOnlyList<RestaurantOrderLineModifierDto> StructuredModifiers { get; set; } = Array.Empty<RestaurantOrderLineModifierDto>();
}

public sealed class RestaurantOrderLineDto
{
  public long Id { get; set; }
  public long? ProductId { get; set; }
  public long? MenuSectionId { get; set; }
  public string? MenuSectionName { get; set; }
  public int? MenuSectionSortOrder { get; set; }
  public string ProductName { get; set; } = string.Empty;
  public bool IsCustom { get; set; }
  public string LineKind { get; set; } = RestaurantOrderLineKinds.Standard;
  public long? ParentOrderLineId { get; set; }
  public long? ComboSlotId { get; set; }
  public long? ComboSlotOptionId { get; set; }
  public string? ParentProductName { get; set; }
  public string? ComboSlotName { get; set; }
  public decimal Quantity { get; set; }
  public decimal UnitPrice { get; set; }
  public decimal BaseUnitPrice { get; set; }
  public decimal ChoicePriceDelta { get; set; }
  public string Status { get; set; } = string.Empty;
  public string? Notes { get; set; }
  public int? PreparationMinutes { get; set; }
  public DateTime? StartedAt { get; set; }
  public DateTime? ReadyAt { get; set; }
  public IReadOnlyList<string> Modifiers { get; set; } = Array.Empty<string>();
  public IReadOnlyList<RestaurantOrderLineModifierDto> StructuredModifiers { get; set; } = Array.Empty<RestaurantOrderLineModifierDto>();
}

public sealed class RestaurantOrderLineModifierDto
{
  public long ModifierOptionId { get; set; }
  public string GroupName { get; set; } = string.Empty;
  public string Name { get; set; } = string.Empty;
  public decimal PriceDelta { get; set; }
  public int Quantity { get; set; } = 1;
  public string EffectKind { get; set; } = RestaurantModifierEffectKinds.AdjustQuantity;
}

public sealed class RestaurantOrderEventDto
{
  public long Id { get; set; }
  public string EventType { get; set; } = string.Empty;
  public string Category { get; set; } = string.Empty;
  public string Title { get; set; } = string.Empty;
  public string? Description { get; set; }
  public string? Actor { get; set; }
  public DateTime OccurredAt { get; set; }
}

public sealed class RestaurantKitchenBoardDto
{
  public DateTime ServerTimeUtc { get; set; }
  public IReadOnlyList<RestaurantOrderDto> Orders { get; set; } = Array.Empty<RestaurantOrderDto>();
}

public sealed class RestaurantPublicOrderDto
{
  public Guid Id { get; set; }
  public int Folio { get; set; }
  public string? CustomerName { get; set; }
  public string OrderType { get; set; } = string.Empty;
  public string? TableName { get; set; }
  public string Status { get; set; } = string.Empty;
}

public sealed class RestaurantCashShiftDto
{
  public Guid Id { get; set; }
  public int SiteId { get; set; }
  public int CashRegisterId { get; set; }
  public string RegisterName { get; set; } = string.Empty;
  public string Status { get; set; } = string.Empty;
  public decimal OpeningFloat { get; set; }
  public DateTime OpenedAt { get; set; }
  public string OpenedBy { get; set; } = string.Empty;
  public DateTime? ClosedAt { get; set; }
  public string? ClosedBy { get; set; }
  public decimal GrossSales { get; set; }
  public IReadOnlyList<RestaurantCashShiftPaymentSummaryDto> PaymentMethods { get; set; } = Array.Empty<RestaurantCashShiftPaymentSummaryDto>();
  public decimal? ExpectedCash { get; set; }
  public decimal? CountedCash { get; set; }
  public decimal? Difference { get; set; }
  public DateTime? ApprovedAt { get; set; }
  public string? ApprovedBy { get; set; }
  public DateTime? ReopenedAt { get; set; }
  public string? ReopenedBy { get; set; }
}

public sealed class RestaurantCashShiftLogDto
{
  public RestaurantCashShiftDto Shift { get; set; } = new();
  public int OrderCount { get; set; }
  public int PaymentCount { get; set; }
  public int RefundCount { get; set; }
  public int CancellationCount { get; set; }
  public decimal GrossSales { get; set; }
  public decimal TipTotal { get; set; }
  public decimal RefundTotal { get; set; }
  public decimal CancellationTotal { get; set; }
  public IReadOnlyList<RestaurantCashShiftPaymentSummaryDto> PaymentMethods { get; set; } = Array.Empty<RestaurantCashShiftPaymentSummaryDto>();
  public IReadOnlyList<RestaurantCashShiftLogEntryDto> Entries { get; set; } = Array.Empty<RestaurantCashShiftLogEntryDto>();
}

public sealed class RestaurantCashShiftPaymentSummaryDto
{
  public string PaymentMethod { get; set; } = string.Empty;
  public int PaymentCount { get; set; }
  public int RefundCount { get; set; }
  public int CancellationCount { get; set; }
  public decimal Sales { get; set; }
  public decimal Tips { get; set; }
  public decimal Refunds { get; set; }
  public decimal Cancellations { get; set; }
  public decimal NetTotal => Sales + Tips - Refunds - Cancellations;
}

public sealed class RestaurantCashShiftLogEntryDto
{
  public string Id { get; set; } = string.Empty;
  public DateTime OccurredAt { get; set; }
  public string EventType { get; set; } = string.Empty;
  public string Category { get; set; } = string.Empty;
  public string Title { get; set; } = string.Empty;
  public string? Description { get; set; }
  public string? Actor { get; set; }
  public string? AuthorizedBy { get; set; }
  public decimal? Amount { get; set; }
  public bool IsNegative { get; set; }
  public string? PaymentMethod { get; set; }
  public Guid? OrderId { get; set; }
  public int? OrderFolio { get; set; }
  public string? CustomerName { get; set; }
}

public sealed class RestaurantCashRegisterDto
{
  public int Id { get; set; }
  public string Code { get; set; } = string.Empty;
  public string Name { get; set; } = string.Empty;
  public bool IsActive { get; set; }
}

public sealed class RestaurantCashRegisterUpsertRequest
{
  [Required] public string Rfc { get; set; } = string.Empty;
  public int SiteId { get; set; }
  [Required, StringLength(30)] public string Code { get; set; } = string.Empty;
  [Required, StringLength(100)] public string Name { get; set; } = string.Empty;
}

public sealed class RestaurantCashShiftOpenRequest
{
  [Required] public string Rfc { get; set; } = string.Empty;
  public int SiteId { get; set; }
  public int CashRegisterId { get; set; }
  [Range(0, 999999999)] public decimal OpeningFloat { get; set; }
}

public sealed class RestaurantCashShiftCloseRequest
{
  [Required] public string Rfc { get; set; } = string.Empty;
  public Guid ShiftId { get; set; }
  [Range(0, 999999999)] public decimal CountedCash { get; set; }
}

public sealed class RestaurantCommandResult
{
  public bool Success { get; init; }
  public string Message { get; init; } = string.Empty;
  public long? EntityId { get; init; }

  public static RestaurantCommandResult Ok(string message, long? entityId = null) => new() { Success = true, Message = message, EntityId = entityId };
  public static RestaurantCommandResult Fail(string message) => new() { Success = false, Message = message };
}

public sealed class BomVersionDto
{
  public long Id { get; set; }
  public int ProductMaterialId { get; set; }
  public string ProductName { get; set; } = string.Empty;
  public int VersionNumber { get; set; }
  public string Status { get; set; } = string.Empty;
  public decimal YieldQuantity { get; set; }
  public int YieldUnitId { get; set; }
  public decimal ExpectedWastePercent { get; set; }
  public decimal TheoreticalCost { get; set; }
  public string? SafetyNotes { get; set; }
  public DateTime CreatedAt { get; set; }
  public string? CreatedBy { get; set; }
  public DateTime? EffectiveFrom { get; set; }
  public DateTime? RetiredAt { get; set; }
  public int ComponentCount { get; set; }
  public int StepCount { get; set; }
  public IReadOnlyList<string> Allergens { get; set; } = Array.Empty<string>();
  public IReadOnlyList<BomComponentDto> Components { get; set; } = Array.Empty<BomComponentDto>();
  public IReadOnlyList<RecipeStepDto> Steps { get; set; } = Array.Empty<RecipeStepDto>();
}

public sealed class BomCostBreakdownDto
{
  public long BomVersionId { get; set; }
  public decimal YieldQuantity { get; set; }
  public int YieldUnitId { get; set; }
  public string YieldUnitName { get; set; } = string.Empty;
  public decimal StoredUnitCost { get; set; }
  public decimal CurrentBatchCost { get; set; }
  public decimal CurrentUnitCost { get; set; }
  public IReadOnlyList<BomCostLineDto> Lines { get; set; } = Array.Empty<BomCostLineDto>();
}

/// <summary>Receta activa que consume un material, para poder medir el impacto de cambiarlo.</summary>
public sealed class RecipeUsageDto
{
  public int ProductMaterialId { get; set; }
  public string ProductName { get; set; } = string.Empty;
  public int VersionNumber { get; set; }
  public decimal Quantity { get; set; }
  public string UnitName { get; set; } = string.Empty;
}

public sealed class BomCostLineDto
{
  public int MaterialId { get; set; }
  public string MaterialName { get; set; } = string.Empty;
  public decimal RecipeQuantity { get; set; }
  public string RecipeUnitName { get; set; } = string.Empty;
  public decimal WastePercent { get; set; }
  public decimal ConversionFactor { get; set; }
  public decimal BaseQuantity { get; set; }
  public decimal QuantityWithWaste { get; set; }
  public string BaseUnitName { get; set; } = string.Empty;
  public decimal UnitCost { get; set; }
  public string CostSource { get; set; } = string.Empty;
  /// <summary>El material tiene receta activa pero está clasificado como comprado, así que su receta no cuenta.</summary>
  public bool RecipeCostIgnored { get; set; }
  public decimal BatchCost { get; set; }
  public decimal UnitContribution { get; set; }
}

public sealed class BomComponentDto
{
  public long Id { get; set; }
  public int MaterialId { get; set; }
  public string MaterialName { get; set; } = string.Empty;
  public decimal Quantity { get; set; }
  public int UnitId { get; set; }
  public string UnitName { get; set; } = string.Empty;
  public decimal ExpectedWastePercent { get; set; }
}

public sealed class RecipeStepDto
{
  public long Id { get; set; }
  public int StepNumber { get; set; }
  public string Instruction { get; set; } = string.Empty;
  public int? DurationMinutes { get; set; }
  public decimal? TemperatureC { get; set; }
  public string? Equipment { get; set; }
  public byte[]? Image { get; set; }
  public string? ImageFileName { get; set; }
  public string? ImageContentType { get; set; }
}

public sealed class BomDraftSaveRequest
{
  [Required] public string Rfc { get; set; } = string.Empty;
  public long? BomVersionId { get; set; }
  public int ProductMaterialId { get; set; }
  public decimal YieldQuantity { get; set; } = 1;
  public int YieldUnitId { get; set; }
  public decimal ExpectedWastePercent { get; set; }
  [StringLength(2000)] public string? SafetyNotes { get; set; }
  [MinLength(1)] public List<BomComponentSaveRequest> Components { get; set; } = [];
  public List<RecipeStepSaveRequest> Steps { get; set; } = [];
}

public sealed class BomComponentSaveRequest
{
  public int MaterialId { get; set; }
  public decimal Quantity { get; set; }
  public int UnitId { get; set; }
  public decimal ExpectedWastePercent { get; set; }
}

public sealed class RecipeStepSaveRequest
{
  public int StepNumber { get; set; }
  public string Instruction { get; set; } = string.Empty;
  public int? DurationMinutes { get; set; }
  public decimal? TemperatureC { get; set; }
  public string? Equipment { get; set; }
  public byte[]? Image { get; set; }
  [StringLength(200)] public string? ImageFileName { get; set; }
  [StringLength(100)] public string? ImageContentType { get; set; }
}

public sealed class RestaurantAllergenDto
{
  public int Id { get; set; }
  public string Code { get; set; } = string.Empty;
  public string Name { get; set; } = string.Empty;
  public bool IsActive { get; set; }
  public IReadOnlyList<int> MaterialIds { get; set; } = Array.Empty<int>();
}

public sealed class RestaurantAllergenSaveRequest
{
  public int? Id { get; set; }
  [Required, StringLength(40)] public string Code { get; set; } = string.Empty;
  [Required, StringLength(100)] public string Name { get; set; } = string.Empty;
  public bool IsActive { get; set; } = true;
}

public sealed class MaterialUnitConversionDto
{
  public int Id { get; set; }
  public int MaterialId { get; set; }
  public string MaterialName { get; set; } = string.Empty;
  public int FromUnitId { get; set; }
  public string FromUnitCode { get; set; } = string.Empty;
  public int ToUnitId { get; set; }
  public string ToUnitCode { get; set; } = string.Empty;
  public decimal Factor { get; set; }
  public string? Notes { get; set; }
  public bool IsActive { get; set; }
}

public sealed class MaterialUnitConversionSaveRequest
{
  [Required] public string Rfc { get; set; } = string.Empty;
  public int? Id { get; set; }
  public int MaterialId { get; set; }
  public int FromUnitId { get; set; }
  public int ToUnitId { get; set; }
  [Range(typeof(decimal), "0.0000000001", "99999999999999")] public decimal Factor { get; set; } = 1;
  [StringLength(500)] public string? Notes { get; set; }
  public bool IsActive { get; set; } = true;
}

public sealed class RecipeUnitOptionDto
{
  public int MaterialId { get; set; }
  public int UnitId { get; set; }
  public string UnitCode { get; set; } = string.Empty;
  public string UnitName { get; set; } = string.Empty;
  public decimal FactorToBase { get; set; }
  public bool IsBase { get; set; }
}

public sealed class RecipeValidationIssueDto
{
  public string Section { get; set; } = string.Empty;
  public string Code { get; set; } = string.Empty;
  public string Message { get; set; } = string.Empty;
}

public sealed class RecipeActivationReadinessDto
{
  public bool CanActivate => Issues.Count == 0;
  public int? ReplacesVersionNumber { get; set; }
  public IReadOnlyList<RecipeValidationIssueDto> Issues { get; set; } = Array.Empty<RecipeValidationIssueDto>();
  public IReadOnlyList<string> Warnings { get; set; } = Array.Empty<string>();
}

public sealed class RestaurantProductionOrderDto
{
  public Guid Id { get; set; }
  public string ProductionCode { get; set; } = string.Empty;
  public int SiteId { get; set; }
  public string SiteName { get; set; } = string.Empty;
  public int ProductMaterialId { get; set; }
  public string ProductName { get; set; } = string.Empty;
  public long BomVersionId { get; set; }
  public int BomVersionNumber { get; set; }
  public decimal PlannedQuantity { get; set; }
  public decimal? ActualQuantity { get; set; }
  public int UnitId { get; set; }
  public string UnitName { get; set; } = string.Empty;
  public int OutputLocationId { get; set; }
  public string OutputLocationName { get; set; } = string.Empty;
  public string? OutputLotCode { get; set; }
  public DateTime? OutputExpiresAt { get; set; }
  public string Status { get; set; } = string.Empty;
  public decimal FrozenTheoreticalCost { get; set; }
  public decimal? WasteQuantity { get; set; }
  public DateTime PlannedAt { get; set; }
  public DateTime? StartedAt { get; set; }
  public DateTime? CompletedAt { get; set; }
}

public sealed class RestaurantProductionPlanRequest
{
  [Required] public string Rfc { get; set; } = string.Empty;
  public int SiteId { get; set; }
  public long BomVersionId { get; set; }
  [Range(typeof(decimal), "0.0001", "999999999")] public decimal PlannedQuantity { get; set; } = 1;
  public int OutputLocationId { get; set; }
  [Required, StringLength(100)] public string IdempotencyKey { get; set; } = string.Empty;
}

public sealed class RestaurantProductionCompleteRequest
{
  [Required] public string Rfc { get; set; } = string.Empty;
  public Guid ProductionOrderId { get; set; }
  [Range(typeof(decimal), "0.0001", "999999999")] public decimal ActualQuantity { get; set; }
  [Range(typeof(decimal), "0", "999999999")] public decimal WasteQuantity { get; set; }
  [Required, StringLength(80)] public string OutputLotCode { get; set; } = string.Empty;
  public DateTime? ExpiresAt { get; set; }
}

public sealed class RestaurantLookupDto
{
  public long Id { get; set; }
  public string Label { get; set; } = string.Empty;
}

public sealed class RestaurantProductionWorkspaceDto
{
  public IReadOnlyList<RestaurantProductionOrderDto> Orders { get; set; } = Array.Empty<RestaurantProductionOrderDto>();
  public IReadOnlyList<RestaurantLookupDto> ActiveBoms { get; set; } = Array.Empty<RestaurantLookupDto>();
  public IReadOnlyList<RestaurantLookupDto> OutputLocations { get; set; } = Array.Empty<RestaurantLookupDto>();
  /// <summary>Materiales con receta activa que no se pueden producir por su clasificación.</summary>
  public IReadOnlyList<RestaurantLookupDto> UnproducibleWithRecipe { get; set; } = Array.Empty<RestaurantLookupDto>();
}

public sealed class RestaurantMenuAdminDto
{
  public long Id { get; set; }
  public string MenuCode { get; set; } = string.Empty;
  public string Name { get; set; } = string.Empty;
  public bool IsPublished { get; set; }
  public bool IsActive { get; set; }
  public IReadOnlyList<RestaurantMenuScheduleAdminDto> Schedules { get; set; } = Array.Empty<RestaurantMenuScheduleAdminDto>();
  public IReadOnlyList<RestaurantMenuSectionAdminDto> Sections { get; set; } = Array.Empty<RestaurantMenuSectionAdminDto>();
}

public sealed class RestaurantMenuScheduleAdminDto
{
  public int SiteId { get; set; }
  public byte DayOfWeek { get; set; }
  public TimeSpan StartsAt { get; set; }
  public TimeSpan EndsAt { get; set; }
}

public sealed class RestaurantMenuSectionAdminDto
{
  public long Id { get; set; }
  public string Name { get; set; } = string.Empty;
  public int SortOrder { get; set; }
  public IReadOnlyList<long> ProductIds { get; set; } = Array.Empty<long>();
}

public sealed class RestaurantMenuSaveRequest
{
  [Required] public string Rfc { get; set; } = string.Empty;
  public long? Id { get; set; }
  [Required, StringLength(40)] public string MenuCode { get; set; } = string.Empty;
  [Required, StringLength(120)] public string Name { get; set; } = string.Empty;
  public bool IsPublished { get; set; }
  public bool IsActive { get; set; } = true;
  public List<RestaurantMenuScheduleSaveRequest> Schedules { get; set; } = [];
  [MinLength(1)] public List<RestaurantMenuSectionSaveRequest> Sections { get; set; } = [];
}

public sealed class RestaurantMenuScheduleSaveRequest
{
  public int SiteId { get; set; }
  [Range(0, 6)] public byte DayOfWeek { get; set; }
  public TimeSpan StartsAt { get; set; } = new(6, 0, 0);
  public TimeSpan EndsAt { get; set; } = new(23, 0, 0);
}

public sealed class RestaurantMenuSectionSaveRequest
{
  [Required, StringLength(100)] public string Name { get; set; } = string.Empty;
  public int SortOrder { get; set; }
  public List<long> ProductIds { get; set; } = [];
}

public sealed class RestaurantComboAdminDto
{
  public long ProductId { get; set; }
  public string ProductName { get; set; } = string.Empty;
  public string Sku { get; set; } = string.Empty;
  public bool IsActive { get; set; }
  public IReadOnlyList<RestaurantComboSlotDto> Slots { get; set; } = Array.Empty<RestaurantComboSlotDto>();
}

public sealed class RestaurantComboSaveRequest
{
  [Required] public string Rfc { get; set; } = string.Empty;
  public long ProductId { get; set; }
  [MinLength(1)] public List<RestaurantComboSlotSaveRequest> Slots { get; set; } = [];
}

public sealed class RestaurantComboSlotSaveRequest
{
  public long? Id { get; set; }
  [Required, StringLength(120)] public string Name { get; set; } = string.Empty;
  [Range(0, 50)] public int MinSelections { get; set; }
  [Range(1, 50)] public int MaxSelections { get; set; } = 1;
  public int SortOrder { get; set; }
  public bool IsActive { get; set; } = true;
  [MinLength(1)] public List<RestaurantComboSlotOptionSaveRequest> Options { get; set; } = [];
}

public sealed class RestaurantComboSlotOptionSaveRequest
{
  public long? Id { get; set; }
  public long ComponentProductId { get; set; }
  [Range(typeof(decimal), "0.0001", "999999")] public decimal Quantity { get; set; } = 1;
  [Range(typeof(decimal), "0", "999999999")] public decimal PriceDelta { get; set; }
  public bool IsDefault { get; set; }
  public int SortOrder { get; set; }
  public bool IsActive { get; set; } = true;
  public List<RestaurantComboOptionRouteSaveRequest> Routes { get; set; } = [];
}

public sealed class RestaurantComboOptionRouteSaveRequest
{
  public long MenuId { get; set; }
  public long MenuSectionId { get; set; }
}

public sealed class RestaurantModifierAdminDto
{
  public long Id { get; set; }
  public string Name { get; set; } = string.Empty;
  public int MinSelections { get; set; }
  public int MaxSelections { get; set; }
  public int SortOrder { get; set; }
  public bool IsActive { get; set; }
  public IReadOnlyList<long> ProductIds { get; set; } = Array.Empty<long>();
  public IReadOnlyList<RestaurantModifierOptionAdminDto> Options { get; set; } = Array.Empty<RestaurantModifierOptionAdminDto>();
}

public sealed class RestaurantModifierOptionAdminDto
{
  public long Id { get; set; }
  public string Name { get; set; } = string.Empty;
  public decimal PriceDelta { get; set; }
  public int SortOrder { get; set; }
  public IReadOnlyList<RestaurantModifierDeltaAdminDto> IngredientDeltas { get; set; } = Array.Empty<RestaurantModifierDeltaAdminDto>();
}

public sealed class RestaurantModifierDeltaAdminDto
{
  public int MaterialId { get; set; }
  public string MaterialName { get; set; } = string.Empty;
  public string EffectKind { get; set; } = RestaurantModifierEffectKinds.AdjustQuantity;
  public decimal QuantityDelta { get; set; }
  public int? UnitId { get; set; }
  public string? UnitName { get; set; }
}

public sealed class RestaurantModifierSaveRequest
{
  [Required] public string Rfc { get; set; } = string.Empty;
  public long? Id { get; set; }
  [Required, StringLength(120)] public string Name { get; set; } = string.Empty;
  public int MinSelections { get; set; }
  public int MaxSelections { get; set; } = 1;
  public int SortOrder { get; set; }
  public bool IsActive { get; set; } = true;
  public List<long> ProductIds { get; set; } = [];
  [MinLength(1)] public List<RestaurantModifierOptionSaveRequest> Options { get; set; } = [];
}

public sealed class RestaurantModifierOptionSaveRequest
{
  public long? Id { get; set; }
  [Required, StringLength(120)] public string Name { get; set; } = string.Empty;
  public decimal PriceDelta { get; set; }
  public int SortOrder { get; set; }
  public List<RestaurantModifierDeltaSaveRequest> IngredientDeltas { get; set; } = [];
}

public sealed class RestaurantModifierDeltaSaveRequest
{
  public int MaterialId { get; set; }
  [Required, StringLength(20)] public string EffectKind { get; set; } = RestaurantModifierEffectKinds.AdjustQuantity;
  public decimal QuantityDelta { get; set; }
  public int? UnitId { get; set; }
}

public sealed class RestaurantKitchenStationAdminDto
{
  public int Id { get; set; }
  public string Code { get; set; } = string.Empty;
  public string Name { get; set; } = string.Empty;
  public int SortOrder { get; set; }
  public bool IsActive { get; set; }
}

public sealed class RestaurantKitchenStationLookupDto
{
  public int Id { get; set; }
  public int SiteId { get; set; }
  public string SiteName { get; set; } = string.Empty;
  public string Code { get; set; } = string.Empty;
  public string Name { get; set; } = string.Empty;
  public int SortOrder { get; set; }
  public bool IsActive { get; set; }
}

public sealed class RestaurantLocationPriorityDto
{
  public int LocationId { get; set; }
  public string LocationName { get; set; } = string.Empty;
  public string StationCode { get; set; } = "GENERAL";
  public int Priority { get; set; }
}

public sealed class RestaurantAccountingConfigurationDto
{
  public string? CashAccount { get; set; }
  public string? CardBankAccount { get; set; }
  public string? TransferBankAccount { get; set; }
  public string? PlatformReceivableAccount { get; set; }
  public string? SalesAccount { get; set; }
  public string? VatAccount { get; set; }
  public string? DiscountAccount { get; set; }
  public string? TipsPayableAccount { get; set; }
  public string? PlatformCommissionAccount { get; set; }
  public string? InventoryAccount { get; set; }
  public string? CostOfSalesAccount { get; set; }
  public string? WasteAccount { get; set; }
  public bool DailyPolicyEnabled { get; set; }
}

public sealed class RestaurantSiteOperationsDto
{
  public IReadOnlyList<RestaurantDiningTableDto> Tables { get; set; } = Array.Empty<RestaurantDiningTableDto>();
  public IReadOnlyList<RestaurantKitchenStationAdminDto> Stations { get; set; } = Array.Empty<RestaurantKitchenStationAdminDto>();
  public IReadOnlyList<RestaurantExternalProviderDto> ExternalProviders { get; set; } = Array.Empty<RestaurantExternalProviderDto>();
  public IReadOnlyList<RestaurantLocationPriorityDto> LocationPriorities { get; set; } = Array.Empty<RestaurantLocationPriorityDto>();
  public IReadOnlyList<RestaurantLookupDto> AvailableLocations { get; set; } = Array.Empty<RestaurantLookupDto>();
  public RestaurantAccountingConfigurationDto Accounting { get; set; } = new();
}

public sealed class RestaurantSiteOperationsSaveRequest
{
  [Required] public string Rfc { get; set; } = string.Empty;
  public int SiteId { get; set; }
  public TimeSpan OperationalDayCutoff { get; set; } = new(4, 0, 0);
  public List<RestaurantDiningTableSaveRequest> Tables { get; set; } = [];
  public List<RestaurantKitchenStationSaveRequest> Stations { get; set; } = [];
  public List<RestaurantExternalProviderSaveRequest> ExternalProviders { get; set; } = [];
  public List<RestaurantLocationPrioritySaveRequest> LocationPriorities { get; set; } = [];
  public RestaurantAccountingConfigurationDto Accounting { get; set; } = new();
}

public sealed class RestaurantDiningTableSaveRequest
{
  public int? Id { get; set; }
  [Required, StringLength(20)] public string Code { get; set; } = string.Empty;
  [Required, StringLength(80)] public string Name { get; set; } = string.Empty;
  public int? Capacity { get; set; }
  public bool IsActive { get; set; } = true;
}

public sealed class RestaurantKitchenStationSaveRequest
{
  public int? Id { get; set; }
  [Required, StringLength(30)] public string Code { get; set; } = string.Empty;
  [Required, StringLength(100)] public string Name { get; set; } = string.Empty;
  public int SortOrder { get; set; }
  public bool IsActive { get; set; } = true;
}

public sealed class RestaurantExternalProviderSaveRequest
{
  public int? Id { get; set; }
  [Required, StringLength(30)] public string Code { get; set; } = string.Empty;
  [Required, StringLength(120)] public string Name { get; set; } = string.Empty;
  [Range(0, 1)] public decimal DefaultCommissionRate { get; set; }
  public bool IsActive { get; set; } = true;
}

public sealed class RestaurantLocationPrioritySaveRequest
{
  public int LocationId { get; set; }
  [Required, StringLength(30)] public string StationCode { get; set; } = "GENERAL";
  public int Priority { get; set; }
}

public sealed class RestaurantReportDto
{
  public int OrderCount { get; set; }
  public decimal NetSales { get; set; }
  public decimal TaxTotal { get; set; }
  public decimal DiscountTotal { get; set; }
  public decimal TipTotal { get; set; }
  public decimal TheoreticalCost { get; set; }
  public decimal PendingSettlement { get; set; }
  public decimal AverageTicket { get; set; }
  public IReadOnlyList<RestaurantReportBreakdownDto> PaymentMethods { get; set; } = Array.Empty<RestaurantReportBreakdownDto>();
  public IReadOnlyList<RestaurantTopProductDto> TopProducts { get; set; } = Array.Empty<RestaurantTopProductDto>();
  public IReadOnlyList<RestaurantDailySalesDto> DailySales { get; set; } = Array.Empty<RestaurantDailySalesDto>();
}

public sealed class RestaurantReportBreakdownDto
{
  public string Label { get; set; } = string.Empty;
  public decimal Amount { get; set; }
}

public sealed class RestaurantTopProductDto
{
  public string ProductName { get; set; } = string.Empty;
  public decimal Quantity { get; set; }
  public decimal Sales { get; set; }
}

public sealed class RestaurantDailySalesDto
{
  public DateTime OperationalDate { get; set; }
  public int OrderCount { get; set; }
  public decimal Sales { get; set; }
  public decimal Cost { get; set; }
}

public sealed class RestaurantSettlementCandidateDto
{
  public Guid OrderId { get; set; }
  public int Folio { get; set; }
  public DateTime OperationalDate { get; set; }
  public int ExternalProviderId { get; set; }
  public string ProviderName { get; set; } = string.Empty;
  public string? ExternalReference { get; set; }
  public decimal GrossAmount { get; set; }
  public decimal CommissionAmount { get; set; }
  public decimal NetAmount { get; set; }
  public DateTime? DeliveredAt { get; set; }
}

public sealed class RestaurantProviderSettlementDto
{
  public Guid Id { get; set; }
  public string SettlementCode { get; set; } = string.Empty;
  public string ProviderName { get; set; } = string.Empty;
  public string Status { get; set; } = string.Empty;
  public decimal GrossAmount { get; set; }
  public decimal CommissionAmount { get; set; }
  public decimal NetAmount { get; set; }
  public DateTime? SettledAt { get; set; }
  public DateTime CreatedAt { get; set; }
  public int OrderCount { get; set; }
}

public sealed class RestaurantSettlementCreateRequest
{
  [Required] public string Rfc { get; set; } = string.Empty;
  public int SiteId { get; set; }
  [Required, StringLength(40)] public string SettlementCode { get; set; } = string.Empty;
  [MinLength(1)] public List<Guid> OrderIds { get; set; } = [];
}

public sealed class RestaurantAccountingPreviewDto
{
  public DateTime OperationalDate { get; set; }
  public int EligibleOrderCount { get; set; }
  public decimal Sales { get; set; }
  public decimal Tax { get; set; }
  public decimal Discounts { get; set; }
  public decimal Cost { get; set; }
  public bool ConfigurationComplete { get; set; }
  public int? ExistingTransactionId { get; set; }
  public IReadOnlyList<RestaurantReportBreakdownDto> Receipts { get; set; } = Array.Empty<RestaurantReportBreakdownDto>();
}
