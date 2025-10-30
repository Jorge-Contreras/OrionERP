using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using OrionERP.Application.Features.Cfdi.ContabilidadRegistros;

namespace OrionERP.Infrastructure.Features.Cfdi.ContabilidadRegistros;

public sealed class ContabilidadRegistrosService : IContabilidadRegistrosService
{
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

    var parameters = new DynamicParameters();
    parameters.Add("@startDate", startDate, DbType.DateTime);
    parameters.Add("@endDate", endDate, DbType.DateTime);
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

  private static string NormalizeTwoDigits(string value)
  {
    var trimmed = value.Trim();
    if (trimmed.Length == 1 && char.IsDigit(trimmed[0]))
    {
      return trimmed.PadLeft(2, '0');
    }

    return trimmed;
  }
}
