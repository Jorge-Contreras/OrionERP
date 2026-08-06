using System.ComponentModel.DataAnnotations;

namespace OrionERP.Application.Features.CapitalHumano.Workforce;

public static class AttendanceEventTypes
{
  public const string In = "IN";
  public const string Out = "OUT";
  public const string BreakStart = "BREAK_START";
  public const string BreakEnd = "BREAK_END";

  public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
  {
    In,
    Out,
    BreakStart,
    BreakEnd
  };
}

public static class AttendanceSources
{
  public const string Login = "LOGIN";
  public const string Kiosk = "KIOSK";
  public const string Adjustment = "ADJUSTMENT";
}

public static class ApprovalStatuses
{
  public const string Pending = "PENDING";
  public const string Approved = "APPROVED";
  public const string Rejected = "REJECTED";
  public const string Returned = "RETURNED";
}

public static class PrenominaStatuses
{
  public const string Open = "OPEN";
  public const string Ready = "READY";
  public const string Locked = "LOCKED";
  public const string Exported = "EXPORTED";
  public const string Reopened = "REOPENED";
}

public sealed record CurrentEmployeeContext(
  string UserName,
  int? EmployeeId,
  IReadOnlySet<string> Roles,
  IReadOnlySet<string> AllowedRfcs,
  string? EmployeeRfc = null)
{
  public bool IsInRole(params string[] roles)
    => Roles.Contains("Administrador") || roles.Any(Roles.Contains);

  public bool CanAccessRfc(string rfc)
    => AllowedRfcs.Contains(rfc);
}

public interface ICurrentEmployeeAccessor
{
  ValueTask<CurrentEmployeeContext?> GetCurrentAsync(CancellationToken ct = default);
}

public sealed class WorkforceCommandResult
{
  public bool Success { get; init; }
  public string Message { get; init; } = string.Empty;
  public long? EntityId { get; init; }

  public static WorkforceCommandResult Ok(string message, long? entityId = null)
    => new() { Success = true, Message = message, EntityId = entityId };

  public static WorkforceCommandResult Fail(string message, long? entityId = null)
    => new() { Success = false, Message = message, EntityId = entityId };
}

public sealed class EmployeeWorkforceOptionDto
{
  public int EmployeeId { get; set; }
  public string Name { get; set; } = string.Empty;
  public string? Position { get; set; }
  public bool HasLogin { get; set; }
  public bool IsConfigured { get; set; }
}

public sealed class WorkSiteDto
{
  public int Id { get; set; }
  public string Rfc { get; set; } = string.Empty;
  public string Code { get; set; } = string.Empty;
  public string Name { get; set; } = string.Empty;
  public string TimeZoneId { get; set; } = "America/Mexico_City";
  public decimal Latitude { get; set; }
  public decimal Longitude { get; set; }
  public int RadiusMeters { get; set; } = 150;
  public int MaxAccuracyMeters { get; set; } = 100;
  public bool IsActive { get; set; } = true;
}

public sealed class WorkSiteSaveRequest
{
  public int? Id { get; set; }

  [Required, StringLength(50)]
  public string Rfc { get; set; } = string.Empty;

  [Required, StringLength(30)]
  public string Code { get; set; } = string.Empty;

  [Required, StringLength(150)]
  public string Name { get; set; } = string.Empty;

  [Required, StringLength(100)]
  public string TimeZoneId { get; set; } = "America/Mexico_City";

  [Range(-90, 90)]
  public decimal Latitude { get; set; }

  [Range(-180, 180)]
  public decimal Longitude { get; set; }

  [Range(25, 5000)]
  public int RadiusMeters { get; set; } = 150;

  [Range(10, 5000)]
  public int MaxAccuracyMeters { get; set; } = 100;

  public bool IsActive { get; set; } = true;
}

public sealed class ScheduleDayDto
{
  public int DayOfWeek { get; set; }
  public bool IsWorkingDay { get; set; }
  public TimeSpan? StartTime { get; set; }
  public TimeSpan? EndTime { get; set; }
  public int UnpaidBreakMinutes { get; set; }
}

public sealed class ScheduleBreakDto
{
  public int Id { get; set; }
  public string Name { get; set; } = string.Empty;
  public TimeSpan? StartTime { get; set; }
  public int DurationMinutes { get; set; }
  public bool IsPaid { get; set; }
  public bool IsRequired { get; set; } = true;
}

public sealed class ScheduleTemplateDto
{
  public int Id { get; set; }
  public string Rfc { get; set; } = string.Empty;
  public string Code { get; set; } = string.Empty;
  public string Name { get; set; } = string.Empty;
  public bool IsActive { get; set; } = true;
  public List<ScheduleDayDto> Days { get; set; } = [];
  public List<ScheduleBreakDto> Breaks { get; set; } = [];
}

public sealed class ScheduleTemplateSaveRequest
{
  public int? Id { get; set; }
  [Required, StringLength(50)] public string Rfc { get; set; } = string.Empty;
  [Required, StringLength(30)] public string Code { get; set; } = string.Empty;
  [Required, StringLength(150)] public string Name { get; set; } = string.Empty;
  public bool IsActive { get; set; } = true;
  public List<ScheduleDayDto> Days { get; set; } = [];
  public List<ScheduleBreakDto> Breaks { get; set; } = [];
}

public class AttendancePolicyDto
{
  public int Id { get; set; }
  public string Rfc { get; set; } = string.Empty;
  public string Code { get; set; } = string.Empty;
  public string Name { get; set; } = string.Empty;
  public DateOnly EffectiveFrom { get; set; }
  public DateOnly? EffectiveTo { get; set; }
  public int WeeklyOrdinaryMinutes { get; set; }
  public int WeeklyDoubleOvertimeMinutes { get; set; }
  public int WeeklyTripleOvertimeMinutes { get; set; }
  public int GraceMinutes { get; set; }
  public int RoundingMinutes { get; set; }
  public bool LocationRequired { get; set; }
  public bool IsActive { get; set; }
}

public sealed class AttendancePolicySaveRequest : AttendancePolicyDto;

public class PayGroupDto
{
  public int Id { get; set; }
  public string Rfc { get; set; } = string.Empty;
  public string Code { get; set; } = string.Empty;
  public string Name { get; set; } = string.Empty;
  public string Frequency { get; set; } = "WEEKLY";
  public bool IsActive { get; set; } = true;
}

public sealed class PayGroupSaveRequest : PayGroupDto;

public sealed class EmployeeWorkAssignmentDto
{
  public int Id { get; set; }
  public string Rfc { get; set; } = string.Empty;
  public int EmployeeId { get; set; }
  public string EmployeeName { get; set; } = string.Empty;
  public int SiteId { get; set; }
  public string SiteName { get; set; } = string.Empty;
  public int ScheduleTemplateId { get; set; }
  public string ScheduleName { get; set; } = string.Empty;
  public int AttendancePolicyId { get; set; }
  public string PolicyName { get; set; } = string.Empty;
  public int PayGroupId { get; set; }
  public string PayGroupName { get; set; } = string.Empty;
  public DateOnly EffectiveFrom { get; set; }
  public DateOnly? EffectiveTo { get; set; }
}

public sealed class EmployeeWorkAssignmentSaveRequest
{
  public int? Id { get; set; }
  [Required, StringLength(50)] public string Rfc { get; set; } = string.Empty;
  [Range(1, int.MaxValue)] public int EmployeeId { get; set; }
  [Range(1, int.MaxValue)] public int SiteId { get; set; }
  [Range(1, int.MaxValue)] public int ScheduleTemplateId { get; set; }
  [Range(1, int.MaxValue)] public int AttendancePolicyId { get; set; }
  [Range(1, int.MaxValue)] public int PayGroupId { get; set; }
  public DateOnly EffectiveFrom { get; set; } = DateOnly.FromDateTime(DateTime.Today);
  public DateOnly? EffectiveTo { get; set; }
}

public sealed class SupervisorAssignmentDto
{
  public int Id { get; set; }
  public int EmployeeId { get; set; }
  public string EmployeeName { get; set; } = string.Empty;
  public int SupervisorEmployeeId { get; set; }
  public string SupervisorName { get; set; } = string.Empty;
  public DateOnly EffectiveFrom { get; set; }
  public DateOnly? EffectiveTo { get; set; }
}

public sealed class SupervisorAssignmentSaveRequest
{
  public int? Id { get; set; }
  [Required, StringLength(50)] public string Rfc { get; set; } = string.Empty;
  [Range(1, int.MaxValue)] public int EmployeeId { get; set; }
  [Range(1, int.MaxValue)] public int SupervisorEmployeeId { get; set; }
  public DateOnly EffectiveFrom { get; set; } = DateOnly.FromDateTime(DateTime.Today);
  public DateOnly? EffectiveTo { get; set; }
}

public sealed class WorkforceReadinessDto
{
  public int EmployeeId { get; set; }
  public string EmployeeName { get; set; } = string.Empty;
  public bool HasLogin { get; set; }
  public bool HasWorkAssignment { get; set; }
  public bool HasSupervisor { get; set; }
  public bool IsReady => HasLogin && HasWorkAssignment && HasSupervisor;
}

public sealed class HolidayDto
{
  public int Id { get; set; }
  public int? SiteId { get; set; }
  public DateOnly HolidayDate { get; set; }
  public string Name { get; set; } = string.Empty;
  public bool IsPaid { get; set; }
}

public sealed class HolidaySaveRequest
{
  public int? Id { get; set; }
  [Required, StringLength(50)] public string Rfc { get; set; } = string.Empty;
  public int? SiteId { get; set; }
  public DateOnly HolidayDate { get; set; } = DateOnly.FromDateTime(DateTime.Today);
  [Required, StringLength(150)] public string Name { get; set; } = string.Empty;
  public bool IsPaid { get; set; } = true;
}

public sealed class WorkforceSetupSnapshotDto
{
  public IReadOnlyList<EmployeeWorkforceOptionDto> Employees { get; set; } = [];
  public IReadOnlyList<WorkSiteDto> Sites { get; set; } = [];
  public IReadOnlyList<ScheduleTemplateDto> Schedules { get; set; } = [];
  public IReadOnlyList<AttendancePolicyDto> Policies { get; set; } = [];
  public IReadOnlyList<PayGroupDto> PayGroups { get; set; } = [];
  public IReadOnlyList<EmployeeWorkAssignmentDto> WorkAssignments { get; set; } = [];
  public IReadOnlyList<SupervisorAssignmentDto> SupervisorAssignments { get; set; } = [];
  public IReadOnlyList<WorkforceReadinessDto> Readiness { get; set; } = [];
  public IReadOnlyList<KioskDeviceDto> Kiosks { get; set; } = [];
  public IReadOnlyList<HolidayDto> Holidays { get; set; } = [];
  public IReadOnlyList<PrivacyNoticeDto> PrivacyNotices { get; set; } = [];
}

public sealed class LocationEvidenceDto
{
  public decimal? Latitude { get; set; }
  public decimal? Longitude { get; set; }
  public decimal? AccuracyMeters { get; set; }
  public DateTimeOffset? CapturedAt { get; set; }
}

public sealed class AttendancePunchRequest
{
  [Required, StringLength(50)] public string Rfc { get; set; } = string.Empty;
  [Required, StringLength(30)] public string EventType { get; set; } = AttendanceEventTypes.In;
  [Required, StringLength(100)] public string IdempotencyKey { get; set; } = Guid.NewGuid().ToString("N");
  public LocationEvidenceDto Location { get; set; } = new();
}

public sealed class AttendancePunchResult
{
  public bool Success { get; set; }
  public string Message { get; set; } = string.Empty;
  public long? EventId { get; set; }
  public string? NextEventType { get; set; }
  public string LocationStatus { get; set; } = "UNAVAILABLE";
  public decimal? DistanceMeters { get; set; }
  public bool RequiresReview { get; set; }
}

public sealed class AttendanceEventDto
{
  public long Id { get; set; }
  public int EmployeeId { get; set; }
  public string EventType { get; set; } = string.Empty;
  public string Source { get; set; } = string.Empty;
  public DateTime OccurredAtUtc { get; set; }
  public DateOnly WorkDate { get; set; }
  public string SiteName { get; set; } = string.Empty;
  public string LocationStatus { get; set; } = string.Empty;
  public decimal? DistanceMeters { get; set; }
  public decimal? AccuracyMeters { get; set; }
  public bool IsAdjustment { get; set; }
}

public sealed class AttendanceDayDto
{
  public long Id { get; set; }
  public int EmployeeId { get; set; }
  public string EmployeeName { get; set; } = string.Empty;
  public DateOnly WorkDate { get; set; }
  public int ScheduledMinutes { get; set; }
  public int WorkedMinutes { get; set; }
  public int BreakMinutes { get; set; }
  public int AbsenceMinutes { get; set; }
  public int LateMinutes { get; set; }
  public int EarlyDepartureMinutes { get; set; }
  public int OvertimeCandidateMinutes { get; set; }
  public int OvertimeApprovedMinutes { get; set; }
  public string Status { get; set; } = "OPEN";
  public bool HasExceptions { get; set; }
}

public sealed class AttendanceExceptionDto
{
  public long Id { get; set; }
  public int EmployeeId { get; set; }
  public string EmployeeName { get; set; } = string.Empty;
  public DateOnly WorkDate { get; set; }
  public string ExceptionType { get; set; } = string.Empty;
  public string Detail { get; set; } = string.Empty;
  public string? Resolution { get; set; }
  public string Status { get; set; } = ApprovalStatuses.Pending;
  public DateTime CreatedAtUtc { get; set; }
}

public sealed class AttendanceCorrectionRequestDto
{
  public long Id { get; set; }
  public int EmployeeId { get; set; }
  public string EmployeeName { get; set; } = string.Empty;
  public string EventType { get; set; } = string.Empty;
  public DateTime RequestedAtUtc { get; set; }
  public string Reason { get; set; } = string.Empty;
  public string? DecisionReason { get; set; }
  public string Status { get; set; } = ApprovalStatuses.Pending;
}

public sealed class AttendanceCorrectionCreateRequest
{
  [Required, StringLength(50)] public string Rfc { get; set; } = string.Empty;
  [Required, StringLength(30)] public string EventType { get; set; } = AttendanceEventTypes.In;
  public DateTime RequestedAtLocal { get; set; } = DateTime.Now;
  [Required, StringLength(500)] public string Reason { get; set; } = string.Empty;
}

public sealed class AttendancePeriodSummaryDto
{
  public DateOnly FromDate { get; set; }
  public DateOnly ToDate { get; set; }
  public int ScheduledMinutes { get; set; }
  public int WorkedMinutes { get; set; }
  public int OvertimeApprovedMinutes { get; set; }
  public int PendingExceptions { get; set; }
}

public sealed class EmployeeAttendanceDashboardDto
{
  public int EmployeeId { get; set; }
  public string EmployeeName { get; set; } = string.Empty;
  public string? Position { get; set; }
  public string SiteName { get; set; } = string.Empty;
  public string ScheduleName { get; set; } = string.Empty;
  public string CurrentState { get; set; } = "OUT";
  public string NextEventType { get; set; } = AttendanceEventTypes.In;
  public AttendancePeriodSummaryDto Period { get; set; } = new();
  public IReadOnlyList<AttendanceDayDto> Days { get; set; } = [];
  public IReadOnlyList<AttendanceEventDto> RecentEvents { get; set; } = [];
  public IReadOnlyList<AttendanceExceptionDto> Exceptions { get; set; } = [];
  public IReadOnlyList<AttendanceCorrectionRequestDto> CorrectionRequests { get; set; } = [];
  public IReadOnlyList<LeaveBalanceDto> LeaveBalances { get; set; } = [];
  public IReadOnlyList<LeaveRequestDto> LeaveRequests { get; set; } = [];
  public PrivacyNoticeDto? PrivacyNotice { get; set; }
}

public sealed class PrivacyNoticeDto
{
  public int Id { get; set; }
  public string Version { get; set; } = string.Empty;
  public string Title { get; set; } = string.Empty;
  public string NoticeText { get; set; } = string.Empty;
  public DateOnly EffectiveFrom { get; set; }
  public bool IsActive { get; set; }
  public bool IsAcknowledged { get; set; }
}

public sealed class PrivacyNoticeSaveRequest
{
  public int? Id { get; set; }
  [Required, StringLength(50)] public string Rfc { get; set; } = string.Empty;
  [Required, StringLength(30)] public string Version { get; set; } = string.Empty;
  [Required, StringLength(200)] public string Title { get; set; } = string.Empty;
  [Required, StringLength(8000)] public string NoticeText { get; set; } = string.Empty;
  public DateOnly EffectiveFrom { get; set; } = DateOnly.FromDateTime(DateTime.Today);
  public bool IsActive { get; set; }
}

public sealed class TeamAttendanceDashboardDto
{
  public IReadOnlyList<AttendanceDayDto> Days { get; set; } = [];
  public IReadOnlyList<AttendanceExceptionDto> Exceptions { get; set; } = [];
  public IReadOnlyList<AttendanceCorrectionRequestDto> Corrections { get; set; } = [];
  public IReadOnlyList<LeaveRequestDto> LeaveRequests { get; set; } = [];
  public int EmployeesAtWork { get; set; }
  public int PendingActions { get; set; }
}

public sealed class LeaveTypeDto
{
  public int Id { get; set; }
  public string Code { get; set; } = string.Empty;
  public string Name { get; set; } = string.Empty;
  public bool IsPaid { get; set; }
  public bool RequiresBalance { get; set; }
}

public sealed class LeavePolicyDto
{
  public int Id { get; set; }
  public string Rfc { get; set; } = string.Empty;
  public int LeaveTypeId { get; set; }
  public string LeaveTypeName { get; set; } = string.Empty;
  public string Code { get; set; } = string.Empty;
  public string Name { get; set; } = string.Empty;
  public DateOnly EffectiveFrom { get; set; }
  public DateOnly? EffectiveTo { get; set; }
  public string AccrualMethod { get; set; } = "NONE";
  public decimal? AnnualDays { get; set; }
  public bool AllowPartialDay { get; set; } = true;
  public bool RequiresReview { get; set; } = true;
  public bool IsActive { get; set; } = true;
}

public sealed class LeavePolicySaveRequest
{
  public int? Id { get; set; }
  [Required, StringLength(50)] public string Rfc { get; set; } = string.Empty;
  [Range(1, int.MaxValue)] public int LeaveTypeId { get; set; }
  [Required, StringLength(30)] public string Code { get; set; } = string.Empty;
  [Required, StringLength(150)] public string Name { get; set; } = string.Empty;
  public DateOnly EffectiveFrom { get; set; } = DateOnly.FromDateTime(DateTime.Today);
  public DateOnly? EffectiveTo { get; set; }
  [Required] public string AccrualMethod { get; set; } = "NONE";
  [Range(0, 366)] public decimal? AnnualDays { get; set; }
  public bool AllowPartialDay { get; set; } = true;
  public bool RequiresReview { get; set; } = true;
  public bool IsActive { get; set; } = true;
}

public sealed class LeaveEnrollmentDto
{
  public long Id { get; set; }
  public int EmployeeId { get; set; }
  public string EmployeeName { get; set; } = string.Empty;
  public int LeavePolicyId { get; set; }
  public string PolicyName { get; set; } = string.Empty;
  public DateOnly EffectiveFrom { get; set; }
  public DateOnly? EffectiveTo { get; set; }
}

public sealed class LeaveEnrollmentSaveRequest
{
  public long? Id { get; set; }
  [Required, StringLength(50)] public string Rfc { get; set; } = string.Empty;
  [Range(1, int.MaxValue)] public int EmployeeId { get; set; }
  [Range(1, int.MaxValue)] public int LeavePolicyId { get; set; }
  public DateOnly EffectiveFrom { get; set; } = DateOnly.FromDateTime(DateTime.Today);
  public DateOnly? EffectiveTo { get; set; }
}

public sealed class LeaveBalanceDto
{
  public int LeaveTypeId { get; set; }
  public string LeaveTypeCode { get; set; } = string.Empty;
  public string LeaveTypeName { get; set; } = string.Empty;
  public decimal BalanceDays { get; set; }
}

public sealed class LeaveRequestDto
{
  public long Id { get; set; }
  public int EmployeeId { get; set; }
  public string EmployeeName { get; set; } = string.Empty;
  public int LeaveTypeId { get; set; }
  public string LeaveTypeName { get; set; } = string.Empty;
  public DateOnly StartDate { get; set; }
  public DateOnly EndDate { get; set; }
  public decimal RequestedDays { get; set; }
  public string Reason { get; set; } = string.Empty;
  public string Status { get; set; } = ApprovalStatuses.Pending;
}

public sealed class LeaveRequestCreateRequest
{
  [Required, StringLength(50)] public string Rfc { get; set; } = string.Empty;
  [Range(1, int.MaxValue)] public int LeaveTypeId { get; set; }
  public DateOnly StartDate { get; set; } = DateOnly.FromDateTime(DateTime.Today);
  public DateOnly EndDate { get; set; } = DateOnly.FromDateTime(DateTime.Today);
  [Range(0.25, 366)] public decimal RequestedDays { get; set; } = 1;
  [Required, StringLength(500)] public string Reason { get; set; } = string.Empty;
}

public sealed class LeaveBalanceAdjustmentRequest
{
  [Required, StringLength(50)] public string Rfc { get; set; } = string.Empty;
  [Range(1, int.MaxValue)] public int EmployeeId { get; set; }
  [Range(1, int.MaxValue)] public int LeaveTypeId { get; set; }
  [Range(-366, 366)] public decimal Days { get; set; }
  [Required, StringLength(500)] public string Reason { get; set; } = string.Empty;
}

public sealed class PrenominaPeriodDto
{
  public long Id { get; set; }
  public string Rfc { get; set; } = string.Empty;
  public int PayGroupId { get; set; }
  public string PayGroupName { get; set; } = string.Empty;
  public DateOnly FromDate { get; set; }
  public DateOnly ToDate { get; set; }
  public string Status { get; set; } = PrenominaStatuses.Open;
  public int Version { get; set; } = 1;
  public int EmployeeCount { get; set; }
  public int PendingExceptions { get; set; }
  public int PendingApprovals { get; set; }
  public DateTime? LockedAtUtc { get; set; }
}

public sealed class PrenominaEmployeeApprovalDto
{
  public long PeriodId { get; set; }
  public int EmployeeId { get; set; }
  public string EmployeeName { get; set; } = string.Empty;
  public string PayGroupName { get; set; } = string.Empty;
  public DateOnly FromDate { get; set; }
  public DateOnly ToDate { get; set; }
  public string Status { get; set; } = ApprovalStatuses.Pending;
}

public sealed class PrenominaPeriodCreateRequest
{
  [Required, StringLength(50)] public string Rfc { get; set; } = string.Empty;
  [Range(1, int.MaxValue)] public int PayGroupId { get; set; }
  public DateOnly FromDate { get; set; } = DateOnly.FromDateTime(DateTime.Today);
  public DateOnly ToDate { get; set; } = DateOnly.FromDateTime(DateTime.Today);
}

public sealed class PrenominaValidationDto
{
  public bool CanLock { get; set; }
  public IReadOnlyList<string> Errors { get; set; } = [];
  public IReadOnlyList<string> Warnings { get; set; } = [];
}

public sealed class PrenominaLineDto
{
  public int EmployeeId { get; set; }
  public string EmployeeName { get; set; } = string.Empty;
  public int ScheduledMinutes { get; set; }
  public int WorkedMinutes { get; set; }
  public int OvertimeApprovedMinutes { get; set; }
  public decimal PaidLeaveDays { get; set; }
  public decimal UnpaidLeaveDays { get; set; }
  public int ExceptionCount { get; set; }
}

public sealed class PrenominaExportBundle
{
  public long ExportId { get; set; }
  public string XlsxFileName { get; set; } = string.Empty;
  public byte[] XlsxBytes { get; set; } = [];
  public string ZipFileName { get; set; } = string.Empty;
  public byte[] ZipBytes { get; set; } = [];
  public string XlsxSha256 { get; set; } = string.Empty;
  public string ZipSha256 { get; set; } = string.Empty;
}

public sealed class KioskDeviceDto
{
  public int Id { get; set; }
  public string Name { get; set; } = string.Empty;
  public int SiteId { get; set; }
  public string SiteName { get; set; } = string.Empty;
  public bool IsActive { get; set; }
  public DateTime? LastSeenAtUtc { get; set; }
}

public sealed class KioskPairingCreateRequest
{
  [Required, StringLength(50)] public string Rfc { get; set; } = string.Empty;
  [Range(1, int.MaxValue)] public int SiteId { get; set; }
  [Required, StringLength(150)] public string DeviceName { get; set; } = string.Empty;
}

public sealed class KioskPairingCodeDto
{
  public string Code { get; set; } = string.Empty;
  public DateTime ExpiresAtUtc { get; set; }
}

public sealed class KioskPairResult
{
  public bool Success { get; set; }
  public string Message { get; set; } = string.Empty;
  public string? DeviceToken { get; set; }
  public string? DeviceName { get; set; }
}

public sealed class KioskCredentialCreateRequest
{
  [Required, StringLength(50)] public string Rfc { get; set; } = string.Empty;
  [Range(1, int.MaxValue)] public int EmployeeId { get; set; }
  [Required, MinLength(4), MaxLength(12)] public string Pin { get; set; } = string.Empty;
}

public sealed class KioskCredentialResult
{
  public bool Success { get; set; }
  public string Message { get; set; } = string.Empty;
  public string? BadgeToken { get; set; }
}

public sealed class KioskPunchRequest
{
  [Required] public string BadgeToken { get; set; } = string.Empty;
  [Required] public string Pin { get; set; } = string.Empty;
  [Required] public string EventType { get; set; } = AttendanceEventTypes.In;
  [Required] public string IdempotencyKey { get; set; } = Guid.NewGuid().ToString("N");
  public LocationEvidenceDto Location { get; set; } = new();
}
