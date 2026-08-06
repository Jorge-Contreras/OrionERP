namespace OrionERP.Application.Features.CapitalHumano.Workforce;

public interface IWorkforceConfigurationService
{
  Task<WorkforceSetupSnapshotDto> GetSetupAsync(string rfc, CancellationToken ct = default);
  Task<WorkforceCommandResult> SaveSiteAsync(WorkSiteSaveRequest request, CancellationToken ct = default);
  Task<WorkforceCommandResult> SaveScheduleAsync(ScheduleTemplateSaveRequest request, CancellationToken ct = default);
  Task<WorkforceCommandResult> SavePolicyAsync(AttendancePolicySaveRequest request, CancellationToken ct = default);
  Task<WorkforceCommandResult> SavePayGroupAsync(PayGroupSaveRequest request, CancellationToken ct = default);
  Task<WorkforceCommandResult> SaveWorkAssignmentAsync(EmployeeWorkAssignmentSaveRequest request, CancellationToken ct = default);
  Task<WorkforceCommandResult> SaveSupervisorAssignmentAsync(SupervisorAssignmentSaveRequest request, CancellationToken ct = default);
  Task<WorkforceCommandResult> SaveHolidayAsync(HolidaySaveRequest request, CancellationToken ct = default);
  Task<WorkforceCommandResult> SavePrivacyNoticeAsync(PrivacyNoticeSaveRequest request, CancellationToken ct = default);
  Task<KioskPairingCodeDto> CreateKioskPairingCodeAsync(KioskPairingCreateRequest request, CancellationToken ct = default);
  Task<KioskCredentialResult> CreateKioskCredentialAsync(KioskCredentialCreateRequest request, CancellationToken ct = default);
}

public interface IAttendanceService
{
  Task<EmployeeAttendanceDashboardDto> GetMyDashboardAsync(string rfc, DateOnly? asOfDate = null, CancellationToken ct = default);
  Task<TeamAttendanceDashboardDto> GetTeamDashboardAsync(string rfc, DateOnly fromDate, DateOnly toDate, CancellationToken ct = default);
  Task<AttendancePunchResult> PunchAsync(AttendancePunchRequest request, CancellationToken ct = default);
  Task<WorkforceCommandResult> SubmitCorrectionAsync(AttendanceCorrectionCreateRequest request, CancellationToken ct = default);
  Task<WorkforceCommandResult> DecideExceptionAsync(long exceptionId, string rfc, bool approve, string reason, CancellationToken ct = default);
  Task<WorkforceCommandResult> ReturnExceptionAsync(long exceptionId, string rfc, string reason, CancellationToken ct = default);
  Task<WorkforceCommandResult> DecideCorrectionAsync(long correctionId, string rfc, bool approve, string reason, CancellationToken ct = default);
  Task<WorkforceCommandResult> ReturnCorrectionAsync(long correctionId, string rfc, string reason, CancellationToken ct = default);
  Task<WorkforceCommandResult> DecideOvertimeAsync(long attendanceDayId, string rfc, int approvedMinutes, string reason, CancellationToken ct = default);
  Task<WorkforceCommandResult> AcknowledgePrivacyNoticeAsync(int privacyNoticeId, string rfc, CancellationToken ct = default);
}

public interface IKioskAttendanceService
{
  Task<KioskPairResult> PairAsync(string pairingCode, CancellationToken ct = default);
  Task<AttendancePunchResult> PunchAsync(string deviceToken, KioskPunchRequest request, CancellationToken ct = default);
}

public interface ILeaveManagementService
{
  Task<IReadOnlyList<LeaveTypeDto>> GetLeaveTypesAsync(string rfc, CancellationToken ct = default);
  Task<IReadOnlyList<LeavePolicyDto>> GetPoliciesAsync(string rfc, CancellationToken ct = default);
  Task<IReadOnlyList<LeaveEnrollmentDto>> GetEnrollmentsAsync(string rfc, CancellationToken ct = default);
  Task<IReadOnlyList<LeaveBalanceDto>> GetMyBalancesAsync(string rfc, CancellationToken ct = default);
  Task<IReadOnlyList<LeaveRequestDto>> GetMyRequestsAsync(string rfc, CancellationToken ct = default);
  Task<IReadOnlyList<LeaveRequestDto>> GetTeamRequestsAsync(string rfc, CancellationToken ct = default);
  Task<WorkforceCommandResult> SubmitRequestAsync(LeaveRequestCreateRequest request, CancellationToken ct = default);
  Task<WorkforceCommandResult> DecideRequestAsync(long requestId, string rfc, bool approve, string reason, CancellationToken ct = default);
  Task<WorkforceCommandResult> AdjustBalanceAsync(LeaveBalanceAdjustmentRequest request, CancellationToken ct = default);
  Task<WorkforceCommandResult> SavePolicyAsync(LeavePolicySaveRequest request, CancellationToken ct = default);
  Task<WorkforceCommandResult> SaveEnrollmentAsync(LeaveEnrollmentSaveRequest request, CancellationToken ct = default);
  Task<WorkforceCommandResult> ProcessVacationAccrualsAsync(string rfc, DateOnly asOfDate, CancellationToken ct = default);
}

public interface IPrenominaService
{
  Task<IReadOnlyList<PrenominaPeriodDto>> GetPeriodsAsync(string rfc, CancellationToken ct = default);
  Task<IReadOnlyList<PrenominaEmployeeApprovalDto>> GetPendingApprovalsAsync(string rfc, CancellationToken ct = default);
  Task<IReadOnlyList<PrenominaLineDto>> GetLinesAsync(long periodId, string rfc, CancellationToken ct = default);
  Task<WorkforceCommandResult> CreatePeriodAsync(PrenominaPeriodCreateRequest request, CancellationToken ct = default);
  Task<PrenominaValidationDto> ValidateAsync(long periodId, string rfc, CancellationToken ct = default);
  Task<WorkforceCommandResult> ApproveEmployeeAsync(long periodId, int employeeId, string rfc, CancellationToken ct = default);
  Task<WorkforceCommandResult> LockAsync(long periodId, string rfc, CancellationToken ct = default);
  Task<WorkforceCommandResult> ReopenAsync(long periodId, string rfc, string reason, CancellationToken ct = default);
}

public interface IPrenominaExportService
{
  Task<PrenominaExportBundle> GenerateAsync(long periodId, string rfc, CancellationToken ct = default);
  Task<PrenominaExportBundle?> GetAsync(long exportId, string rfc, CancellationToken ct = default);
}
