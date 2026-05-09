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
            ROW_NUMBER() OVER (
                ORDER BY CONVERT(date, t.Fecha), t.OrdenBalance, t.Fecha, t.ID, rc.id
            ) AS RowNum
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
    ORDER BY CONVERT(date, CTE.Fecha) DESC, CTE.OrdenBalance DESC, CTE.Fecha DESC, CTE.Poliza DESC, CTE.id DESC;
END;
