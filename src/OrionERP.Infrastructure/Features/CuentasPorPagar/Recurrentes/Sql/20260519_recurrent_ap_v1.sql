SET ANSI_NULLS ON;
GO
SET QUOTED_IDENTIFIER ON;
GO
SET XACT_ABORT ON;
GO

IF SCHEMA_ID('AP') IS NULL
BEGIN
  EXEC('CREATE SCHEMA AP');
END;
GO

IF OBJECT_ID('AP.RecurringPayable', 'U') IS NULL
BEGIN
  CREATE TABLE AP.RecurringPayable
  (
    Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_AP_RecurringPayable PRIMARY KEY,
    Rfc varchar(50) NOT NULL,
    [Name] nvarchar(200) NOT NULL,
    BusinessPartnerId int NULL,
    PayeeNameSnapshot nvarchar(200) NULL,
    PayeeRfcSnapshot varchar(50) NULL,
    Category nvarchar(80) NULL,
    [Description] nvarchar(1000) NULL,
    FrequencyUnit varchar(20) NOT NULL,
    IntervalCount int NOT NULL CONSTRAINT DF_AP_RecurringPayable_IntervalCount DEFAULT (1),
    StartDate date NOT NULL,
    EndDate date NULL,
    DueDayOfMonth int NULL,
    DueMonth int NULL,
    ExpectedAmount decimal(18,2) NULL,
    Currency char(3) NOT NULL CONSTRAINT DF_AP_RecurringPayable_Currency DEFAULT ('MXN'),
    IsActive bit NOT NULL CONSTRAINT DF_AP_RecurringPayable_IsActive DEFAULT (1),
    CreatedAt datetime2(0) NOT NULL CONSTRAINT DF_AP_RecurringPayable_CreatedAt DEFAULT SYSUTCDATETIME(),
    CreatedBy nvarchar(256) NULL,
    UpdatedAt datetime2(0) NULL,
    UpdatedBy nvarchar(256) NULL,
    CONSTRAINT FK_AP_RecurringPayable_BusinessPartner FOREIGN KEY (BusinessPartnerId) REFERENCES dbo.BusinessPartner (Id),
    CONSTRAINT CK_AP_RecurringPayable_FrequencyUnit CHECK (FrequencyUnit IN ('Days','Weeks','Months','Years')),
    CONSTRAINT CK_AP_RecurringPayable_IntervalCount CHECK (IntervalCount >= 1 AND IntervalCount <= 120),
    CONSTRAINT CK_AP_RecurringPayable_DueDay CHECK (DueDayOfMonth IS NULL OR DueDayOfMonth BETWEEN 1 AND 31),
    CONSTRAINT CK_AP_RecurringPayable_DueMonth CHECK (DueMonth IS NULL OR DueMonth BETWEEN 1 AND 12),
    CONSTRAINT CK_AP_RecurringPayable_ExpectedAmount CHECK (ExpectedAmount IS NULL OR ExpectedAmount >= 0)
  );

  CREATE INDEX IX_AP_RecurringPayable_RfcActiveName ON AP.RecurringPayable (Rfc, IsActive, [Name]);
  CREATE INDEX IX_AP_RecurringPayable_BusinessPartner ON AP.RecurringPayable (BusinessPartnerId) WHERE BusinessPartnerId IS NOT NULL;
END;
GO

IF OBJECT_ID('AP.PayableOccurrence', 'U') IS NULL
BEGIN
  CREATE TABLE AP.PayableOccurrence
  (
    Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_AP_PayableOccurrence PRIMARY KEY,
    RecurringPayableId int NOT NULL,
    Rfc varchar(50) NOT NULL,
    PeriodStartDate date NOT NULL,
    DueDate date NOT NULL,
    ExpectedAmount decimal(18,2) NULL,
    ActualPaidAmount decimal(18,2) NOT NULL CONSTRAINT DF_AP_PayableOccurrence_ActualPaidAmount DEFAULT (0),
    [Status] varchar(30) NOT NULL CONSTRAINT DF_AP_PayableOccurrence_Status DEFAULT ('Pending'),
    PaymentDate date NULL,
    Notes nvarchar(1000) NULL,
    CreatedAt datetime2(0) NOT NULL CONSTRAINT DF_AP_PayableOccurrence_CreatedAt DEFAULT SYSUTCDATETIME(),
    UpdatedAt datetime2(0) NULL,
    UpdatedBy nvarchar(256) NULL,
    CONSTRAINT FK_AP_PayableOccurrence_RecurringPayable FOREIGN KEY (RecurringPayableId) REFERENCES AP.RecurringPayable (Id),
    CONSTRAINT CK_AP_PayableOccurrence_Status CHECK ([Status] IN ('Pending','PartiallyPaid','Paid','Skipped','Cancelled')),
    CONSTRAINT CK_AP_PayableOccurrence_ExpectedAmount CHECK (ExpectedAmount IS NULL OR ExpectedAmount >= 0),
    CONSTRAINT CK_AP_PayableOccurrence_ActualPaidAmount CHECK (ActualPaidAmount >= 0)
  );

  CREATE UNIQUE INDEX UX_AP_PayableOccurrence_PayableDueDate ON AP.PayableOccurrence (RecurringPayableId, DueDate);
  CREATE INDEX IX_AP_PayableOccurrence_RfcStatusDueDate ON AP.PayableOccurrence (Rfc, [Status], DueDate, Id);
  CREATE INDEX IX_AP_PayableOccurrence_RfcDueDate ON AP.PayableOccurrence (Rfc, DueDate, Id);
END;
GO

IF OBJECT_ID('AP.OccurrencePayment', 'U') IS NULL
BEGIN
  CREATE TABLE AP.OccurrencePayment
  (
    Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_AP_OccurrencePayment PRIMARY KEY,
    OccurrenceId int NOT NULL,
    Rfc varchar(50) NOT NULL,
    TransaccionId int NULL,
    Amount decimal(18,2) NOT NULL,
    PaymentDate date NOT NULL,
    Notes nvarchar(1000) NULL,
    CreatedAt datetime2(0) NOT NULL CONSTRAINT DF_AP_OccurrencePayment_CreatedAt DEFAULT SYSUTCDATETIME(),
    CreatedBy nvarchar(256) NULL,
    CONSTRAINT FK_AP_OccurrencePayment_Occurrence FOREIGN KEY (OccurrenceId) REFERENCES AP.PayableOccurrence (Id) ON DELETE CASCADE,
    CONSTRAINT FK_AP_OccurrencePayment_Transaccion FOREIGN KEY (TransaccionId) REFERENCES dbo.Transacciones (ID),
    CONSTRAINT CK_AP_OccurrencePayment_Amount CHECK (Amount >= 0)
  );

  CREATE UNIQUE INDEX UX_AP_OccurrencePayment_OccurrenceTransaccion
    ON AP.OccurrencePayment (OccurrenceId, TransaccionId)
    WHERE TransaccionId IS NOT NULL;
  CREATE INDEX IX_AP_OccurrencePayment_Transaccion ON AP.OccurrencePayment (TransaccionId) WHERE TransaccionId IS NOT NULL;
  CREATE INDEX IX_AP_OccurrencePayment_RfcDate ON AP.OccurrencePayment (Rfc, PaymentDate DESC, Id DESC);
END;
GO

IF OBJECT_ID('AP.OccurrenceAttachment', 'U') IS NULL
BEGIN
  CREATE TABLE AP.OccurrenceAttachment
  (
    Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_AP_OccurrenceAttachment PRIMARY KEY,
    OccurrenceId int NOT NULL,
    Rfc varchar(50) NOT NULL,
    FileName nvarchar(260) NOT NULL,
    ContentType varchar(120) NOT NULL,
    Content varbinary(max) NOT NULL,
    SizeBytes bigint NOT NULL,
    UploadedAt datetime2(0) NOT NULL CONSTRAINT DF_AP_OccurrenceAttachment_UploadedAt DEFAULT SYSUTCDATETIME(),
    UploadedBy nvarchar(256) NULL,
    DeletedAt datetime2(0) NULL,
    DeletedBy nvarchar(256) NULL,
    CONSTRAINT FK_AP_OccurrenceAttachment_Occurrence FOREIGN KEY (OccurrenceId) REFERENCES AP.PayableOccurrence (Id) ON DELETE CASCADE,
    CONSTRAINT CK_AP_OccurrenceAttachment_SizeBytes CHECK (SizeBytes >= 0)
  );

  CREATE INDEX IX_AP_OccurrenceAttachment_Occurrence ON AP.OccurrenceAttachment (OccurrenceId, DeletedAt, UploadedAt DESC);
  CREATE INDEX IX_AP_OccurrenceAttachment_Rfc ON AP.OccurrenceAttachment (Rfc, DeletedAt, UploadedAt DESC);
END;
GO

IF OBJECT_ID('AP.AuditLog', 'U') IS NULL
BEGIN
  CREATE TABLE AP.AuditLog
  (
    Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_AP_AuditLog PRIMARY KEY,
    Rfc varchar(50) NOT NULL,
    EntityType varchar(50) NOT NULL,
    EntityId int NOT NULL,
    EventName varchar(80) NOT NULL,
    Detail nvarchar(2000) NULL,
    CreatedAt datetime2(0) NOT NULL CONSTRAINT DF_AP_AuditLog_CreatedAt DEFAULT SYSUTCDATETIME(),
    CreatedBy nvarchar(256) NULL
  );

  CREATE INDEX IX_AP_AuditLog_RfcEntity ON AP.AuditLog (Rfc, EntityType, EntityId, CreatedAt DESC, Id DESC);
END;
GO

IF OBJECT_ID('auth.AspNetRoles', 'U') IS NOT NULL
BEGIN
  INSERT INTO auth.AspNetRoles (Id, [Name], NormalizedName, ConcurrencyStamp)
  SELECT CONVERT(nvarchar(450), NEWID()), roleName, UPPER(roleName), CONVERT(nvarchar(max), NEWID())
  FROM (VALUES (N'APAdmin'), (N'APOperator'), (N'APReadOnly')) AS roles(roleName)
  WHERE NOT EXISTS (
    SELECT 1
    FROM auth.AspNetRoles existing
    WHERE existing.NormalizedName = UPPER(roleName)
  );
END
ELSE IF OBJECT_ID('dbo.AspNetRoles', 'U') IS NOT NULL
BEGIN
  INSERT INTO dbo.AspNetRoles (Id, [Name], NormalizedName, ConcurrencyStamp)
  SELECT CONVERT(nvarchar(450), NEWID()), roleName, UPPER(roleName), CONVERT(nvarchar(max), NEWID())
  FROM (VALUES (N'APAdmin'), (N'APOperator'), (N'APReadOnly')) AS roles(roleName)
  WHERE NOT EXISTS (
    SELECT 1
    FROM dbo.AspNetRoles existing
    WHERE existing.NormalizedName = UPPER(roleName)
  );
END;
GO
