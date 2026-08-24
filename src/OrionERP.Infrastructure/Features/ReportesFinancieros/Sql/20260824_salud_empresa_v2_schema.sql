/*
  Salud Financiera v2 - esquema y configuracion.

  Ejecutar con SQLCMD variables:
    ExpectedDatabase = Orion_Sandbox | grupocarpio
    ApplyChanges     = 0 | 1

  El modo ApplyChanges=0 ejecuta todas las validaciones y revierte la transaccion.
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @ExpectedDatabase sysname = N'$(ExpectedDatabase)';
DECLARE @ApplyChanges bit = TRY_CONVERT(bit, N'$(ApplyChanges)');

IF @ApplyChanges IS NULL
  THROW 51000, 'ApplyChanges debe ser 0 o 1.', 1;

IF DB_NAME() <> @ExpectedDatabase
  THROW 51001, 'La base conectada no coincide con ExpectedDatabase.', 1;

BEGIN TRANSACTION;

IF COL_LENGTH(N'dbo.ROOM', N'IsActive') IS NULL
BEGIN
  ALTER TABLE dbo.ROOM
    ADD IsActive bit NOT NULL
      CONSTRAINT DF_ROOM_IsActive DEFAULT (1) WITH VALUES;
END;

IF COL_LENGTH(N'dbo.ROOM', N'IsRentable') IS NULL
BEGIN
  ALTER TABLE dbo.ROOM
    ADD IsRentable bit NOT NULL
      CONSTRAINT DF_ROOM_IsRentable DEFAULT (0) WITH VALUES;

  EXEC(N'
  UPDATE dbo.ROOM
  SET IsRentable = CASE
    WHEN UPPER(LTRIM(RTRIM(ISNULL(ROOM_TYPE, '''')))) = ''SUITE''
     AND ISNULL(BASE_PRICE, 0) > 0
     AND UPPER(LTRIM(RTRIM(ISNULL(ROOM_NAME, '''')))) NOT LIKE ''%OFICINA%''
     AND UPPER(LTRIM(RTRIM(ISNULL(ROOM_NAME, '''')))) NOT LIKE ''%LAVANDER%''
     AND UPPER(LTRIM(RTRIM(ISNULL(ROOM_NAME, '''')))) NOT LIKE ''%PROTOTIPO%''
    THEN 1 ELSE 0 END;');
END;

IF OBJECT_ID(N'reporteFinanciero.SaludEmpresaConfiguracion', N'U') IS NULL
BEGIN
  CREATE TABLE reporteFinanciero.SaludEmpresaConfiguracion
  (
    RFC varchar(50) NOT NULL
      CONSTRAINT PK_SaludEmpresaConfiguracion PRIMARY KEY,
    HospedajeHabilitado bit NOT NULL
      CONSTRAINT DF_SaludEmpresaConfiguracion_Hospedaje DEFAULT (0),
    RetencionArrendadorPct decimal(9, 4) NOT NULL
      CONSTRAINT DF_SaludEmpresaConfiguracion_Retencion DEFAULT (10),
    ActualizadoPor nvarchar(256) NOT NULL,
    ActualizadoUtc datetime2(3) NOT NULL
      CONSTRAINT DF_SaludEmpresaConfiguracion_ActualizadoUtc DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT CK_SaludEmpresaConfiguracion_Retencion
      CHECK (RetencionArrendadorPct >= 0 AND RetencionArrendadorPct <= 100)
  );
END;

;WITH Rfcs AS
(
  SELECT DISTINCT NULLIF(LTRIM(RTRIM(RFC)), '') AS RFC
  FROM dbo.CuentasContables
  WHERE NULLIF(LTRIM(RTRIM(RFC)), '') IS NOT NULL
  UNION
  SELECT DISTINCT NULLIF(LTRIM(RTRIM(ClaimValue)), '')
  FROM auth.AspNetUserClaims
  WHERE ClaimType = 'rfc'
    AND NULLIF(LTRIM(RTRIM(ClaimValue)), '') IS NOT NULL
  UNION SELECT 'OHM191112Q26'
)
MERGE reporteFinanciero.SaludEmpresaConfiguracion AS target
USING Rfcs AS source
  ON source.RFC = target.RFC
WHEN NOT MATCHED THEN
  INSERT (RFC, HospedajeHabilitado, RetencionArrendadorPct, ActualizadoPor)
  VALUES (source.RFC, CASE WHEN source.RFC = 'OHM191112Q26' THEN 1 ELSE 0 END, 10, N'Migracion Salud Financiera v2');

IF OBJECT_ID(N'reporteFinanciero.SaludEmpresaMeta', N'U') IS NULL
BEGIN
  CREATE TABLE reporteFinanciero.SaludEmpresaMeta
  (
    MetaID bigint IDENTITY(1, 1) NOT NULL
      CONSTRAINT PK_SaludEmpresaMeta PRIMARY KEY,
    RFC varchar(50) NOT NULL,
    Mes date NOT NULL,
    IngresoHabitacionMeta decimal(19, 4) NULL,
    IngresoComplementarioMeta decimal(19, 4) NULL,
    OcupacionPctMeta decimal(9, 4) NULL,
    ADRMeta decimal(19, 4) NULL,
    GastosOperativosMeta decimal(19, 4) NULL,
    ResultadoNetoMeta decimal(19, 4) NULL,
    FlujoNetoMeta decimal(19, 4) NULL,
    SaldoEfectivoMeta decimal(19, 4) NULL,
    Notas nvarchar(1000) NULL,
    ActualizadoPor nvarchar(256) NOT NULL,
    ActualizadoUtc datetime2(3) NOT NULL
      CONSTRAINT DF_SaludEmpresaMeta_ActualizadoUtc DEFAULT (SYSUTCDATETIME()),
    RowVersion rowversion NOT NULL,
    CONSTRAINT UQ_SaludEmpresaMeta_RfcMes UNIQUE (RFC, Mes),
    CONSTRAINT CK_SaludEmpresaMeta_PrimerDia CHECK (DAY(Mes) = 1),
    CONSTRAINT CK_SaludEmpresaMeta_Ocupacion CHECK (OcupacionPctMeta IS NULL OR OcupacionPctMeta BETWEEN 0 AND 100)
  );
END;

IF OBJECT_ID(N'reporteFinanciero.SaludEmpresaMetaAudit', N'U') IS NULL
BEGIN
  CREATE TABLE reporteFinanciero.SaludEmpresaMetaAudit
  (
    AuditID bigint IDENTITY(1, 1) NOT NULL
      CONSTRAINT PK_SaludEmpresaMetaAudit PRIMARY KEY,
    MetaID bigint NOT NULL,
    RFC varchar(50) NOT NULL,
    Mes date NOT NULL,
    Accion varchar(10) NOT NULL,
    IngresoHabitacionMeta decimal(19, 4) NULL,
    IngresoComplementarioMeta decimal(19, 4) NULL,
    OcupacionPctMeta decimal(9, 4) NULL,
    ADRMeta decimal(19, 4) NULL,
    GastosOperativosMeta decimal(19, 4) NULL,
    ResultadoNetoMeta decimal(19, 4) NULL,
    FlujoNetoMeta decimal(19, 4) NULL,
    SaldoEfectivoMeta decimal(19, 4) NULL,
    Notas nvarchar(1000) NULL,
    Usuario nvarchar(256) NOT NULL,
    FechaUtc datetime2(3) NOT NULL
      CONSTRAINT DF_SaludEmpresaMetaAudit_FechaUtc DEFAULT (SYSUTCDATETIME())
  );
END;

IF OBJECT_ID(N'reporteFinanciero.TR_SaludEmpresaMeta_Audit', N'TR') IS NULL
  EXEC(N'
CREATE TRIGGER reporteFinanciero.TR_SaludEmpresaMeta_Audit
ON reporteFinanciero.SaludEmpresaMeta
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
  SET NOCOUNT ON;
  INSERT reporteFinanciero.SaludEmpresaMetaAudit
  (
    MetaID, RFC, Mes, Accion, IngresoHabitacionMeta,
    IngresoComplementarioMeta, OcupacionPctMeta, ADRMeta,
    GastosOperativosMeta, ResultadoNetoMeta, FlujoNetoMeta,
    SaldoEfectivoMeta, Notas, Usuario
  )
  SELECT
    COALESCE(i.MetaID, d.MetaID), COALESCE(i.RFC, d.RFC), COALESCE(i.Mes, d.Mes),
    CASE WHEN i.MetaID IS NULL THEN ''DELETE'' WHEN d.MetaID IS NULL THEN ''INSERT'' ELSE ''UPDATE'' END,
    COALESCE(i.IngresoHabitacionMeta, d.IngresoHabitacionMeta),
    COALESCE(i.IngresoComplementarioMeta, d.IngresoComplementarioMeta),
    COALESCE(i.OcupacionPctMeta, d.OcupacionPctMeta),
    COALESCE(i.ADRMeta, d.ADRMeta),
    COALESCE(i.GastosOperativosMeta, d.GastosOperativosMeta),
    COALESCE(i.ResultadoNetoMeta, d.ResultadoNetoMeta),
    COALESCE(i.FlujoNetoMeta, d.FlujoNetoMeta),
    COALESCE(i.SaldoEfectivoMeta, d.SaldoEfectivoMeta),
    COALESCE(i.Notas, d.Notas),
    COALESCE(i.ActualizadoPor, d.ActualizadoPor, ORIGINAL_LOGIN())
  FROM inserted i
  FULL OUTER JOIN deleted d ON d.MetaID = i.MetaID;
END;');

DECLARE @SeedStart date = DATEFROMPARTS(2026, 1, 1);
DECLARE @SeedEnd date = DATEADD(MONTH, 12, DATEFROMPARTS(YEAR(GETDATE()), MONTH(GETDATE()), 1));

;WITH Months AS
(
  SELECT @SeedStart AS Mes
  UNION ALL
  SELECT DATEADD(MONTH, 1, Mes)
  FROM Months
  WHERE Mes < @SeedEnd
), Rfcs AS
(
  SELECT RFC FROM reporteFinanciero.SaludEmpresaConfiguracion
)
INSERT reporteFinanciero.SaludEmpresaMeta (RFC, Mes, ActualizadoPor)
SELECT r.RFC, m.Mes, N'Migracion Salud Financiera v2'
FROM Rfcs r
CROSS JOIN Months m
WHERE NOT EXISTS
(
  SELECT 1
  FROM reporteFinanciero.SaludEmpresaMeta existing
  WHERE existing.RFC = r.RFC AND existing.Mes = m.Mes
)
OPTION (MAXRECURSION 1000);

IF NOT EXISTS
(
  SELECT 1 FROM sys.indexes
  WHERE object_id = OBJECT_ID(N'dbo.ROOM_CALENDAR')
    AND name = N'IX_ROOM_CALENDAR_SaludEmpresa'
)
  CREATE INDEX IX_ROOM_CALENDAR_SaludEmpresa
    ON dbo.ROOM_CALENDAR (ROOM_DATE, ROOM)
    INCLUDE (IS_LOCKED, LOCK_DESCRIPTION, PRECIO, PORCENTAJE_ARRENDAMIENTO);

IF NOT EXISTS
(
  SELECT 1 FROM sys.indexes
  WHERE object_id = OBJECT_ID(N'dbo.RESERVATION')
    AND name = N'IX_RESERVATION_SaludEmpresa'
)
  CREATE INDEX IX_RESERVATION_SaludEmpresa
    ON dbo.RESERVATION (STATUS, CHECKIN, CHECKOUT)
    INCLUDE (TOTAL_PRICE, SUITE_DISCOUNT_PERCENT, DATE_CANCELED);

IF COL_LENGTH(N'dbo.ROOM', N'IsActive') IS NULL
   OR COL_LENGTH(N'dbo.ROOM', N'IsRentable') IS NULL
   OR OBJECT_ID(N'reporteFinanciero.SaludEmpresaConfiguracion', N'U') IS NULL
   OR OBJECT_ID(N'reporteFinanciero.SaludEmpresaMeta', N'U') IS NULL
   OR OBJECT_ID(N'reporteFinanciero.SaludEmpresaMetaAudit', N'U') IS NULL
  THROW 51002, 'La validacion de Salud Financiera v2 no fue satisfactoria.', 1;

IF @ApplyChanges = 1
BEGIN
  COMMIT TRANSACTION;
  SELECT N'APLICADO' AS Estado, DB_NAME() AS BaseDatos;
END
ELSE
BEGIN
  ROLLBACK TRANSACTION;
  SELECT N'VALIDADO_SIN_CAMBIOS' AS Estado, DB_NAME() AS BaseDatos;
END;
