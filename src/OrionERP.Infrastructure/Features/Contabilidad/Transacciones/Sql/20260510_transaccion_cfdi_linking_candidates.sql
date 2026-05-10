SET ANSI_NULLS ON;
GO
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE [cfdi].[Transaccion_CFDI_Linking_Candidates]
    @Transaccion_ID int,
    @Monto decimal(19, 4) = NULL,
    @Concepto nvarchar(255) = NULL,
    @Comprobante_ID bigint = NULL,
    @Tipo varchar(10) = NULL,
    @Renglones int = 50,
    @Tolerancia decimal(19, 4) = 1.00
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE
        @ContextRfc varchar(50),
        @TransaccionMonto decimal(19, 4),
        @TransaccionFecha date,
        @Objetivo decimal(19, 4),
        @RegularAsignado decimal(19, 4),
        @Pago20Asignado decimal(19, 4),
        @RegularDisponible decimal(19, 4),
        @Pago20Disponible decimal(19, 4);

    SELECT
        @ContextRfc = NULLIF(LTRIM(RTRIM(t.RFC)), ''),
        @TransaccionMonto = ABS(CAST(ISNULL(t.Monto, 0) AS decimal(19, 4))),
        @TransaccionFecha = CAST(t.Fecha AS date)
    FROM dbo.Transacciones AS t
    WHERE t.ID = @Transaccion_ID;

    SET @Objetivo = COALESCE(@Monto, NULLIF(@TransaccionMonto, 0), 0);

    SELECT @RegularAsignado = CAST(ISNULL(SUM(tc.Monto), 0) AS decimal(19, 4))
    FROM dbo.Transaccion_Comprobante AS tc
    JOIN cfdi.Comprobante AS c
        ON c.Comprobante_Id = tc.Comprobante_ID
    WHERE tc.Transaccion_ID = @Transaccion_ID
      AND c.TipoDeComprobante IN ('I', 'N', 'E');

    SELECT @Pago20Asignado = CAST(ISNULL(SUM(td.Monto), 0) AS decimal(19, 4))
    FROM dbo.Transaccion_DoctoRelacionado AS td
    WHERE td.Transaccion_ID = @Transaccion_ID;

    SET @RegularDisponible = CASE WHEN ISNULL(@TransaccionMonto, 0) - ISNULL(@RegularAsignado, 0) > 0
                                  THEN @TransaccionMonto - ISNULL(@RegularAsignado, 0)
                                  ELSE @TransaccionMonto END;
    SET @Pago20Disponible = CASE WHEN ISNULL(@TransaccionMonto, 0) - ISNULL(@Pago20Asignado, 0) > 0
                                 THEN @TransaccionMonto - ISNULL(@Pago20Asignado, 0)
                                 ELSE @TransaccionMonto END;

    ;WITH ConceptosAgg AS
    (
        SELECT
            cs.Comprobante_Id AS ComprobanteId,
            STRING_AGG(CONVERT(varchar(max), cp.Descripcion), ', ') AS Conceptos
        FROM cfdi.Conceptos AS cs
        JOIN cfdi.Concepto AS cp
            ON cp.Conceptos_Id = cs.Conceptos_Id
        GROUP BY cs.Comprobante_Id
    ),
    RegularBase AS
    (
        SELECT TOP (@Renglones)
            cd.Comprobante_Id AS ComprobanteId,
            cd.Fecha,
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
            cd.FormaPago,
            cd.MetodoPago,
            cd.UsoCFDI AS UsoCfdi,
            ca.Conceptos,
            CAST(ISNULL(cd.SubTotal, 0) AS decimal(19, 4)) AS SubTotal,
            CAST(ISNULL(cd.Total, 0) AS decimal(19, 4)) AS Total,
            CAST(ISNULL(cd.IVA, 0) AS decimal(19, 4)) AS Iva,
            CAST(ISNULL(cd.IVA_RETENIDO, 0) AS decimal(19, 4)) AS IvaRetenido,
            CAST(ISNULL(cd.ISR_RETENIDO, 0) AS decimal(19, 4)) AS IsrRetenido,
            CAST(ISNULL(linked.AsignadoCfdi, 0) AS decimal(19, 4)) AS AsignadoCfdi,
            CAST(CASE WHEN CAST(ISNULL(cd.Total, 0) AS decimal(19, 4)) - ISNULL(linked.AsignadoCfdi, 0) > 0
                      THEN CAST(ISNULL(cd.Total, 0) AS decimal(19, 4)) - ISNULL(linked.AsignadoCfdi, 0)
                      ELSE CAST(ISNULL(cd.Total, 0) AS decimal(19, 4)) END AS decimal(19, 4)) AS Pendiente,
            ISNULL(linked.PolizasCount, 0) AS PolizasCount,
            cd.XML_Attachment_ID AS XmlAttachmentId,
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
            ) AS IvaContable,
            ABS(DATEDIFF(day, @TransaccionFecha, cd.Fecha)) AS DateDistance
        FROM cfdi.Comprobante_Detalle AS cd
        LEFT JOIN ConceptosAgg AS ca
            ON ca.ComprobanteId = cd.Comprobante_Id
        OUTER APPLY
        (
            SELECT
                SUM(CAST(tc.Monto AS decimal(19, 4))) AS AsignadoCfdi,
                COUNT(DISTINCT tc.Transaccion_ID) AS PolizasCount
            FROM dbo.Transaccion_Comprobante AS tc
            WHERE tc.Comprobante_ID = cd.Comprobante_Id
        ) AS linked
        OUTER APPLY
        (
            SELECT SUM(CAST(rc.Debe AS decimal(19, 4))) AS Debe, SUM(CAST(rc.Haber AS decimal(19, 4))) AS Haber
            FROM dbo.Registro_Contable AS rc
            WHERE rc.TransaccionID = @Transaccion_ID
              AND rc.Nivel1 = '208'
        ) AS iva208
        OUTER APPLY
        (
            SELECT SUM(CAST(rc.Debe AS decimal(19, 4))) AS Debe, SUM(CAST(rc.Haber AS decimal(19, 4))) AS Haber
            FROM dbo.Registro_Contable AS rc
            WHERE rc.TransaccionID = @Transaccion_ID
              AND rc.Nivel1 = '118'
        ) AS iva118
        WHERE cd.TipoDeComprobante IN ('I', 'N', 'E')
          AND (@ContextRfc IS NULL OR cd.RFC_EMISOR = @ContextRfc OR cd.RFC_RECEPTOR = @ContextRfc)
          AND (@Comprobante_ID IS NULL OR cd.Comprobante_Id = @Comprobante_ID)
          AND (@Concepto IS NULL OR ca.Conceptos LIKE '%' + @Concepto + '%' OR cd.EMISOR LIKE '%' + @Concepto + '%' OR cd.RECEPTOR LIKE '%' + @Concepto + '%')
          AND (@Tipo IS NULL OR @Tipo IN ('CFDI', 'REGULAR') OR cd.TipoDeComprobante = @Tipo)
          AND NOT EXISTS
          (
              SELECT 1
              FROM dbo.Transaccion_Comprobante AS currentLink
              WHERE currentLink.Transaccion_ID = @Transaccion_ID
                AND currentLink.Comprobante_ID = cd.Comprobante_Id
          )
        ORDER BY
            CASE WHEN ABS(CAST(ISNULL(cd.Total, 0) AS decimal(19, 4)) - @Objetivo) <= @Tolerancia THEN 0 ELSE 1 END,
            ABS(DATEDIFF(day, @TransaccionFecha, cd.Fecha)),
            ABS(CAST(ISNULL(cd.Total, 0) AS decimal(19, 4)) - @Objetivo),
            cd.Fecha DESC,
            cd.Comprobante_Id DESC
    ),
    RegularScored AS
    (
        SELECT
            rb.*,
            CAST(CASE
                WHEN rb.Pendiente <= 0 THEN 0
                WHEN @RegularDisponible > 0 THEN IIF(rb.Pendiente < @RegularDisponible, rb.Pendiente, @RegularDisponible)
                ELSE rb.Pendiente
            END AS decimal(19, 4)) AS MontoSugerido
        FROM RegularBase AS rb
    )
    SELECT
        rs.ComprobanteId,
        rs.Fecha,
        rs.Tipo,
        rs.Serie,
        rs.Folio,
        rs.EmisorRfc,
        rs.ReceptorRfc,
        rs.Direccion,
        rs.Uuid,
        rs.FormaPago,
        rs.MetodoPago,
        rs.UsoCfdi,
        rs.Conceptos,
        rs.SubTotal,
        rs.Total,
        rs.Iva,
        rs.IvaRetenido,
        rs.IsrRetenido,
        rs.AsignadoCfdi,
        rs.Pendiente,
        rs.MontoSugerido,
        CAST(ABS(rs.MontoSugerido - @Objetivo) AS decimal(19, 4)) AS DiferenciaObjetivo,
        rs.PolizasCount,
        (CASE WHEN ABS(rs.Total - @Objetivo) <= @Tolerancia THEN 45 ELSE 0 END)
        + (CASE WHEN rs.DateDistance <= 7 THEN 25 WHEN rs.DateDistance <= 31 THEN 15 ELSE 0 END)
        + (CASE WHEN rs.Pendiente > 0 THEN 20 ELSE 0 END)
        + (CASE WHEN rs.PolizasCount = 0 THEN 10 ELSE 0 END) AS MatchScore,
        CASE
            WHEN rs.Pendiente <= 0 THEN 'SIN_DISPONIBLE'
            WHEN ABS(rs.Total - @Objetivo) <= @Tolerancia AND rs.DateDistance <= 31 THEN 'FUERTE'
            WHEN ABS(rs.Total - @Objetivo) <= @Tolerancia OR rs.DateDistance <= 31 THEN 'POSIBLE'
            ELSE 'AMPLIA'
        END AS MatchStatus,
        CAST(CASE WHEN rs.Total <> 0 THEN rs.Iva * (rs.MontoSugerido / rs.Total) ELSE 0 END AS decimal(19, 4)) AS IvaEsperado,
        rs.IvaContable,
        CAST(CASE WHEN rs.Total <> 0 THEN rs.Iva * (rs.MontoSugerido / rs.Total) ELSE 0 END - rs.IvaContable AS decimal(19, 4)) AS IvaDiferencia,
        CASE WHEN rs.Direccion = 'Emitido' THEN '208'
             WHEN rs.Direccion = 'Recibido' THEN '118'
             ELSE NULL END AS IvaCuentaNivel1,
        CASE
            WHEN rs.Direccion = 'Otro' OR rs.Total = 0 THEN 'NA'
            WHEN ABS(CASE WHEN rs.Total <> 0 THEN rs.Iva * (rs.MontoSugerido / rs.Total) ELSE 0 END - rs.IvaContable) <= @Tolerancia THEN 'OK'
            ELSE 'DIFERENCIA'
        END AS IvaStatus,
        rs.XmlAttachmentId
    FROM RegularScored AS rs
    ORDER BY MatchScore DESC, DiferenciaObjetivo, rs.DateDistance, rs.Fecha DESC, rs.ComprobanteId DESC;

    ;WITH PagoBase AS
    (
        SELECT TOP (@Renglones)
            v.DoctoRelacionado_Id AS DoctoRelacionadoId,
            v.Comprobante_Id AS ComprobanteId,
            v.ComprobanteUUID AS ComprobanteUuid,
            v.EmisorRfc,
            v.ReceptorRfc,
            CASE
                WHEN v.EmisorRfc = @ContextRfc THEN 'Emitido'
                WHEN v.ReceptorRfc = @ContextRfc THEN 'Recibido'
                ELSE 'Otro'
            END AS Direccion,
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
            CAST(ISNULL(linked.AsignadoComplemento, 0) AS decimal(19, 4)) AS AsignadoComplemento,
            CAST(CASE WHEN CAST(ISNULL(v.ImpPagado, 0) AS decimal(19, 4)) - ISNULL(linked.AsignadoComplemento, 0) > 0
                      THEN CAST(ISNULL(v.ImpPagado, 0) AS decimal(19, 4)) - ISNULL(linked.AsignadoComplemento, 0)
                      ELSE CAST(ISNULL(v.ImpPagado, 0) AS decimal(19, 4)) END AS decimal(19, 4)) AS Pendiente,
            ISNULL(linked.PolizasCount, 0) AS PolizasCount,
            related.RelatedDocumentsCount,
            CAST(
                CASE
                    WHEN v.EmisorRfc = @ContextRfc THEN ISNULL(iva208.Haber, 0) - ISNULL(iva208.Debe, 0)
                    WHEN v.ReceptorRfc = @ContextRfc THEN ISNULL(iva118.Debe, 0) - ISNULL(iva118.Haber, 0)
                    ELSE 0
                END AS decimal(19, 4)
            ) AS IvaContable,
            v.XML_Attachment_ID AS XmlAttachmentId,
            ABS(DATEDIFF(day, @TransaccionFecha, v.FechaPago)) AS DateDistance
        FROM cfdi.vw_Pagos20_Resumen AS v
        OUTER APPLY
        (
            SELECT
                SUM(CAST(td.Monto AS decimal(19, 4))) AS AsignadoComplemento,
                COUNT(DISTINCT td.Transaccion_ID) AS PolizasCount
            FROM dbo.Transaccion_DoctoRelacionado AS td
            WHERE td.DoctoRelacionado_Id = v.DoctoRelacionado_Id
        ) AS linked
        OUTER APPLY
        (
            SELECT COUNT(*) AS RelatedDocumentsCount
            FROM cfdi.vw_Pagos20_Resumen AS vr
            WHERE vr.Comprobante_Id = v.Comprobante_Id
        ) AS related
        OUTER APPLY
        (
            SELECT SUM(CAST(rc.Debe AS decimal(19, 4))) AS Debe, SUM(CAST(rc.Haber AS decimal(19, 4))) AS Haber
            FROM dbo.Registro_Contable AS rc
            WHERE rc.TransaccionID = @Transaccion_ID
              AND rc.Nivel1 = '208'
        ) AS iva208
        OUTER APPLY
        (
            SELECT SUM(CAST(rc.Debe AS decimal(19, 4))) AS Debe, SUM(CAST(rc.Haber AS decimal(19, 4))) AS Haber
            FROM dbo.Registro_Contable AS rc
            WHERE rc.TransaccionID = @Transaccion_ID
              AND rc.Nivel1 = '118'
        ) AS iva118
        WHERE v.DoctoRelacionado_Id IS NOT NULL
          AND (@ContextRfc IS NULL OR v.EmisorRfc = @ContextRfc OR v.ReceptorRfc = @ContextRfc)
          AND (@Comprobante_ID IS NULL OR v.Comprobante_Id = @Comprobante_ID OR v.DoctoRelacionado_Id = @Comprobante_ID)
          AND (@Concepto IS NULL OR CONVERT(varchar(50), v.UUID_DoctoRelacionado) LIKE '%' + @Concepto + '%' OR v.Folio LIKE '%' + @Concepto + '%')
          AND (@Tipo IS NULL OR @Tipo IN ('P', 'COMP', 'PAGO20'))
          AND NOT EXISTS
          (
              SELECT 1
              FROM dbo.Transaccion_DoctoRelacionado AS currentLink
              WHERE currentLink.Transaccion_ID = @Transaccion_ID
                AND currentLink.DoctoRelacionado_Id = v.DoctoRelacionado_Id
          )
        ORDER BY
            CASE WHEN ABS(CAST(ISNULL(v.ImpPagado, 0) AS decimal(19, 4)) - @Objetivo) <= @Tolerancia THEN 0 ELSE 1 END,
            ABS(DATEDIFF(day, @TransaccionFecha, v.FechaPago)),
            ABS(CAST(ISNULL(v.ImpPagado, 0) AS decimal(19, 4)) - @Objetivo),
            v.FechaPago DESC,
            v.DoctoRelacionado_Id DESC
    ),
    PagoScored AS
    (
        SELECT
            pb.*,
            CAST(CASE
                WHEN pb.Pendiente <= 0 THEN 0
                WHEN @Pago20Disponible > 0 THEN IIF(pb.Pendiente < @Pago20Disponible, pb.Pendiente, @Pago20Disponible)
                ELSE pb.Pendiente
            END AS decimal(19, 4)) AS MontoSugerido
        FROM PagoBase AS pb
    )
    SELECT
        ps.DoctoRelacionadoId,
        ps.ComprobanteId,
        ps.ComprobanteUuid,
        ps.EmisorRfc,
        ps.ReceptorRfc,
        ps.Direccion,
        ps.FechaPago,
        ps.FormaDePagoP,
        ps.MonedaP,
        ps.MontoPago,
        ps.UuidDoctoRelacionado,
        ps.Folio,
        ps.NumParcialidad,
        ps.MonedaDr,
        ps.ImpPagado,
        ps.CompIva,
        ps.AsignadoComplemento,
        ps.Pendiente,
        ps.MontoSugerido,
        CAST(ABS(ps.MontoSugerido - @Objetivo) AS decimal(19, 4)) AS DiferenciaObjetivo,
        ps.PolizasCount,
        ps.RelatedDocumentsCount,
        (CASE WHEN ABS(ps.ImpPagado - @Objetivo) <= @Tolerancia THEN 45 ELSE 0 END)
        + (CASE WHEN ps.DateDistance <= 7 THEN 25 WHEN ps.DateDistance <= 31 THEN 15 ELSE 0 END)
        + (CASE WHEN ps.Pendiente > 0 THEN 20 ELSE 0 END)
        + (CASE WHEN ps.PolizasCount = 0 THEN 10 ELSE 0 END) AS MatchScore,
        CASE
            WHEN ps.Pendiente <= 0 THEN 'SIN_DISPONIBLE'
            WHEN ABS(ps.ImpPagado - @Objetivo) <= @Tolerancia AND ps.DateDistance <= 31 THEN 'FUERTE'
            WHEN ABS(ps.ImpPagado - @Objetivo) <= @Tolerancia OR ps.DateDistance <= 31 THEN 'POSIBLE'
            ELSE 'AMPLIA'
        END AS MatchStatus,
        ps.IvaContable,
        CAST(CASE WHEN ps.ImpPagado <> 0 THEN ps.CompIva * (ps.MontoSugerido / ps.ImpPagado) ELSE 0 END - ps.IvaContable AS decimal(19, 4)) AS IvaDiferencia,
        CASE WHEN ps.Direccion = 'Emitido' THEN '208'
             WHEN ps.Direccion = 'Recibido' THEN '118'
             ELSE NULL END AS IvaCuentaNivel1,
        CASE
            WHEN ps.Direccion = 'Otro' OR ps.ImpPagado = 0 THEN 'NA'
            WHEN ABS(CASE WHEN ps.ImpPagado <> 0 THEN ps.CompIva * (ps.MontoSugerido / ps.ImpPagado) ELSE 0 END - ps.IvaContable) <= @Tolerancia THEN 'OK'
            ELSE 'DIFERENCIA'
        END AS IvaStatus,
        ps.XmlAttachmentId
    FROM PagoScored AS ps
    ORDER BY MatchScore DESC, DiferenciaObjetivo, ps.DateDistance, ps.FechaPago DESC, ps.DoctoRelacionadoId DESC;
END;
GO
