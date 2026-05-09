CREATE OR ALTER PROCEDURE [bancos].[sp_Movimientos_Bancarios]
(
    @RFC         varchar(50),
    @AccountId   int = NULL,
    @Year        int,
    @Month       int,
    @TextFilter  nvarchar(200) = NULL,
    @Tolerancia  decimal(19,2) = 0.01,
    @SaldoBancoTolerancia decimal(19,2) = 1.00
)
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @FechaInicio date = DATEFROMPARTS(@Year, @Month, 1);
    DECLARE @FechaFinExclusiva date = DATEADD(MONTH, 1, @FechaInicio);

    ;WITH CuentasBancoFiltradas AS
    (
        SELECT
            cb.Cuenta_Banco_ID,
            cb.Cuenta_Contable_ID,
            cc.Nivel1,
            cc.Nivel2,
            cc.Nivel3
        FROM bancos.Cuentas_Banco AS cb
        LEFT JOIN dbo.CuentasContables AS cc
            ON cc.id = cb.Cuenta_Contable_ID
           AND cc.RFC = cb.RFC
        WHERE cb.RFC = @RFC
          AND (@AccountId IS NULL OR cb.Cuenta_Banco_ID = @AccountId)
    ),
    SaldoInicialContable AS
    (
        SELECT
            cbf.Cuenta_Banco_ID,
            CAST(ISNULL(
                (
                    SELECT SUM(rc.Debe - rc.Haber)
                    FROM dbo.Registro_Contable AS rc
                    INNER JOIN dbo.Transacciones AS t
                        ON t.ID = rc.TransaccionID
                    WHERE t.RFC = @RFC
                      AND t.Fecha < @FechaInicio
                      AND cbf.Nivel1 IS NOT NULL
                      AND rc.Nivel1 = cbf.Nivel1
                      AND rc.Nivel2 = cbf.Nivel2
                      AND rc.Nivel3 = cbf.Nivel3
                ),
                0
            ) AS decimal(19,2)) AS SaldoInicial
        FROM CuentasBancoFiltradas AS cbf
    ),
    LedgerRows AS
    (
        SELECT
            cbf.Cuenta_Banco_ID,
            t.ID AS TransaccionID,
            t.Fecha,
            t.OrdenBalance,
            rc.id AS RegistroContableId,
            rc.Debe,
            rc.Haber
        FROM CuentasBancoFiltradas AS cbf
        INNER JOIN dbo.Registro_Contable AS rc
            ON cbf.Nivel1 IS NOT NULL
           AND rc.Nivel1 = cbf.Nivel1
           AND rc.Nivel2 = cbf.Nivel2
           AND rc.Nivel3 = cbf.Nivel3
        INNER JOIN dbo.Transacciones AS t
            ON t.ID = rc.TransaccionID
        WHERE t.RFC = @RFC
          AND t.Fecha >= @FechaInicio
          AND t.Fecha < @FechaFinExclusiva
    ),
    LedgerNumbered AS
    (
        SELECT
            lr.*,
            ROW_NUMBER() OVER (
                PARTITION BY lr.Cuenta_Banco_ID
                ORDER BY CONVERT(date, lr.Fecha), lr.OrdenBalance, lr.Fecha, lr.TransaccionID, lr.RegistroContableId
            ) AS AccountingRowNum
        FROM LedgerRows AS lr
    ),
    LedgerRunning AS
    (
        SELECT
            ln.*,
            CAST(
                ISNULL(si.SaldoInicial, 0)
                + SUM(ln.Debe - ln.Haber) OVER (
                    PARTITION BY ln.Cuenta_Banco_ID
                    ORDER BY ln.AccountingRowNum
                    ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW
                )
            AS decimal(19,2)) AS AccountingRunningBalance
        FROM LedgerNumbered AS ln
        LEFT JOIN SaldoInicialContable AS si
            ON si.Cuenta_Banco_ID = ln.Cuenta_Banco_ID
    ),
    LedgerPolicyOrder AS
    (
        SELECT
            lr.Cuenta_Banco_ID,
            lr.TransaccionID,
            MIN(lr.AccountingRowNum) AS AccountingSequence,
            MAX(lr.AccountingRowNum) AS LastAccountingRowNum
        FROM LedgerRunning AS lr
        GROUP BY
            lr.Cuenta_Banco_ID,
            lr.TransaccionID
    ),
    LedgerPolicy AS
    (
        SELECT
            lpo.Cuenta_Banco_ID,
            lpo.TransaccionID,
            lpo.AccountingSequence,
            lr.AccountingRunningBalance
        FROM LedgerPolicyOrder AS lpo
        INNER JOIN LedgerRunning AS lr
            ON lr.Cuenta_Banco_ID = lpo.Cuenta_Banco_ID
           AND lr.TransaccionID = lpo.TransaccionID
           AND lr.AccountingRowNum = lpo.LastAccountingRowNum
    ),
    MovimientosBase AS
    (
        SELECT
            m.Movimiento_ID,
            m.Cuenta_Banco_ID,
            m.Dia,
            m.Secuencia_Diaria,
            m.Concepto,
            m.Tipo,
            m.Cargo,
            m.Abono,
            m.Saldo,
            m.Fecha_Carga,
            m.Nombre_Banco,
            m.Numero_Cuenta,
            m.Secuencia_Clave,
            m.Balance_OK,
            m.Transaccion_ID,
            cbf.Cuenta_Contable_ID,
            cbf.Nivel1,
            cbf.Nivel2,
            cbf.Nivel3
        FROM bancos.Movimientos AS m
        INNER JOIN CuentasBancoFiltradas AS cbf
            ON cbf.Cuenta_Banco_ID = m.Cuenta_Banco_ID
        WHERE m.RFC = @RFC
          AND m.Dia >= @FechaInicio
          AND m.Dia < @FechaFinExclusiva
          AND (@TextFilter IS NULL OR @TextFilter = N'' OR m.Concepto LIKE N'%' + @TextFilter + N'%')
    )
    SELECT
        m.Movimiento_ID AS MovimientoId,
        m.Dia,
        m.Secuencia_Diaria AS [Line],
        m.Concepto,
        m.Tipo,
        m.Cargo,
        m.Abono,
        m.Saldo,
        m.Fecha_Carga AS FechaCarga,
        m.Nombre_Banco AS NombreBanco,
        m.Numero_Cuenta AS NumeroCuenta,
        m.Secuencia_Clave AS SecuenciaClave,
        m.Transaccion_ID AS [Policy],
        COALESCE(NULLIF(LTRIM(RTRIM(iss.IssuesRaw)), N''), N'OK') AS Issues,
        t.Fecha AS PolicyDate,
        t.OrdenBalance,
        lp.AccountingSequence,
        ISNULL(m.Nivel1, '') AS BankAccountNivel1,
        ISNULL(m.Nivel2, '') AS BankAccountNivel2,
        ISNULL(m.Nivel3, '') AS BankAccountNivel3,
        CAST(ISNULL(rcBank.LineCount, 0) AS int) AS BankRegistroLineCount,
        CAST(ISNULL(rcBank.Debe, 0) AS decimal(19,2)) AS BankRegistroDebe,
        CAST(ISNULL(rcBank.Haber, 0) AS decimal(19,2)) AS BankRegistroHaber,
        lp.AccountingRunningBalance,
        CAST(
            CASE
                WHEN m.Saldo IS NULL OR lp.AccountingRunningBalance IS NULL THEN NULL
                ELSE m.Saldo - lp.AccountingRunningBalance
            END
        AS decimal(19,2)) AS BankAccountingVariance,
        CAST(
            CASE
                WHEN m.Saldo IS NOT NULL
                 AND lp.AccountingRunningBalance IS NOT NULL
                 AND ABS(m.Saldo - lp.AccountingRunningBalance) > @SaldoBancoTolerancia
                THEN 1 ELSE 0
            END
        AS bit) AS HasBankAccountingDifference,
        m.Balance_OK AS BalanceOk,
        CASE
            WHEN NULLIF(LTRIM(RTRIM(iss.IssuesRaw)), N'') IS NOT NULL THEN N'Hard'
            WHEN m.Saldo IS NOT NULL
             AND lp.AccountingRunningBalance IS NOT NULL
             AND ABS(m.Saldo - lp.AccountingRunningBalance) > @SaldoBancoTolerancia THEN N'Soft'
            ELSE N'OK'
        END AS AuditSeverity
    FROM MovimientosBase AS m
    LEFT JOIN dbo.Transacciones AS t
        ON t.ID = m.Transaccion_ID
    LEFT JOIN LedgerPolicy AS lp
        ON lp.Cuenta_Banco_ID = m.Cuenta_Banco_ID
       AND lp.TransaccionID = t.ID
    OUTER APPLY
    (
        SELECT
            SUM(rc.Debe)  AS Debe,
            SUM(rc.Haber) AS Haber,
            COUNT(*)      AS LineCount
        FROM dbo.Registro_Contable AS rc
        WHERE t.ID IS NOT NULL
          AND rc.TransaccionID = t.ID
          AND m.Nivel1 IS NOT NULL
          AND rc.Nivel1 = m.Nivel1
          AND rc.Nivel2 = m.Nivel2
          AND rc.Nivel3 = m.Nivel3
    ) AS rcBank
    OUTER APPLY
    (
        SELECT
            SUM(rc.Debe)  AS Debe,
            SUM(rc.Haber) AS Haber,
            COUNT(*)      AS LineCount
        FROM dbo.Registro_Contable AS rc
        WHERE t.ID IS NOT NULL
          AND rc.TransaccionID = t.ID
    ) AS rcTot
    OUTER APPLY
    (
        SELECT
            CAST(CASE WHEN ISNULL(m.Cargo, 0) > 0 THEN ISNULL(m.Cargo, 0) ELSE 0 END AS decimal(19,2)) AS EsperadoDebeBanco,
            CAST(CASE WHEN ISNULL(m.Abono, 0) > 0 THEN ISNULL(m.Abono, 0) ELSE 0 END AS decimal(19,2)) AS EsperadoHaberBanco,
            CAST(
                CASE
                    WHEN t.ID IS NULL OR m.Cuenta_Contable_ID IS NULL OR m.Nivel1 IS NULL THEN 0
                    WHEN ABS(ISNULL(rcBank.Debe, 0)  - (CASE WHEN ISNULL(m.Cargo, 0) > 0 THEN ISNULL(m.Cargo, 0) ELSE 0 END)) > @Tolerancia
                      OR ABS(ISNULL(rcBank.Haber, 0) - (CASE WHEN ISNULL(m.Abono, 0) > 0 THEN ISNULL(m.Abono, 0) ELSE 0 END)) > @Tolerancia
                    THEN 1 ELSE 0
                END
            AS bit) AS EsMismatch
    ) AS exp
    OUTER APPLY
    (
        SELECT
            CONCAT_WS(N',',
                CASE WHEN ISNULL(m.Cargo, 0) = 0 AND ISNULL(m.Abono, 0) = 0 THEN N'MOVIMIENTO_SIN_IMPORTE' END,
                CASE WHEN m.Transaccion_ID IS NULL THEN N'SIN_TRANSACCION' END,
                CASE WHEN m.Transaccion_ID IS NOT NULL AND t.ID IS NULL THEN N'TRANSACCION_NO_EXISTE' END,
                CASE WHEN t.ID IS NOT NULL AND t.RFC <> @RFC THEN N'RFC_TRANSACCION_DIFERENTE' END,
                CASE WHEN m.Cuenta_Contable_ID IS NULL THEN N'CTA_BANCO_SIN_CUENTA_CONTABLE' END,
                CASE WHEN m.Cuenta_Contable_ID IS NOT NULL AND m.Nivel1 IS NULL THEN N'CUENTA_CONTABLE_ID_INVALIDO' END,
                CASE WHEN t.ID IS NOT NULL AND m.Nivel1 IS NOT NULL AND ISNULL(rcBank.LineCount, 0) = 0
                     THEN N'SIN_LINEA_BANCO_EN_REGISTRO_CONTABLE' END,
                CASE WHEN t.ID IS NOT NULL AND ISNULL(rcTot.LineCount, 0) > 0 AND ISNULL(rcTot.LineCount, 0) < 2
                     THEN N'POLIZA_CON_MENOS_DE_2_LINEAS' END,
                CASE WHEN t.ID IS NOT NULL AND ABS(ISNULL(rcTot.Debe, 0) - ISNULL(rcTot.Haber, 0)) > @Tolerancia
                     THEN N'POLIZA_DESCUADRADA' END,
                CASE WHEN t.ID IS NOT NULL AND m.Nivel1 IS NOT NULL AND ISNULL(rcBank.LineCount, 0) > 0 AND exp.EsMismatch = 1
                     THEN N'IMPORTE_NO_CUADRA' END,
                CASE WHEN ISNULL(rcBank.LineCount, 0) > 1 THEN N'MULTIPLES_LINEAS_BANCO' END
            ) AS IssuesRaw
    ) AS iss
    ORDER BY
        m.Secuencia_Clave DESC,
        m.Movimiento_ID DESC;
END;
