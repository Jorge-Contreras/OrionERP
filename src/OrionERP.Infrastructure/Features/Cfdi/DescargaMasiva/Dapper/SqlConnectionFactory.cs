using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace OrionERP.Infrastructure.Features.Cfdi.DescargaMasiva.Dapper;

public sealed class SqlConnectionFactory
{
  private readonly string _cs;
  public SqlConnectionFactory(IConfiguration cfg)
      => _cs = cfg.GetConnectionString("OrionDb")!;

  public IDbConnection Create() => new SqlConnection(_cs);
}
