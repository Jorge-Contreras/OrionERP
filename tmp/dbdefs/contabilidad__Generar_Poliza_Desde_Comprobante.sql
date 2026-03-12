
CREATE PROCEDURE [contabilidad].[Generar_Poliza_Desde_Comprobante]
    @Comprobante_Id INT,
    @RFC            VARCHAR(50),              -- RFC de la empresa (contexto)
    @Categoria      INT          = NULL,      -- opcional, para Transacciones.Categoria
    @TipoPoliza     VARCHAR(50)  = NULL,      -- si NULL se decide como INGRESO / EGRESO
    @FechaPoliza    DATETIME     = NULL,      -- si NULL, usa la fecha del CFDI
    @FormaPago      VARCHAR(10)  = NULL,      -- si NULL, se toma de CFDI (FormaPago)
    @TransaccionID  INT          OUTPUT       -- ID de la póliza generada
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRY
        BEGIN TRAN;

        ----------------------------------------------------------------------
        -- 1) Leer datos del CFDI desde la vista cfdi.Comprobante_Detalle
        ----------------------------------------------------------------------
        DECLARE
            @UsoCFDI           VARCHAR(50),
            @RECEPTOR          VARCHAR(500),
            @EMISOR            VARCHAR(500),
            @UUID              VARCHAR(50),
            @FechaCFDI         DATETIME,
            @SubTotalDesc      DECIMAL(19,2),
            @Descuento         DECIMAL(19,2),
            @Actos_16          DECIMAL(19,2),
            @Actos_0           DECIMAL(19,2),
            @IVA               DECIMAL(19,2),
            @IEPS              DECIMAL(19,2),
            @IVA_RETENIDO      DECIMAL(19,2),
            @ISR_RETENIDO      DECIMAL(19,2),
            @IEPS_RETENIDO     DECIMAL(19,2),
            @TotalCFDI         DECIMAL(19,2),
            @Serie             VARCHAR(25),
            @Folio             VARCHAR(40),
            @TipoDeComprobante VARCHAR(50),
            @Exportacion       VARCHAR(10),
            @MetodoPagoCFDI    VARCHAR(50),
            @LugarExpedicion   VARCHAR(100),
            @Confirmacion      VARCHAR(50),
            @Tipo_Comprobante  VARCHAR(50),
            @Incluir_Declar    BIT,
            @Factor_Declar     DECIMAL(5,4),
            @RFC_EMISOR        VARCHAR(13),
            @RFC_RECEPTOR      VARCHAR(13),
            @Periodicidad      VARCHAR(50),
            @Meses             VARCHAR(50),
            @Anio              INT,
            @FechaCancelacion  DATETIME,
            @EstatusCFDI       VARCHAR(100),
            @FormaPagoCFDI     VARCHAR(50),
            @SumaPolizas       DECIMAL(19,4),
            @PolizaExistente   INT,
            @TienePoliza5505   BIT = 0;

        SELECT
            @UsoCFDI           = cd.UsoCFDI,
            @RECEPTOR          = cd.RECEPTOR,
            @EMISOR            = cd.EMISOR,
            @UUID              = cd.FOLIO_FISCAL,
            @FechaCFDI         = cd.Fecha,
            @SubTotalDesc      = cd.SubTotal_Desc,
            @Descuento         = cd.Descuento,
            @Actos_16          = cd.Actos_16,
            @Actos_0           = cd.Actos_0,
            @IVA               = cd.IVA,
            @IEPS              = cd.IEPS,
            @IVA_RETENIDO      = cd.IVA_RETENIDO,
            @ISR_RETENIDO      = cd.ISR_RETENIDO,
            @IEPS_RETENIDO     = cd.IEPS_RETENIDO,
            @TotalCFDI         = cd.Total,
            @Serie             = cd.Serie,
            @Folio             = cd.Folio,
            @TipoDeComprobante = cd.TipoDeComprobante,
            @Exportacion       = cd.Exportacion,
            @MetodoPagoCFDI    = cd.MetodoPago,
            @LugarExpedicion   = cd.LugarExpedicion,
            @Confirmacion      = cd.Confirmacion,
            @Tipo_Comprobante  = cd.Tipo_Comprobante,
            @Incluir_Declar    = cd.Incluir_En_Declaracion,
            @Factor_Declar     = cd.Factor_Declaracion,
            @RFC_EMISOR        = cd.RFC_EMISOR,
            @RFC_RECEPTOR      = cd.RFC_RECEPTOR,
            @Periodicidad      = cd.PERIODICIDAD,
            @Meses             = cd.MESES,
            @Anio              = cd.ANIO,
            @FechaCancelacion  = cd.FechaCancelacion,
            @EstatusCFDI       = cd.Estatus,
            @FormaPagoCFDI     = cd.FormaPago,
            @SumaPolizas       = cd.SumaPolizas,
            @PolizaExistente   = cd.Poliza
        FROM cfdi.Comprobante_Detalle cd
        WHERE cd.Comprobante_Id = @Comprobante_Id;

        IF @@ROWCOUNT = 0
        BEGIN
            RAISERROR('No se encontró registro en cfdi.Comprobante_Detalle para Comprobante_Id = %d.',16,1,@Comprobante_Id);
            ROLLBACK TRAN;
            RETURN;
        END;

        ----------------------------------------------------------------------
        -- 2) Validaciones básicas
        ----------------------------------------------------------------------
        IF @RFC <> @RFC_EMISOR AND @RFC <> @RFC_RECEPTOR
        BEGIN
            RAISERROR('El RFC proporcionado no corresponde al emisor ni al receptor del CFDI.',16,1);
            ROLLBACK TRAN;
            RETURN;
        END;

        IF @EstatusCFDI LIKE 'CANCEL%'
        BEGIN
            RAISERROR('El CFDI está cancelado. No se genera póliza.',16,1);
            ROLLBACK TRAN;
            RETURN;
        END;

        ----------------------------------------------------------------------
        -- 2.1) Validar ligas existentes en Transaccion_Comprobante
        --      Regla:
        --        - Si hay alguna Transaccion_ID distinta de 5505 -> ERROR.
        --        - Si solo existe 5505, se permite y luego se reemplaza por la nueva póliza.
        ----------------------------------------------------------------------
        IF EXISTS (
            SELECT 1
            FROM dbo.Transaccion_Comprobante tc
            WHERE tc.Comprobante_ID = @Comprobante_Id
              AND tc.Transaccion_ID <> 5505
        )
        BEGIN
            RAISERROR('Ya existe una Transacción distinta de 5505 ligada a este Comprobante (Transaccion_Comprobante).',16,1);
            ROLLBACK TRAN;
            RETURN;
        END;

        IF EXISTS (
            SELECT 1
            FROM dbo.Transaccion_Comprobante tc
            WHERE tc.Comprobante_ID = @Comprobante_Id
              AND tc.Transaccion_ID = 5505
        )
        BEGIN
            SET @TienePoliza5505 = 1;
        END;

        ----------------------------------------------------------------------
        -- 2.2) Validar relación Total vs SubTotal_Desc/Impuestos/Retenciones
        --      Total ˜ (SubTotal_Desc + impuestos - retenciones)
        ----------------------------------------------------------------------
        DECLARE
            @Base           DECIMAL(19,2),
            @TotalTras      DECIMAL(19,2),
            @TotalRet       DECIMAL(19,2),
            @LadoIzq        DECIMAL(19,2),
            @LadoDer        DECIMAL(19,2),
            @DiferenciaCalc DECIMAL(19,4);

        SET @Base      = ISNULL(@SubTotalDesc,0);
        SET @TotalTras = ISNULL(@IVA,0) + ISNULL(@IEPS,0);
        SET @TotalRet  = ISNULL(@IVA_RETENIDO,0) + ISNULL(@ISR_RETENIDO,0) + ISNULL(@IEPS_RETENIDO,0);

        SET @LadoIzq = ISNULL(@Base,0)
                     + ISNULL(@TotalTras,0)
                     - ISNULL(@TotalRet,0);

        SET @LadoDer        = ISNULL(@TotalCFDI,0);
        SET @DiferenciaCalc = ABS(@LadoIzq - @LadoDer);

        IF @DiferenciaCalc > 0.02
        BEGIN
            DECLARE @LadoIzqStr VARCHAR(30),
                    @LadoDerStr VARCHAR(30);

            SET @LadoIzqStr = CONVERT(VARCHAR(30), @LadoIzq);
            SET @LadoDerStr = CONVERT(VARCHAR(30), @LadoDer);

            RAISERROR(
                'Inconsistencia en CFDI: (SubTotal_Desc + impuestos - retenciones = %s) difiere de (TotalCFDI = %s).',
                16, 1,
                @LadoIzqStr,
                @LadoDerStr
            );

            ROLLBACK TRAN;
            RETURN;
        END;

        ----------------------------------------------------------------------
        -- 3) Determinar contexto: ingreso vs gasto
        ----------------------------------------------------------------------
        DECLARE @EsIngreso BIT = 0,
                @EsGasto   BIT = 0;

        IF @RFC = @RFC_EMISOR
            SET @EsIngreso = 1;
        ELSE IF @RFC = @RFC_RECEPTOR
            SET @EsGasto = 1;

        ----------------------------------------------------------------------
        -- 4) Definir cuentas contables (mapeo rápido en variables)
        ----------------------------------------------------------------------
        DECLARE
            -- Base (se registra en cuenta de Actos 16% por simplificación contable)
            @N1_ACT16_G   VARCHAR(50) = '603',
            @N2_ACT16_G   VARCHAR(50) = '85',
            @N3_ACT16_G   VARCHAR(50) = '00',
            @DESC_ACT16_G VARCHAR(200) = 'INSUMOS SUJETOS AL 16%',

            @N1_ACT16_I   VARCHAR(50) = '401',
            @N2_ACT16_I   VARCHAR(50) = '13',
            @N3_ACT16_I   VARCHAR(50) = '00',
            @DESC_ACT16_I VARCHAR(200) = 'VENTAS Y/O SERVICIOS GRAVADOS A LA TASA GENERAL NACIONALES PARTES RELACIONADAS',

            -- IVA
            @N1_IVA_TRAS  VARCHAR(50) = '208',
            @N2_IVA_TRAS  VARCHAR(50) = '01',
            @N3_IVA_TRAS  VARCHAR(50) = '00',
            @DESC_IVA_TRAS VARCHAR(200) = 'IVA TRASLADADO COBRADO',

            @N1_IVA_ACRED VARCHAR(50) = '118',
            @N2_IVA_ACRED VARCHAR(50) = '01',
            @N3_IVA_ACRED VARCHAR(50) = '00',
            @DESC_IVA_ACRED VARCHAR(200) = 'IVA ACREDITABLE PAGADO',

            -- IEPS
            @N1_IEPS_TRAS VARCHAR(50) = '208',
            @N2_IEPS_TRAS VARCHAR(50) = '02',
            @N3_IEPS_TRAS VARCHAR(50) = '00',
            @DESC_IEPS_TRAS VARCHAR(200) = 'IEPS TRASLADADO COBRADO',

            @N1_IEPS_ACRED VARCHAR(50) = '118',
            @N2_IEPS_ACRED VARCHAR(50) = '03',
            @N3_IEPS_ACRED VARCHAR(50) = '00',
            @DESC_IEPS_ACRED VARCHAR(200) = 'IEPS ACREDITABLE PAGADO',

            -- Retenciones (mismas cuentas, distinto sentido ingreso/gasto)
            @N1_RET_IVA   VARCHAR(50) = '216',
            @N2_RET_IVA   VARCHAR(50) = '01',
            @N3_RET_IVA   VARCHAR(50) = '00',
            @DESC_RET_IVA VARCHAR(200) = 'IMPUESTOS RETENIDOS DE IVA',

            @N1_RET_ISR   VARCHAR(50) = '216',
            @N2_RET_ISR   VARCHAR(50) = '03',
            @N3_RET_ISR   VARCHAR(50) = '00',
            @DESC_RET_ISR VARCHAR(200) = 'IMPUESTOS RETENIDOS DE ISR',

            @N1_RET_IEPS  VARCHAR(50) = '216',
            @N2_RET_IEPS  VARCHAR(50) = '12',
            @N3_RET_IEPS  VARCHAR(50) = '00',
            @DESC_RET_IEPS VARCHAR(200) = 'OTROS IMPUESTOS RETENIDOS',

            -- Total (puente)
            @N1_TOTAL_G   VARCHAR(50) = '703',
            @N2_TOTAL_G   VARCHAR(50) = '21',
            @N3_TOTAL_G   VARCHAR(50) = '01',
            @DESC_TOTAL_G VARCHAR(200) = 'GASTOS BANCARIOS PENDIENTES DE REGISTRO',

            @N1_TOTAL_I   VARCHAR(50) = '403',
            @N2_TOTAL_I   VARCHAR(50) = '01',
            @N3_TOTAL_I   VARCHAR(50) = '02',
            @DESC_TOTAL_I VARCHAR(200) = 'INGRESOS BANCARIOS PENDIENTES DE REGISTRO';

        ----------------------------------------------------------------------
        -- 5) Construir movimientos en un @TABLE para cuadrar antes de grabar
        ----------------------------------------------------------------------
        DECLARE @RC TABLE
        (
            Nivel1        VARCHAR(50),
            Nivel2        VARCHAR(50),
            Nivel3        VARCHAR(50),
            Nombre_Cuenta VARCHAR(200),
            Concepto      VARCHAR(200),
            Debe          MONEY,
            Haber         MONEY
        );

        DECLARE
            @ConceptoEntidad NVARCHAR(500),
            @ConceptoRC      VARCHAR(200);

        SET @ConceptoEntidad = CASE
                                   WHEN @EsIngreso = 1 THEN @RECEPTOR
                                   ELSE @EMISOR
                               END;

        SET @ConceptoRC = LEFT(ISNULL(@ConceptoEntidad, ''), 200);

        ----------------------------------------------------------------------
        -- 5.1) Movimientos para GASTO (RFC = RECEPTOR)
        --      Debe : SubTotal_Desc + impuestos
        --      Haber: Retenciones + Bancos pendientes (Total)
        --      NOTA : Ya no se registran Actos_0 ni Descuento en RC.
        ----------------------------------------------------------------------
        IF @EsGasto = 1
        BEGIN
            -- Base (SubTotal_Desc)
            IF ISNULL(@SubTotalDesc,0) <> 0
            BEGIN
                INSERT INTO @RC
                VALUES (@N1_ACT16_G,@N2_ACT16_G,@N3_ACT16_G,@DESC_ACT16_G,@ConceptoRC,CAST(@SubTotalDesc AS MONEY),0);
            END;

            -- IVA acreditable
            IF ISNULL(@IVA,0) <> 0
            BEGIN
                INSERT INTO @RC
                VALUES (@N1_IVA_ACRED,@N2_IVA_ACRED,@N3_IVA_ACRED,@DESC_IVA_ACRED,@ConceptoRC,CAST(@IVA AS MONEY),0);
            END;

            -- IEPS acreditable
            IF ISNULL(@IEPS,0) <> 0
            BEGIN
                INSERT INTO @RC
                VALUES (@N1_IEPS_ACRED,@N2_IEPS_ACRED,@N3_IEPS_ACRED,@DESC_IEPS_ACRED,@ConceptoRC,CAST(@IEPS AS MONEY),0);
            END;

            -- Retenciones (abonos)
            IF ISNULL(@IVA_RETENIDO,0) <> 0
            BEGIN
                INSERT INTO @RC
                VALUES (@N1_RET_IVA,@N2_RET_IVA,@N3_RET_IVA,@DESC_RET_IVA,@ConceptoRC,0,CAST(@IVA_RETENIDO AS MONEY));
            END;

            IF ISNULL(@ISR_RETENIDO,0) <> 0
            BEGIN
                INSERT INTO @RC
                VALUES (@N1_RET_ISR,@N2_RET_ISR,@N3_RET_ISR,@DESC_RET_ISR,@ConceptoRC,0,CAST(@ISR_RETENIDO AS MONEY));
            END;

            IF ISNULL(@IEPS_RETENIDO,0) <> 0
            BEGIN
                INSERT INTO @RC
                VALUES (@N1_RET_IEPS,@N2_RET_IEPS,@N3_RET_IEPS,@DESC_RET_IEPS,@ConceptoRC,0,CAST(@IEPS_RETENIDO AS MONEY));
            END;

            -- Total CFDI (crédito, gastos bancarios pendientes de registro)
            IF ISNULL(@TotalCFDI,0) <> 0
            BEGIN
                INSERT INTO @RC
                VALUES (@N1_TOTAL_G,@N2_TOTAL_G,@N3_TOTAL_G,@DESC_TOTAL_G,@ConceptoRC,0,CAST(@TotalCFDI AS MONEY));
            END;
        END;

        ----------------------------------------------------------------------
        -- 5.2) Movimientos para INGRESO (RFC = EMISOR)
        --      Haber: SubTotal_Desc + impuestos
        --      Debe : Retenciones + Bancos pendientes (Total)
        --      NOTA : Ya no se registran Actos_0 ni Descuento en RC.
        ----------------------------------------------------------------------
        IF @EsIngreso = 1
        BEGIN
            -- Base (SubTotal_Desc)
            IF ISNULL(@SubTotalDesc,0) <> 0
            BEGIN
                INSERT INTO @RC
                VALUES (@N1_ACT16_I,@N2_ACT16_I,@N3_ACT16_I,@DESC_ACT16_I,@ConceptoRC,0,CAST(@SubTotalDesc AS MONEY));
            END;

            -- IVA trasladado
            IF ISNULL(@IVA,0) <> 0
            BEGIN
                INSERT INTO @RC
                VALUES (@N1_IVA_TRAS,@N2_IVA_TRAS,@N3_IVA_TRAS,@DESC_IVA_TRAS,@ConceptoRC,0,CAST(@IVA AS MONEY));
            END;

            -- IEPS trasladado
            IF ISNULL(@IEPS,0) <> 0
            BEGIN
                INSERT INTO @RC
                VALUES (@N1_IEPS_TRAS,@N2_IEPS_TRAS,@N3_IEPS_TRAS,@DESC_IEPS_TRAS,@ConceptoRC,0,CAST(@IEPS AS MONEY));
            END;

            -- Retenciones (cargos)
            IF ISNULL(@IVA_RETENIDO,0) <> 0
            BEGIN
                INSERT INTO @RC
                VALUES (@N1_RET_IVA,@N2_RET_IVA,@N3_RET_IVA,@DESC_RET_IVA,@ConceptoRC,CAST(@IVA_RETENIDO AS MONEY),0);
            END;

            IF ISNULL(@ISR_RETENIDO,0) <> 0
            BEGIN
                INSERT INTO @RC
                VALUES (@N1_RET_ISR,@N2_RET_ISR,@N3_RET_ISR,@DESC_RET_ISR,@ConceptoRC,CAST(@ISR_RETENIDO AS MONEY),0);
            END;

            IF ISNULL(@IEPS_RETENIDO,0) <> 0
            BEGIN
                INSERT INTO @RC
                VALUES (@N1_RET_IEPS,@N2_RET_IEPS,@N3_RET_IEPS,@DESC_RET_IEPS,@ConceptoRC,CAST(@IEPS_RETENIDO AS MONEY),0);
            END;

            -- Total CFDI (débito, ingresos bancarios pendientes de registro)
            IF ISNULL(@TotalCFDI,0) <> 0
            BEGIN
                INSERT INTO @RC
                VALUES (@N1_TOTAL_I,@N2_TOTAL_I,@N3_TOTAL_I,@DESC_TOTAL_I,@ConceptoRC,CAST(@TotalCFDI AS MONEY),0);
            END;
        END;

        ----------------------------------------------------------------------
        -- 6) Validar que haya movimientos y que cuadren
        ----------------------------------------------------------------------
        DECLARE @SumDebe MONEY, @SumHaber MONEY;
        SELECT @SumDebe = SUM(Debe), @SumHaber = SUM(Haber) FROM @RC;

        IF ISNULL(@SumDebe,0) = 0 AND ISNULL(@SumHaber,0) = 0
        BEGIN
            RAISERROR('El CFDI no generó movimientos contables (SubTotal_Desc/Impuestos/Retenciones en cero).',16,1);
            ROLLBACK TRAN;
            RETURN;
        END;

        IF ISNULL(@SumDebe,0) <> ISNULL(@SumHaber,0)
        BEGIN
            DECLARE @SumDebeStr  VARCHAR(30),
                    @SumHaberStr VARCHAR(30);

            SET @SumDebeStr  = CONVERT(VARCHAR(30), @SumDebe);
            SET @SumHaberStr = CONVERT(VARCHAR(30), @SumHaber);

            RAISERROR(
                'El registro contable generado no cuadra (Debe = %s, Haber = %s).',
                16, 1,
                @SumDebeStr,
                @SumHaberStr
            );

            ROLLBACK TRAN;
            RETURN;
        END;

        ----------------------------------------------------------------------
        -- 7) Insertar encabezado de Transacciones (póliza)
        ----------------------------------------------------------------------
        DECLARE
            @ConceptoPoliza NVARCHAR(500),
            @FechaPolizaEF  DATETIME,
            @FormaPagoEF    VARCHAR(10),
            @TipoPolizaEF   VARCHAR(50);

        SET @ConceptoPoliza = LEFT(ISNULL(@ConceptoEntidad,''),500);

        SET @FechaPolizaEF = ISNULL(@FechaPoliza, @FechaCFDI);
        SET @FormaPagoEF   = ISNULL(@FormaPago, LEFT(ISNULL(@FormaPagoCFDI,''),10));

        SET @TipoPolizaEF = CASE 
                                WHEN @TipoPoliza IS NOT NULL THEN @TipoPoliza
                                WHEN @EsIngreso = 1 THEN 'INGRESO'
                                WHEN @EsGasto   = 1 THEN 'EGRESO'
                                ELSE 'CFDI'
                            END;

        INSERT INTO dbo.Transacciones
            (Concepto, Fecha, Categoria, Comprobante, Monto,
             Facturado, NumeroFactura, Estatus, Referencia,
             Cuenta, Memo, EstatusDeAutorizacion,
             Tipo_Poliza, Forma_Pago, RFC)
        VALUES
            (@ConceptoPoliza,
             @FechaPolizaEF,
             @Categoria,
             CONCAT(ISNULL(@Serie,''),' ',ISNULL(@Folio,'')),
             @TotalCFDI,                           -- Monto de la póliza = Total del CFDI
             1,                                    -- Facturado
             @UUID,                                -- NumeroFactura = UUID
             'GENERADA POR CFDI',                  -- Estatus
             CAST(@Comprobante_Id AS VARCHAR(200)),-- Referencia
             'CFDI-IMPUESTOS',                     -- Cuenta (puente)
             @ConceptoPoliza,                      -- Memo
             'AUTOMATICA',                         -- EstatusDeAutorizacion
             @TipoPolizaEF,
             @FormaPagoEF,
             @RFC);

        SET @TransaccionID = SCOPE_IDENTITY();

        ----------------------------------------------------------------------
        -- 8) Grabar movimientos en Registro_Contable
        ----------------------------------------------------------------------
        INSERT INTO dbo.Registro_Contable
            (Nivel1, Nivel2, Nivel3, Nombre_Cuenta,
             Concepto, Debe, Haber, TransaccionID, Referencia)
        SELECT
            rc.Nivel1,
            rc.Nivel2,
            rc.Nivel3,
            rc.Nombre_Cuenta,
            rc.Concepto,
            rc.Debe,
            rc.Haber,
            @TransaccionID,
            CAST(@Comprobante_Id AS VARCHAR(200))
        FROM @RC rc;

        ----------------------------------------------------------------------
        -- 9) Ligar póliza con el Comprobante (Transaccion_Comprobante)
        --     - Si ya existía la póliza 5505, se reemplaza por la nueva.
        --     - Si no existía nada, se inserta normal.
        ----------------------------------------------------------------------
        IF @TienePoliza5505 = 1
        BEGIN
            UPDATE dbo.Transaccion_Comprobante
            SET Transaccion_ID = @TransaccionID,
                Monto          = @TotalCFDI
            WHERE Comprobante_ID = @Comprobante_Id
              AND Transaccion_ID = 5505;
        END
        ELSE
        BEGIN
            INSERT INTO dbo.Transaccion_Comprobante
                (Transaccion_ID, Comprobante_ID, Monto)
            VALUES
                (@TransaccionID, @Comprobante_Id, @TotalCFDI);
        END;

        ----------------------------------------------------------------------
        -- 10) Actualizar TRANSACTION_ATTACHMENT.TranID usando XML_Attachment_ID
        ----------------------------------------------------------------------
        DECLARE @XML_Attachment_ID INT;

        SELECT @XML_Attachment_ID = c.XML_Attachment_ID
        FROM cfdi.Comprobante c
        WHERE c.Comprobante_Id = @Comprobante_Id;

        IF @XML_Attachment_ID IS NOT NULL
        BEGIN
            UPDATE dbo.TRANSACTION_ATTACHMENT
            SET TranID = @TransaccionID
            WHERE ID = @XML_Attachment_ID;
        END;

        COMMIT TRAN;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0
            ROLLBACK TRAN;

        DECLARE @ErrMsg NVARCHAR(4000) = ERROR_MESSAGE(),
                @ErrNum INT = ERROR_NUMBER();

        RAISERROR('Error en contabilidad.Generar_Poliza_Desde_Comprobante (%d): %s',
                   16, 1, @ErrNum, @ErrMsg) WITH NOWAIT;
    END CATCH;
END;

