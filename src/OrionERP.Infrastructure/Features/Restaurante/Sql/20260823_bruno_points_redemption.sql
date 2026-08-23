/*
  Canje y caducidad de puntos de Club Bruno.

  Autorizado por Dirección General el 23 de agosto de 2026 (punto B5 del informe
  "Membresía, promociones y plan comercial"):
    1 punto = $1 MXN · canje desde 100 puntos · vigencia de 12 meses.

  Contenido:
    1. Amplía fidelidad.ProgramSettings con el valor del punto, el mínimo de canje
       y los meses de vigencia.
    2. Fija la política aprobada y habilita la caducidad para el RFC de Bruno's.
    3. Agrega el índice que necesita el cálculo PEPS de caducidad.

  El canje y la caducidad se registran como asientos 'Redeem' y 'Expiration' en
  fidelidad.PointLedger. La tabla no tiene restricción CHECK sobre EntryType, por
  lo que no requiere cambio estructural adicional.

  Uso:
    sqlcmd ... -f 65001 -v ExpectedDatabase="grupocarpio" ApplyChanges="0" -i 20260823_bruno_points_redemption.sql
    sqlcmd ... -f 65001 -v ExpectedDatabase="grupocarpio" ApplyChanges="1" -i 20260823_bruno_points_redemption.sql

  Es idempotente: puede volver a ejecutarse sin duplicar nada.
*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET XACT_ABORT ON;
SET NOCOUNT ON;

DECLARE @ExpectedDatabase sysname = N'$(ExpectedDatabase)';
DECLARE @ApplyChanges bit = TRY_CONVERT(bit, N'$(ApplyChanges)');
DECLARE @BrunoRfc varchar(50) = 'BRUNOS260707L26';
DECLARE @LockResult int;

IF @ExpectedDatabase NOT IN (N'Orion_Sandbox', N'Orion_SandBox', N'grupocarpio')
  THROW 51400, 'ExpectedDatabase debe ser Orion_Sandbox o grupocarpio.', 1;
IF DB_NAME() <> @ExpectedDatabase
  THROW 51401, 'La base conectada no coincide con ExpectedDatabase.', 1;
IF @ApplyChanges IS NULL
  THROW 51402, 'ApplyChanges debe ser 0 o 1.', 1;
IF SESSION_CONTEXT(N'OrionRfc') IS NOT NULL
  THROW 51403, 'La migración requiere SESSION_CONTEXT OrionRfc en NULL.', 1;

SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;

BEGIN TRY
  BEGIN TRANSACTION;

  EXEC @LockResult = sys.sp_getapplock
    @Resource = N'OrionERP:Bruno:PointsRedemption:20260823',
    @LockMode = N'Exclusive',
    @LockOwner = N'Transaction',
    @LockTimeout = 15000;
  IF @LockResult < 0
    THROW 51404, 'No fue posible obtener el bloqueo exclusivo de migración.', 1;

  /* ------------------------------------------------------------------
     1. Nuevas columnas de política
     ------------------------------------------------------------------ */
  IF COL_LENGTH('fidelidad.ProgramSettings','PointValueMxn') IS NULL
    ALTER TABLE fidelidad.ProgramSettings ADD PointValueMxn decimal(18,2) NOT NULL
      CONSTRAINT DF_LoyaltySettings_PointValue DEFAULT 1.00 WITH VALUES;

  IF COL_LENGTH('fidelidad.ProgramSettings','MinimumRedeemPoints') IS NULL
    ALTER TABLE fidelidad.ProgramSettings ADD MinimumRedeemPoints int NOT NULL
      CONSTRAINT DF_LoyaltySettings_MinRedeem DEFAULT 100 WITH VALUES;

  IF COL_LENGTH('fidelidad.ProgramSettings','PointsValidityMonths') IS NULL
    ALTER TABLE fidelidad.ProgramSettings ADD PointsValidityMonths int NOT NULL
      CONSTRAINT DF_LoyaltySettings_Validity DEFAULT 12 WITH VALUES;

  /* Las restricciones también referencian columnas recién creadas: se aplican por
     sp_executesql para que se compilen después del ALTER. */
  IF NOT EXISTS(SELECT 1 FROM sys.check_constraints WHERE [name]='CK_LoyaltySettings_PointValue')
    EXEC sys.sp_executesql N'ALTER TABLE fidelidad.ProgramSettings WITH CHECK
      ADD CONSTRAINT CK_LoyaltySettings_PointValue CHECK(PointValueMxn > 0);';

  IF NOT EXISTS(SELECT 1 FROM sys.check_constraints WHERE [name]='CK_LoyaltySettings_MinRedeem')
    EXEC sys.sp_executesql N'ALTER TABLE fidelidad.ProgramSettings WITH CHECK
      ADD CONSTRAINT CK_LoyaltySettings_MinRedeem CHECK(MinimumRedeemPoints > 0);';

  IF NOT EXISTS(SELECT 1 FROM sys.check_constraints WHERE [name]='CK_LoyaltySettings_Validity')
    EXEC sys.sp_executesql N'ALTER TABLE fidelidad.ProgramSettings WITH CHECK
      ADD CONSTRAINT CK_LoyaltySettings_Validity CHECK(PointsValidityMonths BETWEEN 1 AND 240);';

  /* ------------------------------------------------------------------
     2. Política aprobada para Bruno's
     ------------------------------------------------------------------ */
  /* Las columnas se acaban de crear en este mismo lote, por lo que el compilador
     todavía no las conoce: cualquier referencia directa se resuelve en tiempo de
     ejecución mediante sp_executesql. */
  DECLARE @Affected int;
  EXEC sys.sp_executesql
    N'UPDATE fidelidad.ProgramSettings
      SET PointValueMxn = 1.00,
          MinimumRedeemPoints = 100,
          PointsValidityMonths = 12,
          PointsExpire = 1,
          UpdatedAt = SYSUTCDATETIME(),
          UpdatedBy = N''20260823_bruno_points_redemption''
      WHERE Rfc = @Rfc;
      SET @AffectedOut = @@ROWCOUNT;',
    N'@Rfc varchar(50), @AffectedOut int OUTPUT',
    @Rfc = @BrunoRfc, @AffectedOut = @Affected OUTPUT;

  IF @Affected = 0
    THROW 51405, 'No existe configuración de fidelidad para el RFC de Bruno.', 1;

  /* ------------------------------------------------------------------
     3. Índice de apoyo para el cálculo PEPS de caducidad
     ------------------------------------------------------------------ */
  IF NOT EXISTS
  (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID('fidelidad.PointLedger') AND [name] = 'IX_PointLedger_Expiry'
  )
    CREATE INDEX IX_PointLedger_Expiry
      ON fidelidad.PointLedger(Rfc, MemberId, OccurredAt)
      INCLUDE(PointsDelta, EntryType);

  /* ------------------------------------------------------------------
     Validaciones
     ------------------------------------------------------------------ */
  DECLARE @PolicyOk int = 0;
  EXEC sys.sp_executesql
    N'SELECT @OkOut = COUNT(*) FROM fidelidad.ProgramSettings
      WHERE Rfc = @Rfc AND PointValueMxn = 1.00
        AND MinimumRedeemPoints = 100 AND PointsValidityMonths = 12 AND PointsExpire = 1;',
    N'@Rfc varchar(50), @OkOut int OUTPUT',
    @Rfc = @BrunoRfc, @OkOut = @PolicyOk OUTPUT;

  IF @PolicyOk = 0
    THROW 51406, 'La política de canje no quedó configurada como se aprobó.', 1;

  IF NOT EXISTS
  (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID('fidelidad.PointLedger') AND [name] = 'IX_PointLedger_Expiry'
  )
    THROW 51407, 'No se creó el índice de caducidad.', 1;

  /* Ningún socio debe quedar con saldo negativo ni descuadrado contra el libro mayor. */
  IF EXISTS
  (
    SELECT 1
    FROM fidelidad.MemberAccount member
    OUTER APPLY
    (
      SELECT ISNULL(SUM(ledger.PointsDelta), 0) AS LedgerBalance
      FROM fidelidad.PointLedger ledger
      WHERE ledger.Rfc = member.Rfc AND ledger.MemberId = member.Id
    ) AS totals
    WHERE member.Rfc = @BrunoRfc AND member.PointsBalance <> totals.LedgerBalance
  )
    THROW 51408, 'Hay saldos de socio que no cuadran contra el libro mayor de puntos.', 1;

  EXEC sys.sp_executesql
    N'SELECT DB_NAME() AS DatabaseName, @Apply AS ApplyChanges,
             PesosPerPoint, PointValueMxn, MinimumRedeemPoints, PointsValidityMonths,
             IsAccrualEnabled, PointsExpire
      FROM fidelidad.ProgramSettings WHERE Rfc = @Rfc;',
    N'@Rfc varchar(50), @Apply bit',
    @Rfc = @BrunoRfc, @Apply = @ApplyChanges;

  SELECT COUNT(*) AS SociosConSaldo, ISNULL(SUM(PointsBalance), 0) AS PuntosVigentes,
         CAST(ISNULL(SUM(PointsBalance), 0) * 1.00 AS decimal(18,2)) AS PasivoEstimadoMxn
  FROM fidelidad.MemberAccount WHERE Rfc = @BrunoRfc AND PointsBalance > 0;

  IF @ApplyChanges = 1
    COMMIT TRANSACTION;
  ELSE
  BEGIN
    ROLLBACK TRANSACTION;
    PRINT 'SIMULACIÓN COMPLETA: todos los cambios fueron revertidos.';
  END;
END TRY
BEGIN CATCH
  IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
  THROW;
END CATCH;
