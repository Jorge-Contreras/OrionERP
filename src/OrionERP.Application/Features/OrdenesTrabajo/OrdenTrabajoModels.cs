using System.ComponentModel.DataAnnotations;

namespace OrionERP.Application.Features.OrdenesTrabajo;

public class OrdenTrabajoCommandResult
{
  public bool Success { get; set; }
  public string Message { get; set; } = string.Empty;
  public int? EntityId { get; set; }

  public static OrdenTrabajoCommandResult Ok(string message, int? entityId = null)
    => new()
    {
      Success = true,
      Message = message,
      EntityId = entityId
    };

  public static OrdenTrabajoCommandResult Fail(string message, int? entityId = null)
    => new()
    {
      Success = false,
      Message = message,
      EntityId = entityId
    };
}

public sealed class OrdenTrabajoBinaryContent
{
  public int Id { get; set; }
  public string FileName { get; set; } = string.Empty;
  public string ContentType { get; set; } = "application/octet-stream";
  public byte[] Bytes { get; set; } = Array.Empty<byte>();
}

public sealed class OrdenTrabajoLookupDto
{
  public int Id { get; set; }
  public string Name { get; set; } = string.Empty;
  public string? Code { get; set; }
}

public sealed class OrdenTrabajoCategoriaDto
{
  public int Id { get; set; }
  public string Codigo { get; set; } = string.Empty;
  public string Nombre { get; set; } = string.Empty;
  public bool Activa { get; set; }
  public int Orden { get; set; }
}

public sealed class OrdenTrabajoDashboardFilter
{
  public string? Rfc { get; set; }
  public int? EmployeeId { get; set; }
  public bool AssignedOnly { get; set; }
}

public sealed class OrdenTrabajoDashboardDto
{
  public int OpenCount { get; set; }
  public int OverdueCount { get; set; }
  public int PendingReviewCount { get; set; }
  public int TodayCleaningCount { get; set; }
  public IReadOnlyList<OrdenTrabajoStatusCountDto> StatusCounts { get; set; } = Array.Empty<OrdenTrabajoStatusCountDto>();
  public IReadOnlyList<OrdenTrabajoAssigneeLoadDto> AssigneeLoads { get; set; } = Array.Empty<OrdenTrabajoAssigneeLoadDto>();
  public IReadOnlyList<OrdenTrabajoListItemDto> TodayCleaningOrders { get; set; } = Array.Empty<OrdenTrabajoListItemDto>();
}

public sealed class OrdenTrabajoStatusCountDto
{
  public string Estado { get; set; } = string.Empty;
  public int Count { get; set; }
}

public sealed class OrdenTrabajoAssigneeLoadDto
{
  public int EmployeeId { get; set; }
  public string EmployeeName { get; set; } = string.Empty;
  public int OpenCount { get; set; }
  public int PendingReviewCount { get; set; }
  public int OverdueCount { get; set; }
}

public sealed class OrdenTrabajoSearchFilter
{
  public string? Rfc { get; set; }
  public string? SearchText { get; set; }
  public string? Estado { get; set; }
  public string? CategoriaCodigo { get; set; }
  public int? OwnerEmployeeId { get; set; }
  public int? ParticipantEmployeeId { get; set; }
  public string? CreatedByActor { get; set; }
  public DateTime? ScheduledFrom { get; set; }
  public DateTime? ScheduledTo { get; set; }
  public bool IncludeClosed { get; set; }
  public int Skip { get; set; }
  public int Take { get; set; } = 100;
}

public class OrdenTrabajoListItemDto
{
  public int Id { get; set; }
  public string Folio { get; set; } = string.Empty;
  public string Estado { get; set; } = string.Empty;
  public string Prioridad { get; set; } = OrdenTrabajoCodes.PrioridadNormal;
  public string CategoriaCodigo { get; set; } = string.Empty;
  public string CategoriaNombre { get; set; } = string.Empty;
  public string Titulo { get; set; } = string.Empty;
  public string? Ubicacion { get; set; }
  public int OwnerEmployeeId { get; set; }
  public string OwnerName { get; set; } = string.Empty;
  public DateTime FechaProgramada { get; set; }
  public TimeSpan? HoraInicioProgramada { get; set; }
  public TimeSpan? HoraFinProgramada { get; set; }
  public DateTime? FechaVencimiento { get; set; }
  public int? RoomId { get; set; }
  public string? RoomName { get; set; }
  public int? RoomCalendarId { get; set; }
  public int? ReservationId { get; set; }
  public int StepCount { get; set; }
  public int CompletedStepCount { get; set; }
  public int IssueStepCount { get; set; }
  public decimal EstimatedCost { get; set; }
  public decimal ActualCost { get; set; }
  public bool IsOverdue { get; set; }
}

public sealed class OrdenTrabajoDetailDto : OrdenTrabajoListItemDto
{
  public string Rfc { get; set; } = string.Empty;
  public string? Descripcion { get; set; }
  public int? PlantillaId { get; set; }
  public int? PlantillaVersionId { get; set; }
  public string? PlantillaNombre { get; set; }
  public int? PlantillaVersionNumero { get; set; }
  public bool HasBeenSubmittedForReview { get; set; }
  public string? CanceladaPor { get; set; }
  public DateTime? CanceladaEn { get; set; }
  public string? MotivoCancelacion { get; set; }
  public string? RechazadaPor { get; set; }
  public DateTime? RechazadaEn { get; set; }
  public string? MotivoRechazo { get; set; }
  public DateTime? InicioReal { get; set; }
  public DateTime? FinReal { get; set; }
  public DateTime CreadaEn { get; set; }
  public string CreadaPor { get; set; } = string.Empty;
  public IReadOnlyList<OrdenTrabajoParticipantDto> Helpers { get; set; } = Array.Empty<OrdenTrabajoParticipantDto>();
  public IReadOnlyList<OrdenTrabajoStepDto> Steps { get; set; } = Array.Empty<OrdenTrabajoStepDto>();
  public IReadOnlyList<OrdenTrabajoTransactionDto> Transactions { get; set; } = Array.Empty<OrdenTrabajoTransactionDto>();
  public IReadOnlyList<OrdenTrabajoAuditDto> Audit { get; set; } = Array.Empty<OrdenTrabajoAuditDto>();
}

public sealed class OrdenTrabajoParticipantDto
{
  public int EmployeeId { get; set; }
  public string EmployeeName { get; set; } = string.Empty;
}

public sealed class OrdenTrabajoStepDto
{
  public int Id { get; set; }
  public int OrdenTrabajoId { get; set; }
  public decimal Secuencia { get; set; }
  public string Titulo { get; set; } = string.Empty;
  public string Descripcion { get; set; } = string.Empty;
  public string Estado { get; set; } = OrdenTrabajoCodes.PasoPendiente;
  public string PoliticaFoto { get; set; } = OrdenTrabajoCodes.FotoOpcional;
  public bool RequiereNotasEnIncidencia { get; set; }
  public bool RequiereNotasEnNoAplica { get; set; }
  public int? ProcedimientoId { get; set; }
  public string? Notas { get; set; }
  public DateTime? CompletadoEn { get; set; }
  public string? CompletadoPor { get; set; }
  public int ActiveEvidenceCount { get; set; }
  public IReadOnlyList<OrdenTrabajoEvidenceDto> Evidence { get; set; } = Array.Empty<OrdenTrabajoEvidenceDto>();
}

public sealed class OrdenTrabajoEvidenceDto
{
  public int Id { get; set; }
  public int PasoId { get; set; }
  public string FileName { get; set; } = string.Empty;
  public string ContentType { get; set; } = "image/jpeg";
  public string CaptureSource { get; set; } = OrdenTrabajoCodes.EvidenciaUnknown;
  public byte[]? ThumbnailBytes { get; set; }
  public string? ThumbnailContentType { get; set; }
  public long SizeBytes { get; set; }
  public DateTime CapturadaEn { get; set; }
  public string CapturadaPor { get; set; } = string.Empty;
  public bool Eliminada { get; set; }
}

public sealed class OrdenTrabajoTransactionDto
{
  public int TransaccionId { get; set; }
  public DateTime Fecha { get; set; }
  public string Concepto { get; set; } = string.Empty;
  public decimal Monto { get; set; }
  public string? Estatus { get; set; }
}

public sealed class OrdenTrabajoAuditDto
{
  public int Id { get; set; }
  public string Evento { get; set; } = string.Empty;
  public string? Detalle { get; set; }
  public string CreadoPor { get; set; } = string.Empty;
  public DateTime CreadoEn { get; set; }
}

public sealed class OrdenTrabajoCreateRequest
{
  [Required]
  [StringLength(50)]
  public string Rfc { get; set; } = string.Empty;

  [Required]
  [StringLength(50)]
  public string CategoriaCodigo { get; set; } = OrdenTrabajoCodes.CategoriaMantenimiento;

  [Required]
  [StringLength(200)]
  public string Titulo { get; set; } = string.Empty;

  [StringLength(2000)]
  public string? Descripcion { get; set; }

  [Required]
  public int OwnerEmployeeId { get; set; }

  public IReadOnlyList<int> HelperEmployeeIds { get; set; } = Array.Empty<int>();
  public DateTime FechaProgramada { get; set; } = DateTime.Today;
  public TimeSpan? HoraInicioProgramada { get; set; }
  public TimeSpan? HoraFinProgramada { get; set; }
  public DateTime? FechaVencimiento { get; set; }
  public string Prioridad { get; set; } = OrdenTrabajoCodes.PrioridadNormal;
  public int? TemplateId { get; set; }
  public int? RoomId { get; set; }
  public int? RoomCalendarId { get; set; }
  public int? ReservationId { get; set; }

  [StringLength(500)]
  public string? Ubicacion { get; set; }

  public decimal EstimatedCost { get; set; }

  [StringLength(256)]
  public string? CreatedBy { get; set; }
}

public sealed class OrdenTrabajoUpdateRequest
{
  [Required]
  [StringLength(200)]
  public string Titulo { get; set; } = string.Empty;

  [StringLength(2000)]
  public string? Descripcion { get; set; }

  [Required]
  public int OwnerEmployeeId { get; set; }

  public IReadOnlyList<int> HelperEmployeeIds { get; set; } = Array.Empty<int>();
  public DateTime FechaProgramada { get; set; } = DateTime.Today;
  public TimeSpan? HoraInicioProgramada { get; set; }
  public TimeSpan? HoraFinProgramada { get; set; }
  public DateTime? FechaVencimiento { get; set; }
  public string Prioridad { get; set; } = OrdenTrabajoCodes.PrioridadNormal;

  [StringLength(500)]
  public string? Ubicacion { get; set; }

  public decimal EstimatedCost { get; set; }

  [StringLength(256)]
  public string? UpdatedBy { get; set; }
}

public sealed class OrdenTrabajoStepsSaveRequest
{
  public IReadOnlyList<OrdenTrabajoStepSaveRequest> Steps { get; set; } = Array.Empty<OrdenTrabajoStepSaveRequest>();

  [StringLength(256)]
  public string? SavedBy { get; set; }
}

public sealed class OrdenTrabajoStepSaveRequest
{
  public decimal Secuencia { get; set; }

  [Required]
  [StringLength(200)]
  public string Titulo { get; set; } = string.Empty;

  [Required]
  [StringLength(1000)]
  public string Descripcion { get; set; } = string.Empty;

  public string PoliticaFoto { get; set; } = OrdenTrabajoCodes.FotoOpcional;
  public bool RequiereNotasEnIncidencia { get; set; } = true;
  public bool RequiereNotasEnNoAplica { get; set; } = true;
  public int? ProcedimientoId { get; set; }
}

public sealed class OrdenTrabajoCalendarCreateRequest
{
  [Required]
  [StringLength(50)]
  public string Rfc { get; set; } = string.Empty;

  [Required]
  public int OwnerEmployeeId { get; set; }

  public IReadOnlyList<int> HelperEmployeeIds { get; set; } = Array.Empty<int>();
  public IReadOnlyList<int> RoomCalendarIds { get; set; } = Array.Empty<int>();

  [StringLength(256)]
  public string? CreatedBy { get; set; }
}

public sealed class OrdenTrabajoCalendarCreateResult : OrdenTrabajoCommandResult
{
  public IReadOnlyList<OrdenTrabajoCalendarCellResult> Cells { get; set; } = Array.Empty<OrdenTrabajoCalendarCellResult>();
}

public sealed class OrdenTrabajoCalendarCellResult
{
  public int RoomCalendarId { get; set; }
  public int? WorkOrderId { get; set; }
  public string? Folio { get; set; }
  public string Message { get; set; } = string.Empty;
  public bool Success { get; set; }
}

public sealed class OrdenTrabajoStepUpdateRequest
{
  [Required]
  public string Estado { get; set; } = OrdenTrabajoCodes.PasoHecho;

  [StringLength(2000)]
  public string? Notas { get; set; }

  [StringLength(256)]
  public string? UpdatedBy { get; set; }

  public int? ActorEmployeeId { get; set; }
}

public sealed class OrdenTrabajoEvidenceCreateRequest
{
  public byte[] ImageBytes { get; set; } = Array.Empty<byte>();
  public byte[]? ThumbnailBytes { get; set; }

  [StringLength(200)]
  public string? FileName { get; set; }

  [StringLength(100)]
  public string? ContentType { get; set; }

  [StringLength(100)]
  public string? ThumbnailContentType { get; set; }

  [StringLength(500)]
  public string? DeviceInfo { get; set; }

  public string CaptureSource { get; set; } = OrdenTrabajoCodes.EvidenciaUnknown;

  [StringLength(256)]
  public string? CapturedBy { get; set; }

  public int? ActorEmployeeId { get; set; }
}

public sealed class OrdenTrabajoReviewRequest
{
  [StringLength(2000)]
  public string? Reason { get; set; }

  [StringLength(256)]
  public string? Actor { get; set; }
}

public class OrdenTrabajoTemplateSummaryDto
{
  public int Id { get; set; }
  public string Nombre { get; set; } = string.Empty;
  public string CategoriaCodigo { get; set; } = string.Empty;
  public string CategoriaNombre { get; set; } = string.Empty;
  public string Rfc { get; set; } = string.Empty;
  public bool Activa { get; set; }
  public int? PublishedVersionId { get; set; }
  public int? PublishedVersionNumber { get; set; }
  public int DraftVersionCount { get; set; }
  public int StepCount { get; set; }
}

public sealed class OrdenTrabajoTemplateDetailDto : OrdenTrabajoTemplateSummaryDto
{
  public int? DraftVersionId { get; set; }
  public int? DraftVersionNumber { get; set; }
  public IReadOnlyList<OrdenTrabajoTemplateStepDto> DraftSteps { get; set; } = Array.Empty<OrdenTrabajoTemplateStepDto>();
  public IReadOnlyList<OrdenTrabajoRoomTemplateMappingDto> RoomMappings { get; set; } = Array.Empty<OrdenTrabajoRoomTemplateMappingDto>();
}

public sealed class OrdenTrabajoTemplateStepDto
{
  public int Id { get; set; }
  public decimal Secuencia { get; set; }
  public string Titulo { get; set; } = string.Empty;
  public string Descripcion { get; set; } = string.Empty;
  public string PoliticaFoto { get; set; } = OrdenTrabajoCodes.FotoOpcional;
  public bool RequiereNotasEnIncidencia { get; set; } = true;
  public bool RequiereNotasEnNoAplica { get; set; } = true;
  public int? ProcedimientoId { get; set; }
}

public sealed class OrdenTrabajoRoomTemplateMappingDto
{
  public int RoomId { get; set; }
  public string RoomName { get; set; } = string.Empty;
  public string RoomType { get; set; } = string.Empty;
  public int? TemplateId { get; set; }
  public string? TemplateName { get; set; }
}

public sealed class OrdenTrabajoTemplateSaveRequest
{
  public int? TemplateId { get; set; }

  [Required]
  [StringLength(200)]
  public string Nombre { get; set; } = string.Empty;

  [Required]
  [StringLength(50)]
  public string CategoriaCodigo { get; set; } = OrdenTrabajoCodes.CategoriaLimpieza;

  [Required]
  [StringLength(50)]
  public string Rfc { get; set; } = string.Empty;

  public bool Activa { get; set; } = true;
  public IReadOnlyList<OrdenTrabajoTemplateStepSaveRequest> Steps { get; set; } = Array.Empty<OrdenTrabajoTemplateStepSaveRequest>();

  [StringLength(256)]
  public string? SavedBy { get; set; }
}

public sealed class OrdenTrabajoTemplateStepSaveRequest
{
  public decimal Secuencia { get; set; }

  [Required]
  [StringLength(200)]
  public string Titulo { get; set; } = string.Empty;

  [Required]
  [StringLength(1000)]
  public string Descripcion { get; set; } = string.Empty;

  public string PoliticaFoto { get; set; } = OrdenTrabajoCodes.FotoOpcional;
  public bool RequiereNotasEnIncidencia { get; set; } = true;
  public bool RequiereNotasEnNoAplica { get; set; } = true;
  public int? ProcedimientoId { get; set; }
}

public sealed class OrdenTrabajoTransactionSearchItemDto
{
  public int Id { get; set; }
  public DateTime Fecha { get; set; }
  public string Concepto { get; set; } = string.Empty;
  public decimal Monto { get; set; }
  public string? Estatus { get; set; }
  public string Rfc { get; set; } = string.Empty;
}

public sealed class OrdenTrabajoCalendarBadgeDto
{
  public int RoomCalendarId { get; set; }
  public int WorkOrderId { get; set; }
  public string Folio { get; set; } = string.Empty;
  public string Estado { get; set; } = string.Empty;
  public string CategoriaCodigo { get; set; } = string.Empty;
  public string OwnerName { get; set; } = string.Empty;
  public string? HelperNames { get; set; }
}
