SET ANSI_NULLS ON;
GO
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER VIEW [cfdi].[Transaccion_CFDI_Vinculado_Detalle]
AS
SELECT
    tc.ID AS VinculoId,
    tc.Transaccion_ID AS TransaccionId,
    tc.Comprobante_ID AS ComprobanteId,
    CAST(tc.Monto AS decimal(19, 4)) AS MontoAsignado,
    c.TipoDeComprobante,
    c.XML_Attachment_ID AS XmlAttachmentId
FROM dbo.Transaccion_Comprobante AS tc
JOIN cfdi.Comprobante AS c
    ON c.Comprobante_Id = tc.Comprobante_ID;
GO

CREATE OR ALTER PROCEDURE [cfdi].[Transaccion_CFDI_Vinculados_Resumen]
    @Transaccion_ID int,
    @Tolerancia decimal(19, 4) = 1.00
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE
        @ContextRfc varchar(50),
        @TransaccionMonto decimal(19, 4);

    SELECT
        @ContextRfc = NULLIF(LTRIM(RTRIM(t.RFC)), ''),
        @TransaccionMonto = CAST(t.Monto AS decimal(19, 4))
    FROM dbo.Transacciones AS t
    WHERE t.ID = @Transaccion_ID;

    ;WITH RegularIds AS
    (
        SELECT DISTINCT v.ComprobanteId
        FROM cfdi.Transaccion_CFDI_Vinculado_Detalle AS v
        WHERE v.TransaccionId = @Transaccion_ID
          AND v.TipoDeComprobante IN ('I', 'N', 'E')
    ),
    RegularLinks AS
    (
        SELECT
            cd.Comprobante_Id AS ComprobanteId,
            tc.Transaccion_ID AS TransaccionId,
            t.Fecha,
            t.Concepto,
            CAST(t.Monto AS decimal(19, 4)) AS TransaccionMonto,
            CAST(tc.Monto AS decimal(19, 4)) AS MontoAsignado,
            t.Tipo_Poliza AS TipoPoliza,
            t.Forma_Pago AS FormaPago,
            cd.TipoDeComprobante AS Tipo,
            cd.Serie,
            cd.Folio,
            cd.RFC_EMISOR AS EmisorRfc,
            cd.RFC_RECEPTOR AS ReceptorRfc,
            CASE
                WHEN cd.RFC_EMISOR = @ContextRfc THEN 'Emitido'
                WHEN cd.RFC_RECEPTOR = @ContextRfc THEN 'Recibido'
                ELSE 'Otro'
            END AS Direccion,
            cd.FOLIO_FISCAL AS Uuid,
            cd.FormaPago AS CfdiFormaPago,
            cd.MetodoPago,
            cd.UsoCFDI AS UsoCfdi,
            CAST(cd.SubTotal AS decimal(19, 4)) AS SubTotal,
            CAST(cd.Total AS decimal(19, 4)) AS Total,
            CAST(cd.IVA AS decimal(19, 4)) AS Iva,
            CAST(cd.IVA_RETENIDO AS decimal(19, 4)) AS IvaRetenido,
            CAST(cd.ISR_RETENIDO AS decimal(19, 4)) AS IsrRetenido,
            cd.XML_Attachment_ID AS XmlAttachmentId,
            CAST(ISNULL(txAssigned.AsignadoRegular, 0) AS decimal(19, 4)) AS TransaccionAsignadoRegular,
            CAST(
                CASE
                    WHEN cd.RFC_EMISOR = @ContextRfc AND cd.TipoDeComprobante = 'E'
                        THEN ISNULL(iva208.Debe, 0) - ISNULL(iva208.Haber, 0)
                    WHEN cd.RFC_EMISOR = @ContextRfc
                        THEN ISNULL(iva208.Haber, 0) - ISNULL(iva208.Debe, 0)
                    WHEN cd.RFC_RECEPTOR = @ContextRfc AND cd.TipoDeComprobante = 'E'
                        THEN ISNULL(iva118.Haber, 0) - ISNULL(iva118.Debe, 0)
                    WHEN cd.RFC_RECEPTOR = @ContextRfc
                        THEN ISNULL(iva118.Debe, 0) - ISNULL(iva118.Haber, 0)
                    ELSE 0
                END AS decimal(19, 4)
            ) AS IvaContableTransaccion
        FROM RegularIds AS ids
        JOIN dbo.Transaccion_Comprobante AS tc
            ON tc.Comprobante_ID = ids.ComprobanteId
        JOIN dbo.Transacciones AS t
            ON t.ID = tc.Transaccion_ID
        JOIN cfdi.Comprobante_Detalle AS cd
            ON cd.Comprobante_Id = tc.Comprobante_ID
        OUTER APPLY
        (
            SELECT SUM(CAST(tc2.Monto AS decimal(19, 4))) AS AsignadoRegular
            FROM dbo.Transaccion_Comprobante AS tc2
            JOIN cfdi.Comprobante AS c2
                ON c2.Comprobante_Id = tc2.Comprobante_ID
            WHERE tc2.Transaccion_ID = tc.Transaccion_ID
              AND c2.TipoDeComprobante IN ('I', 'N', 'E')
        ) AS txAssigned
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
    ),
    RegularPolizaRows AS
    (
        SELECT
            rl.*,
            CAST(CASE WHEN rl.Total <> 0 THEN rl.MontoAsignado / rl.Total ELSE 0 END AS decimal(19, 8)) AS ProporcionCfdi,
            CAST(CASE WHEN rl.Total <> 0 THEN rl.Iva * (rl.MontoAsignado / rl.Total) ELSE 0 END AS decimal(19, 4)) AS IvaEsperado,
            CAST(CASE WHEN rl.TransaccionAsignadoRegular <> 0 THEN rl.IvaContableTransaccion * (rl.MontoAsignado / rl.TransaccionAsignadoRegular) ELSE 0 END AS decimal(19, 4)) AS IvaContable,
            CASE WHEN rl.Direccion = 'Emitido' THEN '208'
                 WHEN rl.Direccion = 'Recibido' THEN '118'
                 ELSE NULL END AS IvaCuentaNivel1
        FROM RegularLinks AS rl
    )
    SELECT
        r.*,
        CAST(r.IvaEsperado - r.IvaContable AS decimal(19, 4)) AS IvaDiferencia,
        CASE
            WHEN r.Direccion = 'Otro' OR r.Total = 0 THEN 'NA'
            WHEN ABS(r.IvaEsperado - r.IvaContable) <= @Tolerancia THEN 'OK'
            ELSE 'DIFERENCIA'
        END AS IvaStatus
    INTO #RegularPolizaStatus
    FROM RegularPolizaRows AS r;

    ;WITH ConceptosAgg AS
    (
        SELECT
            cs.Comprobante_Id AS ComprobanteId,
            STRING_AGG(CONVERT(varchar(max), cp.Descripcion), ', ') AS Conceptos
        FROM cfdi.Conceptos AS cs
        JOIN cfdi.Concepto AS cp
            ON cp.Conceptos_Id = cs.Conceptos_Id
        WHERE EXISTS (SELECT 1 FROM #RegularPolizaStatus AS r WHERE r.ComprobanteId = cs.Comprobante_Id)
        GROUP BY cs.Comprobante_Id
    ),
    CurrentTransaccionAssigned AS
    (
        SELECT CAST(ISNULL(SUM(tc.Monto), 0) AS decimal(19, 4)) AS TransaccionAsignado
        FROM dbo.Transaccion_Comprobante AS tc
        JOIN cfdi.Comprobante AS c
            ON c.Comprobante_Id = tc.Comprobante_ID
        WHERE tc.Transaccion_ID = @Transaccion_ID
          AND c.TipoDeComprobante IN ('I', 'N', 'E')
    )
    SELECT
        r.ComprobanteId,
        MAX(r.Fecha) AS Fecha,
        MAX(r.Tipo) AS Tipo,
        MAX(r.Serie) AS Serie,
        MAX(r.Folio) AS Folio,
        MAX(r.EmisorRfc) AS EmisorRfc,
        MAX(r.ReceptorRfc) AS ReceptorRfc,
        MAX(r.Direccion) AS Direccion,
        MAX(r.Uuid) AS Uuid,
        MAX(r.CfdiFormaPago) AS FormaPago,
        MAX(r.MetodoPago) AS MetodoPago,
        MAX(r.UsoCfdi) AS UsoCfdi,
        MAX(ca.Conceptos) AS Conceptos,
        MAX(r.SubTotal) AS SubTotal,
        MAX(r.Total) AS Total,
        MAX(r.Iva) AS Iva,
        MAX(r.IvaRetenido) AS IvaRetenido,
        MAX(r.IsrRetenido) AS IsrRetenido,
        SUM(r.MontoAsignado) AS AsignadoCfdi,
        ISNULL(@TransaccionMonto, 0) AS TransaccionMonto,
        MAX(cta.TransaccionAsignado) AS TransaccionAsignado,
        SUM(r.IvaEsperado) AS IvaEsperado,
        SUM(r.IvaContable) AS IvaContable,
        CAST(SUM(r.IvaEsperado) - SUM(r.IvaContable) AS decimal(19, 4)) AS IvaDiferencia,
        CASE WHEN ABS(MAX(r.Total) - SUM(r.MontoAsignado)) <= @Tolerancia THEN 'OK' ELSE 'DIFERENCIA' END AS TotalCfdiStatus,
        CASE WHEN ABS(ISNULL(@TransaccionMonto, 0) - MAX(cta.TransaccionAsignado)) <= @Tolerancia THEN 'OK' ELSE 'DIFERENCIA' END AS TransaccionAsignacionStatus,
        CASE
            WHEN MAX(r.Direccion) = 'Otro' OR MAX(r.Total) = 0 THEN 'NA'
            WHEN ABS(SUM(r.IvaEsperado) - SUM(r.IvaContable)) <= @Tolerancia THEN 'OK'
            ELSE 'DIFERENCIA'
        END AS IvaStatus,
        COUNT(DISTINCT r.TransaccionId) AS PolizasCount,
        MAX(r.XmlAttachmentId) AS XmlAttachmentId
    FROM #RegularPolizaStatus AS r
    CROSS JOIN CurrentTransaccionAssigned AS cta
    LEFT JOIN ConceptosAgg AS ca
        ON ca.ComprobanteId = r.ComprobanteId
    GROUP BY r.ComprobanteId
    ORDER BY MAX(r.Fecha) DESC, r.ComprobanteId DESC;

    SELECT
        r.ComprobanteId,
        r.TransaccionId,
        r.Fecha,
        r.Concepto,
        r.TransaccionMonto,
        r.MontoAsignado,
        r.TipoPoliza,
        r.FormaPago,
        r.ProporcionCfdi,
        r.IvaEsperado,
        r.IvaContable,
        r.IvaDiferencia,
        r.IvaCuentaNivel1,
        r.IvaStatus
    FROM #RegularPolizaStatus AS r
    ORDER BY r.ComprobanteId, r.Fecha, r.TransaccionId;

    SELECT DISTINCT ComprobanteId
    INTO #PaymentIds
    FROM
    (
        SELECT c.Comprobante_Id AS ComprobanteId
        FROM dbo.Transaccion_Comprobante AS tc
        JOIN cfdi.Comprobante AS c
            ON c.Comprobante_Id = tc.Comprobante_ID
        WHERE tc.Transaccion_ID = @Transaccion_ID
          AND c.TipoDeComprobante = 'P'

        UNION ALL

        SELECT c.Comprobante_Id AS ComprobanteId
        FROM dbo.Transaccion_DoctoRelacionado AS td
        JOIN cfdi.Pagos20_DoctoRelacionado AS dr
            ON dr.DoctoRelacionado_Id = td.DoctoRelacionado_Id
        JOIN cfdi.Pagos20_Pago AS p
            ON p.Pago_Id = dr.Pago_Id
        JOIN cfdi.Pagos20 AS p20
            ON p20.Pagos20_Id = p.Pagos20_Id
        JOIN cfdi.Comprobante AS c
            ON c.Comprobante_Id = p20.Comprobante_Id
        WHERE td.Transaccion_ID = @Transaccion_ID
    ) AS ids;

    SELECT
        p20.Comprobante_Id AS ComprobanteId,
        MIN(p.FechaPago) AS FechaPago,
        MAX(p.FormaDePagoP) AS FormaDePagoP,
        MAX(p.MonedaP) AS MonedaP,
        SUM(CAST(ISNULL(p.Monto, 0) AS decimal(19, 4))) AS MontoPago
    INTO #PagoMontoAgg
    FROM cfdi.Pagos20 AS p20
    JOIN cfdi.Pagos20_Pago AS p
        ON p.Pagos20_Id = p20.Pagos20_Id
    WHERE EXISTS (SELECT 1 FROM #PaymentIds AS ids WHERE ids.ComprobanteId = p20.Comprobante_Id)
    GROUP BY p20.Comprobante_Id;

    SELECT
        p20.Comprobante_Id AS ComprobanteId,
        dr.DoctoRelacionado_Id AS DoctoRelacionadoId,
        dr.IdDocumento AS UuidDoctoRelacionado,
        dr.Folio,
        dr.NumParcialidad,
        dr.MonedaDR AS MonedaDr,
        CAST(ISNULL(dr.ImpSaldoAnt, 0) AS decimal(19, 4)) AS ImpSaldoAnt,
        CAST(ISNULL(dr.ImpPagado, 0) AS decimal(19, 4)) AS ImpPagado,
        CAST(ISNULL(dr.ImpSaldoInsoluto, 0) AS decimal(19, 4)) AS ImpSaldoInsoluto,
        CAST(ISNULL(SUM(CASE WHEN tdr.ImpuestoDR = '002' THEN tdr.ImporteDR ELSE 0 END), 0) AS decimal(19, 4)) AS CompIva
    INTO #PagoDocs
    FROM cfdi.Pagos20 AS p20
    JOIN cfdi.Pagos20_Pago AS p
        ON p.Pagos20_Id = p20.Pagos20_Id
    JOIN cfdi.Pagos20_DoctoRelacionado AS dr
        ON dr.Pago_Id = p.Pago_Id
    LEFT JOIN cfdi.Pagos20_TrasladoDR AS tdr
        ON tdr.DoctoRelacionado_Id = dr.DoctoRelacionado_Id
    WHERE EXISTS (SELECT 1 FROM #PaymentIds AS ids WHERE ids.ComprobanteId = p20.Comprobante_Id)
    GROUP BY
        p20.Comprobante_Id,
        dr.DoctoRelacionado_Id,
        dr.IdDocumento,
        dr.Folio,
        dr.NumParcialidad,
        dr.MonedaDR,
        dr.ImpSaldoAnt,
        dr.ImpPagado,
        dr.ImpSaldoInsoluto;

    SELECT
        ids.ComprobanteId,
        CAST(ISNULL(SUM(pd.ImpPagado), 0) AS decimal(19, 4)) AS ImpPagado,
        CAST(ISNULL(SUM(pd.CompIva), 0) AS decimal(19, 4)) AS CompIva,
        COUNT(pd.DoctoRelacionadoId) AS RelatedDocumentsCount
    INTO #PagoAgg
    FROM #PaymentIds AS ids
    LEFT JOIN #PagoDocs AS pd
        ON pd.ComprobanteId = ids.ComprobanteId
    GROUP BY ids.ComprobanteId;

    SELECT
        ComprobanteId,
        TransaccionId,
        MontoAsignado
    INTO #PaymentLinks
    FROM
    (
        SELECT
            c.Comprobante_Id AS ComprobanteId,
            tc.Transaccion_ID AS TransaccionId,
            CAST(tc.Monto AS decimal(19, 4)) AS MontoAsignado
        FROM dbo.Transaccion_Comprobante AS tc
        JOIN cfdi.Comprobante AS c
            ON c.Comprobante_Id = tc.Comprobante_ID
        WHERE c.TipoDeComprobante = 'P'
          AND EXISTS (SELECT 1 FROM #PaymentIds AS ids WHERE ids.ComprobanteId = c.Comprobante_Id)

        UNION ALL

        SELECT
            c.Comprobante_Id AS ComprobanteId,
            td.Transaccion_ID AS TransaccionId,
            CAST(td.Monto AS decimal(19, 4)) AS MontoAsignado
        FROM dbo.Transaccion_DoctoRelacionado AS td
        JOIN cfdi.Pagos20_DoctoRelacionado AS dr
            ON dr.DoctoRelacionado_Id = td.DoctoRelacionado_Id
        JOIN cfdi.Pagos20_Pago AS p
            ON p.Pago_Id = dr.Pago_Id
        JOIN cfdi.Pagos20 AS p20
            ON p20.Pagos20_Id = p.Pagos20_Id
        JOIN cfdi.Comprobante AS c
            ON c.Comprobante_Id = p20.Comprobante_Id
        WHERE EXISTS (SELECT 1 FROM #PaymentIds AS ids WHERE ids.ComprobanteId = c.Comprobante_Id)
    ) AS links;

    ;WITH PagoTaxLinks AS
    (
        SELECT
            pl.ComprobanteId,
            pl.TransaccionId,
            t.Fecha,
            t.Concepto,
            CAST(t.Monto AS decimal(19, 4)) AS TransaccionMonto,
            pl.MontoAsignado,
            t.Tipo_Poliza AS TipoPoliza,
            t.Forma_Pago AS FormaPago,
            cd.RFC_EMISOR AS EmisorRfc,
            cd.RFC_RECEPTOR AS ReceptorRfc,
            CASE
                WHEN cd.RFC_EMISOR = @ContextRfc THEN 'Emitido'
                WHEN cd.RFC_RECEPTOR = @ContextRfc THEN 'Recibido'
                ELSE 'Otro'
            END AS Direccion,
            pa.CompIva,
            CAST(COUNT(*) OVER (PARTITION BY pl.ComprobanteId) AS decimal(19, 4)) AS PolizasPorComplemento,
            CAST(SUM(pa.CompIva) OVER (PARTITION BY pl.TransaccionId) AS decimal(19, 4)) AS CompIvaPorTransaccion,
            CAST(
                CASE
                    WHEN cd.RFC_EMISOR = @ContextRfc THEN ISNULL(iva208.Haber, 0) - ISNULL(iva208.Debe, 0)
                    WHEN cd.RFC_RECEPTOR = @ContextRfc THEN ISNULL(iva118.Debe, 0) - ISNULL(iva118.Haber, 0)
                    ELSE 0
                END AS decimal(19, 4)
            ) AS IvaContableTransaccion
        FROM #PaymentLinks AS pl
        JOIN dbo.Transacciones AS t
            ON t.ID = pl.TransaccionId
        JOIN cfdi.Comprobante_Detalle AS cd
            ON cd.Comprobante_Id = pl.ComprobanteId
        JOIN #PagoAgg AS pa
            ON pa.ComprobanteId = pl.ComprobanteId
        OUTER APPLY
        (
            SELECT SUM(CAST(rc.Debe AS decimal(19, 4))) AS Debe, SUM(CAST(rc.Haber AS decimal(19, 4))) AS Haber
            FROM dbo.Registro_Contable AS rc
            WHERE rc.TransaccionID = pl.TransaccionId
              AND rc.Nivel1 = '208'
        ) AS iva208
        OUTER APPLY
        (
            SELECT SUM(CAST(rc.Debe AS decimal(19, 4))) AS Debe, SUM(CAST(rc.Haber AS decimal(19, 4))) AS Haber
            FROM dbo.Registro_Contable AS rc
            WHERE rc.TransaccionID = pl.TransaccionId
              AND rc.Nivel1 = '118'
        ) AS iva118
    ),
    PagoPolizaRows AS
    (
        SELECT
            ptl.ComprobanteId,
            ptl.TransaccionId,
            ptl.Fecha,
            ptl.Concepto,
            ptl.TransaccionMonto,
            ptl.MontoAsignado,
            ptl.TipoPoliza,
            ptl.FormaPago,
            CAST(0 AS decimal(19, 8)) AS ProporcionCfdi,
            CAST(CASE WHEN ptl.PolizasPorComplemento <> 0 THEN ptl.CompIva / ptl.PolizasPorComplemento ELSE 0 END AS decimal(19, 4)) AS IvaEsperado,
            CAST(CASE WHEN ptl.CompIvaPorTransaccion <> 0 THEN ptl.IvaContableTransaccion * (ptl.CompIva / ptl.CompIvaPorTransaccion) ELSE 0 END AS decimal(19, 4)) AS IvaContable,
            CASE WHEN ptl.Direccion = 'Emitido' THEN '208'
                 WHEN ptl.Direccion = 'Recibido' THEN '118'
                 ELSE NULL END AS IvaCuentaNivel1
        FROM PagoTaxLinks AS ptl
    )
    SELECT
        p.*,
        CAST(p.IvaEsperado - p.IvaContable AS decimal(19, 4)) AS IvaDiferencia,
        CASE
            WHEN p.IvaCuentaNivel1 IS NULL THEN 'NA'
            WHEN ABS(p.IvaEsperado - p.IvaContable) <= @Tolerancia THEN 'OK'
            ELSE 'DIFERENCIA'
        END AS IvaStatus
    INTO #PagoPolizaFinal
    FROM PagoPolizaRows AS p;

    SELECT
        ids.ComprobanteId,
        tfd.UUID AS ComprobanteUuid,
        cd.RFC_EMISOR AS EmisorRfc,
        cd.RFC_RECEPTOR AS ReceptorRfc,
        CASE
            WHEN cd.RFC_EMISOR = @ContextRfc THEN 'Emitido'
            WHEN cd.RFC_RECEPTOR = @ContextRfc THEN 'Recibido'
            ELSE 'Otro'
        END AS Direccion,
        pm.FechaPago,
        pm.FormaDePagoP,
        pm.MonedaP,
        ISNULL(pm.MontoPago, 0) AS MontoPago,
        pa.ImpPagado,
        pa.CompIva,
        ISNULL(SUM(pp.IvaContable), 0) AS IvaContable,
        CAST(pa.CompIva - ISNULL(SUM(pp.IvaContable), 0) AS decimal(19, 4)) AS IvaDiferencia,
        CASE
            WHEN cd.RFC_EMISOR <> @ContextRfc AND cd.RFC_RECEPTOR <> @ContextRfc THEN 'NA'
            WHEN ABS(pa.CompIva - ISNULL(SUM(pp.IvaContable), 0)) <= @Tolerancia THEN 'OK'
            ELSE 'DIFERENCIA'
        END AS IvaStatus,
        COUNT(DISTINCT pp.TransaccionId) AS PolizasCount,
        pa.RelatedDocumentsCount,
        cd.XML_Attachment_ID AS XmlAttachmentId
    FROM #PaymentIds AS ids
    JOIN cfdi.Comprobante_Detalle AS cd
        ON cd.Comprobante_Id = ids.ComprobanteId
    LEFT JOIN cfdi.TimbreFiscalDigital AS tfd
        ON tfd.Comprobante_Id = ids.ComprobanteId
    LEFT JOIN #PagoMontoAgg AS pm
        ON pm.ComprobanteId = ids.ComprobanteId
    JOIN #PagoAgg AS pa
        ON pa.ComprobanteId = ids.ComprobanteId
    LEFT JOIN #PagoPolizaFinal AS pp
        ON pp.ComprobanteId = ids.ComprobanteId
    GROUP BY
        ids.ComprobanteId,
        tfd.UUID,
        cd.RFC_EMISOR,
        cd.RFC_RECEPTOR,
        pm.FechaPago,
        pm.FormaDePagoP,
        pm.MonedaP,
        pm.MontoPago,
        pa.ImpPagado,
        pa.CompIva,
        pa.RelatedDocumentsCount,
        cd.XML_Attachment_ID
    ORDER BY pm.FechaPago DESC, ids.ComprobanteId DESC;

    SELECT
        pd.ComprobanteId,
        pd.DoctoRelacionadoId,
        TRY_CONVERT(uniqueidentifier, pd.UuidDoctoRelacionado) AS UuidDoctoRelacionado,
        pd.Folio,
        pd.NumParcialidad,
        pd.MonedaDr,
        pd.ImpSaldoAnt,
        pd.ImpPagado,
        pd.ImpSaldoInsoluto,
        pd.CompIva
    FROM #PagoDocs AS pd
    ORDER BY pd.ComprobanteId, pd.DoctoRelacionadoId;

    SELECT
        pp.ComprobanteId,
        pp.TransaccionId,
        pp.Fecha,
        pp.Concepto,
        pp.TransaccionMonto,
        pp.MontoAsignado,
        pp.TipoPoliza,
        pp.FormaPago,
        pp.ProporcionCfdi,
        pp.IvaEsperado,
        pp.IvaContable,
        pp.IvaDiferencia,
        pp.IvaCuentaNivel1,
        pp.IvaStatus
    FROM #PagoPolizaFinal AS pp
    ORDER BY pp.ComprobanteId, pp.Fecha, pp.TransaccionId;
END;
GO
