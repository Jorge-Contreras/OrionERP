/*
  Escalón de compra configurable por material y por proveedor.

  Hasta hoy la regla "solo se puede comprar en presentaciones completas" era implícita: Compras la
  activaba con el solo hecho de que el material tuviera nombre de presentación de compra. Eso es
  correcto para el papel higiénico —el proveedor solo vende el paquete de 24 rollos cerrado— y es
  falso para el pollo, que se controla en gramos, se vende por kilo y el proveedor sí despacha
  1.5 kg. Nada en los datos separaba los dos casos: `PurchaseQuantity > 1` y `PurchaseUnitId IS NOT
  NULL` son igual de ciertos para ambos.

  `PurchaseIncrement` es el escalón mínimo de compra, expresado en unidades de compra:

      1  -> solo presentaciones completas   (el default; conserva la conducta de hoy)
      0  -> fraccionable libre              (1.5 kg, 0.75 kg)
     >0  -> escalón intermedio              (0.5 = medios kilos)

  Se guarda decimal y no bit para que un tercer modo sea después un cambio de pantalla y no de
  esquema. El proveedor puede sobreescribir al material: `logistica.MaterialVendor.PurchaseIncrement`
  en NULL significa "hereda del material", igual que ya ocurre con PurchaseQuantity y PurchaseUnitId.

  `logistica.PurchaseOrderLine.PurchaseIncrementSnapshot` congela la regla al capturar la orden, de
  modo que reabrir una orden vieja no la revalide contra una configuración que cambió después.

  Idempotente. El backfill deja todo en 1 a propósito: no se adivina qué materiales son
  fraccionables, el usuario los marca desde /logistica/materiales. Aplicar este script no cambia la
  conducta de ninguna orden existente. La reversa está al pie de este archivo.
*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET XACT_ABORT ON;
SET NOCOUNT ON;

BEGIN TRANSACTION;

/* ------------------------------------------------------------------
   1. El escalón del material
   ------------------------------------------------------------------ */
IF COL_LENGTH('logistica.Material', 'PurchaseIncrement') IS NULL
  ALTER TABLE logistica.Material
    ADD PurchaseIncrement decimal(18,4) NOT NULL
      CONSTRAINT DF_Material_PurchaseIncrement DEFAULT (1) WITH VALUES;
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE [name] = 'CK_Material_PurchaseIncrement' AND parent_object_id = OBJECT_ID('logistica.Material'))
  ALTER TABLE logistica.Material
    ADD CONSTRAINT CK_Material_PurchaseIncrement CHECK (PurchaseIncrement >= 0);
GO

/* ------------------------------------------------------------------
   2. El override por proveedor. NULL = hereda del material.
   ------------------------------------------------------------------ */
IF COL_LENGTH('logistica.MaterialVendor', 'PurchaseIncrement') IS NULL
  ALTER TABLE logistica.MaterialVendor
    ADD PurchaseIncrement decimal(18,4) NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE [name] = 'CK_MaterialVendor_PurchaseIncrement' AND parent_object_id = OBJECT_ID('logistica.MaterialVendor'))
  ALTER TABLE logistica.MaterialVendor
    ADD CONSTRAINT CK_MaterialVendor_PurchaseIncrement
      CHECK (PurchaseIncrement IS NULL OR PurchaseIncrement >= 0);
GO

/* ------------------------------------------------------------------
   3. El escalón congelado en el renglón de la orden.

   Mismo camino que PurchaseQuantitySnapshot en 20260417_logistics_purchasing_auto_po_v1.sql:
   agregar NULL, rellenar, y sólo entonces exigir NOT NULL con su default.
   ------------------------------------------------------------------ */
IF COL_LENGTH('logistica.PurchaseOrderLine', 'PurchaseIncrementSnapshot') IS NULL
  ALTER TABLE logistica.PurchaseOrderLine
    ADD PurchaseIncrementSnapshot decimal(18,4) NULL;
GO

UPDATE logistica.PurchaseOrderLine
SET PurchaseIncrementSnapshot = 1
WHERE PurchaseIncrementSnapshot IS NULL;
GO

IF EXISTS
(
  SELECT 1 FROM sys.columns
  WHERE object_id = OBJECT_ID('logistica.PurchaseOrderLine')
    AND [name] = 'PurchaseIncrementSnapshot'
    AND is_nullable = 1
)
  ALTER TABLE logistica.PurchaseOrderLine
    ALTER COLUMN PurchaseIncrementSnapshot decimal(18,4) NOT NULL;
GO

IF NOT EXISTS
(
  SELECT 1
  FROM sys.default_constraints dc
  JOIN sys.columns c
    ON c.object_id = dc.parent_object_id
   AND c.column_id = dc.parent_column_id
  WHERE dc.parent_object_id = OBJECT_ID('logistica.PurchaseOrderLine')
    AND c.[name] = 'PurchaseIncrementSnapshot'
)
  ALTER TABLE logistica.PurchaseOrderLine
    ADD CONSTRAINT DF_PurchaseOrderLine_PurchaseIncrementSnapshot
      DEFAULT (1) FOR PurchaseIncrementSnapshot;
GO

/* ------------------------------------------------------------------
   Validaciones finales
   ------------------------------------------------------------------ */
IF COL_LENGTH('logistica.Material', 'PurchaseIncrement') IS NULL
  THROW 51510, 'No se creó logistica.Material.PurchaseIncrement.', 1;

IF COL_LENGTH('logistica.MaterialVendor', 'PurchaseIncrement') IS NULL
  THROW 51511, 'No se creó logistica.MaterialVendor.PurchaseIncrement.', 1;

IF COL_LENGTH('logistica.PurchaseOrderLine', 'PurchaseIncrementSnapshot') IS NULL
  THROW 51512, 'No se creó logistica.PurchaseOrderLine.PurchaseIncrementSnapshot.', 1;

IF EXISTS (SELECT 1 FROM logistica.PurchaseOrderLine WHERE PurchaseIncrementSnapshot IS NULL)
  THROW 51513, 'Quedaron renglones de orden sin escalón de compra.', 1;

COMMIT TRANSACTION;
GO

/* ------------------------------------------------------------------
   Reversa

   ALTER TABLE logistica.PurchaseOrderLine DROP CONSTRAINT DF_PurchaseOrderLine_PurchaseIncrementSnapshot;
   ALTER TABLE logistica.PurchaseOrderLine DROP COLUMN PurchaseIncrementSnapshot;
   ALTER TABLE logistica.MaterialVendor DROP CONSTRAINT CK_MaterialVendor_PurchaseIncrement;
   ALTER TABLE logistica.MaterialVendor DROP COLUMN PurchaseIncrement;
   ALTER TABLE logistica.Material DROP CONSTRAINT CK_Material_PurchaseIncrement;
   ALTER TABLE logistica.Material DROP CONSTRAINT DF_Material_PurchaseIncrement;
   ALTER TABLE logistica.Material DROP COLUMN PurchaseIncrement;
   ------------------------------------------------------------------ */
