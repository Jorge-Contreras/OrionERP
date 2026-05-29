SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET NOCOUNT ON;

IF COL_LENGTH('logistica.PhysicalCountSession', 'CanceledAt') IS NULL
BEGIN
    ALTER TABLE logistica.PhysicalCountSession
        ADD CanceledAt datetime2(0) NULL;
END;
GO

IF COL_LENGTH('logistica.PhysicalCountSession', 'CanceledBy') IS NULL
BEGIN
    ALTER TABLE logistica.PhysicalCountSession
        ADD CanceledBy varchar(256) NULL;
END;
GO

IF COL_LENGTH('logistica.PhysicalCountSession', 'CancelReason') IS NULL
BEGIN
    ALTER TABLE logistica.PhysicalCountSession
        ADD CancelReason varchar(1000) NULL;
END;
GO

IF OBJECT_ID('logistica.PhysicalCountRecountPlan', 'U') IS NULL
BEGIN
    CREATE TABLE logistica.PhysicalCountRecountPlan
    (
        Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_PhysicalCountRecountPlan PRIMARY KEY,
        SessionId int NOT NULL,
        RequestedAt datetime2(0) NOT NULL CONSTRAINT DF_PhysicalCountRecountPlan_RequestedAt DEFAULT (SYSUTCDATETIME()),
        RequestedBy varchar(256) NULL,
        CompletedAt datetime2(0) NULL,
        CompletedBy varchar(256) NULL,
        CONSTRAINT FK_PhysicalCountRecountPlan_Session
            FOREIGN KEY (SessionId) REFERENCES logistica.PhysicalCountSession (Id)
    );
END;
GO

IF OBJECT_ID('logistica.PhysicalCountRecountPlanLine', 'U') IS NULL
BEGIN
    CREATE TABLE logistica.PhysicalCountRecountPlanLine
    (
        Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_PhysicalCountRecountPlanLine PRIMARY KEY,
        RecountPlanId int NOT NULL,
        PhysicalCountLineId int NOT NULL,
        IssueCode varchar(50) NOT NULL,
        Reason varchar(1000) NOT NULL,
        PreviousCountedQuantity decimal(18,4) NULL,
        PreviousVarianceQuantity decimal(18,4) NULL,
        PreviousNotes varchar(1000) NULL,
        PreviousIsMissing bit NOT NULL CONSTRAINT DF_PhysicalCountRecountPlanLine_PreviousIsMissing DEFAULT (0),
        PreviousIsDamaged bit NOT NULL CONSTRAINT DF_PhysicalCountRecountPlanLine_PreviousIsDamaged DEFAULT (0),
        PreviousCapturedAt datetime2(0) NULL,
        PreviousCapturedBy varchar(256) NULL,
        CONSTRAINT FK_PhysicalCountRecountPlanLine_Plan
            FOREIGN KEY (RecountPlanId) REFERENCES logistica.PhysicalCountRecountPlan (Id),
        CONSTRAINT FK_PhysicalCountRecountPlanLine_Line
            FOREIGN KEY (PhysicalCountLineId) REFERENCES logistica.PhysicalCountLine (Id)
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_PhysicalCountRecountPlan_ActiveSession' AND object_id = OBJECT_ID('logistica.PhysicalCountRecountPlan'))
BEGIN
    CREATE UNIQUE INDEX UX_PhysicalCountRecountPlan_ActiveSession
        ON logistica.PhysicalCountRecountPlan (SessionId)
        WHERE CompletedAt IS NULL;
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_PhysicalCountRecountPlan_Status' AND object_id = OBJECT_ID('logistica.PhysicalCountRecountPlan'))
BEGIN
    CREATE INDEX IX_PhysicalCountRecountPlan_Status
        ON logistica.PhysicalCountRecountPlan (CompletedAt, RequestedAt DESC, SessionId);
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_PhysicalCountRecountPlanLine_Line' AND object_id = OBJECT_ID('logistica.PhysicalCountRecountPlanLine'))
BEGIN
    CREATE UNIQUE INDEX UX_PhysicalCountRecountPlanLine_Line
        ON logistica.PhysicalCountRecountPlanLine (RecountPlanId, PhysicalCountLineId);
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_PhysicalCountSession_StatusCreated' AND object_id = OBJECT_ID('logistica.PhysicalCountSession'))
BEGIN
    CREATE INDEX IX_PhysicalCountSession_StatusCreated
        ON logistica.PhysicalCountSession ([Status], CreatedAt DESC, Id DESC);
END;
GO
