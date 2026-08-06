SET ANSI_NULLS ON;
GO
SET QUOTED_IDENTIFIER ON;
GO
SET XACT_ABORT ON;
GO

BEGIN TRANSACTION;

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'rh')
  EXEC('CREATE SCHEMA rh AUTHORIZATION dbo;');

IF OBJECT_ID('rh.WorkSite', 'U') IS NULL
BEGIN
  CREATE TABLE rh.WorkSite
  (
    Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_rh_WorkSite PRIMARY KEY,
    Rfc varchar(50) NOT NULL,
    Code varchar(30) NOT NULL,
    [Name] nvarchar(150) NOT NULL,
    TimeZoneId varchar(100) NOT NULL CONSTRAINT DF_rh_WorkSite_TimeZone DEFAULT ('America/Mexico_City'),
    Latitude decimal(9,6) NOT NULL,
    Longitude decimal(9,6) NOT NULL,
    RadiusMeters int NOT NULL CONSTRAINT DF_rh_WorkSite_Radius DEFAULT (150),
    MaxAccuracyMeters int NOT NULL CONSTRAINT DF_rh_WorkSite_Accuracy DEFAULT (100),
    IsActive bit NOT NULL CONSTRAINT DF_rh_WorkSite_Active DEFAULT (1),
    CreatedAtUtc datetime2(0) NOT NULL CONSTRAINT DF_rh_WorkSite_Created DEFAULT SYSUTCDATETIME(),
    CreatedBy nvarchar(256) NOT NULL,
    UpdatedAtUtc datetime2(0) NULL,
    UpdatedBy nvarchar(256) NULL,
    RowVersion rowversion NOT NULL,
    CONSTRAINT CK_rh_WorkSite_Radius CHECK (RadiusMeters BETWEEN 25 AND 5000),
    CONSTRAINT CK_rh_WorkSite_Accuracy CHECK (MaxAccuracyMeters BETWEEN 10 AND 5000)
  );
  CREATE UNIQUE INDEX UX_rh_WorkSite_RfcCode ON rh.WorkSite (Rfc, Code);
  CREATE INDEX IX_rh_WorkSite_RfcActive ON rh.WorkSite (Rfc, IsActive, [Name]);
END;

IF OBJECT_ID('rh.ScheduleTemplate', 'U') IS NULL
BEGIN
  CREATE TABLE rh.ScheduleTemplate
  (
    Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_rh_ScheduleTemplate PRIMARY KEY,
    Rfc varchar(50) NOT NULL,
    Code varchar(30) NOT NULL,
    [Name] nvarchar(150) NOT NULL,
    IsActive bit NOT NULL CONSTRAINT DF_rh_ScheduleTemplate_Active DEFAULT (1),
    CreatedAtUtc datetime2(0) NOT NULL CONSTRAINT DF_rh_ScheduleTemplate_Created DEFAULT SYSUTCDATETIME(),
    CreatedBy nvarchar(256) NOT NULL,
    UpdatedAtUtc datetime2(0) NULL,
    UpdatedBy nvarchar(256) NULL,
    RowVersion rowversion NOT NULL
  );
  CREATE UNIQUE INDEX UX_rh_ScheduleTemplate_RfcCode ON rh.ScheduleTemplate (Rfc, Code);
END;

IF OBJECT_ID('rh.ScheduleDay', 'U') IS NULL
BEGIN
  CREATE TABLE rh.ScheduleDay
  (
    Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_rh_ScheduleDay PRIMARY KEY,
    ScheduleTemplateId int NOT NULL,
    DayOfWeek tinyint NOT NULL,
    IsWorkingDay bit NOT NULL,
    StartTime time(0) NULL,
    EndTime time(0) NULL,
    UnpaidBreakMinutes int NOT NULL CONSTRAINT DF_rh_ScheduleDay_Break DEFAULT (0),
    CONSTRAINT FK_rh_ScheduleDay_Template FOREIGN KEY (ScheduleTemplateId) REFERENCES rh.ScheduleTemplate (Id) ON DELETE CASCADE,
    CONSTRAINT CK_rh_ScheduleDay_Day CHECK (DayOfWeek BETWEEN 0 AND 6),
    CONSTRAINT CK_rh_ScheduleDay_Break CHECK (UnpaidBreakMinutes BETWEEN 0 AND 480)
  );
  CREATE UNIQUE INDEX UX_rh_ScheduleDay_TemplateDay ON rh.ScheduleDay (ScheduleTemplateId, DayOfWeek);
END;

IF OBJECT_ID('rh.ScheduleBreak', 'U') IS NULL
BEGIN
  CREATE TABLE rh.ScheduleBreak
  (
    Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_rh_ScheduleBreak PRIMARY KEY,
    ScheduleTemplateId int NOT NULL,
    [Name] nvarchar(100) NOT NULL,
    StartTime time(0) NULL,
    DurationMinutes int NOT NULL,
    IsPaid bit NOT NULL,
    IsRequired bit NOT NULL CONSTRAINT DF_rh_ScheduleBreak_Required DEFAULT (1),
    CONSTRAINT FK_rh_ScheduleBreak_Template FOREIGN KEY (ScheduleTemplateId) REFERENCES rh.ScheduleTemplate (Id) ON DELETE CASCADE,
    CONSTRAINT CK_rh_ScheduleBreak_Duration CHECK (DurationMinutes BETWEEN 1 AND 480)
  );
  CREATE INDEX IX_rh_ScheduleBreak_Template ON rh.ScheduleBreak (ScheduleTemplateId, StartTime);
END;

IF OBJECT_ID('rh.AttendancePolicy', 'U') IS NULL
BEGIN
  CREATE TABLE rh.AttendancePolicy
  (
    Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_rh_AttendancePolicy PRIMARY KEY,
    Rfc varchar(50) NOT NULL,
    Code varchar(30) NOT NULL,
    [Name] nvarchar(150) NOT NULL,
    EffectiveFrom date NOT NULL,
    EffectiveTo date NULL,
    WeeklyOrdinaryMinutes int NOT NULL,
    WeeklyDoubleOvertimeMinutes int NOT NULL,
    WeeklyTripleOvertimeMinutes int NOT NULL CONSTRAINT DF_rh_AttendancePolicy_Triple DEFAULT (240),
    GraceMinutes int NOT NULL CONSTRAINT DF_rh_AttendancePolicy_Grace DEFAULT (5),
    RoundingMinutes int NOT NULL CONSTRAINT DF_rh_AttendancePolicy_Rounding DEFAULT (1),
    LocationRequired bit NOT NULL CONSTRAINT DF_rh_AttendancePolicy_Location DEFAULT (1),
    IsActive bit NOT NULL CONSTRAINT DF_rh_AttendancePolicy_Active DEFAULT (1),
    RequiresReview bit NOT NULL CONSTRAINT DF_rh_AttendancePolicy_Review DEFAULT (1),
    CreatedAtUtc datetime2(0) NOT NULL CONSTRAINT DF_rh_AttendancePolicy_Created DEFAULT SYSUTCDATETIME(),
    CreatedBy nvarchar(256) NOT NULL,
    RowVersion rowversion NOT NULL,
    CONSTRAINT CK_rh_AttendancePolicy_Dates CHECK (EffectiveTo IS NULL OR EffectiveTo >= EffectiveFrom),
    CONSTRAINT CK_rh_AttendancePolicy_Minutes CHECK (WeeklyOrdinaryMinutes > 0 AND WeeklyDoubleOvertimeMinutes >= 0 AND WeeklyTripleOvertimeMinutes >= 0)
  );
  CREATE UNIQUE INDEX UX_rh_AttendancePolicy_Version ON rh.AttendancePolicy (Rfc, Code, EffectiveFrom);
  CREATE INDEX IX_rh_AttendancePolicy_Effective ON rh.AttendancePolicy (Rfc, Code, EffectiveFrom, EffectiveTo);
END;

IF OBJECT_ID('rh.OvertimePolicy', 'U') IS NULL
BEGIN
  CREATE TABLE rh.OvertimePolicy
  (
    Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_rh_OvertimePolicy PRIMARY KEY,
    Rfc varchar(50) NOT NULL,
    Code varchar(30) NOT NULL,
    [Name] nvarchar(150) NOT NULL,
    EffectiveFrom date NOT NULL,
    EffectiveTo date NULL,
    WeeklyDoubleMinutes int NOT NULL,
    WeeklyTripleMinutes int NOT NULL,
    RequiresSupervisorApproval bit NOT NULL CONSTRAINT DF_rh_OvertimePolicy_Approval DEFAULT (1),
    IsActive bit NOT NULL CONSTRAINT DF_rh_OvertimePolicy_Active DEFAULT (1),
    RequiresReview bit NOT NULL CONSTRAINT DF_rh_OvertimePolicy_Review DEFAULT (1),
    CreatedAtUtc datetime2(0) NOT NULL CONSTRAINT DF_rh_OvertimePolicy_Created DEFAULT SYSUTCDATETIME(),
    CreatedBy nvarchar(256) NOT NULL,
    RowVersion rowversion NOT NULL,
    CONSTRAINT CK_rh_OvertimePolicy_Dates CHECK (EffectiveTo IS NULL OR EffectiveTo >= EffectiveFrom),
    CONSTRAINT CK_rh_OvertimePolicy_Minutes CHECK (WeeklyDoubleMinutes >= 0 AND WeeklyTripleMinutes >= 0)
  );
  CREATE UNIQUE INDEX UX_rh_OvertimePolicy_Version ON rh.OvertimePolicy (Rfc, Code, EffectiveFrom);
END;

IF OBJECT_ID('rh.PayGroup', 'U') IS NULL
BEGIN
  CREATE TABLE rh.PayGroup
  (
    Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_rh_PayGroup PRIMARY KEY,
    Rfc varchar(50) NOT NULL,
    Code varchar(30) NOT NULL,
    [Name] nvarchar(150) NOT NULL,
    Frequency varchar(20) NOT NULL,
    IsActive bit NOT NULL CONSTRAINT DF_rh_PayGroup_Active DEFAULT (1),
    CreatedAtUtc datetime2(0) NOT NULL CONSTRAINT DF_rh_PayGroup_Created DEFAULT SYSUTCDATETIME(),
    CreatedBy nvarchar(256) NOT NULL,
    RowVersion rowversion NOT NULL,
    CONSTRAINT CK_rh_PayGroup_Frequency CHECK (Frequency IN ('WEEKLY','BIWEEKLY','MONTHLY'))
  );
  CREATE UNIQUE INDEX UX_rh_PayGroup_RfcCode ON rh.PayGroup (Rfc, Code);
END;

IF OBJECT_ID('rh.EmployeeWorkAssignment', 'U') IS NULL
BEGIN
  CREATE TABLE rh.EmployeeWorkAssignment
  (
    Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_rh_EmployeeWorkAssignment PRIMARY KEY,
    Rfc varchar(50) NOT NULL,
    EmployeeId int NOT NULL,
    SiteId int NOT NULL,
    ScheduleTemplateId int NOT NULL,
    AttendancePolicyId int NOT NULL,
    PayGroupId int NOT NULL,
    EffectiveFrom date NOT NULL,
    EffectiveTo date NULL,
    CreatedAtUtc datetime2(0) NOT NULL CONSTRAINT DF_rh_EmployeeWorkAssignment_Created DEFAULT SYSUTCDATETIME(),
    CreatedBy nvarchar(256) NOT NULL,
    UpdatedAtUtc datetime2(0) NULL,
    UpdatedBy nvarchar(256) NULL,
    RowVersion rowversion NOT NULL,
    CONSTRAINT FK_rh_EmployeeWorkAssignment_Employee FOREIGN KEY (EmployeeId) REFERENCES dbo.Capital_Humano (ID),
    CONSTRAINT FK_rh_EmployeeWorkAssignment_Site FOREIGN KEY (SiteId) REFERENCES rh.WorkSite (Id),
    CONSTRAINT FK_rh_EmployeeWorkAssignment_Schedule FOREIGN KEY (ScheduleTemplateId) REFERENCES rh.ScheduleTemplate (Id),
    CONSTRAINT FK_rh_EmployeeWorkAssignment_Policy FOREIGN KEY (AttendancePolicyId) REFERENCES rh.AttendancePolicy (Id),
    CONSTRAINT FK_rh_EmployeeWorkAssignment_PayGroup FOREIGN KEY (PayGroupId) REFERENCES rh.PayGroup (Id),
    CONSTRAINT CK_rh_EmployeeWorkAssignment_Dates CHECK (EffectiveTo IS NULL OR EffectiveTo >= EffectiveFrom)
  );
  CREATE UNIQUE INDEX UX_rh_EmployeeWorkAssignment_Start ON rh.EmployeeWorkAssignment (Rfc, EmployeeId, EffectiveFrom);
  CREATE INDEX IX_rh_EmployeeWorkAssignment_Current ON rh.EmployeeWorkAssignment (Rfc, EmployeeId, EffectiveFrom, EffectiveTo);
END;

IF OBJECT_ID('rh.SupervisorAssignment', 'U') IS NULL
BEGIN
  CREATE TABLE rh.SupervisorAssignment
  (
    Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_rh_SupervisorAssignment PRIMARY KEY,
    Rfc varchar(50) NOT NULL,
    EmployeeId int NOT NULL,
    SupervisorEmployeeId int NOT NULL,
    EffectiveFrom date NOT NULL,
    EffectiveTo date NULL,
    CreatedAtUtc datetime2(0) NOT NULL CONSTRAINT DF_rh_SupervisorAssignment_Created DEFAULT SYSUTCDATETIME(),
    CreatedBy nvarchar(256) NOT NULL,
    RowVersion rowversion NOT NULL,
    CONSTRAINT FK_rh_SupervisorAssignment_Employee FOREIGN KEY (EmployeeId) REFERENCES dbo.Capital_Humano (ID),
    CONSTRAINT FK_rh_SupervisorAssignment_Supervisor FOREIGN KEY (SupervisorEmployeeId) REFERENCES dbo.Capital_Humano (ID),
    CONSTRAINT CK_rh_SupervisorAssignment_Different CHECK (EmployeeId <> SupervisorEmployeeId),
    CONSTRAINT CK_rh_SupervisorAssignment_Dates CHECK (EffectiveTo IS NULL OR EffectiveTo >= EffectiveFrom)
  );
  CREATE UNIQUE INDEX UX_rh_SupervisorAssignment_Start ON rh.SupervisorAssignment (Rfc, EmployeeId, EffectiveFrom);
  CREATE INDEX IX_rh_SupervisorAssignment_Supervisor ON rh.SupervisorAssignment (Rfc, SupervisorEmployeeId, EffectiveFrom, EffectiveTo);
END;

IF OBJECT_ID('rh.Holiday', 'U') IS NULL
BEGIN
  CREATE TABLE rh.Holiday
  (
    Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_rh_Holiday PRIMARY KEY,
    Rfc varchar(50) NOT NULL,
    SiteId int NULL,
    HolidayDate date NOT NULL,
    [Name] nvarchar(150) NOT NULL,
    IsPaid bit NOT NULL CONSTRAINT DF_rh_Holiday_Paid DEFAULT (1),
    CONSTRAINT FK_rh_Holiday_Site FOREIGN KEY (SiteId) REFERENCES rh.WorkSite (Id)
  );
  CREATE UNIQUE INDEX UX_rh_Holiday_ScopeDate ON rh.Holiday (Rfc, SiteId, HolidayDate);
END;

IF OBJECT_ID('rh.KioskDevice', 'U') IS NULL
BEGIN
  CREATE TABLE rh.KioskDevice
  (
    Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_rh_KioskDevice PRIMARY KEY,
    Rfc varchar(50) NOT NULL,
    SiteId int NOT NULL,
    [Name] nvarchar(150) NOT NULL,
    DeviceTokenHash binary(32) NULL,
    IsActive bit NOT NULL CONSTRAINT DF_rh_KioskDevice_Active DEFAULT (0),
    PairedAtUtc datetime2(0) NULL,
    LastSeenAtUtc datetime2(0) NULL,
    CreatedAtUtc datetime2(0) NOT NULL CONSTRAINT DF_rh_KioskDevice_Created DEFAULT SYSUTCDATETIME(),
    CreatedBy nvarchar(256) NOT NULL,
    RowVersion rowversion NOT NULL,
    CONSTRAINT FK_rh_KioskDevice_Site FOREIGN KEY (SiteId) REFERENCES rh.WorkSite (Id)
  );
  CREATE UNIQUE INDEX UX_rh_KioskDevice_Token ON rh.KioskDevice (DeviceTokenHash) WHERE DeviceTokenHash IS NOT NULL;
  CREATE INDEX IX_rh_KioskDevice_RfcSite ON rh.KioskDevice (Rfc, SiteId, IsActive);
END;

IF OBJECT_ID('rh.KioskPairingCode', 'U') IS NULL
BEGIN
  CREATE TABLE rh.KioskPairingCode
  (
    Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_rh_KioskPairingCode PRIMARY KEY,
    KioskDeviceId int NOT NULL,
    CodeHash binary(32) NOT NULL,
    ExpiresAtUtc datetime2(0) NOT NULL,
    UsedAtUtc datetime2(0) NULL,
    CreatedAtUtc datetime2(0) NOT NULL CONSTRAINT DF_rh_KioskPairingCode_Created DEFAULT SYSUTCDATETIME(),
    CONSTRAINT FK_rh_KioskPairingCode_Device FOREIGN KEY (KioskDeviceId) REFERENCES rh.KioskDevice (Id) ON DELETE CASCADE
  );
  CREATE UNIQUE INDEX UX_rh_KioskPairingCode_Hash ON rh.KioskPairingCode (CodeHash);
END;

IF OBJECT_ID('rh.EmployeeKioskCredential', 'U') IS NULL
BEGIN
  CREATE TABLE rh.EmployeeKioskCredential
  (
    Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_rh_EmployeeKioskCredential PRIMARY KEY,
    Rfc varchar(50) NOT NULL,
    EmployeeId int NOT NULL,
    BadgeTokenHash binary(32) NOT NULL,
    PinHash nvarchar(500) NOT NULL,
    FailedAttempts int NOT NULL CONSTRAINT DF_rh_EmployeeKioskCredential_Failed DEFAULT (0),
    LockedUntilUtc datetime2(0) NULL,
    IsActive bit NOT NULL CONSTRAINT DF_rh_EmployeeKioskCredential_Active DEFAULT (1),
    CreatedAtUtc datetime2(0) NOT NULL CONSTRAINT DF_rh_EmployeeKioskCredential_Created DEFAULT SYSUTCDATETIME(),
    CreatedBy nvarchar(256) NOT NULL,
    RowVersion rowversion NOT NULL,
    CONSTRAINT FK_rh_EmployeeKioskCredential_Employee FOREIGN KEY (EmployeeId) REFERENCES dbo.Capital_Humano (ID)
  );
  CREATE UNIQUE INDEX UX_rh_EmployeeKioskCredential_Employee ON rh.EmployeeKioskCredential (Rfc, EmployeeId);
  CREATE UNIQUE INDEX UX_rh_EmployeeKioskCredential_Badge ON rh.EmployeeKioskCredential (BadgeTokenHash);
END;

IF OBJECT_ID('rh.TimeEvent', 'U') IS NULL
BEGIN
  CREATE TABLE rh.TimeEvent
  (
    Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_rh_TimeEvent PRIMARY KEY,
    Rfc varchar(50) NOT NULL,
    EmployeeId int NOT NULL,
    SiteId int NOT NULL,
    WorkDate date NOT NULL,
    EventType varchar(30) NOT NULL,
    [Source] varchar(20) NOT NULL,
    OccurredAtUtc datetime2(3) NOT NULL,
    ClientCapturedAtUtc datetime2(3) NULL,
    IdempotencyKey varchar(100) NOT NULL,
    LocationProtected varbinary(max) NULL,
    LocationStatus varchar(20) NOT NULL,
    DistanceMeters decimal(10,1) NULL,
    AccuracyMeters decimal(10,1) NULL,
    KioskDeviceId int NULL,
    IsAdjustment bit NOT NULL CONSTRAINT DF_rh_TimeEvent_Adjustment DEFAULT (0),
    CorrectsEventId bigint NULL,
    Reason nvarchar(500) NULL,
    CreatedAtUtc datetime2(0) NOT NULL CONSTRAINT DF_rh_TimeEvent_Created DEFAULT SYSUTCDATETIME(),
    CreatedBy nvarchar(256) NOT NULL,
    CONSTRAINT FK_rh_TimeEvent_Employee FOREIGN KEY (EmployeeId) REFERENCES dbo.Capital_Humano (ID),
    CONSTRAINT FK_rh_TimeEvent_Site FOREIGN KEY (SiteId) REFERENCES rh.WorkSite (Id),
    CONSTRAINT FK_rh_TimeEvent_Kiosk FOREIGN KEY (KioskDeviceId) REFERENCES rh.KioskDevice (Id),
    CONSTRAINT FK_rh_TimeEvent_Corrects FOREIGN KEY (CorrectsEventId) REFERENCES rh.TimeEvent (Id),
    CONSTRAINT CK_rh_TimeEvent_Type CHECK (EventType IN ('IN','OUT','BREAK_START','BREAK_END')),
    CONSTRAINT CK_rh_TimeEvent_Source CHECK ([Source] IN ('LOGIN','KIOSK','ADJUSTMENT'))
  );
  CREATE UNIQUE INDEX UX_rh_TimeEvent_Idempotency ON rh.TimeEvent (Rfc, EmployeeId, IdempotencyKey);
  CREATE INDEX IX_rh_TimeEvent_EmployeeDate ON rh.TimeEvent (Rfc, EmployeeId, WorkDate, OccurredAtUtc, Id);
  CREATE INDEX IX_rh_TimeEvent_SiteState ON rh.TimeEvent (Rfc, SiteId, OccurredAtUtc DESC);
END;

IF OBJECT_ID('rh.AttendanceDay', 'U') IS NULL
BEGIN
  CREATE TABLE rh.AttendanceDay
  (
    Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_rh_AttendanceDay PRIMARY KEY,
    Rfc varchar(50) NOT NULL,
    EmployeeId int NOT NULL,
    WorkDate date NOT NULL,
    SiteId int NOT NULL,
    ScheduleTemplateId int NOT NULL,
    AttendancePolicyId int NOT NULL,
    ScheduledMinutes int NOT NULL CONSTRAINT DF_rh_AttendanceDay_Scheduled DEFAULT (0),
    WorkedMinutes int NOT NULL CONSTRAINT DF_rh_AttendanceDay_Worked DEFAULT (0),
    BreakMinutes int NOT NULL CONSTRAINT DF_rh_AttendanceDay_Break DEFAULT (0),
    AbsenceMinutes int NOT NULL CONSTRAINT DF_rh_AttendanceDay_Absence DEFAULT (0),
    LateMinutes int NOT NULL CONSTRAINT DF_rh_AttendanceDay_Late DEFAULT (0),
    EarlyDepartureMinutes int NOT NULL CONSTRAINT DF_rh_AttendanceDay_Early DEFAULT (0),
    OvertimeCandidateMinutes int NOT NULL CONSTRAINT DF_rh_AttendanceDay_OTCandidate DEFAULT (0),
    OvertimeApprovedMinutes int NOT NULL CONSTRAINT DF_rh_AttendanceDay_OTApproved DEFAULT (0),
    [Status] varchar(20) NOT NULL CONSTRAINT DF_rh_AttendanceDay_Status DEFAULT ('OPEN'),
    HasExceptions bit NOT NULL CONSTRAINT DF_rh_AttendanceDay_Exceptions DEFAULT (0),
    CalculatedAtUtc datetime2(0) NOT NULL CONSTRAINT DF_rh_AttendanceDay_Calculated DEFAULT SYSUTCDATETIME(),
    ApprovedAtUtc datetime2(0) NULL,
    ApprovedBy nvarchar(256) NULL,
    RowVersion rowversion NOT NULL,
    CONSTRAINT FK_rh_AttendanceDay_Employee FOREIGN KEY (EmployeeId) REFERENCES dbo.Capital_Humano (ID),
    CONSTRAINT FK_rh_AttendanceDay_Site FOREIGN KEY (SiteId) REFERENCES rh.WorkSite (Id),
    CONSTRAINT FK_rh_AttendanceDay_Schedule FOREIGN KEY (ScheduleTemplateId) REFERENCES rh.ScheduleTemplate (Id),
    CONSTRAINT FK_rh_AttendanceDay_Policy FOREIGN KEY (AttendancePolicyId) REFERENCES rh.AttendancePolicy (Id),
    CONSTRAINT CK_rh_AttendanceDay_Status CHECK ([Status] IN ('OPEN','READY','EXCEPTION','APPROVED'))
  );
  CREATE UNIQUE INDEX UX_rh_AttendanceDay_EmployeeDate ON rh.AttendanceDay (Rfc, EmployeeId, WorkDate);
  CREATE INDEX IX_rh_AttendanceDay_RfcDate ON rh.AttendanceDay (Rfc, WorkDate, [Status]);
END;

IF COL_LENGTH('rh.AttendanceDay', 'AbsenceMinutes') IS NULL
  ALTER TABLE rh.AttendanceDay ADD AbsenceMinutes int NOT NULL CONSTRAINT DF_rh_AttendanceDay_Absence_Upgrade DEFAULT (0) WITH VALUES;

IF OBJECT_ID('rh.AttendanceException', 'U') IS NULL
BEGIN
  CREATE TABLE rh.AttendanceException
  (
    Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_rh_AttendanceException PRIMARY KEY,
    Rfc varchar(50) NOT NULL,
    EmployeeId int NOT NULL,
    WorkDate date NOT NULL,
    TimeEventId bigint NULL,
    AttendanceDayId bigint NULL,
    ExceptionType varchar(40) NOT NULL,
    Detail nvarchar(500) NOT NULL,
    [Status] varchar(20) NOT NULL CONSTRAINT DF_rh_AttendanceException_Status DEFAULT ('PENDING'),
    Resolution nvarchar(500) NULL,
    CreatedAtUtc datetime2(0) NOT NULL CONSTRAINT DF_rh_AttendanceException_Created DEFAULT SYSUTCDATETIME(),
    ResolvedAtUtc datetime2(0) NULL,
    ResolvedBy nvarchar(256) NULL,
    RowVersion rowversion NOT NULL,
    CONSTRAINT FK_rh_AttendanceException_Employee FOREIGN KEY (EmployeeId) REFERENCES dbo.Capital_Humano (ID),
    CONSTRAINT FK_rh_AttendanceException_Event FOREIGN KEY (TimeEventId) REFERENCES rh.TimeEvent (Id),
    CONSTRAINT FK_rh_AttendanceException_Day FOREIGN KEY (AttendanceDayId) REFERENCES rh.AttendanceDay (Id),
    CONSTRAINT CK_rh_AttendanceException_Status CHECK ([Status] IN ('PENDING','APPROVED','REJECTED','RETURNED'))
  );
  CREATE INDEX IX_rh_AttendanceException_Queue ON rh.AttendanceException (Rfc, [Status], WorkDate, EmployeeId);
END;

IF OBJECT_ID('rh.OvertimeDecision', 'U') IS NULL
BEGIN
  CREATE TABLE rh.OvertimeDecision
  (
    Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_rh_OvertimeDecision PRIMARY KEY,
    Rfc varchar(50) NOT NULL,
    AttendanceDayId bigint NOT NULL,
    EmployeeId int NOT NULL,
    CandidateMinutes int NOT NULL,
    ApprovedMinutes int NOT NULL,
    Decision varchar(20) NOT NULL,
    Reason nvarchar(500) NOT NULL,
    DecidedAtUtc datetime2(0) NOT NULL CONSTRAINT DF_rh_OvertimeDecision_Created DEFAULT SYSUTCDATETIME(),
    DecidedBy nvarchar(256) NOT NULL,
    CONSTRAINT FK_rh_OvertimeDecision_Day FOREIGN KEY (AttendanceDayId) REFERENCES rh.AttendanceDay (Id),
    CONSTRAINT FK_rh_OvertimeDecision_Employee FOREIGN KEY (EmployeeId) REFERENCES dbo.Capital_Humano (ID),
    CONSTRAINT CK_rh_OvertimeDecision_Minutes CHECK (CandidateMinutes >= 0 AND ApprovedMinutes BETWEEN 0 AND CandidateMinutes),
    CONSTRAINT CK_rh_OvertimeDecision_Decision CHECK (Decision IN ('APPROVED','PARTIAL','REJECTED'))
  );
  CREATE INDEX IX_rh_OvertimeDecision_Day ON rh.OvertimeDecision (AttendanceDayId, DecidedAtUtc DESC);
END;

IF OBJECT_ID('rh.AttendanceCorrectionRequest', 'U') IS NULL
BEGIN
  CREATE TABLE rh.AttendanceCorrectionRequest
  (
    Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_rh_AttendanceCorrectionRequest PRIMARY KEY,
    Rfc varchar(50) NOT NULL,
    EmployeeId int NOT NULL,
    EventType varchar(30) NOT NULL,
    RequestedAtUtc datetime2(3) NOT NULL,
    Reason nvarchar(500) NOT NULL,
    [Status] varchar(20) NOT NULL CONSTRAINT DF_rh_AttendanceCorrectionRequest_Status DEFAULT ('PENDING'),
    DecisionReason nvarchar(500) NULL,
    CreatedAtUtc datetime2(0) NOT NULL CONSTRAINT DF_rh_AttendanceCorrectionRequest_Created DEFAULT SYSUTCDATETIME(),
    DecidedAtUtc datetime2(0) NULL,
    DecidedBy nvarchar(256) NULL,
    AdjustmentEventId bigint NULL,
    RowVersion rowversion NOT NULL,
    CONSTRAINT FK_rh_AttendanceCorrectionRequest_Employee FOREIGN KEY (EmployeeId) REFERENCES dbo.Capital_Humano (ID),
    CONSTRAINT FK_rh_AttendanceCorrectionRequest_Event FOREIGN KEY (AdjustmentEventId) REFERENCES rh.TimeEvent (Id),
    CONSTRAINT CK_rh_AttendanceCorrectionRequest_Type CHECK (EventType IN ('IN','OUT','BREAK_START','BREAK_END')),
    CONSTRAINT CK_rh_AttendanceCorrectionRequest_Status CHECK ([Status] IN ('PENDING','APPROVED','REJECTED','RETURNED'))
  );
  CREATE INDEX IX_rh_AttendanceCorrectionRequest_Queue ON rh.AttendanceCorrectionRequest (Rfc, [Status], CreatedAtUtc);
END;

IF OBJECT_ID('rh.LeaveType', 'U') IS NULL
BEGIN
  CREATE TABLE rh.LeaveType
  (
    Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_rh_LeaveType PRIMARY KEY,
    Rfc varchar(50) NOT NULL,
    Code varchar(30) NOT NULL,
    [Name] nvarchar(150) NOT NULL,
    IsPaid bit NOT NULL,
    RequiresBalance bit NOT NULL,
    IsActive bit NOT NULL CONSTRAINT DF_rh_LeaveType_Active DEFAULT (1),
    CreatedAtUtc datetime2(0) NOT NULL CONSTRAINT DF_rh_LeaveType_Created DEFAULT SYSUTCDATETIME()
  );
  CREATE UNIQUE INDEX UX_rh_LeaveType_RfcCode ON rh.LeaveType (Rfc, Code);
END;

IF OBJECT_ID('rh.LeavePolicy', 'U') IS NULL
BEGIN
  CREATE TABLE rh.LeavePolicy
  (
    Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_rh_LeavePolicy PRIMARY KEY,
    Rfc varchar(50) NOT NULL,
    LeaveTypeId int NOT NULL,
    Code varchar(30) NOT NULL,
    [Name] nvarchar(150) NOT NULL,
    EffectiveFrom date NOT NULL,
    EffectiveTo date NULL,
    AccrualMethod varchar(30) NOT NULL,
    AnnualDays decimal(8,2) NULL,
    AllowPartialDay bit NOT NULL CONSTRAINT DF_rh_LeavePolicy_Partial DEFAULT (1),
    RequiresReview bit NOT NULL CONSTRAINT DF_rh_LeavePolicy_Review DEFAULT (1),
    IsActive bit NOT NULL CONSTRAINT DF_rh_LeavePolicy_Active DEFAULT (1),
    CreatedAtUtc datetime2(0) NOT NULL CONSTRAINT DF_rh_LeavePolicy_Created DEFAULT SYSUTCDATETIME(),
    CreatedBy nvarchar(256) NOT NULL,
    RowVersion rowversion NOT NULL,
    CONSTRAINT FK_rh_LeavePolicy_Type FOREIGN KEY (LeaveTypeId) REFERENCES rh.LeaveType (Id),
    CONSTRAINT CK_rh_LeavePolicy_Dates CHECK (EffectiveTo IS NULL OR EffectiveTo >= EffectiveFrom),
    CONSTRAINT CK_rh_LeavePolicy_Accrual CHECK (AccrualMethod IN ('NONE','ANNUAL','MEXICO_STATUTORY'))
  );
  CREATE UNIQUE INDEX UX_rh_LeavePolicy_Version ON rh.LeavePolicy (Rfc, Code, EffectiveFrom);
END;

IF OBJECT_ID('rh.LeaveEnrollment', 'U') IS NULL
BEGIN
  CREATE TABLE rh.LeaveEnrollment
  (
    Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_rh_LeaveEnrollment PRIMARY KEY,
    Rfc varchar(50) NOT NULL,
    EmployeeId int NOT NULL,
    LeavePolicyId int NOT NULL,
    EffectiveFrom date NOT NULL,
    EffectiveTo date NULL,
    CreatedAtUtc datetime2(0) NOT NULL CONSTRAINT DF_rh_LeaveEnrollment_Created DEFAULT SYSUTCDATETIME(),
    CreatedBy nvarchar(256) NOT NULL,
    RowVersion rowversion NOT NULL,
    CONSTRAINT FK_rh_LeaveEnrollment_Employee FOREIGN KEY (EmployeeId) REFERENCES dbo.Capital_Humano (ID),
    CONSTRAINT FK_rh_LeaveEnrollment_Policy FOREIGN KEY (LeavePolicyId) REFERENCES rh.LeavePolicy (Id),
    CONSTRAINT CK_rh_LeaveEnrollment_Dates CHECK (EffectiveTo IS NULL OR EffectiveTo >= EffectiveFrom)
  );
  CREATE UNIQUE INDEX UX_rh_LeaveEnrollment_Start ON rh.LeaveEnrollment (Rfc, EmployeeId, LeavePolicyId, EffectiveFrom);
END;

IF OBJECT_ID('rh.LeaveBalanceLedger', 'U') IS NULL
BEGIN
  CREATE TABLE rh.LeaveBalanceLedger
  (
    Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_rh_LeaveBalanceLedger PRIMARY KEY,
    Rfc varchar(50) NOT NULL,
    EmployeeId int NOT NULL,
    LeaveTypeId int NOT NULL,
    TransactionDate date NOT NULL,
    Days decimal(8,2) NOT NULL,
    TransactionType varchar(30) NOT NULL,
    SourceKey varchar(100) NULL,
    Reason nvarchar(500) NOT NULL,
    CreatedAtUtc datetime2(0) NOT NULL CONSTRAINT DF_rh_LeaveBalanceLedger_Created DEFAULT SYSUTCDATETIME(),
    CreatedBy nvarchar(256) NOT NULL,
    CONSTRAINT FK_rh_LeaveBalanceLedger_Employee FOREIGN KEY (EmployeeId) REFERENCES dbo.Capital_Humano (ID),
    CONSTRAINT FK_rh_LeaveBalanceLedger_Type FOREIGN KEY (LeaveTypeId) REFERENCES rh.LeaveType (Id)
  );
  CREATE UNIQUE INDEX UX_rh_LeaveBalanceLedger_Source ON rh.LeaveBalanceLedger (Rfc, EmployeeId, LeaveTypeId, SourceKey) WHERE SourceKey IS NOT NULL;
  CREATE INDEX IX_rh_LeaveBalanceLedger_Balance ON rh.LeaveBalanceLedger (Rfc, EmployeeId, LeaveTypeId, TransactionDate);
END;

IF OBJECT_ID('rh.LeaveRequest', 'U') IS NULL
BEGIN
  CREATE TABLE rh.LeaveRequest
  (
    Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_rh_LeaveRequest PRIMARY KEY,
    Rfc varchar(50) NOT NULL,
    EmployeeId int NOT NULL,
    LeaveTypeId int NOT NULL,
    StartDate date NOT NULL,
    EndDate date NOT NULL,
    RequestedDays decimal(8,2) NOT NULL,
    Reason nvarchar(500) NOT NULL,
    [Status] varchar(20) NOT NULL CONSTRAINT DF_rh_LeaveRequest_Status DEFAULT ('PENDING'),
    DecisionReason nvarchar(500) NULL,
    CreatedAtUtc datetime2(0) NOT NULL CONSTRAINT DF_rh_LeaveRequest_Created DEFAULT SYSUTCDATETIME(),
    CreatedBy nvarchar(256) NOT NULL,
    DecidedAtUtc datetime2(0) NULL,
    DecidedBy nvarchar(256) NULL,
    RowVersion rowversion NOT NULL,
    CONSTRAINT FK_rh_LeaveRequest_Employee FOREIGN KEY (EmployeeId) REFERENCES dbo.Capital_Humano (ID),
    CONSTRAINT FK_rh_LeaveRequest_Type FOREIGN KEY (LeaveTypeId) REFERENCES rh.LeaveType (Id),
    CONSTRAINT CK_rh_LeaveRequest_Dates CHECK (EndDate >= StartDate AND RequestedDays > 0),
    CONSTRAINT CK_rh_LeaveRequest_Status CHECK ([Status] IN ('PENDING','APPROVED','REJECTED','RETURNED','CANCELLED'))
  );
  CREATE INDEX IX_rh_LeaveRequest_Queue ON rh.LeaveRequest (Rfc, [Status], StartDate, EmployeeId);
END;

IF OBJECT_ID('rh.PrenominaPeriod', 'U') IS NULL
BEGIN
  CREATE TABLE rh.PrenominaPeriod
  (
    Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_rh_PrenominaPeriod PRIMARY KEY,
    Rfc varchar(50) NOT NULL,
    PayGroupId int NOT NULL,
    FromDate date NOT NULL,
    ToDate date NOT NULL,
    [Status] varchar(20) NOT NULL CONSTRAINT DF_rh_PrenominaPeriod_Status DEFAULT ('OPEN'),
    Version int NOT NULL CONSTRAINT DF_rh_PrenominaPeriod_Version DEFAULT (1),
    ParentPeriodId bigint NULL,
    LockedAtUtc datetime2(0) NULL,
    LockedBy nvarchar(256) NULL,
    ReopenReason nvarchar(500) NULL,
    CreatedAtUtc datetime2(0) NOT NULL CONSTRAINT DF_rh_PrenominaPeriod_Created DEFAULT SYSUTCDATETIME(),
    CreatedBy nvarchar(256) NOT NULL,
    RowVersion rowversion NOT NULL,
    CONSTRAINT FK_rh_PrenominaPeriod_PayGroup FOREIGN KEY (PayGroupId) REFERENCES rh.PayGroup (Id),
    CONSTRAINT FK_rh_PrenominaPeriod_Parent FOREIGN KEY (ParentPeriodId) REFERENCES rh.PrenominaPeriod (Id),
    CONSTRAINT CK_rh_PrenominaPeriod_Dates CHECK (ToDate >= FromDate),
    CONSTRAINT CK_rh_PrenominaPeriod_Status CHECK ([Status] IN ('OPEN','READY','LOCKED','EXPORTED','REOPENED'))
  );
  CREATE UNIQUE INDEX UX_rh_PrenominaPeriod_Version ON rh.PrenominaPeriod (Rfc, PayGroupId, FromDate, ToDate, Version);
  CREATE INDEX IX_rh_PrenominaPeriod_Status ON rh.PrenominaPeriod (Rfc, [Status], FromDate DESC);
END;

IF OBJECT_ID('rh.PrenominaEmployeeApproval', 'U') IS NULL
BEGIN
  CREATE TABLE rh.PrenominaEmployeeApproval
  (
    Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_rh_PrenominaEmployeeApproval PRIMARY KEY,
    PeriodId bigint NOT NULL,
    EmployeeId int NOT NULL,
    [Status] varchar(20) NOT NULL CONSTRAINT DF_rh_PrenominaEmployeeApproval_Status DEFAULT ('PENDING'),
    ApprovedAtUtc datetime2(0) NULL,
    ApprovedBy nvarchar(256) NULL,
    CONSTRAINT FK_rh_PrenominaEmployeeApproval_Period FOREIGN KEY (PeriodId) REFERENCES rh.PrenominaPeriod (Id) ON DELETE CASCADE,
    CONSTRAINT FK_rh_PrenominaEmployeeApproval_Employee FOREIGN KEY (EmployeeId) REFERENCES dbo.Capital_Humano (ID),
    CONSTRAINT CK_rh_PrenominaEmployeeApproval_Status CHECK ([Status] IN ('PENDING','APPROVED','RETURNED'))
  );
  CREATE UNIQUE INDEX UX_rh_PrenominaEmployeeApproval_Employee ON rh.PrenominaEmployeeApproval (PeriodId, EmployeeId);
END;

IF OBJECT_ID('rh.PrenominaValidationResult', 'U') IS NULL
BEGIN
  CREATE TABLE rh.PrenominaValidationResult
  (
    Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_rh_PrenominaValidationResult PRIMARY KEY,
    PeriodId bigint NOT NULL,
    IsValid bit NOT NULL,
    ErrorsJson nvarchar(max) NOT NULL,
    WarningsJson nvarchar(max) NOT NULL,
    ValidatedAtUtc datetime2(0) NOT NULL CONSTRAINT DF_rh_PrenominaValidation_Created DEFAULT SYSUTCDATETIME(),
    ValidatedBy nvarchar(256) NOT NULL,
    CONSTRAINT FK_rh_PrenominaValidation_Period FOREIGN KEY (PeriodId) REFERENCES rh.PrenominaPeriod (Id)
  );
  CREATE INDEX IX_rh_PrenominaValidation_Period ON rh.PrenominaValidationResult (PeriodId, ValidatedAtUtc DESC);
END;

IF OBJECT_ID('rh.PrenominaSnapshotLine', 'U') IS NULL
BEGIN
  CREATE TABLE rh.PrenominaSnapshotLine
  (
    Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_rh_PrenominaSnapshotLine PRIMARY KEY,
    PeriodId bigint NOT NULL,
    EmployeeId int NOT NULL,
    EmployeeName nvarchar(300) NOT NULL,
    ScheduledMinutes int NOT NULL,
    WorkedMinutes int NOT NULL,
    OvertimeApprovedMinutes int NOT NULL,
    PaidLeaveDays decimal(8,2) NOT NULL,
    UnpaidLeaveDays decimal(8,2) NOT NULL,
    ExceptionCount int NOT NULL,
    SnapshotAtUtc datetime2(0) NOT NULL CONSTRAINT DF_rh_PrenominaSnapshotLine_Created DEFAULT SYSUTCDATETIME(),
    CONSTRAINT FK_rh_PrenominaSnapshotLine_Period FOREIGN KEY (PeriodId) REFERENCES rh.PrenominaPeriod (Id) ON DELETE CASCADE,
    CONSTRAINT FK_rh_PrenominaSnapshotLine_Employee FOREIGN KEY (EmployeeId) REFERENCES dbo.Capital_Humano (ID)
  );
  CREATE UNIQUE INDEX UX_rh_PrenominaSnapshotLine_Employee ON rh.PrenominaSnapshotLine (PeriodId, EmployeeId);
END;

IF OBJECT_ID('rh.PrenominaExport', 'U') IS NULL
BEGIN
  CREATE TABLE rh.PrenominaExport
  (
    Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_rh_PrenominaExport PRIMARY KEY,
    PeriodId bigint NOT NULL,
    LayoutVersion varchar(20) NOT NULL,
    XlsxFileName nvarchar(260) NOT NULL,
    XlsxContent varbinary(max) NOT NULL,
    XlsxSha256 char(64) NOT NULL,
    ZipFileName nvarchar(260) NOT NULL,
    ZipContent varbinary(max) NOT NULL,
    ZipSha256 char(64) NOT NULL,
    CreatedAtUtc datetime2(0) NOT NULL CONSTRAINT DF_rh_PrenominaExport_Created DEFAULT SYSUTCDATETIME(),
    CreatedBy nvarchar(256) NOT NULL,
    CONSTRAINT FK_rh_PrenominaExport_Period FOREIGN KEY (PeriodId) REFERENCES rh.PrenominaPeriod (Id)
  );
  CREATE INDEX IX_rh_PrenominaExport_Period ON rh.PrenominaExport (PeriodId, CreatedAtUtc DESC);
END;

IF OBJECT_ID('rh.PrivacyNotice', 'U') IS NULL
BEGIN
  CREATE TABLE rh.PrivacyNotice
  (
    Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_rh_PrivacyNotice PRIMARY KEY,
    Rfc varchar(50) NOT NULL,
    Version varchar(30) NOT NULL,
    Title nvarchar(200) NOT NULL,
    NoticeText nvarchar(max) NOT NULL,
    EffectiveFrom date NOT NULL,
    IsActive bit NOT NULL CONSTRAINT DF_rh_PrivacyNotice_Active DEFAULT (0),
    CreatedAtUtc datetime2(0) NOT NULL CONSTRAINT DF_rh_PrivacyNotice_Created DEFAULT SYSUTCDATETIME(),
    CreatedBy nvarchar(256) NOT NULL
  );
  CREATE UNIQUE INDEX UX_rh_PrivacyNotice_Version ON rh.PrivacyNotice (Rfc, Version);
END;

IF OBJECT_ID('rh.EmployeePrivacyAcknowledgement', 'U') IS NULL
BEGIN
  CREATE TABLE rh.EmployeePrivacyAcknowledgement
  (
    Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_rh_EmployeePrivacyAcknowledgement PRIMARY KEY,
    PrivacyNoticeId int NOT NULL,
    EmployeeId int NOT NULL,
    AcknowledgedAtUtc datetime2(0) NOT NULL CONSTRAINT DF_rh_EmployeePrivacyAcknowledgement_Created DEFAULT SYSUTCDATETIME(),
    AcknowledgedFrom varchar(20) NOT NULL,
    CONSTRAINT FK_rh_EmployeePrivacyAcknowledgement_Notice FOREIGN KEY (PrivacyNoticeId) REFERENCES rh.PrivacyNotice (Id),
    CONSTRAINT FK_rh_EmployeePrivacyAcknowledgement_Employee FOREIGN KEY (EmployeeId) REFERENCES dbo.Capital_Humano (ID)
  );
  CREATE UNIQUE INDEX UX_rh_EmployeePrivacyAcknowledgement ON rh.EmployeePrivacyAcknowledgement (PrivacyNoticeId, EmployeeId);
END;

IF OBJECT_ID('rh.AuditEvent', 'U') IS NULL
BEGIN
  CREATE TABLE rh.AuditEvent
  (
    Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_rh_AuditEvent PRIMARY KEY,
    Rfc varchar(50) NOT NULL,
    EmployeeId int NULL,
    EntityType varchar(60) NOT NULL,
    EntityId varchar(100) NOT NULL,
    EventType varchar(60) NOT NULL,
    Detail nvarchar(1000) NULL,
    CreatedAtUtc datetime2(0) NOT NULL CONSTRAINT DF_rh_AuditEvent_Created DEFAULT SYSUTCDATETIME(),
    CreatedBy nvarchar(256) NOT NULL,
    CONSTRAINT FK_rh_AuditEvent_Employee FOREIGN KEY (EmployeeId) REFERENCES dbo.Capital_Humano (ID)
  );
  CREATE INDEX IX_rh_AuditEvent_Entity ON rh.AuditEvent (Rfc, EntityType, EntityId, CreatedAtUtc DESC);
END;

;WITH Rfcs AS
(
  SELECT DISTINCT LTRIM(RTRIM(RFC)) AS Rfc
  FROM dbo.Capital_Humano
  WHERE NULLIF(LTRIM(RTRIM(RFC)), '') IS NOT NULL
)
MERGE rh.PayGroup AS target
USING
(
  SELECT Rfc, v.Code, v.[Name], v.Frequency
  FROM Rfcs
  CROSS JOIN (VALUES
    ('SEMANAL', N'Semanal', 'WEEKLY'),
    ('QUINCENAL', N'Quincenal', 'BIWEEKLY'),
    ('MENSUAL', N'Mensual', 'MONTHLY')
  ) v(Code, [Name], Frequency)
) AS source
ON target.Rfc = source.Rfc AND target.Code = source.Code
WHEN NOT MATCHED THEN
  INSERT (Rfc, Code, [Name], Frequency, CreatedBy)
  VALUES (source.Rfc, source.Code, source.[Name], source.Frequency, N'Migracion workforce MVP');

;WITH Rfcs AS
(
  SELECT DISTINCT LTRIM(RTRIM(RFC)) AS Rfc
  FROM dbo.Capital_Humano
  WHERE NULLIF(LTRIM(RTRIM(RFC)), '') IS NOT NULL
)
MERGE rh.LeaveType AS target
USING
(
  SELECT Rfc, v.Code, v.[Name], v.IsPaid, v.RequiresBalance
  FROM Rfcs
  CROSS JOIN (VALUES
    ('VACACIONES', N'Vacaciones', CAST(1 AS bit), CAST(1 AS bit)),
    ('INCAPACIDAD', N'Incapacidad', CAST(1 AS bit), CAST(0 AS bit)),
    ('PERSONAL', N'Permiso personal', CAST(1 AS bit), CAST(1 AS bit)),
    ('SIN_GOCE', N'Permiso sin goce', CAST(0 AS bit), CAST(0 AS bit))
  ) v(Code, [Name], IsPaid, RequiresBalance)
) AS source
ON target.Rfc = source.Rfc AND target.Code = source.Code
WHEN NOT MATCHED THEN
  INSERT (Rfc, Code, [Name], IsPaid, RequiresBalance)
  VALUES (source.Rfc, source.Code, source.[Name], source.IsPaid, source.RequiresBalance);

;WITH Rfcs AS
(
  SELECT DISTINCT LTRIM(RTRIM(RFC)) AS Rfc
  FROM dbo.Capital_Humano
  WHERE NULLIF(LTRIM(RTRIM(RFC)), '') IS NOT NULL
), PolicyVersions AS
(
  SELECT Rfc, v.[Year], v.WeeklyHours, v.DoubleHours
  FROM Rfcs
  CROSS JOIN (VALUES
    (2026, 48, 9),
    (2027, 46, 9),
    (2028, 44, 10),
    (2029, 42, 11),
    (2030, 40, 12)
  ) v([Year], WeeklyHours, DoubleHours)
)
MERGE rh.AttendancePolicy AS target
USING PolicyVersions AS source
ON target.Rfc = source.Rfc AND target.Code = 'MX-LFT' AND target.EffectiveFrom = DATEFROMPARTS(source.[Year], 1, 1)
WHEN NOT MATCHED THEN
  INSERT
  (
    Rfc, Code, [Name], EffectiveFrom, EffectiveTo,
    WeeklyOrdinaryMinutes, WeeklyDoubleOvertimeMinutes, WeeklyTripleOvertimeMinutes,
    GraceMinutes, RoundingMinutes, LocationRequired, IsActive, RequiresReview, CreatedBy
  )
  VALUES
  (
    source.Rfc, 'MX-LFT', CONCAT(N'Mexico LFT ', source.[Year]), DATEFROMPARTS(source.[Year], 1, 1),
    CASE WHEN source.[Year] = 2030 THEN NULL ELSE DATEFROMPARTS(source.[Year], 12, 31) END,
    source.WeeklyHours * 60, source.DoubleHours * 60, 240,
    5, 1, 1, 1, 1, N'Migracion workforce MVP - requiere validacion RH'
  );

;WITH Rfcs AS
(
  SELECT DISTINCT LTRIM(RTRIM(RFC)) AS Rfc
  FROM dbo.Capital_Humano
  WHERE NULLIF(LTRIM(RTRIM(RFC)), '') IS NOT NULL
), PolicyVersions AS
(
  SELECT Rfc, v.[Year], v.DoubleHours
  FROM Rfcs
  CROSS JOIN (VALUES (2026,9),(2027,9),(2028,10),(2029,11),(2030,12)) v([Year],DoubleHours)
)
MERGE rh.OvertimePolicy AS target
USING PolicyVersions AS source
ON target.Rfc=source.Rfc AND target.Code='MX-LFT-OT' AND target.EffectiveFrom=DATEFROMPARTS(source.[Year],1,1)
WHEN NOT MATCHED THEN INSERT
  (Rfc,Code,[Name],EffectiveFrom,EffectiveTo,WeeklyDoubleMinutes,WeeklyTripleMinutes,RequiresSupervisorApproval,RequiresReview,CreatedBy)
VALUES
  (source.Rfc,'MX-LFT-OT',CONCAT(N'Tiempo extra Mexico LFT ',source.[Year]),DATEFROMPARTS(source.[Year],1,1),
   CASE WHEN source.[Year]=2030 THEN NULL ELSE DATEFROMPARTS(source.[Year],12,31) END,
   source.DoubleHours*60,240,1,1,N'Migracion workforce MVP - requiere validacion RH');

MERGE rh.LeavePolicy AS target
USING
(
  SELECT t.Rfc,t.Id LeaveTypeId
  FROM rh.LeaveType t WHERE t.Code='VACACIONES'
) AS source
ON target.Rfc=source.Rfc AND target.Code='MX-VACACIONES' AND target.EffectiveFrom=CONVERT(date,'20260101')
WHEN NOT MATCHED THEN INSERT
  (Rfc,LeaveTypeId,Code,[Name],EffectiveFrom,AccrualMethod,AllowPartialDay,RequiresReview,CreatedBy)
VALUES
  (source.Rfc,source.LeaveTypeId,'MX-VACACIONES',N'Vacaciones legales Mexico',CONVERT(date,'20260101'),
   'MEXICO_STATUTORY',1,1,N'Migracion workforce MVP - requiere validacion RH');

COMMIT TRANSACTION;
GO
