/*
  Baseline del ciclo contable. READ-ONLY: no crea, no altera y no escribe nada, así
  que no es una migración y no entra al manifiesto ni al ledger.

  Es el insumo con el que el usuario decide qué activar. Reporta, por empresa y
  periodo: pólizas, sumas, desbalances y los duplicados que le importan al ciclo.
  No propone ni aplica nada: sólo informa.

  Uso: se ejecuta contra la base que se quiera evaluar. Con @CompanyId nulo evalúa
  todas las empresas; con un valor, sólo ésa.
*/
SET NOCOUNT ON;
SET TRANSACTION ISOLATION LEVEL READ COMMITTED;

DECLARE @CompanyId bigint = NULL;
DECLARE @DesdeFecha date = NULL;

IF OBJECT_ID(N'dbo.Transacciones', N'U') IS NULL
   OR COL_LENGTH(N'dbo.Transacciones', N'CompanyId') IS NULL
  THROW 51999, 'El baseline exige la identidad contable por empresa.', 1;

PRINT '1) Cobertura de identidad: lo que el ciclo no puede gobernar todavia.';
SELECT
  ISNULL(CONVERT(nvarchar(50), poliza.CompanyId), N'(sin empresa)') AS Empresa,
  ISNULL(companyInfo.Rfc, N'(sin vinculo)') AS Rfc,
  CONVERT(bigint, COUNT_BIG(*)) AS Polizas,
  CONVERT(bigint, SUM(CASE WHEN poliza.CompanyId IS NULL THEN 1 ELSE 0 END)) AS SinEmpresa,
  MIN(poliza.Fecha) AS PrimeraFecha,
  MAX(poliza.Fecha) AS UltimaFecha
FROM dbo.Transacciones AS poliza
LEFT JOIN orion.Company AS companyInfo ON companyInfo.CompanyId = poliza.CompanyId
WHERE (@CompanyId IS NULL OR poliza.CompanyId = @CompanyId)
  AND (@DesdeFecha IS NULL OR poliza.Fecha >= @DesdeFecha)
GROUP BY poliza.CompanyId, companyInfo.Rfc
ORDER BY Polizas DESC;

PRINT '2) Por empresa y periodo: polizas, sumas y desbalances.';
SELECT
  poliza.CompanyId,
  companyInfo.Rfc,
  YEAR(poliza.Fecha) AS PeriodYear,
  MONTH(poliza.Fecha) AS PeriodMonth,
  CONVERT(bigint, COUNT_BIG(*)) AS Polizas,
  CONVERT(bigint, SUM(CASE WHEN movimientos.Renglones IS NULL THEN 1 ELSE 0 END)) AS PolizasSinMovimientos,
  CONVERT(decimal(19,2), ISNULL(SUM(movimientos.Debe), 0)) AS SumaCargos,
  CONVERT(decimal(19,2), ISNULL(SUM(movimientos.Haber), 0)) AS SumaAbonos,
  CONVERT(decimal(19,2), ISNULL(SUM(movimientos.Debe), 0) - ISNULL(SUM(movimientos.Haber), 0)) AS Diferencia,
  CONVERT(bigint, SUM(CASE WHEN ABS(ISNULL(movimientos.Debe, 0) - ISNULL(movimientos.Haber, 0)) > 0.005 THEN 1 ELSE 0 END)) AS PolizasDescuadradas
FROM dbo.Transacciones AS poliza
LEFT JOIN orion.Company AS companyInfo ON companyInfo.CompanyId = poliza.CompanyId
OUTER APPLY
(
  SELECT COUNT_BIG(*) AS Renglones, SUM(renglon.Debe) AS Debe, SUM(renglon.Haber) AS Haber
  FROM dbo.Registro_Contable AS renglon
  WHERE renglon.TransaccionID = poliza.ID
) AS movimientos
WHERE (@CompanyId IS NULL OR poliza.CompanyId = @CompanyId)
  AND (@DesdeFecha IS NULL OR poliza.Fecha >= @DesdeFecha)
GROUP BY poliza.CompanyId, companyInfo.Rfc, YEAR(poliza.Fecha), MONTH(poliza.Fecha)
ORDER BY poliza.CompanyId, PeriodYear DESC, PeriodMonth DESC;

PRINT '3) Duplicados relevantes al ciclo: mismo dia, monto y concepto en la misma empresa.';
SELECT TOP (200)
  poliza.CompanyId,
  CONVERT(date, poliza.Fecha) AS Fecha,
  CONVERT(decimal(19,2), poliza.Monto) AS Monto,
  LEFT(poliza.Concepto, 120) AS Concepto,
  CONVERT(int, COUNT(*)) AS Repeticiones,
  STRING_AGG(CONVERT(varchar(20), poliza.ID), ',') AS Polizas
FROM dbo.Transacciones AS poliza
WHERE (@CompanyId IS NULL OR poliza.CompanyId = @CompanyId)
  AND (@DesdeFecha IS NULL OR poliza.Fecha >= @DesdeFecha)
GROUP BY poliza.CompanyId, CONVERT(date, poliza.Fecha), poliza.Monto, LEFT(poliza.Concepto, 120)
HAVING COUNT(*) > 1
ORDER BY COUNT(*) DESC, Fecha DESC;

PRINT '4) Un CFDI ligado a mas de una poliza de la misma empresa: el ciclo lo rechaza de aqui en adelante.';
SELECT TOP (200)
  poliza.CompanyId,
  vinculo.Comprobante_ID AS ComprobanteId,
  CONVERT(int, COUNT(*)) AS Polizas,
  STRING_AGG(CONVERT(varchar(20), vinculo.Transaccion_ID), ',') AS Vinculos
FROM dbo.Transaccion_Comprobante AS vinculo
JOIN dbo.Transacciones AS poliza ON poliza.ID = vinculo.Transaccion_ID
WHERE (@CompanyId IS NULL OR poliza.CompanyId = @CompanyId)
GROUP BY poliza.CompanyId, vinculo.Comprobante_ID
HAVING COUNT(*) > 1
ORDER BY COUNT(*) DESC;

PRINT '5) Estado actual del ciclo. Si el ciclo no esta instalado, esta seccion sale vacia.';
IF OBJECT_ID(N'contabilidad.CompanyCycleActivation', N'U') IS NOT NULL
  EXEC(N'
    SELECT activacion.CompanyId, companyInfo.Rfc, activacion.IsEnabled,
           activacion.LegacyCompatibleUntilUtc, activacion.ActivatedAtUtc, activacion.ActivatedBy
    FROM contabilidad.CompanyCycleActivation AS activacion
    JOIN orion.Company AS companyInfo ON companyInfo.CompanyId = activacion.CompanyId
    ORDER BY activacion.CompanyId;');
IF OBJECT_ID(N'contabilidad.AccountingPeriod', N'U') IS NOT NULL
  EXEC(N'
    SELECT CompanyId, PeriodYear, PeriodMonth, [State], ClosedAtUtc, ClosedBy
    FROM contabilidad.AccountingPeriod
    ORDER BY CompanyId, PeriodYear DESC, PeriodMonth DESC;');
IF COL_LENGTH(N'dbo.Transacciones', N'CycleState') IS NOT NULL
  EXEC(N'
    SELECT CompanyId, ISNULL(CycleState, N''(fuera del ciclo)'') AS CycleState,
           CONVERT(bigint, COUNT_BIG(*)) AS Polizas
    FROM dbo.Transacciones
    GROUP BY CompanyId, CycleState
    ORDER BY CompanyId, CycleState;');
