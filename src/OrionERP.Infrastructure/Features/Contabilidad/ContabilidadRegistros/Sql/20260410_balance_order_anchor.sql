SET NOCOUNT ON;
SET XACT_ABORT ON;

IF COL_LENGTH(N'dbo.Transacciones', N'OrdenBalance') IS NULL
BEGIN
    ALTER TABLE dbo.Transacciones
        ADD OrdenBalance BIGINT NULL;
END;
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION;

UPDATE t
SET OrdenBalance = CAST(t.ID AS BIGINT)
FROM dbo.Transacciones t
WHERE t.OrdenBalance IS NULL
   OR t.OrdenBalance <= 0;

DECLARE @NextOrdenBalance BIGINT =
    ISNULL(
        (
            SELECT MAX(t.OrdenBalance)
            FROM dbo.Transacciones t
        ),
        0
    ) + 1;

IF OBJECT_ID(N'dbo.Seq_Transacciones_OrdenBalance', N'SO') IS NULL
BEGIN
    DECLARE @CreateSequenceSql NVARCHAR(MAX) =
        N'CREATE SEQUENCE dbo.Seq_Transacciones_OrdenBalance AS BIGINT START WITH '
        + CONVERT(NVARCHAR(30), @NextOrdenBalance)
        + N' INCREMENT BY 1;';

    EXEC sys.sp_executesql @CreateSequenceSql;
END;
ELSE
BEGIN
    DECLARE @CurrentSequenceValue BIGINT =
        ISNULL(
            (
                SELECT CONVERT(BIGINT, current_value)
                FROM sys.sequences
                WHERE object_id = OBJECT_ID(N'dbo.Seq_Transacciones_OrdenBalance', N'SO')
            ),
            0
        );

    IF @CurrentSequenceValue < @NextOrdenBalance
    BEGIN
        DECLARE @RestartSequenceSql NVARCHAR(MAX) =
            N'ALTER SEQUENCE dbo.Seq_Transacciones_OrdenBalance RESTART WITH '
            + CONVERT(NVARCHAR(30), @NextOrdenBalance)
            + N';';

        EXEC sys.sp_executesql @RestartSequenceSql;
    END;
END;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.default_constraints dc
    INNER JOIN sys.columns c
        ON c.object_id = dc.parent_object_id
       AND c.column_id = dc.parent_column_id
    WHERE dc.parent_object_id = OBJECT_ID(N'dbo.Transacciones')
      AND c.name = N'OrdenBalance'
)
BEGIN
    ALTER TABLE dbo.Transacciones
        ADD CONSTRAINT DF_Transacciones_OrdenBalance
            DEFAULT (NEXT VALUE FOR dbo.Seq_Transacciones_OrdenBalance) FOR OrdenBalance;
END;

IF EXISTS
(
    SELECT 1
    FROM sys.columns
    WHERE object_id = OBJECT_ID(N'dbo.Transacciones')
      AND name = N'OrdenBalance'
      AND is_nullable = 1
)
BEGIN
    ALTER TABLE dbo.Transacciones
        ALTER COLUMN OrdenBalance BIGINT NOT NULL;
END;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.Transacciones')
      AND name = N'IX_Transacciones_RFC_Fecha_OrdenBalance_ID'
)
BEGIN
    CREATE INDEX IX_Transacciones_RFC_Fecha_OrdenBalance_ID
        ON dbo.Transacciones (RFC, Fecha, OrdenBalance, ID)
        INCLUDE (Facturado);
END;

COMMIT TRANSACTION;
GO

CREATE OR ALTER PROCEDURE [contabilidad].[REGISTROS_CONTABLES_FECHA_NIVELES]
    @startDate DATETIME,
    @endDate   DATETIME,
    @RFC       VARCHAR(50),
    @Nivel1    VARCHAR(50),
    @Nivel2    VARCHAR(50),
    @Nivel3    VARCHAR(50)
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @SaldoInicial MONEY = 0;

    SELECT
        @SaldoInicial = ISNULL(SUM(rc2.Debe - rc2.Haber), 0)
    FROM dbo.Registro_Contable rc2
    INNER JOIN dbo.Transacciones t2
        ON t2.ID = rc2.TransaccionID
    WHERE t2.RFC = @RFC
      AND t2.Fecha < @startDate
      AND (@Nivel1 = '*' OR rc2.Nivel1 = @Nivel1)
      AND (@Nivel2 = '*' OR rc2.Nivel2 = @Nivel2)
      AND (@Nivel3 = '*' OR rc2.Nivel3 = @Nivel3);

    ;WITH CTE_SortedResults AS
    (
        SELECT
            t.Fecha,
            t.OrdenBalance,
            t.Facturado,
            rc.id,
            rc.Nivel1 + '.' + rc.Nivel2 + '.' + rc.Nivel3 AS Cuenta,
            rc.Nombre_Cuenta,
            rc.Debe,
            rc.Haber,
            rc.Referencia,
            rc.Concepto,
            rc.TransaccionID AS Poliza,
            ROW_NUMBER() OVER (ORDER BY t.Fecha, t.OrdenBalance, t.ID, rc.id) AS RowNum
        FROM dbo.Registro_Contable rc
        INNER JOIN dbo.Transacciones t
            ON t.ID = rc.TransaccionID
        WHERE t.RFC = @RFC
          AND t.Fecha >= @startDate
          AND t.Fecha <= @endDate
          AND (@Nivel1 = '*' OR rc.Nivel1 = @Nivel1)
          AND (@Nivel2 = '*' OR rc.Nivel2 = @Nivel2)
          AND (@Nivel3 = '*' OR rc.Nivel3 = @Nivel3)
    )
    SELECT
        CTE.id,
        CTE.Fecha AS FechaOrden,
        CTE.OrdenBalance,
        FORMAT(CTE.Fecha, 'dd/MM/yy h:mmtt', 'es-MX') AS Fecha,
        CTE.Cuenta,
        CTE.Nombre_Cuenta,
        CTE.Concepto,
        FORMAT(CTE.Debe, 'C', 'es-MX') AS Debe,
        FORMAT(CTE.Haber, 'C', 'es-MX') AS Haber,
        FORMAT(
            @SaldoInicial
            + SUM(CTE.Debe - CTE.Haber) OVER (
                ORDER BY CTE.RowNum
                ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW
            ),
            'C',
            'es-MX'
        ) AS Balance,
        @SaldoInicial AS SaldoInicialNumerico,
        CTE.Poliza,
        CASE WHEN CTE.Facturado = 1 THEN NCHAR(10004) ELSE 'X' END AS Revisado,
        CTE.Referencia
    FROM CTE_SortedResults CTE
    ORDER BY CTE.Fecha DESC, CTE.OrdenBalance DESC, CTE.Poliza DESC, CTE.id DESC;
END;
GO
