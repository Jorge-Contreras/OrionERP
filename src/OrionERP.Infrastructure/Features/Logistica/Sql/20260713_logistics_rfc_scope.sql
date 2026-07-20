SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET XACT_ABORT ON;
SET NOCOUNT ON;

DECLARE @InitialRfc varchar(50) = 'OHM191112Q26';

BEGIN TRANSACTION;

IF OBJECT_ID('dbo.BusinessPartnerRfcScope', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.BusinessPartnerRfcScope
    (
        Rfc varchar(50) NOT NULL,
        BusinessPartnerId int NOT NULL,
        IsActive bit NOT NULL CONSTRAINT DF_BusinessPartnerRfcScope_IsActive DEFAULT (1),
        CreatedAt datetime2(0) NOT NULL CONSTRAINT DF_BusinessPartnerRfcScope_CreatedAt DEFAULT (SYSUTCDATETIME()),
        CreatedBy varchar(256) NULL,
        CONSTRAINT PK_BusinessPartnerRfcScope PRIMARY KEY (Rfc, BusinessPartnerId),
        CONSTRAINT FK_BusinessPartnerRfcScope_Partner
            FOREIGN KEY (BusinessPartnerId) REFERENCES dbo.BusinessPartner (Id)
    );
END;

IF COL_LENGTH('logistica.MaterialCategory', 'Rfc') IS NULL ALTER TABLE logistica.MaterialCategory ADD Rfc varchar(50) NULL;
IF COL_LENGTH('logistica.Material', 'Rfc') IS NULL ALTER TABLE logistica.Material ADD Rfc varchar(50) NULL;
IF COL_LENGTH('logistica.Location', 'Rfc') IS NULL ALTER TABLE logistica.Location ADD Rfc varchar(50) NULL;
IF COL_LENGTH('logistica.StockBalance', 'Rfc') IS NULL ALTER TABLE logistica.StockBalance ADD Rfc varchar(50) NULL;
IF COL_LENGTH('logistica.StockTransaction', 'Rfc') IS NULL ALTER TABLE logistica.StockTransaction ADD Rfc varchar(50) NULL;
IF COL_LENGTH('logistica.LocationMaterialAttachment', 'Rfc') IS NULL ALTER TABLE logistica.LocationMaterialAttachment ADD Rfc varchar(50) NULL;
IF COL_LENGTH('logistica.VendorProfile', 'Rfc') IS NULL ALTER TABLE logistica.VendorProfile ADD Rfc varchar(50) NULL;
IF COL_LENGTH('logistica.PhysicalCountSession', 'Rfc') IS NULL ALTER TABLE logistica.PhysicalCountSession ADD Rfc varchar(50) NULL;
IF COL_LENGTH('logistica.PhysicalCountLine', 'Rfc') IS NULL ALTER TABLE logistica.PhysicalCountLine ADD Rfc varchar(50) NULL;
IF COL_LENGTH('logistica.PhysicalCountAttachment', 'Rfc') IS NULL ALTER TABLE logistica.PhysicalCountAttachment ADD Rfc varchar(50) NULL;
IF COL_LENGTH('logistica.PhysicalCountRecountPlan', 'Rfc') IS NULL ALTER TABLE logistica.PhysicalCountRecountPlan ADD Rfc varchar(50) NULL;
IF COL_LENGTH('logistica.PhysicalCountRecountPlanLine', 'Rfc') IS NULL ALTER TABLE logistica.PhysicalCountRecountPlanLine ADD Rfc varchar(50) NULL;
IF COL_LENGTH('logistica.PurchaseOrder', 'Rfc') IS NULL ALTER TABLE logistica.PurchaseOrder ADD Rfc varchar(50) NULL;
IF COL_LENGTH('logistica.PurchaseOrderLine', 'Rfc') IS NULL ALTER TABLE logistica.PurchaseOrderLine ADD Rfc varchar(50) NULL;
IF COL_LENGTH('logistica.PurchaseOrderLineAllocation', 'Rfc') IS NULL ALTER TABLE logistica.PurchaseOrderLineAllocation ADD Rfc varchar(50) NULL;
IF COL_LENGTH('logistica.PurchaseOrderRoomScope', 'Rfc') IS NULL ALTER TABLE logistica.PurchaseOrderRoomScope ADD Rfc varchar(50) NULL;
IF COL_LENGTH('logistica.PurchaseReceipt', 'Rfc') IS NULL ALTER TABLE logistica.PurchaseReceipt ADD Rfc varchar(50) NULL;
IF COL_LENGTH('logistica.PurchaseReceiptLine', 'Rfc') IS NULL ALTER TABLE logistica.PurchaseReceiptLine ADD Rfc varchar(50) NULL;

-- SQL Server compila cada lote antes de ejecutar sus ALTER TABLE. Se abre un lote
-- nuevo para que las referencias siguientes reconozcan las columnas recién creadas.
GO

DECLARE @InitialRfc varchar(50) = 'OHM191112Q26';

UPDATE logistica.MaterialCategory SET Rfc = @InitialRfc WHERE Rfc IS NULL OR LTRIM(RTRIM(Rfc)) = '';
UPDATE logistica.Material SET Rfc = @InitialRfc WHERE Rfc IS NULL OR LTRIM(RTRIM(Rfc)) = '';
UPDATE logistica.Location SET Rfc = @InitialRfc WHERE Rfc IS NULL OR LTRIM(RTRIM(Rfc)) = '';
UPDATE logistica.StockBalance SET Rfc = @InitialRfc WHERE Rfc IS NULL OR LTRIM(RTRIM(Rfc)) = '';
UPDATE logistica.StockTransaction SET Rfc = @InitialRfc WHERE Rfc IS NULL OR LTRIM(RTRIM(Rfc)) = '';
UPDATE logistica.LocationMaterialAttachment SET Rfc = @InitialRfc WHERE Rfc IS NULL OR LTRIM(RTRIM(Rfc)) = '';
UPDATE logistica.VendorProfile SET Rfc = @InitialRfc WHERE Rfc IS NULL OR LTRIM(RTRIM(Rfc)) = '';
UPDATE logistica.PhysicalCountSession SET Rfc = @InitialRfc WHERE Rfc IS NULL OR LTRIM(RTRIM(Rfc)) = '';
UPDATE line SET Rfc = session.Rfc FROM logistica.PhysicalCountLine line JOIN logistica.PhysicalCountSession session ON session.Id = line.SessionId WHERE line.Rfc IS NULL OR LTRIM(RTRIM(line.Rfc)) = '';
UPDATE attachment SET Rfc = line.Rfc FROM logistica.PhysicalCountAttachment attachment JOIN logistica.PhysicalCountLine line ON line.Id = attachment.PhysicalCountLineId WHERE attachment.Rfc IS NULL OR LTRIM(RTRIM(attachment.Rfc)) = '';
UPDATE recountPlan SET Rfc = countSession.Rfc FROM logistica.PhysicalCountRecountPlan recountPlan JOIN logistica.PhysicalCountSession countSession ON countSession.Id = recountPlan.SessionId WHERE recountPlan.Rfc IS NULL OR LTRIM(RTRIM(recountPlan.Rfc)) = '';
UPDATE recountLine SET Rfc = recountPlan.Rfc FROM logistica.PhysicalCountRecountPlanLine recountLine JOIN logistica.PhysicalCountRecountPlan recountPlan ON recountPlan.Id = recountLine.RecountPlanId WHERE recountLine.Rfc IS NULL OR LTRIM(RTRIM(recountLine.Rfc)) = '';
UPDATE logistica.PurchaseOrder SET Rfc = @InitialRfc WHERE Rfc IS NULL OR LTRIM(RTRIM(Rfc)) = '';
UPDATE line SET Rfc = purchaseOrder.Rfc FROM logistica.PurchaseOrderLine line JOIN logistica.PurchaseOrder purchaseOrder ON purchaseOrder.Id = line.PurchaseOrderId WHERE line.Rfc IS NULL OR LTRIM(RTRIM(line.Rfc)) = '';
UPDATE allocation SET Rfc = line.Rfc FROM logistica.PurchaseOrderLineAllocation allocation JOIN logistica.PurchaseOrderLine line ON line.Id = allocation.PurchaseOrderLineId WHERE allocation.Rfc IS NULL OR LTRIM(RTRIM(allocation.Rfc)) = '';
UPDATE roomScope SET Rfc = purchaseOrder.Rfc FROM logistica.PurchaseOrderRoomScope roomScope JOIN logistica.PurchaseOrder purchaseOrder ON purchaseOrder.Id = roomScope.PurchaseOrderId WHERE roomScope.Rfc IS NULL OR LTRIM(RTRIM(roomScope.Rfc)) = '';
UPDATE receipt SET Rfc = purchaseOrder.Rfc FROM logistica.PurchaseReceipt receipt JOIN logistica.PurchaseOrder purchaseOrder ON purchaseOrder.Id = receipt.PurchaseOrderId WHERE receipt.Rfc IS NULL OR LTRIM(RTRIM(receipt.Rfc)) = '';
UPDATE receiptLine SET Rfc = receipt.Rfc FROM logistica.PurchaseReceiptLine receiptLine JOIN logistica.PurchaseReceipt receipt ON receipt.Id = receiptLine.PurchaseReceiptId WHERE receiptLine.Rfc IS NULL OR LTRIM(RTRIM(receiptLine.Rfc)) = '';

INSERT INTO dbo.BusinessPartnerRfcScope (Rfc, BusinessPartnerId, CreatedBy)
SELECT DISTINCT @InitialRfc, source.BusinessPartnerId, '20260713_logistics_rfc_scope'
FROM
(
    SELECT BusinessPartnerId FROM logistica.Material WHERE BusinessPartnerId IS NOT NULL
    UNION
    SELECT BusinessPartnerId FROM logistica.PurchaseOrder
    UNION
    SELECT BusinessPartnerId FROM logistica.VendorProfile
) source
WHERE NOT EXISTS
(
    SELECT 1
    FROM dbo.BusinessPartnerRfcScope scope
    WHERE scope.Rfc = @InitialRfc
      AND scope.BusinessPartnerId = source.BusinessPartnerId
);

IF EXISTS
(
    SELECT 1
    FROM
    (
        SELECT Rfc FROM logistica.MaterialCategory UNION ALL
        SELECT Rfc FROM logistica.Material UNION ALL
        SELECT Rfc FROM logistica.Location UNION ALL
        SELECT Rfc FROM logistica.StockBalance UNION ALL
        SELECT Rfc FROM logistica.StockTransaction UNION ALL
        SELECT Rfc FROM logistica.LocationMaterialAttachment UNION ALL
        SELECT Rfc FROM logistica.VendorProfile UNION ALL
        SELECT Rfc FROM logistica.PhysicalCountSession UNION ALL
        SELECT Rfc FROM logistica.PhysicalCountLine UNION ALL
        SELECT Rfc FROM logistica.PhysicalCountAttachment UNION ALL
        SELECT Rfc FROM logistica.PhysicalCountRecountPlan UNION ALL
        SELECT Rfc FROM logistica.PhysicalCountRecountPlanLine UNION ALL
        SELECT Rfc FROM logistica.PurchaseOrder UNION ALL
        SELECT Rfc FROM logistica.PurchaseOrderLine UNION ALL
        SELECT Rfc FROM logistica.PurchaseOrderLineAllocation UNION ALL
        SELECT Rfc FROM logistica.PurchaseOrderRoomScope UNION ALL
        SELECT Rfc FROM logistica.PurchaseReceipt UNION ALL
        SELECT Rfc FROM logistica.PurchaseReceiptLine
    ) scoped
    WHERE scoped.Rfc IS NULL OR LTRIM(RTRIM(scoped.Rfc)) = ''
)
    THROW 51000, 'No se pudo asignar RFC a todas las filas modernas de Logistica.', 1;

IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID('logistica.MaterialCategory') AND name='Rfc' AND is_nullable=1) ALTER TABLE logistica.MaterialCategory ALTER COLUMN Rfc varchar(50) NOT NULL;
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID('logistica.Material') AND name='Rfc' AND is_nullable=1) ALTER TABLE logistica.Material ALTER COLUMN Rfc varchar(50) NOT NULL;
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID('logistica.Location') AND name='Rfc' AND is_nullable=1) ALTER TABLE logistica.Location ALTER COLUMN Rfc varchar(50) NOT NULL;
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID('logistica.StockBalance') AND name='Rfc' AND is_nullable=1) ALTER TABLE logistica.StockBalance ALTER COLUMN Rfc varchar(50) NOT NULL;
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID('logistica.StockTransaction') AND name='Rfc' AND is_nullable=1) ALTER TABLE logistica.StockTransaction ALTER COLUMN Rfc varchar(50) NOT NULL;
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID('logistica.LocationMaterialAttachment') AND name='Rfc' AND is_nullable=1) ALTER TABLE logistica.LocationMaterialAttachment ALTER COLUMN Rfc varchar(50) NOT NULL;
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID('logistica.VendorProfile') AND name='Rfc' AND is_nullable=1) ALTER TABLE logistica.VendorProfile ALTER COLUMN Rfc varchar(50) NOT NULL;
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID('logistica.PhysicalCountSession') AND name='Rfc' AND is_nullable=1) ALTER TABLE logistica.PhysicalCountSession ALTER COLUMN Rfc varchar(50) NOT NULL;
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID('logistica.PhysicalCountLine') AND name='Rfc' AND is_nullable=1) ALTER TABLE logistica.PhysicalCountLine ALTER COLUMN Rfc varchar(50) NOT NULL;
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID('logistica.PhysicalCountAttachment') AND name='Rfc' AND is_nullable=1) ALTER TABLE logistica.PhysicalCountAttachment ALTER COLUMN Rfc varchar(50) NOT NULL;
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID('logistica.PhysicalCountRecountPlan') AND name='Rfc' AND is_nullable=1) ALTER TABLE logistica.PhysicalCountRecountPlan ALTER COLUMN Rfc varchar(50) NOT NULL;
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID('logistica.PhysicalCountRecountPlanLine') AND name='Rfc' AND is_nullable=1) ALTER TABLE logistica.PhysicalCountRecountPlanLine ALTER COLUMN Rfc varchar(50) NOT NULL;
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID('logistica.PurchaseOrder') AND name='Rfc' AND is_nullable=1) ALTER TABLE logistica.PurchaseOrder ALTER COLUMN Rfc varchar(50) NOT NULL;
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID('logistica.PurchaseOrderLine') AND name='Rfc' AND is_nullable=1) ALTER TABLE logistica.PurchaseOrderLine ALTER COLUMN Rfc varchar(50) NOT NULL;
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID('logistica.PurchaseOrderLineAllocation') AND name='Rfc' AND is_nullable=1) ALTER TABLE logistica.PurchaseOrderLineAllocation ALTER COLUMN Rfc varchar(50) NOT NULL;
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID('logistica.PurchaseOrderRoomScope') AND name='Rfc' AND is_nullable=1) ALTER TABLE logistica.PurchaseOrderRoomScope ALTER COLUMN Rfc varchar(50) NOT NULL;
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID('logistica.PurchaseReceipt') AND name='Rfc' AND is_nullable=1) ALTER TABLE logistica.PurchaseReceipt ALTER COLUMN Rfc varchar(50) NOT NULL;
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID('logistica.PurchaseReceiptLine') AND name='Rfc' AND is_nullable=1) ALTER TABLE logistica.PurchaseReceiptLine ALTER COLUMN Rfc varchar(50) NOT NULL;

IF OBJECT_ID('logistica.DF_MaterialCategory_Rfc', 'D') IS NULL ALTER TABLE logistica.MaterialCategory ADD CONSTRAINT DF_MaterialCategory_Rfc DEFAULT (CONVERT(varchar(50), SESSION_CONTEXT(N'OrionRfc'))) FOR Rfc;
IF OBJECT_ID('logistica.DF_Material_Rfc', 'D') IS NULL ALTER TABLE logistica.Material ADD CONSTRAINT DF_Material_Rfc DEFAULT (CONVERT(varchar(50), SESSION_CONTEXT(N'OrionRfc'))) FOR Rfc;
IF OBJECT_ID('logistica.DF_Location_Rfc', 'D') IS NULL ALTER TABLE logistica.Location ADD CONSTRAINT DF_Location_Rfc DEFAULT (CONVERT(varchar(50), SESSION_CONTEXT(N'OrionRfc'))) FOR Rfc;
IF OBJECT_ID('logistica.DF_StockBalance_Rfc', 'D') IS NULL ALTER TABLE logistica.StockBalance ADD CONSTRAINT DF_StockBalance_Rfc DEFAULT (CONVERT(varchar(50), SESSION_CONTEXT(N'OrionRfc'))) FOR Rfc;
IF OBJECT_ID('logistica.DF_StockTransaction_Rfc', 'D') IS NULL ALTER TABLE logistica.StockTransaction ADD CONSTRAINT DF_StockTransaction_Rfc DEFAULT (CONVERT(varchar(50), SESSION_CONTEXT(N'OrionRfc'))) FOR Rfc;
IF OBJECT_ID('logistica.DF_LocationMaterialAttachment_Rfc', 'D') IS NULL ALTER TABLE logistica.LocationMaterialAttachment ADD CONSTRAINT DF_LocationMaterialAttachment_Rfc DEFAULT (CONVERT(varchar(50), SESSION_CONTEXT(N'OrionRfc'))) FOR Rfc;
IF OBJECT_ID('logistica.DF_VendorProfile_Rfc', 'D') IS NULL ALTER TABLE logistica.VendorProfile ADD CONSTRAINT DF_VendorProfile_Rfc DEFAULT (CONVERT(varchar(50), SESSION_CONTEXT(N'OrionRfc'))) FOR Rfc;
IF OBJECT_ID('logistica.DF_PhysicalCountSession_Rfc', 'D') IS NULL ALTER TABLE logistica.PhysicalCountSession ADD CONSTRAINT DF_PhysicalCountSession_Rfc DEFAULT (CONVERT(varchar(50), SESSION_CONTEXT(N'OrionRfc'))) FOR Rfc;
IF OBJECT_ID('logistica.DF_PhysicalCountLine_Rfc', 'D') IS NULL ALTER TABLE logistica.PhysicalCountLine ADD CONSTRAINT DF_PhysicalCountLine_Rfc DEFAULT (CONVERT(varchar(50), SESSION_CONTEXT(N'OrionRfc'))) FOR Rfc;
IF OBJECT_ID('logistica.DF_PhysicalCountAttachment_Rfc', 'D') IS NULL ALTER TABLE logistica.PhysicalCountAttachment ADD CONSTRAINT DF_PhysicalCountAttachment_Rfc DEFAULT (CONVERT(varchar(50), SESSION_CONTEXT(N'OrionRfc'))) FOR Rfc;
IF OBJECT_ID('logistica.DF_PhysicalCountRecountPlan_Rfc', 'D') IS NULL ALTER TABLE logistica.PhysicalCountRecountPlan ADD CONSTRAINT DF_PhysicalCountRecountPlan_Rfc DEFAULT (CONVERT(varchar(50), SESSION_CONTEXT(N'OrionRfc'))) FOR Rfc;
IF OBJECT_ID('logistica.DF_PhysicalCountRecountPlanLine_Rfc', 'D') IS NULL ALTER TABLE logistica.PhysicalCountRecountPlanLine ADD CONSTRAINT DF_PhysicalCountRecountPlanLine_Rfc DEFAULT (CONVERT(varchar(50), SESSION_CONTEXT(N'OrionRfc'))) FOR Rfc;
IF OBJECT_ID('logistica.DF_PurchaseOrder_Rfc', 'D') IS NULL ALTER TABLE logistica.PurchaseOrder ADD CONSTRAINT DF_PurchaseOrder_Rfc DEFAULT (CONVERT(varchar(50), SESSION_CONTEXT(N'OrionRfc'))) FOR Rfc;
IF OBJECT_ID('logistica.DF_PurchaseOrderLine_Rfc', 'D') IS NULL ALTER TABLE logistica.PurchaseOrderLine ADD CONSTRAINT DF_PurchaseOrderLine_Rfc DEFAULT (CONVERT(varchar(50), SESSION_CONTEXT(N'OrionRfc'))) FOR Rfc;
IF OBJECT_ID('logistica.DF_PurchaseOrderLineAllocation_Rfc', 'D') IS NULL ALTER TABLE logistica.PurchaseOrderLineAllocation ADD CONSTRAINT DF_PurchaseOrderLineAllocation_Rfc DEFAULT (CONVERT(varchar(50), SESSION_CONTEXT(N'OrionRfc'))) FOR Rfc;
IF OBJECT_ID('logistica.DF_PurchaseOrderRoomScope_Rfc', 'D') IS NULL ALTER TABLE logistica.PurchaseOrderRoomScope ADD CONSTRAINT DF_PurchaseOrderRoomScope_Rfc DEFAULT (CONVERT(varchar(50), SESSION_CONTEXT(N'OrionRfc'))) FOR Rfc;
IF OBJECT_ID('logistica.DF_PurchaseReceipt_Rfc', 'D') IS NULL ALTER TABLE logistica.PurchaseReceipt ADD CONSTRAINT DF_PurchaseReceipt_Rfc DEFAULT (CONVERT(varchar(50), SESSION_CONTEXT(N'OrionRfc'))) FOR Rfc;
IF OBJECT_ID('logistica.DF_PurchaseReceiptLine_Rfc', 'D') IS NULL ALTER TABLE logistica.PurchaseReceiptLine ADD CONSTRAINT DF_PurchaseReceiptLine_Rfc DEFAULT (CONVERT(varchar(50), SESSION_CONTEXT(N'OrionRfc'))) FOR Rfc;

DROP INDEX IF EXISTS UX_MaterialCategory_CategoryName ON logistica.MaterialCategory;
DROP INDEX IF EXISTS UX_MaterialCategory_LegacyCategoryId ON logistica.MaterialCategory;
DROP INDEX IF EXISTS UX_Material_MaterialCode ON logistica.Material;
DROP INDEX IF EXISTS UX_Material_LegacyMaterialId ON logistica.Material;
DROP INDEX IF EXISTS UX_Location_LocationCode ON logistica.Location;
DROP INDEX IF EXISTS UX_Location_LegacyEspacioId ON logistica.Location;
DROP INDEX IF EXISTS UX_PhysicalCountSession_SessionCode ON logistica.PhysicalCountSession;
DROP INDEX IF EXISTS UX_PurchaseOrder_Code ON logistica.PurchaseOrder;
DROP INDEX IF EXISTS UX_PurchaseReceipt_Code ON logistica.PurchaseReceipt;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('logistica.MaterialCategory') AND name = 'UX_MaterialCategory_RfcCategoryName') CREATE UNIQUE INDEX UX_MaterialCategory_RfcCategoryName ON logistica.MaterialCategory (Rfc, CategoryName);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('logistica.MaterialCategory') AND name = 'UX_MaterialCategory_RfcLegacyCategoryId') CREATE UNIQUE INDEX UX_MaterialCategory_RfcLegacyCategoryId ON logistica.MaterialCategory (Rfc, LegacyCategoryId) WHERE LegacyCategoryId IS NOT NULL;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('logistica.Material') AND name = 'UX_Material_RfcMaterialCode') CREATE UNIQUE INDEX UX_Material_RfcMaterialCode ON logistica.Material (Rfc, MaterialCode);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('logistica.Material') AND name = 'UX_Material_RfcLegacyMaterialId') CREATE UNIQUE INDEX UX_Material_RfcLegacyMaterialId ON logistica.Material (Rfc, LegacyMaterialId) WHERE LegacyMaterialId IS NOT NULL;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('logistica.Location') AND name = 'UX_Location_RfcLocationCode') CREATE UNIQUE INDEX UX_Location_RfcLocationCode ON logistica.Location (Rfc, LocationCode);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('logistica.Location') AND name = 'UX_Location_RfcLegacyEspacioId') CREATE UNIQUE INDEX UX_Location_RfcLegacyEspacioId ON logistica.Location (Rfc, LegacyEspacioId) WHERE LegacyEspacioId IS NOT NULL;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('logistica.PhysicalCountSession') AND name = 'UX_PhysicalCountSession_RfcSessionCode') CREATE UNIQUE INDEX UX_PhysicalCountSession_RfcSessionCode ON logistica.PhysicalCountSession (Rfc, SessionCode);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('logistica.PurchaseOrder') AND name = 'UX_PurchaseOrder_RfcCode') CREATE UNIQUE INDEX UX_PurchaseOrder_RfcCode ON logistica.PurchaseOrder (Rfc, PurchaseOrderCode);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('logistica.PurchaseReceipt') AND name = 'UX_PurchaseReceipt_RfcCode') CREATE UNIQUE INDEX UX_PurchaseReceipt_RfcCode ON logistica.PurchaseReceipt (Rfc, ReceiptCode);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('logistica.Material') AND name = 'IX_Material_RfcActive') CREATE INDEX IX_Material_RfcActive ON logistica.Material (Rfc, IsActive, MaterialStatus, Id);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('logistica.Location') AND name = 'IX_Location_RfcActive') CREATE INDEX IX_Location_RfcActive ON logistica.Location (Rfc, IsActive, IsInventoryEnabled, Id);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('logistica.StockBalance') AND name = 'IX_StockBalance_RfcMaterial') CREATE INDEX IX_StockBalance_RfcMaterial ON logistica.StockBalance (Rfc, MaterialId, LocationId);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('logistica.StockTransaction') AND name = 'IX_StockTransaction_RfcOccurredAt') CREATE INDEX IX_StockTransaction_RfcOccurredAt ON logistica.StockTransaction (Rfc, OccurredAt DESC, Id DESC);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('logistica.PhysicalCountSession') AND name = 'IX_PhysicalCountSession_RfcStatus') CREATE INDEX IX_PhysicalCountSession_RfcStatus ON logistica.PhysicalCountSession (Rfc, Status, CreatedAt DESC, Id DESC);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('logistica.PurchaseOrder') AND name = 'IX_PurchaseOrder_RfcStatusDate') CREATE INDEX IX_PurchaseOrder_RfcStatusDate ON logistica.PurchaseOrder (Rfc, Status, OrderDate DESC, Id DESC);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('logistica.MaterialCategory') AND name = 'UX_MaterialCategory_RfcId') CREATE UNIQUE INDEX UX_MaterialCategory_RfcId ON logistica.MaterialCategory (Rfc, Id);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('logistica.Material') AND name = 'UX_Material_RfcId') CREATE UNIQUE INDEX UX_Material_RfcId ON logistica.Material (Rfc, Id);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('logistica.Location') AND name = 'UX_Location_RfcId') CREATE UNIQUE INDEX UX_Location_RfcId ON logistica.Location (Rfc, Id);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('logistica.StockBalance') AND name = 'UX_StockBalance_RfcId') CREATE UNIQUE INDEX UX_StockBalance_RfcId ON logistica.StockBalance (Rfc, Id);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('logistica.PhysicalCountSession') AND name = 'UX_PhysicalCountSession_RfcId') CREATE UNIQUE INDEX UX_PhysicalCountSession_RfcId ON logistica.PhysicalCountSession (Rfc, Id);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('logistica.PhysicalCountLine') AND name = 'UX_PhysicalCountLine_RfcId') CREATE UNIQUE INDEX UX_PhysicalCountLine_RfcId ON logistica.PhysicalCountLine (Rfc, Id);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('logistica.PhysicalCountRecountPlan') AND name = 'UX_PhysicalCountRecountPlan_RfcId') CREATE UNIQUE INDEX UX_PhysicalCountRecountPlan_RfcId ON logistica.PhysicalCountRecountPlan (Rfc, Id);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('logistica.PurchaseOrder') AND name = 'UX_PurchaseOrder_RfcId') CREATE UNIQUE INDEX UX_PurchaseOrder_RfcId ON logistica.PurchaseOrder (Rfc, Id);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('logistica.PurchaseOrderLine') AND name = 'UX_PurchaseOrderLine_RfcId') CREATE UNIQUE INDEX UX_PurchaseOrderLine_RfcId ON logistica.PurchaseOrderLine (Rfc, Id);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('logistica.PurchaseOrderLineAllocation') AND name = 'UX_PurchaseOrderLineAllocation_RfcId') CREATE UNIQUE INDEX UX_PurchaseOrderLineAllocation_RfcId ON logistica.PurchaseOrderLineAllocation (Rfc, Id);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('logistica.PurchaseReceipt') AND name = 'UX_PurchaseReceipt_RfcId') CREATE UNIQUE INDEX UX_PurchaseReceipt_RfcId ON logistica.PurchaseReceipt (Rfc, Id);

IF EXISTS
(
    SELECT 1
    FROM sys.key_constraints constraintInfo
    JOIN sys.index_columns indexColumn
      ON indexColumn.object_id = constraintInfo.parent_object_id
     AND indexColumn.index_id = constraintInfo.unique_index_id
    WHERE constraintInfo.parent_object_id = OBJECT_ID('logistica.VendorProfile')
      AND constraintInfo.[type] = 'PK'
    GROUP BY constraintInfo.[name]
    HAVING COUNT(*) = 1
)
BEGIN
    ALTER TABLE logistica.VendorProfile DROP CONSTRAINT PK_VendorProfile;
    ALTER TABLE logistica.VendorProfile ADD CONSTRAINT PK_VendorProfile PRIMARY KEY (Rfc, BusinessPartnerId);
END;

IF OBJECT_ID('logistica.FK_Material_Category_Rfc', 'F') IS NULL
    ALTER TABLE logistica.Material ADD CONSTRAINT FK_Material_Category_Rfc FOREIGN KEY (Rfc, CategoryId) REFERENCES logistica.MaterialCategory (Rfc, Id);
IF OBJECT_ID('logistica.FK_Material_BusinessPartner_Rfc', 'F') IS NULL
    ALTER TABLE logistica.Material ADD CONSTRAINT FK_Material_BusinessPartner_Rfc FOREIGN KEY (Rfc, BusinessPartnerId) REFERENCES dbo.BusinessPartnerRfcScope (Rfc, BusinessPartnerId);
IF OBJECT_ID('logistica.FK_Location_Parent_Rfc', 'F') IS NULL
    ALTER TABLE logistica.Location ADD CONSTRAINT FK_Location_Parent_Rfc FOREIGN KEY (Rfc, ParentLocationId) REFERENCES logistica.Location (Rfc, Id);
IF OBJECT_ID('logistica.FK_StockBalance_Location_Rfc', 'F') IS NULL
    ALTER TABLE logistica.StockBalance ADD CONSTRAINT FK_StockBalance_Location_Rfc FOREIGN KEY (Rfc, LocationId) REFERENCES logistica.Location (Rfc, Id);
IF OBJECT_ID('logistica.FK_StockBalance_Material_Rfc', 'F') IS NULL
    ALTER TABLE logistica.StockBalance ADD CONSTRAINT FK_StockBalance_Material_Rfc FOREIGN KEY (Rfc, MaterialId) REFERENCES logistica.Material (Rfc, Id);
IF OBJECT_ID('logistica.FK_StockTransaction_Balance_Rfc', 'F') IS NULL
    ALTER TABLE logistica.StockTransaction ADD CONSTRAINT FK_StockTransaction_Balance_Rfc FOREIGN KEY (Rfc, StockBalanceId) REFERENCES logistica.StockBalance (Rfc, Id);
IF OBJECT_ID('logistica.FK_StockTransaction_Location_Rfc', 'F') IS NULL
    ALTER TABLE logistica.StockTransaction ADD CONSTRAINT FK_StockTransaction_Location_Rfc FOREIGN KEY (Rfc, LocationId) REFERENCES logistica.Location (Rfc, Id);
IF OBJECT_ID('logistica.FK_StockTransaction_Material_Rfc', 'F') IS NULL
    ALTER TABLE logistica.StockTransaction ADD CONSTRAINT FK_StockTransaction_Material_Rfc FOREIGN KEY (Rfc, MaterialId) REFERENCES logistica.Material (Rfc, Id);
IF OBJECT_ID('logistica.FK_LocationMaterialAttachment_Location_Rfc', 'F') IS NULL
    ALTER TABLE logistica.LocationMaterialAttachment ADD CONSTRAINT FK_LocationMaterialAttachment_Location_Rfc FOREIGN KEY (Rfc, LocationId) REFERENCES logistica.Location (Rfc, Id);
IF OBJECT_ID('logistica.FK_LocationMaterialAttachment_Material_Rfc', 'F') IS NULL
    ALTER TABLE logistica.LocationMaterialAttachment ADD CONSTRAINT FK_LocationMaterialAttachment_Material_Rfc FOREIGN KEY (Rfc, MaterialId) REFERENCES logistica.Material (Rfc, Id);
IF OBJECT_ID('logistica.FK_PhysicalCountSession_Location_Rfc', 'F') IS NULL
    ALTER TABLE logistica.PhysicalCountSession ADD CONSTRAINT FK_PhysicalCountSession_Location_Rfc FOREIGN KEY (Rfc, LocationId) REFERENCES logistica.Location (Rfc, Id);
IF OBJECT_ID('logistica.FK_PhysicalCountLine_Session_Rfc', 'F') IS NULL
    ALTER TABLE logistica.PhysicalCountLine ADD CONSTRAINT FK_PhysicalCountLine_Session_Rfc FOREIGN KEY (Rfc, SessionId) REFERENCES logistica.PhysicalCountSession (Rfc, Id);
IF OBJECT_ID('logistica.FK_PhysicalCountLine_Balance_Rfc', 'F') IS NULL
    ALTER TABLE logistica.PhysicalCountLine ADD CONSTRAINT FK_PhysicalCountLine_Balance_Rfc FOREIGN KEY (Rfc, StockBalanceId) REFERENCES logistica.StockBalance (Rfc, Id);
IF OBJECT_ID('logistica.FK_PhysicalCountLine_Location_Rfc', 'F') IS NULL
    ALTER TABLE logistica.PhysicalCountLine ADD CONSTRAINT FK_PhysicalCountLine_Location_Rfc FOREIGN KEY (Rfc, LocationId) REFERENCES logistica.Location (Rfc, Id);
IF OBJECT_ID('logistica.FK_PhysicalCountLine_Material_Rfc', 'F') IS NULL
    ALTER TABLE logistica.PhysicalCountLine ADD CONSTRAINT FK_PhysicalCountLine_Material_Rfc FOREIGN KEY (Rfc, MaterialId) REFERENCES logistica.Material (Rfc, Id);
IF OBJECT_ID('logistica.FK_PhysicalCountAttachment_Line_Rfc', 'F') IS NULL
    ALTER TABLE logistica.PhysicalCountAttachment ADD CONSTRAINT FK_PhysicalCountAttachment_Line_Rfc FOREIGN KEY (Rfc, PhysicalCountLineId) REFERENCES logistica.PhysicalCountLine (Rfc, Id);
IF OBJECT_ID('logistica.FK_PhysicalCountRecountPlan_Session_Rfc', 'F') IS NULL
    ALTER TABLE logistica.PhysicalCountRecountPlan ADD CONSTRAINT FK_PhysicalCountRecountPlan_Session_Rfc FOREIGN KEY (Rfc, SessionId) REFERENCES logistica.PhysicalCountSession (Rfc, Id);
IF OBJECT_ID('logistica.FK_PhysicalCountRecountPlanLine_Plan_Rfc', 'F') IS NULL
    ALTER TABLE logistica.PhysicalCountRecountPlanLine ADD CONSTRAINT FK_PhysicalCountRecountPlanLine_Plan_Rfc FOREIGN KEY (Rfc, RecountPlanId) REFERENCES logistica.PhysicalCountRecountPlan (Rfc, Id);
IF OBJECT_ID('logistica.FK_PhysicalCountRecountPlanLine_Line_Rfc', 'F') IS NULL
    ALTER TABLE logistica.PhysicalCountRecountPlanLine ADD CONSTRAINT FK_PhysicalCountRecountPlanLine_Line_Rfc FOREIGN KEY (Rfc, PhysicalCountLineId) REFERENCES logistica.PhysicalCountLine (Rfc, Id);
IF OBJECT_ID('logistica.FK_VendorProfile_BusinessPartner_Rfc', 'F') IS NULL
    ALTER TABLE logistica.VendorProfile ADD CONSTRAINT FK_VendorProfile_BusinessPartner_Rfc FOREIGN KEY (Rfc, BusinessPartnerId) REFERENCES dbo.BusinessPartnerRfcScope (Rfc, BusinessPartnerId);
IF OBJECT_ID('logistica.FK_PurchaseOrder_BusinessPartner_Rfc', 'F') IS NULL
    ALTER TABLE logistica.PurchaseOrder ADD CONSTRAINT FK_PurchaseOrder_BusinessPartner_Rfc FOREIGN KEY (Rfc, BusinessPartnerId) REFERENCES dbo.BusinessPartnerRfcScope (Rfc, BusinessPartnerId);
IF OBJECT_ID('logistica.FK_PurchaseOrderLine_Order_Rfc', 'F') IS NULL
    ALTER TABLE logistica.PurchaseOrderLine ADD CONSTRAINT FK_PurchaseOrderLine_Order_Rfc FOREIGN KEY (Rfc, PurchaseOrderId) REFERENCES logistica.PurchaseOrder (Rfc, Id);
IF OBJECT_ID('logistica.FK_PurchaseOrderLine_Material_Rfc', 'F') IS NULL
    ALTER TABLE logistica.PurchaseOrderLine ADD CONSTRAINT FK_PurchaseOrderLine_Material_Rfc FOREIGN KEY (Rfc, MaterialId) REFERENCES logistica.Material (Rfc, Id);
IF OBJECT_ID('logistica.FK_PurchaseOrderLineAllocation_Line_Rfc', 'F') IS NULL
    ALTER TABLE logistica.PurchaseOrderLineAllocation ADD CONSTRAINT FK_PurchaseOrderLineAllocation_Line_Rfc FOREIGN KEY (Rfc, PurchaseOrderLineId) REFERENCES logistica.PurchaseOrderLine (Rfc, Id);
IF OBJECT_ID('logistica.FK_PurchaseOrderLineAllocation_Location_Rfc', 'F') IS NULL
    ALTER TABLE logistica.PurchaseOrderLineAllocation ADD CONSTRAINT FK_PurchaseOrderLineAllocation_Location_Rfc FOREIGN KEY (Rfc, LocationId) REFERENCES logistica.Location (Rfc, Id);
IF OBJECT_ID('logistica.FK_PurchaseOrderRoomScope_Order_Rfc', 'F') IS NULL
    ALTER TABLE logistica.PurchaseOrderRoomScope ADD CONSTRAINT FK_PurchaseOrderRoomScope_Order_Rfc FOREIGN KEY (Rfc, PurchaseOrderId) REFERENCES logistica.PurchaseOrder (Rfc, Id);
IF OBJECT_ID('logistica.FK_PurchaseReceipt_Order_Rfc', 'F') IS NULL
    ALTER TABLE logistica.PurchaseReceipt ADD CONSTRAINT FK_PurchaseReceipt_Order_Rfc FOREIGN KEY (Rfc, PurchaseOrderId) REFERENCES logistica.PurchaseOrder (Rfc, Id);
IF OBJECT_ID('logistica.FK_PurchaseReceiptLine_Receipt_Rfc', 'F') IS NULL
    ALTER TABLE logistica.PurchaseReceiptLine ADD CONSTRAINT FK_PurchaseReceiptLine_Receipt_Rfc FOREIGN KEY (Rfc, PurchaseReceiptId) REFERENCES logistica.PurchaseReceipt (Rfc, Id);
IF OBJECT_ID('logistica.FK_PurchaseReceiptLine_Allocation_Rfc', 'F') IS NULL
    ALTER TABLE logistica.PurchaseReceiptLine ADD CONSTRAINT FK_PurchaseReceiptLine_Allocation_Rfc FOREIGN KEY (Rfc, PurchaseOrderLineAllocationId) REFERENCES logistica.PurchaseOrderLineAllocation (Rfc, Id);
IF OBJECT_ID('logistica.FK_PurchaseReceiptLine_Line_Rfc', 'F') IS NULL
    ALTER TABLE logistica.PurchaseReceiptLine ADD CONSTRAINT FK_PurchaseReceiptLine_Line_Rfc FOREIGN KEY (Rfc, PurchaseOrderLineId) REFERENCES logistica.PurchaseOrderLine (Rfc, Id);
IF OBJECT_ID('logistica.FK_PurchaseReceiptLine_Location_Rfc', 'F') IS NULL
    ALTER TABLE logistica.PurchaseReceiptLine ADD CONSTRAINT FK_PurchaseReceiptLine_Location_Rfc FOREIGN KEY (Rfc, LocationId) REFERENCES logistica.Location (Rfc, Id);
IF OBJECT_ID('logistica.FK_PurchaseReceiptLine_Material_Rfc', 'F') IS NULL
    ALTER TABLE logistica.PurchaseReceiptLine ADD CONSTRAINT FK_PurchaseReceiptLine_Material_Rfc FOREIGN KEY (Rfc, MaterialId) REFERENCES logistica.Material (Rfc, Id);

COMMIT TRANSACTION;
