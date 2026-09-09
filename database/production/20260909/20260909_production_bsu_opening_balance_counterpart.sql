/*
  Corte productivo 2026-09-09: 20260909_production_bsu_opening_balance_counterpart.
  Semántica revisada desde 20260909_bsu_opening_balance_counterpart_sandbox; los bytes de
  aquella no se tocan. Producción exige respaldo autorizado aparte, preview revisado, y
  sólo entonces apply.

  Agrega la contrapartida que le falta a la póliza 27047 de BSU210121M77, 'SALDO INICIAL
  DE ENERO 2024': cargo de 9,266.85 a 102.01.01 sin ningún abono. Es la única póliza
  descuadrada de fondo del sistema; las otras catorce difieren un centavo por redondeo y
  se resolvieron subiendo la tolerancia del validador, sin tocar datos.

  El usuario indicó el 2026-09-09 que el abono va a 401.25.01, 'INGRESO POR ARRENDAMIENTO
  SUITE SEUL' en el catálogo de BSU. La cuenta la eligió el usuario.

  ADVERTENCIA CONTABLE: abonar a ingresos registra 9,266.85 de ingreso en enero de 2024, y
  el estado de resultados de ese ejercicio cambia. Un saldo inicial suele abonarse a
  capital o a resultados de ejercicios anteriores. Se aplica lo que el usuario indicó.

  Aditiva: agrega un renglón, no modifica ni borra ninguno, no mete la póliza al ciclo y
  no la publica. Comprueba la forma de la póliza antes de escribir y la vuelve a leer bajo
  candado, así que no se aplica sobre datos que cambiaron después del preview.
  Idempotente: si ya cuadra, no hace nada.
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;

DECLARE @ExpectedDatabase sysname = N'$(ExpectedDatabase)';
DECLARE @ApplyChangesInput nvarchar(20) = N'$(ApplyChanges)';
DECLARE @ApplyChanges bit = 0;
DECLARE @MigrationId nvarchar(200) = N'$(MigrationId)';
DECLARE @MigrationChecksum varchar(128) = '$(MigrationChecksum)';
DECLARE @AppVersionInput nvarchar(64) = N'$(AppVersion)';
DECLARE @AppVersion nvarchar(64) = NULL;

IF @ApplyChangesInput NOT LIKE N'$' + N'(%'
BEGIN
  IF @ApplyChangesInput NOT IN (N'0', N'1')
    THROW 52080, 'ApplyChanges debe ser 0 o 1.', 1;
  SET @ApplyChanges = CONVERT(bit, @ApplyChangesInput);
END;

IF @ExpectedDatabase LIKE N'$' + N'(%'
   OR @ExpectedDatabase NOT IN (N'grupocarpio', N'Orion_CutoverValidation_20260908')
  THROW 52081, 'Esta migracion admite grupocarpio o el ensayo aislado Orion_CutoverValidation_20260908.', 1;
IF DB_NAME() <> @ExpectedDatabase
  THROW 52082, 'La conexion no coincide con ExpectedDatabase.', 1;
IF @MigrationId LIKE N'$' + N'(%'
   OR NULLIF(LTRIM(RTRIM(@MigrationId)), N'') IS NULL
   OR LEN(@MigrationId) > 200
  THROW 52083, 'MigrationId es obligatorio y debe provenir del manifiesto.', 1;
IF @MigrationChecksum LIKE '$' + '(%'
   OR LEN(@MigrationChecksum) <> 64
   OR @MigrationChecksum LIKE '%[^0-9A-F]%' COLLATE Latin1_General_100_BIN2
  THROW 52084, 'MigrationChecksum debe ser un SHA-256 hexadecimal de 64 caracteres.', 1;
IF @AppVersionInput NOT LIKE N'$' + N'(%'
  SET @AppVersion = NULLIF(LTRIM(RTRIM(@AppVersionInput)), N'');

DECLARE @Poliza int = 27047;
DECLARE @Rfc varchar(50) = 'BSU210121M77';
DECLARE @Importe decimal(19,2) = 9266.85;
DECLARE @Nivel1 varchar(50) = '401', @Nivel2 varchar(50) = '25', @Nivel3 varchar(50) = '01';
DECLARE @Actor nvarchar(256) =
  COALESCE(CONVERT(nvarchar(256), SESSION_CONTEXT(N'OrionERP.UserName')), CONVERT(nvarchar(256), ORIGINAL_LOGIN()));

DECLARE @ExistingChecksum char(64) =
(
  SELECT Checksum FROM orion.SchemaMigration WHERE MigrationId = @MigrationId
);
IF @ExistingChecksum IS NOT NULL
   AND UPPER(@ExistingChecksum) <> UPPER(@MigrationChecksum)
  THROW 52085, 'El mismo MigrationId ya existe con otro checksum.', 1;
IF @ExistingChecksum IS NOT NULL
BEGIN
  SELECT N'YA_APLICADO_CORTE_20260909' AS Estado, @MigrationId AS MigrationId;
  RETURN;
END;

/* Precondiciones por fila: si la poliza no es exactamente la esperada, no se toca. */
DECLARE @Cargos decimal(19,2), @Abonos decimal(19,2), @Renglones int, @Estado varchar(20), @PolizaRfc varchar(50);
SELECT @PolizaRfc = t.RFC, @Estado = ISNULL(t.CycleState, 'NULO')
FROM dbo.Transacciones t WHERE t.ID = @Poliza;
SELECT @Renglones = COUNT(*), @Cargos = ISNULL(SUM(r.Debe), 0), @Abonos = ISNULL(SUM(r.Haber), 0)
FROM dbo.Registro_Contable r WHERE r.TransaccionID = @Poliza;

IF @PolizaRfc IS NULL
  THROW 52086, 'La poliza del saldo inicial no existe en esta base.', 1;
IF @PolizaRfc <> @Rfc
  THROW 52087, 'La poliza no pertenece a la empresa esperada.', 1;
IF @Estado <> 'NULO'
  THROW 52088, 'La poliza ya entro al ciclo; una correccion asi se hace por reversa.', 1;
IF NOT EXISTS
(
  SELECT 1 FROM dbo.CuentasContables
  WHERE RFC = @Rfc AND Nivel1 = @Nivel1 AND Nivel2 = @Nivel2 AND Nivel3 = @Nivel3
)
  THROW 52089, 'La cuenta indicada no existe en el catalogo de la empresa.', 1;

PRINT 'Estado previo de la poliza 27047.';
SELECT @Poliza AS Poliza, @PolizaRfc AS Rfc, @Renglones AS Renglones,
       @Cargos AS Cargos, @Abonos AS Abonos, @Cargos - @Abonos AS Diferencia;

IF ABS(@Cargos - @Abonos) <= 0.01
BEGIN
  /* Ya cuadra: nada que hacer, pero se registra para no volver a intentarlo. */
  IF @ApplyChanges = 1
  BEGIN
    INSERT orion.SchemaMigration (MigrationId, Checksum, AppliedBy, AppVersion, DatabaseName)
      VALUES (@MigrationId, @MigrationChecksum, @Actor, @AppVersion, DB_NAME());
    SELECT N'YA_CUADRABA_SIN_CAMBIOS' AS Estado, DB_NAME() AS BaseDatos, @MigrationId AS MigrationId;
  END
  ELSE
    SELECT N'YA_CUADRABA_SIN_CAMBIOS' AS Estado, DB_NAME() AS BaseDatos, @MigrationId AS MigrationId;
  RETURN;
END;

IF @Renglones <> 1 OR ABS(@Cargos - @Importe) > 0.005 OR ABS(@Abonos) > 0.005
  THROW 52090, 'La poliza no tiene la forma comprobada de un solo cargo sin abono; requiere revision manual.', 1;

BEGIN TRY
  BEGIN TRANSACTION;
  DECLARE @LockResult int;
  EXEC @LockResult = sys.sp_getapplock
    @Resource = N'OrionERP:Accounting:BsuOpeningBalance:Production',
    @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 15000;
  IF @LockResult < 0
    THROW 52091, 'No se obtuvo el candado de la correccion.', 1;

  /* Relectura bajo candado: los datos pudieron cambiar despues del preview. */
  DECLARE @CargosAhora decimal(19,2), @AbonosAhora decimal(19,2), @RenglonesAhora int;
  SELECT @RenglonesAhora = COUNT(*), @CargosAhora = ISNULL(SUM(r.Debe), 0), @AbonosAhora = ISNULL(SUM(r.Haber), 0)
  FROM dbo.Registro_Contable r WITH (UPDLOCK, HOLDLOCK) WHERE r.TransaccionID = @Poliza;
  IF @RenglonesAhora <> 1 OR ABS(@CargosAhora - @Importe) > 0.005 OR ABS(@AbonosAhora) > 0.005
    THROW 52092, 'La poliza cambio despues del preview; no se aplica a ciegas.', 1;

  DECLARE @Descripcion varchar(200) =
  (
    SELECT MAX(Descripcion) FROM dbo.CuentasContables
    WHERE RFC = @Rfc AND Nivel1 = @Nivel1 AND Nivel2 = @Nivel2 AND Nivel3 = @Nivel3
  );

  INSERT dbo.Registro_Contable
    (CompanyId, TransaccionID, Nivel1, Nivel2, Nivel3, Nombre_Cuenta, Concepto, Debe, Haber)
  SELECT t.CompanyId, @Poliza, @Nivel1, @Nivel2, @Nivel3,
         LEFT(ISNULL(@Descripcion, 'INGRESO POR ARRENDAMIENTO'), 200),
         'CONTRAPARTIDA DEL SALDO INICIAL DE ENERO 2024',
         0, @Importe
  FROM dbo.Transacciones t WHERE t.ID = @Poliza;

  DECLARE @CargosFinal decimal(19,2), @AbonosFinal decimal(19,2);
  SELECT @CargosFinal = ISNULL(SUM(r.Debe), 0), @AbonosFinal = ISNULL(SUM(r.Haber), 0)
  FROM dbo.Registro_Contable r WHERE r.TransaccionID = @Poliza;
  IF ABS(@CargosFinal - @AbonosFinal) > 0.005
    THROW 52093, 'La poliza no quedo cuadrada.', 1;

  /* Ninguna otra poliza se toco, y esta sigue fuera del ciclo. */
  IF (SELECT COUNT(*) FROM dbo.Registro_Contable WHERE TransaccionID = @Poliza) <> 2
    THROW 52094, 'Se esperaban exactamente dos renglones tras la correccion.', 1;
  IF EXISTS (SELECT 1 FROM dbo.Transacciones WHERE ID = @Poliza AND CycleState IS NOT NULL)
    THROW 52095, 'Esta correccion no mete la poliza al ciclo.', 1;

  SELECT r.Nivel1, r.Nivel2, r.Nivel3, r.Nombre_Cuenta,
         CONVERT(decimal(19,2), r.Debe) AS Debe, CONVERT(decimal(19,2), r.Haber) AS Haber
  FROM dbo.Registro_Contable r WHERE r.TransaccionID = @Poliza ORDER BY r.id;

  IF @ApplyChanges = 1
  BEGIN
    INSERT orion.SchemaMigration (MigrationId, Checksum, AppliedBy, AppVersion, DatabaseName)
      VALUES (@MigrationId, @MigrationChecksum, @Actor, @AppVersion, DB_NAME());
    COMMIT TRANSACTION;
    SELECT N'APLICADO_CORTE_20260909' AS Estado, DB_NAME() AS BaseDatos, @MigrationId AS MigrationId;
  END
  ELSE
  BEGIN
    ROLLBACK TRANSACTION;
    SELECT N'VALIDADO_SIN_CAMBIOS' AS Estado, DB_NAME() AS BaseDatos, @MigrationId AS MigrationId;
  END;
END TRY
BEGIN CATCH
  IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
  THROW;
END CATCH;
