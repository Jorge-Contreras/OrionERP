SET ANSI_NULLS ON;
GO
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE [cfdi].[CFDI_Poliza_Linking_Workspace]
    @Comprobante_Id int,
    @RFC varchar(50) = NULL,
    @Year int = NULL,
    @Month int = NULL,
    @TransaccionId int = NULL,
    @Concepto nvarchar(255) = NULL,
    @Monto decimal(19, 4) = NULL,
    @TipoPoliza varchar(50) = NULL,
    @FormaPago varchar(50) = NULL,
    @Top int = 200,
    @Tolerancia decimal(19, 4) = 1.00
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE
        @ContextRfc varchar(50) = NULLIF(LTRIM(RTRIM(@RFC)), ''),
        @TargetTotal decimal(19, 4),
        @TargetIva decimal(19, 4),
        @TargetFecha date,
        @TargetDirection varchar(20),
        @TargetTipo varchar(5),
        @Assigned decimal(19, 4),
        @Pending decimal(19, 4),
        @CandidateObjective decimal(19, 4);

    SELECT
        @TargetTotal = CAST(ISNULL(cd.Total, 0) AS decimal(19, 4)),
        @TargetIva = CAST(ISNULL(cd.IVA, 0) AS decimal(19, 4)),
        @TargetFecha = CAST(cd.Fecha AS date),
        @TargetTipo = cd.TipoDeComprobante,
        @TargetDirection = CASE
            WHEN cd.RFC_EMISOR = @ContextRfc THEN 'Emitido'
            WHEN cd.RFC_RECEPTOR = @ContextRfc THEN 'Recibido'
            ELSE 'Otro'
        END
    FROM cfdi.Comprobante_Detalle AS cd
    WHERE cd.Comprobante_Id = @Comprobante_Id
      AND cd.TipoDeComprobante IN ('I', 'N', 'E');

    SET @Assigned = (
        SELECT CAST(ISNULL(SUM(tc.Monto), 0) AS decimal(19, 4))
        FROM dbo.Transaccion_Comprobante AS tc
        WHERE tc.Comprobante_ID = @Comprobante_Id
    );
    SET @Pending = ISNULL(@TargetTotal, 0) - ISNULL(@Assigned, 0);
    SET @CandidateObjective = COALESCE(@Monto, NULLIF(CASE WHEN @Pending > 0 THEN @Pending ELSE @TargetTotal END, 0), @TargetTotal, 0);

    ;WITH LinkedBase AS
    (
        SELECT
            tc.Transaccion_ID AS TransaccionId,
            t.Fecha,
            t.Concepto,
            CAST(t.Monto AS decimal(19, 4)) AS TransaccionMonto,
            CAST(tc.Monto AS decimal(19, 4)) AS MontoAsignado,
            t.Tipo_Poliza AS TipoPoliza,
            t.Forma_Pago AS FormaPago,
            CAST(CASE WHEN @TargetTotal <> 0 THEN @TargetIva * (CAST(tc.Monto AS decimal(19, 4)) / @TargetTotal) ELSE 0 END AS decimal(19, 4)) AS IvaEsperado,
            CAST(
                CASE
                    WHEN @TargetDirection = 'Emitido' AND @TargetTipo = 'E'
                        THEN ISNULL(iva208.Debe, 0) - ISNULL(iva208.Haber, 0)
                    WHEN @TargetDirection = 'Emitido'
                        THEN ISNULL(iva208.Haber, 0) - ISNULL(iva208.Debe, 0)
                    WHEN @TargetDirection = 'Recibido' AND @TargetTipo = 'E'
                        THEN ISNULL(iva118.Haber, 0) - ISNULL(iva118.Debe, 0)
                    WHEN @TargetDirection = 'Recibido'
                        THEN ISNULL(iva118.Debe, 0) - ISNULL(iva118.Haber, 0)
                    ELSE 0
                END AS decimal(19, 4)
            ) AS IvaContableTransaccion,
            CAST(ISNULL(txAssigned.AsignadoRegular, 0) AS decimal(19, 4)) AS TransaccionAsignadoRegular
        FROM dbo.Transaccion_Comprobante AS tc
        JOIN dbo.Transacciones AS t
            ON t.ID = tc.Transaccion_ID
        OUTER APPLY
        (
            SELECT SUM(CAST(rc.Debe AS decimal(19, 4))) AS Debe, SUM(CAST(rc.Haber AS decimal(19, 4))) AS Haber
            FROM dbo.Registro_Contable AS rc
            WHERE rc.TransaccionID = tc.Transaccion_ID
              AND rc.Nivel1 = '208'
        ) AS iva208
        OUTER APPLY
        (
            SELECT SUM(CAST(rc.Debe AS decimal(19, 4))) AS Debe, SUM(CAST(rc.Haber AS decimal(19, 4))) AS Haber
            FROM dbo.Registro_Contable AS rc
            WHERE rc.TransaccionID = tc.Transaccion_ID
              AND rc.Nivel1 = '118'
        ) AS iva118
        OUTER APPLY
        (
            SELECT SUM(CAST(tc2.Monto AS decimal(19, 4))) AS AsignadoRegular
            FROM dbo.Transaccion_Comprobante AS tc2
            JOIN cfdi.Comprobante AS c2
                ON c2.Comprobante_Id = tc2.Comprobante_ID
            WHERE tc2.Transaccion_ID = tc.Transaccion_ID
              AND c2.TipoDeComprobante IN ('I', 'N', 'E')
        ) AS txAssigned
        WHERE tc.Comprobante_ID = @Comprobante_Id
    )
    SELECT
        lb.TransaccionId,
        lb.Fecha,
        lb.Concepto,
        lb.TransaccionMonto,
        lb.MontoAsignado,
        lb.TipoPoliza,
        lb.FormaPago,
        lb.IvaEsperado,
        CAST(CASE WHEN lb.TransaccionAsignadoRegular <> 0 THEN lb.IvaContableTransaccion * (lb.MontoAsignado / lb.TransaccionAsignadoRegular) ELSE 0 END AS decimal(19, 4)) AS IvaContable,
        CAST(lb.IvaEsperado - CASE WHEN lb.TransaccionAsignadoRegular <> 0 THEN lb.IvaContableTransaccion * (lb.MontoAsignado / lb.TransaccionAsignadoRegular) ELSE 0 END AS decimal(19, 4)) AS IvaDiferencia,
        CASE WHEN @TargetDirection = 'Emitido' THEN '208' WHEN @TargetDirection = 'Recibido' THEN '118' ELSE NULL END AS IvaCuentaNivel1,
        CASE
            WHEN @TargetDirection = 'Otro' OR @TargetTotal = 0 THEN 'NA'
            WHEN ABS(lb.IvaEsperado - CASE WHEN lb.TransaccionAsignadoRegular <> 0 THEN lb.IvaContableTransaccion * (lb.MontoAsignado / lb.TransaccionAsignadoRegular) ELSE 0 END) <= @Tolerancia THEN 'OK'
            ELSE 'DIFERENCIA'
        END AS IvaStatus
    INTO #LinkedPolizas
    FROM LinkedBase AS lb;

    SELECT TOP (1)
        cd.Comprobante_Id AS ComprobanteId,
        cd.Fecha,
        cd.TipoDeComprobante AS Tipo,
        cd.Serie,
        cd.Folio,
        cd.RFC_EMISOR AS EmisorRfc,
        cd.RFC_RECEPTOR AS ReceptorRfc,
        cd.EMISOR AS Emisor,
        cd.RECEPTOR AS Receptor,
        @TargetDirection AS Direccion,
        cd.FOLIO_FISCAL AS Uuid,
        cd.FormaPago,
        cd.MetodoPago,
        cd.UsoCFDI AS UsoCfdi,
        CAST(ISNULL(cd.SubTotal, 0) AS decimal(19, 4)) AS SubTotal,
        CAST(ISNULL(cd.Total, 0) AS decimal(19, 4)) AS Total,
        CAST(ISNULL(cd.IVA, 0) AS decimal(19, 4)) AS Iva,
        CAST(ISNULL(cd.IVA_RETENIDO, 0) AS decimal(19, 4)) AS IvaRetenido,
        CAST(ISNULL(cd.ISR_RETENIDO, 0) AS decimal(19, 4)) AS IsrRetenido,
        ISNULL(@Assigned, 0) AS AsignadoCfdi,
        ISNULL(@Pending, 0) AS Pendiente,
        ISNULL(SUM(lp.IvaEsperado), 0) AS IvaEsperado,
        ISNULL(SUM(lp.IvaContable), 0) AS IvaContable,
        CAST(ISNULL(SUM(lp.IvaEsperado), 0) - ISNULL(SUM(lp.IvaContable), 0) AS decimal(19, 4)) AS IvaDiferencia,
        CASE WHEN ABS(CAST(ISNULL(cd.Total, 0) AS decimal(19, 4)) - ISNULL(@Assigned, 0)) <= @Tolerancia THEN 'OK' ELSE 'DIFERENCIA' END AS TotalCfdiStatus,
        CASE
            WHEN @TargetDirection = 'Otro' OR ISNULL(@TargetTotal, 0) = 0 THEN 'NA'
            WHEN ABS(ISNULL(SUM(lp.IvaEsperado), 0) - ISNULL(SUM(lp.IvaContable), 0)) <= @Tolerancia THEN 'OK'
            ELSE 'DIFERENCIA'
        END AS IvaStatus,
        COUNT(DISTINCT lp.TransaccionId) AS PolizasCount,
        cd.XML_Attachment_ID AS XmlAttachmentId
    FROM cfdi.Comprobante_Detalle AS cd
    LEFT JOIN #LinkedPolizas AS lp
        ON 1 = 1
    WHERE cd.Comprobante_Id = @Comprobante_Id
      AND cd.TipoDeComprobante IN ('I', 'N', 'E')
    GROUP BY
        cd.Comprobante_Id,
        cd.Fecha,
        cd.TipoDeComprobante,
        cd.Serie,
        cd.Folio,
        cd.RFC_EMISOR,
        cd.RFC_RECEPTOR,
        cd.EMISOR,
        cd.RECEPTOR,
        cd.FOLIO_FISCAL,
        cd.FormaPago,
        cd.MetodoPago,
        cd.UsoCFDI,
        cd.SubTotal,
        cd.Total,
        cd.IVA,
        cd.IVA_RETENIDO,
        cd.ISR_RETENIDO,
        cd.XML_Attachment_ID;

    SELECT
        TransaccionId,
        Fecha,
        Concepto,
        TransaccionMonto,
        MontoAsignado,
        TipoPoliza,
        FormaPago,
        IvaEsperado,
        IvaContable,
        IvaDiferencia,
        IvaCuentaNivel1,
        IvaStatus
    FROM #LinkedPolizas
    ORDER BY Fecha, TransaccionId;

    ;WITH CandidateBase AS
    (
        SELECT TOP (@Top)
            t.ID AS Id,
            t.Fecha,
            t.Concepto,
            CAST(t.Monto AS decimal(19, 4)) AS Monto,
            t.Tipo_Poliza AS TipoPoliza,
            t.Forma_Pago AS FormaPago,
            CAST(ISNULL(txAssigned.AsignadoRegular, 0) AS decimal(19, 4)) AS MontoAsignado,
            CAST(CASE
                WHEN ABS(CAST(t.Monto AS decimal(19, 4))) - ISNULL(txAssigned.AsignadoRegular, 0) > 0
                    THEN ABS(CAST(t.Monto AS decimal(19, 4))) - ISNULL(txAssigned.AsignadoRegular, 0)
                ELSE 0
            END AS decimal(19, 4)) AS Disponible,
            CAST(
                CASE
                    WHEN @TargetDirection = 'Emitido' AND @TargetTipo = 'E'
                        THEN ISNULL(iva208.Debe, 0) - ISNULL(iva208.Haber, 0)
                    WHEN @TargetDirection = 'Emitido'
                        THEN ISNULL(iva208.Haber, 0) - ISNULL(iva208.Debe, 0)
                    WHEN @TargetDirection = 'Recibido' AND @TargetTipo = 'E'
                        THEN ISNULL(iva118.Haber, 0) - ISNULL(iva118.Debe, 0)
                    WHEN @TargetDirection = 'Recibido'
                        THEN ISNULL(iva118.Debe, 0) - ISNULL(iva118.Haber, 0)
                    ELSE 0
                END AS decimal(19, 4)
            ) AS IvaContableTransaccion,
            ABS(DATEDIFF(day, @TargetFecha, t.Fecha)) AS DateDistance,
            ABS(ABS(CAST(t.Monto AS decimal(19, 4))) - @CandidateObjective) AS AmountDistance
        FROM dbo.Transacciones AS t
        OUTER APPLY
        (
            SELECT SUM(CAST(tc.Monto AS decimal(19, 4))) AS AsignadoRegular
            FROM dbo.Transaccion_Comprobante AS tc
            JOIN cfdi.Comprobante AS c
                ON c.Comprobante_Id = tc.Comprobante_ID
            WHERE tc.Transaccion_ID = t.ID
              AND c.TipoDeComprobante IN ('I', 'N', 'E')
        ) AS txAssigned
        OUTER APPLY
        (
            SELECT SUM(CAST(rc.Debe AS decimal(19, 4))) AS Debe, SUM(CAST(rc.Haber AS decimal(19, 4))) AS Haber
            FROM dbo.Registro_Contable AS rc
            WHERE rc.TransaccionID = t.ID
              AND rc.Nivel1 = '208'
        ) AS iva208
        OUTER APPLY
        (
            SELECT SUM(CAST(rc.Debe AS decimal(19, 4))) AS Debe, SUM(CAST(rc.Haber AS decimal(19, 4))) AS Haber
            FROM dbo.Registro_Contable AS rc
            WHERE rc.TransaccionID = t.ID
              AND rc.Nivel1 = '118'
        ) AS iva118
        WHERE (@ContextRfc IS NULL OR t.RFC = @ContextRfc)
          AND (@TransaccionId IS NULL OR t.ID = @TransaccionId)
          AND (@Year IS NULL OR YEAR(t.Fecha) = @Year)
          AND (@Month IS NULL OR MONTH(t.Fecha) = @Month)
          AND (@Concepto IS NULL OR t.Concepto LIKE '%' + @Concepto + '%')
          AND (@TipoPoliza IS NULL OR t.Tipo_Poliza LIKE '%' + @TipoPoliza + '%')
          AND (@FormaPago IS NULL OR t.Forma_Pago LIKE '%' + @FormaPago + '%')
        ORDER BY
            CASE WHEN ABS(ABS(CAST(t.Monto AS decimal(19, 4))) - @CandidateObjective) <= @Tolerancia THEN 0 ELSE 1 END,
            ABS(DATEDIFF(day, @TargetFecha, t.Fecha)),
            ABS(ABS(CAST(t.Monto AS decimal(19, 4))) - @CandidateObjective),
            t.Fecha DESC,
            t.ID DESC
    ),
    CandidateScored AS
    (
        SELECT
            cb.*,
            CAST(CASE
                WHEN cb.Disponible <= 0 THEN 0
                WHEN @Pending > 0 THEN IIF(cb.Disponible < @Pending, cb.Disponible, @Pending)
                ELSE IIF(cb.Disponible < @TargetTotal, cb.Disponible, @TargetTotal)
            END AS decimal(19, 4)) AS MontoSugerido,
            CAST(CASE
                WHEN cb.Disponible <= 0 THEN @CandidateObjective
                WHEN @Pending > 0 THEN ABS(IIF(cb.Disponible < @Pending, cb.Disponible, @Pending) - @CandidateObjective)
                ELSE ABS(IIF(cb.Disponible < @TargetTotal, cb.Disponible, @TargetTotal) - @CandidateObjective)
            END AS decimal(19, 4)) AS DiferenciaObjetivo,
            (CASE WHEN cb.AmountDistance <= @Tolerancia THEN 45 ELSE 0 END)
            + (CASE WHEN cb.DateDistance <= 7 THEN 25 WHEN cb.DateDistance <= 31 THEN 15 ELSE 0 END)
            + (CASE WHEN cb.Disponible > 0 THEN 20 ELSE 0 END)
            + (CASE WHEN cb.MontoAsignado = 0 THEN 10 ELSE 0 END) AS MatchScore
        FROM CandidateBase AS cb
    )
    SELECT
        cs.Id,
        cs.Fecha,
        cs.Concepto,
        cs.Monto,
        cs.MontoAsignado,
        cs.Disponible,
        cs.MontoSugerido,
        cs.DiferenciaObjetivo,
        cs.TipoPoliza,
        cs.FormaPago,
        cs.MatchScore,
        CASE
            WHEN cs.Disponible <= 0 THEN 'SIN_DISPONIBLE'
            WHEN cs.AmountDistance <= @Tolerancia AND cs.DateDistance <= 31 THEN 'FUERTE'
            WHEN cs.AmountDistance <= @Tolerancia OR cs.DateDistance <= 31 THEN 'POSIBLE'
            ELSE 'AMPLIA'
        END AS MatchStatus,
        cs.IvaContableTransaccion AS IvaContable,
        CAST(CASE WHEN @TargetTotal <> 0 THEN (@TargetIva * (cs.MontoSugerido / @TargetTotal)) - cs.IvaContableTransaccion ELSE 0 END AS decimal(19, 4)) AS IvaDiferencia,
        CASE
            WHEN @TargetDirection = 'Otro' OR @TargetTotal = 0 THEN 'NA'
            WHEN ABS(CASE WHEN @TargetTotal <> 0 THEN (@TargetIva * (cs.MontoSugerido / @TargetTotal)) - cs.IvaContableTransaccion ELSE 0 END) <= @Tolerancia THEN 'OK'
            ELSE 'DIFERENCIA'
        END AS IvaStatus
    FROM CandidateScored AS cs
    ORDER BY cs.MatchScore DESC, cs.DiferenciaObjetivo, cs.DateDistance, cs.Fecha DESC, cs.Id DESC;
END;
GO

CREATE OR ALTER PROCEDURE [cfdi].[Pago20_Poliza_Linking_Workspace]
    @DoctoRelacionado_Id int,
    @RFC varchar(50) = NULL,
    @Year int = NULL,
    @Month int = NULL,
    @TransaccionId int = NULL,
    @Concepto nvarchar(255) = NULL,
    @Monto decimal(19, 4) = NULL,
    @TipoPoliza varchar(50) = NULL,
    @FormaPago varchar(50) = NULL,
    @Top int = 200,
    @Tolerancia decimal(19, 4) = 1.00
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE
        @ContextRfc varchar(50) = NULLIF(LTRIM(RTRIM(@RFC)), ''),
        @ComprobanteId int,
        @TargetImpPagado decimal(19, 4),
        @TargetIva decimal(19, 4),
        @TargetFecha date,
        @TargetDirection varchar(20),
        @Assigned decimal(19, 4),
        @Pending decimal(19, 4),
        @CandidateObjective decimal(19, 4);

    SELECT TOP (1)
        @ComprobanteId = v.Comprobante_Id,
        @TargetImpPagado = CAST(ISNULL(v.ImpPagado, 0) AS decimal(19, 4)),
        @TargetIva = CAST(ISNULL(v.Comp_IVA, 0) AS decimal(19, 4)),
        @TargetFecha = CAST(v.FechaPago AS date),
        @TargetDirection = CASE
            WHEN v.EmisorRfc = @ContextRfc THEN 'Emitido'
            WHEN v.ReceptorRfc = @ContextRfc THEN 'Recibido'
            ELSE 'Otro'
        END
    FROM cfdi.vw_Pagos20_Resumen AS v
    WHERE v.DoctoRelacionado_Id = @DoctoRelacionado_Id;

    SET @Assigned = (
        SELECT CAST(ISNULL(SUM(td.Monto), 0) AS decimal(19, 4))
        FROM dbo.Transaccion_DoctoRelacionado AS td
        WHERE td.DoctoRelacionado_Id = @DoctoRelacionado_Id
    );
    SET @Pending = ISNULL(@TargetImpPagado, 0) - ISNULL(@Assigned, 0);
    SET @CandidateObjective = COALESCE(@Monto, NULLIF(CASE WHEN @Pending > 0 THEN @Pending ELSE @TargetImpPagado END, 0), @TargetImpPagado, 0);

    ;WITH LinkedBase AS
    (
        SELECT
            td.Transaccion_ID AS TransaccionId,
            t.Fecha,
            t.Concepto,
            CAST(t.Monto AS decimal(19, 4)) AS TransaccionMonto,
            CAST(td.Monto AS decimal(19, 4)) AS MontoAsignado,
            t.Tipo_Poliza AS TipoPoliza,
            t.Forma_Pago AS FormaPago,
            CAST(CASE WHEN @TargetImpPagado <> 0 THEN @TargetIva * (CAST(td.Monto AS decimal(19, 4)) / @TargetImpPagado) ELSE 0 END AS decimal(19, 4)) AS IvaEsperado,
            CAST(
                CASE
                    WHEN @TargetDirection = 'Emitido' THEN ISNULL(iva208.Haber, 0) - ISNULL(iva208.Debe, 0)
                    WHEN @TargetDirection = 'Recibido' THEN ISNULL(iva118.Debe, 0) - ISNULL(iva118.Haber, 0)
                    ELSE 0
                END AS decimal(19, 4)
            ) AS IvaContableTransaccion,
            CAST(ISNULL(txAssigned.AsignadoPago20, 0) AS decimal(19, 4)) AS TransaccionAsignadoPago20
        FROM dbo.Transaccion_DoctoRelacionado AS td
        JOIN dbo.Transacciones AS t
            ON t.ID = td.Transaccion_ID
        OUTER APPLY
        (
            SELECT SUM(CAST(rc.Debe AS decimal(19, 4))) AS Debe, SUM(CAST(rc.Haber AS decimal(19, 4))) AS Haber
            FROM dbo.Registro_Contable AS rc
            WHERE rc.TransaccionID = td.Transaccion_ID
              AND rc.Nivel1 = '208'
        ) AS iva208
        OUTER APPLY
        (
            SELECT SUM(CAST(rc.Debe AS decimal(19, 4))) AS Debe, SUM(CAST(rc.Haber AS decimal(19, 4))) AS Haber
            FROM dbo.Registro_Contable AS rc
            WHERE rc.TransaccionID = td.Transaccion_ID
              AND rc.Nivel1 = '118'
        ) AS iva118
        OUTER APPLY
        (
            SELECT SUM(CAST(td2.Monto AS decimal(19, 4))) AS AsignadoPago20
            FROM dbo.Transaccion_DoctoRelacionado AS td2
            WHERE td2.Transaccion_ID = td.Transaccion_ID
        ) AS txAssigned
        WHERE td.DoctoRelacionado_Id = @DoctoRelacionado_Id
    )
    SELECT
        lb.TransaccionId,
        lb.Fecha,
        lb.Concepto,
        lb.TransaccionMonto,
        lb.MontoAsignado,
        lb.TipoPoliza,
        lb.FormaPago,
        lb.IvaEsperado,
        CAST(CASE WHEN lb.TransaccionAsignadoPago20 <> 0 THEN lb.IvaContableTransaccion * (lb.MontoAsignado / lb.TransaccionAsignadoPago20) ELSE 0 END AS decimal(19, 4)) AS IvaContable,
        CAST(lb.IvaEsperado - CASE WHEN lb.TransaccionAsignadoPago20 <> 0 THEN lb.IvaContableTransaccion * (lb.MontoAsignado / lb.TransaccionAsignadoPago20) ELSE 0 END AS decimal(19, 4)) AS IvaDiferencia,
        CASE WHEN @TargetDirection = 'Emitido' THEN '208' WHEN @TargetDirection = 'Recibido' THEN '118' ELSE NULL END AS IvaCuentaNivel1,
        CASE
            WHEN @TargetDirection = 'Otro' OR @TargetImpPagado = 0 THEN 'NA'
            WHEN ABS(lb.IvaEsperado - CASE WHEN lb.TransaccionAsignadoPago20 <> 0 THEN lb.IvaContableTransaccion * (lb.MontoAsignado / lb.TransaccionAsignadoPago20) ELSE 0 END) <= @Tolerancia THEN 'OK'
            ELSE 'DIFERENCIA'
        END AS IvaStatus
    INTO #LinkedPago20Polizas
    FROM LinkedBase AS lb;

    SELECT TOP (1)
        v.DoctoRelacionado_Id AS DoctoRelacionadoId,
        v.Comprobante_Id AS ComprobanteId,
        v.ComprobanteUUID AS ComprobanteUuid,
        v.EmisorRfc,
        v.ReceptorRfc,
        @TargetDirection AS Direccion,
        v.FechaPago,
        v.FormaDePagoP,
        v.MonedaP,
        CAST(ISNULL(v.MontoPago, 0) AS decimal(19, 4)) AS MontoPago,
        v.UUID_DoctoRelacionado AS UuidDoctoRelacionado,
        v.Folio,
        v.NumParcialidad,
        v.MonedaDR AS MonedaDr,
        CAST(ISNULL(v.ImpPagado, 0) AS decimal(19, 4)) AS ImpPagado,
        CAST(ISNULL(v.Comp_IVA, 0) AS decimal(19, 4)) AS CompIva,
        ISNULL(@Assigned, 0) AS AsignadoComplemento,
        ISNULL(@Pending, 0) AS Pendiente,
        ISNULL(SUM(lp.IvaContable), 0) AS IvaContable,
        CAST(ISNULL(v.Comp_IVA, 0) - ISNULL(SUM(lp.IvaContable), 0) AS decimal(19, 4)) AS IvaDiferencia,
        CASE WHEN ABS(CAST(ISNULL(v.ImpPagado, 0) AS decimal(19, 4)) - ISNULL(@Assigned, 0)) <= @Tolerancia THEN 'OK' ELSE 'DIFERENCIA' END AS TotalComplementoStatus,
        CASE
            WHEN @TargetDirection = 'Otro' OR ISNULL(@TargetImpPagado, 0) = 0 THEN 'NA'
            WHEN ABS(CAST(ISNULL(v.Comp_IVA, 0) AS decimal(19, 4)) - ISNULL(SUM(lp.IvaContable), 0)) <= @Tolerancia THEN 'OK'
            ELSE 'DIFERENCIA'
        END AS IvaStatus,
        COUNT(DISTINCT lp.TransaccionId) AS PolizasCount,
        related.RelatedDocumentsCount,
        v.XML_Attachment_ID AS XmlAttachmentId
    FROM cfdi.vw_Pagos20_Resumen AS v
    OUTER APPLY
    (
        SELECT COUNT(*) AS RelatedDocumentsCount
        FROM cfdi.vw_Pagos20_Resumen AS vr
        WHERE vr.Comprobante_Id = v.Comprobante_Id
    ) AS related
    LEFT JOIN #LinkedPago20Polizas AS lp
        ON 1 = 1
    WHERE v.DoctoRelacionado_Id = @DoctoRelacionado_Id
    GROUP BY
        v.DoctoRelacionado_Id,
        v.Comprobante_Id,
        v.ComprobanteUUID,
        v.EmisorRfc,
        v.ReceptorRfc,
        v.FechaPago,
        v.FormaDePagoP,
        v.MonedaP,
        v.MontoPago,
        v.UUID_DoctoRelacionado,
        v.Folio,
        v.NumParcialidad,
        v.MonedaDR,
        v.ImpPagado,
        v.Comp_IVA,
        related.RelatedDocumentsCount,
        v.XML_Attachment_ID;

    SELECT
        DoctoRelacionado_Id AS DoctoRelacionadoId,
        UUID_DoctoRelacionado AS UuidDoctoRelacionado,
        Folio,
        NumParcialidad,
        MonedaDR AS MonedaDr,
        CAST(ISNULL(ImpSaldoAnt, 0) AS decimal(19, 4)) AS ImpSaldoAnt,
        CAST(ISNULL(ImpPagado, 0) AS decimal(19, 4)) AS ImpPagado,
        CAST(ISNULL(ImpSaldoInsoluto, 0) AS decimal(19, 4)) AS ImpSaldoInsoluto,
        CAST(ISNULL(Comp_IVA, 0) AS decimal(19, 4)) AS CompIva
    FROM cfdi.vw_Pagos20_Resumen
    WHERE Comprobante_Id = @ComprobanteId
    ORDER BY FechaPago, DoctoRelacionado_Id;

    SELECT
        TransaccionId,
        Fecha,
        Concepto,
        TransaccionMonto,
        MontoAsignado,
        TipoPoliza,
        FormaPago,
        IvaEsperado,
        IvaContable,
        IvaDiferencia,
        IvaCuentaNivel1,
        IvaStatus
    FROM #LinkedPago20Polizas
    ORDER BY Fecha, TransaccionId;

    ;WITH CandidateBase AS
    (
        SELECT TOP (@Top)
            t.ID AS Id,
            t.Fecha,
            t.Concepto,
            CAST(t.Monto AS decimal(19, 4)) AS Monto,
            t.Tipo_Poliza AS TipoPoliza,
            t.Forma_Pago AS FormaPago,
            CAST(ISNULL(txAssigned.AsignadoPago20, 0) AS decimal(19, 4)) AS MontoAsignado,
            CAST(CASE
                WHEN ABS(CAST(t.Monto AS decimal(19, 4))) - ISNULL(txAssigned.AsignadoPago20, 0) > 0
                    THEN ABS(CAST(t.Monto AS decimal(19, 4))) - ISNULL(txAssigned.AsignadoPago20, 0)
                ELSE 0
            END AS decimal(19, 4)) AS Disponible,
            CAST(
                CASE
                    WHEN @TargetDirection = 'Emitido' THEN ISNULL(iva208.Haber, 0) - ISNULL(iva208.Debe, 0)
                    WHEN @TargetDirection = 'Recibido' THEN ISNULL(iva118.Debe, 0) - ISNULL(iva118.Haber, 0)
                    ELSE 0
                END AS decimal(19, 4)
            ) AS IvaContableTransaccion,
            ABS(DATEDIFF(day, @TargetFecha, t.Fecha)) AS DateDistance,
            ABS(ABS(CAST(t.Monto AS decimal(19, 4))) - @CandidateObjective) AS AmountDistance
        FROM dbo.Transacciones AS t
        OUTER APPLY
        (
            SELECT SUM(CAST(td.Monto AS decimal(19, 4))) AS AsignadoPago20
            FROM dbo.Transaccion_DoctoRelacionado AS td
            WHERE td.Transaccion_ID = t.ID
        ) AS txAssigned
        OUTER APPLY
        (
            SELECT SUM(CAST(rc.Debe AS decimal(19, 4))) AS Debe, SUM(CAST(rc.Haber AS decimal(19, 4))) AS Haber
            FROM dbo.Registro_Contable AS rc
            WHERE rc.TransaccionID = t.ID
              AND rc.Nivel1 = '208'
        ) AS iva208
        OUTER APPLY
        (
            SELECT SUM(CAST(rc.Debe AS decimal(19, 4))) AS Debe, SUM(CAST(rc.Haber AS decimal(19, 4))) AS Haber
            FROM dbo.Registro_Contable AS rc
            WHERE rc.TransaccionID = t.ID
              AND rc.Nivel1 = '118'
        ) AS iva118
        WHERE (@ContextRfc IS NULL OR t.RFC = @ContextRfc)
          AND (@TransaccionId IS NULL OR t.ID = @TransaccionId)
          AND (@Year IS NULL OR YEAR(t.Fecha) = @Year)
          AND (@Month IS NULL OR MONTH(t.Fecha) = @Month)
          AND (@Concepto IS NULL OR t.Concepto LIKE '%' + @Concepto + '%')
          AND (@TipoPoliza IS NULL OR t.Tipo_Poliza LIKE '%' + @TipoPoliza + '%')
          AND (@FormaPago IS NULL OR t.Forma_Pago LIKE '%' + @FormaPago + '%')
        ORDER BY
            CASE WHEN ABS(ABS(CAST(t.Monto AS decimal(19, 4))) - @CandidateObjective) <= @Tolerancia THEN 0 ELSE 1 END,
            ABS(DATEDIFF(day, @TargetFecha, t.Fecha)),
            ABS(ABS(CAST(t.Monto AS decimal(19, 4))) - @CandidateObjective),
            t.Fecha DESC,
            t.ID DESC
    ),
    CandidateScored AS
    (
        SELECT
            cb.*,
            CAST(CASE
                WHEN cb.Disponible <= 0 THEN 0
                WHEN @Pending > 0 THEN IIF(cb.Disponible < @Pending, cb.Disponible, @Pending)
                ELSE IIF(cb.Disponible < @TargetImpPagado, cb.Disponible, @TargetImpPagado)
            END AS decimal(19, 4)) AS MontoSugerido,
            CAST(CASE
                WHEN cb.Disponible <= 0 THEN @CandidateObjective
                WHEN @Pending > 0 THEN ABS(IIF(cb.Disponible < @Pending, cb.Disponible, @Pending) - @CandidateObjective)
                ELSE ABS(IIF(cb.Disponible < @TargetImpPagado, cb.Disponible, @TargetImpPagado) - @CandidateObjective)
            END AS decimal(19, 4)) AS DiferenciaObjetivo,
            (CASE WHEN cb.AmountDistance <= @Tolerancia THEN 45 ELSE 0 END)
            + (CASE WHEN cb.DateDistance <= 7 THEN 25 WHEN cb.DateDistance <= 31 THEN 15 ELSE 0 END)
            + (CASE WHEN cb.Disponible > 0 THEN 20 ELSE 0 END)
            + (CASE WHEN cb.MontoAsignado = 0 THEN 10 ELSE 0 END) AS MatchScore
        FROM CandidateBase AS cb
    )
    SELECT
        cs.Id,
        cs.Fecha,
        cs.Concepto,
        cs.Monto,
        cs.MontoAsignado,
        cs.Disponible,
        cs.MontoSugerido,
        cs.DiferenciaObjetivo,
        cs.TipoPoliza,
        cs.FormaPago,
        cs.MatchScore,
        CASE
            WHEN cs.Disponible <= 0 THEN 'SIN_DISPONIBLE'
            WHEN cs.AmountDistance <= @Tolerancia AND cs.DateDistance <= 31 THEN 'FUERTE'
            WHEN cs.AmountDistance <= @Tolerancia OR cs.DateDistance <= 31 THEN 'POSIBLE'
            ELSE 'AMPLIA'
        END AS MatchStatus,
        cs.IvaContableTransaccion AS IvaContable,
        CAST(CASE WHEN @TargetImpPagado <> 0 THEN (@TargetIva * (cs.MontoSugerido / @TargetImpPagado)) - cs.IvaContableTransaccion ELSE 0 END AS decimal(19, 4)) AS IvaDiferencia,
        CASE
            WHEN @TargetDirection = 'Otro' OR @TargetImpPagado = 0 THEN 'NA'
            WHEN ABS(CASE WHEN @TargetImpPagado <> 0 THEN (@TargetIva * (cs.MontoSugerido / @TargetImpPagado)) - cs.IvaContableTransaccion ELSE 0 END) <= @Tolerancia THEN 'OK'
            ELSE 'DIFERENCIA'
        END AS IvaStatus
    FROM CandidateScored AS cs
    ORDER BY cs.MatchScore DESC, cs.DiferenciaObjetivo, cs.DateDistance, cs.Fecha DESC, cs.Id DESC;
END;
GO
