CREATE OR ALTER PROCEDURE [reporteFinanciero].[Reporte_Salud_Empresa]
    @AnioInicio int,
    @MesInicio tinyint,
    @AnioFin int,
    @MesFin tinyint,
    @RFC varchar(50) = 'OHM191112Q26',
    @IncluirHabitacionesNoRentables bit = 0
AS
BEGIN
    SET NOCOUNT ON;

    IF @AnioInicio IS NULL OR @AnioInicio NOT BETWEEN 1900 AND 9999
    BEGIN
        RAISERROR('El parametro @AnioInicio debe estar entre 1900 y 9999.', 16, 1);
        RETURN;
    END;

    IF @AnioFin IS NULL OR @AnioFin NOT BETWEEN 1900 AND 9999
    BEGIN
        RAISERROR('El parametro @AnioFin debe estar entre 1900 y 9999.', 16, 1);
        RETURN;
    END;

    IF @MesInicio IS NULL OR @MesInicio NOT BETWEEN 1 AND 12
    BEGIN
        RAISERROR('El parametro @MesInicio debe estar entre 1 y 12.', 16, 1);
        RETURN;
    END;

    IF @MesFin IS NULL OR @MesFin NOT BETWEEN 1 AND 12
    BEGIN
        RAISERROR('El parametro @MesFin debe estar entre 1 y 12.', 16, 1);
        RETURN;
    END;

    DECLARE @RfcTrim varchar(50) = NULLIF(LTRIM(RTRIM(@RFC)), '');
    IF @RfcTrim IS NULL
        SET @RfcTrim = 'OHM191112Q26';

    DECLARE @PeriodoInicio date = DATEFROMPARTS(@AnioInicio, @MesInicio, 1);
    DECLARE @PeriodoFinMes date = DATEFROMPARTS(@AnioFin, @MesFin, 1);

    IF @PeriodoFinMes < @PeriodoInicio
    BEGIN
        RAISERROR('El mes final debe ser mayor o igual al mes inicial.', 16, 1);
        RETURN;
    END;

    DECLARE @PeriodoFinExcl date = DATEADD(MONTH, 1, @PeriodoFinMes);
    DECLARE @PeriodoMeses int = DATEDIFF(MONTH, @PeriodoInicio, @PeriodoFinExcl);
    DECLARE @PeriodoAnteriorInicio date = DATEADD(MONTH, -@PeriodoMeses, @PeriodoInicio);
    DECLARE @PeriodoAnteriorFinExcl date = @PeriodoInicio;
    DECLARE @PeriodoAnteriorFinMes date = DATEADD(MONTH, -1, @PeriodoAnteriorFinExcl);
    DECLARE @PeriodoAnioAnteriorInicio date = DATEADD(YEAR, -1, @PeriodoInicio);
    DECLARE @PeriodoAnioAnteriorFinExcl date = DATEADD(YEAR, -1, @PeriodoFinExcl);
    DECLARE @PeriodoAnioAnteriorFinMes date = DATEADD(MONTH, -1, @PeriodoAnioAnteriorFinExcl);
    DECLARE @AnioAcumuladoInicio date = DATEFROMPARTS(YEAR(@PeriodoFinMes), 1, 1);
    DECLARE @AnioAnteriorAcumuladoInicio date = DATEADD(YEAR, -1, @AnioAcumuladoInicio);
    DECLARE @AnioAnteriorAcumuladoFinExcl date = DATEADD(YEAR, -1, @PeriodoFinExcl);

    DECLARE @PeriodoLabel varchar(40) =
        CASE
            WHEN @PeriodoInicio = @PeriodoFinMes THEN CONVERT(char(7), @PeriodoInicio, 120)
            ELSE CONCAT(CONVERT(char(7), @PeriodoInicio, 120), ' a ', CONVERT(char(7), @PeriodoFinMes, 120))
        END;

    DECLARE @PeriodoAnteriorLabel varchar(40) =
        CASE
            WHEN @PeriodoAnteriorInicio = @PeriodoAnteriorFinMes THEN CONVERT(char(7), @PeriodoAnteriorInicio, 120)
            ELSE CONCAT(CONVERT(char(7), @PeriodoAnteriorInicio, 120), ' a ', CONVERT(char(7), @PeriodoAnteriorFinMes, 120))
        END;

    DECLARE @PeriodoAnioAnteriorLabel varchar(40) =
        CASE
            WHEN @PeriodoAnioAnteriorInicio = @PeriodoAnioAnteriorFinMes THEN CONVERT(char(7), @PeriodoAnioAnteriorInicio, 120)
            ELSE CONCAT(CONVERT(char(7), @PeriodoAnioAnteriorInicio, 120), ' a ', CONVERT(char(7), @PeriodoAnioAnteriorFinMes, 120))
        END;

    CREATE TABLE #Periods
    (
        PeriodKey int NOT NULL PRIMARY KEY,
        PeriodLabel varchar(40) NOT NULL,
        PeriodScope varchar(30) NOT NULL,
        DateStart date NOT NULL,
        DateEndExcl date NOT NULL,
        SortOrder int NOT NULL
    );

    INSERT INTO #Periods
        (PeriodKey, PeriodLabel, PeriodScope, DateStart, DateEndExcl, SortOrder)
    VALUES
        (1, @PeriodoLabel, 'Periodo seleccionado', @PeriodoInicio, @PeriodoFinExcl, 1),
        (2, @PeriodoAnteriorLabel, 'Periodo anterior', @PeriodoAnteriorInicio, @PeriodoAnteriorFinExcl, 2),
        (3, @PeriodoAnioAnteriorLabel, 'Mismo periodo ano anterior', @PeriodoAnioAnteriorInicio, @PeriodoAnioAnteriorFinExcl, 3),
        (4, CONCAT(CONVERT(char(4), YEAR(@PeriodoFinMes)), ' acumulado'), 'Acumulado del ano', @AnioAcumuladoInicio, @PeriodoFinExcl, 4),
        (5, CONCAT(CONVERT(char(4), YEAR(@PeriodoFinMes) - 1), ' acumulado'), 'Acumulado ano anterior', @AnioAnteriorAcumuladoInicio, @AnioAnteriorAcumuladoFinExcl, 5);

    CREATE TABLE #RentableRooms
    (
        RoomName varchar(50) NOT NULL PRIMARY KEY,
        OwnerID int NULL,
        BasePrice decimal(19, 4) NULL
    );

    INSERT INTO #RentableRooms
        (RoomName, OwnerID, BasePrice)
    SELECT
        r.ROOM_NAME,
        r.OWNER_ID,
        CAST(r.BASE_PRICE AS decimal(19, 4)) AS BasePrice
    FROM dbo.ROOM AS r
    WHERE
        UPPER(LTRIM(RTRIM(r.ROOM_TYPE))) = 'SUITE'
        AND (
            @IncluirHabitacionesNoRentables = 1
            OR r.ROOM_NAME NOT IN ('OFICINA ALTA VISTA', 'PROTOTIPO INDIVIDUAL')
        );

    SELECT
        p.PeriodKey,
        p.PeriodLabel,
        p.PeriodScope,
        rr.RoomName,
        rr.OwnerID,
        rr.BasePrice,
        rc.id AS RoomCalendarID,
        rc.ROOM_DATE AS RoomDate,
        rc.IS_LOCKED AS IsLocked,
        rc.LOCK_DESCRIPTION AS LockDescription,
        parsed.ReservationID,
        r.ID AS MatchedReservationID,
        CAST(rc.PRECIO AS decimal(19, 4)) AS Precio,
        CAST(rc.PORCENTAJE_ARRENDAMIENTO AS decimal(19, 6)) AS PorcentajeArrendamiento
    INTO #CalendarRows
    FROM #Periods AS p
    INNER JOIN dbo.ROOM_CALENDAR AS rc
        ON rc.ROOM_DATE >= p.DateStart
       AND rc.ROOM_DATE < p.DateEndExcl
    INNER JOIN #RentableRooms AS rr
        ON rr.RoomName = rc.ROOM
    CROSS APPLY
    (
        SELECT TRY_CONVERT(int, NULLIF(LTRIM(RTRIM(rc.LOCK_DESCRIPTION)), '')) AS ReservationID
    ) AS parsed
    LEFT JOIN dbo.RESERVATION AS r
        ON r.ID = parsed.ReservationID;

    CREATE INDEX IX_CalendarRows_Period_Room
        ON #CalendarRows (PeriodKey, RoomName);

    SELECT
        cr.PeriodKey,
        COUNT(*) AS AvailableNights,
        SUM(CASE WHEN cr.IsLocked = 1
                  AND cr.ReservationID IS NOT NULL
                  AND cr.Precio > 0
                 THEN 1 ELSE 0 END) AS OccupiedNights,
        COUNT(DISTINCT CASE WHEN cr.IsLocked = 1
                              AND cr.ReservationID IS NOT NULL
                              AND cr.Precio > 0
                            THEN cr.RoomName END) AS SuitesWithSales,
        SUM(CASE WHEN cr.IsLocked = 1
                  AND cr.ReservationID IS NOT NULL
                  AND cr.Precio > 0
                 THEN cr.Precio ELSE 0 END) AS RoomRevenue,
        SUM(CASE WHEN cr.IsLocked = 1
                  AND cr.ReservationID IS NOT NULL
                  AND cr.Precio > 0
                 THEN cr.Precio * cr.PorcentajeArrendamiento ELSE 0 END) AS EstimatedOwnerShare
    INTO #SalesAgg
    FROM #CalendarRows AS cr
    GROUP BY cr.PeriodKey;

    SELECT
        p.PeriodKey,
        r.ID AS ReservationID,
        CAST(r.TOTAL_PRICE AS decimal(19, 4)) AS TotalPrice
    INTO #ReservationCohort
    FROM #Periods AS p
    INNER JOIN dbo.RESERVATION AS r
        ON r.CHECKIN >= p.DateStart
       AND r.CHECKIN < p.DateEndExcl;

    CREATE INDEX IX_ReservationCohort_Period_Reservation
        ON #ReservationCohort (PeriodKey, ReservationID);

    SELECT
        rc.PeriodKey,
        rc.ReservationID,
        MAX(rc.TotalPrice) AS TotalPrice,
        SUM(CASE WHEN rt.Amount > 0
                  AND t.ID IS NOT NULL
                  AND posted.TransaccionID IS NOT NULL
                 THEN CAST(rt.Amount AS decimal(19, 4)) ELSE 0 END) AS PostedPositivePayments,
        COUNT(DISTINCT CASE WHEN rt.Amount > 0
                              AND t.ID IS NOT NULL
                              AND posted.TransaccionID IS NOT NULL
                            THEN rt.TransaccionID END) AS PostedPaymentTransactions,
        SUM(CASE WHEN rt.Amount > 0
                  AND t.ID IS NOT NULL
                  AND posted.TransaccionID IS NULL
                 THEN CAST(rt.Amount AS decimal(19, 4)) ELSE 0 END) AS UnpostedPositivePayments,
        COUNT(DISTINCT CASE WHEN rt.Amount > 0
                              AND t.ID IS NOT NULL
                              AND posted.TransaccionID IS NULL
                            THEN rt.TransaccionID END) AS UnpostedPaymentTransactions
    INTO #ReservationPayments
    FROM #ReservationCohort AS rc
    LEFT JOIN dbo.Reservation_Transacciones AS rt
        ON rt.ReservationID = rc.ReservationID
    LEFT JOIN dbo.Transacciones AS t
        ON t.ID = rt.TransaccionID
    LEFT JOIN
    (
        SELECT DISTINCT TransaccionID
        FROM dbo.Registro_Contable
    ) AS posted
        ON posted.TransaccionID = rt.TransaccionID
    GROUP BY
        rc.PeriodKey,
        rc.ReservationID;

    SELECT
        rp.PeriodKey,
        COUNT(*) AS ReservationCount,
        SUM(rp.TotalPrice) AS ReservationTotal,
        SUM(ISNULL(rp.PostedPositivePayments, 0)) AS PostedCollections,
        SUM(CASE
                WHEN rp.TotalPrice - ISNULL(rp.PostedPositivePayments, 0) > 0.01
                THEN rp.TotalPrice - ISNULL(rp.PostedPositivePayments, 0)
                ELSE 0
            END) AS OutstandingCollections,
        SUM(ISNULL(rp.UnpostedPositivePayments, 0)) AS UnpostedCollections,
        SUM(ISNULL(rp.PostedPaymentTransactions, 0)) AS PostedPaymentTransactions,
        SUM(ISNULL(rp.UnpostedPaymentTransactions, 0)) AS UnpostedPaymentTransactions
    INTO #CollectionsAgg
    FROM #ReservationPayments AS rp
    GROUP BY rp.PeriodKey;

    SELECT
        p.PeriodKey,
        SUM(CASE WHEN rc.Nivel1 IN ('401', '403') THEN rc.Haber - rc.Debe ELSE 0 END) AS GrossIncome,
        SUM(CASE WHEN rc.Nivel1 = '402' THEN rc.Debe - rc.Haber ELSE 0 END) AS SalesReturns,
        SUM(CASE WHEN rc.Nivel1 IN ('501', '502', '503', '504') THEN rc.Debe - rc.Haber ELSE 0 END) AS CostOfSales,
        SUM(CASE WHEN rc.Nivel1 IN ('602', '603', '604', '605') THEN rc.Debe - rc.Haber ELSE 0 END) AS OperatingExpenses,
        SUM(CASE WHEN rc.Nivel1 = '701' THEN rc.Debe - rc.Haber ELSE 0 END) AS FinancialExpenses,
        SUM(CASE WHEN rc.Nivel1 = '703' THEN rc.Debe - rc.Haber ELSE 0 END) AS OtherExpenses,
        SUM(CASE WHEN rc.Nivel1 = '704' THEN rc.Haber - rc.Debe ELSE 0 END) AS OtherIncome,
        SUM(CASE WHEN rc.Nivel1 = '611' THEN rc.Debe - rc.Haber ELSE 0 END) AS Taxes
    INTO #FinancialAgg
    FROM #Periods AS p
    INNER JOIN dbo.Transacciones AS t
        ON t.Fecha >= p.DateStart
       AND t.Fecha < p.DateEndExcl
       AND t.RFC = @RfcTrim
    INNER JOIN dbo.Registro_Contable AS rc
        ON rc.TransaccionID = t.ID
       AND rc.Nivel1 IN ('401', '402', '403', '501', '502', '503', '504', '602', '603', '604', '605', '701', '703', '704', '611')
       AND UPPER(ISNULL(rc.Nombre_Cuenta, '')) NOT LIKE '%PENDIENTES DE REGISTRO%'
    GROUP BY p.PeriodKey;

    SELECT
        p.PeriodKey,
        SUM(CASE WHEN t.Fecha >= p.DateStart AND t.Fecha < p.DateEndExcl THEN rc.Debe ELSE 0 END) AS CashIn,
        SUM(CASE WHEN t.Fecha >= p.DateStart AND t.Fecha < p.DateEndExcl THEN rc.Haber ELSE 0 END) AS CashOut,
        SUM(CASE WHEN t.Fecha < p.DateStart THEN rc.Debe - rc.Haber ELSE 0 END) AS OpeningCashBalance,
        SUM(CASE WHEN t.Fecha < p.DateEndExcl THEN rc.Debe - rc.Haber ELSE 0 END) AS ClosingCashBalance,
        COUNT(DISTINCT CASE WHEN t.Fecha >= p.DateStart AND t.Fecha < p.DateEndExcl THEN t.ID END) AS CashTransactionCount
    INTO #CashAgg
    FROM #Periods AS p
    INNER JOIN dbo.Transacciones AS t
        ON t.Fecha < p.DateEndExcl
       AND t.RFC = @RfcTrim
    INNER JOIN dbo.Registro_Contable AS rc
        ON rc.TransaccionID = t.ID
       AND rc.Nivel1 IN ('101', '102')
    GROUP BY p.PeriodKey;

    SELECT
        p.PeriodKey,
        rc.Nivel1,
        rc.Nivel2,
        rc.Nivel3,
        rc.Nombre_Cuenta,
        COUNT(DISTINCT t.ID) AS TransactionCount,
        SUM(rc.Debe) AS PendingDebe,
        SUM(rc.Haber) AS PendingHaber,
        SUM(CASE
                WHEN rc.Nivel1 IN ('401', '403', '704') THEN rc.Haber - rc.Debe
                ELSE rc.Debe - rc.Haber
            END) AS PendingNetEffect
    INTO #PendingBankAgg
    FROM #Periods AS p
    INNER JOIN dbo.Transacciones AS t
        ON t.Fecha >= p.DateStart
       AND t.Fecha < p.DateEndExcl
       AND t.RFC = @RfcTrim
    INNER JOIN dbo.Registro_Contable AS rc
        ON rc.TransaccionID = t.ID
       AND UPPER(ISNULL(rc.Nombre_Cuenta, '')) LIKE '%PENDIENTES DE REGISTRO%'
    GROUP BY
        p.PeriodKey,
        rc.Nivel1,
        rc.Nivel2,
        rc.Nivel3,
        rc.Nombre_Cuenta;

    SELECT
        PeriodKey,
        SUM(PendingDebe) AS PendingDebe,
        SUM(PendingHaber) AS PendingHaber,
        SUM(PendingNetEffect) AS PendingNetEffect
    INTO #PendingBankByPeriod
    FROM #PendingBankAgg
    GROUP BY PeriodKey;

    SELECT
        'Indicadores ejecutivos' AS ResultSetName,
        p.SortOrder,
        p.PeriodLabel,
        p.PeriodScope,
        CAST(p.DateStart AS datetime) AS PeriodStart,
        DATEADD(SECOND, -1, CAST(p.DateEndExcl AS datetime)) AS PeriodEnd,
        rr.RentableSuites,
        ISNULL(s.AvailableNights, 0) AS AvailableNights,
        ISNULL(s.OccupiedNights, 0) AS OccupiedNights,
        CAST(100.0 * ISNULL(s.OccupiedNights, 0) / NULLIF(ISNULL(s.AvailableNights, 0), 0) AS decimal(9, 2)) AS OccupancyPct,
        CAST(ISNULL(s.RoomRevenue, 0) AS decimal(19, 2)) AS RoomRevenue,
        CAST(ISNULL(s.RoomRevenue, 0) / NULLIF(ISNULL(s.OccupiedNights, 0), 0) AS decimal(19, 2)) AS ADR,
        CAST(ISNULL(s.RoomRevenue, 0) / NULLIF(ISNULL(s.AvailableNights, 0), 0) AS decimal(19, 2)) AS RevPAR,
        ISNULL(c.ReservationCount, 0) AS ReservationCount,
        CAST(ISNULL(c.ReservationTotal, 0) AS decimal(19, 2)) AS ReservationTotal,
        CAST(ISNULL(c.PostedCollections, 0) AS decimal(19, 2)) AS PostedCollections,
        CAST(100.0 * ISNULL(c.PostedCollections, 0) / NULLIF(ISNULL(c.ReservationTotal, 0), 0) AS decimal(9, 2)) AS CollectionPct,
        CAST(ISNULL(c.OutstandingCollections, 0) AS decimal(19, 2)) AS OutstandingCollections,
        CAST(fin.NetAccountingIncome AS decimal(19, 2)) AS NetAccountingIncome,
        CAST(fin.CostOfSales AS decimal(19, 2)) AS CostOfSales,
        CAST(fin.OperatingExpenses AS decimal(19, 2)) AS OperatingExpenses,
        CAST(fin.FinancialExpenses AS decimal(19, 2)) AS FinancialExpenses,
        CAST(fin.OtherNet AS decimal(19, 2)) AS OtherNet,
        CAST(fin.Taxes AS decimal(19, 2)) AS Taxes,
        CAST(fin.NormalizedOperatingResult AS decimal(19, 2)) AS NormalizedOperatingResult,
        CAST(fin.NetResult AS decimal(19, 2)) AS NetResult,
        CAST(100.0 * fin.NormalizedOperatingResult / NULLIF(fin.NetAccountingIncome, 0) AS decimal(9, 2)) AS OperatingMarginPct,
        CAST(100.0 * fin.NetResult / NULLIF(fin.NetAccountingIncome, 0) AS decimal(9, 2)) AS NetMarginPct,
        CAST(ISNULL(cash.CashIn, 0) AS decimal(19, 2)) AS CashIn,
        CAST(ISNULL(cash.CashOut, 0) AS decimal(19, 2)) AS CashOut,
        CAST(ISNULL(cash.CashIn, 0) - ISNULL(cash.CashOut, 0) AS decimal(19, 2)) AS NetCashflow,
        CAST(ISNULL(s.EstimatedOwnerShare, 0) AS decimal(19, 2)) AS EstimatedOwnerShare,
        CAST(ISNULL(s.EstimatedOwnerShare, 0) * 0.10 AS decimal(19, 2)) AS EstimatedOwnerISR10,
        CAST(ISNULL(s.EstimatedOwnerShare, 0) * 0.90 AS decimal(19, 2)) AS EstimatedOwnerFinalPayout,
        CAST(ISNULL(pending.PendingNetEffect, 0) AS decimal(19, 2)) AS PendingBankNetExcluded
    FROM #Periods AS p
    CROSS JOIN
    (
        SELECT COUNT(*) AS RentableSuites
        FROM #RentableRooms
    ) AS rr
    LEFT JOIN #SalesAgg AS s
        ON s.PeriodKey = p.PeriodKey
    LEFT JOIN #CollectionsAgg AS c
        ON c.PeriodKey = p.PeriodKey
    LEFT JOIN #CashAgg AS cash
        ON cash.PeriodKey = p.PeriodKey
    LEFT JOIN #PendingBankByPeriod AS pending
        ON pending.PeriodKey = p.PeriodKey
    OUTER APPLY
    (
        SELECT
            ISNULL(f.GrossIncome, 0) AS GrossIncome,
            ISNULL(f.SalesReturns, 0) AS SalesReturns,
            ISNULL(f.GrossIncome, 0) - ISNULL(f.SalesReturns, 0) AS NetAccountingIncome,
            ISNULL(f.CostOfSales, 0) AS CostOfSales,
            ISNULL(f.OperatingExpenses, 0) AS OperatingExpenses,
            ISNULL(f.FinancialExpenses, 0) AS FinancialExpenses,
            ISNULL(f.OtherIncome, 0) AS OtherIncome,
            ISNULL(f.OtherExpenses, 0) AS OtherExpenses,
            ISNULL(f.OtherIncome, 0) - ISNULL(f.OtherExpenses, 0) AS OtherNet,
            ISNULL(f.Taxes, 0) AS Taxes,
            ISNULL(f.GrossIncome, 0) - ISNULL(f.SalesReturns, 0)
                - ISNULL(f.CostOfSales, 0)
                - ISNULL(f.OperatingExpenses, 0)
                - ISNULL(f.FinancialExpenses, 0)
                + ISNULL(f.OtherIncome, 0) - ISNULL(f.OtherExpenses, 0) AS NormalizedOperatingResult,
            ISNULL(f.GrossIncome, 0) - ISNULL(f.SalesReturns, 0)
                - ISNULL(f.CostOfSales, 0)
                - ISNULL(f.OperatingExpenses, 0)
                - ISNULL(f.FinancialExpenses, 0)
                + ISNULL(f.OtherIncome, 0) - ISNULL(f.OtherExpenses, 0)
                - ISNULL(f.Taxes, 0) AS NetResult
        FROM (SELECT 1 AS AlwaysOne) AS one_row
        LEFT JOIN #FinancialAgg AS f
            ON f.PeriodKey = p.PeriodKey
    ) AS fin
    ORDER BY p.SortOrder;

    SELECT
        'Desempeno por suite' AS ResultSetName,
        p.SortOrder,
        p.PeriodLabel,
        p.PeriodScope,
        cr.RoomName,
        MIN(cr.OwnerID) AS OwnerID,
        CAST(MIN(cr.BasePrice) AS decimal(19, 2)) AS BasePrice,
        COUNT(*) AS AvailableNights,
        SUM(CASE WHEN cr.IsLocked = 1
                  AND cr.ReservationID IS NOT NULL
                  AND cr.Precio > 0
                 THEN 1 ELSE 0 END) AS OccupiedNights,
        CAST(100.0 * SUM(CASE WHEN cr.IsLocked = 1
                                AND cr.ReservationID IS NOT NULL
                                AND cr.Precio > 0
                               THEN 1 ELSE 0 END) / NULLIF(COUNT(*), 0) AS decimal(9, 2)) AS OccupancyPct,
        CAST(SUM(CASE WHEN cr.IsLocked = 1
                       AND cr.ReservationID IS NOT NULL
                       AND cr.Precio > 0
                      THEN cr.Precio ELSE 0 END) AS decimal(19, 2)) AS RoomRevenue,
        CAST(SUM(CASE WHEN cr.IsLocked = 1
                       AND cr.ReservationID IS NOT NULL
                       AND cr.Precio > 0
                      THEN cr.Precio ELSE 0 END) / NULLIF(SUM(CASE WHEN cr.IsLocked = 1
                                                                     AND cr.ReservationID IS NOT NULL
                                                                     AND cr.Precio > 0
                                                                    THEN 1 ELSE 0 END), 0) AS decimal(19, 2)) AS ADR,
        CAST(SUM(CASE WHEN cr.IsLocked = 1
                       AND cr.ReservationID IS NOT NULL
                       AND cr.Precio > 0
                      THEN cr.Precio ELSE 0 END) / NULLIF(COUNT(*), 0) AS decimal(19, 2)) AS RevPAR,
        CAST(SUM(CASE WHEN cr.IsLocked = 1
                       AND cr.ReservationID IS NOT NULL
                       AND cr.Precio > 0
                      THEN cr.Precio * cr.PorcentajeArrendamiento ELSE 0 END) AS decimal(19, 2)) AS EstimatedOwnerShare,
        CAST(SUM(CASE WHEN cr.IsLocked = 1
                       AND cr.ReservationID IS NOT NULL
                       AND cr.Precio > 0
                      THEN cr.Precio * cr.PorcentajeArrendamiento ELSE 0 END) * 0.10 AS decimal(19, 2)) AS EstimatedOwnerISR10,
        CAST(SUM(CASE WHEN cr.IsLocked = 1
                       AND cr.ReservationID IS NOT NULL
                       AND cr.Precio > 0
                      THEN cr.Precio * cr.PorcentajeArrendamiento ELSE 0 END) * 0.90 AS decimal(19, 2)) AS EstimatedOwnerFinalPayout
    FROM #CalendarRows AS cr
    INNER JOIN #Periods AS p
        ON p.PeriodKey = cr.PeriodKey
    WHERE cr.PeriodKey IN (1, 4)
    GROUP BY
        p.SortOrder,
        p.PeriodLabel,
        p.PeriodScope,
        cr.RoomName
    ORDER BY
        p.SortOrder,
        RoomRevenue DESC,
        cr.RoomName;

    SELECT
        'Desglose financiero' AS ResultSetName,
        p.SortOrder,
        p.PeriodLabel,
        p.PeriodScope,
        CAST(p.DateStart AS datetime) AS PeriodStart,
        DATEADD(SECOND, -1, CAST(p.DateEndExcl AS datetime)) AS PeriodEnd,
        CAST(fin.GrossIncome AS decimal(19, 2)) AS GrossIncome401403,
        CAST(fin.SalesReturns AS decimal(19, 2)) AS SalesReturns402,
        CAST(fin.NetAccountingIncome AS decimal(19, 2)) AS NetAccountingIncome,
        CAST(fin.CostOfSales AS decimal(19, 2)) AS CostOfSales501504,
        CAST(fin.NetAccountingIncome - fin.CostOfSales AS decimal(19, 2)) AS GrossProfit,
        CAST(100.0 * (fin.NetAccountingIncome - fin.CostOfSales) / NULLIF(fin.NetAccountingIncome, 0) AS decimal(9, 2)) AS GrossMarginPct,
        CAST(fin.OperatingExpenses AS decimal(19, 2)) AS OperatingExpenses602605,
        CAST(fin.FinancialExpenses AS decimal(19, 2)) AS FinancialExpenses701,
        CAST(fin.OtherIncome AS decimal(19, 2)) AS OtherIncome704,
        CAST(fin.OtherExpenses AS decimal(19, 2)) AS OtherExpenses703,
        CAST(fin.OtherNet AS decimal(19, 2)) AS OtherNet,
        CAST(fin.Taxes AS decimal(19, 2)) AS Taxes611,
        CAST(fin.NormalizedOperatingResult AS decimal(19, 2)) AS NormalizedOperatingResult,
        CAST(fin.NetResult AS decimal(19, 2)) AS NetResult,
        CAST(100.0 * fin.NormalizedOperatingResult / NULLIF(fin.NetAccountingIncome, 0) AS decimal(9, 2)) AS OperatingMarginPct,
        CAST(100.0 * fin.NetResult / NULLIF(fin.NetAccountingIncome, 0) AS decimal(9, 2)) AS NetMarginPct,
        CAST(ISNULL(pending.PendingDebe, 0) AS decimal(19, 2)) AS PendingBankDebeExcluded,
        CAST(ISNULL(pending.PendingHaber, 0) AS decimal(19, 2)) AS PendingBankHaberExcluded,
        CAST(ISNULL(pending.PendingNetEffect, 0) AS decimal(19, 2)) AS PendingBankNetExcluded
    FROM #Periods AS p
    LEFT JOIN #PendingBankByPeriod AS pending
        ON pending.PeriodKey = p.PeriodKey
    OUTER APPLY
    (
        SELECT
            ISNULL(f.GrossIncome, 0) AS GrossIncome,
            ISNULL(f.SalesReturns, 0) AS SalesReturns,
            ISNULL(f.GrossIncome, 0) - ISNULL(f.SalesReturns, 0) AS NetAccountingIncome,
            ISNULL(f.CostOfSales, 0) AS CostOfSales,
            ISNULL(f.OperatingExpenses, 0) AS OperatingExpenses,
            ISNULL(f.FinancialExpenses, 0) AS FinancialExpenses,
            ISNULL(f.OtherIncome, 0) AS OtherIncome,
            ISNULL(f.OtherExpenses, 0) AS OtherExpenses,
            ISNULL(f.OtherIncome, 0) - ISNULL(f.OtherExpenses, 0) AS OtherNet,
            ISNULL(f.Taxes, 0) AS Taxes,
            ISNULL(f.GrossIncome, 0) - ISNULL(f.SalesReturns, 0)
                - ISNULL(f.CostOfSales, 0)
                - ISNULL(f.OperatingExpenses, 0)
                - ISNULL(f.FinancialExpenses, 0)
                + ISNULL(f.OtherIncome, 0) - ISNULL(f.OtherExpenses, 0) AS NormalizedOperatingResult,
            ISNULL(f.GrossIncome, 0) - ISNULL(f.SalesReturns, 0)
                - ISNULL(f.CostOfSales, 0)
                - ISNULL(f.OperatingExpenses, 0)
                - ISNULL(f.FinancialExpenses, 0)
                + ISNULL(f.OtherIncome, 0) - ISNULL(f.OtherExpenses, 0)
                - ISNULL(f.Taxes, 0) AS NetResult
        FROM (SELECT 1 AS AlwaysOne) AS one_row
        LEFT JOIN #FinancialAgg AS f
            ON f.PeriodKey = p.PeriodKey
    ) AS fin
    ORDER BY p.SortOrder;

    SELECT
        'Flujo de efectivo' AS ResultSetName,
        p.SortOrder,
        p.PeriodLabel,
        p.PeriodScope,
        CAST(p.DateStart AS datetime) AS PeriodStart,
        DATEADD(SECOND, -1, CAST(p.DateEndExcl AS datetime)) AS PeriodEnd,
        ISNULL(cash.CashTransactionCount, 0) AS CashTransactionCount,
        CAST(ISNULL(cash.OpeningCashBalance, 0) AS decimal(19, 2)) AS OpeningCashBalance,
        CAST(ISNULL(cash.CashIn, 0) AS decimal(19, 2)) AS CashIn,
        CAST(ISNULL(cash.CashOut, 0) AS decimal(19, 2)) AS CashOut,
        CAST(ISNULL(cash.CashIn, 0) - ISNULL(cash.CashOut, 0) AS decimal(19, 2)) AS NetCashflow,
        CAST(ISNULL(cash.ClosingCashBalance, 0) AS decimal(19, 2)) AS ClosingCashBalance
    FROM #Periods AS p
    LEFT JOIN #CashAgg AS cash
        ON cash.PeriodKey = p.PeriodKey
    ORDER BY p.SortOrder;

    CREATE TABLE #DataQuality
    (
        SortOrder int NOT NULL,
        PeriodLabel varchar(40) NOT NULL,
        PeriodScope varchar(30) NOT NULL,
        CheckType varchar(60) NOT NULL,
        Severity varchar(20) NOT NULL,
        Item varchar(300) NOT NULL,
        ItemCount int NULL,
        MetricAmount decimal(19, 2) NULL,
        ReferenceAmount decimal(19, 2) NULL,
        NetEffect decimal(19, 2) NULL,
        SampleReference varchar(300) NULL,
        Notes varchar(500) NULL
    );

    ;WITH CalendarIssueRows AS
    (
        SELECT
            cr.PeriodKey,
            CASE
                WHEN cr.LockDescription IS NULL OR LTRIM(RTRIM(cr.LockDescription)) = '' THEN 'Sin ID de reservacion en bloqueo'
                WHEN cr.ReservationID IS NULL THEN 'ID de reservacion no numerico'
                WHEN cr.MatchedReservationID IS NULL THEN 'Reservacion no encontrada'
                ELSE 'OK'
            END AS IssueName,
            cr.RoomName,
            cr.RoomDate,
            cr.RoomCalendarID,
            cr.Precio
        FROM #CalendarRows AS cr
        WHERE cr.IsLocked = 1
    )
    INSERT INTO #DataQuality
        (SortOrder, PeriodLabel, PeriodScope, CheckType, Severity, Item, ItemCount, MetricAmount, ReferenceAmount, NetEffect, SampleReference, Notes)
    SELECT
        p.SortOrder,
        p.PeriodLabel,
        p.PeriodScope,
        'Liga calendario-reservacion',
        CASE WHEN cir.IssueName IN ('Reservacion no encontrada', 'ID de reservacion no numerico') THEN 'Alta' ELSE 'Media' END,
        cir.IssueName,
        COUNT(*) AS ItemCount,
        CAST(SUM(cir.Precio) AS decimal(19, 2)) AS MetricAmount,
        NULL,
        NULL,
        MIN(CONCAT(cir.RoomName, ' ', CONVERT(varchar(10), cir.RoomDate, 23), ' calendario_id=', cir.RoomCalendarID)) AS SampleReference,
        'Noches bloqueadas excluidas de las metricas de venta validas.'
    FROM CalendarIssueRows AS cir
    INNER JOIN #Periods AS p
        ON p.PeriodKey = cir.PeriodKey
    WHERE cir.IssueName <> 'OK'
    GROUP BY
        p.SortOrder,
        p.PeriodLabel,
        p.PeriodScope,
        cir.IssueName;

    INSERT INTO #DataQuality
        (SortOrder, PeriodLabel, PeriodScope, CheckType, Severity, Item, ItemCount, MetricAmount, ReferenceAmount, NetEffect, SampleReference, Notes)
    SELECT
        p.SortOrder,
        p.PeriodLabel,
        p.PeriodScope,
        'Estado de cobranza',
        'Media',
        CASE
            WHEN ISNULL(rp.PostedPositivePayments, 0) = 0 THEN 'Sin cobranza contabilizada'
            ELSE 'Cobranza parcial contabilizada'
        END AS Item,
        COUNT(*) AS ItemCount,
        CAST(SUM(rp.TotalPrice) AS decimal(19, 2)) AS MetricAmount,
        CAST(SUM(ISNULL(rp.PostedPositivePayments, 0)) AS decimal(19, 2)) AS ReferenceAmount,
        CAST(SUM(rp.TotalPrice - ISNULL(rp.PostedPositivePayments, 0)) AS decimal(19, 2)) AS NetEffect,
        MIN(CONCAT('reservacion_id=', rp.ReservationID)) AS SampleReference,
        'Reservaciones con check-in en el periodo y cobranza contabilizada menor al total de la reservacion.'
    FROM #ReservationPayments AS rp
    INNER JOIN #Periods AS p
        ON p.PeriodKey = rp.PeriodKey
    WHERE
        rp.TotalPrice > 0
        AND rp.TotalPrice - ISNULL(rp.PostedPositivePayments, 0) > 0.01
    GROUP BY
        p.SortOrder,
        p.PeriodLabel,
        p.PeriodScope,
        CASE
            WHEN ISNULL(rp.PostedPositivePayments, 0) = 0 THEN 'Sin cobranza contabilizada'
            ELSE 'Cobranza parcial contabilizada'
        END;

    INSERT INTO #DataQuality
        (SortOrder, PeriodLabel, PeriodScope, CheckType, Severity, Item, ItemCount, MetricAmount, ReferenceAmount, NetEffect, SampleReference, Notes)
    SELECT
        p.SortOrder,
        p.PeriodLabel,
        p.PeriodScope,
        'Pago de reservacion no contabilizado',
        'Alta',
        'Pago sin movimientos contables',
        COUNT(*) AS ItemCount,
        CAST(SUM(ISNULL(rp.UnpostedPositivePayments, 0)) AS decimal(19, 2)) AS MetricAmount,
        CAST(SUM(ISNULL(rp.UnpostedPaymentTransactions, 0)) AS decimal(19, 2)) AS ReferenceAmount,
        NULL,
        MIN(CONCAT('reservacion_id=', rp.ReservationID)) AS SampleReference,
        'Pagos de reservaciones ligados a transacciones sin registros en Registro_Contable.'
    FROM #ReservationPayments AS rp
    INNER JOIN #Periods AS p
        ON p.PeriodKey = rp.PeriodKey
    WHERE ISNULL(rp.UnpostedPositivePayments, 0) > 0
    GROUP BY
        p.SortOrder,
        p.PeriodLabel,
        p.PeriodScope;

    INSERT INTO #DataQuality
        (SortOrder, PeriodLabel, PeriodScope, CheckType, Severity, Item, ItemCount, MetricAmount, ReferenceAmount, NetEffect, SampleReference, Notes)
    SELECT
        p.SortOrder,
        p.PeriodLabel,
        p.PeriodScope,
        'Registro bancario pendiente',
        'Media',
        CONCAT(pba.Nivel1, '.', pba.Nivel2, '.', pba.Nivel3, ' ', pba.Nombre_Cuenta),
        pba.TransactionCount,
        CAST(pba.PendingDebe AS decimal(19, 2)),
        CAST(pba.PendingHaber AS decimal(19, 2)),
        CAST(pba.PendingNetEffect AS decimal(19, 2)),
        NULL,
        'Excluido del resultado normalizado y mostrado aqui para conciliacion.'
    FROM #PendingBankAgg AS pba
    INNER JOIN #Periods AS p
        ON p.PeriodKey = pba.PeriodKey;

    SELECT
        'Calidad de datos y conciliacion' AS ResultSetName,
        SortOrder,
        PeriodLabel,
        PeriodScope,
        CheckType,
        Severity,
        Item,
        ItemCount,
        MetricAmount,
        ReferenceAmount,
        NetEffect,
        SampleReference,
        Notes
    FROM #DataQuality
    ORDER BY
        SortOrder,
        CASE Severity WHEN 'Alta' THEN 1 WHEN 'Media' THEN 2 ELSE 3 END,
        CheckType,
        Item;
END;
GO
