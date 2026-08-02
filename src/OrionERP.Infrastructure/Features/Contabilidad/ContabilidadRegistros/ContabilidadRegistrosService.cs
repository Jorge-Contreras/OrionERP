using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using OrionERP.Application.Features.Contabilidad.ContabilidadRegistros;

namespace OrionERP.Infrastructure.Features.Contabilidad.ContabilidadRegistros;

public sealed class ContabilidadRegistrosService : IContabilidadRegistrosService
{
  private const int TransaccionIdBatchSize = 1000;

  private readonly string _connectionString;

  public ContabilidadRegistrosService(IConfiguration configuration)
  {
    _connectionString = configuration.GetConnectionString("OrionDb")
        ?? throw new InvalidOperationException("Missing connection string 'OrionDb'.");
  }

  public async Task<IEnumerable<RegistrosContablesRow>> GetRegistrosAsync(
    DateTime startDate,
    DateTime endDate,
    string rfc,
    string nivel1,
    string nivel2,
    string nivel3)
  {
    if (string.IsNullOrWhiteSpace(rfc) ||
        string.IsNullOrWhiteSpace(nivel1) ||
        string.IsNullOrWhiteSpace(nivel2) ||
        string.IsNullOrWhiteSpace(nivel3))
    {
      return Array.Empty<RegistrosContablesRow>();
    }

    // Normalize dates
    var normalizedStart = startDate.Date;
    var normalizedEnd = endDate.Date.AddDays(1).AddTicks(-1);

    var parameters = new DynamicParameters();
    parameters.Add("@startDate", normalizedStart, DbType.DateTime);
    parameters.Add("@endDate", normalizedEnd, DbType.DateTime);
    parameters.Add("@RFC", rfc.Trim(), DbType.String);
    parameters.Add("@Nivel1", nivel1.Trim(), DbType.String);
    parameters.Add("@Nivel2", NormalizeTwoDigits(nivel2), DbType.String);
    parameters.Add("@Nivel3", NormalizeTwoDigits(nivel3), DbType.String);

    using var connection = new SqlConnection(_connectionString);
    var registros = (await connection.QueryAsync<RegistrosContablesRow>(
        "contabilidad.REGISTROS_CONTABLES_FECHA_NIVELES",
        parameters,
        commandType: CommandType.StoredProcedure))
      .AsList();

    if (registros.Count == 0)
    {
      return registros;
    }

    var cfdiCounts = await GetCfdiCountsAsync(
        connection,
        registros.Select(row => row.Poliza).Distinct());

    return registros
      .Select(row => row with
      {
        CfdiCount = cfdiCounts.GetValueOrDefault(row.Poliza)
      })
      .ToList();
  }

  private static async Task<IReadOnlyDictionary<int, int>> GetCfdiCountsAsync(
    SqlConnection connection,
    IEnumerable<int> transaccionIds)
  {
    const string sql = @"WITH LinkedCfdis AS
(
  SELECT
    tc.Transaccion_ID AS TransaccionId,
    CAST(tc.Comprobante_ID AS bigint) AS ComprobanteId
  FROM dbo.Transaccion_Comprobante AS tc
  INNER JOIN cfdi.Comprobante AS c
          ON c.Comprobante_Id = tc.Comprobante_ID
  WHERE tc.Transaccion_ID IN @TransaccionIds

  UNION

  SELECT
    td.Transaccion_ID AS TransaccionId,
    CAST(p20.Comprobante_Id AS bigint) AS ComprobanteId
  FROM dbo.Transaccion_DoctoRelacionado AS td
  INNER JOIN cfdi.Pagos20_DoctoRelacionado AS dr
          ON dr.DoctoRelacionado_Id = td.DoctoRelacionado_Id
  INNER JOIN cfdi.Pagos20_Pago AS pago
          ON pago.Pago_Id = dr.Pago_Id
  INNER JOIN cfdi.Pagos20 AS p20
          ON p20.Pagos20_Id = pago.Pagos20_Id
  INNER JOIN cfdi.Comprobante AS c
          ON c.Comprobante_Id = p20.Comprobante_Id
  WHERE td.Transaccion_ID IN @TransaccionIds
)
SELECT
  TransaccionId,
  COUNT(*) AS CfdiCount
FROM LinkedCfdis
GROUP BY TransaccionId;";

    var counts = new Dictionary<int, int>();
    foreach (var batch in transaccionIds.Chunk(TransaccionIdBatchSize))
    {
      var rows = await connection.QueryAsync<TransaccionCfdiCountRow>(
          sql,
          new { TransaccionIds = batch });

      foreach (var row in rows)
      {
        counts[row.TransaccionId] = row.CfdiCount;
      }
    }

    return counts;
  }

  public async Task ReorderTransaccionAsync(
    int anchorTransaccionId,
    int targetTransaccionId)
  {
    if (anchorTransaccionId <= 0 || targetTransaccionId <= 0)
    {
      throw new ArgumentException("Transacción inválida para reordenar.");
    }

    using var connection = new SqlConnection(_connectionString);
    await connection.OpenAsync();
    using var transaction = connection.BeginTransaction();

    try
    {
      var fechaRows = (await connection.QueryAsync<TransaccionFechaRow>(
          @"SELECT ID, Fecha, OrdenBalance
            FROM dbo.Transacciones WITH (UPDLOCK, HOLDLOCK)
            WHERE ID IN @Ids",
          new { Ids = new[] { anchorTransaccionId, targetTransaccionId } },
          transaction))
        .ToList();

      var anchorRow = fechaRows.FirstOrDefault(row => row.Id == anchorTransaccionId)
        ?? throw new InvalidOperationException("No se encontró la transacción origen.");
      var targetRow = fechaRows.FirstOrDefault(row => row.Id == targetTransaccionId)
        ?? throw new InvalidOperationException("No se encontró la transacción destino.");

      var anchorFecha = anchorRow.Fecha;
      var targetFecha = targetRow.Fecha;

      if (anchorFecha.Date != targetFecha.Date)
      {
        throw new InvalidOperationException("Solo se pueden reordenar transacciones del mismo día.");
      }

      await connection.ExecuteAsync(
          @"UPDATE dbo.Transacciones
            SET OrdenBalance = CASE
                WHEN ID = @AnchorId THEN @TargetOrdenBalance
                WHEN ID = @TargetId THEN @AnchorOrdenBalance
                ELSE OrdenBalance
            END
            WHERE ID IN (@AnchorId, @TargetId);",
          new
          {
            AnchorId = anchorTransaccionId,
            TargetId = targetTransaccionId,
            AnchorOrdenBalance = anchorRow.OrdenBalance,
            TargetOrdenBalance = targetRow.OrdenBalance
          },
          transaction);

      transaction.Commit();
    }
    catch (Exception ex)
    {
      transaction.Rollback();
      throw new InvalidOperationException("No se pudo reordenar la transacción.", ex);
    }
  }

  private static string NormalizeTwoDigits(string value)
  {
    var trimmed = value.Trim();
    if (trimmed.Length == 1 && char.IsDigit(trimmed[0]))
    {
      return trimmed.PadLeft(2, '0');
    }

    return trimmed;
  }
  private sealed record TransaccionFechaRow
  {
    public int Id { get; init; }
    public DateTime Fecha { get; init; }
    public long OrdenBalance { get; init; }
  }

  private sealed record TransaccionCfdiCountRow
  {
    public int TransaccionId { get; init; }
    public int CfdiCount { get; init; }
  }
}
