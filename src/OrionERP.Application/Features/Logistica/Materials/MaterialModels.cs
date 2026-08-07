using System.ComponentModel.DataAnnotations;
using OrionERP.Application.Features.Logistica.Shared;

namespace OrionERP.Application.Features.Logistica.Materials;

public sealed class MaterialFilter
{
  [Required]
  public string Rfc { get; set; } = string.Empty;
  public string? SearchText { get; set; }
  public int? CategoryId { get; set; }
  public int? VendorId { get; set; }
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
  public string Status { get; set; } = string.Empty;
  public bool IsActive { get; set; }
  public string? CategoryName { get; set; }
  public string? BaseUnitName { get; set; }
  public string? VendorName { get; set; }
  public decimal? Price { get; set; }
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
  public int? BusinessPartnerId { get; set; }
  public decimal? Price { get; set; }
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
  public bool IsActive { get; set; }
  public bool HasImage { get; set; }
  public string? PrimaryImageFileName { get; set; }
  public string? PrimaryImageContentType { get; set; }
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
  public int? BusinessPartnerId { get; set; }
  public decimal? Price { get; set; }

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
