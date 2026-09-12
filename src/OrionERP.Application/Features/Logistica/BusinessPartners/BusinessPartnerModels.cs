using System.ComponentModel.DataAnnotations;
using OrionERP.Application.Features.Logistica.Shared;

namespace OrionERP.Application.Features.Logistica.BusinessPartners;

public sealed class BusinessPartnerFilter
{
  [Required]
  public string OwnerRfc { get; set; } = string.Empty;
  public string? SearchText { get; set; }
  public string? Role { get; set; }
  public bool VendorOnly { get; set; }
  public bool IncludeInactive { get; set; }
}

public sealed class BusinessPartnerListItemDto
{
  public int Id { get; set; }
  public int? LegacyProveedorId { get; set; }
  public string DisplayName { get; set; } = string.Empty;
  public string? Rfc { get; set; }
  public string? Email { get; set; }
  public string? Phone { get; set; }
  public bool IsActive { get; set; }
  public bool HasVendorProfile { get; set; }
  public string PrimaryRole { get; set; } = string.Empty;
  public int MaterialCount { get; set; }
}

public sealed class VendorProfileDto
{
  public int BusinessPartnerId { get; set; }
  public string? PaymentTerms { get; set; }
  public int? DefaultLeadTimeDays { get; set; }
  public bool IsApproved { get; set; }
  public string? Notes { get; set; }
}

public sealed class BusinessPartnerDetailDto
{
  public int Id { get; set; }
  public int? LegacyProveedorId { get; set; }
  public string DisplayName { get; set; } = string.Empty;
  public string? Rfc { get; set; }
  public string? Email { get; set; }
  public string? Phone { get; set; }
  public string? Street { get; set; }
  public string? Neighborhood { get; set; }
  public string? City { get; set; }
  public string? State { get; set; }
  public string? PostalCode { get; set; }
  public string? BusinessLine { get; set; }
  public string? Notes { get; set; }
  public bool IsActive { get; set; }
  public IReadOnlyList<string> Roles { get; set; } = Array.Empty<string>();
  public VendorProfileDto? VendorProfile { get; set; }
}

public sealed class BusinessPartnerUpsertRequest
{
  [Required]
  public string OwnerRfc { get; set; } = string.Empty;

  public int? Id { get; set; }
  public int? LegacyProveedorId { get; set; }

  [Required]
  [StringLength(200)]
  public string DisplayName { get; set; } = string.Empty;

  [StringLength(50)]
  public string? Rfc { get; set; }

  [StringLength(100)]
  public string? Email { get; set; }

  [StringLength(50)]
  public string? Phone { get; set; }

  [StringLength(100)]
  public string? Street { get; set; }

  [StringLength(50)]
  public string? Neighborhood { get; set; }

  [StringLength(50)]
  public string? City { get; set; }

  [StringLength(50)]
  public string? State { get; set; }

  [StringLength(20)]
  public string? PostalCode { get; set; }

  [StringLength(100)]
  public string? BusinessLine { get; set; }

  [StringLength(700)]
  public string? Notes { get; set; }

  public bool IsActive { get; set; } = true;
  public IReadOnlyList<string> Roles { get; set; } = Array.Empty<string>();
  public VendorProfileUpsertRequest? VendorProfile { get; set; }
}

public sealed class VendorProfileUpsertRequest
{
  [StringLength(100)]
  public string? PaymentTerms { get; set; }

  public int? DefaultLeadTimeDays { get; set; }
  public bool IsApproved { get; set; } = true;

  [StringLength(500)]
  public string? Notes { get; set; }
}

public sealed class BusinessPartnerCatalogDto
{
  public IReadOnlyList<LookupOptionDto> Roles { get; set; } = Array.Empty<LookupOptionDto>();
}

public static class VendorDependencyKinds
{
  public const string Operational = "Operational";
  public const string Historical = "Historical";
  public const string Configuration = "Configuration";

  /// <summary>
  /// Datos que pertenecen al socio y desaparecen con él. No lo retienen, pero se
  /// informan porque su borrado también es permanente.
  /// </summary>
  public const string Owned = "Owned";
}

public sealed class VendorDependencyDto
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

public sealed class VendorLifecycleAssessmentDto
{
  public bool Exists { get; set; }
  public int BusinessPartnerId { get; set; }
  public string DisplayName { get; set; } = string.Empty;
  public string? Rfc { get; set; }
  public bool IsActive { get; set; }
  public int? LegacyProveedorId { get; set; }

  /// <summary>Vínculos que impiden eliminar al socio.</summary>
  public IReadOnlyList<VendorDependencyDto> Dependencies { get; set; } = Array.Empty<VendorDependencyDto>();

  /// <summary>Datos propios del socio que se eliminan junto con él.</summary>
  public IReadOnlyList<VendorDependencyDto> OwnedRecords { get; set; } = Array.Empty<VendorDependencyDto>();

  public IReadOnlyList<VendorDependencyDto> OperationalBlockers
    => Dependencies.Where(dependency => dependency.Kind == VendorDependencyKinds.Operational).ToArray();
  public IReadOnlyList<VendorDependencyDto> HistoricalReferences
    => Dependencies.Where(dependency => dependency.Kind == VendorDependencyKinds.Historical).ToArray();
  public IReadOnlyList<VendorDependencyDto> ConfigurationReferences
    => Dependencies.Where(dependency => dependency.Kind == VendorDependencyKinds.Configuration).ToArray();

  public bool HasHistory => HistoricalReferences.Count > 0;
  public bool CanDelete => Exists && Dependencies.Count == 0;
  public long TotalReferences => Dependencies.Sum(dependency => dependency.ReferenceCount);
  public long OperationalReferenceCount => OperationalBlockers.Sum(dependency => dependency.ReferenceCount);
  public long HistoricalReferenceCount => HistoricalReferences.Sum(dependency => dependency.ReferenceCount);
  public long ConfigurationReferenceCount => ConfigurationReferences.Sum(dependency => dependency.ReferenceCount);
  public long OwnedRecordCount => OwnedRecords.Sum(record => record.ReferenceCount);
}

public sealed class VendorDeleteRequest
{
  [Required]
  public string OwnerRfc { get; set; } = string.Empty;

  [Range(1, int.MaxValue)]
  public int BusinessPartnerId { get; set; }

  [Required]
  public string ConfirmationText { get; set; } = string.Empty;

  [StringLength(256)]
  public string? DeletedBy { get; set; }
}
