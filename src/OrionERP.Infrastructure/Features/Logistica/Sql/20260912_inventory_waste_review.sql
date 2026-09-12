/*
  Revisión y reversa de la merma de inventario.

  La merma ya se registraba: es logistica.InventoryAdjustment con AdjustmentType='Waste'. Lo que
  faltaba era poder consultarla y corregirla. Este script agrega las tres columnas que eso exige,
  sin tocar ninguna tabla nueva y sin cambiar la conducta de un solo documento existente.

  1. OccurredOn: el día local en que se tiró el producto.

     CreatedAt es UTC y no sirve para el corte diario de una cocina en México: una merma capturada
     a las 19:00 del día 12 se guarda como 01:00 del día 13. Un reporte "merma de hoy" armado sobre
     CreatedAt le carga al día siguiente todo lo que se tiró después de las 18:00.

     Se deja NULL en los documentos históricos a propósito. Rellenarlo restando seis horas sería
     inventar un dato que nadie capturó; las consultas usan COALESCE(OccurredOn, CONVERT(date,
     CreatedAt)) y así el documento viejo se ordena por lo único que de él se sabe.

  2. ReversalOfAdjustmentId / ReversedByAdjustmentId: el par de enlaces de la corrección.

     Mismo modelo que la reversa de pólizas en AccountingCycle: el documento original nunca se
     borra ni se edita, se compensa con otro que le devuelve la cantidad y el valor exactos, y los
     dos quedan ligados en ambos sentidos. El índice único filtrado impide la segunda reversa a
     nivel de base y no sólo en el servicio.

  3. El índice que sostiene la bandeja de pendientes y el historial.

  No hace falta tocar RLS: InventoryAdjustment e InventoryAdjustmentLine ya tienen predicados
  FILTER y BLOCK en 20260713_zz_logistics_rls.sql, y las columnas nuevas quedan cubiertas por
  ellos. Status sigue sin CHECK, igual que hoy: los valores los valida el servicio, y filas
  anteriores traen 'Draft' o 'Approved'.

  Idempotente. La reversa del script está al pie de este archivo.
*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET XACT_ABORT ON;
SET NOCOUNT ON;

BEGIN TRANSACTION;

/* ------------------------------------------------------------------
   1. El día local de la merma
   ------------------------------------------------------------------ */
IF COL_LENGTH('logistica.InventoryAdjustment', 'OccurredOn') IS NULL
  ALTER TABLE logistica.InventoryAdjustment
    ADD OccurredOn date NULL;
GO

/* ------------------------------------------------------------------
   2. Los enlaces de la reversa
   ------------------------------------------------------------------ */
IF COL_LENGTH('logistica.InventoryAdjustment', 'ReversalOfAdjustmentId') IS NULL
  ALTER TABLE logistica.InventoryAdjustment
    ADD ReversalOfAdjustmentId bigint NULL;
GO

IF COL_LENGTH('logistica.InventoryAdjustment', 'ReversedByAdjustmentId') IS NULL
  ALTER TABLE logistica.InventoryAdjustment
    ADD ReversedByAdjustmentId bigint NULL;
GO

/* Compuestas contra UX_InventoryAdjustment_RfcId: el RFC viaja en toda llave del esquema,
   de modo que una reversa nunca puede apuntar a un documento de otra empresa. */
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE [name] = 'FK_InventoryAdjustment_ReversalOf_Rfc')
  ALTER TABLE logistica.InventoryAdjustment
    ADD CONSTRAINT FK_InventoryAdjustment_ReversalOf_Rfc
      FOREIGN KEY (Rfc, ReversalOfAdjustmentId)
      REFERENCES logistica.InventoryAdjustment (Rfc, Id);
GO

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE [name] = 'FK_InventoryAdjustment_ReversedBy_Rfc')
  ALTER TABLE logistica.InventoryAdjustment
    ADD CONSTRAINT FK_InventoryAdjustment_ReversedBy_Rfc
      FOREIGN KEY (Rfc, ReversedByAdjustmentId)
      REFERENCES logistica.InventoryAdjustment (Rfc, Id);
GO

/* Un documento se reversa una sola vez. */
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = 'UX_InventoryAdjustment_ReversalOf' AND object_id = OBJECT_ID('logistica.InventoryAdjustment'))
  CREATE UNIQUE INDEX UX_InventoryAdjustment_ReversalOf
    ON logistica.InventoryAdjustment (Rfc, ReversalOfAdjustmentId)
    WHERE ReversalOfAdjustmentId IS NOT NULL;
GO

/* ------------------------------------------------------------------
   3. Bandeja de pendientes e historial
   ------------------------------------------------------------------ */
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = 'IX_InventoryAdjustment_WasteReview' AND object_id = OBJECT_ID('logistica.InventoryAdjustment'))
  CREATE INDEX IX_InventoryAdjustment_WasteReview
    ON logistica.InventoryAdjustment (Rfc, AdjustmentType, [Status])
    INCLUDE (OccurredOn, CreatedAt, ReasonCode, ReversedByAdjustmentId, ReversalOfAdjustmentId);
GO

/* ------------------------------------------------------------------
   Validaciones finales
   ------------------------------------------------------------------ */
IF COL_LENGTH('logistica.InventoryAdjustment', 'OccurredOn') IS NULL
  THROW 51523, 'No se creó logistica.InventoryAdjustment.OccurredOn.', 1;

IF COL_LENGTH('logistica.InventoryAdjustment', 'ReversalOfAdjustmentId') IS NULL
  THROW 51524, 'No se creó logistica.InventoryAdjustment.ReversalOfAdjustmentId.', 1;

IF COL_LENGTH('logistica.InventoryAdjustment', 'ReversedByAdjustmentId') IS NULL
  THROW 51525, 'No se creó logistica.InventoryAdjustment.ReversedByAdjustmentId.', 1;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = 'UX_InventoryAdjustment_ReversalOf' AND object_id = OBJECT_ID('logistica.InventoryAdjustment'))
  THROW 51526, 'No se creó el índice que impide la segunda reversa.', 1;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = 'IX_InventoryAdjustment_WasteReview' AND object_id = OBJECT_ID('logistica.InventoryAdjustment'))
  THROW 51527, 'No se creó el índice de revisión de merma.', 1;

COMMIT TRANSACTION;
GO

/* ------------------------------------------------------------------
   Reversa

   DROP INDEX IX_InventoryAdjustment_WasteReview ON logistica.InventoryAdjustment;
   DROP INDEX UX_InventoryAdjustment_ReversalOf ON logistica.InventoryAdjustment;
   ALTER TABLE logistica.InventoryAdjustment DROP CONSTRAINT FK_InventoryAdjustment_ReversedBy_Rfc;
   ALTER TABLE logistica.InventoryAdjustment DROP CONSTRAINT FK_InventoryAdjustment_ReversalOf_Rfc;
   ALTER TABLE logistica.InventoryAdjustment DROP COLUMN ReversedByAdjustmentId;
   ALTER TABLE logistica.InventoryAdjustment DROP COLUMN ReversalOfAdjustmentId;
   ALTER TABLE logistica.InventoryAdjustment DROP COLUMN OccurredOn;
   ------------------------------------------------------------------ */
