using System;

namespace OrionERP.Application.Features.Reservaciones.Extras;

public sealed class ExtraCatalogItemDto
{
  public int ExtraId { get; set; }
  public string Name { get; set; } = string.Empty;
  public string? Description { get; set; }
  public decimal Price { get; set; }
  public bool IsActive { get; set; }
  public int? LegacyRoomId { get; set; }
  public DateTime CreatedAtUtc { get; set; }
  public DateTime UpdatedAtUtc { get; set; }
}

public sealed class ExtraCatalogSaveRequest
{
  public int? ExtraId { get; set; }
  public string Name { get; set; } = string.Empty;
  public string? Description { get; set; }
  public decimal Price { get; set; }
  public bool IsActive { get; set; } = true;
}
