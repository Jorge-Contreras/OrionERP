/*
  Datos bancarios de transferencia electrónica de fondos (SPEI) por sede.

  En México es habitual que el comensal pague por transferencia. El punto de
  venta necesita imprimir en la impresora térmica de 80 mm el titular, banco,
  cuenta, CLABE y tarjeta a los cuales depositar. Esos datos se configuran en
  /restaurante/admin › Sedes y viven junto a la sede porque cada sucursal puede
  cobrar en una cuenta distinta.

  Simulación:
    sqlcmd ... -f 65001 -v ExpectedDatabase="Orion_Sandbox" ApplyChanges="0" -i 20260902_restaurant_transfer_payment_details.sql
  Aplicación:
    sqlcmd ... -f 65001 -v ExpectedDatabase="Orion_Sandbox" ApplyChanges="1" -i 20260902_restaurant_transfer_payment_details.sql

  ApplyChanges=0 ejecuta la misma migración y validaciones dentro de una
  transacción que se revierte.
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET QUOTED_IDENTIFIER ON;
SET NUMERIC_ROUNDABORT OFF;

DECLARE @ExpectedDatabase sysname = N'$(ExpectedDatabase)';
DECLARE @ApplyChangesText nvarchar(10) = N'$(ApplyChanges)';
DECLARE @ApplyChanges bit;

IF @ExpectedDatabase NOT IN (N'Orion_Sandbox', N'Orion_SandBox', N'grupocarpio')
  THROW 51820, 'ExpectedDatabase debe ser Orion_Sandbox o grupocarpio.', 1;
IF DB_NAME() <> @ExpectedDatabase
  THROW 51821, 'La base conectada no coincide con ExpectedDatabase.', 1;
IF @ApplyChangesText NOT IN (N'0', N'1')
  THROW 51822, 'ApplyChanges debe ser 0 o 1.', 1;
SET @ApplyChanges = CONVERT(bit, @ApplyChangesText);
IF OBJECT_ID('restaurante.Site', 'U') IS NULL
  THROW 51823, 'Ejecuta primero las migraciones base de Restaurante.', 1;

BEGIN TRY
  BEGIN TRANSACTION;

  /* Sólo dígitos en cuenta, CLABE y tarjeta: la aplicación normaliza al guardar
     y agrupa al imprimir, así que el separador nunca llega a la base. */
  IF COL_LENGTH('restaurante.Site', 'TransferBankName') IS NULL
    ALTER TABLE restaurante.Site ADD TransferBankName varchar(120) NULL;

  IF COL_LENGTH('restaurante.Site', 'TransferAccountHolder') IS NULL
    ALTER TABLE restaurante.Site ADD TransferAccountHolder varchar(160) NULL;

  IF COL_LENGTH('restaurante.Site', 'TransferAccountNumber') IS NULL
    ALTER TABLE restaurante.Site ADD TransferAccountNumber varchar(30) NULL;

  IF COL_LENGTH('restaurante.Site', 'TransferClabe') IS NULL
    ALTER TABLE restaurante.Site ADD TransferClabe varchar(30) NULL;

  IF COL_LENGTH('restaurante.Site', 'TransferCardNumber') IS NULL
    ALTER TABLE restaurante.Site ADD TransferCardNumber varchar(30) NULL;

  IF COL_LENGTH('restaurante.Site', 'TransferInstructions') IS NULL
    ALTER TABLE restaurante.Site ADD TransferInstructions varchar(300) NULL;

  /* Las restricciones se agregan después de las columnas para poder ejecutar la
     migración sobre bases que ya tenían parte del cambio aplicado. */
  IF NOT EXISTS
  (
    SELECT 1 FROM sys.check_constraints
    WHERE [name] = 'CK_RestaurantSite_TransferAccount' AND parent_object_id = OBJECT_ID('restaurante.Site')
  )
    EXEC('ALTER TABLE restaurante.Site WITH CHECK ADD CONSTRAINT CK_RestaurantSite_TransferAccount CHECK
    (
      TransferAccountNumber IS NULL
      OR (LEN(TransferAccountNumber) BETWEEN 6 AND 20 AND TransferAccountNumber NOT LIKE ''%[^0-9]%'')
    );');

  IF NOT EXISTS
  (
    SELECT 1 FROM sys.check_constraints
    WHERE [name] = 'CK_RestaurantSite_TransferClabe' AND parent_object_id = OBJECT_ID('restaurante.Site')
  )
    EXEC('ALTER TABLE restaurante.Site WITH CHECK ADD CONSTRAINT CK_RestaurantSite_TransferClabe CHECK
    (
      TransferClabe IS NULL
      OR (LEN(TransferClabe) = 18 AND TransferClabe NOT LIKE ''%[^0-9]%'')
    );');

  IF NOT EXISTS
  (
    SELECT 1 FROM sys.check_constraints
    WHERE [name] = 'CK_RestaurantSite_TransferCard' AND parent_object_id = OBJECT_ID('restaurante.Site')
  )
    EXEC('ALTER TABLE restaurante.Site WITH CHECK ADD CONSTRAINT CK_RestaurantSite_TransferCard CHECK
    (
      TransferCardNumber IS NULL
      OR (LEN(TransferCardNumber) BETWEEN 15 AND 19 AND TransferCardNumber NOT LIKE ''%[^0-9]%'')
    );');

  /* Un destino sin titular no se puede imprimir: el cliente no sabría a nombre
     de quién transferir. */
  IF NOT EXISTS
  (
    SELECT 1 FROM sys.check_constraints
    WHERE [name] = 'CK_RestaurantSite_TransferHolder' AND parent_object_id = OBJECT_ID('restaurante.Site')
  )
    EXEC('ALTER TABLE restaurante.Site WITH CHECK ADD CONSTRAINT CK_RestaurantSite_TransferHolder CHECK
    (
      TransferAccountHolder IS NOT NULL
      OR (TransferAccountNumber IS NULL AND TransferClabe IS NULL AND TransferCardNumber IS NULL)
    );');

  /* Validaciones. Las columnas nacen en este mismo lote y SQL Server sólo
     difiere la resolución de nombres de objeto, no de columna: las consultas
     que las leen tienen que compilarse en tiempo de ejecución. */
  IF COL_LENGTH('restaurante.Site', 'TransferClabe') IS NULL
    OR COL_LENGTH('restaurante.Site', 'TransferAccountHolder') IS NULL
    OR COL_LENGTH('restaurante.Site', 'TransferCardNumber') IS NULL
    OR COL_LENGTH('restaurante.Site', 'TransferAccountNumber') IS NULL
    OR COL_LENGTH('restaurante.Site', 'TransferBankName') IS NULL
    OR COL_LENGTH('restaurante.Site', 'TransferInstructions') IS NULL
    THROW 51824, 'Las columnas de transferencia no quedaron creadas en restaurante.Site.', 1;

  DECLARE @MalformedCount int, @OrphanCount int, @SiteCount int, @ConfiguredCount int;

  EXEC sys.sp_executesql
    N'SELECT @Malformed = COUNT(*)
      FROM restaurante.Site
      WHERE (TransferClabe IS NOT NULL AND (LEN(TransferClabe) <> 18 OR TransferClabe LIKE ''%[^0-9]%''))
         OR (TransferCardNumber IS NOT NULL AND (LEN(TransferCardNumber) NOT BETWEEN 15 AND 19 OR TransferCardNumber LIKE ''%[^0-9]%''))
         OR (TransferAccountNumber IS NOT NULL AND (LEN(TransferAccountNumber) NOT BETWEEN 6 AND 20 OR TransferAccountNumber LIKE ''%[^0-9]%''));',
    N'@Malformed int OUTPUT',
    @Malformed = @MalformedCount OUTPUT;

  IF @MalformedCount > 0
    THROW 51825, 'Hay datos bancarios almacenados con separadores o longitud inválida.', 1;

  EXEC sys.sp_executesql
    N'SELECT @Orphan = COUNT(*)
      FROM restaurante.Site
      WHERE TransferAccountHolder IS NULL
        AND (TransferAccountNumber IS NOT NULL OR TransferClabe IS NOT NULL OR TransferCardNumber IS NOT NULL);',
    N'@Orphan int OUTPUT',
    @Orphan = @OrphanCount OUTPUT;

  IF @OrphanCount > 0
    THROW 51826, 'Hay sedes con destino de transferencia sin titular de cuenta.', 1;

  SET @SiteCount = (SELECT COUNT(*) FROM restaurante.Site);
  EXEC sys.sp_executesql
    N'SELECT @Configured = COUNT(*)
      FROM restaurante.Site
      WHERE TransferAccountHolder IS NOT NULL
        AND (TransferAccountNumber IS NOT NULL OR TransferClabe IS NOT NULL OR TransferCardNumber IS NOT NULL);',
    N'@Configured int OUTPUT',
    @Configured = @ConfiguredCount OUTPUT;

  SELECT DB_NAME() AS DatabaseName,
         @ApplyChanges AS ApplyChanges,
         @SiteCount AS SiteCount,
         @ConfiguredCount AS SitesWithTransferDetails,
         CASE WHEN @ApplyChanges = 1 THEN 'COMMITTED' ELSE 'DRY_RUN_VALIDATED' END AS MigrationStatus;

  IF @ApplyChanges = 1
    COMMIT TRANSACTION;
  ELSE
    ROLLBACK TRANSACTION;
END TRY
BEGIN CATCH
  IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
  THROW;
END CATCH;
