SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET NOCOUNT ON;

DELETE FROM logistica.MigrationIssue
WHERE IssueType IN
(
    'DuplicateLegacyCategory',
    'DuplicateLegacyInventory',
    'MissingUnitMapping',
    'MissingPurchaseUnitMapping',
    'MissingCategoryMapping'
);
GO

MERGE dbo.LegacyPartnerCategoryMapping AS target
USING (VALUES
    ('PROVEEDOR', 'Vendor', 1, 'Proveedor material o comercial'),
    ('PROVEDOR', 'Vendor', 1, 'Corrección de typo heredado'),
    ('CLIENTE', 'Customer', 0, 'Socio con rol de cliente'),
    ('ARRENDADOR', 'Landlord', 0, 'Proveedor inmobiliario'),
    ('TBD', 'ServiceProvider', 0, 'Rol provisional'),
    ('NO INFO', 'ServiceProvider', 0, 'Sin clasificación útil')
) AS src(LegacyValue, RoleCode, CreateVendorProfile, Notes)
ON target.LegacyValue = src.LegacyValue
WHEN MATCHED THEN
    UPDATE SET RoleCode = src.RoleCode, CreateVendorProfile = src.CreateVendorProfile, Notes = src.Notes
WHEN NOT MATCHED THEN
    INSERT (LegacyValue, RoleCode, CreateVendorProfile, Notes)
    VALUES (src.LegacyValue, src.RoleCode, src.CreateVendorProfile, src.Notes);
GO

MERGE logistica.LegacyUnitMapping AS target
USING (VALUES
    ('PIEZA', 'PIEZA', 'Unidad estándar'),
    ('PZ', 'PIEZA', 'Alias heredado'),
    ('PIZA', 'PIEZA', 'Typo heredado'),
    ('1', 'PIEZA', 'Valor inválido heredado'),
    ('CAJA', 'CAJA', 'Unidad estándar'),
    ('BOTELLA', 'BOTELLA', 'Unidad estándar'),
    ('BOLSA', 'BOLSA', 'Unidad estándar'),
    ('PAQUETE', 'PAQUETE', 'Unidad estándar'),
    ('FRASCO', 'FRASCO', 'Unidad estándar'),
    ('LATA', 'LATA', 'Unidad estándar')
) AS src(LegacyValue, CanonicalUnitName, Notes)
ON target.LegacyValue = src.LegacyValue
WHEN MATCHED THEN
    UPDATE SET CanonicalUnitName = src.CanonicalUnitName, Notes = src.Notes
WHEN NOT MATCHED THEN
    INSERT (LegacyValue, CanonicalUnitName, Notes)
    VALUES (src.LegacyValue, src.CanonicalUnitName, src.Notes);
GO

MERGE logistica.LegacyMaterialCategoryMapping AS target
USING (VALUES
    ('ABARROTES', 'ABARROTES', 'Consumable', 'Categoría alimentaria'),
    ('CONGELADOS', 'CONGELADOS', 'Consumable', 'Categoría alimentaria'),
    ('FRUTAS_VERDURAS', 'FRUTAS_VERDURAS', 'Consumable', 'Categoría alimentaria'),
    ('LACTEOS', 'LACTEOS', 'Consumable', 'Categoría alimentaria'),
    ('LIMPIEZA', 'LIMPIEZA', 'Consumable', 'Limpieza general'),
    ('LIMPIEZA PERSONAL', 'LIMPIEZA PERSONAL', 'Consumable', 'Higiene personal'),
    ('LIMPIEZA CORP', 'LIMPIEZA PERSONAL', 'Consumable', 'Normalización corporal'),
    ('COCINA', 'COCINA', 'Consumable', 'Consumibles de cocina'),
    ('BLANCOS', 'BLANCOS', 'Reusable', 'Textiles reutilizables'),
    ('UTENCILIOS DE COCINA', 'UTENCILIOS DE COCINA', 'Reusable', 'Utensilios'),
    ('HERRAMIENTA', 'HERRAMIENTA', 'Reusable', 'Herramientas'),
    ('FERRETERIA', 'FERRETERIA', 'Installed', 'Instalación y refacción'),
    ('PLOMERIA', 'PLOMERIA', 'Installed', 'Instalación y refacción'),
    ('PERFILES', 'PERFILES', 'Installed', 'Perfilería'),
    ('MANTENIMIENTO', 'MANTENIMIENTO', 'Installed', 'Material de mantenimiento'),
    ('MOBILIARIO 1', 'MOBILIARIO 1', 'AssetLike', 'Mobiliario'),
    ('MOBILIARIO', 'MOBILIARIO 1', 'AssetLike', 'Normalización de mobiliario'),
    ('ELECTRODOMESTICOS 1', 'ELECTRODOMESTICOS 1', 'AssetLike', 'Electrodomésticos'),
    ('NO CATEGORIZADO', 'NO CATEGORIZADO', 'Consumable', 'Pendiente de clasificar')
) AS src(LegacyValue, CanonicalCategoryName, MaterialClass, Notes)
ON target.LegacyValue = src.LegacyValue
WHEN MATCHED THEN
    UPDATE SET CanonicalCategoryName = src.CanonicalCategoryName, MaterialClass = src.MaterialClass, Notes = src.Notes
WHEN NOT MATCHED THEN
    INSERT (LegacyValue, CanonicalCategoryName, MaterialClass, Notes)
    VALUES (src.LegacyValue, src.CanonicalCategoryName, src.MaterialClass, src.Notes);
GO

MERGE logistica.UnitOfMeasure AS target
USING (
    SELECT
        u.id AS LegacyUnitId,
        LTRIM(RTRIM(CAST(u.Unidad AS varchar(50)))) AS UnitName,
        NULLIF(LTRIM(RTRIM(CAST(u.ABREVIACION AS varchar(10)))), '') AS Abbreviation,
        NULLIF(LTRIM(RTRIM(CAST(u.Descripcion AS varchar(200)))), '') AS [Description]
    FROM logistica.Unidades u
) AS src
ON target.LegacyUnitId = src.LegacyUnitId
WHEN MATCHED THEN
    UPDATE SET UnitName = src.UnitName, Abbreviation = src.Abbreviation, [Description] = src.[Description], IsActive = 1
WHEN NOT MATCHED THEN
    INSERT (LegacyUnitId, UnitName, Abbreviation, [Description], IsActive)
    VALUES (src.LegacyUnitId, src.UnitName, src.Abbreviation, src.[Description], 1);
GO

INSERT INTO logistica.UnitOfMeasure (UnitName, Abbreviation, [Description], IsActive)
SELECT seed.UnitName, seed.Abbreviation, seed.[Description], 1
FROM (VALUES
    ('PIEZA', 'PZ', 'Unidad individual'),
    ('NO DEFINIDA', 'ND', 'Valor de respaldo de migración')
) AS seed(UnitName, Abbreviation, [Description])
WHERE NOT EXISTS (
    SELECT 1
    FROM logistica.UnitOfMeasure existing
    WHERE existing.UnitName = seed.UnitName
);
GO

MERGE logistica.MaterialCategory AS target
USING (
    SELECT
        MIN(c.id) AS LegacyCategoryId,
        LTRIM(RTRIM(CAST(c.CATEGORIA_MATERIAL AS varchar(100)))) AS CategoryName,
        MAX(NULLIF(LTRIM(RTRIM(CAST(c.DESCRIPCION AS varchar(200)))), '')) AS [Description]
    FROM logistica.CATEGORIAS_MATERIAL c
    GROUP BY LTRIM(RTRIM(CAST(c.CATEGORIA_MATERIAL AS varchar(100))))
) AS src
ON target.LegacyCategoryId = src.LegacyCategoryId
WHEN MATCHED THEN
    UPDATE SET CategoryName = src.CategoryName, [Description] = src.[Description], IsActive = 1
WHEN NOT MATCHED THEN
    INSERT (LegacyCategoryId, CategoryName, [Description], IsActive)
    VALUES (src.LegacyCategoryId, src.CategoryName, src.[Description], 1);
GO

INSERT INTO logistica.MigrationIssue (IssueType, SourceTable, LegacyKey, IssueDescription)
SELECT
    'DuplicateLegacyCategory',
    'logistica.CATEGORIAS_MATERIAL',
    CAST(MIN(c.id) AS varchar(100)),
    CONCAT(
        'Categoría duplicada consolidada: ',
        LTRIM(RTRIM(CAST(c.CATEGORIA_MATERIAL AS varchar(100)))),
        ' (ids: ',
        STRING_AGG(CAST(c.id AS varchar(20)), ', '),
        ')'
    )
FROM logistica.CATEGORIAS_MATERIAL c
GROUP BY LTRIM(RTRIM(CAST(c.CATEGORIA_MATERIAL AS varchar(100))))
HAVING COUNT(*) > 1;
GO

INSERT INTO logistica.MaterialCategory (CategoryName, [Description], IsActive)
SELECT DISTINCT mapping.CanonicalCategoryName, mapping.CanonicalCategoryName, 1
FROM logistica.LegacyMaterialCategoryMapping mapping
WHERE NOT EXISTS (
    SELECT 1
    FROM logistica.MaterialCategory existing
    WHERE existing.CategoryName = mapping.CanonicalCategoryName
);
GO

INSERT INTO logistica.MigrationIssue (IssueType, SourceTable, LegacyKey, IssueDescription)
SELECT 'MissingUnitMapping', 'logistica.MATERIALES', CAST(m.ID AS varchar(100)),
       CONCAT('Unidad base sin mapeo: ', LTRIM(RTRIM(m.UNIDAD)))
FROM logistica.MATERIALES m
LEFT JOIN logistica.LegacyUnitMapping mapUnit
  ON mapUnit.LegacyValue = LTRIM(RTRIM(m.UNIDAD))
LEFT JOIN logistica.UnitOfMeasure u
  ON u.UnitName = COALESCE(mapUnit.CanonicalUnitName, NULLIF(LTRIM(RTRIM(m.UNIDAD)), ''))
WHERE NULLIF(LTRIM(RTRIM(m.UNIDAD)), '') IS NOT NULL
  AND u.Id IS NULL;
GO

INSERT INTO logistica.MigrationIssue (IssueType, SourceTable, LegacyKey, IssueDescription)
SELECT 'MissingPurchaseUnitMapping', 'logistica.MATERIALES', CAST(m.ID AS varchar(100)),
       CONCAT('Unidad de compra sin mapeo: ', LTRIM(RTRIM(m.UNIDAD_COMPRA)))
FROM logistica.MATERIALES m
LEFT JOIN logistica.LegacyUnitMapping mapUnit
  ON mapUnit.LegacyValue = LTRIM(RTRIM(m.UNIDAD_COMPRA))
LEFT JOIN logistica.UnitOfMeasure u
  ON u.UnitName = COALESCE(mapUnit.CanonicalUnitName, NULLIF(LTRIM(RTRIM(m.UNIDAD_COMPRA)), ''))
WHERE NULLIF(LTRIM(RTRIM(m.UNIDAD_COMPRA)), '') IS NOT NULL
  AND u.Id IS NULL;
GO

INSERT INTO logistica.MigrationIssue (IssueType, SourceTable, LegacyKey, IssueDescription)
SELECT 'MissingCategoryMapping', 'logistica.MATERIALES', CAST(m.ID AS varchar(100)),
       CONCAT('Categoría sin mapeo: ', LTRIM(RTRIM(m.CATEGORIA)))
FROM logistica.MATERIALES m
LEFT JOIN logistica.LegacyMaterialCategoryMapping mapCategory
  ON mapCategory.LegacyValue = LTRIM(RTRIM(m.CATEGORIA))
LEFT JOIN logistica.MaterialCategory categoryTarget
  ON categoryTarget.CategoryName = COALESCE(mapCategory.CanonicalCategoryName, NULLIF(LTRIM(RTRIM(m.CATEGORIA)), ''))
WHERE categoryTarget.Id IS NULL;
GO

MERGE dbo.BusinessPartner AS target
USING (
    SELECT
        p.id AS LegacyProveedorId,
        LTRIM(RTRIM(p.RazonSocial)) AS PartnerName,
        NULLIF(NULLIF(NULLIF(LTRIM(RTRIM(p.RFC)), ''), 'SIN DATOS'), 'SINDATOS') AS RFC,
        NULLIF(NULLIF(NULLIF(LTRIM(RTRIM(p.Email)), ''), 'SIN DATOS'), 'SINDATOS') AS Email,
        NULLIF(NULLIF(NULLIF(LTRIM(RTRIM(p.Tel)), ''), 'SIN DATOS'), 'SINDATOS') AS Phone,
        NULLIF(LTRIM(RTRIM(p.Calle)), '') AS Street,
        NULLIF(LTRIM(RTRIM(p.Colonia)), '') AS Neighborhood,
        NULLIF(LTRIM(RTRIM(p.Ciudad)), '') AS City,
        NULLIF(LTRIM(RTRIM(p.Estado)), '') AS [State],
        NULLIF(LTRIM(RTRIM(p.CPostal)), '') AS PostalCode,
        NULLIF(LTRIM(RTRIM(p.Giro)), '') AS BusinessLine,
        NULLIF(LTRIM(RTRIM(p.Notas)), '') AS Notes
    FROM dbo.Proveedores p
) AS src
ON target.LegacyProveedorId = src.LegacyProveedorId
WHEN MATCHED THEN
    UPDATE SET
        PartnerName = src.PartnerName,
        Rfc = src.RFC,
        Email = src.Email,
        Phone = src.Phone,
        Street = src.Street,
        Neighborhood = src.Neighborhood,
        City = src.City,
        [State] = src.[State],
        PostalCode = src.PostalCode,
        BusinessLine = src.BusinessLine,
        Notes = src.Notes,
        IsActive = 1,
        UpdatedAt = SYSUTCDATETIME()
WHEN NOT MATCHED THEN
    INSERT (LegacyProveedorId, PartnerName, Rfc, Email, Phone, Street, Neighborhood, City, [State], PostalCode, BusinessLine, Notes, IsActive)
    VALUES (src.LegacyProveedorId, src.PartnerName, src.RFC, src.Email, src.Phone, src.Street, src.Neighborhood, src.City, src.[State], src.PostalCode, src.BusinessLine, src.Notes, 1);
GO

INSERT INTO dbo.BusinessPartnerRole (BusinessPartnerId, RoleCode)
SELECT DISTINCT
    bp.Id,
    CASE
        WHEN UPPER(LTRIM(RTRIM(ISNULL(p.Giro, '')))) LIKE '%GAS%' THEN 'Utility'
        WHEN UPPER(LTRIM(RTRIM(ISNULL(p.Giro, '')))) LIKE '%ELECTR%' THEN 'Utility'
        WHEN UPPER(LTRIM(RTRIM(ISNULL(p.Giro, '')))) LIKE '%LUZ%' THEN 'Utility'
        WHEN UPPER(LTRIM(RTRIM(ISNULL(p.Giro, '')))) LIKE '%AGUA%' THEN 'Utility'
        ELSE COALESCE(mapPartner.RoleCode, 'ServiceProvider')
    END AS RoleCode
FROM dbo.Proveedores p
JOIN dbo.BusinessPartner bp
  ON bp.LegacyProveedorId = p.id
LEFT JOIN dbo.LegacyPartnerCategoryMapping mapPartner
  ON mapPartner.LegacyValue = UPPER(LTRIM(RTRIM(ISNULL(p.CategoriaEmpresa, 'NO INFO'))))
WHERE NOT EXISTS (
    SELECT 1
    FROM dbo.BusinessPartnerRole existing
    WHERE existing.BusinessPartnerId = bp.Id
      AND existing.RoleCode = CASE
          WHEN UPPER(LTRIM(RTRIM(ISNULL(p.Giro, '')))) LIKE '%GAS%' THEN 'Utility'
          WHEN UPPER(LTRIM(RTRIM(ISNULL(p.Giro, '')))) LIKE '%ELECTR%' THEN 'Utility'
          WHEN UPPER(LTRIM(RTRIM(ISNULL(p.Giro, '')))) LIKE '%LUZ%' THEN 'Utility'
          WHEN UPPER(LTRIM(RTRIM(ISNULL(p.Giro, '')))) LIKE '%AGUA%' THEN 'Utility'
          ELSE COALESCE(mapPartner.RoleCode, 'ServiceProvider')
      END
);
GO

MERGE logistica.VendorProfile AS target
USING (
    SELECT DISTINCT bp.Id AS BusinessPartnerId
    FROM dbo.BusinessPartner bp
    LEFT JOIN dbo.BusinessPartnerRole roleMap
      ON roleMap.BusinessPartnerId = bp.Id
    LEFT JOIN logistica.MATERIALES legacyMaterial
      ON legacyMaterial.PROVEEDOR_ID = bp.LegacyProveedorId
    WHERE roleMap.RoleCode = 'Vendor'
       OR legacyMaterial.ID IS NOT NULL
) AS src
ON target.BusinessPartnerId = src.BusinessPartnerId
WHEN MATCHED THEN
    UPDATE SET IsApproved = 1, UpdatedAt = SYSUTCDATETIME()
WHEN NOT MATCHED THEN
    INSERT (BusinessPartnerId, IsApproved)
    VALUES (src.BusinessPartnerId, 1);
GO

SET IDENTITY_INSERT logistica.Material ON;
GO

MERGE logistica.Material AS target
USING (
    SELECT
        legacy.ID AS LegacyMaterialId,
        legacy.ID AS MaterialId,
        CONCAT('MAT-', RIGHT(REPLICATE('0', 6) + CAST(legacy.ID AS varchar(20)), 6)) AS MaterialCode,
        legacy.DESCRIPCION AS [Description],
        COALESCE(baseUnit.Id, fallbackUnit.Id) AS BaseUnitId,
        CAST(ISNULL(legacy.CANTIDAD_UNIDAD_COMPRA, 1) AS decimal(18,4)) AS PurchaseQuantity,
        COALESCE(purchaseUnit.Id, baseUnit.Id, fallbackUnit.Id) AS PurchaseUnitId,
        bp.Id AS BusinessPartnerId,
        CAST(legacy.PRECIO AS decimal(18,4)) AS Price,
        legacy.FECHA_CREADO AS CreatedDate,
        legacy.FECHA_ACTUALIZADO AS UpdatedDate,
        NULLIF(LTRIM(RTRIM(legacy.MARCA)), '') AS Brand,
        NULLIF(LTRIM(RTRIM(legacy.MODELO)), '') AS Model,
        CAST(ISNULL(legacy.PERECEDERO, 0) AS bit) AS IsPerishable,
        legacy.DIAS_DURACION_CADUCIDAD AS ShelfLifeDays,
        CAST(ISNULL(legacy.REQUIERE_REFRIGERACION, 0) AS bit) AS RequiresRefrigeration,
        COALESCE(NULLIF(LTRIM(RTRIM(legacy.STATUS)), ''), 'ACTIVO') AS MaterialStatus,
        categoryTarget.Id AS CategoryId,
        NULLIF(LTRIM(RTRIM(legacy.CODIGO_BARRAS)), '') AS Barcode,
        NULLIF(LTRIM(RTRIM(legacy.CODIGO_PROVEEDOR)), '') AS VendorCode,
        legacy.IMAGEN AS PrimaryImage,
        CASE WHEN legacy.IMAGEN IS NULL THEN NULL ELSE CONCAT('legacy-material-', legacy.ID, '.jpg') END AS PrimaryImageFileName,
        CASE WHEN legacy.IMAGEN IS NULL THEN NULL ELSE 'image/jpeg' END AS PrimaryImageContentType,
        NULLIF(LTRIM(RTRIM(CAST(legacy.LINK_COMPRA AS varchar(max)))), '') AS PurchaseLink,
        COALESCE(mapCategory.MaterialClass, 'Consumable') AS MaterialClass
    FROM logistica.MATERIALES legacy
    LEFT JOIN logistica.LegacyUnitMapping baseMap
      ON baseMap.LegacyValue = LTRIM(RTRIM(legacy.UNIDAD))
    LEFT JOIN logistica.UnitOfMeasure baseUnit
      ON baseUnit.UnitName = COALESCE(baseMap.CanonicalUnitName, NULLIF(LTRIM(RTRIM(legacy.UNIDAD)), ''))
    LEFT JOIN logistica.LegacyUnitMapping purchaseMap
      ON purchaseMap.LegacyValue = LTRIM(RTRIM(legacy.UNIDAD_COMPRA))
    LEFT JOIN logistica.UnitOfMeasure purchaseUnit
      ON purchaseUnit.UnitName = COALESCE(purchaseMap.CanonicalUnitName, NULLIF(LTRIM(RTRIM(legacy.UNIDAD_COMPRA)), ''))
    LEFT JOIN logistica.UnitOfMeasure fallbackUnit
      ON fallbackUnit.UnitName = 'PIEZA'
    LEFT JOIN logistica.LegacyMaterialCategoryMapping mapCategory
      ON mapCategory.LegacyValue = LTRIM(RTRIM(legacy.CATEGORIA))
    LEFT JOIN logistica.MaterialCategory categoryTarget
      ON categoryTarget.CategoryName = COALESCE(mapCategory.CanonicalCategoryName, NULLIF(LTRIM(RTRIM(legacy.CATEGORIA)), ''))
    LEFT JOIN dbo.BusinessPartner bp
      ON bp.LegacyProveedorId = legacy.PROVEEDOR_ID
) AS src
ON target.LegacyMaterialId = src.LegacyMaterialId
WHEN MATCHED THEN
    UPDATE SET
        MaterialCode = src.MaterialCode,
        [Description] = src.[Description],
        BaseUnitId = src.BaseUnitId,
        PurchaseQuantity = src.PurchaseQuantity,
        PurchaseUnitId = src.PurchaseUnitId,
        BusinessPartnerId = src.BusinessPartnerId,
        Price = src.Price,
        CreatedDate = ISNULL(src.CreatedDate, target.CreatedDate),
        UpdatedDate = ISNULL(src.UpdatedDate, target.UpdatedDate),
        Brand = src.Brand,
        Model = src.Model,
        IsPerishable = src.IsPerishable,
        ShelfLifeDays = src.ShelfLifeDays,
        RequiresRefrigeration = src.RequiresRefrigeration,
        MaterialStatus = src.MaterialStatus,
        CategoryId = src.CategoryId,
        Barcode = src.Barcode,
        VendorCode = src.VendorCode,
        PrimaryImage = src.PrimaryImage,
        PrimaryImageFileName = src.PrimaryImageFileName,
        PrimaryImageContentType = src.PrimaryImageContentType,
        PurchaseLink = src.PurchaseLink,
        MaterialClass = src.MaterialClass,
        IsActive = 1
WHEN NOT MATCHED THEN
    INSERT
    (
        Id, MaterialCode, LegacyMaterialId, [Description], BaseUnitId, PurchaseQuantity, PurchaseUnitId,
        BusinessPartnerId, Price, CreatedDate, UpdatedDate, Brand, Model, IsPerishable, ShelfLifeDays,
        RequiresRefrigeration, MaterialStatus, CategoryId, Barcode, VendorCode, PrimaryImage,
        PrimaryImageFileName, PrimaryImageContentType, PurchaseLink, MaterialClass, IsActive
    )
    VALUES
    (
        src.MaterialId, src.MaterialCode, src.LegacyMaterialId, src.[Description], src.BaseUnitId, src.PurchaseQuantity, src.PurchaseUnitId,
        src.BusinessPartnerId, src.Price, ISNULL(src.CreatedDate, CONVERT(date, SYSUTCDATETIME())), ISNULL(src.UpdatedDate, CONVERT(date, SYSUTCDATETIME())),
        src.Brand, src.Model, src.IsPerishable, src.ShelfLifeDays, src.RequiresRefrigeration, src.MaterialStatus, src.CategoryId,
        src.Barcode, src.VendorCode, src.PrimaryImage, src.PrimaryImageFileName, src.PrimaryImageContentType, src.PurchaseLink, src.MaterialClass, 1
    );
GO

SET IDENTITY_INSERT logistica.Material OFF;
GO

UPDATE logistica.Material
SET UpdatedDate = CONVERT(date, SYSUTCDATETIME())
WHERE UpdatedDate IS NULL;
GO

MERGE logistica.Location AS target
USING (
    SELECT DISTINCT
        spaces.PREDIO_ID AS LegacyRoomId,
        spaces.PREDIO_ID AS RoomId,
        CONCAT('ROOM-', RIGHT(REPLICATE('0', 4) + CAST(spaces.PREDIO_ID AS varchar(20)), 4)) AS LocationCode,
        room.ROOM_NAME AS LocationName,
        'Room' AS LocationType,
        NULLIF(room.ROOM_DESCRIPTION, '') AS [Description]
    FROM logistica.ESPACIOS_DE_ALMACEN spaces
    JOIN dbo.ROOM room
      ON room.ID = spaces.PREDIO_ID
) AS src
ON target.LegacyRoomId = src.LegacyRoomId
AND target.ParentLocationId IS NULL
WHEN MATCHED THEN
    UPDATE SET
        RoomId = src.RoomId,
        LocationCode = src.LocationCode,
        LocationName = src.LocationName,
        LocationType = src.LocationType,
        [Description] = src.[Description],
        IsInventoryEnabled = 1,
        IsActive = 1,
        UpdatedAt = SYSUTCDATETIME()
WHEN NOT MATCHED THEN
    INSERT (LocationCode, LegacyRoomId, RoomId, LocationName, LocationType, [Description], IsInventoryEnabled, IsActive)
    VALUES (src.LocationCode, src.LegacyRoomId, src.RoomId, src.LocationName, src.LocationType, src.[Description], 1, 1);
GO

MERGE logistica.Location AS target
USING (
    SELECT
        spaces.id AS LegacyEspacioId,
        spaces.PREDIO_ID AS LegacyRoomId,
        parent.Id AS ParentLocationId,
        spaces.PREDIO_ID AS RoomId,
        CONCAT('LOC-', RIGHT(REPLICATE('0', 6) + CAST(spaces.id AS varchar(20)), 6)) AS LocationCode,
        spaces.ESPACIO AS LocationName,
        CASE
            WHEN UPPER(LTRIM(RTRIM(spaces.TIPO))) = 'DESECHAR' THEN 'Disposal'
            ELSE 'Storage'
        END AS LocationType,
        NULLIF(LTRIM(RTRIM(spaces.DESCRIPCION)), '') AS [Description]
    FROM logistica.ESPACIOS_DE_ALMACEN spaces
    JOIN logistica.Location parent
      ON parent.LegacyRoomId = spaces.PREDIO_ID
     AND parent.ParentLocationId IS NULL
) AS src
ON target.LegacyEspacioId = src.LegacyEspacioId
WHEN MATCHED THEN
    UPDATE SET
        ParentLocationId = src.ParentLocationId,
        LegacyRoomId = src.LegacyRoomId,
        RoomId = src.RoomId,
        LocationCode = src.LocationCode,
        LocationName = src.LocationName,
        LocationType = src.LocationType,
        [Description] = src.[Description],
        IsInventoryEnabled = 1,
        IsActive = 1,
        UpdatedAt = SYSUTCDATETIME()
WHEN NOT MATCHED THEN
    INSERT (LocationCode, LegacyEspacioId, LegacyRoomId, ParentLocationId, RoomId, LocationName, LocationType, [Description], IsInventoryEnabled, IsActive)
    VALUES (src.LocationCode, src.LegacyEspacioId, src.LegacyRoomId, src.ParentLocationId, src.RoomId, src.LocationName, src.LocationType, src.[Description], 1, 1);
GO

INSERT INTO logistica.MigrationIssue (IssueType, SourceTable, LegacyKey, IssueDescription)
SELECT 'DuplicateLegacyInventory', 'logistica.INVENTARIO',
       CONCAT(CAST(i.LOCACION_ID AS varchar(20)), '-', CAST(i.MATERIAL_ID AS varchar(20))),
       CONCAT('Existe más de un registro para ubicación ', i.LOCACION_ID, ' y material ', i.MATERIAL_ID, '. Se conserva el más reciente por FECHA_CONTEO.')
FROM logistica.INVENTARIO i
GROUP BY i.LOCACION_ID, i.MATERIAL_ID
HAVING COUNT(*) > 1;
GO

;WITH RankedInventory AS (
    SELECT
        i.ID AS LegacyInventoryId,
        i.LOCACION_ID,
        i.MATERIAL_ID,
        i.EXISTENCIA,
        i.FECHA_CONTEO,
        i.MAXIMO,
        i.MINIMO,
        i.FRECUENCIA_CONTEO,
        i.FECHA_COMPRA,
        i.NOTAS,
        ROW_NUMBER() OVER (
            PARTITION BY i.LOCACION_ID, i.MATERIAL_ID
            ORDER BY i.FECHA_CONTEO DESC, i.ID DESC
        ) AS RowNumber
    FROM logistica.INVENTARIO i
)
MERGE logistica.StockBalance AS target
USING (
    SELECT
        ri.LegacyInventoryId,
        locationTarget.Id AS LocationId,
        materialTarget.Id AS MaterialId,
        CAST(ISNULL(ri.EXISTENCIA, 0) AS decimal(18,4)) AS Quantity,
        ri.FECHA_CONTEO AS LastCountedAt,
        CAST(ri.MAXIMO AS decimal(18,4)) AS MaxQuantity,
        CAST(ri.MINIMO AS decimal(18,4)) AS MinQuantity,
        ri.FRECUENCIA_CONTEO AS CountFrequencyDays,
        CASE WHEN CAST(ri.FECHA_COMPRA AS date) = '19000101' THEN NULL ELSE CAST(ri.FECHA_COMPRA AS date) END AS LastPurchaseDate,
        NULLIF(CAST(ri.NOTAS AS varchar(max)), '') AS Notes
    FROM RankedInventory ri
    JOIN logistica.Location locationTarget
      ON locationTarget.LegacyEspacioId = ri.LOCACION_ID
    JOIN logistica.Material materialTarget
      ON materialTarget.LegacyMaterialId = ri.MATERIAL_ID
    WHERE ri.RowNumber = 1
) AS src
ON target.LocationId = src.LocationId AND target.MaterialId = src.MaterialId
WHEN MATCHED THEN
    UPDATE SET
        Quantity = src.Quantity,
        LastCountedAt = src.LastCountedAt,
        MaxQuantity = src.MaxQuantity,
        MinQuantity = src.MinQuantity,
        CountFrequencyDays = src.CountFrequencyDays,
        LastPurchaseDate = src.LastPurchaseDate,
        Notes = src.Notes,
        IsRemoved = 0,
        RemovedAt = NULL,
        RemovedBy = NULL,
        UpdatedAt = SYSUTCDATETIME()
WHEN NOT MATCHED THEN
    INSERT (LocationId, MaterialId, Quantity, LastCountedAt, MaxQuantity, MinQuantity, CountFrequencyDays, LastPurchaseDate, Notes)
    VALUES (src.LocationId, src.MaterialId, src.Quantity, src.LastCountedAt, src.MaxQuantity, src.MinQuantity, src.CountFrequencyDays, src.LastPurchaseDate, src.Notes);
GO

MERGE logistica.StockTransaction AS target
USING (
    SELECT
        balance.Id AS StockBalanceId,
        balance.LocationId,
        balance.MaterialId,
        CAST(balance.Quantity AS decimal(18,4)) AS Quantity,
        inventoryWinner.LegacyInventoryId
    FROM logistica.StockBalance balance
    JOIN (
        SELECT
            ri.LegacyInventoryId,
            locationTarget.Id AS LocationId,
            materialTarget.Id AS MaterialId
        FROM (
            SELECT
                i.ID AS LegacyInventoryId,
                i.LOCACION_ID,
                i.MATERIAL_ID,
                ROW_NUMBER() OVER (
                    PARTITION BY i.LOCACION_ID, i.MATERIAL_ID
                    ORDER BY i.FECHA_CONTEO DESC, i.ID DESC
                ) AS RowNumber
            FROM logistica.INVENTARIO i
        ) ri
        JOIN logistica.Location locationTarget
          ON locationTarget.LegacyEspacioId = ri.LOCACION_ID
        JOIN logistica.Material materialTarget
          ON materialTarget.LegacyMaterialId = ri.MATERIAL_ID
        WHERE ri.RowNumber = 1
    ) inventoryWinner
      ON inventoryWinner.LocationId = balance.LocationId
     AND inventoryWinner.MaterialId = balance.MaterialId
) AS src
ON target.ReferenceType = 'LegacyInventory'
AND target.ReferenceId = src.LegacyInventoryId
WHEN NOT MATCHED THEN
    INSERT
    (
        StockBalanceId, LocationId, MaterialId, TransactionType, QuantityDelta, QuantityAfter,
        ReferenceType, ReferenceId, Notes, PerformedBy, OccurredAt
    )
    VALUES
    (
        src.StockBalanceId, src.LocationId, src.MaterialId, 'OpeningBalance', src.Quantity, src.Quantity,
        'LegacyInventory', src.LegacyInventoryId, 'Carga inicial desde sistema heredado.', 'LegacyMigration', SYSUTCDATETIME()
    );
GO

MERGE logistica.LocationMaterialAttachment AS target
USING (
    SELECT
        attachment.ID AS LegacyInventoryAttachmentId,
        attachment.InventarioID AS LegacyInventoryId,
        locationTarget.Id AS LocationId,
        materialTarget.Id AS MaterialId,
        attachment.AttachmentName AS FileName,
        attachment.AttachmentExtension AS FileExtension,
        CASE
            WHEN LOWER(attachment.AttachmentExtension) = 'jpg' THEN 'image/jpeg'
            WHEN LOWER(attachment.AttachmentExtension) = 'jpeg' THEN 'image/jpeg'
            WHEN LOWER(attachment.AttachmentExtension) = 'png' THEN 'image/png'
            WHEN LOWER(attachment.AttachmentExtension) = 'pdf' THEN 'application/pdf'
            ELSE 'application/octet-stream'
        END AS ContentType,
        NULLIF(LTRIM(RTRIM(attachment.AttachmentDescription)), '') AS [Description],
        attachment.Attachment
    FROM logistica.INVENTARIO_ATTACHMENT attachment
    JOIN logistica.INVENTARIO inventoryLegacy
      ON inventoryLegacy.ID = attachment.InventarioID
    JOIN logistica.Location locationTarget
      ON locationTarget.LegacyEspacioId = inventoryLegacy.LOCACION_ID
    JOIN logistica.Material materialTarget
      ON materialTarget.LegacyMaterialId = inventoryLegacy.MATERIAL_ID
) AS src
ON target.LegacyInventoryAttachmentId = src.LegacyInventoryAttachmentId
WHEN MATCHED THEN
    UPDATE SET
        LocationId = src.LocationId,
        MaterialId = src.MaterialId,
        LegacyInventoryId = src.LegacyInventoryId,
        FileName = src.FileName,
        FileExtension = src.FileExtension,
        ContentType = src.ContentType,
        [Description] = src.[Description],
        Attachment = src.Attachment,
        IsDeleted = 0,
        DeletedAt = NULL,
        DeletedBy = NULL
WHEN NOT MATCHED THEN
    INSERT
    (
        LocationId, MaterialId, LegacyInventoryAttachmentId, LegacyInventoryId,
        FileName, FileExtension, ContentType, [Description], Attachment, CreatedBy
    )
    VALUES
    (
        src.LocationId, src.MaterialId, src.LegacyInventoryAttachmentId, src.LegacyInventoryId,
        src.FileName, src.FileExtension, src.ContentType, src.[Description], src.Attachment, 'LegacyMigration'
    );
GO
