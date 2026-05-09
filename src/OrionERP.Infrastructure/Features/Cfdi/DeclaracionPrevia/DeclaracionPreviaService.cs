using System.Data;
using System.Security.Claims;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using OrionERP.Application.Features.Cfdi.Facturama;
using OrionERP.Application.Features.Cfdi.DeclaracionPrevia;

namespace OrionERP.Infrastructure.Features.Cfdi.DeclaracionPrevia;

public class DeclaracionPreviaService : IDeclaracionPreviaService
{
  private static readonly IReadOnlyList<(int, string)> AvailableMonths =
  [
    (1, "ENERO"), (2, "FEBRERO"), (3, "MARZO"), (4, "ABRIL"), (5, "MAYO"), (6, "JUNIO"),
    (7, "JULIO"), (8, "AGOSTO"), (9, "SEPTIEMBRE"), (10, "OCTUBRE"), (11, "NOVIEMBRE"), (12, "DICIEMBRE")
  ];

  private readonly string _connectionString;
  private readonly IFacturamaApiClient _facturamaApiClient;

  public DeclaracionPreviaService(IConfiguration configuration, IFacturamaApiClient facturamaApiClient)
  {
    _facturamaApiClient = facturamaApiClient ?? throw new ArgumentNullException(nameof(facturamaApiClient));
    _connectionString = configuration.GetConnectionString("OrionDb")
      ?? throw new InvalidOperationException("Missing connection string 'OrionDb'.");
  }

  public Task<IReadOnlyList<string>> GetAvailableRfcsAsync(ClaimsPrincipal user)
  {
    var rfcs = user.FindAll("rfc")
      .Select(c => c.Value)
      .Distinct(StringComparer.OrdinalIgnoreCase)
      .OrderBy(r => r)
      .ToList();

    return Task.FromResult<IReadOnlyList<string>>(rfcs);
  }

  public async Task<DeclaracionPreviaData> GetDeclaracionAsync(DeclaracionPreviaRequest request)
  {
    using var conn = new SqlConnection(_connectionString);
    await conn.OpenAsync();

    var common = new
    {
      Year = request.Year,
      Month = request.IsAnnual ? (object?)DBNull.Value : request.Month,
      RFC = request.Rfc,
      RFC_Emisor = request.Rfc,
      RFC_Receptor = request.Rfc,
      Anio = request.Year,
      Mes = request.Month,
      startDate = GetSqlDate(request.StartDate),
      endDate = GetSqlDate(request.EndDate)
    };

    var allCfdiBase = (await conn.QueryAsync<DeclaracionCfdiBase>(
      "EXEC cfdi.Declaracion_CFDI_Base @Year, @Month, @RFC",
      new { Year = request.Year, Month = request.IsAnnual ? (object?)DBNull.Value : request.Month, RFC = request.Rfc })).AsList();

    var emitidas = new List<DeclaracionEmitida>();
    var recibidas = new List<DeclaracionRecibida>();
    var emitidasPpd = new List<DeclaracionEmitida>();
    var recibidasPpd = new List<DeclaracionRecibida>();
    var emitidasNomina = new List<DeclaracionEmitida>();
    var recibidasNomina = new List<DeclaracionRecibida>();
    var tipoEEmitidas = new List<DeclaracionEmitida>();
    var tipoERecibidas = new List<DeclaracionRecibida>();

    foreach (var item in allCfdiBase)
    {
      if (item.EsEmitida)
      {
        if (IsNomina(item.TipoDeComprobante))
        {
          emitidasNomina.Add(new DeclaracionEmitida(item));
        }
        else if (IsTipoE(item.TipoDeComprobante))
        {
          tipoEEmitidas.Add(new DeclaracionEmitida(item));
        }
        else if (IsPpd(item.MetodoPago))
        {
          emitidasPpd.Add(new DeclaracionEmitida(item));
        }
        else
        {
          emitidas.Add(new DeclaracionEmitida(item));
        }
      }

      if (item.EsRecibida)
      {
        if (IsNomina(item.TipoDeComprobante))
        {
          recibidasNomina.Add(new DeclaracionRecibida(item));
        }
        else if (IsTipoE(item.TipoDeComprobante))
        {
          tipoERecibidas.Add(new DeclaracionRecibida(item));
        }
        else if (IsPpd(item.MetodoPago))
        {
          recibidasPpd.Add(new DeclaracionRecibida(item));
        }
        else
        {
          recibidas.Add(new DeclaracionRecibida(item));
        }
      }
    }

    var canceladasOmitidas = (await conn.QueryAsync<DeclaracionCfdiBase>(
      "EXEC cfdi.Declaracion_Canceladas_Omitidas @Year, @Month, @RFC_Emisor",
      new { Year = request.Year, Month = request.IsAnnual ? (object?)DBNull.Value : request.Month, RFC_Emisor = request.Rfc }))
      .Select(ToDeclaracionEmitida)
      .ToList();

    var complementosBase = (await conn.QueryAsync<DeclaracionComplementoBase>(
      "EXEC cfdi.Declaracion_Complementos_Base @Year, @Month, @RFC",
      new { Year = request.Year, Month = request.IsAnnual ? (object?)DBNull.Value : request.Month, RFC = request.Rfc })).AsList();

    var complementosEmitidos = new List<DeclaracionComplementoEmitido>();
    var complementosRecibidos = new List<DeclaracionComplementoRecibido>();

    foreach (var item in complementosBase)
    {
      if (item.EsEmitida)
      {
        complementosEmitidos.Add(new DeclaracionComplementoEmitido(item));
      }

      if (item.EsRecibida)
      {
        complementosRecibidos.Add(new DeclaracionComplementoRecibido(item));
      }
    }

    var desfase = (await conn.QueryAsync<DesfaseItem>(
      "EXEC dbo.Declaracion_Comprobantes_Con_Desfase @RFC, @Anio, @Mes", common)).ToList();

    var desfaseTotals = await conn.QueryFirstOrDefaultAsync<DesfaseTotales>(
      "EXEC dbo.Declaracion_Comprobantes_Con_Desfase_Totales @RFC, @Anio, @Mes", common);

    var polizasNoConsolidadas = (await conn.QueryAsync<PolizaNoConsolidada>(
      "EXEC dbo.Polizas_No_Consolidadas @RFC, @Anio, @Mes", common)).ToList();

    var impuestosSummary = await conn.QueryFirstOrDefaultAsync<string>(
      "EXEC dbo.CALCULATE_TAXES @RFC, @startDate, @endDate",
      common) ?? string.Empty;

    var bancosCajaSummary = await conn.QueryFirstOrDefaultAsync<string?>(
      "EXEC dbo.Reporte_Bancos_Caja @Year, @Month, @RFC",
      new { Year = request.Year, Month = request.IsAnnual ? (object?)DBNull.Value : request.Month, RFC = request.Rfc });

    const int startYear = 2020;
    var currentYear = DateTime.UtcNow.Year;
    var yearCount = Math.Max(1, currentYear - startYear + 1);

    return new DeclaracionPreviaData
    {
      AllCfdiBase = allCfdiBase,
      EmitidasBase = emitidas,
      RecibidasBase = recibidas,
      EmitidasPpdBase = emitidasPpd,
      RecibidasPpdBase = recibidasPpd,
      EmitidasNominaBase = emitidasNomina,
      RecibidasNominaBase = recibidasNomina,
      TipoEEmitidasBase = tipoEEmitidas,
      TipoERecibidasBase = tipoERecibidas,
      CanceladasOmitidasBase = canceladasOmitidas,
      ComplementosBase = complementosBase,
      ComplementosEmitidosBase = complementosEmitidos,
      ComplementosRecibidosBase = complementosRecibidos,
      Emitidas = emitidas,
      Recibidas = recibidas,
      EmitidasPpd = emitidasPpd,
      RecibidasPpd = recibidasPpd,
      EmitidasNomina = emitidasNomina,
      RecibidasNomina = recibidasNomina,
      TipoEEmitidas = tipoEEmitidas,
      TipoERecibidas = tipoERecibidas,
      CanceladasOmitidas = canceladasOmitidas,
      ComplementosEmitidos = complementosEmitidos,
      ComplementosRecibidos = complementosRecibidos,
      EmitidasTotals = ComputeDeclaracionTotales(emitidas),
      EmitidasPpdTotals = ComputeDeclaracionTotales(emitidasPpd),
      EmitidasNominaTotals = ComputeDeclaracionTotales(emitidasNomina),
      RecibidasTotals = ComputeDeclaracionTotales(recibidas),
      RecibidasPpdTotals = ComputeDeclaracionTotales(recibidasPpd),
      RecibidasNominaTotals = ComputeDeclaracionTotales(recibidasNomina),
      TipoEEmitidasTotals = ComputeDeclaracionTotales(tipoEEmitidas),
      TipoERecibidasTotals = ComputeDeclaracionTotales(tipoERecibidas),
      CanceladasOmitidasTotals = ComputeDeclaracionTotales(canceladasOmitidas),
      Desfase = desfase,
      DesfaseTotals = desfaseTotals,
      PolizasNoConsolidadas = polizasNoConsolidadas,
      ImpuestosSummary = impuestosSummary,
      BancosCajaSummary = bancosCajaSummary,
      DisponibleYears = Enumerable.Range(startYear, yearCount).ToArray(),
      DisponibleMonths = AvailableMonths
    };
  }

  public async Task ToggleInclusionAsync(int comprobanteId)
  {
    const string sql = "UPDATE Comprobante SET Incluir_En_Declaracion = CASE WHEN Incluir_En_Declaracion = 1 THEN 0 ELSE 1 END WHERE Comprobante_Id = @Id";
    using var conn = new SqlConnection(_connectionString);
    await conn.ExecuteAsync(sql, new { Id = comprobanteId });
  }

  public async Task<int> ExcludePagosYDevolucionesAsync(string rfc, int year, int? month)
  {
    var (startDate, endDate) = GetDateRange(year, month);
    const string sql = @"
                    UPDATE C
                    SET Incluir_En_Declaracion = 0
                    FROM Comprobante C
                    JOIN Receptor R ON C.Comprobante_ID = R.Comprobante_ID
                    WHERE C.Incluir_En_Declaracion = 1
                      AND (R.UsoCFDI = 'G02' OR R.UsoCFDI = 'CP01')
                      AND R.RFC = @RFC
                      AND C.Fecha >= @StartDate
                      AND C.Fecha < @EndDate";

    using var conn = new SqlConnection(_connectionString);
    return await conn.ExecuteAsync(sql, new { RFC = rfc, StartDate = startDate, EndDate = endDate });
  }

  public async Task<IReadOnlyList<PagoComplementoResumen>> GetComplementosAsync(Guid uuid)
  {
    using var conn = new SqlConnection(_connectionString);
    var resultados = await conn.QueryAsync<PagoComplementoResumen>(
      "EXEC cfdi.Complemento_Resumen_By_UUID @UUID_DoctoRelacionado",
      new { UUID_DoctoRelacionado = uuid });

    return resultados.ToList();
  }

  public async Task<int> GenerarPolizaDesdeComprobanteAsync(int comprobanteId, string rfc)
  {
    using var conn = new SqlConnection(_connectionString);
    var parameters = new DynamicParameters();
    parameters.Add("@Comprobante_Id", comprobanteId);
    parameters.Add("@RFC", rfc);
    parameters.Add("@TransaccionID", dbType: DbType.Int32, direction: ParameterDirection.Output);

    await conn.ExecuteAsync("[contabilidad].[Generar_Poliza_Desde_Comprobante]", parameters, commandType: CommandType.StoredProcedure);

    return parameters.Get<int>("@TransaccionID");
  }

  public async Task CancelEmitidaAsync(string uuid, int comprobanteId)
  {
    var cfdiId = await _facturamaApiClient.FindIssuedCfdiIdByUuidAsync(uuid);
    if (string.IsNullOrWhiteSpace(cfdiId))
    {
      throw new InvalidOperationException("No se encontró el CFDI en Facturama para ese UUID.");
    }

    await _facturamaApiClient.CancelIssuedCfdiAsync(cfdiId);

    using var conn = new SqlConnection(_connectionString);
    await conn.ExecuteAsync("""
UPDATE Comprobante
SET Incluir_En_Declaracion = 0,
    FechaCancelacion = COALESCE(FechaCancelacion, GETDATE()),
    Estatus = 'Cancelado'
WHERE Comprobante_Id = @Id
""", new { Id = comprobanteId });
  }

  public async Task<IReadOnlyList<string>> GenerateDiotAsync(string rfc, int year, int month)
  {
    using var conn = new SqlConnection(_connectionString);
    var lines = await conn.QueryAsync<string>("EXEC cfdi.GenerateDIOTTXT @Year, @Month, @receptor",
      new { Year = year, Month = month, receptor = rfc });

    return lines.ToList();
  }

  public async Task<long?> GetLinkedTransactionIdAsync(int comprobanteId)
  {
    using var conn = new SqlConnection(_connectionString);
    return await conn.ExecuteScalarAsync<long?>(
      @"SELECT TOP (1) tc.Transaccion_ID
FROM dbo.Transaccion_Comprobante tc
JOIN dbo.Transacciones t ON t.ID = tc.Transaccion_ID
WHERE tc.Comprobante_ID = @Cid
ORDER BY t.Fecha, t.ID",
      new { Cid = comprobanteId });
  }

  public async Task<ComprobanteDetalleDto?> GetComprobanteDetalleAsync(int comprobanteId)
  {
    const string sql = @"
SELECT TOP (1)
    [Comprobante_Id]       AS Comprobante_Id,
    [UsoCFDI]              AS UsoCFDI,
    [RECEPTOR]             AS RECEPTOR,
    [EMISOR]               AS EMISOR,
    [FOLIO_FISCAL]         AS FOLIO_FISCAL,
    [Fecha]                AS Fecha,
    [SubTotal]             AS SubTotal,
    [SubTotal_Desc]        AS SubTotal_Desc,
    [IVA]                  AS IVA,
    [IEPS]                 AS IEPS,
    [IVA_RETENIDO]         AS IVA_RETENIDO,
    [ISR_RETENIDO]         AS ISR_RETENIDO,
    [IEPS_RETENIDO]        AS IEPS_RETENIDO,
    [Actos_16]             AS Actos_16,
    [Actos_0]              AS Actos_0,
    [Total]                AS Total
FROM [grupocarpio].[cfdi].[Comprobante_Detalle]
WHERE Comprobante_Id = @Comprobante_Id;";

    using var conn = new SqlConnection(_connectionString);
    return await conn.QueryFirstOrDefaultAsync<ComprobanteDetalleDto>(
      sql,
      new { Comprobante_Id = comprobanteId });
  }

  public async Task<Pago20ResumenDetalleDto?> GetPago20ResumenByDoctoRelacionadoIdAsync(int doctoRelacionadoId)
  {
    const string sql = @"
SELECT TOP (1)
    Comprobante_Id,
    ComprobanteUUID,
    EmisorRfc,
    ReceptorRfc,
    Pago_Id,
    FechaPago,
    FormaDePagoP,
    MonedaP,
    MontoPago,
    DoctoRelacionado_Id,
    UUID_DoctoRelacionado,
    Folio,
    NumParcialidad,
    MonedaDR,
    ImpSaldoAnt,
    ImpPagado,
    ImpSaldoInsoluto,
    Poliza,
    Polizas,
    Comp_Actos16,
    Comp_IVA,
    XML_Attachment_ID
FROM cfdi.vw_Pagos20_Resumen
WHERE DoctoRelacionado_Id = @DoctoRelacionado_Id;";

    using var conn = new SqlConnection(_connectionString);
    return await conn.QueryFirstOrDefaultAsync<Pago20ResumenDetalleDto>(
      sql,
      new { DoctoRelacionado_Id = doctoRelacionadoId });
  }

  private static DeclaracionEmitida ToDeclaracionEmitida(DeclaracionCfdiBase item) => new(item);

  private static DeclaracionRecibida ToDeclaracionRecibida(DeclaracionCfdiBase item) => new(item);

  private static DeclaracionComplementoEmitido ToComplementoEmitido(DeclaracionComplementoBase item) => new(item);

  private static DeclaracionComplementoRecibido ToComplementoRecibido(DeclaracionComplementoBase item) => new(item);

  private static decimal SatRound(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

  private static bool IsNomina(string? tipoDeComprobante) => string.Equals(tipoDeComprobante, "N", StringComparison.OrdinalIgnoreCase);

  private static bool IsTipoE(string? tipoDeComprobante) => string.Equals(tipoDeComprobante, "E", StringComparison.OrdinalIgnoreCase);
  private static bool IsPpd(string? metodoPago) => string.Equals(metodoPago, "PPD", StringComparison.OrdinalIgnoreCase);

  private static string GetSqlDate(DateTime dt) => dt.ToString("yyyy-MM-dd HH:mm:ss");

  private static (DateTime StartDate, DateTime EndDate) GetDateRange(int year, int? month)
  {
    if (month.HasValue)
    {
      var monthStart = new DateTime(year, month.Value, 1);
      return (monthStart, monthStart.AddMonths(1));
    }

    var yearStart = new DateTime(year, 1, 1);
    return (yearStart, yearStart.AddYears(1));
  }

  private static DeclaracionTotales ComputeDeclaracionTotales(IEnumerable<DeclaracionCfdiBase> items)
  {
    var list = items?
      .Where(x => !string.Equals(x.D?.Trim(), "X", StringComparison.OrdinalIgnoreCase))
      .ToList()
      ?? new List<DeclaracionCfdiBase>();

    return new DeclaracionTotales
    {
      CountCFDIs = list.Count,
      SumSubTotal = SatRound(list.Sum(x => x.SubTotal)),
      SumDescuento = SatRound(list.Sum(x => x.Descuento)),
      SumSubTotalDesc = SatRound(list.Sum(x => x.SubTotal_Desc)),
      SumActos16 = SatRound(list.Sum(x => x.Actos_16)),
      SumActos0 = SatRound(list.Sum(x => x.Actos_0)),
      SumIVA = SatRound(list.Sum(x => x.IVA)),
      SumIEPS = SatRound(list.Sum(x => x.IEPS)),
      SumIVA_RETENIDO = SatRound(list.Sum(x => x.IVA_RETENIDO)),
      SumISR_RETENIDO = SatRound(list.Sum(x => x.ISR_RETENIDO)),
      SumIEPS_RETENIDO = SatRound(list.Sum(x => x.IEPS_RETENIDO)),
      SumTotal = SatRound(list.Sum(x => x.Total))
    };
  }
}
