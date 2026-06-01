using System;
using System.Collections.Generic;

namespace OrionERP.Application.Features.Reservaciones.Experiencias;

public sealed class ExperienceCatalogItemDto
{
  public int ExperienceId { get; set; }
  public string Code { get; set; } = string.Empty;
  public string Name { get; set; } = string.Empty;
  public string? Description { get; set; }
  public string Category { get; set; } = string.Empty;
  public string ProviderName { get; set; } = string.Empty;
  public DateOnly? SeasonStart { get; set; }
  public DateOnly? SeasonEnd { get; set; }
  public bool IsPublic { get; set; }
  public bool IsActive { get; set; }
  public int MinimumParticipants { get; set; } = 1;
  public int? MaximumParticipants { get; set; }
  public IReadOnlyList<ExperiencePackageOptionDto> Packages { get; set; } = Array.Empty<ExperiencePackageOptionDto>();
  public IReadOnlyList<ExperienceAddOnOptionDto> AddOns { get; set; } = Array.Empty<ExperienceAddOnOptionDto>();
}

public sealed class ExperiencePackageOptionDto
{
  public int ExperiencePackageId { get; set; }
  public int ExperienceId { get; set; }
  public string Code { get; set; } = string.Empty;
  public string Name { get; set; } = string.Empty;
  public string? ProviderPackageName { get; set; }
  public string? Description { get; set; }
  public string? Includes { get; set; }
  public decimal UnitPrice { get; set; }
  public string TaxMode { get; set; } = ExperienceTaxModes.TaxableExclusive;
  public bool IsPublic { get; set; }
  public bool IsActive { get; set; }
  public int DisplayOrder { get; set; }
}

public sealed class ExperienceAddOnOptionDto
{
  public int ExperienceAddOnId { get; set; }
  public int ExperienceId { get; set; }
  public string Code { get; set; } = string.Empty;
  public string Name { get; set; } = string.Empty;
  public string? Description { get; set; }
  public decimal UnitPrice { get; set; }
  public bool AppliesPerParticipant { get; set; }
  public string TaxMode { get; set; } = ExperienceTaxModes.TaxableExclusive;
  public bool IsPublic { get; set; }
  public bool IsActive { get; set; }
  public int DisplayOrder { get; set; }
}

public static class ExperienceTaxModes
{
  public const string TaxableExclusive = "TaxableExclusive";
  public const string TaxIncluded = "TaxIncluded";
  public const string NonTaxable = "NonTaxable";
}
