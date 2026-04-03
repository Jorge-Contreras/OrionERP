using System.ComponentModel.DataAnnotations;
using OrionERP.Application.Features.Logistica.Shared;

namespace OrionERP.Application.Features.Logistica.BusinessPartners;

public sealed class BusinessPartnerFilter
{
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
