using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using OrionERP.Application.Common;

namespace OrionERP.Infrastructure.Features.Cfdi.DescargaMasiva.Dapper;

public sealed class SqlConnectionFactory : IDbConnectionFactory
{
  private readonly string _cs;
  private readonly ICurrentRfcAccessor _rfcAccessor;

  public SqlConnectionFactory(IConfiguration cfg, ICurrentRfcAccessor rfcAccessor)
  {
    _cs = cfg.GetConnectionString("OrionDb")!;
    _rfcAccessor = rfcAccessor;
  }

  public IDbConnection Create()
  {
    var connection = new SqlConnection(_cs);
    StateChangeEventHandler? handler = null;
    handler = (_, args) =>
    {
      if (args.CurrentState != ConnectionState.Open)
      {
        return;
      }

      connection.StateChange -= handler;
      using var command = connection.CreateCommand();
      command.CommandText = "EXEC sys.sp_set_session_context @key=N'OrionRfc', @value=@Rfc, @read_only=0;";
      command.Parameters.AddWithValue("@Rfc", NormalizeRfc(_rfcAccessor.CurrentRfc));
      command.ExecuteNonQuery();
    };
    connection.StateChange += handler;
    return connection;
  }

  private static string NormalizeRfc(string? value)
    => string.IsNullOrWhiteSpace(value) ? "__UNSCOPED__" : value.Trim().ToUpperInvariant();
}
