namespace OrionERP.Application.Features.Ajustes.Catalogos;

public sealed record HospitalityLegacyConfigurationDto
{
  public IReadOnlyList<HospitalityOwnerAssociationDto> Owners { get; init; } = [];
  public IReadOnlyList<HospitalityOwnerCandidateDto> OwnerCandidates { get; init; } = [];
  public IReadOnlyList<HospitalityActivityMappingDto> ActivityMappings { get; init; } = [];
  public IReadOnlyList<HospitalityLegacyOptionDto> Rooms { get; init; } = [];
  public IReadOnlyList<HospitalityLegacyOptionDto> ActivityTemplates { get; init; } = [];
  public IReadOnlyList<HospitalityLegacyOptionDto> Assignees { get; init; } = [];
}

public sealed record HospitalityOwnerAssociationDto
{
  public int ProveedorId { get; init; }
  public string Nombre { get; init; } = string.Empty;
  public int RoomCount { get; init; }
}

public sealed record HospitalityOwnerCandidateDto
{
  public int ProveedorId { get; init; }
  public string Nombre { get; init; } = string.Empty;
}

public sealed record HospitalityLegacyOptionDto
{
  public int Id { get; init; }
  public string Nombre { get; init; } = string.Empty;
}

public sealed record HospitalityActivityMappingDto
{
  public int RoomId { get; init; }
  public string RoomName { get; init; } = string.Empty;
  public string ActivityType { get; init; } = string.Empty;
  public int TemplateActivityId { get; init; }
  public string TemplateName { get; init; } = string.Empty;
  public int AssigneeEmployeeId { get; init; }
  public string AssigneeName { get; init; } = string.Empty;
  public int CuentaSatId { get; init; }
  public bool IsEnabled { get; init; }
  public int GeneratedActivityCount { get; init; }
}

public sealed record HospitalityActivityMappingSaveRequest
{
  public int RoomId { get; init; }
  public string ActivityType { get; init; } = string.Empty;
  public int TemplateActivityId { get; init; }
  public int AssigneeEmployeeId { get; init; }
  public int CuentaSatId { get; init; }
  public bool IsEnabled { get; init; }
}
