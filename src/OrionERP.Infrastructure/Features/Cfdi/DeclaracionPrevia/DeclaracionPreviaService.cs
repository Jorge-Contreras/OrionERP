using System.Data;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using OrionERP.Application.Features.Cfdi.DeclaracionPrevia;

namespace OrionERP.Infrastructure.Features.Cfdi.DeclaracionPrevia;

public class DeclaracionPreviaService : IDeclaracionPreviaService
{
  private readonly string _connectionString;
  private readonly HttpClient _httpClient;
  private readonly IConfiguration _configuration;

  public DeclaracionPreviaService(IConfiguration configuration, HttpClient httpClient)
  {
    _configuration = configuration;
    _httpClient = httpClient;
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
      new { Year = request.Year, Month = request.IsAnnual ? (object?)DBNull.Value : request.Month, RFC = request.Rfc })).ToList();

    var emitidasBase = allCfdiBase
      .Where(x => x.EsEmitida && !IsNomina(x.TipoDeComprobante) && !IsTipoE(x.TipoDeComprobante) && !IsPpd(x.MetodoPago))
      .ToList();

    var recibidasBase = allCfdiBase
      .Where(x => x.EsRecibida && !IsNomina(x.TipoDeComprobante) && !IsTipoE(x.TipoDeComprobante) && !IsPpd(x.MetodoPago))
      .ToList();

    var emitidasPpdBase = allCfdiBase
      .Where(x => x.EsEmitida && !IsNomina(x.TipoDeComprobante) && !IsTipoE(x.TipoDeComprobante) && IsPpd(x.MetodoPago))
      .ToList();

    var recibidasPpdBase = allCfdiBase
      .Where(x => x.EsRecibida && !IsNomina(x.TipoDeComprobante) && !IsTipoE(x.TipoDeComprobante) && IsPpd(x.MetodoPago))
      .ToList();

    var emitidasNominaBase = allCfdiBase
      .Where(x => x.EsEmitida && IsNomina(x.TipoDeComprobante))
      .ToList();

    var recibidasNominaBase = allCfdiBase
      .Where(x => x.EsRecibida && IsNomina(x.TipoDeComprobante))
      .ToList();

    var tipoEEmitidasBase = allCfdiBase
      .Where(x => x.EsEmitida && IsTipoE(x.TipoDeComprobante))
      .ToList();

    var tipoERecibidasBase = allCfdiBase
      .Where(x => x.EsRecibida && IsTipoE(x.TipoDeComprobante))
      .ToList();

    var canceladasOmitidasBase = (await conn.QueryAsync<DeclaracionCfdiBase>(
      "EXEC cfdi.Declaracion_Canceladas_Omitidas @Year, @Month, @RFC_Emisor",
      new { Year = request.Year, Month = request.IsAnnual ? (object?)DBNull.Value : request.Month, RFC_Emisor = request.Rfc })).ToList();

    var complementosBase = (await conn.QueryAsync<DeclaracionComplementoBase>(
      "EXEC cfdi.Declaracion_Complementos_Base @Year, @Month, @RFC",
      new { Year = request.Year, Month = request.IsAnnual ? (object?)DBNull.Value : request.Month, RFC = request.Rfc })).ToList();

    var complementosEmitidosBase = complementosBase
      .Where(x => x.EsEmitida)
      .ToList();

    var complementosRecibidosBase = complementosBase
      .Where(x => x.EsRecibida)
      .ToList();

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

    return new DeclaracionPreviaData
    {
      AllCfdiBase = allCfdiBase,
      EmitidasBase = emitidasBase,
      RecibidasBase = recibidasBase,
      EmitidasPpdBase = emitidasPpdBase,
      RecibidasPpdBase = recibidasPpdBase,
      EmitidasNominaBase = emitidasNominaBase,
      RecibidasNominaBase = recibidasNominaBase,
      TipoEEmitidasBase = tipoEEmitidasBase,
      TipoERecibidasBase = tipoERecibidasBase,
      CanceladasOmitidasBase = canceladasOmitidasBase,
      ComplementosBase = complementosBase,
      ComplementosEmitidosBase = complementosEmitidosBase,
      ComplementosRecibidosBase = complementosRecibidosBase,
      Emitidas = emitidasBase.Select(ToDeclaracionEmitida).ToList(),
      Recibidas = recibidasBase.Select(ToDeclaracionRecibida).ToList(),
      EmitidasPpd = emitidasPpdBase.Select(ToDeclaracionEmitida).ToList(),
      RecibidasPpd = recibidasPpdBase.Select(ToDeclaracionRecibida).ToList(),
      EmitidasNomina = emitidasNominaBase.Select(ToDeclaracionEmitida).ToList(),
      RecibidasNomina = recibidasNominaBase.Select(ToDeclaracionRecibida).ToList(),
      TipoEEmitidas = tipoEEmitidasBase.Select(ToDeclaracionEmitida).ToList(),
      TipoERecibidas = tipoERecibidasBase.Select(ToDeclaracionRecibida).ToList(),
      CanceladasOmitidas = canceladasOmitidasBase.Select(ToDeclaracionEmitida).ToList(),
      ComplementosEmitidos = complementosEmitidosBase.Select(ToComplementoEmitido).ToList(),
      ComplementosRecibidos = complementosRecibidosBase.Select(ToComplementoRecibido).ToList(),
      EmitidasTotals = ComputeDeclaracionTotales(emitidasBase),
      EmitidasPpdTotals = ComputeDeclaracionTotales(emitidasPpdBase),
      EmitidasNominaTotals = ComputeDeclaracionTotales(emitidasNominaBase),
      RecibidasTotals = ComputeDeclaracionTotales(recibidasBase),
      RecibidasPpdTotals = ComputeDeclaracionTotales(recibidasPpdBase),
      RecibidasNominaTotals = ComputeDeclaracionTotales(recibidasNominaBase),
      TipoEEmitidasTotals = ComputeDeclaracionTotales(tipoEEmitidasBase),
      TipoERecibidasTotals = ComputeDeclaracionTotales(tipoERecibidasBase),
      CanceladasOmitidasTotals = ComputeDeclaracionTotales(canceladasOmitidasBase),
      Desfase = desfase,
      DesfaseTotals = desfaseTotals,
      PolizasNoConsolidadas = polizasNoConsolidadas,
      ImpuestosSummary = impuestosSummary,
      BancosCajaSummary = bancosCajaSummary,
      DisponibleYears = Enumerable.Range(2020, 7).ToList(),
      DisponibleMonths = new List<(int, string)>
      {
        (1, "ENERO"),(2, "FEBRERO"),(3, "MARZO"),(4, "ABRIL"),(5, "MAYO"),(6, "JUNIO"),
        (7, "JULIO"),(8, "AGOSTO"),(9, "SEPTIEMBRE"),(10, "OCTUBRE"),(11, "NOVIEMBRE"),(12, "DICIEMBRE")
      }
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
    const string sql = @"
                    UPDATE C
                    SET Incluir_En_Declaracion = 0
                    FROM Comprobante C
                    JOIN Receptor R ON C.Comprobante_ID = R.Comprobante_ID
                    WHERE C.Incluir_En_Declaracion = 1
                      AND (R.UsoCFDI = 'G02' OR R.UsoCFDI = 'CP01')
                      AND R.RFC = @RFC
                      AND (YEAR(C.Fecha) = @Year AND (@Month IS NULL OR MONTH(C.Fecha) = @Month))";

    using var conn = new SqlConnection(_connectionString);
    return await conn.ExecuteAsync(sql, new { RFC = rfc, Year = year, Month = month });
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
    string facturamaUser = _configuration["Facturama:User"] ?? "jorgecontreras82";
    string facturamaPassword = _configuration["Facturama:Password"] ?? "Orion2020";
    string authHeader = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{facturamaUser}:{facturamaPassword}"));

    _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authHeader);
    _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

    string queryUrl = $"https://api.facturama.mx/cfdi?type=issued&uuid={uuid}";
    var getResp = await _httpClient.GetAsync(queryUrl);
    getResp.EnsureSuccessStatusCode();

    string getBody = await getResp.Content.ReadAsStringAsync();
    string? cfdiId = null;
    using (var jdoc = JsonDocument.Parse(getBody))
    {
      if (jdoc.RootElement.ValueKind == JsonValueKind.Array && jdoc.RootElement.GetArrayLength() > 0)
      {
        cfdiId = jdoc.RootElement[0].GetProperty("Id").GetString();
      }
    }

    if (string.IsNullOrEmpty(cfdiId))
    {
      throw new InvalidOperationException("No se encontró el CFDI en Facturama para ese UUID.");
    }

    string cancelUrl = $"https://api.facturama.mx/cfdi/{cfdiId}?type=issued&motive=02";
    var deleteResp = await _httpClient.DeleteAsync(cancelUrl);
    deleteResp.EnsureSuccessStatusCode();

    using var conn = new SqlConnection(_connectionString);
    await conn.ExecuteAsync("UPDATE Comprobante SET Incluir_En_Declaracion = 0 WHERE Comprobante_Id = @Id", new { Id = comprobanteId });
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
      "SELECT TOP 1 Transaccion_ID FROM Transaccion_Comprobante WHERE Comprobante_ID = @Cid",
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

  private static DeclaracionEmitida ToDeclaracionEmitida(DeclaracionCfdiBase item) => new(item);

  private static DeclaracionRecibida ToDeclaracionRecibida(DeclaracionCfdiBase item) => new(item);

  private static DeclaracionComplementoEmitido ToComplementoEmitido(DeclaracionComplementoBase item) => new(item);

  private static DeclaracionComplementoRecibido ToComplementoRecibido(DeclaracionComplementoBase item) => new(item);

  private static decimal SatRound(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

  private static bool IsNomina(string? tipoDeComprobante) => string.Equals(tipoDeComprobante, "N", StringComparison.OrdinalIgnoreCase);

  private static bool IsTipoE(string? tipoDeComprobante) => string.Equals(tipoDeComprobante, "E", StringComparison.OrdinalIgnoreCase);
  private static bool IsPpd(string? metodoPago) => string.Equals(metodoPago, "PPD", StringComparison.OrdinalIgnoreCase);

  private static string GetSqlDate(DateTime dt) => dt.ToString("yyyy-MM-dd HH:mm:ss");

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
