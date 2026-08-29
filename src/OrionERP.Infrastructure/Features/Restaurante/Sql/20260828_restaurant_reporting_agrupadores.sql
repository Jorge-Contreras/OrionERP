/*
  Reportes de Restaurante por código agrupador SAT y diagnóstico contable-fiscal.

  Contenido:
    1. restaurante.ReporteAgrupadorMapa
       Mapea cada concepto de reporte (ingresos, costo de venta, IVA trasladado,
       etc.) con los códigos agrupadores nivel 1 del Anexo 24 que lo componen.
       Agregar o retirar un agrupador de un reporte es insertar una fila o apagar
       su bandera Incluido, sin recompilar la aplicación.
    2. restaurante.DiagnosticoCorrida y restaurante.DiagnosticoHallazgo
       Historial del diagnóstico contable-fiscal: cada corrida guarda sus
       hallazgos con severidad, monto expuesto y estado, para poder medir si la
       operación mejora mes a mes y para exigir justificación al aceptar un
       hallazgo sin corregirlo.
    3. Semilla del mapeo para cada RFC que ya tiene sede de restaurante.
    4. Incorporación de las tablas nuevas a la política RLS compartida
       logistica.RfcSecurityPolicy.

  Uso:
    sqlcmd ... -f 65001 -v ExpectedDatabase="grupocarpio" ApplyChanges="0" -i 20260828_restaurant_reporting_agrupadores.sql
    sqlcmd ... -f 65001 -v ExpectedDatabase="grupocarpio" ApplyChanges="1" -i 20260828_restaurant_reporting_agrupadores.sql

  ApplyChanges=0 ejecuta y valida todo dentro de una transacción que se revierte.
  ApplyChanges=1 confirma. Producción requiere respaldo previo.
  -f 65001 es obligatorio para conservar los literales Unicode del archivo UTF-8.

  El script es idempotente: puede volver a ejecutarse sin duplicar filas.
*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET XACT_ABORT ON;
SET NOCOUNT ON;

DECLARE @ExpectedDatabase sysname = N'$(ExpectedDatabase)';
DECLARE @ApplyChanges bit = TRY_CONVERT(bit, N'$(ApplyChanges)');
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
    @Resource = N'OrionERP:Restaurante:ReportingAgrupadores:20260828',
    @LockMode = N'Exclusive',
    @LockOwner = N'Transaction',
    @LockTimeout = 15000;
  IF @LockResult < 0
    THROW 51404, 'No fue posible obtener el bloqueo exclusivo de migración.', 1;

  /* ------------------------------------------------------------------
     1. Mapeo de conceptos de reporte contra agrupadores SAT nivel 1
     ------------------------------------------------------------------ */
  IF OBJECT_ID('restaurante.ReporteAgrupadorMapa', 'U') IS NULL
  BEGIN
    CREATE TABLE restaurante.ReporteAgrupadorMapa
    (
      Id              int IDENTITY(1,1) NOT NULL,
      Rfc             varchar(50)   NOT NULL,
      ConceptoClave   varchar(40)   NOT NULL,
      Nivel1          varchar(50)   NOT NULL,
      Signo           smallint      NOT NULL CONSTRAINT DF_ReporteAgrupadorMapa_Signo DEFAULT (1),
      Incluido        bit           NOT NULL CONSTRAINT DF_ReporteAgrupadorMapa_Incluido DEFAULT (1),
      Orden           int           NOT NULL CONSTRAINT DF_ReporteAgrupadorMapa_Orden DEFAULT (0),
      EsPersonalizado bit           NOT NULL CONSTRAINT DF_ReporteAgrupadorMapa_EsPersonalizado DEFAULT (0),
      CreatedAt       datetime2(0)  NOT NULL CONSTRAINT DF_ReporteAgrupadorMapa_CreatedAt DEFAULT (SYSUTCDATETIME()),
      CreatedBy       varchar(200)  NOT NULL CONSTRAINT DF_ReporteAgrupadorMapa_CreatedBy DEFAULT ('sistema'),
      UpdatedAt       datetime2(0)  NULL,
      UpdatedBy       varchar(200)  NULL,
      CONSTRAINT PK_ReporteAgrupadorMapa PRIMARY KEY CLUSTERED (Id),
      CONSTRAINT UQ_ReporteAgrupadorMapa UNIQUE (Rfc, ConceptoClave, Nivel1),
      CONSTRAINT CK_ReporteAgrupadorMapa_Signo CHECK (Signo IN (-1, 1))
    );

    CREATE INDEX IX_ReporteAgrupadorMapa_Rfc_Concepto
      ON restaurante.ReporteAgrupadorMapa (Rfc, ConceptoClave)
      INCLUDE (Nivel1, Signo, Incluido, Orden);
  END;

  /* ------------------------------------------------------------------
     2. Historial del diagnóstico contable-fiscal
     ------------------------------------------------------------------ */
  IF OBJECT_ID('restaurante.DiagnosticoCorrida', 'U') IS NULL
  BEGIN
    CREATE TABLE restaurante.DiagnosticoCorrida
    (
      Id             bigint IDENTITY(1,1) NOT NULL,
      Rfc            varchar(50)   NOT NULL,
      SiteId         int           NOT NULL,
      PeriodoInicio  date          NOT NULL,
      PeriodoFin     date          NOT NULL,
      EjecutadoEn    datetime2(0)  NOT NULL CONSTRAINT DF_DiagnosticoCorrida_EjecutadoEn DEFAULT (SYSUTCDATETIME()),
      EjecutadoPor   varchar(200)  NOT NULL,
      HallazgosTotal int           NOT NULL CONSTRAINT DF_DiagnosticoCorrida_Total DEFAULT (0),
      Criticos       int           NOT NULL CONSTRAINT DF_DiagnosticoCorrida_Criticos DEFAULT (0),
      MontoExpuesto  decimal(18,2) NOT NULL CONSTRAINT DF_DiagnosticoCorrida_Monto DEFAULT (0),
      CONSTRAINT PK_DiagnosticoCorrida PRIMARY KEY CLUSTERED (Id)
    );

    CREATE INDEX IX_DiagnosticoCorrida_Rfc_Fecha
      ON restaurante.DiagnosticoCorrida (Rfc, SiteId, EjecutadoEn DESC);
  END;

  IF OBJECT_ID('restaurante.DiagnosticoHallazgo', 'U') IS NULL
  BEGIN
    CREATE TABLE restaurante.DiagnosticoHallazgo
    (
      Id             bigint IDENTITY(1,1) NOT NULL,
      Rfc            varchar(50)   NOT NULL,
      CorridaId      bigint        NOT NULL,
      ReglaClave     varchar(10)   NOT NULL,
      Severidad      varchar(12)   NOT NULL,
      Titulo         varchar(200)  NOT NULL,
      Detalle        varchar(2000) NOT NULL,
      Agrupadores    varchar(400)  NULL,
      MontoExpuesto  decimal(18,2) NOT NULL CONSTRAINT DF_DiagnosticoHallazgo_Monto DEFAULT (0),
      Conteo         int           NOT NULL CONSTRAINT DF_DiagnosticoHallazgo_Conteo DEFAULT (0),
      AccionSugerida varchar(600)  NULL,
      Estado         varchar(12)   NOT NULL CONSTRAINT DF_DiagnosticoHallazgo_Estado DEFAULT ('Abierto'),
      Justificacion  varchar(1000) NULL,
      ResueltoEn     datetime2(0)  NULL,
      ResueltoPor    varchar(200)  NULL,
      CONSTRAINT PK_DiagnosticoHallazgo PRIMARY KEY CLUSTERED (Id),
      CONSTRAINT FK_DiagnosticoHallazgo_Corrida FOREIGN KEY (CorridaId)
        REFERENCES restaurante.DiagnosticoCorrida (Id),
      CONSTRAINT CK_DiagnosticoHallazgo_Severidad
        CHECK (Severidad IN ('Critica','Alta','Media','Menor','Informativa')),
      CONSTRAINT CK_DiagnosticoHallazgo_Estado
        CHECK (Estado IN ('Abierto','Corregido','Aceptado'))
    );

    CREATE INDEX IX_DiagnosticoHallazgo_Corrida
      ON restaurante.DiagnosticoHallazgo (CorridaId, Severidad, ReglaClave);
    CREATE INDEX IX_DiagnosticoHallazgo_Rfc_Regla
      ON restaurante.DiagnosticoHallazgo (Rfc, ReglaClave, Estado);
  END;

  /* ------------------------------------------------------------------
     3. Semilla del mapeo por RFC con sede de restaurante
     ------------------------------------------------------------------ */
  DECLARE @Semilla TABLE
  (
    ConceptoClave varchar(40)  NOT NULL,
    Nivel1        varchar(50)  NOT NULL,
    Signo         smallint     NOT NULL,
    Orden         int          NOT NULL
  );

  INSERT @Semilla (ConceptoClave, Nivel1, Signo, Orden)
  VALUES
    /* Estado de resultados */
    ('IngresosVenta','401',1,10),
    ('DevolucionesDescuentos','402',-1,20),
    ('OtrosIngresos','403',1,30),
    ('CostoVenta','501',-1,40),
    ('Compras','502',-1,50),
    ('DevolucionesCompras','503',1,60),
    ('OtrosCostos','504',-1,70),
    ('GastosGenerales','601',-1,80),
    ('GastosVenta','602',-1,90),
    ('GastosAdministracion','603',-1,100),
    ('GastosFinancieros','701',-1,110),
    ('ProductosFinancieros','702',1,120),
    ('OtrosGastos','703',-1,130),
    ('OtrosProductos','704',1,140),
    /* Posición financiera y conciliación */
    ('Caja','101',1,200),
    ('Bancos','102',1,210),
    ('Clientes','105',1,220),
    ('Clientes','106',1,221),
    ('Inventarios','115',1,230),
    ('IvaAcreditable','118',1,240),
    ('IvaAcreditable','119',1,241),
    ('ActivoFijo','151',1,250),
    ('ActivoFijo','152',1,251),
    ('ActivoFijo','153',1,252),
    ('ActivoFijo','154',1,253),
    ('ActivoFijo','155',1,254),
    ('ActivoFijo','156',1,255),
    ('ActivoFijo','157',1,256),
    ('ActivoFijo','159',1,257),
    ('ActivoFijo','160',1,258),
    ('ActivoFijo','170',1,259),
    ('DepreciacionAcumulada','171',-1,260),
    ('CargosDiferidos','173',1,270),
    ('CargosDiferidos','174',1,271),
    ('CargosDiferidos','181',1,272),
    ('Proveedores','201',-1,280),
    ('Acreedores','205',-1,290),
    ('Acreedores','251',-1,291),
    ('IvaTrasladado','208',-1,300),
    ('IvaTrasladado','209',-1,301),
    ('ImpuestosPorPagar','213',-1,310),
    ('ImpuestosRetenidos','216',-1,320),
    ('Capital','301',-1,330),
    ('Capital','302',-1,331),
    ('Capital','303',-1,332),
    ('Capital','304',-1,333),
    ('Capital','305',-1,334),
    ('Capital','306',-1,335);

  INSERT restaurante.ReporteAgrupadorMapa (Rfc, ConceptoClave, Nivel1, Signo, Orden, CreatedBy)
  SELECT sedes.Rfc, semilla.ConceptoClave, semilla.Nivel1, semilla.Signo, semilla.Orden, 'migracion:20260828'
  FROM (SELECT DISTINCT Rfc FROM restaurante.Site) sedes
  CROSS JOIN @Semilla semilla
  WHERE NOT EXISTS
  (
    SELECT 1 FROM restaurante.ReporteAgrupadorMapa mapa
    WHERE mapa.Rfc = sedes.Rfc
      AND mapa.ConceptoClave = semilla.ConceptoClave
      AND mapa.Nivel1 = semilla.Nivel1
  );

  DECLARE @FilasSembradas int = @@ROWCOUNT;

  /* ------------------------------------------------------------------
     4. Incorporar las tablas nuevas a la política RLS compartida
     ------------------------------------------------------------------ */
  IF EXISTS
  (
    SELECT 1 FROM sys.security_policies
    WHERE [name]='RfcSecurityPolicy' AND schema_id=SCHEMA_ID('logistica')
  )
  BEGIN
    DECLARE @RlsTable sysname;
    DECLARE @RlsSql nvarchar(max);
    DECLARE RlsCursor CURSOR LOCAL FAST_FORWARD FOR
    SELECT tableInfo.[name]
    FROM sys.tables tableInfo
    JOIN sys.schemas schemaInfo ON schemaInfo.schema_id=tableInfo.schema_id
    WHERE schemaInfo.[name]='restaurante'
      AND tableInfo.[name] IN ('ReporteAgrupadorMapa','DiagnosticoCorrida','DiagnosticoHallazgo')
      AND NOT EXISTS
      (
        SELECT 1 FROM sys.security_predicates predicateInfo
        WHERE predicateInfo.object_id=OBJECT_ID('logistica.RfcSecurityPolicy')
          AND predicateInfo.target_object_id=tableInfo.object_id
      );

    OPEN RlsCursor;
    FETCH NEXT FROM RlsCursor INTO @RlsTable;
    WHILE @@FETCH_STATUS=0
    BEGIN
      SET @RlsSql=N'ALTER SECURITY POLICY logistica.RfcSecurityPolicy
        ADD FILTER PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON [restaurante].'+QUOTENAME(@RlsTable)+N',
        ADD BLOCK PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON [restaurante].'+QUOTENAME(@RlsTable)+N' AFTER INSERT,
        ADD BLOCK PREDICATE logistica.fn_RfcAccessPredicate(Rfc) ON [restaurante].'+QUOTENAME(@RlsTable)+N' AFTER UPDATE;';
      EXEC sys.sp_executesql @RlsSql;
      FETCH NEXT FROM RlsCursor INTO @RlsTable;
    END;
    CLOSE RlsCursor;
    DEALLOCATE RlsCursor;
  END;

  /* ------------------------------------------------------------------
     Validaciones
     ------------------------------------------------------------------ */
  IF OBJECT_ID('restaurante.ReporteAgrupadorMapa','U') IS NULL
    THROW 51405, 'No se creó restaurante.ReporteAgrupadorMapa.', 1;
  IF OBJECT_ID('restaurante.DiagnosticoCorrida','U') IS NULL
    THROW 51406, 'No se creó restaurante.DiagnosticoCorrida.', 1;
  IF OBJECT_ID('restaurante.DiagnosticoHallazgo','U') IS NULL
    THROW 51407, 'No se creó restaurante.DiagnosticoHallazgo.', 1;

  IF EXISTS
  (
    SELECT 1
    FROM (SELECT DISTINCT Rfc FROM restaurante.Site) sedes
    WHERE NOT EXISTS
    (
      SELECT 1 FROM restaurante.ReporteAgrupadorMapa mapa
      WHERE mapa.Rfc = sedes.Rfc AND mapa.ConceptoClave = 'IngresosVenta'
    )
  )
    THROW 51408, 'Quedó al menos una sede sin mapeo de agrupadores sembrado.', 1;

  IF EXISTS
  (
    SELECT mapa.Rfc, mapa.ConceptoClave
    FROM restaurante.ReporteAgrupadorMapa mapa
    GROUP BY mapa.Rfc, mapa.ConceptoClave
    HAVING COUNT(DISTINCT mapa.Signo) > 1
  )
    THROW 51409, 'Un concepto quedó con signos contradictorios entre sus agrupadores.', 1;

  IF EXISTS
  (
    SELECT 1 FROM sys.security_policies policyInfo
    WHERE policyInfo.[name]='RfcSecurityPolicy' AND policyInfo.schema_id=SCHEMA_ID('logistica')
  )
  AND EXISTS
  (
    SELECT 1
    FROM sys.tables tableInfo
    JOIN sys.schemas schemaInfo ON schemaInfo.schema_id=tableInfo.schema_id
    WHERE schemaInfo.[name]='restaurante'
      AND tableInfo.[name] IN ('ReporteAgrupadorMapa','DiagnosticoCorrida','DiagnosticoHallazgo')
      AND NOT EXISTS
      (
        SELECT 1 FROM sys.security_predicates predicateInfo
        WHERE predicateInfo.object_id=OBJECT_ID('logistica.RfcSecurityPolicy')
          AND predicateInfo.target_object_id=tableInfo.object_id
          AND predicateInfo.predicate_type_desc='FILTER'
      )
  )
    THROW 51410, 'Alguna tabla nueva quedó fuera de la política RLS.', 1;

  /* Resumen */
  SELECT
    DB_NAME() AS DatabaseName,
    @ApplyChanges AS ApplyChanges,
    @FilasSembradas AS FilasSembradas,
    (SELECT COUNT(DISTINCT Rfc) FROM restaurante.ReporteAgrupadorMapa) AS RfcsConMapeo,
    (SELECT COUNT(*) FROM restaurante.ReporteAgrupadorMapa) AS FilasMapeo,
    (SELECT COUNT(*) FROM restaurante.DiagnosticoCorrida) AS Corridas;

  SELECT mapa.Rfc,
         mapa.ConceptoClave,
         COUNT(*) AS Agrupadores,
         MIN(mapa.Signo) AS Signo,
         STRING_AGG(mapa.Nivel1, ', ') WITHIN GROUP (ORDER BY mapa.Nivel1) AS Codigos
  FROM restaurante.ReporteAgrupadorMapa mapa
  WHERE mapa.Incluido = 1
  GROUP BY mapa.Rfc, mapa.ConceptoClave
  ORDER BY mapa.Rfc, MIN(mapa.Orden);

  IF @ApplyChanges=1
    COMMIT TRANSACTION;
  ELSE
  BEGIN
    ROLLBACK TRANSACTION;
    PRINT 'SIMULACIÓN COMPLETA: todos los cambios fueron revertidos.';
  END;
END TRY
BEGIN CATCH
  IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
  THROW;
END CATCH;
