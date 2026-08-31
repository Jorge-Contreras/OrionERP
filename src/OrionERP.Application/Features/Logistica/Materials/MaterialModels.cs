using System.ComponentModel.DataAnnotations;
using OrionERP.Application.Features.Logistica.Shared;

namespace OrionERP.Application.Features.Logistica.Materials;

public sealed class MaterialFilter
{
  [Required]
  public string Rfc { get; set; } = string.Empty;
  public string? SearchText { get; set; }
  public int? CategoryId { get; set; }

  /// <summary>Deja únicamente los materiales que ese proveedor surte.</summary>
  public int? VendorId { get; set; }

  /// <summary>
  /// No filtra: marca cuáles de los materiales devueltos ya surte ese proveedor. Compras lo usa
  /// para buscar en todo el catálogo sin perder de vista con quién se compra normalmente cada cosa.
  /// </summary>
  public int? HighlightVendorId { get; set; }

  public string? MaterialClass { get; set; }
  public string? Status { get; set; }
  public bool IncludeInactive { get; set; }
  public bool? HasImage { get; set; }
  public bool? HasStock { get; set; }
  public bool NeedsAttention { get; set; }
  public int Skip { get; set; }
  public int Take { get; set; }
}

public sealed class MaterialListItemDto
{
  public int Id { get; set; }
  public string MaterialCode { get; set; } = string.Empty;
  public string Description { get; set; } = string.Empty;
  public int BaseUnitId { get; set; }
  public string MaterialClass { get; set; } = string.Empty;
  public string ProductType { get; set; } = string.Empty;
  public string FulfillmentMode { get; set; } = string.Empty;
  public string Status { get; set; } = string.Empty;
  public bool IsActive { get; set; }
  public string? CategoryName { get; set; }
  public string? BaseUnitName { get; set; }

  /// <summary>Proveedor principal del material.</summary>
  public string? VendorName { get; set; }

  /// <summary>Cuántos proveedores lo surten, contando al principal.</summary>
  public int VendorCount { get; set; }

  /// <summary>
  /// Verdadero cuando el material ya está vinculado al proveedor de
  /// <see cref="MaterialFilter.HighlightVendorId"/>. Sin ese filtro siempre es falso.
  /// </summary>
  public bool IsHighlightedVendorMaterial { get; set; }

  public decimal? BaseUnitPrice { get; set; }
  public bool HasImage { get; set; }
  public string? Barcode { get; set; }
  public decimal TotalQuantity { get; set; }
  public int LocationCount { get; set; }
}

public sealed class MaterialDetailDto
{
  public int Id { get; set; }
  public string MaterialCode { get; set; } = string.Empty;
  public int? LegacyMaterialId { get; set; }
  public string Description { get; set; } = string.Empty;
  public int BaseUnitId { get; set; }
  public string? BaseUnitName { get; set; }
  public decimal PurchaseQuantity { get; set; }
  public int? PurchaseUnitId { get; set; }
  public string? PurchaseUnitName { get; set; }
  public decimal? BaseUnitPrice { get; set; }
  public DateTime? CreatedDate { get; set; }
  public DateTime? UpdatedDate { get; set; }
  public string? Brand { get; set; }
  public string? Model { get; set; }
  public bool IsPerishable { get; set; }
  public int? ShelfLifeDays { get; set; }
  public bool RequiresRefrigeration { get; set; }
  public string Status { get; set; } = string.Empty;
  public int? CategoryId { get; set; }
  public string? Barcode { get; set; }
  public string? VendorCode { get; set; }
  public string? PurchaseLink { get; set; }
  public string MaterialClass { get; set; } = string.Empty;
  public string ProductType { get; set; } = string.Empty;
  public string FulfillmentMode { get; set; } = string.Empty;
  public bool IsActive { get; set; }
  public bool HasImage { get; set; }
  public string? PrimaryImageFileName { get; set; }
  public string? PrimaryImageContentType { get; set; }

  /// <summary>Proveedores que surten el material, el principal primero.</summary>
  public IReadOnlyList<MaterialVendorLinkDto> Vendors { get; set; } = Array.Empty<MaterialVendorLinkDto>();

  /// <summary>Rol de producción derivado del par almacenado.</summary>
  public string ProductionRole => MaterialProductionRoles.Resolve(ProductType, FulfillmentMode);

  public MaterialVendorLinkDto? PrimaryVendor => Vendors.FirstOrDefault(vendor => vendor.IsPrimary);
  public int? PrimaryVendorId => PrimaryVendor?.BusinessPartnerId;
}

/// <summary>
/// Vínculo entre un material y uno de sus proveedores. Guarda lo que es propio de ese proveedor
/// —su SKU, su presentación, su liga y el último costo que se le pagó— para que comprarle a un
/// segundo proveedor no borre los datos del habitual.
/// </summary>
public sealed class MaterialVendorLinkDto
{
  public int Id { get; set; }
  public int BusinessPartnerId { get; set; }
  public string VendorName { get; set; } = string.Empty;
  public string? VendorRfc { get; set; }
  public bool IsPrimary { get; set; }
  public bool IsActive { get; set; }
  public string? VendorCode { get; set; }
  public decimal? PurchaseQuantity { get; set; }
  public int? PurchaseUnitId { get; set; }
  public string? PurchaseUnitName { get; set; }
  public string? PurchaseLink { get; set; }
  public decimal? LastUnitPrice { get; set; }
  public DateTime? LastPurchaseDate { get; set; }
  public string? Notes { get; set; }
}

/// <summary>Un proveedor tal como llega desde el editor de materiales.</summary>
public sealed class MaterialVendorLinkRequest
{
  [Range(1, int.MaxValue, ErrorMessage = "Elige un proveedor.")]
  public int BusinessPartnerId { get; set; }

  public bool IsPrimary { get; set; }
  public bool IsActive { get; set; } = true;

  [StringLength(100)]
  public string? VendorCode { get; set; }

  [Range(typeof(decimal), "0.0001", "999999999", ErrorMessage = "El contenido debe ser mayor que cero.")]
  public decimal? PurchaseQuantity { get; set; }

  public int? PurchaseUnitId { get; set; }
  public string? PurchaseLink { get; set; }

  [Range(typeof(decimal), "0", "999999999", ErrorMessage = "El precio no puede ser negativo.")]
  public decimal? LastUnitPrice { get; set; }

  [StringLength(500)]
  public string? Notes { get; set; }
}

public sealed class MaterialUpsertRequest
{
  [Required]
  public string Rfc { get; set; } = string.Empty;

  public int? Id { get; set; }

  [Required]
  [StringLength(800)]
  public string Description { get; set; } = string.Empty;

  [Required]
  public int BaseUnitId { get; set; }

  [Range(typeof(decimal), "0.0001", "999999999")]
  public decimal PurchaseQuantity { get; set; } = 1m;

  public int? PurchaseUnitId { get; set; }

  [Range(typeof(decimal), "0", "999999999", ErrorMessage = "El precio por unidad base no puede ser negativo.")]
  public decimal? BaseUnitPrice { get; set; }

  [StringLength(50)]
  public string? Brand { get; set; }

  [StringLength(100)]
  public string? Model { get; set; }

  public bool IsPerishable { get; set; }
  public int? ShelfLifeDays { get; set; }
  public bool RequiresRefrigeration { get; set; }

  [Required]
  [StringLength(50)]
  public string Status { get; set; } = "ACTIVO";

  public int? CategoryId { get; set; }

  [StringLength(50)]
  public string? Barcode { get; set; }

  [StringLength(100)]
  public string? VendorCode { get; set; }

  public string? PurchaseLink { get; set; }

  [Required]
  [StringLength(50)]
  public string MaterialClass { get; set; } = "Consumable";

  /// <summary>
  /// Rol de producción elegido por el usuario. En <c>null</c> o vacío el material conserva la
  /// clasificación que ya tenía, de modo que guardar desde una pantalla que no la muestra no la pise.
  /// </summary>
  [StringLength(40)]
  public string? ProductionRole { get; set; }

  /// <summary>
  /// Proveedores del material. En <c>null</c> los vínculos existentes se conservan tal cual, de
  /// modo que guardar desde una pantalla que no los muestra no los borre —mismo criterio que
  /// <see cref="ProductionRole"/>. Una lista vacía sí significa "quítalos todos".
  /// </summary>
  public List<MaterialVendorLinkRequest>? Vendors { get; set; }

  public bool RemovePrimaryImage { get; set; }
  public byte[]? PrimaryImageBytes { get; set; }
  public string? PrimaryImageFileName { get; set; }
  public string? PrimaryImageContentType { get; set; }
  public byte[]? PrimaryImageThumbnailBytes { get; set; }
  public string? PrimaryImageThumbnailContentType { get; set; }
}

public static class MaterialDependencyKinds
{
  public const string Operational = "Operational";
  public const string Historical = "Historical";
  public const string Configuration = "Configuration";
}

public sealed class MaterialLifecycleAssessmentDto
{
  public bool Exists { get; set; }
  public int MaterialId { get; set; }
  public string MaterialCode { get; set; } = string.Empty;
  public string Description { get; set; } = string.Empty;
  public bool IsActive { get; set; }
  public IReadOnlyList<MaterialDependencyDto> Dependencies { get; set; } = Array.Empty<MaterialDependencyDto>();
  public IReadOnlyList<MaterialDependencyDto> OperationalBlockers
    => Dependencies.Where(dependency => dependency.Kind == MaterialDependencyKinds.Operational).ToArray();
  public IReadOnlyList<MaterialDependencyDto> HistoricalReferences
    => Dependencies.Where(dependency => dependency.Kind == MaterialDependencyKinds.Historical).ToArray();
  public IReadOnlyList<MaterialDependencyDto> ConfigurationReferences
    => Dependencies.Where(dependency => dependency.Kind == MaterialDependencyKinds.Configuration).ToArray();
  public bool HasHistory => HistoricalReferences.Count > 0;
  public bool CanDelete => Exists && IsActive && Dependencies.Count == 0;
  public bool CanDeactivate => Exists && IsActive && HasHistory && OperationalBlockers.Count == 0;
  public bool CanReactivate => Exists && !IsActive;
  public long TotalReferences => Dependencies.Sum(dependency => dependency.ReferenceCount);
  public long OperationalReferenceCount => OperationalBlockers.Sum(dependency => dependency.ReferenceCount);
  public long HistoricalReferenceCount => HistoricalReferences.Sum(dependency => dependency.ReferenceCount);
  public long ConfigurationReferenceCount => ConfigurationReferences.Sum(dependency => dependency.ReferenceCount);
}

public sealed class MaterialDependencyDto
{
  public string Code { get; set; } = string.Empty;
  public string Kind { get; set; } = string.Empty;
  public string Title { get; set; } = string.Empty;
  public string Explanation { get; set; } = string.Empty;
  public long ReferenceCount { get; set; }
  public IReadOnlyList<string> Examples { get; set; } = Array.Empty<string>();
  public string? ResolutionLabel { get; set; }
  public string? ResolutionUrl { get; set; }
}

public sealed class MaterialDeleteRequest
{
  [Required]
  public string Rfc { get; set; } = string.Empty;

  [Range(1, int.MaxValue)]
  public int MaterialId { get; set; }

  [Required]
  public string ConfirmationText { get; set; } = string.Empty;

  [StringLength(256)]
  public string? DeletedBy { get; set; }
}

public sealed class MaterialDeactivateRequest
{
  [Required]
  public string Rfc { get; set; } = string.Empty;

  [Range(1, int.MaxValue)]
  public int MaterialId { get; set; }

  [StringLength(256)]
  public string? DeactivatedBy { get; set; }
}

public sealed class MaterialReactivateRequest
{
  [Required]
  public string Rfc { get; set; } = string.Empty;

  [Range(1, int.MaxValue)]
  public int MaterialId { get; set; }

  [StringLength(256)]
  public string? ReactivatedBy { get; set; }
}

public sealed class MaterialCatalogDto
{
  public IReadOnlyList<LookupOptionDto> Categories { get; set; } = Array.Empty<LookupOptionDto>();
  public IReadOnlyList<LookupOptionDto> Units { get; set; } = Array.Empty<LookupOptionDto>();
  public IReadOnlyList<LookupOptionDto> Vendors { get; set; } = Array.Empty<LookupOptionDto>();
  public IReadOnlyList<string> MaterialClasses { get; set; } = Array.Empty<string>();
  public IReadOnlyList<string> Statuses { get; set; } = Array.Empty<string>();
}

public sealed class MaterialCategoryCreateRequest
{
  [Required]
  public string Rfc { get; set; } = string.Empty;

  [Required]
  [StringLength(100)]
  public string Name { get; set; } = string.Empty;

  [StringLength(200)]
  public string? Description { get; set; }
}

public sealed class UnitOfMeasureCreateRequest
{
  [Required]
  [StringLength(50)]
  public string Name { get; set; } = string.Empty;

  [StringLength(10)]
  public string? Abbreviation { get; set; }

  [StringLength(200)]
  public string? Description { get; set; }
}

public sealed class MaterialStockLocationDto
{
  public int StockBalanceId { get; set; }
  public int LocationId { get; set; }
  public string LocationCode { get; set; } = string.Empty;
  public string LocationName { get; set; } = string.Empty;
  public string LocationType { get; set; } = string.Empty;
  public string? ParentLocationName { get; set; }
  public string? RoomName { get; set; }
  public bool IsLocationActive { get; set; }
  public bool IsInventoryEnabled { get; set; }
  public decimal Quantity { get; set; }
  public decimal ReservedQuantity { get; set; }
  public decimal? MinQuantity { get; set; }
  public decimal? MaxQuantity { get; set; }
  public decimal AverageUnitCost { get; set; }
  public bool IsLowStock { get; set; }
  public bool IsOverStock { get; set; }
  public bool IsCountDue { get; set; }
  public DateTime? LastCountedAt { get; set; }
  public int? CountFrequencyDays { get; set; }
  public DateTime? LastPurchaseDate { get; set; }
  public DateTime? LastMovementAt { get; set; }
  public DateTime? UpdatedAt { get; set; }
  public int MovementCount { get; set; }
  public int AttachmentCount { get; set; }
  public string? Notes { get; set; }
  public bool IsRemoved { get; set; }
  public DateTime? RemovedAt { get; set; }
  public string? RemovedBy { get; set; }

  public decimal AvailableQuantity => Quantity - ReservedQuantity;
  public decimal InventoryValue => Quantity * AverageUnitCost;
  public bool HasThresholds => MinQuantity.HasValue || MaxQuantity.HasValue;

  public DateTime? NextCountDueAt
    => CountFrequencyDays.HasValue && LastCountedAt.HasValue
      ? LastCountedAt.Value.AddDays(CountFrequencyDays.Value)
      : null;

  /// <summary>Units required to bring the balance back up to the configured maximum.</summary>
  public decimal? SuggestedRefillQuantity
    => MaxQuantity.HasValue && MaxQuantity.Value > Quantity ? MaxQuantity.Value - Quantity : null;

  /// <summary>Units above the configured maximum.</summary>
  public decimal? ExcessQuantity
    => MaxQuantity.HasValue && Quantity > MaxQuantity.Value ? Quantity - MaxQuantity.Value : null;

  public string CoverageState
    => !HasThresholds ? "unset"
      : IsLowStock ? "low"
      : IsOverStock ? "over"
      : "ok";
}

public sealed class MaterialMovementTypeOptionDto
{
  public string TransactionType { get; set; } = string.Empty;
  public int MovementCount { get; set; }
  public DateTime? LastOccurredAt { get; set; }
}

public sealed class MaterialInventorySnapshotDto
{
  public int MaterialId { get; set; }
  public IReadOnlyList<MaterialStockLocationDto> Locations { get; set; } = Array.Empty<MaterialStockLocationDto>();
  public IReadOnlyList<MaterialMovementTypeOptionDto> MovementTypes { get; set; } = Array.Empty<MaterialMovementTypeOptionDto>();

  public IReadOnlyList<MaterialStockLocationDto> StoredLocations
    => Locations.Where(location => !location.IsRemoved).ToArray();
  public IReadOnlyList<MaterialStockLocationDto> RemovedLocations
    => Locations.Where(location => location.IsRemoved).ToArray();

  public decimal TotalQuantity => StoredLocations.Sum(location => location.Quantity);
  public decimal TotalReservedQuantity => StoredLocations.Sum(location => location.ReservedQuantity);
  public decimal TotalAvailableQuantity => TotalQuantity - TotalReservedQuantity;
  public decimal TotalInventoryValue => StoredLocations.Sum(location => location.InventoryValue);
  public decimal TotalMinQuantity => StoredLocations.Sum(location => location.MinQuantity ?? 0m);
  public decimal TotalMaxQuantity => StoredLocations.Sum(location => location.MaxQuantity ?? 0m);
  public int LowStockCount => StoredLocations.Count(location => location.IsLowStock);
  public int OverStockCount => StoredLocations.Count(location => location.IsOverStock);
  public int CountDueCount => StoredLocations.Count(location => location.IsCountDue);
  public int MissingThresholdCount => StoredLocations.Count(location => !location.HasThresholds);
  public int TotalMovementCount => MovementTypes.Sum(option => option.MovementCount);

  public DateTime? LastMovementAt => MovementTypes.Max(option => option.LastOccurredAt);

  public bool HasLocations => Locations.Count > 0;
}

public sealed class MaterialMovementFilter
{
  [Required]
  public string Rfc { get; set; } = string.Empty;

  [Range(1, int.MaxValue)]
  public int MaterialId { get; set; }

  public int? LocationId { get; set; }
  public string? TransactionType { get; set; }

  /// <summary>Inclusive lower bound, expressed in UTC.</summary>
  public DateTime? OccurredFromUtc { get; set; }

  /// <summary>Exclusive upper bound, expressed in UTC.</summary>
  public DateTime? OccurredToUtc { get; set; }

  public string? SearchText { get; set; }
  public int Skip { get; set; }
  public int Take { get; set; }
}

public sealed class MaterialMovementDto
{
  public int Id { get; set; }
  public DateTime OccurredAt { get; set; }
  public string TransactionType { get; set; } = string.Empty;
  public decimal QuantityDelta { get; set; }
  public decimal QuantityAfter { get; set; }
  public int LocationId { get; set; }
  public string? LocationCode { get; set; }
  public string? LocationName { get; set; }
  public string? RoomName { get; set; }
  public string? ReferenceType { get; set; }
  public int? ReferenceId { get; set; }
  public string? Notes { get; set; }
  public string? PerformedBy { get; set; }

  public decimal QuantityBefore => QuantityAfter - QuantityDelta;
  public bool IsInbound => QuantityDelta > 0m;
  public bool IsOutbound => QuantityDelta < 0m;
}
