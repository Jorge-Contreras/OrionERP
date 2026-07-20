SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET XACT_ABORT ON;
SET NOCOUNT ON;

BEGIN TRANSACTION;

IF COL_LENGTH('logistica.Material', 'ProductType') IS NULL
  ALTER TABLE logistica.Material ADD ProductType varchar(30) NOT NULL CONSTRAINT DF_Material_ProductType DEFAULT ('RawMaterial') WITH VALUES;
IF COL_LENGTH('logistica.Material', 'FulfillmentMode') IS NULL
  ALTER TABLE logistica.Material ADD FulfillmentMode varchar(30) NOT NULL CONSTRAINT DF_Material_FulfillmentMode DEFAULT ('StockItem') WITH VALUES;
IF COL_LENGTH('logistica.Material', 'TrackLots') IS NULL
  ALTER TABLE logistica.Material ADD TrackLots bit NOT NULL CONSTRAINT DF_Material_TrackLots DEFAULT (0) WITH VALUES;
IF COL_LENGTH('logistica.Material', 'DefaultYieldQuantity') IS NULL
  ALTER TABLE logistica.Material ADD DefaultYieldQuantity decimal(18,6) NULL;

IF COL_LENGTH('logistica.StockBalance', 'ReservedQuantity') IS NULL
  ALTER TABLE logistica.StockBalance ADD ReservedQuantity decimal(18,4) NOT NULL CONSTRAINT DF_StockBalance_ReservedQuantity DEFAULT (0) WITH VALUES;
IF COL_LENGTH('logistica.StockBalance', 'AverageUnitCost') IS NULL
  ALTER TABLE logistica.StockBalance ADD AverageUnitCost decimal(18,6) NOT NULL CONSTRAINT DF_StockBalance_AverageUnitCost DEFAULT (0) WITH VALUES;

GO

IF OBJECT_ID('logistica.UnitConversion', 'U') IS NULL
BEGIN
  CREATE TABLE logistica.UnitConversion
  (
    Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_UnitConversion PRIMARY KEY,
    FromUnitId int NOT NULL,
    ToUnitId int NOT NULL,
    Dimension varchar(20) NOT NULL,
    Factor decimal(24,10) NOT NULL,
    IsActive bit NOT NULL CONSTRAINT DF_UnitConversion_IsActive DEFAULT (1),
    CONSTRAINT CK_UnitConversion_Factor CHECK (Factor > 0),
    CONSTRAINT FK_UnitConversion_FromUnit FOREIGN KEY (FromUnitId) REFERENCES logistica.UnitOfMeasure (Id),
    CONSTRAINT FK_UnitConversion_ToUnit FOREIGN KEY (ToUnitId) REFERENCES logistica.UnitOfMeasure (Id),
    CONSTRAINT UX_UnitConversion UNIQUE (FromUnitId, ToUnitId)
  );
END;

IF OBJECT_ID('logistica.MaterialUnitConversion', 'U') IS NULL
BEGIN
  CREATE TABLE logistica.MaterialUnitConversion
  (
    Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_MaterialUnitConversion PRIMARY KEY,
    Rfc varchar(50) NOT NULL,
    MaterialId int NOT NULL,
    FromUnitId int NOT NULL,
    ToUnitId int NOT NULL,
    Factor decimal(24,10) NOT NULL,
    Notes varchar(500) NULL,
    IsActive bit NOT NULL CONSTRAINT DF_MaterialUnitConversion_IsActive DEFAULT (1),
    CONSTRAINT CK_MaterialUnitConversion_Factor CHECK (Factor > 0),
    CONSTRAINT FK_MaterialUnitConversion_Material_Rfc FOREIGN KEY (Rfc, MaterialId) REFERENCES logistica.Material (Rfc, Id),
    CONSTRAINT FK_MaterialUnitConversion_FromUnit FOREIGN KEY (FromUnitId) REFERENCES logistica.UnitOfMeasure (Id),
    CONSTRAINT FK_MaterialUnitConversion_ToUnit FOREIGN KEY (ToUnitId) REFERENCES logistica.UnitOfMeasure (Id),
    CONSTRAINT UX_MaterialUnitConversion UNIQUE (Rfc, MaterialId, FromUnitId, ToUnitId)
  );
END;

IF OBJECT_ID('logistica.MaterialLot', 'U') IS NULL
BEGIN
  CREATE TABLE logistica.MaterialLot
  (
    Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_MaterialLot PRIMARY KEY,
    Rfc varchar(50) NOT NULL,
    MaterialId int NOT NULL,
    LotCode varchar(80) NOT NULL,
    ManufacturedAt date NULL,
    ExpiresAt date NULL,
    UnitCost decimal(18,6) NOT NULL CONSTRAINT DF_MaterialLot_UnitCost DEFAULT (0),
    SourceType varchar(30) NOT NULL,
    SourceId bigint NULL,
    IsBlocked bit NOT NULL CONSTRAINT DF_MaterialLot_IsBlocked DEFAULT (0),
    CreatedAt datetime2(0) NOT NULL CONSTRAINT DF_MaterialLot_CreatedAt DEFAULT (SYSUTCDATETIME()),
    CreatedBy varchar(256) NULL,
    CONSTRAINT FK_MaterialLot_Material_Rfc FOREIGN KEY (Rfc, MaterialId) REFERENCES logistica.Material (Rfc, Id),
    CONSTRAINT UX_MaterialLot UNIQUE (Rfc, MaterialId, LotCode)
  );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('logistica.MaterialLot') AND name = 'UX_MaterialLot_RfcId')
  CREATE UNIQUE INDEX UX_MaterialLot_RfcId ON logistica.MaterialLot (Rfc, Id);

IF COL_LENGTH('logistica.PurchaseReceiptLine', 'MaterialLotId') IS NULL
  ALTER TABLE logistica.PurchaseReceiptLine ADD MaterialLotId bigint NULL;
IF COL_LENGTH('logistica.PurchaseReceiptLine', 'UnitCost') IS NULL
  ALTER TABLE logistica.PurchaseReceiptLine ADD UnitCost decimal(18,6) NULL;
IF OBJECT_ID('logistica.FK_PurchaseReceiptLine_Lot_Rfc', 'F') IS NULL
  ALTER TABLE logistica.PurchaseReceiptLine ADD CONSTRAINT FK_PurchaseReceiptLine_Lot_Rfc FOREIGN KEY (Rfc, MaterialLotId) REFERENCES logistica.MaterialLot (Rfc, Id);

IF OBJECT_ID('logistica.LotBalance', 'U') IS NULL
BEGIN
  CREATE TABLE logistica.LotBalance
  (
    Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_LotBalance PRIMARY KEY,
    Rfc varchar(50) NOT NULL,
    MaterialLotId bigint NOT NULL,
    MaterialId int NOT NULL,
    LocationId int NOT NULL,
    Quantity decimal(18,4) NOT NULL CONSTRAINT DF_LotBalance_Quantity DEFAULT (0),
    ReservedQuantity decimal(18,4) NOT NULL CONSTRAINT DF_LotBalance_ReservedQuantity DEFAULT (0),
    UpdatedAt datetime2(0) NOT NULL CONSTRAINT DF_LotBalance_UpdatedAt DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT CK_LotBalance_Reserved CHECK (ReservedQuantity >= 0),
    CONSTRAINT FK_LotBalance_Lot_Rfc FOREIGN KEY (Rfc, MaterialLotId) REFERENCES logistica.MaterialLot (Rfc, Id),
    CONSTRAINT FK_LotBalance_Material_Rfc FOREIGN KEY (Rfc, MaterialId) REFERENCES logistica.Material (Rfc, Id),
    CONSTRAINT FK_LotBalance_Location_Rfc FOREIGN KEY (Rfc, LocationId) REFERENCES logistica.Location (Rfc, Id),
    CONSTRAINT UX_LotBalance UNIQUE (Rfc, MaterialLotId, LocationId)
  );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('logistica.LotBalance') AND name = 'IX_LotBalance_Fefo')
  CREATE INDEX IX_LotBalance_Fefo ON logistica.LotBalance (Rfc, MaterialId, LocationId, Quantity, ReservedQuantity, MaterialLotId);

IF OBJECT_ID('logistica.InventoryReservation', 'U') IS NULL
BEGIN
  CREATE TABLE logistica.InventoryReservation
  (
    Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_InventoryReservation PRIMARY KEY,
    Rfc varchar(50) NOT NULL,
    SiteId int NULL,
    ReferenceType varchar(30) NOT NULL,
    ReferenceId uniqueidentifier NOT NULL,
    IdempotencyKey varchar(100) NOT NULL,
    Status varchar(20) NOT NULL,
    CreatedAt datetime2(0) NOT NULL CONSTRAINT DF_InventoryReservation_CreatedAt DEFAULT (SYSUTCDATETIME()),
    ReleasedAt datetime2(0) NULL,
    ConsumedAt datetime2(0) NULL,
    CreatedBy varchar(256) NULL,
    CONSTRAINT UX_InventoryReservation_Idempotency UNIQUE (Rfc, IdempotencyKey)
  );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('logistica.InventoryReservation') AND name = 'UX_InventoryReservation_RfcId')
  CREATE UNIQUE INDEX UX_InventoryReservation_RfcId ON logistica.InventoryReservation (Rfc, Id);

IF OBJECT_ID('logistica.InventoryReservationLine', 'U') IS NULL
BEGIN
  CREATE TABLE logistica.InventoryReservationLine
  (
    Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_InventoryReservationLine PRIMARY KEY,
    Rfc varchar(50) NOT NULL,
    ReservationId bigint NOT NULL,
    MaterialId int NOT NULL,
    LocationId int NOT NULL,
    MaterialLotId bigint NULL,
    RequiredQuantity decimal(18,4) NOT NULL,
    ReservedQuantity decimal(18,4) NOT NULL,
    ConsumedQuantity decimal(18,4) NOT NULL CONSTRAINT DF_InventoryReservationLine_Consumed DEFAULT (0),
    IsDeficit bit NOT NULL CONSTRAINT DF_InventoryReservationLine_IsDeficit DEFAULT (0),
    FrozenUnitCost decimal(18,6) NOT NULL CONSTRAINT DF_InventoryReservationLine_FrozenCost DEFAULT (0),
    CONSTRAINT CK_InventoryReservationLine_Quantity CHECK (RequiredQuantity > 0 AND ReservedQuantity >= 0),
    CONSTRAINT FK_InventoryReservationLine_Header_Rfc FOREIGN KEY (Rfc, ReservationId) REFERENCES logistica.InventoryReservation (Rfc, Id),
    CONSTRAINT FK_InventoryReservationLine_Material_Rfc FOREIGN KEY (Rfc, MaterialId) REFERENCES logistica.Material (Rfc, Id),
    CONSTRAINT FK_InventoryReservationLine_Location_Rfc FOREIGN KEY (Rfc, LocationId) REFERENCES logistica.Location (Rfc, Id),
    CONSTRAINT FK_InventoryReservationLine_Lot_Rfc FOREIGN KEY (Rfc, MaterialLotId) REFERENCES logistica.MaterialLot (Rfc, Id)
  );
END;

IF OBJECT_ID('logistica.InventoryTransfer', 'U') IS NULL
BEGIN
  CREATE TABLE logistica.InventoryTransfer
  (
    Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_InventoryTransfer PRIMARY KEY,
    Rfc varchar(50) NOT NULL,
    TransferCode varchar(30) NOT NULL,
    FromLocationId int NOT NULL,
    ToLocationId int NOT NULL,
    Status varchar(20) NOT NULL,
    Reason varchar(500) NULL,
    CreatedAt datetime2(0) NOT NULL CONSTRAINT DF_InventoryTransfer_CreatedAt DEFAULT (SYSUTCDATETIME()),
    CreatedBy varchar(256) NULL,
    PostedAt datetime2(0) NULL,
    PostedBy varchar(256) NULL,
    CONSTRAINT CK_InventoryTransfer_Locations CHECK (FromLocationId <> ToLocationId),
    CONSTRAINT FK_InventoryTransfer_From_Rfc FOREIGN KEY (Rfc, FromLocationId) REFERENCES logistica.Location (Rfc, Id),
    CONSTRAINT FK_InventoryTransfer_To_Rfc FOREIGN KEY (Rfc, ToLocationId) REFERENCES logistica.Location (Rfc, Id),
    CONSTRAINT UX_InventoryTransfer_Code UNIQUE (Rfc, TransferCode)
  );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('logistica.InventoryTransfer') AND name = 'UX_InventoryTransfer_RfcId')
  CREATE UNIQUE INDEX UX_InventoryTransfer_RfcId ON logistica.InventoryTransfer (Rfc, Id);

IF OBJECT_ID('logistica.InventoryTransferLine', 'U') IS NULL
BEGIN
  CREATE TABLE logistica.InventoryTransferLine
  (
    Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_InventoryTransferLine PRIMARY KEY,
    Rfc varchar(50) NOT NULL,
    TransferId bigint NOT NULL,
    MaterialId int NOT NULL,
    MaterialLotId bigint NULL,
    Quantity decimal(18,4) NOT NULL,
    CONSTRAINT CK_InventoryTransferLine_Quantity CHECK (Quantity > 0),
    CONSTRAINT FK_InventoryTransferLine_Header_Rfc FOREIGN KEY (Rfc, TransferId) REFERENCES logistica.InventoryTransfer (Rfc, Id),
    CONSTRAINT FK_InventoryTransferLine_Material_Rfc FOREIGN KEY (Rfc, MaterialId) REFERENCES logistica.Material (Rfc, Id),
    CONSTRAINT FK_InventoryTransferLine_Lot_Rfc FOREIGN KEY (Rfc, MaterialLotId) REFERENCES logistica.MaterialLot (Rfc, Id)
  );
END;

IF OBJECT_ID('logistica.InventoryAdjustment', 'U') IS NULL
BEGIN
  CREATE TABLE logistica.InventoryAdjustment
  (
    Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_InventoryAdjustment PRIMARY KEY,
    Rfc varchar(50) NOT NULL,
    AdjustmentCode varchar(30) NOT NULL,
    AdjustmentType varchar(20) NOT NULL,
    Status varchar(20) NOT NULL,
    ReasonCode varchar(30) NOT NULL,
    Reason varchar(1000) NOT NULL,
    Evidence varbinary(max) NULL,
    EvidenceFileName varchar(200) NULL,
    CreatedAt datetime2(0) NOT NULL CONSTRAINT DF_InventoryAdjustment_CreatedAt DEFAULT (SYSUTCDATETIME()),
    CreatedBy varchar(256) NULL,
    ApprovedAt datetime2(0) NULL,
    ApprovedBy varchar(256) NULL,
    CONSTRAINT UX_InventoryAdjustment_Code UNIQUE (Rfc, AdjustmentCode)
  );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('logistica.InventoryAdjustment') AND name = 'UX_InventoryAdjustment_RfcId')
  CREATE UNIQUE INDEX UX_InventoryAdjustment_RfcId ON logistica.InventoryAdjustment (Rfc, Id);

IF OBJECT_ID('logistica.InventoryAdjustmentLine', 'U') IS NULL
BEGIN
  CREATE TABLE logistica.InventoryAdjustmentLine
  (
    Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_InventoryAdjustmentLine PRIMARY KEY,
    Rfc varchar(50) NOT NULL,
    AdjustmentId bigint NOT NULL,
    MaterialId int NOT NULL,
    LocationId int NOT NULL,
    MaterialLotId bigint NULL,
    QuantityDelta decimal(18,4) NOT NULL,
    FrozenUnitCost decimal(18,6) NOT NULL CONSTRAINT DF_InventoryAdjustmentLine_Cost DEFAULT (0),
    CONSTRAINT CK_InventoryAdjustmentLine_Quantity CHECK (QuantityDelta <> 0),
    CONSTRAINT FK_InventoryAdjustmentLine_Header_Rfc FOREIGN KEY (Rfc, AdjustmentId) REFERENCES logistica.InventoryAdjustment (Rfc, Id),
    CONSTRAINT FK_InventoryAdjustmentLine_Material_Rfc FOREIGN KEY (Rfc, MaterialId) REFERENCES logistica.Material (Rfc, Id),
    CONSTRAINT FK_InventoryAdjustmentLine_Location_Rfc FOREIGN KEY (Rfc, LocationId) REFERENCES logistica.Location (Rfc, Id),
    CONSTRAINT FK_InventoryAdjustmentLine_Lot_Rfc FOREIGN KEY (Rfc, MaterialLotId) REFERENCES logistica.MaterialLot (Rfc, Id)
  );
END;

IF OBJECT_ID('logistica.BomHeader', 'U') IS NULL
BEGIN
  CREATE TABLE logistica.BomHeader
  (
    Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_BomHeader PRIMARY KEY,
    Rfc varchar(50) NOT NULL,
    ProductMaterialId int NOT NULL,
    BomCode varchar(50) NOT NULL,
    [Name] varchar(200) NOT NULL,
    IsActive bit NOT NULL CONSTRAINT DF_BomHeader_IsActive DEFAULT (1),
    CreatedAt datetime2(0) NOT NULL CONSTRAINT DF_BomHeader_CreatedAt DEFAULT (SYSUTCDATETIME()),
    CreatedBy varchar(256) NULL,
    CONSTRAINT FK_BomHeader_Product_Rfc FOREIGN KEY (Rfc, ProductMaterialId) REFERENCES logistica.Material (Rfc, Id),
    CONSTRAINT UX_BomHeader_Code UNIQUE (Rfc, BomCode),
    CONSTRAINT UX_BomHeader_Product UNIQUE (Rfc, ProductMaterialId)
  );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('logistica.BomHeader') AND name = 'UX_BomHeader_RfcId')
  CREATE UNIQUE INDEX UX_BomHeader_RfcId ON logistica.BomHeader (Rfc, Id);

IF OBJECT_ID('logistica.BomVersion', 'U') IS NULL
BEGIN
  CREATE TABLE logistica.BomVersion
  (
    Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_BomVersion PRIMARY KEY,
    Rfc varchar(50) NOT NULL,
    BomHeaderId bigint NOT NULL,
    VersionNumber int NOT NULL,
    [Status] varchar(20) NOT NULL,
    YieldQuantity decimal(18,6) NOT NULL,
    YieldUnitId int NOT NULL,
    ExpectedWastePercent decimal(9,4) NOT NULL CONSTRAINT DF_BomVersion_Waste DEFAULT (0),
    FrozenTheoreticalCost decimal(18,6) NULL,
    EffectiveFrom datetime2(0) NULL,
    RetiredAt datetime2(0) NULL,
    CreatedAt datetime2(0) NOT NULL CONSTRAINT DF_BomVersion_CreatedAt DEFAULT (SYSUTCDATETIME()),
    CreatedBy varchar(256) NULL,
    CONSTRAINT CK_BomVersion_Yield CHECK (YieldQuantity > 0),
    CONSTRAINT CK_BomVersion_Waste CHECK (ExpectedWastePercent >= 0 AND ExpectedWastePercent <= 100),
    CONSTRAINT FK_BomVersion_Header_Rfc FOREIGN KEY (Rfc, BomHeaderId) REFERENCES logistica.BomHeader (Rfc, Id),
    CONSTRAINT FK_BomVersion_YieldUnit FOREIGN KEY (YieldUnitId) REFERENCES logistica.UnitOfMeasure (Id),
    CONSTRAINT UX_BomVersion_Number UNIQUE (Rfc, BomHeaderId, VersionNumber)
  );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('logistica.BomVersion') AND name = 'UX_BomVersion_RfcId')
  CREATE UNIQUE INDEX UX_BomVersion_RfcId ON logistica.BomVersion (Rfc, Id);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('logistica.BomVersion') AND name = 'UX_BomVersion_Active')
  CREATE UNIQUE INDEX UX_BomVersion_Active ON logistica.BomVersion (Rfc, BomHeaderId) WHERE [Status] = 'Active';

IF OBJECT_ID('logistica.BomComponent', 'U') IS NULL
BEGIN
  CREATE TABLE logistica.BomComponent
  (
    Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_BomComponent PRIMARY KEY,
    Rfc varchar(50) NOT NULL,
    BomVersionId bigint NOT NULL,
    ComponentMaterialId int NOT NULL,
    Quantity decimal(18,6) NOT NULL,
    UnitId int NOT NULL,
    ExpectedWastePercent decimal(9,4) NOT NULL CONSTRAINT DF_BomComponent_Waste DEFAULT (0),
    IsOptional bit NOT NULL CONSTRAINT DF_BomComponent_Optional DEFAULT (0),
    SortOrder int NOT NULL CONSTRAINT DF_BomComponent_Sort DEFAULT (0),
    CONSTRAINT CK_BomComponent_Quantity CHECK (Quantity > 0),
    CONSTRAINT CK_BomComponent_Waste CHECK (ExpectedWastePercent >= 0 AND ExpectedWastePercent <= 100),
    CONSTRAINT FK_BomComponent_Version_Rfc FOREIGN KEY (Rfc, BomVersionId) REFERENCES logistica.BomVersion (Rfc, Id),
    CONSTRAINT FK_BomComponent_Material_Rfc FOREIGN KEY (Rfc, ComponentMaterialId) REFERENCES logistica.Material (Rfc, Id),
    CONSTRAINT FK_BomComponent_Unit FOREIGN KEY (UnitId) REFERENCES logistica.UnitOfMeasure (Id),
    CONSTRAINT UX_BomComponent UNIQUE (Rfc, BomVersionId, ComponentMaterialId)
  );
END;

IF OBJECT_ID('logistica.Recipe', 'U') IS NULL
BEGIN
  CREATE TABLE logistica.Recipe
  (
    Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_Recipe PRIMARY KEY,
    Rfc varchar(50) NOT NULL,
    BomVersionId bigint NOT NULL,
    [Name] varchar(200) NOT NULL,
    SafetyNotes varchar(2000) NULL,
    IsActive bit NOT NULL CONSTRAINT DF_Recipe_IsActive DEFAULT (1),
    CONSTRAINT FK_Recipe_BomVersion_Rfc FOREIGN KEY (Rfc, BomVersionId) REFERENCES logistica.BomVersion (Rfc, Id),
    CONSTRAINT UX_Recipe_BomVersion UNIQUE (Rfc, BomVersionId)
  );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('logistica.Recipe') AND name = 'UX_Recipe_RfcId')
  CREATE UNIQUE INDEX UX_Recipe_RfcId ON logistica.Recipe (Rfc, Id);

IF OBJECT_ID('logistica.RecipeStep', 'U') IS NULL
BEGIN
  CREATE TABLE logistica.RecipeStep
  (
    Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_RecipeStep PRIMARY KEY,
    Rfc varchar(50) NOT NULL,
    RecipeId bigint NOT NULL,
    StepNumber int NOT NULL,
    Instruction varchar(2000) NOT NULL,
    DurationMinutes int NULL,
    TemperatureC decimal(8,2) NULL,
    Equipment varchar(300) NULL,
    Image varbinary(max) NULL,
    ImageFileName varchar(200) NULL,
    ImageContentType varchar(100) NULL,
    CONSTRAINT CK_RecipeStep_Number CHECK (StepNumber > 0),
    CONSTRAINT FK_RecipeStep_Recipe_Rfc FOREIGN KEY (Rfc, RecipeId) REFERENCES logistica.Recipe (Rfc, Id),
    CONSTRAINT UX_RecipeStep_Number UNIQUE (Rfc, RecipeId, StepNumber)
  );
END;

IF OBJECT_ID('logistica.Allergen', 'U') IS NULL
BEGIN
  CREATE TABLE logistica.Allergen
  (
    Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Allergen PRIMARY KEY,
    Code varchar(40) NOT NULL CONSTRAINT UX_Allergen_Code UNIQUE,
    [Name] varchar(100) NOT NULL,
    IsActive bit NOT NULL CONSTRAINT DF_Allergen_IsActive DEFAULT (1)
  );
END;

IF OBJECT_ID('logistica.MaterialAllergen', 'U') IS NULL
BEGIN
  CREATE TABLE logistica.MaterialAllergen
  (
    Rfc varchar(50) NOT NULL,
    MaterialId int NOT NULL,
    AllergenId int NOT NULL,
    CONSTRAINT PK_MaterialAllergen PRIMARY KEY (Rfc, MaterialId, AllergenId),
    CONSTRAINT FK_MaterialAllergen_Material_Rfc FOREIGN KEY (Rfc, MaterialId) REFERENCES logistica.Material (Rfc, Id),
    CONSTRAINT FK_MaterialAllergen_Allergen FOREIGN KEY (AllergenId) REFERENCES logistica.Allergen (Id)
  );
END;

;WITH StandardAllergens(Code, [Name]) AS
(
  SELECT * FROM (VALUES
    ('GLUTEN', 'Gluten'), ('CRUSTACEOS', 'Crustaceos'), ('HUEVO', 'Huevo'),
    ('PESCADO', 'Pescado'), ('CACAHUATE', 'Cacahuate'), ('SOYA', 'Soya'),
    ('LECHE', 'Leche'), ('NUECES', 'Nueces de arbol'), ('APIO', 'Apio'),
    ('MOSTAZA', 'Mostaza'), ('AJONJOLI', 'Ajonjoli'), ('SULFITOS', 'Sulfitos'),
    ('ALTRAMUZ', 'Altramuz'), ('MOLUSCOS', 'Moluscos')
  ) sourceInfo(Code, [Name])
)
INSERT INTO logistica.Allergen (Code, [Name])
SELECT sourceInfo.Code, sourceInfo.[Name]
FROM StandardAllergens sourceInfo
WHERE NOT EXISTS (SELECT 1 FROM logistica.Allergen existing WHERE existing.Code=sourceInfo.Code);

;WITH StandardConversions(FromCode, ToCode, Dimension, Factor) AS
(
  SELECT * FROM (VALUES
    ('GR', 'KG', 'Mass', CAST(0.001 AS decimal(24,10))),
    ('KG', 'GR', 'Mass', CAST(1000 AS decimal(24,10))),
    ('KG', 'TON', 'Mass', CAST(0.001 AS decimal(24,10))),
    ('TON', 'KG', 'Mass', CAST(1000 AS decimal(24,10))),
    ('ML', 'LT', 'Volume', CAST(0.001 AS decimal(24,10))),
    ('LT', 'ML', 'Volume', CAST(1000 AS decimal(24,10))),
    ('GAL', 'LT', 'Volume', CAST(3.785411784 AS decimal(24,10))),
    ('LT', 'GAL', 'Volume', CAST(0.2641720524 AS decimal(24,10))),
    ('CM', 'M', 'Length', CAST(0.01 AS decimal(24,10))),
    ('M', 'CM', 'Length', CAST(100 AS decimal(24,10)))
  ) sourceInfo(FromCode, ToCode, Dimension, Factor)
)
INSERT INTO logistica.UnitConversion (FromUnitId, ToUnitId, Dimension, Factor)
SELECT fromUnit.Id, toUnit.Id, sourceInfo.Dimension, sourceInfo.Factor
FROM StandardConversions sourceInfo
JOIN logistica.UnitOfMeasure fromUnit ON fromUnit.Abbreviation=sourceInfo.FromCode
JOIN logistica.UnitOfMeasure toUnit ON toUnit.Abbreviation=sourceInfo.ToCode
WHERE NOT EXISTS
(
  SELECT 1 FROM logistica.UnitConversion existing
  WHERE existing.FromUnitId=fromUnit.Id AND existing.ToUnitId=toUnit.Id
);

IF OBJECT_ID('logistica.ProductionOrder', 'U') IS NULL
BEGIN
  CREATE TABLE logistica.ProductionOrder
  (
    Id uniqueidentifier NOT NULL CONSTRAINT PK_ProductionOrder PRIMARY KEY,
    Rfc varchar(50) NOT NULL,
    SiteId int NOT NULL,
    ProductionCode varchar(30) NOT NULL,
    ProductMaterialId int NOT NULL,
    BomVersionId bigint NOT NULL,
    PlannedQuantity decimal(18,4) NOT NULL,
    ActualQuantity decimal(18,4) NULL,
    UnitId int NOT NULL,
    OutputLocationId int NOT NULL,
    OutputLotId bigint NULL,
    ReservationId bigint NULL,
    Status varchar(20) NOT NULL,
    FrozenTheoreticalCost decimal(18,6) NOT NULL CONSTRAINT DF_ProductionOrder_Cost DEFAULT (0),
    WasteQuantity decimal(18,4) NULL,
    PlannedAt datetime2(0) NOT NULL,
    StartedAt datetime2(0) NULL,
    CompletedAt datetime2(0) NULL,
    CreatedBy varchar(256) NULL,
    CompletedBy varchar(256) NULL,
    CONSTRAINT CK_ProductionOrder_Planned CHECK (PlannedQuantity > 0),
    CONSTRAINT FK_ProductionOrder_Product_Rfc FOREIGN KEY (Rfc, ProductMaterialId) REFERENCES logistica.Material (Rfc, Id),
    CONSTRAINT FK_ProductionOrder_Bom_Rfc FOREIGN KEY (Rfc, BomVersionId) REFERENCES logistica.BomVersion (Rfc, Id),
    CONSTRAINT FK_ProductionOrder_Location_Rfc FOREIGN KEY (Rfc, OutputLocationId) REFERENCES logistica.Location (Rfc, Id),
    CONSTRAINT FK_ProductionOrder_Lot_Rfc FOREIGN KEY (Rfc, OutputLotId) REFERENCES logistica.MaterialLot (Rfc, Id),
    CONSTRAINT FK_ProductionOrder_Reservation_Rfc FOREIGN KEY (Rfc, ReservationId) REFERENCES logistica.InventoryReservation (Rfc, Id),
    CONSTRAINT FK_ProductionOrder_Unit FOREIGN KEY (UnitId) REFERENCES logistica.UnitOfMeasure (Id),
    CONSTRAINT UX_ProductionOrder_Code UNIQUE (Rfc, ProductionCode)
  );
END;

IF OBJECT_ID('logistica.PhysicalCountLotLine', 'U') IS NULL
BEGIN
  CREATE TABLE logistica.PhysicalCountLotLine
  (
    Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_PhysicalCountLotLine PRIMARY KEY,
    Rfc varchar(50) NOT NULL,
    PhysicalCountLineId int NOT NULL,
    MaterialLotId bigint NOT NULL,
    ExpectedQuantity decimal(18,4) NOT NULL,
    CountedQuantity decimal(18,4) NULL,
    VarianceQuantity AS (CASE WHEN CountedQuantity IS NULL THEN NULL ELSE CountedQuantity - ExpectedQuantity END),
    CONSTRAINT FK_PhysicalCountLotLine_Line_Rfc FOREIGN KEY (Rfc, PhysicalCountLineId) REFERENCES logistica.PhysicalCountLine (Rfc, Id),
    CONSTRAINT FK_PhysicalCountLotLine_Lot_Rfc FOREIGN KEY (Rfc, MaterialLotId) REFERENCES logistica.MaterialLot (Rfc, Id),
    CONSTRAINT UX_PhysicalCountLotLine UNIQUE (Rfc, PhysicalCountLineId, MaterialLotId)
  );
END;

COMMIT TRANSACTION;
