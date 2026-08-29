using System.Data.Common;
using Dapper;
using OrionERP.Application.Common;
using OrionERP.Application.Features.Logistica.Shared;
using OrionERP.Application.Features.Restaurante;

namespace OrionERP.Infrastructure.Features.Restaurante;

public sealed class RestaurantAnalyticsService : IRestaurantAnalyticsService
{
  private readonly IDbConnectionFactory _connectionFactory;

  public RestaurantAnalyticsService(IDbConnectionFactory connectionFactory)
  {
    _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
  }


  public async Task<RestaurantAccountingReportDto> GetAccountingReportAsync(
    RestaurantAnalyticsQuery query,
    CancellationToken ct = default)
  {
    var rfc = LogisticsRfc.Require(query.Rfc);
    var (from, toExclusive, prevFrom, prevToExclusive, ytdFrom) = ResolvePeriods(query);

    using var conn = CreateConnection();
    await EnsureMapSeedAsync(conn, rfc, ct);

    const string sql =
      """
      /* 1. Saldos por agrupador en el periodo */
      SELECT registro.Nivel1,
             CAST(SUM(registro.Debe) AS decimal(18,2))  AS Cargos,
             CAST(SUM(registro.Haber) AS decimal(18,2)) AS Abonos,
             COUNT(*) AS Movimientos
      FROM dbo.Registro_Contable registro
      JOIN dbo.Transacciones poliza ON poliza.ID = registro.TransaccionID
      WHERE poliza.RFC = @Rfc AND poliza.Fecha >= @From AND poliza.Fecha < @ToExclusive
      GROUP BY registro.Nivel1;

      /* 2. Saldos por agrupador en el periodo anterior */
      SELECT registro.Nivel1,
             CAST(SUM(registro.Debe) AS decimal(18,2))  AS Cargos,
             CAST(SUM(registro.Haber) AS decimal(18,2)) AS Abonos,
             COUNT(*) AS Movimientos
      FROM dbo.Registro_Contable registro
      JOIN dbo.Transacciones poliza ON poliza.ID = registro.TransaccionID
      WHERE poliza.RFC = @Rfc AND poliza.Fecha >= @PrevFrom AND poliza.Fecha < @PrevToExclusive
      GROUP BY registro.Nivel1;

      /* 3. Saldos por agrupador acumulados en el ejercicio */
      SELECT registro.Nivel1,
             CAST(SUM(registro.Debe) AS decimal(18,2))  AS Cargos,
             CAST(SUM(registro.Haber) AS decimal(18,2)) AS Abonos,
             COUNT(*) AS Movimientos
      FROM dbo.Registro_Contable registro
      JOIN dbo.Transacciones poliza ON poliza.ID = registro.TransaccionID
      WHERE poliza.RFC = @Rfc AND poliza.Fecha >= @YtdFrom AND poliza.Fecha < @ToExclusive
      GROUP BY registro.Nivel1;

      /* 4. Catálogo de agrupadores nivel 1 del RFC */
      SELECT cuenta.Nivel1, MAX(cuenta.Descripcion) AS Descripcion
      FROM dbo.CuentasContables cuenta
      WHERE cuenta.RFC = @Rfc AND cuenta.Nivel2 = '00' AND cuenta.Nivel3 = '00'
      GROUP BY cuenta.Nivel1;

      /* 5. Operación del punto de venta en el periodo */
      SELECT COUNT(*) AS OrdenesPagadas,
             CAST(ISNULL(SUM(orderInfo.Total - orderInfo.TaxTotal), 0) AS decimal(18,2)) AS VentaNeta,
             CAST(ISNULL(SUM(orderInfo.TaxTotal), 0) AS decimal(18,2))      AS Iva,
             CAST(ISNULL(SUM(orderInfo.DiscountTotal), 0) AS decimal(18,2)) AS Descuentos,
             CAST(ISNULL(SUM(orderInfo.TipTotal), 0) AS decimal(18,2))      AS Propinas,
             CAST(ISNULL(SUM(orderInfo.Total), 0) AS decimal(18,2))         AS Total,
             COUNT(DISTINCT orderInfo.OperationalDate) AS DiasConVenta
      FROM restaurante.[Order] orderInfo
      WHERE orderInfo.Rfc = @Rfc AND orderInfo.SiteId = @SiteId
        AND orderInfo.OperationalDate >= @FromDate AND orderInfo.OperationalDate <= @ToDate
        AND orderInfo.PaymentStatus = 'Paid' AND orderInfo.[Status] <> 'Cancelled';

      /* 6. Cobros por forma de pago */
      SELECT paymentInfo.PaymentMethod AS Label,
             CAST(SUM(paymentInfo.Amount - paymentInfo.RefundedAmount) AS decimal(18,2)) AS Amount
      FROM restaurante.Payment paymentInfo
      JOIN restaurante.[Order] orderInfo
        ON orderInfo.Rfc = paymentInfo.Rfc AND orderInfo.Id = paymentInfo.OrderId
      WHERE orderInfo.Rfc = @Rfc AND orderInfo.SiteId = @SiteId
        AND orderInfo.OperationalDate >= @FromDate AND orderInfo.OperationalDate <= @ToDate
        AND orderInfo.[Status] <> 'Cancelled'
      GROUP BY paymentInfo.PaymentMethod;

      /* 7. Órdenes ligadas a póliza y días con póliza */
      SELECT
        (
          SELECT COUNT(*)
          FROM restaurante.AccountingOrderLink linkInfo
          JOIN restaurante.[Order] orderInfo
            ON orderInfo.Rfc = linkInfo.Rfc AND orderInfo.Id = linkInfo.OrderId
          WHERE linkInfo.Rfc = @Rfc AND orderInfo.SiteId = @SiteId
            AND orderInfo.OperationalDate >= @FromDate AND orderInfo.OperationalDate <= @ToDate
        ) AS OrdenesLigadas,
        (
          SELECT COUNT(DISTINCT CAST(poliza.Fecha AS date))
          FROM dbo.Transacciones poliza
          WHERE poliza.RFC = @Rfc AND poliza.Fecha >= @From AND poliza.Fecha < @ToExclusive
        ) AS DiasConPoliza;

      /* 8. Turnos de caja del periodo */
      SELECT COUNT(*) AS Turnos,
             CAST(ISNULL(SUM(turno.Difference), 0) AS decimal(18,2))      AS DiferenciaNeta,
             CAST(ISNULL(SUM(ABS(turno.Difference)), 0) AS decimal(18,2)) AS DiferenciaAbsoluta,
             SUM(CASE WHEN ABS(ISNULL(turno.Difference, 0)) > 1 THEN 1 ELSE 0 END) AS ConDiferencia,
             SUM(CASE WHEN turno.ApprovedAt IS NULL THEN 1 ELSE 0 END)             AS SinAprobar
      FROM restaurante.CashShift turno
      WHERE turno.Rfc = @Rfc AND turno.SiteId = @SiteId AND turno.[Status] = 'Closed'
        AND CAST(turno.ClosedAt AS date) >= @FromDate AND CAST(turno.ClosedAt AS date) <= @ToDate;

      /* 9. Serie diaria de la operación */
      SELECT pagadas.OperationalDate AS Fecha,
             COUNT(*) AS Ordenes,
             CAST(SUM(pagadas.Total - pagadas.TaxTotal) AS decimal(18,2)) AS VentaPos,
             CAST(SUM(pagadas.TaxTotal) AS decimal(18,2))                 AS IvaPos,
             CAST(MAX(pagadas.Ligada) AS bit) AS TienePolizaLigada
      FROM
      (
        SELECT orderInfo.OperationalDate, orderInfo.Total, orderInfo.TaxTotal,
               CASE WHEN liga.OrderId IS NULL THEN 0 ELSE 1 END AS Ligada
        FROM restaurante.[Order] orderInfo
        OUTER APPLY
        (
          SELECT TOP (1) linkInfo.OrderId
          FROM restaurante.AccountingOrderLink linkInfo
          WHERE linkInfo.Rfc = orderInfo.Rfc AND linkInfo.OrderId = orderInfo.Id
        ) liga
        WHERE orderInfo.Rfc = @Rfc AND orderInfo.SiteId = @SiteId
          AND orderInfo.OperationalDate >= @FromDate AND orderInfo.OperationalDate <= @ToDate
          AND orderInfo.PaymentStatus = 'Paid' AND orderInfo.[Status] <> 'Cancelled'
      ) pagadas
      GROUP BY pagadas.OperationalDate
      ORDER BY pagadas.OperationalDate;

      /* 10. Serie diaria del ingreso contable */
      SELECT CAST(poliza.Fecha AS date) AS Fecha,
             CAST(SUM(registro.Haber - registro.Debe) AS decimal(18,2)) AS IngresoContable
      FROM dbo.Registro_Contable registro
      JOIN dbo.Transacciones poliza ON poliza.ID = registro.TransaccionID
      WHERE poliza.RFC = @Rfc AND poliza.Fecha >= @From AND poliza.Fecha < @ToExclusive
        AND registro.Nivel1 IN @IngresoNivel1
      GROUP BY CAST(poliza.Fecha AS date)
      ORDER BY CAST(poliza.Fecha AS date);
      """;

    var map = await LoadMapRowsAsync(conn, rfc, ct);
    var ingresoNivel1 = map
      .Where(row => row.ConceptoClave == RestaurantAgrupadorConceptos.IngresosVenta && row.Incluido)
      .Select(row => row.Nivel1)
      .DefaultIfEmpty("401")
      .ToArray();

    using var multi = await conn.QueryMultipleAsync(new CommandDefinition(sql, new
    {
      Rfc = rfc,
      query.SiteId,
      From = from,
      ToExclusive = toExclusive,
      PrevFrom = prevFrom,
      PrevToExclusive = prevToExclusive,
      YtdFrom = ytdFrom,
      FromDate = from.Date,
      ToDate = toExclusive.AddDays(-1).Date,
      IngresoNivel1 = ingresoNivel1
    }, cancellationToken: ct));

    var periodo = (await multi.ReadAsync<LedgerAggregateRow>()).ToDictionary(row => row.Nivel1, StringComparer.OrdinalIgnoreCase);
    var anterior = (await multi.ReadAsync<LedgerAggregateRow>()).ToDictionary(row => row.Nivel1, StringComparer.OrdinalIgnoreCase);
    var acumulado = (await multi.ReadAsync<LedgerAggregateRow>()).ToDictionary(row => row.Nivel1, StringComparer.OrdinalIgnoreCase);
    var catalogo = (await multi.ReadAsync<AgrupadorCatalogRow>()).ToDictionary(row => row.Nivel1, row => row.Descripcion, StringComparer.OrdinalIgnoreCase);
    var pos = await multi.ReadSingleAsync<PosSummaryRow>();
    var cobros = (await multi.ReadAsync<RestaurantReportBreakdownDto>()).AsList();
    var links = await multi.ReadSingleAsync<LinkSummaryRow>();
    var caja = await multi.ReadSingleAsync<CashSummaryRow>();
    var seriePos = (await multi.ReadAsync<DailyPosRow>()).AsList();
    var serieContable = (await multi.ReadAsync<DailyLedgerRow>())
      .ToDictionary(row => row.Fecha.Date, row => row.IngresoContable);

    var recipeCosts = await LoadRecipeCostsAsync(conn, rfc, query.SiteId, from.Date, toExclusive.AddDays(-1).Date, ct);
    var costoRecalculado = decimal.Round(recipeCosts.Sum(cost => cost.CostoVendido), 2);

    var mapDto = BuildMap(map, catalogo, periodo);
    var pnl = BuildPnl(map, catalogo, periodo, anterior, acumulado, from, toExclusive.AddDays(-1));
    var agrupadores = BuildAgrupadores(catalogo, periodo, map);

    var summary = new RestaurantAccountingSummaryDto
    {
      From = from,
      To = toExclusive.AddDays(-1),
      VentaNetaPos = pos.VentaNeta,
      IvaTrasladadoPos = pos.Iva,
      DescuentosPos = pos.Descuentos,
      CostoRecalculado = costoRecalculado,
      IngresoContable = pnl.Ingresos,
      GastoContable = pnl.Gastos + pnl.Costo,
      ResultadoContable = pnl.Resultado,
      OrdenesPagadas = pos.OrdenesPagadas,
      AgrupadoresIngreso = ConceptCodes(map, RestaurantAgrupadorConceptos.IngresosVenta),
      AgrupadoresIva = ConceptCodes(map, RestaurantAgrupadorConceptos.IvaTrasladado),
      AgrupadoresCosto = ConceptCodes(map, RestaurantAgrupadorConceptos.CostoVenta),
      AgrupadoresGasto = ConceptCodes(map, RestaurantAgrupadorConceptos.GastosGenerales)
        .Concat(ConceptCodes(map, RestaurantAgrupadorConceptos.GastosVenta))
        .Concat(ConceptCodes(map, RestaurantAgrupadorConceptos.GastosAdministracion))
        .Distinct()
        .ToList()
    };

    var costByDate = recipeCosts.Count == 0
      ? new Dictionary<DateTime, decimal>()
      : await LoadDailyCostAsync(conn, rfc, query.SiteId, from.Date, toExclusive.AddDays(-1).Date, ct);

    var daily = seriePos.Select(row => new RestaurantDailyLedgerPointDto
    {
      Fecha = row.Fecha,
      Ordenes = row.Ordenes,
      VentaPos = row.VentaPos,
      IvaPos = row.IvaPos,
      CostoRecalculado = costByDate.TryGetValue(row.Fecha.Date, out var costo) ? costo : 0m,
      IngresoContable = serieContable.TryGetValue(row.Fecha.Date, out var ingreso) ? ingreso : 0m,
      TienePolizaLigada = row.TienePolizaLigada
    }).ToList();

    var reconciliation = BuildReconciliation(map, periodo, pos, cobros, links, caja, costoRecalculado);

    return new RestaurantAccountingReportDto
    {
      Summary = summary,
      Pnl = pnl,
      Reconciliation = reconciliation,
      Map = mapDto,
      DailySeries = daily,
      Agrupadores = agrupadores
    };
  }

  public async Task<RestaurantAgrupadorMapDto> GetAgrupadorMapAsync(
    RestaurantAnalyticsQuery query,
    CancellationToken ct = default)
  {
    var rfc = LogisticsRfc.Require(query.Rfc);
    var (from, toExclusive, _, _, _) = ResolvePeriods(query);

    using var conn = CreateConnection();
    await EnsureMapSeedAsync(conn, rfc, ct);

    const string sql =
      """
      SELECT registro.Nivel1,
             CAST(SUM(registro.Debe) AS decimal(18,2))  AS Cargos,
             CAST(SUM(registro.Haber) AS decimal(18,2)) AS Abonos,
             COUNT(*) AS Movimientos
      FROM dbo.Registro_Contable registro
      JOIN dbo.Transacciones poliza ON poliza.ID = registro.TransaccionID
      WHERE poliza.RFC = @Rfc AND poliza.Fecha >= @From AND poliza.Fecha < @ToExclusive
      GROUP BY registro.Nivel1;

      SELECT cuenta.Nivel1, MAX(cuenta.Descripcion) AS Descripcion
      FROM dbo.CuentasContables cuenta
      WHERE cuenta.RFC = @Rfc AND cuenta.Nivel2 = '00' AND cuenta.Nivel3 = '00'
      GROUP BY cuenta.Nivel1;
      """;

    using var multi = await conn.QueryMultipleAsync(new CommandDefinition(
      sql, new { Rfc = rfc, From = from, ToExclusive = toExclusive }, cancellationToken: ct));
    var periodo = (await multi.ReadAsync<LedgerAggregateRow>()).ToDictionary(row => row.Nivel1, StringComparer.OrdinalIgnoreCase);
    var catalogo = (await multi.ReadAsync<AgrupadorCatalogRow>()).ToDictionary(row => row.Nivel1, row => row.Descripcion, StringComparer.OrdinalIgnoreCase);
    var map = await LoadMapRowsAsync(conn, rfc, ct);

    return BuildMap(map, catalogo, periodo);
  }

  public async Task<RestaurantCommandResult> SaveAgrupadorMapRowAsync(
    string rfc,
    RestaurantAgrupadorMapRowDto row,
    string userName,
    CancellationToken ct = default)
  {
    var normalizedRfc = LogisticsRfc.Require(rfc);
    var nivel1 = (row.Nivel1 ?? string.Empty).Trim();
    if (string.IsNullOrWhiteSpace(row.ConceptoClave))
      return RestaurantCommandResult.Fail("Falta el concepto del reporte.");
    if (string.IsNullOrWhiteSpace(nivel1))
      return RestaurantCommandResult.Fail("Falta el código agrupador.");
    if (row.Signo is not (1 or -1))
      return RestaurantCommandResult.Fail("El signo del agrupador debe ser 1 o -1.");

    using var conn = CreateConnection();
    var existe = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
      "SELECT COUNT(*) FROM dbo.CuentasContables WHERE RFC=@Rfc AND Nivel1=@Nivel1;",
      new { Rfc = normalizedRfc, Nivel1 = nivel1 }, cancellationToken: ct));
    if (existe == 0)
      return RestaurantCommandResult.Fail($"El agrupador {nivel1} no existe en el catálogo de cuentas del RFC.");

    var affected = await conn.ExecuteAsync(new CommandDefinition(
      """
      MERGE restaurante.ReporteAgrupadorMapa AS target
      USING (SELECT @Rfc AS Rfc, @Concepto AS ConceptoClave, @Nivel1 AS Nivel1) AS source
        ON target.Rfc = source.Rfc
       AND target.ConceptoClave = source.ConceptoClave
       AND target.Nivel1 = source.Nivel1
      WHEN MATCHED THEN UPDATE SET
        Signo = @Signo, Incluido = @Incluido, Orden = @Orden,
        UpdatedAt = SYSUTCDATETIME(), UpdatedBy = @UserName
      WHEN NOT MATCHED THEN INSERT (Rfc, ConceptoClave, Nivel1, Signo, Incluido, Orden, EsPersonalizado, CreatedBy)
        VALUES (@Rfc, @Concepto, @Nivel1, @Signo, @Incluido, @Orden, 1, @UserName);
      """,
      new
      {
        Rfc = normalizedRfc,
        Concepto = row.ConceptoClave.Trim(),
        Nivel1 = nivel1,
        row.Signo,
        row.Incluido,
        row.Orden,
        UserName = userName
      }, cancellationToken: ct));

    return affected > 0
      ? RestaurantCommandResult.Ok($"El agrupador {nivel1} quedó guardado en «{RestaurantAgrupadorConceptos.Etiqueta(row.ConceptoClave)}».")
      : RestaurantCommandResult.Fail("No se pudo guardar el agrupador.");
  }

  public async Task<RestaurantCommandResult> DeleteAgrupadorMapRowAsync(
    string rfc,
    int id,
    string userName,
    CancellationToken ct = default)
  {
    var normalizedRfc = LogisticsRfc.Require(rfc);
    using var conn = CreateConnection();
    var affected = await conn.ExecuteAsync(new CommandDefinition(
      "DELETE FROM restaurante.ReporteAgrupadorMapa WHERE Rfc=@Rfc AND Id=@Id;",
      new { Rfc = normalizedRfc, Id = id }, cancellationToken: ct));
    return affected == 1
      ? RestaurantCommandResult.Ok("El agrupador fue retirado del reporte.")
      : RestaurantCommandResult.Fail("El agrupador ya no existe en el mapeo.");
  }

  public async Task<RestaurantCommandResult> ResetAgrupadorMapAsync(
    string rfc,
    string userName,
    CancellationToken ct = default)
  {
    var normalizedRfc = LogisticsRfc.Require(rfc);
    using var conn = CreateConnection();
    await conn.ExecuteAsync(new CommandDefinition(
      "DELETE FROM restaurante.ReporteAgrupadorMapa WHERE Rfc=@Rfc;",
      new { Rfc = normalizedRfc }, cancellationToken: ct));
    await SeedMapAsync(conn, normalizedRfc, userName, ct);
    return RestaurantCommandResult.Ok("El mapeo de agrupadores volvió a los valores del Anexo 24.");
  }

  public async Task<IReadOnlyList<RestaurantLedgerNodeDto>> GetLedgerBreakdownAsync(
    RestaurantAnalyticsQuery query,
    string nivel1,
    string? nivel2,
    CancellationToken ct = default)
  {
    var rfc = LogisticsRfc.Require(query.Rfc);
    var (from, toExclusive, _, _, _) = ResolvePeriods(query);

    var sql = string.IsNullOrWhiteSpace(nivel2)
      ? """
        SELECT registro.Nivel1, registro.Nivel2, NULL AS Nivel3,
               ISNULL(MAX(cuenta.Descripcion), MAX(registro.Nombre_Cuenta)) AS Descripcion,
               CAST(SUM(registro.Debe) AS decimal(18,2))  AS Cargos,
               CAST(SUM(registro.Haber) AS decimal(18,2)) AS Abonos,
               COUNT(*) AS Movimientos
        FROM dbo.Registro_Contable registro
        JOIN dbo.Transacciones poliza ON poliza.ID = registro.TransaccionID
        LEFT JOIN dbo.CuentasContables cuenta
          ON cuenta.RFC = poliza.RFC AND cuenta.Nivel1 = registro.Nivel1
         AND cuenta.Nivel2 = registro.Nivel2 AND cuenta.Nivel3 = '00'
        WHERE poliza.RFC = @Rfc AND poliza.Fecha >= @From AND poliza.Fecha < @ToExclusive
          AND registro.Nivel1 = @Nivel1
        GROUP BY registro.Nivel1, registro.Nivel2
        ORDER BY registro.Nivel2;
        """
      : """
        SELECT registro.Nivel1, registro.Nivel2, registro.Nivel3,
               ISNULL(MAX(cuenta.Descripcion), MAX(registro.Nombre_Cuenta)) AS Descripcion,
               CAST(SUM(registro.Debe) AS decimal(18,2))  AS Cargos,
               CAST(SUM(registro.Haber) AS decimal(18,2)) AS Abonos,
               COUNT(*) AS Movimientos
        FROM dbo.Registro_Contable registro
        JOIN dbo.Transacciones poliza ON poliza.ID = registro.TransaccionID
        LEFT JOIN dbo.CuentasContables cuenta
          ON cuenta.RFC = poliza.RFC AND cuenta.Nivel1 = registro.Nivel1
         AND cuenta.Nivel2 = registro.Nivel2 AND cuenta.Nivel3 = registro.Nivel3
        WHERE poliza.RFC = @Rfc AND poliza.Fecha >= @From AND poliza.Fecha < @ToExclusive
          AND registro.Nivel1 = @Nivel1 AND registro.Nivel2 = @Nivel2
        GROUP BY registro.Nivel1, registro.Nivel2, registro.Nivel3
        ORDER BY registro.Nivel3;
        """;

    using var conn = CreateConnection();
    var rows = await conn.QueryAsync<RestaurantLedgerNodeDto>(new CommandDefinition(
      sql, new { Rfc = rfc, From = from, ToExclusive = toExclusive, Nivel1 = nivel1, Nivel2 = nivel2 },
      cancellationToken: ct));
    return rows.AsList();
  }

  public async Task<IReadOnlyList<RestaurantLedgerEntryDto>> GetLedgerEntriesAsync(
    RestaurantAnalyticsQuery query,
    string nivel1,
    string? nivel2,
    string? nivel3,
    CancellationToken ct = default)
  {
    var rfc = LogisticsRfc.Require(query.Rfc);
    var (from, toExclusive, _, _, _) = ResolvePeriods(query);

    const string sql =
      """
      SELECT TOP (500)
             poliza.ID AS TransaccionId,
             poliza.Fecha,
             poliza.Tipo_Poliza AS TipoPoliza,
             poliza.Concepto,
             registro.Nivel1 + '-' + registro.Nivel2 + '-' + registro.Nivel3 AS Cuenta,
             registro.Nombre_Cuenta AS NombreCuenta,
             registro.Referencia,
             CAST(registro.Debe AS decimal(18,2))  AS Debe,
             CAST(registro.Haber AS decimal(18,2)) AS Haber
      FROM dbo.Registro_Contable registro
      JOIN dbo.Transacciones poliza ON poliza.ID = registro.TransaccionID
      WHERE poliza.RFC = @Rfc AND poliza.Fecha >= @From AND poliza.Fecha < @ToExclusive
        AND registro.Nivel1 = @Nivel1
        AND (@Nivel2 IS NULL OR registro.Nivel2 = @Nivel2)
        AND (@Nivel3 IS NULL OR registro.Nivel3 = @Nivel3)
      ORDER BY poliza.Fecha, poliza.ID;
      """;

    using var conn = CreateConnection();
    var rows = await conn.QueryAsync<RestaurantLedgerEntryDto>(new CommandDefinition(
      sql, new { Rfc = rfc, From = from, ToExclusive = toExclusive, Nivel1 = nivel1, Nivel2 = nivel2, Nivel3 = nivel3 },
      cancellationToken: ct));
    return rows.AsList();
  }

  public async Task<IReadOnlyList<RestaurantRecipeCostDto>> GetRecipeCostsAsync(
    RestaurantAnalyticsQuery query,
    CancellationToken ct = default)
  {
    var rfc = LogisticsRfc.Require(query.Rfc);
    var (from, toExclusive, _, _, _) = ResolvePeriods(query);
    using var conn = CreateConnection();
    return await LoadRecipeCostsAsync(conn, rfc, query.SiteId, from.Date, toExclusive.AddDays(-1).Date, ct);
  }

  public async Task<IReadOnlyList<RestaurantAgrupadorDto>> GetAvailableAgrupadoresAsync(
    string rfc,
    CancellationToken ct = default)
  {
    var normalizedRfc = LogisticsRfc.Require(rfc);
    using var conn = CreateConnection();
    var rows = await conn.QueryAsync<AgrupadorCatalogRow>(new CommandDefinition(
      """
      SELECT cuenta.Nivel1, MAX(cuenta.Descripcion) AS Descripcion
      FROM dbo.CuentasContables cuenta
      WHERE cuenta.RFC = @Rfc AND cuenta.Nivel2 = '00' AND cuenta.Nivel3 = '00'
      GROUP BY cuenta.Nivel1
      ORDER BY cuenta.Nivel1;
      """, new { Rfc = normalizedRfc }, cancellationToken: ct));

    return rows.Select(row => new RestaurantAgrupadorDto
    {
      Nivel1 = row.Nivel1,
      Descripcion = row.Descripcion
    }).ToList();
  }

  // ------------------------------------------------------------------
  // Construcción de los modelos
  // ------------------------------------------------------------------

  private static RestaurantAgrupadorMapDto BuildMap(
    IReadOnlyList<MapRow> map,
    IReadOnlyDictionary<string, string> catalogo,
    IReadOnlyDictionary<string, LedgerAggregateRow> periodo)
  {
    var conceptos = map
      .GroupBy(row => row.ConceptoClave, StringComparer.OrdinalIgnoreCase)
      .Select(grupo => new RestaurantAgrupadorConceptoDto
      {
        ConceptoClave = grupo.Key,
        Etiqueta = RestaurantAgrupadorConceptos.Etiqueta(grupo.Key),
        Grupo = RestaurantAgrupadorConceptos.Grupo(grupo.Key),
        Signo = grupo.Min(row => row.Signo),
        Orden = grupo.Min(row => row.Orden),
        Agrupadores = grupo
          .OrderBy(row => row.Nivel1, StringComparer.Ordinal)
          .Select(row => ToMapRowDto(row, catalogo, periodo))
          .ToList()
      })
      .OrderBy(concepto => concepto.Orden)
      .ToList();

    var mapeados = map.Where(row => row.Incluido).Select(row => row.Nivel1).ToHashSet(StringComparer.OrdinalIgnoreCase);
    var fuera = periodo.Values
      .Where(row => !mapeados.Contains(row.Nivel1))
      .OrderByDescending(row => row.Cargos + row.Abonos)
      .Select(row => new RestaurantAgrupadorDto
      {
        Nivel1 = row.Nivel1,
        Descripcion = catalogo.TryGetValue(row.Nivel1, out var descripcion) ? descripcion : "Sin descripción en el catálogo",
        Cargos = row.Cargos,
        Abonos = row.Abonos,
        Movimientos = row.Movimientos
      })
      .ToList();

    return new RestaurantAgrupadorMapDto { Conceptos = conceptos, FueraDelMapeo = fuera };
  }

  private static RestaurantAgrupadorMapRowDto ToMapRowDto(
    MapRow row,
    IReadOnlyDictionary<string, string> catalogo,
    IReadOnlyDictionary<string, LedgerAggregateRow> periodo)
  {
    periodo.TryGetValue(row.Nivel1, out var saldo);
    return new RestaurantAgrupadorMapRowDto
    {
      Id = row.Id,
      ConceptoClave = row.ConceptoClave,
      Nivel1 = row.Nivel1,
      Nivel1Descripcion = catalogo.TryGetValue(row.Nivel1, out var descripcion) ? descripcion : string.Empty,
      Signo = row.Signo,
      Incluido = row.Incluido,
      Orden = row.Orden,
      EsPersonalizado = row.EsPersonalizado,
      Movimientos = saldo?.Movimientos ?? 0,
      Importe = saldo is null ? 0m : Math.Abs(saldo.Cargos - saldo.Abonos)
    };
  }

  private static RestaurantPnlDto BuildPnl(
    IReadOnlyList<MapRow> map,
    IReadOnlyDictionary<string, string> catalogo,
    IReadOnlyDictionary<string, LedgerAggregateRow> periodo,
    IReadOnlyDictionary<string, LedgerAggregateRow> anterior,
    IReadOnlyDictionary<string, LedgerAggregateRow> acumulado,
    DateTime from,
    DateTime to)
  {
    var rows = new List<RestaurantPnlRowDto>();
    var conceptos = map
      .Where(row => row.Incluido && RestaurantAgrupadorConceptos.EsDeResultado(row.ConceptoClave))
      .GroupBy(row => row.ConceptoClave, StringComparer.OrdinalIgnoreCase)
      .OrderBy(grupo => grupo.Min(row => row.Orden));

    foreach (var grupo in conceptos)
    {
      var signo = grupo.Min(row => row.Signo);
      var codigos = grupo.Select(row => row.Nivel1).OrderBy(code => code, StringComparer.Ordinal).ToList();
      rows.Add(new RestaurantPnlRowDto
      {
        ConceptoClave = grupo.Key,
        Etiqueta = RestaurantAgrupadorConceptos.Etiqueta(grupo.Key),
        Signo = signo,
        Orden = grupo.Min(row => row.Orden),
        Agrupadores = codigos,
        Periodo = Natural(codigos, periodo, signo),
        PeriodoAnterior = Natural(codigos, anterior, signo),
        Acumulado = Natural(codigos, acumulado, signo),
        Movimientos = codigos.Sum(code => periodo.TryGetValue(code, out var saldo) ? saldo.Movimientos : 0),
        SinCuentas = codigos.All(code => !catalogo.ContainsKey(code))
      });
    }

    var ingresos = SumConcepts(rows,
      RestaurantAgrupadorConceptos.IngresosVenta,
      RestaurantAgrupadorConceptos.DevolucionesDescuentos,
      RestaurantAgrupadorConceptos.OtrosIngresos);
    var costo = SumConcepts(rows,
      RestaurantAgrupadorConceptos.CostoVenta,
      RestaurantAgrupadorConceptos.Compras,
      RestaurantAgrupadorConceptos.DevolucionesCompras,
      RestaurantAgrupadorConceptos.OtrosCostos);
    var gastos = SumConcepts(rows,
      RestaurantAgrupadorConceptos.GastosGenerales,
      RestaurantAgrupadorConceptos.GastosVenta,
      RestaurantAgrupadorConceptos.GastosAdministracion,
      RestaurantAgrupadorConceptos.GastosFinancieros,
      RestaurantAgrupadorConceptos.ProductosFinancieros,
      RestaurantAgrupadorConceptos.OtrosGastos,
      RestaurantAgrupadorConceptos.OtrosProductos);

    foreach (var row in rows)
    {
      row.PorcentajeSobreVenta = ingresos == 0 ? 0 : Math.Abs(row.Periodo) / Math.Abs(ingresos) * 100m;
    }

    return new RestaurantPnlDto
    {
      From = from,
      To = to,
      Rows = rows,
      Ingresos = ingresos,
      Costo = Math.Abs(costo),
      MargenBruto = ingresos + costo,
      Gastos = Math.Abs(gastos),
      Resultado = ingresos + costo + gastos,
      CargosTotales = decimal.Round(periodo.Values.Sum(row => row.Cargos), 2),
      AbonosTotales = decimal.Round(periodo.Values.Sum(row => row.Abonos), 2)
    };
  }

  /// <summary>Importe con el signo natural del concepto: ingresos en positivo, costos y gastos en negativo.</summary>
  private static decimal Natural(
    IEnumerable<string> codigos,
    IReadOnlyDictionary<string, LedgerAggregateRow> saldos,
    short signo)
  {
    var total = 0m;
    foreach (var codigo in codigos)
    {
      if (!saldos.TryGetValue(codigo, out var saldo)) continue;
      total += signo >= 0 ? saldo.Abonos - saldo.Cargos : saldo.Cargos - saldo.Abonos;
    }

    return decimal.Round(signo >= 0 ? total : -total, 2);
  }

  private static decimal SumConcepts(IEnumerable<RestaurantPnlRowDto> rows, params string[] conceptos)
    => decimal.Round(rows.Where(row => conceptos.Contains(row.ConceptoClave, StringComparer.OrdinalIgnoreCase))
      .Sum(row => row.Periodo), 2);

  private static IReadOnlyList<RestaurantAgrupadorDto> BuildAgrupadores(
    IReadOnlyDictionary<string, string> catalogo,
    IReadOnlyDictionary<string, LedgerAggregateRow> periodo,
    IReadOnlyList<MapRow> map)
  {
    var incluidos = map.Where(row => row.Incluido).Select(row => row.Nivel1).ToHashSet(StringComparer.OrdinalIgnoreCase);
    return periodo.Values
      .OrderByDescending(row => Math.Abs(row.Cargos - row.Abonos))
      .Select(row => new RestaurantAgrupadorDto
      {
        Nivel1 = row.Nivel1,
        Descripcion = catalogo.TryGetValue(row.Nivel1, out var descripcion) ? descripcion : "Sin descripción en el catálogo",
        Cargos = row.Cargos,
        Abonos = row.Abonos,
        Movimientos = row.Movimientos,
        Incluido = incluidos.Contains(row.Nivel1)
      })
      .ToList();
  }

  private static RestaurantReconciliationDto BuildReconciliation(
    IReadOnlyList<MapRow> map,
    IReadOnlyDictionary<string, LedgerAggregateRow> periodo,
    PosSummaryRow pos,
    IReadOnlyList<RestaurantReportBreakdownDto> cobros,
    LinkSummaryRow links,
    CashSummaryRow caja,
    decimal costoRecalculado)
  {
    decimal Abono(string concepto)
    {
      var codigos = ConceptCodes(map, concepto);
      return decimal.Round(codigos.Sum(code => periodo.TryGetValue(code, out var saldo) ? saldo.Abonos - saldo.Cargos : 0m), 2);
    }

    decimal Cargo(string concepto)
    {
      var codigos = ConceptCodes(map, concepto);
      return decimal.Round(codigos.Sum(code => periodo.TryGetValue(code, out var saldo) ? saldo.Cargos - saldo.Abonos : 0m), 2);
    }

    bool SinMovimiento(string concepto)
      => ConceptCodes(map, concepto).All(code => !periodo.ContainsKey(code));

    decimal Cobro(string metodo)
      => cobros.FirstOrDefault(row => string.Equals(row.Label, metodo, StringComparison.OrdinalIgnoreCase))?.Amount ?? 0m;

    var rows = new List<RestaurantReconciliationRowDto>
    {
      new()
      {
        Concepto = "Venta antes de impuesto",
        Detalle = "Total cobrado menos IVA en órdenes pagadas del periodo.",
        Operacion = pos.VentaNeta,
        Contabilidad = Abono(RestaurantAgrupadorConceptos.IngresosVenta),
        Agrupadores = ConceptCodes(map, RestaurantAgrupadorConceptos.IngresosVenta),
        AgrupadoresSinMovimiento = SinMovimiento(RestaurantAgrupadorConceptos.IngresosVenta)
      },
      new()
      {
        Concepto = "IVA trasladado cobrado",
        Detalle = "Impuesto calculado por el punto de venta.",
        Operacion = pos.Iva,
        Contabilidad = Abono(RestaurantAgrupadorConceptos.IvaTrasladado),
        Agrupadores = ConceptCodes(map, RestaurantAgrupadorConceptos.IvaTrasladado),
        AgrupadoresSinMovimiento = SinMovimiento(RestaurantAgrupadorConceptos.IvaTrasladado)
      },
      new()
      {
        Concepto = "Descuentos y promociones",
        Detalle = "Descuentos aplicados en la orden.",
        Operacion = pos.Descuentos,
        Contabilidad = Cargo(RestaurantAgrupadorConceptos.DevolucionesDescuentos),
        Agrupadores = ConceptCodes(map, RestaurantAgrupadorConceptos.DevolucionesDescuentos),
        AgrupadoresSinMovimiento = SinMovimiento(RestaurantAgrupadorConceptos.DevolucionesDescuentos)
      },
      new()
      {
        Concepto = "Costo de lo vendido",
        Detalle = "Costo recalculado desde la receta activa contra el costo registrado.",
        Operacion = costoRecalculado,
        Contabilidad = Cargo(RestaurantAgrupadorConceptos.CostoVenta),
        Agrupadores = ConceptCodes(map, RestaurantAgrupadorConceptos.CostoVenta),
        AgrupadoresSinMovimiento = SinMovimiento(RestaurantAgrupadorConceptos.CostoVenta)
      },
      new()
      {
        Concepto = "Cobros en efectivo",
        Detalle = "La cuenta de caja también recibe aportaciones y otros movimientos, así que la diferencia no siempre es un error.",
        Operacion = Cobro("Cash"),
        Contabilidad = Cargo(RestaurantAgrupadorConceptos.Caja),
        Agrupadores = ConceptCodes(map, RestaurantAgrupadorConceptos.Caja),
        AgrupadoresSinMovimiento = SinMovimiento(RestaurantAgrupadorConceptos.Caja),
        NoComparable = true
      },
      new()
      {
        Concepto = "Cobros por transferencia y tarjeta",
        Detalle = "Depósitos que deberían llegar a la cuenta bancaria.",
        Operacion = Cobro("Transfer") + Cobro("ExternalCard"),
        Contabilidad = Cargo(RestaurantAgrupadorConceptos.Bancos),
        Agrupadores = ConceptCodes(map, RestaurantAgrupadorConceptos.Bancos),
        AgrupadoresSinMovimiento = SinMovimiento(RestaurantAgrupadorConceptos.Bancos)
      }
    };

    return new RestaurantReconciliationDto
    {
      Rows = rows,
      OrdenesPagadas = pos.OrdenesPagadas,
      OrdenesLigadas = links.OrdenesLigadas,
      DiasConVenta = pos.DiasConVenta,
      DiasConPoliza = links.DiasConPoliza,
      DiferenciaCajaNeta = caja.DiferenciaNeta,
      DiferenciaCajaAbsoluta = caja.DiferenciaAbsoluta,
      TurnosConDiferencia = caja.ConDiferencia,
      TurnosSinAprobar = caja.SinAprobar
    };
  }

  private static IReadOnlyList<string> ConceptCodes(IReadOnlyList<MapRow> map, string concepto)
    => map.Where(row => row.Incluido && string.Equals(row.ConceptoClave, concepto, StringComparison.OrdinalIgnoreCase))
      .Select(row => row.Nivel1)
      .OrderBy(code => code, StringComparer.Ordinal)
      .ToList();

  // ------------------------------------------------------------------
  // Acceso a datos auxiliar
  // ------------------------------------------------------------------

  private static async Task<IReadOnlyList<RestaurantRecipeCostDto>> LoadRecipeCostsAsync(
    DbConnection conn,
    string rfc,
    int siteId,
    DateTime from,
    DateTime to,
    CancellationToken ct)
  {
    var sql = RestaurantAnalyticsSql.RecipeCostCte +
      """

      SELECT product.Id AS ProductId,
             card.[Name] + CASE WHEN product.VariantName IS NULL THEN '' ELSE ' · ' + product.VariantName END AS Producto,
             CAST(ISNULL(SUM(line.Quantity), 0) AS decimal(18,2))  AS UnidadesVendidas,
             CAST(ISNULL(SUM(line.LineTotal), 0) AS decimal(18,2)) AS Venta,
             CAST(product.Price AS decimal(18,2)) AS PrecioLista,
             CAST(ISNULL(MAX(receta.CostoCongelado), 0) AS decimal(18,6))    AS CostoCongelado,
             CAST(ISNULL(MAX(COALESCE(receta.CostoRecalculado, reventa.Costo)), 0) AS decimal(18,6)) AS CostoRecalculado,
             CAST(ISNULL(MAX(receta.YieldQuantity), 0) AS decimal(18,4))     AS RendimientoReceta,
             MAX(receta.UnidadRendimiento) AS UnidadRendimiento,
             ISNULL(MAX(receta.ComponentesSinConversion), 0) AS ComponentesSinConversion,
             CAST(CASE WHEN MAX(receta.ProductMaterialId) IS NULL THEN 0 ELSE 1 END AS bit) AS TieneReceta,
             CASE
               WHEN MAX(receta.ProductMaterialId) IS NOT NULL THEN 'Receta'
               WHEN MAX(reventa.Costo) IS NOT NULL THEN 'Compra'
               ELSE 'Sin costo'
             END AS CostoOrigen
      FROM restaurante.Product product
      JOIN restaurante.ProductCard card ON card.Rfc = product.Rfc AND card.Id = product.ProductCardId
      JOIN logistica.Material productMaterial
        ON productMaterial.Rfc = product.Rfc AND productMaterial.Id = product.MaterialId
      LEFT JOIN RecetaCosto receta ON receta.ProductMaterialId = product.MaterialId
      OUTER APPLY
      (
        /* Los productos de reventa no tienen receta: su costo es el precio de compra
           del material. Se excluye la preparación a pedido porque ahí el precio
           unitario suele traer capturado el precio de venta. */
        SELECT productMaterial.BaseUnitPrice AS Costo
        WHERE productMaterial.FulfillmentMode = 'StockItem' AND productMaterial.BaseUnitPrice > 0
      ) reventa
      LEFT JOIN restaurante.OrderLine line ON line.Rfc = product.Rfc AND line.ProductId = product.Id
        AND EXISTS
        (
          SELECT 1 FROM restaurante.[Order] orderInfo
          WHERE orderInfo.Rfc = line.Rfc AND orderInfo.Id = line.OrderId
            AND orderInfo.SiteId = @SiteId
            AND orderInfo.OperationalDate >= @From AND orderInfo.OperationalDate <= @To
            AND orderInfo.PaymentStatus = 'Paid' AND orderInfo.[Status] <> 'Cancelled'
        )
      WHERE product.Rfc = @Rfc
      GROUP BY product.Id, card.[Name], product.VariantName, product.Price
      HAVING ISNULL(SUM(line.Quantity), 0) > 0 OR MAX(receta.ProductMaterialId) IS NOT NULL
      ORDER BY Venta DESC;
      """;

    var rows = await conn.QueryAsync<RestaurantRecipeCostDto>(new CommandDefinition(
      sql, new { Rfc = rfc, SiteId = siteId, From = from, To = to }, cancellationToken: ct));
    return rows.AsList();
  }

  private static async Task<Dictionary<DateTime, decimal>> LoadDailyCostAsync(
    DbConnection conn,
    string rfc,
    int siteId,
    DateTime from,
    DateTime to,
    CancellationToken ct)
  {
    var sql = RestaurantAnalyticsSql.RecipeCostCte +
      """

      SELECT orderInfo.OperationalDate AS Fecha,
             CAST(ISNULL(SUM(line.Quantity * ISNULL(COALESCE(receta.CostoRecalculado, reventa.Costo), 0)), 0) AS decimal(18,2)) AS Costo
      FROM restaurante.[Order] orderInfo
      JOIN restaurante.OrderLine line ON line.Rfc = orderInfo.Rfc AND line.OrderId = orderInfo.Id
      LEFT JOIN restaurante.Product product ON product.Rfc = line.Rfc AND product.Id = line.ProductId
      LEFT JOIN logistica.Material productMaterial
        ON productMaterial.Rfc = product.Rfc AND productMaterial.Id = product.MaterialId
      LEFT JOIN RecetaCosto receta ON receta.ProductMaterialId = product.MaterialId
      OUTER APPLY
      (
        SELECT productMaterial.BaseUnitPrice AS Costo
        WHERE productMaterial.FulfillmentMode = 'StockItem' AND productMaterial.BaseUnitPrice > 0
      ) reventa
      WHERE orderInfo.Rfc = @Rfc AND orderInfo.SiteId = @SiteId
        AND orderInfo.OperationalDate >= @From AND orderInfo.OperationalDate <= @To
        AND orderInfo.PaymentStatus = 'Paid' AND orderInfo.[Status] <> 'Cancelled'
      GROUP BY orderInfo.OperationalDate;
      """;

    var rows = await conn.QueryAsync<DailyCostRow>(new CommandDefinition(
      sql, new { Rfc = rfc, SiteId = siteId, From = from, To = to }, cancellationToken: ct));
    return rows.ToDictionary(row => row.Fecha.Date, row => row.Costo);
  }

  private static async Task<IReadOnlyList<MapRow>> LoadMapRowsAsync(DbConnection conn, string rfc, CancellationToken ct)
  {
    var rows = await conn.QueryAsync<MapRow>(new CommandDefinition(
      """
      SELECT Id, ConceptoClave, Nivel1, Signo, Incluido, Orden, EsPersonalizado
      FROM restaurante.ReporteAgrupadorMapa
      WHERE Rfc = @Rfc
      ORDER BY Orden, Nivel1;
      """, new { Rfc = rfc }, cancellationToken: ct));
    return rows.AsList();
  }

  private static async Task EnsureMapSeedAsync(DbConnection conn, string rfc, CancellationToken ct)
  {
    var total = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
      "SELECT COUNT(*) FROM restaurante.ReporteAgrupadorMapa WHERE Rfc=@Rfc;",
      new { Rfc = rfc }, cancellationToken: ct));
    if (total > 0) return;
    await SeedMapAsync(conn, rfc, "sistema", ct);
  }

  private static Task SeedMapAsync(DbConnection conn, string rfc, string userName, CancellationToken ct)
    => conn.ExecuteAsync(new CommandDefinition(
      """
      INSERT INTO restaurante.ReporteAgrupadorMapa (Rfc, ConceptoClave, Nivel1, Signo, Orden, CreatedBy)
      SELECT @Rfc, @ConceptoClave, @Nivel1, @Signo, @Orden, @UserName
      WHERE NOT EXISTS
      (
        SELECT 1 FROM restaurante.ReporteAgrupadorMapa existente
        WHERE existente.Rfc = @Rfc AND existente.ConceptoClave = @ConceptoClave AND existente.Nivel1 = @Nivel1
      );
      """,
      RestaurantAgrupadorConceptos.Semilla.Select(seed => new
      {
        Rfc = rfc,
        seed.ConceptoClave,
        seed.Nivel1,
        seed.Signo,
        seed.Orden,
        UserName = userName
      }).ToArray(), cancellationToken: ct));

  private static (DateTime From, DateTime ToExclusive, DateTime PrevFrom, DateTime PrevToExclusive, DateTime YtdFrom)
    ResolvePeriods(RestaurantAnalyticsQuery query)
  {
    var from = query.From.Date;
    var to = query.To.Date;
    if (to < from) (from, to) = (to, from);
    var toExclusive = to.AddDays(1);
    var length = (to - from).Days + 1;
    var prevToExclusive = from;
    var prevFrom = from.AddDays(-length);
    var ytdFrom = new DateTime(to.Year, 1, 1);
    return (from, toExclusive, prevFrom, prevToExclusive, ytdFrom);
  }

  private DbConnection CreateConnection()
    => _connectionFactory.Create() as DbConnection
      ?? throw new InvalidOperationException("La fábrica de conexiones no devolvió una DbConnection.");

  // ------------------------------------------------------------------
  // Filas crudas
  // ------------------------------------------------------------------

  private sealed class LedgerAggregateRow
  {
    public string Nivel1 { get; set; } = string.Empty;
    public decimal Cargos { get; set; }
    public decimal Abonos { get; set; }
    public int Movimientos { get; set; }
  }

  private sealed class AgrupadorCatalogRow
  {
    public string Nivel1 { get; set; } = string.Empty;
    public string Descripcion { get; set; } = string.Empty;
  }

  private sealed class MapRow
  {
    public int Id { get; set; }
    public string ConceptoClave { get; set; } = string.Empty;
    public string Nivel1 { get; set; } = string.Empty;
    public short Signo { get; set; }
    public bool Incluido { get; set; }
    public int Orden { get; set; }
    public bool EsPersonalizado { get; set; }
  }

  private sealed class PosSummaryRow
  {
    public int OrdenesPagadas { get; set; }
    public decimal VentaNeta { get; set; }
    public decimal Iva { get; set; }
    public decimal Descuentos { get; set; }
    public decimal Propinas { get; set; }
    public decimal Total { get; set; }
    public int DiasConVenta { get; set; }
  }

  private sealed class LinkSummaryRow
  {
    public int OrdenesLigadas { get; set; }
    public int DiasConPoliza { get; set; }
  }

  private sealed class CashSummaryRow
  {
    public int Turnos { get; set; }
    public decimal DiferenciaNeta { get; set; }
    public decimal DiferenciaAbsoluta { get; set; }
    public int ConDiferencia { get; set; }
    public int SinAprobar { get; set; }
  }

  private sealed class DailyPosRow
  {
    public DateTime Fecha { get; set; }
    public int Ordenes { get; set; }
    public decimal VentaPos { get; set; }
    public decimal IvaPos { get; set; }
    public bool TienePolizaLigada { get; set; }
  }

  private sealed class DailyLedgerRow
  {
    public DateTime Fecha { get; set; }
    public decimal IngresoContable { get; set; }
  }

  private sealed class DailyCostRow
  {
    public DateTime Fecha { get; set; }
    public decimal Costo { get; set; }
  }
}
