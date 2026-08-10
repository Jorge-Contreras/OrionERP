/*
  Revierte el incidente de carga BBVA identificado por ArchivoHash
  DB5A37ACB4A6DE34360AD09D7CE29CFA5BB9AC81DCEC609C9A2466C287C52FB8.

  Uso:
    sqlcmd ... -f 65001 -v ExpectedDatabase="grupocarpio" ApplyChanges="0" -i 20260809_cleanup_bank_import_DB5A37AC.sql
    sqlcmd ... -f 65001 -v ExpectedDatabase="grupocarpio" ApplyChanges="1" -i 20260809_cleanup_bank_import_DB5A37AC.sql

  ApplyChanges=0 valida todo dentro de una transacción y revierte.
  ApplyChanges=1 confirma. Los conteos esperados hacen que el script falle
  cerrado si el conjunto cambió desde la investigación del incidente.
*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET XACT_ABORT ON;
SET NOCOUNT ON;

DECLARE @ExpectedDatabase sysname = N'$(ExpectedDatabase)';
DECLARE @ApplyChanges bit = TRY_CONVERT(bit, N'$(ApplyChanges)');
DECLARE @ArchivoHash varchar(64) = 'DB5A37ACB4A6DE34360AD09D7CE29CFA5BB9AC81DCEC609C9A2466C287C52FB8';
DECLARE @ExpectedMovements int = 36;
DECLARE @ExpectedLinks int = 36;
DECLARE @ExpectedAutoPolicies int = 30;
DECLARE @ExpectedAutoAccountingRows int = 62;
DECLARE @ExpectedPreservedPolicies int = 6;
DECLARE @LockResult int;
DECLARE @DeletedLinks int;
DECLARE @DeletedPolicies int;
DECLARE @DeletedMovements int;

IF @ExpectedDatabase <> N'grupocarpio'
  THROW 51500, 'ExpectedDatabase debe ser grupocarpio para esta corrección de incidente.', 1;
IF DB_NAME() <> @ExpectedDatabase
  THROW 51501, 'La base conectada no coincide con ExpectedDatabase.', 1;
IF @ApplyChanges IS NULL
  THROW 51502, 'ApplyChanges debe ser 0 o 1.', 1;
IF SESSION_CONTEXT(N'OrionRfc') IS NOT NULL
  THROW 51503, 'La corrección requiere SESSION_CONTEXT OrionRfc en NULL.', 1;

SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;

BEGIN TRY
  BEGIN TRANSACTION;

  EXEC @LockResult = sys.sp_getapplock
    @Resource = N'OrionERP:Bancos:Cleanup:DB5A37ACB4A6DE34',
    @LockMode = N'Exclusive',
    @LockOwner = N'Transaction',
    @LockTimeout = 15000;

  IF @LockResult < 0
    THROW 51504, 'No fue posible obtener el bloqueo exclusivo para la corrección.', 1;

  DECLARE @AffectedMovements TABLE
  (
    MovimientoId bigint NOT NULL PRIMARY KEY
  );

  INSERT INTO @AffectedMovements (MovimientoId)
  SELECT M.Movimiento_ID
  FROM bancos.Movimientos AS M WITH (UPDLOCK, HOLDLOCK)
  WHERE M.ArchivoHash = @ArchivoHash;

  IF (SELECT COUNT(*) FROM @AffectedMovements) <> @ExpectedMovements
    THROW 51505, 'El conteo de movimientos del archivo cambió; no se realizó la corrección.', 1;

  DECLARE @AutoPolicies TABLE
  (
    TransaccionId int NOT NULL PRIMARY KEY
  );

  INSERT INTO @AutoPolicies (TransaccionId)
  SELECT DISTINCT MT.Transaccion_ID
  FROM bancos.Movimiento_Transaccion AS MT WITH (UPDLOCK, HOLDLOCK)
  INNER JOIN @AffectedMovements AS A
    ON A.MovimientoId = MT.Movimiento_ID
  WHERE MT.CreatedBy = N'auto-polizas';

  IF (SELECT COUNT(*) FROM @AutoPolicies) <> @ExpectedAutoPolicies
    THROW 51506, 'El conteo de pólizas automáticas cambió; no se realizó la corrección.', 1;

  DECLARE @PreservedPolicies TABLE
  (
    TransaccionId int NOT NULL PRIMARY KEY
  );

  INSERT INTO @PreservedPolicies (TransaccionId)
  SELECT DISTINCT MT.Transaccion_ID
  FROM bancos.Movimiento_Transaccion AS MT WITH (UPDLOCK, HOLDLOCK)
  INNER JOIN @AffectedMovements AS A
    ON A.MovimientoId = MT.Movimiento_ID
  WHERE ISNULL(MT.CreatedBy, N'') <> N'auto-polizas';

  IF (SELECT COUNT(*) FROM @PreservedPolicies) <> @ExpectedPreservedPolicies
    THROW 51507, 'El conteo de pólizas que deben conservarse cambió; no se realizó la corrección.', 1;

  IF (SELECT COUNT(*)
      FROM bancos.Movimiento_Transaccion AS MT
      INNER JOIN @AffectedMovements AS A ON A.MovimientoId = MT.Movimiento_ID) <> @ExpectedLinks
    THROW 51508, 'El conteo de ligas bancarias cambió; no se realizó la corrección.', 1;

  IF EXISTS
  (
    SELECT 1
    FROM bancos.Movimiento_Transaccion AS MT
    INNER JOIN @AutoPolicies AS AP ON AP.TransaccionId = MT.Transaccion_ID
    LEFT JOIN @AffectedMovements AS A ON A.MovimientoId = MT.Movimiento_ID
    WHERE A.MovimientoId IS NULL
  )
    THROW 51509, 'Una póliza automática está ligada a movimientos fuera del archivo afectado.', 1;

  IF (SELECT COUNT(*)
      FROM dbo.Registro_Contable AS RC
      INNER JOIN @AutoPolicies AS AP ON AP.TransaccionId = RC.TransaccionID) <> @ExpectedAutoAccountingRows
    THROW 51510, 'El conteo de registros contables automáticos cambió; no se realizó la corrección.', 1;

  IF EXISTS
  (
    SELECT 1 FROM dbo.Transaccion_Comprobante AS TC
    INNER JOIN @AutoPolicies AS AP ON AP.TransaccionId = TC.Transaccion_ID
  ) OR EXISTS
  (
    SELECT 1 FROM dbo.Transaccion_DoctoRelacionado AS TD
    INNER JOIN @AutoPolicies AS AP ON AP.TransaccionId = TD.Transaccion_ID
  ) OR EXISTS
  (
    SELECT 1 FROM dbo.Reservation_Transacciones AS RT
    INNER JOIN @AutoPolicies AS AP ON AP.TransaccionId = RT.TransaccionID
  ) OR EXISTS
  (
    SELECT 1 FROM dbo.TRANSACTION_ATTACHMENT AS TA
    INNER JOIN @AutoPolicies AS AP ON AP.TransaccionId = TA.TranID
  ) OR EXISTS
  (
    SELECT 1 FROM AP.OccurrencePayment AS OP
    INNER JOIN @AutoPolicies AS AP ON AP.TransaccionId = OP.TransaccionId
  ) OR EXISTS
  (
    SELECT 1 FROM dbo.OrdenTrabajoTransaccion AS OT
    INNER JOIN @AutoPolicies AS AP ON AP.TransaccionId = OT.TransaccionId
  )
    THROW 51511, 'Una póliza automática adquirió dependencias ajenas al proceso bancario.', 1;

  SELECT
    DB_NAME() AS DatabaseName,
    @ApplyChanges AS ApplyChanges,
    @ArchivoHash AS ArchivoHash,
    (SELECT COUNT(*) FROM @AffectedMovements) AS MovementsToDelete,
    (SELECT COUNT(*) FROM @AutoPolicies) AS AutoPoliciesToDelete,
    (SELECT COUNT(*) FROM dbo.Registro_Contable AS RC
      INNER JOIN @AutoPolicies AS AP ON AP.TransaccionId = RC.TransaccionID) AS AccountingRowsToDelete,
    (SELECT COUNT(*) FROM @PreservedPolicies) AS ExistingPoliciesToPreserve;

  DELETE MT
  FROM bancos.Movimiento_Transaccion AS MT
  INNER JOIN @AffectedMovements AS A
    ON A.MovimientoId = MT.Movimiento_ID;
  SET @DeletedLinks = @@ROWCOUNT;

  DELETE T
  FROM dbo.Transacciones AS T
  INNER JOIN @AutoPolicies AS AP
    ON AP.TransaccionId = T.ID;
  SET @DeletedPolicies = @@ROWCOUNT;

  DELETE M
  FROM bancos.Movimientos AS M
  INNER JOIN @AffectedMovements AS A
    ON A.MovimientoId = M.Movimiento_ID;
  SET @DeletedMovements = @@ROWCOUNT;

  IF @DeletedLinks <> @ExpectedLinks
    THROW 51512, 'No se eliminaron todas las ligas bancarias esperadas.', 1;
  IF @DeletedPolicies <> @ExpectedAutoPolicies
    THROW 51513, 'No se eliminaron todas las pólizas automáticas esperadas.', 1;
  IF @DeletedMovements <> @ExpectedMovements
    THROW 51514, 'No se eliminaron todos los movimientos esperados.', 1;
  IF EXISTS (SELECT 1 FROM bancos.Movimientos WHERE ArchivoHash = @ArchivoHash)
    THROW 51515, 'Persisten movimientos con el ArchivoHash afectado.', 1;
  IF EXISTS
  (
    SELECT 1
    FROM dbo.Transacciones AS T
    INNER JOIN @AutoPolicies AS AP ON AP.TransaccionId = T.ID
  )
    THROW 51516, 'Persisten pólizas automáticas afectadas.', 1;
  IF (SELECT COUNT(*)
      FROM dbo.Transacciones AS T
      INNER JOIN @PreservedPolicies AS PP ON PP.TransaccionId = T.ID) <> @ExpectedPreservedPolicies
    THROW 51517, 'Una póliza no automática fue eliminada inesperadamente.', 1;

  SELECT
    DB_NAME() AS DatabaseName,
    @ApplyChanges AS ApplyChanges,
    @DeletedLinks AS DeletedMovementLinks,
    @DeletedPolicies AS DeletedAutoPolicies,
    @ExpectedAutoAccountingRows AS DeletedAccountingRows,
    @DeletedMovements AS DeletedMovements,
    (SELECT COUNT(*) FROM dbo.Transacciones AS T
      INNER JOIN @PreservedPolicies AS PP ON PP.TransaccionId = T.ID) AS PreservedExistingPolicies;

  IF @ApplyChanges = 1
    COMMIT TRANSACTION;
  ELSE
    ROLLBACK TRANSACTION;
END TRY
BEGIN CATCH
  IF XACT_STATE() <> 0
    ROLLBACK TRANSACTION;
  THROW;
END CATCH;
