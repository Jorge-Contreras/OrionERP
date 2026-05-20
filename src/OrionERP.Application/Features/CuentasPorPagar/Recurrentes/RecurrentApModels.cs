using System.ComponentModel.DataAnnotations;

namespace OrionERP.Application.Features.CuentasPorPagar.Recurrentes;

public static class RecurrentApStatuses
{
  public const string Pending = "Pending";
  public const string PartiallyPaid = "PartiallyPaid";
  public const string Paid = "Paid";
  public const string Skipped = "Skipped";
  public const string Cancelled = "Cancelled";

  public static readonly IReadOnlyList<string> All =
  [
    Pending,
    PartiallyPaid,
    Paid,
    Skipped,
    Cancelled
  ];
}

public static class RecurrentApFrequencyUnits
{
  public const string Days = "Days";
  public const string Weeks = "Weeks";
  public const string Months = "Months";
  public const string Years = "Years";

  public static readonly IReadOnlyList<string> All =
  [
    Days,
    Weeks,
    Months,
    Years
  ];
}

public sealed class RecurrentApFilter
{
  public string? Rfc { get; set; }
  public DateTime? FromDate { get; set; }
  public DateTime? ToDate { get; set; }
  public string? Status { get; set; }
  public string? SearchText { get; set; }
  public int DueSoonDays { get; set; } = 7;
  public int Take { get; set; } = 500;
  public bool OpenOnly { get; set; }
}

public sealed class RecurrentApDashboardDto
{
  public int TotalOpen { get; set; }
  public int DueSoon { get; set; }
  public int Overdue { get; set; }
  public int PaidThisMonth { get; set; }
  public decimal ExpectedOpenAmount { get; set; }
  public decimal PaidThisMonthAmount { get; set; }
}

public sealed class RecurrentApWorkspaceDto
{
  public RecurrentApDashboardDto Dashboard { get; set; } = new();
  public IReadOnlyList<RecurrentApOccurrenceListItemDto> Occurrences { get; set; } = Array.Empty<RecurrentApOccurrenceListItemDto>();
  public IReadOnlyList<RecurrentApPayableSummaryDto> Payables { get; set; } = Array.Empty<RecurrentApPayableSummaryDto>();
  public IReadOnlyList<RecurrentApVendorOptionDto> Vendors { get; set; } = Array.Empty<RecurrentApVendorOptionDto>();
  public IReadOnlyList<string> Statuses { get; set; } = RecurrentApStatuses.All;
  public IReadOnlyList<string> FrequencyUnits { get; set; } = RecurrentApFrequencyUnits.All;
}

public sealed class RecurrentApPayableSummaryDto
{
  public int Id { get; set; }
  public string Rfc { get; set; } = string.Empty;
  public string Name { get; set; } = string.Empty;
  public int? BusinessPartnerId { get; set; }
  public string? PayeeNameSnapshot { get; set; }
  public string? PayeeRfcSnapshot { get; set; }
  public string? Category { get; set; }
  public string? Description { get; set; }
  public string? Website { get; set; }
  public string? UserName { get; set; }
  public string? Password { get; set; }
  public string FrequencyUnit { get; set; } = RecurrentApFrequencyUnits.Months;
  public int IntervalCount { get; set; } = 1;
  public DateTime StartDate { get; set; } = DateTime.Today;
  public DateTime? EndDate { get; set; }
  public int? DueDayOfMonth { get; set; }
  public int? DueMonth { get; set; }
  public decimal? ExpectedAmount { get; set; }
  public string Currency { get; set; } = "MXN";
  public bool IsActive { get; set; } = true;
}

public sealed class RecurrentApOccurrenceListItemDto
{
  public int Id { get; set; }
  public int RecurringPayableId { get; set; }
  public string Rfc { get; set; } = string.Empty;
  public string PayableName { get; set; } = string.Empty;
  public string? PayeeName { get; set; }
  public string? Category { get; set; }
  public DateTime PeriodStartDate { get; set; }
  public DateTime DueDate { get; set; }
  public decimal? ExpectedAmount { get; set; }
  public decimal ActualPaidAmount { get; set; }
  public string Status { get; set; } = RecurrentApStatuses.Pending;
  public DateTime? PaymentDate { get; set; }
  public string? Notes { get; set; }
  public int PaymentLinkCount { get; set; }
  public int AttachmentCount { get; set; }
  public bool IsDueSoon { get; set; }
  public bool IsOverdue { get; set; }
}

public sealed class RecurrentApOccurrenceDetailDto
{
  public int Id { get; set; }
  public int RecurringPayableId { get; set; }
  public string Rfc { get; set; } = string.Empty;
  public string PayableName { get; set; } = string.Empty;
  public string? PayeeName { get; set; }
  public string? PayeeRfc { get; set; }
  public string? Category { get; set; }
  public string? Description { get; set; }
  public string? Website { get; set; }
  public string? UserName { get; set; }
  public string? Password { get; set; }
  public string FrequencyUnit { get; set; } = RecurrentApFrequencyUnits.Months;
  public int IntervalCount { get; set; } = 1;
  public DateTime StartDate { get; set; }
  public DateTime? EndDate { get; set; }
  public int? DueDayOfMonth { get; set; }
  public int? DueMonth { get; set; }
  public bool IsActive { get; set; }
  public DateTime PeriodStartDate { get; set; }
  public DateTime DueDate { get; set; }
  public decimal? ExpectedAmount { get; set; }
  public decimal ActualPaidAmount { get; set; }
  public string Status { get; set; } = RecurrentApStatuses.Pending;
  public DateTime? PaymentDate { get; set; }
  public string? Notes { get; set; }
}

public sealed class RecurrentApUpsertRequest
{
  public int? Id { get; set; }

  [Required]
  [StringLength(50)]
  public string Rfc { get; set; } = string.Empty;

  [Required]
  [StringLength(200)]
  public string Name { get; set; } = string.Empty;

  public int? BusinessPartnerId { get; set; }

  [StringLength(200)]
  public string? PayeeNameSnapshot { get; set; }

  [StringLength(50)]
  public string? PayeeRfcSnapshot { get; set; }

  [StringLength(80)]
  public string? Category { get; set; }

  [StringLength(1000)]
  public string? Description { get; set; }

  [StringLength(500)]
  public string? Website { get; set; }

  [StringLength(200)]
  public string? UserName { get; set; }

  [StringLength(1000)]
  public string? Password { get; set; }

  [Required]
  public string FrequencyUnit { get; set; } = RecurrentApFrequencyUnits.Months;

  [Range(1, 120)]
  public int IntervalCount { get; set; } = 1;

  [Required]
  public DateTime StartDate { get; set; } = DateTime.Today;

  public DateTime? EndDate { get; set; }

  [Range(1, 31)]
  public int? DueDayOfMonth { get; set; }

  [Range(1, 12)]
  public int? DueMonth { get; set; }

  [Range(typeof(decimal), "0", "999999999999")]
  public decimal? ExpectedAmount { get; set; }

  [StringLength(3, MinimumLength = 3)]
  public string Currency { get; set; } = "MXN";

  public bool IsActive { get; set; } = true;
}

public sealed class RecurrentApOccurrenceStatusRequest
{
  public int OccurrenceId { get; set; }
  public string Rfc { get; set; } = string.Empty;
  public string Status { get; set; } = RecurrentApStatuses.Pending;
  public decimal? ExpectedAmount { get; set; }
  public decimal? ActualAmount { get; set; }
  public DateTime? PaymentDate { get; set; }
  public string? Notes { get; set; }
}

public sealed class RecurrentApReseedResult
{
  public int RecurringPayableId { get; set; }
  public int DeletedCount { get; set; }
  public int CreatedCount { get; set; }
  public int PreservedCount { get; set; }
}

public sealed class RecurrentApTransactionLinkRequest
{
  public int OccurrenceId { get; set; }
  public string Rfc { get; set; } = string.Empty;
  public int TransaccionId { get; set; }
  public decimal? Amount { get; set; }
  public DateTime? PaymentDate { get; set; }
  public string? Notes { get; set; }
}

public sealed class RecurrentApAttachmentCreateRequest
{
  public const long MaxFileSizeBytes = 10 * 1024 * 1024;

  public int OccurrenceId { get; set; }
  public string Rfc { get; set; } = string.Empty;
  public string FileName { get; set; } = string.Empty;
  public string ContentType { get; set; } = "application/octet-stream";
  public byte[] Content { get; set; } = [];
  public string? UploadedBy { get; set; }
}

public sealed class RecurrentApAttachmentDto
{
  public int Id { get; set; }
  public int OccurrenceId { get; set; }
  public string FileName { get; set; } = string.Empty;
  public string ContentType { get; set; } = string.Empty;
  public long SizeBytes { get; set; }
  public DateTime UploadedAt { get; set; }
  public string? UploadedBy { get; set; }
}

public sealed class RecurrentApAttachmentContent
{
  public string FileName { get; set; } = string.Empty;
  public string ContentType { get; set; } = "application/octet-stream";
  public byte[] Content { get; set; } = [];
}

public sealed class RecurrentApTransactionCandidateDto
{
  public int Id { get; set; }
  public DateTime Fecha { get; set; }
  public string? Concepto { get; set; }
  public decimal Monto { get; set; }
  public string? TipoPoliza { get; set; }
  public string? FormaPago { get; set; }
  public bool IsLinkedToAp { get; set; }
}

public sealed class RecurrentApTransactionLinkDto
{
  public int PaymentId { get; set; }
  public int OccurrenceId { get; set; }
  public int RecurringPayableId { get; set; }
  public string Rfc { get; set; } = string.Empty;
  public string PayableName { get; set; } = string.Empty;
  public DateTime DueDate { get; set; }
  public int TransaccionId { get; set; }
  public decimal Amount { get; set; }
  public DateTime PaymentDate { get; set; }
  public string Status { get; set; } = string.Empty;
}

public sealed class RecurrentApVendorOptionDto
{
  public int Id { get; set; }
  public string Name { get; set; } = string.Empty;
  public string? Rfc { get; set; }
}
