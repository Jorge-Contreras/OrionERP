CREATE OR ALTER PROCEDURE reporteFinanciero.Reporte_Salud_Empresa_Conciliacion
  @RFC varchar(50),
  @FechaInicio date,
  @FechaFin date,
  @Pagina int = 1,
  @TamanoPagina int = 25,
  @Severidad varchar(20) = NULL,
  @Tipo varchar(80) = NULL,
  @Busqueda nvarchar(200) = NULL
AS
BEGIN
  SET NOCOUNT ON;

  IF @FechaFin < @FechaInicio THROW 51100, 'FechaFin debe ser mayor o igual a FechaInicio.', 1;
  SET @Pagina = CASE WHEN @Pagina < 1 THEN 1 ELSE @Pagina END;
  SET @TamanoPagina = CASE WHEN @TamanoPagina NOT BETWEEN 1 AND 100 THEN 25 ELSE @TamanoPagina END;
  SET @Severidad = NULLIF(LTRIM(RTRIM(@Severidad)), '');
  SET @Tipo = NULLIF(LTRIM(RTRIM(@Tipo)), '');
  SET @Busqueda = NULLIF(LTRIM(RTRIM(@Busqueda)), N'');
  DECLARE @HospedajeHabilitado bit = ISNULL((
    SELECT HospedajeHabilitado FROM reporteFinanciero.SaludEmpresaConfiguracion WHERE RFC=@RFC
  ),0);

  CREATE TABLE #Issues
  (
    Severity varchar(20) NOT NULL,
    [Type] varchar(80) NOT NULL,
    Item nvarchar(300) NOT NULL,
    EventDate date NULL,
    Amount decimal(19,2) NULL,
    ReferenceAmount decimal(19,2) NULL,
    NetEffect decimal(19,2) NULL,
    Reference nvarchar(300) NULL,
    Notes nvarchar(1000) NULL,
    ReservationId int NULL,
    TransactionId int NULL
  );

  INSERT #Issues
  SELECT
    'Baja', 'Pipeline', 'Cotizacion excluida de realizado, ocupacion y cobranza',
    r.CHECKIN, CAST(r.TOTAL_PRICE AS decimal(19,2)), NULL, NULL,
    CONCAT('reservacion_id=',r.ID), 'Se conserva como pipeline comercial on-books.', r.ID, NULL
  FROM dbo.RESERVATION r
  WHERE @HospedajeHabilitado=1
    AND r.CHECKIN>=@FechaInicio AND r.CHECKIN<DATEADD(DAY,1,@FechaFin)
    AND UPPER(LTRIM(RTRIM(ISNULL(r.STATUS,''))))='COTIZACION';

  INSERT #Issues
  SELECT
    CASE WHEN parsed.ReservationID IS NULL OR r.ID IS NULL THEN 'Alta' ELSE 'Media' END,
    'Calendario-reservacion',
    CASE WHEN NULLIF(LTRIM(RTRIM(rc.LOCK_DESCRIPTION)),'') IS NULL THEN 'Bloqueo sin ID de reservacion'
         WHEN parsed.ReservationID IS NULL THEN 'ID de reservacion no numerico'
         WHEN r.ID IS NULL THEN 'Reservacion referenciada no existe'
         WHEN UPPER(LTRIM(RTRIM(ISNULL(r.STATUS,'')))) NOT IN ('ACTIVA','PAGADA','COTIZACION') THEN 'Estado no valido en calendario'
         ELSE 'Bloqueo sin tarifa' END,
    rc.ROOM_DATE, CAST(rc.PRECIO AS decimal(19,2)), NULL, NULL,
    CONCAT(rc.ROOM,' ',CONVERT(varchar(10),rc.ROOM_DATE,23),' calendario_id=',rc.id),
    'Revisar el calendario y la reservacion antes del cierre.', r.ID, NULL
  FROM dbo.ROOM_CALENDAR rc
  INNER JOIN dbo.ROOM room ON room.ROOM_NAME=rc.ROOM AND room.IsActive=1 AND room.IsRentable=1
  CROSS APPLY (SELECT TRY_CONVERT(int,NULLIF(LTRIM(RTRIM(rc.LOCK_DESCRIPTION)),'')) ReservationID) parsed
  LEFT JOIN dbo.RESERVATION r ON r.ID=parsed.ReservationID
  WHERE @HospedajeHabilitado=1
    AND rc.ROOM_DATE>=@FechaInicio AND rc.ROOM_DATE<DATEADD(DAY,1,@FechaFin)
    AND rc.IS_LOCKED=1
    AND (parsed.ReservationID IS NULL OR r.ID IS NULL OR UPPER(LTRIM(RTRIM(ISNULL(r.STATUS,'')))) NOT IN ('ACTIVA','PAGADA','COTIZACION') OR ISNULL(rc.PRECIO,0)=0);

  ;WITH Dates AS
  (
    SELECT @FechaInicio [Date]
    UNION ALL SELECT DATEADD(DAY,1,[Date]) FROM Dates WHERE [Date]<@FechaFin
  )
  INSERT #Issues
  SELECT 'Alta','Calendario esperado','Noche rentable sin renglon de calendario',d.[Date],NULL,NULL,NULL,
    CONCAT(room.ROOM_NAME,' ',CONVERT(varchar(10),d.[Date],23)),
    'La disponibilidad esperada incluye esta noche, pero falta el renglon operativo.',NULL,NULL
  FROM Dates d CROSS JOIN dbo.ROOM room
  LEFT JOIN dbo.ROOM_CALENDAR rc ON rc.ROOM_DATE=d.[Date] AND rc.ROOM=room.ROOM_NAME
  WHERE @HospedajeHabilitado=1 AND room.IsActive=1 AND room.IsRentable=1 AND rc.id IS NULL
  OPTION(MAXRECURSION 32767);

  SELECT r.ID ReservationID,r.CHECKIN,r.TOTAL_PRICE,r.SUITE_DISCOUNT_PERCENT
  INTO #ReservationScope
  FROM dbo.RESERVATION r
  WHERE @HospedajeHabilitado=1
    AND r.CHECKIN>=@FechaInicio AND r.CHECKIN<DATEADD(DAY,1,@FechaFin)
    AND UPPER(LTRIM(RTRIM(ISNULL(r.STATUS,'')))) IN ('ACTIVA','PAGADA');
  CREATE UNIQUE CLUSTERED INDEX IX_ReservationScope ON #ReservationScope(ReservationID);

  SELECT rs.ReservationID,
    SUM(line.NetAmount) SuiteSubtotal,
    SUM(ROUND(line.NetAmount*0.16,2)) SuiteTax,
    SUM(CASE WHEN YEAR(rs.CHECKIN)<2025 THEN ROUND(line.NetAmount*0.02,2) ELSE 0 END) SuiteIsh
  INTO #SuiteTotals
  FROM dbo.ROOM_CALENDAR rc
  INNER JOIN dbo.ROOM room ON room.ROOM_NAME=rc.ROOM AND room.IsRentable=1
  INNER JOIN #ReservationScope rs ON rs.ReservationID=TRY_CONVERT(int,NULLIF(LTRIM(RTRIM(rc.LOCK_DESCRIPTION)),''))
  CROSS APPLY(SELECT ROUND(CAST(rc.PRECIO AS decimal(19,4))-ROUND(CAST(rc.PRECIO AS decimal(19,4))*ISNULL(rs.SUITE_DISCOUNT_PERCENT,0)/100.0,2),2) NetAmount) line
  WHERE rc.PRECIO>0
  GROUP BY rs.ReservationID;

  SELECT rs.ReservationID,
    SUM(CASE WHEN UPPER(ISNULL(re.TaxMode,'TaxableExclusive'))='TAXINCLUDED' THEN ROUND(line.Amount/1.16,2) ELSE line.Amount END) ExtraSubtotal,
    SUM(CASE WHEN UPPER(ISNULL(re.TaxMode,'TaxableExclusive'))='TAXINCLUDED' THEN line.Amount-ROUND(line.Amount/1.16,2)
             WHEN UPPER(ISNULL(re.TaxMode,'TaxableExclusive'))='NONTAXABLE' THEN 0 ELSE ROUND(line.Amount*0.16,2) END) ExtraTax,
    SUM(CASE WHEN YEAR(rs.CHECKIN)<2025 AND UPPER(ISNULL(re.TaxMode,'TaxableExclusive'))='TAXABLEEXCLUSIVE' THEN ROUND(line.Amount*0.02,2) ELSE 0 END) ExtraIsh
  INTO #ExtraTotals
  FROM #ReservationScope rs
  INNER JOIN dbo.Reservation_Extra re ON re.ReservationID=rs.ReservationID
  CROSS APPLY(SELECT ROUND(CAST(re.UnitPriceSnapshot*re.Quantity AS decimal(19,4)),2) Amount) line
  GROUP BY rs.ReservationID;

  SELECT rs.ReservationID,
    SUM(CASE WHEN UPPER(ISNULL(re.TaxMode,'TaxableExclusive'))='TAXINCLUDED' THEN ROUND(line.Amount/1.16,2) ELSE line.Amount END) ExperienceSubtotal,
    SUM(CASE WHEN UPPER(ISNULL(re.TaxMode,'TaxableExclusive'))='TAXINCLUDED' THEN line.Amount-ROUND(line.Amount/1.16,2)
             WHEN UPPER(ISNULL(re.TaxMode,'TaxableExclusive'))='NONTAXABLE' THEN 0 ELSE ROUND(line.Amount*0.16,2) END) ExperienceTax,
    SUM(CASE WHEN YEAR(rs.CHECKIN)<2025 AND UPPER(ISNULL(re.TaxMode,'TaxableExclusive'))='TAXABLEEXCLUSIVE' THEN ROUND(line.Amount*0.02,2) ELSE 0 END) ExperienceIsh
  INTO #ExperienceTotals
  FROM #ReservationScope rs
  INNER JOIN dbo.Reservation_Experience re ON re.ReservationID=rs.ReservationID
  CROSS APPLY(SELECT ROUND(CAST(re.TotalSnapshot AS decimal(19,4)),2) Amount) line
  GROUP BY rs.ReservationID;

  ;WITH Calculated AS
  (
    SELECT rs.ReservationID,rs.CHECKIN,rs.TOTAL_PRICE,
      ROUND(ISNULL(s.SuiteSubtotal,0)+ISNULL(s.SuiteTax,0)+ISNULL(s.SuiteIsh,0)
        +ISNULL(e.ExtraSubtotal,0)+ISNULL(e.ExtraTax,0)+ISNULL(e.ExtraIsh,0)
        +ISNULL(x.ExperienceSubtotal,0)+ISNULL(x.ExperienceTax,0)+ISNULL(x.ExperienceIsh,0),2) CalculatedTotal
    FROM #ReservationScope rs
    LEFT JOIN #SuiteTotals s ON s.ReservationID=rs.ReservationID
    LEFT JOIN #ExtraTotals e ON e.ReservationID=rs.ReservationID
    LEFT JOIN #ExperienceTotals x ON x.ReservationID=rs.ReservationID
  )
  INSERT #Issues
  SELECT 'Alta','Total de reservacion','Total almacenado no coincide con cargos recalculados',CHECKIN,
    CAST(TOTAL_PRICE AS decimal(19,2)),CAST(CalculatedTotal AS decimal(19,2)),CAST(TOTAL_PRICE-CalculatedTotal AS decimal(19,2)),
    CONCAT('reservacion_id=',ReservationID),
    'Recalculo con noches, descuento de suite, extras, experiencias, IVA e ISH historico.',ReservationID,NULL
  FROM Calculated WHERE ABS(TOTAL_PRICE-CalculatedTotal)>0.01;

  INSERT #Issues
  SELECT 'Alta','Pago no contabilizado','Pago ligado sin movimientos contables',r.CHECKIN,
    CAST(rt.Amount AS decimal(19,2)),NULL,CAST(rt.Amount AS decimal(19,2)),
    CONCAT('reservacion_id=',r.ID,' transaccion_id=',rt.TransaccionID),
    'La transaccion ligada no tiene renglones en Registro_Contable.',r.ID,rt.TransaccionID
  FROM dbo.RESERVATION r
  INNER JOIN dbo.Reservation_Transacciones rt ON rt.ReservationID=r.ID AND rt.Amount>0
  LEFT JOIN dbo.Registro_Contable rc ON rc.TransaccionID=rt.TransaccionID
  WHERE @HospedajeHabilitado=1
    AND r.CHECKIN>=@FechaInicio AND r.CHECKIN<DATEADD(DAY,1,@FechaFin)
    AND UPPER(LTRIM(RTRIM(ISNULL(r.STATUS,'')))) IN ('ACTIVA','PAGADA')
  GROUP BY r.CHECKIN,r.ID,rt.TransaccionID,rt.Amount
  HAVING COUNT(rc.id)=0;

  INSERT #Issues
  SELECT 'Media','Registro bancario pendiente',CONCAT(rc.Nivel1,'.',rc.Nivel2,'.',rc.Nivel3,' ',rc.Nombre_Cuenta),
    CAST(t.Fecha AS date),CAST(rc.Debe AS decimal(19,2)),CAST(rc.Haber AS decimal(19,2)),CAST(rc.Debe-rc.Haber AS decimal(19,2)),
    CONCAT('transaccion_id=',t.ID),
    'Excluido del resultado normalizado hasta completar la conciliacion bancaria.',NULL,t.ID
  FROM dbo.Transacciones t INNER JOIN dbo.Registro_Contable rc ON rc.TransaccionID=t.ID
  WHERE t.RFC=@RFC AND t.Fecha>=@FechaInicio AND t.Fecha<DATEADD(DAY,1,@FechaFin)
    AND UPPER(ISNULL(rc.Nombre_Cuenta,'')) LIKE '%PENDIENTES DE REGISTRO%';

  ;WITH Filtered AS
  (
    SELECT ROW_NUMBER() OVER(ORDER BY CASE Severity WHEN 'Alta' THEN 1 WHEN 'Media' THEN 2 ELSE 3 END,EventDate DESC,[Type],Reference) ReconciliationId,*
    FROM #Issues
    WHERE (@Severidad IS NULL OR Severity=@Severidad)
      AND (@Tipo IS NULL OR [Type]=@Tipo)
      AND (@Busqueda IS NULL OR CONCAT(Item,' ',Reference,' ',Notes) LIKE CONCAT('%',@Busqueda,'%'))
  )
  SELECT ReconciliationId,Severity,[Type],Item,CAST(EventDate AS datetime) EventDate,Amount,ReferenceAmount,NetEffect,Reference,Notes,ReservationId,TransactionId
  FROM Filtered
  ORDER BY ReconciliationId
  OFFSET (@Pagina-1)*@TamanoPagina ROWS FETCH NEXT @TamanoPagina ROWS ONLY;

  SELECT
    COUNT(*) TotalCount,
    SUM(CASE WHEN Severity='Alta' THEN 1 ELSE 0 END) HighCount,
    SUM(CASE WHEN Severity='Media' THEN 1 ELSE 0 END) MediumCount,
    SUM(CASE WHEN Severity='Baja' THEN 1 ELSE 0 END) LowCount
  FROM #Issues
  WHERE (@Severidad IS NULL OR Severity=@Severidad)
    AND (@Tipo IS NULL OR [Type]=@Tipo)
    AND (@Busqueda IS NULL OR CONCAT(Item,' ',Reference,' ',Notes) LIKE CONCAT('%',@Busqueda,'%'));
END;
GO
