using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using OrionERP.Application.Features.Contabilidad.ContabilidadRegistros;

namespace OrionERP.Infrastructure.Features.Contabilidad.ContabilidadRegistros;

public sealed class ContabilidadRegistrosService : IContabilidadRegistrosService
{
  private const int FechaNormalizationStepMs = 10;
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
    return await connection.QueryAsync<RegistrosContablesRow>(
        "contabilidad.REGISTROS_CONTABLES_FECHA_NIVELES",
        parameters,
        commandType: CommandType.StoredProcedure);
  }

  public async Task ReorderTransaccionAsync(
    int anchorTransaccionId,
    int targetTransaccionId,
    IReadOnlyList<int> orderedTransaccionIds)
  {
    if (anchorTransaccionId <= 0 || targetTransaccionId <= 0)
    {
      throw new ArgumentException("Transacción inválida para reordenar.");
    }

    if (orderedTransaccionIds is null || orderedTransaccionIds.Count == 0)
    {
      throw new ArgumentException("No se proporcionó el orden de transacciones.");
    }

    using var connection = new SqlConnection(_connectionString);
    await connection.OpenAsync();
    using var transaction = connection.BeginTransaction();

    try
    {
      var fechaRows = (await connection.QueryAsync<TransaccionFechaRow>(
          @"SELECT ID, Fecha
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

      if (anchorFecha == targetFecha)
      {
        var orderedDistinctIds = GetOrderedDistinctIds(orderedTransaccionIds);
        var tieIds = (await connection.QueryAsync<int>(
            @"SELECT ID
              FROM dbo.Transacciones WITH (UPDLOCK, HOLDLOCK)
              WHERE ID IN @Ids AND Fecha = @Fecha",
            new { Ids = orderedDistinctIds, Fecha = anchorFecha },
            transaction))
          .ToHashSet();

        var orderedTieIds = orderedDistinctIds.Where(id => tieIds.Contains(id)).ToList();
        if (orderedTieIds.Count < 2)
        {
          throw new InvalidOperationException("No se pudo determinar el grupo de empate para reordenar.");
        }

        var baseFecha = anchorFecha;
        for (var index = 0; index < orderedTieIds.Count; index++)
        {
          var id = orderedTieIds[index];
          var normalizedFecha = baseFecha.AddMilliseconds(FechaNormalizationStepMs * index);

          await connection.ExecuteAsync(
              "UPDATE dbo.Transacciones SET Fecha = @Fecha WHERE ID = @Id",
              new { Fecha = normalizedFecha, Id = id },
              transaction);

          if (id == anchorTransaccionId)
          {
            anchorFecha = normalizedFecha;
          }

          if (id == targetTransaccionId)
          {
            targetFecha = normalizedFecha;
          }
        }
      }

      await connection.ExecuteAsync(
          "UPDATE dbo.Transacciones SET Fecha = @Fecha WHERE ID = @Id",
          new { Fecha = targetFecha, Id = anchorTransaccionId },
          transaction);

      await connection.ExecuteAsync(
          "UPDATE dbo.Transacciones SET Fecha = @Fecha WHERE ID = @Id",
          new { Fecha = anchorFecha, Id = targetTransaccionId },
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

  private static List<int> GetOrderedDistinctIds(IReadOnlyList<int> orderedIds)
  {
    var seen = new HashSet<int>();
    var orderedDistinct = new List<int>(orderedIds.Count);

    foreach (var id in orderedIds)
    {
      if (seen.Add(id))
      {
        orderedDistinct.Add(id);
      }
    }

    return orderedDistinct;
  }

  private sealed record TransaccionFechaRow
  {
    public int Id { get; init; }
    public DateTime Fecha { get; init; }
  }
}
