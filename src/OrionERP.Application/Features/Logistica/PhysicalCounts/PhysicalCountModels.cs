using System.ComponentModel.DataAnnotations;
using OrionERP.Application.Features.Logistica.Shared;

namespace OrionERP.Application.Features.Logistica.PhysicalCounts;

public sealed class PhysicalCountSessionSummaryDto
{
  public int Id { get; set; }
  public string SessionCode { get; set; } = string.Empty;
  public string Status { get; set; } = string.Empty;
  public int LocationId { get; set; }
  public string LocationName { get; set; } = string.Empty;
  public string? RoomName { get; set; }
  public DateTime CreatedAt { get; set; }
  public string? CreatedBy { get; set; }
  public DateTime? SubmittedAt { get; set; }
  public string? SubmittedBy { get; set; }
  public DateTime? ApprovedAt { get; set; }
  public string? ApprovedBy { get; set; }
  public DateTime? PostedAt { get; set; }
  public string? PostedBy { get; set; }
  public int LineCount { get; set; }
  public int VarianceLineCount { get; set; }
}

public sealed class PhysicalCountAttachmentDto
{
  public int Id { get; set; }
  public int PhysicalCountLineId { get; set; }
  public string FileName { get; set; } = string.Empty;
  public string FileExtension { get; set; } = string.Empty;
  public string? Description { get; set; }
  public long Length { get; set; }
  public DateTime CreatedAt { get; set; }
  public string? CreatedBy { get; set; }
}

public sealed class PhysicalCountLineDto
{
  public int Id { get; set; }
  public int StockBalanceId { get; set; }
  public int MaterialId { get; set; }
  public string MaterialCode { get; set; } = string.Empty;
  public string MaterialDescription { get; set; } = string.Empty;
  public string MaterialClass { get; set; } = string.Empty;
  public string? BaseUnitName { get; set; }
  public decimal ExpectedQuantity { get; set; }
  public decimal? CountedQuantity { get; set; }
  public decimal? VarianceQuantity { get; set; }
  public string? Notes { get; set; }
  public bool IsMissing { get; set; }
  public bool IsDamaged { get; set; }
  public DateTime? CapturedAt { get; set; }
  public string? CapturedBy { get; set; }
  public int AttachmentCount { get; set; }
  public bool RequiresEvidence { get; set; }
  public IReadOnlyList<PhysicalCountAttachmentDto> Attachments { get; set; } = Array.Empty<PhysicalCountAttachmentDto>();
}

public sealed class PhysicalCountSessionDetailDto
{
  public int Id { get; set; }
  public string SessionCode { get; set; } = string.Empty;
  public string Status { get; set; } = string.Empty;
  public int LocationId { get; set; }
  public string LocationName { get; set; } = string.Empty;
  public string? RoomName { get; set; }
  public string? Notes { get; set; }
  public DateTime CreatedAt { get; set; }
  public string? CreatedBy { get; set; }
  public DateTime? SubmittedAt { get; set; }
  public string? SubmittedBy { get; set; }
  public DateTime? ApprovedAt { get; set; }
  public string? ApprovedBy { get; set; }
  public DateTime? PostedAt { get; set; }
  public string? PostedBy { get; set; }
  public IReadOnlyList<PhysicalCountLineDto> Lines { get; set; } = Array.Empty<PhysicalCountLineDto>();
}

public sealed class PhysicalCountSessionCreateRequest
{
  [Required]
  public int LocationId { get; set; }

  [StringLength(1000)]
  public string? Notes { get; set; }

  [StringLength(256)]
  public string? CreatedBy { get; set; }
}

public sealed class PhysicalCountLineCaptureRequest
{
  [Required]
  public int SessionId { get; set; }

  [Required]
  public int LineId { get; set; }

  public decimal CountedQuantity { get; set; }

  [StringLength(1000)]
  public string? Notes { get; set; }

  public bool IsMissing { get; set; }
  public bool IsDamaged { get; set; }

  [StringLength(256)]
  public string? CapturedBy { get; set; }

  public byte[]? AttachmentBytes { get; set; }

  [StringLength(200)]
  public string? AttachmentFileName { get; set; }

  [StringLength(50)]
  public string? AttachmentExtension { get; set; }

  [StringLength(100)]
  public string? AttachmentContentType { get; set; }

  [StringLength(500)]
  public string? AttachmentDescription { get; set; }
}
