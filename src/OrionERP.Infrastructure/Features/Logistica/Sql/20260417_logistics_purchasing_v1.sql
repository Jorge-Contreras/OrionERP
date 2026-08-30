SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET NOCOUNT ON;

IF OBJECT_ID('logistica.PurchaseOrder', 'U') IS NULL
BEGIN
    CREATE TABLE logistica.PurchaseOrder
    (
        Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_PurchaseOrder PRIMARY KEY,
        PurchaseOrderCode varchar(30) NOT NULL,
        BusinessPartnerId int NOT NULL,
        [Status] varchar(50) NOT NULL CONSTRAINT DF_PurchaseOrder_Status DEFAULT ('Draft'),
        OrderDate date NOT NULL,
        ExpectedDate date NULL,
        Notes varchar(1000) NULL,
        CreatedAt datetime2(0) NOT NULL CONSTRAINT DF_PurchaseOrder_CreatedAt DEFAULT (SYSUTCDATETIME()),
        CreatedBy varchar(256) NULL,
        UpdatedAt datetime2(0) NOT NULL CONSTRAINT DF_PurchaseOrder_UpdatedAt DEFAULT (SYSUTCDATETIME()),
        UpdatedBy varchar(256) NULL,
        IssuedAt datetime2(0) NULL,
        IssuedBy varchar(256) NULL,
        CompletedAt datetime2(0) NULL,
        CompletedBy varchar(256) NULL,
        CancelledAt datetime2(0) NULL,
        CancelledBy varchar(256) NULL,
        CONSTRAINT FK_PurchaseOrder_BusinessPartner
            FOREIGN KEY (BusinessPartnerId) REFERENCES dbo.BusinessPartner (Id)
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_PurchaseOrder_Code' AND object_id = OBJECT_ID('logistica.PurchaseOrder'))
BEGIN
    CREATE UNIQUE INDEX UX_PurchaseOrder_Code
        ON logistica.PurchaseOrder (PurchaseOrderCode);
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_PurchaseOrder_VendorStatusDate' AND object_id = OBJECT_ID('logistica.PurchaseOrder'))
BEGIN
    CREATE INDEX IX_PurchaseOrder_VendorStatusDate
        ON logistica.PurchaseOrder (BusinessPartnerId, [Status], OrderDate DESC, Id DESC);
END;
GO

IF OBJECT_ID('logistica.PurchaseOrderLine', 'U') IS NULL
BEGIN
    CREATE TABLE logistica.PurchaseOrderLine
    (
        Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_PurchaseOrderLine PRIMARY KEY,
        PurchaseOrderId int NOT NULL,
        MaterialId int NOT NULL,
        MaterialCodeSnapshot varchar(20) NOT NULL,
        MaterialDescriptionSnapshot varchar(800) NOT NULL,
        VendorCodeSnapshot varchar(100) NULL,
        BaseUnitNameSnapshot varchar(50) NULL,
        BaseUnitPrice decimal(18,6) NULL,
        OrderedQuantity decimal(18,4) NOT NULL CONSTRAINT DF_PurchaseOrderLine_OrderedQuantity DEFAULT (0),
        ReceivedQuantity decimal(18,4) NOT NULL CONSTRAINT DF_PurchaseOrderLine_ReceivedQuantity DEFAULT (0),
        CreatedAt datetime2(0) NOT NULL CONSTRAINT DF_PurchaseOrderLine_CreatedAt DEFAULT (SYSUTCDATETIME()),
        UpdatedAt datetime2(0) NOT NULL CONSTRAINT DF_PurchaseOrderLine_UpdatedAt DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT FK_PurchaseOrderLine_Order
            FOREIGN KEY (PurchaseOrderId) REFERENCES logistica.PurchaseOrder (Id),
        CONSTRAINT FK_PurchaseOrderLine_Material
            FOREIGN KEY (MaterialId) REFERENCES logistica.Material (Id)
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_PurchaseOrderLine_OrderMaterial' AND object_id = OBJECT_ID('logistica.PurchaseOrderLine'))
BEGIN
    CREATE UNIQUE INDEX UX_PurchaseOrderLine_OrderMaterial
        ON logistica.PurchaseOrderLine (PurchaseOrderId, MaterialId);
END;
GO

IF OBJECT_ID('logistica.PurchaseOrderLineAllocation', 'U') IS NULL
BEGIN
    CREATE TABLE logistica.PurchaseOrderLineAllocation
    (
        Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_PurchaseOrderLineAllocation PRIMARY KEY,
        PurchaseOrderLineId int NOT NULL,
        LocationId int NOT NULL,
        PlannedQuantity decimal(18,4) NOT NULL CONSTRAINT DF_PurchaseOrderLineAllocation_PlannedQuantity DEFAULT (0),
        ReceivedQuantity decimal(18,4) NOT NULL CONSTRAINT DF_PurchaseOrderLineAllocation_ReceivedQuantity DEFAULT (0),
        CreatedAt datetime2(0) NOT NULL CONSTRAINT DF_PurchaseOrderLineAllocation_CreatedAt DEFAULT (SYSUTCDATETIME()),
        UpdatedAt datetime2(0) NOT NULL CONSTRAINT DF_PurchaseOrderLineAllocation_UpdatedAt DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT FK_PurchaseOrderLineAllocation_Line
            FOREIGN KEY (PurchaseOrderLineId) REFERENCES logistica.PurchaseOrderLine (Id),
        CONSTRAINT FK_PurchaseOrderLineAllocation_Location
            FOREIGN KEY (LocationId) REFERENCES logistica.Location (Id)
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_PurchaseOrderLineAllocation_LineLocation' AND object_id = OBJECT_ID('logistica.PurchaseOrderLineAllocation'))
BEGIN
    CREATE UNIQUE INDEX UX_PurchaseOrderLineAllocation_LineLocation
        ON logistica.PurchaseOrderLineAllocation (PurchaseOrderLineId, LocationId);
END;
GO

IF OBJECT_ID('logistica.PurchaseReceipt', 'U') IS NULL
BEGIN
    CREATE TABLE logistica.PurchaseReceipt
    (
        Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_PurchaseReceipt PRIMARY KEY,
        PurchaseOrderId int NOT NULL,
        ReceiptCode varchar(30) NOT NULL,
        ReceiptDate date NOT NULL,
        Notes varchar(1000) NULL,
        CreatedAt datetime2(0) NOT NULL CONSTRAINT DF_PurchaseReceipt_CreatedAt DEFAULT (SYSUTCDATETIME()),
        CreatedBy varchar(256) NULL,
        CONSTRAINT FK_PurchaseReceipt_Order
            FOREIGN KEY (PurchaseOrderId) REFERENCES logistica.PurchaseOrder (Id)
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_PurchaseReceipt_Code' AND object_id = OBJECT_ID('logistica.PurchaseReceipt'))
BEGIN
    CREATE UNIQUE INDEX UX_PurchaseReceipt_Code
        ON logistica.PurchaseReceipt (ReceiptCode);
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_PurchaseReceipt_OrderDate' AND object_id = OBJECT_ID('logistica.PurchaseReceipt'))
BEGIN
    CREATE INDEX IX_PurchaseReceipt_OrderDate
        ON logistica.PurchaseReceipt (PurchaseOrderId, ReceiptDate DESC, Id DESC);
END;
GO

IF OBJECT_ID('logistica.PurchaseReceiptLine', 'U') IS NULL
BEGIN
    CREATE TABLE logistica.PurchaseReceiptLine
    (
        Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_PurchaseReceiptLine PRIMARY KEY,
        PurchaseReceiptId int NOT NULL,
        PurchaseOrderLineAllocationId int NOT NULL,
        PurchaseOrderLineId int NOT NULL,
        LocationId int NOT NULL,
        MaterialId int NOT NULL,
        Quantity decimal(18,4) NOT NULL,
        SubtotalAmount decimal(18,2) NULL,
        IvaAmount decimal(18,2) NULL,
        TotalAmount decimal(18,2) NULL,
        IncludesIva bit NOT NULL CONSTRAINT DF_PurchaseReceiptLine_IncludesIva DEFAULT (0),
        CreatedAt datetime2(0) NOT NULL CONSTRAINT DF_PurchaseReceiptLine_CreatedAt DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT FK_PurchaseReceiptLine_Receipt
            FOREIGN KEY (PurchaseReceiptId) REFERENCES logistica.PurchaseReceipt (Id),
        CONSTRAINT FK_PurchaseReceiptLine_Allocation
            FOREIGN KEY (PurchaseOrderLineAllocationId) REFERENCES logistica.PurchaseOrderLineAllocation (Id),
        CONSTRAINT FK_PurchaseReceiptLine_Line
            FOREIGN KEY (PurchaseOrderLineId) REFERENCES logistica.PurchaseOrderLine (Id),
        CONSTRAINT FK_PurchaseReceiptLine_Location
            FOREIGN KEY (LocationId) REFERENCES logistica.Location (Id),
        CONSTRAINT FK_PurchaseReceiptLine_Material
            FOREIGN KEY (MaterialId) REFERENCES logistica.Material (Id)
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_PurchaseReceiptLine_Receipt' AND object_id = OBJECT_ID('logistica.PurchaseReceiptLine'))
BEGIN
    CREATE INDEX IX_PurchaseReceiptLine_Receipt
        ON logistica.PurchaseReceiptLine (PurchaseReceiptId, Id);
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_PurchaseReceiptLine_Allocation' AND object_id = OBJECT_ID('logistica.PurchaseReceiptLine'))
BEGIN
    CREATE INDEX IX_PurchaseReceiptLine_Allocation
        ON logistica.PurchaseReceiptLine (PurchaseOrderLineAllocationId, Id);
END;
GO
