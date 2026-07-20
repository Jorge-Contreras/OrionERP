SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET XACT_ABORT ON;
SET NOCOUNT ON;

IF SCHEMA_ID('restaurante') IS NULL EXEC('CREATE SCHEMA restaurante');
GO

BEGIN TRANSACTION;

IF OBJECT_ID('restaurante.Site', 'U') IS NULL
BEGIN
  CREATE TABLE restaurante.Site
  (
    Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_RestaurantSite PRIMARY KEY,
    Rfc varchar(50) NOT NULL,
    SiteCode varchar(30) NOT NULL,
    [Name] varchar(150) NOT NULL,
    TimeZoneId varchar(100) NOT NULL CONSTRAINT DF_RestaurantSite_TimeZone DEFAULT ('Central Standard Time (Mexico)'),
    OperationalDayCutoff time(0) NOT NULL CONSTRAINT DF_RestaurantSite_Cutoff DEFAULT ('04:00'),
    CurrencyCode char(3) NOT NULL CONSTRAINT DF_RestaurantSite_Currency DEFAULT ('MXN'),
    TaxRate decimal(9,6) NOT NULL CONSTRAINT DF_RestaurantSite_Tax DEFAULT (0.160000),
    PricesIncludeTax bit NOT NULL CONSTRAINT DF_RestaurantSite_TaxIncluded DEFAULT (1),
    IsEnabled bit NOT NULL CONSTRAINT DF_RestaurantSite_Enabled DEFAULT (0),
    AllowSupervisorDeficit bit NOT NULL CONSTRAINT DF_RestaurantSite_Deficit DEFAULT (0),
    CrossContaminationWarning varchar(300) NOT NULL CONSTRAINT DF_RestaurantSite_CrossWarning DEFAULT ('Puede existir contaminación cruzada. Consulte al personal si tiene alergias.'),
    CreatedAt datetime2(0) NOT NULL CONSTRAINT DF_RestaurantSite_Created DEFAULT (SYSUTCDATETIME()),
    UpdatedAt datetime2(0) NOT NULL CONSTRAINT DF_RestaurantSite_Updated DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT CK_RestaurantSite_Tax CHECK (TaxRate >= 0 AND TaxRate <= 1),
    CONSTRAINT UX_RestaurantSite_Code UNIQUE (Rfc, SiteCode)
  );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('restaurante.Site') AND name = 'UX_RestaurantSite_RfcId')
  CREATE UNIQUE INDEX UX_RestaurantSite_RfcId ON restaurante.Site (Rfc, Id);

IF OBJECT_ID('restaurante.SiteLocationPriority', 'U') IS NULL
BEGIN
  CREATE TABLE restaurante.SiteLocationPriority
  (
    Rfc varchar(50) NOT NULL,
    SiteId int NOT NULL,
    StationCode varchar(30) NOT NULL CONSTRAINT DF_SiteLocationPriority_Station DEFAULT ('GENERAL'),
    LocationId int NOT NULL,
    Priority int NOT NULL,
    CONSTRAINT PK_SiteLocationPriority PRIMARY KEY (Rfc, SiteId, StationCode, LocationId),
    CONSTRAINT FK_SiteLocationPriority_Site_Rfc FOREIGN KEY (Rfc, SiteId) REFERENCES restaurante.Site (Rfc, Id),
    CONSTRAINT FK_SiteLocationPriority_Location_Rfc FOREIGN KEY (Rfc, LocationId) REFERENCES logistica.Location (Rfc, Id),
    CONSTRAINT UX_SiteLocationPriority_Order UNIQUE (Rfc, SiteId, StationCode, Priority)
  );
END;

IF OBJECT_ID('restaurante.DiningTable', 'U') IS NULL
BEGIN
  CREATE TABLE restaurante.DiningTable
  (
    Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_DiningTable PRIMARY KEY,
    Rfc varchar(50) NOT NULL,
    SiteId int NOT NULL,
    TableCode varchar(20) NOT NULL,
    [Name] varchar(80) NOT NULL,
    Capacity int NULL,
    IsActive bit NOT NULL CONSTRAINT DF_DiningTable_Active DEFAULT (1),
    CONSTRAINT FK_DiningTable_Site_Rfc FOREIGN KEY (Rfc, SiteId) REFERENCES restaurante.Site (Rfc, Id),
    CONSTRAINT UX_DiningTable_Code UNIQUE (Rfc, SiteId, TableCode)
  );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('restaurante.DiningTable') AND name = 'UX_DiningTable_RfcId')
  CREATE UNIQUE INDEX UX_DiningTable_RfcId ON restaurante.DiningTable (Rfc, Id);

IF OBJECT_ID('restaurante.KitchenStation', 'U') IS NULL
BEGIN
  CREATE TABLE restaurante.KitchenStation
  (
    Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_KitchenStation PRIMARY KEY,
    Rfc varchar(50) NOT NULL,
    SiteId int NOT NULL,
    StationCode varchar(30) NOT NULL,
    [Name] varchar(100) NOT NULL,
    SortOrder int NOT NULL CONSTRAINT DF_KitchenStation_Sort DEFAULT (0),
    IsActive bit NOT NULL CONSTRAINT DF_KitchenStation_Active DEFAULT (1),
    CONSTRAINT FK_KitchenStation_Site_Rfc FOREIGN KEY (Rfc, SiteId) REFERENCES restaurante.Site (Rfc, Id),
    CONSTRAINT UX_KitchenStation_Code UNIQUE (Rfc, SiteId, StationCode)
  );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('restaurante.KitchenStation') AND name = 'UX_KitchenStation_RfcId')
  CREATE UNIQUE INDEX UX_KitchenStation_RfcId ON restaurante.KitchenStation (Rfc, Id);

IF OBJECT_ID('restaurante.CashRegister', 'U') IS NULL
BEGIN
  CREATE TABLE restaurante.CashRegister
  (
    Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_CashRegister PRIMARY KEY,
    Rfc varchar(50) NOT NULL,
    SiteId int NOT NULL,
    RegisterCode varchar(30) NOT NULL,
    [Name] varchar(100) NOT NULL,
    DeviceKeyHash varbinary(64) NULL,
    IsActive bit NOT NULL CONSTRAINT DF_CashRegister_Active DEFAULT (1),
    CONSTRAINT FK_CashRegister_Site_Rfc FOREIGN KEY (Rfc, SiteId) REFERENCES restaurante.Site (Rfc, Id),
    CONSTRAINT UX_CashRegister_Code UNIQUE (Rfc, SiteId, RegisterCode)
  );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('restaurante.CashRegister') AND name = 'UX_CashRegister_RfcId')
  CREATE UNIQUE INDEX UX_CashRegister_RfcId ON restaurante.CashRegister (Rfc, Id);

IF OBJECT_ID('restaurante.QuickPin', 'U') IS NULL
BEGIN
  CREATE TABLE restaurante.QuickPin
  (
    Rfc varchar(50) NOT NULL,
    CashRegisterId int NOT NULL,
    UserId nvarchar(450) NOT NULL,
    PinHash varbinary(64) NOT NULL,
    PinSalt varbinary(32) NOT NULL,
    FailedAttempts int NOT NULL CONSTRAINT DF_QuickPin_Attempts DEFAULT (0),
    LockedUntil datetime2(0) NULL,
    UpdatedAt datetime2(0) NOT NULL CONSTRAINT DF_QuickPin_Updated DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT PK_QuickPin PRIMARY KEY NONCLUSTERED (Rfc, CashRegisterId, UserId),
    CONSTRAINT FK_QuickPin_Register_Rfc FOREIGN KEY (Rfc, CashRegisterId) REFERENCES restaurante.CashRegister (Rfc, Id)
  );
END;

IF OBJECT_ID('restaurante.QuickPinAttempt', 'U') IS NULL
BEGIN
  CREATE TABLE restaurante.QuickPinAttempt
  (
    Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_QuickPinAttempt PRIMARY KEY,
    Rfc varchar(50) NOT NULL,
    CashRegisterId int NOT NULL,
    UserId nvarchar(450) NULL,
    Succeeded bit NOT NULL,
    FailureReason varchar(100) NULL,
    AttemptedAt datetime2(0) NOT NULL CONSTRAINT DF_QuickPinAttempt_At DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT FK_QuickPinAttempt_Register_Rfc FOREIGN KEY (Rfc, CashRegisterId) REFERENCES restaurante.CashRegister (Rfc, Id)
  );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID('restaurante.QuickPinAttempt') AND name='IX_QuickPinAttempt_Audit')
  CREATE INDEX IX_QuickPinAttempt_Audit ON restaurante.QuickPinAttempt (Rfc, CashRegisterId, AttemptedAt DESC) INCLUDE (UserId, Succeeded, FailureReason);

IF EXISTS
(
  SELECT 1
  FROM sys.key_constraints keyConstraint
  JOIN sys.indexes indexInfo
    ON indexInfo.object_id = keyConstraint.parent_object_id
   AND indexInfo.index_id = keyConstraint.unique_index_id
  WHERE keyConstraint.parent_object_id = OBJECT_ID('restaurante.QuickPin')
    AND keyConstraint.[name] = 'PK_QuickPin'
    AND indexInfo.[type] = 1
)
BEGIN
  ALTER TABLE restaurante.QuickPin DROP CONSTRAINT PK_QuickPin;
  ALTER TABLE restaurante.QuickPin ADD CONSTRAINT PK_QuickPin PRIMARY KEY NONCLUSTERED (Rfc, CashRegisterId, UserId);
END;

IF OBJECT_ID('restaurante.ExternalProvider', 'U') IS NULL
BEGIN
  CREATE TABLE restaurante.ExternalProvider
  (
    Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_ExternalProvider PRIMARY KEY,
    Rfc varchar(50) NOT NULL,
    SiteId int NOT NULL,
    ProviderCode varchar(30) NOT NULL,
    [Name] varchar(120) NOT NULL,
    DefaultCommissionRate decimal(9,6) NOT NULL CONSTRAINT DF_ExternalProvider_Commission DEFAULT (0),
    IsActive bit NOT NULL CONSTRAINT DF_ExternalProvider_Active DEFAULT (1),
    CONSTRAINT CK_ExternalProvider_Commission CHECK (DefaultCommissionRate >= 0 AND DefaultCommissionRate <= 1),
    CONSTRAINT FK_ExternalProvider_Site_Rfc FOREIGN KEY (Rfc, SiteId) REFERENCES restaurante.Site (Rfc, Id),
    CONSTRAINT UX_ExternalProvider_Code UNIQUE (Rfc, SiteId, ProviderCode)
  );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('restaurante.ExternalProvider') AND name = 'UX_ExternalProvider_RfcId')
  CREATE UNIQUE INDEX UX_ExternalProvider_RfcId ON restaurante.ExternalProvider (Rfc, Id);

IF OBJECT_ID('restaurante.AccountingConfiguration', 'U') IS NULL
BEGIN
  CREATE TABLE restaurante.AccountingConfiguration
  (
    Rfc varchar(50) NOT NULL,
    SiteId int NOT NULL,
    CashAccount varchar(50) NULL,
    CardBankAccount varchar(50) NULL,
    TransferBankAccount varchar(50) NULL,
    PlatformReceivableAccount varchar(50) NULL,
    SalesAccount varchar(50) NULL,
    VatAccount varchar(50) NULL,
    DiscountAccount varchar(50) NULL,
    TipsPayableAccount varchar(50) NULL,
    PlatformCommissionAccount varchar(50) NULL,
    InventoryAccount varchar(50) NULL,
    CostOfSalesAccount varchar(50) NULL,
    WasteAccount varchar(50) NULL,
    DailyPolicyEnabled bit NOT NULL CONSTRAINT DF_AccountingConfiguration_Daily DEFAULT (0),
    CONSTRAINT PK_AccountingConfiguration PRIMARY KEY (Rfc, SiteId),
    CONSTRAINT FK_AccountingConfiguration_Site_Rfc FOREIGN KEY (Rfc, SiteId) REFERENCES restaurante.Site (Rfc, Id)
  );
END;

IF OBJECT_ID('restaurante.ProductCard', 'U') IS NULL
BEGIN
  CREATE TABLE restaurante.ProductCard
  (
    Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_ProductCard PRIMARY KEY,
    Rfc varchar(50) NOT NULL,
    CardCode varchar(40) NOT NULL,
    [Name] varchar(160) NOT NULL,
    [Description] varchar(800) NULL,
    FamilyImage varbinary(max) NULL,
    FamilyImageThumbnail varbinary(max) NULL,
    ImageFileName varchar(200) NULL,
    ImageContentType varchar(100) NULL,
    IsActive bit NOT NULL CONSTRAINT DF_ProductCard_Active DEFAULT (1),
    CONSTRAINT UX_ProductCard_Code UNIQUE (Rfc, CardCode)
  );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('restaurante.ProductCard') AND name = 'UX_ProductCard_RfcId')
  CREATE UNIQUE INDEX UX_ProductCard_RfcId ON restaurante.ProductCard (Rfc, Id);

IF OBJECT_ID('restaurante.Product', 'U') IS NULL
BEGIN
  CREATE TABLE restaurante.Product
  (
    Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_RestaurantProduct PRIMARY KEY,
    Rfc varchar(50) NOT NULL,
    ProductCardId bigint NOT NULL,
    MaterialId int NOT NULL,
    Sku varchar(50) NOT NULL,
    VariantName varchar(120) NULL,
    Price decimal(18,2) NOT NULL,
    KitchenStationId int NULL,
    PreparationMinutes int NULL,
    VariantImage varbinary(max) NULL,
    VariantImageThumbnail varbinary(max) NULL,
    VariantImageFileName varchar(200) NULL,
    VariantImageContentType varchar(100) NULL,
    IsActive bit NOT NULL CONSTRAINT DF_RestaurantProduct_Active DEFAULT (1),
    SoldOutOverride bit NOT NULL CONSTRAINT DF_RestaurantProduct_SoldOut DEFAULT (0),
    CONSTRAINT CK_RestaurantProduct_Price CHECK (Price >= 0),
    CONSTRAINT FK_RestaurantProduct_Card_Rfc FOREIGN KEY (Rfc, ProductCardId) REFERENCES restaurante.ProductCard (Rfc, Id),
    CONSTRAINT FK_RestaurantProduct_Material_Rfc FOREIGN KEY (Rfc, MaterialId) REFERENCES logistica.Material (Rfc, Id),
    CONSTRAINT FK_RestaurantProduct_Station_Rfc FOREIGN KEY (Rfc, KitchenStationId) REFERENCES restaurante.KitchenStation (Rfc, Id),
    CONSTRAINT UX_RestaurantProduct_Sku UNIQUE (Rfc, Sku),
    CONSTRAINT UX_RestaurantProduct_Material UNIQUE (Rfc, MaterialId)
  );
END;

IF COL_LENGTH('restaurante.Product','VariantImage') IS NULL
  ALTER TABLE restaurante.Product ADD VariantImage varbinary(max) NULL;
IF COL_LENGTH('restaurante.Product','VariantImageThumbnail') IS NULL
  ALTER TABLE restaurante.Product ADD VariantImageThumbnail varbinary(max) NULL;
IF COL_LENGTH('restaurante.Product','VariantImageFileName') IS NULL
  ALTER TABLE restaurante.Product ADD VariantImageFileName varchar(200) NULL;
IF COL_LENGTH('restaurante.Product','VariantImageContentType') IS NULL
  ALTER TABLE restaurante.Product ADD VariantImageContentType varchar(100) NULL;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('restaurante.Product') AND name = 'UX_RestaurantProduct_RfcId')
  CREATE UNIQUE INDEX UX_RestaurantProduct_RfcId ON restaurante.Product (Rfc, Id);

IF OBJECT_ID('restaurante.ModifierGroup', 'U') IS NULL
BEGIN
  CREATE TABLE restaurante.ModifierGroup
  (
    Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_ModifierGroup PRIMARY KEY,
    Rfc varchar(50) NOT NULL,
    [Name] varchar(120) NOT NULL,
    MinSelections int NOT NULL CONSTRAINT DF_ModifierGroup_Min DEFAULT (0),
    MaxSelections int NOT NULL CONSTRAINT DF_ModifierGroup_Max DEFAULT (1),
    SortOrder int NOT NULL CONSTRAINT DF_ModifierGroup_Sort DEFAULT (0),
    IsActive bit NOT NULL CONSTRAINT DF_ModifierGroup_Active DEFAULT (1),
    CONSTRAINT CK_ModifierGroup_Selection CHECK (MinSelections >= 0 AND MaxSelections >= MinSelections),
    CONSTRAINT UX_ModifierGroup_Name UNIQUE (Rfc, [Name])
  );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('restaurante.ModifierGroup') AND name = 'UX_ModifierGroup_RfcId')
  CREATE UNIQUE INDEX UX_ModifierGroup_RfcId ON restaurante.ModifierGroup (Rfc, Id);

IF OBJECT_ID('restaurante.ModifierOption', 'U') IS NULL
BEGIN
  CREATE TABLE restaurante.ModifierOption
  (
    Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_ModifierOption PRIMARY KEY,
    Rfc varchar(50) NOT NULL,
    ModifierGroupId bigint NOT NULL,
    [Name] varchar(120) NOT NULL,
    PriceDelta decimal(18,2) NOT NULL CONSTRAINT DF_ModifierOption_Price DEFAULT (0),
    SortOrder int NOT NULL CONSTRAINT DF_ModifierOption_Sort DEFAULT (0),
    IsActive bit NOT NULL CONSTRAINT DF_ModifierOption_Active DEFAULT (1),
    CONSTRAINT FK_ModifierOption_Group_Rfc FOREIGN KEY (Rfc, ModifierGroupId) REFERENCES restaurante.ModifierGroup (Rfc, Id),
    CONSTRAINT UX_ModifierOption_Name UNIQUE (Rfc, ModifierGroupId, [Name])
  );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('restaurante.ModifierOption') AND name = 'UX_ModifierOption_RfcId')
  CREATE UNIQUE INDEX UX_ModifierOption_RfcId ON restaurante.ModifierOption (Rfc, Id);

IF OBJECT_ID('restaurante.ProductModifierGroup', 'U') IS NULL
BEGIN
  CREATE TABLE restaurante.ProductModifierGroup
  (
    Rfc varchar(50) NOT NULL,
    ProductId bigint NOT NULL,
    ModifierGroupId bigint NOT NULL,
    SortOrder int NOT NULL CONSTRAINT DF_ProductModifierGroup_Sort DEFAULT (0),
    CONSTRAINT PK_ProductModifierGroup PRIMARY KEY (Rfc, ProductId, ModifierGroupId),
    CONSTRAINT FK_ProductModifierGroup_Product_Rfc FOREIGN KEY (Rfc, ProductId) REFERENCES restaurante.Product (Rfc, Id),
    CONSTRAINT FK_ProductModifierGroup_Group_Rfc FOREIGN KEY (Rfc, ModifierGroupId) REFERENCES restaurante.ModifierGroup (Rfc, Id)
  );
END;

IF OBJECT_ID('restaurante.ModifierIngredientDelta', 'U') IS NULL
BEGIN
  CREATE TABLE restaurante.ModifierIngredientDelta
  (
    Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_ModifierIngredientDelta PRIMARY KEY,
    Rfc varchar(50) NOT NULL,
    ModifierOptionId bigint NOT NULL,
    MaterialId int NOT NULL,
    QuantityDelta decimal(18,6) NOT NULL,
    UnitId int NOT NULL,
    CONSTRAINT CK_ModifierIngredientDelta_Quantity CHECK (QuantityDelta <> 0),
    CONSTRAINT FK_ModifierIngredientDelta_Option_Rfc FOREIGN KEY (Rfc, ModifierOptionId) REFERENCES restaurante.ModifierOption (Rfc, Id),
    CONSTRAINT FK_ModifierIngredientDelta_Material_Rfc FOREIGN KEY (Rfc, MaterialId) REFERENCES logistica.Material (Rfc, Id),
    CONSTRAINT FK_ModifierIngredientDelta_Unit FOREIGN KEY (UnitId) REFERENCES logistica.UnitOfMeasure (Id),
    CONSTRAINT UX_ModifierIngredientDelta UNIQUE (Rfc, ModifierOptionId, MaterialId)
  );
END;

IF OBJECT_ID('restaurante.Menu', 'U') IS NULL
BEGIN
  CREATE TABLE restaurante.Menu
  (
    Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_RestaurantMenu PRIMARY KEY,
    Rfc varchar(50) NOT NULL,
    MenuCode varchar(40) NOT NULL,
    [Name] varchar(120) NOT NULL,
    IsPublished bit NOT NULL CONSTRAINT DF_RestaurantMenu_Published DEFAULT (0),
    IsActive bit NOT NULL CONSTRAINT DF_RestaurantMenu_Active DEFAULT (1),
    CONSTRAINT UX_RestaurantMenu_Code UNIQUE (Rfc, MenuCode)
  );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('restaurante.Menu') AND name = 'UX_RestaurantMenu_RfcId')
  CREATE UNIQUE INDEX UX_RestaurantMenu_RfcId ON restaurante.Menu (Rfc, Id);

IF OBJECT_ID('restaurante.MenuSchedule', 'U') IS NULL
BEGIN
  CREATE TABLE restaurante.MenuSchedule
  (
    Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_MenuSchedule PRIMARY KEY,
    Rfc varchar(50) NOT NULL,
    MenuId bigint NOT NULL,
    SiteId int NOT NULL,
    DayOfWeek tinyint NOT NULL,
    StartsAt time(0) NOT NULL,
    EndsAt time(0) NOT NULL,
    CONSTRAINT CK_MenuSchedule_Day CHECK (DayOfWeek BETWEEN 0 AND 6),
    CONSTRAINT CK_MenuSchedule_Time CHECK (StartsAt <> EndsAt),
    CONSTRAINT FK_MenuSchedule_Menu_Rfc FOREIGN KEY (Rfc, MenuId) REFERENCES restaurante.Menu (Rfc, Id),
    CONSTRAINT FK_MenuSchedule_Site_Rfc FOREIGN KEY (Rfc, SiteId) REFERENCES restaurante.Site (Rfc, Id),
    CONSTRAINT UX_MenuSchedule UNIQUE (Rfc, MenuId, SiteId, DayOfWeek, StartsAt)
  );
END;

IF OBJECT_ID('restaurante.MenuSection', 'U') IS NULL
BEGIN
  CREATE TABLE restaurante.MenuSection
  (
    Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_MenuSection PRIMARY KEY,
    Rfc varchar(50) NOT NULL,
    MenuId bigint NOT NULL,
    [Name] varchar(100) NOT NULL,
    SortOrder int NOT NULL CONSTRAINT DF_MenuSection_Sort DEFAULT (0),
    CONSTRAINT FK_MenuSection_Menu_Rfc FOREIGN KEY (Rfc, MenuId) REFERENCES restaurante.Menu (Rfc, Id),
    CONSTRAINT UX_MenuSection_Name UNIQUE (Rfc, MenuId, [Name])
  );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('restaurante.MenuSection') AND name = 'UX_MenuSection_RfcId')
  CREATE UNIQUE INDEX UX_MenuSection_RfcId ON restaurante.MenuSection (Rfc, Id);

IF OBJECT_ID('restaurante.MenuItem', 'U') IS NULL
BEGIN
  CREATE TABLE restaurante.MenuItem
  (
    Rfc varchar(50) NOT NULL,
    MenuSectionId bigint NOT NULL,
    ProductId bigint NOT NULL,
    SortOrder int NOT NULL CONSTRAINT DF_MenuItem_Sort DEFAULT (0),
    CONSTRAINT PK_MenuItem PRIMARY KEY (Rfc, MenuSectionId, ProductId),
    CONSTRAINT FK_MenuItem_Section_Rfc FOREIGN KEY (Rfc, MenuSectionId) REFERENCES restaurante.MenuSection (Rfc, Id),
    CONSTRAINT FK_MenuItem_Product_Rfc FOREIGN KEY (Rfc, ProductId) REFERENCES restaurante.Product (Rfc, Id)
  );
END;

IF OBJECT_ID('restaurante.DailySequence', 'U') IS NULL
BEGIN
  CREATE TABLE restaurante.DailySequence
  (
    Rfc varchar(50) NOT NULL,
    SiteId int NOT NULL,
    OperationalDate date NOT NULL,
    LastNumber int NOT NULL,
    CONSTRAINT PK_DailySequence PRIMARY KEY (Rfc, SiteId, OperationalDate),
    CONSTRAINT FK_DailySequence_Site_Rfc FOREIGN KEY (Rfc, SiteId) REFERENCES restaurante.Site (Rfc, Id)
  );
END;

IF OBJECT_ID('restaurante.[Order]', 'U') IS NULL
BEGIN
  CREATE TABLE restaurante.[Order]
  (
    Id uniqueidentifier NOT NULL CONSTRAINT PK_RestaurantOrder PRIMARY KEY,
    Rfc varchar(50) NOT NULL,
    SiteId int NOT NULL,
    Folio int NOT NULL,
    OperationalDate date NOT NULL,
    OrderType varchar(20) NOT NULL,
    [Status] varchar(30) NOT NULL,
    PaymentStatus varchar(30) NOT NULL,
    Priority tinyint NOT NULL CONSTRAINT DF_RestaurantOrder_Priority DEFAULT (0),
    PriorityReason varchar(300) NULL,
    PrioritizedBy varchar(256) NULL,
    PrioritizedAt datetime2(0) NULL,
    CustomerName varchar(150) NULL,
    CustomerPhone varchar(30) NULL,
    DiningTableId int NULL,
    CashRegisterId int NULL,
    CashShiftId uniqueidentifier NULL,
    Subtotal decimal(18,2) NOT NULL,
    DiscountTotal decimal(18,2) NOT NULL,
    TaxTotal decimal(18,2) NOT NULL,
    TipTotal decimal(18,2) NOT NULL,
    Total decimal(18,2) NOT NULL,
    BalanceDue decimal(18,2) NOT NULL,
    TaxRateSnapshot decimal(9,6) NOT NULL,
    PricesIncludeTaxSnapshot bit NOT NULL,
    InventoryReservationId bigint NULL,
    TheoreticalCost decimal(18,6) NOT NULL CONSTRAINT DF_RestaurantOrder_Cost DEFAULT (0),
    IdempotencyKey varchar(100) NOT NULL,
    Notes varchar(1000) NULL,
    CreatedAt datetime2(0) NOT NULL CONSTRAINT DF_RestaurantOrder_Created DEFAULT (SYSUTCDATETIME()),
    CreatedBy varchar(256) NULL,
    PaidAt datetime2(0) NULL,
    SentToKitchenAt datetime2(0) NULL,
    ReadyAt datetime2(0) NULL,
    CompletedAt datetime2(0) NULL,
    CancelledAt datetime2(0) NULL,
    CancelledBy varchar(256) NULL,
    CancellationReason varchar(500) NULL,
    RowVersion rowversion NOT NULL,
    CONSTRAINT CK_RestaurantOrder_Amounts CHECK (Subtotal >= 0 AND DiscountTotal >= 0 AND TaxTotal >= 0 AND TipTotal >= 0 AND Total >= 0),
    CONSTRAINT FK_RestaurantOrder_Site_Rfc FOREIGN KEY (Rfc, SiteId) REFERENCES restaurante.Site (Rfc, Id),
    CONSTRAINT FK_RestaurantOrder_Table_Rfc FOREIGN KEY (Rfc, DiningTableId) REFERENCES restaurante.DiningTable (Rfc, Id),
    CONSTRAINT FK_RestaurantOrder_Register_Rfc FOREIGN KEY (Rfc, CashRegisterId) REFERENCES restaurante.CashRegister (Rfc, Id),
    CONSTRAINT FK_RestaurantOrder_Reservation_Rfc FOREIGN KEY (Rfc, InventoryReservationId) REFERENCES logistica.InventoryReservation (Rfc, Id),
    CONSTRAINT UX_RestaurantOrder_Folio UNIQUE (Rfc, SiteId, OperationalDate, Folio),
    CONSTRAINT UX_RestaurantOrder_Idempotency UNIQUE (Rfc, SiteId, IdempotencyKey)
  );
END;

IF COL_LENGTH('restaurante.Order','Priority') IS NULL
  ALTER TABLE restaurante.[Order] ADD Priority tinyint NOT NULL CONSTRAINT DF_RestaurantOrder_Priority DEFAULT (0) WITH VALUES;
IF COL_LENGTH('restaurante.Order','PriorityReason') IS NULL
  ALTER TABLE restaurante.[Order] ADD PriorityReason varchar(300) NULL;
IF COL_LENGTH('restaurante.Order','PrioritizedBy') IS NULL
  ALTER TABLE restaurante.[Order] ADD PrioritizedBy varchar(256) NULL;
IF COL_LENGTH('restaurante.Order','PrioritizedAt') IS NULL
  ALTER TABLE restaurante.[Order] ADD PrioritizedAt datetime2(0) NULL;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('restaurante.[Order]') AND name = 'UX_RestaurantOrder_RfcId')
  CREATE UNIQUE INDEX UX_RestaurantOrder_RfcId ON restaurante.[Order] (Rfc, Id);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('restaurante.[Order]') AND name = 'IX_RestaurantOrder_Kitchen')
  CREATE INDEX IX_RestaurantOrder_Kitchen ON restaurante.[Order] (Rfc, SiteId, OperationalDate, [Status], CreatedAt);

IF OBJECT_ID('restaurante.OrderLine', 'U') IS NULL
BEGIN
  CREATE TABLE restaurante.OrderLine
  (
    Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_OrderLine PRIMARY KEY,
    Rfc varchar(50) NOT NULL,
    OrderId uniqueidentifier NOT NULL,
    ProductId bigint NOT NULL,
    ProductNameSnapshot varchar(180) NOT NULL,
    SkuSnapshot varchar(50) NOT NULL,
    Quantity decimal(18,4) NOT NULL,
    UnitPrice decimal(18,2) NOT NULL,
    DiscountAmount decimal(18,2) NOT NULL CONSTRAINT DF_OrderLine_Discount DEFAULT (0),
    TaxAmount decimal(18,2) NOT NULL,
    LineTotal decimal(18,2) NOT NULL,
    [Status] varchar(20) NOT NULL,
    KitchenStationId int NULL,
    Notes varchar(500) NULL,
    StartedAt datetime2(0) NULL,
    ReadyAt datetime2(0) NULL,
    DeliveredAt datetime2(0) NULL,
    CancelledAt datetime2(0) NULL,
    CONSTRAINT CK_OrderLine_Quantity CHECK (Quantity > 0),
    CONSTRAINT FK_OrderLine_Order_Rfc FOREIGN KEY (Rfc, OrderId) REFERENCES restaurante.[Order] (Rfc, Id),
    CONSTRAINT FK_OrderLine_Product_Rfc FOREIGN KEY (Rfc, ProductId) REFERENCES restaurante.Product (Rfc, Id),
    CONSTRAINT FK_OrderLine_Station_Rfc FOREIGN KEY (Rfc, KitchenStationId) REFERENCES restaurante.KitchenStation (Rfc, Id)
  );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('restaurante.OrderLine') AND name = 'UX_OrderLine_RfcId')
  CREATE UNIQUE INDEX UX_OrderLine_RfcId ON restaurante.OrderLine (Rfc, Id);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('restaurante.OrderLine') AND name = 'IX_OrderLine_Kitchen')
  CREATE INDEX IX_OrderLine_Kitchen ON restaurante.OrderLine (Rfc, KitchenStationId, [Status], OrderId, Id);

IF OBJECT_ID('restaurante.OrderLineModifier', 'U') IS NULL
BEGIN
  CREATE TABLE restaurante.OrderLineModifier
  (
    Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_OrderLineModifier PRIMARY KEY,
    Rfc varchar(50) NOT NULL,
    OrderLineId bigint NOT NULL,
    ModifierOptionId bigint NOT NULL,
    [Name] varchar(120) NOT NULL,
    PriceDelta decimal(18,2) NOT NULL,
    Quantity int NOT NULL CONSTRAINT DF_OrderLineModifier_Quantity DEFAULT (1),
    CONSTRAINT FK_OrderLineModifier_Line_Rfc FOREIGN KEY (Rfc, OrderLineId) REFERENCES restaurante.OrderLine (Rfc, Id),
    CONSTRAINT FK_OrderLineModifier_Option_Rfc FOREIGN KEY (Rfc, ModifierOptionId) REFERENCES restaurante.ModifierOption (Rfc, Id)
  );
END;

IF OBJECT_ID('restaurante.Payment', 'U') IS NULL
BEGIN
  CREATE TABLE restaurante.Payment
  (
    Id uniqueidentifier NOT NULL CONSTRAINT PK_RestaurantPayment PRIMARY KEY,
    Rfc varchar(50) NOT NULL,
    OrderId uniqueidentifier NOT NULL,
    PaymentMethod varchar(30) NOT NULL,
    Amount decimal(18,2) NOT NULL,
    TipAmount decimal(18,2) NOT NULL CONSTRAINT DF_RestaurantPayment_Tip DEFAULT (0),
    [Status] varchar(20) NOT NULL,
    ExternalReference varchar(100) NULL,
    IdempotencyKey varchar(100) NOT NULL,
    PaidAt datetime2(0) NOT NULL CONSTRAINT DF_RestaurantPayment_Paid DEFAULT (SYSUTCDATETIME()),
    ReceivedBy varchar(256) NULL,
    RefundedAmount decimal(18,2) NOT NULL CONSTRAINT DF_RestaurantPayment_Refunded DEFAULT (0),
    CONSTRAINT CK_RestaurantPayment_Amount CHECK (Amount > 0 AND TipAmount >= 0),
    CONSTRAINT FK_RestaurantPayment_Order_Rfc FOREIGN KEY (Rfc, OrderId) REFERENCES restaurante.[Order] (Rfc, Id),
    CONSTRAINT UX_RestaurantPayment_Idempotency UNIQUE (Rfc, IdempotencyKey)
  );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID('restaurante.Payment') AND name='UX_RestaurantPayment_RfcId')
  CREATE UNIQUE INDEX UX_RestaurantPayment_RfcId ON restaurante.Payment (Rfc, Id);

IF OBJECT_ID('restaurante.PaymentRefund', 'U') IS NULL
BEGIN
  CREATE TABLE restaurante.PaymentRefund
  (
    Id uniqueidentifier NOT NULL CONSTRAINT PK_RestaurantPaymentRefund PRIMARY KEY,
    Rfc varchar(50) NOT NULL,
    PaymentId uniqueidentifier NOT NULL,
    Amount decimal(18,2) NOT NULL,
    Reason varchar(500) NOT NULL,
    IdempotencyKey varchar(100) NOT NULL,
    RequestedBy varchar(256) NOT NULL,
    AuthorizedBy varchar(256) NOT NULL,
    RefundedAt datetime2(0) NOT NULL CONSTRAINT DF_RestaurantPaymentRefund_At DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT CK_RestaurantPaymentRefund_Amount CHECK (Amount > 0),
    CONSTRAINT FK_RestaurantPaymentRefund_Payment_Rfc FOREIGN KEY (Rfc, PaymentId) REFERENCES restaurante.Payment (Rfc, Id),
    CONSTRAINT UX_RestaurantPaymentRefund_Idempotency UNIQUE (Rfc, IdempotencyKey)
  );
END;

IF OBJECT_ID('restaurante.Delivery', 'U') IS NULL
BEGIN
  CREATE TABLE restaurante.Delivery
  (
    Rfc varchar(50) NOT NULL,
    OrderId uniqueidentifier NOT NULL,
    ExternalProviderId int NULL,
    ExternalReference varchar(100) NULL,
    AddressLine varchar(300) NOT NULL,
    AddressReferences varchar(500) NULL,
    DeliveryCost decimal(18,2) NOT NULL CONSTRAINT DF_Delivery_Cost DEFAULT (0),
    CommissionAmount decimal(18,2) NOT NULL CONSTRAINT DF_Delivery_Commission DEFAULT (0),
    [Status] varchar(30) NOT NULL,
    DispatchedAt datetime2(0) NULL,
    DeliveredAt datetime2(0) NULL,
    SettledAt datetime2(0) NULL,
    CONSTRAINT PK_Delivery PRIMARY KEY (Rfc, OrderId),
    CONSTRAINT FK_Delivery_Order_Rfc FOREIGN KEY (Rfc, OrderId) REFERENCES restaurante.[Order] (Rfc, Id),
    CONSTRAINT FK_Delivery_Provider_Rfc FOREIGN KEY (Rfc, ExternalProviderId) REFERENCES restaurante.ExternalProvider (Rfc, Id)
  );
END;

IF OBJECT_ID('restaurante.CashShift', 'U') IS NULL
BEGIN
  CREATE TABLE restaurante.CashShift
  (
    Id uniqueidentifier NOT NULL CONSTRAINT PK_CashShift PRIMARY KEY,
    Rfc varchar(50) NOT NULL,
    SiteId int NOT NULL,
    CashRegisterId int NOT NULL,
    [Status] varchar(20) NOT NULL,
    OpeningFloat decimal(18,2) NOT NULL,
    OpenedAt datetime2(0) NOT NULL CONSTRAINT DF_CashShift_Opened DEFAULT (SYSUTCDATETIME()),
    OpenedBy varchar(256) NOT NULL,
    ClosedAt datetime2(0) NULL,
    ClosedBy varchar(256) NULL,
    ExpectedCash decimal(18,2) NULL,
    CountedCash decimal(18,2) NULL,
    Difference decimal(18,2) NULL,
    ApprovedAt datetime2(0) NULL,
    ApprovedBy varchar(256) NULL,
    ReopenedAt datetime2(0) NULL,
    ReopenedBy varchar(256) NULL,
    CONSTRAINT FK_CashShift_Site_Rfc FOREIGN KEY (Rfc, SiteId) REFERENCES restaurante.Site (Rfc, Id),
    CONSTRAINT FK_CashShift_Register_Rfc FOREIGN KEY (Rfc, CashRegisterId) REFERENCES restaurante.CashRegister (Rfc, Id)
  );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('restaurante.CashShift') AND name = 'UX_CashShift_RfcId')
  CREATE UNIQUE INDEX UX_CashShift_RfcId ON restaurante.CashShift (Rfc, Id);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('restaurante.CashShift') AND name = 'UX_CashShift_OpenRegister')
  CREATE UNIQUE INDEX UX_CashShift_OpenRegister ON restaurante.CashShift (Rfc, CashRegisterId) WHERE [Status] = 'Open';

IF OBJECT_ID('restaurante.FK_RestaurantOrder_Shift_Rfc', 'F') IS NULL
  ALTER TABLE restaurante.[Order] ADD CONSTRAINT FK_RestaurantOrder_Shift_Rfc FOREIGN KEY (Rfc, CashShiftId) REFERENCES restaurante.CashShift (Rfc, Id);

IF OBJECT_ID('restaurante.CashMovement', 'U') IS NULL
BEGIN
  CREATE TABLE restaurante.CashMovement
  (
    Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_CashMovement PRIMARY KEY,
    Rfc varchar(50) NOT NULL,
    CashShiftId uniqueidentifier NOT NULL,
    MovementType varchar(30) NOT NULL,
    PaymentMethod varchar(30) NULL,
    Amount decimal(18,2) NOT NULL,
    OrderId uniqueidentifier NULL,
    Reason varchar(500) NULL,
    CreatedAt datetime2(0) NOT NULL CONSTRAINT DF_CashMovement_Created DEFAULT (SYSUTCDATETIME()),
    CreatedBy varchar(256) NULL,
    CONSTRAINT FK_CashMovement_Shift_Rfc FOREIGN KEY (Rfc, CashShiftId) REFERENCES restaurante.CashShift (Rfc, Id),
    CONSTRAINT FK_CashMovement_Order_Rfc FOREIGN KEY (Rfc, OrderId) REFERENCES restaurante.[Order] (Rfc, Id)
  );
END;

IF OBJECT_ID('restaurante.ProviderSettlement', 'U') IS NULL
BEGIN
  CREATE TABLE restaurante.ProviderSettlement
  (
    Id uniqueidentifier NOT NULL CONSTRAINT PK_ProviderSettlement PRIMARY KEY,
    Rfc varchar(50) NOT NULL,
    SiteId int NOT NULL,
    ExternalProviderId int NOT NULL,
    SettlementCode varchar(40) NOT NULL,
    [Status] varchar(20) NOT NULL,
    GrossAmount decimal(18,2) NOT NULL,
    CommissionAmount decimal(18,2) NOT NULL,
    NetAmount decimal(18,2) NOT NULL,
    SettledAt datetime2(0) NULL,
    CreatedAt datetime2(0) NOT NULL CONSTRAINT DF_ProviderSettlement_Created DEFAULT (SYSUTCDATETIME()),
    CreatedBy varchar(256) NULL,
    CONSTRAINT FK_ProviderSettlement_Site_Rfc FOREIGN KEY (Rfc, SiteId) REFERENCES restaurante.Site (Rfc, Id),
    CONSTRAINT FK_ProviderSettlement_Provider_Rfc FOREIGN KEY (Rfc, ExternalProviderId) REFERENCES restaurante.ExternalProvider (Rfc, Id),
    CONSTRAINT UX_ProviderSettlement_Code UNIQUE (Rfc, SiteId, SettlementCode)
  );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('restaurante.ProviderSettlement') AND name = 'UX_ProviderSettlement_RfcId')
  CREATE UNIQUE INDEX UX_ProviderSettlement_RfcId ON restaurante.ProviderSettlement (Rfc, Id);

IF OBJECT_ID('restaurante.ProviderSettlementOrder', 'U') IS NULL
BEGIN
  CREATE TABLE restaurante.ProviderSettlementOrder
  (
    Rfc varchar(50) NOT NULL,
    SettlementId uniqueidentifier NOT NULL,
    OrderId uniqueidentifier NOT NULL,
    GrossAmount decimal(18,2) NOT NULL,
    CommissionAmount decimal(18,2) NOT NULL,
    NetAmount decimal(18,2) NOT NULL,
    CONSTRAINT PK_ProviderSettlementOrder PRIMARY KEY (Rfc, SettlementId, OrderId),
    CONSTRAINT FK_ProviderSettlementOrder_Header_Rfc FOREIGN KEY (Rfc, SettlementId) REFERENCES restaurante.ProviderSettlement (Rfc, Id),
    CONSTRAINT FK_ProviderSettlementOrder_Order_Rfc FOREIGN KEY (Rfc, OrderId) REFERENCES restaurante.[Order] (Rfc, Id)
  );
END;

IF OBJECT_ID('restaurante.AccountingLink', 'U') IS NULL
BEGIN
  CREATE TABLE restaurante.AccountingLink
  (
    Rfc varchar(50) NOT NULL,
    SiteId int NULL,
    OrderId uniqueidentifier NULL,
    OperationalDate date NULL,
    LinkType varchar(30) NOT NULL,
    TransactionId int NOT NULL,
    CfdiId int NULL,
    CreatedAt datetime2(0) NOT NULL CONSTRAINT DF_AccountingLink_Created DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT PK_AccountingLink PRIMARY KEY (Rfc, LinkType, TransactionId)
  );
END;

IF COL_LENGTH('restaurante.AccountingLink','SiteId') IS NULL
  ALTER TABLE restaurante.AccountingLink ADD SiteId int NULL;
GO

UPDATE linkInfo
SET SiteId=orderInfo.SiteId
FROM restaurante.AccountingLink linkInfo
JOIN restaurante.[Order] orderInfo ON orderInfo.Rfc=linkInfo.Rfc AND orderInfo.Id=linkInfo.OrderId
WHERE linkInfo.SiteId IS NULL;

IF OBJECT_ID('restaurante.FK_AccountingLink_Site_Rfc','F') IS NULL
  ALTER TABLE restaurante.AccountingLink ADD CONSTRAINT FK_AccountingLink_Site_Rfc FOREIGN KEY (Rfc,SiteId) REFERENCES restaurante.Site (Rfc,Id);

IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID('restaurante.AccountingLink') AND name='UX_AccountingLink_Daily')
  DROP INDEX UX_AccountingLink_Daily ON restaurante.AccountingLink;
CREATE UNIQUE INDEX UX_AccountingLink_Daily ON restaurante.AccountingLink (Rfc,SiteId,LinkType,OperationalDate) WHERE OperationalDate IS NOT NULL AND LinkType='DailyConsolidated';
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID('restaurante.AccountingLink') AND name='UX_AccountingLink_Order')
  CREATE UNIQUE INDEX UX_AccountingLink_Order ON restaurante.AccountingLink (Rfc,OrderId,LinkType) WHERE OrderId IS NOT NULL;

IF OBJECT_ID('restaurante.AccountingOrderLink','U') IS NULL
BEGIN
  CREATE TABLE restaurante.AccountingOrderLink
  (
    Rfc varchar(50) NOT NULL,
    OrderId uniqueidentifier NOT NULL,
    SiteId int NOT NULL,
    OperationalDate date NOT NULL,
    LinkType varchar(30) NOT NULL,
    TransactionId int NOT NULL,
    CfdiId int NULL,
    CreatedAt datetime2(0) NOT NULL CONSTRAINT DF_AccountingOrderLink_Created DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT PK_AccountingOrderLink PRIMARY KEY (Rfc,OrderId),
    CONSTRAINT FK_AccountingOrderLink_Order_Rfc FOREIGN KEY (Rfc,OrderId) REFERENCES restaurante.[Order] (Rfc,Id),
    CONSTRAINT FK_AccountingOrderLink_Site_Rfc FOREIGN KEY (Rfc,SiteId) REFERENCES restaurante.Site (Rfc,Id)
  );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID('restaurante.AccountingOrderLink') AND name='IX_AccountingOrderLink_Daily')
  CREATE INDEX IX_AccountingOrderLink_Daily ON restaurante.AccountingOrderLink (Rfc,SiteId,OperationalDate,LinkType) INCLUDE (TransactionId,CfdiId);

IF OBJECT_ID('restaurante.EventOutbox', 'U') IS NULL
BEGIN
  CREATE TABLE restaurante.EventOutbox
  (
    Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_RestaurantEventOutbox PRIMARY KEY,
    Rfc varchar(50) NOT NULL,
    SiteId int NOT NULL,
    EventType varchar(80) NOT NULL,
    AggregateId varchar(80) NOT NULL,
    Payload nvarchar(max) NOT NULL,
    OccurredAt datetime2(3) NOT NULL CONSTRAINT DF_RestaurantEventOutbox_Occurred DEFAULT (SYSUTCDATETIME()),
    PublishedAt datetime2(3) NULL,
    Attempts int NOT NULL CONSTRAINT DF_RestaurantEventOutbox_Attempts DEFAULT (0),
    CONSTRAINT CK_RestaurantEventOutbox_Json CHECK (ISJSON(Payload) = 1),
    CONSTRAINT FK_RestaurantEventOutbox_Site_Rfc FOREIGN KEY (Rfc, SiteId) REFERENCES restaurante.Site (Rfc, Id)
  );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('restaurante.EventOutbox') AND name = 'IX_RestaurantEventOutbox_Pending')
  CREATE INDEX IX_RestaurantEventOutbox_Pending ON restaurante.EventOutbox (PublishedAt, Id) INCLUDE (Rfc, SiteId, EventType, AggregateId);

IF OBJECT_ID('restaurante.SupervisorAuthorization', 'U') IS NULL
BEGIN
  CREATE TABLE restaurante.SupervisorAuthorization
  (
    Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_SupervisorAuthorization PRIMARY KEY,
    Rfc varchar(50) NOT NULL,
    SiteId int NOT NULL,
    ActionType varchar(40) NOT NULL,
    AggregateId varchar(80) NOT NULL,
    Reason varchar(500) NOT NULL,
    RequestedBy varchar(256) NOT NULL,
    AuthorizedBy varchar(256) NOT NULL,
    AuthorizedAt datetime2(0) NOT NULL CONSTRAINT DF_SupervisorAuthorization_At DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT FK_SupervisorAuthorization_Site_Rfc FOREIGN KEY (Rfc, SiteId) REFERENCES restaurante.Site (Rfc, Id)
  );
END;

COMMIT TRANSACTION;
GO

CREATE OR ALTER PROCEDURE restaurante.NextDailyFolio
  @Rfc varchar(50),
  @SiteId int,
  @OperationalDate date,
  @Folio int OUTPUT
AS
BEGIN
  SET NOCOUNT ON;
  SET XACT_ABORT ON;

  DECLARE @result TABLE (LastNumber int);

  MERGE restaurante.DailySequence WITH (HOLDLOCK) AS target
  USING (SELECT UPPER(LTRIM(RTRIM(@Rfc))) AS Rfc, @SiteId AS SiteId, @OperationalDate AS OperationalDate) AS source
    ON target.Rfc = source.Rfc
   AND target.SiteId = source.SiteId
   AND target.OperationalDate = source.OperationalDate
  WHEN MATCHED THEN UPDATE SET LastNumber = target.LastNumber + 1
  WHEN NOT MATCHED THEN INSERT (Rfc, SiteId, OperationalDate, LastNumber) VALUES (source.Rfc, source.SiteId, source.OperationalDate, 1)
  OUTPUT inserted.LastNumber INTO @result;

  SELECT @Folio = LastNumber FROM @result;
END;
GO
