SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET NOCOUNT ON;

IF OBJECT_ID('dbo.BusinessPartner', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.BusinessPartner
    (
        Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_BusinessPartner PRIMARY KEY,
        LegacyProveedorId int NULL,
        PartnerName varchar(200) NOT NULL,
        Rfc varchar(50) NULL,
        Email varchar(100) NULL,
        Phone varchar(50) NULL,
        Street varchar(100) NULL,
        Neighborhood varchar(50) NULL,
        City varchar(50) NULL,
        [State] varchar(50) NULL,
        PostalCode varchar(20) NULL,
        BusinessLine varchar(100) NULL,
        Notes varchar(700) NULL,
        IsActive bit NOT NULL CONSTRAINT DF_BusinessPartner_IsActive DEFAULT (1),
        CreatedAt datetime2(0) NOT NULL CONSTRAINT DF_BusinessPartner_CreatedAt DEFAULT (SYSUTCDATETIME()),
        UpdatedAt datetime2(0) NOT NULL CONSTRAINT DF_BusinessPartner_UpdatedAt DEFAULT (SYSUTCDATETIME())
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_BusinessPartner_LegacyProveedorId' AND object_id = OBJECT_ID('dbo.BusinessPartner'))
BEGIN
    CREATE UNIQUE INDEX UX_BusinessPartner_LegacyProveedorId
        ON dbo.BusinessPartner (LegacyProveedorId)
        WHERE LegacyProveedorId IS NOT NULL;
END;
GO

IF OBJECT_ID('dbo.BusinessPartnerRole', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.BusinessPartnerRole
    (
        Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_BusinessPartnerRole PRIMARY KEY,
        BusinessPartnerId int NOT NULL,
        RoleCode varchar(50) NOT NULL,
        CreatedAt datetime2(0) NOT NULL CONSTRAINT DF_BusinessPartnerRole_CreatedAt DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT FK_BusinessPartnerRole_BusinessPartner
            FOREIGN KEY (BusinessPartnerId) REFERENCES dbo.BusinessPartner (Id)
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_BusinessPartnerRole_PartnerRole' AND object_id = OBJECT_ID('dbo.BusinessPartnerRole'))
BEGIN
    CREATE UNIQUE INDEX UX_BusinessPartnerRole_PartnerRole
        ON dbo.BusinessPartnerRole (BusinessPartnerId, RoleCode);
END;
GO

IF OBJECT_ID('dbo.LegacyPartnerCategoryMapping', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.LegacyPartnerCategoryMapping
    (
        LegacyValue varchar(50) NOT NULL CONSTRAINT PK_LegacyPartnerCategoryMapping PRIMARY KEY,
        RoleCode varchar(50) NOT NULL,
        CreateVendorProfile bit NOT NULL CONSTRAINT DF_LegacyPartnerCategoryMapping_CreateVendorProfile DEFAULT (0),
        Notes varchar(250) NULL
    );
END;
GO

IF OBJECT_ID('logistica.UnitOfMeasure', 'U') IS NULL
BEGIN
    CREATE TABLE logistica.UnitOfMeasure
    (
        Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_UnitOfMeasure PRIMARY KEY,
        LegacyUnitId int NULL,
        UnitName varchar(50) NOT NULL,
        Abbreviation varchar(10) NULL,
        [Description] varchar(200) NULL,
        IsActive bit NOT NULL CONSTRAINT DF_UnitOfMeasure_IsActive DEFAULT (1)
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_UnitOfMeasure_LegacyUnitId' AND object_id = OBJECT_ID('logistica.UnitOfMeasure'))
BEGIN
    CREATE UNIQUE INDEX UX_UnitOfMeasure_LegacyUnitId
        ON logistica.UnitOfMeasure (LegacyUnitId)
        WHERE LegacyUnitId IS NOT NULL;
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_UnitOfMeasure_UnitName' AND object_id = OBJECT_ID('logistica.UnitOfMeasure'))
BEGIN
    CREATE UNIQUE INDEX UX_UnitOfMeasure_UnitName
        ON logistica.UnitOfMeasure (UnitName);
END;
GO

IF OBJECT_ID('logistica.LegacyUnitMapping', 'U') IS NULL
BEGIN
    CREATE TABLE logistica.LegacyUnitMapping
    (
        LegacyValue varchar(50) NOT NULL CONSTRAINT PK_LegacyUnitMapping PRIMARY KEY,
        CanonicalUnitName varchar(50) NOT NULL,
        Notes varchar(200) NULL
    );
END;
GO

IF OBJECT_ID('logistica.MaterialCategory', 'U') IS NULL
BEGIN
    CREATE TABLE logistica.MaterialCategory
    (
        Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_MaterialCategory PRIMARY KEY,
        LegacyCategoryId int NULL,
        CategoryName varchar(100) NOT NULL,
        [Description] varchar(200) NULL,
        IsActive bit NOT NULL CONSTRAINT DF_MaterialCategory_IsActive DEFAULT (1)
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_MaterialCategory_LegacyCategoryId' AND object_id = OBJECT_ID('logistica.MaterialCategory'))
BEGIN
    CREATE UNIQUE INDEX UX_MaterialCategory_LegacyCategoryId
        ON logistica.MaterialCategory (LegacyCategoryId)
        WHERE LegacyCategoryId IS NOT NULL;
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_MaterialCategory_CategoryName' AND object_id = OBJECT_ID('logistica.MaterialCategory'))
BEGIN
    CREATE UNIQUE INDEX UX_MaterialCategory_CategoryName
        ON logistica.MaterialCategory (CategoryName);
END;
GO

IF OBJECT_ID('logistica.LegacyMaterialCategoryMapping', 'U') IS NULL
BEGIN
    CREATE TABLE logistica.LegacyMaterialCategoryMapping
    (
        LegacyValue varchar(100) NOT NULL CONSTRAINT PK_LegacyMaterialCategoryMapping PRIMARY KEY,
        CanonicalCategoryName varchar(100) NOT NULL,
        MaterialClass varchar(50) NOT NULL,
        Notes varchar(250) NULL
    );
END;
GO

IF OBJECT_ID('logistica.Material', 'U') IS NULL
BEGIN
    CREATE TABLE logistica.Material
    (
        Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Material PRIMARY KEY,
        MaterialCode varchar(20) NOT NULL,
        LegacyMaterialId int NULL,
        [Description] varchar(800) NOT NULL,
        BaseUnitId int NOT NULL,
        PurchaseQuantity decimal(18,4) NOT NULL CONSTRAINT DF_Material_PurchaseQuantity DEFAULT (1),
        PurchaseUnitId int NULL,
        BusinessPartnerId int NULL,
        BaseUnitPrice decimal(18,6) NULL,
        CreatedDate date NOT NULL CONSTRAINT DF_Material_CreatedDate DEFAULT (CONVERT(date, SYSUTCDATETIME())),
        UpdatedDate date NOT NULL CONSTRAINT DF_Material_UpdatedDate DEFAULT (CONVERT(date, SYSUTCDATETIME())),
        Brand varchar(50) NULL,
        Model varchar(100) NULL,
        IsPerishable bit NOT NULL CONSTRAINT DF_Material_IsPerishable DEFAULT (0),
        ShelfLifeDays int NULL,
        RequiresRefrigeration bit NOT NULL CONSTRAINT DF_Material_RequiresRefrigeration DEFAULT (0),
        MaterialStatus varchar(50) NOT NULL CONSTRAINT DF_Material_Status DEFAULT ('ACTIVO'),
        CategoryId int NULL,
        Barcode varchar(50) NULL,
        VendorCode varchar(100) NULL,
        PrimaryImage varbinary(max) NULL,
        PrimaryImageFileName varchar(200) NULL,
        PrimaryImageContentType varchar(100) NULL,
        PrimaryImageThumbnail varbinary(max) NULL,
        PrimaryImageThumbnailContentType varchar(100) NULL,
        PurchaseLink varchar(max) NULL,
        MaterialClass varchar(50) NOT NULL CONSTRAINT DF_Material_Class DEFAULT ('Consumable'),
        IsActive bit NOT NULL CONSTRAINT DF_Material_IsActive DEFAULT (1),
        CONSTRAINT FK_Material_BaseUnit FOREIGN KEY (BaseUnitId) REFERENCES logistica.UnitOfMeasure (Id),
        CONSTRAINT FK_Material_PurchaseUnit FOREIGN KEY (PurchaseUnitId) REFERENCES logistica.UnitOfMeasure (Id),
        CONSTRAINT FK_Material_BusinessPartner FOREIGN KEY (BusinessPartnerId) REFERENCES dbo.BusinessPartner (Id),
        CONSTRAINT FK_Material_Category FOREIGN KEY (CategoryId) REFERENCES logistica.MaterialCategory (Id)
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_Material_MaterialCode' AND object_id = OBJECT_ID('logistica.Material'))
BEGIN
    CREATE UNIQUE INDEX UX_Material_MaterialCode ON logistica.Material (MaterialCode);
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_Material_LegacyMaterialId' AND object_id = OBJECT_ID('logistica.Material'))
BEGIN
    CREATE UNIQUE INDEX UX_Material_LegacyMaterialId
        ON logistica.Material (LegacyMaterialId)
        WHERE LegacyMaterialId IS NOT NULL;
END;
GO

IF OBJECT_ID('logistica.VendorProfile', 'U') IS NULL
BEGIN
    CREATE TABLE logistica.VendorProfile
    (
        BusinessPartnerId int NOT NULL CONSTRAINT PK_VendorProfile PRIMARY KEY,
        PaymentTerms varchar(100) NULL,
        DefaultLeadTimeDays int NULL,
        IsApproved bit NOT NULL CONSTRAINT DF_VendorProfile_IsApproved DEFAULT (1),
        Notes varchar(500) NULL,
        CreatedAt datetime2(0) NOT NULL CONSTRAINT DF_VendorProfile_CreatedAt DEFAULT (SYSUTCDATETIME()),
        UpdatedAt datetime2(0) NOT NULL CONSTRAINT DF_VendorProfile_UpdatedAt DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT FK_VendorProfile_BusinessPartner
            FOREIGN KEY (BusinessPartnerId) REFERENCES dbo.BusinessPartner (Id)
    );
END;
GO

IF OBJECT_ID('logistica.Location', 'U') IS NULL
BEGIN
    CREATE TABLE logistica.Location
    (
        Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Location PRIMARY KEY,
        LocationCode varchar(50) NOT NULL,
        LegacyEspacioId int NULL,
        LegacyRoomId int NULL,
        ParentLocationId int NULL,
        RoomId int NULL,
        LocationName varchar(200) NOT NULL,
        LocationType varchar(50) NOT NULL,
        [Description] varchar(500) NULL,
        IsInventoryEnabled bit NOT NULL CONSTRAINT DF_Location_IsInventoryEnabled DEFAULT (1),
        IsActive bit NOT NULL CONSTRAINT DF_Location_IsActive DEFAULT (1),
        CreatedAt datetime2(0) NOT NULL CONSTRAINT DF_Location_CreatedAt DEFAULT (SYSUTCDATETIME()),
        UpdatedAt datetime2(0) NOT NULL CONSTRAINT DF_Location_UpdatedAt DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT FK_Location_ParentLocation FOREIGN KEY (ParentLocationId) REFERENCES logistica.Location (Id),
        CONSTRAINT FK_Location_Room FOREIGN KEY (RoomId) REFERENCES dbo.ROOM (ID)
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_Location_LocationCode' AND object_id = OBJECT_ID('logistica.Location'))
BEGIN
    CREATE UNIQUE INDEX UX_Location_LocationCode ON logistica.Location (LocationCode);
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_Location_LegacyEspacioId' AND object_id = OBJECT_ID('logistica.Location'))
BEGIN
    CREATE UNIQUE INDEX UX_Location_LegacyEspacioId
        ON logistica.Location (LegacyEspacioId)
        WHERE LegacyEspacioId IS NOT NULL;
END;
GO

IF OBJECT_ID('logistica.StockBalance', 'U') IS NULL
BEGIN
    CREATE TABLE logistica.StockBalance
    (
        Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_StockBalance PRIMARY KEY,
        LocationId int NOT NULL,
        MaterialId int NOT NULL,
        Quantity decimal(18,4) NOT NULL CONSTRAINT DF_StockBalance_Quantity DEFAULT (0),
        LastCountedAt datetime2(0) NULL,
        MaxQuantity decimal(18,4) NULL,
        MinQuantity decimal(18,4) NULL,
        CountFrequencyDays int NULL,
        LastPurchaseDate date NULL,
        Notes varchar(max) NULL,
        IsRemoved bit NOT NULL CONSTRAINT DF_StockBalance_IsRemoved DEFAULT (0),
        RemovedAt datetime2(0) NULL,
        RemovedBy varchar(256) NULL,
        CreatedAt datetime2(0) NOT NULL CONSTRAINT DF_StockBalance_CreatedAt DEFAULT (SYSUTCDATETIME()),
        UpdatedAt datetime2(0) NOT NULL CONSTRAINT DF_StockBalance_UpdatedAt DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT FK_StockBalance_Location FOREIGN KEY (LocationId) REFERENCES logistica.Location (Id),
        CONSTRAINT FK_StockBalance_Material FOREIGN KEY (MaterialId) REFERENCES logistica.Material (Id)
    );
END;
GO

IF COL_LENGTH('logistica.StockBalance', 'IsRemoved') IS NULL
BEGIN
    ALTER TABLE logistica.StockBalance
        ADD IsRemoved bit NOT NULL CONSTRAINT DF_StockBalance_IsRemoved DEFAULT (0) WITH VALUES;
END;
GO

IF COL_LENGTH('logistica.StockBalance', 'RemovedAt') IS NULL
BEGIN
    ALTER TABLE logistica.StockBalance
        ADD RemovedAt datetime2(0) NULL;
END;
GO

IF COL_LENGTH('logistica.StockBalance', 'RemovedBy') IS NULL
BEGIN
    ALTER TABLE logistica.StockBalance
        ADD RemovedBy varchar(256) NULL;
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_StockBalance_LocationMaterial' AND object_id = OBJECT_ID('logistica.StockBalance'))
BEGIN
    CREATE UNIQUE INDEX UX_StockBalance_LocationMaterial
        ON logistica.StockBalance (LocationId, MaterialId);
END;
GO

IF OBJECT_ID('logistica.StockTransaction', 'U') IS NULL
BEGIN
    CREATE TABLE logistica.StockTransaction
    (
        Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_StockTransaction PRIMARY KEY,
        StockBalanceId int NOT NULL,
        LocationId int NOT NULL,
        MaterialId int NOT NULL,
        TransactionType varchar(50) NOT NULL,
        QuantityDelta decimal(18,4) NOT NULL,
        QuantityAfter decimal(18,4) NOT NULL,
        ReferenceType varchar(50) NULL,
        ReferenceId int NULL,
        Notes varchar(1000) NULL,
        PerformedBy varchar(256) NULL,
        OccurredAt datetime2(0) NOT NULL CONSTRAINT DF_StockTransaction_OccurredAt DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT FK_StockTransaction_StockBalance FOREIGN KEY (StockBalanceId) REFERENCES logistica.StockBalance (Id),
        CONSTRAINT FK_StockTransaction_Location FOREIGN KEY (LocationId) REFERENCES logistica.Location (Id),
        CONSTRAINT FK_StockTransaction_Material FOREIGN KEY (MaterialId) REFERENCES logistica.Material (Id)
    );
END;
GO

IF OBJECT_ID('logistica.LocationMaterialAttachment', 'U') IS NULL
BEGIN
    CREATE TABLE logistica.LocationMaterialAttachment
    (
        Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_LocationMaterialAttachment PRIMARY KEY,
        LocationId int NOT NULL,
        MaterialId int NOT NULL,
        LegacyInventoryAttachmentId int NULL,
        LegacyInventoryId int NULL,
        FileName varchar(200) NOT NULL,
        FileExtension varchar(50) NOT NULL,
        ContentType varchar(100) NULL,
        [Description] varchar(500) NULL,
        Attachment varbinary(max) NOT NULL,
        IsDeleted bit NOT NULL CONSTRAINT DF_LocationMaterialAttachment_IsDeleted DEFAULT (0),
        DeletedAt datetime2(0) NULL,
        DeletedBy varchar(256) NULL,
        CreatedAt datetime2(0) NOT NULL CONSTRAINT DF_LocationMaterialAttachment_CreatedAt DEFAULT (SYSUTCDATETIME()),
        CreatedBy varchar(256) NULL,
        CONSTRAINT FK_LocationMaterialAttachment_Location FOREIGN KEY (LocationId) REFERENCES logistica.Location (Id),
        CONSTRAINT FK_LocationMaterialAttachment_Material FOREIGN KEY (MaterialId) REFERENCES logistica.Material (Id)
    );
END;
GO

IF COL_LENGTH('logistica.LocationMaterialAttachment', 'IsDeleted') IS NULL
BEGIN
    ALTER TABLE logistica.LocationMaterialAttachment
        ADD IsDeleted bit NOT NULL CONSTRAINT DF_LocationMaterialAttachment_IsDeleted DEFAULT (0) WITH VALUES;
END;
GO

IF COL_LENGTH('logistica.LocationMaterialAttachment', 'DeletedAt') IS NULL
BEGIN
    ALTER TABLE logistica.LocationMaterialAttachment
        ADD DeletedAt datetime2(0) NULL;
END;
GO

IF COL_LENGTH('logistica.LocationMaterialAttachment', 'DeletedBy') IS NULL
BEGIN
    ALTER TABLE logistica.LocationMaterialAttachment
        ADD DeletedBy varchar(256) NULL;
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_LocationMaterialAttachment_LegacyInventoryAttachmentId' AND object_id = OBJECT_ID('logistica.LocationMaterialAttachment'))
BEGIN
    CREATE UNIQUE INDEX UX_LocationMaterialAttachment_LegacyInventoryAttachmentId
        ON logistica.LocationMaterialAttachment (LegacyInventoryAttachmentId)
        WHERE LegacyInventoryAttachmentId IS NOT NULL;
END;
GO

IF OBJECT_ID('logistica.PhysicalCountSession', 'U') IS NULL
BEGIN
    CREATE TABLE logistica.PhysicalCountSession
    (
        Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_PhysicalCountSession PRIMARY KEY,
        SessionCode varchar(30) NOT NULL,
        LocationId int NOT NULL,
        [Status] varchar(50) NOT NULL CONSTRAINT DF_PhysicalCountSession_Status DEFAULT ('Draft'),
        Notes varchar(1000) NULL,
        CreatedAt datetime2(0) NOT NULL CONSTRAINT DF_PhysicalCountSession_CreatedAt DEFAULT (SYSUTCDATETIME()),
        CreatedBy varchar(256) NULL,
        SubmittedAt datetime2(0) NULL,
        SubmittedBy varchar(256) NULL,
        ApprovedAt datetime2(0) NULL,
        ApprovedBy varchar(256) NULL,
        PostedAt datetime2(0) NULL,
        PostedBy varchar(256) NULL,
        CanceledAt datetime2(0) NULL,
        CanceledBy varchar(256) NULL,
        CancelReason varchar(1000) NULL,
        CONSTRAINT FK_PhysicalCountSession_Location FOREIGN KEY (LocationId) REFERENCES logistica.Location (Id)
    );
END;
GO

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

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_PhysicalCountSession_SessionCode' AND object_id = OBJECT_ID('logistica.PhysicalCountSession'))
BEGIN
    CREATE UNIQUE INDEX UX_PhysicalCountSession_SessionCode
        ON logistica.PhysicalCountSession (SessionCode);
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_PhysicalCountSession_StatusCreated' AND object_id = OBJECT_ID('logistica.PhysicalCountSession'))
BEGIN
    CREATE INDEX IX_PhysicalCountSession_StatusCreated
        ON logistica.PhysicalCountSession ([Status], CreatedAt DESC, Id DESC);
END;
GO

IF OBJECT_ID('logistica.PhysicalCountLine', 'U') IS NULL
BEGIN
    CREATE TABLE logistica.PhysicalCountLine
    (
        Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_PhysicalCountLine PRIMARY KEY,
        SessionId int NOT NULL,
        StockBalanceId int NOT NULL,
        LocationId int NOT NULL,
        MaterialId int NOT NULL,
        ExpectedQuantity decimal(18,4) NOT NULL,
        CountedQuantity decimal(18,4) NULL,
        VarianceQuantity decimal(18,4) NULL,
        Notes varchar(1000) NULL,
        IsMissing bit NOT NULL CONSTRAINT DF_PhysicalCountLine_IsMissing DEFAULT (0),
        IsDamaged bit NOT NULL CONSTRAINT DF_PhysicalCountLine_IsDamaged DEFAULT (0),
        CapturedAt datetime2(0) NULL,
        CapturedBy varchar(256) NULL,
        CONSTRAINT FK_PhysicalCountLine_Session FOREIGN KEY (SessionId) REFERENCES logistica.PhysicalCountSession (Id),
        CONSTRAINT FK_PhysicalCountLine_StockBalance FOREIGN KEY (StockBalanceId) REFERENCES logistica.StockBalance (Id),
        CONSTRAINT FK_PhysicalCountLine_Location FOREIGN KEY (LocationId) REFERENCES logistica.Location (Id),
        CONSTRAINT FK_PhysicalCountLine_Material FOREIGN KEY (MaterialId) REFERENCES logistica.Material (Id)
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_PhysicalCountLine_SessionStockBalance' AND object_id = OBJECT_ID('logistica.PhysicalCountLine'))
BEGIN
    CREATE UNIQUE INDEX UX_PhysicalCountLine_SessionStockBalance
        ON logistica.PhysicalCountLine (SessionId, StockBalanceId);
END;
GO

IF OBJECT_ID('logistica.PhysicalCountAttachment', 'U') IS NULL
BEGIN
    CREATE TABLE logistica.PhysicalCountAttachment
    (
        Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_PhysicalCountAttachment PRIMARY KEY,
        PhysicalCountLineId int NOT NULL,
        FileName varchar(200) NOT NULL,
        FileExtension varchar(50) NOT NULL,
        ContentType varchar(100) NULL,
        [Description] varchar(500) NULL,
        Attachment varbinary(max) NOT NULL,
        CreatedAt datetime2(0) NOT NULL CONSTRAINT DF_PhysicalCountAttachment_CreatedAt DEFAULT (SYSUTCDATETIME()),
        CreatedBy varchar(256) NULL,
        CONSTRAINT FK_PhysicalCountAttachment_Line FOREIGN KEY (PhysicalCountLineId) REFERENCES logistica.PhysicalCountLine (Id)
    );
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

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_PhysicalCountRecountPlanLine_Line' AND object_id = OBJECT_ID('logistica.PhysicalCountRecountPlanLine'))
BEGIN
    CREATE UNIQUE INDEX UX_PhysicalCountRecountPlanLine_Line
        ON logistica.PhysicalCountRecountPlanLine (RecountPlanId, PhysicalCountLineId);
END;
GO

IF OBJECT_ID('logistica.MigrationIssue', 'U') IS NULL
BEGIN
    CREATE TABLE logistica.MigrationIssue
    (
        Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_MigrationIssue PRIMARY KEY,
        IssueType varchar(100) NOT NULL,
        SourceTable varchar(100) NOT NULL,
        LegacyKey varchar(100) NOT NULL,
        IssueDescription varchar(1000) NOT NULL,
        CreatedAt datetime2(0) NOT NULL CONSTRAINT DF_MigrationIssue_CreatedAt DEFAULT (SYSUTCDATETIME())
    );
END;
GO
