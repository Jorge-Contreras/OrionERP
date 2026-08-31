/*
  Canje transaccional de puntos Club Bruno desde el POS.

  Uso:
    sqlcmd ... -f 65001 -v ExpectedDatabase="Orion_SandBox" ApplyChanges="0" -i 20260830_bruno_pos_points_redemption.sql
    sqlcmd ... -f 65001 -v ExpectedDatabase="Orion_SandBox" ApplyChanges="1" -i 20260830_bruno_pos_points_redemption.sql

  ApplyChanges=0 ejecuta todas las validaciones y revierte la transacción.
  La migración es aditiva e idempotente.
*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET XACT_ABORT ON;
SET NOCOUNT ON;

DECLARE @ExpectedDatabase sysname = N'$(ExpectedDatabase)';
DECLARE @ApplyChanges bit = TRY_CONVERT(bit, N'$(ApplyChanges)');
DECLARE @LockResult int;

IF @ExpectedDatabase NOT IN (N'Orion_Sandbox', N'Orion_SandBox', N'grupocarpio')
  THROW 51800, 'ExpectedDatabase debe ser Orion_Sandbox, Orion_SandBox o grupocarpio.', 1;
IF DB_NAME() <> @ExpectedDatabase
  THROW 51801, 'La base conectada no coincide con ExpectedDatabase.', 1;
IF @ApplyChanges IS NULL
  THROW 51802, 'ApplyChanges debe ser 0 o 1.', 1;
IF SESSION_CONTEXT(N'OrionRfc') IS NOT NULL
  THROW 51803, 'La migración requiere SESSION_CONTEXT OrionRfc en NULL.', 1;
IF OBJECT_ID('restaurante.[Order]','U') IS NULL OR OBJECT_ID('fidelidad.PointLedger','U') IS NULL
  THROW 51804, 'El esquema de restaurante y fidelidad debe existir antes de esta migración.', 1;
IF COL_LENGTH('fidelidad.ProgramSettings','PointValueMxn') IS NULL
  THROW 51805, 'Primero debe aplicarse la política de canje 20260823_bruno_points_redemption.sql.', 1;

SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;

BEGIN TRY
  BEGIN TRANSACTION;

  EXEC @LockResult = sys.sp_getapplock
    @Resource = N'OrionERP:Bruno:PosPointsRedemption:20260830',
    @LockMode = N'Exclusive',
    @LockOwner = N'Transaction',
    @LockTimeout = 15000;
  IF @LockResult < 0
    THROW 51806, 'No fue posible obtener el bloqueo exclusivo de migración.', 1;

  IF COL_LENGTH('restaurante.Order','RedeemedPoints') IS NULL
    ALTER TABLE restaurante.[Order] ADD RedeemedPoints int NOT NULL
      CONSTRAINT DF_RestaurantOrder_RedeemedPoints DEFAULT 0 WITH VALUES;

  IF COL_LENGTH('restaurante.Order','RedemptionValue') IS NULL
    ALTER TABLE restaurante.[Order] ADD RedemptionValue decimal(18,2) NOT NULL
      CONSTRAINT DF_RestaurantOrder_RedemptionValue DEFAULT 0 WITH VALUES;

  IF NOT EXISTS(SELECT 1 FROM sys.check_constraints WHERE [name]='CK_RestaurantOrder_RedeemedPoints')
    EXEC sys.sp_executesql N'ALTER TABLE restaurante.[Order] WITH CHECK
      ADD CONSTRAINT CK_RestaurantOrder_RedeemedPoints CHECK(RedeemedPoints>=0);';

  IF NOT EXISTS(SELECT 1 FROM sys.check_constraints WHERE [name]='CK_RestaurantOrder_RedemptionValue')
    EXEC sys.sp_executesql N'ALTER TABLE restaurante.[Order] WITH CHECK
      ADD CONSTRAINT CK_RestaurantOrder_RedemptionValue
      CHECK(RedemptionValue>=0 AND RedemptionValue<=DiscountTotal
            AND ((RedeemedPoints=0 AND RedemptionValue=0) OR (RedeemedPoints>0 AND RedemptionValue>0)));';

  /* Una orden cubierta por puntos puede tener solamente propina en efectivo. */
  IF EXISTS(SELECT 1 FROM sys.check_constraints WHERE [name]='CK_RestaurantPayment_Amount')
    ALTER TABLE restaurante.Payment DROP CONSTRAINT CK_RestaurantPayment_Amount;

  ALTER TABLE restaurante.Payment WITH CHECK
    ADD CONSTRAINT CK_RestaurantPayment_Amount
    CHECK(Amount>=0 AND TipAmount>=0 AND Amount+TipAmount>0);

  IF NOT EXISTS
  (
    SELECT 1 FROM sys.indexes
    WHERE object_id=OBJECT_ID('fidelidad.PointLedger') AND [name]='IX_PointLedger_Order'
  )
    CREATE INDEX IX_PointLedger_Order
      ON fidelidad.PointLedger(Rfc,OrderId,EntryType)
      INCLUDE(PointsDelta,BalanceAfter,RefundId,SourceKey);

  DECLARE @InvalidOrders int;
  EXEC sys.sp_executesql
    N'SELECT @InvalidOut=COUNT(*) FROM restaurante.[Order]
      WHERE RedeemedPoints<0 OR RedemptionValue<0 OR RedemptionValue>DiscountTotal;',
    N'@InvalidOut int OUTPUT',
    @InvalidOut=@InvalidOrders OUTPUT;
  IF @InvalidOrders>0
    THROW 51807, 'La validación detectó valores de canje inválidos en órdenes.', 1;

  IF NOT EXISTS
  (
    SELECT 1 FROM fidelidad.ProgramSettings
    WHERE Rfc='BRUNOS260707L26' AND PointValueMxn=1.00 AND MinimumRedeemPoints=100
  )
    THROW 51808, 'La política de Club Bruno no conserva valor $1 y mínimo 100 puntos.', 1;

  SELECT
    DB_NAME() AS DatabaseName,
    @ApplyChanges AS ApplyChanges,
    COL_LENGTH('restaurante.Order','RedeemedPoints') AS RedeemedPointsColumnBytes,
    COL_LENGTH('restaurante.Order','RedemptionValue') AS RedemptionValueColumnBytes,
    (SELECT COUNT_BIG(*) FROM fidelidad.PointLedger WHERE EntryType='Redeem' AND OrderId IS NULL) AS ExistingUnlinkedRedemptions;

  IF @ApplyChanges=1
    COMMIT TRANSACTION;
  ELSE
    ROLLBACK TRANSACTION;
END TRY
BEGIN CATCH
  IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
  THROW;
END CATCH;
