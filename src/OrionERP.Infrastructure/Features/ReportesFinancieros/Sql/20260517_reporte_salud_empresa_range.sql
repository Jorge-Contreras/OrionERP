CREATE OR ALTER PROCEDURE [reporteFinanciero].[Reporte_Salud_Empresa]
    @AnioInicio int,
    @MesInicio tinyint,
    @AnioFin int,
    @MesFin tinyint,
    @RFC varchar(50) = 'OHM191112Q26',
    @IncluirHabitacionesNoRentables bit = 0,
    @FechaCorte date = NULL
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
    DECLARE @FechaCorteEfectiva date = ISNULL(@FechaCorte, CAST(GETDATE() AS date));
    DECLARE @PeriodoRealFinExcl date = CASE
        WHEN @FechaCorteEfectiva < @PeriodoInicio THEN @PeriodoInicio
        WHEN @FechaCorteEfectiva < @PeriodoFinExcl THEN DATEADD(DAY, 1, @FechaCorteEfectiva)
        ELSE @PeriodoFinExcl
    END;
    DECLARE @PeriodoMeses int = DATEDIFF(MONTH, @PeriodoInicio, @PeriodoFinExcl);
    DECLARE @PeriodoAnteriorInicio date = DATEADD(MONTH, -@PeriodoMeses, @PeriodoInicio);
    DECLARE @PeriodoAnteriorFinExcl date = DATEADD(DAY, DATEDIFF(DAY, @PeriodoInicio, @PeriodoRealFinExcl), @PeriodoAnteriorInicio);
    IF @PeriodoAnteriorFinExcl > @PeriodoInicio SET @PeriodoAnteriorFinExcl = @PeriodoInicio;
    DECLARE @PeriodoAnteriorFinMes date = DATEADD(MONTH, -1, @PeriodoAnteriorFinExcl);
    DECLARE @PeriodoAnioAnteriorInicio date = DATEADD(YEAR, -1, @PeriodoInicio);
    DECLARE @PeriodoAnioAnteriorFinExcl date = DATEADD(YEAR, -1, @PeriodoRealFinExcl);
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
        (1, @PeriodoLabel, 'MTD realizado', @PeriodoInicio, @PeriodoRealFinExcl, 1),
        (2, @PeriodoAnteriorLabel, 'Periodo anterior', @PeriodoAnteriorInicio, @PeriodoAnteriorFinExcl, 2),
        (3, @PeriodoAnioAnteriorLabel, 'Mismo periodo ano anterior', @PeriodoAnioAnteriorInicio, @PeriodoAnioAnteriorFinExcl, 3),
        (4, CONCAT(CONVERT(char(4), YEAR(@PeriodoFinMes)), ' acumulado'), 'Acumulado del ano', @AnioAcumuladoInicio, @PeriodoRealFinExcl, 4),
        (5, CONCAT(CONVERT(char(4), YEAR(@PeriodoFinMes) - 1), ' acumulado'), 'Acumulado ano anterior', @AnioAnteriorAcumuladoInicio, @AnioAnteriorAcumuladoFinExcl, 5);

    CREATE TABLE #RentableRooms
    (
        RoomName varchar(50) NOT NULL PRIMARY KEY,
        OwnerID int NULL,
        BasePrice decimal(19, 4) NULL
    );

    DECLARE @HospedajeHabilitado bit = ISNULL((
        SELECT HospedajeHabilitado
        FROM reporteFinanciero.SaludEmpresaConfiguracion
        WHERE RFC = @RfcTrim
    ), 0);
    DECLARE @RetencionArrendadorPct decimal(9,4) = ISNULL((
        SELECT RetencionArrendadorPct
        FROM reporteFinanciero.SaludEmpresaConfiguracion
        WHERE RFC = @RfcTrim
    ), 10);

    INSERT INTO #RentableRooms
        (RoomName, OwnerID, BasePrice)
    SELECT
        r.ROOM_NAME,
        r.OWNER_ID,
        CAST(r.BASE_PRICE AS decimal(19, 4)) AS BasePrice
    FROM dbo.ROOM AS r
    WHERE @HospedajeHabilitado = 1
      AND r.IsActive = 1
      AND (@IncluirHabitacionesNoRentables = 1 OR r.IsRentable = 1);

    ;WITH ExpectedDates AS
    (
        SELECT
            p.PeriodKey,
            DATEADD(DAY, n.Value, p.DateStart) AS RoomDate
        FROM #Periods p
        CROSS APPLY
        (
            SELECT TOP (DATEDIFF(DAY, p.DateStart, p.DateEndExcl))
                ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS Value
            FROM sys.all_objects a
            CROSS JOIN sys.all_objects b
        ) n
    )
    SELECT
        p.PeriodKey,
        p.PeriodLabel,
        p.PeriodScope,
        rr.RoomName,
        rr.OwnerID,
        rr.BasePrice,
        rc.id AS RoomCalendarID,
        dates.RoomDate,
        ISNULL(rc.IS_LOCKED, 0) AS IsLocked,
        rc.LOCK_DESCRIPTION AS LockDescription,
        parsed.ReservationID,
        r.ID AS MatchedReservationID,
        CAST(ISNULL(rc.PRECIO, 0) AS decimal(19, 4)) AS Precio,
        CAST(ISNULL(rc.PORCENTAJE_ARRENDAMIENTO, 0) AS decimal(19, 6)) AS PorcentajeArrendamiento,
        UPPER(LTRIM(RTRIM(ISNULL(r.STATUS, '')))) AS ReservationStatus,
        CAST(ISNULL(r.SUITE_DISCOUNT_PERCENT, 0) AS decimal(9,4)) AS SuiteDiscountPercent
    INTO #CalendarRows
    FROM #Periods AS p
    INNER JOIN ExpectedDates AS dates
        ON dates.PeriodKey = p.PeriodKey
    CROSS JOIN #RentableRooms AS rr
    LEFT JOIN dbo.ROOM_CALENDAR AS rc
        ON rc.ROOM_DATE = dates.RoomDate
       AND rc.ROOM = rr.RoomName
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
                  AND cr.ReservationStatus IN ('ACTIVA', 'PAGADA')
                 THEN 1 ELSE 0 END) AS OccupiedNights,
        COUNT(DISTINCT CASE WHEN cr.IsLocked = 1
                              AND cr.ReservationID IS NOT NULL
                              AND cr.Precio > 0
                              AND cr.ReservationStatus IN ('ACTIVA', 'PAGADA')
                            THEN cr.RoomName END) AS SuitesWithSales,
        SUM(CASE WHEN cr.IsLocked = 1
                  AND cr.ReservationID IS NOT NULL
                  AND cr.Precio > 0
                  AND cr.ReservationStatus IN ('ACTIVA', 'PAGADA')
                 THEN ROUND(cr.Precio - ROUND(cr.Precio * cr.SuiteDiscountPercent / 100.0, 2), 2) ELSE 0 END) AS RoomRevenue,
        SUM(CASE WHEN cr.IsLocked = 1
                  AND cr.ReservationID IS NOT NULL
                  AND cr.Precio > 0
                  AND cr.ReservationStatus IN ('ACTIVA', 'PAGADA')
                 THEN ROUND(cr.Precio - ROUND(cr.Precio * cr.SuiteDiscountPercent / 100.0, 2), 2) * cr.PorcentajeArrendamiento ELSE 0 END) AS EstimatedOwnerShare
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
        ON @HospedajeHabilitado = 1
       AND r.CHECKIN >= p.DateStart
       AND r.CHECKIN < p.DateEndExcl
       AND UPPER(LTRIM(RTRIM(ISNULL(r.STATUS, '')))) IN ('ACTIVA', 'PAGADA');

    SELECT
        p.PeriodKey,
        COUNT(*) AS PipelineReservationCount,
        SUM(CAST(ISNULL(r.TOTAL_PRICE, 0) AS decimal(19,4))) AS PipelineReservationTotal
    INTO #PipelineAgg
    FROM #Periods p
    INNER JOIN dbo.RESERVATION r
      ON @HospedajeHabilitado = 1
     AND r.CHECKIN >= p.DateStart
     AND r.CHECKIN < p.DateEndExcl
     AND UPPER(LTRIM(RTRIM(ISNULL(r.STATUS, '')))) = 'COTIZACION'
    GROUP BY p.PeriodKey;

    SELECT
        p.PeriodKey,
        SUM(CASE
              WHEN UPPER(ISNULL(re.TaxMode, 'TaxableExclusive')) = 'TAXINCLUDED'
                THEN ROUND(CAST(re.UnitPriceSnapshot * re.Quantity AS decimal(19,4)) / 1.16, 2)
              ELSE ROUND(CAST(re.UnitPriceSnapshot * re.Quantity AS decimal(19,4)), 2)
            END) AS ExtrasRevenue
    INTO #ExtrasAgg
    FROM #Periods p
    INNER JOIN dbo.RESERVATION r
      ON @HospedajeHabilitado = 1
     AND r.CHECKIN >= p.DateStart AND r.CHECKIN < p.DateEndExcl
     AND UPPER(LTRIM(RTRIM(ISNULL(r.STATUS, '')))) IN ('ACTIVA', 'PAGADA')
    INNER JOIN dbo.Reservation_Extra re ON re.ReservationID = r.ID
    GROUP BY p.PeriodKey;

    SELECT
        p.PeriodKey,
        SUM(CASE
              WHEN UPPER(ISNULL(re.TaxMode, 'TaxableExclusive')) = 'TAXINCLUDED'
                THEN ROUND(CAST(re.TotalSnapshot AS decimal(19,4)) / 1.16, 2)
              ELSE ROUND(CAST(re.TotalSnapshot AS decimal(19,4)), 2)
            END) AS ExperiencesRevenue
    INTO #ExperiencesAgg
    FROM #Periods p
    INNER JOIN dbo.Reservation_Experience re
      ON @HospedajeHabilitado = 1
     AND re.ExperienceDate >= p.DateStart AND re.ExperienceDate < p.DateEndExcl
    INNER JOIN dbo.RESERVATION r
      ON r.ID = re.ReservationID
     AND UPPER(LTRIM(RTRIM(ISNULL(r.STATUS, '')))) IN ('ACTIVA', 'PAGADA')
    GROUP BY p.PeriodKey;

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
        SUM(CASE WHEN rc.Nivel1 IN ('501', '502', '503', '504', '505') THEN rc.Debe - rc.Haber ELSE 0 END) AS CostOfSales,
        SUM(CASE WHEN rc.Nivel1 = '601' THEN rc.Debe - rc.Haber ELSE 0 END) AS GeneralExpenses,
        SUM(CASE WHEN rc.Nivel1 IN ('602', '603', '604', '605') THEN rc.Debe - rc.Haber ELSE 0 END) AS OperatingExpenses,
        SUM(CASE WHEN rc.Nivel1 = '606' THEN rc.Debe - rc.Haber ELSE 0 END) AS OtherOperatingExpenses,
        SUM(CASE WHEN rc.Nivel1 IN ('607', '608', '609', '610') THEN rc.Debe - rc.Haber ELSE 0 END) AS ProfitSharing,
        SUM(CASE WHEN rc.Nivel1 = '612' THEN rc.Debe - rc.Haber ELSE 0 END) AS NonDeductible,
        SUM(CASE WHEN rc.Nivel1 = '613' THEN rc.Debe - rc.Haber ELSE 0 END) AS Depreciation,
        SUM(CASE WHEN rc.Nivel1 = '614' THEN rc.Debe - rc.Haber ELSE 0 END) AS Amortization,
        SUM(CASE WHEN rc.Nivel1 = '701' THEN rc.Debe - rc.Haber ELSE 0 END) AS FinancialExpenses,
        SUM(CASE WHEN rc.Nivel1 = '702' THEN rc.Haber - rc.Debe ELSE 0 END) AS FinancialIncome,
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
       AND rc.Nivel1 IN ('401', '402', '403', '501', '502', '503', '504', '505', '601', '602', '603', '604', '605', '606', '607', '608', '609', '610', '611', '612', '613', '614', '701', '702', '703', '704')
       AND UPPER(ISNULL(rc.Nombre_Cuenta, '')) NOT LIKE '%PENDIENTES DE REGISTRO%'
    GROUP BY p.PeriodKey;

    SELECT
        t.ID AS TransactionID,
        CAST(t.Fecha AS date) AS TransactionDate,
        SUM(CASE WHEN rc.Nivel1 IN ('101', '102') THEN rc.Debe ELSE 0 END) AS CashDebit,
        SUM(CASE WHEN rc.Nivel1 IN ('101', '102') THEN rc.Haber ELSE 0 END) AS CashCredit
    INTO #CashTransactions
    FROM dbo.Transacciones t
    INNER JOIN dbo.Registro_Contable rc ON rc.TransaccionID = t.ID
    WHERE t.RFC = @RfcTrim
      AND rc.Nivel1 IN ('101', '102')
      AND t.Fecha < (SELECT MAX(DateEndExcl) FROM #Periods)
    GROUP BY t.ID, CAST(t.Fecha AS date);

    SELECT
        p.PeriodKey,
        SUM(CASE WHEN tx.TransactionDate >= p.DateStart AND tx.TransactionDate < p.DateEndExcl
                  AND tx.CashDebit > tx.CashCredit THEN tx.CashDebit - tx.CashCredit ELSE 0 END) AS CashIn,
        SUM(CASE WHEN tx.TransactionDate >= p.DateStart AND tx.TransactionDate < p.DateEndExcl
                  AND tx.CashCredit > tx.CashDebit THEN tx.CashCredit - tx.CashDebit ELSE 0 END) AS CashOut,
        SUM(CASE WHEN tx.TransactionDate < p.DateStart THEN tx.CashDebit - tx.CashCredit ELSE 0 END) AS OpeningCashBalance,
        SUM(CASE WHEN tx.TransactionDate < p.DateEndExcl THEN tx.CashDebit - tx.CashCredit ELSE 0 END) AS ClosingCashBalance,
        COUNT(DISTINCT CASE WHEN tx.TransactionDate >= p.DateStart AND tx.TransactionDate < p.DateEndExcl THEN tx.TransactionID END) AS CashTransactionCount
    INTO #CashAgg
    FROM #Periods p
    INNER JOIN #CashTransactions tx ON tx.TransactionDate < p.DateEndExcl
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
        CAST(ISNULL(extras.ExtrasRevenue, 0) AS decimal(19, 2)) AS ExtrasRevenue,
        CAST(ISNULL(experiences.ExperiencesRevenue, 0) AS decimal(19, 2)) AS ExperiencesRevenue,
        CAST(CASE WHEN @HospedajeHabilitado = 1
                  THEN ISNULL(s.RoomRevenue, 0) + ISNULL(extras.ExtrasRevenue, 0) + ISNULL(experiences.ExperiencesRevenue, 0)
                  ELSE fin.NetAccountingIncome
             END AS decimal(19, 2)) AS TotalOperatingRevenue,
        CAST(ISNULL(s.RoomRevenue, 0) / NULLIF(ISNULL(s.OccupiedNights, 0), 0) AS decimal(19, 2)) AS ADR,
        CAST(ISNULL(s.RoomRevenue, 0) / NULLIF(ISNULL(s.AvailableNights, 0), 0) AS decimal(19, 2)) AS RevPAR,
        CAST((ISNULL(s.RoomRevenue, 0) + ISNULL(extras.ExtrasRevenue, 0) + ISNULL(experiences.ExperiencesRevenue, 0)) / NULLIF(ISNULL(s.AvailableNights, 0), 0) AS decimal(19, 2)) AS TRevPAR,
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
        CAST(ISNULL(s.EstimatedOwnerShare, 0) * @RetencionArrendadorPct / 100.0 AS decimal(19, 2)) AS EstimatedOwnerISR10,
        CAST(ISNULL(s.EstimatedOwnerShare, 0) * (1 - @RetencionArrendadorPct / 100.0) AS decimal(19, 2)) AS EstimatedOwnerFinalPayout,
        CAST(ISNULL(pending.PendingNetEffect, 0) AS decimal(19, 2)) AS PendingBankNetExcluded,
        ISNULL(pipeline.PipelineReservationCount, 0) AS PipelineReservationCount,
        CAST(ISNULL(pipeline.PipelineReservationTotal, 0) AS decimal(19,2)) AS PipelineReservationTotal,
        CAST(@FechaCorteEfectiva AS datetime) AS CutoffDate,
        CAST(CASE WHEN p.PeriodKey IN (1,4) AND @PeriodoRealFinExcl < @PeriodoFinExcl THEN 1 ELSE 0 END AS bit) AS IsProvisional
    FROM #Periods AS p
    CROSS JOIN
    (
        SELECT COUNT(*) AS RentableSuites
        FROM #RentableRooms
    ) AS rr
    LEFT JOIN #SalesAgg AS s
        ON s.PeriodKey = p.PeriodKey
    LEFT JOIN #ExtrasAgg AS extras
        ON extras.PeriodKey = p.PeriodKey
    LEFT JOIN #ExperiencesAgg AS experiences
        ON experiences.PeriodKey = p.PeriodKey
    LEFT JOIN #PipelineAgg AS pipeline
        ON pipeline.PeriodKey = p.PeriodKey
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
            ISNULL(f.GeneralExpenses, 0) AS GeneralExpenses,
            ISNULL(f.OperatingExpenses, 0) AS BaseOperatingExpenses,
            ISNULL(f.OtherOperatingExpenses, 0) AS OtherOperatingExpenses,
            ISNULL(f.GeneralExpenses, 0) + ISNULL(f.OperatingExpenses, 0) + ISNULL(f.OtherOperatingExpenses, 0) AS OperatingExpenses,
            ISNULL(f.Depreciation, 0) AS Depreciation,
            ISNULL(f.Amortization, 0) AS Amortization,
            ISNULL(f.ProfitSharing, 0) AS ProfitSharing,
            ISNULL(f.NonDeductible, 0) AS NonDeductible,
            ISNULL(f.FinancialExpenses, 0) AS FinancialExpenses,
            ISNULL(f.FinancialIncome, 0) AS FinancialIncome,
            ISNULL(f.OtherIncome, 0) AS OtherIncome,
            ISNULL(f.OtherExpenses, 0) AS OtherExpenses,
            ISNULL(f.OtherIncome, 0) - ISNULL(f.OtherExpenses, 0) AS OtherNet,
            ISNULL(f.Taxes, 0) AS Taxes,
            ISNULL(f.GrossIncome, 0) - ISNULL(f.SalesReturns, 0)
                - ISNULL(f.CostOfSales, 0)
                - ISNULL(f.GeneralExpenses, 0)
                - ISNULL(f.OperatingExpenses, 0)
                - ISNULL(f.OtherOperatingExpenses, 0) AS EstimatedOperatingEbitda,
            ISNULL(f.GrossIncome, 0) - ISNULL(f.SalesReturns, 0)
                - ISNULL(f.CostOfSales, 0)
                - ISNULL(f.GeneralExpenses, 0)
                - ISNULL(f.OperatingExpenses, 0)
                - ISNULL(f.OtherOperatingExpenses, 0)
                - ISNULL(f.Depreciation, 0)
                - ISNULL(f.Amortization, 0) AS NormalizedOperatingResult,
            ISNULL(f.GrossIncome, 0) - ISNULL(f.SalesReturns, 0)
                - ISNULL(f.CostOfSales, 0)
                - ISNULL(f.GeneralExpenses, 0)
                - ISNULL(f.OperatingExpenses, 0)
                - ISNULL(f.OtherOperatingExpenses, 0)
                - ISNULL(f.Depreciation, 0)
                - ISNULL(f.Amortization, 0)
                + ISNULL(f.FinancialIncome, 0) - ISNULL(f.FinancialExpenses, 0)
                + ISNULL(f.OtherIncome, 0) - ISNULL(f.OtherExpenses, 0)
                - ISNULL(f.ProfitSharing, 0)
                - ISNULL(f.Taxes, 0)
                - ISNULL(f.NonDeductible, 0) AS NetResult
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
                  AND cr.ReservationStatus IN ('ACTIVA', 'PAGADA')
                 THEN 1 ELSE 0 END) AS OccupiedNights,
        CAST(100.0 * SUM(CASE WHEN cr.IsLocked = 1
                                AND cr.ReservationID IS NOT NULL
                                AND cr.Precio > 0
                                AND cr.ReservationStatus IN ('ACTIVA', 'PAGADA')
                               THEN 1 ELSE 0 END) / NULLIF(COUNT(*), 0) AS decimal(9, 2)) AS OccupancyPct,
        CAST(SUM(CASE WHEN cr.IsLocked = 1
                       AND cr.ReservationID IS NOT NULL
                       AND cr.Precio > 0
                       AND cr.ReservationStatus IN ('ACTIVA', 'PAGADA')
                      THEN ROUND(cr.Precio - ROUND(cr.Precio * cr.SuiteDiscountPercent / 100.0, 2), 2) ELSE 0 END) AS decimal(19, 2)) AS RoomRevenue,
        CAST(SUM(CASE WHEN cr.IsLocked = 1
                       AND cr.ReservationID IS NOT NULL
                       AND cr.Precio > 0
                       AND cr.ReservationStatus IN ('ACTIVA', 'PAGADA')
                      THEN ROUND(cr.Precio - ROUND(cr.Precio * cr.SuiteDiscountPercent / 100.0, 2), 2) ELSE 0 END) / NULLIF(SUM(CASE WHEN cr.IsLocked = 1
                                                                     AND cr.ReservationID IS NOT NULL
                                                                     AND cr.Precio > 0
                                                                     AND cr.ReservationStatus IN ('ACTIVA', 'PAGADA')
                                                                    THEN 1 ELSE 0 END), 0) AS decimal(19, 2)) AS ADR,
        CAST(SUM(CASE WHEN cr.IsLocked = 1
                       AND cr.ReservationID IS NOT NULL
                       AND cr.Precio > 0
                       AND cr.ReservationStatus IN ('ACTIVA', 'PAGADA')
                      THEN ROUND(cr.Precio - ROUND(cr.Precio * cr.SuiteDiscountPercent / 100.0, 2), 2) ELSE 0 END) / NULLIF(COUNT(*), 0) AS decimal(19, 2)) AS RevPAR,
        CAST(SUM(CASE WHEN cr.IsLocked = 1
                       AND cr.ReservationID IS NOT NULL
                       AND cr.Precio > 0
                       AND cr.ReservationStatus IN ('ACTIVA', 'PAGADA')
                      THEN ROUND(cr.Precio - ROUND(cr.Precio * cr.SuiteDiscountPercent / 100.0, 2), 2) * cr.PorcentajeArrendamiento ELSE 0 END) AS decimal(19, 2)) AS EstimatedOwnerShare,
        CAST(SUM(CASE WHEN cr.IsLocked = 1
                       AND cr.ReservationID IS NOT NULL
                       AND cr.Precio > 0
                       AND cr.ReservationStatus IN ('ACTIVA', 'PAGADA')
                      THEN ROUND(cr.Precio - ROUND(cr.Precio * cr.SuiteDiscountPercent / 100.0, 2), 2) * cr.PorcentajeArrendamiento ELSE 0 END) * @RetencionArrendadorPct / 100.0 AS decimal(19, 2)) AS EstimatedOwnerISR10,
        CAST(SUM(CASE WHEN cr.IsLocked = 1
                       AND cr.ReservationID IS NOT NULL
                       AND cr.Precio > 0
                       AND cr.ReservationStatus IN ('ACTIVA', 'PAGADA')
                      THEN ROUND(cr.Precio - ROUND(cr.Precio * cr.SuiteDiscountPercent / 100.0, 2), 2) * cr.PorcentajeArrendamiento ELSE 0 END) * (1 - @RetencionArrendadorPct / 100.0) AS decimal(19, 2)) AS EstimatedOwnerFinalPayout
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
        CAST(fin.GrossIncome AS decimal(19, 2)) AS GrossIncome401,
        CAST(fin.SalesReturns AS decimal(19, 2)) AS SalesReturns402,
        CAST(fin.NetAccountingIncome AS decimal(19, 2)) AS NetAccountingIncome,
        CAST(fin.CostOfSales AS decimal(19, 2)) AS CostOfSales501504,
        CAST(fin.NetAccountingIncome - fin.CostOfSales AS decimal(19, 2)) AS GrossProfit,
        CAST(100.0 * (fin.NetAccountingIncome - fin.CostOfSales) / NULLIF(fin.NetAccountingIncome, 0) AS decimal(9, 2)) AS GrossMarginPct,
        CAST(fin.BaseOperatingExpenses AS decimal(19, 2)) AS OperatingExpenses602605,
        CAST(fin.GeneralExpenses AS decimal(19, 2)) AS GeneralExpenses601,
        CAST(fin.OtherOperatingExpenses AS decimal(19, 2)) AS OtherOperatingExpenses606,
        CAST(fin.Depreciation AS decimal(19, 2)) AS Depreciation613,
        CAST(fin.Amortization AS decimal(19, 2)) AS Amortization614,
        CAST(fin.EstimatedOperatingEbitda AS decimal(19, 2)) AS EstimatedOperatingEbitda,
        CAST(fin.NormalizedOperatingResult AS decimal(19, 2)) AS OperatingResult,
        CAST(fin.FinancialExpenses AS decimal(19, 2)) AS FinancialExpenses701,
        CAST(fin.FinancialIncome AS decimal(19, 2)) AS FinancialIncome702,
        CAST(fin.OtherIncome AS decimal(19, 2)) AS OtherIncome704,
        CAST(fin.OtherExpenses AS decimal(19, 2)) AS OtherExpenses703,
        CAST(fin.OtherNet AS decimal(19, 2)) AS OtherNet,
        CAST(fin.Taxes AS decimal(19, 2)) AS Taxes611,
        CAST(fin.ProfitSharing AS decimal(19, 2)) AS ProfitSharing607610,
        CAST(fin.NonDeductible AS decimal(19, 2)) AS NonDeductible612,
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
            ISNULL(f.GeneralExpenses, 0) AS GeneralExpenses,
            ISNULL(f.OperatingExpenses, 0) AS BaseOperatingExpenses,
            ISNULL(f.OtherOperatingExpenses, 0) AS OtherOperatingExpenses,
            ISNULL(f.GeneralExpenses, 0) + ISNULL(f.OperatingExpenses, 0) + ISNULL(f.OtherOperatingExpenses, 0) AS OperatingExpenses,
            ISNULL(f.Depreciation, 0) AS Depreciation,
            ISNULL(f.Amortization, 0) AS Amortization,
            ISNULL(f.ProfitSharing, 0) AS ProfitSharing,
            ISNULL(f.NonDeductible, 0) AS NonDeductible,
            ISNULL(f.FinancialExpenses, 0) AS FinancialExpenses,
            ISNULL(f.FinancialIncome, 0) AS FinancialIncome,
            ISNULL(f.OtherIncome, 0) AS OtherIncome,
            ISNULL(f.OtherExpenses, 0) AS OtherExpenses,
            ISNULL(f.OtherIncome, 0) - ISNULL(f.OtherExpenses, 0) AS OtherNet,
            ISNULL(f.Taxes, 0) AS Taxes,
            ISNULL(f.GrossIncome, 0) - ISNULL(f.SalesReturns, 0)
                - ISNULL(f.CostOfSales, 0)
                - ISNULL(f.GeneralExpenses, 0)
                - ISNULL(f.OperatingExpenses, 0)
                - ISNULL(f.OtherOperatingExpenses, 0) AS EstimatedOperatingEbitda,
            ISNULL(f.GrossIncome, 0) - ISNULL(f.SalesReturns, 0)
                - ISNULL(f.CostOfSales, 0)
                - ISNULL(f.GeneralExpenses, 0)
                - ISNULL(f.OperatingExpenses, 0)
                - ISNULL(f.OtherOperatingExpenses, 0)
                - ISNULL(f.Depreciation, 0)
                - ISNULL(f.Amortization, 0) AS NormalizedOperatingResult,
            ISNULL(f.GrossIncome, 0) - ISNULL(f.SalesReturns, 0)
                - ISNULL(f.CostOfSales, 0)
                - ISNULL(f.GeneralExpenses, 0)
                - ISNULL(f.OperatingExpenses, 0)
                - ISNULL(f.OtherOperatingExpenses, 0)
                - ISNULL(f.Depreciation, 0)
                - ISNULL(f.Amortization, 0)
                + ISNULL(f.FinancialIncome, 0) - ISNULL(f.FinancialExpenses, 0)
                + ISNULL(f.OtherIncome, 0) - ISNULL(f.OtherExpenses, 0)
                - ISNULL(f.ProfitSharing, 0)
                - ISNULL(f.Taxes, 0)
                - ISNULL(f.NonDeductible, 0) AS NetResult
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

    INSERT INTO #DataQuality
        (SortOrder, PeriodLabel, PeriodScope, CheckType, Severity, Item, ItemCount, MetricAmount, ReferenceAmount, NetEffect, SampleReference, Notes)
    SELECT
        p.SortOrder, p.PeriodLabel, p.PeriodScope,
        'Calendario esperado', 'Alta', 'Noche rentable sin renglon de calendario',
        COUNT(*), NULL, NULL, NULL,
        MIN(CONCAT(cr.RoomName, ' ', CONVERT(varchar(10), cr.RoomDate, 23))),
        'Las noches disponibles se calculan aun cuando falta el calendario; el hueco requiere correccion operativa.'
    FROM #CalendarRows cr
    INNER JOIN #Periods p ON p.PeriodKey = cr.PeriodKey
    WHERE cr.RoomCalendarID IS NULL
    GROUP BY p.SortOrder, p.PeriodLabel, p.PeriodScope;

    INSERT INTO #DataQuality
        (SortOrder, PeriodLabel, PeriodScope, CheckType, Severity, Item, ItemCount, MetricAmount, ReferenceAmount, NetEffect, SampleReference, Notes)
    SELECT
        p.SortOrder, p.PeriodLabel, p.PeriodScope,
        'Precio de hospedaje', 'Alta', 'Noche vendida con precio cero',
        COUNT(*), 0, NULL, 0,
        MIN(CONCAT('reservacion_id=', cr.ReservationID, ' ', cr.RoomName, ' ', CONVERT(varchar(10), cr.RoomDate, 23))),
        'La noche no cuenta como vendida ni genera ingreso; revisar la tarifa antes del cierre.'
    FROM #CalendarRows cr
    INNER JOIN #Periods p ON p.PeriodKey = cr.PeriodKey
    WHERE cr.IsLocked = 1
      AND cr.ReservationStatus IN ('ACTIVA', 'PAGADA')
      AND cr.ReservationID IS NOT NULL
      AND cr.Precio = 0
    GROUP BY p.SortOrder, p.PeriodLabel, p.PeriodScope;

    INSERT INTO #DataQuality
        (SortOrder, PeriodLabel, PeriodScope, CheckType, Severity, Item, ItemCount, MetricAmount, ReferenceAmount, NetEffect, SampleReference, Notes)
    SELECT
        p.SortOrder, p.PeriodLabel, p.PeriodScope,
        'Pipeline', 'Baja', 'Cotizaciones excluidas de realizado y cobranza',
        pipeline.PipelineReservationCount, pipeline.PipelineReservationTotal, NULL, NULL, NULL,
        'Se presentan por separado como oportunidad comercial on-books, sin afectar KPIs realizados.'
    FROM #PipelineAgg pipeline
    INNER JOIN #Periods p ON p.PeriodKey = pipeline.PeriodKey
    WHERE pipeline.PipelineReservationCount > 0;

    INSERT INTO #DataQuality
        (SortOrder, PeriodLabel, PeriodScope, CheckType, Severity, Item, ItemCount, MetricAmount, ReferenceAmount, NetEffect, SampleReference, Notes)
    SELECT
        p.SortOrder, p.PeriodLabel, p.PeriodScope,
        'Mapeo contable', 'Alta', CONCAT('Familia activa sin mapeo: ', rc.Nivel1),
        COUNT(DISTINCT t.ID),
        CAST(SUM(ABS(rc.Debe - rc.Haber)) AS decimal(19,2)), NULL, NULL,
        MIN(CONCAT('transaccion_id=', t.ID)),
        'Ningun grupo contable activo puede desaparecer silenciosamente del estado financiero.'
    FROM #Periods p
    INNER JOIN dbo.Transacciones t
      ON t.Fecha >= p.DateStart AND t.Fecha < p.DateEndExcl AND t.RFC = @RfcTrim
    INNER JOIN dbo.Registro_Contable rc ON rc.TransaccionID = t.ID
    WHERE rc.Nivel1 >= '400' AND rc.Nivel1 < '800'
      AND rc.Nivel1 NOT IN ('401','402','403','501','502','503','504','505','601','602','603','604','605','606','607','608','609','610','611','612','613','614','701','702','703','704')
      AND ABS(rc.Debe - rc.Haber) > 0.005
    GROUP BY p.SortOrder, p.PeriodLabel, p.PeriodScope, rc.Nivel1;

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

    /* Result set 6: metadata verificable del reporte. */
    SELECT
        @RfcTrim AS Rfc,
        CAST(@FechaCorteEfectiva AS datetime) AS CutoffDate,
        SYSUTCDATETIME() AS GeneratedAtUtc,
        CAST(CASE WHEN @PeriodoRealFinExcl < @PeriodoFinExcl THEN 1 ELSE 0 END AS bit) AS IsProvisional,
        @HospedajeHabilitado AS LodgingEnabled,
        @RetencionArrendadorPct AS OwnerWithholdingPct,
        CAST(CASE WHEN EXISTS (SELECT 1 FROM #DataQuality WHERE SortOrder = 1 AND Severity = 'Alta') THEN 0 ELSE 1 END AS bit) AS RatiosAvailable,
        CASE WHEN EXISTS (SELECT 1 FROM #DataQuality WHERE SortOrder = 1 AND Severity = 'Alta')
             THEN 'Ratios ocultos por conciliaciones o calidad de datos de severidad alta.'
             ELSE 'Ratios disponibles con informacion interna no auditada.' END AS RatioAvailabilityNotes,
        'Salud Financiera v2' AS MethodologyVersion;

    /* Result set 7: tendencia mensual de 12 meses. */
    DECLARE @TrendStart date = DATEADD(MONTH, -11, @PeriodoFinMes);
    ;WITH Months AS
    (
        SELECT @TrendStart AS MonthStart
        UNION ALL
        SELECT DATEADD(MONTH, 1, MonthStart)
        FROM Months
        WHERE MonthStart < @PeriodoFinMes
    )
    SELECT
        CAST(m.MonthStart AS datetime) AS [Month],
        CONVERT(char(7), m.MonthStart, 120) AS MonthLabel,
        CAST(ISNULL(hotel.RoomRevenue, 0) AS decimal(19,2)) AS RoomRevenue,
        CAST(ISNULL(extra.ExtrasRevenue, 0) + ISNULL(experience.ExperiencesRevenue, 0) AS decimal(19,2)) AS ComplementaryRevenue,
        CAST(CASE WHEN @HospedajeHabilitado = 1
                  THEN ISNULL(hotel.RoomRevenue, 0) + ISNULL(extra.ExtrasRevenue, 0) + ISNULL(experience.ExperiencesRevenue, 0)
                  ELSE ISNULL(fin.NetIncome, 0)
             END AS decimal(19,2)) AS TotalOperatingRevenue,
        CAST(ISNULL(fin.NetResult, 0) AS decimal(19,2)) AS NetResult,
        CAST(100.0 * ISNULL(fin.OperatingResult, 0) / NULLIF(ISNULL(fin.NetIncome, 0), 0) AS decimal(9,2)) AS OperatingMarginPct,
        CAST(100.0 * ISNULL(hotel.OccupiedNights, 0) / NULLIF(CASE WHEN @HospedajeHabilitado = 1 THEN rr.RoomCount * DATEDIFF(DAY, m.MonthStart, DATEADD(MONTH,1,m.MonthStart)) ELSE 0 END, 0) AS decimal(9,2)) AS OccupancyPct,
        CAST(ISNULL(hotel.RoomRevenue, 0) / NULLIF(ISNULL(hotel.OccupiedNights, 0), 0) AS decimal(19,2)) AS ADR,
        CAST(ISNULL(hotel.RoomRevenue, 0) / NULLIF(CASE WHEN @HospedajeHabilitado = 1 THEN rr.RoomCount * DATEDIFF(DAY, m.MonthStart, DATEADD(MONTH,1,m.MonthStart)) ELSE 0 END, 0) AS decimal(19,2)) AS RevPAR,
        target.IngresoHabitacionMeta + target.IngresoComplementarioMeta AS RevenueTarget,
        target.ResultadoNetoMeta AS NetResultTarget,
        CAST(CASE WHEN @HospedajeHabilitado = 1
                  THEN ISNULL(previousYear.TotalRevenue, 0)
                  ELSE ISNULL(previousYearFinancial.NetIncome, 0)
             END AS decimal(19,2)) AS PreviousYearRevenue
    FROM Months m
    CROSS JOIN (SELECT COUNT(*) AS RoomCount FROM #RentableRooms) rr
    OUTER APPLY
    (
        SELECT
            SUM(CASE WHEN UPPER(LTRIM(RTRIM(ISNULL(r.STATUS,'')))) IN ('ACTIVA','PAGADA') AND rc.PRECIO > 0
                     THEN ROUND(CAST(rc.PRECIO AS decimal(19,4)) - ROUND(CAST(rc.PRECIO AS decimal(19,4)) * ISNULL(r.SUITE_DISCOUNT_PERCENT,0) / 100.0, 2), 2) ELSE 0 END) AS RoomRevenue,
            SUM(CASE WHEN UPPER(LTRIM(RTRIM(ISNULL(r.STATUS,'')))) IN ('ACTIVA','PAGADA') AND rc.PRECIO > 0 THEN 1 ELSE 0 END) AS OccupiedNights
        FROM dbo.ROOM_CALENDAR rc
        INNER JOIN #RentableRooms rooms ON rooms.RoomName = rc.ROOM
        LEFT JOIN dbo.RESERVATION r ON r.ID = TRY_CONVERT(int, NULLIF(LTRIM(RTRIM(rc.LOCK_DESCRIPTION)),''))
        WHERE rc.ROOM_DATE >= m.MonthStart AND rc.ROOM_DATE < DATEADD(MONTH,1,m.MonthStart)
    ) hotel
    OUTER APPLY
    (
        SELECT SUM(CASE WHEN UPPER(ISNULL(re.TaxMode,'TaxableExclusive'))='TAXINCLUDED'
                        THEN ROUND(CAST(re.UnitPriceSnapshot * re.Quantity AS decimal(19,4))/1.16,2)
                        ELSE ROUND(CAST(re.UnitPriceSnapshot * re.Quantity AS decimal(19,4)),2) END) AS ExtrasRevenue
        FROM dbo.RESERVATION r
        INNER JOIN dbo.Reservation_Extra re ON re.ReservationID = r.ID
        WHERE @HospedajeHabilitado = 1
          AND r.CHECKIN >= m.MonthStart AND r.CHECKIN < DATEADD(MONTH,1,m.MonthStart)
          AND UPPER(LTRIM(RTRIM(ISNULL(r.STATUS,'')))) IN ('ACTIVA','PAGADA')
    ) extra
    OUTER APPLY
    (
        SELECT SUM(CASE WHEN UPPER(ISNULL(re.TaxMode,'TaxableExclusive'))='TAXINCLUDED'
                        THEN ROUND(CAST(re.TotalSnapshot AS decimal(19,4))/1.16,2)
                        ELSE ROUND(CAST(re.TotalSnapshot AS decimal(19,4)),2) END) AS ExperiencesRevenue
        FROM dbo.Reservation_Experience re
        INNER JOIN dbo.RESERVATION r ON r.ID = re.ReservationID
        WHERE @HospedajeHabilitado = 1
          AND re.ExperienceDate >= m.MonthStart AND re.ExperienceDate < DATEADD(MONTH,1,m.MonthStart)
          AND UPPER(LTRIM(RTRIM(ISNULL(r.STATUS,'')))) IN ('ACTIVA','PAGADA')
    ) experience
    OUTER APPLY
    (
        SELECT
            SUM(CASE WHEN rc.Nivel1 IN ('401','403') THEN rc.Haber-rc.Debe ELSE 0 END)
              - SUM(CASE WHEN rc.Nivel1='402' THEN rc.Debe-rc.Haber ELSE 0 END) AS NetIncome,
            SUM(CASE WHEN rc.Nivel1 IN ('401','403') THEN rc.Haber-rc.Debe ELSE 0 END)
              - SUM(CASE WHEN rc.Nivel1='402' THEN rc.Debe-rc.Haber ELSE 0 END)
              - SUM(CASE WHEN rc.Nivel1 IN ('501','502','503','504','505','601','602','603','604','605','606','613','614') THEN rc.Debe-rc.Haber ELSE 0 END) AS OperatingResult,
            SUM(CASE WHEN rc.Nivel1 IN ('401','403','702','704') THEN rc.Haber-rc.Debe
                     WHEN rc.Nivel1 IN ('402','501','502','503','504','505','601','602','603','604','605','606','607','608','609','610','611','612','613','614','701','703') THEN rc.Haber-rc.Debe ELSE 0 END) AS NetResult
        FROM dbo.Transacciones t
        INNER JOIN dbo.Registro_Contable rc ON rc.TransaccionID=t.ID
        WHERE t.RFC=@RfcTrim AND t.Fecha>=m.MonthStart AND t.Fecha<DATEADD(MONTH,1,m.MonthStart)
          AND UPPER(ISNULL(rc.Nombre_Cuenta,'')) NOT LIKE '%PENDIENTES DE REGISTRO%'
    ) fin
    LEFT JOIN reporteFinanciero.SaludEmpresaMeta target ON target.RFC=@RfcTrim AND target.Mes=m.MonthStart
    OUTER APPLY
    (
        SELECT
          SUM(CASE WHEN UPPER(LTRIM(RTRIM(ISNULL(r.STATUS,'')))) IN ('ACTIVA','PAGADA') AND rc.PRECIO>0
                   THEN ROUND(CAST(rc.PRECIO AS decimal(19,4))-ROUND(CAST(rc.PRECIO AS decimal(19,4))*ISNULL(r.SUITE_DISCOUNT_PERCENT,0)/100.0,2),2) ELSE 0 END)
          + ISNULL((SELECT SUM(CASE WHEN UPPER(ISNULL(re.TaxMode,'TaxableExclusive'))='TAXINCLUDED' THEN ROUND(CAST(re.UnitPriceSnapshot*re.Quantity AS decimal(19,4))/1.16,2) ELSE ROUND(CAST(re.UnitPriceSnapshot*re.Quantity AS decimal(19,4)),2) END)
                    FROM dbo.RESERVATION rx INNER JOIN dbo.Reservation_Extra re ON re.ReservationID=rx.ID
                    WHERE @HospedajeHabilitado=1 AND rx.CHECKIN>=DATEADD(YEAR,-1,m.MonthStart) AND rx.CHECKIN<DATEADD(YEAR,-1,DATEADD(MONTH,1,m.MonthStart)) AND UPPER(LTRIM(RTRIM(ISNULL(rx.STATUS,'')))) IN ('ACTIVA','PAGADA')),0)
          + ISNULL((SELECT SUM(CASE WHEN UPPER(ISNULL(rex.TaxMode,'TaxableExclusive'))='TAXINCLUDED' THEN ROUND(CAST(rex.TotalSnapshot AS decimal(19,4))/1.16,2) ELSE ROUND(CAST(rex.TotalSnapshot AS decimal(19,4)),2) END)
                    FROM dbo.Reservation_Experience rex INNER JOIN dbo.RESERVATION rx ON rx.ID=rex.ReservationID
                    WHERE @HospedajeHabilitado=1 AND rex.ExperienceDate>=DATEADD(YEAR,-1,m.MonthStart) AND rex.ExperienceDate<DATEADD(YEAR,-1,DATEADD(MONTH,1,m.MonthStart)) AND UPPER(LTRIM(RTRIM(ISNULL(rx.STATUS,'')))) IN ('ACTIVA','PAGADA')),0) AS TotalRevenue
        FROM dbo.ROOM_CALENDAR rc
        INNER JOIN #RentableRooms rooms ON rooms.RoomName=rc.ROOM
        LEFT JOIN dbo.RESERVATION r ON r.ID=TRY_CONVERT(int,NULLIF(LTRIM(RTRIM(rc.LOCK_DESCRIPTION)),''))
        WHERE rc.ROOM_DATE>=DATEADD(YEAR,-1,m.MonthStart) AND rc.ROOM_DATE<DATEADD(YEAR,-1,DATEADD(MONTH,1,m.MonthStart))
    ) previousYear
    OUTER APPLY
    (
        SELECT
            SUM(CASE WHEN rc.Nivel1 IN ('401','403') THEN rc.Haber-rc.Debe ELSE 0 END)
              - SUM(CASE WHEN rc.Nivel1='402' THEN rc.Debe-rc.Haber ELSE 0 END) AS NetIncome
        FROM dbo.Transacciones t
        INNER JOIN dbo.Registro_Contable rc ON rc.TransaccionID=t.ID
        WHERE t.RFC=@RfcTrim
          AND t.Fecha>=DATEADD(YEAR,-1,m.MonthStart)
          AND t.Fecha<DATEADD(YEAR,-1,DATEADD(MONTH,1,m.MonthStart))
          AND UPPER(ISNULL(rc.Nombre_Cuenta,'')) NOT LIKE '%PENDIENTES DE REGISTRO%'
    ) previousYearFinancial
    ORDER BY m.MonthStart
    OPTION (MAXRECURSION 100);

    /* Result set 8: mezcla de ingresos operativos del MTD. */
    SELECT RevenueType, CAST(Amount AS decimal(19,2)) AS Amount,
           CAST(100.0*Amount/NULLIF(SUM(Amount) OVER(),0) AS decimal(9,2)) AS MixPct
    FROM
    (
        SELECT 'Habitacion' AS RevenueType, ISNULL((SELECT RoomRevenue FROM #SalesAgg WHERE PeriodKey=1),0) AS Amount
        UNION ALL SELECT 'Extras', ISNULL((SELECT ExtrasRevenue FROM #ExtrasAgg WHERE PeriodKey=1),0)
        UNION ALL SELECT 'Experiencias', ISNULL((SELECT ExperiencesRevenue FROM #ExperiencesAgg WHERE PeriodKey=1),0)
    ) mix;

    /* Result set 9: gastos por familia y cuenta. */
    SELECT
        CASE
          WHEN rc.Nivel1 IN ('501','502','503','504','505') THEN 'Costos'
          WHEN rc.Nivel1='601' THEN 'Gastos generales'
          WHEN rc.Nivel1 IN ('602','603','604','605','606') THEN 'Gastos operativos'
          WHEN rc.Nivel1 IN ('613','614') THEN 'Depreciacion y amortizacion'
          WHEN rc.Nivel1 IN ('701','702') THEN 'Resultado financiero'
          WHEN rc.Nivel1 IN ('703','704') THEN 'Otros resultados'
          WHEN rc.Nivel1 IN ('607','608','609','610','611','612') THEN 'Participaciones e impuestos'
          ELSE 'Sin mapear' END AS AccountFamily,
        CONCAT(rc.Nivel1,'.',rc.Nivel2,'.',rc.Nivel3) AS AccountCode,
        MAX(rc.Nombre_Cuenta) AS AccountName,
        CAST(SUM(CASE WHEN rc.Nivel1 IN ('702','704') THEN rc.Debe-rc.Haber ELSE rc.Debe-rc.Haber END) AS decimal(19,2)) AS Amount,
        CAST(100.0*SUM(ABS(rc.Debe-rc.Haber))/NULLIF(SUM(SUM(ABS(rc.Debe-rc.Haber))) OVER(),0) AS decimal(9,2)) AS MixPct,
        CAST(CASE WHEN rc.Nivel1 IN ('501','502','503','504','505','601','602','603','604','605','606','607','608','609','610','611','612','613','614','701','702','703','704') THEN 1 ELSE 0 END AS bit) AS IsMapped
    FROM dbo.Transacciones t
    INNER JOIN dbo.Registro_Contable rc ON rc.TransaccionID=t.ID
    WHERE t.RFC=@RfcTrim AND t.Fecha>=@PeriodoInicio AND t.Fecha<@PeriodoRealFinExcl
      AND rc.Nivel1>='500' AND rc.Nivel1<'800'
      AND UPPER(ISNULL(rc.Nombre_Cuenta,'')) NOT LIKE '%PENDIENTES DE REGISTRO%'
    GROUP BY rc.Nivel1,rc.Nivel2,rc.Nivel3
    ORDER BY ABS(SUM(rc.Debe-rc.Haber)) DESC;

    /* Result set 10: snapshot de liquidez y capital de trabajo. */
    ;WITH Balance AS
    (
      SELECT rc.Nivel1,
        SUM(CASE WHEN rc.Nivel1<'200' THEN rc.Debe-rc.Haber ELSE rc.Haber-rc.Debe END) AS Amount
      FROM dbo.Transacciones t
      INNER JOIN dbo.Registro_Contable rc ON rc.TransaccionID=t.ID
      WHERE t.RFC=@RfcTrim AND t.Fecha<DATEADD(DAY,1,@FechaCorteEfectiva)
      GROUP BY rc.Nivel1
    ), Snapshot AS
    (
      SELECT 'cash' MetricKey, 'Caja y bancos' MetricLabel, ISNULL(SUM(CASE WHEN Nivel1 IN ('101','102') THEN Amount END),0) Amount FROM Balance
      UNION ALL SELECT 'receivables','Clientes',ISNULL(SUM(CASE WHEN Nivel1 IN ('105','106') THEN Amount END),0) FROM Balance
      UNION ALL SELECT 'suppliers','Proveedores',ISNULL(SUM(CASE WHEN Nivel1 IN ('201','202') THEN Amount END),0) FROM Balance
      UNION ALL SELECT 'creditors','Acreedores',ISNULL(SUM(CASE WHEN Nivel1 IN ('203','204','205') THEN Amount END),0) FROM Balance
      UNION ALL SELECT 'taxes','Impuestos por pagar',ISNULL(SUM(CASE WHEN Nivel1 IN ('206','207','208') THEN Amount END),0) FROM Balance
      UNION ALL SELECT 'owners','Obligaciones de arrendadores',ISNULL(SUM(CASE WHEN Nivel1 IN ('209','210') THEN Amount END),0) FROM Balance
      UNION ALL SELECT 'working_capital','Capital de trabajo',
        ISNULL(SUM(CASE WHEN Nivel1 IN ('101','102','105','106') THEN Amount WHEN Nivel1 IN ('201','202','203','204','205','206','207','208','209','210') THEN -Amount ELSE 0 END),0) FROM Balance
    )
    SELECT MetricKey,MetricLabel,CAST(Amount AS decimal(19,2)) Amount,
      CAST(CASE WHEN EXISTS(SELECT 1 FROM #DataQuality WHERE SortOrder=1 AND Severity='Alta') THEN 0 ELSE 1 END AS bit) IsAvailable,
      CASE WHEN EXISTS(SELECT 1 FROM #DataQuality WHERE SortOrder=1 AND Severity='Alta') THEN 'No disponible por conciliaciones de severidad alta.' ELSE NULL END Notes
    FROM Snapshot;

    /* Result set 11: variaciones del mes seleccionado contra meta. */
    ;WITH Actual AS
    (
      SELECT
        ISNULL(s.RoomRevenue,0) RoomRevenue,
        ISNULL(e.ExtrasRevenue,0)+ISNULL(x.ExperiencesRevenue,0) ComplementaryRevenue,
        CAST(100.0*ISNULL(s.OccupiedNights,0)/NULLIF(ISNULL(s.AvailableNights,0),0) AS decimal(19,4)) OccupancyPct,
        CAST(ISNULL(s.RoomRevenue,0)/NULLIF(ISNULL(s.OccupiedNights,0),0) AS decimal(19,4)) ADR,
        ISNULL(f.GeneralExpenses,0)+ISNULL(f.OperatingExpenses,0)+ISNULL(f.OtherOperatingExpenses,0) OperatingExpenses,
        ISNULL(f.GrossIncome,0)-ISNULL(f.SalesReturns,0)-ISNULL(f.CostOfSales,0)-ISNULL(f.GeneralExpenses,0)-ISNULL(f.OperatingExpenses,0)-ISNULL(f.OtherOperatingExpenses,0)-ISNULL(f.Depreciation,0)-ISNULL(f.Amortization,0)+ISNULL(f.FinancialIncome,0)-ISNULL(f.FinancialExpenses,0)+ISNULL(f.OtherIncome,0)-ISNULL(f.OtherExpenses,0)-ISNULL(f.ProfitSharing,0)-ISNULL(f.Taxes,0)-ISNULL(f.NonDeductible,0) NetResult,
        ISNULL(c.CashIn,0)-ISNULL(c.CashOut,0) NetCashFlow,
        ISNULL(c.ClosingCashBalance,0) ClosingCash
      FROM (SELECT 1 n) one
      LEFT JOIN #SalesAgg s ON s.PeriodKey=1
      LEFT JOIN #ExtrasAgg e ON e.PeriodKey=1
      LEFT JOIN #ExperiencesAgg x ON x.PeriodKey=1
      LEFT JOIN #FinancialAgg f ON f.PeriodKey=1
      LEFT JOIN #CashAgg c ON c.PeriodKey=1
    ), Metrics AS
    (
      SELECT 'room_revenue' MetricKey,'Ingreso de habitaciones' MetricLabel,a.RoomRevenue ActualValue,t.IngresoHabitacionMeta TargetValue,CAST(0 AS bit) LowerIsBetter FROM Actual a CROSS JOIN reporteFinanciero.SaludEmpresaMeta t WHERE t.RFC=@RfcTrim AND t.Mes=@PeriodoFinMes
      UNION ALL SELECT 'complementary_revenue','Ingreso complementario',a.ComplementaryRevenue,t.IngresoComplementarioMeta,0 FROM Actual a CROSS JOIN reporteFinanciero.SaludEmpresaMeta t WHERE t.RFC=@RfcTrim AND t.Mes=@PeriodoFinMes
      UNION ALL SELECT 'occupancy','Ocupacion',a.OccupancyPct,t.OcupacionPctMeta,0 FROM Actual a CROSS JOIN reporteFinanciero.SaludEmpresaMeta t WHERE t.RFC=@RfcTrim AND t.Mes=@PeriodoFinMes
      UNION ALL SELECT 'adr','ADR',a.ADR,t.ADRMeta,0 FROM Actual a CROSS JOIN reporteFinanciero.SaludEmpresaMeta t WHERE t.RFC=@RfcTrim AND t.Mes=@PeriodoFinMes
      UNION ALL SELECT 'operating_expenses','Gastos operativos',a.OperatingExpenses,t.GastosOperativosMeta,1 FROM Actual a CROSS JOIN reporteFinanciero.SaludEmpresaMeta t WHERE t.RFC=@RfcTrim AND t.Mes=@PeriodoFinMes
      UNION ALL SELECT 'net_result','Resultado neto',a.NetResult,t.ResultadoNetoMeta,0 FROM Actual a CROSS JOIN reporteFinanciero.SaludEmpresaMeta t WHERE t.RFC=@RfcTrim AND t.Mes=@PeriodoFinMes
      UNION ALL SELECT 'net_cash_flow','Flujo neto',a.NetCashFlow,t.FlujoNetoMeta,0 FROM Actual a CROSS JOIN reporteFinanciero.SaludEmpresaMeta t WHERE t.RFC=@RfcTrim AND t.Mes=@PeriodoFinMes
      UNION ALL SELECT 'closing_cash','Saldo de efectivo',a.ClosingCash,t.SaldoEfectivoMeta,0 FROM Actual a CROSS JOIN reporteFinanciero.SaludEmpresaMeta t WHERE t.RFC=@RfcTrim AND t.Mes=@PeriodoFinMes
    )
    SELECT CAST(@PeriodoFinMes AS datetime) [Month],MetricKey,MetricLabel,CAST(ActualValue AS decimal(19,2)) ActualValue,
      CAST(TargetValue AS decimal(19,2)) TargetValue,CAST(CASE WHEN TargetValue IS NULL THEN NULL ELSE ActualValue-TargetValue END AS decimal(19,2)) VarianceValue,
      CAST(CASE WHEN TargetValue IS NULL OR TargetValue=0 THEN NULL ELSE 100.0*(ActualValue-TargetValue)/ABS(TargetValue) END AS decimal(9,2)) VariancePct,LowerIsBetter
    FROM Metrics;

    /* Result set 12: outlook on-books diario de 90 dias. */
    ;WITH Dates AS
    (
      SELECT DATEADD(DAY,1,@FechaCorteEfectiva) [Date]
      UNION ALL SELECT DATEADD(DAY,1,[Date]) FROM Dates WHERE [Date]<DATEADD(DAY,90,@FechaCorteEfectiva)
    )
    SELECT CAST(d.[Date] AS datetime) [Date],
      CASE WHEN @HospedajeHabilitado=1 THEN rooms.RoomCount ELSE 0 END AvailableNights,
      ISNULL(booked.OnBooksNights,0) OnBooksNights,
      CAST(ISNULL(booked.RoomRevenue,0) AS decimal(19,2)) RoomRevenue,
      CAST(ISNULL(complementary.Amount,0) AS decimal(19,2)) ComplementaryRevenue,
      CAST(100.0*ISNULL(booked.OnBooksNights,0)/NULLIF(CASE WHEN @HospedajeHabilitado=1 THEN rooms.RoomCount ELSE 0 END,0) AS decimal(9,2)) OccupancyPct
    FROM Dates d
    CROSS JOIN (SELECT COUNT(*) RoomCount FROM #RentableRooms) rooms
    OUTER APPLY
    (
      SELECT COUNT(*) OnBooksNights,
        SUM(ROUND(CAST(rc.PRECIO AS decimal(19,4))-ROUND(CAST(rc.PRECIO AS decimal(19,4))*ISNULL(r.SUITE_DISCOUNT_PERCENT,0)/100.0,2),2)) RoomRevenue
      FROM dbo.ROOM_CALENDAR rc INNER JOIN #RentableRooms rr ON rr.RoomName=rc.ROOM
      INNER JOIN dbo.RESERVATION r ON r.ID=TRY_CONVERT(int,NULLIF(LTRIM(RTRIM(rc.LOCK_DESCRIPTION)),''))
      WHERE rc.ROOM_DATE=d.[Date] AND rc.IS_LOCKED=1 AND rc.PRECIO>0 AND UPPER(LTRIM(RTRIM(ISNULL(r.STATUS,'')))) IN ('ACTIVA','PAGADA')
    ) booked
    OUTER APPLY
    (
      SELECT
        ISNULL((SELECT SUM(CASE WHEN UPPER(ISNULL(re.TaxMode,'TaxableExclusive'))='TAXINCLUDED' THEN ROUND(CAST(re.UnitPriceSnapshot*re.Quantity AS decimal(19,4))/1.16,2) ELSE ROUND(CAST(re.UnitPriceSnapshot*re.Quantity AS decimal(19,4)),2) END)
                FROM dbo.RESERVATION r INNER JOIN dbo.Reservation_Extra re ON re.ReservationID=r.ID WHERE @HospedajeHabilitado=1 AND r.CHECKIN=d.[Date] AND UPPER(LTRIM(RTRIM(ISNULL(r.STATUS,'')))) IN ('ACTIVA','PAGADA')),0)
        + ISNULL((SELECT SUM(CASE WHEN UPPER(ISNULL(re.TaxMode,'TaxableExclusive'))='TAXINCLUDED' THEN ROUND(CAST(re.TotalSnapshot AS decimal(19,4))/1.16,2) ELSE ROUND(CAST(re.TotalSnapshot AS decimal(19,4)),2) END)
                FROM dbo.Reservation_Experience re INNER JOIN dbo.RESERVATION r ON r.ID=re.ReservationID WHERE @HospedajeHabilitado=1 AND re.ExperienceDate=d.[Date] AND UPPER(LTRIM(RTRIM(ISNULL(r.STATUS,'')))) IN ('ACTIVA','PAGADA')),0) Amount
    ) complementary
    ORDER BY d.[Date] OPTION(MAXRECURSION 100);

    /* Result set 13: outlook on-books mensual de 12 meses. */
    ;WITH Months AS
    (
      SELECT DATEFROMPARTS(YEAR(@FechaCorteEfectiva),MONTH(@FechaCorteEfectiva),1) [Month]
      UNION ALL SELECT DATEADD(MONTH,1,[Month]) FROM Months WHERE [Month]<DATEADD(MONTH,11,DATEFROMPARTS(YEAR(@FechaCorteEfectiva),MONTH(@FechaCorteEfectiva),1))
    )
    SELECT CAST(m.[Month] AS datetime) [Month],
      CASE WHEN @HospedajeHabilitado=1 THEN rooms.RoomCount*DATEDIFF(DAY,m.[Month],DATEADD(MONTH,1,m.[Month])) ELSE 0 END AvailableNights,
      ISNULL(booked.OnBooksNights,0) OnBooksNights,
      CAST(ISNULL(booked.RoomRevenue,0) AS decimal(19,2)) RoomRevenue,
      CAST(ISNULL(extra.Amount,0)+ISNULL(experience.Amount,0) AS decimal(19,2)) ComplementaryRevenue,
      CAST(100.0*ISNULL(booked.OnBooksNights,0)/NULLIF(CASE WHEN @HospedajeHabilitado=1 THEN rooms.RoomCount*DATEDIFF(DAY,m.[Month],DATEADD(MONTH,1,m.[Month])) ELSE 0 END,0) AS decimal(9,2)) OccupancyPct
    FROM Months m CROSS JOIN (SELECT COUNT(*) RoomCount FROM #RentableRooms) rooms
    OUTER APPLY
    (
      SELECT COUNT(*) OnBooksNights,SUM(ROUND(CAST(rc.PRECIO AS decimal(19,4))-ROUND(CAST(rc.PRECIO AS decimal(19,4))*ISNULL(r.SUITE_DISCOUNT_PERCENT,0)/100.0,2),2)) RoomRevenue
      FROM dbo.ROOM_CALENDAR rc INNER JOIN #RentableRooms rr ON rr.RoomName=rc.ROOM
      INNER JOIN dbo.RESERVATION r ON r.ID=TRY_CONVERT(int,NULLIF(LTRIM(RTRIM(rc.LOCK_DESCRIPTION)),''))
      WHERE rc.ROOM_DATE>=m.[Month] AND rc.ROOM_DATE<DATEADD(MONTH,1,m.[Month]) AND rc.ROOM_DATE>=@FechaCorteEfectiva AND rc.IS_LOCKED=1 AND rc.PRECIO>0 AND UPPER(LTRIM(RTRIM(ISNULL(r.STATUS,'')))) IN ('ACTIVA','PAGADA')
    ) booked
    OUTER APPLY
    (
      SELECT SUM(CASE WHEN UPPER(ISNULL(re.TaxMode,'TaxableExclusive'))='TAXINCLUDED' THEN ROUND(CAST(re.UnitPriceSnapshot*re.Quantity AS decimal(19,4))/1.16,2) ELSE ROUND(CAST(re.UnitPriceSnapshot*re.Quantity AS decimal(19,4)),2) END) Amount
      FROM dbo.RESERVATION r INNER JOIN dbo.Reservation_Extra re ON re.ReservationID=r.ID
      WHERE @HospedajeHabilitado=1 AND r.CHECKIN>=m.[Month] AND r.CHECKIN<DATEADD(MONTH,1,m.[Month]) AND r.CHECKIN>=@FechaCorteEfectiva AND UPPER(LTRIM(RTRIM(ISNULL(r.STATUS,'')))) IN ('ACTIVA','PAGADA')
    ) extra
    OUTER APPLY
    (
      SELECT SUM(CASE WHEN UPPER(ISNULL(re.TaxMode,'TaxableExclusive'))='TAXINCLUDED' THEN ROUND(CAST(re.TotalSnapshot AS decimal(19,4))/1.16,2) ELSE ROUND(CAST(re.TotalSnapshot AS decimal(19,4)),2) END) Amount
      FROM dbo.Reservation_Experience re INNER JOIN dbo.RESERVATION r ON r.ID=re.ReservationID
      WHERE @HospedajeHabilitado=1 AND re.ExperienceDate>=m.[Month] AND re.ExperienceDate<DATEADD(MONTH,1,m.[Month]) AND re.ExperienceDate>=@FechaCorteEfectiva AND UPPER(LTRIM(RTRIM(ISNULL(r.STATUS,'')))) IN ('ACTIVA','PAGADA')
    ) experience
    ORDER BY m.[Month] OPTION(MAXRECURSION 20);
END;
GO
