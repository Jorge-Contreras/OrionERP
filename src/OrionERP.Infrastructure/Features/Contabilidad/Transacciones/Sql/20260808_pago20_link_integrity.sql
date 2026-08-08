SET ANSI_NULLS ON;
GO
SET QUOTED_IDENTIFIER ON;
GO

/* Template context and Pago20 amount sources. */
IF COL_LENGTH(N'dbo.PlantillaContable', N'Contexto') IS NULL
BEGIN
    ALTER TABLE dbo.PlantillaContable
        ADD Contexto varchar(30) NOT NULL
            CONSTRAINT DF_PlantillaContable_Contexto DEFAULT ('TRANSACCION') WITH VALUES;
END;
GO

IF OBJECT_ID(N'dbo.CK_PlantillaContable_Contexto', N'C') IS NULL
BEGIN
    ALTER TABLE dbo.PlantillaContable WITH CHECK
        ADD CONSTRAINT CK_PlantillaContable_Contexto
        CHECK (Contexto IN ('TRANSACCION', 'PAGO20_RECIBIDO', 'PAGO20_EMITIDO'));
END;
GO

IF OBJECT_ID(N'dbo.CK_PlantillaContableLinea_MontoTipo', N'C') IS NOT NULL
BEGIN
    ALTER TABLE dbo.PlantillaContableLinea
        DROP CONSTRAINT CK_PlantillaContableLinea_MontoTipo;
END;
GO

ALTER TABLE dbo.PlantillaContableLinea WITH CHECK
    ADD CONSTRAINT CK_PlantillaContableLinea_MontoTipo CHECK
    (
        MontoTipo IN
        (
            'MONTO_TOTAL',
            'SUBTOTAL_IVA_16',
            'IVA_16',
            'PAGO20_TOTAL_ASIGNADO',
            'PAGO20_SUBTOTAL',
            'PAGO20_TRASLADO_ISR',
            'PAGO20_TRASLADO_IVA',
            'PAGO20_TRASLADO_IEPS',
            'PAGO20_RETENCION_ISR',
            'PAGO20_RETENCION_IVA',
            'PAGO20_RETENCION_IEPS'
        )
    );
GO

/* Recoverable audit for unambiguous legacy type-P migrations. */
IF OBJECT_ID(N'cfdi.Pago20_Link_Migration_Audit', N'U') IS NULL
BEGIN
    CREATE TABLE cfdi.Pago20_Link_Migration_Audit
    (
        MigrationAuditId int IDENTITY(1,1) NOT NULL
            CONSTRAINT PK_Pago20_Link_Migration_Audit PRIMARY KEY,
        MigrationVersion varchar(30) NOT NULL,
        SourceLinkId int NOT NULL,
        TransaccionId int NOT NULL,
        ComprobanteId int NOT NULL,
        DoctoRelacionadoId int NOT NULL,
        OriginalMonto money NOT NULL,
        RequiresAmountReview bit NOT NULL,
        MigratedAtUtc datetime2(0) NOT NULL
            CONSTRAINT DF_Pago20_Link_Migration_Audit_MigratedAtUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT UQ_Pago20_Link_Migration_Audit_SourceLink UNIQUE (SourceLinkId)
    );
END;
GO

SET XACT_ABORT ON;
SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;
BEGIN TRANSACTION;

SELECT
    tc.ID AS SourceLinkId,
    tc.Transaccion_ID AS TransaccionId,
    tc.Comprobante_ID AS ComprobanteId,
    singleDocument.DoctoRelacionadoId,
    tc.Monto AS OriginalMonto
INTO #SafeLegacyPago20Links
FROM dbo.Transaccion_Comprobante AS tc WITH (UPDLOCK, HOLDLOCK)
JOIN cfdi.Comprobante AS c
  ON c.Comprobante_Id = tc.Comprobante_ID
CROSS APPLY
(
    SELECT
        MIN(dr.DoctoRelacionado_Id) AS DoctoRelacionadoId,
        COUNT(*) AS DocumentCount
    FROM cfdi.Pagos20 AS p20
    JOIN cfdi.Pagos20_Pago AS p
      ON p.Pagos20_Id = p20.Pagos20_Id
    JOIN cfdi.Pagos20_DoctoRelacionado AS dr
      ON dr.Pago_Id = p.Pago_Id
    WHERE p20.Comprobante_Id = tc.Comprobante_ID
) AS singleDocument
WHERE c.TipoDeComprobante = 'P'
  AND singleDocument.DocumentCount = 1
  AND NOT EXISTS
  (
      SELECT 1
      FROM dbo.Transaccion_DoctoRelacionado AS canonical
      WHERE canonical.Transaccion_ID = tc.Transaccion_ID
        AND canonical.DoctoRelacionado_Id = singleDocument.DoctoRelacionadoId
  );

INSERT INTO cfdi.Pago20_Link_Migration_Audit
(
    MigrationVersion,
    SourceLinkId,
    TransaccionId,
    ComprobanteId,
    DoctoRelacionadoId,
    OriginalMonto,
    RequiresAmountReview
)
SELECT
    '20260808_pago20_link_integrity',
    source.SourceLinkId,
    source.TransaccionId,
    source.ComprobanteId,
    source.DoctoRelacionadoId,
    source.OriginalMonto,
    CASE WHEN source.OriginalMonto <= 0 THEN 1 ELSE 0 END
FROM #SafeLegacyPago20Links AS source
WHERE NOT EXISTS
(
    SELECT 1
    FROM cfdi.Pago20_Link_Migration_Audit AS audit
    WHERE audit.SourceLinkId = source.SourceLinkId
);

INSERT INTO dbo.Transaccion_DoctoRelacionado
(
    Transaccion_ID,
    DoctoRelacionado_Id,
    Monto
)
SELECT
    source.TransaccionId,
    source.DoctoRelacionadoId,
    source.OriginalMonto
FROM #SafeLegacyPago20Links AS source
WHERE NOT EXISTS
(
    SELECT 1
    FROM dbo.Transaccion_DoctoRelacionado AS canonical
    WHERE canonical.Transaccion_ID = source.TransaccionId
      AND canonical.DoctoRelacionado_Id = source.DoctoRelacionadoId
);

DELETE legacy
FROM dbo.Transaccion_Comprobante AS legacy
JOIN #SafeLegacyPago20Links AS source
  ON source.SourceLinkId = legacy.ID
WHERE EXISTS
(
    SELECT 1
    FROM dbo.Transaccion_DoctoRelacionado AS canonical
    WHERE canonical.Transaccion_ID = source.TransaccionId
      AND canonical.DoctoRelacionado_Id = source.DoctoRelacionadoId
      AND canonical.Monto = source.OriginalMonto
);

COMMIT TRANSACTION;
GO

CREATE OR ALTER TRIGGER dbo.TR_Transaccion_Comprobante_BlockPago20Direct
ON dbo.Transaccion_Comprobante
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;

    IF EXISTS
    (
        SELECT 1
        FROM inserted AS newLink
        JOIN cfdi.Comprobante AS c
          ON c.Comprobante_Id = newLink.Comprobante_ID
        WHERE c.TipoDeComprobante = 'P'
    )
    BEGIN
        THROW 51020, 'Los CFDI tipo P deben ligarse mediante Transaccion_DoctoRelacionado.', 1;
    END;
END;
GO

/* Seed generic received/emitted templates when all account roles exist. */
UPDATE dbo.PlantillaContable
SET Nombre = CASE Contexto
        WHEN 'PAGO20_RECIBIDO' THEN N'Pago20 recibido - generica'
        WHEN 'PAGO20_EMITIDO' THEN N'Pago20 emitido - generica'
        ELSE Nombre
    END,
    Descripcion = N'Plantilla generica Pago20 creada desde las cuentas CFDI configuradas.'
WHERE Origen = N'Pago20Seed'
  AND Contexto IN ('PAGO20_RECIBIDO', 'PAGO20_EMITIDO')
  AND
  (
      Nombre <> CASE Contexto
          WHEN 'PAGO20_RECIBIDO' THEN N'Pago20 recibido - generica'
          WHEN 'PAGO20_EMITIDO' THEN N'Pago20 emitido - generica'
          ELSE Nombre
      END
      OR Descripcion <> N'Plantilla generica Pago20 creada desde las cuentas CFDI configuradas.'
  );

DECLARE @Rfc varchar(50);
DECLARE rfc_cursor CURSOR LOCAL FAST_FORWARD FOR
SELECT defaults.Rfc
FROM dbo.CfdiPolizaCuentaDefault AS defaults
JOIN dbo.CuentasContables AS account
  ON account.id = defaults.CuentaContableId
 AND account.RFC = defaults.Rfc
WHERE defaults.CuentaClave IN
(
    'SUBTOTAL_GASTO', 'SUBTOTAL_INGRESO', 'IVA_TRASLADADO', 'IVA_ACREDITABLE',
    'IEPS_TRASLADADO', 'IEPS_ACREDITABLE', 'RETENCION_IVA', 'RETENCION_ISR',
    'RETENCION_IEPS', 'TOTAL_GASTO', 'TOTAL_INGRESO'
)
GROUP BY defaults.Rfc
HAVING COUNT(DISTINCT defaults.CuentaClave) = 11;

OPEN rfc_cursor;
FETCH NEXT FROM rfc_cursor INTO @Rfc;
WHILE @@FETCH_STATUS = 0
BEGIN
    DECLARE @TemplateContext varchar(30);
    DECLARE @TemplateName nvarchar(200);
    DECLARE @TemplateId int;
    DECLARE @CategoriaId int;

    DECLARE context_cursor CURSOR LOCAL FAST_FORWARD FOR
    SELECT Contexto, Nombre
    FROM (VALUES
        ('PAGO20_RECIBIDO', N'Pago20 recibido - generica'),
        ('PAGO20_EMITIDO', N'Pago20 emitido - generica')
    ) AS contexts(Contexto, Nombre);

    OPEN context_cursor;
    FETCH NEXT FROM context_cursor INTO @TemplateContext, @TemplateName;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        SELECT @TemplateId = PlantillaContableID
        FROM dbo.PlantillaContable
        WHERE Origen = N'Pago20Seed'
          AND RFC = @Rfc
          AND Contexto = @TemplateContext;

        IF @TemplateId IS NULL
        BEGIN
            SELECT @CategoriaId = ISNULL(MAX(CategoriaID), 0) + 1
            FROM dbo.PlantillaContable WITH (UPDLOCK, HOLDLOCK);

            INSERT INTO dbo.PlantillaContable
            (
                Nombre, Descripcion, CategoriaID, RFC, TipoPoliza,
                Contexto, Activa, Origen
            )
            VALUES
            (
                @TemplateName,
                N'Plantilla generica Pago20 creada desde las cuentas CFDI configuradas.',
                @CategoriaId,
                @Rfc,
                NULL,
                @TemplateContext,
                1,
                N'Pago20Seed'
            );

            SET @TemplateId = SCOPE_IDENTITY();
        END;

        IF NOT EXISTS
        (
            SELECT 1
            FROM dbo.PlantillaContableLinea
            WHERE PlantillaContableID = @TemplateId
              AND Activa = 1
        )
        BEGIN
            ;WITH LineDefinitions AS
            (
                SELECT *
                FROM (VALUES
                    ('PAGO20_RECIBIDO', 1, 'SUBTOTAL_GASTO',  'DEBE',  'PAGO20_SUBTOTAL'),
                    ('PAGO20_RECIBIDO', 2, 'IVA_ACREDITABLE', 'DEBE',  'PAGO20_TRASLADO_IVA'),
                    ('PAGO20_RECIBIDO', 3, 'IEPS_ACREDITABLE','DEBE',  'PAGO20_TRASLADO_IEPS'),
                    ('PAGO20_RECIBIDO', 4, 'RETENCION_ISR',   'HABER', 'PAGO20_RETENCION_ISR'),
                    ('PAGO20_RECIBIDO', 5, 'RETENCION_IVA',   'HABER', 'PAGO20_RETENCION_IVA'),
                    ('PAGO20_RECIBIDO', 6, 'RETENCION_IEPS',  'HABER', 'PAGO20_RETENCION_IEPS'),
                    ('PAGO20_RECIBIDO', 7, 'TOTAL_GASTO',     'HABER', 'PAGO20_TOTAL_ASIGNADO'),
                    ('PAGO20_EMITIDO',  1, 'TOTAL_INGRESO',   'DEBE',  'PAGO20_TOTAL_ASIGNADO'),
                    ('PAGO20_EMITIDO',  2, 'SUBTOTAL_INGRESO','HABER', 'PAGO20_SUBTOTAL'),
                    ('PAGO20_EMITIDO',  3, 'IVA_TRASLADADO',  'HABER', 'PAGO20_TRASLADO_IVA'),
                    ('PAGO20_EMITIDO',  4, 'IEPS_TRASLADADO', 'HABER', 'PAGO20_TRASLADO_IEPS'),
                    ('PAGO20_EMITIDO',  5, 'RETENCION_ISR',   'DEBE',  'PAGO20_RETENCION_ISR'),
                    ('PAGO20_EMITIDO',  6, 'RETENCION_IVA',   'DEBE',  'PAGO20_RETENCION_IVA'),
                    ('PAGO20_EMITIDO',  7, 'RETENCION_IEPS',  'DEBE',  'PAGO20_RETENCION_IEPS')
                ) AS definitions(Contexto, Orden, CuentaClave, Naturaleza, MontoTipo)
            )
            INSERT INTO dbo.PlantillaContableLinea
            (
                PlantillaContableID, Orden, CuentaContableID, Naturaleza,
                MontoTipo, Factor, ConceptoTipo, ConceptoFijo, Activa
            )
            SELECT
                @TemplateId,
                definition.Orden,
                defaults.CuentaContableId,
                definition.Naturaleza,
                definition.MontoTipo,
                1,
                'TRANSACCION',
                NULL,
                1
            FROM LineDefinitions AS definition
            JOIN dbo.CfdiPolizaCuentaDefault AS defaults
              ON defaults.Rfc = @Rfc
             AND defaults.CuentaClave = definition.CuentaClave
            WHERE definition.Contexto = @TemplateContext;
        END;

        SET @TemplateId = NULL;
        FETCH NEXT FROM context_cursor INTO @TemplateContext, @TemplateName;
    END;
    CLOSE context_cursor;
    DEALLOCATE context_cursor;

    FETCH NEXT FROM rfc_cursor INTO @Rfc;
END;
CLOSE rfc_cursor;
DEALLOCATE rfc_cursor;

;WITH RequiredAccounts AS
(
    SELECT CuentaClave
    FROM (VALUES
        ('SUBTOTAL_GASTO'), ('SUBTOTAL_INGRESO'), ('IVA_TRASLADADO'), ('IVA_ACREDITABLE'),
        ('IEPS_TRASLADADO'), ('IEPS_ACREDITABLE'), ('RETENCION_IVA'), ('RETENCION_ISR'),
        ('RETENCION_IEPS'), ('TOTAL_GASTO'), ('TOTAL_INGRESO')
    ) AS required(CuentaClave)
),
CompanyRfcs AS
(
    SELECT DISTINCT LTRIM(RTRIM(RFC)) AS Rfc
    FROM dbo.CuentasContables
    WHERE NULLIF(LTRIM(RTRIM(RFC)), '') IS NOT NULL
      AND LEN(LTRIM(RTRIM(RFC))) IN (12, 13)
),
MissingAccounts AS
(
    SELECT company.Rfc, required.CuentaClave
    FROM CompanyRfcs AS company
    CROSS JOIN RequiredAccounts AS required
    WHERE NOT EXISTS
    (
        SELECT 1
        FROM dbo.CfdiPolizaCuentaDefault AS defaults
        JOIN dbo.CuentasContables AS account
          ON account.id = defaults.CuentaContableId
         AND account.RFC = defaults.Rfc
        WHERE defaults.Rfc = company.Rfc
          AND defaults.CuentaClave = required.CuentaClave
    )
)
SELECT
    Rfc,
    COUNT(*) AS MissingAccountCount,
    STRING_AGG(CuentaClave, ', ') WITHIN GROUP (ORDER BY CuentaClave) AS MissingAccountKeys
FROM MissingAccounts
GROUP BY Rfc
ORDER BY Rfc;
GO

/* Canonical transaction summary: Pago20 output is limited to exact linked documents. */
CREATE OR ALTER PROCEDURE [cfdi].[Transaccion_CFDI_Vinculados_Resumen]
    @Transaccion_ID int,
    @Tolerancia decimal(19,4) = 1.00
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @ContextRfc varchar(50), @TransaccionMonto decimal(19,4);
    SELECT
        @ContextRfc = NULLIF(LTRIM(RTRIM(RFC)), ''),
        @TransaccionMonto = CAST(Monto AS decimal(19,4))
    FROM dbo.Transacciones
    WHERE ID = @Transaccion_ID;

    SELECT
        tc.Comprobante_ID AS ComprobanteId,
        tc.Transaccion_ID AS TransaccionId,
        c.Fecha AS CfdiFecha,
        t.Fecha,
        t.Concepto,
        CAST(t.Monto AS decimal(19,4)) AS TransaccionMonto,
        CAST(tc.Monto AS decimal(19,4)) AS MontoAsignado,
        t.Tipo_Poliza AS TipoPoliza,
        t.Forma_Pago AS FormaPago,
        c.TipoDeComprobante AS Tipo,
        c.Serie,
        c.Folio,
        cd.RFC_EMISOR AS EmisorRfc,
        cd.RFC_RECEPTOR AS ReceptorRfc,
        CASE WHEN cd.RFC_EMISOR = @ContextRfc THEN 'Emitido'
             WHEN cd.RFC_RECEPTOR = @ContextRfc THEN 'Recibido'
             ELSE 'Otro' END AS Direccion,
        cd.FOLIO_FISCAL AS Uuid,
        cd.FormaPago AS CfdiFormaPago,
        cd.MetodoPago,
        cd.UsoCFDI AS UsoCfdi,
        CAST(cd.SubTotal AS decimal(19,4)) AS SubTotal,
        CAST(cd.Total AS decimal(19,4)) AS Total,
        CAST(cd.IVA AS decimal(19,4)) AS Iva,
        CAST(cd.IVA_RETENIDO AS decimal(19,4)) AS IvaRetenido,
        CAST(cd.ISR_RETENIDO AS decimal(19,4)) AS IsrRetenido,
        cd.XML_Attachment_ID AS XmlAttachmentId,
        CAST(CASE WHEN cd.Total <> 0 THEN tc.Monto / cd.Total ELSE 0 END AS decimal(19,8)) AS ProporcionCfdi,
        CAST(CASE WHEN cd.Total <> 0 THEN cd.IVA * (tc.Monto / cd.Total) ELSE 0 END AS decimal(19,4)) AS IvaEsperado,
        CAST(CASE
            WHEN cd.RFC_EMISOR = t.RFC AND c.TipoDeComprobante = 'E' THEN ISNULL(iva208.Debe,0) - ISNULL(iva208.Haber,0)
            WHEN cd.RFC_EMISOR = t.RFC THEN ISNULL(iva208.Haber,0) - ISNULL(iva208.Debe,0)
            WHEN cd.RFC_RECEPTOR = t.RFC AND c.TipoDeComprobante = 'E' THEN ISNULL(iva118.Haber,0) - ISNULL(iva118.Debe,0)
            WHEN cd.RFC_RECEPTOR = t.RFC THEN ISNULL(iva118.Debe,0) - ISNULL(iva118.Haber,0)
            ELSE 0 END AS decimal(19,4)) AS IvaContable
    INTO #RegularLinks
    FROM dbo.Transaccion_Comprobante AS tc
    JOIN dbo.Transacciones AS t ON t.ID = tc.Transaccion_ID
    JOIN cfdi.Comprobante AS c ON c.Comprobante_Id = tc.Comprobante_ID
    JOIN cfdi.Comprobante_Detalle AS cd ON cd.Comprobante_Id = c.Comprobante_Id
    OUTER APPLY (SELECT SUM(Debe) AS Debe, SUM(Haber) AS Haber FROM dbo.Registro_Contable WHERE TransaccionID=t.ID AND Nivel1='208') AS iva208
    OUTER APPLY (SELECT SUM(Debe) AS Debe, SUM(Haber) AS Haber FROM dbo.Registro_Contable WHERE TransaccionID=t.ID AND Nivel1='118') AS iva118
    WHERE c.TipoDeComprobante IN ('I','N','E')
      AND EXISTS
      (
          SELECT 1
          FROM dbo.Transaccion_Comprobante AS currentLink
          WHERE currentLink.Transaccion_ID = @Transaccion_ID
            AND currentLink.Comprobante_ID = tc.Comprobante_ID
      );

    SELECT
        regular.ComprobanteId,
        MAX(regular.CfdiFecha) AS Fecha,
        MAX(regular.Tipo) AS Tipo,
        MAX(regular.Serie) AS Serie,
        MAX(regular.Folio) AS Folio,
        MAX(regular.EmisorRfc) AS EmisorRfc,
        MAX(regular.ReceptorRfc) AS ReceptorRfc,
        MAX(regular.Direccion) AS Direccion,
        MAX(regular.Uuid) AS Uuid,
        MAX(regular.CfdiFormaPago) AS FormaPago,
        MAX(regular.MetodoPago) AS MetodoPago,
        MAX(regular.UsoCfdi) AS UsoCfdi,
        MAX(concepts.Conceptos) AS Conceptos,
        MAX(regular.SubTotal) AS SubTotal,
        MAX(regular.Total) AS Total,
        MAX(regular.Iva) AS Iva,
        MAX(regular.IvaRetenido) AS IvaRetenido,
        MAX(regular.IsrRetenido) AS IsrRetenido,
        SUM(regular.MontoAsignado) AS AsignadoCfdi,
        ISNULL(@TransaccionMonto,0) AS TransaccionMonto,
        MAX(currentAssignment.TransaccionAsignado) AS TransaccionAsignado,
        SUM(regular.IvaEsperado) AS IvaEsperado,
        SUM(regular.IvaContable) AS IvaContable,
        CAST(SUM(regular.IvaEsperado)-SUM(regular.IvaContable) AS decimal(19,4)) AS IvaDiferencia,
        CASE WHEN ABS(MAX(regular.Total)-SUM(regular.MontoAsignado)) <= @Tolerancia THEN 'OK' ELSE 'DIFERENCIA' END AS TotalCfdiStatus,
        CASE WHEN ABS(ABS(ISNULL(@TransaccionMonto,0))-MAX(currentAssignment.TransaccionAsignado)) <= @Tolerancia THEN 'OK' ELSE 'DIFERENCIA' END AS TransaccionAsignacionStatus,
        CASE WHEN ABS(SUM(regular.IvaEsperado)-SUM(regular.IvaContable)) <= @Tolerancia THEN 'OK' ELSE 'DIFERENCIA' END AS IvaStatus,
        COUNT(DISTINCT regular.TransaccionId) AS PolizasCount,
        MAX(regular.XmlAttachmentId) AS XmlAttachmentId
    FROM #RegularLinks AS regular
    OUTER APPLY
    (
        SELECT STRING_AGG(CONVERT(varchar(max), concepto.Descripcion), ', ') AS Conceptos
        FROM cfdi.Conceptos AS conceptos
        JOIN cfdi.Concepto AS concepto ON concepto.Conceptos_Id = conceptos.Conceptos_Id
        WHERE conceptos.Comprobante_Id = regular.ComprobanteId
    ) AS concepts
    OUTER APPLY
    (
        SELECT CAST(ISNULL(SUM(Monto),0) AS decimal(19,4)) AS TransaccionAsignado
        FROM dbo.Transaccion_Comprobante AS currentRegular
        JOIN cfdi.Comprobante AS currentCfdi ON currentCfdi.Comprobante_Id=currentRegular.Comprobante_ID
        WHERE currentRegular.Transaccion_ID=@Transaccion_ID AND currentCfdi.TipoDeComprobante IN ('I','N','E')
    ) AS currentAssignment
    GROUP BY regular.ComprobanteId
    ORDER BY MAX(regular.CfdiFecha) DESC, regular.ComprobanteId DESC;

    SELECT
        ComprobanteId, TransaccionId, Fecha, Concepto, TransaccionMonto, MontoAsignado,
        TipoPoliza, FormaPago, ProporcionCfdi, IvaEsperado, IvaContable,
        CAST(IvaEsperado-IvaContable AS decimal(19,4)) AS IvaDiferencia,
        CASE WHEN Direccion='Emitido' THEN '208' WHEN Direccion='Recibido' THEN '118' ELSE NULL END AS IvaCuentaNivel1,
        CASE WHEN ABS(IvaEsperado-IvaContable)<=@Tolerancia THEN 'OK' ELSE 'DIFERENCIA' END AS IvaStatus
    FROM #RegularLinks
    ORDER BY ComprobanteId, Fecha, TransaccionId;

    SELECT
        p20.Comprobante_Id AS ComprobanteId,
        p.Pago_Id AS PagoId,
        dr.DoctoRelacionado_Id AS DoctoRelacionadoId,
        dr.IdDocumento AS UuidDoctoRelacionado,
        dr.Folio,
        dr.NumParcialidad,
        dr.MonedaDR AS MonedaDr,
        p.MonedaP,
        p.FechaPago,
        p.FormaDePagoP,
        CAST(p.Monto AS decimal(19,4)) AS MontoPago,
        CAST(ISNULL(dr.ImpSaldoAnt,0) AS decimal(19,4)) AS ImpSaldoAnt,
        CAST(dr.ImpPagado AS decimal(19,4)) AS ImpPagado,
        CAST(dr.ImpSaldoInsoluto AS decimal(19,4)) AS ImpSaldoInsoluto,
        CAST(td.Monto AS decimal(19,4)) AS MontoAsignado,
        CAST(ISNULL(taxes.CompIva,0) AS decimal(19,4)) AS CompIva,
        CAST(CASE WHEN dr.ImpPagado<>0 THEN ISNULL(taxes.CompIva,0)*(td.Monto/dr.ImpPagado) ELSE 0 END AS decimal(19,4)) AS IvaEsperado,
        cd.RFC_EMISOR AS EmisorRfc,
        cd.RFC_RECEPTOR AS ReceptorRfc,
        CASE WHEN cd.RFC_EMISOR=@ContextRfc THEN 'Emitido'
             WHEN cd.RFC_RECEPTOR=@ContextRfc THEN 'Recibido'
             ELSE 'Otro' END AS Direccion,
        tfd.UUID AS ComprobanteUuid,
        cd.XML_Attachment_ID AS XmlAttachmentId
    INTO #CurrentPagoDocs
    FROM dbo.Transaccion_DoctoRelacionado AS td
    JOIN cfdi.Pagos20_DoctoRelacionado AS dr ON dr.DoctoRelacionado_Id=td.DoctoRelacionado_Id
    JOIN cfdi.Pagos20_Pago AS p ON p.Pago_Id=dr.Pago_Id
    JOIN cfdi.Pagos20 AS p20 ON p20.Pagos20_Id=p.Pagos20_Id
    JOIN cfdi.Comprobante AS c ON c.Comprobante_Id=p20.Comprobante_Id AND c.TipoDeComprobante='P'
    JOIN cfdi.Comprobante_Detalle AS cd ON cd.Comprobante_Id=c.Comprobante_Id
    LEFT JOIN cfdi.TimbreFiscalDigital AS tfd ON tfd.Comprobante_Id=c.Comprobante_Id
    OUTER APPLY
    (
        SELECT SUM(CASE WHEN traslado.ImpuestoDR='002' THEN ISNULL(traslado.ImporteDR,0) ELSE 0 END) AS CompIva
        FROM cfdi.Pagos20_TrasladoDR AS traslado
        WHERE traslado.DoctoRelacionado_Id=dr.DoctoRelacionado_Id
    ) AS taxes
    WHERE td.Transaccion_ID=@Transaccion_ID;

    DECLARE @Pago20IvaContable decimal(19,4) = 0;
    IF EXISTS (SELECT 1 FROM #CurrentPagoDocs WHERE Direccion='Recibido')
        SELECT @Pago20IvaContable=CAST(ISNULL(SUM(Debe),0)-ISNULL(SUM(Haber),0) AS decimal(19,4)) FROM dbo.Registro_Contable WHERE TransaccionID=@Transaccion_ID AND Nivel1='118';
    ELSE IF EXISTS (SELECT 1 FROM #CurrentPagoDocs WHERE Direccion='Emitido')
        SELECT @Pago20IvaContable=CAST(ISNULL(SUM(Haber),0)-ISNULL(SUM(Debe),0) AS decimal(19,4)) FROM dbo.Registro_Contable WHERE TransaccionID=@Transaccion_ID AND Nivel1='208';

    DECLARE @Pago20IvaEsperadoTotal decimal(19,4) = (SELECT ISNULL(SUM(IvaEsperado),0) FROM #CurrentPagoDocs);

    SELECT
        docs.ComprobanteId,
        MAX(docs.ComprobanteUuid) AS ComprobanteUuid,
        MAX(docs.EmisorRfc) AS EmisorRfc,
        MAX(docs.ReceptorRfc) AS ReceptorRfc,
        MAX(docs.Direccion) AS Direccion,
        MIN(docs.FechaPago) AS FechaPago,
        MAX(docs.FormaDePagoP) AS FormaDePagoP,
        MAX(docs.MonedaP) AS MonedaP,
        MAX(paymentTotals.MontoPago) AS MontoPago,
        SUM(docs.ImpPagado) AS ImpPagado,
        SUM(docs.MontoAsignado) AS MontoAsignado,
        SUM(docs.IvaEsperado) AS CompIva,
        CAST(CASE WHEN @Pago20IvaEsperadoTotal<>0 THEN @Pago20IvaContable*(SUM(docs.IvaEsperado)/@Pago20IvaEsperadoTotal) ELSE 0 END AS decimal(19,4)) AS IvaContable,
        CAST(SUM(docs.IvaEsperado)-CASE WHEN @Pago20IvaEsperadoTotal<>0 THEN @Pago20IvaContable*(SUM(docs.IvaEsperado)/@Pago20IvaEsperadoTotal) ELSE 0 END AS decimal(19,4)) AS IvaDiferencia,
        CASE WHEN ABS(SUM(docs.IvaEsperado)-CASE WHEN @Pago20IvaEsperadoTotal<>0 THEN @Pago20IvaContable*(SUM(docs.IvaEsperado)/@Pago20IvaEsperadoTotal) ELSE 0 END)<=@Tolerancia THEN 'OK' ELSE 'DIFERENCIA' END AS IvaStatus,
        MAX(linkCounts.PolizasCount) AS PolizasCount,
        COUNT(*) AS RelatedDocumentsCount,
        MAX(docs.XmlAttachmentId) AS XmlAttachmentId
    FROM #CurrentPagoDocs AS docs
    OUTER APPLY
    (
        SELECT CAST(ISNULL(SUM(p.Monto),0) AS decimal(19,4)) AS MontoPago
        FROM cfdi.Pagos20 AS p20
        JOIN cfdi.Pagos20_Pago AS p ON p.Pagos20_Id=p20.Pagos20_Id
        WHERE p20.Comprobante_Id=docs.ComprobanteId
    ) AS paymentTotals
    OUTER APPLY
    (
        SELECT COUNT(DISTINCT links.Transaccion_ID) AS PolizasCount
        FROM dbo.Transaccion_DoctoRelacionado AS links
        WHERE links.DoctoRelacionado_Id IN (SELECT DoctoRelacionadoId FROM #CurrentPagoDocs WHERE ComprobanteId=docs.ComprobanteId)
    ) AS linkCounts
    GROUP BY docs.ComprobanteId
    ORDER BY MIN(docs.FechaPago) DESC, docs.ComprobanteId DESC;

    SELECT
        docs.ComprobanteId, docs.PagoId, docs.DoctoRelacionadoId, docs.UuidDoctoRelacionado,
        docs.Folio, docs.NumParcialidad, docs.MonedaDr, docs.MonedaP, docs.FechaPago,
        docs.FormaDePagoP, docs.MontoPago, docs.ImpSaldoAnt, docs.ImpPagado,
        docs.ImpSaldoInsoluto, docs.CompIva, docs.MontoAsignado, docs.IvaEsperado,
        linkCounts.PolizasCount
    FROM #CurrentPagoDocs AS docs
    OUTER APPLY
    (
        SELECT COUNT(*) AS PolizasCount
        FROM dbo.Transaccion_DoctoRelacionado
        WHERE DoctoRelacionado_Id=docs.DoctoRelacionadoId
    ) AS linkCounts
    ORDER BY docs.ComprobanteId, docs.DoctoRelacionadoId;

    SELECT
        docs.ComprobanteId,
        docs.DoctoRelacionadoId,
        linked.Transaccion_ID AS TransaccionId,
        transactionRow.Fecha,
        transactionRow.Concepto,
        CAST(transactionRow.Monto AS decimal(19,4)) AS TransaccionMonto,
        CAST(linked.Monto AS decimal(19,4)) AS MontoAsignado,
        transactionRow.Tipo_Poliza AS TipoPoliza,
        transactionRow.Forma_Pago AS FormaPago,
        CAST(0 AS decimal(19,8)) AS ProporcionCfdi,
        CAST(CASE WHEN docs.ImpPagado<>0 THEN docs.CompIva*(linked.Monto/docs.ImpPagado) ELSE 0 END AS decimal(19,4)) AS IvaEsperado,
        CAST(0 AS decimal(19,4)) AS IvaContable,
        CAST(CASE WHEN docs.ImpPagado<>0 THEN docs.CompIva*(linked.Monto/docs.ImpPagado) ELSE 0 END AS decimal(19,4)) AS IvaDiferencia,
        CASE WHEN docs.Direccion='Emitido' THEN '208' WHEN docs.Direccion='Recibido' THEN '118' ELSE NULL END AS IvaCuentaNivel1,
        'NA' AS IvaStatus
    FROM #CurrentPagoDocs AS docs
    JOIN dbo.Transaccion_DoctoRelacionado AS linked ON linked.DoctoRelacionado_Id=docs.DoctoRelacionadoId
    JOIN dbo.Transacciones AS transactionRow ON transactionRow.ID=linked.Transaccion_ID
    ORDER BY docs.ComprobanteId, docs.DoctoRelacionadoId, transactionRow.Fecha, linked.Transaccion_ID;

    SELECT
        tc.Comprobante_ID AS ComprobanteId,
        tfd.UUID AS ComprobanteUuid,
        cd.RFC_EMISOR AS EmisorRfc,
        cd.RFC_RECEPTOR AS ReceptorRfc,
        CAST(tc.Monto AS decimal(19,4)) AS MontoAsignado,
        documentCount.RelatedDocumentsCount,
        CASE
            WHEN documentCount.RelatedDocumentsCount=0 THEN 'SIN_DOCUMENTO_RELACIONADO'
            WHEN documentCount.RelatedDocumentsCount>1 THEN 'MULTIPLES_DOCUMENTOS_RELACIONADOS'
            WHEN tc.Monto<=0 THEN 'MONTO_REQUIERE_REVISION'
            ELSE 'PENDIENTE_DE_MIGRACION'
        END AS LegacyReason,
        cd.XML_Attachment_ID AS XmlAttachmentId
    FROM dbo.Transaccion_Comprobante AS tc
    JOIN cfdi.Comprobante AS c ON c.Comprobante_Id=tc.Comprobante_ID AND c.TipoDeComprobante='P'
    JOIN cfdi.Comprobante_Detalle AS cd ON cd.Comprobante_Id=c.Comprobante_Id
    LEFT JOIN cfdi.TimbreFiscalDigital AS tfd ON tfd.Comprobante_Id=c.Comprobante_Id
    OUTER APPLY
    (
        SELECT COUNT(*) AS RelatedDocumentsCount
        FROM cfdi.Pagos20 AS p20
        JOIN cfdi.Pagos20_Pago AS p ON p.Pagos20_Id=p20.Pagos20_Id
        JOIN cfdi.Pagos20_DoctoRelacionado AS dr ON dr.Pago_Id=p.Pago_Id
        WHERE p20.Comprobante_Id=tc.Comprobante_ID
    ) AS documentCount
    WHERE tc.Transaccion_ID=@Transaccion_ID
    ORDER BY tc.Comprobante_ID;
END;
GO
