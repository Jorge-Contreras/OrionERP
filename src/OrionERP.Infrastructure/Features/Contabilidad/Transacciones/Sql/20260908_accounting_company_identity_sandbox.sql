/*
  Identidad técnica de empresa en el agregado contable, exclusivamente en Sandbox.

  Aditiva y reversible en lectura: agrega CompanyId NULLABLE a dbo.Transacciones y
  dbo.Registro_Contable, y lo rellena únicamente por el vínculo legacy exacto con
  orion.Company (Rfc es su clave primaria, así que la coincidencia no es ambigua).
  No impone NOT NULL, no crea RLS y no crea índices: eso es E8, y sólo después de
  acreditar a los escritores. Los consumidores heredados siguen leyendo por RFC.

  El DEFAULT toma la empresa del contexto de sesión que fija AccountingConnectionFactory,
  así que las filas nuevas nacen identificadas sin cambiar a ningún escritor. Sin
  contexto queda NULL, igual que hoy.

  Los triggers de auditoría contable se suspenden durante el relleno y se verifican
  restaurados antes del commit: un identificador técnico no es un cambio de negocio y
  no debe sepultar la bitácora. DISABLE TRIGGER toma Sch-M sobre la tabla, así que
  ninguna escritura ajena se cuela sin auditar mientras dura: quedan bloqueadas.
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
    THROW 51960, 'ApplyChanges debe ser 0 o 1.', 1;
  SET @ApplyChanges = CONVERT(bit, @ApplyChangesInput);
END;

IF @ExpectedDatabase LIKE N'$' + N'(%' OR @ExpectedDatabase <> N'Orion_Sandbox'
  THROW 51961, 'Esta migracion admite exclusivamente Orion_Sandbox.', 1;
IF DB_NAME() <> N'Orion_Sandbox'
  THROW 51962, 'La conexion no apunta a Orion_Sandbox.', 1;
IF @MigrationId LIKE N'$' + N'(%'
   OR NULLIF(LTRIM(RTRIM(@MigrationId)), N'') IS NULL
   OR LEN(@MigrationId) > 200
  THROW 51963, 'MigrationId es obligatorio y debe provenir del manifiesto.', 1;
IF @MigrationChecksum LIKE '$' + '(%'
   OR LEN(@MigrationChecksum) <> 64
   OR @MigrationChecksum LIKE '%[^0-9A-F]%' COLLATE Latin1_General_100_BIN2
  THROW 51964, 'MigrationChecksum debe ser un SHA-256 hexadecimal de 64 caracteres.', 1;
IF @AppVersionInput NOT LIKE N'$' + N'(%'
  SET @AppVersion = NULLIF(LTRIM(RTRIM(@AppVersionInput)), N'');

IF OBJECT_ID(N'orion.SchemaMigration', N'U') IS NULL
   OR OBJECT_ID(N'orion.Company', N'U') IS NULL
   OR OBJECT_ID(N'dbo.Transacciones', N'U') IS NULL
   OR OBJECT_ID(N'dbo.Registro_Contable', N'U') IS NULL
  THROW 51965, 'Falta la fundacion de plataforma o el agregado contable.', 1;
IF NOT EXISTS
(
  SELECT 1 FROM orion.SchemaMigration WHERE MigrationId = N'20260901_platform_foundation'
)
  THROW 51966, 'La fundacion de plataforma no esta registrada en el ledger.', 1;

DECLARE @ExistingChecksum char(64) =
(
  SELECT Checksum FROM orion.SchemaMigration WHERE MigrationId = @MigrationId
);
IF @ExistingChecksum IS NOT NULL
   AND UPPER(@ExistingChecksum) <> UPPER(@MigrationChecksum)
  THROW 51967, 'El mismo MigrationId ya existe con otro checksum.', 1;
IF @ExistingChecksum IS NOT NULL
BEGIN
  IF COL_LENGTH(N'dbo.Transacciones', N'CompanyId') IS NULL
     OR COL_LENGTH(N'dbo.Registro_Contable', N'CompanyId') IS NULL
    THROW 51968, 'La migracion esta registrada pero falta alguna columna.', 1;
  SELECT N'YA_APLICADO_SOLO_SANDBOX' AS Estado, @MigrationId AS MigrationId;
  RETURN;
END;

/* Una columna preexistente no registrada se reconcilia a mano, no se adopta. */
IF COL_LENGTH(N'dbo.Transacciones', N'CompanyId') IS NOT NULL
   OR COL_LENGTH(N'dbo.Registro_Contable', N'CompanyId') IS NOT NULL
  THROW 51969, 'Ya existe CompanyId sin registrar; requiere reconciliacion explicita.', 1;

/* Un predicado de seguridad sobre estas tablas escondería filas del relleno y lo
   dejaría incompleto sin error. Es exactamente el modo en que E8 puede fallar. */
IF EXISTS
(
  SELECT 1 FROM sys.security_predicates
  WHERE target_object_id IN (OBJECT_ID(N'dbo.Transacciones'), OBJECT_ID(N'dbo.Registro_Contable'))
)
  THROW 51970, 'El agregado contable ya tiene predicados de seguridad; el relleno seria parcial.', 1;

IF OBJECT_ID(N'dbo.trg_Transacciones_Audit', N'TR') IS NULL
   OR OBJECT_ID(N'dbo.Registro_Contable', N'U') IS NULL
   OR OBJECT_ID(N'dbo.trg_Registro_Contable_Audit', N'TR') IS NULL
  THROW 51971, 'Faltan los triggers de auditoria contable que esta migracion debe restaurar.', 1;
IF EXISTS
(
  SELECT 1 FROM sys.triggers
  WHERE object_id IN (OBJECT_ID(N'dbo.trg_Transacciones_Audit'), OBJECT_ID(N'dbo.trg_Registro_Contable_Audit'))
    AND is_disabled = 1
)
  THROW 51972, 'Un trigger de auditoria contable ya estaba deshabilitado; no se enmascara ese estado.', 1;

PRINT 'Estado previo: filas por empresa resoluble y no resoluble.';
SELECT
  CONVERT(bigint, COUNT_BIG(*)) AS TransaccionesTotal,
  CONVERT(bigint, SUM(CASE WHEN companyInfo.CompanyId IS NULL THEN 1 ELSE 0 END)) AS TransaccionesSinEmpresa,
  CONVERT(int, COUNT(DISTINCT transaccion.RFC)) AS RfcDistintos
FROM dbo.Transacciones AS transaccion
LEFT JOIN orion.Company AS companyInfo
  ON companyInfo.Rfc = transaccion.RFC AND companyInfo.IsActive = 1;

SELECT TOP (50) transaccion.RFC AS RfcSinEmpresaActiva, CONVERT(bigint, COUNT_BIG(*)) AS Polizas
FROM dbo.Transacciones AS transaccion
WHERE NOT EXISTS
(
  SELECT 1 FROM orion.Company AS companyInfo
  WHERE companyInfo.Rfc = transaccion.RFC AND companyInfo.IsActive = 1
)
GROUP BY transaccion.RFC
ORDER BY COUNT_BIG(*) DESC;

BEGIN TRY
  BEGIN TRANSACTION;
  DECLARE @LockResult int;
  EXEC @LockResult = sys.sp_getapplock
    @Resource = N'OrionERP:Accounting:CompanyIdentity:Sandbox',
    @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 15000;
  IF @LockResult < 0
    THROW 51973, 'No se obtuvo el candado de la migracion contable.', 1;

  ALTER TABLE dbo.Transacciones
    ADD CompanyId bigint NULL
      CONSTRAINT DF_Transacciones_CompanyId
      DEFAULT (TRY_CONVERT(bigint, SESSION_CONTEXT(N'OrionERP.CompanyId')));
  ALTER TABLE dbo.Registro_Contable
    ADD CompanyId bigint NULL
      CONSTRAINT DF_Registro_Contable_CompanyId
      DEFAULT (TRY_CONVERT(bigint, SESSION_CONTEXT(N'OrionERP.CompanyId')));

  ALTER TABLE dbo.Transacciones DISABLE TRIGGER trg_Transacciones_Audit;
  ALTER TABLE dbo.Registro_Contable DISABLE TRIGGER trg_Registro_Contable_Audit;

  /* Dinamico porque la columna nace en este mismo lote. Vinculo legacy exacto:
     orion.Company.Rfc es clave primaria, asi que no hay coincidencia ambigua ni se
     recurre a TaxRfc, que es el RFC fiscal y una cosa distinta. */
  DECLARE @BackfillHeaders nvarchar(max) = N'
    UPDATE transaccion
    SET CompanyId = companyInfo.CompanyId
    FROM dbo.Transacciones AS transaccion
    JOIN orion.Company AS companyInfo
      ON companyInfo.Rfc = transaccion.RFC AND companyInfo.IsActive = 1
    WHERE transaccion.CompanyId IS NULL;';
  EXEC sys.sp_executesql @BackfillHeaders;
  DECLARE @HeadersFilled bigint = @@ROWCOUNT;

  /* Un movimiento no tiene RFC propio: cuelga de su poliza, y esa es su empresa. */
  DECLARE @BackfillLines nvarchar(max) = N'
    UPDATE movimiento
    SET CompanyId = transaccion.CompanyId
    FROM dbo.Registro_Contable AS movimiento
    JOIN dbo.Transacciones AS transaccion ON transaccion.ID = movimiento.TransaccionID
    WHERE movimiento.CompanyId IS NULL AND transaccion.CompanyId IS NOT NULL;';
  EXEC sys.sp_executesql @BackfillLines;
  DECLARE @LinesFilled bigint = @@ROWCOUNT;

  ALTER TABLE dbo.Transacciones ENABLE TRIGGER trg_Transacciones_Audit;
  ALTER TABLE dbo.Registro_Contable ENABLE TRIGGER trg_Registro_Contable_Audit;
  IF EXISTS
  (
    SELECT 1 FROM sys.triggers
    WHERE object_id IN (OBJECT_ID(N'dbo.trg_Transacciones_Audit'), OBJECT_ID(N'dbo.trg_Registro_Contable_Audit'))
      AND is_disabled = 1
  )
    THROW 51974, 'La auditoria contable no quedo restaurada.', 1;

  /* Los casos que no se resolvieron quedan reportados, no adivinados: se quedan en
     NULL y siguen operando por RFC como hasta hoy. */
  DECLARE @Report nvarchar(max) = N'
    SELECT
      @HeadersFilledOut AS PolizasIdentificadas,
      @LinesFilledOut AS MovimientosIdentificados,
      CONVERT(bigint, (SELECT COUNT_BIG(*) FROM dbo.Transacciones WHERE CompanyId IS NULL)) AS PolizasSinEmpresa,
      CONVERT(bigint, (SELECT COUNT_BIG(*) FROM dbo.Registro_Contable WHERE CompanyId IS NULL)) AS MovimientosSinEmpresa,
      CONVERT(bigint, (SELECT COUNT_BIG(*) FROM dbo.Registro_Contable AS movimiento
                       JOIN dbo.Transacciones AS transaccion ON transaccion.ID = movimiento.TransaccionID
                       WHERE movimiento.CompanyId <> transaccion.CompanyId)) AS MovimientosDiscrepantes;';
  EXEC sys.sp_executesql @Report,
    N'@HeadersFilledOut bigint, @LinesFilledOut bigint',
    @HeadersFilledOut = @HeadersFilled, @LinesFilledOut = @LinesFilled;

  DECLARE @Mismatch bigint;
  DECLARE @MismatchSql nvarchar(max) = N'
    SELECT @Out = COUNT_BIG(*)
    FROM dbo.Registro_Contable AS movimiento
    JOIN dbo.Transacciones AS transaccion ON transaccion.ID = movimiento.TransaccionID
    WHERE movimiento.CompanyId <> transaccion.CompanyId;';
  EXEC sys.sp_executesql @MismatchSql, N'@Out bigint OUTPUT', @Out = @Mismatch OUTPUT;
  IF @Mismatch > 0
    THROW 51975, 'Un movimiento quedo con empresa distinta a la de su poliza.', 1;

  SELECT
    N'dbo.Transacciones' AS Tabla,
    CONVERT(bit, CASE WHEN COL_LENGTH(N'dbo.Transacciones', N'CompanyId') IS NULL THEN 0 ELSE 1 END) AS ColumnaAgregada
  UNION ALL
  SELECT
    N'dbo.Registro_Contable',
    CONVERT(bit, CASE WHEN COL_LENGTH(N'dbo.Registro_Contable', N'CompanyId') IS NULL THEN 0 ELSE 1 END);

  IF @ApplyChanges = 1
  BEGIN
    INSERT orion.SchemaMigration (MigrationId, Checksum, AppliedBy, AppVersion, DatabaseName)
      VALUES (@MigrationId, @MigrationChecksum,
              COALESCE(CONVERT(nvarchar(256), SESSION_CONTEXT(N'OrionERP.UserName')), CONVERT(nvarchar(256), ORIGINAL_LOGIN())),
              @AppVersion, DB_NAME());
    COMMIT TRANSACTION;
    SELECT N'APLICADO_SOLO_SANDBOX' AS Estado, DB_NAME() AS BaseDatos, @MigrationId AS MigrationId;
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
