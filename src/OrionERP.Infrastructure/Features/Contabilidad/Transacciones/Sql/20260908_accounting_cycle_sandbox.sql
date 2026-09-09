/*
  Ciclo contable formal, exclusivamente en Sandbox. Se entrega APAGADO.

  Crea la capacidad y no la enciende: contabilidad.CompanyCycleActivation nace vacía,
  así que ninguna empresa entra al ciclo hasta que el usuario apruebe su baseline.
  Mientras esté apagada, la operación existente no cambia en absoluto.

  Aditiva. El Estatus legacy se conserva tal cual y no se reinterpreta: CycleState
  NULL significa "esta póliza nunca entró al ciclo", que es el estado de toda la
  historia. No se cierra ningún periodo histórico ni se cambia su condición por
  defecto: la ausencia de fila en AccountingPeriod es periodo abierto.

  El cierre del libro mayor es distinto de fiscal.DeclaracionCierre, que es cierre
  declarativo de impuestos. Esta migración no lo toca.

  La inmutabilidad de Posted se impone también en SQL. Los triggers son inertes para
  toda la historia porque sólo actúan cuando la fila ya está Posted o Reversed, y eso
  sólo puede ocurrir por una publicación explícita del servicio.
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
    THROW 51980, 'ApplyChanges debe ser 0 o 1.', 1;
  SET @ApplyChanges = CONVERT(bit, @ApplyChangesInput);
END;

IF @ExpectedDatabase LIKE N'$' + N'(%' OR @ExpectedDatabase <> N'Orion_Sandbox'
  THROW 51981, 'Esta migracion admite exclusivamente Orion_Sandbox.', 1;
IF DB_NAME() <> N'Orion_Sandbox'
  THROW 51982, 'La conexion no apunta a Orion_Sandbox.', 1;
IF @MigrationId LIKE N'$' + N'(%'
   OR NULLIF(LTRIM(RTRIM(@MigrationId)), N'') IS NULL
   OR LEN(@MigrationId) > 200
  THROW 51983, 'MigrationId es obligatorio y debe provenir del manifiesto.', 1;
IF @MigrationChecksum LIKE '$' + '(%'
   OR LEN(@MigrationChecksum) <> 64
   OR @MigrationChecksum LIKE '%[^0-9A-F]%' COLLATE Latin1_General_100_BIN2
  THROW 51984, 'MigrationChecksum debe ser un SHA-256 hexadecimal de 64 caracteres.', 1;
IF @AppVersionInput NOT LIKE N'$' + N'(%'
  SET @AppVersion = NULLIF(LTRIM(RTRIM(@AppVersionInput)), N'');

IF OBJECT_ID(N'orion.SchemaMigration', N'U') IS NULL
   OR OBJECT_ID(N'dbo.Transacciones', N'U') IS NULL
   OR OBJECT_ID(N'dbo.Registro_Contable', N'U') IS NULL
  THROW 51985, 'Falta la fundacion de plataforma o el agregado contable.', 1;

/* Depende de E3: sin CompanyId no hay a que colgar periodos ni habilitacion. */
IF NOT EXISTS
(
  SELECT 1 FROM orion.SchemaMigration WHERE MigrationId = N'20260908_accounting_company_identity_sandbox'
)
  THROW 51986, 'Falta la identidad contable por empresa; el ciclo depende de ella.', 1;
IF COL_LENGTH(N'dbo.Transacciones', N'CompanyId') IS NULL
   OR COL_LENGTH(N'dbo.Registro_Contable', N'CompanyId') IS NULL
  THROW 51987, 'Falta la columna CompanyId del agregado contable.', 1;

DECLARE @ExistingChecksum char(64) =
(
  SELECT Checksum FROM orion.SchemaMigration WHERE MigrationId = @MigrationId
);
IF @ExistingChecksum IS NOT NULL
   AND UPPER(@ExistingChecksum) <> UPPER(@MigrationChecksum)
  THROW 51988, 'El mismo MigrationId ya existe con otro checksum.', 1;
IF @ExistingChecksum IS NOT NULL
BEGIN
  IF OBJECT_ID(N'contabilidad.AccountingPeriod', N'U') IS NULL
     OR OBJECT_ID(N'contabilidad.CompanyCycleActivation', N'U') IS NULL
     OR COL_LENGTH(N'dbo.Transacciones', N'CycleState') IS NULL
    THROW 51989, 'La migracion esta registrada pero falta alguna pieza del ciclo.', 1;
  SELECT N'YA_APLICADO_SOLO_SANDBOX' AS Estado, @MigrationId AS MigrationId;
  RETURN;
END;

/* Piezas preexistentes sin registrar se reconcilian a mano, no se adoptan. */
IF OBJECT_ID(N'contabilidad.AccountingPeriod', N'U') IS NOT NULL
   OR OBJECT_ID(N'contabilidad.CompanyCycleActivation', N'U') IS NOT NULL
   OR COL_LENGTH(N'dbo.Transacciones', N'CycleState') IS NOT NULL
   OR OBJECT_ID(N'dbo.TR_Transacciones_CycleImmutability', N'TR') IS NOT NULL
   OR OBJECT_ID(N'dbo.TR_Registro_Contable_CycleImmutability', N'TR') IS NOT NULL
  THROW 51990, 'Ya existe una pieza del ciclo sin registrar; requiere reconciliacion explicita.', 1;

IF SCHEMA_ID(N'contabilidad') IS NULL
  THROW 51991, 'Falta el esquema contabilidad.', 1;

PRINT 'Estado previo del agregado: nada de esto cambia al aplicar.';
SELECT
  CONVERT(bigint, COUNT_BIG(*)) AS PolizasTotal,
  CONVERT(bigint, COUNT(DISTINCT CompanyId)) AS EmpresasConPolizas,
  CONVERT(int, COUNT(DISTINCT Estatus)) AS EstatusLegacyDistintos
FROM dbo.Transacciones;

BEGIN TRY
  BEGIN TRANSACTION;
  DECLARE @LockResult int;
  EXEC @LockResult = sys.sp_getapplock
    @Resource = N'OrionERP:Accounting:Cycle:Sandbox',
    @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 15000;
  IF @LockResult < 0
    THROW 51992, 'No se obtuvo el candado de la migracion del ciclo.', 1;

  /* Habilitacion por empresa. Nace vacia: sin fila, la empresa esta fuera del ciclo.
     LegacyCompatibleUntilUtc delimita la historia todavia no conciliada: las polizas
     anteriores a ese instante siguen operando como hoy aunque el ciclo este encendido. */
  CREATE TABLE contabilidad.CompanyCycleActivation
  (
    CompanyId bigint NOT NULL
      CONSTRAINT PK_contabilidad_CompanyCycleActivation PRIMARY KEY,
    IsEnabled bit NOT NULL
      CONSTRAINT DF_CompanyCycleActivation_IsEnabled DEFAULT (0),
    LegacyCompatibleUntilUtc datetime2(0) NULL,
    ActivatedAtUtc datetime2(0) NULL,
    ActivatedBy nvarchar(256) NULL,
    BaselineNote nvarchar(1000) NULL,
    CreatedAtUtc datetime2(0) NOT NULL
      CONSTRAINT DF_CompanyCycleActivation_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
    UpdatedAtUtc datetime2(0) NOT NULL
      CONSTRAINT DF_CompanyCycleActivation_UpdatedAtUtc DEFAULT (SYSUTCDATETIME()),
    RowVersion rowversion NOT NULL,
    CONSTRAINT FK_CompanyCycleActivation_Company FOREIGN KEY (CompanyId)
      REFERENCES orion.Company (CompanyId),
    CONSTRAINT CK_CompanyCycleActivation_Enabled CHECK
      (IsEnabled = 0 OR ActivatedAtUtc IS NOT NULL)
  );

  /* Periodos del libro mayor por empresa. La ausencia de fila es periodo abierto, asi
     que aplicar esta migracion no cierra nada. */
  CREATE TABLE contabilidad.AccountingPeriod
  (
    CompanyId bigint NOT NULL,
    PeriodYear int NOT NULL,
    PeriodMonth int NOT NULL,
    [State] varchar(20) NOT NULL
      CONSTRAINT DF_AccountingPeriod_State DEFAULT ('Open'),
    ClosedAtUtc datetime2(0) NULL,
    ClosedBy nvarchar(256) NULL,
    ClosingNote nvarchar(1000) NULL,
    CreatedAtUtc datetime2(0) NOT NULL
      CONSTRAINT DF_AccountingPeriod_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
    UpdatedAtUtc datetime2(0) NOT NULL
      CONSTRAINT DF_AccountingPeriod_UpdatedAtUtc DEFAULT (SYSUTCDATETIME()),
    RowVersion rowversion NOT NULL,
    CONSTRAINT PK_contabilidad_AccountingPeriod PRIMARY KEY (CompanyId, PeriodYear, PeriodMonth),
    CONSTRAINT FK_AccountingPeriod_Company FOREIGN KEY (CompanyId)
      REFERENCES orion.Company (CompanyId),
    CONSTRAINT CK_AccountingPeriod_State CHECK ([State] IN ('Open', 'Closed')),
    CONSTRAINT CK_AccountingPeriod_Year CHECK (PeriodYear BETWEEN 1990 AND 2999),
    CONSTRAINT CK_AccountingPeriod_Month CHECK (PeriodMonth BETWEEN 1 AND 12),
    CONSTRAINT CK_AccountingPeriod_Closed CHECK
      ([State] = 'Open' OR ClosedAtUtc IS NOT NULL)
  );

  /* Columnas del ciclo en el agregado. Todas nullables: la historia queda con
     CycleState NULL, fuera del ciclo, y su Estatus legacy intacto. */
  ALTER TABLE dbo.Transacciones ADD
    CycleState varchar(20) NULL,
    PostedAtUtc datetime2(0) NULL,
    PostedBy nvarchar(256) NULL,
    ReversalOfTransaccionId int NULL,
    ReversalReason nvarchar(400) NULL,
    ReversedAtUtc datetime2(0) NULL,
    ReversedBy nvarchar(256) NULL;

  /* Dinamico porque las columnas nacen en este mismo lote. */
  EXEC(N'
  ALTER TABLE dbo.Transacciones WITH CHECK
    ADD CONSTRAINT CK_Transacciones_CycleState CHECK
      (CycleState IS NULL OR CycleState IN (''Draft'', ''Posted'', ''Reversed''));
  ALTER TABLE dbo.Transacciones WITH CHECK
    ADD CONSTRAINT CK_Transacciones_PostedRequiresStamp CHECK
      (CycleState <> ''Posted'' OR PostedAtUtc IS NOT NULL);
  ALTER TABLE dbo.Transacciones WITH CHECK
    ADD CONSTRAINT CK_Transacciones_ReversalRequiresReason CHECK
      (ReversalOfTransaccionId IS NULL OR NULLIF(LTRIM(RTRIM(ReversalReason)), N'''') IS NOT NULL);
  ALTER TABLE dbo.Transacciones WITH CHECK
    ADD CONSTRAINT CK_Transacciones_ReversalNotSelf CHECK
      (ReversalOfTransaccionId IS NULL OR ReversalOfTransaccionId <> ID);
  ALTER TABLE dbo.Transacciones WITH CHECK
    ADD CONSTRAINT FK_Transacciones_ReversalOf FOREIGN KEY (ReversalOfTransaccionId)
      REFERENCES dbo.Transacciones (ID);');

  /* Una poliza se reversa una sola vez, y lo garantiza el indice, no el servicio:
     dos reversas simultaneas no pueden ganar las dos. */
  EXEC(N'
  CREATE UNIQUE INDEX UX_Transacciones_ReversalOf
    ON dbo.Transacciones (ReversalOfTransaccionId)
    WHERE ReversalOfTransaccionId IS NOT NULL;');

  /* Inmutabilidad en SQL, no solo en el servicio. Inerte para toda la historia: solo
     actua cuando la fila anterior ya estaba Posted o Reversed. */
  EXEC(N'
  CREATE TRIGGER dbo.TR_Transacciones_CycleImmutability
  ON dbo.Transacciones
  AFTER UPDATE, DELETE
  AS
  BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM deleted WHERE CycleState IN (''Posted'', ''Reversed''))
      RETURN;

    IF EXISTS (SELECT 1 FROM deleted previousRow WHERE previousRow.CycleState IN (''Posted'', ''Reversed'')
               AND NOT EXISTS (SELECT 1 FROM inserted WHERE inserted.ID = previousRow.ID))
      THROW 51993, ''Una poliza publicada o reversada no se elimina; corrige por reversa.'', 1;

    /* Desde Posted solo se admite pasar a Reversed, y Reversed es terminal. */
    IF EXISTS
    (
      SELECT 1
      FROM deleted previousRow
      JOIN inserted currentRow ON currentRow.ID = previousRow.ID
      WHERE previousRow.CycleState IN (''Posted'', ''Reversed'')
        AND
        (
          currentRow.CycleState IS NULL
          OR (previousRow.CycleState = ''Posted'' AND currentRow.CycleState NOT IN (''Posted'', ''Reversed''))
          OR (previousRow.CycleState = ''Reversed'' AND currentRow.CycleState <> ''Reversed'')
        )
    )
      THROW 51994, ''Transicion de ciclo no permitida sobre una poliza publicada.'', 1;

    /* Los datos de negocio de una poliza publicada no se editan. La reversa escribe
       ReversedAtUtc, ReversedBy y CycleState, y nada mas. */
    IF EXISTS
    (
      SELECT 1
      FROM deleted previousRow
      JOIN inserted currentRow ON currentRow.ID = previousRow.ID
      WHERE previousRow.CycleState IN (''Posted'', ''Reversed'')
        AND
        (
          ISNULL(currentRow.Concepto, N'''') <> ISNULL(previousRow.Concepto, N'''')
          OR currentRow.Fecha <> previousRow.Fecha
          OR currentRow.Monto <> previousRow.Monto
          OR ISNULL(currentRow.Cuenta, N'''') <> ISNULL(previousRow.Cuenta, N'''')
          OR currentRow.RFC <> previousRow.RFC
          OR ISNULL(currentRow.CompanyId, CONVERT(bigint, -1)) <> ISNULL(previousRow.CompanyId, CONVERT(bigint, -1))
          OR currentRow.Tipo_Poliza <> previousRow.Tipo_Poliza
          OR currentRow.Forma_Pago <> previousRow.Forma_Pago
          OR ISNULL(currentRow.Facturado, CONVERT(bit, 0)) <> ISNULL(previousRow.Facturado, CONVERT(bit, 0))
          OR ISNULL(currentRow.Memo, N'''') <> ISNULL(previousRow.Memo, N'''')
          OR ISNULL(currentRow.Estatus, N'''') <> ISNULL(previousRow.Estatus, N'''')
          OR ISNULL(currentRow.ReversalOfTransaccionId, -1) <> ISNULL(previousRow.ReversalOfTransaccionId, -1)
          OR ISNULL(currentRow.PostedAtUtc, CONVERT(datetime2(0), ''19000101'')) <> ISNULL(previousRow.PostedAtUtc, CONVERT(datetime2(0), ''19000101''))
        )
    )
      THROW 51995, ''Una poliza publicada es inmutable; corrige por reversa.'', 1;
  END;');

  /* La via alterna que hay que cerrar: DeleteMovimientoAsync borraba movimientos de
     una poliza autorizada. Ahora la base lo impide, venga de donde venga. */
  EXEC(N'
  CREATE TRIGGER dbo.TR_Registro_Contable_CycleImmutability
  ON dbo.Registro_Contable
  AFTER INSERT, UPDATE, DELETE
  AS
  BEGIN
    SET NOCOUNT ON;
    IF EXISTS
    (
      SELECT 1
      FROM
      (
        SELECT TransaccionID FROM inserted
        UNION
        SELECT TransaccionID FROM deleted
      ) AS touched
      JOIN dbo.Transacciones AS poliza ON poliza.ID = touched.TransaccionID
      WHERE poliza.CycleState IN (''Posted'', ''Reversed'')
    )
      THROW 51996, ''Los movimientos de una poliza publicada no se editan ni se borran.'', 1;
  END;');

  /* Comprobacion de que se entrega apagado y que nada historico cambio. */
  DECLARE @Enabled bigint = (SELECT COUNT_BIG(*) FROM contabilidad.CompanyCycleActivation WHERE IsEnabled = 1);
  DECLARE @Closed bigint = (SELECT COUNT_BIG(*) FROM contabilidad.AccountingPeriod WHERE [State] = 'Closed');
  IF @Enabled <> 0 OR @Closed <> 0
    THROW 51997, 'El ciclo debe entregarse apagado y sin periodos cerrados.', 1;

  DECLARE @InCycle bigint;
  DECLARE @InCycleSql nvarchar(max) = N'SELECT @Out = COUNT_BIG(*) FROM dbo.Transacciones WHERE CycleState IS NOT NULL;';
  EXEC sys.sp_executesql @InCycleSql, N'@Out bigint OUTPUT', @Out = @InCycle OUTPUT;
  IF @InCycle <> 0
    THROW 51998, 'Ninguna poliza historica debe entrar al ciclo al aplicar la migracion.', 1;

  SELECT
    CONVERT(bigint, (SELECT COUNT_BIG(*) FROM contabilidad.CompanyCycleActivation)) AS EmpresasActivadas,
    CONVERT(bigint, (SELECT COUNT_BIG(*) FROM contabilidad.AccountingPeriod)) AS PeriodosDeclarados,
    @InCycle AS PolizasEnElCiclo,
    CONVERT(bit, CASE WHEN OBJECT_ID(N'dbo.TR_Transacciones_CycleImmutability', N'TR') IS NULL THEN 0 ELSE 1 END) AS TriggerCabecera,
    CONVERT(bit, CASE WHEN OBJECT_ID(N'dbo.TR_Registro_Contable_CycleImmutability', N'TR') IS NULL THEN 0 ELSE 1 END) AS TriggerMovimientos,
    CONVERT(bit, CASE WHEN EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_Transacciones_ReversalOf') THEN 1 ELSE 0 END) AS ReversaUnica;

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
