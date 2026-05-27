SET XACT_ABORT ON;
GO

IF OBJECT_ID(N'bancos.Movimiento_Transaccion', N'U') IS NULL
BEGIN
    CREATE TABLE bancos.Movimiento_Transaccion
    (
        Movimiento_ID  bigint        NOT NULL,
        Transaccion_ID int           NOT NULL,
        Debe           decimal(19,2) NOT NULL CONSTRAINT DF_Movimiento_Transaccion_Debe DEFAULT (0),
        Haber          decimal(19,2) NOT NULL CONSTRAINT DF_Movimiento_Transaccion_Haber DEFAULT (0),
        CreatedAt      datetime2(7)  NOT NULL CONSTRAINT DF_Movimiento_Transaccion_CreatedAt DEFAULT SYSUTCDATETIME(),
        CreatedBy      nvarchar(128) NULL,
        UpdatedAt      datetime2(7)  NOT NULL CONSTRAINT DF_Movimiento_Transaccion_UpdatedAt DEFAULT SYSUTCDATETIME(),
        UpdatedBy      nvarchar(128) NULL,
        CONSTRAINT PK_Movimiento_Transaccion PRIMARY KEY (Movimiento_ID, Transaccion_ID),
        CONSTRAINT CK_Movimiento_Transaccion_Amount CHECK
        (
            (Debe > 0 AND Haber = 0)
            OR (Debe = 0 AND Haber > 0)
        ),
        CONSTRAINT FK_Movimiento_Transaccion_Movimientos FOREIGN KEY (Movimiento_ID)
            REFERENCES bancos.Movimientos (Movimiento_ID),
        CONSTRAINT FK_Movimiento_Transaccion_Transacciones FOREIGN KEY (Transaccion_ID)
            REFERENCES dbo.Transacciones (ID)
    );

    CREATE INDEX IX_Movimiento_Transaccion_Transaccion
        ON bancos.Movimiento_Transaccion (Transaccion_ID, Movimiento_ID)
        INCLUDE (Debe, Haber);
END;
GO

IF COL_LENGTH(N'bancos.Movimientos', N'Transaccion_ID') IS NOT NULL
BEGIN
    INSERT INTO bancos.Movimiento_Transaccion
        (Movimiento_ID, Transaccion_ID, Debe, Haber, CreatedAt, CreatedBy, UpdatedAt, UpdatedBy)
    SELECT
        M.Movimiento_ID,
        M.Transaccion_ID,
        CASE WHEN ISNULL(M.Cargo, 0) > 0 THEN ABS(M.Cargo) ELSE 0 END AS Debe,
        CASE WHEN ISNULL(M.Abono, 0) > 0 THEN ABS(M.Abono) ELSE 0 END AS Haber,
        SYSUTCDATETIME(),
        N'backfill',
        SYSUTCDATETIME(),
        N'backfill'
    FROM bancos.Movimientos AS M
    WHERE M.Transaccion_ID IS NOT NULL
      AND NOT EXISTS (
          SELECT 1
          FROM bancos.Movimiento_Transaccion AS MT
          WHERE MT.Movimiento_ID = M.Movimiento_ID
            AND MT.Transaccion_ID = M.Transaccion_ID
      )
      AND (
          (ISNULL(M.Cargo, 0) > 0 AND ISNULL(M.Abono, 0) = 0)
          OR (ISNULL(M.Abono, 0) > 0 AND ISNULL(M.Cargo, 0) = 0)
      );
END;
GO

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
    ),
    LinkRows AS
    (
        SELECT
            mt.Movimiento_ID,
            mt.Transaccion_ID,
            mt.Debe,
            mt.Haber,
            t.Fecha,
            t.OrdenBalance,
            ROW_NUMBER() OVER (
                PARTITION BY mt.Movimiento_ID
                ORDER BY t.Fecha, t.OrdenBalance, t.ID
            ) AS LinkRank
        FROM bancos.Movimiento_Transaccion AS mt
        INNER JOIN dbo.Transacciones AS t
            ON t.ID = mt.Transaccion_ID
    ),
    LinkAgg AS
    (
        SELECT
            lr.Movimiento_ID,
            COUNT(*) AS PolicyCount,
            SUM(lr.Debe) AS LinkedDebe,
            SUM(lr.Haber) AS LinkedHaber,
            STRING_AGG(CONVERT(varchar(20), lr.Transaccion_ID), ', ') WITHIN GROUP (ORDER BY lr.Fecha, lr.OrdenBalance, lr.Transaccion_ID) AS LinkedPolicyIds,
            STRING_AGG(CONVERT(varchar(20), lr.Transaccion_ID) + ':' + CONVERT(varchar(40), CAST(CASE WHEN lr.Debe > 0 THEN lr.Debe ELSE lr.Haber END AS decimal(19,2))), ', ') WITHIN GROUP (ORDER BY lr.Fecha, lr.OrdenBalance, lr.Transaccion_ID) AS LinkedPolicySummary
        FROM LinkRows AS lr
        GROUP BY lr.Movimiento_ID
    ),
    PrimaryLink AS
    (
        SELECT *
        FROM LinkRows
        WHERE LinkRank = 1
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
        pl.Transaccion_ID AS [Policy],
        CAST(ISNULL(la.PolicyCount, 0) AS int) AS PolicyCount,
        ISNULL(la.LinkedPolicyIds, '') AS LinkedPolicyIds,
        ISNULL(la.LinkedPolicySummary, '') AS LinkedPolicySummary,
        CAST(ISNULL(la.LinkedDebe, 0) AS decimal(19,2)) AS LinkedDebe,
        CAST(ISNULL(la.LinkedHaber, 0) AS decimal(19,2)) AS LinkedHaber,
        COALESCE(NULLIF(LTRIM(RTRIM(iss.IssuesRaw)), N''), N'OK') AS Issues,
        t.Fecha AS PolicyDate,
        t.OrdenBalance,
        lp.AccountingSequence,
        ISNULL(m.Nivel1, '') AS BankAccountNivel1,
        ISNULL(m.Nivel2, '') AS BankAccountNivel2,
        ISNULL(m.Nivel3, '') AS BankAccountNivel3,
        CAST(ISNULL(la.PolicyCount, 0) AS int) AS BankRegistroLineCount,
        CAST(ISNULL(la.LinkedDebe, 0) AS decimal(19,2)) AS BankRegistroDebe,
        CAST(ISNULL(la.LinkedHaber, 0) AS decimal(19,2)) AS BankRegistroHaber,
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
    LEFT JOIN LinkAgg AS la
        ON la.Movimiento_ID = m.Movimiento_ID
    LEFT JOIN PrimaryLink AS pl
        ON pl.Movimiento_ID = m.Movimiento_ID
    LEFT JOIN dbo.Transacciones AS t
        ON t.ID = pl.Transaccion_ID
    LEFT JOIN LedgerPolicy AS lp
        ON lp.Cuenta_Banco_ID = m.Cuenta_Banco_ID
       AND lp.TransaccionID = t.ID
    OUTER APPLY
    (
        SELECT
            CAST(CASE WHEN ISNULL(m.Cargo, 0) > 0 THEN ISNULL(m.Cargo, 0) ELSE 0 END AS decimal(19,2)) AS EsperadoDebeBanco,
            CAST(CASE WHEN ISNULL(m.Abono, 0) > 0 THEN ISNULL(m.Abono, 0) ELSE 0 END AS decimal(19,2)) AS EsperadoHaberBanco
    ) AS exp
    OUTER APPLY
    (
        SELECT TOP (1) 1 AS HasMissingBankLine
        FROM bancos.Movimiento_Transaccion AS mt
        WHERE mt.Movimiento_ID = m.Movimiento_ID
          AND NOT EXISTS (
              SELECT 1
              FROM dbo.Registro_Contable AS rc
              WHERE rc.TransaccionID = mt.Transaccion_ID
                AND rc.Nivel1 = m.Nivel1
                AND rc.Nivel2 = m.Nivel2
                AND rc.Nivel3 = m.Nivel3
          )
    ) AS missingRc
    OUTER APPLY
    (
        SELECT
            CONCAT_WS(N',',
                CASE WHEN ISNULL(m.Cargo, 0) = 0 AND ISNULL(m.Abono, 0) = 0 THEN N'MOVIMIENTO_SIN_IMPORTE' END,
                CASE WHEN ISNULL(la.PolicyCount, 0) = 0 THEN N'SIN_TRANSACCION' END,
                CASE WHEN m.Cuenta_Contable_ID IS NULL THEN N'CTA_BANCO_SIN_CUENTA_CONTABLE' END,
                CASE WHEN m.Cuenta_Contable_ID IS NOT NULL AND m.Nivel1 IS NULL THEN N'CUENTA_CONTABLE_ID_INVALIDO' END,
                CASE WHEN ISNULL(la.PolicyCount, 0) > 0 AND missingRc.HasMissingBankLine = 1
                     THEN N'SIN_LINEA_BANCO_EN_REGISTRO_CONTABLE' END,
                CASE WHEN ISNULL(la.PolicyCount, 0) > 0
                       AND (ABS(ISNULL(la.LinkedDebe, 0) - exp.EsperadoDebeBanco) > @Tolerancia
                            OR ABS(ISNULL(la.LinkedHaber, 0) - exp.EsperadoHaberBanco) > @Tolerancia)
                     THEN N'IMPORTE_NO_CUADRA' END
            ) AS IssuesRaw
    ) AS iss
    ORDER BY
        m.Secuencia_Clave DESC,
        m.Movimiento_ID DESC;
END;
GO

CREATE OR ALTER PROCEDURE [bancos].[sp_Movimientos_Bancarios_Con_Inconsistencias]
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

    DECLARE @Rows TABLE
    (
        MovimientoId bigint,
        Dia date,
        [Line] int,
        Concepto varchar(500),
        Tipo char(1),
        Cargo decimal(19,2),
        Abono decimal(19,2),
        Saldo decimal(19,2),
        FechaCarga datetime2(7),
        NombreBanco varchar(100),
        NumeroCuenta varchar(50),
        SecuenciaClave bigint,
        [Policy] int NULL,
        PolicyCount int,
        LinkedPolicyIds varchar(max),
        LinkedPolicySummary varchar(max),
        LinkedDebe decimal(19,2),
        LinkedHaber decimal(19,2),
        Issues nvarchar(max),
        PolicyDate datetime NULL,
        OrdenBalance bigint NULL,
        AccountingSequence int NULL,
        BankAccountNivel1 varchar(50),
        BankAccountNivel2 varchar(50),
        BankAccountNivel3 varchar(50),
        BankRegistroLineCount int,
        BankRegistroDebe decimal(19,2),
        BankRegistroHaber decimal(19,2),
        AccountingRunningBalance decimal(19,2) NULL,
        BankAccountingVariance decimal(19,2) NULL,
        HasBankAccountingDifference bit,
        BalanceOk bit NULL,
        AuditSeverity nvarchar(20)
    );

    INSERT INTO @Rows
    EXEC bancos.sp_Movimientos_Bancarios
        @RFC = @RFC,
        @AccountId = @AccountId,
        @Year = @Year,
        @Month = @Month,
        @TextFilter = @TextFilter,
        @Tolerancia = @Tolerancia,
        @SaldoBancoTolerancia = @SaldoBancoTolerancia;

    SELECT *
    FROM @Rows
    WHERE ISNULL(Issues, N'OK') <> N'OK'
       OR ISNULL(HasBankAccountingDifference, 0) = 1
       OR ISNULL(AuditSeverity, N'OK') <> N'OK'
    ORDER BY SecuenciaClave DESC, MovimientoId DESC;
END;
GO

IF COL_LENGTH(N'bancos.Movimientos', N'Transaccion_ID') IS NOT NULL
BEGIN
    DECLARE @dropSql nvarchar(max) = N'';

    SELECT @dropSql = @dropSql + N'ALTER TABLE '
        + QUOTENAME(OBJECT_SCHEMA_NAME(fk.parent_object_id)) + N'.' + QUOTENAME(OBJECT_NAME(fk.parent_object_id))
        + N' DROP CONSTRAINT ' + QUOTENAME(fk.name) + N';' + CHAR(13)
    FROM sys.foreign_key_columns AS fkc
    INNER JOIN sys.foreign_keys AS fk
        ON fk.object_id = fkc.constraint_object_id
    INNER JOIN sys.columns AS c
        ON c.object_id = fkc.parent_object_id
       AND c.column_id = fkc.parent_column_id
    WHERE fkc.parent_object_id = OBJECT_ID(N'bancos.Movimientos')
      AND c.name = N'Transaccion_ID';

    IF @dropSql <> N''
    BEGIN
        EXEC sp_executesql @dropSql;
    END;

    SET @dropSql = N'';

    SELECT @dropSql = @dropSql + N'DROP INDEX ' + QUOTENAME(i.name)
        + N' ON bancos.Movimientos;' + CHAR(13)
    FROM sys.indexes AS i
    WHERE i.object_id = OBJECT_ID(N'bancos.Movimientos')
      AND i.index_id > 0
      AND EXISTS (
          SELECT 1
          FROM sys.index_columns AS ic
          INNER JOIN sys.columns AS c
              ON c.object_id = ic.object_id
             AND c.column_id = ic.column_id
          WHERE ic.object_id = i.object_id
            AND ic.index_id = i.index_id
            AND c.name = N'Transaccion_ID'
      );

    IF @dropSql <> N''
    BEGIN
        EXEC sp_executesql @dropSql;
    END;

    ALTER TABLE bancos.Movimientos DROP COLUMN Transaccion_ID;
END;
GO
