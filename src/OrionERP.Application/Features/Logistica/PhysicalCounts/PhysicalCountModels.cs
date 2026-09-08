using System.ComponentModel.DataAnnotations;
using OrionERP.Application.Features.Logistica.Shared;

namespace OrionERP.Application.Features.Logistica.PhysicalCounts;

public static class PhysicalCountSessionStatuses
{
  public const string Draft = "Draft";
  public const string Submitted = "Submitted";
  public const string Approved = "Approved";
  public const string Recount = "Recount";
  public const string Posted = "Posted";
  public const string Canceled = "Canceled";
}

/// <summary>
/// Qué define el alcance de una sesión de conteo. <see cref="Location"/> recorre el subárbol de una
/// ubicación (comportamiento histórico); <see cref="Material"/> recorre todas las ubicaciones donde
/// los materiales elegidos tienen saldo.
/// </summary>
public static class PhysicalCountSessionScopeTypes
{
  public const string Location = "Location";
  public const string Material = "Material";

  public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
  {
    Location,
    Material
  };

  public static bool IsMaterialScope(string? scopeType)
    => string.Equals(scopeType, Material, StringComparison.OrdinalIgnoreCase);

  public static string Normalize(string? scopeType)
    => IsMaterialScope(scopeType) ? Material : Location;
}

public static class PhysicalCountRecountIssueCodes
{
  public const string QuantityMismatch = "QuantityMismatch";
  public const string UnitIssue = "UnitIssue";
  public const string WrongMaterial = "WrongMaterial";
  public const string EvidenceMissing = "EvidenceMissing";
  public const string ConditionIssue = "ConditionIssue";
  public const string Other = "Other";

  public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
  {
    QuantityMismatch,
    UnitIssue,
    WrongMaterial,
    EvidenceMissing,
    ConditionIssue,
    Other
  };
}

public static class PhysicalCountAuditEventTypes
{
  public const string SessionStarted = "SessionStarted";
  public const string LineCounted = "LineCounted";
  public const string EvidenceAdded = "EvidenceAdded";
  public const string Submitted = "Submitted";
  public const string RecountRequested = "RecountRequested";
  public const string RecountCompleted = "RecountCompleted";
  public const string Approved = "Approved";
  public const string Posted = "Posted";
  public const string Canceled = "Canceled";
}

public sealed class PhysicalCountSessionSummaryDto
{
  public int Id { get; set; }
  public string SessionCode { get; set; } = string.Empty;
  public string Status { get; set; } = string.Empty;
  public string ScopeType { get; set; } = PhysicalCountSessionScopeTypes.Location;
  public int? LocationId { get; set; }
  public string? LocationName { get; set; }
  public string? RoomName { get; set; }

  /// <summary>Materiales que definieron el alcance. Cero en un conteo por ubicación.</summary>
  public int MaterialCount { get; set; }

  /// <summary>Ubicaciones distintas que abarcan los renglones generados.</summary>
  public int LocationCount { get; set; }

  /// <summary>Descripción del primer material del alcance, para etiquetar la sesión.</summary>
  public string? PrimaryMaterialLabel { get; set; }

  public DateTime CreatedAt { get; set; }
  public string? CreatedBy { get; set; }
  public DateTime? SubmittedAt { get; set; }
  public string? SubmittedBy { get; set; }
  public DateTime? ApprovedAt { get; set; }
  public string? ApprovedBy { get; set; }
  public DateTime? PostedAt { get; set; }
  public string? PostedBy { get; set; }
  public DateTime? CanceledAt { get; set; }
  public string? CanceledBy { get; set; }
  public string? CancelReason { get; set; }
  public DateTime? RecountRequestedAt { get; set; }
  public string? RecountRequestedBy { get; set; }
  public int LineCount { get; set; }
  public int CountedLineCount { get; set; }
  public int VarianceLineCount { get; set; }
  public int RecountLineCount { get; set; }
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
  public string? Barcode { get; set; }
  public string MaterialClass { get; set; } = string.Empty;
  public string? BaseUnitName { get; set; }
  public int LocationId { get; set; }
  public string LocationCode { get; set; } = string.Empty;
  public string LocationName { get; set; } = string.Empty;
  public string? RoomName { get; set; }

  /// <summary>
  /// Orden de recorrido: sala, ubicación y material. Fija la ruta del contador cuando la sesión
  /// abarca varias ubicaciones.
  /// </summary>
  public int? CountSequence { get; set; }

  public decimal ExpectedQuantity { get; set; }
  public decimal? CountedQuantity { get; set; }
  public decimal? VarianceQuantity { get; set; }
  public string? Notes { get; set; }
  public bool IsMissing { get; set; }
  public bool IsDamaged { get; set; }
  public DateTime? CapturedAt { get; set; }
  public string? CapturedBy { get; set; }
  public string? RecountIssueCode { get; set; }
  public string? RecountReason { get; set; }
  public DateTime? RecountRequestedAt { get; set; }
  public string? RecountRequestedBy { get; set; }
  public int AttachmentCount { get; set; }
  public IReadOnlyList<PhysicalCountAttachmentDto> Attachments { get; set; } = Array.Empty<PhysicalCountAttachmentDto>();
}

public sealed class PhysicalCountAuditEventDto
{
  public string EventType { get; set; } = string.Empty;
  public DateTime OccurredAt { get; set; }
  public string? PerformedBy { get; set; }
  public int? MaterialId { get; set; }
  public string? MaterialCode { get; set; }
  public string? MaterialDescription { get; set; }
  public string? LocationName { get; set; }
  public decimal? ExpectedQuantity { get; set; }
  public decimal? CountedQuantity { get; set; }
  public string? Details { get; set; }
}

/// <summary>Un material del alcance de la sesión, con lo que aportó al conteo.</summary>
public sealed class PhysicalCountMaterialScopeDto
{
  public int MaterialId { get; set; }
  public string MaterialCode { get; set; } = string.Empty;
  public string MaterialDescription { get; set; } = string.Empty;
  public int LineCount { get; set; }
  public int LocationCount { get; set; }
}

public sealed class PhysicalCountSessionDetailDto
{
  public int Id { get; set; }
  public string SessionCode { get; set; } = string.Empty;
  public string Status { get; set; } = string.Empty;
  public string ScopeType { get; set; } = PhysicalCountSessionScopeTypes.Location;
  public int? LocationId { get; set; }
  public string? LocationName { get; set; }
  public string? RoomName { get; set; }
  public int? MaxLocationsPerMaterial { get; set; }
  public int MaterialCount { get; set; }
  public int LocationCount { get; set; }
  public string? PrimaryMaterialLabel { get; set; }
  public string? Notes { get; set; }
  public DateTime CreatedAt { get; set; }
  public string? CreatedBy { get; set; }
  public DateTime? SubmittedAt { get; set; }
  public string? SubmittedBy { get; set; }
  public DateTime? ApprovedAt { get; set; }
  public string? ApprovedBy { get; set; }
  public DateTime? PostedAt { get; set; }
  public string? PostedBy { get; set; }
  public DateTime? CanceledAt { get; set; }
  public string? CanceledBy { get; set; }
  public string? CancelReason { get; set; }
  public int? ActiveRecountPlanId { get; set; }
  public DateTime? RecountRequestedAt { get; set; }
  public string? RecountRequestedBy { get; set; }
  public IReadOnlyList<PhysicalCountLineDto> Lines { get; set; } = Array.Empty<PhysicalCountLineDto>();
  public IReadOnlyList<PhysicalCountAuditEventDto> AuditEvents { get; set; } = Array.Empty<PhysicalCountAuditEventDto>();
  public IReadOnlyList<PhysicalCountMaterialScopeDto> Materials { get; set; } = Array.Empty<PhysicalCountMaterialScopeDto>();
}

public sealed class PhysicalCountPendingRecountDto
{
  public int Id { get; set; }
  public string SessionCode { get; set; } = string.Empty;
  public string ScopeType { get; set; } = PhysicalCountSessionScopeTypes.Location;
  public string? LocationName { get; set; }
  public string? RoomName { get; set; }
  public int MaterialCount { get; set; }
  public int LocationCount { get; set; }
  public string? PrimaryMaterialLabel { get; set; }
  public DateTime RecountRequestedAt { get; set; }
  public string? RecountRequestedBy { get; set; }
  public int LineCount { get; set; }
  public int RecountLineCount { get; set; }
  public string IssueSummary { get; set; } = string.Empty;
}

/// <summary>
/// Alcance de un conteo: materiales, ubicación, o ambos. Es la base compartida por la creación y por
/// la vista previa, para que el jefe de Logística vea exactamente lo que se va a generar.
/// </summary>
public class PhysicalCountScopeRequest
{
  [StringLength(20)]
  public string ScopeType { get; set; } = PhysicalCountSessionScopeTypes.Location;

  /// <summary>Obligatoria en alcance por ubicación; opcional en alcance por material, donde limita el recorrido.</summary>
  public int? LocationId { get; set; }

  /// <summary>Materiales a contar. Vacío en alcance por ubicación.</summary>
  public IReadOnlyList<int> MaterialIds { get; set; } = Array.Empty<int>();

  /// <summary>Tope de ubicaciones por material; prioriza lo nunca contado y lo más antiguo.</summary>
  [Range(1, 1000)]
  public int? MaxLocationsPerMaterial { get; set; }

  public bool IsMaterialScope => PhysicalCountSessionScopeTypes.IsMaterialScope(ScopeType);
}

public sealed class PhysicalCountSessionCreateRequest : PhysicalCountScopeRequest
{
  [StringLength(1000)]
  public string? Notes { get; set; }

  [StringLength(256)]
  public string? CreatedBy { get; set; }
}

public sealed class PhysicalCountScopePreviewRequest : PhysicalCountScopeRequest
{
}

/// <summary>Un renglón de la vista previa: cuánto aportaría cada material al conteo.</summary>
public sealed class PhysicalCountScopeMaterialPreviewDto
{
  public int MaterialId { get; set; }
  public string MaterialCode { get; set; } = string.Empty;
  public string MaterialDescription { get; set; } = string.Empty;
  public int LocationCount { get; set; }
  public decimal TotalQuantity { get; set; }
  public DateTime? LastCountedAt { get; set; }
}

/// <summary>Un choque con una sesión abierta que ya reclamó ese material en esa ubicación.</summary>
public sealed class PhysicalCountScopeConflictDto
{
  public int SessionId { get; set; }
  public string SessionCode { get; set; } = string.Empty;
  public string Status { get; set; } = string.Empty;
  public string MaterialCode { get; set; } = string.Empty;
  public string MaterialDescription { get; set; } = string.Empty;
  public string LocationName { get; set; } = string.Empty;
  public int OverlappingLineCount { get; set; }
}

public sealed class PhysicalCountScopePreviewDto
{
  public int MaterialCount { get; set; }
  public int LocationCount { get; set; }
  public int LineCount { get; set; }
  public IReadOnlyList<PhysicalCountScopeMaterialPreviewDto> Materials { get; set; } = Array.Empty<PhysicalCountScopeMaterialPreviewDto>();
  public IReadOnlyList<PhysicalCountScopeConflictDto> Conflicts { get; set; } = Array.Empty<PhysicalCountScopeConflictDto>();
  public bool HasConflicts => Conflicts.Count > 0;
}

public sealed class PhysicalCountLineCaptureRequest
{
  [Required]
  public int SessionId { get; set; }

  [Required]
  public int LineId { get; set; }

  public DateTime? ExpectedCapturedAt { get; set; }

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

public sealed class PhysicalCountRecountRequest
{
  [Required]
  public int SessionId { get; set; }

  [StringLength(256)]
  public string? RequestedBy { get; set; }

  public IReadOnlyList<PhysicalCountRecountLineRequest> Lines { get; set; } = Array.Empty<PhysicalCountRecountLineRequest>();
}

public sealed class PhysicalCountRecountLineRequest
{
  [Required]
  public int LineId { get; set; }

  [Required]
  [StringLength(50)]
  public string IssueCode { get; set; } = PhysicalCountRecountIssueCodes.QuantityMismatch;

  [Required]
  [StringLength(1000)]
  public string Reason { get; set; } = string.Empty;
}

public sealed class PhysicalCountCancelRequest
{
  [Required]
  public int SessionId { get; set; }

  [StringLength(256)]
  public string? CanceledBy { get; set; }

  [Required]
  [StringLength(1000)]
  public string Reason { get; set; } = string.Empty;
}
