IF COL_LENGTH('logistica.Material', 'PrimaryImageThumbnail') IS NULL
BEGIN
    ALTER TABLE logistica.Material
        ADD PrimaryImageThumbnail varbinary(max) NULL;
END;
GO

IF COL_LENGTH('logistica.Material', 'PrimaryImageThumbnailContentType') IS NULL
BEGIN
    ALTER TABLE logistica.Material
        ADD PrimaryImageThumbnailContentType varchar(100) NULL;
END;
GO
