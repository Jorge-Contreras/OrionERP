using System.Data.Common;
using Dapper;
using OrionERP.Application.Common;
using OrionERP.Application.Features.Contabilidad.ContabilidadRegistros;
using OrionERP.Application.Features.Logistica.Shared;
using OrionERP.Application.Features.Restaurante;

namespace OrionERP.Infrastructure.Features.Restaurante;

/// <summary>
/// Motor de reglas del diagnóstico contable-fiscal del Restaurante. Cada regla
/// mide una condición concreta sobre la contabilidad, la operación del punto de
/// venta o el inventario, y devuelve el monto expuesto y los códigos
/// agrupadores afectados.
/// </summary>
public sealed partial class RestaurantDiagnosticsService : IRestaurantDiagnosticsService
{
  /// <summary>Cantidad a partir de la cual un conteo físico deja de ser creíble.</summary>
  private const decimal ConteoAtipicoUmbral = 1_000_000m;

  private readonly IDbConnectionFactory _connectionFactory;
  private readonly IRestaurantAccountingService _accountingService;
  private readonly ICuentasContablesRepository _accountsRepository;

  public RestaurantDiagnosticsService(
    IDbConnectionFactory connectionFactory,
    IRestaurantAccountingService accountingService,
    ICuentasContablesRepository accountsRepository)
  {
    _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    _accountingService = accountingService ?? throw new ArgumentNullException(nameof(accountingService));
    _accountsRepository = accountsRepository ?? throw new ArgumentNullException(nameof(accountsRepository));
  }

  public async Task<RestaurantDiagnosticRunDto> RunAsync(
    RestaurantAnalyticsQuery query,
    string userName,
    CancellationToken ct = default)
  {
    var rfc = LogisticsRfc.Require(query.Rfc);
    var from = query.From.Date;
    var to = query.To.Date;
    if (to < from) (from, to) = (to, from);
    var toExclusive = to.AddDays(1);

    using var conn = CreateConnection();
    var facts = await LoadFactsAsync(conn, rfc, query.SiteId, from, to, toExclusive, ct);
    var missing = await GetMissingAccountsAsync(rfc, ct);

    var findings = new List<RestaurantDiagnosticFindingDto>();
    AddConfigurationFindings(findings, facts, missing);
    AddLedgerFindings(findings, facts);
    AddOperationFindings(findings, facts);
    AddCostFindings(findings, facts);

    var ordered = findings
      .OrderBy(finding => RestaurantDiagnosticSeverities.Rank(finding.Severidad))
      .ThenByDescending(finding => finding.MontoExpuesto)
      .ThenBy(finding => finding.ReglaClave, StringComparer.Ordinal)
      .ToList();

    var run = await PersistRunAsync(conn, rfc, query.SiteId, from, to, userName, ordered, ct);
    return run;
  }

  public async Task<RestaurantDiagnosticRunDto?> GetLatestRunAsync(
    string rfc,
    int siteId,
    CancellationToken ct = default)
  {
    var runs = await GetHistoryAsync(rfc, siteId, 1, ct);
    return runs.Count == 0 ? null : runs[0];
  }

  public async Task<IReadOnlyList<RestaurantDiagnosticRunDto>> GetHistoryAsync(
    string rfc,
    int siteId,
    int take = 12,
    CancellationToken ct = default)
  {
    var normalizedRfc = LogisticsRfc.Require(rfc);
    using var conn = CreateConnection();

    const string sql =
      """
      SELECT TOP (@Take) Id, SiteId, PeriodoInicio, PeriodoFin, EjecutadoEn, EjecutadoPor,
             HallazgosTotal, Criticos, MontoExpuesto
      FROM restaurante.DiagnosticoCorrida
      WHERE Rfc = @Rfc AND SiteId = @SiteId
      ORDER BY EjecutadoEn DESC, Id DESC;
      """;

    var runs = (await conn.QueryAsync<RestaurantDiagnosticRunDto>(new CommandDefinition(
      sql, new { Rfc = normalizedRfc, SiteId = siteId, Take = Math.Clamp(take, 1, 60) },
      cancellationToken: ct))).AsList();
    if (runs.Count == 0) return runs;

    var ids = runs.Select(run => run.Id).ToArray();
    var findings = (await conn.QueryAsync<RestaurantDiagnosticFindingDto>(new CommandDefinition(
      """
      SELECT Id, CorridaId, ReglaClave, Severidad, Titulo, Detalle, Agrupadores,
             MontoExpuesto, Conteo, AccionSugerida, Estado, Justificacion, ResueltoEn, ResueltoPor
      FROM restaurante.DiagnosticoHallazgo
      WHERE Rfc = @Rfc AND CorridaId IN @Ids;
      """, new { Rfc = normalizedRfc, Ids = ids }, cancellationToken: ct))).AsList();

    foreach (var run in runs)
    {
      run.Findings = findings
        .Where(finding => finding.CorridaId == run.Id)
        .OrderBy(finding => RestaurantDiagnosticSeverities.Rank(finding.Severidad))
        .ThenByDescending(finding => finding.MontoExpuesto)
        .ThenBy(finding => finding.ReglaClave, StringComparer.Ordinal)
        .ToList();
    }

    return runs;
  }

  public async Task<RestaurantCommandResult> AcceptFindingAsync(
    string rfc,
    long findingId,
    string justificacion,
    string userName,
    CancellationToken ct = default)
  {
    var normalizedRfc = LogisticsRfc.Require(rfc);
    if (string.IsNullOrWhiteSpace(justificacion) || justificacion.Trim().Length < 15)
      return RestaurantCommandResult.Fail("Escribe una justificación de al menos 15 caracteres para aceptar el hallazgo.");

    using var conn = CreateConnection();
    var affected = await conn.ExecuteAsync(new CommandDefinition(
      """
      UPDATE restaurante.DiagnosticoHallazgo
      SET Estado = 'Aceptado',
          Justificacion = @Justificacion,
          ResueltoEn = SYSUTCDATETIME(),
          ResueltoPor = @UserName
      WHERE Rfc = @Rfc AND Id = @Id AND Estado = 'Abierto';
      """,
      new
      {
        Rfc = normalizedRfc,
        Id = findingId,
        Justificacion = justificacion.Trim(),
        UserName = userName
      }, cancellationToken: ct));

    return affected == 1
      ? RestaurantCommandResult.Ok("El hallazgo quedó aceptado con su justificación.")
      : RestaurantCommandResult.Fail("El hallazgo ya no está abierto o no pertenece a este RFC.");
  }

  public async Task<IReadOnlyList<RestaurantMissingAccountDto>> GetMissingAccountsAsync(
    string rfc,
    CancellationToken ct = default)
  {
    var normalizedRfc = LogisticsRfc.Require(rfc);
    using var conn = CreateConnection();

    const string sql =
      """
      SELECT cuenta.Nivel1, cuenta.Nivel2, cuenta.Nivel3, cuenta.Descripcion
      FROM dbo.CuentasContables cuenta
      WHERE cuenta.RFC = @Rfc AND (cuenta.Nivel3 = '00' OR cuenta.Nivel2 = '00');
      """;

    var catalogo = (await conn.QueryAsync<CatalogRow>(new CommandDefinition(
      sql, new { Rfc = normalizedRfc }, cancellationToken: ct))).AsList();

    var detalle = (await conn.QueryAsync<CatalogRow>(new CommandDefinition(
      """
      SELECT cuenta.Nivel1, cuenta.Nivel2, cuenta.Nivel3, cuenta.Descripcion
      FROM dbo.CuentasContables cuenta
      WHERE cuenta.RFC = @Rfc AND cuenta.Nivel3 <> '00';
      """, new { Rfc = normalizedRfc }, cancellationToken: ct))).AsList();

    var nivel1 = catalogo
      .Where(row => row.Nivel2 == "00" && row.Nivel3 == "00")
      .GroupBy(row => row.Nivel1, StringComparer.Ordinal)
      .ToDictionary(grupo => grupo.Key, grupo => grupo.First().Descripcion, StringComparer.Ordinal);
    var nivel2 = catalogo
      .Where(row => row.Nivel3 == "00" && row.Nivel2 != "00")
      .GroupBy(row => row.Nivel1 + "-" + row.Nivel2, StringComparer.Ordinal)
      .ToDictionary(grupo => grupo.Key, grupo => grupo.First().Descripcion, StringComparer.Ordinal);
    var existentes = detalle
      .Select(row => row.Nivel1 + "-" + row.Nivel2)
      .ToHashSet(StringComparer.Ordinal);

    return RestaurantRequiredAccounts.Catalogo
      .Where(definicion => !existentes.Contains(definicion.Nivel1 + "-" + definicion.Nivel2))
      .Select(definicion => new RestaurantMissingAccountDto
      {
        Nivel1 = definicion.Nivel1,
        Nivel2 = definicion.Nivel2,
        Nivel1Descripcion = nivel1.TryGetValue(definicion.Nivel1, out var d1) ? d1 : string.Empty,
        Nivel2Descripcion = nivel2.TryGetValue(definicion.Nivel1 + "-" + definicion.Nivel2, out var d2) ? d2 : string.Empty,
        DescripcionSugerida = definicion.DescripcionSugerida,
        Uso = definicion.Uso,
        CampoConfiguracion = definicion.CampoConfiguracion,
        EncabezadoDisponible = nivel2.ContainsKey(definicion.Nivel1 + "-" + definicion.Nivel2)
      })
      .ToList();
  }

  public async Task<RestaurantCommandResult> CreateMissingAccountsAsync(
    string rfc,
    IReadOnlyList<RestaurantMissingAccountDto> accounts,
    string userName,
    CancellationToken ct = default)
  {
    var normalizedRfc = LogisticsRfc.Require(rfc);
    var seleccionadas = accounts.Where(account => account.Seleccionada && account.EncabezadoDisponible).ToList();
    if (seleccionadas.Count == 0)
      return RestaurantCommandResult.Fail("No hay cuentas seleccionadas cuyo encabezado exista en el catálogo.");

    var creadas = new List<string>();
    foreach (var account in seleccionadas)
    {
      ct.ThrowIfCancellationRequested();
      var siguiente = await NextNivel3Async(normalizedRfc, account.Nivel1, account.Nivel2, ct);
      var descripcion = string.IsNullOrWhiteSpace(account.DescripcionSugerida)
        ? account.Nivel2Descripcion
        : account.DescripcionSugerida.Trim();
      await _accountsRepository.CreateNivel3Async(normalizedRfc, account.Nivel1, account.Nivel2, siguiente, descripcion);
      creadas.Add($"{account.Nivel1}-{account.Nivel2}-{siguiente} {descripcion}");
    }

    return RestaurantCommandResult.Ok(
      $"Se dieron de alta {creadas.Count} cuenta(s) de detalle: {string.Join(" · ", creadas)}.");
  }

  public async Task<RestaurantPolicyBackfillResultDto> BackfillDailyPoliciesAsync(
    RestaurantAnalyticsQuery query,
    string userName,
    CancellationToken ct = default)
  {
    var rfc = LogisticsRfc.Require(query.Rfc);
    var from = query.From.Date;
    var to = query.To.Date;
    if (to < from) (from, to) = (to, from);
    if ((to - from).Days > 120)
      return new RestaurantPolicyBackfillResultDto
      {
        Success = false,
        Message = "El rango no puede exceder 120 días para generar pólizas en bloque."
      };

    using var conn = CreateConnection();
    var pendientes = (await conn.QueryAsync<PendingDayRow>(new CommandDefinition(
      """
      SELECT orderInfo.OperationalDate AS Fecha,
             COUNT(*) AS Ordenes,
             CAST(SUM(orderInfo.Total) AS decimal(18,2)) AS Importe
      FROM restaurante.[Order] orderInfo
      WHERE orderInfo.Rfc = @Rfc AND orderInfo.SiteId = @SiteId
        AND orderInfo.OperationalDate >= @From AND orderInfo.OperationalDate <= @To
        AND orderInfo.PaymentStatus = 'Paid' AND orderInfo.[Status] <> 'Cancelled'
        AND NOT EXISTS
        (
          SELECT 1 FROM restaurante.AccountingOrderLink linkInfo
          WHERE linkInfo.Rfc = orderInfo.Rfc AND linkInfo.OrderId = orderInfo.Id
        )
      GROUP BY orderInfo.OperationalDate
      ORDER BY orderInfo.OperationalDate;
      """, new { Rfc = rfc, query.SiteId, From = from, To = to }, cancellationToken: ct))).AsList();

    if (pendientes.Count == 0)
      return new RestaurantPolicyBackfillResultDto
      {
        Success = true,
        Message = "Todas las órdenes pagadas del rango ya están ligadas a una póliza."
      };

    var days = new List<RestaurantPolicyBackfillDayDto>();
    foreach (var pendiente in pendientes)
    {
      ct.ThrowIfCancellationRequested();
      var result = await _accountingService.GenerateDailyPolicyAsync(rfc, query.SiteId, pendiente.Fecha, userName, ct);
      days.Add(new RestaurantPolicyBackfillDayDto
      {
        Fecha = pendiente.Fecha,
        Generada = result.Success,
        Mensaje = result.Message,
        Ordenes = pendiente.Ordenes,
        Importe = pendiente.Importe
      });
    }

    var generadas = days.Count(day => day.Generada);
    return new RestaurantPolicyBackfillResultDto
    {
      Success = generadas > 0,
      Days = days,
      Message = generadas == days.Count
        ? $"Se generaron {generadas} póliza(s) diaria(s)."
        : $"Se generaron {generadas} de {days.Count} póliza(s). Las fechas rechazadas necesitan conciliarse antes de contabilizarse."
    };
  }

  // ------------------------------------------------------------------
  // Reglas
  // ------------------------------------------------------------------

  private static void AddConfigurationFindings(
    List<RestaurantDiagnosticFindingDto> findings,
    DiagnosticFacts facts,
    IReadOnlyList<RestaurantMissingAccountDto> missing)
  {
    // R01 · La contabilidad automática del punto de venta no está configurada.
    var faltantes = facts.Configuracion is null
      ? RestaurantAccountingFields.Requeridos.ToList()
      : RestaurantAccountingFields.Faltantes(facts.Configuracion).ToList();
    if (faltantes.Count > 0 || facts.Configuracion?.DailyPolicyEnabled != true)
    {
      findings.Add(new RestaurantDiagnosticFindingDto
      {
        ReglaClave = "R01",
        Severidad = RestaurantDiagnosticSeverities.Critica,
        Titulo = "La contabilidad automática del punto de venta no está configurada",
        Detalle = facts.Configuracion is null
          ? "La sede no tiene un renglón en restaurante.AccountingConfiguration, así que la póliza diaria no puede generarse."
          : $"Faltan {faltantes.Count} cuenta(s) por asignar ({string.Join(", ", faltantes)}) y la generación diaria está {(facts.Configuracion.DailyPolicyEnabled ? "activa" : "apagada")}.",
        Agrupadores = "101,102,401,208,402,115,501",
        MontoExpuesto = facts.CobrosPos,
        Conteo = faltantes.Count,
        AccionSugerida = "Asigna las cuentas y enciende la póliza diaria en Configuración operativa."
      });
    }

    // R04 · Cuentas de detalle que el restaurante necesita y no existen.
    if (missing.Count > 0)
    {
      findings.Add(new RestaurantDiagnosticFindingDto
      {
        ReglaClave = "R04",
        Severidad = RestaurantDiagnosticSeverities.Alta,
        Titulo = "Faltan cuentas de detalle en el catálogo del RFC",
        Detalle = "No existen las cuentas nivel 3 de: " +
                  string.Join(", ", missing.Select(account => $"{account.Nivel1}-{account.Nivel2} {account.DescripcionSugerida}")) + ".",
        Agrupadores = string.Join(",", missing.Select(account => account.Nivel1).Distinct()),
        Conteo = missing.Count,
        AccionSugerida = "Dalas de alta desde el diagnóstico; los encabezados nivel 2 del Anexo 24 ya existen."
      });
    }

    // R21 · El RFC no tiene perfil fiscal ante el SAT.
    if (!facts.TienePerfilSat)
    {
      findings.Add(new RestaurantDiagnosticFindingDto
      {
        ReglaClave = "R21",
        Severidad = RestaurantDiagnosticSeverities.Critica,
        Titulo = "El RFC no tiene perfil fiscal registrado",
        Detalle = "No hay renglón en dbo.SatRfcProfile: sin certificado de sello no puede emitirse CFDI ni presentarse contabilidad electrónica. " +
                  "Mientras siga así, estos libros sirven para administrar el negocio pero no son declarables.",
        Agrupadores = "401,208",
        AccionSugerida = "Define bajo qué persona moral o física opera el restaurante y carga su perfil fiscal."
      });
    }
  }

  private static void AddLedgerFindings(List<RestaurantDiagnosticFindingDto> findings, DiagnosticFacts facts)
  {
    // R03 · El IVA cobrado no se separó del ingreso.
    if (facts.IvaPos > 0.01m && facts.IvaTrasladadoContable <= 0.01m)
    {
      findings.Add(new RestaurantDiagnosticFindingDto
      {
        ReglaClave = "R03",
        Severidad = RestaurantDiagnosticSeverities.Critica,
        Titulo = "El IVA cobrado no se separó del ingreso",
        Detalle = $"El punto de venta trasladó {facts.IvaPos:C} de IVA en el periodo y no hay un solo movimiento en los agrupadores 208 y 209. " +
                  "El ingreso queda sobrestimado por ese importe y no hay base para declarar.",
        Agrupadores = "208,209,213,401",
        MontoExpuesto = facts.IvaPos,
        AccionSugerida = "Da de alta 208-01 y 213-01, reclasifica el IVA del periodo y deja que la póliza diaria lo separe en adelante."
      });
    }

    // R05 · Movimientos contra cuentas de encabezado.
    if (facts.MovimientosEncabezado > 0)
    {
      findings.Add(new RestaurantDiagnosticFindingDto
      {
        ReglaClave = "R05",
        Severidad = RestaurantDiagnosticSeverities.Media,
        Titulo = "Movimientos registrados contra cuentas de encabezado",
        Detalle = $"{facts.MovimientosEncabezado} asiento(s) por {facts.ImporteEncabezado:C} se registraron contra cuentas con nivel 2 igual a 00, " +
                  $"en los agrupadores {facts.AgrupadoresEncabezado}. Una cuenta de encabezado no debe recibir movimientos y en contabilidad " +
                  "electrónica impide clasificar la naturaleza de la operación.",
        Agrupadores = facts.AgrupadoresEncabezado,
        MontoExpuesto = facts.ImporteEncabezado,
        Conteo = facts.MovimientosEncabezado,
        AccionSugerida = "Abre la cuenta de detalle bajo el nivel 2 que corresponda y reclasifica los movimientos."
      });
    }

    // R06 · Ingresos en la cuenta genérica en vez de la subcuenta gravada.
    if (facts.IngresoEnEncabezado > 0.01m)
    {
      findings.Add(new RestaurantDiagnosticFindingDto
      {
        ReglaClave = "R06",
        Severidad = RestaurantDiagnosticSeverities.Media,
        Titulo = "Las ventas se registran en la cuenta genérica de ingresos",
        Detalle = $"{facts.IngresoEnEncabezado:C} de ingreso están en 401-00. Para un restaurante que cobra de contado corresponde 401-02, " +
                  "ventas gravadas a la tasa general de contado, para que el reporte distinga lo gravado, lo del 0% y lo exento.",
        Agrupadores = "401",
        MontoExpuesto = facts.IngresoEnEncabezado,
        AccionSugerida = "Crea 401-02-01 y apúntala como cuenta de ventas del punto de venta."
      });
    }

    // R07 · Cuentas de detalle duplicadas por descripción.
    if (facts.CuentasDuplicadas > 0)
    {
      findings.Add(new RestaurantDiagnosticFindingDto
      {
        ReglaClave = "R07",
        Severidad = RestaurantDiagnosticSeverities.Media,
        Titulo = "Cuentas de detalle duplicadas en el catálogo",
        Detalle = $"{facts.CuentasDuplicadas} descripción(es) se repiten en más de una cuenta de detalle: {facts.DetalleDuplicadas}. " +
                  "El mismo concepto se parte entre cuentas y ningún reporte lo suma completo.",
        Conteo = facts.CuentasDuplicadas,
        AccionSugerida = "Consolida en una sola cuenta por concepto y reclasifica los movimientos."
      });
    }

    // R08 · Pólizas capturadas sin asientos.
    if (facts.PolizasSinAsientos > 0)
    {
      findings.Add(new RestaurantDiagnosticFindingDto
      {
        ReglaClave = "R08",
        Severidad = RestaurantDiagnosticSeverities.Media,
        Titulo = "Pólizas capturadas sin asientos",
        Detalle = $"{facts.PolizasSinAsientos} transacción(es) por {facts.ImportePolizasSinAsientos:C} existen en el encabezado pero no tienen " +
                  "renglones en Registro_Contable. Aparecen en listados pero no afectan saldos.",
        MontoExpuesto = facts.ImportePolizasSinAsientos,
        Conteo = facts.PolizasSinAsientos,
        AccionSugerida = "Completa el asiento o cancela la póliza."
      });
    }

    // R09 · El encabezado de la póliza no coincide con su asiento.
    if (facts.PolizasDescuadradasMonto > 0)
    {
      findings.Add(new RestaurantDiagnosticFindingDto
      {
        ReglaClave = "R09",
        Severidad = RestaurantDiagnosticSeverities.Menor,
        Titulo = "Pólizas cuyo importe no coincide con su asiento",
        Detalle = $"{facts.PolizasDescuadradasMonto} póliza(s) declaran en el encabezado un monto distinto a la suma del debe.",
        Conteo = facts.PolizasDescuadradasMonto,
        AccionSugerida = "Ajusta el encabezado al asiento."
      });
    }

    // R10 · La cuenta de bancos quedó con saldo acreedor.
    if (facts.SaldoBancos < -0.01m)
    {
      findings.Add(new RestaurantDiagnosticFindingDto
      {
        ReglaClave = "R10",
        Severidad = RestaurantDiagnosticSeverities.Media,
        Titulo = "La cuenta de bancos tiene saldo acreedor",
        Detalle = $"El agrupador 102 quedó en {facts.SaldoBancos:C}. Una cuenta de banco no puede tener saldo a favor del banco: " +
                  "faltan depósitos por registrar o hay cargos mal clasificados.",
        Agrupadores = "102",
        MontoExpuesto = Math.Abs(facts.SaldoBancos),
        AccionSugerida = "Registra los depósitos del periodo y concilia contra el estado de cuenta."
      });
    }

    // R11 · Activo fijo sin capital que lo respalde.
    if (facts.ActivoFijo > 0.01m && facts.Capital <= 0.01m)
    {
      findings.Add(new RestaurantDiagnosticFindingDto
      {
        ReglaClave = "R11",
        Severidad = RestaurantDiagnosticSeverities.Alta,
        Titulo = "Activo fijo sin capital que lo respalde",
        Detalle = $"Hay {facts.ActivoFijo:C} de activo fijo y cargos diferidos, cero en los agrupadores de capital (301 a 306) y " +
                  $"{facts.Acreedores:C} de pasivo con acreedores. La inversión se sostiene sólo con pasivo de partes relacionadas.",
        Agrupadores = "301,302,303,304,305,306,205",
        MontoExpuesto = facts.Acreedores,
        AccionSugerida = "Define con el contador qué parte es aportación de capital y qué parte préstamo documentado, y abre la depreciación mensual."
      });
    }

    // R12 · Sueldos sin retenciones ni seguridad social.
    if (facts.Sueldos > 0.01m && facts.Retenciones <= 0.01m)
    {
      findings.Add(new RestaurantDiagnosticFindingDto
      {
        ReglaClave = "R12",
        Severidad = RestaurantDiagnosticSeverities.Critica,
        Titulo = "Sueldos pagados sin retenciones ni seguridad social",
        Detalle = $"{facts.Sueldos:C} están registrados en cuentas de sueldos y salarios (601-01 y 603-01) con {facts.Empleados} empleado(s) " +
                  $"dados de alta y {facts.PeriodosNomina} periodo(s) de nómina procesados, sin un solo movimiento en el agrupador 216 de " +
                  "impuestos retenidos ni en el 213 de impuestos por pagar.",
        Agrupadores = "601,603,216,213",
        MontoExpuesto = facts.Sueldos,
        Conteo = facts.Empleados,
        AccionSugerida = "Decide con el contador la figura de cada persona: empleados con alta en IMSS y CFDI de nómina, o profesionistas con retención de ISR e IVA."
      });
    }

    // R13 · Existencias sin inventario contable.
    if (facts.ValorInventario > 0.01m && facts.MovimientosInventarioContable == 0)
    {
      findings.Add(new RestaurantDiagnosticFindingDto
      {
        ReglaClave = "R13",
        Severidad = RestaurantDiagnosticSeverities.Alta,
        Titulo = "No existe inventario contable",
        Detalle = $"El almacén reporta {facts.ValorInventario:C} en {facts.SaldosInventario} saldo(s) con existencia y el agrupador 115 no tiene " +
                  "un solo movimiento. Las compras se gastan al momento, así que el resultado de cada mes depende de cuándo se surtió la despensa.",
        Agrupadores = "115,501,502",
        MontoExpuesto = facts.ValorInventario,
        Conteo = facts.SaldosInventario,
        AccionSugerida = "Da de alta 115-02 y activa la cuenta de inventario en la configuración del punto de venta."
      });
    }

    // R14 · Compras que nunca se trasladan a costo de venta.
    if (facts.Compras > 0.01m && facts.CostoVenta <= 0.01m)
    {
      findings.Add(new RestaurantDiagnosticFindingDto
      {
        ReglaClave = "R14",
        Severidad = RestaurantDiagnosticSeverities.Alta,
        Titulo = "Las compras nunca se trasladan a costo de venta",
        Detalle = $"El agrupador 502 acumula {facts.Compras:C} y el 501 está en cero. Sin costo de venta no existe margen bruto y el costo de " +
                  "alimentos aparente no describe la operación.",
        Agrupadores = "501,502",
        MontoExpuesto = facts.Compras,
        AccionSugerida = "Da de alta 501-01 y cierra cada mes contra inventario."
      });
    }
  }

  private static void AddOperationFindings(List<RestaurantDiagnosticFindingDto> findings, DiagnosticFacts facts)
  {
    // R02 · Órdenes pagadas sin póliza que las respalde.
    if (facts.OrdenesSinLigar > 0)
    {
      findings.Add(new RestaurantDiagnosticFindingDto
      {
        ReglaClave = "R02",
        Severidad = facts.IngresoContable > 0.01m
          ? RestaurantDiagnosticSeverities.Media
          : RestaurantDiagnosticSeverities.Critica,
        Titulo = facts.IngresoContable > 0.01m
          ? "Las ventas están en los libros, pero sin trazabilidad"
          : "Las ventas del periodo no llegaron a los libros",
        Detalle = facts.IngresoContable > 0.01m
          ? $"{facts.OrdenesSinLigar} orden(es) pagadas por {facts.CobrosPos:C} no tienen renglón en AccountingOrderLink, aunque el agrupador 401 " +
            $"registra {facts.IngresoContable:C}. El ingreso se capturó a mano: no se puede auditar qué orden respalda qué asiento ni detectar una venta capturada dos veces."
          : $"{facts.OrdenesSinLigar} orden(es) pagadas por {facts.CobrosPos:C} no tienen póliza ni ingreso contable en el periodo.",
        Agrupadores = "401,208,101,102",
        MontoExpuesto = facts.CobrosPos,
        Conteo = facts.OrdenesSinLigar,
        AccionSugerida = "Genera las pólizas diarias del periodo desde el diagnóstico, después de depurar la recaptura manual para no duplicar el ingreso."
      });
    }

    // R15 · Diferencias de efectivo en los turnos de caja.
    if (facts.TurnosConDiferencia > 0 || facts.TurnosSinAprobar > 0)
    {
      var severidad = Math.Abs(facts.DiferenciaCajaNeta) > 5000m
        ? RestaurantDiagnosticSeverities.Alta
        : RestaurantDiagnosticSeverities.Media;
      findings.Add(new RestaurantDiagnosticFindingDto
      {
        ReglaClave = "R15",
        Severidad = severidad,
        Titulo = facts.DiferenciaCajaNeta > 0
          ? "Sobrante sistemático de efectivo en los turnos"
          : "Diferencias de efectivo en los turnos de caja",
        Detalle = $"{facts.TurnosCerrados} turno(s) cerrados con {facts.DiferenciaCajaNeta:C} de diferencia neta y {facts.DiferenciaCajaAbsoluta:C} " +
                  $"en valor absoluto repartida en {facts.TurnosConDiferencia} turno(s). {facts.TurnosSinAprobar} cerraron sin aprobación de supervisor. " +
                  "Un sobrante sostenido apunta a cobros que entraron al cajón sin registrarse como orden, o a fondos de caja que no se reponen al valor declarado.",
        Agrupadores = "101",
        MontoExpuesto = Math.Abs(facts.DiferenciaCajaNeta),
        Conteo = facts.TurnosConDiferencia,
        AccionSugerida = "Fija un fondo de caja constante, exige aprobación para cerrar e investiga los turnos con diferencia mayor a $500."
      });
    }

    // R16 · Conteos físicos con cantidades imposibles.
    if (facts.ConteosAtipicos > 0)
    {
      findings.Add(new RestaurantDiagnosticFindingDto
      {
        ReglaClave = "R16",
        Severidad = RestaurantDiagnosticSeverities.Media,
        Titulo = "Conteos físicos con cantidades imposibles",
        Detalle = $"{facts.ConteosAtipicos} captura(s) de conteo superaron {ConteoAtipicoUmbral:N0} unidades. " +
                  (string.IsNullOrWhiteSpace(facts.DetalleConteos) ? string.Empty : $"Ejemplos: {facts.DetalleConteos}. ") +
                  "La pantalla de conteo no cuestiona ningún número.",
        Agrupadores = "115",
        Conteo = facts.ConteosAtipicos,
        AccionSugerida = "Pide confirmación cuando el conteo se desvíe más de un múltiplo razonable del saldo anterior o del máximo configurado."
      });
    }

    // R17 · Productos que se preparan al momento con existencia en almacén.
    if (facts.MaterialesFantasma > 0)
    {
      findings.Add(new RestaurantDiagnosticFindingDto
      {
        ReglaClave = "R17",
        Severidad = RestaurantDiagnosticSeverities.Alta,
        Titulo = "Productos preparados al momento con existencia en almacén",
        Detalle = $"{facts.MaterialesFantasma} material(es) marcados como preparación a pedido acumulan {facts.UnidadesFantasma:N0} unidades " +
                  $"valuadas en {facts.ValorFantasma:C}. Un producto que se prepara al momento no debe tener existencia.",
        Agrupadores = "115",
        MontoExpuesto = facts.ValorFantasma,
        Conteo = facts.MaterialesFantasma,
        AccionSugerida = "Pon en cero la existencia de todo producto a pedido y bloquea su conteo."
      });
    }
  }

  private static void AddCostFindings(List<RestaurantDiagnosticFindingDto> findings, DiagnosticFacts facts)
  {
    // R18 · Precio de venta capturado como costo del material.
    if (facts.MaterialesPrecioComoCosto > 0)
    {
      findings.Add(new RestaurantDiagnosticFindingDto
      {
        ReglaClave = "R18",
        Severidad = RestaurantDiagnosticSeverities.Alta,
        Titulo = "Precio de venta capturado como costo del material",
        Detalle = $"{facts.MaterialesPrecioComoCosto} producto(s) tienen su precio unitario de material igual o mayor a su precio de lista. " +
                  "Cuando uno de ellos entra como ingrediente de otra receta, el costeo toma el precio de venta antes que el costo real.",
        Conteo = facts.MaterialesPrecioComoCosto,
        AccionSugerida = "Vacía el precio unitario de todo material que tenga receta activa, para que el costeo use la receta."
      });
    }

    // R19 · Componentes de receta sin conversión de unidad.
    if (facts.ComponentesSinConversion > 0)
    {
      findings.Add(new RestaurantDiagnosticFindingDto
      {
        ReglaClave = "R19",
        Severidad = RestaurantDiagnosticSeverities.Alta,
        Titulo = "Componentes de receta sin conversión de unidad",
        Detalle = $"{facts.ComponentesSinConversion} de {facts.ComponentesTotales} componentes de recetas activas no resuelven su factor de " +
                  "conversión, así que desaparecen del costo sin aviso y el platillo aparece más barato de lo que es.",
        Agrupadores = "501",
        Conteo = facts.ComponentesSinConversion,
        AccionSugerida = "Configura la conversión de unidad del material o captura el componente en su unidad base."
      });
    }
    else if (facts.ComponentesTotales > 0)
    {
      findings.Add(new RestaurantDiagnosticFindingDto
      {
        ReglaClave = "R19",
        Severidad = RestaurantDiagnosticSeverities.Informativa,
        Titulo = "Conversiones de unidad completas",
        Detalle = $"Los {facts.ComponentesTotales} componentes de las recetas activas resuelven su factor de conversión. " +
                  "Ninguno se descarta en silencio del costeo.",
        Conteo = facts.ComponentesTotales,
        Estado = RestaurantDiagnosticStates.Corregido
      });
    }

    // R20 · El costo guardado en las órdenes no es utilizable.
    if (facts.OrdenesCostoAbsurdo > 0)
    {
      findings.Add(new RestaurantDiagnosticFindingDto
      {
        ReglaClave = "R20",
        Severidad = RestaurantDiagnosticSeverities.Media,
        Titulo = "El costo guardado en las órdenes no es utilizable",
        Detalle = $"{facts.OrdenesCostoAbsurdo} orden(es) tienen un costo teórico mayor al doble de su venta; el máximo llega a " +
                  $"{facts.MaxCostoAbsurdo:C}. Los reportes recalculan el costo desde la receta activa e ignoran ese campo.",
        Agrupadores = "501",
        Conteo = facts.OrdenesCostoAbsurdo,
        AccionSugerida = "No uses el margen histórico de las órdenes; recalcula desde la receta hasta que el campo se corrija."
      });
    }

    // R22 · Recetas vendibles sin costo utilizable.
    if (facts.RecetasSinCosto > 0 || facts.RecetasRendimientoLote > 0)
    {
      findings.Add(new RestaurantDiagnosticFindingDto
      {
        ReglaClave = "R22",
        Severidad = RestaurantDiagnosticSeverities.Media,
        Titulo = "Recetas vendibles sin costo utilizable",
        Detalle = $"{facts.RecetasSinCosto} receta(s) activas de productos a la venta cuestan cero y {facts.RecetasRendimientoLote} declaran su " +
                  "rendimiento en unidad de lote, de modo que el costo por porción queda en centavos y el platillo aparece con margen casi total. " +
                  (string.IsNullOrWhiteSpace(facts.DetalleRecetas) ? string.Empty : $"Ejemplos: {facts.DetalleRecetas}."),
        Agrupadores = "501",
        Conteo = facts.RecetasSinCosto + facts.RecetasRendimientoLote,
        AccionSugerida = "Corrige el rendimiento a porciones servidas y completa las recetas sin componentes."
      });
    }

    // R24 · Productos vendidos sin ningún costo del cual partir.
    if (facts.ProductosSinCosto > 0)
    {
      findings.Add(new RestaurantDiagnosticFindingDto
      {
        ReglaClave = "R24",
        Severidad = RestaurantDiagnosticSeverities.Alta,
        Titulo = "Productos vendidos sin costo asignado",
        Detalle = $"{facts.ProductosSinCosto} producto(s) vendieron {facts.VentaSinCosto:C} sin receta activa ni precio de compra en su material. " +
                  "Son típicamente artículos de reventa —cerveza, refrescos, agua, cigarros— cuyo margen aparece al 100% porque el sistema no " +
                  "tiene de dónde tomar su costo.",
        Agrupadores = "501,502",
        MontoExpuesto = facts.VentaSinCosto,
        Conteo = facts.ProductosSinCosto,
        AccionSugerida = "Captura el precio de compra en el material de cada producto de reventa, o dale una receta si se prepara."
      });
    }

    // R23 · Costo congelado desactualizado respecto al recalculado.
    if (facts.RecetasConDeriva > 0)
    {
      findings.Add(new RestaurantDiagnosticFindingDto
      {
        ReglaClave = "R23",
        Severidad = RestaurantDiagnosticSeverities.Media,
        Titulo = "Costos de receta desactualizados",
        Detalle = $"{facts.RecetasConDeriva} de {facts.RecetasActivas} recetas activas tienen deriva entre el costo guardado y el recalculado " +
                  $"con los precios de hoy; la mayor es de {facts.DerivaMaxima:C}. El costo sólo se refresca al guardar la receta.",
        Agrupadores = "501",
        Conteo = facts.RecetasConDeriva,
        AccionSugerida = "Los reportes recalculan al consultarse; además conviene refrescar el costo guardado cuando cambie el precio de un ingrediente."
      });
    }
  }

  // ------------------------------------------------------------------
  // Persistencia
  // ------------------------------------------------------------------

  private static async Task<RestaurantDiagnosticRunDto> PersistRunAsync(
    DbConnection conn,
    string rfc,
    int siteId,
    DateTime from,
    DateTime to,
    string userName,
    IReadOnlyList<RestaurantDiagnosticFindingDto> findings,
    CancellationToken ct)
  {
    var criticos = findings.Count(finding => finding.Severidad == RestaurantDiagnosticSeverities.Critica);
    var monto = decimal.Round(findings.Sum(finding => finding.MontoExpuesto), 2);

    var runId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
      """
      INSERT INTO restaurante.DiagnosticoCorrida
        (Rfc, SiteId, PeriodoInicio, PeriodoFin, EjecutadoPor, HallazgosTotal, Criticos, MontoExpuesto)
      OUTPUT INSERTED.Id
      VALUES (@Rfc, @SiteId, @From, @To, @UserName, @Total, @Criticos, @Monto);
      """,
      new
      {
        Rfc = rfc,
        SiteId = siteId,
        From = from,
        To = to,
        UserName = userName,
        Total = findings.Count,
        Criticos = criticos,
        Monto = monto
      }, cancellationToken: ct));

    if (findings.Count > 0)
    {
      await conn.ExecuteAsync(new CommandDefinition(
        """
        INSERT INTO restaurante.DiagnosticoHallazgo
          (Rfc, CorridaId, ReglaClave, Severidad, Titulo, Detalle, Agrupadores, MontoExpuesto, Conteo, AccionSugerida, Estado)
        VALUES (@Rfc, @CorridaId, @ReglaClave, @Severidad, @Titulo, @Detalle, @Agrupadores, @MontoExpuesto, @Conteo, @AccionSugerida, @Estado);
        """,
        findings.Select(finding => new
        {
          Rfc = rfc,
          CorridaId = runId,
          finding.ReglaClave,
          finding.Severidad,
          Titulo = Truncate(finding.Titulo, 200),
          Detalle = Truncate(finding.Detalle, 2000),
          Agrupadores = Truncate(finding.Agrupadores, 400),
          finding.MontoExpuesto,
          finding.Conteo,
          AccionSugerida = Truncate(finding.AccionSugerida, 600),
          finding.Estado
        }).ToArray(), cancellationToken: ct));
    }

    var stored = (await conn.QueryAsync<RestaurantDiagnosticFindingDto>(new CommandDefinition(
      """
      SELECT Id, CorridaId, ReglaClave, Severidad, Titulo, Detalle, Agrupadores,
             MontoExpuesto, Conteo, AccionSugerida, Estado, Justificacion, ResueltoEn, ResueltoPor
      FROM restaurante.DiagnosticoHallazgo
      WHERE Rfc = @Rfc AND CorridaId = @CorridaId;
      """, new { Rfc = rfc, CorridaId = runId }, cancellationToken: ct))).AsList();

    return new RestaurantDiagnosticRunDto
    {
      Id = runId,
      SiteId = siteId,
      PeriodoInicio = from,
      PeriodoFin = to,
      EjecutadoEn = DateTime.UtcNow,
      EjecutadoPor = userName,
      HallazgosTotal = findings.Count,
      Criticos = criticos,
      MontoExpuesto = monto,
      Findings = stored
        .OrderBy(finding => RestaurantDiagnosticSeverities.Rank(finding.Severidad))
        .ThenByDescending(finding => finding.MontoExpuesto)
        .ThenBy(finding => finding.ReglaClave, StringComparer.Ordinal)
        .ToList()
    };
  }

  private async Task<string> NextNivel3Async(string rfc, string nivel1, string nivel2, CancellationToken ct)
  {
    using var conn = CreateConnection();
    var max = await conn.ExecuteScalarAsync<int?>(new CommandDefinition(
      """
      SELECT MAX(TRY_CONVERT(int, cuenta.Nivel3))
      FROM dbo.CuentasContables cuenta
      WHERE cuenta.RFC = @Rfc AND cuenta.Nivel1 = @Nivel1 AND cuenta.Nivel2 = @Nivel2 AND cuenta.Nivel3 <> '00';
      """, new { Rfc = rfc, Nivel1 = nivel1, Nivel2 = nivel2 }, cancellationToken: ct));
    return ((max ?? 0) + 1).ToString("00");
  }

  private static string? Truncate(string? value, int max)
    => string.IsNullOrEmpty(value) || value.Length <= max ? value : value[..max];

  private DbConnection CreateConnection()
    => _connectionFactory.Create() as DbConnection
      ?? throw new InvalidOperationException("La fábrica de conexiones no devolvió una DbConnection.");

  private sealed class CatalogRow
  {
    public string Nivel1 { get; set; } = string.Empty;
    public string Nivel2 { get; set; } = string.Empty;
    public string Nivel3 { get; set; } = string.Empty;
    public string Descripcion { get; set; } = string.Empty;
  }

  private sealed class PendingDayRow
  {
    public DateTime Fecha { get; set; }
    public int Ordenes { get; set; }
    public decimal Importe { get; set; }
  }
}

/// <summary>Cuentas que la póliza diaria necesita para poder generarse.</summary>
public static class RestaurantAccountingFields
{
  public static readonly IReadOnlyList<string> Requeridos =
    ["Efectivo", "Ventas", "IVA", "Descuentos", "Inventario", "Costo de venta", "Banco / tarjetas", "Transferencias"];

  public static IEnumerable<string> Faltantes(RestaurantAccountingConfigurationDto configuration)
  {
    if (string.IsNullOrWhiteSpace(configuration.CashAccount)) yield return "Efectivo";
    if (string.IsNullOrWhiteSpace(configuration.SalesAccount)) yield return "Ventas";
    if (string.IsNullOrWhiteSpace(configuration.VatAccount)) yield return "IVA";
    if (string.IsNullOrWhiteSpace(configuration.DiscountAccount)) yield return "Descuentos";
    if (string.IsNullOrWhiteSpace(configuration.InventoryAccount)) yield return "Inventario";
    if (string.IsNullOrWhiteSpace(configuration.CostOfSalesAccount)) yield return "Costo de venta";
    if (string.IsNullOrWhiteSpace(configuration.CardBankAccount)) yield return "Banco / tarjetas";
    if (string.IsNullOrWhiteSpace(configuration.TransferBankAccount)) yield return "Transferencias";
  }
}
