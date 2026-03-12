
CREATE PROCEDURE [cfdi].[PROCESAR_SAT_XML_V2]
    @TransaccionID INT,
    @AttachmentID  INT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRY
        BEGIN TRAN;

        ----------------------------------------------------------------------
        -- 0) Cargar y sanear XML
        ----------------------------------------------------------------------
        DECLARE
            @varXML        VARCHAR(MAX),
            @startIndex    INT,
            @x             XML,
            @UUID          VARCHAR(50),
            @ComprobanteID INT = NULL;

        SELECT
            @varXML = CAST(a.attachment AS VARCHAR(MAX)) COLLATE Modern_Spanish_CI_AS
        FROM dbo.transaction_attachment AS a
        WHERE a.id = @AttachmentID;

        IF @varXML IS NULL
            THROW 51010, 'No se encontró el adjunto para @AttachmentID.', 1;

        -- Quitar ruido antes del primer '<'
        SET @startIndex = CHARINDEX('<', @varXML);

        IF @startIndex > 1
            SET @varXML = SUBSTRING(@varXML, @startIndex, LEN(@varXML));

        -- Normalizador 3.3 -> 4.0 (fallback)
        SET @varXML = REPLACE(@varXML, 'http://www.sat.gob.mx/cfd/3', 'http://www.sat.gob.mx/cfd/4');

        SET @varXML = REPLACE(
            @varXML,
            'http://www.sat.gob.mx/cfd/3 http://www.sat.gob.mx/sitio_internet/cfd/3/cfdv33.xsd',
            'http://www.sat.gob.mx/cfd/4 http://www.sat.gob.mx/sitio_internet/cfd/4/cfdv40.xsd'
        );

        SET @x = TRY_CAST(@varXML AS XML);

        IF @x IS NULL
            THROW 51000, 'El archivo adjunto no es un XML válido.', 1;

        ----------------------------------------------------------------------
        -- 1) UUID (TimbreFiscalDigital)
        -- Más robusto usando local-name()
        ----------------------------------------------------------------------
        SELECT
            @UUID = T.value('@UUID', 'varchar(50)')
        FROM @x.nodes('/*[local-name()="Comprobante"]/*[local-name()="Complemento"]/*[local-name()="TimbreFiscalDigital"]') AS X(T);

        IF @UUID IS NULL
            THROW 51001, 'El XML no contiene TimbreFiscalDigital con UUID.', 1;

        ----------------------------------------------------------------------
        -- 2) Buscar Comprobante existente por UUID
        ----------------------------------------------------------------------
        SELECT TOP (1)
            @ComprobanteID = c.Comprobante_Id
        FROM cfdi.TimbreFiscalDigital t
        JOIN cfdi.Comprobante c
            ON c.Comprobante_Id = t.Comprobante_Id
        WHERE t.UUID = @UUID;

        ----------------------------------------------------------------------
        -- 3) Clasificar INGRESO / GASTO por la transacción
        ----------------------------------------------------------------------
        DECLARE @Tipo_Comprobante VARCHAR(10) =
        (
            SELECT
                CASE WHEN monto > 0 THEN 'INGRESO' ELSE 'GASTO' END
            FROM dbo.Transacciones
            WHERE ID = @TransaccionID
        );

        IF @Tipo_Comprobante IS NULL
            SET @Tipo_Comprobante = 'INGRESO';

        ----------------------------------------------------------------------
        -- 4) Atributos de Comprobante
        ----------------------------------------------------------------------
        DECLARE
            @Version           VARCHAR(5),
            @Serie             VARCHAR(25),
            @Folio             VARCHAR(40),
            @Fecha             DATETIME2(0),
            @Sello             NVARCHAR(MAX),
            @FormaPago         VARCHAR(3),
            @NoCertificado     VARCHAR(30),
            @Certificado       NVARCHAR(MAX),
            @Condiciones       VARCHAR(1000),
            @SubTotal          DECIMAL(18, 6),
            @Descuento         DECIMAL(18, 6),
            @Moneda            CHAR(3),
            @TipoCambio        DECIMAL(18, 6),
            @Total             DECIMAL(18, 6),
            @TipoDeComprobante CHAR(1),
            @Exportacion       CHAR(2),
            @MetodoPago        VARCHAR(3),
            @LugarExpedicion   VARCHAR(10),
            @Confirmacion      VARCHAR(5);

        ;WITH XMLNAMESPACES ('http://www.sat.gob.mx/cfd/4' AS cfdi)
        SELECT
            @Version           = C.value('@Version', 'varchar(5)'),
            @Serie             = C.value('@Serie', 'varchar(25)'),
            @Folio             = C.value('@Folio', 'varchar(40)'),
            @Fecha             = C.value('@Fecha', 'datetime2(0)'),
            @Sello             = C.value('@Sello', 'nvarchar(max)'),
            @FormaPago         = C.value('@FormaPago', 'varchar(3)'),
            @NoCertificado     = C.value('@NoCertificado', 'varchar(30)'),
            @Certificado       = C.value('@Certificado', 'nvarchar(max)'),
            @Condiciones       = C.value('@CondicionesDePago', 'varchar(1000)'),
            @SubTotal          = C.value('@SubTotal', 'decimal(18,6)'),
            @Descuento         = C.value('@Descuento', 'decimal(18,6)'),
            @Moneda            = C.value('@Moneda', 'char(3)'),
            @TipoCambio        = C.value('@TipoCambio', 'decimal(18,6)'),
            @Total             = C.value('@Total', 'decimal(18,6)'),
            @TipoDeComprobante = C.value('@TipoDeComprobante', 'char(1)'),
            @Exportacion       = C.value('@Exportacion', 'char(2)'),
            @MetodoPago        = C.value('@MetodoPago', 'varchar(3)'),
            @LugarExpedicion   = C.value('@LugarExpedicion', 'varchar(10)'),
            @Confirmacion      = C.value('@Confirmacion', 'varchar(5)')
        FROM @x.nodes('/cfdi:Comprobante') AS N(C);

        ----------------------------------------------------------------------
        -- 5) Upsert Comprobante + limpieza de hijos si existía
        ----------------------------------------------------------------------
        IF @ComprobanteID IS NULL
        BEGIN
            INSERT INTO cfdi.Comprobante
            (
                version, serie, folio, fecha, sello, formapago, nocertificado, certificado,
                condicionesdepago, subtotal, descuento, moneda, tipocambio, total,
                tipodecomprobante, exportacion, metodopago, lugarexpedicion, confirmacion,
                Tipo_Comprobante, XML_Attachment_ID
            )
            VALUES
            (
                @Version, @Serie, @Folio, @Fecha, @Sello, @FormaPago, @NoCertificado, @Certificado,
                @Condiciones, @SubTotal, @Descuento, @Moneda, @TipoCambio, @Total,
                @TipoDeComprobante, @Exportacion, @MetodoPago, @LugarExpedicion, @Confirmacion,
                @Tipo_Comprobante, @AttachmentID
            );

            SET @ComprobanteID = SCOPE_IDENTITY();
        END
        ELSE
        BEGIN
            ------------------------------------------------------------------
            -- Limpiar hijos (reinsertaremos todo)
            -- Pagos20 (en orden inverso de FK)
            ------------------------------------------------------------------
            -- 5.1) Transaccion_DoctoRelacionado (si existe)
            IF OBJECT_ID(N'dbo.Transaccion_DoctoRelacionado', 'U') IS NOT NULL
            BEGIN
                DELETE td
                FROM dbo.Transaccion_DoctoRelacionado AS td
                JOIN cfdi.Pagos20_DoctoRelacionado AS dr
                    ON dr.DoctoRelacionado_Id = td.DoctoRelacionado_Id
                JOIN cfdi.Pagos20_Pago AS pp
                    ON pp.Pago_Id = dr.Pago_Id
                JOIN cfdi.Pagos20 AS p20
                    ON p20.Pagos20_Id = pp.Pagos20_Id
                WHERE p20.Comprobante_Id = @ComprobanteID;
            END

            -- 5.2) Impuestos DR (Retenciones / Traslados)
            DELETE p20rdr
            FROM cfdi.Pagos20_RetencionDR AS p20rdr
            JOIN cfdi.Pagos20_DoctoRelacionado AS dr
                ON dr.DoctoRelacionado_Id = p20rdr.DoctoRelacionado_Id
            JOIN cfdi.Pagos20_Pago AS pp
                ON pp.Pago_Id = dr.Pago_Id
            JOIN cfdi.Pagos20 AS p20
                ON p20.Pagos20_Id = pp.Pagos20_Id
            WHERE p20.Comprobante_Id = @ComprobanteID;

            DELETE p20tdr
            FROM cfdi.Pagos20_TrasladoDR AS p20tdr
            JOIN cfdi.Pagos20_DoctoRelacionado AS dr
                ON dr.DoctoRelacionado_Id = p20tdr.DoctoRelacionado_Id
            JOIN cfdi.Pagos20_Pago AS pp
                ON pp.Pago_Id = dr.Pago_Id
            JOIN cfdi.Pagos20 AS p20
                ON p20.Pagos20_Id = pp.Pagos20_Id
            WHERE p20.Comprobante_Id = @ComprobanteID;

            -- 5.3) Impuestos a nivel Pago (Retenciones / Traslados)
            DELETE p20rp
            FROM cfdi.Pagos20_RetencionP AS p20rp
            JOIN cfdi.Pagos20_Pago AS pp
                ON pp.Pago_Id = p20rp.Pago_Id
            JOIN cfdi.Pagos20 AS p20
                ON p20.Pagos20_Id = pp.Pagos20_Id
            WHERE p20.Comprobante_Id = @ComprobanteID;

            DELETE p20tp
            FROM cfdi.Pagos20_TrasladoP AS p20tp
            JOIN cfdi.Pagos20_Pago AS pp
                ON pp.Pago_Id = p20tp.Pago_Id
            JOIN cfdi.Pagos20 AS p20
                ON p20.Pagos20_Id = pp.Pagos20_Id
            WHERE p20.Comprobante_Id = @ComprobanteID;

            -- 5.4) Doctos relacionados
            DELETE dr
            FROM cfdi.Pagos20_DoctoRelacionado AS dr
            JOIN cfdi.Pagos20_Pago AS pp
                ON pp.Pago_Id = dr.Pago_Id
            JOIN cfdi.Pagos20 AS p20
                ON p20.Pagos20_Id = pp.Pagos20_Id
            WHERE p20.Comprobante_Id = @ComprobanteID;

            -- 5.5) Totales, Pagos y Pagos20
            DELETE p20t
            FROM cfdi.Pagos20_Totales AS p20t
            JOIN cfdi.Pagos20 AS p20
                ON p20.Pagos20_Id = p20t.Pagos20_Id
            WHERE p20.Comprobante_Id = @ComprobanteID;

            DELETE pp
            FROM cfdi.Pagos20_Pago AS pp
            JOIN cfdi.Pagos20 AS p20
                ON p20.Pagos20_Id = pp.Pagos20_Id
            WHERE p20.Comprobante_Id = @ComprobanteID;

            DELETE p20
            FROM cfdi.Pagos20 AS p20
            WHERE p20.Comprobante_Id = @ComprobanteID;

            ------------------------------------------------------------------
            -- Conceptos/Impuestos
            ------------------------------------------------------------------
            DELETE trl
            FROM cfdi.traslado trl
            WHERE EXISTS
            (
                SELECT 1
                FROM cfdi.traslados tr
                JOIN cfdi.impuestos i
                    ON i.impuestos_id = tr.impuestos_id
                WHERE trl.traslados_id = tr.traslados_id
                  AND i.Comprobante_Id = @ComprobanteID
            );

            DELETE rdet
            FROM cfdi.retencion rdet
            WHERE EXISTS
            (
                SELECT 1
                FROM cfdi.retenciones r
                JOIN cfdi.impuestos i
                    ON i.impuestos_id = r.impuestos_id
                WHERE rdet.retenciones_id = r.retenciones_id
                  AND i.Comprobante_Id = @ComprobanteID
            );

            DELETE r
            FROM cfdi.retenciones r
            WHERE EXISTS
            (
                SELECT 1
                FROM cfdi.impuestos i
                WHERE r.impuestos_id = i.impuestos_id
                  AND i.Comprobante_Id = @ComprobanteID
            );

            DELETE t
            FROM cfdi.traslados t
            WHERE EXISTS
            (
                SELECT 1
                FROM cfdi.impuestos i
                WHERE t.impuestos_id = i.impuestos_id
                  AND i.Comprobante_Id = @ComprobanteID
            );

            DELETE i
            FROM cfdi.impuestos i
            WHERE i.Comprobante_Id = @ComprobanteID;

            DELETE cpto
            FROM cfdi.concepto cpto
            WHERE EXISTS
            (
                SELECT 1
                FROM cfdi.conceptos cs
                WHERE cpto.conceptos_id = cs.conceptos_id
                  AND cs.Comprobante_Id = @ComprobanteID
            );

            DELETE cs
            FROM cfdi.conceptos cs
            WHERE cs.Comprobante_Id = @ComprobanteID;

            ------------------------------------------------------------------
            -- Otros hijos simples
            ------------------------------------------------------------------
            DELETE FROM cfdi.InformacionGlobal
            WHERE Comprobante_Id = @ComprobanteID;

            DELETE FROM cfdi.TimbreFiscalDigital
            WHERE Comprobante_Id = @ComprobanteID;

            DELETE FROM cfdi.Emisor
            WHERE Comprobante_Id = @ComprobanteID;

            DELETE FROM cfdi.Receptor
            WHERE Comprobante_Id = @ComprobanteID;

            ------------------------------------------------------------------
            -- Actualizar encabezado
            ------------------------------------------------------------------
            UPDATE cfdi.Comprobante
            SET
                version          = @Version,
                serie            = @Serie,
                folio            = @Folio,
                fecha            = @Fecha,
                sello            = @Sello,
                formapago        = @FormaPago,
                nocertificado    = @NoCertificado,
                certificado      = @Certificado,
                condicionesdepago= @Condiciones,
                subtotal         = @SubTotal,
                descuento        = @Descuento,
                moneda           = @Moneda,
                tipocambio       = @TipoCambio,
                total            = @Total,
                tipodecomprobante= @TipoDeComprobante,
                exportacion      = @Exportacion,
                metodopago       = @MetodoPago,
                lugarexpedicion  = @LugarExpedicion,
                confirmacion     = @Confirmacion,
                Tipo_Comprobante = @Tipo_Comprobante,
                XML_Attachment_ID= ISNULL(XML_Attachment_ID, @AttachmentID)
            WHERE Comprobante_Id = @ComprobanteID;
        END

        ----------------------------------------------------------------------
        -- 6) TimbreFiscalDigital
        -- (usa local-name para tolerar variaciones)
        ----------------------------------------------------------------------
        INSERT INTO cfdi.TimbreFiscalDigital
        (
            Version, UUID, FechaTimbrado, RfcProvCertif, Leyenda, SelloCFD,
            NoCertificadoSAT, SelloSAT, Comprobante_Id
        )
        SELECT
            T.value('@Version', 'varchar(10)'),
            T.value('@UUID', 'varchar(50)'),
            T.value('@FechaTimbrado', 'datetime2(0)'),
            T.value('@RfcProvCertif', 'varchar(20)'),
            T.value('@Leyenda', 'varchar(150)'),
            T.value('@SelloCFD', 'nvarchar(max)'),
            T.value('@NoCertificadoSAT', 'varchar(30)'),
            T.value('@SelloSAT', 'nvarchar(max)'),
            @ComprobanteID
        FROM @x.nodes('/*[local-name()="Comprobante"]/*[local-name()="Complemento"]/*[local-name()="TimbreFiscalDigital"]') AS X(T);

        ----------------------------------------------------------------------
        -- 7) Relación con la Transacción (upsert)
        ----------------------------------------------------------------------
        IF NOT EXISTS
        (
            SELECT 1
            FROM dbo.transaccion_comprobante
            WHERE comprobante_id = @ComprobanteID
              AND transaccion_id = @TransaccionID
        )
        BEGIN
            INSERT INTO dbo.transaccion_comprobante (comprobante_id, transaccion_id, Monto)
            VALUES (@ComprobanteID, @TransaccionID, @Total);
        END

        ----------------------------------------------------------------------
        -- 8) Emisor / Receptor / Información Global
        ----------------------------------------------------------------------
        ;WITH XMLNAMESPACES ('http://www.sat.gob.mx/cfd/4' AS cfdi)
        INSERT INTO cfdi.Emisor (rfc, nombre, regimenfiscal, facatradquirente, comprobante_id)
        SELECT
            E.value('@Rfc', 'varchar(13)'),
            E.value('@Nombre', 'varchar(300)'),
            E.value('@RegimenFiscal', 'varchar(3)'),
            E.value('@FacAtrAdquirente', 'varchar(20)'),
            @ComprobanteID
        FROM @x.nodes('/cfdi:Comprobante/cfdi:Emisor') AS X(E);

        ;WITH XMLNAMESPACES ('http://www.sat.gob.mx/cfd/4' AS cfdi)
        INSERT INTO cfdi.Receptor
        (
            rfc, nombre, domiciliofiscalreceptor, residenciafiscal, numregidtrib,
            regimenfiscalreceptor, usocfdi, comprobante_id
        )
        SELECT
            R.value('@Rfc', 'varchar(13)'),
            R.value('@Nombre', 'varchar(300)'),
            R.value('@DomicilioFiscalReceptor', 'varchar(5)'),
            R.value('@ResidenciaFiscalReceptor', 'varchar(3)'),
            R.value('@NumRegIdTrib', 'varchar(40)'),
            R.value('@RegimenFiscalReceptor', 'varchar(3)'),
            R.value('@UsoCFDI', 'varchar(4)'),
            @ComprobanteID
        FROM @x.nodes('/cfdi:Comprobante/cfdi:Receptor') AS X(R);

        DECLARE @HasIG BIT = 0;

        ;WITH XMLNAMESPACES ('http://www.sat.gob.mx/cfd/4' AS cfdi)
        SELECT TOP 1
            @HasIG = 1
        FROM @x.nodes('/cfdi:Comprobante/cfdi:InformacionGlobal') AS X(N);

        IF @HasIG = 1
        BEGIN
            PRINT 'HAS_IG';

            ;WITH XMLNAMESPACES ('http://www.sat.gob.mx/cfd/4' AS cfdi)
            INSERT INTO cfdi.InformacionGlobal (Comprobante_Id, Periodicidad, Meses, Anio)
            SELECT
                @ComprobanteID,
                IG.value('@Periodicidad', 'char(2)'),
                IG.value('@Meses', 'varchar(20)'),
                IG.value('@Año', 'smallint')
            FROM @x.nodes('/cfdi:Comprobante/cfdi:InformacionGlobal') AS X(IG);

            PRINT 'Finished setting IG with XML';
        END
        ELSE
        BEGIN
            PRINT 'DOES NOT HAVE IG';

            INSERT INTO cfdi.InformacionGlobal (Comprobante_Id, Periodicidad, Meses, Anio)
            SELECT
                @ComprobanteID,
                '01',
                RIGHT('0' + CAST(MONTH(@Fecha) AS VARCHAR(2)), 2),
                YEAR(@Fecha);

            PRINT 'Finished setting IG Manually';
        END

        ----------------------------------------------------------------------
        -- 9) Conceptos + Impuestos por concepto
        ----------------------------------------------------------------------
        DECLARE @HasConceptos BIT = 0;

        ;WITH XMLNAMESPACES ('http://www.sat.gob.mx/cfd/4' AS cfdi)
        SELECT TOP 1
            @HasConceptos = 1
        FROM @x.nodes('/cfdi:Comprobante/cfdi:Conceptos/cfdi:Concepto') AS X(N);

        IF @HasConceptos = 1
        BEGIN
            DECLARE @Conceptos_Id INT;

            INSERT INTO cfdi.conceptos (Comprobante_Id)
            VALUES (@ComprobanteID);

            SET @Conceptos_Id = SCOPE_IDENTITY();

            IF OBJECT_ID('tempdb..#ConceptosX') IS NOT NULL
                DROP TABLE #ConceptosX;

            ;WITH XMLNAMESPACES ('http://www.sat.gob.mx/cfd/4' AS cfdi)
            SELECT
                ROW_NUMBER() OVER (ORDER BY (SELECT 1)) AS Linea,
                C.value('@ClaveProdServ', 'varchar(20)')      AS ClaveProdServ,
                C.value('@NoIdentificacion', 'varchar(100)')  AS NoIdentificacion,
                C.value('@Cantidad', 'decimal(18,6)')         AS Cantidad,
                C.value('@ClaveUnidad', 'varchar(20)')        AS ClaveUnidad,
                C.value('@Unidad', 'varchar(20)')             AS Unidad,
                C.value('@Descripcion', 'varchar(1000)')      AS Descripcion,
                C.value('@ValorUnitario', 'decimal(18,6)')    AS ValorUnitario,
                C.value('@Importe', 'decimal(18,6)')          AS Importe,
                C.value('@Descuento', 'decimal(18,6)')        AS Descuento,
                C.value('@ObjetoImp', 'varchar(3)')           AS ObjetoImp,
                C.query('.')                                  AS NodeC
            INTO #ConceptosX
            FROM @x.nodes('/cfdi:Comprobante/cfdi:Conceptos/cfdi:Concepto') AS X(C);

            INSERT INTO cfdi.concepto
            (
                ClaveProdServ, NoIdentificacion, Cantidad, ClaveUnidad, Unidad, Descripcion,
                ValorUnitario, Importe, Descuento, ObjetoImp, Conceptos_Id, Linea
            )
            SELECT
                ClaveProdServ, NoIdentificacion, Cantidad, ClaveUnidad, Unidad, Descripcion,
                ValorUnitario, Importe, Descuento, ObjetoImp, @Conceptos_Id, Linea
            FROM #ConceptosX;

            INSERT INTO cfdi.impuestos (Concepto_Id, Comprobante_Id)
            SELECT
                c.concepto_id,
                @ComprobanteID
            FROM cfdi.concepto c
            WHERE c.conceptos_id = @Conceptos_Id;

            -- Traslados por concepto
            ;WITH XMLNAMESPACES ('http://www.sat.gob.mx/cfd/4' AS cfdi)
            INSERT INTO cfdi.traslados (Impuestos_Id)
            SELECT i.impuestos_id
            FROM cfdi.impuestos i
            WHERE i.Comprobante_Id = @ComprobanteID;

            ;WITH XMLNAMESPACES ('http://www.sat.gob.mx/cfd/4' AS cfdi)
            INSERT INTO cfdi.traslado (Base, Impuesto, TipoFactor, TasaOCuota, Importe, Traslados_Id)
            SELECT
                T.X.value('@Base', 'decimal(18,6)'),
                T.X.value('@Impuesto', 'varchar(3)'),
                T.X.value('@TipoFactor', 'varchar(10)'),
                T.X.value('@TasaOCuota', 'decimal(18,6)'),
                T.X.value('@Importe', 'decimal(18,6)'),
                tr.Traslados_Id
            FROM cfdi.concepto c
            JOIN cfdi.impuestos i
                ON i.Concepto_Id = c.concepto_id
               AND i.Comprobante_Id = @ComprobanteID
            JOIN cfdi.traslados tr
                ON tr.impuestos_id = i.impuestos_id
            JOIN #ConceptosX CX
                ON CX.Linea = c.Linea
            CROSS APPLY CX.NodeC.nodes('cfdi:Concepto/cfdi:Impuestos/cfdi:Traslados/cfdi:Traslado') AS T(X);

            -- Retenciones por concepto
            ;WITH XMLNAMESPACES ('http://www.sat.gob.mx/cfd/4' AS cfdi)
            INSERT INTO cfdi.retenciones (Impuestos_Id)
            SELECT i.impuestos_id
            FROM cfdi.impuestos i
            WHERE i.Comprobante_Id = @ComprobanteID;

            ;WITH XMLNAMESPACES ('http://www.sat.gob.mx/cfd/4' AS cfdi)
            INSERT INTO cfdi.retencion (Base, Impuesto, TipoFactor, TasaOCuota, Importe, Retenciones_Id)
            SELECT
                R.X.value('@Base', 'decimal(18,6)'),
                R.X.value('@Impuesto', 'varchar(3)'),
                R.X.value('@TipoFactor', 'varchar(10)'),
                R.X.value('@TasaOCuota', 'decimal(18,6)'),
                R.X.value('@Importe', 'decimal(18,6)'),
                re.Retenciones_Id
            FROM cfdi.concepto c
            JOIN cfdi.impuestos i
                ON i.Concepto_Id = c.concepto_id
               AND i.Comprobante_Id = @ComprobanteID
            JOIN cfdi.retenciones re
                ON re.impuestos_id = i.impuestos_id
            JOIN #ConceptosX CX
                ON CX.Linea = c.Linea
            CROSS APPLY CX.NodeC.nodes('cfdi:Concepto/cfdi:Impuestos/cfdi:Retenciones/cfdi:Retencion') AS R(X);

            DROP TABLE IF EXISTS #ConceptosX;
        END

        ----------------------------------------------------------------------
        -- 10) Complemento Pagos (soporta Pagos 1.0 y 2.0) si es tipo 'P'
        ----------------------------------------------------------------------
        IF @TipoDeComprobante = 'P'
        BEGIN
            PRINT '>> [10] TipoDeComprobante = P: Starting Pagos processing...';

            DECLARE @Pagos20_Id INT;
            DECLARE @PagosVersionXML VARCHAR(5) = NULL;

            -- Leer versión real del nodo <Pagos> si existe (1.0 / 2.0)
            SELECT TOP 1
                @PagosVersionXML = P.value('@Version', 'varchar(5)')
            FROM @x.nodes('/*[local-name()="Comprobante"]/*[local-name()="Complemento"]/*[local-name()="Pagos"]') AS X(P);

            -- Insert Pagos20 header (mantengo '2.0' por compatibilidad de esquema)
            PRINT '>> [10.0] Inserting cfdi.Pagos20';

            INSERT INTO cfdi.Pagos20 (Comprobante_Id, Version)
            VALUES (@ComprobanteID, '2.0');

            SET @Pagos20_Id = SCOPE_IDENTITY();

            PRINT '>> [10.0] Pagos20_Id = ' + CAST(@Pagos20_Id AS VARCHAR);

            -- Insert Totales SOLO si existe el nodo (típico de Pagos 2.0)
            IF @x.exist('/*[local-name()="Comprobante"]/*[local-name()="Complemento"]/*[local-name()="Pagos"]/*[local-name()="Totales"]') = 1
            BEGIN
                PRINT '>> [10.0] Inserting cfdi.Pagos20_Totales (Totales node detected)';

                INSERT INTO cfdi.Pagos20_Totales
                (
                    Pagos20_Id,
                    TotalRetencionesIVA, TotalRetencionesISR, TotalRetencionesIEPS,
                    TotalTrasladosBaseIVA16, TotalTrasladosImpuestoIVA16,
                    TotalTrasladosBaseIVA08, TotalTrasladosImpuestoIVA08,
                    TotalTrasladosBaseIVA00, TotalTrasladosImpuestoIVA00,
                    MontoTotalPagos
                )
                SELECT
                    @Pagos20_Id,
                    T.value('@TotalRetencionesIVA', 'decimal(18,2)'),
                    T.value('@TotalRetencionesISR', 'decimal(18,2)'),
                    T.value('@TotalRetencionesIEPS', 'decimal(18,2)'),
                    T.value('@TotalTrasladosBaseIVA16', 'decimal(18,2)'),
                    T.value('@TotalTrasladosImpuestoIVA16', 'decimal(18,2)'),
                    T.value('@TotalTrasladosBaseIVA08', 'decimal(18,2)'),
                    T.value('@TotalTrasladosImpuestoIVA08', 'decimal(18,2)'),
                    T.value('@TotalTrasladosBaseIVA00', 'decimal(18,2)'),
                    T.value('@TotalTrasladosImpuestoIVA00', 'decimal(18,2)'),
                    T.value('@MontoTotalPagos', 'decimal(18,2)')
                FROM @x.nodes('/*[local-name()="Comprobante"]/*[local-name()="Complemento"]/*[local-name()="Pagos"]/*[local-name()="Totales"]') AS X(T);
            END
            ELSE
            BEGIN
                PRINT '>> [10.0] Totales node not found (likely Pagos 1.0). Skipping Totales insert.';
            END

            ----------------------------------------------------------------------
            -- Extract pagos into #PagosTmp (namespace agnóstico)
            ----------------------------------------------------------------------
            PRINT '>> [10.1] Extracting pagos from XML';

            IF OBJECT_ID('tempdb..#PagosTmp') IS NOT NULL
                DROP TABLE #PagosTmp;

            SELECT
                ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS RowNum,
                P.value('@FechaPago', 'datetime2(0)')       AS FechaPago,
                P.value('@FormaDePagoP', 'varchar(3)')      AS FormaDePagoP,
                P.value('@MonedaP', 'char(3)')              AS MonedaP,
                P.value('@TipoCambioP', 'decimal(18,6)')    AS TipoCambioP,
                P.value('@Monto', 'decimal(18,6)')          AS Monto,
                P.query('.')                                 AS NodeP
            INTO #PagosTmp
            FROM @x.nodes('/*[local-name()="Comprobante"]/*[local-name()="Complemento"]/*[local-name()="Pagos"]/*[local-name()="Pago"]') AS X(P);

            DECLARE @cnt_pagos INT;

            SELECT @cnt_pagos = COUNT(*)
            FROM #PagosTmp;

            PRINT '>> [10.1] Found pagos: ' + CAST(@cnt_pagos AS VARCHAR);

            IF @cnt_pagos = 0
            BEGIN
                PRINT '>> [10] No pagos found in complemento. Skipping Pagos processing safely.';

                -- Limpieza defensiva del header recién insertado
                DELETE FROM cfdi.Pagos20_Totales
                WHERE Pagos20_Id = @Pagos20_Id;

                DELETE FROM cfdi.Pagos20
                WHERE Pagos20_Id = @Pagos20_Id;

                -- IMPORTANTE: NO RETURN aquí (evita Msg 266)
            END
            ELSE
            BEGIN
                ----------------------------------------------------------------------
                -- MERGE to insert pagos and capture Pago_Id WITHOUT alias problems
                ----------------------------------------------------------------------
                PRINT '>> [10.1.2] MERGE inserting into cfdi.Pagos20_Pago';

                IF OBJECT_ID('tempdb..#InsertedPagos') IS NOT NULL
                    DROP TABLE #InsertedPagos;

                CREATE TABLE #InsertedPagos
                (
                    RowNum  INT,
                    Pago_Id INT
                );

                MERGE cfdi.Pagos20_Pago AS T
                USING #PagosTmp AS S
                    ON 1 = 0 -- forces all rows to INSERT
                WHEN NOT MATCHED THEN
                    INSERT (Pagos20_Id, FechaPago, FormaDePagoP, MonedaP, TipoCambioP, Monto)
                    VALUES (@Pagos20_Id, S.FechaPago, S.FormaDePagoP, S.MonedaP, ISNULL(S.TipoCambioP, 1), S.Monto)
                OUTPUT
                    S.RowNum,
                    inserted.Pago_Id
                INTO #InsertedPagos (RowNum, Pago_Id);

                SELECT @cnt_pagos = COUNT(*)
                FROM #InsertedPagos;

                PRINT '>> [10.1.2] Inserted pagos: ' + CAST(@cnt_pagos AS VARCHAR);

                ----------------------------------------------------------------------
                -- [10.2] Extracting DoctoRelacionado nodes
                ----------------------------------------------------------------------
                PRINT '>> [10.2] Extracting DoctoRelacionado nodes...';

                IF OBJECT_ID('tempdb..#DoctosTmp') IS NOT NULL
                    DROP TABLE #DoctosTmp;

                DECLARE @RowCountDocTemp INT;

                SELECT
                    ip.Pago_Id,
                    DR.value('@IdDocumento', 'varchar(50)')           AS IdDocumento,
                    DR.value('@Serie', 'varchar(25)')                 AS Serie,
                    DR.value('@Folio', 'varchar(40)')                 AS Folio,
                    DR.value('@MonedaDR', 'char(3)')                  AS MonedaDR,
                    DR.value('@EquivalenciaDR', 'decimal(18,6)')      AS EquivalenciaDR,
                    DR.value('@NumParcialidad', 'int')                AS NumParcialidad,
                    DR.value('@ImpSaldoAnt', 'decimal(18,2)')         AS ImpSaldoAnt,
                    DR.value('@ImpPagado', 'decimal(18,2)')           AS ImpPagado,
                    DR.value('@ImpSaldoInsoluto', 'decimal(18,2)')    AS ImpSaldoInsoluto,
                    DR.value('@ObjetoImpDR', 'varchar(2)')            AS ObjetoImpDR,
                    DR.query('.')                                      AS NodeDR
                INTO #DoctosTmp
                FROM #PagosTmp pt
                JOIN #InsertedPagos ip
                    ON ip.RowNum = pt.RowNum
                CROSS APPLY pt.NodeP.nodes('/*[local-name()="Pago"]/*[local-name()="DoctoRelacionado"]') AS X(DR);

                SELECT @RowCountDocTemp = COUNT(*)
                FROM #DoctosTmp;

                PRINT '>> [10.2] DoctoRelacionado rows: ' + CAST(@RowCountDocTemp AS VARCHAR);

                INSERT INTO cfdi.Pagos20_DoctoRelacionado
                (
                    Pago_Id, IdDocumento, Serie, Folio, MonedaDR, EquivalenciaDR,
                    NumParcialidad, ImpSaldoAnt, ImpPagado, ImpSaldoInsoluto, ObjetoImpDR
                )
                SELECT
                    Pago_Id, IdDocumento, Serie, Folio, MonedaDR, EquivalenciaDR,
                    NumParcialidad, ImpSaldoAnt, ImpPagado, ImpSaldoInsoluto, ObjetoImpDR
                FROM #DoctosTmp;

                PRINT '>> [10.2] Inserted into cfdi.Pagos20_DoctoRelacionado';

                ----------------------------------------------------------------------
                -- [10.3] Impuestos DR (Traslados / Retenciones)
                ----------------------------------------------------------------------
                PRINT '>> [10.3] Inserting cfdi.Pagos20_TrasladoDR / RetencionDR';

                -- Traslados DR
                INSERT INTO cfdi.Pagos20_TrasladoDR
                (
                    DoctoRelacionado_Id, BaseDR, ImpuestoDR, TipoFactorDR, TasaOCuotaDR, ImporteDR
                )
                SELECT
                    d.DoctoRelacionado_Id,
                    T.value('@BaseDR', 'decimal(18,6)'),
                    T.value('@ImpuestoDR', 'varchar(3)'),
                    T.value('@TipoFactorDR', 'varchar(10)'),
                    T.value('@TasaOCuotaDR', 'decimal(18,6)'),
                    T.value('@ImporteDR', 'decimal(18,6)')
                FROM #DoctosTmp tmp
                JOIN cfdi.Pagos20_DoctoRelacionado d
                    ON d.Pago_Id = tmp.Pago_Id
                   AND CONVERT(VARCHAR(50), d.IdDocumento) = tmp.IdDocumento
                CROSS APPLY tmp.NodeDR.nodes(
                    '/*[local-name()="DoctoRelacionado"]/*[local-name()="ImpuestosDR"]/*[local-name()="TrasladosDR"]/*[local-name()="TrasladoDR"]'
                ) AS X(T);

                PRINT '>> [10.3] Inserted Pagos20_TrasladoDR rows: ' + CAST(@@ROWCOUNT AS VARCHAR);

                -- Retenciones DR
                INSERT INTO cfdi.Pagos20_RetencionDR
                (
                    DoctoRelacionado_Id, ImpuestoDR, ImporteDR
                )
                SELECT
                    d.DoctoRelacionado_Id,
                    R.value('@ImpuestoDR', 'varchar(3)'),
                    R.value('@ImporteDR', 'decimal(18,6)')
                FROM #DoctosTmp tmp
                JOIN cfdi.Pagos20_DoctoRelacionado d
                    ON d.Pago_Id = tmp.Pago_Id
                   AND CONVERT(VARCHAR(50), d.IdDocumento) = tmp.IdDocumento
                CROSS APPLY tmp.NodeDR.nodes(
                    '/*[local-name()="DoctoRelacionado"]/*[local-name()="ImpuestosDR"]/*[local-name()="RetencionesDR"]/*[local-name()="RetencionDR"]'
                ) AS X(R);

                PRINT '>> [10.3] Inserted Pagos20_RetencionDR rows: ' + CAST(@@ROWCOUNT AS VARCHAR);

                ----------------------------------------------------------------------
                -- [10.4] Impuestos a nivel Pago (Traslados / Retenciones)
                ----------------------------------------------------------------------
                PRINT '>> [10.4] Inserting cfdi.Pagos20_TrasladoP / RetencionP';

                -- Traslados P
                INSERT INTO cfdi.Pagos20_TrasladoP
                (
                    Pago_Id, BaseP, ImpuestoP, TipoFactorP, TasaOCuotaP, ImporteP
                )
                SELECT
                    ip.Pago_Id,
                    Tp.value('@BaseP', 'decimal(18,6)'),
                    Tp.value('@ImpuestoP', 'varchar(3)'),
                    Tp.value('@TipoFactorP', 'varchar(10)'),
                    Tp.value('@TasaOCuotaP', 'decimal(18,6)'),
                    Tp.value('@ImporteP', 'decimal(18,6)')
                FROM #PagosTmp pt
                JOIN #InsertedPagos ip
                    ON ip.RowNum = pt.RowNum
                CROSS APPLY pt.NodeP.nodes(
                    '/*[local-name()="Pago"]/*[local-name()="ImpuestosP"]/*[local-name()="TrasladosP"]/*[local-name()="TrasladoP"]'
                ) AS X(Tp);

                PRINT '>> [10.4] Inserted Pagos20_TrasladoP rows: ' + CAST(@@ROWCOUNT AS VARCHAR);

                -- Retenciones P
                INSERT INTO cfdi.Pagos20_RetencionP
                (
                    Pago_Id, ImpuestoP, ImporteP
                )
                SELECT
                    ip.Pago_Id,
                    Rp.value('@ImpuestoP', 'varchar(3)'),
                    Rp.value('@ImporteP', 'decimal(18,6)')
                FROM #PagosTmp pt
                JOIN #InsertedPagos ip
                    ON ip.RowNum = pt.RowNum
                CROSS APPLY pt.NodeP.nodes(
                    '/*[local-name()="Pago"]/*[local-name()="ImpuestosP"]/*[local-name()="RetencionesP"]/*[local-name()="RetencionP"]'
                ) AS X(Rp);

                PRINT '>> [10.4] Inserted Pagos20_RetencionP rows: ' + CAST(@@ROWCOUNT AS VARCHAR);
            END
        END

        ----------------------------------------------------------------------
        -- 11) OK: devolver Comprobante_ID
        ----------------------------------------------------------------------
        COMMIT;

        SELECT @ComprobanteID AS Comprobante_ID;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0
            ROLLBACK;

        DECLARE
            @msg NVARCHAR(4000) = ERROR_MESSAGE(),
            @sev INT = ERROR_SEVERITY(),
            @st  INT = ERROR_STATE();

        RAISERROR(@msg, @sev, @st);
    END CATCH
END;

