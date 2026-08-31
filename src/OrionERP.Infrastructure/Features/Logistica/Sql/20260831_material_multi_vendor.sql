/*
  Varios proveedores por material.

  Hasta hoy `logistica.Material.BusinessPartnerId` era una sola columna, y tres consultas la
  usaban como filtro duro: el catálogo de materiales, los candidatos de la orden automática y la
  resolución de renglones al capturar una orden de compra. Cuando el proveedor habitual no tiene
  el producto y hay que comprarlo con otro, el material simplemente no aparece: no había forma de
  registrar la compra sin reasignarle el proveedor al material y perder el dato del habitual.

  Este script normaliza la relación: `logistica.MaterialVendor` guarda un renglón por
  material-proveedor, con los datos comerciales que son propios de cada proveedor —su SKU, su
  presentación, su liga y el último costo que se le pagó— y una marca de proveedor principal.

  Lo que se queda en `logistica.Material` y por qué:
    BaseUnitPrice, PurchaseQuantity, PurchaseUnitId  alimentan el costeo de recetas y las
                                                     conversiones de unidad de toda la aplicación
    VendorCode, PurchaseLink                         VendorCode es criterio de búsqueda y se
                                                     congela en los renglones de orden y en el PDF
  Todas ellas reflejan al proveedor principal y la aplicación las mantiene sincronizadas al
  guardar. La única columna que desaparece es `BusinessPartnerId`.

  Idempotente. No borra datos: el respaldo del vínculo original queda en
  `logistica.MaterialVendorBackfill` y la reversa está al pie de este archivo.
*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET XACT_ABORT ON;
SET NOCOUNT ON;

BEGIN TRANSACTION;

/* ------------------------------------------------------------------
   1. Tabla del vínculo material-proveedor
   ------------------------------------------------------------------ */
IF OBJECT_ID('logistica.MaterialVendor', 'U') IS NULL
BEGIN
  CREATE TABLE logistica.MaterialVendor
  (
    Id                int IDENTITY(1,1) NOT NULL CONSTRAINT PK_MaterialVendor PRIMARY KEY,
    Rfc               varchar(50)   NOT NULL,
    MaterialId        int           NOT NULL,
    BusinessPartnerId int           NOT NULL,
    IsPrimary         bit           NOT NULL CONSTRAINT DF_MaterialVendor_IsPrimary DEFAULT (0),
    IsActive          bit           NOT NULL CONSTRAINT DF_MaterialVendor_IsActive DEFAULT (1),
    VendorCode        varchar(100)  NULL,
    PurchaseQuantity  decimal(18,4) NULL,
    PurchaseUnitId    int           NULL,
    PurchaseLink      varchar(max)  NULL,
    LastUnitPrice     decimal(18,6) NULL,
    LastPurchaseDate  date          NULL,
    Notes             varchar(500)  NULL,
    CreatedAt         datetime2(0)  NOT NULL CONSTRAINT DF_MaterialVendor_CreatedAt DEFAULT (SYSUTCDATETIME()),
    UpdatedAt         datetime2(0)  NOT NULL CONSTRAINT DF_MaterialVendor_UpdatedAt DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT FK_MaterialVendor_Material
      FOREIGN KEY (Rfc, MaterialId) REFERENCES logistica.Material (Rfc, Id),
    CONSTRAINT FK_MaterialVendor_PartnerScope
      FOREIGN KEY (Rfc, BusinessPartnerId) REFERENCES dbo.BusinessPartnerRfcScope (Rfc, BusinessPartnerId),
    CONSTRAINT FK_MaterialVendor_PurchaseUnit
      FOREIGN KEY (PurchaseUnitId) REFERENCES logistica.UnitOfMeasure (Id)
  );
END;
GO

-- Un proveedor no puede aparecer dos veces en el mismo material.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = 'UX_MaterialVendor_Material_Partner' AND object_id = OBJECT_ID('logistica.MaterialVendor'))
  CREATE UNIQUE INDEX UX_MaterialVendor_Material_Partner
    ON logistica.MaterialVendor (Rfc, MaterialId, BusinessPartnerId);
GO

-- A lo sumo un proveedor principal por material; la aplicación garantiza que sea exactamente uno.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = 'UX_MaterialVendor_Primary' AND object_id = OBJECT_ID('logistica.MaterialVendor'))
  CREATE UNIQUE INDEX UX_MaterialVendor_Primary
    ON logistica.MaterialVendor (Rfc, MaterialId)
    WHERE IsPrimary = 1;
GO

-- Camino de acceso de Compras: "qué materiales le compro a este proveedor".
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = 'IX_MaterialVendor_Partner' AND object_id = OBJECT_ID('logistica.MaterialVendor'))
  CREATE INDEX IX_MaterialVendor_Partner
    ON logistica.MaterialVendor (Rfc, BusinessPartnerId)
    INCLUDE (MaterialId, IsPrimary, IsActive);
GO

/* ------------------------------------------------------------------
   2. Respaldo del vínculo original, para poder revertir
   ------------------------------------------------------------------ */
IF OBJECT_ID('logistica.MaterialVendorBackfill', 'U') IS NULL
BEGIN
  CREATE TABLE logistica.MaterialVendorBackfill
  (
    Id                   int IDENTITY(1,1) NOT NULL CONSTRAINT PK_MaterialVendorBackfill PRIMARY KEY,
    Rfc                  varchar(50)  NOT NULL,
    MaterialId           int          NOT NULL,
    [Description]        varchar(800) NULL,
    OldBusinessPartnerId int          NULL,
    AppliedAtUtc         datetime2(0) NOT NULL CONSTRAINT DF_MaterialVendorBackfill_AppliedAtUtc DEFAULT (SYSUTCDATETIME())
  );
END;
GO

/* ------------------------------------------------------------------
   3. Backfill: el proveedor actual pasa a ser el proveedor principal
   ------------------------------------------------------------------ */
IF COL_LENGTH('logistica.Material', 'BusinessPartnerId') IS NOT NULL
BEGIN
  DECLARE @BackfillSql nvarchar(max);

  -- El SQL va en cadena porque la columna desaparece más abajo y el compilador de lotes
  -- rechazaría el script completo una vez aplicado.
  SET @BackfillSql = N'
    INSERT INTO logistica.MaterialVendorBackfill (Rfc, MaterialId, [Description], OldBusinessPartnerId)
    SELECT m.Rfc, m.Id, m.[Description], m.BusinessPartnerId
    FROM logistica.Material m
    WHERE m.BusinessPartnerId IS NOT NULL
      AND NOT EXISTS (SELECT 1 FROM logistica.MaterialVendorBackfill b
                      WHERE b.Rfc = m.Rfc AND b.MaterialId = m.Id);

    INSERT INTO logistica.MaterialVendor
      (Rfc, MaterialId, BusinessPartnerId, IsPrimary, IsActive,
       VendorCode, PurchaseQuantity, PurchaseUnitId, PurchaseLink, LastUnitPrice)
    SELECT
        m.Rfc,
        m.Id,
        m.BusinessPartnerId,
        1,
        1,
        m.VendorCode,
        NULLIF(m.PurchaseQuantity, 0),
        m.PurchaseUnitId,
        m.PurchaseLink,
        m.BaseUnitPrice
    FROM logistica.Material m
    WHERE m.BusinessPartnerId IS NOT NULL
      AND EXISTS (SELECT 1 FROM dbo.BusinessPartnerRfcScope scope
                  WHERE scope.Rfc = m.Rfc AND scope.BusinessPartnerId = m.BusinessPartnerId)
      AND NOT EXISTS (SELECT 1 FROM logistica.MaterialVendor mv
                      WHERE mv.Rfc = m.Rfc AND mv.MaterialId = m.Id);

    -- Un material cuyo proveedor quedó fuera del alcance de su RFC no puede vincularse: se
    -- reporta para que no pase inadvertido en vez de perderse en silencio.
    IF EXISTS (SELECT 1 FROM logistica.Material m
               WHERE m.BusinessPartnerId IS NOT NULL
                 AND NOT EXISTS (SELECT 1 FROM dbo.BusinessPartnerRfcScope scope
                                 WHERE scope.Rfc = m.Rfc AND scope.BusinessPartnerId = m.BusinessPartnerId))
      THROW 51501, ''Hay materiales cuyo proveedor no tiene alcance en el RFC del material. Corrige dbo.BusinessPartnerRfcScope antes de migrar.'', 1;';

  EXEC sys.sp_executesql @BackfillSql;
END;
GO

/* ------------------------------------------------------------------
   4. Incorporar la tabla nueva a la política RLS compartida
   ------------------------------------------------------------------ */
IF EXISTS
(
  SELECT 1 FROM sys.security_policies
  WHERE [name] = 'RfcSecurityPolicy' AND schema_id = SCHEMA_ID('logistica')
)
AND NOT EXISTS
(
  SELECT 1 FROM sys.security_predicates predicateInfo
  WHERE predicateInfo.object_id = OBJECT_ID('logistica.RfcSecurityPolicy')
    AND predicateInfo.target_object_id = OBJECT_ID('logistica.MaterialVendor')
)
BEGIN
  -- En SQL dinámico porque SQL Server valida ALTER SECURITY POLICY al compilar el lote, aunque el
  -- IF sea falso: escrito directo, la segunda corrida fallaría con el predicado ya definido.
  DECLARE @RlsSql nvarchar(max) = N'
    ALTER SECURITY POLICY logistica.RfcSecurityPolicy
      ADD FILTER PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.MaterialVendor,
      ADD BLOCK PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.MaterialVendor AFTER INSERT,
      ADD BLOCK PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.MaterialVendor AFTER UPDATE;';
  EXEC sys.sp_executesql @RlsSql;
END;
GO

/* ------------------------------------------------------------------
   5. Verificar antes de soltar la columna
   ------------------------------------------------------------------ */
IF COL_LENGTH('logistica.Material', 'BusinessPartnerId') IS NOT NULL
BEGIN
  DECLARE @CheckSql nvarchar(max);
  SET @CheckSql = N'
    DECLARE @ConProveedor int = (SELECT COUNT(*) FROM logistica.Material WHERE BusinessPartnerId IS NOT NULL);
    DECLARE @Vinculados   int = (SELECT COUNT(DISTINCT MaterialId) FROM logistica.MaterialVendor);
    IF @ConProveedor <> @Vinculados
      THROW 51502, ''El respaldo de proveedores no coincide con los materiales que tenían proveedor. No se elimina la columna.'', 1;';
  EXEC sys.sp_executesql @CheckSql;
END;
GO

/* ------------------------------------------------------------------
   6. Eliminar la columna y sus llaves foráneas
   ------------------------------------------------------------------ */
IF COL_LENGTH('logistica.Material', 'BusinessPartnerId') IS NOT NULL
BEGIN
  IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE [name] = 'FK_Material_BusinessPartner_Rfc' AND parent_object_id = OBJECT_ID('logistica.Material'))
    ALTER TABLE logistica.Material DROP CONSTRAINT FK_Material_BusinessPartner_Rfc;

  IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE [name] = 'FK_Material_BusinessPartner' AND parent_object_id = OBJECT_ID('logistica.Material'))
    ALTER TABLE logistica.Material DROP CONSTRAINT FK_Material_BusinessPartner;

  DECLARE @IndexName sysname;
  DECLARE @DropIndexSql nvarchar(max);
  DECLARE IndexCursor CURSOR LOCAL FAST_FORWARD FOR
    SELECT DISTINCT i.[name]
    FROM sys.indexes i
    JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
    JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
    WHERE i.object_id = OBJECT_ID('logistica.Material')
      AND c.[name] = 'BusinessPartnerId'
      AND i.is_primary_key = 0;

  OPEN IndexCursor;
  FETCH NEXT FROM IndexCursor INTO @IndexName;
  WHILE @@FETCH_STATUS = 0
  BEGIN
    SET @DropIndexSql = N'DROP INDEX ' + QUOTENAME(@IndexName) + N' ON logistica.Material;';
    EXEC sys.sp_executesql @DropIndexSql;
    FETCH NEXT FROM IndexCursor INTO @IndexName;
  END;
  CLOSE IndexCursor;
  DEALLOCATE IndexCursor;

  ALTER TABLE logistica.Material DROP COLUMN BusinessPartnerId;
END;
GO

/* ------------------------------------------------------------------
   Validaciones finales
   ------------------------------------------------------------------ */
IF OBJECT_ID('logistica.MaterialVendor', 'U') IS NULL
  THROW 51503, 'No se creó logistica.MaterialVendor.', 1;

IF COL_LENGTH('logistica.Material', 'BusinessPartnerId') IS NOT NULL
  THROW 51504, 'logistica.Material.BusinessPartnerId sigue existiendo.', 1;

COMMIT TRANSACTION;
GO

/* ------------------------------------------------------------------
   Reversa

   ALTER TABLE logistica.Material ADD BusinessPartnerId int NULL;
   GO
   UPDATE m SET m.BusinessPartnerId = b.OldBusinessPartnerId
   FROM logistica.Material m
   JOIN logistica.MaterialVendorBackfill b ON b.Rfc = m.Rfc AND b.MaterialId = m.Id;
   ALTER TABLE logistica.Material ADD CONSTRAINT FK_Material_BusinessPartner
     FOREIGN KEY (BusinessPartnerId) REFERENCES dbo.BusinessPartner (Id);
   ALTER TABLE logistica.Material ADD CONSTRAINT FK_Material_BusinessPartner_Rfc
     FOREIGN KEY (Rfc, BusinessPartnerId) REFERENCES dbo.BusinessPartnerRfcScope (Rfc, BusinessPartnerId);
   ALTER SECURITY POLICY logistica.RfcSecurityPolicy
     DROP FILTER PREDICATE ON logistica.MaterialVendor,
     DROP BLOCK PREDICATE ON logistica.MaterialVendor AFTER INSERT,
     DROP BLOCK PREDICATE ON logistica.MaterialVendor AFTER UPDATE;
   DROP TABLE logistica.MaterialVendor;
   ------------------------------------------------------------------ */
