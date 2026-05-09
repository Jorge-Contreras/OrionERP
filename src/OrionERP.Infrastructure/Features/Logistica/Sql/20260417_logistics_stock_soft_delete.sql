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
