using System.Data.Common;
using Dapper;
using OrionERP.Application.Features.Restaurante;

namespace OrionERP.Infrastructure.Features.Restaurante;

public sealed partial class RestaurantDiagnosticsService
{
  /// <summary>
  /// Mide de una sola pasada todo lo que las reglas necesitan. Los saldos de
  /// resultado se miden en el periodo consultado; los de balance y los del
  /// catálogo, inventario y recetas se miden al estado actual, porque describen
  /// la situación de la empresa y no la actividad del rango.
  /// </summary>
  private static async Task<DiagnosticFacts> LoadFactsAsync(
    DbConnection conn,
    string rfc,
    int siteId,
    DateTime from,
    DateTime to,
    DateTime toExclusive,
    CancellationToken ct)
  {
    const string sql =
      """
      /* 1. Configuración contable del punto de venta */
      SELECT CashAccount, CardBankAccount, TransferBankAccount, PlatformReceivableAccount, SalesAccount,
             VatAccount, DiscountAccount, TipsPayableAccount, PlatformCommissionAccount,
             InventoryAccount, CostOfSalesAccount, WasteAccount, DailyPolicyEnabled
      FROM restaurante.AccountingConfiguration
      WHERE Rfc = @Rfc AND SiteId = @SiteId;

      /* 2. Operación del punto de venta en el periodo */
      SELECT COUNT(*) AS OrdenesPagadas,
             CAST(ISNULL(SUM(pagadas.Total), 0) AS decimal(18,2))    AS CobrosPos,
             CAST(ISNULL(SUM(pagadas.TaxTotal), 0) AS decimal(18,2)) AS IvaPos,
             ISNULL(SUM(pagadas.SinLigar), 0) AS OrdenesSinLigar
      FROM
      (
        SELECT orderInfo.Total,
               orderInfo.TaxTotal,
               CASE WHEN liga.OrderId IS NULL THEN 1 ELSE 0 END AS SinLigar
        FROM restaurante.[Order] orderInfo
        OUTER APPLY
        (
          SELECT TOP (1) linkInfo.OrderId
          FROM restaurante.AccountingOrderLink linkInfo
          WHERE linkInfo.Rfc = orderInfo.Rfc AND linkInfo.OrderId = orderInfo.Id
        ) liga
        WHERE orderInfo.Rfc = @Rfc AND orderInfo.SiteId = @SiteId
          AND orderInfo.OperationalDate >= @From AND orderInfo.OperationalDate <= @To
          AND orderInfo.PaymentStatus = 'Paid' AND orderInfo.[Status] <> 'Cancelled'
      ) pagadas;

      /* 3. Saldos de resultado del periodo y de balance acumulados */
      SELECT
        CAST(ISNULL(SUM(CASE WHEN periodo.Nivel1 IN ('208','209') THEN periodo.Haber - periodo.Debe END), 0) AS decimal(18,2)) AS IvaTrasladadoContable,
        CAST(ISNULL(SUM(CASE WHEN periodo.Nivel1 = '401' THEN periodo.Haber - periodo.Debe END), 0) AS decimal(18,2))          AS IngresoContable,
        CAST(ISNULL(SUM(CASE WHEN periodo.Nivel1 = '401' AND periodo.Nivel2 = '00' THEN periodo.Haber - periodo.Debe END), 0) AS decimal(18,2)) AS IngresoEnEncabezado,
        CAST(ISNULL(SUM(CASE WHEN periodo.Nivel1 = '502' THEN periodo.Debe - periodo.Haber END), 0) AS decimal(18,2))          AS Compras,
        CAST(ISNULL(SUM(CASE WHEN periodo.Nivel1 = '501' THEN periodo.Debe - periodo.Haber END), 0) AS decimal(18,2))          AS CostoVenta,
        CAST(ISNULL(SUM(CASE WHEN periodo.Nivel1 IN ('601','603') AND periodo.Nivel2 = '01' THEN periodo.Debe - periodo.Haber END), 0) AS decimal(18,2)) AS Sueldos,
        CAST(ISNULL(SUM(CASE WHEN periodo.Nivel1 IN ('216','213') THEN periodo.Haber - periodo.Debe END), 0) AS decimal(18,2)) AS Retenciones
      FROM
      (
        SELECT registro.Nivel1, registro.Nivel2, registro.Debe, registro.Haber
        FROM dbo.Registro_Contable registro
        JOIN dbo.Transacciones poliza ON poliza.ID = registro.TransaccionID
        WHERE poliza.RFC = @Rfc AND poliza.Fecha >= @From AND poliza.Fecha < @ToExclusive
      ) periodo;

      /* 4. Saldos de balance acumulados a la fecha de corte */
      SELECT
        CAST(ISNULL(SUM(CASE WHEN historico.Nivel1 = '102' THEN historico.Debe - historico.Haber END), 0) AS decimal(18,2)) AS SaldoBancos,
        CAST(ISNULL(SUM(CASE WHEN historico.Nivel1 IN ('151','152','153','154','155','156','157','159','160','170','173','174','181')
                             THEN historico.Debe - historico.Haber END), 0) AS decimal(18,2)) AS ActivoFijo,
        CAST(ISNULL(SUM(CASE WHEN historico.Nivel1 IN ('301','302','303','304','305','306') THEN historico.Haber - historico.Debe END), 0) AS decimal(18,2)) AS Capital,
        CAST(ISNULL(SUM(CASE WHEN historico.Nivel1 IN ('205','251') THEN historico.Haber - historico.Debe END), 0) AS decimal(18,2)) AS Acreedores,
        ISNULL(SUM(CASE WHEN historico.Nivel1 = '115' THEN 1 END), 0) AS MovimientosInventarioContable
      FROM
      (
        SELECT registro.Nivel1, registro.Debe, registro.Haber
        FROM dbo.Registro_Contable registro
        JOIN dbo.Transacciones poliza ON poliza.ID = registro.TransaccionID
        WHERE poliza.RFC = @Rfc AND poliza.Fecha < @ToExclusive
      ) historico;

      /* 5. Movimientos contra cuentas de encabezado */
      SELECT COUNT(*) AS Movimientos,
             CAST(ISNULL(SUM(encabezado.Debe + encabezado.Haber), 0) AS decimal(18,2)) AS Importe,
             ISNULL
             (
               (
                 SELECT STRING_AGG(CONVERT(varchar(50), niveles.Nivel1), ',') WITHIN GROUP (ORDER BY niveles.Nivel1)
                 FROM (SELECT DISTINCT interno.Nivel1 FROM
                       (
                         SELECT registroInterno.Nivel1
                         FROM dbo.Registro_Contable registroInterno
                         JOIN dbo.Transacciones polizaInterna ON polizaInterna.ID = registroInterno.TransaccionID
                         WHERE polizaInterna.RFC = @Rfc AND polizaInterna.Fecha < @ToExclusive
                           AND registroInterno.Nivel2 = '00'
                       ) interno) niveles
               ), ''
             ) AS Agrupadores
      FROM
      (
        SELECT registro.Debe, registro.Haber
        FROM dbo.Registro_Contable registro
        JOIN dbo.Transacciones poliza ON poliza.ID = registro.TransaccionID
        WHERE poliza.RFC = @Rfc AND poliza.Fecha < @ToExclusive AND registro.Nivel2 = '00'
      ) encabezado;

      /* 6. Cuentas de detalle duplicadas por descripción */
      SELECT COUNT(*) AS Duplicadas,
             ISNULL(STRING_AGG(CONVERT(varchar(200), duplicadas.Descripcion), ' · '), '') AS Detalle
      FROM
      (
        SELECT TOP (5) cuenta.Descripcion
        FROM dbo.CuentasContables cuenta
        WHERE cuenta.RFC = @Rfc AND cuenta.Nivel3 <> '00'
        GROUP BY cuenta.Descripcion
        HAVING COUNT(*) > 1
        ORDER BY COUNT(*) DESC
      ) duplicadas;

      /* 7. Pólizas sin asientos y con importe distinto al asiento */
      SELECT
        (
          SELECT COUNT(*)
          FROM dbo.Transacciones poliza
          WHERE poliza.RFC = @Rfc AND poliza.Fecha < @ToExclusive
            AND NOT EXISTS (SELECT 1 FROM dbo.Registro_Contable registro WHERE registro.TransaccionID = poliza.ID)
        ) AS PolizasSinAsientos,
        (
          SELECT CAST(ISNULL(SUM(poliza.Monto), 0) AS decimal(18,2))
          FROM dbo.Transacciones poliza
          WHERE poliza.RFC = @Rfc AND poliza.Fecha < @ToExclusive
            AND NOT EXISTS (SELECT 1 FROM dbo.Registro_Contable registro WHERE registro.TransaccionID = poliza.ID)
        ) AS ImportePolizasSinAsientos,
        (
          SELECT COUNT(*)
          FROM
          (
            SELECT poliza.ID, poliza.Monto, SUM(registro.Debe) AS Debe
            FROM dbo.Transacciones poliza
            JOIN dbo.Registro_Contable registro ON registro.TransaccionID = poliza.ID
            WHERE poliza.RFC = @Rfc AND poliza.Fecha < @ToExclusive
            GROUP BY poliza.ID, poliza.Monto
            HAVING ABS(SUM(registro.Debe) - poliza.Monto) > 0.01
          ) descuadradas
        ) AS PolizasDescuadradasMonto;

      /* 8. Personal y nómina */
      SELECT
        (SELECT COUNT(*) FROM dbo.Capital_Humano personal WHERE personal.RFC = @Rfc) AS Empleados,
        (SELECT COUNT(*) FROM rh.PrenominaPeriod periodo WHERE periodo.Rfc = @Rfc)   AS PeriodosNomina,
        (SELECT COUNT(*) FROM dbo.SatRfcProfile perfil WHERE perfil.Rfc = @Rfc)      AS PerfilSat;

      /* 9. Turnos de caja del periodo */
      SELECT COUNT(*) AS TurnosCerrados,
             CAST(ISNULL(SUM(turno.Difference), 0) AS decimal(18,2))      AS DiferenciaNeta,
             CAST(ISNULL(SUM(ABS(turno.Difference)), 0) AS decimal(18,2)) AS DiferenciaAbsoluta,
             ISNULL(SUM(CASE WHEN ABS(ISNULL(turno.Difference, 0)) > 1 THEN 1 ELSE 0 END), 0) AS ConDiferencia,
             ISNULL(SUM(CASE WHEN turno.ApprovedAt IS NULL THEN 1 ELSE 0 END), 0)             AS SinAprobar
      FROM restaurante.CashShift turno
      WHERE turno.Rfc = @Rfc AND turno.SiteId = @SiteId AND turno.[Status] = 'Closed'
        AND CAST(turno.ClosedAt AS date) >= @From AND CAST(turno.ClosedAt AS date) <= @To;

      /* 10. Inventario: valor, conteos imposibles y existencia fantasma */
      SELECT
        (
          SELECT CAST(ISNULL(SUM(saldo.Quantity * ISNULL(material.BaseUnitPrice, 0)), 0) AS decimal(18,2))
          FROM logistica.StockBalance saldo
          JOIN logistica.Material material ON material.Rfc = saldo.Rfc AND material.Id = saldo.MaterialId
          WHERE saldo.Rfc = @Rfc AND saldo.IsRemoved = 0
        ) AS ValorInventario,
        (
          SELECT COUNT(*)
          FROM logistica.StockBalance saldo
          WHERE saldo.Rfc = @Rfc AND saldo.IsRemoved = 0 AND saldo.Quantity <> 0
        ) AS SaldosInventario,
        (
          SELECT COUNT(*)
          FROM logistica.StockTransaction movimiento
          WHERE movimiento.Rfc = @Rfc AND ABS(movimiento.QuantityDelta) >= @ConteoUmbral
        ) AS ConteosAtipicos;

      /* 11. Ejemplos de conteos imposibles */
      SELECT TOP (3) material.Description AS Material,
             CAST(ABS(movimiento.QuantityDelta) AS decimal(28,0)) AS Cantidad
      FROM logistica.StockTransaction movimiento
      JOIN logistica.Material material ON material.Rfc = movimiento.Rfc AND material.Id = movimiento.MaterialId
      WHERE movimiento.Rfc = @Rfc AND ABS(movimiento.QuantityDelta) >= @ConteoUmbral
      ORDER BY ABS(movimiento.QuantityDelta) DESC;

      /* 12. Existencia de productos que se preparan al momento */
      SELECT COUNT(DISTINCT material.Id) AS Materiales,
             CAST(ISNULL(SUM(saldo.Quantity), 0) AS decimal(18,2)) AS Unidades,
             CAST(ISNULL(SUM(saldo.Quantity * ISNULL(material.BaseUnitPrice, 0)), 0) AS decimal(18,2)) AS Valor
      FROM logistica.Material material
      JOIN logistica.StockBalance saldo
        ON saldo.Rfc = material.Rfc AND saldo.MaterialId = material.Id AND saldo.IsRemoved = 0
      WHERE material.Rfc = @Rfc AND material.FulfillmentMode = 'MakeToOrder' AND saldo.Quantity > 0;

      /* 13. Materiales cuyo costo unitario es su precio de venta */
      SELECT COUNT(DISTINCT material.Id) AS Materiales
      FROM logistica.Material material
      JOIN restaurante.Product product ON product.Rfc = material.Rfc AND product.MaterialId = material.Id
      WHERE material.Rfc = @Rfc
        AND material.FulfillmentMode = 'MakeToOrder'
        AND product.Price > 0
        AND material.BaseUnitPrice >= product.Price * 0.9;

      /* 14. Componentes de recetas activas y su cobertura de conversión */
      SELECT COUNT(*) AS Componentes,
             ISNULL(SUM(CASE WHEN COALESCE(materialConversion.Factor, globalConversion.Factor,
                        CASE WHEN component.UnitId = componentMaterial.BaseUnitId THEN 1 END) IS NULL
                      THEN 1 ELSE 0 END), 0) AS SinConversion
      FROM logistica.BomHeader header
      JOIN logistica.BomVersion versionInfo
        ON versionInfo.Rfc = header.Rfc AND versionInfo.BomHeaderId = header.Id AND versionInfo.[Status] = 'Active'
      JOIN logistica.BomComponent component
        ON component.Rfc = versionInfo.Rfc AND component.BomVersionId = versionInfo.Id
      JOIN logistica.Material componentMaterial
        ON componentMaterial.Rfc = component.Rfc AND componentMaterial.Id = component.ComponentMaterialId
      OUTER APPLY
      (
        SELECT TOP (1) conversionInfo.Factor
        FROM logistica.MaterialUnitConversion conversionInfo
        WHERE conversionInfo.Rfc = componentMaterial.Rfc AND conversionInfo.MaterialId = componentMaterial.Id
          AND conversionInfo.FromUnitId = component.UnitId AND conversionInfo.ToUnitId = componentMaterial.BaseUnitId
          AND conversionInfo.IsActive = 1
      ) materialConversion
      OUTER APPLY
      (
        SELECT TOP (1) conversionInfo.Factor
        FROM logistica.UnitConversion conversionInfo
        WHERE conversionInfo.FromUnitId = component.UnitId AND conversionInfo.ToUnitId = componentMaterial.BaseUnitId
          AND conversionInfo.IsActive = 1
      ) globalConversion
      WHERE header.Rfc = @Rfc;

      /* 15. Órdenes cuyo costo guardado es imposible */
      SELECT COUNT(*) AS Ordenes,
             CAST(ISNULL(MAX(orderInfo.TheoreticalCost), 0) AS decimal(18,2)) AS MaximoCosto
      FROM restaurante.[Order] orderInfo
      WHERE orderInfo.Rfc = @Rfc AND orderInfo.SiteId = @SiteId
        AND orderInfo.OperationalDate >= @From AND orderInfo.OperationalDate <= @To
        AND orderInfo.[Status] <> 'Cancelled'
        AND orderInfo.TheoreticalCost > 100 AND orderInfo.TheoreticalCost > orderInfo.Total * 2;

      /* 16. Productos vendidos sin costo asignado */
      SELECT COUNT(DISTINCT product.Id) AS Productos,
             CAST(ISNULL(SUM(line.LineTotal), 0) AS decimal(18,2)) AS Venta
      FROM restaurante.OrderLine line
      JOIN restaurante.[Order] orderInfo ON orderInfo.Rfc = line.Rfc AND orderInfo.Id = line.OrderId
      JOIN restaurante.Product product ON product.Rfc = line.Rfc AND product.Id = line.ProductId
      JOIN logistica.Material productMaterial
        ON productMaterial.Rfc = product.Rfc AND productMaterial.Id = product.MaterialId
      WHERE orderInfo.Rfc = @Rfc AND orderInfo.SiteId = @SiteId
        AND orderInfo.OperationalDate >= @From AND orderInfo.OperationalDate <= @To
        AND orderInfo.PaymentStatus = 'Paid' AND orderInfo.[Status] <> 'Cancelled'
        AND NOT EXISTS
        (
          SELECT 1
          FROM logistica.BomHeader header
          JOIN logistica.BomVersion versionInfo
            ON versionInfo.Rfc = header.Rfc AND versionInfo.BomHeaderId = header.Id AND versionInfo.[Status] = 'Active'
          WHERE header.Rfc = product.Rfc AND header.ProductMaterialId = product.MaterialId
        )
        AND NOT (productMaterial.FulfillmentMode = 'StockItem' AND productMaterial.BaseUnitPrice > 0);
      """;

    using var multi = await conn.QueryMultipleAsync(new CommandDefinition(sql, new
    {
      Rfc = rfc,
      SiteId = siteId,
      From = from,
      To = to,
      ToExclusive = toExclusive,
      ConteoUmbral = ConteoAtipicoUmbral
    }, cancellationToken: ct));

    var facts = new DiagnosticFacts
    {
      Configuracion = await multi.ReadSingleOrDefaultAsync<RestaurantAccountingConfigurationDto>()
    };

    var pos = await multi.ReadSingleAsync<PosRow>();
    facts.OrdenesPagadas = pos.OrdenesPagadas;
    facts.CobrosPos = pos.CobrosPos;
    facts.IvaPos = pos.IvaPos;
    facts.OrdenesSinLigar = pos.OrdenesSinLigar;

    var resultado = await multi.ReadSingleAsync<ResultRow>();
    facts.IvaTrasladadoContable = resultado.IvaTrasladadoContable;
    facts.IngresoContable = resultado.IngresoContable;
    facts.IngresoEnEncabezado = resultado.IngresoEnEncabezado;
    facts.Compras = resultado.Compras;
    facts.CostoVenta = resultado.CostoVenta;
    facts.Sueldos = resultado.Sueldos;
    facts.Retenciones = resultado.Retenciones;

    var balance = await multi.ReadSingleAsync<BalanceRow>();
    facts.SaldoBancos = balance.SaldoBancos;
    facts.ActivoFijo = balance.ActivoFijo;
    facts.Capital = balance.Capital;
    facts.Acreedores = balance.Acreedores;
    facts.MovimientosInventarioContable = balance.MovimientosInventarioContable;

    var encabezado = await multi.ReadSingleAsync<HeaderAccountRow>();
    facts.MovimientosEncabezado = encabezado.Movimientos;
    facts.ImporteEncabezado = encabezado.Importe;
    facts.AgrupadoresEncabezado = string.Join(",",
      encabezado.Agrupadores.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct());

    var duplicadas = await multi.ReadSingleAsync<DuplicateRow>();
    facts.CuentasDuplicadas = duplicadas.Duplicadas;
    facts.DetalleDuplicadas = duplicadas.Detalle;

    var polizas = await multi.ReadSingleAsync<PolicyRow>();
    facts.PolizasSinAsientos = polizas.PolizasSinAsientos;
    facts.ImportePolizasSinAsientos = polizas.ImportePolizasSinAsientos;
    facts.PolizasDescuadradasMonto = polizas.PolizasDescuadradasMonto;

    var personal = await multi.ReadSingleAsync<StaffRow>();
    facts.Empleados = personal.Empleados;
    facts.PeriodosNomina = personal.PeriodosNomina;
    facts.TienePerfilSat = personal.PerfilSat > 0;

    var caja = await multi.ReadSingleAsync<CashRow>();
    facts.TurnosCerrados = caja.TurnosCerrados;
    facts.DiferenciaCajaNeta = caja.DiferenciaNeta;
    facts.DiferenciaCajaAbsoluta = caja.DiferenciaAbsoluta;
    facts.TurnosConDiferencia = caja.ConDiferencia;
    facts.TurnosSinAprobar = caja.SinAprobar;

    var inventario = await multi.ReadSingleAsync<InventoryRow>();
    facts.ValorInventario = inventario.ValorInventario;
    facts.SaldosInventario = inventario.SaldosInventario;
    facts.ConteosAtipicos = inventario.ConteosAtipicos;

    var ejemplos = (await multi.ReadAsync<CountSampleRow>()).AsList();
    facts.DetalleConteos = string.Join(" · ",
      ejemplos.Select(sample => $"{sample.Material} {sample.Cantidad:N0}"));

    var fantasma = await multi.ReadSingleAsync<PhantomRow>();
    facts.MaterialesFantasma = fantasma.Materiales;
    facts.UnidadesFantasma = fantasma.Unidades;
    facts.ValorFantasma = fantasma.Valor;

    facts.MaterialesPrecioComoCosto = await multi.ReadSingleAsync<int>();

    var componentes = await multi.ReadSingleAsync<ComponentRow>();
    facts.ComponentesTotales = componentes.Componentes;
    facts.ComponentesSinConversion = componentes.SinConversion;

    var costo = await multi.ReadSingleAsync<AbsurdCostRow>();
    facts.OrdenesCostoAbsurdo = costo.Ordenes;
    facts.MaxCostoAbsurdo = costo.MaximoCosto;

    var sinCosto = await multi.ReadSingleAsync<UncostedRow>();
    facts.ProductosSinCosto = sinCosto.Productos;
    facts.VentaSinCosto = sinCosto.Venta;

    await LoadRecipeFactsAsync(conn, rfc, facts, ct);
    return facts;
  }

  /// <summary>
  /// Recetas activas de productos a la venta: cuáles no cuestan, cuáles declaran
  /// su rendimiento en unidad de lote y cuánto se alejó el costo guardado del
  /// recalculado con los precios de hoy.
  /// </summary>
  private static async Task LoadRecipeFactsAsync(
    DbConnection conn,
    string rfc,
    DiagnosticFacts facts,
    CancellationToken ct)
  {
    var sql = RestaurantAnalyticsSql.RecipeCostCte +
      """

      SELECT COUNT(*) AS RecetasActivas,
             ISNULL(SUM(CASE WHEN receta.CostoRecalculado <= 0.01 THEN 1 ELSE 0 END), 0) AS SinCosto,
             ISNULL(SUM(CASE WHEN receta.YieldQuantity > 1 THEN 1 ELSE 0 END), 0)        AS RendimientoLote,
             ISNULL(SUM(CASE WHEN ABS(receta.CostoCongelado - receta.CostoRecalculado) > 0.01 THEN 1 ELSE 0 END), 0) AS ConDeriva,
             CAST(ISNULL(MAX(ABS(receta.CostoCongelado - receta.CostoRecalculado)), 0) AS decimal(18,2)) AS DerivaMaxima
      FROM RecetaCosto receta
      WHERE EXISTS
      (
        SELECT 1 FROM restaurante.Product product
        WHERE product.Rfc = @Rfc AND product.MaterialId = receta.ProductMaterialId
      );
      """;

    var resumen = await conn.QuerySingleAsync<RecipeFactRow>(new CommandDefinition(
      sql, new { Rfc = rfc }, cancellationToken: ct));
    facts.RecetasActivas = resumen.RecetasActivas;
    facts.RecetasSinCosto = resumen.SinCosto;
    facts.RecetasRendimientoLote = resumen.RendimientoLote;
    facts.RecetasConDeriva = resumen.ConDeriva;
    facts.DerivaMaxima = resumen.DerivaMaxima;

    var detalleSql = RestaurantAnalyticsSql.RecipeCostCte +
      """

      SELECT TOP (4) material.Description AS Producto,
             CAST(receta.CostoRecalculado AS decimal(18,4)) AS Costo,
             CAST(receta.YieldQuantity AS decimal(18,2))    AS Rendimiento
      FROM RecetaCosto receta
      JOIN logistica.Material material ON material.Rfc = @Rfc AND material.Id = receta.ProductMaterialId
      WHERE EXISTS
      (
        SELECT 1 FROM restaurante.Product product
        WHERE product.Rfc = @Rfc AND product.MaterialId = receta.ProductMaterialId
      )
      AND (receta.CostoRecalculado <= 0.01 OR receta.YieldQuantity > 1)
      ORDER BY receta.YieldQuantity DESC, receta.CostoRecalculado;
      """;

    var ejemplos = (await conn.QueryAsync<RecipeSampleRow>(new CommandDefinition(
      detalleSql, new { Rfc = rfc }, cancellationToken: ct))).AsList();
    facts.DetalleRecetas = string.Join(" · ", ejemplos.Select(sample =>
      sample.Rendimiento > 1
        ? $"{sample.Producto} rinde {sample.Rendimiento:N0}"
        : $"{sample.Producto} sin costo"));
  }

  private sealed class DiagnosticFacts
  {
    public RestaurantAccountingConfigurationDto? Configuracion { get; set; }

    public int OrdenesPagadas { get; set; }
    public decimal CobrosPos { get; set; }
    public decimal IvaPos { get; set; }
    public int OrdenesSinLigar { get; set; }

    public decimal IvaTrasladadoContable { get; set; }
    public decimal IngresoContable { get; set; }
    public decimal IngresoEnEncabezado { get; set; }
    public decimal Compras { get; set; }
    public decimal CostoVenta { get; set; }
    public decimal Sueldos { get; set; }
    public decimal Retenciones { get; set; }

    public decimal SaldoBancos { get; set; }
    public decimal ActivoFijo { get; set; }
    public decimal Capital { get; set; }
    public decimal Acreedores { get; set; }
    public int MovimientosInventarioContable { get; set; }

    public int MovimientosEncabezado { get; set; }
    public decimal ImporteEncabezado { get; set; }
    public string AgrupadoresEncabezado { get; set; } = string.Empty;

    public int CuentasDuplicadas { get; set; }
    public string DetalleDuplicadas { get; set; } = string.Empty;

    public int PolizasSinAsientos { get; set; }
    public decimal ImportePolizasSinAsientos { get; set; }
    public int PolizasDescuadradasMonto { get; set; }

    public int Empleados { get; set; }
    public int PeriodosNomina { get; set; }
    public bool TienePerfilSat { get; set; }

    public int TurnosCerrados { get; set; }
    public decimal DiferenciaCajaNeta { get; set; }
    public decimal DiferenciaCajaAbsoluta { get; set; }
    public int TurnosConDiferencia { get; set; }
    public int TurnosSinAprobar { get; set; }

    public decimal ValorInventario { get; set; }
    public int SaldosInventario { get; set; }
    public int ConteosAtipicos { get; set; }
    public string DetalleConteos { get; set; } = string.Empty;

    public int MaterialesFantasma { get; set; }
    public decimal UnidadesFantasma { get; set; }
    public decimal ValorFantasma { get; set; }

    public int MaterialesPrecioComoCosto { get; set; }
    public int ComponentesTotales { get; set; }
    public int ComponentesSinConversion { get; set; }

    public int OrdenesCostoAbsurdo { get; set; }
    public decimal MaxCostoAbsurdo { get; set; }

    public int ProductosSinCosto { get; set; }
    public decimal VentaSinCosto { get; set; }

    public int RecetasActivas { get; set; }
    public int RecetasSinCosto { get; set; }
    public int RecetasRendimientoLote { get; set; }
    public int RecetasConDeriva { get; set; }
    public decimal DerivaMaxima { get; set; }
    public string DetalleRecetas { get; set; } = string.Empty;
  }

  private sealed class PosRow
  {
    public int OrdenesPagadas { get; set; }
    public decimal CobrosPos { get; set; }
    public decimal IvaPos { get; set; }
    public int OrdenesSinLigar { get; set; }
  }

  private sealed class ResultRow
  {
    public decimal IvaTrasladadoContable { get; set; }
    public decimal IngresoContable { get; set; }
    public decimal IngresoEnEncabezado { get; set; }
    public decimal Compras { get; set; }
    public decimal CostoVenta { get; set; }
    public decimal Sueldos { get; set; }
    public decimal Retenciones { get; set; }
  }

  private sealed class BalanceRow
  {
    public decimal SaldoBancos { get; set; }
    public decimal ActivoFijo { get; set; }
    public decimal Capital { get; set; }
    public decimal Acreedores { get; set; }
    public int MovimientosInventarioContable { get; set; }
  }

  private sealed class HeaderAccountRow
  {
    public int Movimientos { get; set; }
    public decimal Importe { get; set; }
    public string Agrupadores { get; set; } = string.Empty;
  }

  private sealed class DuplicateRow
  {
    public int Duplicadas { get; set; }
    public string Detalle { get; set; } = string.Empty;
  }

  private sealed class PolicyRow
  {
    public int PolizasSinAsientos { get; set; }
    public decimal ImportePolizasSinAsientos { get; set; }
    public int PolizasDescuadradasMonto { get; set; }
  }

  private sealed class StaffRow
  {
    public int Empleados { get; set; }
    public int PeriodosNomina { get; set; }
    public int PerfilSat { get; set; }
  }

  private sealed class CashRow
  {
    public int TurnosCerrados { get; set; }
    public decimal DiferenciaNeta { get; set; }
    public decimal DiferenciaAbsoluta { get; set; }
    public int ConDiferencia { get; set; }
    public int SinAprobar { get; set; }
  }

  private sealed class InventoryRow
  {
    public decimal ValorInventario { get; set; }
    public int SaldosInventario { get; set; }
    public int ConteosAtipicos { get; set; }
  }

  private sealed class CountSampleRow
  {
    public string Material { get; set; } = string.Empty;
    public decimal Cantidad { get; set; }
  }

  private sealed class PhantomRow
  {
    public int Materiales { get; set; }
    public decimal Unidades { get; set; }
    public decimal Valor { get; set; }
  }

  private sealed class ComponentRow
  {
    public int Componentes { get; set; }
    public int SinConversion { get; set; }
  }

  private sealed class AbsurdCostRow
  {
    public int Ordenes { get; set; }
    public decimal MaximoCosto { get; set; }
  }

  private sealed class UncostedRow
  {
    public int Productos { get; set; }
    public decimal Venta { get; set; }
  }

  private sealed class RecipeFactRow
  {
    public int RecetasActivas { get; set; }
    public int SinCosto { get; set; }
    public int RendimientoLote { get; set; }
    public int ConDeriva { get; set; }
    public decimal DerivaMaxima { get; set; }
  }

  private sealed class RecipeSampleRow
  {
    public string Producto { get; set; } = string.Empty;
    public decimal Costo { get; set; }
    public decimal Rendimiento { get; set; }
  }
}
