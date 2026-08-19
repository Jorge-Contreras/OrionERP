/*
  Adds the minimum reviewed, fictional workforce reference data required by
  the RH-CAPITAL-HUMANO course. Run only through Sanitize-OrionTraining.ps1,
  after the synthetic cohort has been provisioned and before final attestation.

  This batch deliberately creates no privileged role, kiosk credential,
  pre-approved request, or real location evidence. Trainees acknowledge the
  fictional privacy notice, then create their own attendance, correction, and
  leave practice records through OrionERP.
*/

SET NOCOUNT ON;
SET XACT_ABORT ON;

IF DB_NAME() COLLATE Latin1_General_100_BIN2 <> N'Orion_Training' COLLATE Latin1_General_100_BIN2
  THROW 51820, 'SCENARIO PROVISIONING BLOCKED: the active database is not exactly Orion_Training.', 1;

IF ISNULL(TRY_CONVERT(nvarchar(64), SESSION_CONTEXT(N'OrionTrainingSanitizerApply')), N'') <> N'20260817-v1'
  THROW 51821, 'SCENARIO PROVISIONING BLOCKED: the guarded sanitizer session did not authorize this batch.', 1;

DECLARE @RequiredTables table
(
  SchemaName sysname NOT NULL,
  TableName sysname NOT NULL,
  PRIMARY KEY (SchemaName, TableName)
);

INSERT @RequiredTables (SchemaName, TableName)
VALUES
  (N'dbo', N'Capital_Humano'),
  (N'rh', N'WorkSite'),
  (N'rh', N'ScheduleTemplate'),
  (N'rh', N'ScheduleDay'),
  (N'rh', N'AttendancePolicy'),
  (N'rh', N'PayGroup'),
  (N'rh', N'EmployeeWorkAssignment'),
  (N'rh', N'LeaveType'),
  (N'rh', N'LeavePolicy'),
  (N'rh', N'LeaveEnrollment'),
  (N'rh', N'LeaveBalanceLedger'),
  (N'rh', N'PrivacyNotice');

IF EXISTS
(
  SELECT 1
  FROM @RequiredTables required
  WHERE OBJECT_ID(QUOTENAME(required.SchemaName) + N'.' + QUOTENAME(required.TableName), N'U') IS NULL
)
  THROW 51822, 'SCENARIO PROVISIONING BLOCKED: a required workforce table is missing.', 1;

IF COL_LENGTH(N'rh.WorkSite', N'Rfc') IS NULL
   OR COL_LENGTH(N'rh.ScheduleDay', N'DayOfWeek') IS NULL
   OR COL_LENGTH(N'rh.AttendancePolicy', N'LocationRequired') IS NULL
   OR COL_LENGTH(N'rh.EmployeeWorkAssignment', N'EmployeeId') IS NULL
   OR COL_LENGTH(N'rh.LeavePolicy', N'LeaveTypeId') IS NULL
   OR COL_LENGTH(N'rh.LeaveEnrollment', N'EmployeeId') IS NULL
   OR COL_LENGTH(N'rh.LeaveBalanceLedger', N'SourceKey') IS NULL
   OR COL_LENGTH(N'rh.PrivacyNotice', N'NoticeText') IS NULL
  THROW 51823, 'SCENARIO PROVISIONING BLOCKED: the workforce schema shape was not reviewed.', 1;

DECLARE @TrainingRfc varchar(50) = 'XAXX010101000';
DECLARE @ScenarioActor nvarchar(256) = N'OrionERP Training scenario v1';

IF (SELECT COUNT(*) FROM dbo.Capital_Humano WHERE ID IN (990001, 990002, 990003)) <> 3
   OR EXISTS
      (
        SELECT 1
        FROM dbo.Capital_Humano
        WHERE ID IN (990001, 990002, 990003)
          AND
          (
            RFC COLLATE Latin1_General_100_BIN2 <> @TrainingRfc COLLATE Latin1_General_100_BIN2
            OR Usuario_Acceso NOT LIKE N'%@training.orion.local'
            OR Tipo_Capital_Humano COLLATE Latin1_General_100_BIN2 <> N'SINTÉTICO' COLLATE Latin1_General_100_BIN2
          )
      )
  THROW 51824, 'SCENARIO PROVISIONING BLOCKED: the expected fictional instructor and trainees are missing.', 1;

/* The sanitizer must leave the complete RH schema empty before this include. */
IF EXISTS
(
  SELECT 1
  FROM sys.tables tableInfo
  JOIN sys.schemas schemaInfo ON schemaInfo.schema_id = tableInfo.schema_id
  JOIN sys.dm_db_partition_stats partitionInfo ON partitionInfo.object_id = tableInfo.object_id
  WHERE schemaInfo.name = N'rh'
    AND tableInfo.is_ms_shipped = 0
    AND partitionInfo.index_id IN (0, 1)
  GROUP BY tableInfo.object_id
  HAVING SUM(partitionInfo.row_count) <> 0
)
  THROW 51825, 'SCENARIO PROVISIONING BLOCKED: the RH schema is not empty after sanitization.', 1;

BEGIN TRY
  BEGIN TRANSACTION;

  INSERT rh.WorkSite
    (Rfc, Code, [Name], TimeZoneId, Latitude, Longitude, RadiusMeters,
     MaxAccuracyMeters, IsActive, CreatedBy)
  VALUES
    (@TrainingRfc, 'TRN-SITE-01', N'TRAINING · SITIO FICTICIO',
     'America/Mexico_City', 0, 0, 100, 100, 1, @ScenarioActor);

  DECLARE @WorkSiteId int = CONVERT(int, SCOPE_IDENTITY());

  INSERT rh.ScheduleTemplate (Rfc, Code, [Name], IsActive, CreatedBy)
  VALUES
    (@TrainingRfc, 'TRN-DIARIO', N'TRAINING · HORARIO FICTICIO DIARIO', 1, @ScenarioActor);

  DECLARE @ScheduleTemplateId int = CONVERT(int, SCOPE_IDENTITY());

  INSERT rh.ScheduleDay
    (ScheduleTemplateId, DayOfWeek, IsWorkingDay, StartTime, EndTime, UnpaidBreakMinutes)
  SELECT @ScheduleTemplateId, source.DayOfWeek, 1, CONVERT(time(0), '09:00:00'),
         CONVERT(time(0), '17:00:00'), 60
  FROM (VALUES (0), (1), (2), (3), (4), (5), (6)) source(DayOfWeek);

  INSERT rh.AttendancePolicy
    (Rfc, Code, [Name], EffectiveFrom, EffectiveTo, WeeklyOrdinaryMinutes,
     WeeklyDoubleOvertimeMinutes, WeeklyTripleOvertimeMinutes, GraceMinutes,
     RoundingMinutes, LocationRequired, IsActive, RequiresReview, CreatedBy)
  VALUES
    (@TrainingRfc, 'TRN-ASISTENCIA', N'TRAINING · ASISTENCIA FICTICIA',
     CONVERT(date, '2026-01-01'), NULL, 2880, 540, 240, 5, 1,
     0, 1, 1, @ScenarioActor);

  DECLARE @AttendancePolicyId int = CONVERT(int, SCOPE_IDENTITY());

  INSERT rh.PayGroup (Rfc, Code, [Name], Frequency, IsActive, CreatedBy)
  VALUES
    (@TrainingRfc, 'TRN-SEMANAL', N'TRAINING · GRUPO FICTICIO', 'WEEKLY', 1, @ScenarioActor);

  DECLARE @PayGroupId int = CONVERT(int, SCOPE_IDENTITY());

  INSERT rh.EmployeeWorkAssignment
    (Rfc, EmployeeId, SiteId, ScheduleTemplateId, AttendancePolicyId,
     PayGroupId, EffectiveFrom, EffectiveTo, CreatedBy)
  VALUES
    (@TrainingRfc, 990002, @WorkSiteId, @ScheduleTemplateId, @AttendancePolicyId,
     @PayGroupId, CONVERT(date, '2026-01-05'), NULL, @ScenarioActor),
    (@TrainingRfc, 990003, @WorkSiteId, @ScheduleTemplateId, @AttendancePolicyId,
     @PayGroupId, CONVERT(date, '2026-01-05'), NULL, @ScenarioActor);

  INSERT rh.LeaveType (Rfc, Code, [Name], IsPaid, RequiresBalance, IsActive)
  VALUES
    (@TrainingRfc, 'TRN-PERSONAL', N'TRAINING · PERMISO PERSONAL FICTICIO', 1, 1, 1),
    (@TrainingRfc, 'TRN-SIN-GOCE', N'TRAINING · PERMISO SIN GOCE FICTICIO', 0, 0, 1);

  DECLARE @PersonalLeaveTypeId int =
    (SELECT Id FROM rh.LeaveType WHERE Rfc = @TrainingRfc AND Code = 'TRN-PERSONAL');
  DECLARE @UnpaidLeaveTypeId int =
    (SELECT Id FROM rh.LeaveType WHERE Rfc = @TrainingRfc AND Code = 'TRN-SIN-GOCE');

  INSERT rh.LeavePolicy
    (Rfc, LeaveTypeId, Code, [Name], EffectiveFrom, EffectiveTo, AccrualMethod,
     AnnualDays, AllowPartialDay, RequiresReview, IsActive, CreatedBy)
  VALUES
    (@TrainingRfc, @PersonalLeaveTypeId, 'TRN-PERSONAL-POL',
     N'TRAINING · POLÍTICA PERSONAL FICTICIA', CONVERT(date, '2026-01-01'),
     NULL, 'NONE', NULL, 1, 1, 1, @ScenarioActor),
    (@TrainingRfc, @UnpaidLeaveTypeId, 'TRN-SIN-GOCE-POL',
     N'TRAINING · POLÍTICA SIN GOCE FICTICIA', CONVERT(date, '2026-01-01'),
     NULL, 'NONE', NULL, 1, 1, 1, @ScenarioActor);

  DECLARE @PersonalLeavePolicyId int =
    (SELECT Id FROM rh.LeavePolicy WHERE Rfc = @TrainingRfc AND Code = 'TRN-PERSONAL-POL');
  DECLARE @UnpaidLeavePolicyId int =
    (SELECT Id FROM rh.LeavePolicy WHERE Rfc = @TrainingRfc AND Code = 'TRN-SIN-GOCE-POL');

  INSERT rh.LeaveEnrollment
    (Rfc, EmployeeId, LeavePolicyId, EffectiveFrom, EffectiveTo, CreatedBy)
  VALUES
    (@TrainingRfc, 990002, @PersonalLeavePolicyId, CONVERT(date, '2026-01-05'), NULL, @ScenarioActor),
    (@TrainingRfc, 990002, @UnpaidLeavePolicyId, CONVERT(date, '2026-01-05'), NULL, @ScenarioActor),
    (@TrainingRfc, 990003, @PersonalLeavePolicyId, CONVERT(date, '2026-01-05'), NULL, @ScenarioActor),
    (@TrainingRfc, 990003, @UnpaidLeavePolicyId, CONVERT(date, '2026-01-05'), NULL, @ScenarioActor);

  INSERT rh.LeaveBalanceLedger
    (Rfc, EmployeeId, LeaveTypeId, TransactionDate, Days, TransactionType,
     SourceKey, Reason, CreatedBy)
  VALUES
    (@TrainingRfc, 990002, @PersonalLeaveTypeId, CONVERT(date, '2026-01-05'),
     5, 'TRAINING_INITIAL', 'training:initial:trainee01',
     N'Saldo ficticio exclusivamente para práctica', @ScenarioActor),
    (@TrainingRfc, 990003, @PersonalLeaveTypeId, CONVERT(date, '2026-01-05'),
     5, 'TRAINING_INITIAL', 'training:initial:trainee02',
     N'Saldo ficticio exclusivamente para práctica', @ScenarioActor);

  INSERT rh.PrivacyNotice
    (Rfc, Version, Title, NoticeText, EffectiveFrom, IsActive, CreatedBy)
  VALUES
    (@TrainingRfc, 'TRN-v1', N'Aviso ficticio del entorno de capacitación',
     N'Este aviso pertenece únicamente a Orion_Training. No captures ubicación, documentos ni datos personales reales. La evidencia de este ejercicio debe ser ficticia y el entorno puede reiniciarse sin conservar tus prácticas.',
     CONVERT(date, '2026-01-01'), 1, @ScenarioActor);

  /* Exact, fail-closed scenario manifest. */
  IF (SELECT COUNT(*) FROM rh.WorkSite) <> 1
     OR NOT EXISTS
        (SELECT 1 FROM rh.WorkSite WHERE Id = @WorkSiteId AND Rfc = @TrainingRfc
          AND Code = 'TRN-SITE-01' AND IsActive = 1)
     OR (SELECT COUNT(*) FROM rh.ScheduleTemplate) <> 1
     OR (SELECT COUNT(*) FROM rh.ScheduleDay) <> 7
     OR (SELECT COUNT(DISTINCT DayOfWeek) FROM rh.ScheduleDay WHERE ScheduleTemplateId = @ScheduleTemplateId) <> 7
     OR EXISTS
        (SELECT 1 FROM rh.ScheduleDay WHERE ScheduleTemplateId <> @ScheduleTemplateId
          OR IsWorkingDay <> 1 OR StartTime IS NULL OR StartTime <> CONVERT(time(0), '09:00:00')
          OR EndTime IS NULL OR EndTime <> CONVERT(time(0), '17:00:00') OR UnpaidBreakMinutes <> 60)
     OR (SELECT COUNT(*) FROM rh.AttendancePolicy) <> 1
     OR NOT EXISTS
        (SELECT 1 FROM rh.AttendancePolicy WHERE Id = @AttendancePolicyId
          AND Rfc = @TrainingRfc AND Code = 'TRN-ASISTENCIA'
          AND LocationRequired = 0 AND IsActive = 1)
     OR (SELECT COUNT(*) FROM rh.PayGroup) <> 1
     OR (SELECT COUNT(*) FROM rh.EmployeeWorkAssignment) <> 2
     OR EXISTS
        (
          SELECT 1
          FROM (VALUES (990002), (990003)) expected(EmployeeId)
          WHERE (SELECT COUNT(*) FROM rh.EmployeeWorkAssignment actual
                 WHERE actual.EmployeeId = expected.EmployeeId) <> 1
        )
     OR EXISTS
        (SELECT 1 FROM rh.EmployeeWorkAssignment WHERE Rfc <> @TrainingRfc
          OR EmployeeId NOT IN (990002, 990003)
          OR SiteId <> @WorkSiteId
          OR ScheduleTemplateId <> @ScheduleTemplateId
          OR AttendancePolicyId <> @AttendancePolicyId
          OR PayGroupId <> @PayGroupId)
    THROW 51826, 'SCENARIO PROVISIONING FAILED: the fictional attendance manifest is not exact.', 1;

  IF (SELECT COUNT(*) FROM rh.LeaveType) <> 2
     OR (SELECT COUNT(*) FROM rh.LeavePolicy) <> 2
     OR EXISTS
        (SELECT 1 FROM rh.LeavePolicy policyInfo
          JOIN rh.LeaveType typeInfo ON typeInfo.Id = policyInfo.LeaveTypeId
          WHERE typeInfo.Code <> CASE policyInfo.Code
            WHEN 'TRN-PERSONAL-POL' THEN 'TRN-PERSONAL'
            WHEN 'TRN-SIN-GOCE-POL' THEN 'TRN-SIN-GOCE'
            ELSE 'INVALID'
          END)
     OR (SELECT COUNT(*) FROM rh.LeaveEnrollment) <> 4
     OR EXISTS
        (
          SELECT 1
          FROM (VALUES (990002), (990003)) expectedEmployee(EmployeeId)
          CROSS JOIN (VALUES (@PersonalLeavePolicyId), (@UnpaidLeavePolicyId)) expectedPolicy(LeavePolicyId)
          WHERE (SELECT COUNT(*) FROM rh.LeaveEnrollment actual
                 WHERE actual.EmployeeId = expectedEmployee.EmployeeId
                   AND actual.LeavePolicyId = expectedPolicy.LeavePolicyId) <> 1
        )
     OR EXISTS
        (SELECT 1 FROM rh.LeaveEnrollment WHERE Rfc <> @TrainingRfc OR EmployeeId NOT IN (990002, 990003))
     OR (SELECT COUNT(*) FROM rh.LeaveBalanceLedger) <> 2
     OR EXISTS
        (SELECT 1 FROM rh.LeaveBalanceLedger WHERE Rfc <> @TrainingRfc
          OR EmployeeId NOT IN (990002, 990003)
          OR LeaveTypeId <> @PersonalLeaveTypeId
          OR Days <> 5
          OR SourceKey NOT LIKE 'training:initial:trainee0[12]')
     OR (SELECT COUNT(*) FROM rh.PrivacyNotice) <> 1
     OR NOT EXISTS
        (SELECT 1 FROM rh.PrivacyNotice WHERE Rfc = @TrainingRfc
          AND Version = 'TRN-v1' AND IsActive = 1
          AND Title = N'Aviso ficticio del entorno de capacitación'
          AND NoticeText LIKE N'%Orion_Training%')
    THROW 51827, 'SCENARIO PROVISIONING FAILED: the fictional leave/privacy manifest is not exact.', 1;

  DECLARE @AllowedNonEmptyRhTables table (TableName sysname NOT NULL PRIMARY KEY);
  INSERT @AllowedNonEmptyRhTables (TableName)
  VALUES
    (N'WorkSite'), (N'ScheduleTemplate'), (N'ScheduleDay'),
    (N'AttendancePolicy'), (N'PayGroup'), (N'EmployeeWorkAssignment'),
    (N'LeaveType'), (N'LeavePolicy'), (N'LeaveEnrollment'),
    (N'LeaveBalanceLedger'), (N'PrivacyNotice');

  IF EXISTS
  (
    SELECT 1
    FROM sys.tables tableInfo
    JOIN sys.schemas schemaInfo ON schemaInfo.schema_id = tableInfo.schema_id
    JOIN sys.dm_db_partition_stats partitionInfo ON partitionInfo.object_id = tableInfo.object_id
    LEFT JOIN @AllowedNonEmptyRhTables allowed ON allowed.TableName = tableInfo.name
    WHERE schemaInfo.name = N'rh'
      AND tableInfo.is_ms_shipped = 0
      AND partitionInfo.index_id IN (0, 1)
      AND allowed.TableName IS NULL
    GROUP BY tableInfo.object_id
    HAVING SUM(partitionInfo.row_count) <> 0
  )
    THROW 51828, 'SCENARIO PROVISIONING FAILED: an unreviewed RH table became non-empty.', 1;

  COMMIT TRANSACTION;
END TRY
BEGIN CATCH
  IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
  THROW;
END CATCH;
