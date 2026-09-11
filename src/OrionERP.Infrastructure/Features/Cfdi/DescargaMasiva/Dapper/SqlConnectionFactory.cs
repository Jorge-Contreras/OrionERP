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
    connection.StateChange += (_, args) =>
    {
      if (args.CurrentState != ConnectionState.Open)
      {
        return;
      }

      using var command = connection.CreateCommand();
      command.CommandText = """
        EXEC sys.sp_set_session_context @key=N'OrionRfc', @value=NULL, @read_only=0;
        EXEC sys.sp_set_session_context @key=N'OrionERP.CompanyId', @value=NULL, @read_only=0;
        EXEC sys.sp_set_session_context @key=N'OrionERP.SiteId', @value=NULL, @read_only=0;
        EXEC sys.sp_set_session_context @key=N'OrionERP.ModuleCode', @value=NULL, @read_only=0;
        EXEC sys.sp_set_session_context @key=N'OrionERP.PublicSiteId', @value=NULL, @read_only=0;
        EXEC sys.sp_set_session_context @key=N'OrionERP.HospitalityCompanyId', @value=NULL, @read_only=0;
        EXEC sys.sp_set_session_context @key=N'OrionERP.HospitalitySiteId', @value=NULL, @read_only=0;
        EXEC sys.sp_set_session_context @key=N'OrionERP.HospitalityRfc', @value=NULL, @read_only=0;

        DECLARE @CompanyId bigint =
        (
          SELECT CompanyId FROM orion.Company
          WHERE Rfc=@Rfc AND IsActive=1
        );
        IF @Rfc<>N'__UNSCOPED__' AND @CompanyId IS NULL
          THROW 52240,'El RFC de la conexión no corresponde a una empresa activa.',1;
        EXEC sys.sp_set_session_context @key=N'OrionRfc', @value=@Rfc, @read_only=0;
        EXEC sys.sp_set_session_context @key=N'OrionERP.CompanyId', @value=@CompanyId, @read_only=0;
        """;
      command.Parameters.AddWithValue("@Rfc", NormalizeRfc(_rfcAccessor.CurrentRfc));
      command.ExecuteNonQuery();
    };
    return connection;
  }

  private static string NormalizeRfc(string? value)
    => string.IsNullOrWhiteSpace(value) ? "__UNSCOPED__" : value.Trim().ToUpperInvariant();
}
