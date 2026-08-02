/*
  Retira puntos todavía vigentes de órdenes canceladas y deja PointsEarned en cero.

  Uso:
    sqlcmd ... -f 65001 -v ExpectedDatabase="Orion_Sandbox" ApplyChanges="0" -i 20260802_cancelled_order_loyalty_reconciliation.sql
    sqlcmd ... -f 65001 -v ExpectedDatabase="Orion_Sandbox" ApplyChanges="1" -i 20260802_cancelled_order_loyalty_reconciliation.sql

  ApplyChanges=0 ejecuta y valida la reconciliación dentro de una transacción
  que se revierte. ApplyChanges=1 confirma. Producción requiere respaldo y
  autorización explícita.
*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET XACT_ABORT ON;
SET NOCOUNT ON;

DECLARE @ExpectedDatabase sysname = N'$(ExpectedDatabase)';
DECLARE @ApplyChanges bit = TRY_CONVERT(bit, N'$(ApplyChanges)');
DECLARE @BrunoRfc varchar(50) = 'BRUNOS260707L26';
DECLARE @LockResult int;
DECLARE @OrderId uniqueidentifier;
DECLARE @Rfc varchar(50);
DECLARE @MemberId uniqueidentifier;
DECLARE @OutstandingPoints int;
DECLARE @CurrentBalance int;
DECLARE @PointsToReverse int;
DECLARE @BalanceAfter int;
DECLARE @SourceKey varchar(120);
DECLARE @OrdersReconciled int = 0;
DECLARE @PointsReversed int = 0;
DECLARE @AdditionalOrdersCleared int = 0;

IF @ExpectedDatabase NOT IN (N'Orion_Sandbox', N'Orion_SandBox', N'grupocarpio')
  THROW 51320, 'ExpectedDatabase debe ser Orion_Sandbox o grupocarpio.', 1;
IF DB_NAME() <> @ExpectedDatabase
  THROW 51321, 'La base conectada no coincide con ExpectedDatabase.', 1;
IF @ApplyChanges IS NULL
  THROW 51322, 'ApplyChanges debe ser 0 o 1.', 1;
IF SESSION_CONTEXT(N'OrionRfc') IS NOT NULL
  THROW 51323, 'La migración requiere SESSION_CONTEXT OrionRfc en NULL.', 1;
IF OBJECT_ID('restaurante.Order','U') IS NULL
   OR OBJECT_ID('fidelidad.MemberAccount','U') IS NULL
   OR OBJECT_ID('fidelidad.PointLedger','U') IS NULL
  THROW 51324, 'Falta aplicar el esquema de restaurante y fidelidad.', 1;

SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;

BEGIN TRY
  BEGIN TRANSACTION;

  EXEC @LockResult = sys.sp_getapplock
    @Resource = N'OrionERP:Bruno:CancelledOrderLoyalty:20260802',
    @LockMode = N'Exclusive',
    @LockOwner = N'Transaction',
    @LockTimeout = 15000;
  IF @LockResult < 0
    THROW 51325, 'No fue posible obtener el bloqueo exclusivo de migración.', 1;

  DECLARE CancelledOrders CURSOR LOCAL STATIC READ_ONLY FOR
    SELECT
      orderInfo.Id,
      orderInfo.Rfc,
      orderInfo.MemberId,
      CASE
        WHEN pointInfo.EarnedPoints > pointInfo.ReversedPoints
          THEN pointInfo.EarnedPoints - pointInfo.ReversedPoints
        ELSE 0
      END AS OutstandingPoints
    FROM restaurante.[Order] orderInfo WITH(UPDLOCK,HOLDLOCK)
    CROSS APPLY
    (
      SELECT
        CASE
          WHEN ISNULL(SUM(CASE WHEN ledger.EntryType='Earn' AND ledger.PointsDelta>0 THEN ledger.PointsDelta ELSE 0 END),0) > orderInfo.PointsEarned
            THEN ISNULL(SUM(CASE WHEN ledger.EntryType='Earn' AND ledger.PointsDelta>0 THEN ledger.PointsDelta ELSE 0 END),0)
          ELSE orderInfo.PointsEarned
        END AS EarnedPoints,
        ISNULL(SUM(CASE
          WHEN ledger.EntryType IN ('RefundReversal','CancellationReversal') AND ledger.PointsDelta<0
            THEN -ledger.PointsDelta
          ELSE 0
        END),0) AS ReversedPoints
      FROM fidelidad.PointLedger ledger
      WHERE ledger.Rfc=orderInfo.Rfc AND ledger.OrderId=orderInfo.Id
    ) pointInfo
    WHERE orderInfo.Rfc=@BrunoRfc
      AND orderInfo.[Status]='Cancelled'
      AND orderInfo.MemberId IS NOT NULL
      AND NOT EXISTS
      (
        SELECT 1
        FROM fidelidad.PointLedger existing
        WHERE existing.Rfc=orderInfo.Rfc
          AND existing.SourceKey=CONCAT(
            'order:',
            REPLACE(CONVERT(varchar(36),orderInfo.Id),'-',''),
            ':cancellation-points')
      )
    ORDER BY orderInfo.Rfc,orderInfo.MemberId,COALESCE(orderInfo.CancelledAt,orderInfo.CreatedAt),orderInfo.Id;

  OPEN CancelledOrders;
  FETCH NEXT FROM CancelledOrders INTO @OrderId,@Rfc,@MemberId,@OutstandingPoints;
  WHILE @@FETCH_STATUS=0
  BEGIN
    SET @CurrentBalance = NULL;
    SELECT @CurrentBalance=member.PointsBalance
    FROM fidelidad.MemberAccount member WITH(UPDLOCK,HOLDLOCK)
    WHERE member.Rfc=@Rfc AND member.Id=@MemberId;

    IF @CurrentBalance IS NULL
      THROW 51326, 'Una orden cancelada referencia una membresía inexistente.', 1;

    SET @PointsToReverse = CASE
      WHEN @OutstandingPoints<=0 OR @CurrentBalance<=0 THEN 0
      WHEN @OutstandingPoints<@CurrentBalance THEN @OutstandingPoints
      ELSE @CurrentBalance
    END;
    SET @BalanceAfter = @CurrentBalance - @PointsToReverse;
    SET @SourceKey = CONCAT(
      'order:',
      REPLACE(CONVERT(varchar(36),@OrderId),'-',''),
      ':cancellation-points');

    IF @PointsToReverse>0
    BEGIN
      UPDATE fidelidad.MemberAccount
      SET PointsBalance=@BalanceAfter,UpdatedAt=SYSUTCDATETIME()
      WHERE Rfc=@Rfc AND Id=@MemberId;

      INSERT fidelidad.PointLedger
        (Rfc,MemberId,EntryType,PointsDelta,BalanceAfter,EligibleMerchandiseAmount,
         OrderId,SourceKey,Reason,CreatedBy)
      VALUES
        (@Rfc,@MemberId,'CancellationReversal',-@PointsToReverse,@BalanceAfter,0,
         @OrderId,@SourceKey,N'Reversión por orden cancelada',N'20260802_cancelled_order_loyalty_reconciliation');

      SET @PointsReversed += @PointsToReverse;
    END;

    UPDATE restaurante.[Order]
    SET PointsEarned=0
    WHERE Rfc=@Rfc AND Id=@OrderId;
    SET @OrdersReconciled += 1;

    FETCH NEXT FROM CancelledOrders INTO @OrderId,@Rfc,@MemberId,@OutstandingPoints;
  END;
  CLOSE CancelledOrders;
  DEALLOCATE CancelledOrders;

  UPDATE restaurante.[Order]
  SET PointsEarned=0
  WHERE Rfc=@BrunoRfc AND [Status]='Cancelled' AND PointsEarned<>0;
  SET @AdditionalOrdersCleared = @@ROWCOUNT;

  IF EXISTS(SELECT 1 FROM restaurante.[Order] WHERE Rfc=@BrunoRfc AND [Status]='Cancelled' AND PointsEarned<>0)
    THROW 51327, 'Persisten órdenes canceladas con puntos acreditados.', 1;
  IF EXISTS(SELECT 1 FROM fidelidad.MemberAccount WHERE Rfc=@BrunoRfc AND PointsBalance<0)
    THROW 51328, 'La reconciliación produjo un saldo negativo.', 1;

  SELECT
    DB_NAME() AS DatabaseName,
    @ApplyChanges AS ApplyChanges,
    @OrdersReconciled AS OrdersReconciled,
    @AdditionalOrdersCleared AS AdditionalOrdersCleared,
    @PointsReversed AS PointsReversed;

  IF @ApplyChanges=1
    COMMIT TRANSACTION;
  ELSE
  BEGIN
    ROLLBACK TRANSACTION;
    PRINT 'SIMULACIÓN COMPLETA: todos los cambios fueron revertidos.';
  END;
END TRY
BEGIN CATCH
  IF CURSOR_STATUS('local','CancelledOrders')>=0 CLOSE CancelledOrders;
  IF CURSOR_STATUS('local','CancelledOrders')>-3 DEALLOCATE CancelledOrders;
  IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
  THROW;
END CATCH;
