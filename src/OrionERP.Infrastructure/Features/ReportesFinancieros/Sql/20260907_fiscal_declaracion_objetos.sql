/*
  Declaracion mensual - funciones y procedimientos.

  Requiere 20260907_fiscal_declaracion_schema.sql aplicado primero.
  Todo es CREATE OR ALTER, asi que se puede volver a correr sin guardas.

  Principio de diseno
  -------------------
  Toda la clasificacion fiscal de un CFDI vive en fiscal.fn_Cfdi_Periodo y en
  ningun otro lado. Los tres reportes y el generador de poliza agregan sobre
  ella. Hoy conviven contabilidad.fn_HojaTrabajo_CFDIs y dbo.CALCULATE_TAXES,
  que clasifican distinto y por eso dan cifras distintas del mismo mes; esto
  existe para que eso no se repita.

  Reglas del SAT que se codifican aqui, verificadas contra el acuse de julio
  2026 de OHM191112Q26 (OHM191112Q26.38.2026.pdf):

    IVA trasladado   = IVA 16% de CFDI tipo I emitidos con MetodoPago PUE
                     + IVA 16% de complementos de pago emitidos cuya FechaPago
                       cae en el mes.

    IVA acreditable  = IVA 16% de CFDI tipo I recibidos que cumplan las tres
      (precarga SAT)   condiciones: MetodoPago PUE, FormaPago bancarizada (no
                       efectivo '01' ni 'por definir' '99') y UsoCFDI G01 o G03
                     + IVA 16% de complementos recibidos por FechaPago.
                     Reproduce exacto: 33 documentos, subtotal 161,884,
                     descuento 6,899, base 141,750, IVA 22,680, mas 4
                     complementos con base 2,418 e IVA 387.

    Ingresos ISR     = SubTotal menos Descuento de CFDI tipo I emitidos
                       vigentes del periodo, sin importar el metodo de pago
                       (el ISR de persona moral es devengado, no flujo),
                       menos los CFDI tipo E emitidos.

  El periodo de un comprobante sale de InformacionGlobal (ANIO/MESES) cuando
  existe, y de la fecha del comprobante cuando no. Es lo que hace que una
  factura global de enero timbrada en febrero declare en enero.
*/

-- Las opciones SET se congelan al crear cada objeto, y sqlcmd las trae apagadas
-- por omision. Sin QUOTED_IDENTIFIER ON, cualquier SELECT INTO que toque una
-- tabla con indice filtrado -como fiscal.DeclaracionPresentada- falla en
-- tiempo de ejecucion, no al crearse.
SET ANSI_NULLS ON;
GO
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER FUNCTION fiscal.fn_Cfdi_Periodo
(
  @Rfc varchar(50),
  @Ejercicio int,
  @Periodo tinyint = NULL   -- NULL = ejercicio completo
)
RETURNS TABLE
AS
RETURN
(
  SELECT
    c.Comprobante_Id,
    c.Fecha,
    c.FechaCancelacion,
    c.Estatus,
    tfd.UUID AS FolioFiscal,
    PeriodoAnio = ISNULL(TRY_CONVERT(int, ig.ANIO), YEAR(c.Fecha)),
    PeriodoMes  = ISNULL(TRY_CONVERT(int, ig.MESES), MONTH(c.Fecha)),
    RfcEmisor   = e.Rfc,
    RfcReceptor = r.Rfc,
    NombreEmisor   = e.Nombre,
    NombreReceptor = r.Nombre,
    EsEmitida  = CONVERT(bit, CASE WHEN e.Rfc = @Rfc THEN 1 ELSE 0 END),
    EsRecibida = CONVERT(bit, CASE WHEN r.Rfc = @Rfc THEN 1 ELSE 0 END),
    c.TipoDeComprobante,
    c.MetodoPago,
    c.FormaPago,
    r.UsoCFDI,
    EsVigente = CONVERT(bit, CASE WHEN c.FechaCancelacion IS NULL THEN 1 ELSE 0 END),
    EsPue     = CONVERT(bit, CASE WHEN ISNULL(c.MetodoPago, '') <> 'PPD' THEN 1 ELSE 0 END),
    EsBancarizada = CONVERT(bit, CASE WHEN ISNULL(c.FormaPago, '99') NOT IN ('01', '99') THEN 1 ELSE 0 END),
    UsoAcreditable = CONVERT(bit, CASE WHEN r.UsoCFDI IN ('G01', 'G03') THEN 1 ELSE 0 END),
    -- EsAcreditable codifica la regla legal (no la de precarga del SAT): PUE, uso
    -- G01/G03 y pago bancarizado o en efectivo hasta $2,000 (art. 27-III LISR,
    -- art. 5-I LIVA). El SAT no precarga el efectivo, pero <= $2,000 sigue siendo
    -- acreditable y se captura a mano en el portal; > $2,000 en efectivo no lo es.
    EsAcreditable = CONVERT(bit, CASE
      WHEN ISNULL(c.MetodoPago, '') <> 'PPD'
       AND r.UsoCFDI IN ('G01', 'G03')
       AND (ISNULL(c.FormaPago, '99') NOT IN ('01', '99')
            OR (ISNULL(c.FormaPago, '99') = '01' AND ISNULL(c.Total, 0) <= 2000))
      THEN 1 ELSE 0 END),
    Signo = CONVERT(smallint, CASE WHEN c.TipoDeComprobante = 'E' THEN -1 ELSE 1 END),
    SubTotal     = CAST(ISNULL(c.SubTotal, 0) AS decimal(19,4)),
    Descuento    = CAST(ISNULL(c.Descuento, 0) AS decimal(19,4)),
    SubTotalNeto = CAST(ISNULL(c.SubTotal, 0) - ISNULL(c.Descuento, 0) AS decimal(19,4)),
    Total        = CAST(ISNULL(c.Total, 0) AS decimal(19,4)),
    Base16     = ISNULL(tras.Base16, 0),
    Iva16      = ISNULL(tras.Iva16, 0),
    Base8      = ISNULL(tras.Base8, 0),
    Iva8       = ISNULL(tras.Iva8, 0),
    Base0      = ISNULL(tras.Base0, 0),
    BaseExento = ISNULL(tras.BaseExento, 0),
    Ieps       = ISNULL(tras.Ieps, 0),
    IvaRetenido  = ISNULL(rete.IvaRetenido, 0),
    IsrRetenido  = ISNULL(rete.IsrRetenido, 0),
    IepsRetenido = ISNULL(rete.IepsRetenido, 0),
    IncluirEnDeclaracion = CONVERT(bit, ISNULL(c.Incluir_En_Declaracion, 1)),
    FactorDeclaracion = CAST(ISNULL(c.Factor_Declaracion, 1.0) AS decimal(9,4))
  FROM cfdi.Comprobante AS c
  LEFT JOIN cfdi.Receptor AS r ON r.Comprobante_Id = c.Comprobante_Id
  LEFT JOIN cfdi.Emisor AS e ON e.Comprobante_Id = c.Comprobante_Id
  LEFT JOIN cfdi.InformacionGlobal AS ig ON ig.Comprobante_ID = c.Comprobante_Id
  LEFT JOIN cfdi.TimbreFiscalDigital AS tfd ON tfd.Comprobante_Id = c.Comprobante_Id
  OUTER APPLY
  (
    SELECT
      Base16 = SUM(CASE WHEN t.Impuesto = '002' AND t.TipoFactor = 'Tasa' AND ROUND(t.TasaOCuota, 4) = 0.16
                        THEN CAST(t.Base AS decimal(19,4)) ELSE 0 END),
      Iva16  = SUM(CASE WHEN t.Impuesto = '002' AND t.TipoFactor = 'Tasa' AND ROUND(t.TasaOCuota, 4) = 0.16
                        THEN CAST(t.Importe AS decimal(19,4)) ELSE 0 END),
      Base8  = SUM(CASE WHEN t.Impuesto = '002' AND t.TipoFactor = 'Tasa' AND ROUND(t.TasaOCuota, 4) = 0.08
                        THEN CAST(t.Base AS decimal(19,4)) ELSE 0 END),
      Iva8   = SUM(CASE WHEN t.Impuesto = '002' AND t.TipoFactor = 'Tasa' AND ROUND(t.TasaOCuota, 4) = 0.08
                        THEN CAST(t.Importe AS decimal(19,4)) ELSE 0 END),
      Base0  = SUM(CASE WHEN t.Impuesto = '002' AND t.TipoFactor = 'Tasa' AND ROUND(t.TasaOCuota, 4) = 0.00
                        THEN CAST(t.Base AS decimal(19,4)) ELSE 0 END),
      BaseExento = SUM(CASE WHEN t.Impuesto = '002' AND t.TipoFactor = 'Exento'
                        THEN CAST(t.Base AS decimal(19,4)) ELSE 0 END),
      Ieps   = SUM(CASE WHEN t.Impuesto = '003'
                        THEN CAST(t.Importe AS decimal(19,4)) ELSE 0 END)
    FROM cfdi.Impuestos AS i
    LEFT JOIN cfdi.Traslados AS ts ON ts.Impuestos_Id = i.Impuestos_Id
    LEFT JOIN cfdi.Traslado AS t ON t.Traslados_Id = ts.Traslados_Id
    WHERE i.Comprobante_Id = c.Comprobante_Id
  ) AS tras
  OUTER APPLY
  (
    SELECT
      IvaRetenido  = SUM(CASE WHEN x.Impuesto = '002' THEN CAST(x.Importe AS decimal(19,4)) ELSE 0 END),
      IsrRetenido  = SUM(CASE WHEN x.Impuesto = '001' THEN CAST(x.Importe AS decimal(19,4)) ELSE 0 END),
      IepsRetenido = SUM(CASE WHEN x.Impuesto = '003' THEN CAST(x.Importe AS decimal(19,4)) ELSE 0 END)
    FROM cfdi.Impuestos AS i
    LEFT JOIN cfdi.Retenciones AS rs ON rs.Impuestos_Id = i.Impuestos_Id
    LEFT JOIN cfdi.Retencion AS x ON x.Retenciones_Id = rs.Retenciones_Id
    WHERE i.Comprobante_Id = c.Comprobante_Id
  ) AS rete
  WHERE (e.Rfc = @Rfc OR r.Rfc = @Rfc)
    AND ISNULL(TRY_CONVERT(int, ig.ANIO), YEAR(c.Fecha)) = @Ejercicio
    AND (@Periodo IS NULL
         OR ISNULL(TRY_CONVERT(int, ig.MESES), MONTH(c.Fecha)) = @Periodo)
);
GO

/*--------------------------------------------------------------------------
  fn_Complementos_Periodo - complementos de pago (CFDI tipo P) del periodo.

  El mes de un complemento es el de su FechaPago, no el del timbrado ni el de
  la factura que salda. Es lo que hace que un pago de julio sobre una factura
  de mayo acredite IVA en julio.

  Se apoya en cfdi.vw_Pagos20_Resumen, que ya calcula Comp_Actos16 y Comp_IVA
  a nivel DoctoRelacionado (que es lo correcto: los traslados del pago se
  reparten entre los documentos que salda). Esa vista ya filtra por
  Incluir_En_Declaracion = 1.
--------------------------------------------------------------------------*/
CREATE OR ALTER FUNCTION fiscal.fn_Complementos_Periodo
(
  @Rfc varchar(50),
  @Ejercicio int,
  @Periodo tinyint = NULL   -- NULL = ejercicio completo
)
RETURNS TABLE
AS
RETURN
(
  SELECT
    v.Comprobante_Id,
    v.ComprobanteUUID,
    v.Pago_Id,
    v.DoctoRelacionado_Id,
    v.UUID_DoctoRelacionado,
    v.Folio,
    v.FechaPago,
    PeriodoAnio = YEAR(v.FechaPago),
    PeriodoMes  = MONTH(v.FechaPago),
    v.EmisorRfc,
    v.ReceptorRfc,
    EsEmitida  = CONVERT(bit, CASE WHEN v.EmisorRfc = @Rfc THEN 1 ELSE 0 END),
    EsRecibida = CONVERT(bit, CASE WHEN v.ReceptorRfc = @Rfc THEN 1 ELSE 0 END),
    v.FormaDePagoP,
    v.MonedaP,
    v.NumParcialidad,
    ImpPagado = CAST(ISNULL(v.ImpPagado, 0) AS decimal(19,4)),
    Base16    = CAST(ISNULL(v.Comp_Actos16, 0) AS decimal(19,4)),
    Iva16     = CAST(ISNULL(v.Comp_IVA, 0) AS decimal(19,4)),
    v.Poliza,
    v.Polizas
  FROM cfdi.vw_Pagos20_Resumen AS v
  WHERE (v.EmisorRfc = @Rfc OR v.ReceptorRfc = @Rfc)
    AND v.FechaPago IS NOT NULL
    AND YEAR(v.FechaPago) = @Ejercicio
    AND (@Periodo IS NULL OR MONTH(v.FechaPago) = @Periodo)
);
GO

/*--------------------------------------------------------------------------
  fn_Saldos_Contables - movimientos del ejercicio por mes y cuenta.

  Una fila por (Mes, Nivel1, Nivel2, Nivel3). Se deja al maximo detalle porque
  hay conceptos que solo existen a tercer nivel, como 113-01-05 (IVA retenido
  por plataforma) frente al resto de 113-01 (IVA a favor). Los llamadores
  agregan al nivel que necesiten.

  Mismo criterio que reporteFinanciero.Rpt_BalanzaComprobacion: los
  movimientos cuelgan de dbo.Transacciones por t.RFC y t.Fecha, no de la fecha
  del CFDI. Si una poliza de julio paga una factura de mayo, cuenta en julio,
  que es justo el desfase que la pestana de hallazgos senala.
--------------------------------------------------------------------------*/
CREATE OR ALTER FUNCTION fiscal.fn_Saldos_Contables
(
  @Rfc varchar(50),
  @Ejercicio int
)
RETURNS TABLE
AS
RETURN
(
  SELECT
    Mes = MONTH(t.Fecha),
    rc.Nivel1,
    rc.Nivel2,
    rc.Nivel3,
    Debe  = SUM(CAST(ISNULL(rc.Debe, 0) AS decimal(19,4))),
    Haber = SUM(CAST(ISNULL(rc.Haber, 0) AS decimal(19,4))),
    Saldo = SUM(CAST(ISNULL(rc.Debe, 0) - ISNULL(rc.Haber, 0) AS decimal(19,4)))
  FROM dbo.Registro_Contable AS rc
  INNER JOIN dbo.Transacciones AS t ON t.ID = rc.TransaccionID
  WHERE t.RFC = @Rfc
    AND t.Fecha >= DATEFROMPARTS(@Ejercicio, 1, 1)
    AND t.Fecha <  DATEFROMPARTS(@Ejercicio + 1, 1, 1)
  GROUP BY MONTH(t.Fecha), rc.Nivel1, rc.Nivel2, rc.Nivel3
);
GO

/*--------------------------------------------------------------------------
  fn_Coeficiente_Vigente - el coeficiente que aplica a una fecha dada.

  El coeficiente de un pago provisional es el de la ultima declaracion anual
  presentada ANTES de presentar ese pago, y por eso cambia a medio ano: OHM
  declaro enero y febrero de 2026 con 0.1166 (anual 2024) y de marzo en adelante
  con 0.3726 (anual 2025, presentada el 10/04/2026). Resolverlo por ejercicio en
  vez de por fecha equivocaria cinco de los siete meses ya presentados, y ademas
  romperia las complementarias: la de enero debe recalcularse con 0.1166, no con
  el coeficiente de hoy.
--------------------------------------------------------------------------*/
CREATE OR ALTER FUNCTION fiscal.fn_Coeficiente_Vigente
(
  @Rfc varchar(50),
  @Fecha date
)
RETURNS TABLE
AS
RETURN
(
  SELECT TOP (1)
    c.Coeficiente,
    c.Ejercicio AS EjercicioOrigen,
    c.FechaPresentacion,
    c.Origen
  FROM fiscal.CoeficienteUtilidad AS c
  WHERE c.Rfc = @Rfc
    AND c.FechaPresentacion IS NOT NULL
    AND c.FechaPresentacion <= @Fecha
  ORDER BY c.FechaPresentacion DESC, c.Ejercicio DESC
);
GO

/*--------------------------------------------------------------------------
  fn_Congruencia_Cfdi - IVA del CFDI contra el IVA que quedo en su poliza.

  Es la misma consulta que hasta ahora vivia embebida en C# dentro de
  DeclaracionPreviaService.ApplyCfdiCongruenceAsync. Se mueve aqui, con la
  misma firma de lista de ids, para que la pantalla de Declaracion Previa y la
  de Declaracion Mensual no puedan divergir: si una dice que un CFDI cuadra y
  la otra que no, el problema deja de ser de datos y pasa a ser de que hay dos
  definiciones de "cuadra".

  Prorratea cuando un CFDI se paga en varias polizas o una poliza paga varios
  CFDIs: el IVA esperado se reparte por (Monto asignado / Total del CFDI) y el
  IVA contable por (Monto asignado / total asignado en esa poliza).
--------------------------------------------------------------------------*/
CREATE OR ALTER FUNCTION fiscal.fn_Congruencia_Cfdi
(
  @ComprobanteIds varchar(max),
  @ContextRfc varchar(50),
  @Tolerancia decimal(19,4) = 1.00
)
RETURNS TABLE
AS
RETURN
(
  WITH TargetIds AS
  (
    SELECT DISTINCT TRY_CONVERT(int, value) AS ComprobanteId
    FROM STRING_SPLIT(@ComprobanteIds, ',')
    WHERE TRY_CONVERT(int, value) IS NOT NULL
  ),
  RegularLinks AS
  (
    SELECT
      cd.Comprobante_Id AS ComprobanteId,
      tc.Transaccion_ID AS TransaccionId,
      CAST(tc.Monto AS decimal(19,4)) AS MontoAsignado,
      CAST(cd.Total AS decimal(19,4)) AS Total,
      CAST(cd.IVA AS decimal(19,4)) AS Iva,
      CASE
        WHEN cd.RFC_EMISOR = @ContextRfc THEN 'Emitido'
        WHEN cd.RFC_RECEPTOR = @ContextRfc THEN 'Recibido'
        ELSE 'Otro'
      END AS Direccion,
      CAST(ISNULL(txAssigned.AsignadoRegular, 0) AS decimal(19,4)) AS TransaccionAsignadoRegular,
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
        END AS decimal(19,4)
      ) AS IvaContableTransaccion
    FROM TargetIds AS ids
    JOIN cfdi.Comprobante_Detalle AS cd ON cd.Comprobante_Id = ids.ComprobanteId
    JOIN dbo.Transaccion_Comprobante AS tc ON tc.Comprobante_ID = cd.Comprobante_Id
    JOIN dbo.Transacciones AS t ON t.ID = tc.Transaccion_ID
    OUTER APPLY
    (
      SELECT SUM(CAST(tc2.Monto AS decimal(19,4))) AS AsignadoRegular
      FROM dbo.Transaccion_Comprobante AS tc2
      JOIN cfdi.Comprobante AS c2 ON c2.Comprobante_Id = tc2.Comprobante_ID
      WHERE tc2.Transaccion_ID = tc.Transaccion_ID
        AND c2.TipoDeComprobante IN ('I', 'N', 'E')
    ) AS txAssigned
    OUTER APPLY
    (
      SELECT SUM(CAST(rc.Debe AS decimal(19,4))) AS Debe, SUM(CAST(rc.Haber AS decimal(19,4))) AS Haber
      FROM dbo.Registro_Contable AS rc
      WHERE rc.TransaccionID = tc.Transaccion_ID AND rc.Nivel1 = '208'
    ) AS iva208
    OUTER APPLY
    (
      SELECT SUM(CAST(rc.Debe AS decimal(19,4))) AS Debe, SUM(CAST(rc.Haber AS decimal(19,4))) AS Haber
      FROM dbo.Registro_Contable AS rc
      WHERE rc.TransaccionID = tc.Transaccion_ID AND rc.Nivel1 = '118'
    ) AS iva118
    WHERE cd.TipoDeComprobante IN ('I', 'N', 'E')
  ),
  RegularStatus AS
  (
    SELECT
      rl.ComprobanteId,
      rl.TransaccionId,
      rl.MontoAsignado,
      rl.Total,
      rl.Direccion,
      CAST(CASE WHEN rl.Total <> 0 THEN rl.Iva * (rl.MontoAsignado / rl.Total) ELSE 0 END AS decimal(19,4)) AS IvaEsperado,
      CAST(CASE WHEN rl.TransaccionAsignadoRegular <> 0
                THEN rl.IvaContableTransaccion * (rl.MontoAsignado / rl.TransaccionAsignadoRegular)
                ELSE 0 END AS decimal(19,4)) AS IvaContable
    FROM RegularLinks AS rl
  )
  SELECT
    ComprobanteId,
    SUM(IvaEsperado) AS IvaEsperado,
    SUM(IvaContable) AS IvaContable,
    CAST(SUM(IvaEsperado) - SUM(IvaContable) AS decimal(19,4)) AS IvaDiferencia,
    CASE WHEN ABS(MAX(Total) - SUM(MontoAsignado)) <= @Tolerancia THEN 'OK' ELSE 'DIFERENCIA' END AS TotalCfdiStatus,
    CASE WHEN COUNT(DISTINCT TransaccionId) > 0 THEN 'OK' ELSE 'DIFERENCIA' END AS TransaccionAsignacionStatus,
    CASE
      WHEN MAX(Direccion) = 'Otro' OR MAX(Total) = 0 THEN 'NA'
      WHEN ABS(SUM(IvaEsperado) - SUM(IvaContable)) <= @Tolerancia THEN 'OK'
      ELSE 'DIFERENCIA'
    END AS IvaStatus
  FROM RegularStatus
  GROUP BY ComprobanteId
);
GO

/*--------------------------------------------------------------------------
  fn_Isr_Provisional - la cadena del pago provisional de ISR (persona moral)
  de un periodo, en las dos bases: la del SAT (precarga estricta, ingresos
  devengados de CFDI tipo I) y la nuestra (flujo, respetando
  Incluir_En_Declaracion y Factor_Declaracion, menos notas de credito).

  Es la unica definicion del calculo. Rpt_Declaracion_Mensual la lee para
  mostrarla renglon por renglon y Generar_Poliza_Isr registra su resultado en
  la poliza; si divergieran, el asiento no cuadraria contra lo que muestra la
  pantalla, que es justo el error que este modulo cierra.

  El coeficiente es el de fn_Coeficiente_Vigente a la fecha en que se presento
  el periodo (o a hoy si aun no se presenta), porque cambia a media año.
  SinHistorial = 1 cuando el periodo no es enero y faltan las declaraciones
  anteriores: sin ellas la cadena acumulada del SAT no se puede armar y las
  columnas *Sat quedan NULL en vez de fingir que no hubo ingresos previos.
--------------------------------------------------------------------------*/
CREATE OR ALTER FUNCTION fiscal.fn_Isr_Provisional
(
  @Rfc varchar(50),
  @Ejercicio int,
  @Periodo tinyint
)
RETURNS TABLE
AS
RETURN
(
  WITH MesCfdi AS
  (
    SELECT
      PeriodoMes,
      IngTipoI   = CAST(SUM(CASE WHEN EsEmitida = 1 AND EsVigente = 1 AND TipoDeComprobante = 'I'
                                 THEN SubTotalNeto ELSE 0 END) AS decimal(19,4)),
      IngTipoE   = CAST(SUM(CASE WHEN EsEmitida = 1 AND EsVigente = 1 AND TipoDeComprobante = 'E'
                                 THEN SubTotalNeto ELSE 0 END) AS decimal(19,4)),
      IngNuestro = CAST(SUM(CASE WHEN EsEmitida = 1 AND EsVigente = 1 AND IncluirEnDeclaracion = 1
                                  AND TipoDeComprobante IN ('I','E')
                                 THEN Signo * SubTotalNeto * FactorDeclaracion ELSE 0 END) AS decimal(19,4)),
      IsrRet     = CAST(SUM(CASE WHEN EsEmitida = 1 AND EsVigente = 1
                                 THEN IsrRetenido ELSE 0 END) AS decimal(19,4))
    FROM fiscal.fn_Cfdi_Periodo(@Rfc, @Ejercicio, NULL)
    WHERE PeriodoMes BETWEEN 1 AND 12
    GROUP BY PeriodoMes
  ),
  DeclAnt AS
  (
    SELECT
      IngDeclAnt   = CAST(SUM(d.IngresosNominalesPeriodo) AS decimal(19,4)),
      PagosProvAnt = CAST(SUM(d.IsrACargo) AS decimal(19,4))
    FROM fiscal.DeclaracionPresentada AS d
    WHERE d.Rfc = @Rfc AND d.Ejercicio = @Ejercicio AND d.Periodo < @Periodo
      AND d.Id = (SELECT MAX(d2.Id) FROM fiscal.DeclaracionPresentada AS d2
                  WHERE d2.Rfc = d.Rfc AND d2.Ejercicio = d.Ejercicio AND d2.Periodo = d.Periodo)
  ),
  DeclMes AS
  (
    SELECT TOP (1) IsrACargoDeclarado = CAST(d.IsrACargo AS decimal(19,4))
    FROM fiscal.DeclaracionPresentada AS d
    WHERE d.Rfc = @Rfc AND d.Ejercicio = @Ejercicio AND d.Periodo = @Periodo
    ORDER BY d.TipoDeclaracion DESC, d.NumeroComplementaria DESC, d.Id DESC
  ),
  Param AS
  (
    SELECT
      TasaIsr      = ISNULL((SELECT TasaIsr FROM fiscal.PerfilFiscal WHERE Rfc = @Rfc), 0.30),
      DedInmediata = CAST(ISNULL((SELECT DeduccionInmediata FROM fiscal.EjercicioFiscal WHERE Rfc = @Rfc AND Ejercicio = @Ejercicio), 0) AS decimal(19,4)),
      Ptu          = CAST(ISNULL((SELECT PtuPagada FROM fiscal.EjercicioFiscal WHERE Rfc = @Rfc AND Ejercicio = @Ejercicio), 0) AS decimal(19,4)),
      Perdidas     = CAST(ISNULL((SELECT PerdidasFiscalesPorAplicar FROM fiscal.EjercicioFiscal WHERE Rfc = @Rfc AND Ejercicio = @Ejercicio), 0) AS decimal(19,4)),
      FechaCoef    = ISNULL((SELECT MIN(FechaPresentacion) FROM fiscal.DeclaracionPresentada
                             WHERE Rfc = @Rfc AND Ejercicio = @Ejercicio AND Periodo = @Periodo),
                            CAST(SYSDATETIME() AS date))
  ),
  Ingresos AS
  (
    SELECT
      IngSatMes     = ISNULL((SELECT IngTipoI   FROM MesCfdi WHERE PeriodoMes = @Periodo), 0),
      IngTipoEMes   = ISNULL((SELECT IngTipoE   FROM MesCfdi WHERE PeriodoMes = @Periodo), 0),
      IngNuestroMes = ISNULL((SELECT IngNuestro FROM MesCfdi WHERE PeriodoMes = @Periodo), 0),
      IngNuestroAnt = ISNULL((SELECT SUM(IngNuestro) FROM MesCfdi WHERE PeriodoMes < @Periodo), 0),
      IsrRetAcum    = ISNULL((SELECT SUM(IsrRet)     FROM MesCfdi WHERE PeriodoMes <= @Periodo), 0)
  ),
  Base0 AS
  (
    SELECT
      i.IngSatMes, i.IngTipoEMes, i.IngNuestroMes, i.IngNuestroAnt, i.IsrRetAcum,
      da.IngDeclAnt, da.PagosProvAnt, dm.IsrACargoDeclarado,
      p.TasaIsr, p.DedInmediata, p.Ptu, p.Perdidas,
      co.Coeficiente, co.EjercicioOrigen AS CoeficienteOrigen,
      SinHistorial = CONVERT(bit, CASE WHEN @Periodo > 1 AND da.IngDeclAnt IS NULL THEN 1 ELSE 0 END),
      TotalIngSat     = CAST(i.IngSatMes + ISNULL(da.IngDeclAnt, 0) AS decimal(19,4)),
      TotalIngNuestro = CAST(i.IngNuestroMes + i.IngNuestroAnt AS decimal(19,4))
    FROM Ingresos AS i
    CROSS JOIN DeclAnt AS da
    CROSS JOIN Param AS p
    OUTER APPLY (SELECT * FROM DeclMes) AS dm
    OUTER APPLY fiscal.fn_Coeficiente_Vigente(@Rfc, p.FechaCoef) AS co
  ),
  Util AS
  (
    SELECT b.*,
      UtilSat     = CAST(b.TotalIngSat * ISNULL(b.Coeficiente, 0) AS decimal(19,4)),
      UtilNuestro = CAST(b.TotalIngNuestro * ISNULL(b.Coeficiente, 0) AS decimal(19,4))
    FROM Base0 AS b
  ),
  Grav AS
  (
    SELECT u.*,
      BaseSat     = CAST(CASE WHEN u.UtilSat - u.DedInmediata - u.Ptu - u.Perdidas < 0 THEN 0
                              ELSE u.UtilSat - u.DedInmediata - u.Ptu - u.Perdidas END AS decimal(19,4)),
      BaseNuestro = CAST(CASE WHEN u.UtilNuestro - u.DedInmediata - u.Ptu - u.Perdidas < 0 THEN 0
                              ELSE u.UtilNuestro - u.DedInmediata - u.Ptu - u.Perdidas END AS decimal(19,4))
    FROM Util AS u
  ),
  Caus AS
  (
    SELECT g.*,
      CausadoSat     = CAST(g.BaseSat * g.TasaIsr AS decimal(19,4)),
      CausadoNuestro = CAST(g.BaseNuestro * g.TasaIsr AS decimal(19,4))
    FROM Grav AS g
  )
  SELECT
    IngSatMes, IngTipoEMes, IngNuestroMes, IngNuestroAnt,
    IngDeclAnt, PagosProvAnt, IsrRetAcum, IsrACargoDeclarado,
    TasaIsr, DedInmediata, Ptu, Perdidas, Coeficiente, CoeficienteOrigen, SinHistorial,
    TotalIngSat, TotalIngNuestro, UtilSat, UtilNuestro, BaseSat, BaseNuestro,
    CausadoSat, CausadoNuestro,
    IsrCargoSat     = CAST(CausadoSat     - ISNULL(PagosProvAnt, 0) - IsrRetAcum AS decimal(19,4)),
    IsrCargoNuestro = CAST(CausadoNuestro - ISNULL(PagosProvAnt, 0) - IsrRetAcum AS decimal(19,4))
  FROM Caus
);
GO

/*--------------------------------------------------------------------------
  Rpt_Declaracion_Mensual - las cifras del mes, en el orden del formato SAT.

  Devuelve siete conjuntos: encabezado, renglones de ISR, renglones de IVA,
  conciliacion CFDI/contabilidad/declarado, retenciones, asiento de cierre de
  IVA y asiento de provision del pago provisional de ISR.

  Cada renglon de ISR e IVA trae tres columnas y esa es la idea entera:

    ValorSat       lo que el portal va a precargar. Aplica la regla estricta
                   del SAT e ignora deliberadamente Incluir_En_Declaracion,
                   porque el SAT no sabe de nuestras exclusiones manuales.

    ValorNuestro   lo que sostenemos que es correcto: base de flujo efectivo
                   (PUE mas complementos, menos notas de credito), respetando
                   Incluir_En_Declaracion y Factor_Declaracion.

    ValorDeclarado lo que ya se presento, si es que se presento.

  La diferencia entre las dos primeras es exactamente la correccion que hoy se
  hace a mano cada mes, y cada CFDI que la causa aparece en la pestana de
  hallazgos con el motivo.
--------------------------------------------------------------------------*/
CREATE OR ALTER PROCEDURE fiscal.Rpt_Declaracion_Mensual
  @Rfc varchar(50),
  @Ejercicio int,
  @Periodo tinyint
AS
BEGIN
  SET NOCOUNT ON;

  IF @Rfc IS NULL OR LTRIM(RTRIM(@Rfc)) = ''
    THROW 51200, 'El parametro @Rfc es obligatorio.', 1;
  IF @Periodo NOT BETWEEN 1 AND 12
    THROW 51201, 'El parametro @Periodo debe estar entre 1 y 12.', 1;
  IF @Ejercicio NOT BETWEEN 2000 AND 2100
    THROW 51202, 'El parametro @Ejercicio esta fuera de rango.', 1;

  DECLARE @TipoPersona char(1), @RegimenFiscal varchar(3), @TasaIsr decimal(9,4),
          @DiaVencimiento tinyint, @ObligadoIva bit;

  SELECT @TipoPersona = TipoPersona, @RegimenFiscal = RegimenFiscal, @TasaIsr = TasaIsr,
         @DiaVencimiento = DiaVencimiento, @ObligadoIva = ObligadoIva
  FROM fiscal.PerfilFiscal WHERE Rfc = @Rfc;

  -- Sin perfil no se inventa nada: se asume persona moral al 30% y la pagina
  -- avisa, en vez de calcular en silencio con supuestos que nadie reviso.
  SET @TipoPersona   = ISNULL(@TipoPersona, 'M');
  SET @TasaIsr       = ISNULL(@TasaIsr, 0.30);
  SET @DiaVencimiento = ISNULL(@DiaVencimiento, 17);
  SET @ObligadoIva   = ISNULL(@ObligadoIva, 1);

  -- Si el periodo ya se presento se usa la fecha real de presentacion, para que
  -- una complementaria reproduzca el coeficiente vigente aquel dia. Si aun no se
  -- presenta, se usa hoy.
  DECLARE @FechaCoeficiente date = ISNULL(
    (SELECT MIN(FechaPresentacion) FROM fiscal.DeclaracionPresentada
     WHERE Rfc = @Rfc AND Ejercicio = @Ejercicio AND Periodo = @Periodo),
    CAST(SYSDATETIME() AS date));

  DECLARE @Coeficiente decimal(9,4), @CoeficienteOrigen int;
  SELECT @Coeficiente = Coeficiente, @CoeficienteOrigen = EjercicioOrigen
  FROM fiscal.fn_Coeficiente_Vigente(@Rfc, @FechaCoeficiente);

  DECLARE @Ptu decimal(19,4), @Perdidas decimal(19,4), @DedInmediata decimal(19,4), @ProporcionIva decimal(9,4);
  SELECT @Ptu = PtuPagada, @Perdidas = PerdidasFiscalesPorAplicar,
         @DedInmediata = DeduccionInmediata, @ProporcionIva = ProporcionIva
  FROM fiscal.EjercicioFiscal WHERE Rfc = @Rfc AND Ejercicio = @Ejercicio;
  SET @Ptu = ISNULL(@Ptu, 0);
  SET @Perdidas = ISNULL(@Perdidas, 0);
  SET @DedInmediata = ISNULL(@DedInmediata, 0);
  SET @ProporcionIva = ISNULL(@ProporcionIva, 1.0);

  -- DATENAME(MONTH, ...) devuelve el mes en el idioma del login, que en este
  -- servidor es ingles. El nombre del mes se imprime en la pagina y se copia al
  -- portal, asi que se fija aqui y no se deja al azar de la sesion.
  DECLARE @NombreMes varchar(12) = CHOOSE(@Periodo,
    'ENERO','FEBRERO','MARZO','ABRIL','MAYO','JUNIO',
    'JULIO','AGOSTO','SEPTIEMBRE','OCTUBRE','NOVIEMBRE','DICIEMBRE');

  DECLARE @FechaVencimiento date = DATEFROMPARTS(
      CASE WHEN @Periodo = 12 THEN @Ejercicio + 1 ELSE @Ejercicio END,
      CASE WHEN @Periodo = 12 THEN 1 ELSE @Periodo + 1 END,
      @DiaVencimiento);

  /*----------------------------------------------------------------------
    Agregados de CFDI por mes del ejercicio completo. Se necesita la serie
    entera y no solo el mes porque el ISR provisional es acumulado: el pago
    de julio se calcula sobre los ingresos de enero a julio.
  ----------------------------------------------------------------------*/
  CREATE TABLE #Agg
  (
    Mes tinyint NOT NULL PRIMARY KEY,
    IngresosTipoI decimal(19,4) NOT NULL DEFAULT 0,
    IngresosTipoE decimal(19,4) NOT NULL DEFAULT 0,
    IngresosNuestro decimal(19,4) NOT NULL DEFAULT 0,
    TrasBase16 decimal(19,4) NOT NULL DEFAULT 0,
    TrasIva16 decimal(19,4) NOT NULL DEFAULT 0,
    TrasBase0 decimal(19,4) NOT NULL DEFAULT 0,
    TrasBaseExento decimal(19,4) NOT NULL DEFAULT 0,
    TrasBase16Comp decimal(19,4) NOT NULL DEFAULT 0,
    TrasIva16Comp decimal(19,4) NOT NULL DEFAULT 0,
    AcredBase16Sat decimal(19,4) NOT NULL DEFAULT 0,
    AcredIva16Sat decimal(19,4) NOT NULL DEFAULT 0,
    AcredBase0Sat decimal(19,4) NOT NULL DEFAULT 0,
    AcredBase16Comp decimal(19,4) NOT NULL DEFAULT 0,
    AcredIva16Comp decimal(19,4) NOT NULL DEFAULT 0,
    AcredIva16Pue decimal(19,4) NOT NULL DEFAULT 0,
    AcredIva16Efvo decimal(19,4) NOT NULL DEFAULT 0,
    AcredIva16Todas decimal(19,4) NOT NULL DEFAULT 0,
    AcredIva16TipoE decimal(19,4) NOT NULL DEFAULT 0,
    IsrRetenido decimal(19,4) NOT NULL DEFAULT 0,
    IvaRetenidoEmitidas decimal(19,4) NOT NULL DEFAULT 0,
    IvaRetenidoRecibidas decimal(19,4) NOT NULL DEFAULT 0,
    DocsEmitidasVigentes int NOT NULL DEFAULT 0,
    DocsEmitidasCanceladas int NOT NULL DEFAULT 0,
    DocsRecibidasSat int NOT NULL DEFAULT 0,
    DocsCompRecibidos int NOT NULL DEFAULT 0
  );

  INSERT INTO #Agg (Mes) SELECT v.Mes FROM (VALUES (1),(2),(3),(4),(5),(6),(7),(8),(9),(10),(11),(12)) v(Mes);

  ;WITH c AS (SELECT * FROM fiscal.fn_Cfdi_Periodo(@Rfc, @Ejercicio, NULL)),
  m AS
  (
    SELECT
      Mes = PeriodoMes,
      IngresosTipoI = SUM(CASE WHEN EsEmitida=1 AND EsVigente=1 AND TipoDeComprobante='I' THEN SubTotalNeto ELSE 0 END),
      IngresosTipoE = SUM(CASE WHEN EsEmitida=1 AND EsVigente=1 AND TipoDeComprobante='E' THEN SubTotalNeto ELSE 0 END),
      IngresosNuestro = SUM(CASE WHEN EsEmitida=1 AND EsVigente=1 AND IncluirEnDeclaracion=1 AND TipoDeComprobante IN ('I','E')
                                 THEN Signo * SubTotalNeto * FactorDeclaracion ELSE 0 END),
      TrasBase16 = SUM(CASE WHEN EsEmitida=1 AND EsVigente=1 AND TipoDeComprobante='I' AND EsPue=1 THEN Base16 ELSE 0 END),
      TrasIva16  = SUM(CASE WHEN EsEmitida=1 AND EsVigente=1 AND TipoDeComprobante='I' AND EsPue=1 THEN Iva16 ELSE 0 END),
      TrasBase0  = SUM(CASE WHEN EsEmitida=1 AND EsVigente=1 AND TipoDeComprobante='I' AND EsPue=1 THEN Base0 ELSE 0 END),
      TrasBaseExento = SUM(CASE WHEN EsEmitida=1 AND EsVigente=1 AND TipoDeComprobante='I' AND EsPue=1 THEN BaseExento ELSE 0 END),
      AcredBase16Sat = SUM(CASE WHEN EsRecibida=1 AND EsVigente=1 AND TipoDeComprobante='I'
                                 AND EsPue=1 AND EsBancarizada=1 AND UsoAcreditable=1 THEN Base16 ELSE 0 END),
      AcredIva16Sat  = SUM(CASE WHEN EsRecibida=1 AND EsVigente=1 AND TipoDeComprobante='I'
                                 AND EsPue=1 AND EsBancarizada=1 AND UsoAcreditable=1 THEN Iva16 ELSE 0 END),
      AcredBase0Sat  = SUM(CASE WHEN EsRecibida=1 AND EsVigente=1 AND TipoDeComprobante='I'
                                 AND EsPue=1 AND EsBancarizada=1 AND UsoAcreditable=1 THEN Base0 ELSE 0 END),
      -- "Nuestro" acreditable: la regla legal, no la precarga del SAT. Incluye el
      -- efectivo <= $2,000 (art. 27-III LISR) y excluye el efectivo > $2,000 y el
      -- uso de CFDI fuera de G01/G03.
      AcredIva16Pue  = SUM(CASE WHEN EsRecibida=1 AND EsVigente=1 AND TipoDeComprobante='I'
                                 AND EsAcreditable=1 AND IncluirEnDeclaracion=1 THEN Iva16 * FactorDeclaracion ELSE 0 END),
      -- La parte del renglon anterior que el SAT NO precarga: efectivo acreditable
      -- por ser <= $2,000. Es lo que hay que capturar a mano en el portal.
      AcredIva16Efvo = SUM(CASE WHEN EsRecibida=1 AND EsVigente=1 AND TipoDeComprobante='I'
                                 AND EsAcreditable=1 AND EsBancarizada=0 AND IncluirEnDeclaracion=1
                                 THEN Iva16 * FactorDeclaracion ELSE 0 END),
      AcredIva16Todas = SUM(CASE WHEN EsRecibida=1 AND EsVigente=1 AND TipoDeComprobante='I' THEN Iva16 ELSE 0 END),
      AcredIva16TipoE = SUM(CASE WHEN EsRecibida=1 AND EsVigente=1 AND TipoDeComprobante='E' THEN Iva16 ELSE 0 END),
      IsrRetenido = SUM(CASE WHEN EsEmitida=1 AND EsVigente=1 THEN IsrRetenido ELSE 0 END),
      IvaRetenidoEmitidas = SUM(CASE WHEN EsEmitida=1 AND EsVigente=1 THEN IvaRetenido ELSE 0 END),
      IvaRetenidoRecibidas = SUM(CASE WHEN EsRecibida=1 AND EsVigente=1 THEN IvaRetenido ELSE 0 END),
      DocsEmitidasVigentes = SUM(CASE WHEN EsEmitida=1 AND EsVigente=1 AND TipoDeComprobante='I' THEN 1 ELSE 0 END),
      DocsEmitidasCanceladas = SUM(CASE WHEN EsEmitida=1 AND EsVigente=0 AND TipoDeComprobante='I' THEN 1 ELSE 0 END),
      DocsRecibidasSat = SUM(CASE WHEN EsRecibida=1 AND EsVigente=1 AND TipoDeComprobante='I'
                                 AND EsPue=1 AND EsBancarizada=1 AND UsoAcreditable=1 THEN 1 ELSE 0 END)
    FROM c
    WHERE PeriodoMes BETWEEN 1 AND 12
    GROUP BY PeriodoMes
  )
  UPDATE a SET
    IngresosTipoI = m.IngresosTipoI, IngresosTipoE = m.IngresosTipoE, IngresosNuestro = m.IngresosNuestro,
    TrasBase16 = m.TrasBase16, TrasIva16 = m.TrasIva16, TrasBase0 = m.TrasBase0, TrasBaseExento = m.TrasBaseExento,
    AcredBase16Sat = m.AcredBase16Sat, AcredIva16Sat = m.AcredIva16Sat, AcredBase0Sat = m.AcredBase0Sat,
    AcredIva16Pue = m.AcredIva16Pue, AcredIva16Efvo = m.AcredIva16Efvo,
    AcredIva16Todas = m.AcredIva16Todas, AcredIva16TipoE = m.AcredIva16TipoE,
    IsrRetenido = m.IsrRetenido, IvaRetenidoEmitidas = m.IvaRetenidoEmitidas,
    IvaRetenidoRecibidas = m.IvaRetenidoRecibidas,
    DocsEmitidasVigentes = m.DocsEmitidasVigentes, DocsEmitidasCanceladas = m.DocsEmitidasCanceladas,
    DocsRecibidasSat = m.DocsRecibidasSat
  FROM #Agg a JOIN m ON m.Mes = a.Mes;

  ;WITH p AS (SELECT * FROM fiscal.fn_Complementos_Periodo(@Rfc, @Ejercicio, NULL)),
  mp AS
  (
    SELECT
      Mes = PeriodoMes,
      TrasBase16Comp = SUM(CASE WHEN EsEmitida=1 THEN Base16 ELSE 0 END),
      TrasIva16Comp  = SUM(CASE WHEN EsEmitida=1 THEN Iva16 ELSE 0 END),
      AcredBase16Comp = SUM(CASE WHEN EsRecibida=1 THEN Base16 ELSE 0 END),
      AcredIva16Comp  = SUM(CASE WHEN EsRecibida=1 THEN Iva16 ELSE 0 END),
      DocsCompRecibidos = SUM(CASE WHEN EsRecibida=1 THEN 1 ELSE 0 END)
    FROM p GROUP BY PeriodoMes
  )
  UPDATE a SET
    TrasBase16Comp = mp.TrasBase16Comp, TrasIva16Comp = mp.TrasIva16Comp,
    AcredBase16Comp = mp.AcredBase16Comp, AcredIva16Comp = mp.AcredIva16Comp,
    DocsCompRecibidos = mp.DocsCompRecibidos
  FROM #Agg a JOIN mp ON mp.Mes = a.Mes;

  /*----------------------------------------------------------------------
    Lo declarado. Para "periodos anteriores" el SAT solo considera las
    declaraciones presentadas y pagadas, asi que se toma de lo importado y no
    de lo calculado; si falta el historico el renglon queda en NULL y la
    pagina lo senala en vez de inventar un acumulado.
  ----------------------------------------------------------------------*/
  DECLARE @DeclId int, @DeclNumOp varchar(30), @DeclFecha date, @DeclTipo char(1),
          @DeclIngresos decimal(19,4), @DeclIsrACargo decimal(19,4),
          @DeclIvaACargo decimal(19,4), @DeclIvaAcreditable decimal(19,4),
          @DeclSaldoFavor decimal(19,4), @DeclCoeficiente decimal(9,4);

  SELECT TOP (1)
    @DeclId = Id, @DeclNumOp = NumeroOperacion, @DeclFecha = FechaPresentacion,
    @DeclTipo = TipoDeclaracion, @DeclIngresos = IngresosNominalesPeriodo,
    @DeclIsrACargo = IsrACargo, @DeclIvaACargo = TotalIvaACargo,
    @DeclIvaAcreditable = TotalIvaAcreditable, @DeclSaldoFavor = IvaSaldoAFavor,
    @DeclCoeficiente = CoeficienteUtilidad
  FROM fiscal.DeclaracionPresentada
  WHERE Rfc = @Rfc AND Ejercicio = @Ejercicio AND Periodo = @Periodo
  ORDER BY TipoDeclaracion DESC, NumeroComplementaria DESC, Id DESC;

  DECLARE @IngDeclAnt decimal(19,4), @PagosProvAnt decimal(19,4);
  SELECT
    @IngDeclAnt = SUM(d.IngresosNominalesPeriodo),
    @PagosProvAnt = SUM(d.IsrACargo)
  FROM fiscal.DeclaracionPresentada AS d
  WHERE d.Rfc = @Rfc AND d.Ejercicio = @Ejercicio AND d.Periodo < @Periodo
    AND d.Id = (SELECT MAX(d2.Id) FROM fiscal.DeclaracionPresentada d2
                WHERE d2.Rfc = d.Rfc AND d2.Ejercicio = d.Ejercicio AND d2.Periodo = d.Periodo);

  /*----------------------------------------------------------------------
    Cadena de ISR (persona moral). Todo el calculo vive en
    fiscal.fn_Isr_Provisional; aqui solo se lee para mostrarlo renglon por
    renglon en el orden del formato del SAT. @IsrRetMes se saca aparte porque
    la pestana de retenciones lo compara contra 216-03.
  ----------------------------------------------------------------------*/
  DECLARE @IsrRetMes decimal(19,4) =
    ISNULL((SELECT IsrRetenido FROM #Agg WHERE Mes = @Periodo), 0);

  DECLARE @IngSatMes decimal(19,4), @IngNuestroMes decimal(19,4), @IngTipoEMes decimal(19,4),
          @IngNuestroAnt decimal(19,4), @IsrRetAcum decimal(19,4), @SinHistorial bit,
          @TotalIngSat decimal(19,4), @TotalIngNuestro decimal(19,4),
          @UtilSat decimal(19,4), @UtilNuestro decimal(19,4),
          @BaseSat decimal(19,4), @BaseNuestro decimal(19,4),
          @CausadoSat decimal(19,4), @CausadoNuestro decimal(19,4),
          @IsrCargoSat decimal(19,4), @IsrCargoNuestro decimal(19,4);

  SELECT
    @IngSatMes       = IngSatMes,
    @IngNuestroMes   = IngNuestroMes,
    @IngTipoEMes     = IngTipoEMes,
    @IngNuestroAnt   = IngNuestroAnt,
    @IsrRetAcum      = IsrRetAcum,
    @SinHistorial    = SinHistorial,
    @TotalIngSat     = TotalIngSat,
    @TotalIngNuestro = TotalIngNuestro,
    @UtilSat         = UtilSat,
    @UtilNuestro     = UtilNuestro,
    @BaseSat         = BaseSat,
    @BaseNuestro     = BaseNuestro,
    @CausadoSat      = CausadoSat,
    @CausadoNuestro  = CausadoNuestro,
    @IsrCargoSat     = IsrCargoSat,
    @IsrCargoNuestro = IsrCargoNuestro
  FROM fiscal.fn_Isr_Provisional(@Rfc, @Ejercicio, @Periodo);

  /*----------------------------------------------------------------------
    Cadena de IVA.
      ValorSat     = precarga estricta (PUE + bancarizada + G01/G03) + complementos
      ValorNuestro = base de flujo: PUE + complementos - notas de credito
    Para la base de actos pagados se conserva tambien el Actos_16 operativo de
    Declaracion Previa. Ese importe incluye efectivo y solo obedece la marca
    Incluir_En_Declaracion; es el que se captura manualmente en el portal.
    La tercera cifra, "IVA de todas las recibidas", va como renglon informativo
    porque es la que se ha venido capturando y conviene verla al lado.
  ----------------------------------------------------------------------*/
  DECLARE @TrasBase16 decimal(19,4), @TrasIva16 decimal(19,4), @TrasBase0 decimal(19,4),
          @TrasBaseEx decimal(19,4), @TrasBase16C decimal(19,4), @TrasIva16C decimal(19,4),
          @AcredBase16S decimal(19,4), @AcredIva16S decimal(19,4), @AcredBase0S decimal(19,4),
          @AcredBase16C decimal(19,4), @AcredIva16C decimal(19,4), @AcredIva16Pue decimal(19,4),
          @AcredIva16Efvo decimal(19,4),
          @AcredIva16Todas decimal(19,4), @AcredIva16E decimal(19,4), @IvaRetNos decimal(19,4);

  SELECT @TrasBase16 = TrasBase16, @TrasIva16 = TrasIva16, @TrasBase0 = TrasBase0,
         @TrasBaseEx = TrasBaseExento, @TrasBase16C = TrasBase16Comp, @TrasIva16C = TrasIva16Comp,
         @AcredBase16S = AcredBase16Sat, @AcredIva16S = AcredIva16Sat, @AcredBase0S = AcredBase0Sat,
         @AcredBase16C = AcredBase16Comp, @AcredIva16C = AcredIva16Comp,
         @AcredIva16Pue = AcredIva16Pue, @AcredIva16Efvo = AcredIva16Efvo,
         @AcredIva16Todas = AcredIva16Todas,
         @AcredIva16E = AcredIva16TipoE, @IvaRetNos = IvaRetenidoEmitidas
  FROM #Agg WHERE Mes = @Periodo;

  -- Debe coincidir centavo por centavo con la columna Actos16 de Declaracion
  -- Previa. No se reconstruye desde Base16: esa base XML conserva mas de dos
  -- decimales y, para agosto 2026 de OHM191112Q26, produciria 116,337.05 en vez
  -- de los 116,336.93 que el usuario revisa y captura. La columna SAT mantiene
  -- sus condiciones de precarga; la columna Nuestra solo excluye los CFDI que
  -- el usuario marco manualmente fuera de la declaracion.
  DECLARE @AcredActos16Sat decimal(19,4) = ISNULL((
    SELECT SUM(CAST(cd.Actos_16 AS decimal(19,4)))
    FROM fiscal.fn_Cfdi_Periodo(@Rfc, @Ejercicio, @Periodo) AS f
    INNER JOIN cfdi.COMPROBANTE_DETALLE AS cd ON cd.Comprobante_Id = f.Comprobante_Id
    WHERE f.EsRecibida = 1 AND f.EsVigente = 1 AND f.TipoDeComprobante = 'I'
      AND f.EsPue = 1 AND f.EsBancarizada = 1 AND f.UsoAcreditable = 1
  ), 0);

  DECLARE @AcredActos16Incluidos decimal(19,4) = ISNULL((
    SELECT SUM(CAST(cd.Actos_16 AS decimal(19,4)))
    FROM fiscal.fn_Cfdi_Periodo(@Rfc, @Ejercicio, @Periodo) AS f
    INNER JOIN cfdi.COMPROBANTE_DETALLE AS cd ON cd.Comprobante_Id = f.Comprobante_Id
    WHERE f.EsRecibida = 1 AND f.EsVigente = 1 AND f.TipoDeComprobante = 'I'
      AND f.EsPue = 1 AND f.IncluirEnDeclaracion = 1
  ), 0);

  DECLARE @IvaCargoSat decimal(19,4) = @TrasIva16 + @TrasIva16C;
  DECLARE @IvaAcredSat decimal(19,4) = CAST((@AcredIva16S + @AcredIva16C) * @ProporcionIva AS decimal(19,4));
  DECLARE @IvaAcredNuestro decimal(19,4) = CAST((@AcredIva16Pue + @AcredIva16C - @AcredIva16E) * @ProporcionIva AS decimal(19,4));
  DECLARE @SaldoSat decimal(19,4) = @IvaAcredSat + @IvaRetNos - @IvaCargoSat;
  DECLARE @SaldoNuestro decimal(19,4) = @IvaAcredNuestro + @IvaRetNos - @IvaCargoSat;

  DECLARE @Tol decimal(19,4) = 1.00;

  ------------------------------------------------------------------ 1) ENCABEZADO
  SELECT
    Rfc = @Rfc,
    Ejercicio = @Ejercicio,
    Periodo = @Periodo,
    NombreMes = @NombreMes,
    TipoPersona = @TipoPersona,
    RegimenFiscal = @RegimenFiscal,
    TasaIsr = @TasaIsr,
    Coeficiente = @Coeficiente,
    CoeficienteOrigen = @CoeficienteOrigen,
    ProporcionIva = @ProporcionIva,
    ObligadoIva = @ObligadoIva,
    FechaVencimiento = @FechaVencimiento,
    DiasParaVencimiento = DATEDIFF(DAY, CAST(SYSDATETIME() AS date), @FechaVencimiento),
    TienePerfil = CONVERT(bit, CASE WHEN EXISTS (SELECT 1 FROM fiscal.PerfilFiscal WHERE Rfc = @Rfc) THEN 1 ELSE 0 END),
    TieneCoeficiente = CONVERT(bit, CASE WHEN @Coeficiente IS NULL THEN 0 ELSE 1 END),
    TieneDeclaracion = CONVERT(bit, CASE WHEN @DeclId IS NULL THEN 0 ELSE 1 END),
    DeclaracionTipo = @DeclTipo,
    NumeroOperacion = @DeclNumOp,
    FechaPresentacion = @DeclFecha,
    TieneHistorialAnterior = CONVERT(bit, CASE WHEN @IngDeclAnt IS NULL AND @Periodo > 1 THEN 0 ELSE 1 END),
    TransaccionIdCierre = (SELECT TransaccionIdCierre FROM fiscal.DeclaracionCierre
                           WHERE Rfc = @Rfc AND Ejercicio = @Ejercicio AND Periodo = @Periodo),
    TransaccionIdIsr = (SELECT TransaccionIdIsr FROM fiscal.DeclaracionIsrProvisional
                        WHERE Rfc = @Rfc AND Ejercicio = @Ejercicio AND Periodo = @Periodo),
    IsrACargo = @IsrCargoNuestro,
    IvaSaldoAFavor = CASE WHEN @SaldoNuestro > 0 THEN @SaldoNuestro ELSE 0 END,
    IvaACargo = CASE WHEN @SaldoNuestro < 0 THEN -@SaldoNuestro ELSE 0 END;

  ------------------------------------------------------------------ 2) ISR
  ;WITH r (Orden, Concepto, Formato, EsTotal, ValorSat, ValorNuestro, ValorDeclarado) AS
  (
    SELECT  1, 'Suma de facturas emitidas de tipo Ingreso del periodo', 'money', 0, @IngSatMes, @IngSatMes, @DeclIngresos
    UNION ALL SELECT 2, 'Notas de credito emitidas (tipo E) del periodo', 'money', 0, 0, @IngTipoEMes, NULL
    UNION ALL SELECT 3, 'Ingresos nominales del periodo', 'money', 1, @IngSatMes, @IngNuestroMes, @DeclIngresos
    UNION ALL SELECT 4, 'Ingresos nominales de periodos anteriores', 'money', 0, @IngDeclAnt, @IngNuestroAnt, @IngDeclAnt
    UNION ALL SELECT 5, 'Total de ingresos nominales del periodo', 'money', 1,
                     CASE WHEN @SinHistorial = 1 THEN NULL ELSE @TotalIngSat END, @TotalIngNuestro, NULL
    UNION ALL SELECT 6, 'Coeficiente de utilidad', 'rate', 0, @Coeficiente, @Coeficiente, @DeclCoeficiente
    UNION ALL SELECT 7, 'Utilidad fiscal para pago provisional', 'money', 0, CASE WHEN @SinHistorial = 1 THEN NULL ELSE @UtilSat END, @UtilNuestro, NULL
    UNION ALL SELECT 8, 'Deduccion inmediata de inversiones', 'money', 0, @DedInmediata, @DedInmediata, NULL
    UNION ALL SELECT 9, 'PTU pagada en el ejercicio', 'money', 0, @Ptu, @Ptu, NULL
    UNION ALL SELECT 10, 'Perdidas fiscales de ejercicios anteriores', 'money', 0, @Perdidas, @Perdidas, NULL
    UNION ALL SELECT 11, 'Base gravable del pago provisional', 'money', 1, CASE WHEN @SinHistorial = 1 THEN NULL ELSE @BaseSat END, @BaseNuestro, NULL
    UNION ALL SELECT 12, 'Impuesto causado', 'money', 1, CASE WHEN @SinHistorial = 1 THEN NULL ELSE @CausadoSat END, @CausadoNuestro, NULL
    UNION ALL SELECT 13, 'Pagos provisionales efectuados de periodos anteriores', 'money', 0, @PagosProvAnt, @PagosProvAnt, NULL
    UNION ALL SELECT 14, 'Total de ISR retenido del periodo', 'money', 0, 0, @IsrRetAcum, NULL
    UNION ALL SELECT 15, 'ISR a cargo', 'money', 1, CASE WHEN @SinHistorial = 1 THEN NULL ELSE @IsrCargoSat END, @IsrCargoNuestro, @DeclIsrACargo
  )
  SELECT
    r.Orden, Seccion = 'ISR', r.Concepto, r.Formato, r.EsTotal,
    r.ValorSat, r.ValorNuestro, r.ValorDeclarado,
    DifSatNuestro = CASE WHEN r.Formato = 'rate' THEN NULL
                         ELSE ISNULL(r.ValorNuestro, 0) - ISNULL(r.ValorSat, 0) END,
    DifDeclarado  = CASE WHEN r.ValorDeclarado IS NULL THEN NULL
                         ELSE r.ValorDeclarado - ISNULL(r.ValorNuestro, 0) END,
    Estado = CASE
               WHEN r.ValorDeclarado IS NULL THEN 'PENDIENTE'
               WHEN ABS(r.ValorDeclarado - ISNULL(r.ValorNuestro, 0)) <= @Tol THEN 'OK'
               ELSE 'DIFERENCIA'
             END
  FROM r ORDER BY r.Orden;

  ------------------------------------------------------------------ 3) IVA
  ;WITH r (Orden, Concepto, Formato, EsTotal, ValorSat, ValorNuestro, ValorDeclarado) AS
  (
    SELECT  1, 'Actos gravados 16% - facturas emitidas tipo Ingreso (PUE)', 'money', 0, @TrasBase16, @TrasBase16, NULL
    UNION ALL SELECT  2, 'Actos gravados 16% - complementos de pago emitidos', 'money', 0, @TrasBase16C, @TrasBase16C, NULL
    UNION ALL SELECT  3, 'Valor de los actos o actividades gravados a la tasa 16%', 'money', 1, @TrasBase16 + @TrasBase16C, @TrasBase16 + @TrasBase16C, NULL
    UNION ALL SELECT  4, 'Valor de los actos gravados a la tasa 0%', 'money', 0, @TrasBase0, @TrasBase0, NULL
    UNION ALL SELECT  5, 'Valor de los actos exentos', 'money', 0, @TrasBaseEx, @TrasBaseEx, NULL
    UNION ALL SELECT  6, 'IVA 16% de facturas emitidas tipo Ingreso (PUE)', 'money', 0, @TrasIva16, @TrasIva16, NULL
    UNION ALL SELECT  7, 'IVA 16% de complementos de pago emitidos', 'money', 0, @TrasIva16C, @TrasIva16C, NULL
    UNION ALL SELECT  8, 'Total de IVA a cargo', 'money', 1, @IvaCargoSat, @IvaCargoSat, @DeclIvaACargo
    UNION ALL SELECT  9, 'Actos pagados 16% - facturas recibidas tipo Ingreso (incluidas manualmente)', 'money', 0, @AcredActos16Sat, @AcredActos16Incluidos, NULL
    UNION ALL SELECT 10, 'Actos pagados 16% - complementos de pago recibidos', 'money', 0, @AcredBase16C, @AcredBase16C, NULL
    UNION ALL SELECT 11, 'Total de actos o actividades pagados a la tasa del 16% (captura SAT)', 'money', 1,
                     @AcredActos16Sat + @AcredBase16C, @AcredActos16Incluidos + @AcredBase16C, NULL
    UNION ALL SELECT 12, 'Actos pagados a la tasa 0%', 'money', 0, @AcredBase0S, @AcredBase0S, NULL
    UNION ALL SELECT 13, 'IVA 16% de facturas recibidas tipo Ingreso', 'money', 0, @AcredIva16S, @AcredIva16Pue, NULL
    UNION ALL SELECT 14, 'IVA 16% de complementos de pago recibidos', 'money', 0, @AcredIva16C, @AcredIva16C, NULL
    UNION ALL SELECT 15, 'IVA 16% de notas de credito recibidas (tipo E)', 'money', 0, 0, -@AcredIva16E, NULL
    UNION ALL SELECT 16, 'Proporcion de IVA acreditable', 'rate', 0, @ProporcionIva, @ProporcionIva, NULL
    UNION ALL SELECT 17, 'Total de IVA acreditable', 'money', 1, @IvaAcredSat, @IvaAcredNuestro, @DeclIvaAcreditable
    UNION ALL SELECT 18, 'IVA retenido al contribuyente', 'money', 0, @IvaRetNos, @IvaRetNos, NULL
    UNION ALL SELECT 19, 'Saldo a favor', 'money', 1,
                     CASE WHEN @SaldoSat > 0 THEN @SaldoSat ELSE 0 END,
                     CASE WHEN @SaldoNuestro > 0 THEN @SaldoNuestro ELSE 0 END, @DeclSaldoFavor
    UNION ALL SELECT 20, 'IVA a cargo (a pagar)', 'money', 1,
                     CASE WHEN @SaldoSat < 0 THEN -@SaldoSat ELSE 0 END,
                     CASE WHEN @SaldoNuestro < 0 THEN -@SaldoNuestro ELSE 0 END, NULL
    UNION ALL SELECT 21, 'Informativo: IVA 16% de todas las facturas recibidas tipo I (incluye PPD)', 'money', 0, NULL, @AcredIva16Todas, NULL
    UNION ALL SELECT 22, 'Informativo: IVA de pagos en efectivo <= $2,000 (acreditable, no precargado - agregar en el portal)', 'money', 0, NULL, @AcredIva16Efvo, NULL
  )
  SELECT
    r.Orden, Seccion = 'IVA', r.Concepto, r.Formato, r.EsTotal,
    r.ValorSat, r.ValorNuestro, r.ValorDeclarado,
    DifSatNuestro = CASE WHEN r.Formato = 'rate' OR r.ValorSat IS NULL THEN NULL
                         ELSE ISNULL(r.ValorNuestro, 0) - ISNULL(r.ValorSat, 0) END,
    DifDeclarado  = CASE WHEN r.ValorDeclarado IS NULL THEN NULL
                         ELSE r.ValorDeclarado - ISNULL(r.ValorNuestro, 0) END,
    Estado = CASE
               WHEN r.ValorDeclarado IS NULL THEN 'PENDIENTE'
               WHEN ABS(r.ValorDeclarado - ISNULL(r.ValorNuestro, 0)) <= @Tol THEN 'OK'
               ELSE 'DIFERENCIA'
             END
  FROM r ORDER BY r.Orden;

  /*----------------------------------------------------------------------
    Saldos contables del mes.

    Para conciliar se usa el Debe de 118-01 y el Haber de 208-01, no el saldo
    neto: la poliza de cierre deja ambas cuentas en cero, asi que el neto no
    dice nada una vez cerrado el mes. El Debe de 118 es el IVA que se acredito
    durante el mes y el Haber de 208 el que se traslado, que es justo lo que
    hay que comparar contra los CFDIs.
  ----------------------------------------------------------------------*/
  SELECT Nivel1, Nivel2, Nivel3, Debe, Haber, Saldo
  INTO #Cont
  FROM fiscal.fn_Saldos_Contables(@Rfc, @Ejercicio)
  WHERE Mes = @Periodo;

  /*----------------------------------------------------------------------
    IVA acreditado y trasladado del mes, SIN las polizas de cierre.

    fn_Saldos_Contables agrega por cuenta y no ve la poliza, asi que para
    conciliar contra los CFDIs se recalcula aqui el movimiento neto de 118-01
    y 208-01 (Debe - Haber) excluyendo:
      - la poliza de cierre registrada en fiscal.DeclaracionCierre, y
      - cualquier poliza del mes cuyo asiento sea 100% cuentas de impuestos
        (113/118/119/208/209/213/216): son cierres o reclasificaciones de IVA,
        se capturen a mano o las genere Generar_Poliza_Cierre.
    Asi una nota de credito recibida -que abona 118-01 contra el banco o un
    gasto- SI baja el acreditable, pero el asiento que solo mueve 118 contra
    208/113 no. Con la base bruta SUM(Debe) anterior, un mes ya cerrado veia el
    acreditable inflado por el abono de cada nota de credito, y esa diferencia
    quedaba sin un hallazgo que la explicara.
  ----------------------------------------------------------------------*/
  DECLARE @IniMes date = DATEFROMPARTS(@Ejercicio, @Periodo, 1);
  DECLARE @FinMes date = DATEADD(MONTH, 1, @IniMes);

  CREATE TABLE #PolCierre (TransaccionID int PRIMARY KEY);
  INSERT INTO #PolCierre (TransaccionID)
  SELECT t.ID
  FROM dbo.Transacciones AS t
  WHERE t.RFC = @Rfc AND t.Fecha >= @IniMes AND t.Fecha < @FinMes
    AND EXISTS (SELECT 1 FROM dbo.Registro_Contable rc WHERE rc.TransaccionID = t.ID)
    AND NOT EXISTS (SELECT 1 FROM dbo.Registro_Contable rc
                    WHERE rc.TransaccionID = t.ID
                      AND ISNULL(rc.Nivel1, '') NOT IN ('113','118','119','208','209','213','216'))
  UNION
  SELECT dc.TransaccionIdCierre
  FROM fiscal.DeclaracionCierre AS dc
  WHERE dc.Rfc = @Rfc AND dc.Ejercicio = @Ejercicio AND dc.Periodo = @Periodo
    AND dc.TransaccionIdCierre IS NOT NULL;

  DECLARE @Cont118Debe decimal(19,4), @Cont208Haber decimal(19,4);
  SELECT
    @Cont118Debe  = ISNULL(SUM(CASE WHEN rc.Nivel1 = '118' AND rc.Nivel2 IN ('1','01') THEN rc.Debe - rc.Haber END), 0),
    @Cont208Haber = ISNULL(SUM(CASE WHEN rc.Nivel1 = '208' AND rc.Nivel2 IN ('1','01') THEN rc.Haber - rc.Debe END), 0)
  FROM dbo.Registro_Contable AS rc
  JOIN dbo.Transacciones AS t ON t.ID = rc.TransaccionID
  WHERE t.RFC = @Rfc AND t.Fecha >= @IniMes AND t.Fecha < @FinMes
    AND rc.Nivel1 IN ('118', '208')
    AND NOT EXISTS (SELECT 1 FROM #PolCierre pc WHERE pc.TransaccionID = t.ID);

  DECLARE @Cont401 decimal(19,4) = ISNULL((SELECT SUM(Haber - Debe) FROM #Cont WHERE Nivel1='401'), 0);
  DECLARE @Cont402 decimal(19,4) = ISNULL((SELECT SUM(Debe - Haber) FROM #Cont WHERE Nivel1='402'), 0);
  DECLARE @Cont213Iva decimal(19,4) = ISNULL((SELECT SUM(Haber - Debe) FROM #Cont WHERE Nivel1='213' AND Nivel2 IN ('1','01')), 0);
  -- Solo el Haber (la provision de ISR del mes). El neto Haber-Debe incluiria el
  -- pago de las provisiones de meses anteriores y volveria el renglon incomparable.
  DECLARE @Cont213IsrProv decimal(19,4) = ISNULL((SELECT SUM(Haber) FROM #Cont WHERE Nivel1='213' AND Nivel2 IN ('3','03')), 0);
  DECLARE @Cont113Iva decimal(19,4) = ISNULL((SELECT SUM(Debe - Haber) FROM #Cont WHERE Nivel1='113' AND Nivel2 IN ('1','01') AND Nivel3 <> '05'), 0);
  DECLARE @Cont113Plataforma decimal(19,4) = ISNULL((SELECT SUM(Debe - Haber) FROM #Cont WHERE Nivel1='113' AND Nivel2 IN ('1','01') AND Nivel3 = '05'), 0);
  DECLARE @Cont114Pagos decimal(19,4) = ISNULL((SELECT SUM(Debe - Haber) FROM #Cont WHERE Nivel1='114' AND Nivel2 IN ('1','01') AND Nivel3 <> '03'), 0);
  DECLARE @Cont114Plataforma decimal(19,4) = ISNULL((SELECT SUM(Debe - Haber) FROM #Cont WHERE Nivel1='114' AND Nivel2 IN ('1','01') AND Nivel3 = '03'), 0);
  DECLARE @Cont119 decimal(19,4) = ISNULL((SELECT SUM(Saldo) FROM #Cont WHERE Nivel1='119'), 0);
  DECLARE @Cont209 decimal(19,4) = ISNULL((SELECT SUM(Saldo) FROM #Cont WHERE Nivel1='209'), 0);
  DECLARE @Cont216Iva decimal(19,4) = ISNULL((SELECT SUM(Haber - Debe) FROM #Cont WHERE Nivel1='216' AND Nivel2 IN ('1','01')), 0);
  DECLARE @Cont216Isr decimal(19,4) = ISNULL((SELECT SUM(Haber - Debe) FROM #Cont WHERE Nivel1='216' AND Nivel2 IN ('3','03')), 0);

  ------------------------------------------------------------------ 4) CONCILIACION
  ;WITH r (Orden, Concepto, Cuenta, ValorCfdi, ValorContable, ValorDeclarado, NoComparable) AS
  (
    SELECT 1, 'IVA trasladado cobrado', '208-01', @IvaCargoSat, @Cont208Haber, @DeclIvaACargo, 0
    UNION ALL SELECT 2, 'IVA acreditable pagado', '118-01', @IvaAcredNuestro, @Cont118Debe, @DeclIvaAcreditable, 0
    UNION ALL SELECT 3, 'Ingresos del periodo', '401 menos 402', @IngNuestroMes, @Cont401 - @Cont402, @DeclIngresos, 0
    UNION ALL SELECT 4, 'IVA por pagar del periodo', '213-01',
                     CASE WHEN @SaldoNuestro < 0 THEN -@SaldoNuestro ELSE 0 END, @Cont213Iva, NULL, 0
    UNION ALL SELECT 5, 'IVA a favor del periodo', '113-01',
                     CASE WHEN @SaldoNuestro > 0 THEN @SaldoNuestro ELSE 0 END, @Cont113Iva, @DeclSaldoFavor, 0
    UNION ALL SELECT 6, 'ISR del periodo (provision registrada)', '213-03',
                     CASE WHEN @SinHistorial = 1 THEN NULL ELSE @IsrCargoNuestro END,
                     @Cont213IsrProv, @DeclIsrACargo, @SinHistorial
    UNION ALL SELECT 7, 'Pagos provisionales de ISR', '114-01', NULL, @Cont114Pagos, NULL, 1
    UNION ALL SELECT 8, 'IVA acreditable pendiente de pago (PPD recibidas)', '119-01', NULL, @Cont119, NULL, 1
    UNION ALL SELECT 9, 'IVA trasladado no cobrado (PPD emitidas)', '209-01', NULL, @Cont209, NULL, 1
  )
  SELECT
    r.Orden, r.Concepto, r.Cuenta, r.ValorCfdi, r.ValorContable, r.ValorDeclarado,
    NoComparable = CONVERT(bit, r.NoComparable),
    DifCfdiContable = CASE WHEN r.NoComparable = 1 OR r.ValorCfdi IS NULL THEN NULL
                           ELSE r.ValorContable - r.ValorCfdi END,
    DifDeclarado = CASE WHEN r.ValorDeclarado IS NULL OR r.ValorCfdi IS NULL THEN NULL
                        ELSE r.ValorDeclarado - r.ValorCfdi END,
    Estado = CASE
               WHEN r.NoComparable = 1 THEN 'NA'
               WHEN r.ValorCfdi IS NULL THEN 'NA'
               WHEN ABS(r.ValorContable - r.ValorCfdi) <= @Tol THEN 'OK'
               ELSE 'DIFERENCIA'
             END
  FROM r ORDER BY r.Orden;

  ------------------------------------------------------------------ 5) RETENCIONES
  DECLARE @IvaRetARecibidas decimal(19,4) = ISNULL((SELECT IvaRetenidoRecibidas FROM #Agg WHERE Mes = @Periodo), 0);

  ;WITH r (Orden, Concepto, Cuenta, ValorCfdi, ValorContable, ValorDeclarado, Nota) AS
  (
    SELECT 1, 'ISR retenido al contribuyente (facturas emitidas)', '216-03', @IsrRetMes, @Cont216Isr, NULL,
              'Disminuye el ISR a cargo del periodo.'
    UNION ALL SELECT 2, 'IVA retenido al contribuyente (facturas emitidas)', '216-01', @IvaRetNos, @Cont216Iva, NULL,
              'Se acredita contra el IVA a cargo.'
    UNION ALL SELECT 3, 'IVA retenido por el contribuyente (facturas recibidas)', '216-01', @IvaRetARecibidas, NULL, NULL,
              'Es un entero a cargo, no un acreditamiento.'
    UNION ALL SELECT 4, 'IVA retenido por plataforma tecnologica pendiente de acreditar', '113-01-05', NULL, @Cont113Plataforma, NULL,
              'Retenciones de plataforma que aun no se acreditan.'
    UNION ALL SELECT 5, 'ISR retenido por plataforma tecnologica acreditable', '114-01-03', NULL, @Cont114Plataforma, NULL,
              'Pago provisional acreditable retenido por la plataforma.'
  )
  SELECT
    r.Orden, r.Concepto, r.Cuenta, r.ValorCfdi, r.ValorContable, r.ValorDeclarado, r.Nota,
    DifCfdiContable = CASE WHEN r.ValorCfdi IS NULL OR r.ValorContable IS NULL THEN NULL
                           ELSE r.ValorContable - r.ValorCfdi END,
    Estado = CASE
               WHEN r.ValorCfdi IS NULL OR r.ValorContable IS NULL THEN 'NA'
               WHEN ABS(r.ValorContable - r.ValorCfdi) <= @Tol THEN 'OK'
               ELSE 'DIFERENCIA'
             END
  FROM r ORDER BY r.Orden;

  ------------------------------------------------------------------ 6) ASIENTO DE CIERRE
  /*
    Salda 118-01 contra 208-01 y manda el neto a 213-01 (por pagar) o 113-01
    (a favor). Se propone sobre las cifras contables reales del mes, no sobre
    las del CFDI: la poliza tiene que cerrar lo que la contabilidad trae, y si
    contabilidad y CFDI no coinciden eso se corrige antes, en la pestana de
    hallazgos, no aqui.

    Se usa el saldo vivo (Debe menos Haber) y no el movimiento del mes. Asi, un
    mes que ya se cerro devuelve cero renglones -que es la respuesta correcta,
    no hay nada que cerrar- y un mes que arrastra saldo de meses anteriores lo
    arrastra tambien al cierre.
  */
  DECLARE @Cont118Saldo decimal(19,4) = ISNULL((SELECT SUM(Debe - Haber) FROM #Cont WHERE Nivel1='118' AND Nivel2 IN ('1','01')), 0);
  DECLARE @Cont208Saldo decimal(19,4) = ISNULL((SELECT SUM(Haber - Debe) FROM #Cont WHERE Nivel1='208' AND Nivel2 IN ('1','01')), 0);
  DECLARE @NetoContable decimal(19,4) = @Cont118Saldo - @Cont208Saldo;

  ;WITH r (Orden, Cuenta, NombreCuenta, Debe, Haber) AS
  (
    SELECT 1, '208-01', 'IVA TRASLADADO COBRADO', @Cont208Saldo, CAST(0 AS decimal(19,4))
    UNION ALL SELECT 2, '118-01', 'IVA ACREDITABLE PAGADO', CAST(0 AS decimal(19,4)), @Cont118Saldo
    UNION ALL SELECT 3,
      CASE WHEN @NetoContable > 0 THEN '113-01' ELSE '213-01' END,
      CASE WHEN @NetoContable > 0 THEN 'IVA A FAVOR' ELSE 'IVA POR PAGAR' END,
      CASE WHEN @NetoContable > 0 THEN @NetoContable ELSE CAST(0 AS decimal(19,4)) END,
      CASE WHEN @NetoContable > 0 THEN CAST(0 AS decimal(19,4)) ELSE -@NetoContable END
  )
  SELECT
    r.Orden, r.Cuenta, r.NombreCuenta, r.Debe, r.Haber,
    Concepto = CONCAT('Cierre de IVA ', @NombreMes, ' ', @Ejercicio),
    Fecha = EOMONTH(DATEFROMPARTS(@Ejercicio, @Periodo, 1)),
    Neto = @NetoContable,
    EsFavor = CONVERT(bit, CASE WHEN @NetoContable > 0 THEN 1 ELSE 0 END)
  FROM r
  WHERE r.Debe <> 0 OR r.Haber <> 0
  ORDER BY r.Orden;

  ------------------------------------------------------------------ 7) ASIENTO DE ISR PROVISIONAL
  /*
    Provision del pago provisional de ISR del mes: Debe a "pagos provisionales"
    (activo, para netear contra el ISR anual) contra Haber "ISR por pagar"
    (pasivo). El importe es el de fiscal.fn_Isr_Provisional: el ISR a cargo ya
    declarado del periodo si existe, y si no el calculado (nuestro).

    Las cuentas salen de fiscal.EjercicioFiscal y, cuando estan vacias, de la
    subcuenta de 114-01 / 213-03 cuya descripcion trae el ejercicio (114-01-02
    "PAGOS PROVISIONALES DE ISR 2026", 213-03-03 "ISR POR PAGAR 2026").

    Siempre devuelve las dos lineas -aunque el monto sea cero o falten datos-
    para que la pestana muestre el asiento y el motivo; el boton se habilita
    solo cuando hay algo que registrar.
  */
  DECLARE @CtaPagosProvIsr varchar(20), @CtaIsrPorPagar varchar(20);
  SELECT @CtaPagosProvIsr = CuentaPagosProvIsr, @CtaIsrPorPagar = CuentaIsrPorPagar
  FROM fiscal.EjercicioFiscal WHERE Rfc = @Rfc AND Ejercicio = @Ejercicio;

  IF @CtaPagosProvIsr IS NULL
    SELECT TOP (1) @CtaPagosProvIsr = CONCAT(Nivel1, '-', Nivel2, '-', Nivel3)
    FROM dbo.CuentasContables
    WHERE RFC = @Rfc AND Nivel1 = '114' AND Nivel2 IN ('1','01')
      AND Descripcion LIKE '%' + CONVERT(varchar(4), @Ejercicio) + '%'
    ORDER BY Nivel3;

  IF @CtaIsrPorPagar IS NULL
    SELECT TOP (1) @CtaIsrPorPagar = CONCAT(Nivel1, '-', Nivel2, '-', Nivel3)
    FROM dbo.CuentasContables
    WHERE RFC = @Rfc AND Nivel1 = '213' AND Nivel2 IN ('3','03')
      AND Descripcion LIKE '%' + CONVERT(varchar(4), @Ejercicio) + '%'
    ORDER BY Nivel3;

  DECLARE @IsrPolizaBase varchar(20) =
    CASE WHEN @DeclIsrACargo IS NOT NULL THEN 'DECLARADO' ELSE 'NUESTRO' END;
  DECLARE @IsrPolizaMonto decimal(19,4) = CASE
    WHEN @DeclIsrACargo IS NOT NULL THEN @DeclIsrACargo
    WHEN @SinHistorial = 1 THEN NULL
    ELSE @IsrCargoNuestro END;
  IF @IsrPolizaMonto < 0 SET @IsrPolizaMonto = 0;

  DECLARE @IsrIdGenerado int = (SELECT TransaccionIdIsr FROM fiscal.DeclaracionIsrProvisional
                                WHERE Rfc = @Rfc AND Ejercicio = @Ejercicio AND Periodo = @Periodo);

  ;WITH r (Orden, Cuenta, NombreCuenta, Debe, Haber, Mensaje) AS
  (
    SELECT 1, @CtaPagosProvIsr, CONCAT('PAGOS PROVISIONALES DE ISR ', @Ejercicio),
           ISNULL(@IsrPolizaMonto, 0), CAST(0 AS decimal(19,4)),
           CONVERT(nvarchar(200), CASE
             WHEN @CtaPagosProvIsr IS NULL OR @CtaIsrPorPagar IS NULL
               THEN N'No se encontro la subcuenta de 114-01 / 213-03 del ejercicio; capturala en fiscal.EjercicioFiscal.'
             WHEN @IsrIdGenerado IS NOT NULL THEN CONCAT(N'Ya generada en la poliza ', @IsrIdGenerado, N'.')
             WHEN @IsrPolizaMonto IS NULL THEN N'Faltan las declaraciones de meses anteriores para armar la cadena acumulada.'
             WHEN @IsrPolizaMonto = 0 THEN N'El pago provisional del periodo es cero.'
             WHEN @IsrPolizaBase = 'DECLARADO' THEN N'Sobre el ISR a cargo ya declarado del periodo.'
             ELSE N'Sobre el ISR a cargo calculado; el periodo aun no se declara. Si presentas complementarias de meses anteriores, regenera la poliza.'
           END)
    UNION ALL
    SELECT 2, @CtaIsrPorPagar, CONCAT('ISR POR PAGAR ', @Ejercicio),
           CAST(0 AS decimal(19,4)), ISNULL(@IsrPolizaMonto, 0), CONVERT(nvarchar(200), NULL)
  )
  SELECT
    r.Orden, r.Cuenta, r.NombreCuenta, r.Debe, r.Haber,
    Concepto = CONCAT('Pago provisional de ISR ', @NombreMes, ' ', @Ejercicio),
    Fecha = EOMONTH(DATEFROMPARTS(@Ejercicio, @Periodo, 1)),
    Neto = ISNULL(@IsrPolizaMonto, 0),
    EsFavor = CONVERT(bit, 0),
    r.Mensaje
  FROM r
  ORDER BY r.Orden;

  DROP TABLE #Cont;
  DROP TABLE #Agg;
  DROP TABLE #PolCierre;
END;
GO

/*--------------------------------------------------------------------------
  Rpt_Declaracion_Hallazgos - por que las cifras no cuadran.

  Cada renglon dice que documento causa una diferencia y por que. La regla es
  que ninguna diferencia de las pestanas anteriores puede quedar sin un
  hallazgo que la explique: si el total no cuadra y aqui no hay nada, el
  reporte esta mintiendo.
--------------------------------------------------------------------------*/
CREATE OR ALTER PROCEDURE fiscal.Rpt_Declaracion_Hallazgos
  @Rfc varchar(50),
  @Ejercicio int,
  @Periodo tinyint
AS
BEGIN
  SET NOCOUNT ON;

  IF @Periodo NOT BETWEEN 1 AND 12
    THROW 51201, 'El parametro @Periodo debe estar entre 1 y 12.', 1;

  DECLARE @Tol decimal(19,4) = 1.00;

  DECLARE @FechaDeclaracion date =
    (SELECT MAX(FechaPresentacion) FROM fiscal.DeclaracionPresentada
     WHERE Rfc = @Rfc AND Ejercicio = @Ejercicio AND Periodo = @Periodo);

  SELECT * INTO #C FROM fiscal.fn_Cfdi_Periodo(@Rfc, @Ejercicio, @Periodo);

  DECLARE @Ids varchar(max);
  SELECT @Ids = STRING_AGG(CONVERT(varchar(20), Comprobante_Id), ',') FROM #C;

  SELECT * INTO #Cong FROM fiscal.fn_Congruencia_Cfdi(ISNULL(@Ids, ''), @Rfc, 1.00);

  CREATE TABLE #H
  (
    Severidad varchar(10) NOT NULL,
    Orden tinyint NOT NULL,
    Tipo varchar(80) NOT NULL,
    Descripcion nvarchar(500) NOT NULL,
    Monto decimal(19,4) NULL,
    ComprobanteId int NULL,
    FolioFiscal nvarchar(80) NULL,
    Contraparte nvarchar(400) NULL,
    Fecha date NULL,
    RutaDetalle nvarchar(300) NULL
  );

  -- 1) CFDI PUE recibido que el SAT no va a precargar como acreditable.
  --    Nuestra columna cuenta el acreditable segun la ley; la precarga del SAT
  --    exige ademas forma de pago bancarizada. Se separan dos casos:
  --      EsAcreditable = 1 -> efectivo <= $2,000 con uso G01/G03: SI es acreditable
  --        (art. 27-III LISR), solo hay que capturarlo a mano en el portal. Baja.
  --      EsAcreditable = 0 -> efectivo > $2,000 o uso != G01/G03: NO es acreditable
  --        ni deducible; hay que revisar el gasto. Media.
  --    Los PPD no entran aqui: se excluyen por igual de las dos columnas (su IVA
  --    se acredita hasta el complemento de pago), asi que no explican ninguna
  --    diferencia. El PPD sin complemento contabilizado lo cubre el hallazgo 2.
  INSERT INTO #H (Severidad, Orden, Tipo, Descripcion, Monto, ComprobanteId, FolioFiscal, Contraparte, Fecha, RutaDetalle)
  SELECT
    CASE WHEN EsAcreditable = 1 THEN 'Baja' ELSE 'Media' END,
    2, 'IVA acreditable no precargado',
    CASE WHEN EsAcreditable = 1 THEN
      CONCAT('Pago en efectivo de ', FORMAT(Total, 'N2'), ' (<= $2,000): es acreditable, ',
             'pero el SAT no lo precarga. Agregalo manualmente en el portal (+',
             FORMAT(Iva16, 'N2'), ' de IVA).')
    ELSE
      CONCAT('El SAT no precarga este CFDI y ademas no es acreditable: ',
        CASE WHEN EsBancarizada = 0 THEN CONCAT('pago en efectivo de ', FORMAT(Total, 'N2'),
             ' (> $2,000, art. 27-III LISR). ') ELSE '' END,
        CASE WHEN UsoAcreditable = 0 THEN CONCAT('uso de CFDI ', ISNULL(UsoCFDI,'(vacio)'),
             ' fuera de G01/G03. ') ELSE '' END)
    END,
    Iva16, Comprobante_Id, FolioFiscal, CONCAT(RfcEmisor, ' ', NombreEmisor), CAST(Fecha AS date),
    '/cfdi/declaracion-previa'
  FROM #C
  WHERE EsRecibida = 1 AND EsVigente = 1 AND TipoDeComprobante = 'I' AND Iva16 <> 0
    AND EsPue = 1 AND (EsBancarizada = 0 OR UsoAcreditable = 0);

  -- 2) CFDI sin poliza. Sin poliza el importe no llega a la balanza y la
  --    conciliacion contra 118/208 falla. Un PPD normalmente no tiene poliza
  --    directa: su gasto e IVA se reconocen conforme se paga, a traves de la
  --    poliza del complemento de pago (ligada por Transaccion_DoctoRelacionado,
  --    no por Transaccion_Comprobante). Por eso se mide tambien lo pagado con
  --    poliza y se gradua la severidad:
  --      PUE sin ningun amarre     -> Alta  (hueco real en la balanza)
  --      PPD sin ningun amarre     -> Media (falta la poliza de provision)
  --      PPD amarrado en parte     -> Baja  (informativo; el resto entra con los REP)
  --    Un PPD ya liquidado y contabilizado por sus REP no genera hallazgo.
  INSERT INTO #H (Severidad, Orden, Tipo, Descripcion, Monto, ComprobanteId, FolioFiscal, Contraparte, Fecha, RutaDetalle)
  SELECT
    CASE WHEN c.EsPue = 1 THEN 'Alta'
         WHEN x.PagadoConPoliza = 0 THEN 'Media'
         ELSE 'Baja' END,
    1, 'CFDI sin poliza',
    CASE
      WHEN c.EsPue = 1 THEN
        N'El CFDI no tiene ninguna poliza ligada, por lo que su importe no aparece en la balanza.'
      WHEN x.PagadoConPoliza = 0 THEN
        N'CFDI PPD sin poliza de provision: el gasto no esta devengado ni el IVA en 119-01. Su importe llegara a la balanza cuando se registre el complemento de pago.'
      ELSE
        CONCAT(N'CFDI PPD contabilizado por sus complementos de pago (',
               FORMAT(x.PagadoConPoliza, 'N2'), ' de ', FORMAT(c.Total, 'N2'),
               N' liquidado); el resto entra a la balanza al recibir los REP pendientes.')
    END,
    c.Total, c.Comprobante_Id, c.FolioFiscal,
    CASE WHEN c.EsEmitida = 1 THEN CONCAT(c.RfcReceptor, ' ', c.NombreReceptor)
         ELSE CONCAT(c.RfcEmisor, ' ', c.NombreEmisor) END,
    CAST(c.Fecha AS date), '/cfdi/declaracion-previa'
  FROM #C AS c
  CROSS APPLY
  (
    SELECT PagadoConPoliza = ISNULL(SUM(v.ImpPagado), 0)
    FROM cfdi.vw_Pagos20_Resumen AS v
    WHERE v.UUID_DoctoRelacionado = c.FolioFiscal
      AND ISNULL(v.Polizas, 0) > 0
      AND ((c.EsRecibida = 1 AND v.ReceptorRfc = @Rfc)
        OR (c.EsEmitida = 1 AND v.EmisorRfc = @Rfc))
  ) AS x
  WHERE c.EsVigente = 1 AND c.TipoDeComprobante IN ('I','E')
    AND NOT EXISTS (SELECT 1 FROM dbo.Transaccion_Comprobante tc WHERE tc.Comprobante_ID = c.Comprobante_Id)
    AND x.PagadoConPoliza < c.Total - @Tol;

  -- 3) El IVA del CFDI no coincide con el que quedo en su poliza.
  INSERT INTO #H (Severidad, Orden, Tipo, Descripcion, Monto, ComprobanteId, FolioFiscal, Contraparte, Fecha, RutaDetalle)
  SELECT 'Alta', 1, 'IVA contabilizado distinto al del CFDI',
    CONCAT('El CFDI traslada ', FORMAT(g.IvaEsperado, 'N2'), ' de IVA pero su poliza registro ',
           FORMAT(g.IvaContable, 'N2'), '.'),
    g.IvaDiferencia, c.Comprobante_Id, c.FolioFiscal,
    CASE WHEN c.EsEmitida = 1 THEN CONCAT(c.RfcReceptor, ' ', c.NombreReceptor)
         ELSE CONCAT(c.RfcEmisor, ' ', c.NombreEmisor) END,
    CAST(c.Fecha AS date), '/contabilidad/registros-contables'
  FROM #Cong AS g
  JOIN #C AS c ON c.Comprobante_Id = g.ComprobanteId
  WHERE g.IvaStatus = 'DIFERENCIA';

  -- 4) CFDI cancelado despues de presentar. Es el caso de julio 2026: el SAT
  --    precargo 48 vigentes y hoy la base tiene 47, asi que lo declarado y lo
  --    calculado ya no pueden coincidir sin una complementaria.
  INSERT INTO #H (Severidad, Orden, Tipo, Descripcion, Monto, ComprobanteId, FolioFiscal, Contraparte, Fecha, RutaDetalle)
  SELECT 'Alta', 1, 'CFDI cancelado despues de declarar',
    CONCAT('Se cancelo el ', CONVERT(varchar(10), c.FechaCancelacion, 103),
           ', despues de presentar la declaracion del periodo el ',
           CONVERT(varchar(10), @FechaDeclaracion, 103), '. Requiere complementaria.'),
    c.Total, c.Comprobante_Id, c.FolioFiscal, CONCAT(c.RfcReceptor, ' ', c.NombreReceptor),
    CAST(c.Fecha AS date), '/cfdi/declaracion-previa'
  FROM #C AS c
  WHERE @FechaDeclaracion IS NOT NULL
    AND c.EsVigente = 0
    AND c.FechaCancelacion > @FechaDeclaracion;

  -- 5) Desfase de periodo: la poliza que paga el CFDI cae en otro mes. No es
  --    necesariamente un error (un PPD se paga despues), pero mueve el IVA de
  --    mes y por eso hay que verlo.
  INSERT INTO #H (Severidad, Orden, Tipo, Descripcion, Monto, ComprobanteId, FolioFiscal, Contraparte, Fecha, RutaDetalle)
  SELECT 'Media', 2, 'Poliza en periodo distinto al del CFDI',
    CONCAT('El CFDI corresponde a ', @Periodo, '/', @Ejercicio,
           ' pero su poliza esta fechada en ', FORMAT(t.Fecha, 'MM/yyyy'), '.'),
    c.Total, c.Comprobante_Id, c.FolioFiscal,
    CASE WHEN c.EsEmitida = 1 THEN CONCAT(c.RfcReceptor, ' ', c.NombreReceptor)
         ELSE CONCAT(c.RfcEmisor, ' ', c.NombreEmisor) END,
    CAST(c.Fecha AS date), '/contabilidad/registros-contables'
  FROM #C AS c
  CROSS APPLY
  (
    SELECT TOP (1) t2.Fecha
    FROM dbo.Transaccion_Comprobante tc
    JOIN dbo.Transacciones t2 ON t2.ID = tc.Transaccion_ID
    WHERE tc.Comprobante_ID = c.Comprobante_Id
    ORDER BY t2.Fecha, t2.ID
  ) AS t
  WHERE c.EsVigente = 1 AND c.TipoDeComprobante IN ('I','E')
    AND (YEAR(t.Fecha) <> @Ejercicio OR MONTH(t.Fecha) <> @Periodo);

  -- 6) Complemento de pago sin poliza.
  INSERT INTO #H (Severidad, Orden, Tipo, Descripcion, Monto, ComprobanteId, FolioFiscal, Contraparte, Fecha, RutaDetalle)
  SELECT 'Media', 2, 'Complemento de pago sin poliza',
    CONCAT('El complemento de pago por ', FORMAT(p.ImpPagado, 'N2'),
           ' no tiene poliza ligada, asi que su IVA no llego a la balanza.'),
    p.Iva16, p.Comprobante_Id, CONVERT(nvarchar(80), p.ComprobanteUUID),
    CASE WHEN p.EsEmitida = 1 THEN p.ReceptorRfc ELSE p.EmisorRfc END,
    CAST(p.FechaPago AS date), '/cfdi/declaracion-previa'
  FROM fiscal.fn_Complementos_Periodo(@Rfc, @Ejercicio, @Periodo) AS p
  WHERE ISNULL(p.Polizas, 0) = 0;

  -- 7) Cuentas duplicadas en el catalogo. Rpt_BalanzaComprobacion resuelve los
  --    duplicados con MAX(Descripcion), asi que la balanza muestra un nombre
  --    arbitrario de los dos y una cuenta puede leerse como si fuera otra.
  INSERT INTO #H (Severidad, Orden, Tipo, Descripcion, Monto, RutaDetalle)
  SELECT 'Baja', 3, 'Cuenta duplicada en el catalogo',
    CONCAT('La cuenta ', d.Nivel1, '-', d.Nivel2, '-', d.Nivel3, ' esta capturada ',
           d.Repeticiones, ' veces con descripciones distintas: ', d.Descripciones, '.'),
    NULL, '/ReportesFinancieros/BalanzaComprobacion'
  FROM
  (
    SELECT cc.Nivel1, cc.Nivel2, cc.Nivel3,
           Repeticiones = COUNT(*),
           Descripciones = STRING_AGG(CONVERT(nvarchar(200), cc.Descripcion), ' | ')
    FROM dbo.CuentasContables AS cc
    WHERE cc.RFC = @Rfc
    GROUP BY cc.Nivel1, cc.Nivel2, cc.Nivel3
    HAVING COUNT(*) > 1
  ) AS d;

  -- 8) Ingreso contable sin CFDI. Espejo de "CFDI sin poliza": una poliza que
  --    abona a 401 y no liga ningun CFDI emitido deja la balanza de ingresos por
  --    encima de lo declarable (IVA trasladado e ISR sub-declarados). Solo se
  --    levanta cuando el ingreso contable del mes supera al de los CFDI emitidos;
  --    si el total cuadra, los enlaces sueltos son facturacion global y no una
  --    diferencia real.
  DECLARE @Ing401Mes decimal(19,4) = ISNULL((
    SELECT SUM(CASE WHEN rc.Nivel1 = '401' THEN rc.Haber - rc.Debe ELSE rc.Debe - rc.Haber END)
    FROM dbo.Registro_Contable AS rc
    JOIN dbo.Transacciones AS t ON t.ID = rc.TransaccionID
    WHERE t.RFC = @Rfc AND rc.Nivel1 IN ('401', '402')
      AND t.Fecha >= DATEFROMPARTS(@Ejercicio, @Periodo, 1)
      AND t.Fecha <  DATEADD(MONTH, 1, DATEFROMPARTS(@Ejercicio, @Periodo, 1))), 0);
  DECLARE @IngCfdiMes decimal(19,4) = ISNULL((
    SELECT SUM(Signo * SubTotalNeto) FROM #C
    WHERE EsEmitida = 1 AND EsVigente = 1 AND TipoDeComprobante IN ('I', 'E')), 0);

  IF @Ing401Mes - @IngCfdiMes > @Tol
    INSERT INTO #H (Severidad, Orden, Tipo, Descripcion, Monto, ComprobanteId, FolioFiscal, Contraparte, Fecha, RutaDetalle)
    SELECT 'Alta', 1, 'Ingreso contable sin CFDI',
      CONCAT(N'La poliza abona ', FORMAT(x.Ingreso, 'N2'),
             N' a ingresos (401) sin ningun CFDI emitido ligado. Falta timbrar la factura, ',
             N'o el ingreso debe quedar en una cuenta de ingresos pendientes de CFDI hasta emitirla.'),
      x.Ingreso, NULL, NULL, N'Poliza ' + CONVERT(nvarchar(20), t.ID), CAST(t.Fecha AS date),
      '/contabilidad/registros-contables'
    FROM dbo.Transacciones AS t
    CROSS APPLY
    (
      SELECT Ingreso = SUM(rc.Haber - rc.Debe)
      FROM dbo.Registro_Contable AS rc
      WHERE rc.TransaccionID = t.ID AND rc.Nivel1 = '401'
    ) AS x
    WHERE t.RFC = @Rfc
      AND t.Fecha >= DATEFROMPARTS(@Ejercicio, @Periodo, 1)
      AND t.Fecha <  DATEADD(MONTH, 1, DATEFROMPARTS(@Ejercicio, @Periodo, 1))
      AND x.Ingreso > @Tol
      AND NOT EXISTS (SELECT 1
                      FROM dbo.Transaccion_Comprobante AS tc
                      JOIN cfdi.Comprobante AS c ON c.Comprobante_Id = tc.Comprobante_ID
                      WHERE tc.Transaccion_ID = t.ID AND c.TipoDeComprobante IN ('I', 'E'));

  SELECT
    Severidad, Tipo, Descripcion, Monto, ComprobanteId, FolioFiscal, Contraparte, Fecha, RutaDetalle
  FROM #H
  ORDER BY Orden, Tipo, ABS(ISNULL(Monto, 0)) DESC, ComprobanteId;

  SELECT
    Total = COUNT(*),
    Altas = SUM(CASE WHEN Severidad = 'Alta' THEN 1 ELSE 0 END),
    Medias = SUM(CASE WHEN Severidad = 'Media' THEN 1 ELSE 0 END),
    Bajas = SUM(CASE WHEN Severidad = 'Baja' THEN 1 ELSE 0 END)
  FROM #H;

  DROP TABLE #H; DROP TABLE #Cong; DROP TABLE #C;
END;
GO

/*--------------------------------------------------------------------------
  Rpt_Declaracion_Ejercicio - los doce meses, calculado contra declarado.

  Es la vista que decide que meses hay que re-presentar. RequiereComplementaria
  se marca cuando existe declaracion presentada y alguna de las tres cifras que
  la definen -ingresos nominales, IVA a cargo o IVA acreditable- se separa mas
  de un peso de lo calculado.
--------------------------------------------------------------------------*/
CREATE OR ALTER PROCEDURE fiscal.Rpt_Declaracion_Ejercicio
  @Rfc varchar(50),
  @Ejercicio int
AS
BEGIN
  SET NOCOUNT ON;

  DECLARE @Tol decimal(19,4) = 1.00;
  DECLARE @TasaIsr decimal(9,4) = ISNULL((SELECT TasaIsr FROM fiscal.PerfilFiscal WHERE Rfc = @Rfc), 0.30);
  DECLARE @Hoy date = CAST(SYSDATETIME() AS date);

  ;WITH Meses AS (SELECT v.Mes FROM (VALUES (1),(2),(3),(4),(5),(6),(7),(8),(9),(10),(11),(12)) v(Mes)),
  Cfdi AS (SELECT * FROM fiscal.fn_Cfdi_Periodo(@Rfc, @Ejercicio, NULL)),
  Comp AS (SELECT * FROM fiscal.fn_Complementos_Periodo(@Rfc, @Ejercicio, NULL)),
  AggC AS
  (
    SELECT Mes = PeriodoMes,
      Ingresos = SUM(CASE WHEN EsEmitida=1 AND EsVigente=1 AND IncluirEnDeclaracion=1 AND TipoDeComprobante IN ('I','E')
                          THEN Signo * SubTotalNeto * FactorDeclaracion ELSE 0 END),
      IvaTras = SUM(CASE WHEN EsEmitida=1 AND EsVigente=1 AND TipoDeComprobante='I' AND EsPue=1 THEN Iva16 ELSE 0 END),
      -- Mismo criterio legal que Rpt_Declaracion_Mensual: EsAcreditable en vez de
      -- EsPue (incluye efectivo <= $2,000, excluye efectivo > $2,000 y uso != G01/G03).
      IvaAcredPue = SUM(CASE WHEN EsRecibida=1 AND EsVigente=1 AND TipoDeComprobante='I' AND EsAcreditable=1
                             AND IncluirEnDeclaracion=1 THEN Iva16 * FactorDeclaracion ELSE 0 END),
      IvaAcredE = SUM(CASE WHEN EsRecibida=1 AND EsVigente=1 AND TipoDeComprobante='E' THEN Iva16 ELSE 0 END),
      IvaRet = SUM(CASE WHEN EsEmitida=1 AND EsVigente=1 THEN IvaRetenido ELSE 0 END),
      IsrRet = SUM(CASE WHEN EsEmitida=1 AND EsVigente=1 THEN IsrRetenido ELSE 0 END)
    FROM Cfdi WHERE PeriodoMes BETWEEN 1 AND 12 GROUP BY PeriodoMes
  ),
  AggP AS
  (
    SELECT Mes = PeriodoMes,
      IvaTrasComp = SUM(CASE WHEN EsEmitida=1 THEN Iva16 ELSE 0 END),
      IvaAcredComp = SUM(CASE WHEN EsRecibida=1 THEN Iva16 ELSE 0 END)
    FROM Comp GROUP BY PeriodoMes
  ),
  Decl AS
  (
    SELECT d.Periodo, d.IngresosNominalesPeriodo, d.IsrACargo, d.TotalIvaACargo,
           d.TotalIvaAcreditable, d.IvaSaldoAFavor, d.TipoDeclaracion, d.NumeroOperacion,
           d.FechaPresentacion, d.EsParcial
    FROM fiscal.DeclaracionPresentada d
    WHERE d.Rfc = @Rfc AND d.Ejercicio = @Ejercicio
      AND d.Id = (SELECT MAX(d2.Id) FROM fiscal.DeclaracionPresentada d2
                  WHERE d2.Rfc = d.Rfc AND d2.Ejercicio = d.Ejercicio AND d2.Periodo = d.Periodo)
  ),
  Base AS
  (
    SELECT
      m.Mes,
      Ingresos = ISNULL(c.Ingresos, 0),
      IvaACargo = ISNULL(c.IvaTras, 0) + ISNULL(p.IvaTrasComp, 0),
      IvaAcreditable = ISNULL(c.IvaAcredPue, 0) + ISNULL(p.IvaAcredComp, 0) - ISNULL(c.IvaAcredE, 0),
      IvaRet = ISNULL(c.IvaRet, 0),
      IsrRet = ISNULL(c.IsrRet, 0),
      d.IngresosNominalesPeriodo, d.IsrACargo AS IsrDeclarado, d.TotalIvaACargo AS IvaACargoDeclarado,
      d.TotalIvaAcreditable AS IvaAcreditableDeclarado, d.IvaSaldoAFavor AS SaldoFavorDeclarado,
      d.TipoDeclaracion, d.NumeroOperacion, d.FechaPresentacion, d.EsParcial,
      Coeficiente = coef.Coeficiente
    FROM Meses m
    LEFT JOIN AggC c ON c.Mes = m.Mes
    LEFT JOIN AggP p ON p.Mes = m.Mes
    LEFT JOIN Decl d ON d.Periodo = m.Mes
    -- Cada mes lleva el coeficiente vigente el dia en que se presento; los que
    -- aun no se presentan usan el de hoy.
    OUTER APPLY fiscal.fn_Coeficiente_Vigente(@Rfc, ISNULL(d.FechaPresentacion, @Hoy)) AS coef
  ),
  Acum AS
  (
    SELECT b.*,
      IngresosAcum = SUM(b.Ingresos) OVER (ORDER BY b.Mes ROWS UNBOUNDED PRECEDING),
      IsrRetAcum = SUM(b.IsrRet) OVER (ORDER BY b.Mes ROWS UNBOUNDED PRECEDING),
      PagosProvAnt = SUM(ISNULL(b.IsrDeclarado, 0)) OVER (ORDER BY b.Mes ROWS BETWEEN UNBOUNDED PRECEDING AND 1 PRECEDING)
    FROM Base b
  )
  SELECT
    a.Mes,
    NombreMes = CHOOSE(a.Mes, 'ENERO','FEBRERO','MARZO','ABRIL','MAYO','JUNIO',
                              'JULIO','AGOSTO','SEPTIEMBRE','OCTUBRE','NOVIEMBRE','DICIEMBRE'),
    a.Ingresos,
    IngresosDeclarado = a.IngresosNominalesPeriodo,
    DifIngresos = CASE WHEN a.IngresosNominalesPeriodo IS NULL THEN NULL
                       ELSE a.IngresosNominalesPeriodo - a.Ingresos END,
    a.IngresosAcum,
    a.Coeficiente,
    IsrCausado = CAST(a.IngresosAcum * ISNULL(a.Coeficiente, 0) * @TasaIsr AS decimal(19,4)),
    PagosProvAnteriores = ISNULL(a.PagosProvAnt, 0),
    IsrACargo = CAST(a.IngresosAcum * ISNULL(a.Coeficiente, 0) * @TasaIsr
                     - ISNULL(a.PagosProvAnt, 0) - a.IsrRetAcum AS decimal(19,4)),
    IsrACargoDeclarado = a.IsrDeclarado,
    a.IvaACargo,
    IvaACargoDeclarado = a.IvaACargoDeclarado,
    a.IvaAcreditable,
    IvaAcreditableDeclarado = a.IvaAcreditableDeclarado,
    SaldoAFavor = CASE WHEN a.IvaAcreditable + a.IvaRet - a.IvaACargo > 0
                       THEN a.IvaAcreditable + a.IvaRet - a.IvaACargo ELSE 0 END,
    SaldoAFavorDeclarado = a.SaldoFavorDeclarado,
    a.TipoDeclaracion, a.NumeroOperacion, a.FechaPresentacion,
    EsParcial = ISNULL(a.EsParcial, CONVERT(bit, 0)),
    TieneDeclaracion = CONVERT(bit, CASE WHEN a.NumeroOperacion IS NULL AND a.IngresosNominalesPeriodo IS NULL THEN 0 ELSE 1 END),
    RequiereComplementaria = CONVERT(bit, CASE
      WHEN a.IngresosNominalesPeriodo IS NULL AND a.IvaACargoDeclarado IS NULL THEN 0
      WHEN ABS(ISNULL(a.IngresosNominalesPeriodo, a.Ingresos) - a.Ingresos) > @Tol THEN 1
      WHEN ABS(ISNULL(a.IvaACargoDeclarado, a.IvaACargo) - a.IvaACargo) > @Tol THEN 1
      WHEN ABS(ISNULL(a.IvaAcreditableDeclarado, a.IvaAcreditable) - a.IvaAcreditable) > @Tol THEN 1
      ELSE 0 END)
  FROM Acum a
  ORDER BY a.Mes;
END;
GO

/*--------------------------------------------------------------------------
  Generar_Poliza_Cierre - salda 118-01 contra 208-01 al cierre del mes.

  @Aplicar = 0 devuelve el asiento propuesto sin escribir nada. Es el mismo
  patron de los scripts de este repo: se revisa primero y se aplica despues.

  Rechaza cuando:
    - ya existe un cierre registrado para el periodo. Con @Regenerar = 1 la
      poliza anterior no se borra: se cancela con un asiento inverso y se
      genera una nueva,
    - las cuentas 118-01 y 208-01 ya estan en cero, es decir no hay nada que
      cerrar,
    - falta alguna de las cuentas destino en el catalogo del RFC.

  No valida la conciliacion contra los CFDIs a proposito: la poliza cierra lo
  que la contabilidad trae. Si contabilidad y CFDI no coinciden eso se arregla
  en las polizas de origen, no metiendo la diferencia en el asiento de cierre.
  La pagina es la que bloquea el boton cuando hay hallazgos de severidad alta.
--------------------------------------------------------------------------*/
CREATE OR ALTER PROCEDURE fiscal.Generar_Poliza_Cierre
  @Rfc varchar(50),
  @Ejercicio int,
  @Periodo tinyint,
  @Aplicar bit = 0,
  @Regenerar bit = 0,
  @Usuario nvarchar(256) = NULL,
  @TransaccionID int = NULL OUTPUT
AS
BEGIN
  SET NOCOUNT ON;
  SET XACT_ABORT ON;

  IF @Periodo NOT BETWEEN 1 AND 12
    THROW 51201, 'El parametro @Periodo debe estar entre 1 y 12.', 1;

  DECLARE @NombreMes varchar(12) = CHOOSE(@Periodo,
    'ENERO','FEBRERO','MARZO','ABRIL','MAYO','JUNIO',
    'JULIO','AGOSTO','SEPTIEMBRE','OCTUBRE','NOVIEMBRE','DICIEMBRE');
  DECLARE @FechaPoliza date = EOMONTH(DATEFROMPARTS(@Ejercicio, @Periodo, 1));
  DECLARE @Concepto varchar(500) = CONCAT('Cierre de IVA ', @NombreMes, ' ', @Ejercicio);

  DECLARE @Existente int =
    (SELECT TransaccionIdCierre FROM fiscal.DeclaracionCierre
     WHERE Rfc = @Rfc AND Ejercicio = @Ejercicio AND Periodo = @Periodo);

  IF @Existente IS NOT NULL AND @Aplicar = 1 AND @Regenerar = 0
    THROW 51210, 'Ya existe una poliza de cierre para este periodo. Usa @Regenerar = 1 si de verdad quieres reemplazarla.', 1;

  DECLARE @Saldo118 decimal(19,4), @Saldo208 decimal(19,4);
  SELECT
    @Saldo118 = ISNULL(SUM(CASE WHEN Nivel1='118' AND Nivel2 IN ('1','01') THEN Debe - Haber ELSE 0 END), 0),
    @Saldo208 = ISNULL(SUM(CASE WHEN Nivel1='208' AND Nivel2 IN ('1','01') THEN Haber - Debe ELSE 0 END), 0)
  FROM fiscal.fn_Saldos_Contables(@Rfc, @Ejercicio)
  WHERE Mes = @Periodo;

  DECLARE @Neto decimal(19,4) = @Saldo118 - @Saldo208;

  IF @Saldo118 = 0 AND @Saldo208 = 0
  BEGIN
    IF @Aplicar = 1
      THROW 51211, 'Las cuentas 118-01 y 208-01 no tienen saldo en el periodo: no hay nada que cerrar.', 1;
    SELECT Orden = 1, Cuenta = NULL, NombreCuenta = NULL, Debe = CAST(0 AS decimal(19,4)),
           Haber = CAST(0 AS decimal(19,4)), Concepto = @Concepto, Fecha = @FechaPoliza,
           Neto = @Neto, EsFavor = CONVERT(bit, 0),
           Mensaje = N'Sin saldo por cerrar en 118-01 y 208-01.'
    WHERE 1 = 0;
    RETURN;
  END;

  DECLARE @CuentaNeto varchar(10) = CASE WHEN @Neto > 0 THEN '113' ELSE '213' END;

  DECLARE @Lineas TABLE
  (
    Orden tinyint, Nivel1 varchar(10), Nivel2 varchar(10), Nivel3 varchar(10),
    NombreCuenta varchar(200), Debe decimal(19,4), Haber decimal(19,4)
  );

  INSERT INTO @Lineas (Orden, Nivel1, Nivel2, Nivel3, NombreCuenta, Debe, Haber)
  SELECT 1, '208', '01', '00', 'IVA TRASLADADO COBRADO', @Saldo208, 0
  WHERE @Saldo208 <> 0
  UNION ALL
  SELECT 2, '118', '01', '00', 'IVA ACREDITABLE PAGADO', 0, @Saldo118
  WHERE @Saldo118 <> 0
  UNION ALL
  SELECT 3, @CuentaNeto, '01', '00',
         CASE WHEN @Neto > 0 THEN 'IVA A FAVOR' ELSE 'IVA POR PAGAR' END,
         CASE WHEN @Neto > 0 THEN @Neto ELSE 0 END,
         CASE WHEN @Neto > 0 THEN 0 ELSE -@Neto END
  WHERE @Neto <> 0;

  IF @Aplicar = 0
  BEGIN
    SELECT
      l.Orden,
      Cuenta = CONCAT(l.Nivel1, '-', l.Nivel2, '-', l.Nivel3),
      l.NombreCuenta, l.Debe, l.Haber,
      Concepto = @Concepto, Fecha = @FechaPoliza, Neto = @Neto,
      EsFavor = CONVERT(bit, CASE WHEN @Neto > 0 THEN 1 ELSE 0 END),
      Mensaje = CONVERT(nvarchar(200), NULL)
    FROM @Lineas l ORDER BY l.Orden;
    RETURN;
  END;

  IF (SELECT SUM(Debe) FROM @Lineas) <> (SELECT SUM(Haber) FROM @Lineas)
    THROW 51212, 'El asiento de cierre no cuadra: la suma del debe difiere de la del haber.', 1;

  BEGIN TRANSACTION;

  -- Regenerar cancela la poliza anterior con un asiento inverso en vez de
  -- borrarla. Dos razones: una poliza contabilizada no se borra, se reversa; y
  -- ademas dbo.Transacciones tiene un trigger de auditoria
  -- (trg_Transacciones_Audit) cuyo camino de DELETE falla con "could not
  -- produce a query plan" en esta edicion de SQL Server, asi que un DELETE aqui
  -- reventaria la transaccion completa.
  IF @Existente IS NOT NULL AND @Regenerar = 1
  BEGIN
    DECLARE @ConceptoRev varchar(500) = CONCAT('Cancelacion de ', @Concepto);
    DECLARE @TxRev int;

    INSERT INTO dbo.Transacciones
      (Concepto, Fecha, Monto, Facturado, Estatus, Referencia,
       Cuenta, Memo, EstatusDeAutorizacion, Tipo_Poliza, Forma_Pago, RFC)
    SELECT @ConceptoRev, @FechaPoliza, t.Monto, 0,
           'CIERRE FISCAL CANCELADO',
           CONCAT(@Ejercicio, '-', RIGHT('0' + CONVERT(varchar(2), @Periodo), 2)),
           'CIERRE-IVA', @ConceptoRev, 'AUTOMATICA', 'DIARIO', '99', @Rfc
    FROM dbo.Transacciones t WHERE t.ID = @Existente;

    SET @TxRev = CONVERT(int, SCOPE_IDENTITY());

    INSERT INTO dbo.Registro_Contable
      (Nivel1, Nivel2, Nivel3, Nombre_Cuenta, Concepto, Debe, Haber, TransaccionID, Referencia)
    SELECT rc.Nivel1, rc.Nivel2, rc.Nivel3, rc.Nombre_Cuenta, @ConceptoRev,
           rc.Haber, rc.Debe, @TxRev,
           CONCAT('REV-', @Existente)
    FROM dbo.Registro_Contable rc WHERE rc.TransaccionID = @Existente;

    DELETE FROM fiscal.DeclaracionCierre
      WHERE Rfc = @Rfc AND Ejercicio = @Ejercicio AND Periodo = @Periodo;
  END;

  -- Categoria y OrdenBalance se omiten a proposito: la tabla les pone valor por
  -- omision (1 y la secuencia Seq_Transacciones_OrdenBalance). Tipo_Poliza y
  -- Forma_Pago son NOT NULL, y se usan los valores que ya emplean las polizas
  -- de diario existentes de este RFC.
  INSERT INTO dbo.Transacciones
    (Concepto, Fecha, Monto, Facturado, Estatus, Referencia,
     Cuenta, Memo, EstatusDeAutorizacion, Tipo_Poliza, Forma_Pago, RFC)
  VALUES
    (@Concepto, @FechaPoliza, ABS(@Neto), 0,
     'CIERRE FISCAL', CONCAT(@Ejercicio, '-', RIGHT('0' + CONVERT(varchar(2), @Periodo), 2)),
     'CIERRE-IVA', @Concepto, 'AUTOMATICA', 'DIARIO', '99', @Rfc);

  SET @TransaccionID = CONVERT(int, SCOPE_IDENTITY());

  INSERT INTO dbo.Registro_Contable
    (Nivel1, Nivel2, Nivel3, Nombre_Cuenta, Concepto, Debe, Haber, TransaccionID, Referencia)
  SELECT l.Nivel1, l.Nivel2, l.Nivel3, l.NombreCuenta, @Concepto, l.Debe, l.Haber,
         @TransaccionID, CONCAT(@Ejercicio, '-', RIGHT('0' + CONVERT(varchar(2), @Periodo), 2))
  FROM @Lineas l ORDER BY l.Orden;

  INSERT INTO fiscal.DeclaracionCierre
    (Rfc, Ejercicio, Periodo, TransaccionIdCierre, IvaTrasladado, IvaAcreditable,
     NetoAFavor, NetoPorPagar, GeneradoPor)
  VALUES
    (@Rfc, @Ejercicio, @Periodo, @TransaccionID, @Saldo208, @Saldo118,
     CASE WHEN @Neto > 0 THEN @Neto ELSE 0 END,
     CASE WHEN @Neto < 0 THEN -@Neto ELSE 0 END, @Usuario);

  COMMIT TRANSACTION;

  SELECT
    l.Orden,
    Cuenta = CONCAT(l.Nivel1, '-', l.Nivel2, '-', l.Nivel3),
    l.NombreCuenta, l.Debe, l.Haber,
    Concepto = @Concepto, Fecha = @FechaPoliza, Neto = @Neto,
    EsFavor = CONVERT(bit, CASE WHEN @Neto > 0 THEN 1 ELSE 0 END),
    Mensaje = CONVERT(nvarchar(200), CONCAT(N'Poliza ', @TransaccionID, N' generada.'))
  FROM @Lineas l ORDER BY l.Orden;
END;
GO

/*--------------------------------------------------------------------------
  Generar_Poliza_Isr - registra la provision del pago provisional de ISR.

  Asiento: Debe "pagos provisionales de ISR" (activo, para netear contra el
  ISR anual) contra Haber "ISR por pagar" (pasivo), por el importe de
  fiscal.fn_Isr_Provisional: el ISR a cargo ya declarado del periodo si
  existe, y si no el calculado (nuestro).

  @Aplicar = 0 devuelve el asiento propuesto sin escribir. Rechaza cuando:
    - ya existe una poliza para el periodo (salvo @Regenerar = 1, que la
      cancela con asiento inverso -no la borra- y genera otra),
    - el importe es cero o no se puede armar la cadena (faltan meses),
    - falta la subcuenta de 114-01 / 213-03 del ejercicio.

  Es gemela de Generar_Poliza_Cierre: mismo patron de revision/aplicacion,
  misma forma de deshacer (asiento inverso, porque DELETE sobre
  dbo.Transacciones revienta en esta edicion de SQL Server).
--------------------------------------------------------------------------*/
CREATE OR ALTER PROCEDURE fiscal.Generar_Poliza_Isr
  @Rfc varchar(50),
  @Ejercicio int,
  @Periodo tinyint,
  @Aplicar bit = 0,
  @Regenerar bit = 0,
  @Usuario nvarchar(256) = NULL,
  @TransaccionID int = NULL OUTPUT
AS
BEGIN
  SET NOCOUNT ON;
  SET XACT_ABORT ON;

  IF @Periodo NOT BETWEEN 1 AND 12
    THROW 51201, 'El parametro @Periodo debe estar entre 1 y 12.', 1;

  DECLARE @NombreMes varchar(12) = CHOOSE(@Periodo,
    'ENERO','FEBRERO','MARZO','ABRIL','MAYO','JUNIO',
    'JULIO','AGOSTO','SEPTIEMBRE','OCTUBRE','NOVIEMBRE','DICIEMBRE');
  DECLARE @FechaPoliza date = EOMONTH(DATEFROMPARTS(@Ejercicio, @Periodo, 1));
  DECLARE @Concepto varchar(500) = CONCAT('Pago provisional de ISR ', @NombreMes, ' ', @Ejercicio);
  DECLARE @Referencia varchar(20) = CONCAT(@Ejercicio, '-', RIGHT('0' + CONVERT(varchar(2), @Periodo), 2));

  DECLARE @Existente int =
    (SELECT TransaccionIdIsr FROM fiscal.DeclaracionIsrProvisional
     WHERE Rfc = @Rfc AND Ejercicio = @Ejercicio AND Periodo = @Periodo);

  IF @Existente IS NOT NULL AND @Aplicar = 1 AND @Regenerar = 0
    THROW 51220, 'Ya existe una poliza de ISR provisional para este periodo. Usa @Regenerar = 1 si de verdad quieres reemplazarla.', 1;

  -- Cuentas destino: mismo criterio que Rpt_Declaracion_Mensual.
  DECLARE @CtaProv varchar(20), @CtaPagar varchar(20);
  SELECT @CtaProv = CuentaPagosProvIsr, @CtaPagar = CuentaIsrPorPagar
  FROM fiscal.EjercicioFiscal WHERE Rfc = @Rfc AND Ejercicio = @Ejercicio;

  IF @CtaProv IS NULL
    SELECT TOP (1) @CtaProv = CONCAT(Nivel1, '-', Nivel2, '-', Nivel3)
    FROM dbo.CuentasContables
    WHERE RFC = @Rfc AND Nivel1 = '114' AND Nivel2 IN ('1','01')
      AND Descripcion LIKE '%' + CONVERT(varchar(4), @Ejercicio) + '%'
    ORDER BY Nivel3;

  IF @CtaPagar IS NULL
    SELECT TOP (1) @CtaPagar = CONCAT(Nivel1, '-', Nivel2, '-', Nivel3)
    FROM dbo.CuentasContables
    WHERE RFC = @Rfc AND Nivel1 = '213' AND Nivel2 IN ('3','03')
      AND Descripcion LIKE '%' + CONVERT(varchar(4), @Ejercicio) + '%'
    ORDER BY Nivel3;

  -- Importe: el declarado del periodo si existe, si no el calculado (nuestro).
  DECLARE @DeclIsr decimal(19,4) =
    (SELECT TOP (1) IsrACargo FROM fiscal.DeclaracionPresentada
     WHERE Rfc = @Rfc AND Ejercicio = @Ejercicio AND Periodo = @Periodo
     ORDER BY TipoDeclaracion DESC, NumeroComplementaria DESC, Id DESC);

  DECLARE @IsrNuestro decimal(19,4), @SinHistorial bit;
  SELECT @IsrNuestro = IsrCargoNuestro, @SinHistorial = SinHistorial
  FROM fiscal.fn_Isr_Provisional(@Rfc, @Ejercicio, @Periodo);

  DECLARE @Base varchar(20) = CASE WHEN @DeclIsr IS NOT NULL THEN 'DECLARADO' ELSE 'NUESTRO' END;
  DECLARE @Monto decimal(19,4) = CASE
    WHEN @DeclIsr IS NOT NULL THEN @DeclIsr
    WHEN @SinHistorial = 1 THEN NULL
    ELSE @IsrNuestro END;
  IF @Monto < 0 SET @Monto = 0;

  DECLARE @Motivo nvarchar(200) = CASE
    WHEN @CtaProv IS NULL OR @CtaPagar IS NULL
      THEN N'No se encontro la subcuenta de 114-01 / 213-03 del ejercicio; capturala en fiscal.EjercicioFiscal.'
    WHEN @Monto IS NULL THEN N'Faltan las declaraciones de meses anteriores para armar la cadena acumulada.'
    WHEN @Monto = 0 THEN N'El pago provisional del periodo es cero.'
    WHEN @Base = 'DECLARADO' THEN N'Sobre el ISR a cargo ya declarado del periodo.'
    ELSE N'Sobre el ISR a cargo calculado; el periodo aun no se declara.' END;

  IF @Aplicar = 0
  BEGIN
    ;WITH r (Orden, Cuenta, NombreCuenta, Debe, Haber) AS
    (
      SELECT 1, @CtaProv, CONCAT('PAGOS PROVISIONALES DE ISR ', @Ejercicio), ISNULL(@Monto, 0), CAST(0 AS decimal(19,4))
      UNION ALL SELECT 2, @CtaPagar, CONCAT('ISR POR PAGAR ', @Ejercicio), CAST(0 AS decimal(19,4)), ISNULL(@Monto, 0)
    )
    SELECT r.Orden, r.Cuenta, r.NombreCuenta, r.Debe, r.Haber,
      Concepto = @Concepto, Fecha = @FechaPoliza, Neto = ISNULL(@Monto, 0),
      EsFavor = CONVERT(bit, 0), Mensaje = @Motivo
    FROM r ORDER BY r.Orden;
    RETURN;
  END;

  IF @CtaProv IS NULL OR @CtaPagar IS NULL
    THROW 51223, 'No se encontro la subcuenta de 114-01 / 213-03 del ejercicio. Capturala en fiscal.EjercicioFiscal.', 1;
  IF @Monto IS NULL
    THROW 51221, 'Faltan las declaraciones de meses anteriores: la cadena acumulada de ISR no se puede armar.', 1;
  IF @Monto = 0
    THROW 51222, 'El pago provisional del periodo es cero: no hay nada que registrar.', 1;

  DECLARE @Lineas TABLE
  (
    Orden tinyint, Nivel1 varchar(10), Nivel2 varchar(10), Nivel3 varchar(10),
    NombreCuenta varchar(200), Debe decimal(19,4), Haber decimal(19,4)
  );

  INSERT INTO @Lineas (Orden, Nivel1, Nivel2, Nivel3, NombreCuenta, Debe, Haber)
  SELECT 1, PARSENAME(REPLACE(@CtaProv, '-', '.'), 3), PARSENAME(REPLACE(@CtaProv, '-', '.'), 2),
            PARSENAME(REPLACE(@CtaProv, '-', '.'), 1),
            CONCAT('PAGOS PROVISIONALES DE ISR ', @Ejercicio), @Monto, 0
  UNION ALL
  SELECT 2, PARSENAME(REPLACE(@CtaPagar, '-', '.'), 3), PARSENAME(REPLACE(@CtaPagar, '-', '.'), 2),
            PARSENAME(REPLACE(@CtaPagar, '-', '.'), 1),
            CONCAT('ISR POR PAGAR ', @Ejercicio), 0, @Monto;

  BEGIN TRANSACTION;

  IF @Existente IS NOT NULL AND @Regenerar = 1
  BEGIN
    DECLARE @ConceptoRev varchar(500) = CONCAT('Cancelacion de ', @Concepto);
    DECLARE @TxRev int;

    INSERT INTO dbo.Transacciones
      (Concepto, Fecha, Monto, Facturado, Estatus, Referencia,
       Cuenta, Memo, EstatusDeAutorizacion, Tipo_Poliza, Forma_Pago, RFC)
    SELECT @ConceptoRev, @FechaPoliza, t.Monto, 0,
           'ISR PROVISIONAL CANCELADO', @Referencia,
           'ISR-PROV', @ConceptoRev, 'AUTOMATICA', 'DIARIO', '99', @Rfc
    FROM dbo.Transacciones t WHERE t.ID = @Existente;

    SET @TxRev = CONVERT(int, SCOPE_IDENTITY());

    INSERT INTO dbo.Registro_Contable
      (Nivel1, Nivel2, Nivel3, Nombre_Cuenta, Concepto, Debe, Haber, TransaccionID, Referencia)
    SELECT rc.Nivel1, rc.Nivel2, rc.Nivel3, rc.Nombre_Cuenta, @ConceptoRev,
           rc.Haber, rc.Debe, @TxRev, CONCAT('REV-', @Existente)
    FROM dbo.Registro_Contable rc WHERE rc.TransaccionID = @Existente;

    DELETE FROM fiscal.DeclaracionIsrProvisional
      WHERE Rfc = @Rfc AND Ejercicio = @Ejercicio AND Periodo = @Periodo;
  END;

  INSERT INTO dbo.Transacciones
    (Concepto, Fecha, Monto, Facturado, Estatus, Referencia,
     Cuenta, Memo, EstatusDeAutorizacion, Tipo_Poliza, Forma_Pago, RFC)
  VALUES
    (@Concepto, @FechaPoliza, @Monto, 0,
     'ISR PROVISIONAL', @Referencia,
     'ISR-PROV', @Concepto, 'AUTOMATICA', 'DIARIO', '99', @Rfc);

  SET @TransaccionID = CONVERT(int, SCOPE_IDENTITY());

  INSERT INTO dbo.Registro_Contable
    (Nivel1, Nivel2, Nivel3, Nombre_Cuenta, Concepto, Debe, Haber, TransaccionID, Referencia)
  SELECT l.Nivel1, l.Nivel2, l.Nivel3, l.NombreCuenta, @Concepto, l.Debe, l.Haber,
         @TransaccionID, @Referencia
  FROM @Lineas l ORDER BY l.Orden;

  INSERT INTO fiscal.DeclaracionIsrProvisional
    (Rfc, Ejercicio, Periodo, TransaccionIdIsr, IsrDelPeriodo, BaseCalculo, GeneradoPor)
  VALUES
    (@Rfc, @Ejercicio, @Periodo, @TransaccionID, @Monto, @Base, @Usuario);

  COMMIT TRANSACTION;

  SELECT
    l.Orden,
    Cuenta = CONCAT(l.Nivel1, '-', l.Nivel2, '-', l.Nivel3),
    l.NombreCuenta, l.Debe, l.Haber,
    Concepto = @Concepto, Fecha = @FechaPoliza, Neto = @Monto,
    EsFavor = CONVERT(bit, 0),
    Mensaje = CONVERT(nvarchar(200), CONCAT(N'Poliza ', @TransaccionID, N' generada.'))
  FROM @Lineas l ORDER BY l.Orden;
END;
GO
