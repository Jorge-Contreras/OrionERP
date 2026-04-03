using System.ComponentModel.DataAnnotations;

namespace OrionERP.Application.Features.Logistica.Locations;

public sealed class LocationFilter
{
  public string? SearchText { get; set; }
  public int? RoomId { get; set; }
  public bool IncludeInactive { get; set; }
}

public sealed class LocationListItemDto
{
  public int Id { get; set; }
  public string LocationCode { get; set; } = string.Empty;
  public string LocationName { get; set; } = string.Empty;
  public string LocationType { get; set; } = string.Empty;
  public int? ParentLocationId { get; set; }
  public string? ParentLocationName { get; set; }
  public int? RoomId { get; set; }
  public string? RoomName { get; set; }
  public bool IsInventoryEnabled { get; set; }
  public bool IsActive { get; set; }
  public int ChildCount { get; set; }
  public int MaterialCount { get; set; }
}

public sealed class LocationDetailDto
{
  public int Id { get; set; }
  public string LocationCode { get; set; } = string.Empty;
  public string LocationName { get; set; } = string.Empty;
  public string LocationType { get; set; } = string.Empty;
  public int? ParentLocationId { get; set; }
  public int? RoomId { get; set; }
  public string? Description { get; set; }
  public bool IsInventoryEnabled { get; set; }
  public bool IsActive { get; set; }
  public int? LegacyEspacioId { get; set; }
  public int? LegacyRoomId { get; set; }
}

public sealed class LocationTreeNodeDto
{
  public int Id { get; set; }
  public string LocationCode { get; set; } = string.Empty;
  public string LocationName { get; set; } = string.Empty;
  public string LocationType { get; set; } = string.Empty;
  public int? RoomId { get; set; }
  public bool IsInventoryEnabled { get; set; }
  public IReadOnlyList<LocationTreeNodeDto> Children { get; set; } = Array.Empty<LocationTreeNodeDto>();
}

public sealed class LocationUpsertRequest
{
  public int? Id { get; set; }

  [StringLength(50)]
  public string? LocationCode { get; set; }

  [Required]
  [StringLength(200)]
  public string LocationName { get; set; } = string.Empty;

  [Required]
  [StringLength(50)]
  public string LocationType { get; set; } = "Storage";

  public int? ParentLocationId { get; set; }
  public int? RoomId { get; set; }

  [StringLength(500)]
  public string? Description { get; set; }

  public bool IsInventoryEnabled { get; set; } = true;
  public bool IsActive { get; set; } = true;
}
