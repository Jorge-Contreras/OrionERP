namespace OrionERP.Application.Features.Restaurante;

public static class RestaurantSaleReadinessStatuses
{
  public const string Ready = "LISTO";
  public const string Warning = "ADVERTENCIA";
  public const string SupervisorRequired = "REQUIERE SUPERVISOR";
  public const string InventoryBlocked = "BLOQUEADO - INVENTARIO";
  public const string ConfigurationBlocked = "BLOQUEADO - CONFIGURACIÓN";
  public const string SoldOut = "AGOTADO";
}

public static class RestaurantSaleReadinessSeverities
{
  public const string Ready = "LISTO";
  public const string Information = "INFORMACIÓN";
  public const string Warning = "ADVERTENCIA";
  public const string Error = "ERROR";
}

public sealed class RestaurantSaleReadinessReport
{
  public string Rfc { get; set; } = string.Empty;
  public int SiteId { get; set; }
  public string SiteCode { get; set; } = string.Empty;
  public string SiteName { get; set; } = string.Empty;
  public string SiteTimeZoneId { get; set; } = string.Empty;
  public string MenuName { get; set; } = string.Empty;
  public bool UsesFallbackCatalog { get; set; }
  public bool AllowSupervisorDeficit { get; set; }
  public DateTimeOffset GeneratedAtUtc { get; set; }
  public DateTimeOffset GeneratedAtLocal { get; set; }
  public decimal SimulatedQuantity { get; set; } = 1m;
  public IReadOnlyList<RestaurantSaleReadinessProduct> Products { get; set; } = [];
  public IReadOnlyList<RestaurantSaleReadinessIngredient> Ingredients { get; set; } = [];
  public IReadOnlyList<RestaurantSaleReadinessBomRow> BomRows { get; set; } = [];
  public IReadOnlyList<RestaurantSaleReadinessModifierRow> Modifiers { get; set; } = [];
  public IReadOnlyList<RestaurantSaleReadinessEnvironmentCheck> EnvironmentChecks { get; set; } = [];
  public IReadOnlyList<RestaurantSaleReadinessAction> Actions { get; set; } = [];
}

public sealed class RestaurantSaleReadinessProduct
{
  public long ProductId { get; set; }
  public string Sku { get; set; } = string.Empty;
  public string ProductName { get; set; } = string.Empty;
  public string Sections { get; set; } = string.Empty;
  public decimal Price { get; set; }
  public int MaterialId { get; set; }
  public string MaterialCode { get; set; } = string.Empty;
  public string MaterialName { get; set; } = string.Empty;
  public string FulfillmentMode { get; set; } = string.Empty;
  public string? KitchenStationName { get; set; }
  public bool IsActive { get; set; }
  public bool IsSoldOut { get; set; }
  public string Status { get; set; } = RestaurantSaleReadinessStatuses.Ready;
  public bool CanSellWithoutOverride { get; set; }
  public bool RequiresSupervisor { get; set; }
  public decimal? EstimatedSellableUnits { get; set; }
  public int LeafIngredientCount { get; set; }
  public int ErrorCount { get; set; }
  public int WarningCount { get; set; }
  public string? BottleneckMaterial { get; set; }
  public string? PredictedPosMessage { get; set; }
  public string SuggestedAction { get; set; } = string.Empty;
}

public sealed class RestaurantSaleReadinessIngredient
{
  public long ProductId { get; set; }
  public string ProductSku { get; set; } = string.Empty;
  public string ProductName { get; set; } = string.Empty;
  public int MaterialId { get; set; }
  public string MaterialCode { get; set; } = string.Empty;
  public string MaterialName { get; set; } = string.Empty;
  public string BaseUnit { get; set; } = string.Empty;
  public string BomPath { get; set; } = string.Empty;
  public int BomDepth { get; set; }
  public string FulfillmentMode { get; set; } = string.Empty;
  public bool TrackLots { get; set; }
  public decimal RequiredQuantity { get; set; }
  public decimal StockQuantity { get; set; }
  public decimal ReservedQuantity { get; set; }
  public decimal UsableQuantity { get; set; }
  public decimal ExcludedLotQuantity { get; set; }
  public decimal ProjectedUsableQuantity { get; set; }
  public decimal MinimumQuantity { get; set; }
  public decimal ShortageQuantity { get; set; }
  public decimal? EstimatedSellableUnits { get; set; }
  public string LocationSummary { get; set; } = string.Empty;
  public string Status { get; set; } = RestaurantSaleReadinessStatuses.Ready;
  public string? PredictedPosMessage { get; set; }
}

public sealed class RestaurantSaleReadinessBomRow
{
  public long ProductId { get; set; }
  public string ProductSku { get; set; } = string.Empty;
  public string ProductName { get; set; } = string.Empty;
  public int Depth { get; set; }
  public string Path { get; set; } = string.Empty;
  public int ParentMaterialId { get; set; }
  public string ParentMaterialCode { get; set; } = string.Empty;
  public string ParentMaterialName { get; set; } = string.Empty;
  public long? BomVersionId { get; set; }
  public int? BomVersionNumber { get; set; }
  public decimal? YieldQuantity { get; set; }
  public string YieldUnit { get; set; } = string.Empty;
  public int? ComponentMaterialId { get; set; }
  public string ComponentMaterialCode { get; set; } = string.Empty;
  public string ComponentMaterialName { get; set; } = string.Empty;
  public string ComponentFulfillmentMode { get; set; } = string.Empty;
  public decimal? ComponentQuantity { get; set; }
  public string ComponentUnit { get; set; } = string.Empty;
  public decimal? ExpectedWastePercent { get; set; }
  public decimal? ConversionFactor { get; set; }
  public decimal? RequiredBaseQuantity { get; set; }
  public string Status { get; set; } = RestaurantSaleReadinessStatuses.Ready;
  public string? Message { get; set; }
}

public sealed class RestaurantSaleReadinessModifierRow
{
  public long ProductId { get; set; }
  public string ProductSku { get; set; } = string.Empty;
  public string ProductName { get; set; } = string.Empty;
  public long GroupId { get; set; }
  public string GroupName { get; set; } = string.Empty;
  public int MinSelections { get; set; }
  public int MaxSelections { get; set; }
  public long? OptionId { get; set; }
  public string OptionName { get; set; } = string.Empty;
  public decimal PriceDelta { get; set; }
  public int? MaterialId { get; set; }
  public string MaterialCode { get; set; } = string.Empty;
  public string MaterialName { get; set; } = string.Empty;
  public decimal? QuantityDelta { get; set; }
  public string DeltaUnit { get; set; } = string.Empty;
  public decimal? ConversionFactor { get; set; }
  public decimal? BaseQuantityImpact { get; set; }
  public decimal? AvailableAfterBaseProduct { get; set; }
  public string Status { get; set; } = RestaurantSaleReadinessStatuses.Ready;
  public string? Message { get; set; }
}

public sealed class RestaurantSaleReadinessEnvironmentCheck
{
  public string Area { get; set; } = string.Empty;
  public string Check { get; set; } = string.Empty;
  public string Status { get; set; } = RestaurantSaleReadinessStatuses.Ready;
  public string Detail { get; set; } = string.Empty;
  public string RecommendedAction { get; set; } = string.Empty;
}

public sealed class RestaurantSaleReadinessAction
{
  public string Severity { get; set; } = RestaurantSaleReadinessSeverities.Information;
  public string ProductSku { get; set; } = string.Empty;
  public string ProductName { get; set; } = string.Empty;
  public int? MaterialId { get; set; }
  public string Material { get; set; } = string.Empty;
  public string Issue { get; set; } = string.Empty;
  public decimal? ShortageQuantity { get; set; }
  public string RecommendedAction { get; set; } = string.Empty;
}

public sealed class RestaurantSaleReadinessWorkbook
{
  public string FileName { get; set; } = string.Empty;
  public string ContentType { get; set; } = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
  public byte[] Bytes { get; set; } = [];
}

