/*
  Balanza y resultados sobre pólizas publicadas, exclusivamente en Sandbox.

  Los dos procedimientos viven sólo en la base. Sus definiciones se volcaron con
  OBJECT_DEFINITION antes de tocarlas y quedan versionadas aquí, íntegras, con un
  único cambio: el parámetro @SoloPublicadas.

  **Por omisión es 0, y con 0 el comportamiento es exactamente el de hoy.** Es
  deliberado: mientras el baseline histórico no esté aprobado, no se puede añadir un
  WHERE de publicación y hacer desaparecer la historia en silencio. Con el ciclo de
  E4 apagado ninguna póliza está publicada, así que la variante nueva devolvería cero,
  y el reporte vigente se conserva claramente identificado.

  Con @SoloPublicadas = 1 se agregan únicamente asientos del ciclo formal, aplicando
  el contrato original/reversa de E4:
    - CycleState NULL o 'Draft' quedan fuera.
    - 'Posted' entra.
    - 'Reversed' TAMBIÉN entra, y es lo correcto: el asiento original permanece y la
      póliza de reversa aporta sus movimientos invertidos, que lo cancelan. Excluir el
      original dejaría sólo el inverso y el reporte quedaría al revés.

  No se toca 20260907_fiscal_declaracion_deploy.ps1.
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
    THROW 52020, 'ApplyChanges debe ser 0 o 1.', 1;
  SET @ApplyChanges = CONVERT(bit, @ApplyChangesInput);
END;

IF @ExpectedDatabase LIKE N'$' + N'(%' OR @ExpectedDatabase <> N'Orion_Sandbox'
  THROW 52021, 'Esta migracion admite exclusivamente Orion_Sandbox.', 1;
IF DB_NAME() <> N'Orion_Sandbox'
  THROW 52022, 'La conexion no apunta a Orion_Sandbox.', 1;
IF @MigrationId LIKE N'$' + N'(%'
   OR NULLIF(LTRIM(RTRIM(@MigrationId)), N'') IS NULL
   OR LEN(@MigrationId) > 200
  THROW 52023, 'MigrationId es obligatorio y debe provenir del manifiesto.', 1;
IF @MigrationChecksum LIKE '$' + '(%'
   OR LEN(@MigrationChecksum) <> 64
   OR @MigrationChecksum LIKE '%[^0-9A-F]%' COLLATE Latin1_General_100_BIN2
  THROW 52024, 'MigrationChecksum debe ser un SHA-256 hexadecimal de 64 caracteres.', 1;
IF @AppVersionInput NOT LIKE N'$' + N'(%'
  SET @AppVersion = NULLIF(LTRIM(RTRIM(@AppVersionInput)), N'');

IF OBJECT_ID(N'orion.SchemaMigration', N'U') IS NULL
   OR OBJECT_ID(N'reporteFinanciero.Rpt_BalanzaComprobacion', N'P') IS NULL
   OR OBJECT_ID(N'reporteFinanciero.ESTADO_PERDIDAS_GANANCIAS', N'P') IS NULL
  THROW 52025, 'Faltan los procedimientos de reportes que esta migracion versiona.', 1;
IF NOT EXISTS (SELECT 1 FROM orion.SchemaMigration WHERE MigrationId = N'20260908_accounting_cycle_sandbox')
  THROW 52026, 'Los reportes publicados dependen del ciclo contable formal.', 1;

DECLARE @ExistingChecksum char(64) =
(
  SELECT Checksum FROM orion.SchemaMigration WHERE MigrationId = @MigrationId
);
IF @ExistingChecksum IS NOT NULL
   AND UPPER(@ExistingChecksum) <> UPPER(@MigrationChecksum)
  THROW 52027, 'El mismo MigrationId ya existe con otro checksum.', 1;
IF @ExistingChecksum IS NOT NULL
BEGIN
  SELECT N'YA_APLICADO_SOLO_SANDBOX' AS Estado, @MigrationId AS MigrationId;
  RETURN;
END;

PRINT 'Estado previo de los dos reportes vigentes.';
SELECT
  OBJECT_SCHEMA_NAME(p.object_id) + '.' + OBJECT_NAME(p.object_id) AS Procedimiento,
  CONVERT(bit, CASE WHEN EXISTS
  (
    SELECT 1 FROM sys.parameters q
    WHERE q.object_id = p.object_id AND q.name = '@SoloPublicadas'
  ) THEN 1 ELSE 0 END) AS YaTieneElParametro
FROM sys.procedures p
WHERE p.object_id IN (OBJECT_ID(N'reporteFinanciero.Rpt_BalanzaComprobacion'),
                      OBJECT_ID(N'reporteFinanciero.ESTADO_PERDIDAS_GANANCIAS'));

BEGIN TRY
  BEGIN TRANSACTION;
  DECLARE @LockResult int;
  EXEC @LockResult = sys.sp_getapplock
    @Resource = N'OrionERP:Accounting:PublishedReports:Sandbox',
    @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 15000;
  IF @LockResult < 0
    THROW 52028, 'No se obtuvo el candado de la migracion de reportes.', 1;

  DECLARE @BalanzaSql nvarchar(max) = CONVERT(nvarchar(max), N'');
  SET @BalanzaSql = @BalanzaSql + N'
/*==============================================================
  2) Stored Procedure: Balanza de Comprobación

   - Modo mensual si @Mes tiene valor
   - Modo anual si @Mes IS NULL
   - Filtra CuentasContables por RFC usando RFC
   - Presentación: DEBE primero, luego HABER
   - Saldos SIEMPRE = (Debe - Haber)

   FIX (Enero / Saldo Inicial):
   - Ya NO limitamos Movimientos a >= @FechaAnioInicio.
     Para enero, el saldo inicial requiere ver movimientos previos (p.ej. dic del año anterior).
==============================================================*/
CREATE OR ALTER PROCEDURE [reporteFinanciero].[Rpt_BalanzaComprobacion]
    @Anio INT,
    @Mes  INT = NULL,      -- NULL => modo ANUAL
    @Rfc  VARCHAR(50) = NULL,
    @SoloPublicadas BIT = 0
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @RfcTrim VARCHAR(50) = NULLIF(LTRIM(RTRIM(@Rfc)), '''');

    /*=========================
      Validaciones
    =========================*/
    IF @Anio IS NULL OR @Anio < 1900
    BEGIN
        RAISERROR(''El parámetro @Anio es inválido.'', 16, 1);
        RETURN;
    END;

    IF @Mes IS NOT NULL AND @Mes NOT BETWEEN 1 AND 12
    BEGIN
        RAISERROR(''El parámetro @Mes debe estar entre 1 y 12, o NULL para modo anual.'', 16, 1);
        RETURN;
    END;

    /*=========================
      Rango de fechas
    =========================*/
    DECLARE @FechaAnioInicio      DATE = DATEFROMPARTS(@Anio, 1, 1);
    DECLARE @FechaInicioPeriodo   DATE;
    DECLARE @FechaFinPeriodoExcl  DATE;
    DECLARE @ModoReporte          VARCHAR(10);

    IF @Mes IS NULL
    BEGIN
        -- MODO ANUAL
        SET @ModoReporte         = ''ANUAL'';
        SET @FechaInicioPeriodo  = @FechaAnioInicio;
        SET @FechaFinPeriodoExcl = DATEADD(YEAR, 1, @FechaAnioInicio);
    END
    ELSE
    BEGIN
        -- MODO MENSUAL
        SET @ModoReporte         = ''MENSUAL'';
        SET @FechaInicioPeriodo  = DATEFROMPARTS(@Anio, @Mes, 1);
        SET @FechaFinPeriodoExcl = DATEADD(DAY, 1, EOMONTH(@FechaInicioPeriodo));
    END;

    /*
      IMPORTANTE:
      Para que Saldo_Inicial funcione en enero, Movimientos debe incluir
      fechas anteriores a @FechaInicioPeriodo. Si tu histórico es grande y
      te preocupa performance, podemos acotar este inicio (por ejemplo, al
      año anterior) pero eso depende de tu lógica contable (cierres).
    */
    DECLARE @FechaMovInicio DATE = DATEFROMPARTS(1900, 1, 1);

    ;WITH
    /*----------------------------------------------------------
      Catálogo de cuentas por nivel (filtrado por RFC)
      MAX(Descripcion) evita duplicados accidentales dentro del RFC
    ----------------------------------------------------------*/
    NombreNivel1 AS
    (
        SELECT
            c.Nivel1,
            MAX(c.Descripcion) AS Descripcion
        FROM dbo.Cuent';
  SET @BalanzaSql = @BalanzaSql + N'asContables AS c
        WHERE
            c.Nivel3 = ''00''
            AND c.Nivel2 = ''00''
            AND (@RfcTrim IS NULL OR c.RFC = @RfcTrim)
        GROUP BY
            c.Nivel1
    ),
    NombreNivel2 AS
    (
        SELECT
            c.Nivel1,
            c.Nivel2,
            MAX(c.Descripcion) AS Descripcion
        FROM dbo.CuentasContables AS c
        WHERE
            c.Nivel3 = ''00''
            AND c.Nivel2 <> ''00''
            AND (@RfcTrim IS NULL OR c.RFC = @RfcTrim)
        GROUP BY
            c.Nivel1,
            c.Nivel2
    ),
    NombreNivel3 AS
    (
        SELECT
            c.Nivel1,
            c.Nivel2,
            c.Nivel3,
            MAX(c.Descripcion) AS Descripcion
        FROM dbo.CuentasContables AS c
        WHERE
            c.Nivel3 <> ''00''
            AND (@RfcTrim IS NULL OR c.RFC = @RfcTrim)
        GROUP BY
            c.Nivel1,
            c.Nivel2,
            c.Nivel3
    ),

    /*----------------------------------------------------------
      Movimientos contables hasta el fin del periodo (incluye histórico
      para poder calcular Saldo_Inicial correctamente en enero)
    ----------------------------------------------------------*/
    Movimientos AS
    (
        SELECT
            t.RFC,
            t.Fecha,
            rc.Nivel1,
            rc.Nivel2,
            rc.Nivel3,
            rc.Debe,
            rc.Haber
        FROM dbo.Registro_Contable AS rc
        INNER JOIN dbo.Transacciones AS t
            ON t.ID = rc.TransaccionID
        WHERE
            t.Fecha >= @FechaMovInicio
            AND t.Fecha <  @FechaFinPeriodoExcl
            AND (@RfcTrim IS NULL OR t.RFC = @RfcTrim)
            AND (@SoloPublicadas = 0 OR t.CycleState IN (''Posted'', ''Reversed''))
    ),

    /*----------------------------------------------------------
      Agregación por jerarquía
    ----------------------------------------------------------*/
    Agr AS
    (
        SELECT
            m.RFC,
            m.Nivel1,
            m.Nivel2,
            m.Nivel3,
            GROUPING(m.Nivel2) AS G_N2,
            GROUPING(m.Nivel3) AS G_N3,

            SUM(CASE WHEN m.Fecha < @FechaInicioPeriodo THEN m.Debe  ELSE 0 END) AS Debe_Ant,
            SUM(CASE WHEN m.Fecha < @FechaInicioPeriodo THEN m.Haber ELSE 0 END) AS Haber_Ant,

            SUM(CASE WHEN m.Fecha >= @FechaInicioPeriodo
                       AND m.Fecha <  @FechaFinPeriodoExcl
                     THEN m.Debe ELSE 0 END) AS Debe_Mes,

            SUM(CASE WHEN m.Fecha >= @FechaInicioPeriodo
                       AND m.Fecha <  @FechaFinPeriodoExcl
                     THEN m.Haber ELSE 0 END) AS Haber_Mes
        FROM Movimientos AS m
        GROUP BY
            GROUPING SETS
            (
                (m.RFC, m.Nivel1, m.Nivel2, m.Nivel3)';
  SET @BalanzaSql = @BalanzaSql + N',  -- Nivel 3
                (m.RFC, m.Nivel1, m.Nivel2),            -- Nivel 2
                (m.RFC, m.Nivel1)                       -- Nivel 1
            )
    )

    /*----------------------------------------------------------
      Salida final
      Saldos: (Debe - Haber)

      REGLA NUEVA:
      - Si en el periodo no hay movimientos (Debe_Mes = 0 y Haber_Mes = 0),
        entonces NO mostrar la fila.
    ----------------------------------------------------------*/
    SELECT
        @ModoReporte AS ModoReporte,
        @Anio        AS Anio,
        @Mes         AS Mes,

        CAST(@FechaInicioPeriodo AS DATETIME) AS PeriodoInicio,
        CAST(DATEADD(SECOND, -1, CAST(@FechaFinPeriodoExcl AS DATETIME)) AS DATETIME) AS PeriodoFin,

        a.RFC,
        a.Nivel1,
        a.Nivel2,
        a.Nivel3,

        nn1.Descripcion AS Nivel1Descripcion,
        nn2.Descripcion AS Nivel2Descripcion,
        n3.Descripcion  AS Nivel3Descripcion,

        CASE
            WHEN a.G_N3 = 0 THEN n3.Descripcion
            WHEN a.G_N2 = 0 THEN nn2.Descripcion
            ELSE nn1.Descripcion
        END AS Nombre_Cuenta,

        /* ---- Saldo Inicial: DEBE, HABER, SALDO ---- */
        a.Debe_Ant,
        a.Haber_Ant,
        (a.Debe_Ant - a.Haber_Ant) AS Saldo_Inicial,

        /* ---- Periodo (Mes o Año): DEBE, HABER, SALDO ---- */
        a.Debe_Mes,
        a.Haber_Mes,
        (a.Debe_Mes - a.Haber_Mes) AS Saldo_Mes,

        /* ---- Saldo Final ---- */
        (a.Debe_Ant - a.Haber_Ant) + (a.Debe_Mes - a.Haber_Mes) AS Saldo_Final,

        CASE
            WHEN a.G_N3 = 0 THEN 3
            WHEN a.G_N2 = 0 THEN 2
            ELSE 1
        END AS NivelJerarquia,

        CASE WHEN a.G_N2 = 1 THEN ''00'' ELSE a.Nivel2 END AS SortNivel2,
        CASE WHEN a.G_N3 = 1 THEN ''00'' ELSE a.Nivel3 END AS SortNivel3
    FROM Agr AS a
    LEFT JOIN NombreNivel1 AS nn1
        ON nn1.Nivel1 = a.Nivel1
    LEFT JOIN NombreNivel2 AS nn2
        ON nn2.Nivel1 = a.Nivel1
       AND nn2.Nivel2 = a.Nivel2
    LEFT JOIN NombreNivel3 AS n3
        ON n3.Nivel1 = a.Nivel1
       AND n3.Nivel2 = a.Nivel2
       AND n3.Nivel3 = a.Nivel3
    WHERE
        a.Nivel1 IS NOT NULL
        AND (ISNULL(a.Debe_Mes, 0) <> 0 OR ISNULL(a.Haber_Mes, 0) <> 0)
    ORDER BY
        a.RFC,
        a.Nivel1,
        CASE WHEN a.G_N2 = 1 THEN ''00'' ELSE a.Nivel2 END,
        CASE WHEN a.G_N3 = 1 THEN ''00'' ELSE a.Nivel3 END,
        NivelJerarquia;
END;

';

  DECLARE @ResultadosSql nvarchar(max) = CONVERT(nvarchar(max), N'');
  SET @ResultadosSql = @ResultadosSql + N'
CREATE OR ALTER PROCEDURE [reporteFinanciero].[ESTADO_PERDIDAS_GANANCIAS]
    @startDate DATETIME,
    @endDate   DATETIME,
    @RFC       VARCHAR(13),
    @SoloPublicadas BIT = 0
AS
BEGIN
    SET NOCOUNT ON;

    -------------------------------------------------------------------------
    -- 1) Tabla temporal de salida (mismo layout que tu versión original)
    -------------------------------------------------------------------------
    IF OBJECT_ID(''tempdb..#EstadoPerdidasGanancias'') IS NOT NULL
        DROP TABLE #EstadoPerdidasGanancias;

    CREATE TABLE #EstadoPerdidasGanancias
    (
        ID          INT,
        DESCRIPCION VARCHAR(200),
        PRIMERO     MONEY DEFAULT 0,
        SEGUNDO     MONEY DEFAULT 0,
        TERCERO     MONEY DEFAULT 0,
        CUARTO      MONEY DEFAULT 0
    );

    INSERT INTO #EstadoPerdidasGanancias (ID, DESCRIPCION)
    VALUES 
        (1 , ''    VENTAS''),
        (2 , ''(-) REBAJAS Y DEVOLUCIONES SOBRE VENTAS''),
        (3 , ''(=) VENTAS NETAS''),
        (4 , ''    COMPRAS''),
        (5 , ''(+) GASTOS SOBRE COMPRAS''),
        (6 , ''(=) COMPRAS TOTALES''),
        (7 , ''(-) REBAJAS Y DEVOLUCIONES SOBRE COMPRA''),
        (8 , ''(=) COMPRAS NETAS''),
        (9 , ''(+) INVENTARIO INICIAL''),
        (10, ''(=) SUMA TOTAL DE MERCANCIAS''),
        (11, ''(-) INVENTARIO FINAL''),
        (12, ''(=) COSTO DE VENTAS''),
        (13, ''(=) UTILIDAD BRUTA''),
        (14, ''(-) GASTOS DE OPERACION''),
        (15, ''      * GASTO DE VENTAS''),
        (16, ''      * GASTOS DE ADMINISTRACION''),
        (17, ''      * GASTOS FINANCIEROS''),
        (18, ''(=) UTILIDAD DE OPERACION''),
        (19, ''      * OTROS GASTOS''),
        (20, ''      * OTROS PRODUCTOS''),
        (21, '' (+) PERDIDA/GANANCIA OTROS PRODUCTOS''),
        (22, ''(=) UTILIDAD ANTES DE ISR Y PTU''),
        (23, ''(-) IMPUESTOS''),
        (24, ''(=) UTILIDAD NETA'');

    -------------------------------------------------------------------------
    -- 2) Variables base (por grupos SAT)
    -------------------------------------------------------------------------
    DECLARE
        @Ventas_401               MONEY = 0,
        @Ingresos_403             MONEY = 0,
        @Devoluciones_Sobre_Venta MONEY = 0,
        @Compras                  MONEY = 0,
        @Reba_Dev_Compra          MONEY = 0,
        @Inventario_Inicial       MONEY = 0,
        @Inventario_Final         MONEY = 0,
        @Gastos_Ventas            MONEY = 0,
        @Gastos_Administracion    MONEY = 0,
        @Gastos_Financieros       MONEY = 0,
        @Otros_Gastos             MONEY = 0,
        @Otros_Productos          MONEY = 0,
        @ISR_611                  MONEY = 0,
        @ISR_213                  MONEY = 0;

    -------------------------------------------------------------------------
    -- 3) I';
  SET @ResultadosSql = @ResultadosSql + N'nventario Inicial (saldo al día anterior a @startDate)
    --    Nivel1 = 115 (Inventario)
    -------------------------------------------------------------------------
    SELECT
        @Inventario_Inicial = ISNULL(
            SUM(CASE WHEN rc.Nivel1 = ''115''
                     THEN rc.Debe - rc.Haber
                     ELSE 0 END), 0)
    FROM Registro_Contable rc
    INNER JOIN Transacciones t
        ON rc.TransaccionID = t.ID
    WHERE t.RFC   = @RFC
      AND (@SoloPublicadas = 0 OR t.CycleState IN (''Posted'', ''Reversed''))
      AND t.Fecha < @startDate;

    -------------------------------------------------------------------------
    -- 4) Inventario Final (saldo al cierre de @endDate)
    -------------------------------------------------------------------------
    SELECT
        @Inventario_Final = ISNULL(
            SUM(CASE WHEN rc.Nivel1 = ''115''
                     THEN rc.Debe - rc.Haber
                     ELSE 0 END), 0)
    FROM Registro_Contable rc
    INNER JOIN Transacciones t
        ON rc.TransaccionID = t.ID
    WHERE t.RFC   = @RFC
      AND (@SoloPublicadas = 0 OR t.CycleState IN (''Posted'', ''Reversed''))
      AND t.Fecha <= @endDate;

    -------------------------------------------------------------------------
    -- 5) Agregados del periodo [@startDate, @endDate]
    -------------------------------------------------------------------------
    SELECT
        @Ventas_401 = ISNULL(SUM(CASE 
                                   WHEN rc.Nivel1 = ''401''
                                   THEN rc.Haber - rc.Debe
                                   ELSE 0
                                 END), 0),
        @Ingresos_403 = ISNULL(SUM(CASE 
                                     WHEN rc.Nivel1 = ''403''
                                     THEN rc.Haber - rc.Debe
                                     ELSE 0
                                   END), 0),
        @Devoluciones_Sobre_Venta = ISNULL(SUM(CASE 
                                                 WHEN rc.Nivel1 = ''402''
                                                 THEN rc.Debe - rc.Haber
                                                 ELSE 0
                                               END), 0),
        @Compras = ISNULL(SUM(CASE 
                                 WHEN rc.Nivel1 = ''502''
                                 THEN rc.Debe - rc.Haber
                                 ELSE 0
                               END), 0),
        @Reba_Dev_Compra = ISNULL(SUM(CASE 
                                        WHEN rc.Nivel1 = ''503''
                                        THEN rc.Haber - rc.Debe
                                        ELSE 0
                                      END), 0),
        @Gastos_Ventas = ISNULL(SUM(CASE 
                                      WHEN rc.Nivel1';
  SET @ResultadosSql = @ResultadosSql + N' = ''602''
                                      THEN rc.Debe - rc.Haber
                                      ELSE 0
                                    END), 0),
        @Gastos_Administracion = ISNULL(SUM(CASE 
                                              WHEN rc.Nivel1 = ''603''
                                              THEN rc.Debe - rc.Haber
                                              ELSE 0
                                            END), 0),
        @Gastos_Financieros = ISNULL(SUM(CASE 
                                           WHEN rc.Nivel1 = ''701''
                                           THEN rc.Debe - rc.Haber
                                           ELSE 0
                                         END), 0),
        @Otros_Gastos = ISNULL(SUM(CASE 
                                     WHEN rc.Nivel1 = ''703''
                                     THEN rc.Debe - rc.Haber
                                     ELSE 0
                                   END), 0),
        @Otros_Productos = ISNULL(SUM(CASE 
                                        WHEN rc.Nivel1 = ''704''
                                        THEN rc.Haber - rc.Debe
                                        ELSE 0
                                      END), 0),
        @ISR_611 = ISNULL(SUM(CASE 
                                 WHEN rc.Nivel1 = ''611''
                                 THEN rc.Debe - rc.Haber
                                 ELSE 0
                               END), 0),
        @ISR_213 = ISNULL(SUM(CASE 
                                 WHEN rc.Nivel1 = ''213''
                                      AND rc.Nivel2 = ''03''
                                      AND rc.Nivel3 = ''2''
                                 THEN rc.Haber
                                 ELSE 0
                               END), 0)
    FROM Registro_Contable rc
    INNER JOIN Transacciones t
        ON rc.TransaccionID = t.ID
    WHERE t.RFC   = @RFC
      AND (@SoloPublicadas = 0 OR t.CycleState IN (''Posted'', ''Reversed''))
      AND t.Fecha >= @startDate
      AND t.Fecha <= @endDate;

    -------------------------------------------------------------------------
    -- 6) Cálculos intermedios del Estado de Resultados
    -------------------------------------------------------------------------
    DECLARE
        @Ventas_Totales        MONEY = 0,
        @Ventas_Netas          MONEY = 0,
        @Compras_Totales       MONEY = 0,
        @Compras_Netas         MONEY = 0,
        @Suma_Total_Mercancias MONEY = 0,
        @Costo_Ventas          MONEY = 0,
        @Utilidad_Bruta        MONEY = 0,
        @Gastos_Operacion      MONEY = 0,
        @Utilidad_Operacion    MONEY = 0,
        @Otros_Productos_Total MONEY = 0,
        @Utilidad_Antes_ISR    MONEY = 0,
        @Impuestos             MO';
  SET @ResultadosSql = @ResultadosSql + N'NEY = 0,
        @Utilidad_Neta         MONEY = 0;

    -- Ventas totales = Ingresos 401 + Otros ingresos operativos 403
    SET @Ventas_Totales = @Ventas_401 + @Ingresos_403;

    -- Ventas netas = Ventas totales - devoluciones sobre venta (402)
    SET @Ventas_Netas = @Ventas_Totales - @Devoluciones_Sobre_Venta;

    -- Compras totales = Compras (502) + gastos sobre compras (por ahora 0)
    SET @Compras_Totales = @Compras + 0;

    -- Compras netas = Compras totales - rebajas/devoluciones sobre compras (503)
    SET @Compras_Netas = @Compras_Totales - @Reba_Dev_Compra;

    -- Suma total de mercancías
    SET @Suma_Total_Mercancias = @Compras_Netas + @Inventario_Inicial;

    -- Costo de ventas = Suma mercancías - inventario final
    SET @Costo_Ventas = @Suma_Total_Mercancias - @Inventario_Final;

    -- Utilidad bruta
    SET @Utilidad_Bruta = @Ventas_Netas - @Costo_Ventas;

    -- Gastos de operación (ventas + administración + financieros)
    SET @Gastos_Operacion = @Gastos_Ventas + @Gastos_Administracion + @Gastos_Financieros;

    -- Utilidad de operación
    SET @Utilidad_Operacion = @Utilidad_Bruta - @Gastos_Operacion;

    -- Resultado de otros productos / otros gastos
    SET @Otros_Productos_Total = @Otros_Productos - @Otros_Gastos;

    -- Utilidad antes de ISR y PTU
    SET @Utilidad_Antes_ISR = @Utilidad_Operacion + @Otros_Productos_Total;

    -- Impuestos: preferimos ISR contable (611); si está en cero usamos 213.03.2
    SET @Impuestos = CASE 
                        WHEN @ISR_611 <> 0 THEN @ISR_611
                        ELSE @ISR_213
                     END;

    -- Utilidad neta
    SET @Utilidad_Neta = @Utilidad_Antes_ISR - @Impuestos;

    -------------------------------------------------------------------------
    -- 7) Poblar la tabla temporal con los importes calculados
    -------------------------------------------------------------------------
    UPDATE #EstadoPerdidasGanancias
        SET CUARTO = @Ventas_Totales
    WHERE ID = 1;

    UPDATE #EstadoPerdidasGanancias
        SET TERCERO = @Devoluciones_Sobre_Venta
    WHERE ID = 2;

    UPDATE #EstadoPerdidasGanancias
        SET CUARTO = @Ventas_Netas
    WHERE ID = 3;

    UPDATE #EstadoPerdidasGanancias
        SET TERCERO = @Compras
    WHERE ID = 4;

    -- Gastos sobre compra (por ahora 0)
    UPDATE #EstadoPerdidasGanancias
        SET TERCERO = 0
    WHERE ID = 5;

    UPDATE #EstadoPerdidasGanancias
        SET CUARTO = @Compras_Totales
    WHERE ID = 6;

    UPDATE #EstadoPerdidasGanancias
        SET TERCERO = @Reba_Dev_Compra
    WHERE ID = 7;

    UPDATE #EstadoPerdidasGanancias
        SET CUARTO = @Compras_Netas
    WHERE ID = 8;

    UPDATE #EstadoPerdidasGanancias
        SET TERCERO = @Inventario_Inicial
    WHERE ID = 9;

    UPDATE #EstadoPerdid';
  SET @ResultadosSql = @ResultadosSql + N'asGanancias
        SET CUARTO = @Suma_Total_Mercancias
    WHERE ID = 10;

    UPDATE #EstadoPerdidasGanancias
        SET TERCERO = @Inventario_Final
    WHERE ID = 11;

    UPDATE #EstadoPerdidasGanancias
        SET CUARTO = @Costo_Ventas
    WHERE ID = 12;

    UPDATE #EstadoPerdidasGanancias
        SET CUARTO = @Utilidad_Bruta
    WHERE ID = 13;

    UPDATE #EstadoPerdidasGanancias
        SET TERCERO = @Gastos_Operacion
    WHERE ID = 14;

    UPDATE #EstadoPerdidasGanancias
        SET SEGUNDO = @Gastos_Ventas
    WHERE ID = 15;

    UPDATE #EstadoPerdidasGanancias
        SET SEGUNDO = @Gastos_Administracion
    WHERE ID = 16;

    UPDATE #EstadoPerdidasGanancias
        SET SEGUNDO = @Gastos_Financieros
    WHERE ID = 17;

    UPDATE #EstadoPerdidasGanancias
        SET CUARTO = @Utilidad_Operacion
    WHERE ID = 18;

    UPDATE #EstadoPerdidasGanancias
        SET TERCERO = @Otros_Gastos
    WHERE ID = 19;

    UPDATE #EstadoPerdidasGanancias
        SET TERCERO = @Otros_Productos
    WHERE ID = 20;

    UPDATE #EstadoPerdidasGanancias
        SET TERCERO = @Otros_Productos_Total
    WHERE ID = 21;

    UPDATE #EstadoPerdidasGanancias
        SET CUARTO = @Utilidad_Antes_ISR
    WHERE ID = 22;

    UPDATE #EstadoPerdidasGanancias
        SET TERCERO = @Impuestos
    WHERE ID = 23;

    UPDATE #EstadoPerdidasGanancias
        SET CUARTO = @Utilidad_Neta
    WHERE ID = 24;

    -------------------------------------------------------------------------
    -- 8) Resultado
    -------------------------------------------------------------------------
    SELECT *
    FROM #EstadoPerdidasGanancias
    ORDER BY ID;
END

';

  EXEC sys.sp_executesql @BalanzaSql;
  EXEC sys.sp_executesql @ResultadosSql;

  /* El parametro nuevo existe en los dos y nace con valor por omision. */
  IF NOT EXISTS (SELECT 1 FROM sys.parameters WHERE object_id = OBJECT_ID(N'reporteFinanciero.Rpt_BalanzaComprobacion') AND name = '@SoloPublicadas')
     OR NOT EXISTS (SELECT 1 FROM sys.parameters WHERE object_id = OBJECT_ID(N'reporteFinanciero.ESTADO_PERDIDAS_GANANCIAS') AND name = '@SoloPublicadas')
    THROW 52029, 'Los procedimientos no quedaron con el parametro de publicacion.', 1;
  /* sys.parameters.has_default_value queda en 0 para procedimientos T-SQL aunque el
     valor por omision exista, asi que el default se comprueba sobre la definicion. No
     se invoca el procedimiento: bajo XACT_ABORT ON, cualquier error suyo condenaria
     esta transaccion aunque se capture. */
  IF OBJECT_DEFINITION(OBJECT_ID(N'reporteFinanciero.Rpt_BalanzaComprobacion')) NOT LIKE '%@SoloPublicadas BIT = 0%'
     OR OBJECT_DEFINITION(OBJECT_ID(N'reporteFinanciero.ESTADO_PERDIDAS_GANANCIAS')) NOT LIKE '%@SoloPublicadas BIT = 0%'
    THROW 52030, 'El parametro de publicacion debe declararse con valor por omision 0.', 1;
  /* Y el reporte vigente sigue siendo el mismo: con 0, ningun filtro se aplica. */
  IF OBJECT_DEFINITION(OBJECT_ID(N'reporteFinanciero.Rpt_BalanzaComprobacion')) NOT LIKE '%@SoloPublicadas = 0 OR t.CycleState IN (%'
    THROW 52031, 'El filtro de publicacion debe ser condicional, no incondicional.', 1;

  SELECT
    CONVERT(bigint, (SELECT COUNT_BIG(*) FROM dbo.Transacciones WHERE CycleState IN ('Posted', 'Reversed'))) AS AsientosPublicados,
    CONVERT(bigint, (SELECT COUNT_BIG(*) FROM dbo.Transacciones)) AS AsientosTotales,
    CONVERT(bigint, (SELECT COUNT_BIG(*) FROM contabilidad.CompanyCycleActivation WHERE IsEnabled = 1)) AS EmpresasConCicloEncendido;

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
