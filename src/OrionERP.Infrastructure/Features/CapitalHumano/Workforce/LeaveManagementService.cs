using System.Data;
using Dapper;
using OrionERP.Application.Common;
using OrionERP.Application.Features.CapitalHumano.Workforce;

namespace OrionERP.Infrastructure.Features.CapitalHumano.Workforce;

public sealed class LeaveManagementService : WorkforceServiceBase, ILeaveManagementService
{
  public LeaveManagementService(IDbConnectionFactory connectionFactory, ICurrentEmployeeAccessor currentEmployeeAccessor)
    : base(connectionFactory, currentEmployeeAccessor) { }

  public async Task<IReadOnlyList<LeaveTypeDto>> GetLeaveTypesAsync(string rfc, CancellationToken ct = default)
  {
    var normalizedRfc = NormalizeRfc(rfc);
    await RequireActorAsync(normalizedRfc, false, ct);
    using var connection = CreateOpenConnection();
    return (await connection.QueryAsync<LeaveTypeDto>(new CommandDefinition(
      "SELECT Id,Code,[Name],IsPaid,RequiresBalance FROM rh.LeaveType WHERE Rfc=@Rfc AND IsActive=1 ORDER BY [Name];",
      new { Rfc = normalizedRfc }, cancellationToken: ct))).AsList();
  }

  public async Task<IReadOnlyList<LeavePolicyDto>> GetPoliciesAsync(string rfc, CancellationToken ct = default)
  {
    var normalizedRfc = NormalizeRfc(rfc);
    await RequireActorAsync(normalizedRfc, false, ct, "CapitalHumanoAdmin", "CapitalHumanoNomina");
    using var connection = CreateOpenConnection();
    return (await connection.QueryAsync<LeavePolicyDto>(new CommandDefinition(
      """
      SELECT p.Id,p.Rfc,p.LeaveTypeId,t.[Name] LeaveTypeName,p.Code,p.[Name],p.EffectiveFrom,p.EffectiveTo,
        p.AccrualMethod,p.AnnualDays,p.AllowPartialDay,p.RequiresReview,p.IsActive
      FROM rh.LeavePolicy p INNER JOIN rh.LeaveType t ON t.Id=p.LeaveTypeId
      WHERE p.Rfc=@Rfc ORDER BY p.EffectiveFrom DESC,p.[Name];
      """, new { Rfc = normalizedRfc }, cancellationToken: ct))).AsList();
  }

  public async Task<IReadOnlyList<LeaveEnrollmentDto>> GetEnrollmentsAsync(string rfc, CancellationToken ct = default)
  {
    var normalizedRfc = NormalizeRfc(rfc);
    await RequireActorAsync(normalizedRfc, false, ct, "CapitalHumanoAdmin", "CapitalHumanoNomina");
    using var connection = CreateOpenConnection();
    return (await connection.QueryAsync<LeaveEnrollmentDto>(new CommandDefinition(
      """
      SELECT e.Id,e.EmployeeId,COALESCE(NULLIF(ch.NombreCorto,''),CONCAT(ch.Nombre,' ',ch.ApellidoPaterno)) EmployeeName,
        e.LeavePolicyId,p.[Name] PolicyName,e.EffectiveFrom,e.EffectiveTo
      FROM rh.LeaveEnrollment e
      INNER JOIN rh.LeavePolicy p ON p.Id=e.LeavePolicyId AND p.Rfc=e.Rfc
      INNER JOIN dbo.Capital_Humano ch ON ch.ID=e.EmployeeId AND ch.RFC=e.Rfc
      WHERE e.Rfc=@Rfc ORDER BY e.EffectiveFrom DESC,EmployeeName,p.[Name];
      """, new { Rfc = normalizedRfc }, cancellationToken: ct))).AsList();
  }

  public async Task<IReadOnlyList<LeaveBalanceDto>> GetMyBalancesAsync(string rfc, CancellationToken ct = default)
  {
    var normalizedRfc = NormalizeRfc(rfc);
    var actor = await RequireActorAsync(normalizedRfc, true, ct);
    using var connection = CreateOpenConnection();
    return await AttendanceService.QueryBalancesAsync(connection, normalizedRfc, actor.EmployeeId!.Value, ct);
  }

  public async Task<IReadOnlyList<LeaveRequestDto>> GetMyRequestsAsync(string rfc, CancellationToken ct = default)
  {
    var normalizedRfc = NormalizeRfc(rfc);
    var actor = await RequireActorAsync(normalizedRfc, true, ct);
    using var connection = CreateOpenConnection();
    return await AttendanceService.QueryLeaveRequestsAsync(connection, normalizedRfc, actor.EmployeeId!.Value, ct);
  }

  public async Task<IReadOnlyList<LeaveRequestDto>> GetTeamRequestsAsync(string rfc, CancellationToken ct = default)
  {
    var normalizedRfc = NormalizeRfc(rfc);
    var actor = await RequireActorAsync(normalizedRfc, false, ct, "CapitalHumanoAdmin", "CapitalHumanoSupervisor", "CapitalHumanoNomina");
    using var connection = CreateOpenConnection();
    var elevated = actor.IsInRole("Administrador", "CapitalHumanoAdmin", "CapitalHumanoNomina");
    if (!elevated && !actor.EmployeeId.HasValue)
      throw new UnauthorizedAccessException("El supervisor debe estar ligado a un empleado para consultar su equipo.");
    var supervisorId = elevated ? (int?)null : actor.EmployeeId;
    return (await connection.QueryAsync<LeaveRequestDto>(new CommandDefinition(
      """
      SELECT l.Id,l.EmployeeId,COALESCE(NULLIF(ch.NombreCorto,''),CONCAT(ch.Nombre,' ',ch.ApellidoPaterno)) EmployeeName,
        l.LeaveTypeId,t.[Name] LeaveTypeName,l.StartDate,l.EndDate,l.RequestedDays,l.Reason,l.[Status]
      FROM rh.LeaveRequest l INNER JOIN dbo.Capital_Humano ch ON ch.ID=l.EmployeeId INNER JOIN rh.LeaveType t ON t.Id=l.LeaveTypeId
      WHERE l.Rfc=@Rfc AND (@SupervisorId IS NULL OR EXISTS
        (SELECT 1 FROM rh.SupervisorAssignment sa WHERE sa.Rfc=l.Rfc AND sa.EmployeeId=l.EmployeeId
         AND sa.SupervisorEmployeeId=@SupervisorId AND sa.EffectiveFrom<=l.StartDate AND (sa.EffectiveTo IS NULL OR sa.EffectiveTo>=l.StartDate)))
      ORDER BY CASE l.[Status] WHEN 'PENDING' THEN 0 ELSE 1 END,l.StartDate DESC;
      """, new { Rfc = normalizedRfc, SupervisorId = supervisorId }, cancellationToken: ct))).AsList();
  }

  public async Task<WorkforceCommandResult> SubmitRequestAsync(LeaveRequestCreateRequest request, CancellationToken ct = default)
  {
    var rfc = NormalizeRfc(request.Rfc);
    var actor = await RequireActorAsync(rfc, true, ct);
    if (request.EndDate < request.StartDate || request.RequestedDays <= 0)
      return WorkforceCommandResult.Fail("Las fechas o dias solicitados no son validos.");
    var calendarDays = request.EndDate.DayNumber - request.StartDate.DayNumber + 1;
    if (request.RequestedDays > calendarDays)
      return WorkforceCommandResult.Fail("Los dias solicitados exceden el periodo seleccionado.");
    using var connection = CreateOpenConnection();
    var id = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
      """
      IF NOT EXISTS (SELECT 1 FROM rh.LeaveType WHERE Id=@LeaveTypeId AND Rfc=@Rfc AND IsActive=1)
        THROW 51000,'El tipo de ausencia no es valido.',1;
      DECLARE @PolicyId int=(
        SELECT TOP(1) p.Id FROM rh.LeaveEnrollment enrollment
        INNER JOIN rh.LeavePolicy p ON p.Id=enrollment.LeavePolicyId AND p.Rfc=enrollment.Rfc
        WHERE enrollment.Rfc=@Rfc AND enrollment.EmployeeId=@EmployeeId AND p.LeaveTypeId=@LeaveTypeId
          AND p.IsActive=1 AND p.EffectiveFrom<=@StartDate AND (p.EffectiveTo IS NULL OR p.EffectiveTo>=@EndDate)
          AND enrollment.EffectiveFrom<=@StartDate AND (enrollment.EffectiveTo IS NULL OR enrollment.EffectiveTo>=@EndDate)
        ORDER BY p.EffectiveFrom DESC,p.Id DESC);
      IF @PolicyId IS NULL THROW 51000,'El empleado no tiene una politica de ausencia vigente para este tipo.',1;
      IF @RequestedDays<>FLOOR(@RequestedDays) AND EXISTS(SELECT 1 FROM rh.LeavePolicy WHERE Id=@PolicyId AND AllowPartialDay=0)
        THROW 51000,'La politica vigente no permite ausencias parciales.',1;
      IF EXISTS(SELECT 1 FROM rh.LeaveRequest WHERE Rfc=@Rfc AND EmployeeId=@EmployeeId
        AND [Status] IN ('PENDING','APPROVED','RETURNED') AND StartDate<=@EndDate AND EndDate>=@StartDate)
        THROW 51000,'Ya existe una solicitud de ausencia que se traslapa con este periodo.',1;
      INSERT INTO rh.LeaveRequest
        (Rfc,EmployeeId,LeaveTypeId,StartDate,EndDate,RequestedDays,Reason,CreatedBy)
      VALUES (@Rfc,@EmployeeId,@LeaveTypeId,@StartDate,@EndDate,@RequestedDays,@Reason,@Actor);
      SELECT CAST(SCOPE_IDENTITY() AS bigint);
      """, new { Rfc = rfc, EmployeeId = actor.EmployeeId!.Value, request.LeaveTypeId, request.StartDate, request.EndDate, request.RequestedDays, Reason = request.Reason.Trim(), Actor = actor.UserName }, cancellationToken: ct));
    return WorkforceCommandResult.Ok("Solicitud de ausencia enviada.", id);
  }

  public async Task<WorkforceCommandResult> DecideRequestAsync(long requestId, string rfc, bool approve, string reason, CancellationToken ct = default)
  {
    var normalizedRfc = NormalizeRfc(rfc);
    var actor = await RequireActorAsync(normalizedRfc, false, ct, "CapitalHumanoAdmin", "CapitalHumanoSupervisor");
    using var connection = CreateOpenConnection();
    using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
    var request = await connection.QuerySingleOrDefaultAsync<LeaveDecisionRow>(new CommandDefinition(
      """
      SELECT l.EmployeeId,l.LeaveTypeId,l.StartDate,l.EndDate,l.RequestedDays,t.RequiresBalance
      FROM rh.LeaveRequest l WITH(UPDLOCK,HOLDLOCK) INNER JOIN rh.LeaveType t ON t.Id=l.LeaveTypeId
      WHERE l.Id=@Id AND l.Rfc=@Rfc AND l.[Status]='PENDING';
      """, new { Id = requestId, Rfc = normalizedRfc }, transaction, cancellationToken: ct));
    if (request is null || !await CanManageEmployeeAsync(connection, transaction, actor, normalizedRfc, request.EmployeeId, request.StartDate, ct))
    {
      transaction.Rollback();
      return WorkforceCommandResult.Fail("La solicitud no existe o no pertenece a su equipo.");
    }
    if (approve && request.RequiresBalance)
    {
      var balance = await connection.ExecuteScalarAsync<decimal>(new CommandDefinition(
        "SELECT ISNULL(SUM(Days),0) FROM rh.LeaveBalanceLedger WITH(UPDLOCK,HOLDLOCK) WHERE Rfc=@Rfc AND EmployeeId=@EmployeeId AND LeaveTypeId=@LeaveTypeId;",
        new { Rfc = normalizedRfc, request.EmployeeId, request.LeaveTypeId }, transaction, cancellationToken: ct));
      if (balance < request.RequestedDays)
      {
        transaction.Rollback();
        return WorkforceCommandResult.Fail("El saldo disponible no cubre la solicitud.");
      }
      await connection.ExecuteAsync(new CommandDefinition(
        """
        INSERT INTO rh.LeaveBalanceLedger
          (Rfc,EmployeeId,LeaveTypeId,TransactionDate,Days,TransactionType,SourceKey,Reason,CreatedBy)
        VALUES (@Rfc,@EmployeeId,@LeaveTypeId,@StartDate,-@RequestedDays,'LEAVE_USED',@SourceKey,@Reason,@Actor);
        """, new { Rfc = normalizedRfc, request.EmployeeId, request.LeaveTypeId, request.StartDate, request.RequestedDays, SourceKey = $"leave:{requestId}", Reason = $"Solicitud {requestId} aprobada", Actor = actor.UserName }, transaction, cancellationToken: ct));
    }
    await connection.ExecuteAsync(new CommandDefinition(
      "UPDATE rh.LeaveRequest SET [Status]=@Status,DecisionReason=@Reason,DecidedAtUtc=SYSUTCDATETIME(),DecidedBy=@Actor WHERE Id=@Id;",
      new { Id = requestId, Status = approve ? ApprovalStatuses.Approved : ApprovalStatuses.Rejected, Reason = reason.Trim(), Actor = actor.UserName }, transaction, cancellationToken: ct));
    await WriteAuditAsync(connection, transaction, normalizedRfc, request.EmployeeId, "LeaveRequest", requestId, approve ? "APPROVED" : "REJECTED", reason, actor.UserName, ct);
    transaction.Commit();
    return WorkforceCommandResult.Ok(approve ? "Ausencia aprobada." : "Ausencia rechazada.", requestId);
  }

  public async Task<WorkforceCommandResult> AdjustBalanceAsync(LeaveBalanceAdjustmentRequest request, CancellationToken ct = default)
  {
    var rfc = NormalizeRfc(request.Rfc);
    var actor = await RequireActorAsync(rfc, false, ct, "CapitalHumanoAdmin");
    if (request.Days == 0) return WorkforceCommandResult.Fail("El ajuste debe ser distinto de cero.");
    using var connection = CreateOpenConnection();
    var id = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
      """
      IF NOT EXISTS (SELECT 1 FROM dbo.Capital_Humano WHERE ID=@EmployeeId AND RFC=@Rfc)
        THROW 51000,'El empleado no pertenece al RFC.',1;
      IF NOT EXISTS (SELECT 1 FROM rh.LeaveType WHERE Id=@LeaveTypeId AND Rfc=@Rfc)
        THROW 51000,'El tipo de ausencia no pertenece al RFC.',1;
      INSERT INTO rh.LeaveBalanceLedger
        (Rfc,EmployeeId,LeaveTypeId,TransactionDate,Days,TransactionType,Reason,CreatedBy)
      VALUES (@Rfc,@EmployeeId,@LeaveTypeId,CAST(GETDATE() AS date),@Days,'MANUAL_ADJUSTMENT',@Reason,@Actor);
      SELECT CAST(SCOPE_IDENTITY() AS bigint);
      """, new { Rfc = rfc, request.EmployeeId, request.LeaveTypeId, request.Days, Reason = request.Reason.Trim(), Actor = actor.UserName }, cancellationToken: ct));
    return WorkforceCommandResult.Ok("Saldo ajustado con una transaccion auditable.", id);
  }

  public async Task<WorkforceCommandResult> SavePolicyAsync(LeavePolicySaveRequest request, CancellationToken ct = default)
  {
    var rfc = NormalizeRfc(request.Rfc);
    var actor = await RequireActorAsync(rfc, false, ct, "CapitalHumanoAdmin");
    var method = request.AccrualMethod.Trim().ToUpperInvariant();
    if (method is not ("NONE" or "ANNUAL" or "MEXICO_STATUTORY"))
      return WorkforceCommandResult.Fail("El metodo de acumulacion no es valido.");
    if (request.EffectiveTo < request.EffectiveFrom)
      return WorkforceCommandResult.Fail("La vigencia de la politica no es valida.");
    if (method == "ANNUAL" && request.AnnualDays is null or <= 0)
      return WorkforceCommandResult.Fail("Indica los dias anuales para una politica anual.");
    using var connection = CreateOpenConnection();
    using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
    var id = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
      """
      IF NOT EXISTS(SELECT 1 FROM rh.LeaveType WHERE Id=@LeaveTypeId AND Rfc=@Rfc)
        THROW 51000,'El tipo de ausencia no pertenece al RFC.',1;
      IF @Id IS NULL
      BEGIN
        INSERT INTO rh.LeavePolicy
          (Rfc,LeaveTypeId,Code,[Name],EffectiveFrom,EffectiveTo,AccrualMethod,AnnualDays,AllowPartialDay,RequiresReview,IsActive,CreatedBy)
        VALUES
          (@Rfc,@LeaveTypeId,@Code,@Name,@EffectiveFrom,@EffectiveTo,@AccrualMethod,@AnnualDays,@AllowPartialDay,@RequiresReview,@IsActive,@Actor);
        SELECT CAST(SCOPE_IDENTITY() AS int);
      END
      ELSE
      BEGIN
        UPDATE rh.LeavePolicy SET LeaveTypeId=@LeaveTypeId,Code=@Code,[Name]=@Name,EffectiveFrom=@EffectiveFrom,
          EffectiveTo=@EffectiveTo,AccrualMethod=@AccrualMethod,AnnualDays=@AnnualDays,AllowPartialDay=@AllowPartialDay,
          RequiresReview=@RequiresReview,IsActive=@IsActive WHERE Id=@Id AND Rfc=@Rfc;
        SELECT CASE WHEN @@ROWCOUNT=0 THEN 0 ELSE @Id END;
      END;
      """, new { request.Id, Rfc = rfc, request.LeaveTypeId, Code = request.Code.Trim().ToUpperInvariant(), Name = request.Name.Trim(), request.EffectiveFrom, request.EffectiveTo, AccrualMethod = method, request.AnnualDays, request.AllowPartialDay, request.RequiresReview, request.IsActive, Actor = actor.UserName }, transaction, cancellationToken: ct));
    if (id <= 0) { transaction.Rollback(); return WorkforceCommandResult.Fail("No se encontro la politica."); }
    await WriteAuditAsync(connection, transaction, rfc, null, "LeavePolicy", id, "SAVED", request.Code, actor.UserName, ct);
    transaction.Commit();
    return WorkforceCommandResult.Ok("Politica de ausencia guardada.", id);
  }

  public async Task<WorkforceCommandResult> SaveEnrollmentAsync(LeaveEnrollmentSaveRequest request, CancellationToken ct = default)
  {
    var rfc = NormalizeRfc(request.Rfc);
    var actor = await RequireActorAsync(rfc, false, ct, "CapitalHumanoAdmin");
    if (request.EffectiveTo < request.EffectiveFrom)
      return WorkforceCommandResult.Fail("La vigencia de la inscripcion no es valida.");
    using var connection = CreateOpenConnection();
    using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
    var id = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
      """
      IF NOT EXISTS(SELECT 1 FROM dbo.Capital_Humano WHERE ID=@EmployeeId AND RFC=@Rfc)
        THROW 51000,'El empleado no pertenece al RFC.',1;
      IF NOT EXISTS(SELECT 1 FROM rh.LeavePolicy WHERE Id=@LeavePolicyId AND Rfc=@Rfc AND IsActive=1)
        THROW 51000,'La politica no pertenece al RFC o no esta activa.',1;
      IF EXISTS(SELECT 1 FROM rh.LeaveEnrollment enrollment INNER JOIN rh.LeavePolicy existingPolicy ON existingPolicy.Id=enrollment.LeavePolicyId
        INNER JOIN rh.LeavePolicy requestedPolicy ON requestedPolicy.Id=@LeavePolicyId
        WHERE enrollment.Rfc=@Rfc AND enrollment.EmployeeId=@EmployeeId AND existingPolicy.LeaveTypeId=requestedPolicy.LeaveTypeId
          AND (@Id IS NULL OR enrollment.Id<>@Id)
          AND enrollment.EffectiveFrom<=ISNULL(@EffectiveTo,'99991231') AND ISNULL(enrollment.EffectiveTo,'99991231')>=@EffectiveFrom)
        THROW 51000,'Ya existe una inscripcion traslapada para este tipo de ausencia.',1;
      IF @Id IS NULL
      BEGIN
        INSERT INTO rh.LeaveEnrollment(Rfc,EmployeeId,LeavePolicyId,EffectiveFrom,EffectiveTo,CreatedBy)
        VALUES(@Rfc,@EmployeeId,@LeavePolicyId,@EffectiveFrom,@EffectiveTo,@Actor);
        SELECT CAST(SCOPE_IDENTITY() AS bigint);
      END
      ELSE
      BEGIN
        UPDATE rh.LeaveEnrollment SET EmployeeId=@EmployeeId,LeavePolicyId=@LeavePolicyId,EffectiveFrom=@EffectiveFrom,EffectiveTo=@EffectiveTo
        WHERE Id=@Id AND Rfc=@Rfc;
        SELECT CASE WHEN @@ROWCOUNT=0 THEN CAST(0 AS bigint) ELSE @Id END;
      END;
      """, new { request.Id, Rfc = rfc, request.EmployeeId, request.LeavePolicyId, request.EffectiveFrom, request.EffectiveTo, Actor = actor.UserName }, transaction, cancellationToken: ct));
    if (id <= 0) { transaction.Rollback(); return WorkforceCommandResult.Fail("No se encontro la inscripcion."); }
    await WriteAuditAsync(connection, transaction, rfc, request.EmployeeId, "LeaveEnrollment", id, "SAVED", null, actor.UserName, ct);
    transaction.Commit();
    return WorkforceCommandResult.Ok("Inscripcion de ausencia guardada.", id);
  }

  public async Task<WorkforceCommandResult> ProcessVacationAccrualsAsync(string rfc, DateOnly asOfDate, CancellationToken ct = default)
  {
    var normalizedRfc = NormalizeRfc(rfc);
    var actor = await RequireActorAsync(normalizedRfc, false, ct, "CapitalHumanoAdmin");
    using var connection = CreateOpenConnection();
    using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
    var leaveTypeId = await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
      "SELECT Id FROM rh.LeaveType WHERE Rfc=@Rfc AND Code='VACACIONES' AND IsActive=1;", new { Rfc = normalizedRfc }, transaction, cancellationToken: ct));
    if (!leaveTypeId.HasValue)
    {
      transaction.Rollback();
      return WorkforceCommandResult.Fail("No existe el tipo de ausencia VACACIONES.");
    }
    var employees = (await connection.QueryAsync<HireRow>(new CommandDefinition(
      "SELECT ID EmployeeId,Fecha_Alta HireDate FROM dbo.Capital_Humano WHERE RFC=@Rfc AND Fecha_Alta IS NOT NULL AND UPPER(LTRIM(RTRIM(ISNULL([Status],''))))='ACTIVO';",
      new { Rfc = normalizedRfc }, transaction, cancellationToken: ct))).AsList();
    var inserted = 0;
    foreach (var employee in employees)
    {
      var hire = DateOnly.FromDateTime(employee.HireDate);
      var years = asOfDate.Year - hire.Year;
      if (asOfDate < hire.AddYears(years)) years--;
      for (var year = 1; year <= years; year++)
      {
        var days = MexicoVacationAccrualCalculator.GetAnnualEntitlementDays(year);
        inserted += await connection.ExecuteAsync(new CommandDefinition(
          """
          IF NOT EXISTS (SELECT 1 FROM rh.LeaveBalanceLedger WITH(UPDLOCK,HOLDLOCK) WHERE Rfc=@Rfc AND EmployeeId=@EmployeeId AND LeaveTypeId=@LeaveTypeId AND SourceKey=@SourceKey)
            INSERT INTO rh.LeaveBalanceLedger (Rfc,EmployeeId,LeaveTypeId,TransactionDate,Days,TransactionType,SourceKey,Reason,CreatedBy)
            VALUES (@Rfc,@EmployeeId,@LeaveTypeId,@Anniversary,@Days,'VACATION_ACCRUAL',@SourceKey,@Reason,@Actor);
          """, new { Rfc = normalizedRfc, employee.EmployeeId, LeaveTypeId = leaveTypeId.Value, Anniversary = hire.AddYears(year), Days = days, SourceKey = $"vacation:{employee.EmployeeId}:{year}", Reason = $"Aniversario laboral {year}", Actor = actor.UserName }, transaction, cancellationToken: ct));
      }
    }
    await WriteAuditAsync(connection, transaction, normalizedRfc, null, "LeaveAccrual", asOfDate, "PROCESSED", $"{inserted} movimientos nuevos", actor.UserName, ct);
    transaction.Commit();
    return WorkforceCommandResult.Ok($"Acumulacion procesada: {inserted} movimientos nuevos.");
  }

  private sealed class LeaveDecisionRow { public int EmployeeId { get; set; } public int LeaveTypeId { get; set; } public DateOnly StartDate { get; set; } public DateOnly EndDate { get; set; } public decimal RequestedDays { get; set; } public bool RequiresBalance { get; set; } }
  private sealed class HireRow { public int EmployeeId { get; set; } public DateTime HireDate { get; set; } }
}
