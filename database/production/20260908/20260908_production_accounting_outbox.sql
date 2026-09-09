/*
  Corte productivo 2026-09-08: 20260908_production_accounting_outbox.
  Semántica revisada desde 20260908_accounting_outbox_sandbox; los bytes de aquella no
  se tocan. Producción exige respaldo autorizado aparte, preview revisado, y sólo
  entonces apply.

  contabilidad.AccountingOutbox es la bandeja propia del contrato contable y no
  interfiere con restaurante.EventOutbox, que alimenta SignalR.

  Las cuatro piezas del contrato: identidad única de operación garantizada por índice
  único, registro transaccional del evento, consumo idempotente con reclamo explícito, y
  el id de la póliza grabado en cuanto existe, que es lo que convierte un fallo entre
  crearla y vincularla en algo recuperable.

  contabilidad.HospitalityAccountingMapping nace VACÍA a propósito: Hospedaje se entrega
  apagada porque faltan los mappings empresariales. No se infiere ninguna cuenta ni
  categoría por texto, y dbo.CreateTransaccionesForRoom sigue bloqueado.
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
    THROW 52000, 'ApplyChanges debe ser 0 o 1.', 1;
  SET @ApplyChanges = CONVERT(bit, @ApplyChangesInput);
END;

IF @ExpectedDatabase LIKE N'$' + N'(%'
   OR @ExpectedDatabase NOT IN (N'grupocarpio', N'Orion_CutoverValidation_20260908')
  THROW 52050, 'Esta migracion admite grupocarpio o el ensayo aislado Orion_CutoverValidation_20260908.', 1;
IF DB_NAME() <> @ExpectedDatabase
  THROW 52051, 'La conexion no coincide con ExpectedDatabase.', 1;
IF @MigrationId LIKE N'$' + N'(%'
   OR NULLIF(LTRIM(RTRIM(@MigrationId)), N'') IS NULL
   OR LEN(@MigrationId) > 200
  THROW 52003, 'MigrationId es obligatorio y debe provenir del manifiesto.', 1;
IF @MigrationChecksum LIKE '$' + '(%'
   OR LEN(@MigrationChecksum) <> 64
   OR @MigrationChecksum LIKE '%[^0-9A-F]%' COLLATE Latin1_General_100_BIN2
  THROW 52004, 'MigrationChecksum debe ser un SHA-256 hexadecimal de 64 caracteres.', 1;
IF @AppVersionInput NOT LIKE N'$' + N'(%'
  SET @AppVersion = NULLIF(LTRIM(RTRIM(@AppVersionInput)), N'');

IF OBJECT_ID(N'orion.SchemaMigration', N'U') IS NULL
   OR OBJECT_ID(N'dbo.Transacciones', N'U') IS NULL
   OR OBJECT_ID(N'restaurante.AccountingLink', N'U') IS NULL
  THROW 52005, 'Falta la fundacion de plataforma o el agregado contable.', 1;
IF NOT EXISTS (SELECT 1 FROM orion.SchemaMigration WHERE MigrationId = N'20260908_production_accounting_cycle')
  THROW 52006, 'La bandeja durable depende del ciclo contable formal.', 1;

DECLARE @ExistingChecksum char(64) =
(
  SELECT Checksum FROM orion.SchemaMigration WHERE MigrationId = @MigrationId
);
IF @ExistingChecksum IS NOT NULL
   AND UPPER(@ExistingChecksum) <> UPPER(@MigrationChecksum)
  THROW 52007, 'El mismo MigrationId ya existe con otro checksum.', 1;
IF @ExistingChecksum IS NOT NULL
BEGIN
  IF OBJECT_ID(N'contabilidad.AccountingOutbox', N'U') IS NULL
     OR OBJECT_ID(N'contabilidad.HospitalityAccountingMapping', N'U') IS NULL
    THROW 52008, 'La migracion esta registrada pero falta alguna pieza.', 1;
  SELECT N'YA_APLICADO_CORTE_20260908' AS Estado, @MigrationId AS MigrationId;
  RETURN;
END;

IF OBJECT_ID(N'contabilidad.AccountingOutbox', N'U') IS NOT NULL
   OR OBJECT_ID(N'contabilidad.HospitalityAccountingMapping', N'U') IS NOT NULL
  THROW 52009, 'Ya existe una pieza de la bandeja sin registrar; requiere reconciliacion explicita.', 1;

BEGIN TRY
  BEGIN TRANSACTION;
  DECLARE @LockResult int;
  EXEC @LockResult = sys.sp_getapplock
    @Resource = N'OrionERP:Accounting:Outbox:Production',
    @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 15000;
  IF @LockResult < 0
    THROW 52010, 'No se obtuvo el candado de la migracion de la bandeja.', 1;

  CREATE TABLE contabilidad.AccountingOutbox
  (
    Id bigint IDENTITY(1,1) NOT NULL
      CONSTRAINT PK_contabilidad_AccountingOutbox PRIMARY KEY,
    CompanyId bigint NOT NULL,
    Rfc varchar(50) NOT NULL,
    SourceModule varchar(30) NOT NULL,
    OperationKey varchar(200) NOT NULL,
    Payload nvarchar(max) NOT NULL,
    [Status] varchar(20) NOT NULL
      CONSTRAINT DF_AccountingOutbox_Status DEFAULT ('Pending'),
    TransaccionId int NULL,
    Attempts int NOT NULL CONSTRAINT DF_AccountingOutbox_Attempts DEFAULT (0),
    ClaimedAtUtc datetime2(3) NULL,
    ClaimedBy nvarchar(256) NULL,
    LinkedAtUtc datetime2(3) NULL,
    CompletedAtUtc datetime2(3) NULL,
    LastError nvarchar(2000) NULL,
    CreatedAtUtc datetime2(3) NOT NULL
      CONSTRAINT DF_AccountingOutbox_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
    UpdatedAtUtc datetime2(3) NOT NULL
      CONSTRAINT DF_AccountingOutbox_UpdatedAtUtc DEFAULT (SYSUTCDATETIME()),
    RowVersion rowversion NOT NULL,
    CONSTRAINT FK_AccountingOutbox_Company FOREIGN KEY (CompanyId)
      REFERENCES orion.Company (CompanyId),
    CONSTRAINT FK_AccountingOutbox_Transaccion FOREIGN KEY (TransaccionId)
      REFERENCES dbo.Transacciones (ID),
    CONSTRAINT CK_AccountingOutbox_Status CHECK
      ([Status] IN ('Pending', 'Claimed', 'Completed', 'Failed')),
    CONSTRAINT CK_AccountingOutbox_Json CHECK (ISJSON(Payload) = 1),
    CONSTRAINT CK_AccountingOutbox_Completed CHECK
      ([Status] <> 'Completed' OR (TransaccionId IS NOT NULL AND LinkedAtUtc IS NOT NULL))
  );

  /* La identidad única de operación. Un reintento concurrente choca aquí. */
  CREATE UNIQUE INDEX UX_AccountingOutbox_Operation
    ON contabilidad.AccountingOutbox (CompanyId, SourceModule, OperationKey);
  CREATE INDEX IX_AccountingOutbox_Pending
    ON contabilidad.AccountingOutbox ([Status], Id) INCLUDE (CompanyId, SourceModule, OperationKey);

  /* Mappings de Hospedaje por empresa y sede. Nace vacía: sin fila, el flujo de
     Hospedaje está apagado y dbo.CreateTransaccionesForRoom sigue bloqueado. Las
     cuentas y categorías las declara el usuario; no se infieren por nombre. */
  CREATE TABLE contabilidad.HospitalityAccountingMapping
  (
    CompanyId bigint NOT NULL,
    SiteId bigint NOT NULL,
    LeaseIncomeAccount varchar(100) NOT NULL,
    LeaseReceivableAccount varchar(100) NOT NULL,
    VatPayableAccount varchar(100) NULL,
    CategoryId int NULL,
    BankAccount varchar(100) NULL,
    IsEnabled bit NOT NULL
      CONSTRAINT DF_HospitalityAccountingMapping_IsEnabled DEFAULT (0),
    ConfiguredAtUtc datetime2(0) NULL,
    ConfiguredBy nvarchar(256) NULL,
    CreatedAtUtc datetime2(0) NOT NULL
      CONSTRAINT DF_HospitalityAccountingMapping_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
    UpdatedAtUtc datetime2(0) NOT NULL
      CONSTRAINT DF_HospitalityAccountingMapping_UpdatedAtUtc DEFAULT (SYSUTCDATETIME()),
    RowVersion rowversion NOT NULL,
    CONSTRAINT PK_contabilidad_HospitalityAccountingMapping PRIMARY KEY (CompanyId, SiteId),
    CONSTRAINT FK_HospitalityAccountingMapping_Site FOREIGN KEY (CompanyId, SiteId)
      REFERENCES orion.Site (CompanyId, SiteId),
    CONSTRAINT CK_HospitalityAccountingMapping_Enabled CHECK
      (IsEnabled = 0 OR (ConfiguredAtUtc IS NOT NULL
                         AND NULLIF(LTRIM(RTRIM(LeaseIncomeAccount)), '') IS NOT NULL
                         AND NULLIF(LTRIM(RTRIM(LeaseReceivableAccount)), '') IS NOT NULL))
  );

  /* Comprobación de que se entrega apagada y sin contabilizar nada. */
  DECLARE @Mappings bigint = (SELECT COUNT_BIG(*) FROM contabilidad.HospitalityAccountingMapping);
  DECLARE @Queued bigint = (SELECT COUNT_BIG(*) FROM contabilidad.AccountingOutbox);
  IF @Mappings <> 0 OR @Queued <> 0
    THROW 52011, 'La bandeja y los mappings deben entregarse vacios.', 1;

  SELECT
    @Queued AS OperacionesEnBandeja,
    @Mappings AS MappingsDeHospedaje,
    CONVERT(bit, CASE WHEN EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_AccountingOutbox_Operation') THEN 1 ELSE 0 END) AS IdentidadUnicaDeOperacion,
    CONVERT(bigint, (SELECT COUNT_BIG(*) FROM restaurante.AccountingLink)) AS VinculosExistentes;

  IF @ApplyChanges = 1
  BEGIN
    INSERT orion.SchemaMigration (MigrationId, Checksum, AppliedBy, AppVersion, DatabaseName)
      VALUES (@MigrationId, @MigrationChecksum,
              COALESCE(CONVERT(nvarchar(256), SESSION_CONTEXT(N'OrionERP.UserName')), CONVERT(nvarchar(256), ORIGINAL_LOGIN())),
              @AppVersion, DB_NAME());
    COMMIT TRANSACTION;
    SELECT N'APLICADO_CORTE_20260908' AS Estado, DB_NAME() AS BaseDatos, @MigrationId AS MigrationId;
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
