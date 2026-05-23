SET ANSI_NULLS ON;
GO
SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID(N'dbo.CfdiPolizaCuentaDefault', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CfdiPolizaCuentaDefault
    (
        Rfc varchar(50) NOT NULL,
        CuentaClave varchar(50) NOT NULL,
        CuentaContableId int NOT NULL,
        CreadoEn datetime2(0) NOT NULL CONSTRAINT DF_CfdiPolizaCuentaDefault_CreadoEn DEFAULT SYSUTCDATETIME(),
        ActualizadoEn datetime2(0) NOT NULL CONSTRAINT DF_CfdiPolizaCuentaDefault_ActualizadoEn DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_CfdiPolizaCuentaDefault PRIMARY KEY (Rfc, CuentaClave),
        CONSTRAINT FK_CfdiPolizaCuentaDefault_CuentasContables FOREIGN KEY (CuentaContableId) REFERENCES dbo.CuentasContables(id)
    );
END;
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.CfdiPolizaCuentaDefault')
      AND name = N'IX_CfdiPolizaCuentaDefault_CuentaContableId'
)
BEGIN
    CREATE INDEX IX_CfdiPolizaCuentaDefault_CuentaContableId
        ON dbo.CfdiPolizaCuentaDefault (CuentaContableId);
END;
GO

/*==============================================================
  Regenera Registro_Contable y actualiza la p�liza existente
  (dbo.Transacciones.ID = @Transaccion_ID) usando un CFDI.

  CAMBIO solicitado:
    - Los renglones de dbo.Registro_Contable.Concepto ahora usan
      dbo.Transacciones.Concepto (la p�liza existente) en vez del
      concepto calculado desde el comprobante.
==============================================================*/
CREATE OR ALTER PROCEDURE [contabilidad].[Regenerar_Poliza_Desde_Comprobante_En_Transaccion]
    @Comprobante_Id INT,
    @Transaccion_ID INT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRY
        BEGIN TRAN;

        ----------------------------------------------------------------------
        -- 0) Validar Transacci�n existente y tomar contexto (RFC, etc.)
        --    + Tomar Concepto actual de la p�liza para reutilizarlo en RC.
        ----------------------------------------------------------------------
        DECLARE
            @RFC               VARCHAR(50),
            @Categoria         INT,
            @FormaPagoActual   VARCHAR(10),
            @TipoPolizaActual  VARCHAR(50),
            @ConceptoTrans     NVARCHAR(500);   -- <-- nuevo: concepto ya existente en Transacciones

        SELECT
            @RFC              = t.RFC,
            @Categoria        = t.Categoria,
            @FormaPagoActual  = t.Forma_Pago,
            @TipoPolizaActual = t.Tipo_Poliza,
            @ConceptoTrans    = t.Concepto
        FROM dbo.Transacciones t WITH (UPDLOCK, HOLDLOCK)
        WHERE t.ID = @Transaccion_ID;

        IF @@ROWCOUNT = 0
        BEGIN
            RAISERROR('No existe dbo.Transacciones.ID = %d.',16,1,@Transaccion_ID);
            ROLLBACK TRAN;
            RETURN;
        END;

        ----------------------------------------------------------------------
        -- 1) Leer datos del CFDI desde cfdi.Comprobante_Detalle
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
            @PolizaExistente   INT;

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
            RAISERROR('No se encontr� registro en cfdi.Comprobante_Detalle para Comprobante_Id = %d.',16,1,@Comprobante_Id);
            ROLLBACK TRAN;
            RETURN;
        END;

        ----------------------------------------------------------------------
        -- 2) Validaciones b�sicas
        ----------------------------------------------------------------------
        IF @RFC <> @RFC_EMISOR AND @RFC <> @RFC_RECEPTOR
        BEGIN
            RAISERROR('El RFC de dbo.Transacciones (%s) no corresponde al emisor ni al receptor del CFDI.',16,1,@RFC);
            ROLLBACK TRAN;
            RETURN;
        END;

        IF @EstatusCFDI LIKE 'CANCEL%'
        BEGIN
            RAISERROR('El CFDI est� cancelado. No se regenera p�liza.',16,1);
            ROLLBACK TRAN;
            RETURN;
        END;

        ----------------------------------------------------------------------
        -- 2.1) Validar ligas existentes en Transaccion_Comprobante
        ----------------------------------------------------------------------
        DECLARE @TienePoliza5505 BIT = 0;

        IF EXISTS (
            SELECT 1
            FROM dbo.Transaccion_Comprobante tc
            WHERE tc.Comprobante_ID = @Comprobante_Id
              AND tc.Transaccion_ID NOT IN (5505, @Transaccion_ID)
        )
        BEGIN
            RAISERROR('Ya existe una Transacci�n distinta de 5505 y distinta de la actual ligada a este Comprobante (Transaccion_Comprobante).',16,1);
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
        -- 2.2) Validar relaci�n Total vs SubTotal_Desc/Impuestos/Retenciones
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
        -- 4) Definir cuentas contables desde Ajustes por RFC
        ----------------------------------------------------------------------
        DECLARE
            @N1_ACT16_G   VARCHAR(50), @N2_ACT16_G   VARCHAR(50), @N3_ACT16_G   VARCHAR(50),
            @DESC_ACT16_G VARCHAR(200),

            @N1_ACT16_I   VARCHAR(50), @N2_ACT16_I   VARCHAR(50), @N3_ACT16_I   VARCHAR(50),
            @DESC_ACT16_I VARCHAR(200),

            @N1_IVA_TRAS   VARCHAR(50), @N2_IVA_TRAS   VARCHAR(50), @N3_IVA_TRAS   VARCHAR(50),
            @DESC_IVA_TRAS VARCHAR(200),

            @N1_IVA_ACRED   VARCHAR(50), @N2_IVA_ACRED   VARCHAR(50), @N3_IVA_ACRED   VARCHAR(50),
            @DESC_IVA_ACRED VARCHAR(200),

            @N1_IEPS_TRAS   VARCHAR(50), @N2_IEPS_TRAS   VARCHAR(50), @N3_IEPS_TRAS   VARCHAR(50),
            @DESC_IEPS_TRAS VARCHAR(200),

            @N1_IEPS_ACRED   VARCHAR(50), @N2_IEPS_ACRED   VARCHAR(50), @N3_IEPS_ACRED   VARCHAR(50),
            @DESC_IEPS_ACRED VARCHAR(200),

            @N1_RET_IVA   VARCHAR(50), @N2_RET_IVA   VARCHAR(50), @N3_RET_IVA   VARCHAR(50),
            @DESC_RET_IVA VARCHAR(200),

            @N1_RET_ISR   VARCHAR(50), @N2_RET_ISR   VARCHAR(50), @N3_RET_ISR   VARCHAR(50),
            @DESC_RET_ISR VARCHAR(200),

            @N1_RET_IEPS   VARCHAR(50), @N2_RET_IEPS   VARCHAR(50), @N3_RET_IEPS   VARCHAR(50),
            @DESC_RET_IEPS VARCHAR(200),

            @N1_TOTAL_G   VARCHAR(50), @N2_TOTAL_G   VARCHAR(50), @N3_TOTAL_G   VARCHAR(50),
            @DESC_TOTAL_G VARCHAR(200),

            @N1_TOTAL_I   VARCHAR(50), @N2_TOTAL_I   VARCHAR(50), @N3_TOTAL_I   VARCHAR(50),
            @DESC_TOTAL_I VARCHAR(200);

        DECLARE @MissingCfdiCuentas NVARCHAR(MAX);

        ;WITH RequiredAccounts AS
        (
            SELECT 'SUBTOTAL_GASTO' AS CuentaClave, 'Subtotal gasto' AS Nombre UNION ALL
            SELECT 'SUBTOTAL_INGRESO', 'Subtotal ingreso' UNION ALL
            SELECT 'IVA_TRASLADADO', 'IVA trasladado' UNION ALL
            SELECT 'IVA_ACREDITABLE', 'IVA acreditable' UNION ALL
            SELECT 'IEPS_TRASLADADO', 'IEPS trasladado' UNION ALL
            SELECT 'IEPS_ACREDITABLE', 'IEPS acreditable' UNION ALL
            SELECT 'RETENCION_IVA', 'Retencion IVA' UNION ALL
            SELECT 'RETENCION_ISR', 'Retencion ISR' UNION ALL
            SELECT 'RETENCION_IEPS', 'Retencion IEPS' UNION ALL
            SELECT 'TOTAL_GASTO', 'Total gasto' UNION ALL
            SELECT 'TOTAL_INGRESO', 'Total ingreso'
        )
        SELECT @MissingCfdiCuentas = STUFF((
            SELECT ', ' + required.Nombre
            FROM RequiredAccounts AS required
            LEFT JOIN dbo.CfdiPolizaCuentaDefault AS defaults
                ON defaults.Rfc = @RFC
               AND defaults.CuentaClave = required.CuentaClave
            LEFT JOIN dbo.CuentasContables AS account
                ON account.id = defaults.CuentaContableId
               AND account.RFC = @RFC
            WHERE account.id IS NULL
               OR NULLIF(LTRIM(RTRIM(ISNULL(account.Nivel1,''))), '') IS NULL
               OR NULLIF(LTRIM(RTRIM(ISNULL(account.Nivel2,''))), '') IS NULL
               OR NULLIF(LTRIM(RTRIM(ISNULL(account.Nivel3,''))), '') IS NULL
            ORDER BY required.CuentaClave
            FOR XML PATH(''), TYPE
        ).value('.', 'nvarchar(max)'), 1, 2, '');

        IF @MissingCfdiCuentas IS NOT NULL
        BEGIN
            RAISERROR('Configura las cuentas contables CFDI para el RFC %s antes de regenerar movimientos: %s.',
                      16, 1, @RFC, @MissingCfdiCuentas);
            ROLLBACK TRAN;
            RETURN;
        END;

        SELECT
            @N1_ACT16_G = MAX(CASE WHEN defaults.CuentaClave = 'SUBTOTAL_GASTO' THEN account.Nivel1 END),
            @N2_ACT16_G = MAX(CASE WHEN defaults.CuentaClave = 'SUBTOTAL_GASTO' THEN account.Nivel2 END),
            @N3_ACT16_G = MAX(CASE WHEN defaults.CuentaClave = 'SUBTOTAL_GASTO' THEN account.Nivel3 END),
            @DESC_ACT16_G = MAX(CASE WHEN defaults.CuentaClave = 'SUBTOTAL_GASTO' THEN COALESCE(NULLIF(LTRIM(RTRIM(account.Descripcion)), ''), CONCAT(account.Nivel1, '.', account.Nivel2, '.', account.Nivel3)) END),

            @N1_ACT16_I = MAX(CASE WHEN defaults.CuentaClave = 'SUBTOTAL_INGRESO' THEN account.Nivel1 END),
            @N2_ACT16_I = MAX(CASE WHEN defaults.CuentaClave = 'SUBTOTAL_INGRESO' THEN account.Nivel2 END),
            @N3_ACT16_I = MAX(CASE WHEN defaults.CuentaClave = 'SUBTOTAL_INGRESO' THEN account.Nivel3 END),
            @DESC_ACT16_I = MAX(CASE WHEN defaults.CuentaClave = 'SUBTOTAL_INGRESO' THEN COALESCE(NULLIF(LTRIM(RTRIM(account.Descripcion)), ''), CONCAT(account.Nivel1, '.', account.Nivel2, '.', account.Nivel3)) END),

            @N1_IVA_TRAS = MAX(CASE WHEN defaults.CuentaClave = 'IVA_TRASLADADO' THEN account.Nivel1 END),
            @N2_IVA_TRAS = MAX(CASE WHEN defaults.CuentaClave = 'IVA_TRASLADADO' THEN account.Nivel2 END),
            @N3_IVA_TRAS = MAX(CASE WHEN defaults.CuentaClave = 'IVA_TRASLADADO' THEN account.Nivel3 END),
            @DESC_IVA_TRAS = MAX(CASE WHEN defaults.CuentaClave = 'IVA_TRASLADADO' THEN COALESCE(NULLIF(LTRIM(RTRIM(account.Descripcion)), ''), CONCAT(account.Nivel1, '.', account.Nivel2, '.', account.Nivel3)) END),

            @N1_IVA_ACRED = MAX(CASE WHEN defaults.CuentaClave = 'IVA_ACREDITABLE' THEN account.Nivel1 END),
            @N2_IVA_ACRED = MAX(CASE WHEN defaults.CuentaClave = 'IVA_ACREDITABLE' THEN account.Nivel2 END),
            @N3_IVA_ACRED = MAX(CASE WHEN defaults.CuentaClave = 'IVA_ACREDITABLE' THEN account.Nivel3 END),
            @DESC_IVA_ACRED = MAX(CASE WHEN defaults.CuentaClave = 'IVA_ACREDITABLE' THEN COALESCE(NULLIF(LTRIM(RTRIM(account.Descripcion)), ''), CONCAT(account.Nivel1, '.', account.Nivel2, '.', account.Nivel3)) END),

            @N1_IEPS_TRAS = MAX(CASE WHEN defaults.CuentaClave = 'IEPS_TRASLADADO' THEN account.Nivel1 END),
            @N2_IEPS_TRAS = MAX(CASE WHEN defaults.CuentaClave = 'IEPS_TRASLADADO' THEN account.Nivel2 END),
            @N3_IEPS_TRAS = MAX(CASE WHEN defaults.CuentaClave = 'IEPS_TRASLADADO' THEN account.Nivel3 END),
            @DESC_IEPS_TRAS = MAX(CASE WHEN defaults.CuentaClave = 'IEPS_TRASLADADO' THEN COALESCE(NULLIF(LTRIM(RTRIM(account.Descripcion)), ''), CONCAT(account.Nivel1, '.', account.Nivel2, '.', account.Nivel3)) END),

            @N1_IEPS_ACRED = MAX(CASE WHEN defaults.CuentaClave = 'IEPS_ACREDITABLE' THEN account.Nivel1 END),
            @N2_IEPS_ACRED = MAX(CASE WHEN defaults.CuentaClave = 'IEPS_ACREDITABLE' THEN account.Nivel2 END),
            @N3_IEPS_ACRED = MAX(CASE WHEN defaults.CuentaClave = 'IEPS_ACREDITABLE' THEN account.Nivel3 END),
            @DESC_IEPS_ACRED = MAX(CASE WHEN defaults.CuentaClave = 'IEPS_ACREDITABLE' THEN COALESCE(NULLIF(LTRIM(RTRIM(account.Descripcion)), ''), CONCAT(account.Nivel1, '.', account.Nivel2, '.', account.Nivel3)) END),

            @N1_RET_IVA = MAX(CASE WHEN defaults.CuentaClave = 'RETENCION_IVA' THEN account.Nivel1 END),
            @N2_RET_IVA = MAX(CASE WHEN defaults.CuentaClave = 'RETENCION_IVA' THEN account.Nivel2 END),
            @N3_RET_IVA = MAX(CASE WHEN defaults.CuentaClave = 'RETENCION_IVA' THEN account.Nivel3 END),
            @DESC_RET_IVA = MAX(CASE WHEN defaults.CuentaClave = 'RETENCION_IVA' THEN COALESCE(NULLIF(LTRIM(RTRIM(account.Descripcion)), ''), CONCAT(account.Nivel1, '.', account.Nivel2, '.', account.Nivel3)) END),

            @N1_RET_ISR = MAX(CASE WHEN defaults.CuentaClave = 'RETENCION_ISR' THEN account.Nivel1 END),
            @N2_RET_ISR = MAX(CASE WHEN defaults.CuentaClave = 'RETENCION_ISR' THEN account.Nivel2 END),
            @N3_RET_ISR = MAX(CASE WHEN defaults.CuentaClave = 'RETENCION_ISR' THEN account.Nivel3 END),
            @DESC_RET_ISR = MAX(CASE WHEN defaults.CuentaClave = 'RETENCION_ISR' THEN COALESCE(NULLIF(LTRIM(RTRIM(account.Descripcion)), ''), CONCAT(account.Nivel1, '.', account.Nivel2, '.', account.Nivel3)) END),

            @N1_RET_IEPS = MAX(CASE WHEN defaults.CuentaClave = 'RETENCION_IEPS' THEN account.Nivel1 END),
            @N2_RET_IEPS = MAX(CASE WHEN defaults.CuentaClave = 'RETENCION_IEPS' THEN account.Nivel2 END),
            @N3_RET_IEPS = MAX(CASE WHEN defaults.CuentaClave = 'RETENCION_IEPS' THEN account.Nivel3 END),
            @DESC_RET_IEPS = MAX(CASE WHEN defaults.CuentaClave = 'RETENCION_IEPS' THEN COALESCE(NULLIF(LTRIM(RTRIM(account.Descripcion)), ''), CONCAT(account.Nivel1, '.', account.Nivel2, '.', account.Nivel3)) END),

            @N1_TOTAL_G = MAX(CASE WHEN defaults.CuentaClave = 'TOTAL_GASTO' THEN account.Nivel1 END),
            @N2_TOTAL_G = MAX(CASE WHEN defaults.CuentaClave = 'TOTAL_GASTO' THEN account.Nivel2 END),
            @N3_TOTAL_G = MAX(CASE WHEN defaults.CuentaClave = 'TOTAL_GASTO' THEN account.Nivel3 END),
            @DESC_TOTAL_G = MAX(CASE WHEN defaults.CuentaClave = 'TOTAL_GASTO' THEN COALESCE(NULLIF(LTRIM(RTRIM(account.Descripcion)), ''), CONCAT(account.Nivel1, '.', account.Nivel2, '.', account.Nivel3)) END),

            @N1_TOTAL_I = MAX(CASE WHEN defaults.CuentaClave = 'TOTAL_INGRESO' THEN account.Nivel1 END),
            @N2_TOTAL_I = MAX(CASE WHEN defaults.CuentaClave = 'TOTAL_INGRESO' THEN account.Nivel2 END),
            @N3_TOTAL_I = MAX(CASE WHEN defaults.CuentaClave = 'TOTAL_INGRESO' THEN account.Nivel3 END),
            @DESC_TOTAL_I = MAX(CASE WHEN defaults.CuentaClave = 'TOTAL_INGRESO' THEN COALESCE(NULLIF(LTRIM(RTRIM(account.Descripcion)), ''), CONCAT(account.Nivel1, '.', account.Nivel2, '.', account.Nivel3)) END)
        FROM dbo.CfdiPolizaCuentaDefault AS defaults
        INNER JOIN dbo.CuentasContables AS account
            ON account.id = defaults.CuentaContableId
           AND account.RFC = @RFC
        WHERE defaults.Rfc = @RFC
          AND defaults.CuentaClave IN
          (
              'SUBTOTAL_GASTO',
              'SUBTOTAL_INGRESO',
              'IVA_TRASLADADO',
              'IVA_ACREDITABLE',
              'IEPS_TRASLADADO',
              'IEPS_ACREDITABLE',
              'RETENCION_IVA',
              'RETENCION_ISR',
              'RETENCION_IEPS',
              'TOTAL_GASTO',
              'TOTAL_INGRESO'
          );
        ----------------------------------------------------------------------
        -- 5) Construir movimientos (IMPORTANTE: Concepto = Transacciones.Concepto)
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
            @ConceptoRC VARCHAR(200);

        -- Si la p�liza tiene concepto, �salo; si viene vac�o, usa fallback razonable.
        SET @ConceptoRC =
            LEFT(
                COALESCE(NULLIF(LTRIM(RTRIM(ISNULL(@ConceptoTrans,''))),''), ISNULL(@EMISOR,''), ISNULL(@RECEPTOR,'')),
                200
            );

        IF @EsGasto = 1
        BEGIN
            IF ISNULL(@SubTotalDesc,0) <> 0
                INSERT INTO @RC VALUES (@N1_ACT16_G,@N2_ACT16_G,@N3_ACT16_G,@DESC_ACT16_G,@ConceptoRC,CAST(@SubTotalDesc AS MONEY),0);

            IF ISNULL(@IVA,0) <> 0
                INSERT INTO @RC VALUES (@N1_IVA_ACRED,@N2_IVA_ACRED,@N3_IVA_ACRED,@DESC_IVA_ACRED,@ConceptoRC,CAST(@IVA AS MONEY),0);

            IF ISNULL(@IEPS,0) <> 0
                INSERT INTO @RC VALUES (@N1_IEPS_ACRED,@N2_IEPS_ACRED,@N3_IEPS_ACRED,@DESC_IEPS_ACRED,@ConceptoRC,CAST(@IEPS AS MONEY),0);

            IF ISNULL(@IVA_RETENIDO,0) <> 0
                INSERT INTO @RC VALUES (@N1_RET_IVA,@N2_RET_IVA,@N3_RET_IVA,@DESC_RET_IVA,@ConceptoRC,0,CAST(@IVA_RETENIDO AS MONEY));

            IF ISNULL(@ISR_RETENIDO,0) <> 0
                INSERT INTO @RC VALUES (@N1_RET_ISR,@N2_RET_ISR,@N3_RET_ISR,@DESC_RET_ISR,@ConceptoRC,0,CAST(@ISR_RETENIDO AS MONEY));

            IF ISNULL(@IEPS_RETENIDO,0) <> 0
                INSERT INTO @RC VALUES (@N1_RET_IEPS,@N2_RET_IEPS,@N3_RET_IEPS,@DESC_RET_IEPS,@ConceptoRC,0,CAST(@IEPS_RETENIDO AS MONEY));

            IF ISNULL(@TotalCFDI,0) <> 0
                INSERT INTO @RC VALUES (@N1_TOTAL_G,@N2_TOTAL_G,@N3_TOTAL_G,@DESC_TOTAL_G,@ConceptoRC,0,CAST(@TotalCFDI AS MONEY));
        END;

        IF @EsIngreso = 1
        BEGIN
            IF ISNULL(@SubTotalDesc,0) <> 0
                INSERT INTO @RC VALUES (@N1_ACT16_I,@N2_ACT16_I,@N3_ACT16_I,@DESC_ACT16_I,@ConceptoRC,0,CAST(@SubTotalDesc AS MONEY));

            IF ISNULL(@IVA,0) <> 0
                INSERT INTO @RC VALUES (@N1_IVA_TRAS,@N2_IVA_TRAS,@N3_IVA_TRAS,@DESC_IVA_TRAS,@ConceptoRC,0,CAST(@IVA AS MONEY));

            IF ISNULL(@IEPS,0) <> 0
                INSERT INTO @RC VALUES (@N1_IEPS_TRAS,@N2_IEPS_TRAS,@N3_IEPS_TRAS,@DESC_IEPS_TRAS,@ConceptoRC,0,CAST(@IEPS AS MONEY));

            IF ISNULL(@IVA_RETENIDO,0) <> 0
                INSERT INTO @RC VALUES (@N1_RET_IVA,@N2_RET_IVA,@N3_RET_IVA,@DESC_RET_IVA,@ConceptoRC,CAST(@IVA_RETENIDO AS MONEY),0);

            IF ISNULL(@ISR_RETENIDO,0) <> 0
                INSERT INTO @RC VALUES (@N1_RET_ISR,@N2_RET_ISR,@N3_RET_ISR,@DESC_RET_ISR,@ConceptoRC,CAST(@ISR_RETENIDO AS MONEY),0);

            IF ISNULL(@IEPS_RETENIDO,0) <> 0
                INSERT INTO @RC VALUES (@N1_RET_IEPS,@N2_RET_IEPS,@N3_RET_IEPS,@DESC_RET_IEPS,@ConceptoRC,CAST(@IEPS_RETENIDO AS MONEY),0);

            IF ISNULL(@TotalCFDI,0) <> 0
                INSERT INTO @RC VALUES (@N1_TOTAL_I,@N2_TOTAL_I,@N3_TOTAL_I,@DESC_TOTAL_I,@ConceptoRC,CAST(@TotalCFDI AS MONEY),0);
        END;

        ----------------------------------------------------------------------
        -- 6) Validar que haya movimientos y que cuadren
        ----------------------------------------------------------------------
        DECLARE @SumDebe MONEY, @SumHaber MONEY;
        SELECT @SumDebe = SUM(Debe), @SumHaber = SUM(Haber) FROM @RC;

        IF ISNULL(@SumDebe,0) = 0 AND ISNULL(@SumHaber,0) = 0
        BEGIN
            RAISERROR('El CFDI no gener� movimientos contables (SubTotal_Desc/Impuestos/Retenciones en cero).',16,1);
            ROLLBACK TRAN;
            RETURN;
        END;

        IF ISNULL(@SumDebe,0) <> ISNULL(@SumHaber,0)
        BEGIN
            DECLARE @SumDebeStr  VARCHAR(30),
                    @SumHaberStr VARCHAR(30);

            SET @SumDebeStr  = CONVERT(VARCHAR(30), @SumDebe);
            SET @SumHaberStr = CONVERT(VARCHAR(30), @SumHaber);

            RAISERROR('El registro contable generado no cuadra (Debe = %s, Haber = %s).',16,1,@SumDebeStr,@SumHaberStr);
            ROLLBACK TRAN;
            RETURN;
        END;

        ----------------------------------------------------------------------
        -- 7) Actualizar encabezado de Transacciones (p�liza existente)
        --    NOTA: si quieres conservar Concepto original SIEMPRE, dime y lo dejo fijo.
        ----------------------------------------------------------------------
        DECLARE
            @FechaPolizaEF  DATETIME,
            @FormaPagoEF    VARCHAR(10),
            @TipoPolizaEF   VARCHAR(50);

        SET @FechaPolizaEF  = ISNULL(@FechaCFDI, GETDATE());
        SET @FormaPagoEF    = LEFT(NULLIF(LTRIM(RTRIM(ISNULL(@FormaPagoCFDI,''))),''),10);

        SET @TipoPolizaEF = CASE
                                WHEN @EsIngreso = 1 THEN 'INGRESO'
                                WHEN @EsGasto   = 1 THEN 'EGRESO'
                                ELSE ISNULL(NULLIF(@TipoPolizaActual,''),'CFDI')
                            END;

        UPDATE dbo.Transacciones
        SET
            -- Concepto              = Concepto,            -- no se toca; queda el de la Transacci�n
            Fecha                 = @FechaPolizaEF,
            Comprobante           = CONCAT(ISNULL(@Serie,''),' ',ISNULL(@Folio,'')),
            Monto                 = @TotalCFDI,
            Facturado             = 1,
            NumeroFactura         = @UUID,
            Estatus               = 'GENERADA POR CFDI',
            Referencia            = CAST(@Comprobante_Id AS VARCHAR(200)),
            Cuenta                = 'CFDI-IMPUESTOS',
            Memo                  = Concepto,              -- opcional: memo = concepto actual
            EstatusDeAutorizacion = 'AUTOMATICA',
            Tipo_Poliza           = @TipoPolizaEF,
            Forma_Pago            = COALESCE(NULLIF(@FormaPagoEF,''), @FormaPagoActual),
            RFC                   = @RFC
        WHERE ID = @Transaccion_ID;

        ----------------------------------------------------------------------
        -- 8) Borrar y re-crear movimientos en Registro_Contable
        ----------------------------------------------------------------------
        DELETE dbo.Registro_Contable
        WHERE TransaccionID = @Transaccion_ID;

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
            @Transaccion_ID,
            CAST(@Comprobante_Id AS VARCHAR(200))
        FROM @RC rc;

        ----------------------------------------------------------------------
        -- 9) Ligar p�liza con el Comprobante (Transaccion_Comprobante)
        ----------------------------------------------------------------------
        IF @TienePoliza5505 = 1
        BEGIN
            UPDATE dbo.Transaccion_Comprobante
            SET Transaccion_ID = @Transaccion_ID,
                Monto          = @TotalCFDI
            WHERE Comprobante_ID = @Comprobante_Id
              AND Transaccion_ID = 5505;
        END
        ELSE IF EXISTS (
            SELECT 1
            FROM dbo.Transaccion_Comprobante tc
            WHERE tc.Comprobante_ID = @Comprobante_Id
              AND tc.Transaccion_ID = @Transaccion_ID
        )
        BEGIN
            UPDATE dbo.Transaccion_Comprobante
            SET Monto = @TotalCFDI
            WHERE Comprobante_ID = @Comprobante_Id
              AND Transaccion_ID = @Transaccion_ID;
        END
        ELSE
        BEGIN
            INSERT INTO dbo.Transaccion_Comprobante
                (Transaccion_ID, Comprobante_ID, Monto)
            VALUES
                (@Transaccion_ID, @Comprobante_Id, @TotalCFDI);
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
            SET TranID = @Transaccion_ID
            WHERE ID = @XML_Attachment_ID;
        END;

        COMMIT TRAN;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0
            ROLLBACK TRAN;

        DECLARE @ErrMsg NVARCHAR(4000) = ERROR_MESSAGE(),
                @ErrNum INT = ERROR_NUMBER();

        RAISERROR('Error en contabilidad.Regenerar_Poliza_Desde_Comprobante_En_Transaccion (%d): %s',
                   16, 1, @ErrNum, @ErrMsg) WITH NOWAIT;
    END CATCH;
END;


GO
