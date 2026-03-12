
CREATE PROCEDURE [cfdi].[CFDIs_Candidatos_Para_Poliza]
  @Monto            DECIMAL(19,2) = NULL,      -- optional: equality on Total
  @Concepto         VARCHAR(200)  = NULL,      -- optional: contains on Conceptos
  @Rfc              VARCHAR(20),               -- mandatory: either Emisor or Receptor RFC must match
  @Comprobante_ID   BIGINT        = NULL,      -- optional: filters final Comprobante_Id
  @Renglones        INT           = NULL,      -- optional: number of final rows (NULL/0 = all)
  @Tipo             VARCHAR(10)   = NULL,      -- optional: filters final Tipo ('COMP','CFDI','NOMINA','NOTA','OTRO')
  @Comprobantes_In  VARCHAR(MAX)  = NULL       -- optional: comma-delimited list of Comprobante_Id (overrides other filters except RFC)
AS
BEGIN
  SET NOCOUNT ON;

  /* Normalize inputs (local variables also mitigate parameter sniffing) */
  DECLARE @ConceptoTrim VARCHAR(200) = NULLIF(LTRIM(RTRIM(@Concepto)), '');
  DECLARE @RfcTrim      VARCHAR(20)  = LTRIM(RTRIM(@Rfc));
  DECLARE @TipoTrim     VARCHAR(10)  = NULLIF(LTRIM(RTRIM(@Tipo)), '');
  DECLARE @MontoFilter  DECIMAL(19,2) = @Monto;
  DECLARE @CompIdFilter BIGINT        = @Comprobante_ID;
  DECLARE @RowsLimit    INT           = ISNULL(NULLIF(@Renglones, 0), 2147483647); -- "all" if NULL/0

  /* Parse @Comprobantes_In once into a PK table (fast seek) */
  DECLARE @WantedIds TABLE (Comprobante_Id BIGINT NOT NULL PRIMARY KEY);
  IF @Comprobantes_In IS NOT NULL AND LTRIM(RTRIM(@Comprobantes_In)) <> ''
  BEGIN
    INSERT INTO @WantedIds (Comprobante_Id)
    SELECT DISTINCT TRY_CONVERT(BIGINT, LTRIM(RTRIM(value)))
    FROM STRING_SPLIT(@Comprobantes_In, ',')
    WHERE TRY_CONVERT(BIGINT, LTRIM(RTRIM(value))) IS NOT NULL;
  END

  DECLARE @ApplyList BIT = CASE WHEN EXISTS (SELECT 1 FROM @WantedIds) THEN 1 ELSE 0 END;

  /* Payments (complementos) -> DoctoRelacionado_Id is the target Comprobante_Id */
  ;WITH Q_Pagos AS
  (
    SELECT
      DR.DoctoRelacionado_Id                               AS Comprobante_Id,
      CAST(P.FechaPago AS datetime2(0))                    AS Fecha,
      CAST('COMP' AS varchar(10))                          AS Tipo,
      C.Serie                                              AS Serie,
      C.Folio                                              AS Folio,
      E.Rfc                                                AS Emisor_Rfc,
      R.Rfc                                                AS Receptor_Rfc,
      LEFT(DR.IdDocumento, 8)                              AS UUID,
      P.FormaDePagoP                                       AS FormaPago,
      CAST(DR.ImpPagado AS decimal(19,2))                  AS Total,
      ISNULL(TA.Polizas, 0)                                AS Polizas,
      ISNULL(TA.Asignado, 0.0)                             AS Asignado,
      C.MetodoPago                                         AS MetodoPago,
      R.UsoCFDI                                            AS UsoCFDI,
      CA.Concepto                                          AS Conceptos,
      C.XML_Attachment_ID                                  AS XML_Attachment_ID
    FROM cfdi.Pagos20_Pago             AS P
    JOIN cfdi.Pagos20_DoctoRelacionado AS DR  ON DR.Pago_Id       = P.Pago_Id
    JOIN cfdi.TimbreFiscalDigital      AS TFD ON TFD.UUID         = DR.IdDocumento
    JOIN cfdi.Comprobante              AS C   ON C.Comprobante_Id = TFD.Comprobante_Id
    JOIN cfdi.Emisor                   AS E   ON E.Comprobante_Id = C.Comprobante_Id
    JOIN cfdi.Receptor                 AS R   ON R.Comprobante_Id = C.Comprobante_Id
    OUTER APPLY
    (
      SELECT STRING_AGG(CP.Descripcion, ', ') AS Concepto
      FROM cfdi.Conceptos CS
      JOIN cfdi.Concepto  CP ON CP.Conceptos_Id = CS.Conceptos_Id
      WHERE CS.Comprobante_Id = C.Comprobante_Id
    ) AS CA
    OUTER APPLY
    (
      SELECT COUNT(DISTINCT TD.Transaccion_ID) AS Polizas,
             SUM(TD.Monto)                     AS Asignado
      FROM dbo.Transaccion_DoctoRelacionado TD
      WHERE TD.DoctoRelacionado_Id = DR.DoctoRelacionado_Id
    ) AS TA
    WHERE
        R.UsoCFDI <> 'CP01'
    AND (E.Rfc = @RfcTrim OR R.Rfc = @RfcTrim)
    AND (@ApplyList = 0 OR EXISTS (SELECT 1 FROM @WantedIds W WHERE W.Comprobante_Id = DR.DoctoRelacionado_Id))
    AND (@ApplyList = 1 OR @MontoFilter IS NULL OR DR.ImpPagado = @MontoFilter)
    AND (@ApplyList = 1 OR @ConceptoTrim IS NULL OR CA.Concepto LIKE '%' + @ConceptoTrim + '%')
    AND (@ApplyList = 1 OR @CompIdFilter IS NULL OR DR.DoctoRelacionado_Id = @CompIdFilter)
    AND (@ApplyList = 1 OR @TipoTrim IS NULL OR @TipoTrim = 'COMP') -- early short-circuit
  ),
  Q_Comprobantes AS
  (
    SELECT
      C.Comprobante_Id                                     AS Comprobante_Id,
      CAST(C.Fecha AS datetime2(0))                        AS Fecha,
      CAST(CASE UPPER(C.TipoDeComprobante)
             WHEN 'I' THEN 'CFDI'
             WHEN 'N' THEN 'NOMINA'
             WHEN 'E' THEN 'NOTA'
             WHEN 'P' THEN 'PAGO'
             ELSE 'OTRO'
           END AS varchar(10))                             AS Tipo,
      C.Serie                                              AS Serie,
      C.Folio                                              AS Folio,
      E.Rfc                                                AS Emisor_Rfc,
      R.Rfc                                                AS Receptor_Rfc,
      LEFT(TFD.UUID, 8)                                    AS UUID,
      C.FormaPago                                          AS FormaPago,
      CAST(C.Total AS decimal(19,2))                       AS Total,
      ISNULL(AC.Polizas, 0)                                AS Polizas,
      ISNULL(AC.Asignado, 0.0)                             AS Asignado,
      C.MetodoPago                                         AS MetodoPago,
      R.UsoCFDI                                            AS UsoCFDI,
      CA.Concepto                                          AS Conceptos,
      C.XML_Attachment_ID                                  AS XML_Attachment_ID

    FROM cfdi.Comprobante AS C
    JOIN cfdi.Emisor   AS E    ON E.Comprobante_Id = C.Comprobante_Id
    JOIN cfdi.Receptor AS R    ON R.Comprobante_Id = C.Comprobante_Id
    LEFT JOIN cfdi.TimbreFiscalDigital AS TFD ON TFD.Comprobante_Id = C.Comprobante_Id
    OUTER APPLY
    (
      SELECT STRING_AGG(CP.Descripcion, ', ') AS Concepto
      FROM cfdi.Conceptos CS
      JOIN cfdi.Concepto  CP ON CP.Conceptos_Id = CS.Conceptos_Id
      WHERE CS.Comprobante_Id = C.Comprobante_Id
    ) AS CA
    OUTER APPLY
    (
      SELECT COUNT(DISTINCT TC.Transaccion_ID) AS Polizas,
             SUM(TC.Monto)                     AS Asignado
      FROM dbo.Transaccion_Comprobante AS TC
      WHERE TC.Comprobante_ID = C.Comprobante_Id
    ) AS AC
    WHERE
        R.UsoCFDI <> 'CP01'
    AND UPPER(C.TipoDeComprobante) <> 'P'  -- exclude PAGO from base CFDI set
    AND (E.Rfc = @RfcTrim OR R.Rfc = @RfcTrim)
    AND (@ApplyList = 0 OR EXISTS (SELECT 1 FROM @WantedIds W WHERE W.Comprobante_Id = C.Comprobante_Id))
    AND (@ApplyList = 1 OR @MontoFilter IS NULL OR C.Total = @MontoFilter)
    AND (@ApplyList = 1 OR @ConceptoTrim IS NULL OR CA.Concepto LIKE '%' + @ConceptoTrim + '%')
    AND (@ApplyList = 1 OR @CompIdFilter IS NULL OR C.Comprobante_Id = @CompIdFilter)
    AND
    (
      @ApplyList = 1 OR @TipoTrim IS NULL OR
      CASE UPPER(C.TipoDeComprobante)
        WHEN 'I' THEN 'CFDI'
        WHEN 'N' THEN 'NOMINA'
        WHEN 'E' THEN 'NOTA'
        WHEN 'P' THEN 'PAGO'
        ELSE 'OTRO'
      END = @TipoTrim
    )
  )
  SELECT TOP (@RowsLimit) *
  FROM
  (
    SELECT * FROM Q_Pagos
    UNION ALL
    SELECT * FROM Q_Comprobantes
  ) AS U
  /* Final guards (cheap, mostly redundant due to pushdown; kept for correctness) */
  WHERE
        (@ApplyList = 0 OR U.Comprobante_Id IN (SELECT Comprobante_Id FROM @WantedIds))
    AND (@ApplyList = 1 OR @CompIdFilter IS NULL OR U.Comprobante_Id = @CompIdFilter)
    AND (@ApplyList = 1 OR @TipoTrim IS NULL OR U.Tipo = @TipoTrim)
  ORDER BY U.Fecha DESC, U.Comprobante_Id DESC

  OPTION (RECOMPILE); -- enables good plans across very different filter shapes
END

