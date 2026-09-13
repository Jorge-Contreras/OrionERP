/*
  Vínculo entre órdenes de compra (logistica.PurchaseOrder) y pólizas (dbo.Transacciones).

  Muchos a muchos con monto: una compra puede quedar cubierta por varias pólizas (facturas
  parciales) y una póliza puede cubrir varias compras (una factura consolidada del proveedor).
  Que la suma por compra no rebase lo recibido, ni la suma por póliza su monto, depende de otras
  tablas; lo hace cumplir PurchaseAccountingService bajo UPDLOCK/HOLDLOCK, no un CHECK.

  Origin distingue la póliza que Compras generó (Generated) de la que alguien ligó (Manual).

  La llave a dbo.Transacciones no lleva cascada: una póliza ligada a una compra no se borra.

  La tabla entra al aislamiento fail-closed por RFC y a orion.TenantTableClassification; sin la
  clasificación, database/validation/validate-rfc-tenant-isolation.sql rechaza el esquema.

  Idempotente. La reversa está al pie de este archivo.
*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET XACT_ABORT ON;
SET NOCOUNT ON;

BEGIN TRANSACTION;

/* ------------------------------------------------------------------
   1. La tabla
   ------------------------------------------------------------------ */
IF OBJECT_ID('logistica.PurchaseAccountingLink', 'U') IS NULL
  CREATE TABLE logistica.PurchaseAccountingLink
  (
    Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_PurchaseAccountingLink PRIMARY KEY,
    Rfc varchar(50) NOT NULL CONSTRAINT DF_PurchaseAccountingLink_Rfc DEFAULT (CONVERT(varchar(50), SESSION_CONTEXT(N'OrionRfc'))),
    PurchaseOrderId int NOT NULL,
    TransaccionId int NOT NULL,
    MontoAsignado decimal(18,2) NOT NULL,
    Origin varchar(20) NOT NULL CONSTRAINT DF_PurchaseAccountingLink_Origin DEFAULT ('Manual'),
    CreatedAt datetime2(0) NOT NULL CONSTRAINT DF_PurchaseAccountingLink_CreatedAt DEFAULT (SYSUTCDATETIME()),
    CreatedBy varchar(256) NULL,
    CONSTRAINT FK_PurchaseAccountingLink_Order_Rfc
      FOREIGN KEY (Rfc, PurchaseOrderId) REFERENCES logistica.PurchaseOrder (Rfc, Id),
    CONSTRAINT FK_PurchaseAccountingLink_Transaccion
      FOREIGN KEY (TransaccionId) REFERENCES dbo.Transacciones (ID),
    CONSTRAINT CK_PurchaseAccountingLink_Monto CHECK (MontoAsignado > 0),
    CONSTRAINT CK_PurchaseAccountingLink_Origin CHECK (Origin IN ('Generated', 'Manual'))
  );
GO

/* ------------------------------------------------------------------
   2. Índices. Cubren las sumas por compra y por póliza que se toman bajo candado.
   ------------------------------------------------------------------ */
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = 'UX_PurchaseAccountingLink_OrderTransaccion' AND object_id = OBJECT_ID('logistica.PurchaseAccountingLink'))
  CREATE UNIQUE INDEX UX_PurchaseAccountingLink_OrderTransaccion
    ON logistica.PurchaseAccountingLink (PurchaseOrderId, TransaccionId)
    INCLUDE (Rfc, MontoAsignado, Origin);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = 'IX_PurchaseAccountingLink_Transaccion' AND object_id = OBJECT_ID('logistica.PurchaseAccountingLink'))
  CREATE INDEX IX_PurchaseAccountingLink_Transaccion
    ON logistica.PurchaseAccountingLink (TransaccionId, PurchaseOrderId)
    INCLUDE (Rfc, MontoAsignado);
GO

/* ------------------------------------------------------------------
   3. Aislamiento por RFC, igual que el resto de Logística
   ------------------------------------------------------------------ */
IF NOT EXISTS
(
  SELECT 1 FROM sys.security_predicates
  WHERE target_object_id = OBJECT_ID('logistica.PurchaseAccountingLink')
    AND predicate_type = 0
)
  ALTER SECURITY POLICY logistica.RfcSecurityPolicy
    ADD FILTER PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.PurchaseAccountingLink,
    ADD BLOCK PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.PurchaseAccountingLink AFTER INSERT,
    ADD BLOCK PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON logistica.PurchaseAccountingLink AFTER UPDATE;
GO

IF OBJECT_ID('orion.TenantTableClassification', 'U') IS NOT NULL
   AND NOT EXISTS
   (
     SELECT 1 FROM orion.TenantTableClassification
     WHERE SchemaName = 'logistica' AND TableName = 'PurchaseAccountingLink'
   )
  INSERT orion.TenantTableClassification (SchemaName, TableName, Classification, OwnerColumn, ReviewedInMigrationId, Notes)
  VALUES ('logistica', 'PurchaseAccountingLink', 'TENANT_OWNED', 'Rfc', '20260912_logistics_purchase_accounting_link',
          N'Vínculo muchos a muchos entre órdenes de compra y pólizas, con monto por vínculo.');
GO

/* ------------------------------------------------------------------
   Validaciones finales
   ------------------------------------------------------------------ */
IF OBJECT_ID('logistica.PurchaseAccountingLink', 'U') IS NULL
  THROW 51600, 'No se creó logistica.PurchaseAccountingLink.', 1;

IF COL_LENGTH('logistica.PurchaseAccountingLink', 'Origin') IS NULL
  THROW 51601, 'logistica.PurchaseAccountingLink no tiene la columna Origin.', 1;

IF NOT EXISTS
(
  SELECT 1 FROM sys.security_predicates
  WHERE target_object_id = OBJECT_ID('logistica.PurchaseAccountingLink')
    AND predicate_type = 0
)
  THROW 51602, 'logistica.PurchaseAccountingLink quedó sin filtro por RFC.', 1;

IF OBJECT_ID('orion.TenantTableClassification', 'U') IS NOT NULL
   AND NOT EXISTS
   (
     SELECT 1 FROM orion.TenantTableClassification
     WHERE SchemaName = 'logistica' AND TableName = 'PurchaseAccountingLink'
   )
  THROW 51603, 'logistica.PurchaseAccountingLink quedó sin clasificación de aislamiento.', 1;

COMMIT TRANSACTION;
GO

/* ------------------------------------------------------------------
   Reversa

   ALTER SECURITY POLICY logistica.RfcSecurityPolicy
     DROP FILTER PREDICATE ON logistica.PurchaseAccountingLink,
     DROP BLOCK PREDICATE ON logistica.PurchaseAccountingLink AFTER INSERT,
     DROP BLOCK PREDICATE ON logistica.PurchaseAccountingLink AFTER UPDATE;
   DELETE FROM orion.TenantTableClassification
   WHERE SchemaName = 'logistica' AND TableName = 'PurchaseAccountingLink';
   DROP TABLE logistica.PurchaseAccountingLink;
   ------------------------------------------------------------------ */
