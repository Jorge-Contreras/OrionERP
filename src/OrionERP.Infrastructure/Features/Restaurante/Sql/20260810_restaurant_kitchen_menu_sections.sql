SET NOCOUNT ON;
SET XACT_ABORT ON;

IF OBJECT_ID('restaurante.OrderLine', 'U') IS NULL
  THROW 51130, 'Ejecuta primero 20260713_restaurant_operations.sql.', 1;

BEGIN TRANSACTION;

IF COL_LENGTH('restaurante.OrderLine', 'MenuSectionIdSnapshot') IS NULL
BEGIN
  ALTER TABLE restaurante.OrderLine
    ADD MenuSectionIdSnapshot bigint NULL;
END;

IF COL_LENGTH('restaurante.OrderLine', 'MenuSectionNameSnapshot') IS NULL
BEGIN
  ALTER TABLE restaurante.OrderLine
    ADD MenuSectionNameSnapshot varchar(100) NULL;
END;

IF COL_LENGTH('restaurante.OrderLine', 'MenuSectionSortOrderSnapshot') IS NULL
BEGIN
  ALTER TABLE restaurante.OrderLine
    ADD MenuSectionSortOrderSnapshot int NULL;
END;

EXEC sys.sp_executesql N'
  UPDATE lineInfo
  SET MenuSectionIdSnapshot = COALESCE(lineInfo.MenuSectionIdSnapshot, sectionMatch.MenuSectionId),
      MenuSectionNameSnapshot = COALESCE(lineInfo.MenuSectionNameSnapshot, sectionMatch.MenuSectionName),
      MenuSectionSortOrderSnapshot = COALESCE(lineInfo.MenuSectionSortOrderSnapshot, sectionMatch.MenuSectionSortOrder)
  FROM restaurante.OrderLine lineInfo
  OUTER APPLY
  (
    SELECT TOP (1)
           sectionInfo.Id AS MenuSectionId,
           sectionInfo.[Name] AS MenuSectionName,
           sectionInfo.SortOrder AS MenuSectionSortOrder
    FROM restaurante.MenuItem menuItem
    JOIN restaurante.MenuSection sectionInfo
      ON sectionInfo.Rfc=menuItem.Rfc AND sectionInfo.Id=menuItem.MenuSectionId
    JOIN restaurante.Menu menuInfo
      ON menuInfo.Rfc=sectionInfo.Rfc AND menuInfo.Id=sectionInfo.MenuId
    WHERE menuItem.Rfc=lineInfo.Rfc
      AND menuItem.ProductId=lineInfo.ProductId
    ORDER BY menuInfo.IsActive DESC,menuInfo.IsPublished DESC,
             sectionInfo.SortOrder,sectionInfo.Id,menuItem.SortOrder
  ) sectionMatch
  WHERE lineInfo.IsCustom=0
    AND sectionMatch.MenuSectionId IS NOT NULL
    AND
    (
      lineInfo.MenuSectionIdSnapshot IS NULL
      OR lineInfo.MenuSectionNameSnapshot IS NULL
      OR lineInfo.MenuSectionSortOrderSnapshot IS NULL
    );';

COMMIT TRANSACTION;
