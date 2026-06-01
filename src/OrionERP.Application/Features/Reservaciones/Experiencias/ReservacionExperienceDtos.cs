using System;
using System.Collections.Generic;

namespace OrionERP.Application.Features.Reservaciones.Experiencias;

public sealed class ReservacionExperienceDto
{
  public int Id { get; set; }
  public int ReservationId { get; set; }
  public int ExperienceId { get; set; }
  public int ExperiencePackageId { get; set; }
  public DateTime ExperienceDate { get; set; }
  public string ExperienceName { get; set; } = string.Empty;
  public string PackageName { get; set; } = string.Empty;
  public string ProviderName { get; set; } = string.Empty;
  public string? PackageIncludes { get; set; }
  public int AdultParticipants { get; set; }
  public int ChildParticipants { get; set; }
  public decimal UnitPrice { get; set; }
  public decimal PackageSubtotal { get; set; }
  public decimal AddOnsTotal { get; set; }
  public decimal Total { get; set; }
  public string TaxMode { get; set; } = ExperienceTaxModes.TaxableExclusive;
  public string? Notes { get; set; }
  public int TotalParticipants => AdultParticipants + ChildParticipants;
  public bool RequiresOperationalWarning => ChildParticipants > 0;
  public IReadOnlyList<ReservacionExperienceAddOnDto> AddOns { get; set; } = Array.Empty<ReservacionExperienceAddOnDto>();
}

public sealed class ReservacionExperienceAddOnDto
{
  public int Id { get; set; }
  public int ReservationExperienceId { get; set; }
  public int ExperienceAddOnId { get; set; }
  public string AddOnName { get; set; } = string.Empty;
  public int Quantity { get; set; }
  public decimal UnitPrice { get; set; }
  public decimal Total { get; set; }
  public string TaxMode { get; set; } = ExperienceTaxModes.TaxableExclusive;
}

public class ReservacionExperienceCreateRequest
{
  public int ReservationId { get; set; }
  public int ExperienceId { get; set; }
  public int ExperiencePackageId { get; set; }
  public DateTime ExperienceDate { get; set; }
  public int AdultParticipants { get; set; }
  public int ChildParticipants { get; set; }
  public IReadOnlyList<ReservacionExperienceAddOnRequest> AddOns { get; set; } = Array.Empty<ReservacionExperienceAddOnRequest>();
  public string? Notes { get; set; }
}

public sealed class ReservacionExperienceUpdateRequest : ReservacionExperienceCreateRequest
{
  public int Id { get; set; }
}

public sealed class ReservacionExperienceAddOnRequest
{
  public int ExperienceAddOnId { get; set; }
  public int Quantity { get; set; }
}
