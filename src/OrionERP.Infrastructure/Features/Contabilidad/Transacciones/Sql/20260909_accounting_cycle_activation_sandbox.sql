/*
  Encendido del ciclo contable para el piloto, y mapping de Hospedaje, exclusivamente
  en Sandbox.

  Estas son las dos decisiones que el usuario tomó el 2026-09-09, y van por migración
  para que queden con ledger y checksum en vez de como escritura suelta:

  1. Ciclo encendido SÓLO para BRUNOS260707L26, con corte 2026-09-09. Todo lo anterior
     a esa fecha queda en modo compatible y sigue operando como hoy; el ciclo gobierna
     de esa fecha en adelante. Ninguna otra empresa se activa.

  2. Mapping de Bonhomía Suites, la única sede con Hospedaje habilitado:
     LeaseIncomeAccount = 401.25.02 (INGRESOS POR ARRENDAMIENTO BONHOMIA) y
     LeaseReceivableAccount = 205.01.01 (OTROS DEUDORES DIVERSOS). Las eligió el
     usuario sobre su propio catálogo; aquí no se infiere ninguna cuenta por texto.

  ATENCIÓN, lo que esto NO hace: dbo.CreateTransaccionesForRoom sigue lanzando 51823.
  El mapping es el dato que faltaba, pero el camino de código que lo consume a través
  del contrato durable todavía no está escrito. IsEnabled = 1 significa que el mapping
  está completo y aprobado, no que la contabilización de Hospedaje ya funcione.

  No se cierra ningún periodo ni se publica ninguna póliza histórica.
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
    THROW 52060, 'ApplyChanges debe ser 0 o 1.', 1;
  SET @ApplyChanges = CONVERT(bit, @ApplyChangesInput);
END;

IF @ExpectedDatabase LIKE N'$' + N'(%' OR @ExpectedDatabase <> N'Orion_Sandbox'
  THROW 52061, 'Esta migracion admite exclusivamente Orion_Sandbox.', 1;
IF DB_NAME() <> N'Orion_Sandbox'
  THROW 52062, 'La conexion no apunta a Orion_Sandbox.', 1;
IF @MigrationId LIKE N'$' + N'(%'
   OR NULLIF(LTRIM(RTRIM(@MigrationId)), N'') IS NULL
   OR LEN(@MigrationId) > 200
  THROW 52063, 'MigrationId es obligatorio y debe provenir del manifiesto.', 1;
IF @MigrationChecksum LIKE '$' + '(%'
   OR LEN(@MigrationChecksum) <> 64
   OR @MigrationChecksum LIKE '%[^0-9A-F]%' COLLATE Latin1_General_100_BIN2
  THROW 52064, 'MigrationChecksum debe ser un SHA-256 hexadecimal de 64 caracteres.', 1;
IF @AppVersionInput NOT LIKE N'$' + N'(%'
  SET @AppVersion = NULLIF(LTRIM(RTRIM(@AppVersionInput)), N'');

DECLARE @PilotoRfc varchar(50) = 'BRUNOS260707L26';
DECLARE @HospedajeRfc varchar(50) = 'OHM191112Q26';
DECLARE @HospedajeSiteKey varchar(100) = 'bonhomia-suites';
DECLARE @Corte datetime2(0) = CONVERT(datetime2(0), '2026-09-09T00:00:00');
DECLARE @CuentaIngreso varchar(100) = '401.25.02';
DECLARE @CuentaPorCobrar varchar(100) = '205.01.01';
DECLARE @Actor nvarchar(256) =
  COALESCE(CONVERT(nvarchar(256), SESSION_CONTEXT(N'OrionERP.UserName')), CONVERT(nvarchar(256), ORIGINAL_LOGIN()));

IF OBJECT_ID(N'contabilidad.CompanyCycleActivation', N'U') IS NULL
   OR OBJECT_ID(N'contabilidad.HospitalityAccountingMapping', N'U') IS NULL
  THROW 52065, 'Faltan las tablas del ciclo o del mapping de Hospedaje.', 1;
IF NOT EXISTS (SELECT 1 FROM orion.SchemaMigration WHERE MigrationId = N'20260908_accounting_outbox_sandbox')
  THROW 52066, 'Esta activacion depende de la bandeja contable durable.', 1;

DECLARE @PilotoCompanyId bigint =
  (SELECT CompanyId FROM orion.Company WHERE Rfc = @PilotoRfc AND IsActive = 1);
IF @PilotoCompanyId IS NULL
  THROW 52067, 'La empresa piloto no existe o esta inactiva.', 1;

DECLARE @HospedajeCompanyId bigint, @HospedajeSiteId bigint;
SELECT @HospedajeCompanyId = s.CompanyId, @HospedajeSiteId = s.SiteId
FROM orion.Site s
JOIN orion.Company c ON c.CompanyId = s.CompanyId
WHERE c.Rfc = @HospedajeRfc AND c.IsActive = 1 AND s.IsActive = 1 AND s.SiteKey = @HospedajeSiteKey;
IF @HospedajeCompanyId IS NULL OR @HospedajeSiteId IS NULL
  THROW 52068, 'La sede de Hospedaje del mapping no existe o esta inactiva.', 1;

DECLARE @ExistingChecksum char(64) =
(
  SELECT Checksum FROM orion.SchemaMigration WHERE MigrationId = @MigrationId
);
IF @ExistingChecksum IS NOT NULL
   AND UPPER(@ExistingChecksum) <> UPPER(@MigrationChecksum)
  THROW 52069, 'El mismo MigrationId ya existe con otro checksum.', 1;
IF @ExistingChecksum IS NOT NULL
BEGIN
  SELECT N'YA_APLICADO_SOLO_SANDBOX' AS Estado, @MigrationId AS MigrationId;
  RETURN;
END;

/* Una activacion o un mapping preexistentes se reconcilian a mano. */
IF EXISTS (SELECT 1 FROM contabilidad.CompanyCycleActivation)
   OR EXISTS (SELECT 1 FROM contabilidad.HospitalityAccountingMapping)
  THROW 52070, 'Ya hay activaciones o mappings sin registrar; requiere reconciliacion explicita.', 1;

PRINT 'Estado previo del piloto: nada de esto entra al ciclo por aplicar la migracion.';
SELECT
  @PilotoRfc AS Piloto,
  CONVERT(bigint, COUNT_BIG(*)) AS PolizasTotal,
  CONVERT(bigint, SUM(CASE WHEN t.Fecha < @Corte THEN 1 ELSE 0 END)) AS QuedanEnModoCompatible,
  CONVERT(bigint, SUM(CASE WHEN t.Fecha >= @Corte THEN 1 ELSE 0 END)) AS EntranAlCiclo,
  CONVERT(bigint, SUM(CASE WHEN t.CycleState IS NOT NULL THEN 1 ELSE 0 END)) AS YaEnElCiclo
FROM dbo.Transacciones t
WHERE t.CompanyId = @PilotoCompanyId;

BEGIN TRY
  BEGIN TRANSACTION;
  DECLARE @LockResult int;
  EXEC @LockResult = sys.sp_getapplock
    @Resource = N'OrionERP:Accounting:CycleActivation:Sandbox',
    @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 15000;
  IF @LockResult < 0
    THROW 52071, 'No se obtuvo el candado de la activacion.', 1;

  INSERT contabilidad.CompanyCycleActivation
    (CompanyId, IsEnabled, LegacyCompatibleUntilUtc, ActivatedAtUtc, ActivatedBy, BaselineNote)
  VALUES
    (@PilotoCompanyId, 1, @Corte, SYSUTCDATETIME(), @Actor,
     N'Piloto aprobado por el usuario el 2026-09-09. Cero polizas descuadradas en el baseline. Corte 2026-09-09: la historia previa sigue en modo compatible.');

  INSERT contabilidad.HospitalityAccountingMapping
    (CompanyId, SiteId, LeaseIncomeAccount, LeaseReceivableAccount, IsEnabled, ConfiguredAtUtc, ConfiguredBy)
  VALUES
    (@HospedajeCompanyId, @HospedajeSiteId, @CuentaIngreso, @CuentaPorCobrar, 1, SYSUTCDATETIME(), @Actor);

  /* Sigue siendo una sola empresa, y ninguna poliza historica entro al ciclo. */
  IF (SELECT COUNT_BIG(*) FROM contabilidad.CompanyCycleActivation WHERE IsEnabled = 1) <> 1
    THROW 52072, 'Debe quedar exactamente una empresa con el ciclo encendido.', 1;
  IF EXISTS (SELECT 1 FROM dbo.Transacciones WHERE CycleState IS NOT NULL)
    THROW 52073, 'Ninguna poliza historica debe entrar al ciclo por esta activacion.', 1;
  IF (SELECT COUNT_BIG(*) FROM contabilidad.AccountingPeriod WHERE [State] = 'Closed') <> 0
    THROW 52074, 'Esta migracion no cierra periodos.', 1;

  SELECT
    c.Rfc AS EmpresaConCiclo,
    a.LegacyCompatibleUntilUtc AS Corte,
    CONVERT(bigint, (SELECT COUNT_BIG(*) FROM dbo.Transacciones WHERE CompanyId = a.CompanyId AND Fecha < a.LegacyCompatibleUntilUtc)) AS EnModoCompatible,
    CONVERT(bigint, (SELECT COUNT_BIG(*) FROM dbo.Transacciones WHERE CompanyId = a.CompanyId AND Fecha >= a.LegacyCompatibleUntilUtc)) AS BajoElCiclo
  FROM contabilidad.CompanyCycleActivation a
  JOIN orion.Company c ON c.CompanyId = a.CompanyId;

  SELECT
    c.Rfc AS EmpresaHospedaje, s.SiteKey,
    m.LeaseIncomeAccount, m.LeaseReceivableAccount, m.IsEnabled
  FROM contabilidad.HospitalityAccountingMapping m
  JOIN orion.Company c ON c.CompanyId = m.CompanyId
  JOIN orion.Site s ON s.CompanyId = m.CompanyId AND s.SiteId = m.SiteId;

  IF @ApplyChanges = 1
  BEGIN
    INSERT orion.SchemaMigration (MigrationId, Checksum, AppliedBy, AppVersion, DatabaseName)
      VALUES (@MigrationId, @MigrationChecksum, @Actor, @AppVersion, DB_NAME());
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
