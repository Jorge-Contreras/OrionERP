



/* ==== ACTUALIZAR VISTA ==== */
CREATE VIEW [cfdi].[Comprobante_Detalle]
AS
SELECT
    r.UsoCFDI                                       AS UsoCFDI,
    r.Rfc + '-' + r.Nombre                          AS RECEPTOR,
    e.Rfc + '-' + e.Nombre                          AS EMISOR,
    tfd.UUID                                        AS FOLIO_FISCAL,
    c.Fecha,
    ROUND(c.SubTotal, 2)                            AS SubTotal,
    ROUND(c.SubTotal - ISNULL(c.Descuento, 0), 2)   AS SubTotal_Desc,

    /* Impuestos agregados */
    ROUND(ISNULL(tax_traslados.IVA,0),2)            AS IVA,
    ROUND(ISNULL(tax_traslados.IEPS,0),2)           AS IEPS,
    ROUND(ISNULL(tax_retenciones.IVA_RETENIDO,0),2) AS IVA_RETENIDO,
    ROUND(ISNULL(tax_retenciones.ISR_RETENIDO,0),2) AS ISR_RETENIDO,
    ROUND(ISNULL(tax_retenciones.IEPS_RETENIDO,0),2)AS IEPS_RETENIDO,

    ROUND(CASE WHEN tax_traslados.IVA <> 0 THEN tax_traslados.IVA/0.16 ELSE 0 END,2) AS Actos_16,
    dbo.RedondeoSAT(c.Total
          - CASE WHEN tax_traslados.IVA <> 0 THEN (tax_traslados.IVA/0.16)+tax_traslados.IVA -tax_retenciones.IVA_RETENIDO ELSE 0 END) AS Actos_0,

    c.Serie,
    c.Folio,
    ROUND(c.Total,2)                                AS Total,
    c.Sello,
    CAST(c.FormaPago       AS varchar(255))         AS FormaPago,
    c.NoCertificado,
    CAST(c.Version         AS varchar(50))          AS Version,
    CAST(c.Certificado     AS varchar(max))         AS Certificado,
    c.CondicionesDePago,
    ROUND(c.Descuento,2)                            AS Descuento,
    CAST(c.Moneda          AS varchar(10))          AS Moneda,
    c.TipoCambio,
    CAST(c.TipoDeComprobante AS varchar(50))        AS TipoDeComprobante,
    CAST(c.Exportacion     AS varchar(10))          AS Exportacion,
    CAST(c.MetodoPago      AS varchar(50))          AS MetodoPago,
    CAST(c.LugarExpedicion AS varchar(100))         AS LugarExpedicion,
    CAST(c.Confirmacion    AS varchar(50))          AS Confirmacion,
    c.Comprobante_Id,
    c.Tipo_Comprobante,
    c.Incluir_En_Declaracion,
    c.Factor_Declaracion,
    e.Rfc                                           AS RFC_EMISOR,
    r.Rfc                                           AS RFC_RECEPTOR,
    ig.PERIODICIDAD,
    ig.MESES,
    ig.ANIO,
    c.FechaCancelacion,
    c.Estatus,

    /* Fechas de todas las transacciones relacionadas */
    STUFF((
        SELECT DISTINCT ', ' + CONVERT(varchar, t2.Fecha, 23)
        FROM dbo.Transaccion_Comprobante tc2
        JOIN dbo.Transacciones t2 ON t2.ID = tc2.Transaccion_ID
        WHERE tc2.Comprobante_ID = c.Comprobante_Id
        FOR XML PATH(''), TYPE).value('.', 'nvarchar(max)'), 1, 2, '') AS FechasTransacciones,

    /* ===== NUEVA COLUMNA ===== */
    FirstTx.FirstTransaccionID                      AS Poliza,

    /* Suma de montos de transacciones */
    ISNULL(SumPolizas.SumaMontos,0)                 AS SumaPolizas,
	c.XML_Attachment_ID
FROM dbo.Comprobante c
LEFT JOIN dbo.Receptor              r  ON r.Comprobante_Id = c.Comprobante_Id
LEFT JOIN dbo.Emisor                e  ON e.Comprobante_Id = c.Comprobante_Id
LEFT JOIN dbo.TimbreFiscalDigital   tfd ON tfd.Comprobante_Id = c.Comprobante_Id
LEFT JOIN dbo.InformacionGlobal     ig  ON ig.Comprobante_ID = c.Comprobante_Id

/* Traslados */
OUTER APPLY (
    SELECT
        SUM(CASE WHEN t.Impuesto='002' THEN t.Importe ELSE 0 END) AS IVA,
        SUM(CASE WHEN t.Impuesto='003' THEN t.Importe ELSE 0 END) AS IEPS
    FROM dbo.Impuestos i
    LEFT JOIN dbo.Traslados  ts ON ts.Impuestos_Id = i.Impuestos_Id
    LEFT JOIN dbo.Traslado    t ON t.Traslados_Id = ts.Traslados_Id
    WHERE i.Comprobante_Id = c.Comprobante_Id
) tax_traslados

/* Retenciones */
OUTER APPLY (
    SELECT
        SUM(CASE WHEN rt2.Impuesto='002' THEN rt2.Importe ELSE 0 END) AS IVA_RETENIDO,
        SUM(CASE WHEN rt2.Impuesto='001' THEN rt2.Importe ELSE 0 END) AS ISR_RETENIDO,
        SUM(CASE WHEN rt2.Impuesto='003' THEN rt2.Importe ELSE 0 END) AS IEPS_RETENIDO
    FROM dbo.Impuestos i
    LEFT JOIN dbo.Retenciones rt  ON rt.Impuestos_Id = i.Impuestos_Id
    LEFT JOIN dbo.Retencion   rt2 ON rt2.Retenciones_Id = rt.Retenciones_Id
    WHERE i.Comprobante_Id = c.Comprobante_Id
) tax_retenciones

/* Suma de montos */
OUTER APPLY (
    SELECT SUM(tc.Monto) AS SumaMontos
    FROM dbo.Transaccion_Comprobante tc
    WHERE tc.Comprobante_ID = c.Comprobante_Id
) SumPolizas

/* Primer Transaccion_ID (por fecha / ID) */
OUTER APPLY (
    SELECT TOP (1) t.ID AS FirstTransaccionID
    FROM dbo.Transaccion_Comprobante tc
    JOIN dbo.Transacciones t ON t.ID = tc.Transaccion_ID
    WHERE tc.Comprobante_ID = c.Comprobante_Id
    ORDER BY t.Fecha, t.ID      -- “primera” = más antigua; ajusta si tu criterio es otro
) FirstTx;

